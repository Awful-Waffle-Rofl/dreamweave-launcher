using System;
using System.Threading;
using System.Threading.Tasks;

namespace DreamWeave.Launcher.Core.Downloads
{
    /// <summary>Downloads one manifest-pinned file and verifies it. Production implementation: <see cref="DownloadService"/>.</summary>
    public interface IDownloader
    {
        /// <summary>
        /// Downloads <paramref name="request"/> and returns only once the file at its destination
        /// has the expected size and SHA-256.
        /// </summary>
        /// <exception cref="LauncherException">
        /// <see cref="LauncherError.DiskFull"/> (checked before any request),
        /// <see cref="LauncherError.NetworkUnavailable"/>, <see cref="LauncherError.SourceHttpError"/>,
        /// <see cref="LauncherError.SourceQuotaExceeded"/>, <see cref="LauncherError.SizeMismatch"/>,
        /// <see cref="LauncherError.HashMismatch"/>, <see cref="LauncherError.AccessDenied"/>,
        /// <see cref="LauncherError.ManifestInvalid"/> (unusable source address or transport), or
        /// <see cref="LauncherError.Cancelled"/>.
        /// </exception>
        Task<DownloadResult> DownloadAsync(DownloadRequest request, IProgress<ProgressReport> progress, CancellationToken cancellationToken);
    }
}
