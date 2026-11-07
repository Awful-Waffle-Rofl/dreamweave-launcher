using System;
using System.IO;
using System.Threading;
using DreamWeave.Launcher.Core.State;

namespace DreamWeave.Launcher.Core.Detection
{
    /// <summary>What happened when a file was checked.</summary>
    public enum FileCheckStatus
    {
        /// <summary>The file was read and hashed (or its hash came from the cache).</summary>
        Hashed = 0,

        /// <summary>No such file.</summary>
        Missing = 1,

        /// <summary>Another process holds the file in a way that blocks reading it (sharing or lock violation).</summary>
        Locked = 2,

        /// <summary>Windows denied access to the file.</summary>
        AccessDenied = 3,

        /// <summary>The file's length differs from the expected size, so it was not hashed.</summary>
        SizeMismatch = 4,

        /// <summary>Any other read failure.</summary>
        Error = 5,
    }

    /// <summary>Result of <see cref="FileVerifier.Verify"/>.</summary>
    public sealed class FileCheckResult
    {
        /// <summary>Creates a result.</summary>
        public FileCheckResult(string path, FileCheckStatus status, long? size, string sha256, bool fromCache, string message)
        {
            Path = path;
            Status = status;
            Size = size;
            Sha256 = sha256;
            FromCache = fromCache;
            Message = message;
        }

        /// <summary>Full path that was checked.</summary>
        public string Path { get; }

        /// <summary>Outcome.</summary>
        public FileCheckStatus Status { get; }

        /// <summary>File length when known.</summary>
        public long? Size { get; }

        /// <summary>SHA-256 (64 uppercase hex) when <see cref="Status"/> is Hashed.</summary>
        public string Sha256 { get; }

        /// <summary>True when the hash was reused from the cache without reading the file.</summary>
        public bool FromCache { get; }

        /// <summary>Plain-English note for failures, or null.</summary>
        public string Message { get; }
    }

    /// <summary>
    /// Hashes files with <see cref="Hasher"/>, reusing a cached hash while a file's size and
    /// last-write time are unchanged (<see cref="LauncherState.FileVerifyCache"/>). Failures to
    /// read a file are reported, never thrown: the owner runs game clients continuously, so a
    /// locked file is an ordinary outcome. Read-only apart from the cache entry in the launcher's
    /// own state file.
    /// </summary>
    public sealed class FileVerifier
    {
        private readonly IAcFileSystem _fs;
        private readonly StateStore _state;

        /// <summary>Creates a verifier.</summary>
        public FileVerifier(IAcFileSystem fileSystem, StateStore state)
        {
            _fs = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
            _state = state ?? throw new ArgumentNullException(nameof(state));
        }

        /// <summary>The file system this verifier reads through.</summary>
        public IAcFileSystem FileSystem => _fs;

        /// <summary>Checks one file.</summary>
        /// <param name="fullPath">Absolute path (resolve manifest paths with <see cref="AcPathSafety"/> first).</param>
        /// <param name="expectedSize">When set and the file has a different length, the file is reported SizeMismatch without hashing.</param>
        /// <param name="progress">Optional hashing progress.</param>
        /// <param name="cancellationToken">Cancels a long hash.</param>
        public FileCheckResult Verify(string fullPath, long? expectedSize = null, IProgress<ProgressReport> progress = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            FileStamp before;
            try
            {
                before = _fs.GetStamp(fullPath);
            }
            catch (Exception ex) when (IsReadFailure(ex))
            {
                return Failure(fullPath, ex);
            }

            if (before == null)
            {
                return new FileCheckResult(fullPath, FileCheckStatus.Missing, null, null, false, "The file does not exist.");
            }

            if (expectedSize.HasValue && before.Size != expectedSize.Value)
            {
                return new FileCheckResult(fullPath, FileCheckStatus.SizeMismatch, before.Size, null, false, "The file is " + before.Size + " bytes; expected " + expectedSize.Value + ".");
            }

            string cached = TryCache(fullPath, before);
            if (cached != null)
            {
                return new FileCheckResult(fullPath, FileCheckStatus.Hashed, before.Size, cached, true, null);
            }

            string hash;
            try
            {
                using (Stream stream = _fs.OpenRead(fullPath))
                {
                    hash = Hasher.ComputeSha256Hex(stream, null, progress, cancellationToken, "verify:" + System.IO.Path.GetFileName(fullPath));
                }
            }
            catch (Exception ex) when (IsReadFailure(ex))
            {
                return Failure(fullPath, ex);
            }

            // Only cache a hash taken from a file that did not change while it was being read.
            FileStamp after = null;
            try
            {
                after = _fs.GetStamp(fullPath);
            }
            catch (Exception ex) when (IsReadFailure(ex))
            {
            }

            if (after != null && Same(before, after))
            {
                StoreCache(fullPath, before, hash);
            }

            return new FileCheckResult(fullPath, FileCheckStatus.Hashed, before.Size, hash, false, null);
        }

        private string TryCache(string fullPath, FileStamp stamp)
        {
            return _state.Read(s =>
            {
                FileVerifyEntry e;
                if (s.FileVerifyCache.TryGetValue(fullPath, out e)
                    && e != null
                    && e.Size == stamp.Size
                    && AsUtcTicks(e.LastWriteUtc) == AsUtcTicks(stamp.LastWriteUtc)
                    && Hasher.IsSha256Hex(e.Sha256))
                {
                    return e.Sha256.ToUpperInvariant();
                }

                return null;
            });
        }

        private void StoreCache(string fullPath, FileStamp stamp, string hash)
        {
            try
            {
                _state.Update(s => s.FileVerifyCache[fullPath] = new FileVerifyEntry
                {
                    Size = stamp.Size,
                    LastWriteUtc = new DateTime(AsUtcTicks(stamp.LastWriteUtc), DateTimeKind.Utc),
                    Sha256 = hash,
                });
            }
            catch (LauncherException)
            {
                // The cache is an optimisation; a state file that cannot be written must not fail verification.
            }
        }

        private static bool Same(FileStamp a, FileStamp b)
        {
            return a.Size == b.Size && AsUtcTicks(a.LastWriteUtc) == AsUtcTicks(b.LastWriteUtc);
        }

        /// <summary>UTC ticks; an Unspecified-kind value is taken to already be UTC (state.json stores UTC).</summary>
        private static long AsUtcTicks(DateTime value)
        {
            return value.Kind == DateTimeKind.Local ? value.ToUniversalTime().Ticks : value.Ticks;
        }

        private static bool IsReadFailure(Exception ex)
        {
            return ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException || ex is ArgumentException;
        }

        private static FileCheckResult Failure(string path, Exception ex)
        {
            if (ex is FileNotFoundException || ex is DirectoryNotFoundException)
            {
                return new FileCheckResult(path, FileCheckStatus.Missing, null, null, false, "The file does not exist.");
            }

            if (ex is UnauthorizedAccessException)
            {
                return new FileCheckResult(path, FileCheckStatus.AccessDenied, null, null, false, "Windows denied access to the file.");
            }

            if (ex is IOException)
            {
                int code = ex.HResult & 0xFFFF;
                if (code == 32 || code == 33)
                {
                    return new FileCheckResult(path, FileCheckStatus.Locked, null, null, false, "The file is in use by another program.");
                }
            }

            return new FileCheckResult(path, FileCheckStatus.Error, null, null, false, "The file could not be read: " + ex.Message);
        }
    }
}
