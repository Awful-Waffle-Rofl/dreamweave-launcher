using System;
using System.Threading;
using System.Threading.Tasks;

namespace DreamWeave.Launcher.Core.Net
{
    /// <summary>
    /// Small, size-capped HTTP GET. The ONLY production implementation is <see cref="HttpFetcher"/>;
    /// tests substitute fakes. Transport is never trusted: callers verify what they receive.
    /// </summary>
    public interface IHttpFetcher
    {
        /// <summary>
        /// Downloads the whole body of <paramref name="uri"/> into memory.
        /// </summary>
        /// <param name="uri">Absolute http(s) URI.</param>
        /// <param name="maxBytes">Hard cap on the body length; a longer body is abandoned.</param>
        /// <param name="cancellationToken">Player cancellation.</param>
        /// <returns>The response body.</returns>
        /// <exception cref="LauncherException">
        /// <see cref="LauncherError.NetworkUnavailable"/> (connect/DNS/timeout),
        /// <see cref="LauncherError.SourceHttpError"/> (non-success status),
        /// <see cref="LauncherError.SourceQuotaExceeded"/> (HTTP 429),
        /// <see cref="LauncherError.ResponseTooLarge"/> (body over <paramref name="maxBytes"/>), or
        /// <see cref="LauncherError.Cancelled"/> (the token fired).
        /// </exception>
        Task<byte[]> GetBytesAsync(Uri uri, long maxBytes, CancellationToken cancellationToken);
    }
}
