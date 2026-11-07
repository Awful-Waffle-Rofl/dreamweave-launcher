using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DreamWeave.Launcher.Core
{
    /// <summary>
    /// The ONLY place the launcher computes SHA-256 values. All hex output is 64 uppercase
    /// characters; all comparisons go through <see cref="FixedTimeEquals(string, string)"/>.
    /// </summary>
    public static class Hasher
    {
        /// <summary>Length of a SHA-256 value in hex characters.</summary>
        public const int Sha256HexLength = 64;

        private const int BufferSize = 1024 * 1024;
        private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(250);

        /// <summary>SHA-256 of an in-memory buffer, as raw bytes.</summary>
        public static byte[] ComputeSha256(byte[] data)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            using (var sha = SHA256.Create())
            {
                return sha.ComputeHash(data);
            }
        }

        /// <summary>SHA-256 of an in-memory buffer, as 64 uppercase hex characters.</summary>
        public static string ComputeSha256Hex(byte[] data)
        {
            return ToHex(ComputeSha256(data));
        }

        /// <summary>
        /// SHA-256 of a stream read from its current position to the end, as uppercase hex.
        /// </summary>
        /// <param name="stream">Readable stream; it is not closed.</param>
        /// <param name="totalBytes">Expected length for progress, or null to use the stream length when seekable.</param>
        /// <param name="progress">Optional progress sink (throttled to roughly 4 reports per second, plus a final report).</param>
        /// <param name="cancellationToken">Cancels between buffer reads with <see cref="OperationCanceledException"/>.</param>
        /// <param name="stepId">Step id stamped on progress reports.</param>
        public static string ComputeSha256Hex(Stream stream, long? totalBytes = null, IProgress<ProgressReport> progress = null, CancellationToken cancellationToken = default(CancellationToken), string stepId = "hash")
        {
            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }

            long? total = totalBytes ?? (stream.CanSeek ? stream.Length - stream.Position : (long?)null);
            var tracker = new ProgressTracker(stepId, total, progress);
            byte[] buffer = new byte[BufferSize];
            using (var sha = SHA256.Create())
            {
                int read;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    read = stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        break;
                    }

                    sha.TransformBlock(buffer, 0, read, null, 0);
                    tracker.Advance(read);
                }

                sha.TransformFinalBlock(buffer, 0, 0);
                tracker.Finish();
                return ToHex(sha.Hash);
            }
        }

        /// <summary>Asynchronous form of <see cref="ComputeSha256Hex(Stream, long?, IProgress{ProgressReport}, CancellationToken, string)"/>.</summary>
        public static async Task<string> ComputeSha256HexAsync(Stream stream, long? totalBytes = null, IProgress<ProgressReport> progress = null, CancellationToken cancellationToken = default(CancellationToken), string stepId = "hash")
        {
            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }

            long? total = totalBytes ?? (stream.CanSeek ? stream.Length - stream.Position : (long?)null);
            var tracker = new ProgressTracker(stepId, total, progress);
            byte[] buffer = new byte[BufferSize];
            using (var sha = SHA256.Create())
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                    if (read <= 0)
                    {
                        break;
                    }

                    sha.TransformBlock(buffer, 0, read, null, 0);
                    tracker.Advance(read);
                }

                sha.TransformFinalBlock(buffer, 0, 0);
                tracker.Finish();
                return ToHex(sha.Hash);
            }
        }

        /// <summary>SHA-256 of a whole file, as uppercase hex.</summary>
        public static string ComputeFileSha256Hex(string path, IProgress<ProgressReport> progress = null, CancellationToken cancellationToken = default(CancellationToken), string stepId = "hash")
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan))
            {
                return ComputeSha256Hex(fs, fs.Length, progress, cancellationToken, stepId);
            }
        }

        /// <summary>Asynchronous SHA-256 of a whole file, as uppercase hex.</summary>
        public static async Task<string> ComputeFileSha256HexAsync(string path, IProgress<ProgressReport> progress = null, CancellationToken cancellationToken = default(CancellationToken), string stepId = "hash")
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan | FileOptions.Asynchronous))
            {
                return await ComputeSha256HexAsync(fs, fs.Length, progress, cancellationToken, stepId).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Compares two hex hash strings case-insensitively in time that depends only on their
        /// length. Returns false when either is null or the lengths differ.
        /// </summary>
        public static bool FixedTimeEquals(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length)
            {
                return false;
            }

            int diff = 0;
            for (int i = 0; i < a.Length; i++)
            {
                diff |= FoldCase(a[i]) ^ FoldCase(b[i]);
            }

            return diff == 0;
        }

        /// <summary>Compares two byte arrays in time that depends only on their length.</summary>
        public static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length)
            {
                return false;
            }

            int diff = 0;
            for (int i = 0; i < a.Length; i++)
            {
                diff |= a[i] ^ b[i];
            }

            return diff == 0;
        }

        /// <summary>True when <paramref name="value"/> is exactly 64 hex characters (either case).</summary>
        public static bool IsSha256Hex(string value)
        {
            if (value == null || value.Length != Sha256HexLength)
            {
                return false;
            }

            foreach (char c in value)
            {
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Uppercase hex encoding of a byte array.</summary>
        public static string ToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte x in bytes)
            {
                sb.Append(x.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }

            return sb.ToString();
        }

        private static int FoldCase(char c)
        {
            // ASCII-only fold; branch-free enough for hex input.
            return (c >= 'a' && c <= 'z') ? c - 32 : c;
        }

        private sealed class ProgressTracker
        {
            private readonly string _stepId;
            private readonly long? _total;
            private readonly IProgress<ProgressReport> _progress;
            private readonly Stopwatch _clock = Stopwatch.StartNew();
            private TimeSpan _lastReport = TimeSpan.Zero;
            private long _done;

            public ProgressTracker(string stepId, long? total, IProgress<ProgressReport> progress)
            {
                _stepId = stepId;
                _total = total;
                _progress = progress;
            }

            public void Advance(int bytes)
            {
                _done += bytes;
                if (_progress != null && _clock.Elapsed - _lastReport >= ReportInterval)
                {
                    _lastReport = _clock.Elapsed;
                    Report();
                }
            }

            public void Finish()
            {
                if (_progress != null)
                {
                    Report();
                }
            }

            private void Report()
            {
                double seconds = _clock.Elapsed.TotalSeconds;
                double? rate = seconds > 0 ? _done / seconds : (double?)null;
                TimeSpan? eta = null;
                if (rate.HasValue && rate.Value > 0 && _total.HasValue && _total.Value >= _done)
                {
                    eta = TimeSpan.FromSeconds((_total.Value - _done) / rate.Value);
                }

                _progress.Report(new ProgressReport(_stepId, _done, _total, rate, eta, "Verifying"));
            }
        }
    }
}
