using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using DreamWeave.Launcher.Core.Detection;

namespace DreamWeave.Launcher.Core.Install
{
    /// <summary>
    /// The only code that writes into the game folder. Every path goes through
    /// <see cref="AcPathSafety.ResolveForWrite"/> (again right before each open and each replace).
    /// <para>
    /// A target is GUARDED before it is replaced: opened with read access and
    /// <see cref="FileShare.Delete"/> only. That open fails with a sharing violation when anything
    /// else has the file open (a running client: reported as GameRunning before any file is
    /// touched), and while it is held nobody else can open the file at all, yet
    /// <see cref="File.Replace(string, string, string, bool)"/> still succeeds (measured on
    /// Windows 11 with .NET Framework: Replace succeeds under a FileShare.Delete or
    /// FileShare.Read|Delete guard and fails with a sharing violation under FileShare.Read alone).
    /// </para>
    /// <para>
    /// New content goes to <c>&lt;target&gt;.dwtmp</c> in the same folder, is flushed to disk, then
    /// replaces the target atomically (<see cref="File.Move(string, string)"/> when the target is absent).
    /// </para>
    /// </summary>
    public static class AcWriter
    {
        /// <summary>Suffix of the sibling temp file a new version is written to before it replaces the target.</summary>
        internal const string TempSuffix = ".dwtmp";

        private const int ErrorSharingViolation = 32;
        private const int ErrorLockViolation = 33;
        private const int ReplaceAttempts = 5;

