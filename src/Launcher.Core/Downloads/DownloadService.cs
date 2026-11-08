using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DreamWeave.Launcher.Core.Net;

namespace DreamWeave.Launcher.Core.Downloads
{
    /// <summary>
    /// The launcher's <see cref="IDownloader"/>. For each request it:
    /// <list type="number">
    /// <item>returns at once when the final file already has the expected size and SHA-256 (a stale
    /// final file is deleted);</item>
    /// <item>checks that the staging volume has the remaining bytes plus <see cref="FreeSpaceMargin"/>
    /// free, failing with <see cref="LauncherError.DiskFull"/> before any network request;</item>
    /// <item>hands the transfer to the transport named by <see cref="DownloadRequest.Transport"/>;</item>
    /// <item>verifies the part's length and SHA-256 (via <see cref="Hasher"/>, reported as a
    /// "Verifying" phase under the same step id), deleting it on a mismatch;</item>
    /// <item>renames the verified part to the final name.</item>
    /// </list>
    /// The final name therefore only ever holds verified bytes.
    /// </summary>
    public sealed class DownloadService : IDownloader
    {
        /// <summary>Free space kept in reserve beyond the bytes still to download.</summary>
        public const long FreeSpaceMargin = 64L * 1024 * 1024;

        private readonly string _stagingDir;
        private readonly IDiskSpace _disk;
        private readonly Dictionary<string, IDownloadTransport> _transports = new Dictionary<string, IDownloadTransport>(StringComparer.Ordinal);
        private readonly IDownloadLog _log;

        /// <summary>Production constructor: HTTP and mega.nz transports, real free-space query, <see cref="AppPaths.LogFile"/> log.</summary>
        public DownloadService(AppPaths paths)
            : this(paths?.StagingDir, new SystemDiskSpace(), new IDownloadTransport[] { new HttpTransport(new HttpStreamClient()), new MegaTransport() }, paths == null ? null : new FileDownloadLog(paths.LogFile))
        {
        }

        /// <summary>Constructor for tests and custom wiring. <paramref name="log"/> (optional) receives one record per source attempt.</summary>
        public DownloadService(string stagingDir, IDiskSpace disk, IEnumerable<IDownloadTransport> transports, IDownloadLog log = null)
        {
            _log = log;
            if (string.IsNullOrWhiteSpace(stagingDir))
            {
                throw new ArgumentException("A staging folder is required.", nameof(stagingDir));
            }

            _stagingDir = Path.GetFullPath(stagingDir);
            _disk = disk ?? throw new ArgumentNullException(nameof(disk));
            if (transports == null)
            {
                throw new ArgumentNullException(nameof(transports));
            }

            foreach (IDownloadTransport t in transports)
            {
                _transports[t.Name] = t;
            }
        }

        /// <inheritdoc />
        public async Task<DownloadResult> DownloadAsync(DownloadRequest request, IProgress<ProgressReport> progress, CancellationToken cancellationToken)
        {
            Validate(request);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(request.DestinationPath)));

