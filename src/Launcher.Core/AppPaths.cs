using System;
using System.IO;

namespace DreamWeave.Launcher.Core
{
    /// <summary>
    /// Every on-disk location the launcher owns, derived from one root folder. Production uses
    /// <see cref="CreateDefault"/> (rooted at %LOCALAPPDATA%); tests construct it on a temp folder.
    /// Nothing here creates folders; writers create what they need.
    /// </summary>
    public sealed class AppPaths
    {
        /// <summary>Creates the path set under <paramref name="root"/> (normally %LOCALAPPDATA%).</summary>
        public AppPaths(string root)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                throw new ArgumentException("A root folder is required.", nameof(root));
            }

            Root = Path.GetFullPath(root);
            InstallDir = Path.Combine(Root, "Programs", "DreamWeave Launcher");
            DataDir = Path.Combine(Root, "DreamWeave");
            StagingDir = Path.Combine(DataDir, "staging");
            StateFile = Path.Combine(DataDir, "state.json");
            ManifestCacheFile = Path.Combine(DataDir, "manifest.cache.json");
            LogFile = Path.Combine(DataDir, "launcher.log");
            LegacyManifestCacheFile = Path.Combine(DataDir, "manifest.json");
            LegacyManifestSigCacheFile = Path.Combine(DataDir, "manifest.json.sig");
        }

        /// <summary>Path set rooted at the current user's %LOCALAPPDATA%.</summary>
        public static AppPaths CreateDefault()
        {
            return new AppPaths(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        }

        /// <summary>The root everything else derives from.</summary>
        public string Root { get; }

        /// <summary>Where the launcher exe is installed: <c>Root\Programs\DreamWeave Launcher</c>.</summary>
        public string InstallDir { get; }

        /// <summary>Launcher data (state, manifest cache, staging): <c>Root\DreamWeave</c>.</summary>
        public string DataDir { get; }

        /// <summary>Scratch area for partial downloads and extraction: <c>DataDir\staging</c>.</summary>
        public string StagingDir { get; }

        /// <summary>The persisted <see cref="State.LauncherState"/>: <c>DataDir\state.json</c>.</summary>
        public string StateFile { get; }

        /// <summary>
        /// Last verified manifest AND its signature, kept together in one atomically written file
        /// (<c>DataDir\manifest.cache.json</c>, holding both as base64) so the pair can never be torn.
        /// </summary>
        public string ManifestCacheFile { get; }

        /// <summary>
        /// Rolling download log (<c>DataDir\launcher.log</c>, plus at most one <c>launcher.log.1</c>). It sits
        /// in <see cref="DataDir"/>, which uninstall deletes whole.
        /// </summary>
        public string LogFile { get; }

        /// <summary>Pre-release two-file cache location (manifest bytes). Never read; deleted after a successful cache write.</summary>
        internal string LegacyManifestCacheFile { get; }

        /// <summary>Pre-release two-file cache location (signature). Never read; deleted after a successful cache write.</summary>
        internal string LegacyManifestSigCacheFile { get; }
    }
}
