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
    /// Holds folders open, delete sharing denied, so that while the set lives none of them can be
    /// renamed, deleted or replaced by a junction (a rename or delete needs DELETE access, which
    /// conflicts with the held handle). Each folder is opened WITHOUT following a reparse point,
    /// refused when it is one, and refused when the opened folder is not the one its path names
    /// (a swap that happened before the open).
    /// </summary>
    internal sealed class FolderPins : IDisposable
    {
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

        private readonly List<SafeFileHandle> _handles = new List<SafeFileHandle>();
        private readonly HashSet<string> _pinned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Number of folders held (for tests).</summary>
        internal int Count => _handles.Count;

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
        /// and the folder that will hold <paramref name="relativePath"/>. Folders that do not exist
        /// yet are left to the writer, which re-checks the path right before each write.
        /// </summary>
        public void PinFoldersOnTheWay(string root, string relativePath)
        {
            string dir = Path.GetDirectoryName(AcPathSafety.Resolve(root, relativePath));
            string rootFull = Path.GetFullPath(root).TrimEnd('\\');
            if (dir == null || dir.Length <= rootFull.Length)
            {
                return;
            }

            string current = rootFull;
            foreach (string segment in dir.Substring(rootFull.Length).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries))
            {
                current = current + "\\" + segment;
                if (!TryPin(current))
                {
                    return;
                }
            }
        }

        private bool TryPin(string folder)
        {
            string full = Path.GetFullPath(folder).TrimEnd('\\');
            if (_pinned.Contains(full))
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
                if (actual == null || !string.Equals(actual.TrimEnd('\\'), expected.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                {
                    throw new LauncherException(LauncherError.ReparsePointInPath, "Nothing was placed because '" + AcPathSafety.ToDisplayPath(full) + "' no longer names the folder that was checked.");
                }
            }
            catch
            {
                h.Dispose();
                throw;
            }

            _handles.Add(h);
            _pinned.Add(full);
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
            foreach (SafeFileHandle h in _handles)
            {
                h.Dispose();
            }

            _handles.Clear();
            _pinned.Clear();
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
