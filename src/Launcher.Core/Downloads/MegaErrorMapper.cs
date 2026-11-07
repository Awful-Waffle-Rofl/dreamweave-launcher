extern alias MegaApi;

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using MegaApi::CG.Web.MegaApiClient;

namespace DreamWeave.Launcher.Core.Downloads
{
    /// <summary>
    /// Classifies what MegaApiClient 1.10.5 throws. Sources read at tag v1.10.5 of
    /// github.com/gpailler/MegaApiClient:
    /// <list type="bullet">
    /// <item>API commands (login, link resolution, download-URL request) go through
    /// <c>MegaApiClient.RequestCore</c>, which retries <c>ApiResultCode.RequestFailedRetry</c> (-3)
    /// itself (17 attempts, exponential delay) and throws <see cref="ApiException"/> carrying an
    /// <see cref="ApiResultCode"/> for any other non-zero code, or for -3 once its retries run out.</item>
    /// <item>The encrypted file body is fetched by <c>WebClient.GetRequestRaw</c>, which is
    /// <c>HttpClient.GetStreamAsync(url).Result</c>: a non-success status (mega answers HTTP 509 when the
    /// anonymous transfer quota is used up) surfaces as an <see cref="AggregateException"/> wrapping
    /// <see cref="HttpRequestException"/> whose message carries the status code; connection failures wrap
    /// <see cref="WebException"/>. Mid-body failures surface as <see cref="IOException"/>.</item>
    /// <item><c>MegaAesCtrStreamDecrypter</c> throws <see cref="DownloadException"/> ("Invalid file checksum")
    /// when mega's own MAC over the decrypted bytes does not match.</item>
    /// <item>The async methods are <c>Task.Run</c> wrappers around the synchronous ones, so the
    /// exceptions above arrive unchanged (an AggregateException from <c>.Result</c> is NOT unwrapped).</item>
    /// </list>
    /// </summary>
    public static class MegaErrorMapper
    {
        private static readonly Regex StatusInMessage = new Regex(@"\b([1-5][0-9]{2})\b", RegexOptions.CultureInvariant);

        /// <summary>Maps <paramref name="ex"/> to a classified failure and whether a retry could help.</summary>
        public static LauncherException Map(Exception ex, out bool transient)
        {
            Exception e = Unwrap(ex);
            transient = false;

            if (e is LauncherException le)
            {
                transient = le.Code == LauncherError.NetworkUnavailable;
                return le;
            }

            if (e is ApiException api)
            {
                return MapApi(api, out transient);
            }

            if (e is DownloadException)
            {
                transient = true;
                return new LauncherException(LauncherError.HashMismatch, "The file from mega.nz arrived damaged (mega's own checksum failed).", e);
            }

            if (e is HttpRequestException http)
            {
                int? status = StatusOf(http);
                if (status == 509)
                {
                    return Quota(e);
                }

                if (status.HasValue)
                {
                    transient = status.Value >= 500 || status.Value == 408;
                    if (status.Value == 429)
                    {
                        return Quota(e);
                    }

                    return new LauncherException(LauncherError.SourceHttpError, "mega.nz answered HTTP " + status.Value + ".", e);
                }

                transient = true;
                return new LauncherException(LauncherError.NetworkUnavailable, "Could not reach mega.nz.", e);
            }

            if (e is UnauthorizedAccessException)
            {
                return LauncherException.FromIo(e, "saving the download");
            }

            if (e is IOException io)
            {
                // Only these IOException kinds are worth a retry: a network interruption (the
                // connection stream wraps a SocketException/WebException), a sharing violation
                // (ERROR_SHARING_VIOLATION, 0x20: antivirus or a still-unwinding read holding the part),
                // and file-exists (below).
                // Everything else (disk full, bad path, device errors) fails fast.
                if (IsNetworkInterruption(io))
                {
                    transient = true;
                    return new LauncherException(LauncherError.NetworkUnavailable, "The connection to mega.nz was interrupted.", e);
                }

                // 0x50 ERROR_FILE_EXISTS / 0xB7 ERROR_ALREADY_EXISTS: an abandoned earlier task
                // re-created the part (CreateNew) between this attempt's delete and its own CreateNew.
                int code = io.HResult & 0xFFFF;
                transient = code == 0x20 || code == 0x50 || code == 0xB7;
                return LauncherException.FromIo(e, "saving the download");
            }

            if (e is WebException || e is SocketException || e is TimeoutException)
            {
                transient = true;
                return new LauncherException(LauncherError.NetworkUnavailable, "Could not reach mega.nz.", e);
            }

            if (e is ArgumentException)
            {
                return new LauncherException(LauncherError.ManifestInvalid, "The mega.nz link in the manifest is not a valid file link.", e);
            }

            return new LauncherException(LauncherError.Unknown, "Unexpected error while downloading from mega.nz: " + e.Message, e);
        }

