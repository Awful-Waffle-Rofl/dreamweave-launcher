using System;
using System.IO;

namespace DreamWeave.Launcher.Core.Detection
{
    /// <summary>Where a player's installed Decal was found.</summary>
    public sealed class DecalLocation
    {
        /// <summary>Creates a location.</summary>
        public DecalLocation(string injectDllPath, string fileVersion)
        {
            InjectDllPath = injectDllPath;
            FileVersion = fileVersion;
        }

        /// <summary>Full path to the player's own <c>Inject.dll</c> (exists on disk).</summary>
        public string InjectDllPath { get; }

        /// <summary>The file version of <c>Inject.dll</c>, or null when it could not be read.</summary>
        public string FileVersion { get; }
    }

    /// <summary>
    /// Finds the Decal agent the PLAYER installed, so the launcher can offer a "Launch with Decal"
    /// mode. Read-only: it never writes the registry, the disk or the state, and it never downloads,
    /// installs, modifies or configures Decal. It reads the agent path the Decal installer recorded
    /// and checks that <c>Inject.dll</c> is present beside it, exactly as other Asheron's Call
    /// launchers (ThwargLauncher, Mag-ACClientLauncher) do. Decal is the player's own software and the
    /// launcher only reports whether it is there.
    /// </summary>
    public sealed class DecalLocator
    {
        /// <summary>
        /// Registry key (under HKLM, 32-bit view, so the <c>WOW6432Node</c> branch on 64-bit Windows)
        /// that the Decal installer writes. <see cref="IRegistryReader"/> already reads the 32-bit view.
        /// </summary>
        public const string AgentKeyPath = "SOFTWARE\\Decal\\Agent";

        /// <summary>The value under <see cref="AgentKeyPath"/> holding Decal's install folder.</summary>
        public const string AgentPathValue = "AgentPath";

        /// <summary>The injection entry-point library Decal ships.</summary>
        public const string InjectDllName = "Inject.dll";

        private readonly IRegistryReader _registry;
        private readonly IAcFileSystem _fileSystem;
        private readonly Func<string, string> _readFileVersion;

        /// <summary>Creates a locator over the real registry and file system.</summary>
        public DecalLocator(IRegistryReader registry, IAcFileSystem fileSystem)
            : this(registry, fileSystem, ReadFileVersion)
        {
        }

        /// <summary>Creates a locator over the given seams (tests). The version reader may be null.</summary>
        public DecalLocator(IRegistryReader registry, IAcFileSystem fileSystem, Func<string, string> readFileVersion)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
            _readFileVersion = readFileVersion ?? (_ => null);
        }

        /// <summary>
        /// The player's Decal <c>Inject.dll</c>, or null when Decal is not installed, the registry value
        /// is missing or empty, or the file is not on disk. Never throws.
        /// </summary>
        public DecalLocation Locate()
        {
            string agentPath = _registry.GetStringValue(AgentKeyPath, AgentPathValue);
            if (string.IsNullOrWhiteSpace(agentPath))
            {
                return null;
            }

            string injectDll;
            try
            {
                injectDll = Path.Combine(agentPath.Trim(), InjectDllName);
            }
            catch (ArgumentException)
            {
                // The recorded path held an invalid character: treat Decal as not found.
                return null;
            }

            if (!_fileSystem.FileExists(injectDll))
            {
                return null;
            }

            string version = null;
            try
            {
                version = _readFileVersion(injectDll);
            }
            catch (Exception)
            {
                // The version is a diagnostic only; its absence never hides a present Decal.
            }

            return new DecalLocation(injectDll, version);
        }

        private static string ReadFileVersion(string path)
        {
            try
            {
                return System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileVersion;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                return null;
            }
        }
    }
}
