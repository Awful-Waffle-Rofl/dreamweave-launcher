using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using DreamWeave.Launcher.Core.Detection;
using Microsoft.Win32.SafeHandles;

namespace DreamWeave.Launcher.Core.Install
{
    /// <summary>
    /// Holds folders so that, while the set lives, none of them can be renamed, deleted or turned
    /// into a junction:
    /// <list type="bullet">
    /// <item>Each folder is opened WITHOUT following a reparse point, with delete sharing denied (a
    /// rename or delete needs DELETE access, which conflicts with the held handle), refused when it
    /// is a reparse point, and refused when the opened folder is not the one its path names (a swap
    /// that happened before the open).</item>
    /// <item>Each folder also holds a sentinel file, opened exclusively and deleted on close. A
    /// folder can only be converted into a junction in place (FSCTL_SET_REPARSE_POINT) while it is
    /// EMPTY, and nobody can delete the sentinel, so the folder never becomes empty.</item>
    /// <item>Before each write, <see cref="RequireNoReparse"/> re-reads the attributes through the
    /// held handles, as a last check.</item>
    /// </list>
    /// </summary>
    internal sealed class FolderPins : IDisposable
    {
        /// <summary>Name prefix of the sentinel file held in each pinned folder.</summary>
        internal const string SentinelPrefix = ".dwpin-";

        private const uint FileListDirectory = 0x0001;
        private const uint FileReadAttributes = 0x0080;
        private const uint FileShareRead = 0x0001;
        private const uint FileShareWrite = 0x0002;
        private const uint OpenExisting = 3;
        private const uint FileFlagBackupSemantics = 0x02000000;
        private const uint FileFlagOpenReparsePoint = 0x00200000;
        private const int ErrorFileNotFound = 2;
        private const int ErrorPathNotFound = 3;
        private const int ErrorSharingViolation = 32;

        private readonly Dictionary<string, Pinned> _pinned = new Dictionary<string, Pinned>(StringComparer.OrdinalIgnoreCase);
        private readonly bool _sentinels;

        /// <summary>Creates an empty set.</summary>
        /// <param name="sentinels">False only in tests that exercise the last-check layer on its own.</param>
        public FolderPins(bool sentinels = true)
        {
            _sentinels = sentinels;
        }

        /// <summary>Number of folders held (for tests).</summary>
        internal int Count => _pinned.Count;

        /// <summary>
        /// A full folder path without a trailing separator, except a drive root, which keeps it
        /// (<c>C:\</c>, never the drive-relative <c>C:</c>).
        /// </summary>
        internal static string TrimFolder(string path)
        {
            string trimmed = path.TrimEnd('\\');
            return trimmed.Length == 2 && trimmed[1] == ':' ? trimmed + "\\" : trimmed;
        }

        /// <summary>
        /// Pins <paramref name="folder"/>, which must exist.
        /// </summary>
        /// <exception cref="LauncherException">ReparsePointInPath when it is a junction or link, or not the folder its path names; AccessDenied or Unknown when it cannot be held.</exception>
        public void Pin(string folder)
        {
            if (!TryPin(folder))
            {
                throw new LauncherException(LauncherError.ElevatedTaskInvalid, "The game folder '" + AcPathSafety.ToDisplayPath(folder) + "' does not exist.");
            }
        }

        /// <summary>
        /// Pins every folder that currently exists between <paramref name="root"/> (already pinned)
        /// and the folder that will hold <paramref name="relativePath"/>.
        /// </summary>
        public void PinFoldersOnTheWay(string root, string relativePath)
        {
            string dir = Path.GetDirectoryName(AcPathSafety.Resolve(root, relativePath));
            string rootFull = TrimFolder(Path.GetFullPath(root));
            if (dir == null || dir.Length <= rootFull.Length)
            {
                return;
            }

            string current = rootFull;
            foreach (string segment in dir.Substring(rootFull.Length).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);

                // A folder that does not exist yet is not pinned: the writer creates it and
                // re-checks the whole path below the game folder right before each write.
                if (!TryPin(current))
                {
                    return;
                }
            }
        }

