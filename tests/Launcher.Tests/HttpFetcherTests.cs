using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests
{
    [TestClass]
    public class HttpFetcherTests
    {
        private static readonly Uri Url = new Uri("https://example.test/manifest.json");

        private static LauncherError Fetch(HttpMessageHandler handler, long maxBytes, out byte[] body)
        {
            using (var fetcher = new HttpFetcher(handler, true, TimeSpan.FromSeconds(10)))
            {
                try
                {
                    body = fetcher.GetBytesAsync(Url, maxBytes, CancellationToken.None).GetAwaiter().GetResult();
                    return LauncherError.None;
                }
                catch (LauncherException ex)
                {
                    body = null;
                    return ex.Code;
                }
            }
        }

        [TestMethod]
        public void WithinCap_ReturnsBody()
        {
            byte[] body;
            Assert.AreEqual(LauncherError.None, Fetch(new StubHandler(HttpStatusCode.OK, new byte[100], declareLength: true), 100, out body));
            Assert.AreEqual(100, body.Length);
        }

        [TestMethod]
        public void OversizeDeclaredLength_IsRejected()
        {
            byte[] body;
            Assert.AreEqual(LauncherError.ResponseTooLarge, Fetch(new StubHandler(HttpStatusCode.OK, new byte[101], declareLength: true), 100, out body));
        }

        [TestMethod]
        public void OversizeUndeclaredLength_IsRejectedWhileStreaming()
        {
            byte[] body;
            Assert.AreEqual(LauncherError.ResponseTooLarge, Fetch(new StubHandler(HttpStatusCode.OK, new byte[300000], declareLength: false), 256 * 1024, out body));
        }

        [TestMethod]
        public void NotFound_IsSourceHttpError()
        {
            byte[] body;
            Assert.AreEqual(LauncherError.SourceHttpError, Fetch(new StubHandler(HttpStatusCode.NotFound, new byte[0], true), 100, out body));
        }

        [TestMethod]
        public void TooManyRequests_IsQuotaExceeded()
        {
            byte[] body;
            Assert.AreEqual(LauncherError.SourceQuotaExceeded, Fetch(new StubHandler((HttpStatusCode)429, new byte[0], true), 100, out body));
        }

        [TestMethod]
        public void ConnectionFailure_IsNetworkUnavailable()
        {
            byte[] body;
            Assert.AreEqual(LauncherError.NetworkUnavailable, Fetch(new ThrowingHandler(), 100, out body));
        }

        [TestMethod]
        public void TlsConfig_DoesNotLeaveAnExplicitListWithoutTls12()
        {
            // Only checks the configured protocol flags after EnsureModernTls. It cannot prove which
            // version the OS negotiates (SystemDefault defers that to SChannel), so it is not a
            // minimum-TLS guarantee.
            SecurityProtocolType saved = ServicePointManager.SecurityProtocol;
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls11;
                TlsConfig.EnsureModernTls();
                Assert.IsTrue((ServicePointManager.SecurityProtocol & SecurityProtocolType.Tls12) != 0, "protocol set: " + ServicePointManager.SecurityProtocol);
            }
            finally
            {
                ServicePointManager.SecurityProtocol = saved;
            }
        }

        [TestMethod]
        public void Redirects_AreFollowed_UpToTheCap()
        {
            byte[] body;
            var handler = new RedirectHandler(HttpFetcher.MaxRedirects, "https");
            Assert.AreEqual(LauncherError.None, Fetch(handler, 100, out body));
            Assert.AreEqual(HttpFetcher.MaxRedirects + 1, handler.Calls);
            Assert.AreEqual(4, body.Length);
        }

        [TestMethod]
        public void TooManyRedirects_AreRefused()
        {
            byte[] body;
            Assert.AreEqual(LauncherError.SourceHttpError, Fetch(new RedirectHandler(HttpFetcher.MaxRedirects + 1, "https"), 100, out body));
        }

        [TestMethod]
        public void HttpsToHttpRedirect_IsRefused_BeforeFollowing()
        {
            byte[] body;
            var handler = new RedirectHandler(1, "http");
            Assert.AreEqual(LauncherError.SourceHttpError, Fetch(handler, 100, out body));
            Assert.AreEqual(1, handler.Calls, "the insecure location must never be requested");
        }

        /// <summary>Answers 302 for the first <c>hops</c> requests (to a new path on <c>scheme</c>), then 200 with a 4-byte body.</summary>
        private sealed class RedirectHandler : HttpMessageHandler
        {
            private readonly int _hops;
            private readonly string _scheme;

            public RedirectHandler(int hops, string scheme)
            {
                _hops = hops;
                _scheme = scheme;
            }

            public int Calls { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Calls++;
                if (Calls <= _hops)
                {
                    var r = new HttpResponseMessage(HttpStatusCode.Found);
                    r.Headers.Location = new Uri(_scheme + "://example.test/hop" + Calls);
                    return Task.FromResult(r);
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[4]) });
            }
        }

        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly HttpStatusCode _status;
            private readonly byte[] _body;
            private readonly bool _declareLength;

            public StubHandler(HttpStatusCode status, byte[] body, bool declareLength)
            {
                _status = status;
                _body = body;
                _declareLength = declareLength;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                HttpContent content = _declareLength
                    ? (HttpContent)new ByteArrayContent(_body)
                    : new StreamContent(new UnknownLengthStream(_body));
                return Task.FromResult(new HttpResponseMessage(_status) { Content = content });
            }
        }

        private sealed class ThrowingHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                throw new HttpRequestException("simulated DNS failure");
            }
        }

        /// <summary>Non-seekable stream so StreamContent cannot compute a Content-Length.</summary>
        private sealed class UnknownLengthStream : Stream
        {
            private readonly MemoryStream _inner;

            public UnknownLengthStream(byte[] data)
            {
                _inner = new MemoryStream(data);
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
