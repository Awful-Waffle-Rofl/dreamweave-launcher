using System;
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
    /// <summary>
    /// The elevated <c>--place-verified</c> body, run in-process as the current (unelevated) user
    /// over temp folders with a real signed manifest cache.
    /// </summary>
    [TestClass]
    public class VerifiedPlacementRunnerTests
    {
        private const string Url = "https://primary.example.test/manifest.json";
        private const string Overlay = "eor-dats";
        private const string Patch = "fix";
        private static readonly byte[] A = InstallData.Bytes("elevated first file");
        private static readonly byte[] B = InstallData.Bytes("elevated second file, longer");
        private static readonly byte[] C = InstallData.Bytes("elevated nested file");
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
                "      \"files\": [ " + FileJson("a.dat", A) + ", " + FileJson("b.dat", B) + ", " + FileJson("sub/c.dat", C) + " ] },\n" +
                "    { \"id\": \"fix\", \"kind\": \"byte-patch\", \"target\": \"acclient.exe\", \"inputSha256\": \"" + InstallData.Hash(Client) + "\", \"outputSha256\": \"" + InstallData.Hash(ClientPatched) + "\",\n" +
                "      \"patches\": [ { \"offset\": 19, \"expect\": \"" + Hasher.ToHex(Encoding.ASCII.GetBytes("56")) + "\", \"replace\": \"5858\" } ] }\n" +
                "  ]\n}\n";
            return new UTF8Encoding(false).GetBytes(json);
        }

        private sealed class Fixture : IDisposable
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
                Manifest = r.Verified.Manifest;
            }

            public string Root => _dir.Path;

            public AppPaths Paths { get; }

            public LauncherManifest Manifest { get; }

            public string Ac { get; }

            public string Target(string rel) => Path.Combine(Ac, rel);

            public string Ready(string componentId = Overlay) => VerifiedPlacementRunner.ReadyFolder(Paths, componentId);

            public void Stage(string rel, byte[] content, string componentId = Overlay)
            {
                string p = Path.Combine(Ready(componentId), rel.Replace('/', '\\'));
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                File.WriteAllBytes(p, content);
            }

            public VerifiedPlacementResult Run(string componentId = Overlay, string acFolder = null, string appRoot = null, SigningKey trusted = null)
            {
                return new VerifiedPlacementRunner(new ManifestVerifier(new[] { (trusted ?? _key).PublicKey })).Execute(appRoot ?? Paths.Root, acFolder ?? Ac, componentId);
            }

            public void Dispose() => _dir.Dispose();
        }

        // --- the allowed set -------------------------------------------------------------------

        [TestMethod]
        public void VerifiedStagedFiles_AreWritten()
        {
            using (var f = new Fixture())
            {
                f.Stage("a.dat", A);
                f.Stage("b.dat", B);
                f.Stage("sub/c.dat", C);

                VerifiedPlacementResult r = f.Run();

                Assert.AreEqual(LauncherError.None, r.Code, r.Detail);
                CollectionAssert.AreEqual(A, File.ReadAllBytes(f.Target("a.dat")));
                CollectionAssert.AreEqual(B, File.ReadAllBytes(f.Target("b.dat")));
                CollectionAssert.AreEqual(C, File.ReadAllBytes(f.Target(@"sub\c.dat")));
                Assert.IsFalse(InstallData.AnyTempLeft(f.Ac));
            }
        }

        [TestMethod]
        public void StagedFileNotInManifest_IsRefused_NothingWritten()
        {
            using (var f = new Fixture())
            {
                f.Stage("a.dat", A);
                f.Stage("evil.dll", A);

                VerifiedPlacementResult r = f.Run();

                Assert.AreEqual(LauncherError.ElevatedTaskInvalid, r.Code, r.Detail);
                Assert.IsFalse(File.Exists(f.Target("evil.dll")));
                Assert.IsFalse(File.Exists(f.Target("a.dat")), "an unexpected staged file refuses the whole request");
            }
        }

        [TestMethod]
        public void StagedFileWithManifestBytesUnderAnotherName_IsRefused()
        {
            using (var f = new Fixture())
            {
                // Right bytes, wrong place: a.dat's content staged as a path the manifest never names.
                f.Stage("sub/a.dat", A);

                Assert.AreEqual(LauncherError.ElevatedTaskInvalid, f.Run().Code);
                Assert.IsFalse(File.Exists(f.Target(@"sub\a.dat")));
            }
        }

        [TestMethod]
        public void TamperedStagedFile_IsHashMismatch_NothingWritten()
        {
            using (var f = new Fixture())
            {
                byte[] badB = (byte[])B.Clone();
                badB[0] ^= 0x20;
                f.Stage("a.dat", A);
                f.Stage("b.dat", badB);

                VerifiedPlacementResult r = f.Run();

                Assert.AreEqual(LauncherError.HashMismatch, r.Code, r.Detail);
                Assert.IsFalse(File.Exists(f.Target("a.dat")), "no file may be written when any staged file fails");
                Assert.IsFalse(File.Exists(f.Target("b.dat")));
            }
        }

        [TestMethod]
        public void NothingStaged_IsInvalid()
        {
            using (var f = new Fixture())
            {
                Assert.AreEqual(LauncherError.ElevatedTaskInvalid, f.Run().Code, "no ready folder");
                Directory.CreateDirectory(f.Ready());
                Assert.AreEqual(LauncherError.ElevatedTaskInvalid, f.Run().Code, "empty ready folder");
            }
        }

        [TestMethod]
        public void UnknownComponent_IsInvalid()
        {
            using (var f = new Fixture())
            {
                f.Stage("a.dat", A, "nope");
                Assert.AreEqual(LauncherError.ElevatedTaskInvalid, f.Run("nope").Code);
            }
        }

        [TestMethod]
        [DataRow(@"..\eor-dats")]
        [DataRow("..")]
        [DataRow("EOR-DATS")]
        [DataRow("eor_dats")]
        [DataRow("eor-dats ")]
        [DataRow("-eor-dats")]
        [DataRow("")]
        public void UnsafeComponentId_IsInvalid(string componentId)
        {
            using (var f = new Fixture())
            {
                f.Stage("a.dat", A);

                VerifiedPlacementResult r = f.Run(componentId);

                Assert.AreEqual(LauncherError.ElevatedTaskInvalid, r.Code, r.Detail);
                StringAssert.Contains(r.Detail, "component id");
                Assert.IsFalse(File.Exists(f.Target("a.dat")));
            }
        }

        [TestMethod]
        public void SafeComponentIdRule_IsLowercaseDigitsAndHyphen()
        {
            Assert.IsTrue(VerifiedPlacementRunner.IsSafeComponentId("palette-leak-fix"));
            Assert.IsTrue(VerifiedPlacementRunner.IsSafeComponentId("eor-dats2"));
            Assert.IsFalse(VerifiedPlacementRunner.IsSafeComponentId(new string('a', VerifiedPlacementRunner.MaxComponentIdLength + 1)));
            Assert.IsFalse(VerifiedPlacementRunner.IsSafeComponentId("a/b"));
            Assert.IsFalse(VerifiedPlacementRunner.IsSafeComponentId("a.b"));
            Assert.IsFalse(VerifiedPlacementRunner.IsSafeComponentId(null));
        }

        [TestMethod]
        public void FolderWithoutClient_IsInvalid()
        {
            using (var f = new Fixture())
            {
                string elsewhere = Path.Combine(f.Root, "not-ac");
                Directory.CreateDirectory(elsewhere);
                f.Stage("a.dat", A);

                Assert.AreEqual(LauncherError.ElevatedTaskInvalid, f.Run(acFolder: elsewhere).Code);
                Assert.IsFalse(File.Exists(Path.Combine(elsewhere, "a.dat")));
            }
        }

        [TestMethod]
        public void AlreadyCurrentTarget_IsLeftAlone()
        {
            using (var f = new Fixture())
            {
                File.WriteAllBytes(f.Target("a.dat"), A);
                var stamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
                File.SetLastWriteTimeUtc(f.Target("a.dat"), stamp);
                f.Stage("a.dat", A);

                Assert.AreEqual(LauncherError.None, f.Run().Code);
                Assert.AreEqual(stamp, File.GetLastWriteTimeUtc(f.Target("a.dat")));
            }
        }

        // --- the signed manifest ---------------------------------------------------------------

        [TestMethod]
        public void UntrustedManifestSignature_IsRejected_NothingWritten()
        {
            using (var f = new Fixture())
            {
                f.Stage("a.dat", A);

                VerifiedPlacementResult r = f.Run(trusted: _otherKey);

                Assert.AreEqual(LauncherError.ManifestSignatureInvalid, r.Code, r.Detail);
                Assert.IsFalse(File.Exists(f.Target("a.dat")));
            }
        }

        [TestMethod]
        public void TamperedCachedManifest_IsRejected_NothingWritten()
        {
            using (var f = new Fixture())
            {
                string cache = f.Paths.ManifestCacheFile;
                string text = File.ReadAllText(cache);
                int at = text.IndexOf("\"manifestB64\":\"", StringComparison.Ordinal) + 20;
                char c = text[at];
                File.WriteAllText(cache, text.Substring(0, at) + (c == 'A' ? 'B' : 'A') + text.Substring(at + 1));
                f.Stage("a.dat", A);

                VerifiedPlacementResult r = f.Run();

                Assert.AreEqual(LauncherError.ManifestSignatureInvalid, r.Code, r.Detail);
                Assert.IsFalse(File.Exists(f.Target("a.dat")));
            }
        }

        [TestMethod]
        public void OverTheShoulderAppRoot_IsWhereEverythingIsFound()
        {
            using (var f = new Fixture())
            {
                // The elevated process may run as another account: its own %LOCALAPPDATA% has no
                // cache and no staging. Everything must come from the appRoot argument.
                Assert.IsFalse(string.Equals(AppPaths.CreateDefault().Root, f.Paths.Root, StringComparison.OrdinalIgnoreCase));
                f.Stage("a.dat", A);

                VerifiedPlacementResult r = f.Run(appRoot: f.Paths.Root);

                Assert.AreEqual(LauncherError.None, r.Code, r.Detail);
                CollectionAssert.AreEqual(A, File.ReadAllBytes(f.Target("a.dat")));
            }
        }

        [TestMethod]
        public void AppRootWithoutCache_IsManifestUnavailable_WritesNothingThere()
        {
            using (var f = new Fixture())
            {
                string empty = Path.Combine(f.Root, "emptyroot");
                Directory.CreateDirectory(empty);
                f.Stage("a.dat", A);

                VerifiedPlacementResult r = f.Run(appRoot: empty);

                Assert.AreEqual(LauncherError.ManifestUnavailable, r.Code, r.Detail);
                Assert.AreEqual(0, Directory.GetFileSystemEntries(empty).Length, "no state file, lock file or folder may be created");
                Assert.IsFalse(File.Exists(f.Target("a.dat")));
            }
        }

        // --- byte patch: computed unelevated, placed elevated ---------------------------------

        [TestMethod]
        public void PatchApply_StagedUnelevated_IsPlaced()
        {
            using (var f = new Fixture())
            {
                ManifestComponent fix = f.Manifest.Components.Single(c => c.Id == Patch);
                BytePatchResult staged = StageUnderDeny(f, () => new BytePatcher(f.Paths, new FixedDiskSpace(long.MaxValue)).Apply(fix, f.Ac));
                CollectionAssert.AreEqual(Client, File.ReadAllBytes(f.Target("acclient.exe")), "nothing may change before the placement");
                CollectionAssert.AreEqual(ClientPatched, File.ReadAllBytes(Path.Combine(f.Ready(Patch), "acclient.exe")), "the PATCHED bytes are what is staged");

                VerifiedPlacementResult r = f.Run(staged.Placement.ComponentId);

                Assert.AreEqual(LauncherError.None, r.Code, r.Detail);
                CollectionAssert.AreEqual(ClientPatched, File.ReadAllBytes(f.Target("acclient.exe")));
                Assert.IsFalse(InstallData.AnyTempLeft(f.Ac));
                staged.CleanUp();
                Assert.IsFalse(Directory.Exists(f.Ready(Patch)));
            }
        }

        [TestMethod]
        public void PatchRevert_StagedUnelevated_IsPlaced()
        {
            using (var f = new Fixture())
            {
                File.WriteAllBytes(f.Target("acclient.exe"), ClientPatched);
                ManifestComponent fix = f.Manifest.Components.Single(c => c.Id == Patch);
                BytePatchResult staged = StageUnderDeny(f, () => new BytePatcher(f.Paths, new FixedDiskSpace(long.MaxValue)).Revert(fix, f.Ac));
                CollectionAssert.AreEqual(Client, File.ReadAllBytes(Path.Combine(f.Ready(Patch), "acclient.exe")), "the ORIGINAL bytes are what is staged");

                VerifiedPlacementResult r = f.Run(Patch);

                Assert.AreEqual(LauncherError.None, r.Code, r.Detail);
                CollectionAssert.AreEqual(Client, File.ReadAllBytes(f.Target("acclient.exe")));
            }
        }

        [TestMethod]
        public void PatchStagedWithOtherBytes_IsHashMismatch()
        {
            using (var f = new Fixture())
            {
                f.Stage("acclient.exe", InstallData.Bytes("client bytes: 0123456780"), Patch);

                Assert.AreEqual(LauncherError.HashMismatch, f.Run(Patch).Code);
                CollectionAssert.AreEqual(Client, File.ReadAllBytes(f.Target("acclient.exe")));
            }
        }

        [TestMethod]
        public void PatchStagedBesideAnotherFile_IsInvalid()
        {
            using (var f = new Fixture())
            {
                f.Stage("acclient.exe", ClientPatched, Patch);
                f.Stage("d3d9.dll", ClientPatched, Patch);

                Assert.AreEqual(LauncherError.ElevatedTaskInvalid, f.Run(Patch).Code);
                CollectionAssert.AreEqual(Client, File.ReadAllBytes(f.Target("acclient.exe")));
                Assert.IsFalse(File.Exists(f.Target("d3d9.dll")));
            }
        }

        [TestMethod]
        public void PatchOverAnUnknownClient_IsPatchInputMismatch()
        {
            using (var f = new Fixture())
            {
                // Staged output is genuine, but the installed client is neither the input nor the output.
                byte[] other = InstallData.Bytes("client bytes: some other build");
                File.WriteAllBytes(f.Target("acclient.exe"), other);
                f.Stage("acclient.exe", ClientPatched, Patch);

                Assert.AreEqual(LauncherError.PatchInputMismatch, f.Run(Patch).Code);
                CollectionAssert.AreEqual(other, File.ReadAllBytes(f.Target("acclient.exe")));
            }
        }

        // --- the overlay end to end ------------------------------------------------------------

        [TestMethod]
        public void Overlay_StagedUnelevated_IsPlaced_ThenCleanedUp()
        {
            using (var f = new Fixture())
            {
                string zip = InstallData.Zip(Path.Combine(f.Root, "dl", "eor.zip"), InstallData.Entry("a.dat", A), InstallData.Entry("b.dat", B), InstallData.Entry("sub/", null), InstallData.Entry("sub/c.dat", C));
                ManifestComponent eor = f.Manifest.Components.Single(c => c.Id == Overlay);
                ZipOverlayResult staged = StageUnderDeny(f, () => new ZipOverlayInstaller(f.Paths, new FixedDiskSpace(long.MaxValue)).Install(f.Manifest, eor, zip, f.Ac));
                Assert.AreEqual(f.Ready(), staged.Placement.ReadyFolder);
                Assert.AreEqual(f.Paths.Root, staged.Placement.AppRoot);

                VerifiedPlacementResult r = f.Run(staged.Placement.ComponentId, staged.Placement.AcFolder, staged.Placement.AppRoot);

                Assert.AreEqual(LauncherError.None, r.Code, r.Detail);
                CollectionAssert.AreEqual(A, File.ReadAllBytes(f.Target("a.dat")));
                CollectionAssert.AreEqual(C, File.ReadAllBytes(f.Target(@"sub\c.dat")));
                staged.CleanUp();
                Assert.IsFalse(Directory.Exists(f.Ready()));
            }
        }

        private static T StageUnderDeny<T>(Fixture f, Func<T> stage)
        {
            T result;
            string reason;
            using (DenyWrite deny = DenyWrite.TryApply(f.Ac, out reason))
            {
                if (deny == null)
                {
                    Assert.Inconclusive(reason);
                }

                result = stage();
            }

            InstallOutcome outcome = result is BytePatchResult b ? b.Outcome : ((ZipOverlayResult)(object)result).Outcome;
            Assert.AreEqual(InstallOutcome.ElevationRequired, outcome);
            return result;
        }

        // --- locations: reparse points, network and device paths --------------------------------

        [TestMethod]
        public void JunctionInsideReady_IsRefused()
        {
            using (var f = new Fixture())
            {
                string elsewhere = Path.Combine(f.Root, "elsewhere");
                Directory.CreateDirectory(f.Ready());
                string reason;
                if (!InstallData.TryMakeJunction(Path.Combine(f.Ready(), "sub"), elsewhere, out reason))
                {
                    Assert.Inconclusive(reason);
                }

                File.WriteAllBytes(Path.Combine(elsewhere, "c.dat"), C);

                Assert.AreEqual(LauncherError.ReparsePointInPath, f.Run().Code);
                Assert.IsFalse(File.Exists(f.Target(@"sub\c.dat")));
            }
        }

        [TestMethod]
        public void ReadyFolderIsAJunction_IsRefused()
        {
            using (var f = new Fixture())
            {
                string elsewhere = Path.Combine(f.Root, "elsewhere");
                Directory.CreateDirectory(Path.GetDirectoryName(f.Ready()));
                string reason;
                if (!InstallData.TryMakeJunction(f.Ready(), elsewhere, out reason))
                {
                    Assert.Inconclusive(reason);
                }

                File.WriteAllBytes(Path.Combine(elsewhere, "a.dat"), A);

                Assert.AreEqual(LauncherError.ReparsePointInPath, f.Run().Code);
                Assert.IsFalse(File.Exists(f.Target("a.dat")));
            }
        }

        [TestMethod]
        public void JunctionInsideTheGameFolder_IsRefused()
        {
            using (var f = new Fixture())
            {
                string outside = Path.Combine(f.Root, "outside");
                string reason;
                if (!InstallData.TryMakeJunction(f.Target("sub"), outside, out reason))
                {
                    Assert.Inconclusive(reason);
                }

                f.Stage("sub/c.dat", C);

                Assert.AreEqual(LauncherError.ReparsePointInPath, f.Run().Code);
                Assert.AreEqual(0, Directory.GetFileSystemEntries(outside).Length, "nothing may be written through the junction");
            }
        }

        [TestMethod]
        public void AppRootThroughJunction_WritesNothing_IsManifestUnavailable()
        {
            using (var f = new Fixture())
            {
                // appRoot\DreamWeave is a junction to another app's folder.
                string attackerRoot = Path.Combine(f.Root, "attackerroot");
                string victim = Path.Combine(f.Root, "protected");
                Directory.CreateDirectory(attackerRoot);
                Directory.CreateDirectory(victim);
                byte[] foreign = InstallData.Bytes("this is another app's state.json (not JSON)");
                File.WriteAllBytes(Path.Combine(victim, "state.json"), foreign);
                string link = Path.Combine(attackerRoot, "DreamWeave");
                string reason;
                if (!InstallData.TryMakeJunction(link, victim, out reason))
                {
                    Assert.Inconclusive(reason);
                }

                VerifiedPlacementResult r = f.Run(appRoot: attackerRoot);

                Assert.AreEqual(LauncherError.ManifestUnavailable, r.Code, r.Detail);
                StringAssert.Contains(r.Detail, "'" + link + "'", "the detail must name the junction folder itself");
                CollectionAssert.AreEqual(foreign, File.ReadAllBytes(Path.Combine(victim, "state.json")), "the foreign file must be byte-identical");
                CollectionAssert.AreEqual(new[] { "state.json" }, Directory.GetFileSystemEntries(victim).Select(Path.GetFileName).ToArray(), "no .lock, .corrupt-* or anything else may appear");
            }
        }

        [TestMethod]
        [DataRow(@"\\server\share\ac")]
        [DataRow(@"\\.\C:\ac")]
        [DataRow("relative\\ac")]
        public void AcFolderNotLocal_IsInvalid(string acFolder)
        {
            using (var f = new Fixture())
            {
                f.Stage("a.dat", A);
                VerifiedPlacementResult r = f.Run(acFolder: acFolder);
                Assert.AreEqual(LauncherError.ElevatedTaskInvalid, r.Code, r.Detail);
                StringAssert.Contains(r.Detail, "not allowed");
            }
        }

        [TestMethod]
        public void AcFolderAsExtendedPath_ToARealInstall_IsInvalid_NothingWritten()
        {
            using (var f = new Fixture())
            {
                // The same, real game folder in \\?\ form: only the local-fixed-path rule stops it.
                f.Stage("a.dat", A);

                VerifiedPlacementResult r = f.Run(acFolder: @"\\?\" + f.Ac);

                Assert.AreEqual(LauncherError.ElevatedTaskInvalid, r.Code, r.Detail);
                Assert.IsFalse(File.Exists(f.Target("a.dat")));
            }
        }

        [TestMethod]
        public void AppRootAsExtendedPath_ToTheRealData_IsManifestUnavailable_NothingWritten()
        {
            using (var f = new Fixture())
            {
                f.Stage("a.dat", A);

                VerifiedPlacementResult r = f.Run(appRoot: @"\\?\" + f.Paths.Root);

                Assert.AreEqual(LauncherError.ManifestUnavailable, r.Code, r.Detail);
                Assert.IsFalse(File.Exists(f.Target("a.dat")));
            }
        }

        [TestMethod]
        [DataRow(@"\\server\share\app")]
        [DataRow(@"\\.\C:\app")]
        public void AppRootNotLocal_IsManifestUnavailable(string appRoot)
        {
            using (var f = new Fixture())
            {
                Assert.AreEqual(LauncherError.ManifestUnavailable, f.Run(appRoot: appRoot).Code);
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
                        f.Stage("a.dat", A);

                        VerifiedPlacementResult r = f.Run();

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
