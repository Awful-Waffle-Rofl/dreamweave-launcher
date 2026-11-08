using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using DreamWeave.Launcher.Core.Detection;
using DreamWeave.Launcher.Core.Manifests;

namespace DreamWeave.Launcher.Core.Install
{
    /// <summary>
    /// Installs a zip-overlay component (the End-of-Retail data files) from a downloaded archive.
    /// <list type="number">
    /// <item>Plan: hash each target directly (never through the verify cache); a file already
    /// matching the manifest, or carrying the output hash of a byte patch that targets it, is
    /// skipped. Nothing to write means the archive is not even opened.</item>
    /// <item>Probe: every target that will be written is opened exclusively and every target
    /// folder probed for write access. A file in use fails the whole step with GameRunning before
    /// anything is touched; no write access means the work is handed to the elevated helper.</item>
    /// <item>Stage: the archive is scanned (any entry with an unsafe name, such as a zip-slip
    /// <c>..</c> path, refuses the whole archive; entries the manifest does not list are reported
    /// in <see cref="ZipOverlayResult.UnexpectedEntries"/> and never extracted). Only entries whose
    /// normalized name equals a manifest <c>files[].path</c> are streamed, each to the same relative
    /// path under the component's fixed ready folder (<see cref="VerifiedPlacementRunner.ReadyFolder"/>),
    /// and checked for size and SHA-256. A file already there from an earlier, failed elevated
    /// placement is reused when it still verifies. A staged file stays open with write sharing
    /// denied from verification until it has been copied.</item>
    /// <item>Write: only after every file is staged and verified, the targets are guarded again
    /// and each is replaced atomically (see <see cref="AcWriter"/>). When the folder is not
    /// writable, the ready folder is trimmed to exactly the files to write and the result carries
    /// a <see cref="PlacementRequest"/> for the elevated helper instead.</item>
    /// </list>
    /// The ready folder is deleted after a direct install and after any failure here; after a
    /// hand-off it is kept until the caller has seen the placement succeed.
    /// Run from a background thread: it reads and writes over a gigabyte.
    /// </summary>
    public sealed class ZipOverlayInstaller
    {
        private readonly AppPaths _paths;
        private readonly Downloads.IDiskSpace _disk;

        /// <summary>
        /// Creates an installer staging under <paramref name="paths"/>'s <see cref="AppPaths.StagingDir"/>,
        /// in the component's own folder (<see cref="Downloads.DownloadRequest.ComponentFolder"/>).
        /// </summary>
        /// <param name="paths">Launcher paths.</param>
        /// <param name="diskSpace">Free-space query; <see cref="Downloads.SystemDiskSpace"/> when null.</param>
        public ZipOverlayInstaller(AppPaths paths, Downloads.IDiskSpace diskSpace = null)
        {
            _paths = paths ?? throw new ArgumentNullException(nameof(paths));
            _disk = diskSpace ?? new Downloads.SystemDiskSpace();
        }

