using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Manifests;
using DreamWeave.Launcher.Core.SelfInstall;
using DreamWeave.Launcher.Core.Update;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.SelfInstall
{
    [TestClass]
    public class SelfUpdaterTests
    {
        private static readonly byte[] OldBytes = Encoding.ASCII.GetBytes("old launcher bytes");
        private static readonly byte[] NewBytes = Encoding.ASCII.GetBytes("new launcher bytes, version 2");

        private TempDir temp;
        private AppPaths paths;
        private string exe;
        private FaultyFileSystem fs;
        private FakeProcessLauncher processes;
        private FakeAuthenticode authenticode;

        [TestInitialize]
        public void Setup()
        {
            temp = new TempDir();
            paths = new AppPaths(temp.Path);
            Directory.CreateDirectory(paths.InstallDir);
            exe = SelfInstallLayout.InstalledExePath(paths);
            File.WriteAllBytes(exe, OldBytes);
            fs = new FaultyFileSystem();
            processes = new FakeProcessLauncher();
            authenticode = new FakeAuthenticode();
        }

        [TestCleanup]
        public void Cleanup() => temp.Dispose();

        private static LauncherManifest Manifest(string version, byte[] bytes, string subject = null, string sha = null)
        {
            return new LauncherManifest
            {
                Launcher = new LauncherUpdateInfo
                {
                    Version = version,
                    MinVersion = "1.0.0",
                    Url = "https://example.test/DreamWeaveLauncher.exe",
                    Sha256 = sha ?? Hasher.ComputeSha256Hex(bytes),
                    Size = bytes.Length,
                    AuthenticodeSubject = subject,
                },
            };
        }

        private SelfUpdater Updater(byte[] served, string running = "1.0.0")
        {
            return new SelfUpdater(paths, SemVersion.Parse(running, "running"), exe, new FakeDownloader(served), authenticode, fs, processes);
        }

        private void AssertOriginalIntact()
        {
            CollectionAssert.AreEqual(OldBytes, File.ReadAllBytes(exe));
            Assert.IsFalse(File.Exists(exe + ".old"), ".old should not remain");
            Assert.IsFalse(File.Exists(exe + ".new"), ".new should not remain");
            Assert.AreEqual(0, processes.Started.Count, "nothing should be started");
        }

        [TestMethod]
        public async Task Apply_SwapsVerifiedBytes_AndRelaunchesWithUpdated()
        {
            var downloader = new FakeDownloader(NewBytes);
            var updater = new SelfUpdater(paths, SemVersion.Parse("1.0.0", "r"), exe, downloader, authenticode, fs, processes);

            SelfUpdateResult r = await updater.ApplyAsync(Manifest("2.0.0", NewBytes), null, CancellationToken.None);

            Assert.AreEqual("2.0.0", r.NewVersion.ToString());
            CollectionAssert.AreEqual(NewBytes, File.ReadAllBytes(exe));
            CollectionAssert.AreEqual(OldBytes, File.ReadAllBytes(exe + ".old"));
            Assert.IsFalse(File.Exists(exe + ".new"));
            Assert.AreEqual(1, processes.Started.Count);
            Assert.AreEqual(exe, processes.Started[0].FileName);
            Assert.AreEqual("--updated", processes.Started[0].Arguments);

            // Staged at StagingDir\launcher-update\ with the manifest's size and hash, then cleaned up.
            Assert.AreEqual(1, downloader.Requests.Count);
            Assert.AreEqual("launcher-update", downloader.Requests[0].StepId);
            Assert.AreEqual(Path.Combine(paths.StagingDir, "launcher-update", "DreamWeaveLauncher.exe"), downloader.Requests[0].DestinationPath);
            Assert.AreEqual(NewBytes.Length, downloader.Requests[0].ExpectedSize);
            Assert.IsFalse(File.Exists(downloader.Requests[0].DestinationPath));

            // Null authenticodeSubject: hash-only, the signature check is never called.
            Assert.AreEqual(0, authenticode.Calls.Count);
        }

        [TestMethod]
        public async Task Apply_Downgrade_IsRefusedBeforeDownloading()
        {
            var downloader = new FakeDownloader(NewBytes);
            var updater = new SelfUpdater(paths, SemVersion.Parse("2.0.0", "r"), exe, downloader, authenticode, fs, processes);

            LauncherException ex = await Assert.ThrowsExactlyAsync<LauncherException>(() => updater.ApplyAsync(Manifest("1.5.0", NewBytes), null, CancellationToken.None));

            Assert.AreEqual(LauncherError.UpdateNotNewer, ex.Code);
            Assert.AreEqual(0, downloader.Requests.Count);
            AssertOriginalIntact();
        }

        [TestMethod]
        public async Task Apply_SameVersion_IsRefused()
        {
            LauncherException ex = await Assert.ThrowsExactlyAsync<LauncherException>(() => Updater(NewBytes, "2.0.0").ApplyAsync(Manifest("2.0.0", NewBytes), null, CancellationToken.None));
            Assert.AreEqual(LauncherError.UpdateNotNewer, ex.Code);
            AssertOriginalIntact();
        }

        [TestMethod]
        public async Task Apply_HashMismatch_DoesNotSwap()
        {
            // The downloader hands back bytes that do not match the manifest (for example a file
            // altered after the download verified it): the updater's own check must stop the swap.
            byte[] tampered = Encoding.ASCII.GetBytes("new launcher bytes, version X");
            Assert.AreEqual(NewBytes.Length, tampered.Length);

            LauncherException ex = await Assert.ThrowsExactlyAsync<LauncherException>(() => Updater(tampered).ApplyAsync(Manifest("2.0.0", NewBytes), null, CancellationToken.None));

            Assert.AreEqual(LauncherError.HashMismatch, ex.Code);
            Assert.AreEqual(0, fs.Moves.Count, "no rename may happen after a hash mismatch");
            AssertOriginalIntact();
        }

        [TestMethod]
        public async Task Apply_SizeMismatch_DoesNotSwap()
        {
            byte[] shorter = Encoding.ASCII.GetBytes("short");
            LauncherException ex = await Assert.ThrowsExactlyAsync<LauncherException>(() => Updater(shorter).ApplyAsync(Manifest("2.0.0", NewBytes), null, CancellationToken.None));
            Assert.AreEqual(LauncherError.SizeMismatch, ex.Code);
            Assert.AreEqual(0, fs.Moves.Count);
            AssertOriginalIntact();
        }

        [TestMethod]
        public async Task Apply_SignedManifest_RequiresSignatureCheck()
        {
            authenticode.Fail = true;
            LauncherException ex = await Assert.ThrowsExactlyAsync<LauncherException>(() => Updater(NewBytes).ApplyAsync(Manifest("2.0.0", NewBytes, subject: "CN=Example"), null, CancellationToken.None));

            Assert.AreEqual(LauncherError.UpdateSignatureInvalid, ex.Code);
            CollectionAssert.AreEqual(new[] { "CN=Example" }, authenticode.Calls);
            AssertOriginalIntact();
        }

        [TestMethod]
        public async Task Apply_SignedManifest_PassingSignature_Swaps()
        {
            await Updater(NewBytes).ApplyAsync(Manifest("2.0.0", NewBytes, subject: "CN=Example"), null, CancellationToken.None);
            CollectionAssert.AreEqual(new[] { "CN=Example" }, authenticode.Calls);
            CollectionAssert.AreEqual(NewBytes, File.ReadAllBytes(exe));
        }

        [TestMethod]
        public async Task Apply_SecondRenameFails_RollsBack()
        {
            // Move 1: exe -> exe.old; move 2: exe.new -> exe (fails); move 3: exe.old -> exe (rollback).
            fs.MoveFailure = (n, src, dst) => n == 2 ? new IOException("injected failure") : null;

            LauncherException ex = await Assert.ThrowsExactlyAsync<LauncherException>(() => Updater(NewBytes).ApplyAsync(Manifest("2.0.0", NewBytes), null, CancellationToken.None));

            Assert.AreEqual(LauncherError.UpdateSwapFailed, ex.Code);
            Assert.AreEqual(3, fs.Moves.Count);
            AssertOriginalIntact();
        }

        [TestMethod]
        public async Task Apply_FirstRenameFails_LeavesOriginal()
        {
            fs.MoveFailure = (n, src, dst) => n == 1 ? new UnauthorizedAccessException("injected") : null;

            LauncherException ex = await Assert.ThrowsExactlyAsync<LauncherException>(() => Updater(NewBytes).ApplyAsync(Manifest("2.0.0", NewBytes), null, CancellationToken.None));

            Assert.AreEqual(LauncherError.UpdateSwapFailed, ex.Code);
            AssertOriginalIntact();
        }

        [TestMethod]
        public async Task Apply_RollbackAlsoFails_ReportsRollbackFailed_AndKeepsBothCopies()
        {
            fs.MoveFailure = (n, src, dst) => n >= 2 ? new IOException("injected") : null;

            LauncherException ex = await Assert.ThrowsExactlyAsync<LauncherException>(() => Updater(NewBytes).ApplyAsync(Manifest("2.0.0", NewBytes), null, CancellationToken.None));

            Assert.AreEqual(LauncherError.UpdateRollbackFailed, ex.Code);
            CollectionAssert.AreEqual(OldBytes, File.ReadAllBytes(exe + ".old"));
            CollectionAssert.AreEqual(NewBytes, File.ReadAllBytes(exe + ".new"));
        }

        [TestMethod]
        public async Task Apply_RelaunchFails_RestoresOriginal()
        {
            processes.StartFailure = psi => new System.ComponentModel.Win32Exception(2);

            LauncherException ex = await Assert.ThrowsExactlyAsync<LauncherException>(() => Updater(NewBytes).ApplyAsync(Manifest("2.0.0", NewBytes), null, CancellationToken.None));

            Assert.AreEqual(LauncherError.UpdateSwapFailed, ex.Code);
            CollectionAssert.AreEqual(OldBytes, File.ReadAllBytes(exe));
            Assert.IsFalse(File.Exists(exe + ".old"));
            Assert.IsFalse(File.Exists(exe + ".new"));
        }

        [TestMethod]
        public async Task Apply_StaleOldInUse_FailsBeforeTouchingTheExe()
        {
            File.WriteAllBytes(exe + ".old", OldBytes);
            using (new FileStream(exe + ".old", FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                LauncherException ex = await Assert.ThrowsExactlyAsync<LauncherException>(() => Updater(NewBytes).ApplyAsync(Manifest("2.0.0", NewBytes), null, CancellationToken.None));
                Assert.AreEqual(LauncherError.UpdateSwapFailed, ex.Code);
            }

            CollectionAssert.AreEqual(OldBytes, File.ReadAllBytes(exe));
            Assert.IsFalse(File.Exists(exe + ".new"));
            Assert.AreEqual(0, fs.Moves.Count);
        }

        [TestMethod]
        public void HeldIncomingFile_CannotBeWrittenByOthers_ButCanBeRenamed()
        {
            // The property the swap relies on: while the updater holds <exe>.new, another writer is
            // refused, yet the holder can still rename it into place.
            string path = Path.Combine(temp.Path, "held.bin");
            string moved = Path.Combine(temp.Path, "moved.bin");
            var real = new SelfFileSystem();
            using (FileStream held = real.CreateHeldForMove(path))
            {
                held.Write(NewBytes, 0, NewBytes.Length);
                held.Flush(true);
                Assert.ThrowsExactly<IOException>(() => new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete).Dispose());
                real.Move(path, moved);
            }

            CollectionAssert.AreEqual(NewBytes, File.ReadAllBytes(moved));
        }

        [TestMethod]
        public void HeldStagedFile_CannotBeRenamedOrWritten()
        {
            string path = Path.Combine(temp.Path, "staged.bin");
            File.WriteAllBytes(path, NewBytes);
            using (new SelfFileSystem().OpenReadLocked(path))
            {
                Assert.ThrowsExactly<IOException>(() => File.Move(path, path + ".x"));
                Assert.ThrowsExactly<IOException>(() => new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete).Dispose());
            }
        }

        [TestMethod]
        public void Swap_RenamesARunningExe()
        {
            // Windows lets a running image be renamed (not deleted). Prove it with a copy of a
            // harmless system exe started from the temp folder, standing in for the launcher.
            string ping = Path.Combine(Environment.SystemDirectory, "PING.EXE");
            if (!File.Exists(ping))
            {
                Assert.Inconclusive("PING.EXE not found at " + ping);
            }

            File.Delete(exe);
            File.Copy(ping, exe);
            File.WriteAllBytes(exe + ".new", NewBytes);
            using (Process p = Process.Start(new ProcessStartInfo(exe, "-n 30 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true }))
            {
                try
                {
                    Assert.ThrowsExactly<UnauthorizedAccessException>(() => File.Delete(exe), "a running exe cannot be deleted");
                    string old = ExeSwap.Swap(new SelfFileSystem(), exe);
                    Assert.AreEqual(exe + ".old", old);
                    CollectionAssert.AreEqual(NewBytes, File.ReadAllBytes(exe));
                    Assert.IsTrue(File.Exists(old));
                }
                finally
                {
                    p.Kill();
                    p.WaitForExit();
                }
            }
        }

        [TestMethod]
        public void CompleteUpdate_DeletesOldAndStrayNew()
        {
            File.WriteAllBytes(exe + ".old", OldBytes);
            File.WriteAllBytes(exe + ".new", OldBytes);

            Assert.IsTrue(SelfUpdater.CompleteUpdate(exe, fs, TimeSpan.FromSeconds(5)));

            Assert.IsFalse(File.Exists(exe + ".old"));
            Assert.IsFalse(File.Exists(exe + ".new"));
            Assert.IsTrue(File.Exists(exe), "the running exe is never touched");
        }

        [TestMethod]
        public void CompleteUpdate_WaitsForTheOldProcessToRelease()
        {
            File.WriteAllBytes(exe + ".old", OldBytes);
            var holder = new FileStream(exe + ".old", FileMode.Open, FileAccess.Read, FileShare.Read);
            using (new Timer(_ => holder.Dispose(), null, 400, Timeout.Infinite))
            {
                Assert.IsTrue(SelfUpdater.CompleteUpdate(exe, fs, TimeSpan.FromSeconds(10)));
            }

            Assert.IsFalse(File.Exists(exe + ".old"));
        }

        [TestMethod]
        public void CompleteUpdate_GivesUpAfterTheWait()
        {
            File.WriteAllBytes(exe + ".old", OldBytes);
            using (new FileStream(exe + ".old", FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.IsFalse(SelfUpdater.CompleteUpdate(exe, fs, TimeSpan.FromMilliseconds(300)));
            }
        }

        [TestMethod]
        public void CompleteUpdate_NoOld_IsTrue()
        {
            Assert.IsTrue(SelfUpdater.CompleteUpdate(exe, fs, TimeSpan.Zero));
        }
    }
}
