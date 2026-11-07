using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using DreamWeave.Launcher.Core.Detection;
using DreamWeave.Launcher.Core.Manifests;

namespace DreamWeave.Launcher.Core.Install
{
    /// <summary>Outcome of <see cref="ElevatedTaskRunner.Execute"/>.</summary>
    public sealed class ElevatedTaskResult
    {
        internal ElevatedTaskResult(LauncherError code, string detail)
        {
            Code = code;
            Detail = detail;
        }

        /// <summary><see cref="LauncherError.None"/> on success; also the process exit code of <c>--elevated-copy</c>.</summary>
        public LauncherError Code { get; }

        /// <summary>Plain-English detail.</summary>
        public string Detail { get; }
    }

    /// <summary>
    /// The body of the launcher's elevated <c>--elevated-copy &lt;task&gt;</c> mode. It trusts the
    /// task file for LOCATIONS and component ids only (see <see cref="ElevatedTask"/>):
    /// <list type="bullet">
    /// <item>The manifest comes from the launcher's own signed cache, re-verified here against the
    /// trusted keys (and the rollback floor from state); a manifest that does not verify stops
    /// everything.</item>
    /// <item>Every staged file must be named by the verified component, is opened with write and
    /// delete sharing denied, and is re-hashed through that handle against the manifest before
    /// anything is written; the same handle is then the copy source, so the verified bytes are the
    /// written bytes.</item>
    /// <item>The game folder must be an existing install (contains acclient.exe), and every write
    /// goes through the same guarded, reparse-checked path as the unelevated installers.</item>
    /// <item>Every location from the task (launcher data root, game folder, staged files) must be
    /// on a local fixed drive (no UNC, device or <c>\\?\</c> form); the data root's manifest
    /// cache and every staged file must have no junction or link on the way to them.</item>
    /// <item>Before it has verified anything the helper WRITES NOTHING: it reads the cache file
    /// directly (<c>ManifestProvider.ReadCacheReadOnly</c>) and never opens the launcher's state
    /// store, which would create lock files and could rename a "corrupt" file in whatever folder
    /// the task named. The cost: the elevated rollback floor is only the compiled-in
    /// <see cref="TrustedKeys.MinimumSerial"/> plus the cache's own serial, not the state file's
    /// highest serial. That is acceptable because anyone able to roll the cache back can roll the
    /// (equally user-writable) state file back too.</item>
    /// </list>
    /// <para>
    /// Threat model: the elevated process runs the launcher exe from its per-user, USER-WRITABLE
    /// install folder. It therefore protects against tampered DATA (task file, staged files,
    /// cached manifest) but not against tampered CODE: malware running as the user could replace
    /// the exe itself. Elevation is a fallback, not a step to take routinely: when the game folder
    /// is writable for the user (the default <c>C:\Turbine</c> normally is, because folders created
    /// under the drive root inherit Authenticated Users: Modify) the executors write directly and
    /// never ask for it.
    /// </para>
    /// Never throws: every failure becomes a non-zero <see cref="ElevatedTaskResult.Code"/>.
    /// </summary>
    public sealed class ElevatedTaskRunner
    {
        private readonly ManifestVerifier _verifier;

        /// <summary>Creates a runner trusting <paramref name="verifier"/>'s keys (production: <see cref="ManifestVerifier.CreateProduction"/>).</summary>
        public ElevatedTaskRunner(ManifestVerifier verifier)
        {
            _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        }

        /// <summary>Entry point for <c>--elevated-copy &lt;taskPath&gt;</c>; returns the process exit code (a <see cref="LauncherError"/> value, 0 on success).</summary>
        public static int Run(string taskPath)
        {
            return (int)new ElevatedTaskRunner(ManifestVerifier.CreateProduction()).Execute(taskPath).Code;
        }

