using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using Microsoft.Win32;

namespace DreamWeave.Launcher.Core.SelfInstall
{
    /// <summary>
    /// The small slice of HKEY_CURRENT_USER the launcher writes (its own uninstall key). Paths are
    /// relative to the hive root. Production implementation: <see cref="UserRegistry"/>; tests use
    /// an in-memory fake, so no unit test touches the real registry.
    /// </summary>
    public interface IUserRegistry
    {
        /// <summary>
        /// Creates <paramref name="subKeyPath"/> if needed and writes every value: a <see cref="string"/>
        /// as REG_SZ, an <see cref="int"/> as REG_DWORD. Values already in the key that are not named
        /// are left alone.
        /// </summary>
        /// <exception cref="LauncherException"><see cref="LauncherError.RegistryWriteFailed"/>.</exception>
        void SetValues(string subKeyPath, IReadOnlyDictionary<string, object> values);

        /// <summary>The value (string or int), or null when the key or value is absent.</summary>
        object GetValue(string subKeyPath, string valueName);

        /// <summary>Deletes the key and everything under it. Returns false when it was absent.</summary>
        /// <exception cref="LauncherException"><see cref="LauncherError.RegistryWriteFailed"/>.</exception>
        bool DeleteKeyTree(string subKeyPath);
    }

    /// <summary><see cref="IUserRegistry"/> over <see cref="Registry.CurrentUser"/> (default view; HKCU\Software is not redirected).</summary>
    public sealed class UserRegistry : IUserRegistry
    {
        /// <inheritdoc />
        public void SetValues(string subKeyPath, IReadOnlyDictionary<string, object> values)
        {
            if (values == null)
            {
                throw new ArgumentNullException(nameof(values));
            }

            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(subKeyPath, true))
                {
                    if (key == null)
                    {
                        throw new LauncherException(LauncherError.RegistryWriteFailed, "Windows refused to create the registry key HKCU\\" + subKeyPath + ".");
                    }

                    foreach (KeyValuePair<string, object> v in values)
                    {
                        if (v.Value is int)
                        {
                            key.SetValue(v.Key, (int)v.Value, RegistryValueKind.DWord);
                        }
                        else if (v.Value is string)
                        {
                            key.SetValue(v.Key, (string)v.Value, RegistryValueKind.String);
                        }
                        else
                        {
                            throw new ArgumentException("Registry value '" + v.Key + "' must be a string or an int.", nameof(values));
                        }
                    }
                }
            }
            catch (Exception ex) when (IsRegistryFailure(ex))
            {
                throw new LauncherException(LauncherError.RegistryWriteFailed, "Could not write the registry key HKCU\\" + subKeyPath + ": " + ex.Message, ex);
            }
        }

        /// <inheritdoc />
        public object GetValue(string subKeyPath, string valueName)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(subKeyPath, false))
                {
                    return key?.GetValue(valueName);
                }
            }
            catch (Exception ex) when (IsRegistryFailure(ex))
            {
                return null;
            }
        }

        /// <inheritdoc />
        public bool DeleteKeyTree(string subKeyPath)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(subKeyPath, false))
                {
                    if (key == null)
                    {
                        return false;
                    }
                }

                Registry.CurrentUser.DeleteSubKeyTree(subKeyPath, false);
                return true;
            }
            catch (Exception ex) when (IsRegistryFailure(ex))
            {
                throw new LauncherException(LauncherError.RegistryWriteFailed, "Could not remove the registry key HKCU\\" + subKeyPath + ": " + ex.Message, ex);
            }
        }

        private static bool IsRegistryFailure(Exception ex)
        {
            return ex is SecurityException || ex is UnauthorizedAccessException || ex is IOException || ex is ObjectDisposedException;
        }
    }
}
