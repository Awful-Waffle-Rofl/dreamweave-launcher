using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using DreamWeave.Launcher.App;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Manifests;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.App
{
    [TestClass]
    public class ElevatedCopyRunnerTests
    {
        private const string Exe = @"C:\Users\p\AppData\Local\Programs\DreamWeave Launcher\DreamWeaveLauncher.exe";
        private const string Task = @"C:\Users\p\AppData\Local\DreamWeave\staging\elevated-task-1.json";

        [TestMethod]
        public void StartsSelfWithRunasAndQuotedTask_ThenCleansUp()
        {
            var processes = new FakeProcessLauncher { ExitCode = 0 };
            int cleaned = 0;
            LauncherError result = new ElevatedCopyRunner(processes, Exe).Run(Task, () => cleaned++);
            Assert.AreEqual(LauncherError.None, result);
            Assert.AreEqual(1, cleaned);
            Assert.AreEqual(1, processes.Ran.Count);
            Assert.AreEqual(Exe, processes.Ran[0].FileName);
            Assert.AreEqual("runas", processes.Ran[0].Verb);
            Assert.IsTrue(processes.Ran[0].UseShellExecute);
            Assert.AreEqual("--elevated-copy \"" + Task + "\"", processes.Ran[0].Arguments);
        }

        [TestMethod]
        public void ExitCode_IsTheLauncherError_AndCleanUpStillRuns()
        {
            var processes = new FakeProcessLauncher { ExitCode = (int)LauncherError.HashMismatch };
            int cleaned = 0;
            Assert.AreEqual(LauncherError.HashMismatch, new ElevatedCopyRunner(processes, Exe).Run(Task, () => cleaned++));
            Assert.AreEqual(1, cleaned);
            Assert.AreEqual(LauncherError.Unknown, ElevatedCopyRunner.ExitCodeToError(-5));
        }

        [TestMethod]
        public void DeclinedUac_IsElevationDeclined_AndCleansUp()
        {
            var processes = new FakeProcessLauncher { Throw = new Win32Exception(1223) };
            int cleaned = 0;
            Assert.AreEqual(LauncherError.ElevationDeclined, new ElevatedCopyRunner(processes, Exe).Run(Task, () => cleaned++));
            Assert.AreEqual(1, cleaned);
        }

        [TestMethod]
        public void TaskPathWithQuote_IsRefusedWithoutStarting()
        {
            var processes = new FakeProcessLauncher();
            Assert.AreEqual(LauncherError.ElevatedTaskInvalid, new ElevatedCopyRunner(processes, Exe).Run("C:\\a\"b.json", null));
            Assert.AreEqual(0, processes.Ran.Count);
        }
    }

    [TestClass]
    public class ExePathsTests
    {
        [TestMethod]
        public void InstalledCopy_ComparedCaseInsensitivelyOnFullPath()
        {
            var paths = new AppPaths(@"C:\Users\p\AppData\Local");
            Assert.IsTrue(ExePaths.IsInstalledCopy(@"c:\users\P\appdata\local\programs\dreamweave launcher\DREAMWEAVELAUNCHER.EXE", paths));
            Assert.IsTrue(ExePaths.IsInstalledCopy(@"C:\Users\p\AppData\Local\Programs\DreamWeave Launcher\.\DreamWeaveLauncher.exe", paths));
            Assert.IsFalse(ExePaths.IsInstalledCopy(@"C:\Users\p\Downloads\DreamWeaveLauncher.exe", paths));
        }

        [TestMethod]
        public void UninstallCurrentExePath_UsesInstalledPathWhenRunningTheInstalledCopy()
        {
            var paths = new AppPaths(@"C:\Users\p\AppData\Local");
            string installed = @"C:\Users\p\AppData\Local\Programs\DreamWeave Launcher\DreamWeaveLauncher.exe";
            Assert.AreEqual(installed, ExePaths.UninstallCurrentExePath(@"c:\USERS\p\appdata\local\programs\dreamweave launcher\dreamweavelauncher.exe", paths));
            Assert.AreEqual(@"C:\Users\p\Downloads\DreamWeaveLauncher.exe", ExePaths.UninstallCurrentExePath(@"C:\Users\p\Downloads\DreamWeaveLauncher.exe", paths));
        }
    }

    [TestClass]
    public class ManifestServiceTests
    {
        private static ManifestLoadResult Result(LauncherManifest m, LauncherError status)
        {
            VerifiedManifest v = m == null ? null : new VerifiedManifest(m, new byte[] { 1 }, new byte[] { 2 }, "key");
            return new ManifestLoadResult(v, status, false, "test", new ManifestSourceAttempt[0]);
        }

        [TestMethod]
        public void KeepsLastGood_WhenALaterLoadHasNoManifest()
        {
            LauncherManifest good = AppFixtures.Sample();
            int call = 0;
            var service = new ManifestService(ct => System.Threading.Tasks.Task.FromResult(++call == 1 ? Result(good, LauncherError.None) : Result(null, LauncherError.ManifestUnavailable)));
            ManifestLoadResult first = service.LoadAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreSame(good, first.Manifest);
            ManifestLoadResult second = service.LoadAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreSame(good, second.Manifest, "the last good result is returned when a reload has none");
            Assert.AreEqual(LauncherError.ManifestUnavailable, service.Latest.Status);
            Assert.AreSame(good, service.Manifest);
        }

        [TestMethod]
        public void Loads_AreSerialized()
        {
            int inFlight = 0;
            int maxInFlight = 0;
            var service = new ManifestService(async ct =>
            {
                int now = Interlocked.Increment(ref inFlight);
                int seen;
                while ((seen = Volatile.Read(ref maxInFlight)) < now && Interlocked.CompareExchange(ref maxInFlight, now, seen) != seen)
                {
                }

                await System.Threading.Tasks.Task.Delay(30).ConfigureAwait(false);
                Interlocked.Decrement(ref inFlight);
                return Result(AppFixtures.Sample(), LauncherError.None);
            });
            Task<ManifestLoadResult>[] loads = { service.LoadAsync(CancellationToken.None), service.LoadAsync(CancellationToken.None), service.LoadAsync(CancellationToken.None) };
            System.Threading.Tasks.Task.WaitAll(loads);
            Assert.AreEqual(1, maxInFlight);
        }
    }

    [TestClass]
    public class AggregateProgressTests
    {
        [TestMethod]
        public void WeighsBySize_AndIgnoresVerifyPass()
        {
            LauncherManifest m = AppFixtures.Manifest(AppFixtures.Installer(), AppFixtures.Overlay());
            var agg = new AggregateProgress(m.Components);
            agg.Begin("ac-client");
            agg.Report(new ProgressReport("ac-client", 598884909 / 2, 598884909, null, null, Core.Downloads.TransferProgress.DownloadingMessage));
            double half = agg.Fraction;
            Assert.IsTrue(half > 0.2 && half < 0.35, half.ToString());
            agg.Report(new ProgressReport("ac-client", 10, 598884909, null, null, "Verifying"));
            Assert.AreEqual(half, agg.Fraction, 1e-9, "the verify pass must not send the bar backwards");
            agg.Complete("ac-client");
            agg.Complete("eor-dats");
            Assert.AreEqual(1.0, agg.Fraction, 1e-9);
        }
    }
}