        /// <summary>Loads, verifies and carries out the task at <paramref name="taskPath"/>.</summary>
        public ElevatedTaskResult Execute(string taskPath, CancellationToken cancellationToken = default(CancellationToken))
        {
            try
            {
                ElevatedTask task = ElevatedTask.Load(taskPath);
                string acReason = AcPathSafety.CheckLocalFixedPath(task.AcFolder);
                if (acReason != null)
                {
                    throw new LauncherException(LauncherError.ElevatedTaskInvalid, "The elevated task names a game folder that is not allowed: " + acReason + ".");
                }

                LauncherManifest manifest = LoadVerifiedManifest(task.AppRoot);
                string acFolder = AcInstallLocator.Normalize(task.AcFolder);
                if (acFolder == null || !new AcInstallLocator(new NoRegistry(), new RealAcFileSystem()).ValidateFolder(acFolder))
                {
                    throw new LauncherException(LauncherError.ElevatedTaskInvalid, "The elevated task names a folder that is not an Asheron's Call install.");
                }

                foreach (ElevatedTaskItem item in task.Items)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw new LauncherException(LauncherError.Cancelled, "Cancelled.");
                    }

                    ManifestComponent component = item == null ? null : manifest.Components.FirstOrDefault(c => string.Equals(c.Id, item.ComponentId, StringComparison.Ordinal));
                    if (component == null)
                    {
                        throw new LauncherException(LauncherError.ElevatedTaskInvalid, "The elevated task names a component the verified download list does not have.");
                    }

                    switch (item.Operation)
                    {
                        case ElevatedOperations.Overlay when component.Kind == ComponentKinds.ZipOverlay:
                            Overlay(manifest, component, item, acFolder, cancellationToken);
                            break;
                        case ElevatedOperations.PatchApply when component.Kind == ComponentKinds.BytePatch:
                            new BytePatcher(null).Apply(component, acFolder);
                            break;
                        case ElevatedOperations.PatchRevert when component.Kind == ComponentKinds.BytePatch:
                            new BytePatcher(null).Revert(component, acFolder);
                            break;
                        default:
                            throw new LauncherException(LauncherError.ElevatedTaskInvalid, "The elevated task asks for an operation component '" + component.Id + "' does not support.");
                    }
                }

