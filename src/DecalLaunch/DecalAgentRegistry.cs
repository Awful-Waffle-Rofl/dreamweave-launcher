using System;
using System.Security;
using Microsoft.Win32;

namespace DreamWeave.DecalLaunch
{
    /// <summary>
    /// Read-only lookup of the folder Decal's own installer recorded, so the helper does not trust the
    /// launcher's Inject.dll argument on its own. Never writes the registry.
    /// </summary>
    internal static class DecalAgentRegistry
    {
        private const string AgentKeyPath = "SOFTWARE\\Decal\\Agent";
        private const string AgentPathValue = "AgentPath";

        /// <summary>HKLM SOFTWARE\Decal\Agent AgentPath in the 32-bit view (WOW6432Node), or null.</summary>
        public static string ReadAgentPath()
        {
            try
            {
                using (RegistryKey hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
                using (RegistryKey key = hive.OpenSubKey(AgentKeyPath, false))
                {
                    return key?.GetValue(AgentPathValue) as string;
                }
            }
            catch (Exception ex) when (ex is SecurityException || ex is UnauthorizedAccessException || ex is System.IO.IOException || ex is ObjectDisposedException)
            {
                return null;
            }
        }
    }
}