        /// <summary>
        /// Guards every existing target and probes every target folder for write access, before
        /// anything is changed. Returns null when the current user cannot write (the caller hands
        /// the work to the elevated helper); on success the caller owns (and must dispose) the guards.
        /// </summary>
        /// <exception cref="LauncherException">GameRunning when any target is in use (every guard taken so far is released); ReparsePointInPath.</exception>
        internal static GuardSet Acquire(string acFolder, IEnumerable<string> relativePaths)
        {
            var set = new GuardSet();
            try
            {
                var probedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string rel in relativePaths)
                {
                    string full = AcPathSafety.ResolveForWrite(acFolder, rel);
                    FileStream guard;
                    if (!TryOpenGuard(full, rel, out guard))
                    {
                        set.Dispose();
                        return null;
                    }

                    if (guard != null)
                    {
                        set.Add(rel, guard);
                    }

                    string dir = NearestExistingFolder(Path.GetDirectoryName(full));
                    if (probedDirs.Add(dir) && !CanCreateIn(dir))
                    {
                        set.Dispose();
                        return null;
                    }
                }

                return set;
            }
            catch
            {
                set.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Opens the guard on an existing target. Returns false when access is denied (elevation
        /// needed); sets <paramref name="guard"/> to null when the file does not exist.
        /// </summary>
        /// <exception cref="LauncherException">GameRunning when the file is in use.</exception>
        internal static bool TryOpenGuard(string full, string relativePath, out FileStream guard)
        {
            guard = null;

            // Preferred guard: READ-WRITE access, share Delete only. Asking for write access also
            // catches a RUNNING executable: Windows refuses write access to a mapped image with a
            // sharing violation even though no open handle conflicts (a read-only guard opens fine
            // on a running acclient.exe, and File.Replace then succeeds over it, leaving the old image
            // behind as an undeletable ~RF*.TMP). While this guard is held no process can start from
            // the file either (measured: CreateProcess fails with a sharing violation).
            try
            {
                guard = new FileStream(full, FileMode.Open, FileAccess.ReadWrite, FileShare.Delete, 81920, FileOptions.None);
                return true;
            }
            catch (FileNotFoundException)
            {
                return true;
            }
            catch (DirectoryNotFoundException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                // Read-only attribute or no write permission: fall through to the read guard.
            }
            catch (IOException ex) when (IsInUse(ex))
            {
                throw GameRunning(relativePath, ex);
            }
            catch (IOException ex)
            {
                throw LauncherException.FromIo(ex, "opening " + relativePath);
            }

            // Fallback: probe for a running image with a write open that shares everything (a
            // sharing violation here can only be the mapped image or another writer), then take a
            // read guard. A process started between the probe and the guard is NOT detected (the
            // window is very short, not measured here). If the exe starts inside that window the
            // replace still succeeds and leaves the old image behind as an undeletable ~RF*.TMP;
            // Commit's cleanup cannot remove it, and a later run snapshots it as a pre-existing
            // (foreign) file, so nothing ever cleans it up.
            // A READ-ONLY file cannot be opened for write at all: Windows answers access denied
            // before it looks at sharing, so a running read-only exe would pass this probe and be
            // replaced under the client (measured: the replace succeeded). Clear the attribute for
            // the probe only and put it back; Commit clears it anyway when it replaces the file.
            bool restoreReadOnly = false;
            try
            {
                FileAttributes attributes = File.GetAttributes(full);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(full, attributes & ~FileAttributes.ReadOnly);
                    restoreReadOnly = true;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Cannot lift the attribute: the probe below falls back to its old behaviour.
            }

            try
            {
                using (new FileStream(full, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None))
                {
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Still denied (no write permission): the probe only looks for sharing violations.
            }
            catch (IOException ex) when (IsInUse(ex))
            {
                throw GameRunning(relativePath, ex);
            }
            catch (IOException)
            {
                // Anything else surfaces from the guard open below.
            }
            finally
            {
                if (restoreReadOnly)
                {
                    try
                    {
                        // Only the ReadOnly bit goes back; other attributes are whatever they are now.
                        File.SetAttributes(full, File.GetAttributes(full) | FileAttributes.ReadOnly);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                    }
                }
            }

            try
            {
                guard = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Delete, 81920, FileOptions.None);
                return true;
            }
            catch (FileNotFoundException)
            {
                return true;
            }
            catch (DirectoryNotFoundException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (IOException ex) when (IsInUse(ex))
            {
                throw GameRunning(relativePath, ex);
            }
            catch (IOException ex)
            {
                throw LauncherException.FromIo(ex, "opening " + relativePath);
            }
        }

        /// <summary>
        /// Writes <paramref name="length"/> bytes from <paramref name="source"/> (read from its
        /// current position) to <c>&lt;target&gt;.dwtmp</c>, hashing exactly the bytes written, and
        /// only when that hash equals <paramref name="expectedSha256"/> flushes and atomically
        /// replaces the target; otherwise the temp file is deleted and HashMismatch thrown with the
        /// target untouched. Earlier verification of the source is therefore not load-bearing: a
        /// source that changes between verification and copy is caught here. The caller holds the
        /// target's guard (when the target exists) across this call.
        /// </summary>
        internal static void ReplaceFrom(string acFolder, string relativePath, Stream source, long length, string expectedSha256, CancellationToken cancellationToken)
        {
            string tmpRel = relativePath + TempSuffix;
            string full = AcPathSafety.ResolveForWrite(acFolder, relativePath);
            string dir = Path.GetDirectoryName(full);
            try
            {
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw LauncherException.FromIo(ex, "creating the folder for " + relativePath);
            }

            string tmp = AcPathSafety.ResolveForWrite(acFolder, tmpRel);
            RemoveStaleTemp(tmp, tmpRel);
            try
            {
                string written;
                using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.SequentialScan))
                {
                    try
                    {
                        written = Hasher.CopyWithSha256Hex(source, fs, length, cancellationToken);
                    }
                    catch (EndOfStreamException ex)
                    {
                        throw new LauncherException(LauncherError.SizeMismatch, "The staged copy of " + relativePath + " ended early, so it was not installed.", ex);
                    }

                    fs.Flush(true);
                }

                if (!Hasher.FixedTimeEquals(written, expectedSha256))
                {
                    throw new LauncherException(LauncherError.HashMismatch, "The staged copy of " + relativePath + " changed after it was verified, so it was not installed.");
                }

                Commit(acFolder, relativePath, tmpRel);
            }
            catch (OperationCanceledException ex)
            {
                TryDelete(tmp);
                throw new LauncherException(LauncherError.Cancelled, "Cancelled.", ex);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                TryDelete(tmp);
                if (ex is IOException && IsInUse((IOException)ex))
                {
                    throw GameRunning(relativePath, ex);
                }

                throw LauncherException.FromIo(ex, "writing " + relativePath);
            }
            catch
            {
                TryDelete(tmp);
                throw;
            }
        }

        /// <summary>Writes <paramref name="data"/> to <c>&lt;target&gt;.dwtmp</c> and returns the temp path; the caller verifies it and calls <see cref="Commit"/>.</summary>
        internal static string WriteTemp(string acFolder, string relativePath, byte[] data)
        {
            string tmpRel = relativePath + TempSuffix;
            string tmp = AcPathSafety.ResolveForWrite(acFolder, tmpRel);
            RemoveStaleTemp(tmp, tmpRel);
            try
            {
                using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.None))
                {
                    fs.Write(data, 0, data.Length);
                    fs.Flush(true);
                }

                return tmp;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                TryDelete(tmp);
                throw LauncherException.FromIo(ex, "writing " + relativePath);
            }
        }

