using System.Threading;
using System.Threading.Tasks;

namespace DreamWeave.Launcher.Core.Downloads
{
    /// <summary>
    /// One way of fetching bytes (plain HTTP, mega.nz). A transport only fills
    /// <see cref="DownloadRequest.PartPath"/>; <see cref="DownloadService"/> owns the disk-space
    /// check, hashing, and the rename to the final name.
    /// </summary>
    public interface IDownloadTransport
    {
        /// <summary>The <see cref="Manifests.Transports"/> value this transport serves.</summary>
        string Name { get; }

        /// <summary>Bytes already on disk that the next transfer would keep (0 when it cannot resume).</summary>
        long GetResumableBytes(DownloadRequest request);

        /// <summary>
        /// Writes exactly <see cref="DownloadRequest.ExpectedSize"/> bytes to
        /// <see cref="DownloadRequest.PartPath"/>, reporting absolute byte counts to <paramref name="progress"/>.
        /// Retries transient failures itself.
        /// </summary>
        /// <exception cref="LauncherException">Any classified failure, including <see cref="LauncherError.Cancelled"/>.</exception>
        Task DownloadToPartAsync(DownloadRequest request, TransferProgress progress, CancellationToken cancellationToken);
    }
}
