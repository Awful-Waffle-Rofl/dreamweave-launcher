using System;
using System.IO;
using System.Security;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DreamWeave.Launcher.Core.Cleanup
{
    /// <summary>Read-only view of the current user's registry (HKEY_CURRENT_USER). The launcher never writes to it.</summary>
    public interface IUserRegistryReader
    {
        /// <summary>The string value, or null when the key or value is absent, is not a string, or cannot be read.</summary>
        string GetStringValue(string subKeyPath, string valueName);
    }

    /// <summary><see cref="IUserRegistryReader"/> over <see cref="Registry.CurrentUser"/>. Read only.</summary>
    public sealed class UserRegistryReader : IUserRegistryReader
    {
        /// <inheritdoc />
        public string GetStringValue(string subKeyPath, string valueName)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(subKeyPath, false))
                {
                    return key?.GetValue(valueName) as string;
                }
            }
            catch (Exception ex) when (ex is SecurityException || ex is UnauthorizedAccessException || ex is System.IO.IOException || ex is ObjectDisposedException)
            {
                return null;
            }
        }
    }

    /// <summary>Finds the setup program's uninstall command, if the launcher was installed by the setup program.</summary>
    public static class SetupUninstallLocator
    {
        /// <summary>
        /// The <c>UninstallString</c> under <see cref="SetupConstants.SetupUninstallKeyPath"/>, or null when
        /// absent or blank (a development or portable run, which has no setup uninstaller).
        /// </summary>
        public static string FindUninstallString(IUserRegistryReader registry)
        {
            if (registry == null)
            {
                throw new ArgumentNullException(nameof(registry));
            }

            string s = registry.GetStringValue(SetupConstants.SetupUninstallKeyPath, "UninstallString");
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }

        private static readonly Regex SetupUninstallerName = new Regex(@"^unins\d{3}\.exe$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        /// <summary>
        /// Decides whether an <c>UninstallString</c> read from HKCU may be run. The registry is writable by
        /// anything running as the user, so the string is never trusted: it must name an Inno Setup
        /// uninstaller (<c>unins000.exe</c> style) that exists, takes no arguments, and sits in the same
        /// folder as the running launcher (the setup installs both there, wherever the player put it).
        /// Anything else (a relative path, <c>cmd /c ...</c>, a system exe, a file elsewhere) is refused
        /// and the caller falls back to <c>--cleanup</c>.
        /// </summary>
        public static bool TryResolve(string uninstallString, ISelfFileSystem fileSystem, string runningExe, out string program, out string arguments)
        {
            program = null;
            arguments = null;
            if (string.IsNullOrWhiteSpace(uninstallString) || fileSystem == null || string.IsNullOrWhiteSpace(runningExe))
            {
                return false;
            }

            string candidate, args;
            LauncherCleanup.SplitCommandLine(uninstallString, out candidate, out args);
            if (string.IsNullOrWhiteSpace(candidate) || args.Length != 0)
            {
                return false;
            }

            try
            {
                if (!Path.IsPathRooted(candidate) || candidate.StartsWith(@"\\", StringComparison.Ordinal))
                {
                    return false;
                }

                string full = Path.GetFullPath(candidate);
                if (!SetupUninstallerName.IsMatch(Path.GetFileName(full)))
                {
                    return false;
                }

                string exeDir = Path.GetDirectoryName(Path.GetFullPath(runningExe));
                if (!CleanupLayout.SamePath(Path.GetDirectoryName(full), exeDir))
                {
                    return false;
                }

                if (!fileSystem.FileExists(full))
                {
                    return false;
                }

                program = full;
                arguments = string.Empty;
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is System.Security.SecurityException)
            {
                return false;
            }
        }
    }
}