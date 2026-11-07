using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using DreamWeave.Launcher.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests
{
    [TestClass]
    public class HasherTests
    {
        // FIPS 180-2 test vectors.
        private const string AbcSha256 = "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD";
        private const string EmptySha256 = "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855";

        [TestMethod]
        public void ComputeSha256Hex_MatchesKnownVectors()
        {
            Assert.AreEqual(AbcSha256, Hasher.ComputeSha256Hex(Encoding.ASCII.GetBytes("abc")));
            Assert.AreEqual(EmptySha256, Hasher.ComputeSha256Hex(new byte[0]));
        }

        [TestMethod]
        public void ComputeFileSha256Hex_MatchesVectorAndReportsFinalProgress()
        {
            using (var dir = new TempDir())
            {
                string path = Path.Combine(dir.Path, "abc.bin");
                File.WriteAllBytes(path, Encoding.ASCII.GetBytes("abc"));
                var reports = new List<ProgressReport>();
                var progress = new SyncProgress(reports.Add);

                string hex = Hasher.ComputeFileSha256Hex(path, progress, CancellationToken.None, "test-step");

                Assert.AreEqual(AbcSha256, hex);
                Assert.IsTrue(reports.Count >= 1);
                ProgressReport last = reports[reports.Count - 1];
                Assert.AreEqual("test-step", last.StepId);
                Assert.AreEqual(3L, last.BytesDone);
                Assert.AreEqual(3L, last.BytesTotal);
            }
        }

        [TestMethod]
        public void ComputeFileSha256HexAsync_MatchesSync()
        {
            using (var dir = new TempDir())
            {
                string path = Path.Combine(dir.Path, "data.bin");
                var data = new byte[3 * 1024 * 1024 + 17];
                new Random(1).NextBytes(data);
                File.WriteAllBytes(path, data);

                string sync = Hasher.ComputeFileSha256Hex(path);
                string async = Hasher.ComputeFileSha256HexAsync(path).GetAwaiter().GetResult();

                Assert.AreEqual(sync, async);
                Assert.AreEqual(Hasher.ComputeSha256Hex(data), sync);
            }
        }

        [TestMethod]
        public void ComputeSha256Hex_HonorsCancellation()
        {
            using (var cts = new CancellationTokenSource())
            using (var ms = new MemoryStream(new byte[10]))
            {
                cts.Cancel();
                Assert.ThrowsExactly<OperationCanceledException>(() => Hasher.ComputeSha256Hex(ms, null, null, cts.Token));
            }
        }

        [TestMethod]
        public void FixedTimeEquals_IsCaseInsensitiveAndLengthStrict()
        {
            Assert.IsTrue(Hasher.FixedTimeEquals(AbcSha256, AbcSha256.ToLowerInvariant()));
            Assert.IsFalse(Hasher.FixedTimeEquals(AbcSha256, EmptySha256));
            Assert.IsFalse(Hasher.FixedTimeEquals(AbcSha256, AbcSha256.Substring(1)));
            Assert.IsFalse(Hasher.FixedTimeEquals(null, AbcSha256));
        }

        [TestMethod]
        public void IsSha256Hex_ChecksLengthAndAlphabet()
        {
            Assert.IsTrue(Hasher.IsSha256Hex(AbcSha256));
            Assert.IsFalse(Hasher.IsSha256Hex(AbcSha256.Substring(1)));
            Assert.IsFalse(Hasher.IsSha256Hex(AbcSha256.Substring(1) + "G"));
            Assert.IsFalse(Hasher.IsSha256Hex(null));
        }

        /// <summary>Invokes the callback synchronously (Progress&lt;T&gt; posts to a sync context, which tests do not want).</summary>
        private sealed class SyncProgress : IProgress<ProgressReport>
        {
            private readonly Action<ProgressReport> _callback;

            public SyncProgress(Action<ProgressReport> callback)
            {
                _callback = callback;
            }

            public void Report(ProgressReport value) => _callback(value);
        }
    }
}
