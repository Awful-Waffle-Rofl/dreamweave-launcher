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

        /// <summary>Production constructor: HTTP and mega.nz transports, real free-space query.</summary>
        public DownloadService(AppPaths paths)
            : this(paths?.StagingDir, new SystemDiskSpace(), new IDownloadTransport[] { new HttpTransport(new HttpStreamClient()), new MegaTransport() })
        {
        }

        /// <summary>Constructor for tests and custom wiring.</summary>
        public DownloadService(string stagingDir, IDiskSpace disk, IEnumerable<IDownloadTransport> transports)
        {
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
            IDownloadTransport transport = Validate(request);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(request.DestinationPath)));

                DownloadResult existing = await TryExistingAsync(request, progress, cancellationToken).ConfigureAwait(false);
                if (existing != null)
                {
                    return existing;
                }

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

        private IDownloadTransport Validate(DownloadRequest request)
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
