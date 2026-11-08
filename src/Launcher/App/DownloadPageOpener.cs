using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Cleanup;
using DreamWeave.Launcher.Core.Manifests;

namespace DreamWeave.Launcher.App
{
    /// <summary>
    /// Opens the launcher download page in the default browser. This is the whole "update": the launcher
    /// never downloads, writes, renames or replaces its own exe.
    /// </summary>
    internal static class DownloadPageOpener
    {
        /// <summary>
        /// Validates <paramref name="url"/> as an absolute https URL again (it came from a manifest, possibly
        /// cached) and starts it through the shell. Returns <see cref="LauncherError.None"/> when the page was
        /// opened, <see cref="LauncherError.ManifestInvalid"/> when the URL is not acceptable (nothing is
        /// started), or <see cref="LauncherError.Unknown"/> when the shell could not start it.
        /// </summary>
        public static LauncherError Open(IProcessLauncher processes, string url)
        {
            if (processes == null)
            {
                throw new ArgumentNullException(nameof(processes));
            }

            if (!LauncherUpdateInfo.IsValidDownloadPage(url))
            {
                return LauncherError.ManifestInvalid;
            }

            try
            {
                processes.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return LauncherError.None;
            }
            catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException || ex is FileNotFoundException || ex is PlatformNotSupportedException)
            {
                return LauncherError.Unknown;
            }
        }
    }
}