using System;
using System.Threading;
using System.Threading.Tasks;

namespace DreamWeave.Launcher.Core.Net
{
    /// <summary>
    /// Streaming HTTP GET for large downloads. Unlike <see cref="IHttpFetcher"/> there is no
    /// whole-body timeout and no in-memory buffering: the caller reads the body itself and applies
    /// its own per-read inactivity timeout. Non-success statuses are RETURNED, not thrown, so the
    /// caller can decide what to retry. The ONLY production implementation is <see cref="HttpStreamClient"/>.
    /// </summary>
    public interface IHttpStreamClient
    {
        /// <summary>
        /// Sends a GET (never HEAD) and returns once the response headers have arrived, following
        /// redirects under <see cref="HttpFetcher"/>'s rules (the range headers are re-sent on every hop).
        /// </summary>
        /// <param name="uri">Absolute http(s) URI.</param>
        /// <param name="rangeFrom">First byte to request (<c>Range: bytes=rangeFrom-</c>), or null for no Range header.</param>
        /// <param name="rangeTo">Inclusive last byte, or null for open-ended. Ignored when <paramref name="rangeFrom"/> is null.</param>
        /// <param name="ifRange">Raw <c>If-Range</c> value (a strong ETag or an HTTP date), or null.</param>
        /// <param name="cancellationToken">Player cancellation.</param>
        /// <returns>The response; the caller disposes it.</returns>
        /// <exception cref="LauncherException">
        /// <see cref="LauncherError.NetworkUnavailable"/> (connect/DNS failure or no headers within the header timeout),
        /// <see cref="LauncherError.SourceHttpError"/> (redirect rule violated), or
        /// <see cref="LauncherError.Cancelled"/> (the token fired).
        /// </exception>
        Task<HttpStreamResponse> GetAsync(Uri uri, long? rangeFrom, long? rangeTo, string ifRange, CancellationToken cancellationToken);
    }
}