        /// <summary>Atomically moves <c>&lt;target&gt;.dwtmp</c> over the target, re-checking both paths first.</summary>
        internal static void Commit(string acFolder, string relativePath, string tmpRelativePath)
        {
            for (int attempt = 1; ; attempt++)
            {
                // Re-checked on every attempt: the last look before the rename.
                string full = AcPathSafety.ResolveForWrite(acFolder, relativePath);
                string tmp = AcPathSafety.ResolveForWrite(acFolder, tmpRelativePath);
                try
                {
                    if (File.Exists(full))
                    {
                        FileAttributes a = File.GetAttributes(full);
                        if ((a & FileAttributes.ReadOnly) != 0)
                        {
                            File.SetAttributes(full, a & ~FileAttributes.ReadOnly);
                        }

                        HashSet<string> before = ReplaceLeftovers(full);
                        try
                        {
                            File.Replace(tmp, full, null, true);
                        }
                        finally
                        {
                            RemoveNewLeftovers(full, before);
                        }
                    }
                    else
                    {
                        File.Move(tmp, full);
                    }

                    return;
                }
                catch (IOException ex) when (IsInUse(ex) && attempt < ReplaceAttempts)
                {
                    // Antivirus scanners briefly open fresh files; retry a few times.
                    Thread.Sleep(20 * attempt);
                }
            }
        }

