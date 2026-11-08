using System;
using System.IO;

namespace DreamWeave.Launcher.Core.Cleanup
{
    /// <summary>
    /// Fixed names and path helpers shared by cleanup. The launcher no longer installs or updates
    /// itself (the setup program owns install and uninstall; an update is a download page the player opens),
    /// so this holds only what is still needed.
    /// </summary>
    public static class CleanupLayout
    {
        /// <summary>True when two paths name the same location (full paths, case-insensitive).</summary>
        internal static bool SamePath(string a, string b)
        {
            return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>True when <paramref name="path"/> is strictly inside <paramref name="folder"/>.</summary>
        internal static bool IsInside(string path, string folder)
        {
            string root = Normalize(folder) + Path.DirectorySeparatorChar;
            return Normalize(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        private static string Normalize(string p)
        {
            return Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }
}