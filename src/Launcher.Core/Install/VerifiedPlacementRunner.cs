using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using DreamWeave.Launcher.Core.Detection;
using DreamWeave.Launcher.Core.Manifests;

namespace DreamWeave.Launcher.Core.Install
{
    /// <summary>Outcome of <see cref="VerifiedPlacementRunner.Execute"/>.</summary>
    public sealed class VerifiedPlacementResult
    {
        internal VerifiedPlacementResult(LauncherError code, string detail)
        {
            Code = code;
            Detail = detail;
        }

        /// <summary><see cref="LauncherError.None"/> on success; also the process exit code of <c>--place-verified</c>.</summary>
        public LauncherError Code { get; }

        /// <summary>Plain-English detail.</summary>
        public string Detail { get; }
    }

    /// <summary>
    /// The body of the launcher's elevated <c>--place-verified "&lt;appRoot&gt;" "&lt;acFolder&gt;" &lt;componentId&gt;</c>
    /// mode: the ONE thing the launcher ever does elevated. It has no operations and reads no task
    /// file. It places the files the unelevated launcher staged for one component, and only those
    /// the verified manifest names with the exact bytes it names:
    /// <list type="bullet">
    /// <item>The manifest comes from the launcher's own signed cache under <c>appRoot</c>, re-verified
    /// here against the trusted keys (and the rollback floor); a manifest that does not verify stops
    /// everything.</item>
    /// <item>The component id must be <c>[a-z0-9-]</c> only (no leading hyphen), so it is a plain
    /// folder name and a plain command-line word.</item>
    /// <item>The allowed set (relative path to SHA-256) is built ONLY from that component in that
    /// manifest: a zip overlay's <c>files[]</c>; a byte patch's <c>target</c> with
    /// <c>outputSha256</c> (apply) or <c>inputSha256</c> (revert).</item>
    /// <item>The staged files live at a fixed path, <see cref="ReadyFolder"/>. Any file there that
    /// is not in the allowed set, and any junction or link anywhere from <c>appRoot</c> down through
    /// the ready folder, refuses the whole request before anything is written. Each staged file is
    /// opened with write and delete sharing denied and hashed THROUGH that handle; the same handle
    /// is the copy source, so the verified bytes are the written bytes.</item>
    /// <item>The game folder must be an existing install (contains acclient.exe) on a local fixed
    /// drive, and every write goes through the same guarded, reparse-checked, atomic path as the
    /// unelevated installers (<see cref="AcWriter"/>).</item>
    /// <item>Before it has verified anything the helper WRITES NOTHING: it reads the cache file
    /// directly (<c>ManifestProvider.ReadCacheReadOnly</c>) and never opens the launcher's state
    /// store.</item>
    /// <item>Replay posture (accepted): the elevated rollback floor is the compiled-in
    /// <see cref="TrustedKeys.MinimumSerial"/> ONLY (<c>ReadCacheReadOnly</c> passes floor 0, and
    /// the cache's own serial is no floor over itself); state.json, which holds the highest serial
    /// seen, is deliberately not read. So an older manifest signed by a trusted key can be replayed
    /// through <c>appRoot</c>. Anyone able to roll the cache back can roll the (equally
    /// user-writable) state file back too, so reading it would add nothing.</item>
    /// <item>The game folder is pinned for the whole run: the folder itself and every existing
    /// folder on the way to a target must not be a junction or link, and each is held open with
    /// delete sharing denied (so it cannot be renamed, deleted or swapped for a junction) and
    /// checked to still be the folder its path names.</item>
    /// </list>
    /// <para>
    /// Patch logic never runs here: the unelevated <see cref="BytePatcher"/> builds the patched or
    /// reverted file itself (reading the game folder needs no elevation) and stages the result.
    /// </para>
    /// <para>
    /// Threat model: the elevated process runs the launcher exe from its per-user, USER-WRITABLE
    /// install folder. It protects against tampered DATA (arguments, staged files, cached manifest)
    /// but not against tampered CODE. Elevation is a fallback: when the game folder is writable for
    /// the user the installers write directly and never ask for it.
    /// </para>
    /// Never throws: every failure becomes a non-zero <see cref="VerifiedPlacementResult.Code"/>.
    /// </summary>
    public sealed class VerifiedPlacementRunner
    {
        /// <summary>Name of the per-component folder that holds verified files waiting to be placed.</summary>
        public const string ReadyFolderName = "ready";

