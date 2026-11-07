using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Install;
using DreamWeave.Launcher.Core.Manifests;
using DreamWeave.Launcher.Core.State;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.Install
{
    [TestClass]
    public class ElevatedTaskRunnerTests
    {
        private const string Url = "https://primary.example.test/manifest.json";
        private static readonly byte[] A = InstallData.Bytes("elevated first file");
        private static readonly byte[] B = InstallData.Bytes("elevated second file, longer");
        private static readonly byte[] Client = InstallData.Bytes("client bytes: 0123456789");
        private static readonly byte[] ClientPatched = InstallData.Bytes("client bytes: 01234XX789");

        private static SigningKey _key;
        private static SigningKey _otherKey;

        [ClassInitialize]
        public static void Init(TestContext context)
        {
            _key = SigningKey.Generate();
            _otherKey = SigningKey.Generate();
        }

        private static string FileJson(string path, byte[] content)
        {
            return "{ \"path\": \"" + path + "\", \"size\": " + content.Length + ", \"sha256\": \"" + InstallData.Hash(content) + "\" }";
        }

        private static byte[] ManifestBytes()
        {
            string json =
                "{\n" +
                "  \"schema\": 1,\n  \"serial\": 5,\n  \"issued\": \"2026-11-07T00:00:00Z\",\n" +
                "  \"server\": { \"name\": \"DreamWeave\", \"host\": \"play.example.test\", \"port\": 9000 },\n" +
                "  \"components\": [\n" +
                "    { \"id\": \"eor-dats\", \"kind\": \"zip-overlay\", \"required\": true, \"url\": \"https://example.test/x.zip\", \"size\": 10, \"sha256\": \"" + new string('A', 64) + "\",\n" +
                "      \"files\": [ " + FileJson("a.dat", A) + ", " + FileJson("b.dat", B) + " ] },\n" +
                "    { \"id\": \"fix\", \"kind\": \"byte-patch\", \"target\": \"acclient.exe\", \"inputSha256\": \"" + InstallData.Hash(Client) + "\", \"outputSha256\": \"" + InstallData.Hash(ClientPatched) + "\",\n" +
                "      \"patches\": [ { \"offset\": 19, \"expect\": \"" + Hasher.ToHex(Encoding.ASCII.GetBytes("56")) + "\", \"replace\": \"5858\" } ] }\n" +
                "  ]\n}\n";
            return new UTF8Encoding(false).GetBytes(json);
        }

        private sealed class Fixture : System.IDisposable
        {
            private readonly TempDir _dir = new TempDir();

            public Fixture()
            {
                Paths = new AppPaths(Path.Combine(_dir.Path, "app"));
                Ac = Path.Combine(_dir.Path, "ac");
                Directory.CreateDirectory(Ac);
                File.WriteAllBytes(Path.Combine(Ac, "acclient.exe"), Client);
                Directory.CreateDirectory(Paths.StagingDir);

                // Populate the signed cache exactly as the unelevated launcher would.
                byte[] manifest = ManifestBytes();
                var fetcher = new FakeHttpFetcher();
                fetcher.Serve(Url, manifest);
                fetcher.Serve(Url + ".sig", _key.Sign(manifest).ToFileBytes());
                ManifestLoadResult r = new ManifestProvider(fetcher, new ManifestVerifier(new[] { _key.PublicKey }), Paths, StateStore.Open(Paths), new[] { ManifestSource.FromManifestUrl("primary", Url) })
                    .LoadAsync(CancellationToken.None).GetAwaiter().GetResult();
                Assert.AreEqual(LauncherError.None, r.Status, "fixture: cache population failed");
            }

            public AppPaths Paths { get; }

            public string Ac { get; }

            public string Target(string rel) => Path.Combine(Ac, rel);

            public string StageFile(string name, byte[] content)
            {
                string p = Path.Combine(Paths.StagingDir, name);
                File.WriteAllBytes(p, content);
                return p;
            }

            public string OverlayTask(params ElevatedTaskFile[] files)
            {
                var task = new ElevatedTask { AppRoot = Paths.Root, AcFolder = Ac };
                task.Items.Add(new ElevatedTaskItem { ComponentId = "eor-dats", Operation = ElevatedOperations.Overlay, Files = new List<ElevatedTaskFile>(files) });
                return task.Save(Paths);
            }

            public ElevatedTaskResult Run(string taskPath, SigningKey trusted = null)
            {
                return new ElevatedTaskRunner(new ManifestVerifier(new[] { (trusted ?? _key).PublicKey })).Execute(taskPath);
            }

            public void Dispose() => _dir.Dispose();
        }

        [TestMethod]
        public void VerifiedStagedFiles_AreWritten()
        {
            using (var f = new Fixture())
            {
                string task = f.OverlayTask(
                    new ElevatedTaskFile { Path = "a.dat", Staged = f.StageFile("0.part", A) },
                    new ElevatedTaskFile { Path = "b.dat", Staged = f.StageFile("1.part", B) });

                ElevatedTaskResult r = f.Run(task);

                Assert.AreEqual(LauncherError.None, r.Code, r.Detail);
                CollectionAssert.AreEqual(A, File.ReadAllBytes(f.Target("a.dat")));
                CollectionAssert.AreEqual(B, File.ReadAllBytes(f.Target("b.dat")));
                Assert.IsFalse(InstallData.AnyTempLeft(f.Ac));
            }
        }

        [TestMethod]
        public void TamperedStagedFile_IsRejected_NothingWritten()
        {
            using (var f = new Fixture())
            {
                byte[] badB = (byte[])B.Clone();
                badB[0] ^= 0x20;
                string task = f.OverlayTask(
                    new ElevatedTaskFile { Path = "a.dat", Staged = f.StageFile("0.part", A) },
                    new ElevatedTaskFile { Path = "b.dat", Staged = f.StageFile("1.part", badB) });

                ElevatedTaskResult r = f.Run(task);

                Assert.AreEqual(LauncherError.HashMismatch, r.Code, r.Detail);
                Assert.IsFalse(File.Exists(f.Target("a.dat")), "no file may be written when any staged file fails");
                Assert.IsFalse(File.Exists(f.Target("b.dat")));
            }
        }

        [TestMethod]
        public void InvalidManifestSignature_IsRejected_NothingWritten()
        {
            using (var f = new Fixture())
            {
                string task = f.OverlayTask(new ElevatedTaskFile { Path = "a.dat", Staged = f.StageFile("0.part", A) });

                ElevatedTaskResult r = f.Run(task, trusted: _otherKey);

                Assert.AreEqual(LauncherError.ManifestSignatureInvalid, r.Code, r.Detail);
                Assert.IsFalse(File.Exists(f.Target("a.dat")));
            }
        }

        [TestMethod]
        public void TamperedCache_IsRejected()
        {
            using (var f = new Fixture())
            {
                string cache = f.Paths.ManifestCacheFile;
                string text = File.ReadAllText(cache);
                int at = text.IndexOf("\"manifestB64\":\"", System.StringComparison.Ordinal) + 20;
                char c = text[at];
                File.WriteAllText(cache, text.Substring(0, at) + (c == 'A' ? 'B' : 'A') + text.Substring(at + 1));
                string task = f.OverlayTask(new ElevatedTaskFile { Path = "a.dat", Staged = f.StageFile("0.part", A) });

                ElevatedTaskResult r = f.Run(task);

                Assert.AreEqual(LauncherError.ManifestSignatureInvalid, r.Code, r.Detail);
                Assert.IsFalse(File.Exists(f.Target("a.dat")));
            }
        }

        [TestMethod]
        public void FileNotInComponent_IsTaskInvalid()
        {
            using (var f = new Fixture())
            {
                string task = f.OverlayTask(new ElevatedTaskFile { Path = "evil.dll", Staged = f.StageFile("0.part", A) });

                Assert.AreEqual(LauncherError.ElevatedTaskInvalid, f.Run(task).Code);
                Assert.IsFalse(File.Exists(f.Target("evil.dll")));
            }
        }

        [TestMethod]
        public void UnknownComponent_IsTaskInvalid()
        {
            using (var f = new Fixture())
            {
                var t = new ElevatedTask { AppRoot = f.Paths.Root, AcFolder = f.Ac };
                t.Items.Add(new ElevatedTaskItem { ComponentId = "nope", Operation = ElevatedOperations.Overlay });

                Assert.AreEqual(LauncherError.ElevatedTaskInvalid, f.Run(t.Save(f.Paths)).Code);
            }
        }

        [TestMethod]
        public void FolderWithoutClient_IsTaskInvalid()
        {
            using (var f = new Fixture())
            {
                string elsewhere = Path.Combine(f.Paths.Root, "not-ac");
                Directory.CreateDirectory(elsewhere);
                var t = new ElevatedTask { AppRoot = f.Paths.Root, AcFolder = elsewhere };
                t.Items.Add(new ElevatedTaskItem { ComponentId = "eor-dats", Operation = ElevatedOperations.Overlay, Files = { new ElevatedTaskFile { Path = "a.dat", Staged = f.StageFile("0.part", A) } } });

                Assert.AreEqual(LauncherError.ElevatedTaskInvalid, f.Run(t.Save(f.Paths)).Code);
                Assert.IsFalse(File.Exists(Path.Combine(elsewhere, "a.dat")));
            }
        }

        [TestMethod]
        public void PatchApply_Task_PatchesTheClient()
        {
            using (var f = new Fixture())
            {
                var t = new ElevatedTask { AppRoot = f.Paths.Root, AcFolder = f.Ac };
                t.Items.Add(new ElevatedTaskItem { ComponentId = "fix", Operation = ElevatedOperations.PatchApply });

                ElevatedTaskResult r = f.Run(t.Save(f.Paths));

                Assert.AreEqual(LauncherError.None, r.Code, r.Detail);
                CollectionAssert.AreEqual(ClientPatched, File.ReadAllBytes(f.Target("acclient.exe")));
            }
        }

        [TestMethod]
        public void AppRootThroughJunction_WritesNothing_IsManifestUnavailable()
        {
            using (var f = new Fixture())
            {
                // Reviewer probe e1: the task's appRoot\DreamWeave is a junction to another app's folder.
                string attackerRoot = Path.Combine(f.Paths.Root, "..", "attackerroot");
                string victim = Path.Combine(f.Paths.Root, "..", "protected");
                Directory.CreateDirectory(attackerRoot);
                Directory.CreateDirectory(victim);
                byte[] foreign = InstallData.Bytes("this is another app's state.json (not JSON)");
                File.WriteAllBytes(Path.Combine(victim, "state.json"), foreign);
                string reason;
                if (!InstallData.TryMakeJunction(Path.Combine(Path.GetFullPath(attackerRoot), "DreamWeave"), Path.GetFullPath(victim), out reason))
                {
                    Assert.Inconclusive(reason);
                }

                var t = new ElevatedTask { AppRoot = Path.GetFullPath(attackerRoot), AcFolder = f.Ac };
                t.Items.Add(new ElevatedTaskItem { ComponentId = "eor-dats", Operation = ElevatedOperations.Overlay, Files = { new ElevatedTaskFile { Path = "a.dat", Staged = f.StageFile("0.part", A) } } });

                ElevatedTaskResult r = f.Run(t.Save(f.Paths));

                Assert.AreEqual(LauncherError.ManifestUnavailable, r.Code, r.Detail);
                CollectionAssert.AreEqual(foreign, File.ReadAllBytes(Path.Combine(victim, "state.json")), "the foreign file must be byte-identical");
                CollectionAssert.AreEqual(new[] { "state.json" }, Directory.GetFileSystemEntries(victim).Select(Path.GetFileName).ToArray(), "no .lock, .corrupt-* or anything else may appear");
                Assert.IsFalse(File.Exists(f.Target("a.dat")));
            }
        }

        [TestMethod]
        public void AppRootWithoutCache_WritesNothingThere()
        {
            using (var f = new Fixture())
            {
                string empty = Path.Combine(f.Paths.Root, "..", "emptyroot");
                Directory.CreateDirectory(empty);
                var t = new ElevatedTask { AppRoot = Path.GetFullPath(empty), AcFolder = f.Ac };
                t.Items.Add(new ElevatedTaskItem { ComponentId = "eor-dats", Operation = ElevatedOperations.Overlay, Files = { new ElevatedTaskFile { Path = "a.dat", Staged = f.StageFile("0.part", A) } } });

                ElevatedTaskResult r = f.Run(t.Save(f.Paths));

                Assert.AreEqual(LauncherError.ManifestUnavailable, r.Code, r.Detail);
                Assert.AreEqual(0, Directory.GetFileSystemEntries(empty).Length, "no state file, lock file or folder may be created");
            }
        }

        [TestMethod]
        [DataRow(@"\\server\share\0.part")]
        [DataRow(@"\\?\C:\x\0.part")]
        [DataRow(@"\\.\C:\x\0.part")]
        [DataRow(@"relative\0.part")]
        public void StagedPathNotLocal_IsTaskInvalid(string staged)
        {
            using (var f = new Fixture())
            {
                string task = f.OverlayTask(new ElevatedTaskFile { Path = "a.dat", Staged = staged });
                Assert.AreEqual(LauncherError.ElevatedTaskInvalid, f.Run(task).Code);
                Assert.IsFalse(File.Exists(f.Target("a.dat")));
            }
        }

        [TestMethod]
        public void StagedFileBehindJunction_IsRefused()
        {
            using (var f = new Fixture())
            {
                string real = Path.Combine(f.Paths.Root, "..", "realstage");
                string link = Path.Combine(f.Paths.StagingDir, "link");
                string reason;
                if (!InstallData.TryMakeJunction(link, Path.GetFullPath(real), out reason))
                {
                    Assert.Inconclusive(reason);
                }

                File.WriteAllBytes(Path.Combine(real, "0.part"), A);
                string task = f.OverlayTask(new ElevatedTaskFile { Path = "a.dat", Staged = Path.Combine(link, "0.part") });

                Assert.AreEqual(LauncherError.ReparsePointInPath, f.Run(task).Code);
                Assert.IsFalse(File.Exists(f.Target("a.dat")));
            }
        }

        [TestMethod]
        [DataRow(@"\\server\share\ac")]
        [DataRow(@"\\?\C:\Turbine\Asheron's Call")]
        [DataRow(@"\\.\C:\ac")]
        public void AcFolderNotLocal_IsTaskInvalid(string acFolder)
        {
            using (var f = new Fixture())
            {
                var t = new ElevatedTask { AppRoot = f.Paths.Root, AcFolder = acFolder };
                t.Items.Add(new ElevatedTaskItem { ComponentId = "eor-dats", Operation = ElevatedOperations.Overlay, Files = { new ElevatedTaskFile { Path = "a.dat", Staged = f.StageFile("0.part", A) } } });
                Assert.AreEqual(LauncherError.ElevatedTaskInvalid, f.Run(t.Save(f.Paths)).Code);
            }
        }

        [TestMethod]
        public void GarbageTaskFile_IsTaskInvalid()
        {
            using (var f = new Fixture())
            {
                string p = f.StageFile("task.json", InstallData.Bytes("{ not json"));
                Assert.AreEqual(LauncherError.ElevatedTaskInvalid, f.Run(p).Code);
            }
        }

        [TestMethod]
        public void AppRootThroughJunction_ErrorNamesTheJunctionFolder()
        {
            using (var f = new Fixture())
            {
                string attackerRoot = Path.GetFullPath(Path.Combine(f.Paths.Root, "..", "attackerroot2"));
                string victim = Path.GetFullPath(Path.Combine(f.Paths.Root, "..", "protected2"));
                Directory.CreateDirectory(attackerRoot);
                Directory.CreateDirectory(victim);
                string link = Path.Combine(attackerRoot, "DreamWeave");
                string reason;
                if (!InstallData.TryMakeJunction(link, victim, out reason))
                {
                    Assert.Inconclusive(reason);
                }

                var t = new ElevatedTask { AppRoot = attackerRoot, AcFolder = f.Ac };
                t.Items.Add(new ElevatedTaskItem { ComponentId = "eor-dats", Operation = ElevatedOperations.Overlay, Files = { new ElevatedTaskFile { Path = "a.dat", Staged = f.StageFile("0.part", A) } } });

                ElevatedTaskResult r = f.Run(t.Save(f.Paths));

                Assert.AreEqual(LauncherError.ManifestUnavailable, r.Code, r.Detail);
                StringAssert.Contains(r.Detail, "'" + link + "'", "the detail must name the junction folder itself");
                StringAssert.Contains(r.Detail, "junction");
            }
        }

        [TestMethod]
        public void OverlayOntoReadOnlyRunningExe_IsGameRunning_AttributeKept_NoOrphans()
        {
            using (var f = new Fixture())
            {
                // A real running image living at the manifest path a.dat.
                using (var running = new RunningExe(f.Target("a.dat")))
                {
                    byte[] before = File.ReadAllBytes(running.Path);
                    File.SetAttributes(running.Path, File.GetAttributes(running.Path) | FileAttributes.ReadOnly);
                    try
                    {
                        string task = f.OverlayTask(new ElevatedTaskFile { Path = "a.dat", Staged = f.StageFile("0.part", A) });

                        ElevatedTaskResult r = f.Run(task);

                        Assert.AreEqual(LauncherError.GameRunning, r.Code, r.Detail);
                        CollectionAssert.AreEqual(before, File.ReadAllBytes(running.Path));
                        Assert.AreNotEqual(0, (int)(File.GetAttributes(running.Path) & FileAttributes.ReadOnly), "the read-only attribute must still be set");
                    }
                    finally
                    {
                        File.SetAttributes(running.Path, File.GetAttributes(running.Path) & ~FileAttributes.ReadOnly);
                    }
                }

                Assert.IsFalse(InstallData.AnyTempLeft(f.Ac), "no .dwtmp and no ~RF*.TMP may be left behind");
            }
        }
    }
}
