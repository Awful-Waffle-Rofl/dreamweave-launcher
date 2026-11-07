using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace DreamWeave.Launcher.Core.Net
{
    /// <summary>
    /// <see cref="HttpClient"/>-based <see cref="IHttpStreamClient"/>. Only the wait for response
    /// HEADERS is time-limited (<see cref="DefaultHeaderTimeout"/>); body reads are the caller's to
    /// time. No automatic decompression, so byte offsets in Range/Content-Range always refer to the
    /// stored file. Redirects follow <see cref="RedirectPolicy"/> (same rules as <see cref="HttpFetcher"/>).
    /// </summary>
    public sealed class HttpStreamClient : IHttpStreamClient, IDisposable
    {
        /// <summary>Default limit on waiting for response headers.</summary>
        public static readonly TimeSpan DefaultHeaderTimeout = TimeSpan.FromSeconds(60);

        private readonly HttpClient _client;
        private readonly TimeSpan _headerTimeout;

        static HttpStreamClient()
        {
            TlsConfig.EnsureModernTls();
        }

        /// <summary>Production constructor: a real network handler, no decompression, manual redirects.</summary>
        public HttpStreamClient()
            : this(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.None, AllowAutoRedirect = false }, true, DefaultHeaderTimeout)
        {
        }

        /// <summary>Constructor for tests.</summary>
        /// <param name="handler">Message handler; automatic redirects should be off.</param>
        /// <param name="disposeHandler">Whether disposing this client disposes <paramref name="handler"/>.</param>
        /// <param name="headerTimeout">Limit on waiting for response headers.</param>
        public HttpStreamClient(HttpMessageHandler handler, bool disposeHandler, TimeSpan headerTimeout)
        {
            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            _client = new HttpClient(handler, disposeHandler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
            _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DreamWeaveLauncher", typeof(HttpStreamClient).Assembly.GetName().Version.ToString()));
            _headerTimeout = headerTimeout;
        }

        /// <inheritdoc />
        public async Task<HttpStreamResponse> GetAsync(Uri uri, long? rangeFrom, long? rangeTo, string ifRange, CancellationToken cancellationToken)
        {
            if (uri == null)
            {
                throw new ArgumentNullException(nameof(uri));
            }

            if (!uri.IsAbsoluteUri || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                throw new ArgumentException("Only absolute http(s) URIs are supported.", nameof(uri));
            }

            Uri current = uri;
            for (int hops = 0; ; hops++)
            {
                HttpResponseMessage response = await SendOnceAsync(uri, current, rangeFrom, rangeTo, ifRange, cancellationToken).ConfigureAwait(false);
                int status = (int)response.StatusCode;
                if (!RedirectPolicy.IsRedirect(status))
                {
                    return Wrap(response, current);
                }

                Uri location = response.Headers.Location;
                response.Dispose();
                current = RedirectPolicy.NextHop(uri, current, location, hops);
            }
        }

        /// <summary>Disposes the underlying client.</summary>
        public void Dispose()
        {
            _client.Dispose();
        }

        private async Task<HttpResponseMessage> SendOnceAsync(Uri original, Uri current, long? rangeFrom, long? rangeTo, string ifRange, CancellationToken cancellationToken)
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            using (var request = new HttpRequestMessage(HttpMethod.Get, current))
            {
                if (rangeFrom.HasValue)
                {
                    request.Headers.Range = new RangeHeaderValue(rangeFrom.Value, rangeTo);
                    if (!string.IsNullOrEmpty(ifRange))
                    {
                        request.Headers.TryAddWithoutValidation("If-Range", ifRange);
                    }
                }

                cts.CancelAfter(_headerTimeout);
                try
                {
                    return await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw new LauncherException(LauncherError.Cancelled, "Cancelled.", ex);
                    }

                    throw new LauncherException(LauncherError.NetworkUnavailable, original.Host + " did not respond in time.", ex);
                }
                catch (HttpRequestException ex)
                {
                    throw new LauncherException(LauncherError.NetworkUnavailable, "Could not reach " + original.Host + ".", ex);
                }
                catch (IOException ex)
                {
                    throw new LauncherException(LauncherError.NetworkUnavailable, "The connection to " + original.Host + " was interrupted.", ex);
                }
            }
        }

        private static HttpStreamResponse Wrap(HttpResponseMessage response, Uri finalUri)
        {
            HttpContentHeaders content = response.Content?.Headers;
            ContentRangeHeaderValue range = content?.ContentRange;
            string etag = response.Headers.ETag?.ToString();
            string lastModified = FirstHeader(content, "Last-Modified");

            TimeSpan? retryAfter = null;
            RetryConditionHeaderValue ra = response.Headers.RetryAfter;
            if (ra != null)
            {
                if (ra.Delta.HasValue)
                {
                    retryAfter = ra.Delta.Value;
                }
                else if (ra.Date.HasValue)
                {
                    TimeSpan d = ra.Date.Value - DateTimeOffset.UtcNow;
                    retryAfter = d < TimeSpan.Zero ? TimeSpan.Zero : d;
                }
            }

            return new HttpStreamResponse(
                (int)response.StatusCode,
                finalUri,
                content?.ContentLength,
                range?.From,
                range?.To,
                range?.Length,
                etag,
                lastModified,
                retryAfter,
                () => response.Content.ReadAsStreamAsync(),
                response);
        }

        private static string FirstHeader(HttpHeaders headers, string name)
        {
            IEnumerable<string> values;
            if (headers != null && headers.TryGetValues(name, out values))
            {
                return values.FirstOrDefault();
            }

            return null;
        }
    }
}
