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
        public void TlsConfig_LeavesTls12Available()
        {
            TlsConfig.EnsureModernTls();
            var p = ServicePointManager.SecurityProtocol;
            Assert.IsTrue(p == SecurityProtocolType.SystemDefault || (p & SecurityProtocolType.Tls12) != 0, "protocol set: " + p);
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
