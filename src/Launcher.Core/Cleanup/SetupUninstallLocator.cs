using System;
using System.Security;
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
    }
}