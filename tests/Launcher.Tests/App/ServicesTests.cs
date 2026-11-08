using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DreamWeave.Launcher.App;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Install;
using DreamWeave.Launcher.Core.Manifests;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.App
{
    [TestClass]
    public class ElevatedPlacementRunnerTests
    {
        private const string Exe = @"C:\Users\p\AppData\Local\Programs\DreamWeave Launcher\DreamWeaveLauncher.exe";
        private static readonly PlacementRequest Request = new PlacementRequest(@"C:\Users\p\AppData\Local", @"C:\Turbine\Asheron's Call", "eor-dats");

        [TestMethod]
        public void StartsSelfWithRunas_AndExactlyTheFixedPlacementCommand()
        {
            var processes = new FakeProcessLauncher { ExitCode = 0 };
            LauncherError result = new ElevatedPlacementRunner(processes, Exe).Run(Request);
            Assert.AreEqual(LauncherError.None, result);
            Assert.AreEqual(1, processes.Ran.Count);
            Assert.AreEqual(Exe, processes.Ran[0].FileName);
            Assert.AreEqual("runas", processes.Ran[0].Verb);
            Assert.IsTrue(processes.Ran[0].UseShellExecute);
            Assert.AreEqual(@"--place-verified ""C:\Users\p\AppData\Local"" ""C:\Turbine\Asheron's Call"" eor-dats", processes.Ran[0].Arguments);
        }

        [TestMethod]
        public void ExitCode_IsTheLauncherError()
        {
            var processes = new FakeProcessLauncher { ExitCode = (int)LauncherError.HashMismatch };
            Assert.AreEqual(LauncherError.HashMismatch, new ElevatedPlacementRunner(processes, Exe).Run(Request));
            Assert.AreEqual(LauncherError.Unknown, ElevatedPlacementRunner.ExitCodeToError(-5));
        }

        [TestMethod]
        public void DeclinedUac_IsElevationDeclined()
        {
            var processes = new FakeProcessLauncher { Throw = new Win32Exception(1223) };
            Assert.AreEqual(LauncherError.ElevationDeclined, new ElevatedPlacementRunner(processes, Exe).Run(Request));
        }

        [TestMethod]
        public void Run_NeverDeletesTheStagedFiles()
        {
            using (var dir = new TempDir())
            {
                var request = new PlacementRequest(dir.Path, @"C:\ac", "eor-dats");
                Directory.CreateDirectory(request.ReadyFolder);
                File.WriteAllBytes(Path.Combine(request.ReadyFolder, "a.dat"), new byte[] { 1 });
                Assert.AreEqual(LauncherError.HashMismatch, new ElevatedPlacementRunner(new FakeProcessLauncher { ExitCode = (int)LauncherError.HashMismatch }, Exe).Run(request));
                Assert.IsTrue(File.Exists(Path.Combine(request.ReadyFolder, "a.dat")), "kept after a failure so a retry can reuse it");
            }
        }

        [TestMethod]
        [DataRow(@"C:\a""b", @"C:\ac", "eor-dats")]
        [DataRow(@"C:\a", "C:\\ac\nx", "eor-dats")]
        [DataRow(@"C:\a", @"C:\ac", "EOR-DATS")]
        [DataRow(@"C:\a", @"C:\ac", "eor-dats --evil")]
        [DataRow(@"C:\a", @"C:\ac", "-x")]
        [DataRow(" ", @"C:\ac", "eor-dats")]
        public void MalformedRequest_IsRefusedWithoutStarting(string appRoot, string acFolder, string componentId)
        {
            var processes = new FakeProcessLauncher();
            Assert.AreEqual(LauncherError.ElevatedTaskInvalid, new ElevatedPlacementRunner(processes, Exe).Run(new PlacementRequest(appRoot, acFolder, componentId)));
            Assert.AreEqual(0, processes.Ran.Count);
        }

        [TestMethod]
        public void TrailingBackslash_IsDoubled_SoTheQuoteIsNotEscaped()
        {
            Assert.AreEqual(@"""D:\\""", ElevatedPlacementRunner.Quote(@"D:\"));
            Assert.AreEqual(@"""C:\x""", ElevatedPlacementRunner.Quote(@"C:\x"));
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