        /// <summary>Installs <paramref name="component"/> from <paramref name="zipPath"/> into <paramref name="acFolder"/>.</summary>
        /// <exception cref="LauncherException">
        /// GameRunning, ArchiveInvalid, SizeMismatch, HashMismatch, ReparsePointInPath, ManifestInvalid,
        /// Cancelled, DiskFull, AccessDenied. On any of these before the write phase, the game folder is unchanged.
        /// </exception>
        public ZipOverlayResult Install(LauncherManifest manifest, ManifestComponent component, string zipPath, string acFolder, IProgress<ProgressReport> progress = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (manifest == null)
            {
                throw new ArgumentNullException(nameof(manifest));
            }

            if (component == null)
            {
                throw new ArgumentNullException(nameof(component));
            }

            if (component.Kind != ComponentKinds.ZipOverlay || component.Files == null || component.Files.Count == 0)
            {
                throw new LauncherException(LauncherError.ManifestInvalid, "Component '" + component.Id + "' is not a zip overlay with files.");
            }

            RequireFolder(acFolder);

            // 1. Plan.
            var toWrite = new List<ManifestFile>();
            var skipped = new List<string>();
            foreach (ManifestFile f in component.Files)
            {
                ThrowIfCancelled(cancellationToken);
                if (OverlayFiles.IsCurrent(manifest, acFolder, f, progress, cancellationToken))
                {
                    skipped.Add(f.Path);
                }
                else
                {
                    toWrite.Add(f);
                }
            }

            if (toWrite.Count == 0)
            {
                // Already done: nothing may be left waiting for a placement.
                VerifiedPlacementRunner.ClearReady(_paths, component.Id);
                return new ZipOverlayResult(InstallOutcome.NothingToDo, new string[0], skipped, new string[0], null);
            }

            // 2. Probe early, so a running client fails fast instead of after a long extraction.
            bool needsElevation;
            using (AcWriter.GuardSet probe = AcWriter.Acquire(acFolder, toWrite.Select(f => f.Path)))
            {
                needsElevation = probe == null;
            }

            // Known now: refuse before a long extraction that could never be placed. (Checked again
            // at hand-off, for a folder that turns unwritable between the probe and the write.)
            if (needsElevation)
            {
                RequirePlaceableId(component.Id);
            }

            // Free space: every staged copy at once, and the largest .dwtmp beside its target
            // (files are replaced one at a time, so only one temp exists in the game folder).
            // On one volume both exist together, so the volume must hold the sum.
            AcWriter.RequireFreeSpaceCombined(_disk, _paths.StagingDir, toWrite.Sum(f => f.Size), "the unpacked game files", acFolder, toWrite.Max(f => f.Size), "the game folder");

            // 3. Stage, inside this component's own fixed ready folder.
            string stagingFolder = Path.Combine(Downloads.DownloadRequest.ComponentFolder(_paths.StagingDir, component.Id), VerifiedPlacementRunner.ReadyFolderName);
            var staged = new List<KeyValuePair<ManifestFile, FileStream>>();
            var unexpected = new List<string>();
            bool handedOff = false;
            try
            {
                try
                {
                    Directory.CreateDirectory(stagingFolder);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    throw LauncherException.FromIo(ex, "creating the staging folder");
                }

                // The ready folder can outlive a failed placement: never stage or tidy through a link in it.
                VerifiedPlacementRunner.RemoveLinks(stagingFolder);
                Stage(component, zipPath, toWrite, stagingFolder, staged, unexpected, progress, cancellationToken);

                // 4. Write (or hand off).
                var written = new List<string>();
                if (!needsElevation)
                {
                    var items = staged.Select(s => new KeyValuePair<ManifestFile, Stream>(s.Key, s.Value)).ToList();
                    if (OverlayFiles.TryWriteAll(acFolder, items, written, cancellationToken))
                    {
                        return new ZipOverlayResult(InstallOutcome.Completed, written, skipped, unexpected, null);
                    }
                }

                RequirePlaceableId(component.Id);

                // The elevated helper refuses ANY file it does not expect, so leave exactly the files to write.
                RemoveUnlisted(stagingFolder, toWrite);
                foreach (KeyValuePair<ManifestFile, FileStream> s in staged)
                {
                    s.Value.Dispose();
                }

                handedOff = true;
                return new ZipOverlayResult(InstallOutcome.ElevationRequired, new string[0], skipped, unexpected, new PlacementRequest(_paths.Root, acFolder, component.Id));
            }
            finally
            {
                foreach (KeyValuePair<ManifestFile, FileStream> s in staged)
                {
                    s.Value.Dispose();
                }

                if (!handedOff)
                {
                    AcWriter.TryDeleteFolder(stagingFolder);
                }
            }
        }

        private static void Stage(ManifestComponent component, string zipPath, List<ManifestFile> toWrite, string stagingFolder, List<KeyValuePair<ManifestFile, FileStream>> staged, List<string> unexpected, IProgress<ProgressReport> progress, CancellationToken cancellationToken)
        {
            FileStream zipStream;
            try
            {
                zipStream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.None);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw LauncherException.FromIo(ex, "opening the downloaded archive");
            }

