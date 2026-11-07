using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json.Linq;
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

        /// <summary>Most bytes read from a 503 response body (to learn its <c>code</c>); no other body is ever read.</summary>
        public const int MaxErrorBodyBytes = 1024;

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
                        using (var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = content })
                        using (HttpResponseMessage response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
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

                            int status = (int)response.StatusCode;
                            string code = null;
                            if (status == 503)
                            {
                                code = await ReadCodeAsync(response, cts.Token).ConfigureAwait(false);
                            }

                            return new HttpPostResult(status, retryAfter, code);
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

        /// <summary>
        /// Reads at most <see cref="MaxErrorBodyBytes"/> bytes and returns the JSON <c>code</c> if it is a
        /// short lower-case token, else null. Bounded by <paramref name="token"/> (the poster timeout and the
        /// caller's token): on net48 the response stream ignores the token passed to <c>ReadAsync</c>, so the
        /// response itself is disposed when the token fires, which aborts the read. Expiry yields null.
        /// </summary>
        private static async Task<string> ReadCodeAsync(HttpResponseMessage response, CancellationToken token)
        {
            try
            {
                using (token.Register(() => response.Dispose()))
                using (Stream s = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                {
                    var buffer = new byte[MaxErrorBodyBytes];
                    int total = 0;
                    while (total < buffer.Length)
                    {
                        int n = await s.ReadAsync(buffer, total, buffer.Length - total, token).ConfigureAwait(false);
                        if (n <= 0)
                        {
                            break;
                        }

                        total += n;
                    }

                    JObject o = JObject.Parse(Encoding.UTF8.GetString(buffer, 0, total));
                    string code = o["code"] != null && o["code"].Type == JTokenType.String ? (string)o["code"] : null;
                    return code != null && System.Text.RegularExpressions.Regex.IsMatch(code, "^[a-z_]{1,32}\\z") ? code : null;
                }
            }
            catch (Exception)
            {
                // Includes cancellation, a disposed response and unparseable bodies: no code.
                return null;
            }
        }
        /// <summary>Disposes the underlying client.</summary>
        public void Dispose()
        {
            _client.Dispose();
        }
    }
}
