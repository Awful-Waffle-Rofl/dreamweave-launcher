using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Net;
using DreamWeave.Launcher.Core.Telemetry;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace DreamWeave.Launcher.Tests.Telemetry
{
    /// <summary>A loopback stand-in for the telemetry endpoint. Handler returns (status, retryAfterSeconds) or hangs.</summary>
    internal sealed class Seen
    {
        public string Method, Path, Query, ContentType, UserAgent, Cookie, Authorization;
        public byte[] Body;
    }

    internal sealed class FakeEndpoint : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly Task _loop;

        public FakeEndpoint(Func<HttpListenerRequest, byte[], Tuple<int, string>> handler)
        {
            // Pick a free port, then bind it; another process can take it in between, so retry.
            int port = 0;
            for (int attempt = 0; ; attempt++)
            {
                var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
                _listener = new HttpListener();
                _listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
                try
                {
                    _listener.Start();
                    break;
                }
                catch (HttpListenerException) when (attempt < 10)
                {
                    _listener.Close();
                }
            }

            Url = "http://127.0.0.1:" + port + "/tools/api/launcher/v1/events";
            _loop = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try
                    {
                        ctx = await _listener.GetContextAsync().ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        return;
                    }

                    try
                    {
                        byte[] body;
                        using (var ms = new MemoryStream())
                        {
                            ctx.Request.InputStream.CopyTo(ms);
                            body = ms.ToArray();
                        }

                        lock (Requests)
                        {
                            Requests.Add(new Seen { Method = ctx.Request.HttpMethod, Path = ctx.Request.Url.AbsolutePath, Query = ctx.Request.Url.Query, ContentType = ctx.Request.ContentType, UserAgent = ctx.Request.UserAgent, Cookie = ctx.Request.Headers["Cookie"], Authorization = ctx.Request.Headers["Authorization"], Body = body });
                        }

                        Hit.Set();
                        Tuple<int, string> reply = handler(ctx.Request, body);
                        if (reply == null)
                        {
                            continue; // hang: never answer
                        }

                        ctx.Response.StatusCode = reply.Item1;
                        if (reply.Item2 != null)
                        {
                            ctx.Response.AddHeader("Retry-After", reply.Item2);
                        }

                        ctx.Response.Close();
                    }
                    catch (Exception)
                    {
                    }
                }
            });
        }

        public string Url { get; }

        public ManualResetEventSlim Hit { get; } = new ManualResetEventSlim(false);

        public List<Seen> Requests { get; } = new List<Seen>();

        public int Count
        {
            get
            {
                lock (Requests)
                {
                    return Requests.Count;
                }
            }
        }

        public void Dispose()
        {
            try
            {
                _listener.Abort();
            }
            catch (Exception)
            {
            }
        }
    }

    [TestClass]
    public class TelemetryLoopbackTests
    {
        [TestMethod]
        public void RealPost_SendsContractHeadersAndBody_AndAccepts202()
        {
            using (var ep = new FakeEndpoint((r, b) => Tuple.Create(202, (string)null)))
            using (var h = new TelemetryHarness())
            using (var poster = new JsonPoster())
            using (TelemetryClient c = h.NewClient(poster, loopback: true))
            {
                c.SetOptIn(true);
                c.Configure(true, TelemetryHarness.Manifest(ep.Url));
                Assert.IsTrue(c.IsEnabled);
                c.Record("manifest", TelemetryOutcome.Succeeded, LauncherError.None, TimeSpan.FromMilliseconds(12));
                c.FlushNowAsync(CancellationToken.None).GetAwaiter().GetResult();

                Assert.AreEqual(1, ep.Count);
                Seen req = ep.Requests[0];
                Assert.AreEqual("POST", req.Method);
                Assert.AreEqual("/tools/api/launcher/v1/events", req.Path);
                Assert.AreEqual(string.Empty, req.Query);
                Assert.AreEqual("application/json", req.ContentType);
                Assert.AreEqual("DreamWeaveLauncher", req.UserAgent);
                Assert.IsNull(req.Cookie);
                Assert.IsNull(req.Authorization);
                JObject body = JObject.Parse(Encoding.UTF8.GetString(ep.Requests[0].Body));
                Assert.AreEqual(1, (int)body["schema"]);
                Assert.AreEqual("manifest", (string)body["events"][0]["step"]);
                Assert.AreEqual(0, c.QueuedCount);
            }
        }

        [TestMethod]
        public void RealPost_429WithRetryAfter_ParsesTheHeader()
        {
            int n = 0;
            using (var ep = new FakeEndpoint((r, b) => Interlocked.Increment(ref n) == 1 ? Tuple.Create(429, "3") : Tuple.Create(202, (string)null)))
            using (var h = new TelemetryHarness())
            using (var poster = new JsonPoster())
            using (TelemetryClient c = h.NewClient(poster, loopback: true))
            {
                c.SetOptIn(true);
                c.Configure(true, TelemetryHarness.Manifest(ep.Url));
                c.Record("manifest", TelemetryOutcome.Succeeded);
                c.FlushNowAsync(CancellationToken.None).GetAwaiter().GetResult();
                Assert.AreEqual(2, ep.Count);
                Assert.IsTrue(h.Clock.Delays.Contains(TimeSpan.FromSeconds(3)));
            }
        }

        [TestMethod]
        public void RealPost_Redirect_IsNotFollowed_AndTheBatchIsDropped()
        {
            using (var target = new FakeEndpoint((r, b) => Tuple.Create(202, (string)null)))
            using (var ep = new FakeEndpoint((r, b) => Tuple.Create(307, (string)null)))
            using (var h = new TelemetryHarness())
            using (var poster = new JsonPoster())
            using (TelemetryClient c = h.NewClient(poster, loopback: true))
            {
                c.SetOptIn(true);
                c.Configure(true, TelemetryHarness.Manifest(ep.Url));
                c.Record("manifest", TelemetryOutcome.Succeeded);
                c.FlushNowAsync(CancellationToken.None).GetAwaiter().GetResult();
                Assert.AreEqual(1, ep.Count);
                Assert.AreEqual(0, target.Count);
                Assert.AreEqual(0, c.QueuedCount);
            }
        }

        [TestMethod]
        public void Record_NeverBlocks_WhileTheEndpointHangs()
        {
            using (var ep = new FakeEndpoint((r, b) => null))
            using (var h = new TelemetryHarness())
            using (var poster = new JsonPoster(new System.Net.Http.HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }, true, TimeSpan.FromSeconds(30)))
            {
                TelemetryClient c = new TelemetryClient(h.Paths, h.Store, null, new TelemetryOptions
                {
                    Poster = poster,
                    LauncherVersion = "1.4.0",
                    OsBuild = 26200,
                    AllowLoopbackHttp = true,
                    Debounce = TimeSpan.Zero,
                });
                try
                {
                    c.SetOptIn(true);
                    c.Configure(true, TelemetryHarness.Manifest(ep.Url));
                    c.Record("manifest", TelemetryOutcome.Started);
                    Assert.IsTrue(ep.Hit.Wait(TimeSpan.FromSeconds(10)), "the flusher reached the hanging endpoint");

                    var sw = Stopwatch.StartNew();
                    for (int i = 0; i < 100; i++)
                    {
                        c.RecordComponent("step-" + (i % 9), TelemetryOutcome.Succeeded);
                    }

                    sw.Stop();
                    Assert.IsTrue(sw.ElapsedMilliseconds < 5000, "100 Record calls took " + sw.ElapsedMilliseconds + " ms while a request hung");
                }
                finally
                {
                    var sw = Stopwatch.StartNew();
                    c.Dispose();
                    Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(20), "Dispose cancels the hung request");
                }
            }
        }

        [TestMethod]
        public void Record_BeforeConfigure_CostsNothing()
        {
            using (var h = new TelemetryHarness())
            using (TelemetryClient c = h.NewClient())
            {
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < 10000; i++)
                {
                    c.Record("manifest", TelemetryOutcome.Started);
                }

                Assert.IsTrue(sw.ElapsedMilliseconds < 5000);
                Assert.AreEqual(0, c.QueuedCount);
            }
        }
    }
}