            using (zipStream)
            {
                ZipArchive archive;
                try
                {
                    archive = new ZipArchive(zipStream, ZipArchiveMode.Read, false);
                }
                catch (InvalidDataException ex)
                {
                    throw new LauncherException(LauncherError.ArchiveInvalid, "The downloaded archive is not a readable zip file.", ex);
                }

                using (archive)
                {
                    Dictionary<string, ZipArchiveEntry> found = MapEntries(component, archive, unexpected);
                    foreach (ManifestFile f in toWrite)
                    {
                        ThrowIfCancelled(cancellationToken);
                        ZipArchiveEntry entry;
                        if (!found.TryGetValue(Canonical(f.Path), out entry))
                        {
                            throw new LauncherException(LauncherError.ArchiveInvalid, "The downloaded archive does not contain " + f.Path + ".");
                        }

                        staged.Add(new KeyValuePair<ManifestFile, FileStream>(f, Extract(entry, f, AcPathSafety.Resolve(stagingFolder, f.Path), progress, cancellationToken)));
                    }
                }
            }
        }

        /// <summary>Validates every entry name and maps the manifest's files to their entries.</summary>
        private static Dictionary<string, ZipArchiveEntry> MapEntries(ManifestComponent component, ZipArchive archive, List<string> unexpected)
        {
            var wanted = new HashSet<string>(component.Files.Select(f => Canonical(f.Path)), StringComparer.OrdinalIgnoreCase);
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string w in wanted)
            {
                for (int slash = w.LastIndexOf('/'); slash > 0; slash = w.LastIndexOf('/', slash - 1))
                {
                    folders.Add(w.Substring(0, slash));
                }
            }

            var found = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            IReadOnlyCollection<ZipArchiveEntry> entries;
            try
            {
                entries = archive.Entries;
            }
            catch (InvalidDataException ex)
            {
                throw new LauncherException(LauncherError.ArchiveInvalid, "The downloaded archive's directory is damaged.", ex);
            }

            foreach (ZipArchiveEntry entry in entries)
            {
                string name = (entry.FullName ?? string.Empty).Replace('\\', '/');
                bool isFolder = name.EndsWith("/", StringComparison.Ordinal);
                string trimmed = isFolder ? name.Substring(0, name.Length - 1) : name;
                string reason = AcPathSafety.CheckRelativePath(trimmed);
                if (reason != null)
                {
                    throw new LauncherException(LauncherError.ArchiveInvalid, "The downloaded archive has an entry with an unsafe name ('" + entry.FullName + "': " + reason + "), so none of it was used.");
                }

                if (isFolder)
                {
                    if (!folders.Contains(trimmed))
                    {
                        unexpected.Add(entry.FullName);
                    }

                    continue;
                }

                if (!wanted.Contains(trimmed))
                {
                    unexpected.Add(entry.FullName);
                    continue;
                }

                if (found.ContainsKey(trimmed))
                {
                    throw new LauncherException(LauncherError.ArchiveInvalid, "The downloaded archive contains " + trimmed + " more than once.");
                }

                found[trimmed] = entry;
            }

