using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Net;
using DreamWeave.Launcher.Core.State;
using DreamWeave.Launcher.Core.Telemetry;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace DreamWeave.Launcher.Tests.Telemetry
{
    internal sealed class CancelOnlyPoster : IJsonPoster
    {
        public async Task<HttpPostResult> PostJsonAsync(Uri uri, byte[] body, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                throw new LauncherException(LauncherError.Cancelled, "Cancelled.", ex);
            }

            return new HttpPostResult(202, null);
        }
    }

    internal sealed class FixedResponseHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _make;

        public FixedResponseHandler(Func<HttpResponseMessage> make)
        {
            _make = make;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_make());
        }
    }

    /// <summary>Content whose body must never be read.</summary>
    internal sealed class ExplodingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext context)
        {
            throw new InvalidOperationException("the body must not be read");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 10;
            return true;
        }
    }

    [TestClass]
    public class TelemetryReviewFixTests
    {
        private static void Flush(TelemetryClient c)
        {
            c.FlushNowAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        private static void WaitFor(Func<bool> condition, string what)
        {
            DateTime end = DateTime.UtcNow.AddSeconds(15);
            while (!condition())
            {
                if (DateTime.UtcNow > end)
                {
                    Assert.Fail("timed out waiting for " + what);
                }

                Thread.Sleep(10);
            }
        }

        // ---- F1: opt-out race ----

        [TestMethod]
        public void OptOutDuringInterBatchDelay_PostsNothingMore_AndOldRequestCarriesOldId()
        {
            using (var h = new TelemetryHarness())
            using (TelemetryClient c = h.NewClient(tweak: o => o.MinRequestInterval = TimeSpan.FromSeconds(6)))
            {
                c.SetOptIn(true);
                c.Configure(true, TelemetryHarness.Manifest());
                Guid oldId = h.Store.InstallId;
                for (int i = 0; i < 60; i++)
                {
                    c.Record("manifest", TelemetryOutcome.Succeeded);
                }

                h.Clock.OnDelay = () => c.SetOptIn(false);
                Flush(c);

                Assert.AreEqual(1, h.Poster.Bodies.Count, "no request after SetOptIn(false) returned");
                Assert.AreEqual(oldId.ToString("D"), (string)h.Poster.Bodies[0]["installId"]);
                Assert.AreNotEqual(oldId, h.Store.InstallId);
                Assert.AreEqual(0, c.QueuedCount);
                Assert.IsFalse(File.Exists(h.QueueFile));
            }
        }

        [TestMethod]
        public void OptOutDuringRetryAfterWait_DoesNotRetry()
        {
            using (var h = new TelemetryHarness())
            {
                h.Poster.Then(429, TimeSpan.FromSeconds(9)).Then(202);
                using (TelemetryClient c = h.EnabledClient())
                {
                    Guid oldId = h.Store.InstallId;
                    c.Record("manifest", TelemetryOutcome.Succeeded);
                    h.Clock.OnDelay = () => c.SetOptIn(false);
                    Flush(c);

                    Assert.AreEqual(1, h.Poster.Bodies.Count, "the retry after the wait was not posted");
                    Assert.AreEqual(oldId.ToString("D"), (string)h.Poster.Bodies[0]["installId"]);
                    Assert.IsFalse(File.Exists(h.QueueFile));
                }
            }
        }

        // ---- F2: stale queue files ----

        [TestMethod]
        public void ConfigureOptedOut_DeletesALeftoverQueueFile()
        {
            using (var h = new TelemetryHarness())
            {
                Directory.CreateDirectory(h.Paths.DataDir);
                File.WriteAllText(h.QueueFile, "{\"version\":1,\"events\":[]}");
                File.WriteAllText(h.QueueFile + ".abc.tmp", "x");
                using (TelemetryClient c = h.NewClient())
                {
                    c.Configure(false, TelemetryHarness.Manifest());
                    Assert.IsFalse(File.Exists(h.QueueFile));
                    Assert.IsFalse(File.Exists(h.QueueFile + ".abc.tmp"));
                }
            }
        }

        [TestMethod]
        public void OptingBackIn_DiscardsAQueueLeftFromBefore()
        {
            using (var h = new TelemetryHarness())
            {
                h.Poster.ThenNetworkError();
                using (TelemetryClient c1 = h.EnabledClient())
                {
                    c1.Record("manifest", TelemetryOutcome.Succeeded);
                    Flush(c1);
                }

                Assert.IsTrue(File.Exists(h.QueueFile));
                h.Store.Update(s => s.TelemetryOptIn = false);

                var second = new FakePoster();
                using (TelemetryClient c2 = h.NewClient(second))
                {
                    c2.SetOptIn(true);
                    c2.Configure(true, TelemetryHarness.Manifest());
                    Assert.AreEqual(0, c2.QueuedCount, "the old queue was not loaded");
                    Assert.IsFalse(File.Exists(h.QueueFile));
                    Flush(c2);
                    Assert.AreEqual(0, second.Bodies.Count);
                    c2.Record("launch", TelemetryOutcome.Started);
                    Flush(c2);
                    Assert.AreEqual(1, second.AllEvents.Count());
                }
            }
        }

        // ---- F3: 503 codes ----

        [TestMethod]
        public void Disabled503_IsTerminal_NoRetryNoShortWait()
        {
            using (var h = new TelemetryHarness())
            {
                h.Poster.Then(503, null, "disabled");
                using (TelemetryClient c = h.EnabledClient())
                {
                    c.Record("manifest", TelemetryOutcome.Succeeded);
                    Flush(c);
                    Assert.AreEqual(1, h.Poster.Bodies.Count);
                    Assert.AreEqual(0, h.Clock.Delays.Count, "no wait was awaited");
                    Assert.AreEqual(1, c.QueuedCount);

                    c.Record("manifest", TelemetryOutcome.Started);
                    Flush(c);
                    Assert.AreEqual(1, h.Poster.Bodies.Count, "silent for the rest of the process");
                    Assert.IsTrue(c.NotBeforeUtc >= h.Clock.UtcNow + TimeSpan.FromHours(5));
                }
            }
        }

        [TestMethod]
        public void Busy503_StillGetsOneRetry()
        {
            using (var h = new TelemetryHarness())
            {
                h.Poster.Then(503, TimeSpan.FromSeconds(60), "busy").Then(202);
                using (TelemetryClient c = h.EnabledClient())
                {
                    c.Record("manifest", TelemetryOutcome.Succeeded);
                    Flush(c);
                    Assert.AreEqual(2, h.Poster.Bodies.Count);
                    Assert.IsTrue(h.Clock.Delays.Contains(TimeSpan.FromSeconds(60)));
                    Assert.AreEqual(0, c.QueuedCount);
                }
            }
        }

        private static HttpPostResult PostVia(Func<HttpResponseMessage> make)
        {
            using (var poster = new JsonPoster(new FixedResponseHandler(make), true, TimeSpan.FromSeconds(10)))
            {
                return poster.PostJsonAsync(new Uri("https://www.example.test/e"), new byte[] { 123, 125 }, CancellationToken.None).GetAwaiter().GetResult();
            }
        }

        [TestMethod]
        public void JsonPoster_Reads503Code_AndRetryAfter()
        {
            HttpPostResult r = PostVia(() =>
            {
                var m = new HttpResponseMessage((HttpStatusCode)503) { Content = new StringContent("{\"code\":\"disabled\",\"message\":\"x\"}", Encoding.UTF8, "application/json") };
                m.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
                return m;
            });
            Assert.AreEqual(503, r.Status);
            Assert.AreEqual("disabled", r.Code);
            Assert.AreEqual(TimeSpan.FromSeconds(60), r.RetryAfter);
        }

        [TestMethod]
        public void JsonPoster_CodeIsNull_ForOversizeJunkAndOddValues()
        {
            Assert.IsNull(PostVia(() => new HttpResponseMessage((HttpStatusCode)503) { Content = new StringContent(new string(' ', 2000) + "{\"code\":\"disabled\"}") }).Code, "beyond the 1 KiB cap");
            Assert.IsNull(PostVia(() => new HttpResponseMessage((HttpStatusCode)503) { Content = new StringContent("not json") }).Code);
            Assert.IsNull(PostVia(() => new HttpResponseMessage((HttpStatusCode)503) { Content = new StringContent("{\"code\":\"Has Space!\"}") }).Code);
        }

        [TestMethod]
        public void JsonPoster_NeverReadsTheBody_OfOtherStatuses()
        {
            HttpPostResult r = PostVia(() => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new ExplodingContent() });
            Assert.AreEqual(400, r.Status);
            Assert.IsNull(r.Code);
            HttpPostResult ok = PostVia(() => new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new ExplodingContent() });
            Assert.AreEqual(202, ok.Status);
        }

        // ---- F5: cancellation ----

        [TestMethod]
        public void FlushNow_RethrowsCallerCancellationAsOperationCanceled_AndKeepsTheQueue()
        {
            using (var h = new TelemetryHarness())
            using (TelemetryClient c = h.EnabledClient(new CancelOnlyPoster()))
            {
                c.Record("manifest", TelemetryOutcome.Succeeded);
                using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
                {
                    Assert.Throws<OperationCanceledException>(() => c.FlushNowAsync(cts.Token).GetAwaiter().GetResult());
                }

                Assert.AreEqual(1, c.QueuedCount);
            }
        }

        // ---- F7: step vocabulary ----

        [TestMethod]
        public void Record_AcceptsOnlyTheFixedSteps_ComponentsGoThroughRecordComponent()
        {
            using (var h = new TelemetryHarness())
            using (TelemetryClient c = h.EnabledClient())
            {
                foreach (string s in new[] { "manifest", "ac-client", "eor-dats", "palette-leak-fix", "launch", "launcher-update", "self-install", "uninstall" })
                {
                    Assert.IsTrue(TelemetryEvent.IsStepValid(s), s);
                    c.Record(s, TelemetryOutcome.Started);
                }

                Assert.AreEqual(8, c.QueuedCount);
                c.Record("player-name-here", TelemetryOutcome.Started);
                c.Record("C:\\Games", TelemetryOutcome.Started);
                c.Record(null, TelemetryOutcome.Started);
                Assert.AreEqual(8, c.QueuedCount, "unknown steps are dropped by Record");

                c.RecordComponent("mega-extras", TelemetryOutcome.Succeeded);
                c.RecordComponent("Not A Component", TelemetryOutcome.Succeeded);
                Assert.AreEqual(9, c.QueuedCount);
            }
        }

        // ---- F8 ----

        [TestMethod]
        public void StateLockedDuringOptOut_StillStopsAndWipesLocally_ThenThrows()
        {
            using (var h = new TelemetryHarness())
            {
                var shortStore = new StateStore(h.Paths.StateFile, TimeSpan.FromMilliseconds(200));
                h.Poster.ThenNetworkError();
                using (TelemetryClient c = h.NewClient(store: shortStore))
                {
                    c.SetOptIn(true);
                    c.Configure(true, TelemetryHarness.Manifest());
                    c.Record("manifest", TelemetryOutcome.Succeeded);
                    Flush(c);
                    Assert.IsTrue(File.Exists(h.QueueFile));

                    using (new FileStream(h.Paths.StateFile + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                    {
                        LauncherException ex = Assert.Throws<LauncherException>(() => c.SetOptIn(false));
                        Assert.AreEqual(LauncherError.StateLocked, ex.Code);
                    }

                    Assert.IsFalse(c.IsEnabled, "recording stopped even though the state file could not be written");
                    Assert.AreEqual(0, c.QueuedCount);
                    Assert.IsFalse(File.Exists(h.QueueFile));
                    c.Record("manifest", TelemetryOutcome.Started);
                    Assert.AreEqual(0, c.QueuedCount);
                    Assert.IsTrue(new StateStore(h.Paths.StateFile).Read(s => s.TelemetryOptIn), "the saved flag is unchanged: the caller must retry");
                }
            }
        }

        [TestMethod]
        public void WorkerLoop_RunsUnderTheFakeClock_WithDebounceAndFailureCooldown()
        {
            using (var h = new TelemetryHarness())
            {
                h.Poster.ThenNetworkError().Then(202);
                using (TelemetryClient c = h.NewClient(startFlusher: true, tweak: o =>
                {
                    o.Debounce = TimeSpan.FromSeconds(5);
                    o.FailureCooldown = TimeSpan.FromSeconds(60);
                }))
                {
                    c.SetOptIn(true);
                    c.Configure(true, TelemetryHarness.Manifest());
                    c.Record("manifest", TelemetryOutcome.Started);
                    WaitFor(() => h.Poster.Bodies.Count == 1 && c.NotBeforeUtc > h.Clock.UtcNow, "the failed first send and its cooldown");
                    Assert.IsTrue(h.Clock.Delays.Contains(TimeSpan.FromSeconds(5)), "the debounce was awaited");

                    c.Record("manifest", TelemetryOutcome.Succeeded);
                    WaitFor(() => h.Poster.Bodies.Count == 2, "the second send");
                    Assert.IsTrue(h.Clock.Delays.Any(d => d >= TimeSpan.FromSeconds(50) && d <= TimeSpan.FromSeconds(60)), "the cooldown was awaited");
                    Assert.AreEqual(2, ((JArray)h.Poster.Bodies[1]["events"]).Count, "the kept event was resent with the new one");
                    WaitFor(() => c.QueuedCount == 0, "the queue to drain");
                }
            }
        }
    }
}
