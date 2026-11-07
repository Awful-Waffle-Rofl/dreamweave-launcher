using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.State;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests
{
    [TestClass]
    public class StateStoreTests
    {
        [TestMethod]
        public void FirstRun_CreatesStableInstallId_TelemetryOffByDefault()
        {
            using (var dir = new TempDir())
            {
                var paths = new AppPaths(dir.Path);
                var first = StateStore.Open(paths);
                Guid id = first.InstallId;

                Assert.AreNotEqual(Guid.Empty, id);
                Assert.IsTrue(File.Exists(paths.StateFile), "install id must be persisted on first run");
                Assert.AreEqual(id, StateStore.Open(paths).InstallId);
                Assert.IsFalse(first.Read(s => s.TelemetryOptIn));
            }
        }

        [TestMethod]
        public void Update_WritesAtomically_LeavesNoTempFile()
        {
            using (var dir = new TempDir())
            {
                var paths = new AppPaths(dir.Path);
                var store = StateStore.Open(paths);
                for (int i = 1; i <= 5; i++)
                {
                    int n = i;
                    store.Update(s => s.HighestManifestSerial = n);
                }

                Assert.AreEqual(5L, new StateStore(paths.StateFile).HighestManifestSerial);
                string[] leftovers = Directory.GetFiles(paths.DataDir).Select(Path.GetFileName).Where(f => f != "state.json" && f != "state.json.lock").ToArray();
                Assert.AreEqual(0, leftovers.Length, "unexpected files: " + string.Join(", ", leftovers));
            }
        }

        [TestMethod]
        public void UnknownFields_ArePreserved()
        {
            using (var dir = new TempDir())
            {
                var paths = new AppPaths(dir.Path);
                Directory.CreateDirectory(paths.DataDir);
                File.WriteAllText(paths.StateFile, "{ \"highestManifestSerial\": 4, \"installId\": \"11111111-2222-3333-4444-555555555555\", \"fromTheFuture\": { \"a\": [1, 2] } }");

                var store = StateStore.Open(paths);
                Assert.AreEqual(4L, store.HighestManifestSerial);
                store.Update(s => s.AcFolder = @"C:\Turbine\Asheron's Call");

                string text = File.ReadAllText(paths.StateFile);
                StringAssert.Contains(text, "fromTheFuture");
                Assert.AreEqual(new Guid("11111111-2222-3333-4444-555555555555"), StateStore.Open(paths).InstallId);
            }
        }

        [TestMethod]
        public void CorruptFile_IsSetAsideAndStartsFresh()
        {
            using (var dir = new TempDir())
            {
                var paths = new AppPaths(dir.Path);
                Directory.CreateDirectory(paths.DataDir);
                File.WriteAllText(paths.StateFile, "{ this is not json");

                var store = StateStore.Open(paths);

                Assert.AreEqual(0L, store.HighestManifestSerial);
                Assert.AreNotEqual(Guid.Empty, store.InstallId);
                Assert.AreEqual(1, Directory.GetFiles(paths.DataDir, "state.json.corrupt-*").Length);
            }
        }

        [TestMethod]
        public void FileVerifyCache_RoundTrips_CaseInsensitive()
        {
            using (var dir = new TempDir())
            {
                var paths = new AppPaths(dir.Path);
                var when = new DateTime(2026, 11, 7, 1, 2, 3, DateTimeKind.Utc);
                StateStore.Open(paths).Update(s => s.FileVerifyCache[@"C:\AC\client_portal.dat"] = new FileVerifyEntry { Size = 926941184, LastWriteUtc = when, Sha256 = new string('D', 64) });

                FileVerifyEntry e = StateStore.Open(paths).Read(s => s.FileVerifyCache[@"c:\ac\CLIENT_PORTAL.DAT"]);

                Assert.AreEqual(926941184L, e.Size);
                Assert.AreEqual(when, e.LastWriteUtc);
                Assert.AreEqual(DateTimeKind.Utc, e.LastWriteUtc.Kind);
                Assert.AreEqual(new string('D', 64), e.Sha256);
            }
        }

        [TestMethod]
        public void ParallelWriters_AcrossFourStores_ZeroFailures()
        {
            using (var dir = new TempDir())
            {
                var paths = new AppPaths(dir.Path);
                StateStore[] stores = Enumerable.Range(0, 4).Select(_ => StateStore.Open(paths)).ToArray();
                const int writers = 128;
                int failures = 0;

                Parallel.For(0, writers, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i =>
                {
                    try
                    {
                        stores[i % stores.Length].Update(s => s.FileVerifyCache["file" + i] = new FileVerifyEntry { Size = i, Sha256 = new string('A', 64) });
                    }
                    catch (Exception)
                    {
                        System.Threading.Interlocked.Increment(ref failures);
                    }
                });

                Assert.AreEqual(0, failures);
                int entries = StateStore.Open(paths).Read(s => s.FileVerifyCache.Count);
                Assert.AreEqual(writers, entries, "every writer's entry must survive (read-modify-write under the lock file)");
                Assert.AreEqual(0, Directory.GetFiles(paths.DataDir, "*.tmp").Length, "no temp files left behind");
            }
        }

        [TestMethod]
        public void TwoStores_RacingSerialUpdates_KeepTheMaximum()
        {
            using (var dir = new TempDir())
            {
                var paths = new AppPaths(dir.Path);
                var a = StateStore.Open(paths);
                var b = StateStore.Open(paths);

                Parallel.Invoke(
                    () => { for (int i = 1; i <= 10; i++) { int n = i; a.Update(s => s.HighestManifestSerial = n); } },
                    () => { for (int i = 1; i <= 10; i++) { int n = i; b.Update(s => s.HighestManifestSerial = n); } });

                Assert.IsTrue(StateStore.Open(paths).HighestManifestSerial >= 10);

                // Monotonic: a stale store cannot lower it either.
                var stale = StateStore.Open(paths);
                stale.Update(s => s.HighestManifestSerial = 2);
                Assert.AreEqual(10L, StateStore.Open(paths).HighestManifestSerial);
            }
        }

        [TestMethod]
        public void ThrowingMutator_LeavesStateAndFileUnchanged()
        {
            using (var dir = new TempDir())
            {
                var paths = new AppPaths(dir.Path);
                var store = StateStore.Open(paths);
                store.Update(s => s.AcFolder = "before");
                string fileBefore = File.ReadAllText(paths.StateFile);

                Assert.ThrowsExactly<InvalidOperationException>(() => store.Update(s =>
                {
                    s.AcFolder = "half-done";
                    throw new InvalidOperationException("mutator failed");
                }));

                Assert.AreEqual("before", store.Read(s => s.AcFolder));
                Assert.AreEqual(fileBefore, File.ReadAllText(paths.StateFile));
            }
        }

        [TestMethod]
        public void AtomicFile_UsesUniqueTempNames()
        {
            using (var dir = new TempDir())
            {
                string target = Path.Combine(dir.Path, "x.json");
                Parallel.For(0, 32, i => AtomicFile.WriteAllBytes(Path.Combine(dir.Path, "f" + i + ".json"), new byte[] { (byte)i }));
                AtomicFile.WriteAllBytes(target, new byte[] { 1 });
                AtomicFile.WriteAllBytes(target, new byte[] { 2 });

                CollectionAssert.AreEqual(new byte[] { 2 }, File.ReadAllBytes(target));
                Assert.AreEqual(0, Directory.GetFiles(dir.Path, "*.tmp").Length);
                Assert.AreEqual(33, Directory.GetFiles(dir.Path, "*.json").Length);
            }
        }

        [TestMethod]
        public void HeldLockFile_TimesOutAsStateLocked()
        {
            using (var dir = new TempDir())
            {
                var paths = new AppPaths(dir.Path);
                StateStore.Open(paths);
                using (new FileStream(paths.StateFile + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    var ex = Assert.ThrowsExactly<LauncherException>(() => new StateStore(paths.StateFile, TimeSpan.FromMilliseconds(200)));

                    Assert.AreEqual(LauncherError.StateLocked, ex.Code);
                    Assert.IsTrue(clock.Elapsed < TimeSpan.FromSeconds(10), "the injected timeout must be honoured: " + clock.Elapsed);
                }

                Assert.IsNotNull(new StateStore(paths.StateFile, TimeSpan.FromMilliseconds(200)), "control: succeeds once the lock is released");
            }
        }

        [TestMethod]
        [DataRow(unchecked((int)0x80070020), true)]
        [DataRow(unchecked((int)0x80070021), true)]
        [DataRow(unchecked((int)0x80070070), false)]
        [DataRow(unchecked((int)0x80070005), false)]
        [DataRow(unchecked((int)0x800700CE), false)]
        public void OnlySharingAndLockViolations_AreRetried(int hresult, bool retried)
        {
            Assert.AreEqual(retried, StateStore.IsSharingOrLockViolation(new IOException("x", hresult)));
        }

        [TestMethod]
        public void StateLocked_HasAppendedStableCode()
        {
            Assert.AreEqual(42, (int)LauncherError.StateLocked);
        }

        [TestMethod]
        public void AppPaths_DeriveFromRoot()
        {
            using (var dir = new TempDir())
            {
                var p = new AppPaths(dir.Path);
                Assert.AreEqual(Path.Combine(dir.Path, "Programs", "DreamWeave Launcher"), p.InstallDir);
                Assert.AreEqual(Path.Combine(dir.Path, "DreamWeave"), p.DataDir);
                Assert.AreEqual(Path.Combine(p.DataDir, "staging"), p.StagingDir);
                Assert.AreEqual(Path.Combine(p.DataDir, "state.json"), p.StateFile);
                Assert.AreEqual(Path.Combine(p.DataDir, "manifest.cache.json"), p.ManifestCacheFile);
            }
        }
    }
}
