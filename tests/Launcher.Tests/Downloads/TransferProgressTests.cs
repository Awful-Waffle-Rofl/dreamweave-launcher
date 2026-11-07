using System;
using System.Collections.Generic;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Downloads;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.Downloads
{
    [TestClass]
    public class TransferProgressTests
    {
        private sealed class Clock
        {
            public TimeSpan Now { get; set; }

            public TimeSpan Read() => Now;
        }

        [TestMethod]
        public void Reports_AreThrottledToTenPerSecond_PlusFinal()
        {
            var clock = new Clock();
            var sink = new ListProgress();
            var tp = new TransferProgress("s", 1000000, sink, clock.Read);
            for (int ms = 0; ms < 1000; ms += 10)
            {
                clock.Now = TimeSpan.FromMilliseconds(ms);
                tp.Report(ms * 100);
            }

            int throttled = sink.Reports.Count;
            Assert.IsTrue(throttled >= 9 && throttled <= 11, "reports in 1 s: " + throttled);
            tp.Complete();
            Assert.AreEqual(throttled + 1, sink.Reports.Count);
            Assert.AreEqual(99000, sink.Reports[sink.Reports.Count - 1].BytesDone);
        }

        [TestMethod]
        public void Rate_IsSmoothed_AndEtaFollowsIt()
        {
            var clock = new Clock();
            var sink = new ListProgress();
            var tp = new TransferProgress("s", 10000, sink, clock.Read);
            tp.Report(0);
            Assert.IsNull(tp.BytesPerSecond, "no rate before the first sample interval");

            clock.Now = TimeSpan.FromMilliseconds(500);
            tp.Report(500); // 1000 B/s
            Assert.AreEqual(1000, tp.BytesPerSecond.Value, 1e-6);

            clock.Now = TimeSpan.FromMilliseconds(1000);
            tp.Report(1500); // 2000 B/s instantaneous -> 0.3*2000 + 0.7*1000 = 1300
            Assert.AreEqual(1300, tp.BytesPerSecond.Value, 1e-6);

            ProgressReport last = sink.Reports[sink.Reports.Count - 1];
            Assert.AreEqual(1500, last.BytesDone);
            Assert.AreEqual(10000, last.BytesTotal);
            Assert.AreEqual(1300, last.BytesPerSecond.Value, 1e-6);
            Assert.AreEqual((10000 - 1500) / 1300.0, last.Eta.Value.TotalSeconds, 0.01);
            Assert.AreEqual(TransferProgress.DownloadingMessage, last.Message);
        }

        [TestMethod]
        public void SamplesCloserThanTheInterval_DoNotChangeTheRate()
        {
            var clock = new Clock();
            var tp = new TransferProgress("s", 10000, null, clock.Read);
            tp.Report(0);
            clock.Now = TimeSpan.FromMilliseconds(500);
            tp.Report(500);
            clock.Now = TimeSpan.FromMilliseconds(600);
            tp.Report(5000);
            Assert.AreEqual(1000, tp.BytesPerSecond.Value, 1e-6);
        }

        [TestMethod]
        public void RestartFromZero_IsReported_AndDoesNotProduceNegativeRate()
        {
            var clock = new Clock();
            var sink = new ListProgress();
            var tp = new TransferProgress("s", 10000, sink, clock.Read);
            tp.Report(0);
            clock.Now = TimeSpan.FromMilliseconds(500);
            tp.Report(5000);
            clock.Now = TimeSpan.FromMilliseconds(700);
            tp.Report(0);
            clock.Now = TimeSpan.FromMilliseconds(1200);
            tp.Report(1000);
            Assert.IsTrue(tp.BytesPerSecond.Value > 0);
            tp.Complete();
            Assert.AreEqual(1000, sink.Reports[sink.Reports.Count - 1].BytesDone);
        }
    }
}
