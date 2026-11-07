using System;
using System.Threading;
using System.Threading.Tasks;

namespace DreamWeave.Launcher.Core.Net
{
    /// <summary>The status line of a POST response (the body is never read).</summary>
    public sealed class HttpPostResult
    {
        /// <summary>Creates a result.</summary>
        public HttpPostResult(int status, TimeSpan? retryAfter)
        {
            Status = status;
            RetryAfter = retryAfter;
        }

        /// <summary>The HTTP status code.</summary>
        public int Status { get; }

        /// <summary>The parsed <c>Retry-After</c> header (delta seconds or HTTP date), or null when absent or unparseable.</summary>
        public TimeSpan? RetryAfter { get; }

        /// <summary>True for a 2xx status.</summary>
        public bool IsSuccess => Status >= 200 && Status <= 299;

        /// <summary>True for a status <see cref="RedirectPolicy"/> would follow on a GET. A POST never follows it.</summary>
        public bool IsRedirect => RedirectPolicy.IsRedirect(Status);
    }

    /// <summary>
    /// Small JSON POST used by telemetry. The ONLY production implementation is <see cref="JsonPoster"/>;
    /// tests substitute fakes. Non-success statuses are RETURNED, not thrown; redirects are never followed.
    /// </summary>
    public interface IJsonPoster
    {
        /// <summary>Posts <paramref name="body"/> as <c>application/json</c> and returns the status.</summary>
        /// <exception cref="LauncherException">
        /// <see cref="LauncherError.NetworkUnavailable"/> (connect/DNS failure, timeout) or
        /// <see cref="LauncherError.Cancelled"/> (the token fired).
        /// </exception>
        Task<HttpPostResult> PostJsonAsync(Uri uri, byte[] body, CancellationToken cancellationToken);
    }
}
