using System;
using System.IO;
using System.Linq;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Install;
using DreamWeave.Launcher.Core.Manifests;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace DreamWeave.Launcher.Tests.Install
{
    [TestClass]
    public class ZipOverlayInstallerTests
    {
        private static readonly byte[] A = InstallData.Bytes("contents of the first data file");
        private static readonly byte[] B = InstallData.Bytes("contents of the second data file, a bit longer");
        private static readonly byte[] OldA = InstallData.Bytes("an older first data file");

        private sealed class Fixture : IDisposable
        {
            private readonly TempDir _dir = new TempDir();

            public Fixture()
            {
                Paths = new AppPaths(Path.Combine(_dir.Path, "app"));
                Ac = Path.Combine(_dir.Path, "ac");
                Directory.CreateDirectory(Ac);
                Zip = Path.Combine(_dir.Path, "dl", "eor.zip");
                Component = InstallData.Overlay("eor-dats", InstallData.File("a.dat", A), InstallData.File("b.dat", B));
                Manifest = InstallData.Manifest(Component);
            }

            public string Root => _dir.Path;

            public AppPaths Paths { get; }

            public string Ac { get; }

            public string Zip { get; }

            public ManifestComponent Component { get; set; }

            public LauncherManifest Manifest { get; set; }

            public string Target(string rel) => Path.Combine(Ac, rel);

            public DreamWeave.Launcher.Core.Downloads.IDiskSpace Disk { get; set; } = new FixedDiskSpace(long.MaxValue);

            public ZipOverlayResult Run() => new ZipOverlayInstaller(Paths, Disk).Install(Manifest, Component, Zip, Ac);

            public bool StagingEmpty() => InstallData.NoFilesUnder(Paths.StagingDir);

            public void Dispose() => _dir.Dispose();
        }

        [TestMethod]
        public void HappyPath_WritesEveryFile_LeavesNoTemps()
        {
            using (var f = new Fixture())
            {
                InstallData.Zip(f.Zip, InstallData.Entry("a.dat", A), InstallData.Entry("b.dat", B));

                ZipOverlayResult r = f.Run();

                Assert.AreEqual(InstallOutcome.Completed, r.Outcome);
                CollectionAssert.AreEqual(new[] { "a.dat", "b.dat" }, r.Written.ToArray());
                Assert.AreEqual(0, r.UnexpectedEntries.Count);
                CollectionAssert.AreEqual(A, File.ReadAllBytes(f.Target("a.dat")));
                CollectionAssert.AreEqual(B, File.ReadAllBytes(f.Target("b.dat")));
                Assert.IsFalse(InstallData.AnyTempLeft(f.Ac));
                Assert.IsTrue(f.StagingEmpty(), "staged copies must be removed after a completed install");
            }
        }

        [TestMethod]
        public void ReplacesExistingOutdatedFile()
        {
            using (var f = new Fixture())
            {
                File.WriteAllBytes(f.Target("a.dat"), OldA);
                InstallData.Zip(f.Zip, InstallData.Entry("a.dat", A), InstallData.Entry("b.dat", B));

                ZipOverlayResult r = f.Run();

                Assert.AreEqual(InstallOutcome.Completed, r.Outcome);
                CollectionAssert.AreEqual(A, File.ReadAllBytes(f.Target("a.dat")));
            }
        }

        [TestMethod]
        public void ZipSlipEntry_RefusesWholeArchive()
        {
            using (var f = new Fixture())
            {
                InstallData.Zip(f.Zip, InstallData.Entry("a.dat", A), InstallData.Entry("../evil.dat", B), InstallData.Entry("b.dat", B));

                var ex = Assert.ThrowsExactly<LauncherException>(() => f.Run());

                Assert.AreEqual(LauncherError.ArchiveInvalid, ex.Code);
                Assert.IsFalse(File.Exists(f.Target("a.dat")));
                Assert.IsFalse(File.Exists(f.Target("b.dat")));
                Assert.IsFalse(File.Exists(Path.Combine(f.Root, "evil.dat")));
                Assert.IsTrue(f.StagingEmpty());
            }
        }

        [TestMethod]
        public void RootedEntry_RefusesWholeArchive()
        {
            using (var f = new Fixture())
            {
                InstallData.Zip(f.Zip, InstallData.Entry("a.dat", A), InstallData.Entry("b.dat", B), InstallData.Entry("C:/evil.dat", B));

                Assert.AreEqual(LauncherError.ArchiveInvalid, Assert.ThrowsExactly<LauncherException>(() => f.Run()).Code);
                Assert.IsFalse(File.Exists(f.Target("a.dat")));
            }
        }

        [TestMethod]
        public void UnexpectedEntry_IsReported_AndNotExtracted()
        {
            using (var f = new Fixture())
            {
                InstallData.Zip(f.Zip, InstallData.Entry("a.dat", A), InstallData.Entry("readme.txt", InstallData.Bytes("hello")), InstallData.Entry("extra/", null), InstallData.Entry("b.dat", B));

                ZipOverlayResult r = f.Run();

                Assert.AreEqual(InstallOutcome.Completed, r.Outcome);
                CollectionAssert.AreEquivalent(new[] { "readme.txt", "extra/" }, r.UnexpectedEntries.ToArray());
                Assert.IsFalse(File.Exists(f.Target("readme.txt")));
                Assert.IsFalse(Directory.Exists(f.Target("extra")));
            }
        }

        [TestMethod]
        public void DuplicateEntry_IsArchiveInvalid()
        {
            using (var f = new Fixture())
            {
                InstallData.Zip(f.Zip, InstallData.Entry("a.dat", A), InstallData.Entry("A.DAT", A), InstallData.Entry("b.dat", B));
                Assert.AreEqual(LauncherError.ArchiveInvalid, Assert.ThrowsExactly<LauncherException>(() => f.Run()).Code);
            }
        }

        [TestMethod]
        public void MissingEntry_IsArchiveInvalid_NothingWritten()
        {
            using (var f = new Fixture())
            {
                InstallData.Zip(f.Zip, InstallData.Entry("a.dat", A));

                Assert.AreEqual(LauncherError.ArchiveInvalid, Assert.ThrowsExactly<LauncherException>(() => f.Run()).Code);
                Assert.IsFalse(File.Exists(f.Target("a.dat")), "a.dat must not be written when b.dat is missing");
            }
        }

        [TestMethod]
        public void HashMismatch_NothingWrittenToAnyTarget()
        {
            using (var f = new Fixture())
            {
                File.WriteAllBytes(f.Target("a.dat"), OldA);
                byte[] badB = (byte[])B.Clone();
                badB[3] ^= 0x01;
                InstallData.Zip(f.Zip, InstallData.Entry("a.dat", A), InstallData.Entry("b.dat", badB));

                var ex = Assert.ThrowsExactly<LauncherException>(() => f.Run());

                Assert.AreEqual(LauncherError.HashMismatch, ex.Code);
                CollectionAssert.AreEqual(OldA, File.ReadAllBytes(f.Target("a.dat")), "a verified a.dat must not be written while b.dat fails");
                Assert.IsFalse(File.Exists(f.Target("b.dat")));
                Assert.IsFalse(InstallData.AnyTempLeft(f.Ac));
                Assert.IsTrue(f.StagingEmpty());
            }
        }

        [TestMethod]
        public void SizeMismatch_NothingWrittenToAnyTarget()
        {
            using (var f = new Fixture())
            {
                File.WriteAllBytes(f.Target("a.dat"), OldA);
                InstallData.Zip(f.Zip, InstallData.Entry("a.dat", A), InstallData.Entry("b.dat", InstallData.Bytes("short")));

                var ex = Assert.ThrowsExactly<LauncherException>(() => f.Run());

                Assert.AreEqual(LauncherError.SizeMismatch, ex.Code);
                CollectionAssert.AreEqual(OldA, File.ReadAllBytes(f.Target("a.dat")));
                Assert.IsFalse(File.Exists(f.Target("b.dat")));
            }
        }

        [TestMethod]
        public void LockedTarget_IsGameRunning_NoFileTouched()
        {
            using (var f = new Fixture())
            {
                File.WriteAllBytes(f.Target("b.dat"), InstallData.Bytes("old b"));
                InstallData.Zip(f.Zip, InstallData.Entry("a.dat", A), InstallData.Entry("b.dat", B));

                using (InstallData.HoldLikeGame(f.Target("b.dat")))
                {
                    var ex = Assert.ThrowsExactly<LauncherException>(() => f.Run());
                    Assert.AreEqual(LauncherError.GameRunning, ex.Code);
                }

                Assert.IsFalse(File.Exists(f.Target("a.dat")), "a.dat comes first and must not be written when b.dat is locked");
                CollectionAssert.AreEqual(InstallData.Bytes("old b"), File.ReadAllBytes(f.Target("b.dat")));
                Assert.IsFalse(InstallData.AnyTempLeft(f.Ac));
                Assert.IsTrue(f.StagingEmpty());
            }
        }

        [TestMethod]
        public void RunningExecutableTarget_IsGameRunning_NoOrphans()
        {
            using (var f = new Fixture())
            {
                f.Component = InstallData.Overlay("eor-dats", InstallData.File("a.dat", A), InstallData.File("pingcopy.exe", B));
                f.Manifest = InstallData.Manifest(f.Component);
                InstallData.Zip(f.Zip, InstallData.Entry("a.dat", A), InstallData.Entry("pingcopy.exe", B));
                byte[] before;
                using (var running = new RunningExe(f.Target("pingcopy.exe")))
                {
                    before = File.ReadAllBytes(running.Path);
                    var ex = Assert.ThrowsExactly<LauncherException>(() => f.Run());
                    Assert.AreEqual(LauncherError.GameRunning, ex.Code, ex.Detail);
                }

                CollectionAssert.AreEqual(before, File.ReadAllBytes(f.Target("pingcopy.exe")));
                Assert.IsFalse(File.Exists(f.Target("a.dat")), "nothing may be written when a target is a running program");
                Assert.IsFalse(InstallData.AnyTempLeft(f.Ac), "no .dwtmp and no ~RF*.TMP may be left behind");
            }
        }

        [TestMethod]
        public void NotEnoughFreeSpace_IsDiskFull_NothingWritten()
        {
            using (var f = new Fixture())
            {
                f.Disk = new FixedDiskSpace(1024);
                InstallData.Zip(f.Zip, InstallData.Entry("a.dat", A), InstallData.Entry("b.dat", B));

                var ex = Assert.ThrowsExactly<LauncherException>(() => f.Run());

                Assert.AreEqual(LauncherError.DiskFull, ex.Code);
                Assert.IsFalse(File.Exists(f.Target("a.dat")));
                Assert.IsTrue(f.StagingEmpty());
            }
        }

        [TestMethod]
        public void StagedBytesChangingAfterVerification_IsHashMismatch_TargetUntouched()
        {
            using (var f = new Fixture())
            {
                File.WriteAllBytes(f.Target("a.dat"), OldA);
                byte[] swapped = (byte[])A.Clone();
                swapped[2] ^= 0x40;
                var stream = new SwapAfterFirstPassStream(A, swapped);
                Assert.AreEqual(InstallData.Hash(A), Hasher.ComputeSha256Hex(stream), "first pass (the verification) sees the good bytes");

                var items = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<ManifestFile, Stream>>
                {
                    new System.Collections.Generic.KeyValuePair<ManifestFile, Stream>(f.Component.Files[0], stream),
                };
                var written = new System.Collections.Generic.List<string>();

                var ex = Assert.ThrowsExactly<LauncherException>(() => OverlayFiles.TryWriteAll(f.Ac, items, written, System.Threading.CancellationToken.None));

                Assert.AreEqual(LauncherError.HashMismatch, ex.Code);
                Assert.AreEqual(0, written.Count);
                CollectionAssert.AreEqual(OldA, File.ReadAllBytes(f.Target("a.dat")));
                Assert.IsFalse(InstallData.AnyTempLeft(f.Ac));
            }
        }

        [TestMethod]
        public void StagesInsideTheComponentFolder()
        {
            using (var f = new Fixture())
            {
                InstallData.Zip(f.Zip, InstallData.Entry("a.dat", A), InstallData.Entry("b.dat", B));
                using (DenyWrite deny = DenyWrite.TryApply(f.Ac, out string reason))
                {
                    if (deny == null)
                    {
                        Assert.Inconclusive(reason);
                    }

                    ZipOverlayResult r = f.Run();
                    string expectedParent = DreamWeave.Launcher.Core.Downloads.DownloadRequest.ComponentFolder(f.Paths.StagingDir, "eor-dats");
                    Assert.AreEqual(expectedParent, Path.GetDirectoryName(r.StagingFolder));
                    r.CleanUp();
                }
            }
        }

        [TestMethod]
        public void AlreadyCorrectFile_IsSkipped_AndNotRewritten()
        {
            using (var f = new Fixture())
            {
                File.WriteAllBytes(f.Target("a.dat"), A);
                var stamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
                File.SetLastWriteTimeUtc(f.Target("a.dat"), stamp);
                InstallData.Zip(f.Zip, InstallData.Entry("a.dat", A), InstallData.Entry("b.dat", B));

                ZipOverlayResult r = f.Run();

                CollectionAssert.AreEqual(new[] { "a.dat" }, r.Skipped.ToArray());
                CollectionAssert.AreEqual(new[] { "b.dat" }, r.Written.ToArray());
                Assert.AreEqual(stamp, File.GetLastWriteTimeUtc(f.Target("a.dat")));
            }
        }

        [TestMethod]
        public void EverythingCorrect_IsNothingToDo_WithoutOpeningTheArchive()
        {
            using (var f = new Fixture())
            {
                File.WriteAllBytes(f.Target("a.dat"), A);
                File.WriteAllBytes(f.Target("b.dat"), B);

                ZipOverlayResult r = f.Run();

                Assert.AreEqual(InstallOutcome.NothingToDo, r.Outcome);
                Assert.IsFalse(File.Exists(f.Zip));
            }
        }

        [TestMethod]
        public void TargetCarryingPatchOutput_IsSkipped()
        {
            using (var f = new Fixture())
            {
                byte[] patched = InstallData.Bytes("contents of the first data file, patched");
                f.Manifest = InstallData.Manifest(f.Component, new ManifestComponent { Id = "fix", Kind = ComponentKinds.BytePatch, Target = "a.dat", InputSha256 = InstallData.Hash(A), OutputSha256 = InstallData.Hash(patched) });
                File.WriteAllBytes(f.Target("a.dat"), patched);
                InstallData.Zip(f.Zip, InstallData.Entry("a.dat", A), InstallData.Entry("b.dat", B));

                ZipOverlayResult r = f.Run();

                CollectionAssert.AreEqual(new[] { "a.dat" }, r.Skipped.ToArray(), "a repair must not revert an applied patch");
                CollectionAssert.AreEqual(patched, File.ReadAllBytes(f.Target("a.dat")));
            }
        }

        [TestMethod]
        public void ReparsePointInPath_IsRefused()
        {
            using (var f = new Fixture())
            {
                string outside = Path.Combine(f.Root, "outside");
                string reason;
                if (!InstallData.TryMakeJunction(f.Target("sub"), outside, out reason))
                {
                    Assert.Inconclusive(reason);
                }

                f.Component = InstallData.Overlay("eor-dats", InstallData.File("sub/x.dat", A));
                f.Manifest = InstallData.Manifest(f.Component);
                InstallData.Zip(f.Zip, InstallData.Entry("sub/", null), InstallData.Entry("sub/x.dat", A));

                var ex = Assert.ThrowsExactly<LauncherException>(() => f.Run());

                Assert.AreEqual(LauncherError.ReparsePointInPath, ex.Code);
                Assert.AreEqual(0, Directory.GetFileSystemEntries(outside).Length, "nothing may be written through the junction");
            }
        }

        [TestMethod]
        public void NestedPath_CreatesFolder()
        {
            using (var f = new Fixture())
            {
                f.Component = InstallData.Overlay("eor-dats", InstallData.File("sub/deeper/x.dat", A));
                f.Manifest = InstallData.Manifest(f.Component);
                InstallData.Zip(f.Zip, InstallData.Entry("sub/", null), InstallData.Entry("sub/deeper/", null), InstallData.Entry("sub/deeper/x.dat", A));

                ZipOverlayResult r = f.Run();

                Assert.AreEqual(InstallOutcome.Completed, r.Outcome);
                Assert.AreEqual(0, r.UnexpectedEntries.Count);
                CollectionAssert.AreEqual(A, File.ReadAllBytes(Path.Combine(f.Ac, "sub", "deeper", "x.dat")));
            }
        }

        [TestMethod]
        public void UnwritableFolder_ReturnsElevationTask_WithoutHashes()
        {
            using (var f = new Fixture())
            {
                File.WriteAllBytes(f.Target("a.dat"), OldA);
                InstallData.Zip(f.Zip, InstallData.Entry("a.dat", A), InstallData.Entry("b.dat", B));
                ZipOverlayResult r;
                string reason;
                using (DenyWrite deny = DenyWrite.TryApply(f.Ac, out reason))
                {
                    if (deny == null)
                    {
                        Assert.Inconclusive(reason);
                    }

                    r = f.Run();
                }

                Assert.AreEqual(InstallOutcome.ElevationRequired, r.Outcome);
                CollectionAssert.AreEqual(OldA, File.ReadAllBytes(f.Target("a.dat")));
                Assert.IsTrue(File.Exists(r.TaskPath));
                string json = File.ReadAllText(r.TaskPath);
                Assert.IsFalse(json.Contains(InstallData.Hash(A)) || json.Contains(InstallData.Hash(B)), "the task must carry no hashes");
                JObject task = JObject.Parse(json);
                Assert.AreEqual(ElevatedTask.CurrentFormat, (string)task["format"]);
                Assert.AreEqual(2, task["items"][0]["files"].Count());
                foreach (ElevatedTaskFile tf in r.Task.Items[0].Files)
                {
                    Assert.IsTrue(File.Exists(tf.Staged), "staged copies must survive the hand-off");
                }

                r.CleanUp();
                Assert.IsFalse(File.Exists(r.TaskPath));
                Assert.IsFalse(Directory.Exists(r.StagingFolder));
            }
        }
    }
}