        /// <summary>
        /// Last check before a write to <paramref name="targetFullPath"/>: re-reads, through the
        /// held handles, every pinned folder that contains it, and refuses when one has become a
        /// reparse point.
        /// </summary>
        /// <exception cref="LauncherException">ReparsePointInPath.</exception>
        public void RequireNoReparse(string targetFullPath)
        {
            for (string dir = Path.GetDirectoryName(Path.GetFullPath(targetFullPath)); dir != null; dir = Path.GetDirectoryName(dir))
            {
                Pinned pin;
                if (!_pinned.TryGetValue(TrimFolder(dir), out pin))
                {
                    continue;
                }

                ByHandleFileInformation info;
                if (!GetFileInformationByHandle(pin.Handle, out info) || (info.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0)
                {
                    throw new LauncherException(LauncherError.ReparsePointInPath, "Nothing more was placed because '" + AcPathSafety.ToDisplayPath(dir) + "' became a junction or link, or could not be checked.");
                }
            }
        }

        private bool TryPin(string folder)
        {
            string full = TrimFolder(Path.GetFullPath(folder));
            if (_pinned.ContainsKey(full))
            {
                return true;
            }

            SafeFileHandle h = CreateFile(full, FileListDirectory | FileReadAttributes, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
            if (h.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                h.Dispose();
                if (error == ErrorFileNotFound || error == ErrorPathNotFound)
                {
                    return false;
                }

                var inner = new Win32Exception(error);
                if (error == ErrorSharingViolation)
                {
                    throw new LauncherException(LauncherError.AccessDenied, "'" + AcPathSafety.ToDisplayPath(full) + "' is in use and could not be held while files are placed.", inner);
                }

                throw new LauncherException(LauncherError.AccessDenied, "'" + AcPathSafety.ToDisplayPath(full) + "' could not be held while files are placed: " + inner.Message, inner);
            }

            FileStream sentinel = null;
            try
            {
                ByHandleFileInformation info;
                if (!GetFileInformationByHandle(h, out info))
                {
                    throw new LauncherException(LauncherError.Unknown, "'" + AcPathSafety.ToDisplayPath(full) + "' could not be checked: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
                }

                if ((info.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0)
                {
                    throw new LauncherException(LauncherError.ReparsePointInPath, "Nothing was placed because '" + AcPathSafety.ToDisplayPath(full) + "' is a junction or link.");
                }

                if ((info.FileAttributes & (uint)FileAttributes.Directory) == 0)
                {
                    throw new LauncherException(LauncherError.ElevatedTaskInvalid, "'" + AcPathSafety.ToDisplayPath(full) + "' is not a folder.");
                }

                string actual = FinalPath(h);
                string expected = LongPath(full);
                if (actual == null || !string.Equals(TrimFolder(actual), TrimFolder(expected), StringComparison.OrdinalIgnoreCase))
                {
                    throw new LauncherException(LauncherError.ReparsePointInPath, "Nothing was placed because '" + AcPathSafety.ToDisplayPath(full) + "' no longer names the folder that was checked.");
                }

                if (_sentinels)
                {
                    try
                    {
                        sentinel = new FileStream(Path.Combine(full, SentinelPrefix + Guid.NewGuid().ToString("N")), FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        throw new LauncherException(LauncherError.AccessDenied, "'" + AcPathSafety.ToDisplayPath(full) + "' could not be held while files are placed: " + ex.Message, ex);
                    }
                }
            }
            catch
            {
                sentinel?.Dispose();
                h.Dispose();
                throw;
            }

            _pinned.Add(full, new Pinned(h, sentinel));
            return true;
        }

        private static string FinalPath(SafeFileHandle h)
        {
            var buffer = new StringBuilder(1024);
            uint n = GetFinalPathNameByHandle(h, buffer, (uint)buffer.Capacity, 0);
            if (n == 0)
            {
                return null;
            }

            if (n >= buffer.Capacity)
            {
                buffer = new StringBuilder((int)n + 1);
                n = GetFinalPathNameByHandle(h, buffer, (uint)buffer.Capacity, 0);
                if (n == 0 || n >= buffer.Capacity)
                {
                    return null;
                }
            }

            return AcPathSafety.StripExtendedPrefix(buffer.ToString());
        }

        /// <summary>The path with every 8.3 short segment expanded, so it compares with a final path.</summary>
        private static string LongPath(string full)
        {
            var buffer = new StringBuilder(1024);
            uint n = GetLongPathName(full, buffer, (uint)buffer.Capacity);
            if (n == 0 || n >= buffer.Capacity)
            {
                return full;
            }

            return buffer.ToString();
        }

        public void Dispose()
        {
            foreach (Pinned p in _pinned.Values)
            {
                // Sentinel first: it is deleted on close, while the folder is still held.
                p.Sentinel?.Dispose();
                p.Handle.Dispose();
            }

            _pinned.Clear();
        }

        private sealed class Pinned
        {
            public Pinned(SafeFileHandle handle, FileStream sentinel)
            {
                Handle = handle;
                Sentinel = sentinel;
            }

            public SafeFileHandle Handle { get; }

            public FileStream Sentinel { get; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out ByHandleFileInformation lpFileInformation);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle hFile, StringBuilder lpszFilePath, uint cchFilePath, uint dwFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetLongPathName(string lpszShortPath, StringBuilder lpszLongPath, uint cchBuffer);
    }
}
