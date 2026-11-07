using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using DreamWeave.Launcher.Core.State;

namespace DreamWeave.Launcher.Core.Detection
{
    /// <summary>How an Asheron's Call folder was found.</summary>
    public enum AcInstallSource
    {
        /// <summary>No valid folder was found.</summary>
        NotFound = 0,

        /// <summary>The folder saved in the launcher state (a previous detection or the player's choice).</summary>
        SavedFolder = 1,

        /// <summary>The InstallShield uninstall entry with the known product GUID.</summary>
        UninstallKeyGuid = 2,

        /// <summary>Another uninstall entry whose DisplayName says Asheron's Call.</summary>
        UninstallKeyDisplayName = 3,

        /// <summary>The default install folder.</summary>
        DefaultFolder = 4,
    }

    /// <summary>Outcome of <see cref="AcInstallLocator.Locate(string)"/>.</summary>
    public sealed class AcInstallLocation
    {
        /// <summary>Creates a result.</summary>
        public AcInstallLocation(string folder, AcInstallSource source, IReadOnlyList<string> notes)
        {
            Folder = folder;
            Source = source;
            Notes = notes ?? new string[0];
        }

        /// <summary>The chosen folder (contains acclient.exe), or null when <see cref="Source"/> is NotFound.</summary>
        public string Folder { get; }

        /// <summary>How it was found.</summary>
        public AcInstallSource Source { get; }

        /// <summary>True when a valid folder was found.</summary>
        public bool Found => Folder != null;

        /// <summary>One line per candidate considered, for diagnostics.</summary>
        public IReadOnlyList<string> Notes { get; }
    }

    /// <summary>
    /// Finds the Asheron's Call client folder. Candidates, in order: the saved folder, the
    /// uninstall key with the known GUID, any uninstall key whose DisplayName matches, the
    /// default folder. The first candidate that passes <see cref="ValidateFolder"/> wins.
    /// Read-only: it never writes to the registry, the state, or the disk.
    /// </summary>
    public sealed class AcInstallLocator
    {
        /// <summary>Registry path (under HKLM, 32-bit view) holding the uninstall entries.</summary>
        public const string UninstallRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

        /// <summary>The InstallShield product GUID of the retail Asheron's Call client.</summary>
        public const string UninstallGuid = "{F0EE55BA-193D-4670-90C0-76E0E25F3A08}";

        /// <summary>The default install folder.</summary>
        public const string DefaultInstallFolder = @"C:\Turbine\Asheron's Call";

        /// <summary>The file whose presence marks a folder as a client install.</summary>
        public const string ClientExeName = "acclient.exe";

        private readonly IRegistryReader _registry;
        private readonly IAcFileSystem _fs;
        private readonly string _defaultFolder;

        /// <summary>Creates a locator.</summary>
        /// <param name="registry">Registry access.</param>
        /// <param name="fileSystem">File-system access.</param>
        /// <param name="defaultFolder">Default install folder; <see cref="DefaultInstallFolder"/> when null.</param>
        public AcInstallLocator(IRegistryReader registry, IAcFileSystem fileSystem, string defaultFolder = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _fs = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
            _defaultFolder = defaultFolder ?? DefaultInstallFolder;
        }

        /// <summary>Locates the folder, using the one saved in <paramref name="state"/> as the first candidate.</summary>
        public AcInstallLocation Locate(StateStore state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            return Locate(state.Read(s => s.AcFolder));
        }

        /// <summary>Locates the folder; <paramref name="savedFolder"/> (may be null) is the first candidate.</summary>
        public AcInstallLocation Locate(string savedFolder)
        {
            var notes = new List<string>();

            if (!string.IsNullOrWhiteSpace(savedFolder))
            {
                AcInstallLocation hit = Try(savedFolder, AcInstallSource.SavedFolder, "saved folder", notes);
                if (hit != null)
                {
                    return hit;
                }
            }

            string guidKey = UninstallRoot + "\\" + UninstallGuid;
            string byGuid = _registry.GetStringValue(guidKey, "InstallLocation");
            if (byGuid == null)
            {
                notes.Add("uninstall key " + UninstallGuid + ": not present");
            }
            else
            {
                AcInstallLocation hit = Try(byGuid, AcInstallSource.UninstallKeyGuid, "uninstall key " + UninstallGuid, notes);
                if (hit != null)
                {
                    return hit;
                }
            }

            foreach (string name in _registry.GetSubKeyNames(UninstallRoot))
            {
                if (string.Equals(name, UninstallGuid, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string key = UninstallRoot + "\\" + name;
                if (!IsAsheronsCall(_registry.GetStringValue(key, "DisplayName")))
                {
                    continue;
                }

                string location = _registry.GetStringValue(key, "InstallLocation");
                if (location == null)
                {
                    notes.Add("uninstall key " + name + ": matches by name but has no InstallLocation");
                    continue;
                }

                AcInstallLocation hit = Try(location, AcInstallSource.UninstallKeyDisplayName, "uninstall key " + name, notes);
                if (hit != null)
                {
                    return hit;
                }
            }

            AcInstallLocation fallback = Try(_defaultFolder, AcInstallSource.DefaultFolder, "default folder", notes);
            return fallback ?? new AcInstallLocation(null, AcInstallSource.NotFound, notes);
        }

        /// <summary>True when <paramref name="folder"/> is a directory containing acclient.exe.</summary>
        public bool ValidateFolder(string folder)
        {
            string normal = Normalize(folder);
            return normal != null && _fs.DirectoryExists(normal) && _fs.FileExists(Path.Combine(normal, ClientExeName));
        }

        private AcInstallLocation Try(string raw, AcInstallSource source, string label, List<string> notes)
        {
            string normal = Normalize(raw);
            if (normal == null)
            {
                notes.Add(label + ": '" + raw + "' is not a usable folder path");
                return null;
            }

            if (!ValidateFolder(normal))
            {
                notes.Add(label + ": '" + normal + "' has no " + ClientExeName);
                return null;
            }

            notes.Add(label + ": '" + normal + "' is a valid install");
            return new AcInstallLocation(normal, source, notes);
        }

        private static bool IsAsheronsCall(string displayName)
        {
            if (string.IsNullOrEmpty(displayName))
            {
                return false;
            }

            // Registry entries sometimes carry a typographic apostrophe.
            string plain = displayName.Replace((char)0x2019, '\'');
            return plain.IndexOf("Asheron's Call", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Trims quotes and trailing separators and makes the path absolute; null when it is not a rooted, well-formed path.</summary>
        internal static string Normalize(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            string trimmed = raw.Trim().Trim('"').Trim();
            if (trimmed.Length == 0 || !Path.IsPathRooted(trimmed))
            {
                return null;
            }

            try
            {
                string full = Path.GetFullPath(trimmed);
                string root = Path.GetPathRoot(full);
                return full.Length > root.Length ? full.TrimEnd('\\', '/') : full;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is SecurityException)
            {
                return null;
            }
        }
    }
}
