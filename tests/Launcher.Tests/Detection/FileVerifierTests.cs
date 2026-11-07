using System;
using System.IO;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Detection;
using DreamWeave.Launcher.Core.State;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.Detection
{
    [TestClass]
    public class FileVerifierTests
    {
        private const string P = @"C:\g\a.dat";

        [TestMethod]
        public void First_Check_Hashes_And_Second_Uses_Cache_Without_Opening()
        {
            using (var dir = new TempDir())
            {
                var fs = new FakeFs();
                byte[] data = DetectionData.Bytes("hello dats");
                fs.SetFile(P, data);
                var v = new FileVerifier(fs, DetectionData.NewState(dir));

                FileCheckResult a = v.Verify(P);
                Assert.AreEqual(FileCheckStatus.Hashed, a.Status);
                Assert.AreEqual(DetectionData.Hash(data), a.Sha256);
                Assert.IsFalse(a.FromCache);
                Assert.AreEqual(1, fs.OpenCount);

                FileCheckResult b = v.Verify(P);
                Assert.IsTrue(b.FromCache);
                Assert.AreEqual(a.Sha256, b.Sha256);
                Assert.AreEqual(1, fs.OpenCount, "a cache hit must not read the file");
            }
        }

        [TestMethod]
        public void Cache_Survives_Reopening_The_State_File()
        {
            using (var dir = new TempDir())
            {
                var fs = new FakeFs();
                fs.SetFile(P, DetectionData.Bytes("persist me"), new DateTime(2026, 5, 6, 7, 8, 9, 123, DateTimeKind.Utc).AddTicks(4567));
                new FileVerifier(fs, DetectionData.NewState(dir)).Verify(P);
                Assert.AreEqual(1, fs.OpenCount);

                var again = new FileVerifier(fs, DetectionData.NewState(dir)).Verify(P);
                Assert.IsTrue(again.FromCache, "mtime must round-trip through state.json at full tick precision");
                Assert.AreEqual(1, fs.OpenCount);
            }
        }

        [TestMethod]
        public void Changed_Mtime_Misses_The_Cache_And_Rehashes()
        {
            using (var dir = new TempDir())
            {
                var fs = new FakeFs();
                fs.SetFile(P, DetectionData.Bytes("version one"), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                var v = new FileVerifier(fs, DetectionData.NewState(dir));
                string first = v.Verify(P).Sha256;

                byte[] two = DetectionData.Bytes("version two");
                fs.SetFile(P, two, new DateTime(2026, 1, 1, 0, 0, 1, DateTimeKind.Utc));
                FileCheckResult r = v.Verify(P);

                Assert.IsFalse(r.FromCache);
                Assert.AreEqual(2, fs.OpenCount);
                Assert.AreNotEqual(first, r.Sha256);
                Assert.AreEqual(DetectionData.Hash(two), r.Sha256);
            }
        }

        [TestMethod]
        public void Changed_Size_With_Same_Mtime_Misses_The_Cache()
        {
            using (var dir = new TempDir())
            {
                var fs = new FakeFs();
                var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                fs.SetFile(P, DetectionData.Bytes("abc"), t);
                var v = new FileVerifier(fs, DetectionData.NewState(dir));
                v.Verify(P);
                fs.SetFile(P, DetectionData.Bytes("abcd"), t);
                Assert.IsFalse(v.Verify(P).FromCache);
            }
        }

        [TestMethod]
        public void Missing_File_Is_Reported_Not_Thrown()
        {
            using (var dir = new TempDir())
            {
                FileCheckResult r = new FileVerifier(new FakeFs(), DetectionData.NewState(dir)).Verify(P);
                Assert.AreEqual(FileCheckStatus.Missing, r.Status);
                Assert.IsNull(r.Sha256);
            }
        }

        [TestMethod]
        public void File_Deleted_Between_Stat_And_Open_Is_Missing()
        {
            using (var dir = new TempDir())
            {
                var fs = new FakeFs();
                fs.SetFile(P, new byte[] { 1 });
                fs.OpenFailures[P] = new FileNotFoundException("gone");
                Assert.AreEqual(FileCheckStatus.Missing, new FileVerifier(fs, DetectionData.NewState(dir)).Verify(P).Status);
            }
        }

        [TestMethod]
        public void Sharing_Violation_Is_Reported_As_Locked_And_Not_Cached()
        {
            using (var dir = new TempDir())
            {
                var fs = new FakeFs();
                fs.SetFile(P, new byte[] { 1, 2, 3 });
                fs.OpenFailures[P] = new IOException("in use", unchecked((int)0x80070020));
                var v = new FileVerifier(fs, DetectionData.NewState(dir));
                Assert.AreEqual(FileCheckStatus.Locked, v.Verify(P).Status);

                fs.OpenFailures.Clear();
                FileCheckResult ok = v.Verify(P);
                Assert.AreEqual(FileCheckStatus.Hashed, ok.Status);
                Assert.IsFalse(ok.FromCache);
            }
        }

        [TestMethod]
        public void Lock_Violation_Is_Reported_As_Locked()
        {
            using (var dir = new TempDir())
            {
                var fs = new FakeFs();
                fs.SetFile(P, new byte[] { 1 });
                fs.OpenFailures[P] = new IOException("locked", unchecked((int)0x80070021));
                Assert.AreEqual(FileCheckStatus.Locked, new FileVerifier(fs, DetectionData.NewState(dir)).Verify(P).Status);
            }
        }

        [TestMethod]
        public void Access_Denied_Is_Reported()
        {
            using (var dir = new TempDir())
            {
                var fs = new FakeFs();
                fs.SetFile(P, new byte[] { 1 });
                fs.OpenFailures[P] = new UnauthorizedAccessException("no");
                Assert.AreEqual(FileCheckStatus.AccessDenied, new FileVerifier(fs, DetectionData.NewState(dir)).Verify(P).Status);
            }
        }

        [TestMethod]
        public void Other_Io_Failure_Is_Error()
        {
            using (var dir = new TempDir())
            {
                var fs = new FakeFs();
                fs.SetFile(P, new byte[] { 1 });
                fs.OpenFailures[P] = new IOException("disk on fire", unchecked((int)0x8007001F));
                Assert.AreEqual(FileCheckStatus.Error, new FileVerifier(fs, DetectionData.NewState(dir)).Verify(P).Status);
            }
        }

        [TestMethod]
        public void Expected_Size_Mismatch_Skips_Hashing()
        {
            using (var dir = new TempDir())
            {
                var fs = new FakeFs();
                fs.SetFile(P, new byte[10]);
                FileCheckResult r = new FileVerifier(fs, DetectionData.NewState(dir)).Verify(P, 11);
                Assert.AreEqual(FileCheckStatus.SizeMismatch, r.Status);
                Assert.AreEqual(10L, r.Size);
                Assert.AreEqual(0, fs.OpenCount);
            }
        }

        [TestMethod]
        public void Progress_Is_Reported_While_Hashing()
        {
            using (var dir = new TempDir())
            {
                var fs = new FakeFs();
                fs.SetFile(P, new byte[3 * 1024 * 1024]);
                var seen = new System.Collections.Generic.List<ProgressReport>();
                new FileVerifier(fs, DetectionData.NewState(dir)).Verify(P, null, new SyncProgress(seen.Add));
                Assert.IsTrue(seen.Count >= 1);
                Assert.AreEqual(3L * 1024 * 1024, seen[seen.Count - 1].BytesDone);
            }
        }

        [TestMethod]
        public void File_Changed_During_The_Read_Is_Not_Cached()
        {
            using (var dir = new TempDir())
            {
                var fs = new FakeFs();
                byte[] data = DetectionData.Bytes("being written");
                fs.SetFile(P, data, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                bool first = true;
                fs.OnOpen = path =>
                {
                    if (first)
                    {
                        first = false;
                        fs.SetFile(P, data, new DateTime(2026, 1, 1, 0, 0, 9, DateTimeKind.Utc));
                    }
                };
                var v = new FileVerifier(fs, DetectionData.NewState(dir));

                Assert.AreEqual(FileCheckStatus.Hashed, v.Verify(P).Status);
                FileCheckResult second = v.Verify(P);
                Assert.IsFalse(second.FromCache, "the first hash was taken while the file changed, so it must not have been cached");
                Assert.AreEqual(2, fs.OpenCount);
                Assert.IsTrue(v.Verify(P).FromCache, "a stable file is cached");
            }
        }

        [TestMethod]
        public void Cancellation_Propagates_And_Caches_Nothing()
        {
            using (var dir = new TempDir())
            {
                var fs = new FakeFs();
                fs.SetFile(P, new byte[3 * 1024 * 1024]);
                var v = new FileVerifier(fs, DetectionData.NewState(dir));
                using (var cts = new System.Threading.CancellationTokenSource())
                {
                    cts.Cancel();
                    Assert.ThrowsExactly<OperationCanceledException>(() => v.Verify(P, null, null, cts.Token));
                }

                Assert.IsFalse(v.Verify(P).FromCache);
            }
        }
        [TestMethod]
        public void Real_Disk_Locked_File_Reports_Locked()
        {
            using (var dir = new TempDir())
            {
                string path = Path.Combine(dir.Path, "locked.dat");
                File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
                var v = new FileVerifier(new RealAcFileSystem(), DetectionData.NewState(dir));

                using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    Assert.AreEqual(FileCheckStatus.Locked, v.Verify(path).Status);
                }

                Assert.AreEqual(FileCheckStatus.Hashed, v.Verify(path).Status);
            }
        }

        [TestMethod]
        public void Real_Disk_File_Open_For_Write_By_Another_Process_Still_Verifies()
        {
            using (var dir = new TempDir())
            {
                string path = Path.Combine(dir.Path, "playing.dat");
                File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
                var v = new FileVerifier(new RealAcFileSystem(), DetectionData.NewState(dir));

                using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
                {
                    FileCheckResult r = v.Verify(path);
                    Assert.AreEqual(FileCheckStatus.Hashed, r.Status);
                    Assert.AreEqual(DetectionData.Hash(new byte[] { 1, 2, 3 }), r.Sha256);
                }
            }
        }

        [TestMethod]
        public void Real_Disk_Missing_And_Cache_Roundtrip()
        {
            using (var dir = new TempDir())
            {
                string path = Path.Combine(dir.Path, "x.dat");
                var v = new FileVerifier(new RealAcFileSystem(), DetectionData.NewState(dir));
                Assert.AreEqual(FileCheckStatus.Missing, v.Verify(path).Status);

                File.WriteAllBytes(path, new byte[] { 5, 5, 5 });
                Assert.IsFalse(v.Verify(path).FromCache);
                Assert.IsTrue(new FileVerifier(new RealAcFileSystem(), DetectionData.NewState(dir)).Verify(path).FromCache);

                File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(5));
                Assert.IsFalse(v.Verify(path).FromCache);
            }
        }

        private sealed class SyncProgress : IProgress<ProgressReport>
        {
            private readonly Action<ProgressReport> _sink;

            public SyncProgress(Action<ProgressReport> sink)
            {
                _sink = sink;
            }

            public void Report(ProgressReport value)
            {
                _sink(value);
            }
        }
    }
}