        private static LauncherException MapApi(ApiException api, out bool transient)
        {
            transient = false;
            switch (api.ApiResultCode)
            {
                case ApiResultCode.QuotaExceeded:
                case ApiResultCode.TooManyRequests:
                case ApiResultCode.ToManyRequestsForThisResource:
                case ApiResultCode.TooManyConnectionsOnThisResource:
                    return Quota(api);

                case ApiResultCode.RequestFailedRetry:
                case ApiResultCode.ResourceTemporarilyNotAvailable:
                case ApiResultCode.InternalError:
                    transient = true;
                    return new LauncherException(LauncherError.SourceHttpError, "mega.nz is busy right now (" + api.ApiResultCode + ").", api);

                case ApiResultCode.ResourceNotExists:
                case ApiResultCode.ResourceExpired:
                case ApiResultCode.ResourceAdministrativelyBlocked:
                case ApiResultCode.AccessDenied:
                    return new LauncherException(LauncherError.SourceHttpError, "The file is no longer available on mega.nz (" + api.ApiResultCode + ").", api);

                default:
                    return new LauncherException(LauncherError.SourceHttpError, "mega.nz refused the request (" + api.ApiResultCode + ").", api);
            }
        }

        private static LauncherException Quota(Exception inner)
        {
            return new LauncherException(LauncherError.SourceQuotaExceeded, "mega.nz's free download limit has been reached for your connection. Try again in a few hours.", inner);
        }

        /// <summary>
        /// The HTTP status inside an <see cref="HttpRequestException"/> message, or null. .NET
        /// Framework has no StatusCode property; its English EnsureSuccessStatusCode message is
        /// "Response status code does not indicate success: 509 (Bandwidth Limit Exceeded)." When
        /// that marker is present the number after it wins. Otherwise (a localized framework) the
        /// whole message is scanned for 4xx/5xx tokens, preferring 509, then 429, then 408, then any
        /// 5xx, then any other 4xx.
        /// </summary>
        internal static int? StatusOf(HttpRequestException http)
        {
            string message = http.Message ?? string.Empty;
            int marker = message.IndexOf("success:", StringComparison.OrdinalIgnoreCase);
            if (marker >= 0)
            {
                Match m = StatusInMessage.Match(message, marker);
                if (m.Success)
                {
                    return int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                }
            }

            int? best = null;
            int bestRank = int.MaxValue;
            foreach (Match m in StatusInMessage.Matches(message))
            {
                int code = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                int rank = code == 509 ? 0 : code == 429 ? 1 : code == 408 ? 2 : code >= 500 ? 3 : code >= 400 ? 4 : int.MaxValue;
                if (rank < bestRank)
                {
                    best = code;
                    bestRank = rank;
                }
            }

            return best;
        }

        private static bool IsNetworkInterruption(IOException io)
        {
            for (Exception inner = io.InnerException; inner != null; inner = inner.InnerException)
            {
                if (inner is SocketException || inner is WebException || inner is HttpRequestException)
                {
                    return true;
                }
            }

            return false;
        }

        private static Exception Unwrap(Exception ex)
        {
            Exception e = ex;
            while (e is AggregateException agg && agg.InnerExceptions.Count >= 1)
            {
                e = agg.InnerExceptions[0];
            }

            return e;
        }
    }
}
