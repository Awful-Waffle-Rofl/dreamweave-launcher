using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.Downloads
{
    /// <summary>Redirect rules for <see cref="HttpStreamClient"/>, mirroring HttpFetcherTests.</summary>
    [TestClass]
    public class HttpStreamClientTests
    {
        private static readonly Uri Url = new Uri("https://example.test/file.bin");

        /// <summary>Answers 302 for the first <c>hops</c> requests with <c>location(n)</c>, then 206.</summary>
        private sealed class RedirectHandler : HttpMessageHandler
        {
            private readonly int _hops;
            private readonly Func<int, Uri> _location;

            public RedirectHandler(int hops, Func<int, Uri> location)
            {
                _hops = hops;
                _location = location;
            }

            public List<HttpRequestMessage> Seen { get; } = new List<HttpRequestMessage>();

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Seen.Add(request);
                if (Seen.Count <= _hops)
                {
                    var r = new HttpResponseMessage(HttpStatusCode.Found);
                    r.Headers.Location = _location(Seen.Count);
                    return Task.FromResult(r);
                }

                return Task.FromResult(new HttpResponseMessage((HttpStatusCode)206) { Content = new ByteArrayContent(new byte[1]) });
            }
        }

        private static LauncherError Get(RedirectHandler handler, out HttpStreamResponse response)
        {
            using (var client = new HttpStreamClient(handler, true, TimeSpan.FromSeconds(10)))
            {
                try
                {
                    response = client.GetAsync(Url, 0, 0, null, CancellationToken.None).GetAwaiter().GetResult();
                    return LauncherError.None;
                }
                catch (LauncherException ex)
                {
                    response = null;
                    return ex.Code;
                }
            }
        }

        [TestMethod]
        public void Redirects_AreFollowed_UpToTheCap_WithRangeOnEveryHop()
        {
            var handler = new RedirectHandler(HttpFetcher.MaxRedirects, n => new Uri("https://example.test/hop" + n));
            HttpStreamResponse response;
            Assert.AreEqual(LauncherError.None, Get(handler, out response));
            using (response)
            {
                Assert.AreEqual(206, response.StatusCode);
                Assert.AreEqual(new Uri("https://example.test/hop" + HttpFetcher.MaxRedirects), response.FinalUri);
            }

            Assert.AreEqual(HttpFetcher.MaxRedirects + 1, handler.Seen.Count);
            foreach (HttpRequestMessage r in handler.Seen)
            {
                Assert.AreEqual("bytes=0-0", r.Headers.Range.ToString());
            }
        }

        [TestMethod]
        public void SixHops_AreRefused()
        {
            var handler = new RedirectHandler(HttpFetcher.MaxRedirects + 1, n => new Uri("https://example.test/hop" + n));
            HttpStreamResponse response;
            Assert.AreEqual(LauncherError.SourceHttpError, Get(handler, out response));
            Assert.AreEqual(HttpFetcher.MaxRedirects + 1, handler.Seen.Count, "the sixth location is never requested");
        }

        [TestMethod]
        public void HttpsToHttp_IsRefused_BeforeFollowing()
        {
            var handler = new RedirectHandler(1, n => new Uri("http://example.test/insecure"));
            HttpStreamResponse response;
            Assert.AreEqual(LauncherError.SourceHttpError, Get(handler, out response));
            Assert.AreEqual(1, handler.Seen.Count);
        }

        [TestMethod]
        public void MissingLocation_IsRefused()
        {
            var handler = new RedirectHandler(1, n => null);
            HttpStreamResponse response;
            Assert.AreEqual(LauncherError.SourceHttpError, Get(handler, out response));
            Assert.AreEqual(1, handler.Seen.Count);
        }

        [TestMethod]
        public void FtpLocation_IsRefused()
        {
            var handler = new RedirectHandler(1, n => new Uri("ftp://example.test/file.bin"));
            HttpStreamResponse response;
            Assert.AreEqual(LauncherError.SourceHttpError, Get(handler, out response));
            Assert.AreEqual(1, handler.Seen.Count);
        }
    }
}