            return found;
        }

        private static FileStream Extract(ZipArchiveEntry entry, ManifestFile f, string stagedPath, IProgress<ProgressReport> progress, CancellationToken cancellationToken)
        {
            if (entry.Length != f.Size)
            {
                throw new LauncherException(LauncherError.SizeMismatch, f.Path + " in the downloaded archive is " + entry.Length + " bytes; expected " + f.Size + ".");
            }

            FileStream reused = TryReuseStaged(stagedPath, f, progress, cancellationToken);
            if (reused != null)
            {
                return reused;
            }

            FileStream fs;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(stagedPath));

                // Write sharing denied from here until the copy into the game folder is done.
                fs = new FileStream(stagedPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 81920, FileOptions.SequentialScan);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw LauncherException.FromIo(ex, "staging " + f.Path);
            }

            try
            {
                long total = 0;
                using (Stream source = entry.Open())
                {
                    byte[] buffer = new byte[1024 * 1024];
                    while (true)
                    {
                        ThrowIfCancelled(cancellationToken);
                        int read = source.Read(buffer, 0, buffer.Length);
                        if (read <= 0)
                        {
                            break;
                        }

                        total += read;
                        if (total > f.Size)
                        {
                            throw new LauncherException(LauncherError.SizeMismatch, f.Path + " in the downloaded archive is larger than expected.");
                        }

                        fs.Write(buffer, 0, read);
                    }
                }

                if (total != f.Size)
                {
                    throw new LauncherException(LauncherError.SizeMismatch, f.Path + " in the downloaded archive is " + total + " bytes; expected " + f.Size + ".");
                }

                fs.Flush(true);
                fs.Position = 0;
                string hash;
                try
                {
                    hash = Hasher.ComputeSha256Hex(fs, f.Size, progress, cancellationToken, "verify:" + f.Path);
                }
                catch (OperationCanceledException ex)
                {
                    throw new LauncherException(LauncherError.Cancelled, "Cancelled.", ex);
                }

                if (!Hasher.FixedTimeEquals(hash, f.Sha256))
                {
                    throw new LauncherException(LauncherError.HashMismatch, f.Path + " in the downloaded archive failed its integrity check, so nothing was installed.");
                }

                fs.Position = 0;
                return fs;
            }
            catch (InvalidDataException ex)
            {
                fs.Dispose();
                throw new LauncherException(LauncherError.ArchiveInvalid, f.Path + " in the downloaded archive is damaged.", ex);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                fs.Dispose();
                throw LauncherException.FromIo(ex, "staging " + f.Path);
            }
            catch
            {
                fs.Dispose();
                throw;
            }
        }

        /// <summary>
        /// A copy left in the ready folder by an earlier hand-off whose placement failed: opened
        /// with write sharing denied and verified through that handle. Returns it when it still
        /// matches the manifest; otherwise deletes it and returns null (extract again).
        /// </summary>
        private static FileStream TryReuseStaged(string stagedPath, ManifestFile f, IProgress<ProgressReport> progress, CancellationToken cancellationToken)
        {
            if (!File.Exists(stagedPath))
            {
                return null;
            }

            try
            {
                var fs = new FileStream(stagedPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
                bool keep = false;
                try
                {
                    if (fs.Length == f.Size)
                    {
                        string hash = Hasher.ComputeSha256Hex(fs, f.Size, progress, cancellationToken, "verify:" + f.Path);
                        keep = Hasher.FixedTimeEquals(hash, f.Sha256);
                    }

                    if (keep)
                    {
                        fs.Position = 0;
                        return fs;
                    }
                }
                catch (OperationCanceledException ex)
                {
                    throw new LauncherException(LauncherError.Cancelled, "Cancelled.", ex);
                }
                finally
                {
                    if (!keep)
                    {
                        fs.Dispose();
                    }
                }

                File.Delete(stagedPath);
                return null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw LauncherException.FromIo(ex, "staging " + f.Path);
            }
        }

        private static void RequirePlaceableId(string componentId)
        {
            if (!VerifiedPlacementRunner.IsSafeComponentId(componentId))
            {
                throw new LauncherException(LauncherError.ElevatedTaskInvalid, "Component '" + componentId + "' cannot be placed with administrator rights because its id is not a plain lowercase name.");
            }
        }

        /// <summary>
        /// Deletes every file under <paramref name="readyFolder"/> that is not one of
        /// <paramref name="keep"/>. Never descends into a junction or link: a link is removed
        /// itself (never what it points to).
        /// </summary>
        internal static void RemoveUnlisted(string readyFolder, List<ManifestFile> keep)
        {
            var wanted = new HashSet<string>(keep.Select(f => Path.GetFullPath(AcPathSafety.Resolve(readyFolder, f.Path))), StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!Directory.Exists(readyFolder))
                {
                    return;
                }

                var pending = new Stack<DirectoryInfo>();
                pending.Push(new DirectoryInfo(readyFolder));
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
                        else if (!wanted.Contains(Path.GetFullPath(entry.FullName)))
                        {
                            File.Delete(entry.FullName);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw LauncherException.FromIo(ex, "tidying the staged files");
            }
        }

        internal static string Canonical(string path)
        {
            return (path ?? string.Empty).Replace('\\', '/');
        }

        internal static void RequireFolder(string acFolder)
        {
            if (string.IsNullOrWhiteSpace(acFolder) || !Path.IsPathRooted(acFolder) || !Directory.Exists(acFolder))
            {
                throw new ArgumentException("The game folder '" + acFolder + "' does not exist.", nameof(acFolder));
            }
        }

        private static void ThrowIfCancelled(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new LauncherException(LauncherError.Cancelled, "Cancelled.");
            }
        }
    }

    /// <summary>Overlay logic shared by <see cref="ZipOverlayInstaller"/> and <see cref="VerifiedPlacementRunner"/>.</summary>
    internal static class OverlayFiles
    {
        /// <summary>
        /// True when the target already holds the manifest's bytes, or the output of a byte patch
        /// that targets it (so a repair never silently reverts the player's opted-in patch). Hashes
        /// the file directly; the verify cache is never consulted for a write decision.
        /// </summary>
        /// <exception cref="LauncherException">GameRunning when the target cannot even be read because it is in use.</exception>
        internal static bool IsCurrent(LauncherManifest manifest, string acFolder, ManifestFile f, IProgress<ProgressReport> progress, CancellationToken cancellationToken)
        {
            string full = AcPathSafety.ResolveForWrite(acFolder, f.Path);
            string patchOutput = PatchedFiles.OutputFor(manifest.Components, f.Path, f.Sha256);
            FileStream fs;
            try
            {
                fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan);
            }
            catch (Exception ex) when (ex is FileNotFoundException || ex is DirectoryNotFoundException || ex is UnauthorizedAccessException)
            {
                return false;
            }
            catch (IOException ex) when (AcWriter.IsInUse(ex))
            {
                throw AcWriter.GameRunning(f.Path, ex);
            }
            catch (IOException ex)
            {
                throw LauncherException.FromIo(ex, "checking " + f.Path);
            }

            using (fs)
            {
                if (fs.Length != f.Size && patchOutput == null)
                {
                    return false;
                }

                string hash;
                try
                {
                    hash = Hasher.ComputeSha256Hex(fs, fs.Length, progress, cancellationToken, "check:" + f.Path);
                }
                catch (OperationCanceledException ex)
                {
                    throw new LauncherException(LauncherError.Cancelled, "Cancelled.", ex);
                }
                catch (IOException ex)
                {
                    throw LauncherException.FromIo(ex, "checking " + f.Path);
                }

                return (fs.Length == f.Size && Hasher.FixedTimeEquals(hash, f.Sha256))
                    || (patchOutput != null && Hasher.FixedTimeEquals(hash, patchOutput));
            }
        }

        /// <summary>
        /// Guards every target, then replaces each from its verified staged stream. Returns false
        /// (nothing written) when the current user cannot write the game folder.
        /// </summary>
        internal static bool TryWriteAll(string acFolder, IList<KeyValuePair<ManifestFile, Stream>> items, List<string> written, CancellationToken cancellationToken)
        {
            using (AcWriter.GuardSet guards = AcWriter.Acquire(acFolder, items.Select(i => i.Key.Path)))
            {
                if (guards == null)
                {
                    return false;
                }

                foreach (KeyValuePair<ManifestFile, Stream> item in items)
                {
                    try
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            throw new LauncherException(LauncherError.Cancelled, "Cancelled.");
                        }

                        item.Value.Position = 0;
                        AcWriter.ReplaceFrom(acFolder, item.Key.Path, item.Value, item.Key.Size, item.Key.Sha256, cancellationToken);
                        written.Add(item.Key.Path);
                    }
                    catch (LauncherException ex) when (written.Count > 0)
                    {
                        // Each file is replaced atomically, but the set is not: say what already changed.
                        throw new LauncherException(ex.Code, ex.Detail + " Already replaced: " + string.Join(", ", written) + ".", ex);
                    }
                }
            }

            return true;
        }
    }
}
