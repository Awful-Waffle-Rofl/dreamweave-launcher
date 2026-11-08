using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DreamWeave.Launcher.Core.Downloads
{
    /// <summary>A temporary download URL for one mega.nz file, from the API's <c>g</c> command.</summary>
    public sealed class MegaDownloadTicket
    {
        /// <summary>Creates a ticket.</summary>
        public MegaDownloadTicket(Uri url, long size)
        {
            Url = url ?? throw new ArgumentNullException(nameof(url));
            Size = size;
        }

        /// <summary>The storage server URL serving the ENCRYPTED file body (supports HTTP ranges).</summary>
        public Uri Url { get; }

        /// <summary>The file's length in bytes (the same encrypted or decrypted: CTR adds nothing).</summary>
        public long Size { get; }
    }

    /// <summary>The one mega.nz API call the launcher makes. Tests substitute a fake or point it at a loopback server.</summary>
    public interface IMegaApi
    {
        /// <summary>Asks for a download URL for the public file <paramref name="handle"/>.</summary>
        /// <exception cref="LauncherException">A classified failure (see <see cref="MegaErrorMapper"/>).</exception>
        /// <exception cref="TransientDownloadException">A failure worth retrying.</exception>
        Task<MegaDownloadTicket> GetDownloadAsync(string handle, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Anonymous <c>g</c> command against mega's JSON API: POST <c>[{"a":"g","g":1,"ssl":2,"p":"&lt;handle&gt;"}]</c>
    /// to <c>https://g.api.mega.co.nz/cs?id=&lt;n&gt;</c>. The answer is an array holding either an object with
    /// <c>g</c> (the storage URL) and <c>s</c> (the size), or a negative result code; a bare negative number
    /// is a request-level error. <c>ssl: 2</c> asks for an https storage URL.
    /// </summary>
    public sealed class MegaApi : IMegaApi, IDisposable
    {
        /// <summary>mega's API endpoint.</summary>
        public static readonly Uri DefaultBaseUri = new Uri("https://g.api.mega.co.nz/");

        private readonly HttpClient _client;
        private readonly Uri _base;
        private int _sequence = new Random().Next(1, int.MaxValue / 2);

        static MegaApi()
        {
            Net.TlsConfig.EnsureModernTls();
        }

        /// <summary>Production constructor.</summary>
        public MegaApi()
            : this(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false }, true, DefaultBaseUri)
        {
        }

        /// <summary>Constructor for tests.</summary>
        public MegaApi(HttpMessageHandler handler, bool disposeHandler, Uri baseUri)
        {
            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            _base = baseUri ?? throw new ArgumentNullException(nameof(baseUri));
            _client = new HttpClient(handler, disposeHandler) { Timeout = Timeout.InfiniteTimeSpan };
            _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DreamWeaveLauncher", null));
            _client.DefaultRequestHeaders.ExpectContinue = false;
        }

        /// <inheritdoc />
        public async Task<MegaDownloadTicket> GetDownloadAsync(string handle, CancellationToken cancellationToken)
        {
            int id = Interlocked.Increment(ref _sequence);
            var uri = new Uri(_base, "cs?id=" + id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            string body = "[{\"a\":\"g\",\"g\":1,\"ssl\":2,\"p\":" + JsonConvert.ToString(handle ?? string.Empty) + "}]";
            string text;
            try
            {
                using (var content = new StringContent(body, Encoding.UTF8, "application/json"))
                using (HttpResponseMessage response = await _client.PostAsync(uri, content, cancellationToken).ConfigureAwait(false))
                {
                    int status = (int)response.StatusCode;
                    if (status < 200 || status > 299)
                    {
                        bool transient;
                        LauncherException mapped = MegaErrorMapper.MapStatus(status, out transient);
                        if (transient)
                        {
                            throw new TransientDownloadException(mapped);
                        }

                        throw mapped;
                    }

                    text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                throw new LauncherException(LauncherError.Cancelled, "Cancelled.", ex);
            }
            catch (HttpRequestException ex)
            {
                throw new TransientDownloadException(new LauncherException(LauncherError.NetworkUnavailable, "Could not reach mega.nz.", ex));
            }

            return Parse(text);
        }

        /// <summary>Parses the API's answer to one <c>g</c> command.</summary>
        internal static MegaDownloadTicket Parse(string text)
        {
            JToken root;
            try
            {
                root = JToken.Parse(text ?? string.Empty);
            }
            catch (JsonException ex)
            {
                throw new TransientDownloadException(new LauncherException(LauncherError.SourceHttpError, "mega.nz sent an answer that could not be read.", ex));
            }

            JToken first = root is JArray arr && arr.Count > 0 ? arr[0] : root;
            if (first.Type == JTokenType.Integer)
            {
                throw Code((int)first);
            }

            var obj = first as JObject;
            if (obj == null)
            {
                throw new TransientDownloadException(new LauncherException(LauncherError.SourceHttpError, "mega.nz sent an answer that could not be read."));
            }

            JToken e = obj["e"];
            if (e != null && e.Type == JTokenType.Integer && (int)e < 0)
            {
                throw Code((int)e);
            }

            JToken g = obj["g"];
            string url = g is JArray ga && ga.Count > 0 ? (string)ga[0] : (g != null && g.Type == JTokenType.String ? (string)g : null);
            JToken s = obj["s"];
            Uri parsed;
            if (url == null || s == null || s.Type != JTokenType.Integer || !Uri.TryCreate(url, UriKind.Absolute, out parsed) || (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp))
            {
                throw new TransientDownloadException(new LauncherException(LauncherError.SourceHttpError, "mega.nz did not hand out a download address."));
            }

            return new MegaDownloadTicket(parsed, (long)s);
        }

        /// <summary>Disposes the HTTP client.</summary>
        public void Dispose()
        {
            _client.Dispose();
        }

        private static Exception Code(int code)
        {
            bool transient;
            LauncherException mapped = MegaErrorMapper.MapApiCode(code, out transient);
            return transient ? (Exception)new TransientDownloadException(mapped) : mapped;
        }
    }
}
