using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace DreamWeave.Launcher.Core.Net
{
    /// <summary>
    /// The launcher's only <see cref="HttpClient"/>-based fetcher. Enforces the caller's size cap
    /// both on Content-Length and while streaming (the cap counts decoded bytes, so a compressed
    /// response cannot inflate past it), and applies an overall timeout to the whole request.
    /// Redirects are followed by this class, not the handler: at most <see cref="MaxRedirects"/>,
    /// and never from https to a non-https URL (both fail as <see cref="LauncherError.SourceHttpError"/>).
    /// A handler passed to the test constructor should have automatic redirects off for these
    /// rules to apply.
    /// </summary>
    public sealed class HttpFetcher : IHttpFetcher, IDisposable
    {
        /// <summary>Default overall timeout for one request, headers and body.</summary>
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

        /// <summary>Maximum redirects followed for one request.</summary>
        public const int MaxRedirects = 5;

        private readonly HttpClient _client;
        private readonly TimeSpan _timeout;

        static HttpFetcher()
        {
            TlsConfig.EnsureModernTls();
        }

        /// <summary>Production constructor: a real network handler with gzip/deflate and <see cref="DefaultTimeout"/>.</summary>
        public HttpFetcher()
            : this(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate, AllowAutoRedirect = false }, true, DefaultTimeout)
        {
        }

        /// <summary>Constructor for tests and specialised transports.</summary>
        /// <param name="handler">Message handler to send through.</param>
        /// <param name="disposeHandler">Whether disposing this fetcher disposes <paramref name="handler"/>.</param>
        /// <param name="timeout">Overall per-request timeout.</param>
        public HttpFetcher(HttpMessageHandler handler, bool disposeHandler, TimeSpan timeout)
        {
            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            _client = new HttpClient(handler, disposeHandler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
            _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DreamWeaveLauncher", typeof(HttpFetcher).Assembly.GetName().Version.ToString()));
            _timeout = timeout;
        }

        /// <inheritdoc />
        public async Task<byte[]> GetBytesAsync(Uri uri, long maxBytes, CancellationToken cancellationToken)
        {
            if (uri == null)
            {
                throw new ArgumentNullException(nameof(uri));
            }

            if (!uri.IsAbsoluteUri || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                throw new ArgumentException("Only absolute http(s) URIs are supported.", nameof(uri));
            }

            if (maxBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxBytes));
            }

            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                cts.CancelAfter(_timeout);
                try
                {
                    using (var response = await GetFollowingRedirectsAsync(uri, cts.Token).ConfigureAwait(false))
                    {
                        int status = (int)response.StatusCode;
                        if (status == 429)
                        {
                            throw new LauncherException(LauncherError.SourceQuotaExceeded, uri.Host + " is rate limiting downloads (HTTP 429). Try again later.");
                        }

                        if (!response.IsSuccessStatusCode)
                        {
                            throw new LauncherException(LauncherError.SourceHttpError, uri.Host + " answered HTTP " + status + ".");
                        }

                        long? declared = response.Content.Headers.ContentLength;
                        if (declared.HasValue && declared.Value > maxBytes)
                        {
                            throw TooLarge(uri, maxBytes);
                        }

                        using (Stream body = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var buffer = new MemoryStream())
                        {
                            byte[] chunk = new byte[81920];
                            while (true)
                            {
                                int read = await body.ReadAsync(chunk, 0, chunk.Length, cts.Token).ConfigureAwait(false);
                                if (read <= 0)
                                {
                                    break;
                                }

                                if (buffer.Length + read > maxBytes)
                                {
                                    throw TooLarge(uri, maxBytes);
                                }

                                buffer.Write(chunk, 0, read);
                            }

                            return buffer.ToArray();
                        }
                    }
                }
                catch (OperationCanceledException ex)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw new LauncherException(LauncherError.Cancelled, "Cancelled.", ex);
                    }

                    throw new LauncherException(LauncherError.NetworkUnavailable, uri.Host + " did not respond in time.", ex);
                }
                catch (HttpRequestException ex)
                {
                    throw new LauncherException(LauncherError.NetworkUnavailable, "Could not reach " + uri.Host + ".", ex);
                }
                catch (IOException ex)
                {
                    throw new LauncherException(LauncherError.NetworkUnavailable, "The connection to " + uri.Host + " was interrupted.", ex);
                }
            }
        }

        private async Task<HttpResponseMessage> GetFollowingRedirectsAsync(Uri uri, CancellationToken token)
        {
            Uri current = uri;
            for (int hops = 0; ; hops++)
            {
                HttpResponseMessage response = await _client.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                int status = (int)response.StatusCode;
                bool redirect = status == 301 || status == 302 || status == 303 || status == 307 || status == 308;
                if (!redirect)
                {
                    return response;
                }

                Uri location = response.Headers.Location;
                response.Dispose();
                if (location == null)
                {
                    throw new LauncherException(LauncherError.SourceHttpError, current.Host + " sent a redirect without a location.");
                }

                if (hops >= MaxRedirects)
                {
                    throw new LauncherException(LauncherError.SourceHttpError, uri.Host + " redirected more than " + MaxRedirects + " times.");
                }

                Uri next = location.IsAbsoluteUri ? location : new Uri(current, location);
                if (current.Scheme == Uri.UriSchemeHttps && next.Scheme != Uri.UriSchemeHttps)
                {
                    throw new LauncherException(LauncherError.SourceHttpError, current.Host + " tried to redirect a secure download to an insecure address; refused.");
                }

                if (next.Scheme != Uri.UriSchemeHttps && next.Scheme != Uri.UriSchemeHttp)
                {
                    throw new LauncherException(LauncherError.SourceHttpError, current.Host + " redirected to an unsupported address.");
                }

                current = next;
            }
        }

        /// <summary>Disposes the underlying client.</summary>
        public void Dispose()
        {
            _client.Dispose();
        }

        private static LauncherException TooLarge(Uri uri, long maxBytes)
        {
            return new LauncherException(LauncherError.ResponseTooLarge, uri.Host + " sent more than the " + maxBytes + "-byte limit.");
        }
    }
}
