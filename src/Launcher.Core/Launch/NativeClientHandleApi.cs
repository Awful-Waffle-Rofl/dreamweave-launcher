using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace DreamWeave.Launcher.Core.Launch
{
    /// <summary>
    /// <see cref="IClientHandleApi"/> over Windows. This file holds every P/Invoke of the client lock
    /// release. It reads one process's own handle table (NtQueryInformationProcess with
    /// ProcessHandleInformation), never the system-wide table, and never queries object names
    /// (NtQueryObject is not used, so a synchronous pipe handle cannot hang it). Struct offsets follow
    /// the launcher's own pointer size, which is the layout Windows returns to the caller.
    /// </summary>
    public sealed class NativeClientHandleApi : IClientHandleApi
    {
        private const uint ProcessDupHandle = 0x0040;
        // ProcessHandleInformation needs the full query right: with PROCESS_QUERY_LIMITED_INFORMATION
        // (0x1000) the live check got STATUS_ACCESS_DENIED (0xC0000022) on the started client.
        private const uint ProcessQueryInformation = 0x0400;
        private const uint Synchronize = 0x00100000;
        private const uint DuplicateCloseSource = 0x1;
        private const uint DuplicateSameAccess = 0x2;
        private const uint StillActive = 259;
        private const int ProcessHandleInformation = 51;
        private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
        private const int MaxTableBytes = 64 * 1024 * 1024;

        private int _lastError;

        /// <inheritdoc />
        public bool Supported
        {
            get
            {
                return HasExport("kernelbase.dll", "CompareObjectHandles") && HasExport("ntdll.dll", "NtQueryInformationProcess");
            }
        }

        /// <inheritdoc />
        public IntPtr CurrentProcess => GetCurrentProcess();

        /// <inheritdoc />
        public int LastError => _lastError;

        /// <inheritdoc />
        public IntPtr OpenProcess(int pid)
        {
            IntPtr h = OpenProcessNative(ProcessDupHandle | ProcessQueryInformation, false, (uint)pid);
            Capture(h != IntPtr.Zero);
            return h;
        }

        /// <inheritdoc />
        public bool HasExited(IntPtr process)
        {
            return GetExitCodeProcess(process, out uint code) && code != StillActive;
        }

        /// <inheritdoc />
        public IReadOnlyList<ClientHandleEntry> HandleTable(IntPtr process)
        {
            int size = 32 * 1024;
            while (size <= MaxTableBytes)
            {
                IntPtr buffer = Marshal.AllocHGlobal(size);
                try
                {
                    int status = NtQueryInformationProcess(process, ProcessHandleInformation, buffer, size, out int needed);
                    if (status == StatusInfoLengthMismatch)
                    {
                        size = Math.Max(size * 2, needed + 4096);
                        continue;
                    }

                    if (status < 0)
                    {
                        _lastError = status;
                        return null;
                    }

                    return Parse(buffer, size);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }

            _lastError = StatusInfoLengthMismatch;
            return null;
        }

        /// <inheritdoc />
        public IntPtr OpenSemaphore(string name)
        {
            IntPtr h = OpenSemaphoreW(Synchronize, false, name);
            Capture(h != IntPtr.Zero);
            return h;
        }

        /// <inheritdoc />
        public IntPtr DuplicateIntoSelf(IntPtr process, IntPtr value)
        {
            bool ok = DuplicateHandle(process, value, GetCurrentProcess(), out IntPtr copy, 0, false, DuplicateSameAccess);
            Capture(ok);
            return ok ? copy : IntPtr.Zero;
        }

        /// <inheritdoc />
        public bool SameObject(IntPtr first, IntPtr second)
        {
            return CompareObjectHandles(first, second);
        }

        /// <inheritdoc />
        public bool CloseInProcess(IntPtr process, IntPtr value)
        {
            bool ok = DuplicateHandle(process, value, IntPtr.Zero, out IntPtr _, 0, false, DuplicateCloseSource);
            Capture(ok);
            return ok;
        }

        /// <inheritdoc />
        public void CloseOwn(IntPtr handle)
        {
            if (handle != IntPtr.Zero)
            {
                CloseHandle(handle);
            }
        }

        /// <summary>
        /// PROCESS_HANDLE_SNAPSHOT_INFORMATION: NumberOfHandles and Reserved (ULONG_PTR each), then
        /// PROCESS_HANDLE_TABLE_ENTRY_INFO entries of HandleValue, HandleCount, PointerCount (pointer
        /// size each), then GrantedAccess, ObjectTypeIndex, HandleAttributes, Reserved (ULONG each):
        /// 40 bytes per entry in a 64-bit process, 28 in a 32-bit one.
        /// </summary>
        internal static IReadOnlyList<ClientHandleEntry> Parse(IntPtr buffer, int size)
        {
            int ptr = IntPtr.Size;
            int entrySize = (3 * ptr) + 16;
            int header = 2 * ptr;
            long count = ptr == 8 ? Marshal.ReadInt64(buffer) : Marshal.ReadInt32(buffer);
            long fits = (size - header) / entrySize;
            if (count < 0 || count > fits)
            {
                count = Math.Max(0, fits);
            }

            var entries = new List<ClientHandleEntry>((int)count);
            for (long i = 0; i < count; i++)
            {
                int at = header + (int)(i * entrySize);
                IntPtr value = Marshal.ReadIntPtr(buffer, at);
                int typeIndex = Marshal.ReadInt32(buffer, at + (3 * ptr) + 4);
                entries.Add(new ClientHandleEntry(value, typeIndex));
            }

            return entries;
        }

        private void Capture(bool ok)
        {
            _lastError = ok ? 0 : Marshal.GetLastWin32Error();
        }

        private static bool HasExport(string module, string name)
        {
            IntPtr m = GetModuleHandleW(module);
            return m != IntPtr.Zero && GetProcAddress(m, name) != IntPtr.Zero;
        }

        [DllImport("kernel32.dll", EntryPoint = "OpenProcess", SetLastError = true)]
        private static extern IntPtr OpenProcessNative(uint access, bool inherit, uint pid);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenSemaphoreW(uint access, bool inherit, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr sourceHandle, IntPtr targetProcess, out IntPtr targetHandle, uint access, bool inherit, uint options);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernelbase.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CompareObjectHandles(IntPtr first, IntPtr second);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, IntPtr info, int length, out int returnLength);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string module);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, BestFitMapping = false)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);
    }
}
