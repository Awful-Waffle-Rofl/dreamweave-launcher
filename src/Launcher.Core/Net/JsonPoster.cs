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
    /// The launcher's only JSON POST client. Built for telemetry's privacy rules: no cookies
    /// (<c>UseCookies = false</c>), no credentials, no automatic redirects (a redirect status is
    /// returned to the caller, never followed, so a POST body is never re-sent to another address),
    /// a generic <c>User-Agent</c> with no version or machine detail, and an overall timeout. The
    /// request body goes out as a bare <c>application/json</c> content type (no charset parameter).
    /// </summary>
    public sealed class JsonPoster : IJsonPoster, IDisposable
    {
        /// <summary>Default overall timeout for one POST.</summary>
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

        private readonly HttpClient _client;
        private readonly TimeSpan _timeout;

        static JsonPoster()
        {
            TlsConfig.EnsureModernTls();
        }

        /// <summary>Production constructor.</summary>
        public JsonPoster()
            : this(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false }, true, DefaultTimeout)
        {
        }

        /// <summary>Constructor for tests. The handler should have automatic redirects and cookies off.</summary>
        public JsonPoster(HttpMessageHandler handler, bool disposeHandler, TimeSpan timeout)
        {
            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            _client = new HttpClient(handler, disposeHandler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
            _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DreamWeaveLauncher", null));
            _client.DefaultRequestHeaders.ExpectContinue = false;
            _timeout = timeout;
        }

        /// <inheritdoc />
        public async Task<HttpPostResult> PostJsonAsync(Uri uri, byte[] body, CancellationToken cancellationToken)
        {
            if (uri == null)
            {
                throw new ArgumentNullException(nameof(uri));
            }

            if (body == null)
            {
                throw new ArgumentNullException(nameof(body));
            }

            if (!uri.IsAbsoluteUri || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                throw new ArgumentException("Only absolute http(s) URIs are supported.", nameof(uri));
            }

            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                cts.CancelAfter(_timeout);
                try
                {
                    using (var content = new ByteArrayContent(body))
                    {
                        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                        using (HttpResponseMessage response = await _client.PostAsync(uri, content, cts.Token).ConfigureAwait(false))
                        {
                            TimeSpan? retryAfter = null;
                            RetryConditionHeaderValue header = response.Headers.RetryAfter;
                            if (header != null)
                            {
                                if (header.Delta.HasValue)
                                {
                                    retryAfter = header.Delta.Value;
                                }
                                else if (header.Date.HasValue)
                                {
                                    retryAfter = header.Date.Value - DateTimeOffset.UtcNow;
                                }
                            }

                            if (retryAfter.HasValue && retryAfter.Value < TimeSpan.Zero)
                            {
                                retryAfter = TimeSpan.Zero;
                            }

                            return new HttpPostResult((int)response.StatusCode, retryAfter);
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

        /// <summary>Disposes the underlying client.</summary>
        public void Dispose()
        {
            _client.Dispose();
        }
    }
}
