using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace DreamWeave.DecalLaunch
{
    /// <summary>
    /// Starts acclient.exe suspended, loads the player's own Decal <c>Inject.dll</c> into it and calls
    /// that library's documented <c>DecalStartup</c> entry point, then resumes the client. This is the
    /// same sequence other Asheron's Call launchers use. The launcher that ships this helper does not
    /// import any of these APIs; they are confined to this isolated x86 helper. No client file is read,
    /// written or modified; only the player's own Decal library is loaded, through Decal's own entry
    /// point, into a process this helper itself just created.
    ///
    /// Fail-open: if any injection step fails the client is still resumed, so the player gets a plain
    /// client rather than a hung one, and <see cref="Launch"/> reports that Decal did not load.
    /// </summary>
    internal static class NativeDecalInjector
    {
        /// <summary>
        /// Creates <paramref name="inputs"/>.<see cref="DecalLaunchInputs.AcclientPath"/> suspended,
        /// injects Decal, then resumes it. Returns the new process id. <paramref name="injectionFailed"/>
        /// is true when the client started but Decal could not be loaded.
        /// </summary>
        /// <exception cref="Win32Exception">When the client could not be created (nothing was started).</exception>
        public static int Launch(DecalLaunchInputs inputs, string arguments, out bool injectionFailed)
        {
            injectionFailed = false;

            // The command line's first token is the program; acclient reads its own args after it.
            string commandLine = "\"" + inputs.AcclientPath + "\" " + (arguments ?? string.Empty);

            var si = new StartupInfo();
            si.cb = Marshal.SizeOf(typeof(StartupInfo));
            ProcessInformation pi;

            bool created = CreateProcessW(
                inputs.AcclientPath,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                CreateSuspended,
                IntPtr.Zero,
                inputs.GameFolder,
                ref si,
                out pi);

            if (!created)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcess on acclient.exe failed.");
            }

            try
            {
                try
                {
                    InjectDecal(pi.hProcess, inputs.InjectDllPath);
                }
                catch (Exception)
                {
                    // Fail-open: the client is resumed below regardless, running without Decal.
                    injectionFailed = true;
                }

                if (ResumeThread(pi.hThread) == unchecked((uint)-1))
                {
                    // The resume itself failed: the process is suspended and useless. Terminate it so no
                    // orphaned, frozen client is left behind, and report the failure as a start failure.
                    TerminateProcess(pi.hProcess, 1);
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "ResumeThread on the client failed.");
                }

                return pi.dwProcessId;
            }
            finally
            {
                CloseHandle(pi.hThread);
                CloseHandle(pi.hProcess);
            }
        }

        /// <summary>
        /// Loads <paramref name="injectDllPath"/> into <paramref name="process"/> and calls its
        /// <c>DecalStartup</c> export there. Throws on any failure (the caller fails open).
        /// </summary>
        private static void InjectDecal(IntPtr process, string injectDllPath)
        {
            IntPtr kernel32 = GetModuleHandleW("kernel32.dll");
            if (kernel32 == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "kernel32 handle.");
            }

            IntPtr loadLibrary = GetProcAddress(kernel32, "LoadLibraryW");
            if (loadLibrary == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "LoadLibraryW address.");
            }

            // Write the Inject.dll path into the target, then have it LoadLibraryW that path.
            byte[] pathBytes = Encoding.Unicode.GetBytes(injectDllPath + "\0");
            IntPtr remotePath = VirtualAllocEx(process, IntPtr.Zero, (uint)pathBytes.Length, MemCommit | MemReserve, PageReadWrite);
            if (remotePath == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "VirtualAllocEx for the path.");
            }

            uint remoteModule;
            bool freeRemotePath = true;
            try
            {
                if (!WriteProcessMemory(process, remotePath, pathBytes, (uint)pathBytes.Length, out _))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "WriteProcessMemory of the path.");
                }

                remoteModule = RunRemoteThread(process, loadLibrary, remotePath);
                if (remoteModule == 0)
                {
                    throw new InvalidOperationException("Inject.dll did not load in the client.");
                }
            }
            catch (RemoteThreadTimeoutException)
            {
                // The LoadLibraryW thread may still be reading the path buffer: leave it allocated (a few
                // bytes in the client) rather than free memory a live thread is using.
                freeRemotePath = false;
                throw;
            }
            finally
            {
                if (freeRemotePath)
                {
                    VirtualFreeEx(process, remotePath, 0, MemRelease);
                }
            }

            // Load the same Inject.dll locally to find DecalStartup's offset from the module base, then
            // call it at the same offset inside the client. acclient and this helper are both 32-bit, so
            // the module base and offset are the same pointer width.
            IntPtr localModule = LoadLibraryW(injectDllPath);
            if (localModule == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "LoadLibraryW(Inject.dll) locally.");
            }

            try
            {
                IntPtr localStartup = GetProcAddress(localModule, "DecalStartup");
                if (localStartup == IntPtr.Zero)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "DecalStartup export.");
                }

                long offset = localStartup.ToInt64() - localModule.ToInt64();
                IntPtr remoteStartup = new IntPtr(unchecked((long)remoteModule) + offset);
                RunRemoteThread(process, remoteStartup, IntPtr.Zero);
            }
            finally
            {
                FreeLibrary(localModule);
            }
        }

        /// <summary>How long one remote thread (LoadLibraryW, then DecalStartup) may run before injection is abandoned.</summary>
        internal const uint RemoteThreadTimeoutMs = 15000;

        /// <summary>
        /// Runs <paramref name="start"/>(<paramref name="arg"/>) in <paramref name="process"/> and returns its
        /// 32-bit exit code. Waits at most <see cref="RemoteThreadTimeoutMs"/>; on a timeout it throws
        /// <see cref="RemoteThreadTimeoutException"/> so the caller fails open and resumes the client.
        /// </summary>
        private static uint RunRemoteThread(IntPtr process, IntPtr start, IntPtr arg)
        {
            IntPtr thread = CreateRemoteThread(process, IntPtr.Zero, 0, start, arg, 0, IntPtr.Zero);
            if (thread == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateRemoteThread.");
            }

            try
            {
                const uint waitObject0 = 0x00000000;
                const uint waitTimeout = 0x00000102;
                uint wait = WaitForSingleObject(thread, RemoteThreadTimeoutMs);
                if (wait == waitTimeout)
                {
                    throw new RemoteThreadTimeoutException();
                }

                if (wait != waitObject0)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "WaitForSingleObject on the remote thread.");
                }

                GetExitCodeThread(thread, out uint code);
                return code;
            }
            finally
            {
                // Closing our handle does not stop the thread.
                CloseHandle(thread);
            }
        }

        /// <summary>A remote thread did not finish within <see cref="RemoteThreadTimeoutMs"/>.</summary>
        private sealed class RemoteThreadTimeoutException : Exception
        {
            public RemoteThreadTimeoutException()
                : base("A remote thread in the client did not finish in time.")
            {
            }
        }

        private const uint CreateSuspended = 0x00000004;
        private const uint MemCommit = 0x00001000;
        private const uint MemReserve = 0x00002000;
        private const uint MemRelease = 0x00008000;
        private const uint PageReadWrite = 0x04;

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateProcessW(
            string applicationName,
            string commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            string currentDirectory,
            ref StartupInfo startupInfo,
            out ProcessInformation processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint ResumeThread(IntPtr thread);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateProcess(IntPtr process, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string moduleName);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryW(string path);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FreeLibrary(IntPtr module);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, uint size, uint allocationType, uint protect);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool VirtualFreeEx(IntPtr process, IntPtr address, uint size, uint freeType);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] buffer, uint size, out IntPtr written);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateRemoteThread(IntPtr process, IntPtr threadAttributes, uint stackSize, IntPtr startAddress, IntPtr parameter, uint creationFlags, IntPtr threadId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetExitCodeThread(IntPtr thread, out uint exitCode);
    }
}
