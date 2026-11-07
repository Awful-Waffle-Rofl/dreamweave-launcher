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
    internal static class AcWriter
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
        /// current position) to <c>&lt;target&gt;.dwtmp</c>, flushes, and atomically replaces the
        /// target. The caller holds the target's guard (when the target exists) across this call.
        /// </summary>
        internal static void ReplaceFrom(string acFolder, string relativePath, Stream source, long length, CancellationToken cancellationToken)
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
                using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.SequentialScan))
                {
                    Copy(source, fs, length, cancellationToken);
                    fs.Flush(true);
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

                        File.Replace(tmp, full, null, true);
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

        internal static bool IsInUse(IOException ex)
        {
            int code = ex.HResult & 0xFFFF;
            return code == ErrorSharingViolation || code == ErrorLockViolation;
        }

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

        private static void Copy(Stream source, Stream destination, long length, CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[1024 * 1024];
            long remaining = length;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read <= 0)
                {
                    throw new LauncherException(LauncherError.SizeMismatch, "A staged file ended early.");
                }

                destination.Write(buffer, 0, read);
                remaining -= read;
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
}