        /// <summary>
        /// ReplaceFile moves the replaced file aside as <c>&lt;name&gt;~RF&lt;hex&gt;.TMP</c> in the same
        /// folder and deletes it; when the delete fails (measured: replacing a RUNNING exe) the
        /// orphan stays. Snapshot of the existing ones, so only files our own replace left are removed.
        /// </summary>
        private static HashSet<string> ReplaceLeftovers(string full)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string f in Directory.GetFiles(Path.GetDirectoryName(full), Path.GetFileName(full) + "~RF*.TMP"))
                {
                    set.Add(f);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
            }

            return set;
        }

        private static void RemoveNewLeftovers(string full, HashSet<string> before)
        {
            foreach (string f in ReplaceLeftovers(full))
            {
                if (!before.Contains(f))
                {
                    TryDelete(f);
                }
            }
        }

        internal static bool IsInUse(IOException ex)
        {
            int code = ex.HResult & 0xFFFF;
            return code == ErrorSharingViolation || code == ErrorLockViolation;
        }

        /// <summary>Headroom required on top of the bytes a step will write (file-system overhead, other writers).</summary>
        internal const long FreeSpaceMargin = 64L * 1024 * 1024;

        /// <summary>Throws DiskFull unless the volume holding <paramref name="folder"/> has <paramref name="bytes"/> plus <see cref="FreeSpaceMargin"/> available.</summary>
        internal static void RequireFreeSpace(Downloads.IDiskSpace disk, string folder, long bytes, string where)
        {
            long need = checked(bytes + FreeSpaceMargin);
            long free = disk.GetFreeBytes(NearestExistingFolder(folder));
            if (free < need)
            {
                throw new LauncherException(
                    LauncherError.DiskFull,
                    "Not enough free space " + where + ": " + need + " bytes are needed and " + free + " are available. Nothing was changed.");
            }
        }

        /// <summary>
        /// As <see cref="RequireFreeSpace"/> for two writes that run one after the other. Both
        /// single-folder checks ALWAYS run (each folder's own volume must hold its bytes plus a
        /// margin, so a mount point onto another volume is never skipped). When both folders are on
        /// the SAME volume, the volume must also hold the COMBINED bytes plus one margin, since both
        /// exist at once. Volume identity is the volume GUID (see <see cref="VolumeIdentity"/>); a
        /// folder whose identity does not resolve is treated as being on a different volume.
        /// </summary>
        /// <param name="volumeId">Test seam: maps a folder to a volume identity (null = unresolved). Defaults to <see cref="VolumeIdentity"/>.</param>
        internal static void RequireFreeSpaceCombined(Downloads.IDiskSpace disk, string firstFolder, long firstBytes, string firstWhere, string secondFolder, long secondBytes, string secondWhere, Func<string, string> volumeId = null)
        {
            RequireFreeSpace(disk, firstFolder, firstBytes, firstWhere);
            RequireFreeSpace(disk, secondFolder, secondBytes, secondWhere);

            Func<string, string> identify = volumeId ?? VolumeIdentity;
            string firstVolume = identify(firstFolder);
            string secondVolume = identify(secondFolder);
            if (firstVolume != null && string.Equals(firstVolume, secondVolume, StringComparison.OrdinalIgnoreCase))
            {
                RequireFreeSpace(disk, firstFolder, checked(firstBytes + secondBytes), "on the drive holding both " + firstWhere + " and " + secondWhere);
            }
        }

        /// <summary>
        /// The non-throwing preview of <see cref="RequireFreeSpaceCombined"/> for the UI: the same
        /// per-folder needs (bytes plus <see cref="FreeSpaceMargin"/>) and, when both folders are on one
        /// volume (<see cref="VolumeIdentity"/>), the combined need (both byte counts plus one margin).
        /// </summary>
        /// <exception cref="LauncherException">When the free space of a volume cannot be read.</exception>
        public static FreeSpacePreview PreviewFreeSpace(Downloads.IDiskSpace disk, string firstFolder, long firstBytes, string secondFolder, long secondBytes)
        {
            return PreviewFreeSpace(disk, firstFolder, firstBytes, secondFolder, secondBytes, null);
        }

        /// <summary>As the public overload, with a volume-identity seam for tests.</summary>
        internal static FreeSpacePreview PreviewFreeSpace(Downloads.IDiskSpace disk, string firstFolder, long firstBytes, string secondFolder, long secondBytes, Func<string, string> volumeId)
        {
            if (disk == null)
            {
                throw new ArgumentNullException(nameof(disk));
            }

            firstBytes = Math.Max(0, firstBytes);
            secondBytes = Math.Max(0, secondBytes);
            long firstFree = disk.GetFreeBytes(NearestExistingFolder(Path.GetFullPath(firstFolder)));
            long secondFree = disk.GetFreeBytes(NearestExistingFolder(Path.GetFullPath(secondFolder)));
            Func<string, string> identify = volumeId ?? VolumeIdentity;
            string firstVolume = identify(firstFolder);
            bool same = firstVolume != null && string.Equals(firstVolume, identify(secondFolder), StringComparison.OrdinalIgnoreCase);
            return new FreeSpacePreview(
                same,
                firstFree,
                checked(firstBytes + FreeSpaceMargin),
                secondFree,
                checked(secondBytes + FreeSpaceMargin),
                same ? checked(firstBytes + secondBytes + FreeSpaceMargin) : (long?)null);
        }

        /// <summary>
        /// The volume GUID name (<c>\\?\Volume{...}\</c>) of the volume holding the nearest existing
        /// folder at or above <paramref name="folder"/>, or null when it cannot be resolved.
        /// <c>GetVolumePathName</c> finds the volume's mount point (a drive root or a mounted folder);
        /// <c>GetVolumeNameForVolumeMountPoint</c> turns that into the GUID, so two paths on one
        /// volume compare equal and a mount point onto another volume does not.
        /// </summary>
        internal static string VolumeIdentity(string folder)
        {
            try
            {
                string existing = Path.GetFullPath(NearestExistingFolder(Path.GetFullPath(folder)));
                var mountPoint = new System.Text.StringBuilder(1024);
                if (!GetVolumePathName(existing, mountPoint, (uint)mountPoint.Capacity))
                {
                    return null;
                }

                var volume = new System.Text.StringBuilder(64);
                if (!GetVolumeNameForVolumeMountPoint(mountPoint.ToString(), volume, (uint)volume.Capacity))
                {
                    return null;
                }

                return volume.ToString();
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool GetVolumePathName(string lpszFileName, System.Text.StringBuilder lpszVolumePathName, uint cchBufferLength);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool GetVolumeNameForVolumeMountPoint(string lpszVolumeMountPoint, System.Text.StringBuilder lpszVolumeName, uint cchBufferLength);

        internal static LauncherException GameRunning(string relativePath, Exception inner)
        {
            return new LauncherException(
                LauncherError.GameRunning,
                relativePath + " is in use, probably by a running Asheron's Call client. Close every game client and try again; nothing was changed.",
                inner);
        }

        internal static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        internal static void TryDeleteFolder(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        /// <summary>A leftover temp file from an interrupted run is deleted; a LINK at that name is refused (ResolveForWrite already threw for it).</summary>
        private static void RemoveStaleTemp(string tmp, string tmpRel)
        {
            try
            {
                if (File.Exists(tmp))
                {
                    File.Delete(tmp);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                if (ex is IOException && IsInUse((IOException)ex))
                {
                    throw GameRunning(tmpRel, ex);
                }

                throw LauncherException.FromIo(ex, "removing a leftover " + tmpRel);
            }
        }

        private static string NearestExistingFolder(string dir)
        {
            string current = dir;
            while (!string.IsNullOrEmpty(current) && !Directory.Exists(current))
            {
                current = Path.GetDirectoryName(current);
            }

            return current ?? dir;
        }

        /// <summary>True when a file can be created in <paramref name="dir"/> (a create-and-delete-on-close probe); false on access denied.</summary>
        internal static bool CanCreateIn(string dir)
        {
            string probe = Path.Combine(dir, ".dwprobe-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
                {
                }

                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (IOException ex)
            {
                throw LauncherException.FromIo(ex, "checking that the game folder can be written");
            }
        }

        /// <summary>Guards held on existing targets, by manifest-relative path.</summary>
        internal sealed class GuardSet : IDisposable
        {
            private readonly Dictionary<string, FileStream> _guards = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);

            public void Add(string relativePath, FileStream guard)
            {
                FileStream old;
                if (_guards.TryGetValue(relativePath, out old))
                {
                    old.Dispose();
                }

                _guards[relativePath] = guard;
            }

            /// <summary>The guard for <paramref name="relativePath"/>, or null when the target did not exist.</summary>
            public FileStream Get(string relativePath)
            {
                FileStream g;
                return _guards.TryGetValue(relativePath, out g) ? g : null;
            }

            public void Dispose()
            {
                foreach (FileStream g in _guards.Values)
                {
                    g.Dispose();
                }

                _guards.Clear();
            }
        }
    }

    /// <summary>Result of <see cref="AcWriter.PreviewFreeSpace(Downloads.IDiskSpace, string, long, string, long)"/>.</summary>
    public sealed class FreeSpacePreview
    {
        internal FreeSpacePreview(bool sameVolume, long firstFree, long firstNeeded, long secondFree, long secondNeeded, long? combinedNeeded)
        {
            SameVolume = sameVolume;
            FirstFree = firstFree;
            FirstNeeded = firstNeeded;
            SecondFree = secondFree;
            SecondNeeded = secondNeeded;
            CombinedNeeded = combinedNeeded;
        }

        /// <summary>True when both folders resolve to the same volume.</summary>
        public bool SameVolume { get; }

        /// <summary>Bytes available on the first folder's volume.</summary>
        public long FirstFree { get; }

        /// <summary>Bytes the first folder's volume needs (its bytes plus the margin).</summary>
        public long FirstNeeded { get; }

        /// <summary>Bytes available on the second folder's volume.</summary>
        public long SecondFree { get; }

        /// <summary>Bytes the second folder's volume needs (its bytes plus the margin).</summary>
        public long SecondNeeded { get; }

        /// <summary>Same volume only: both byte counts plus one margin; otherwise null.</summary>
        public long? CombinedNeeded { get; }

        /// <summary>True when every check <see cref="AcWriter"/> would make passes.</summary>
        public bool Enough => FirstFree >= FirstNeeded && SecondFree >= SecondNeeded && (!CombinedNeeded.HasValue || FirstFree >= CombinedNeeded.Value);
    }
}