using System;
using System.IO;

namespace DreamWeave.Launcher.Core.SelfInstall
{
    /// <summary>Fixed names the launcher's own install uses (exe, shortcut, uninstall key, command-line switches).</summary>
    public static class SelfInstallLayout
    {
        /// <summary>File name of the installed exe inside <see cref="AppPaths.InstallDir"/>.</summary>
        public const string ExeFileName = "DreamWeaveLauncher.exe";

        /// <summary>File name of the Start-menu shortcut inside the per-user Programs folder.</summary>
        public const string ShortcutFileName = LauncherInfo.ProductName + ".lnk";

        /// <summary>Name of the per-user uninstall key.</summary>
        public const string UninstallKeyName = "DreamWeaveLauncher";

        /// <summary>The per-user uninstall key, relative to HKEY_CURRENT_USER.</summary>
        public const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + UninstallKeyName;

        /// <summary>Publisher shown in Apps and Features.</summary>
        public const string Publisher = "DreamWeave";

        /// <summary>Command-line switch that runs the uninstaller.</summary>
        public const string UninstallSwitch = "--uninstall";

        /// <summary>Command-line switch a freshly swapped-in launcher is started with.</summary>
        public const string UpdatedSwitch = "--updated";

        /// <summary>Suffix of the previous exe kept during a swap (deleted by the new process).</summary>
        public const string OldSuffix = ".old";

        /// <summary>Suffix of the incoming exe written next to the target before a swap.</summary>
        public const string NewSuffix = ".new";

        /// <summary>Full path of the installed exe.</summary>
        public static string InstalledExePath(AppPaths paths)
        {
            if (paths == null)
            {
                throw new ArgumentNullException(nameof(paths));
            }

            return Path.Combine(paths.InstallDir, ExeFileName);
        }

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