                return new ElevatedTaskResult(LauncherError.None, "Done.");
            }
            catch (LauncherException ex)
            {
                return new ElevatedTaskResult(ex.Code, ex.Detail);
            }
            catch (Exception ex)
            {
                return new ElevatedTaskResult(LauncherError.Unknown, "Unexpected error: " + ex.Message);
            }
        }

        /// <summary>
        /// Reads and verifies the cached manifest under <paramref name="appRoot"/> WITHOUT writing
        /// anything. Location problems are ManifestUnavailable (the helper has no usable manifest);
        /// a cache that is present but fails verification keeps its own code (ManifestSignatureInvalid ...).
        /// </summary>
        private LauncherManifest LoadVerifiedManifest(string appRoot)
        {
            string reason = AcPathSafety.CheckLocalFixedPath(appRoot);
            if (reason != null)
            {
                throw new LauncherException(LauncherError.ManifestUnavailable, "The elevated task's launcher data folder is not allowed: " + reason + ".");
            }

            var paths = new AppPaths(appRoot);
            try
            {
                // Root included: every segment from appRoot down to the cache file.
                AcPathSafety.RefuseReparsePointsBelow(Path.GetDirectoryName(paths.Root) ?? paths.Root, paths.ManifestCacheFile, "The cached download list was not read");
            }
            catch (LauncherException ex)
            {
                throw new LauncherException(LauncherError.ManifestUnavailable, ex.Detail, ex);
            }

            LauncherError error;
            string detail;
            VerifiedManifest v = ManifestProvider.ReadCacheReadOnly(paths, _verifier, out error, out detail);
            if (v == null)
            {
                throw new LauncherException(error, "The elevated helper could not verify the cached download list: " + (detail ?? "no cache") + ".");
            }

            return v.Manifest;
        }

        private static void Overlay(LauncherManifest manifest, ManifestComponent component, ElevatedTaskItem item, string acFolder, CancellationToken cancellationToken)
        {
            if (item.Files == null || item.Files.Count == 0)
            {
                throw new LauncherException(LauncherError.ElevatedTaskInvalid, "The elevated task lists no files for component '" + component.Id + "'.");
            }

            var byPath = new Dictionary<string, ManifestFile>(StringComparer.OrdinalIgnoreCase);
            foreach (ManifestFile f in component.Files)
            {
                byPath[ZipOverlayInstaller.Canonical(f.Path)] = f;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var items = new List<KeyValuePair<ManifestFile, Stream>>();
            try
            {
                foreach (ElevatedTaskFile tf in item.Files)
                {
                    ManifestFile f;
                    if (tf == null || !byPath.TryGetValue(ZipOverlayInstaller.Canonical(tf.Path), out f) || !seen.Add(f.Path))
                    {
                        throw new LauncherException(LauncherError.ElevatedTaskInvalid, "The elevated task names a file component '" + component.Id + "' does not list, or names one twice.");
                    }

                    if (OverlayFiles.IsCurrent(manifest, acFolder, f, null, cancellationToken))
                    {
                        continue;
                    }

                    items.Add(new KeyValuePair<ManifestFile, Stream>(f, OpenVerifiedStaged(tf.Staged, f, cancellationToken)));
                }

                if (items.Count == 0)
                {
                    return;
                }

                var written = new List<string>();
                if (!OverlayFiles.TryWriteAll(acFolder, items, written, cancellationToken))
                {
                    throw new LauncherException(LauncherError.AccessDenied, "Windows denied access to the game folder even with administrator rights.");
                }
            }
            finally
            {
                foreach (KeyValuePair<ManifestFile, Stream> i in items)
                {
                    i.Value.Dispose();
                }
            }
        }

        /// <summary>Opens a staged file with write/delete sharing denied and verifies size and SHA-256 THROUGH that handle.</summary>
        private static Stream OpenVerifiedStaged(string staged, ManifestFile f, CancellationToken cancellationToken)
        {
            string reason = AcPathSafety.CheckLocalFixedPath(staged);
            if (reason != null)
            {
                throw new LauncherException(LauncherError.ElevatedTaskInvalid, "The elevated task names a staged file that is not allowed (" + reason + ").");
            }

            FileStream fs;
            try
            {
                // Every folder on the way and the file itself: no junctions or links.
                AcPathSafety.RefuseReparsePointsInPath(staged, "The staged copy of " + f.Path + " was not used");
                fs = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                throw new LauncherException(LauncherError.HashMismatch, "The staged copy of " + f.Path + " could not be opened, so it was not used.", ex);
            }

            try
            {
                if (fs.Length != f.Size)
                {
                    throw new LauncherException(LauncherError.SizeMismatch, "The staged copy of " + f.Path + " is the wrong size, so it was not used.");
                }

                string hash = Hasher.ComputeSha256Hex(fs, fs.Length, null, cancellationToken, "verify:" + f.Path);
                if (!Hasher.FixedTimeEquals(hash, f.Sha256))
                {
                    throw new LauncherException(LauncherError.HashMismatch, "The staged copy of " + f.Path + " failed its integrity check, so it was not used.");
                }

                fs.Position = 0;
                return fs;
            }
            catch (OperationCanceledException ex)
            {
                fs.Dispose();
                throw new LauncherException(LauncherError.Cancelled, "Cancelled.", ex);
            }
            catch
            {
                fs.Dispose();
                throw;
            }
        }

        private sealed class NoRegistry : IRegistryReader
        {
            public string GetStringValue(string subKeyPath, string valueName)
            {
                return null;
            }

            public IReadOnlyList<string> GetSubKeyNames(string subKeyPath)
            {
                return new string[0];
            }
        }
    }
}
