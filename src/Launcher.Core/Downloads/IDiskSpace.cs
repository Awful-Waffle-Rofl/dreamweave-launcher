using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace DreamWeave.Launcher.Core.Downloads
{
    /// <summary>Free-space query, injectable so tests can simulate a full disk.</summary>
    public interface IDiskSpace
    {
        /// <summary>Bytes available to the current user on the volume holding <paramref name="directory"/> (which exists).</summary>
        long GetFreeBytes(string directory);
    }

    /// <summary>
    /// <see cref="IDiskSpace"/> backed by <c>GetDiskFreeSpaceEx</c>, which (unlike <see cref="DriveInfo"/>)
    /// also works for UNC paths and mounted folders and honours per-user quotas.
    /// </summary>
    public sealed class SystemDiskSpace : IDiskSpace
    {
        /// <inheritdoc />
        public long GetFreeBytes(string directory)
        {
            string dir = Path.GetFullPath(directory);
            if (!dir.EndsWith("\\", StringComparison.Ordinal))
            {
                dir += "\\";
            }

            ulong available, total, totalFree;
            if (!GetDiskFreeSpaceEx(dir, out available, out total, out totalFree))
            {
                throw new LauncherException(LauncherError.Unknown, "Could not read the free space on the drive holding " + directory + ".", new Win32Exception(Marshal.GetLastWin32Error()));
            }

            return available > long.MaxValue ? long.MaxValue : (long)available;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetDiskFreeSpaceEx(string lpDirectoryName, out ulong lpFreeBytesAvailable, out ulong lpTotalNumberOfBytes, out ulong lpTotalNumberOfFreeBytes);
    }
}