                DownloadResult existing = await TryExistingAsync(request, progress, cancellationToken).ConfigureAwait(false);
                if (existing != null)
                {
                    Log(request, 0, DownloadAttemptOutcome.AlreadyPresent, existing.Size, TimeSpan.Zero, null);
                    return existing;
                }
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                throw new LauncherException(LauncherError.Cancelled, "Cancelled.", ex);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw LauncherException.FromIo(ex, "preparing the download");
            }

            // The primary first, then each alternate in manifest order. Every source must produce the
            // same size and SHA-256; a different hash from a mirror is a failure, never accepted. Each
            // source has its own part file, so a read of one that is still unwinding cannot block the next.
            DeleteSourceParts(request);
            LauncherException last = null;
            DownloadRequest previous = null;
            int total = 1 + request.Alternates.Count;
            for (int i = 0; i < total; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new LauncherException(LauncherError.Cancelled, "Cancelled.");
                }

                DownloadSource source = i == 0 ? new DownloadSource(request.Transport, request.Url, request.SourceLabel) : request.Alternates[i - 1];
                DownloadRequest attempt = request.ForSource(source, i, i < total - 1);
                if (i > 0)
                {
                    // Free the previous source's partial file if it can be (a stalled read may still hold it).
                    DiscardPart(previous);
                    request.OnFallback?.Invoke(source.Label);
                }

                request.OnSourceStarted?.Invoke(new SourceAttempt(i, source, last));
                previous = attempt;
                try
                {
                    DownloadResult result = await DownloadFromSourceAsync(attempt, i, progress, cancellationToken).ConfigureAwait(false);
                    DeleteSourceParts(request);

                    // The primary's own part (an abandoned read may have left it) is not wanted any more either.
                    PartFiles.TryDelete(request.PartPath);
                    PartFiles.TryDelete(request.SidecarPath);
                    return result;
                }
                catch (LauncherException ex)
                {
                    ex.Data["transport"] = attempt.Transport;
                    if (ex.Code == LauncherError.Cancelled || i >= total - 1 || !CanFallBack(ex.Code))
                    {
                        throw;
                    }

                    last = ex;
                }
            }

            // Not reached while the last source throws its own error; kept as a safety net.
            throw last ?? new LauncherException(LauncherError.Unknown, "No download source was available.");
        }

        /// <summary>Best-effort removal of every source's leftover part and sidecar (<c>&lt;dest&gt;.s*.part*</c>); a file still held is left alone.</summary>
        private static void DeleteSourceParts(DownloadRequest request)
        {
            try
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(request.DestinationPath));
                if (!Directory.Exists(dir))
                {
                    return;
                }

                foreach (string file in Directory.GetFiles(dir, Path.GetFileName(request.DestinationPath) + ".s*.part*"))
                {
                    PartFiles.TryDelete(file);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
            }
        }
        /// <summary>Failures another source could cure. Disk, permission and cancellation problems are not among them.</summary>
        private static bool CanFallBack(LauncherError code)
        {
            switch (code)
            {
                case LauncherError.NetworkUnavailable:
                case LauncherError.SourceHttpError:
                case LauncherError.SourceQuotaExceeded:
                case LauncherError.ResponseTooLarge:
                case LauncherError.HashMismatch:
                case LauncherError.SizeMismatch:
                case LauncherError.ManifestInvalid:
                    return true;
                default:
                    return false;
            }
        }

        private async Task<DownloadResult> DownloadFromSourceAsync(DownloadRequest request, int sourceIndex, IProgress<ProgressReport> progress, CancellationToken cancellationToken)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                DownloadResult result = await DownloadFromSourceCoreAsync(request, progress, cancellationToken).ConfigureAwait(false);
                Log(request, sourceIndex, DownloadAttemptOutcome.Succeeded, result.Size, clock.Elapsed, null);
                return result;
            }
            catch (Exception ex)
            {
                bool cancelled = cancellationToken.IsCancellationRequested || (ex is LauncherException le && le.Code == LauncherError.Cancelled);
                Log(request, sourceIndex, cancelled ? DownloadAttemptOutcome.Cancelled : DownloadAttemptOutcome.Failed, Math.Max(0, PartFiles.LengthOrMissing(request.PartPath)), clock.Elapsed, ex);
                throw;
            }
        }

        private void Log(DownloadRequest request, int sourceIndex, DownloadAttemptOutcome outcome, long bytes, TimeSpan duration, Exception error)
        {
            if (_log == null)
            {
                return;
            }

            try
            {
                _log.Record(new DownloadAttemptRecord
                {
                    ComponentId = request.StepId,
                    SourceIndex = sourceIndex,
                    Transport = request.Transport,
                    Host = request.Url != null && request.Url.IsAbsoluteUri ? request.Url.Host : null,
                    Outcome = outcome,
                    Bytes = bytes,
                    Duration = duration,
                    Error = error,
                });
            }
            catch (Exception)
            {
                // A log sink never fails a download.
            }
        }

        private async Task<DownloadResult> DownloadFromSourceCoreAsync(DownloadRequest request, IProgress<ProgressReport> progress, CancellationToken cancellationToken)
        {
            Validate(request);
            IDownloadTransport transport = TransportFor(request);
            try
            {
                long remaining = request.ExpectedSize - transport.GetResumableBytes(request);
                long free = _disk.GetFreeBytes(_stagingDir);
                if (free < remaining + FreeSpaceMargin)
                {
                    throw new LauncherException(
                        LauncherError.DiskFull,
                        "Not enough free disk space: " + FormatMb(remaining + FreeSpaceMargin) + " is needed on the drive holding " + _stagingDir + ", but only " + FormatMb(free) + " is free.");
                }

                var tracker = new TransferProgress(request.StepId, request.ExpectedSize, progress);
                try
                {
                    await transport.DownloadToPartAsync(request, tracker, cancellationToken).ConfigureAwait(false);
                }
                catch (LauncherException ex) when (ex.Code == LauncherError.SizeMismatch || ex.Code == LauncherError.HashMismatch)
                {
                    DiscardPart(request);
                    throw;
                }

                tracker.Complete();
                return await VerifyAndPromoteAsync(request, progress, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                throw new LauncherException(LauncherError.Cancelled, "Cancelled.", ex);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw LauncherException.FromIo(ex, "preparing the download");
            }
        }

        private void Validate(DownloadRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (request.Url == null || !request.Url.IsAbsoluteUri)
            {
                throw new LauncherException(LauncherError.ManifestInvalid, "The download address for '" + request.StepId + "' is not valid.");
            }

            if (request.ExpectedSize <= 0 || !Hasher.IsSha256Hex(request.ExpectedSha256))
            {
                throw new LauncherException(LauncherError.ManifestInvalid, "The manifest entry for '" + request.StepId + "' has no usable size or SHA-256.");
            }

            if (string.IsNullOrEmpty(request.DestinationPath))
            {
                throw new ArgumentException("A destination path is required.", nameof(request));
            }

            string dest = Path.GetFullPath(request.DestinationPath);
            string root = _stagingDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Downloads must be saved inside the staging folder (" + _stagingDir + ").", nameof(request));
            }

            // One folder per component (StagingDir\<StepId>\), so no other component's files can sit
            // beside this one; the install executor refuses an installer with any DLL next to it.
            string expectedFolder = Path.GetFullPath(DownloadRequest.ComponentFolder(_stagingDir, request.StepId));
            if (!string.Equals(Path.GetDirectoryName(dest), expectedFolder, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Downloads must be saved in their component's own staging folder (" + expectedFolder + ").", nameof(request));
            }

        }

        /// <summary>The transport for this source; an unknown type is a manifest problem another source may cure.</summary>
        private IDownloadTransport TransportFor(DownloadRequest request)
        {
            IDownloadTransport transport;
            if (request.Transport == null || !_transports.TryGetValue(request.Transport, out transport))
            {
                throw new LauncherException(LauncherError.ManifestInvalid, "This launcher cannot download '" + request.StepId + "' (unknown source type '" + request.Transport + "').");
            }

            return transport;
        }

        private static async Task<DownloadResult> TryExistingAsync(DownloadRequest request, IProgress<ProgressReport> progress, CancellationToken cancellationToken)
        {
            long length = PartFiles.LengthOrMissing(request.DestinationPath);
            if (length < 0)
            {
                return null;
            }

            if (length == request.ExpectedSize)
            {
                string hash = await Hasher.ComputeFileSha256HexAsync(request.DestinationPath, progress, cancellationToken, request.StepId).ConfigureAwait(false);
                if (Hasher.FixedTimeEquals(hash, request.ExpectedSha256))
                {
                    return new DownloadResult(request.DestinationPath, length, hash, true);
                }
            }

            PartFiles.Delete(request.DestinationPath, "removing an outdated download");
            return null;
        }

        private static async Task<DownloadResult> VerifyAndPromoteAsync(DownloadRequest request, IProgress<ProgressReport> progress, CancellationToken cancellationToken)
        {
            long length = PartFiles.LengthOrMissing(request.PartPath);
            if (length != request.ExpectedSize)
            {
                DiscardPart(request);
                throw new LauncherException(LauncherError.SizeMismatch, "The downloaded file is " + Math.Max(length, 0) + " bytes, but " + request.ExpectedSize + " bytes were expected.");
            }

            string hash = await Hasher.ComputeFileSha256HexAsync(request.PartPath, progress, cancellationToken, request.StepId).ConfigureAwait(false);
            if (!Hasher.FixedTimeEquals(hash, request.ExpectedSha256))
            {
                DiscardPart(request);
                throw new LauncherException(LauncherError.HashMismatch, "The downloaded file failed its integrity check and was deleted. Please try again.");
            }

            try
            {
                File.Move(request.PartPath, request.DestinationPath);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw LauncherException.FromIo(ex, "saving the verified download");
            }

            PartFiles.TryDelete(request.SidecarPath);
            return new DownloadResult(request.DestinationPath, length, hash, false);
        }

        private static void DiscardPart(DownloadRequest request)
        {
            PartFiles.TryDelete(request.PartPath);
            PartFiles.TryDelete(request.SidecarPath);
        }

        private static string FormatMb(long bytes)
        {
            return (bytes / (1024.0 * 1024.0)).ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " MB";
        }
    }
}
