using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using DreamWeave.Launcher.App;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Cleanup;
using DreamWeave.Launcher.Tests.Cleanup;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.App
{
    [TestClass]
    public class SilentCleanupTests
    {
        private static string UniqueName() => @"Local\DreamWeaveLauncher-Test-" + Guid.NewGuid().ToString("N");

        [TestMethod]
        public void ThrowingPathsFactory_StillReturnsZero()
        {
            int exit = SilentCleanup.Run(() => { throw new InvalidOperationException("no profile"); }, p => null, w => SingleInstance.Acquire(UniqueName(), w));
            Assert.AreEqual(0, exit);
        }

        [TestMethod]
        public void ThrowingEnvironmentFactory_StillReturnsZero()
        {
            using (var fx = new CleanupFixture())
            {
                int exit = SilentCleanup.Run(() => fx.Paths, p => { throw new System.ComponentModel.Win32Exception(5); }, w => SingleInstance.Acquire(UniqueName(), w));
                Assert.AreEqual(0, exit);
            }
        }

        [TestMethod]
        public void ThrowingAcquire_StillReturnsZero()
        {
            using (var fx = new CleanupFixture())
            {
                Assert.AreEqual(0, SilentCleanup.Run(() => fx.Paths, p => fx.Environment, w => { throw new UnauthorizedAccessException(); }));
            }
        }

        [TestMethod]
        public void Success_RemovesDataAndPasswords_NeverRunsTheAcUninstaller_AndReturnsZero()
        {
            using (var fx = new CleanupFixture())
            {
                Directory.CreateDirectory(fx.Paths.DataDir);
                File.WriteAllText(fx.Paths.StateFile, "{}");
                fx.Credentials.Save("alice", "pw");
                fx.MachineRegistry.Set(DreamWeave.Launcher.Core.Detection.AcInstallLocator.UninstallRoot + "\\" + DreamWeave.Launcher.Core.Detection.AcInstallLocator.UninstallGuid, "UninstallString", "MsiExec.exe /X{x}");

                int exit = SilentCleanup.Run(() => fx.Paths, p => fx.Environment, w => SingleInstance.Acquire(UniqueName(), w));

                Assert.AreEqual(0, exit);
                Assert.IsFalse(Directory.Exists(fx.Paths.DataDir));
                Assert.AreEqual(0, fx.Credentials.Saved.Count);
                Assert.AreEqual(0, fx.Processes.Ran.Count);
            }
        }

        [TestMethod]
        public void LauncherStillRunning_AfterTheWait_DoesNothing_AndLogsIfTheLogExists()
        {
            using (var fx = new CleanupFixture())
            {
                Directory.CreateDirectory(fx.Paths.DataDir);
                File.WriteAllText(fx.Paths.StateFile, "{}");
                File.WriteAllText(fx.Paths.LogFile, string.Empty);
                fx.Credentials.Save("alice", "pw");

                // A holder that never lets go. Mutexes belong to a thread, so the contender runs on another one,
                // and its wait is shortened so the test is fast; the wait SilentCleanup asked for is recorded.
                string name = UniqueName();
                TimeSpan asked = TimeSpan.Zero;
                int exit = -1;
                using (SingleInstance holder = SingleInstance.Acquire(name, TimeSpan.Zero))
                {
                    Assert.IsTrue(holder.Owned);
                    var contender = new Thread(() => exit = SilentCleanup.Run(() => fx.Paths, p => fx.Environment, w =>
                    {
                        asked = w;
                        return SingleInstance.Acquire(name, TimeSpan.FromMilliseconds(200));
                    }));
                    contender.Start();
                    contender.Join();
                }

                Assert.AreEqual(0, exit);
                Assert.AreEqual(TimeSpan.FromSeconds(15), asked, "silent cleanup asks for a 15 s wait");
                Assert.IsTrue(Directory.Exists(fx.Paths.DataDir), "an open launcher's data is never deleted");
                Assert.AreEqual(1, fx.Credentials.Saved.Count);
                StringAssert.Contains(File.ReadAllText(fx.Paths.LogFile), "skipped");
            }
        }
        [TestMethod]
        public void SingleInstance_Acquire_WaitsForAHolderThatReleasesAfterTwoSeconds()
        {
            string name = UniqueName();
            var held = new ManualResetEventSlim(false);
            var holder = new Thread(() =>
            {
                using (SingleInstance first = SingleInstance.Acquire(name, TimeSpan.Zero))
                {
                    held.Set();
                    Thread.Sleep(2000);
                    first.Release();
                }
            });
            holder.Start();
            Assert.IsTrue(held.Wait(TimeSpan.FromSeconds(5)), "fixture: the holder took the lock");

            Stopwatch clock = Stopwatch.StartNew();
            using (SingleInstance second = SingleInstance.Acquire(name, TimeSpan.FromSeconds(15)))
            {
                clock.Stop();
                Assert.IsTrue(second.Owned, "a bounded wait picks the lock up once the old launcher lets go");
                Assert.IsTrue(clock.Elapsed >= TimeSpan.FromMilliseconds(1000), "it really waited: " + clock.Elapsed);
                Assert.IsTrue(clock.Elapsed < TimeSpan.FromSeconds(10), "and not the whole wait: " + clock.Elapsed);
            }

            holder.Join();
        }

        [TestMethod]
        public void SingleInstance_Acquire_WithZeroWait_DoesNotWait()
        {
            string name = UniqueName();
            using (SingleInstance first = SingleInstance.Acquire(name, TimeSpan.Zero))
            {
                bool secondOwned = true;
                var t = new Thread(() => { using (SingleInstance s = SingleInstance.Acquire(name, TimeSpan.Zero)) { secondOwned = s.Owned; } });
                t.Start();
                t.Join();
                Assert.IsTrue(first.Owned);
                Assert.IsFalse(secondOwned);
            }
        }
    }
}