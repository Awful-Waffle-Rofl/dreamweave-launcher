using System.Collections.Generic;
using System.IO;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Install;
using DreamWeave.Launcher.Core.Manifests;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.Install
{
    [TestClass]
    public class BytePatcherTests
    {
        private static byte[] Original()
        {
            var data = new byte[64];
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = (byte)(i * 7 + 3);
            }

            return data;
        }

        private static byte[] Patched()
        {
            byte[] data = Original();
            data[5] = 0x90;
            data[6] = 0x90;
            data[7] = 0x90;
            data[40] = 0xCC;
            data[41] = 0xCC;
            return data;
        }

        private static string Hex(byte[] data, int offset, int count)
        {
            var part = new byte[count];
            System.Buffer.BlockCopy(data, offset, part, 0, count);
            return Hasher.ToHex(part);
        }

        private static ManifestComponent Component(string target = "acclient.exe")
        {
            byte[] o = Original();
            return new ManifestComponent
            {
                Id = "palette-leak-fix",
                Kind = ComponentKinds.BytePatch,
                Target = target,
                InputSha256 = InstallData.Hash(o),
                OutputSha256 = InstallData.Hash(Patched()),
                Patches = new List<BytePatch>
                {
                    new BytePatch { Offset = 5, Expect = Hex(o, 5, 3), Replace = "909090" },
                    new BytePatch { Offset = 40, Expect = Hex(o, 40, 2), Replace = "CCCC" },
                },
            };
        }

        private sealed class Fixture : System.IDisposable
        {
            private readonly TempDir _dir = new TempDir();

            public Fixture(byte[] content)
            {
                Paths = new AppPaths(Path.Combine(_dir.Path, "app"));
                Ac = Path.Combine(_dir.Path, "ac");
                Directory.CreateDirectory(Ac);
                if (content != null)
                {
                    File.WriteAllBytes(Exe, content);
                }
            }

            public string Root => _dir.Path;

            public AppPaths Paths { get; }

            public string Ac { get; }

            public string Exe => Path.Combine(Ac, "acclient.exe");

            public BytePatcher Patcher => new BytePatcher(Paths);

            public void Dispose() => _dir.Dispose();
        }

        [TestMethod]
        public void Apply_Then_Revert_RoundTrips()
        {
            using (var f = new Fixture(Original()))
            {
                Assert.AreEqual(InstallOutcome.Completed, f.Patcher.Apply(Component(), f.Ac).Outcome);
                CollectionAssert.AreEqual(Patched(), File.ReadAllBytes(f.Exe));

                Assert.AreEqual(InstallOutcome.NothingToDo, f.Patcher.Apply(Component(), f.Ac).Outcome, "applying twice is a no-op");

                Assert.AreEqual(InstallOutcome.Completed, f.Patcher.Revert(Component(), f.Ac).Outcome);
                CollectionAssert.AreEqual(Original(), File.ReadAllBytes(f.Exe));

                Assert.AreEqual(InstallOutcome.NothingToDo, f.Patcher.Revert(Component(), f.Ac).Outcome);
                Assert.IsFalse(InstallData.AnyTempLeft(f.Ac));
            }
        }

        [TestMethod]
        public void InputHashMismatch_IsPatchInputMismatch_Untouched()
        {
            byte[] other = Original();
            other[0] ^= 0xFF;
            using (var f = new Fixture(other))
            {
                var ex = Assert.ThrowsExactly<LauncherException>(() => f.Patcher.Apply(Component(), f.Ac));
                Assert.AreEqual(LauncherError.PatchInputMismatch, ex.Code);
                CollectionAssert.AreEqual(other, File.ReadAllBytes(f.Exe));
            }
        }

        [TestMethod]
        public void ExpectBytesMismatch_IsPatchInputMismatch_Untouched()
        {
            using (var f = new Fixture(Original()))
            {
                ManifestComponent c = Component();
                c.Patches[1].Expect = "0000";

                var ex = Assert.ThrowsExactly<LauncherException>(() => f.Patcher.Apply(c, f.Ac));

                Assert.AreEqual(LauncherError.PatchInputMismatch, ex.Code);
                CollectionAssert.AreEqual(Original(), File.ReadAllBytes(f.Exe));
                Assert.IsFalse(InstallData.AnyTempLeft(f.Ac));
            }
        }

        [TestMethod]
        public void PatchBeyondEnd_IsPatchInputMismatch()
        {
            using (var f = new Fixture(Original()))
            {
                ManifestComponent c = Component();
                c.Patches[1].Offset = 63;
                Assert.AreEqual(LauncherError.PatchInputMismatch, Assert.ThrowsExactly<LauncherException>(() => f.Patcher.Apply(c, f.Ac)).Code);
            }
        }

        [TestMethod]
        public void OutputHashMismatch_IsPatchOutputMismatch_TargetUntouched()
        {
            using (var f = new Fixture(Original()))
            {
                ManifestComponent c = Component();
                c.OutputSha256 = new string('B', 64);

                var ex = Assert.ThrowsExactly<LauncherException>(() => f.Patcher.Apply(c, f.Ac));

                Assert.AreEqual(LauncherError.PatchOutputMismatch, ex.Code);
                CollectionAssert.AreEqual(Original(), File.ReadAllBytes(f.Exe));
                Assert.IsFalse(InstallData.AnyTempLeft(f.Ac));
            }
        }

        [TestMethod]
        public void LockedTarget_IsGameRunning_Untouched()
        {
            using (var f = new Fixture(Original()))
            {
                using (InstallData.HoldLikeGame(f.Exe))
                {
                    var ex = Assert.ThrowsExactly<LauncherException>(() => f.Patcher.Apply(Component(), f.Ac));
                    Assert.AreEqual(LauncherError.GameRunning, ex.Code);
                }

                CollectionAssert.AreEqual(Original(), File.ReadAllBytes(f.Exe));
            }
        }

        [TestMethod]
        public void RunningExecutableTarget_IsGameRunning_NoOrphans()
        {
            using (var f = new Fixture(null))
            {
                string exe = Path.Combine(f.Ac, "pingcopy.exe");
                using (var running = new RunningExe(exe))
                {
                    byte[] ping = File.ReadAllBytes(exe);
                    var c = new ManifestComponent
                    {
                        Id = "fix",
                        Kind = ComponentKinds.BytePatch,
                        Target = "pingcopy.exe",
                        InputSha256 = InstallData.Hash(ping),
                        OutputSha256 = new string('C', 64),
                        Patches = new List<BytePatch> { new BytePatch { Offset = 0, Expect = Hex(ping, 0, 2), Replace = "0000" } },
                    };

                    var ex = Assert.ThrowsExactly<LauncherException>(() => f.Patcher.Apply(c, f.Ac));

                    Assert.AreEqual(LauncherError.GameRunning, ex.Code, ex.Detail);
                    CollectionAssert.AreEqual(ping, File.ReadAllBytes(exe));
                }

                Assert.IsFalse(InstallData.AnyTempLeft(f.Ac), "no .dwtmp and no ~RF*.TMP may be left behind");
            }
        }

        [TestMethod]
        public void NotEnoughFreeSpace_IsDiskFull_Untouched()
        {
            using (var f = new Fixture(Original()))
            {
                var ex = Assert.ThrowsExactly<LauncherException>(() => new BytePatcher(f.Paths, new FixedDiskSpace(10)).Apply(Component(), f.Ac));
                Assert.AreEqual(LauncherError.DiskFull, ex.Code);
                CollectionAssert.AreEqual(Original(), File.ReadAllBytes(f.Exe));
                Assert.IsFalse(InstallData.AnyTempLeft(f.Ac));
            }
        }

        [TestMethod]
        public void MissingTarget_IsPatchInputMismatch()
        {
            using (var f = new Fixture(null))
            {
                Assert.AreEqual(LauncherError.PatchInputMismatch, Assert.ThrowsExactly<LauncherException>(() => f.Patcher.Apply(Component(), f.Ac)).Code);
            }
        }

        [TestMethod]
        public void StaleTempFile_IsReplaced()
        {
            using (var f = new Fixture(Original()))
            {
                File.WriteAllBytes(f.Exe + ".dwtmp", new byte[] { 1, 2, 3 });
                Assert.AreEqual(InstallOutcome.Completed, f.Patcher.Apply(Component(), f.Ac).Outcome);
                CollectionAssert.AreEqual(Patched(), File.ReadAllBytes(f.Exe));
                Assert.IsFalse(InstallData.AnyTempLeft(f.Ac));
            }
        }

        [TestMethod]
        public void TargetBehindJunction_IsRefused()
        {
            using (var f = new Fixture(null))
            {
                string outside = Path.Combine(f.Root, "outside");
                string reason;
                if (!InstallData.TryMakeJunction(Path.Combine(f.Ac, "sub"), outside, out reason))
                {
                    Assert.Inconclusive(reason);
                }

                File.WriteAllBytes(Path.Combine(outside, "acclient.exe"), Original());

                var ex = Assert.ThrowsExactly<LauncherException>(() => f.Patcher.Apply(Component("sub/acclient.exe"), f.Ac));

                Assert.AreEqual(LauncherError.ReparsePointInPath, ex.Code);
                CollectionAssert.AreEqual(Original(), File.ReadAllBytes(Path.Combine(outside, "acclient.exe")));
            }
        }

        [TestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public void UnwritableFolder_StagesTheComputedResult_ForPlacement(bool apply)
        {
            using (var f = new Fixture(apply ? Original() : Patched()))
            {
                // A stale file from an earlier attempt must not survive into the ready folder.
                string ready = VerifiedPlacementRunner.ReadyFolder(f.Paths, "palette-leak-fix");
                Directory.CreateDirectory(ready);
                File.WriteAllBytes(Path.Combine(ready, "stale.bin"), new byte[] { 1 });
                BytePatchResult r;
                string reason;
                using (DenyWrite deny = DenyWrite.TryApply(f.Ac, out reason))
                {
                    if (deny == null)
                    {
                        Assert.Inconclusive(reason);
                    }

                    r = apply ? f.Patcher.Apply(Component(), f.Ac) : f.Patcher.Revert(Component(), f.Ac);
                }

                Assert.AreEqual(InstallOutcome.ElevationRequired, r.Outcome);
                Assert.AreEqual("palette-leak-fix", r.Placement.ComponentId);
                Assert.AreEqual(f.Ac, r.Placement.AcFolder);
                Assert.AreEqual(ready, r.Placement.ReadyFolder);
                CollectionAssert.AreEqual(apply ? Original() : Patched(), File.ReadAllBytes(f.Exe), "the game folder is untouched");
                CollectionAssert.AreEqual(new[] { Path.Combine(ready, "acclient.exe") }, Directory.GetFiles(ready, "*", SearchOption.AllDirectories));
                CollectionAssert.AreEqual(apply ? Patched() : Original(), File.ReadAllBytes(Path.Combine(ready, "acclient.exe")), "the patch is computed unelevated");
                r.CleanUp();
                Assert.IsFalse(Directory.Exists(ready), "CleanUp removes the ready folder");
            }
        }

        [TestMethod]
        public void NothingToDo_RemovesAStaleReadyFolder()
        {
            using (var f = new Fixture(Patched()))
            {
                string ready = VerifiedPlacementRunner.ReadyFolder(f.Paths, "palette-leak-fix");
                Directory.CreateDirectory(ready);
                File.WriteAllBytes(Path.Combine(ready, "acclient.exe"), Patched());

                Assert.AreEqual(InstallOutcome.NothingToDo, f.Patcher.Apply(Component(), f.Ac).Outcome);
                Assert.IsFalse(Directory.Exists(ready), "a left-over ready folder must not outlive a finished step");
            }
        }

        [TestMethod]
        public void UnreadableTarget_IsAccessDenied_NothingStaged()
        {
            using (var f = new Fixture(Original()))
            {
                var me = System.Security.Principal.WindowsIdentity.GetCurrent().User;
                var rule = new System.Security.AccessControl.FileSystemAccessRule(me, System.Security.AccessControl.FileSystemRights.ReadData, System.Security.AccessControl.AccessControlType.Deny);
                System.Security.AccessControl.FileSecurity acl;
                try
                {
                    acl = File.GetAccessControl(f.Exe);
                    acl.AddAccessRule(rule);
                    File.SetAccessControl(f.Exe, acl);
                }
                catch (System.Exception ex)
                {
                    Assert.Inconclusive("could not set a deny-read ACL: " + ex.Message);
                    return;
                }

                try
                {
                    var ex = Assert.ThrowsExactly<LauncherException>(() => f.Patcher.Apply(Component(), f.Ac));
                    Assert.AreEqual(LauncherError.AccessDenied, ex.Code, ex.Detail);
                    Assert.IsTrue(InstallData.NoFilesUnder(f.Paths.StagingDir));
                }
                finally
                {
                    acl = File.GetAccessControl(f.Exe);
                    acl.RemoveAccessRule(rule);
                    File.SetAccessControl(f.Exe, acl);
                }
            }
        }

        [TestMethod]
        public void UnwritableFolder_WrongInput_IsRefusedBeforeAnythingIsStaged()
        {
            using (var f = new Fixture(InstallData.Bytes("some other client build")))
            {
                string reason;
                using (DenyWrite deny = DenyWrite.TryApply(f.Ac, out reason))
                {
                    if (deny == null)
                    {
                        Assert.Inconclusive(reason);
                    }

                    var ex = Assert.ThrowsExactly<LauncherException>(() => f.Patcher.Apply(Component(), f.Ac));
                    Assert.AreEqual(LauncherError.PatchInputMismatch, ex.Code);
                }

                Assert.IsTrue(InstallData.NoFilesUnder(f.Paths.StagingDir));
            }
        }

        [TestMethod]
        public void ReadOnlyRunningExecutableTarget_IsGameRunning_AttributeKept_NoOrphans()
        {
            using (var f = new Fixture(null))
            {
                string exe = Path.Combine(f.Ac, "pingcopy.exe");
                using (var running = new RunningExe(exe))
                {
                    byte[] ping = File.ReadAllBytes(exe);
                    var c = new ManifestComponent
                    {
                        Id = "fix",
                        Kind = ComponentKinds.BytePatch,
                        Target = "pingcopy.exe",
                        InputSha256 = InstallData.Hash(ping),
                        OutputSha256 = new string('C', 64),
                        Patches = new List<BytePatch> { new BytePatch { Offset = 0, Expect = Hex(ping, 0, 2), Replace = "0000" } },
                    };

                    File.SetAttributes(exe, File.GetAttributes(exe) | FileAttributes.ReadOnly);
                    try
                    {
                        var ex = Assert.ThrowsExactly<LauncherException>(() => f.Patcher.Apply(c, f.Ac));

                        Assert.AreEqual(LauncherError.GameRunning, ex.Code, ex.Detail);
                        CollectionAssert.AreEqual(ping, File.ReadAllBytes(exe));
                        Assert.AreNotEqual(0, (int)(File.GetAttributes(exe) & FileAttributes.ReadOnly), "the read-only attribute must still be set");
                    }
                    finally
                    {
                        File.SetAttributes(exe, File.GetAttributes(exe) & ~FileAttributes.ReadOnly);
                    }
                }

                Assert.IsFalse(InstallData.AnyTempLeft(f.Ac), "no .dwtmp and no ~RF*.TMP may be left behind");
            }
        }
    }
}
