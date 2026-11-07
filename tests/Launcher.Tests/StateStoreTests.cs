using System;
using System.IO;
using System.Linq;
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
                string[] leftovers = Directory.GetFiles(paths.DataDir).Select(Path.GetFileName).Where(f => f != "state.json").ToArray();
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
        public void AppPaths_DeriveFromRoot()
        {
            using (var dir = new TempDir())
            {
                var p = new AppPaths(dir.Path);
                Assert.AreEqual(Path.Combine(dir.Path, "Programs", "DreamWeave Launcher"), p.InstallDir);
                Assert.AreEqual(Path.Combine(dir.Path, "DreamWeave"), p.DataDir);
                Assert.AreEqual(Path.Combine(p.DataDir, "staging"), p.StagingDir);
                Assert.AreEqual(Path.Combine(p.DataDir, "state.json"), p.StateFile);
                Assert.AreEqual(Path.Combine(p.DataDir, "manifest.json"), p.ManifestCacheFile);
                Assert.AreEqual(p.ManifestCacheFile + ".sig", p.ManifestSigCacheFile);
            }
        }
    }
}