        /// <summary>Longest component id accepted.</summary>
        public const int MaxComponentIdLength = 64;

        private readonly ManifestVerifier _verifier;

        /// <summary>
        /// Test seam: runs after the game folder and the folders on the way to every target are
        /// pinned, right before anything is written (the window a folder swap would use).
        /// </summary>
        internal Action<string> AfterPinned { get; set; }

        /// <summary>
        /// Test seam: false holds pinned folders WITHOUT their sentinel files, so a test can reach
        /// the last check (<see cref="FolderPins.RequireNoReparse"/>) on its own. Always true in production.
        /// </summary>
        internal bool PinSentinels { get; set; } = true;

        /// <summary>Creates a runner trusting <paramref name="verifier"/>'s keys (production: <see cref="ManifestVerifier.CreateProduction"/>).</summary>
        public VerifiedPlacementRunner(ManifestVerifier verifier)
        {
            _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        }

        /// <summary>Entry point for <c>--place-verified</c>; returns the process exit code (a <see cref="LauncherError"/> value, 0 on success).</summary>
        public static int Run(string appRoot, string acFolder, string componentId)
        {
            return (int)new VerifiedPlacementRunner(ManifestVerifier.CreateProduction()).Execute(appRoot, acFolder, componentId).Code;
        }

        /// <summary>True for a non-empty id of at most <see cref="MaxComponentIdLength"/> characters from <c>[a-z0-9-]</c>, not starting with a hyphen.</summary>
        public static bool IsSafeComponentId(string componentId)
        {
            if (string.IsNullOrEmpty(componentId) || componentId.Length > MaxComponentIdLength || componentId[0] == '-')
            {
                return false;
            }

            foreach (char ch in componentId)
            {
                if (!((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '-'))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>The fixed staging folder for <paramref name="componentId"/>: <c>StagingDir\&lt;componentId&gt;\ready</c>.</summary>
        public static string ReadyFolder(AppPaths paths, string componentId)
        {
            if (paths == null)
            {
                throw new ArgumentNullException(nameof(paths));
            }

            return Path.Combine(Downloads.DownloadRequest.ComponentFolder(paths.StagingDir, componentId), ReadyFolderName);
        }

        /// <summary>
        /// Unelevated side, before staging into a ready folder that may survive from an earlier
        /// attempt: removes every junction or link in it (the links themselves, never what they
        /// point to), so staging and tidying never follow one out of the folder.
        /// </summary>
        /// <exception cref="LauncherException">On an I/O failure while removing a link.</exception>
        internal static void RemoveLinks(string ready)
        {
            try
            {
                if (!Directory.Exists(ready) && !File.Exists(ready))
                {
                    return;
                }

                if ((File.GetAttributes(ready) & FileAttributes.ReparsePoint) != 0)
                {
                    Directory.Delete(ready, false);
                    return;
                }

                var pending = new Stack<DirectoryInfo>();
                pending.Push(new DirectoryInfo(ready));
                while (pending.Count > 0)
                {
                    foreach (FileSystemInfo entry in pending.Pop().GetFileSystemInfos())
                    {
                        bool isDirectory = (entry.Attributes & FileAttributes.Directory) != 0;
                        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            if (isDirectory)
                            {
                                Directory.Delete(entry.FullName, false);
                            }
                            else
                            {
                                File.Delete(entry.FullName);
                            }
                        }
                        else if (isDirectory)
                        {
                            pending.Push((DirectoryInfo)entry);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw LauncherException.FromIo(ex, "clearing the staging folder");
            }
        }

        /// <summary>
        /// Unelevated side: removes the ready folder of <paramref name="componentId"/> (links first,
        /// never followed). A no-op without paths or for an id that is no folder name.
        /// </summary>
        internal static void ClearReady(AppPaths paths, string componentId)
        {
            if (paths == null || !Downloads.DownloadRequest.IsSafeFolderName(componentId))
            {
                return;
            }

            string ready = ReadyFolder(paths, componentId);
            try
            {
                RemoveLinks(ready);
            }
            catch (LauncherException)
            {
                // Best effort, as the delete below.
            }

            AcWriter.TryDeleteFolder(ready);
        }

        /// <summary>Verifies the request and places the staged files of <paramref name="componentId"/>.</summary>
        public VerifiedPlacementResult Execute(string appRoot, string acFolder, string componentId, CancellationToken cancellationToken = default(CancellationToken))
        {
            try
            {
                if (!IsSafeComponentId(componentId))
                {
                    throw Invalid("The placement request names a component id that is not allowed.");
                }

                if (string.IsNullOrWhiteSpace(appRoot) || string.IsNullOrWhiteSpace(acFolder))
                {
                    throw Invalid("The placement request is incomplete.");
                }

                string acReason = AcPathSafety.CheckLocalFixedPath(acFolder);
                if (acReason != null)
                {
                    throw Invalid("The placement request names a game folder that is not allowed: " + acReason + ".");
                }

                LauncherManifest manifest = LoadVerifiedManifest(appRoot);
                string ac = AcInstallLocator.Normalize(acFolder);
                if (ac == null || !new AcInstallLocator(new NoRegistry(), new RealAcFileSystem()).ValidateFolder(ac))
                {
                    throw Invalid("The placement request names a folder that is not an Asheron's Call install.");
                }

                // The folder itself, and every folder above it: no junction or link (an install moved
                // behind a junction is not supported elevated). Pinned below, so it stays that way.
                AcPathSafety.RefuseReparsePointsInPath(ac, "Nothing was placed");

                ManifestComponent component = manifest.Components.FirstOrDefault(c => string.Equals(c.Id, componentId, StringComparison.Ordinal));
                if (component == null)
                {
                    throw Invalid("The placement request names a component the verified download list does not have.");
                }

                var paths = new AppPaths(appRoot);
                string ready = ReadyFolder(paths, componentId);
                List<string> staged = ListStaged(paths, ready);

                using (var pins = new FolderPins(PinSentinels))
                {
                    pins.Pin(ac);
                    switch (component.Kind)
                    {
                        case ComponentKinds.ZipOverlay:
                            PlaceOverlay(manifest, component, ready, staged, ac, pins, cancellationToken);
                            break;
                        case ComponentKinds.BytePatch:
                            PlacePatch(component, ready, staged, ac, pins, cancellationToken);
                            break;
                        default:
                            throw Invalid("Component '" + component.Id + "' has nothing that can be placed.");
                    }
                }

                return new VerifiedPlacementResult(LauncherError.None, "Done.");
            }
            catch (LauncherException ex)
            {
                return new VerifiedPlacementResult(ex.Code, ex.Detail);
            }
            catch (Exception ex)
            {
                return new VerifiedPlacementResult(LauncherError.Unknown, "Unexpected error: " + ex.Message);
            }
        }

        private static LauncherException Invalid(string detail)
        {
            return new LauncherException(LauncherError.ElevatedTaskInvalid, detail);
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
                throw new LauncherException(LauncherError.ManifestUnavailable, "The placement request's launcher data folder is not allowed: " + reason + ".");
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

        /// <summary>
        /// Every file under <paramref name="ready"/>, as canonical relative paths. Refuses a junction
        /// or link on the way from appRoot to the ready folder and anywhere inside it (never followed).
        /// </summary>
        private static List<string> ListStaged(AppPaths paths, string ready)
        {
            AcPathSafety.RefuseReparsePointsBelow(Path.GetDirectoryName(paths.Root) ?? paths.Root, ready, "The staged files were not used");
            if (!Directory.Exists(ready))
            {
                throw Invalid("Nothing is staged for this component.");
            }

            var found = new List<string>();
            var pending = new Stack<KeyValuePair<DirectoryInfo, string>>();
            pending.Push(new KeyValuePair<DirectoryInfo, string>(new DirectoryInfo(ready), string.Empty));
            try
            {
                while (pending.Count > 0)
                {
                    KeyValuePair<DirectoryInfo, string> dir = pending.Pop();
                    foreach (FileSystemInfo entry in dir.Key.GetFileSystemInfos())
                    {
                        string rel = dir.Value.Length == 0 ? entry.Name : dir.Value + "/" + entry.Name;
                        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            throw new LauncherException(
                                LauncherError.ReparsePointInPath,
                                "The staged files were not used because '" + AcPathSafety.ToDisplayPath(entry.FullName) + "' is a junction or link.");
                        }

                        if ((entry.Attributes & FileAttributes.Directory) != 0)
                        {
                            pending.Push(new KeyValuePair<DirectoryInfo, string>((DirectoryInfo)entry, rel));
                        }
                        else
                        {
                            found.Add(rel);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw LauncherException.FromIo(ex, "listing the staged files");
            }

            if (found.Count == 0)
            {
                throw Invalid("Nothing is staged for this component.");
            }

            return found;
        }

        private void PinTargets(FolderPins pins, string acFolder, IEnumerable<string> relativePaths)
        {
            foreach (string rel in relativePaths)
            {
                pins.PinFoldersOnTheWay(acFolder, rel);
            }

            AfterPinned?.Invoke(acFolder);
        }

        private void PlaceOverlay(LauncherManifest manifest, ManifestComponent component, string ready, List<string> staged, string acFolder, FolderPins pins, CancellationToken cancellationToken)
        {
            var allowed = new Dictionary<string, ManifestFile>(StringComparer.OrdinalIgnoreCase);
            foreach (ManifestFile f in component.Files ?? new List<ManifestFile>())
            {
                allowed[ZipOverlayInstaller.Canonical(f.Path)] = f;
            }

            var files = new List<ManifestFile>();
            foreach (string rel in staged)
            {
                ManifestFile f;
                if (!allowed.TryGetValue(rel, out f))
                {
                    throw Invalid("A staged file is not one component '" + component.Id + "' lists, so nothing was placed.");
                }

                files.Add(f);
            }

            PinTargets(pins, acFolder, files.Select(f => f.Path));
            var items = new List<KeyValuePair<ManifestFile, Stream>>();
            try
            {
                foreach (ManifestFile f in files)
                {
                    ThrowIfCancelled(cancellationToken);
                    if (OverlayFiles.IsCurrent(manifest, acFolder, f, null, cancellationToken))
                    {
                        continue;
                    }

                    string hash;
                    items.Add(new KeyValuePair<ManifestFile, Stream>(f, OpenVerifiedStaged(AcPathSafety.Resolve(ready, f.Path), f.Path, f.Size, new[] { f.Sha256 }, out hash, cancellationToken)));
                }

                if (items.Count == 0)
                {
                    return;
                }

                if (!OverlayFiles.TryWriteAll(acFolder, items, new List<string>(), cancellationToken, pins.RequireNoReparse))
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

        private void PlacePatch(ManifestComponent component, string ready, List<string> staged, string acFolder, FolderPins pins, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(component.Target) || !Hasher.IsSha256Hex(component.InputSha256) || !Hasher.IsSha256Hex(component.OutputSha256))
            {
                throw new LauncherException(LauncherError.ManifestInvalid, "Component '" + component.Id + "' is not a usable byte patch.");
            }

            string target = ZipOverlayInstaller.Canonical(component.Target);
            if (staged.Count != 1 || !string.Equals(staged[0], target, StringComparison.OrdinalIgnoreCase))
            {
                throw Invalid("A staged file is not the target of component '" + component.Id + "', so nothing was placed.");
            }

            PinTargets(pins, acFolder, new[] { component.Target });
            string stagedHash;
            using (FileStream source = OpenVerifiedStaged(AcPathSafety.Resolve(ready, component.Target), component.Target, -1, new[] { component.OutputSha256, component.InputSha256 }, out stagedHash, cancellationToken))
            {
                // Applying places the output over the input; reverting places the input over the output.
                bool applying = Hasher.FixedTimeEquals(stagedHash, component.OutputSha256);
                string requiredNow = applying ? component.InputSha256 : component.OutputSha256;
                using (AcWriter.GuardSet guards = AcWriter.Acquire(acFolder, new[] { component.Target }))
                {
                    if (guards == null)
                    {
                        throw new LauncherException(LauncherError.AccessDenied, "Windows denied access to " + component.Target + " even with administrator rights.");
                    }

                    FileStream current = guards.Get(component.Target);
                    if (current == null)
                    {
                        throw new LauncherException(LauncherError.PatchInputMismatch, component.Target + " is not installed, so the patch cannot be placed.");
                    }

                    string currentHash;
                    try
                    {
                        current.Position = 0;
                        currentHash = Hasher.ComputeSha256Hex(current, current.Length, null, cancellationToken, "check:" + component.Target);
                    }
                    catch (OperationCanceledException ex)
                    {
                        throw new LauncherException(LauncherError.Cancelled, "Cancelled.", ex);
                    }
                    catch (IOException ex)
                    {
                        throw LauncherException.FromIo(ex, "checking " + component.Target);
                    }

                    if (Hasher.FixedTimeEquals(currentHash, stagedHash))
                    {
                        return;
                    }

                    if (!Hasher.FixedTimeEquals(currentHash, requiredNow))
                    {
                        throw new LauncherException(LauncherError.PatchInputMismatch, component.Target + " is not the file this patch was made for, so it was left unchanged.");
                    }

                    source.Position = 0;
                    string targetFull = AcPathSafety.Resolve(acFolder, component.Target);
                    pins.RequireNoReparse(targetFull);
                    AcWriter.ReplaceFrom(acFolder, component.Target, source, source.Length, stagedHash, cancellationToken, () => pins.RequireNoReparse(targetFull));
                }
            }
        }

        /// <summary>
        /// Opens a staged file with write/delete sharing denied and verifies its size (when
        /// <paramref name="size"/> is not negative) and SHA-256 THROUGH that handle against
        /// <paramref name="accepted"/>; returns the handle at position 0.
        /// </summary>
        private static FileStream OpenVerifiedStaged(string staged, string what, long size, string[] accepted, out string matched, CancellationToken cancellationToken)
        {
            matched = null;
            FileStream fs;
            try
            {
                // Every folder on the way and the file itself: no junctions or links.
                AcPathSafety.RefuseReparsePointsInPath(staged, "The staged copy of " + what + " was not used");
                fs = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                throw new LauncherException(LauncherError.HashMismatch, "The staged copy of " + what + " could not be opened, so it was not used.", ex);
            }

            try
            {
                if ((size >= 0 && fs.Length != size) || (size < 0 && fs.Length > BytePatcher.MaxTargetBytes))
                {
                    throw new LauncherException(LauncherError.SizeMismatch, "The staged copy of " + what + " is the wrong size, so it was not used.");
                }

                string hash = Hasher.ComputeSha256Hex(fs, fs.Length, null, cancellationToken, "verify:" + what);
                matched = accepted.FirstOrDefault(a => Hasher.FixedTimeEquals(hash, a));
                if (matched == null)
                {
                    throw new LauncherException(LauncherError.HashMismatch, "The staged copy of " + what + " failed its integrity check, so it was not used.");
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

        private static void ThrowIfCancelled(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new LauncherException(LauncherError.Cancelled, "Cancelled.");
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
