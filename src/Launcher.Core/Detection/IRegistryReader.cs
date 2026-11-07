using System;
using System.Collections.Generic;
using System.Security;
using Microsoft.Win32;

namespace DreamWeave.Launcher.Core.Detection
{
    /// <summary>
    /// Read-only view of the machine registry (HKEY_LOCAL_MACHINE, 32-bit view). Paths are
    /// relative to the hive root, for example <c>SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall</c>;
    /// in the 32-bit view that resolves to the <c>WOW6432Node</c> branch on a 64-bit Windows.
    /// </summary>
    public interface IRegistryReader
    {
        /// <summary>The string value, or null when the key or value is absent, is not a string, or cannot be read.</summary>
        string GetStringValue(string subKeyPath, string valueName);

        /// <summary>Names of the immediate sub-keys, or an empty list when the key is absent or cannot be read.</summary>
        IReadOnlyList<string> GetSubKeyNames(string subKeyPath);
    }

    /// <summary>
    /// <see cref="IRegistryReader"/> over <see cref="Registry"/>. Never writes; every failure to
    /// read reads as "absent".
    /// </summary>
    public sealed class RegistryReader : IRegistryReader
    {
        /// <inheritdoc />
        public string GetStringValue(string subKeyPath, string valueName)
        {
            try
            {
                using (RegistryKey hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
                using (RegistryKey key = hive.OpenSubKey(subKeyPath, false))
                {
                    return key?.GetValue(valueName) as string;
                }
            }
            catch (Exception ex) when (IsReadFailure(ex))
            {
                return null;
            }
        }

        /// <inheritdoc />
        public IReadOnlyList<string> GetSubKeyNames(string subKeyPath)
        {
            try
            {
                using (RegistryKey hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
                using (RegistryKey key = hive.OpenSubKey(subKeyPath, false))
                {
                    return key == null ? new string[0] : key.GetSubKeyNames();
                }
            }
            catch (Exception ex) when (IsReadFailure(ex))
            {
                return new string[0];
            }
        }

        private static bool IsReadFailure(Exception ex)
        {
            return ex is SecurityException || ex is UnauthorizedAccessException || ex is System.IO.IOException || ex is ObjectDisposedException;
        }
    }
}
