using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace DreamWeave.Launcher.Core.Install
{
    /// <summary>One running process as a process snapshot reports it.</summary>
    public struct ProcessEntry
    {
        /// <summary>Creates an entry.</summary>
        public ProcessEntry(int pid, int parentPid, string exeName)
        {
            Pid = pid;
            ParentPid = parentPid;
            ExeName = exeName ?? string.Empty;
        }

        /// <summary>The process id.</summary>
        public int Pid { get; }

        /// <summary>The id of the process that created it (it may have exited since).</summary>
        public int ParentPid { get; }

        /// <summary>The image file name without a folder, for example <c>IDriver.exe</c>.</summary>
        public string ExeName { get; }
    }

    /// <summary>Lists the running processes. Production: <see cref="ToolhelpProcessTable"/>; tests use a fake.</summary>
    public interface IProcessTable
    {
        /// <summary>Every process running now. Throws <see cref="Win32Exception"/> when Windows cannot list them.</summary>
        IReadOnlyList<ProcessEntry> Snapshot();
    }

    /// <summary>
    /// <see cref="IProcessTable"/> over <c>CreateToolhelp32Snapshot</c>. It needs no access to the
    /// processes themselves, so it also lists elevated ones (the installer runs as administrator,
    /// the launcher does not), with their parent ids.
    /// </summary>
    public sealed class ToolhelpProcessTable : IProcessTable
    {
        private const uint Th32csSnapProcess = 0x00000002;
        private static readonly IntPtr InvalidHandle = new IntPtr(-1);

        /// <summary>The shared instance.</summary>
        public static readonly ToolhelpProcessTable Instance = new ToolhelpProcessTable();

        /// <inheritdoc />
        public IReadOnlyList<ProcessEntry> Snapshot()
        {
            IntPtr snap = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
            if (snap == InvalidHandle)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                var list = new List<ProcessEntry>();
                var e = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf(typeof(ProcessEntry32)) };
                if (!Process32First(snap, ref e))
                {
                    return list;
                }

                do
                {
                    list.Add(new ProcessEntry((int)e.th32ProcessID, (int)e.th32ParentProcessID, e.szExeFile));
                    e.dwSize = (uint)Marshal.SizeOf(typeof(ProcessEntry32));
                }
                while (Process32Next(snap, ref e));

                return list;
            }
            finally
            {
                CloseHandle(snap);
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessEntry32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "Process32FirstW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32First(IntPtr hSnapshot, ref ProcessEntry32 lppe);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "Process32NextW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32Next(IntPtr hSnapshot, ref ProcessEntry32 lppe);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);
    }

    /// <summary>
    /// Answers "is the game installer still running?" for the installer the launcher started.
    /// <para>
    /// The started process alone cannot answer it: an InstallShield self-extractor unpacks itself,
    /// starts the real setup as a child and exits early, and the setup engine (IDriver.exe and its
    /// helpers, or IKernel.exe on older versions) is started through COM, so it is not a descendant
    /// at all. The watch therefore tracks the process tree and the known helpers together:
    /// </para>
    /// <list type="bullet">
    /// <item>Roots: the started process id (when Windows returned one) and any process whose image
    /// name is the staged installer's file name (when it did not).</item>
    /// <item>On every <see cref="IsRunning"/> call every process whose parent is tracked is adopted,
    /// to a fixed point, BEFORE tracked ids that are gone are dropped, so the child of a parent that
    /// exited between two polls is still adopted (its parent id still names the dead parent).</item>
    /// <item>Any process named like an InstallShield helper counts as running, whoever started it.</item>
    /// </list>
    /// A snapshot that fails counts as running: the overlay waits rather than guesses.
    /// </summary>
    public sealed class InstallerProcessWatch
    {
        /// <summary>InstallShield engine and helper image names (case-insensitive, whole name).</summary>
        public static readonly IReadOnlyList<string> HelperNames = new[]
        {
            "IDriver.exe",
            "ISBEW64.exe",
            "IKernel.exe",
            "_ISDel.exe",
        };

        // Temporary InstallShield stubs: _INS5576._MP (InstallShield 5 self-extract), _ISTMP0.EXE and similar.
        private static readonly Regex HelperPattern = new Regex(@"^(_INS\d+\._MP|_ISTMP\d*\.EXE)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private readonly IProcessTable _table;
        private readonly string _installerExeName;
        private readonly HashSet<int> _tracked = new HashSet<int>();

        /// <param name="table">Lists the running processes.</param>
        /// <param name="rootPid">Id of the process that was started, or null when Windows did not report one.</param>
        /// <param name="installerExeName">File name of the staged installer (for example <c>ac-client.exe</c>), or null.</param>
        public InstallerProcessWatch(IProcessTable table, int? rootPid, string installerExeName)
        {
            _table = table ?? throw new ArgumentNullException(nameof(table));
            _installerExeName = installerExeName;
            if (rootPid.HasValue && rootPid.Value > 0)
            {
                _tracked.Add(rootPid.Value);
            }
        }

        /// <summary>What the last <see cref="IsRunning"/> call saw, for the log (process names only).</summary>
        public string LastReason { get; private set; } = string.Empty;

        /// <summary>True while any tracked process or any InstallShield helper is running.</summary>
        public bool IsRunning()
        {
            IReadOnlyList<ProcessEntry> all;
            try
            {
                all = _table.Snapshot();
            }
            catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException || ex is ExternalException)
            {
                LastReason = "process list unavailable";
                return true;
            }

            var alive = new HashSet<int>(all.Select(p => p.Pid));
            foreach (ProcessEntry p in all)
            {
                if (_installerExeName != null && string.Equals(p.ExeName, _installerExeName, StringComparison.OrdinalIgnoreCase))
                {
                    _tracked.Add(p.Pid);
                }
            }

            // Adopt children (and their children) before dropping the dead, so an orphan is never missed.
            bool grew = true;
            while (grew)
            {
                grew = false;
                foreach (ProcessEntry p in all)
                {
                    if (p.Pid != p.ParentPid && _tracked.Contains(p.ParentPid) && _tracked.Add(p.Pid))
                    {
                        grew = true;
                    }
                }
            }

            _tracked.RemoveWhere(pid => !alive.Contains(pid));

            var running = all.Where(p => _tracked.Contains(p.Pid) || IsHelper(p.ExeName)).Select(p => p.ExeName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            LastReason = string.Join(", ", running);
            return running.Count > 0;
        }

        /// <summary>True when <paramref name="exeName"/> is a known InstallShield helper.</summary>
        public static bool IsHelper(string exeName)
        {
            if (string.IsNullOrEmpty(exeName))
            {
                return false;
            }

            return HelperNames.Any(h => string.Equals(h, exeName, StringComparison.OrdinalIgnoreCase)) || HelperPattern.IsMatch(exeName);
        }
    }
}
