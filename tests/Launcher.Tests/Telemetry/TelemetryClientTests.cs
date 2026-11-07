using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Telemetry;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace DreamWeave.Launcher.Tests.Telemetry
{
    [TestClass]
    public class TelemetryClientTests
    {
        private static void Flush(TelemetryClient c)
        {
            c.FlushNowAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        [TestMethod]
        public void OptedOut_NothingRecordedQueuedOrSent()
        {
            using (var h = new TelemetryHarness())
            using (TelemetryClient c = h.NewClient())
            {
                c.Configure(false, TelemetryHarness.Manifest());
                Assert.IsFalse(c.IsEnabled);
                c.Record("manifest", TelemetryOutcome.Succeeded);
                Flush(c);
                c.Dispose();
                Assert.AreEqual(0, h.Poster.Bodies.Count);
                Assert.IsFalse(File.Exists(h.QueueFile));
            }
        }

        [TestMethod]
        public void NeverConfigured_RecordsNothing()
        {
            using (var h = new TelemetryHarness())
            using (TelemetryClient c = h.NewClient())
            {
                h.Store.Update(s => s.TelemetryOptIn = true);
                c.Record("manifest", TelemetryOutcome.Succeeded);
                Flush(c);
                Assert.AreEqual(0, h.Poster.Bodies.Count);
                Assert.IsFalse(File.Exists(h.QueueFile));
            }
        }

        [TestMethod]
        public void OptedIn_ButEndpointNull_NothingHappens()
        {
            using (var h = new TelemetryHarness())
            using (TelemetryClient c = h.NewClient())
            {
                h.Store.Update(s => s.TelemetryOptIn = true);
                c.Configure(true, TelemetryHarness.Manifest(endpoint: null));
                Assert.IsFalse(c.IsEnabled);
                c.Record("manifest", TelemetryOutcome.Succeeded);
                Flush(c);
                c.Dispose();
                Assert.AreEqual(0, h.Poster.Bodies.Count);
                Assert.IsFalse(File.Exists(h.QueueFile));
            }
        }

        [TestMethod]
        public void OptedIn_ButEndpointInvalid_NothingHappens()
        {
            using (var h = new TelemetryHarness())
            using (TelemetryClient c = h.NewClient())
            {
                c.Configure(true, TelemetryHarness.Manifest("https://collector.elsewhere.test/e"));
                Assert.IsFalse(c.IsEnabled);
                c.Record("manifest", TelemetryOutcome.Succeeded);
                Flush(c);
                Assert.AreEqual(0, h.Poster.Bodies.Count);
            }
        }

        [TestMethod]
        public void Batch_MatchesTheContractExactly()
        {
            using (var h = new TelemetryHarness())
            using (TelemetryClient c = h.EnabledClient())
            {
                c.Record("manifest", TelemetryOutcome.Started);
                c.Record("manifest", TelemetryOutcome.Succeeded, LauncherError.None, TimeSpan.FromMilliseconds(812));
                c.Record("ac-client", TelemetryOutcome.Failed, LauncherError.HashMismatch, TimeSpan.FromHours(30));
                c.Record("ac-client", TelemetryOutcome.Cancelled, LauncherError.Cancelled, TimeSpan.FromSeconds(-5));
                Flush(c);

                Assert.AreEqual(1, h.Poster.Bodies.Count);
                Assert.AreEqual(TelemetryHarness.Endpoint, h.Poster.Uris[0].ToString());
                Assert.IsTrue(h.Poster.BodyLengths[0] <= 16384);
                JObject body = h.Poster.Bodies[0];
                CollectionAssert.AreEquivalent(new[] { "schema", "installId", "runId", "launcherVersion", "osBuild", "events" }, body.Properties().Select(p => p.Name).ToArray());
                Assert.AreEqual(1, (int)body["schema"]);
                Assert.AreEqual(JTokenType.Integer, body["schema"].Type);
                Assert.AreEqual(h.Store.InstallId, Guid.ParseExact((string)body["installId"], "D"));
                Assert.AreEqual(c.RunId, Guid.ParseExact((string)body["runId"], "D"));
                Assert.AreEqual("1.4.0", (string)body["launcherVersion"]);
                Assert.AreEqual(JTokenType.Integer, body["osBuild"].Type);
                int os = (int)body["osBuild"];
                Assert.IsTrue(os >= 7600 && os <= 99999);

                var events = (JArray)body["events"];
                Assert.IsTrue(events.Count >= 1 && events.Count <= 50);
                Assert.AreEqual(4, events.Count);
                for (int i = 0; i < events.Count; i++)
                {
                    var e = (JObject)events[i];
                    CollectionAssert.AreEquivalent(new[] { "seq", "step", "outcome", "error", "durationMs" }, e.Properties().Select(p => p.Name).ToArray());
                    Assert.AreEqual(i, (int)e["seq"]);
                    Assert.IsTrue(TelemetryEvent.IsStepValid((string)e["step"]));
                    Assert.IsTrue(TelemetryEvent.IsOutcomeValid((string)e["outcome"]));
                    int err = (int)e["error"];
                    Assert.IsTrue(err >= 0 && err <= 65535);
                }

                Assert.AreEqual(JTokenType.Null, events[0]["durationMs"].Type);
                Assert.AreEqual(812L, (long)events[1]["durationMs"]);
                Assert.AreEqual("failed", (string)events[2]["outcome"]);
                Assert.AreEqual((int)LauncherError.HashMismatch, (int)events[2]["error"]);
                Assert.AreEqual(86400000L, (long)events[2]["durationMs"], "clamped to the contract maximum");
                Assert.AreEqual(0L, (long)events[3]["durationMs"], "negative clamps to zero");
            }
        }

        [TestMethod]
        public void InvalidSteps_AreDroppedAtRecord()
        {
            using (var h = new TelemetryHarness())
            using (TelemetryClient c = h.EnabledClient())
            {
                foreach (string bad in new[] { null, "", "a", "A1", "1abc", "has space", "under_score", "x" + new string('a', 32), "ok\n" })
                {
                    c.RecordComponent(bad, TelemetryOutcome.Started);
                }

                c.RecordComponent("ab", TelemetryOutcome.Started);
                c.RecordComponent("a" + new string('b', 31), TelemetryOutcome.Started);
                Flush(c);
                Assert.AreEqual(2, h.Poster.AllEvents.Count());
            }
        }

        [TestMethod]
        public void BatchesOfFifty_PerRequest()
        {
            using (var h = new TelemetryHarness())
            using (TelemetryClient c = h.EnabledClient())
            {
                for (int i = 0; i < 120; i++)
                {
                    c.RecordComponent("step-" + (i % 10).ToString(CultureInfo.InvariantCulture), TelemetryOutcome.Succeeded);
                }

                Flush(c);
                CollectionAssert.AreEqual(new[] { 50, 50, 20 }, h.Poster.Bodies.Select(b => ((JArray)b["events"]).Count).ToArray());
                CollectionAssert.AreEqual(Enumerable.Range(0, 120).ToArray(), h.Poster.AllEvents.Select(e => (int)e["seq"]).ToArray());
                Assert.IsTrue(h.Poster.BodyLengths.All(n => n <= 16384));
                Assert.AreEqual(0, c.QueuedCountForTests());
                Assert.IsFalse(File.Exists(h.QueueFile), "an empty queue leaves no file");
            }
        }

        [TestMethod]
        public void RateLimited_WaitsRetryAfter_ThenRetriesOnce()
        {
            using (var h = new TelemetryHarness())
            {
                h.Poster.Then(429, TimeSpan.FromSeconds(7)).Then(202);
                using (TelemetryClient c = h.EnabledClient())
                {
                    c.Record("manifest", TelemetryOutcome.Succeeded);
                    Flush(c);

                    Assert.AreEqual(2, h.Poster.Bodies.Count);
                    Assert.IsTrue(h.Clock.Delays.Contains(TimeSpan.FromSeconds(7)), "waited the server's Retry-After");
                    Assert.AreEqual(0, c.QueuedCountForTests());
                }
            }
        }

        [TestMethod]
        public void ServiceUnavailable_IsRetriedOnce_ThenKeptQueuedIfStillBusy()
        {
            using (var h = new TelemetryHarness())
            {
                h.Poster.Then(503, TimeSpan.FromSeconds(60)).Then(503, TimeSpan.FromSeconds(60));
                using (TelemetryClient c = h.EnabledClient())
                {
                    c.Record("manifest", TelemetryOutcome.Succeeded);
                    Flush(c);

                    Assert.AreEqual(2, h.Poster.Bodies.Count, "exactly one retry, no loop");
                    Assert.AreEqual(1, c.QueuedCountForTests());
                    Assert.IsTrue(File.Exists(h.QueueFile), "kept for a later start");
                }
            }
        }

        [TestMethod]
        public void RetryAfterBeyondTheCap_IsNotWaitedOut()
        {
            using (var h = new TelemetryHarness())
            {
                h.Poster.Then(429, TimeSpan.FromHours(2));
                using (TelemetryClient c = h.EnabledClient())
                {
                    c.Record("manifest", TelemetryOutcome.Succeeded);
                    Flush(c);
                    Assert.AreEqual(1, h.Poster.Bodies.Count);
                    Assert.AreEqual(1, c.QueuedCountForTests());
                    Assert.IsFalse(h.Clock.Delays.Any(d => d >= TimeSpan.FromHours(1)));
                }
            }
        }

        [TestMethod]
        public void MissingRetryAfter_UsesTheDefaultWait()
        {
            using (var h = new TelemetryHarness())
            {
                h.Poster.Then(429).Then(202);
                using (TelemetryClient c = h.EnabledClient())
                {
                    c.Record("manifest", TelemetryOutcome.Succeeded);
                    Flush(c);
                    Assert.AreEqual(2, h.Poster.Bodies.Count);
                    Assert.IsTrue(h.Clock.Delays.Contains(TimeSpan.FromSeconds(60)));
                }
            }
        }

        [TestMethod]
        public void OtherClientErrors_AreDroppedAndNeverRetried()
        {
            foreach (int status in new[] { 400, 404, 413, 415, 500, 302 })
            {
                using (var h = new TelemetryHarness())
                {
                    h.Poster.Then(status);
                    using (TelemetryClient c = h.EnabledClient())
                    {
                        c.Record("manifest", TelemetryOutcome.Succeeded);
                        Flush(c);
                        Assert.AreEqual(1, h.Poster.Bodies.Count, "status " + status);
                        Assert.AreEqual(0, c.QueuedCountForTests(), "status " + status);
                        Flush(c);
                        Assert.AreEqual(1, h.Poster.Bodies.Count, "status " + status + " not resent");
                    }
                }
            }
        }

        [TestMethod]
        public void NetworkError_KeepsEventsAndStops()
        {
            using (var h = new TelemetryHarness())
            {
                h.Poster.ThenNetworkError();
                using (TelemetryClient c = h.EnabledClient())
                {
                    c.Record("manifest", TelemetryOutcome.Succeeded);
                    Flush(c);
                    Assert.AreEqual(1, h.Poster.Bodies.Count);
                    Assert.AreEqual(1, c.QueuedCountForTests());
                    Assert.IsTrue(File.Exists(h.QueueFile));
                }
            }
        }

        [TestMethod]
        public void Queue_PersistsAcrossClientInstances()
        {
            using (var h = new TelemetryHarness())
            {
                Guid firstRun;
                h.Poster.ThenNetworkError();
                using (TelemetryClient c1 = h.EnabledClient())
                {
                    firstRun = c1.RunId;
                    c1.Record("manifest", TelemetryOutcome.Succeeded, LauncherError.None, TimeSpan.FromMilliseconds(5));
                    c1.Record("ac-client", TelemetryOutcome.Failed, LauncherError.DiskFull);
                    Flush(c1);
                }

                Assert.IsTrue(File.Exists(h.QueueFile));
                var second = new FakePoster();
                using (TelemetryClient c2 = h.NewClient(second))
                {
                    c2.Configure(true, TelemetryHarness.Manifest());
                    c2.Record("launch", TelemetryOutcome.Started);
                    Flush(c2);

                    Assert.AreNotEqual(firstRun, c2.RunId);
                    Assert.AreEqual(2, second.Bodies.Count, "old run and new run travel in separate requests");
                    Assert.AreEqual(firstRun.ToString("D"), (string)second.Bodies[0]["runId"]);
                    Assert.AreEqual(2, ((JArray)second.Bodies[0]["events"]).Count);
                    Assert.AreEqual((int)LauncherError.DiskFull, (int)second.Bodies[0]["events"][1]["error"]);
                    Assert.AreEqual(5L, (long)second.Bodies[0]["events"][0]["durationMs"]);
                    Assert.AreEqual(c2.RunId.ToString("D"), (string)second.Bodies[1]["runId"]);
                    Assert.AreEqual(0, (int)second.Bodies[1]["events"][0]["seq"], "seq restarts per run");
                    Assert.IsFalse(File.Exists(h.QueueFile));
                }
            }
        }

        [TestMethod]
        public void Cap_DropsTheOldestEvents()
        {
            using (var h = new TelemetryHarness())
            {
                h.Poster.ThenNetworkError();
                using (TelemetryClient c = h.EnabledClient())
                {
                    for (int i = 0; i < 250; i++)
                    {
                        c.RecordComponent("step", TelemetryOutcome.Succeeded);
                    }

                    Assert.AreEqual(200, c.QueuedCountForTests());
                }

                var next = new FakePoster();
                using (TelemetryClient c2 = h.NewClient(next))
                {
                    c2.Configure(true, TelemetryHarness.Manifest());
                    Flush(c2);
                    int[] seqs = next.AllEvents.Select(e => (int)e["seq"]).ToArray();
                    Assert.AreEqual(200, seqs.Length);
                    Assert.AreEqual(50, seqs.First(), "events 0..49 were the oldest and are gone");
                    Assert.AreEqual(249, seqs.Last());
                }
            }
        }

        [TestMethod]
        public void Events_OlderThanSevenDays_AreDropped()
        {
            using (var h = new TelemetryHarness())
            {
                h.Poster.ThenNetworkError();
                using (TelemetryClient c = h.EnabledClient())
                {
                    c.RecordComponent("old-one", TelemetryOutcome.Started);
                    c.RecordComponent("old-two", TelemetryOutcome.Started);
                    Flush(c);
                    Assert.AreEqual(2, c.QueuedCountForTests());
                    h.Clock.Now = h.Clock.Now.AddDays(6);
                    c.RecordComponent("fresh", TelemetryOutcome.Started);
                    h.Clock.Now = h.Clock.Now.AddDays(1).AddMinutes(1);
                }

                var next = new FakePoster();
                using (TelemetryClient c2 = h.NewClient(next))
                {
                    c2.Configure(true, TelemetryHarness.Manifest());
                    Flush(c2);
                    string[] steps = next.AllEvents.Select(e => (string)e["step"]).ToArray();
                    CollectionAssert.AreEqual(new[] { "fresh" }, steps, "7 days and a minute later only the 1-day-old event survives");
                }
            }
        }

        [TestMethod]
        public void SetOptInFalse_DeletesQueue_AndRegeneratesInstallId()
        {
            using (var h = new TelemetryHarness())
            {
                h.Poster.ThenNetworkError();
                using (TelemetryClient c = h.EnabledClient())
                {
                    Guid before = h.Store.InstallId;
                    c.Record("manifest", TelemetryOutcome.Succeeded);
                    Flush(c);
                    Assert.IsTrue(File.Exists(h.QueueFile));

                    c.SetOptIn(false);

                    Assert.IsFalse(File.Exists(h.QueueFile));
                    Assert.AreEqual(0, c.QueuedCountForTests());
                    Assert.IsFalse(c.IsEnabled);
                    Assert.IsFalse(h.Store.Read(s => s.TelemetryOptIn));
                    Assert.AreNotEqual(before, h.Store.InstallId);
                    Assert.AreNotEqual(Guid.Empty, h.Store.InstallId);

                    c.Record("manifest", TelemetryOutcome.Succeeded);
                    Flush(c);
                    c.Dispose();
                    Assert.IsFalse(File.Exists(h.QueueFile), "nothing is written after opting out");
                    Assert.AreEqual(1, h.Poster.Bodies.Count);
                }

                Assert.IsFalse(Directory.GetFiles(h.Paths.DataDir, TelemetryClient.QueueFileName + "*").Any());
            }
        }

        [TestMethod]
        public void SetOptInTrue_PersistsAndEnables()
        {
            using (var h = new TelemetryHarness())
            using (TelemetryClient c = h.NewClient())
            {
                c.Configure(false, TelemetryHarness.Manifest());
                c.SetOptIn(true);
                Assert.IsTrue(c.IsEnabled);
                Assert.IsTrue(h.Store.Read(s => s.TelemetryOptIn));
            }
        }

        [TestMethod]
        public void ConfigureAsync_ReadsTheStoredOptIn()
        {
            using (var h = new TelemetryHarness())
            using (TelemetryClient c = h.NewClient())
            {
                h.Store.Update(s => s.TelemetryOptIn = true);
                c.ConfigureAsync(TelemetryHarness.Manifest()).GetAwaiter().GetResult();
                Assert.IsTrue(c.IsEnabled);
            }
        }

        [TestMethod]
        public void CorruptQueueFile_IsIgnored()
        {
            using (var h = new TelemetryHarness())
            {
                Directory.CreateDirectory(h.Paths.DataDir);
                File.WriteAllText(h.QueueFile, "{ not json");
                using (TelemetryClient c = h.NewClient())
                {
                    c.Configure(true, TelemetryHarness.Manifest());
                    c.Record("manifest", TelemetryOutcome.Succeeded);
                    Flush(c);
                    Assert.AreEqual(1, h.Poster.AllEvents.Count());
                }
            }
        }

        [TestMethod]
        public void TamperedQueueEntries_AreDroppedOnLoad()
        {
            using (var h = new TelemetryHarness())
            {
                string good = new JObject
                {
                    ["seq"] = 0, ["step"] = "manifest", ["outcome"] = "succeeded", ["error"] = 0, ["durationMs"] = null,
                    ["runId"] = Guid.NewGuid().ToString("D"), ["v"] = "1.4.0", ["os"] = 26200, ["at"] = h.Clock.Now.ToString("o", CultureInfo.InvariantCulture),
                }.ToString();
                string bad = new JObject
                {
                    ["seq"] = 0, ["step"] = "Bad Step!", ["outcome"] = "succeeded", ["error"] = 0, ["durationMs"] = null,
                    ["runId"] = Guid.NewGuid().ToString("D"), ["v"] = "1.4.0", ["os"] = 26200, ["at"] = h.Clock.Now.ToString("o", CultureInfo.InvariantCulture),
                }.ToString();
                Directory.CreateDirectory(h.Paths.DataDir);
                File.WriteAllText(h.QueueFile, "{\"version\":1,\"events\":[" + good + "," + bad + "]}");
                using (TelemetryClient c = h.NewClient())
                {
                    c.Configure(true, TelemetryHarness.Manifest());
                    Flush(c);
                    Assert.AreEqual(1, h.Poster.AllEvents.Count());
                }
            }
        }

        [TestMethod]
        public void QueueFile_IsAsciiNoBom_AndHoldsNoPersonalData()
        {
            using (var h = new TelemetryHarness())
            {
                h.Poster.ThenNetworkError();
                using (TelemetryClient c = h.EnabledClient())
                {
                    c.Record("manifest", TelemetryOutcome.Failed, LauncherError.NetworkUnavailable);
                    Flush(c);
                }

                byte[] bytes = File.ReadAllBytes(h.QueueFile);
                Assert.IsFalse(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
                JObject root = JObject.Parse(System.Text.Encoding.UTF8.GetString(bytes));
                var e = (JObject)root["events"][0];
                CollectionAssert.AreEquivalent(new[] { "seq", "step", "outcome", "error", "durationMs", "runId", "v", "os", "at" }, e.Properties().Select(p => p.Name).ToArray());
                Assert.IsFalse(System.Text.Encoding.UTF8.GetString(bytes).Contains(h.Store.InstallId.ToString("D")), "the install id is read from state at send time and is never stored in the queue");
            }
        }
    }

    internal static class TelemetryClientTestExtensions
    {
        public static int QueuedCountForTests(this TelemetryClient c)
        {
            // QueuedCount is internal; the test assembly sees it through InternalsVisibleTo.
            return c.QueuedCount;
        }
    }
}
