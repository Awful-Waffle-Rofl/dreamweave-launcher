using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DreamWeave.Launcher.App;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Detection;
using DreamWeave.Launcher.Core.Downloads;
using DreamWeave.Launcher.Core.Install;
using DreamWeave.Launcher.Core.Manifests;
using DreamWeave.Launcher.Core.State;
using DreamWeave.Launcher.Tests.Detection;
using DreamWeave.Launcher.Tests.Install;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.App
{
    /// <summary>
    /// <see cref="InstallSession"/> end to end over temp folders: a fake downloader, a fake process
    /// starter (no installer ever runs) and the real locator and evaluator.
    /// </summary>
    [TestClass]
    public class InstallSessionTests
    {
        private static readonly byte[] InstallerBytes = { 1, 2, 3, 4, 5 };
        private static readonly byte[] ClientBytes = { 9, 9, 9 };

        private TempDir _dir;
        private string _acFolder;
        private StateStore _state;
        private FakeDownloader _downloads;
        private FakeStarter _starter;
        private string _saved;
        private InstallDependencies _deps;

        [TestInitialize]
        public void Setup()
        {
            _dir = new TempDir();
            _acFolder = Path.Combine(_dir.Path, "ac");
            Directory.CreateDirectory(_acFolder);
            var paths = new AppPaths(Path.Combine(_dir.Path, "root"));
            _state = new StateStore(Path.Combine(_dir.Path, "state.json"));
            _downloads = new FakeDownloader(InstallerBytes);
            _starter = new FakeStarter();
            var fs = new RealAcFileSystem();
            _deps = new InstallDependencies
            {
                Paths = paths,
                Downloads = _downloads,
                Disk = new FixedDiskSpace(long.MaxValue / 4),
                Locator = new AcInstallLocator(new FakeRegistry(), fs, _acFolder),
                Evaluator = new ComponentEvaluator(new FileVerifier(fs, _state)),
                ReadSavedFolder = () => _saved,
                SaveFolder = f => _saved = f,
                Installers = new InstallerRunner(_starter),
                PollInterval = TimeSpan.FromMilliseconds(20),
            };
        }

        [TestCleanup]
        public void Cleanup()
        {
            _dir.Dispose();
        }

        private static ManifestComponent Installer()
        {
            return new ManifestComponent
            {
                Id = "ac-client",
                Kind = ComponentKinds.Installer,
                Required = true,
                Title = "Asheron's Call client",
                Transport = Transports.Http,
                Url = "https://example.test/ac1install.exe",
                Size = InstallerBytes.Length,
                Sha256 = Hasher.ComputeSha256Hex(InstallerBytes),
                SatisfiedWhen = new List<SatisfiedWhenRule> { new SatisfiedWhenRule { Path = "acclient.exe", Sha256 = new List<string> { Hasher.ComputeSha256Hex(ClientBytes) } } },
            };
        }

        private static ManifestComponent OverlayWithClient()
        {
            return new ManifestComponent
            {
                Id = "eor-dats",
                Kind = ComponentKinds.ZipOverlay,
                Required = true,
                Files = new List<ManifestFile> { new ManifestFile { Path = "acclient.exe", Size = ClientBytes.Length, Sha256 = Hasher.ComputeSha256Hex(ClientBytes) } },
            };
        }

        private void InstallClient(byte[] bytes)
        {
            File.WriteAllBytes(Path.Combine(_acFolder, "acclient.exe"), bytes);
        }

        private static async Task WaitFor(Func<bool> condition)
        {
            for (int i = 0; i < 500 && !condition(); i++)
            {
                await Task.Delay(10).ConfigureAwait(false);
            }

            Assert.IsTrue(condition(), "timed out waiting");
        }

        [TestMethod]
        public void FirstDownload_IsNotOnTheCallingThread()
        {
            InstallClient(ClientBytes);
            var m = AppFixtures.Manifest(Installer());
            var session = new InstallSession(_deps, m, _acFolder);
            int caller = Thread.CurrentThread.ManagedThreadId;
            InstallRunResult r = session.RunAsync(m.Components, new Collector(), CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual(LauncherError.None, r.Error);
            Assert.AreEqual(1, _downloads.ThreadIds.Count);
            Assert.AreNotEqual(caller, _downloads.ThreadIds.First(), "the first download ran on the caller's (UI) thread");
        }

        [TestMethod]
        public void Handoff_CompletesWhenDetected_NotWhenTheProcessExits()
        {
            var m = AppFixtures.Manifest(Installer());
            var session = new InstallSession(_deps, m, null);
            var events = new Collector();
            Task<InstallRunResult> run = session.RunAsync(m.Components, events, CancellationToken.None);
            WaitFor(() => events.Has(InstallEventKind.HandoffStarted)).GetAwaiter().GetResult();
            Assert.IsFalse(run.IsCompleted, "the hand-off waits for detection");
            Assert.AreEqual(InstallPhase.Stoppable, session.Phase);

            InstallClient(ClientBytes);
            InstallRunResult r = run.GetAwaiter().GetResult();
            Assert.AreEqual(LauncherError.None, r.Error);
            Assert.AreEqual(_acFolder, r.AcFolder);
            Assert.AreEqual(_acFolder, _saved, "the detected folder is saved");
            Assert.IsTrue(events.Has(InstallEventKind.HandoffEnded));
            Assert.AreEqual(1, _starter.Starts.Count);
            Assert.AreEqual(InstallPhase.Idle, session.Phase);
        }

        [TestMethod]
        public void IFinished_ChecksAtOnce()
        {
            _deps.PollInterval = TimeSpan.FromMinutes(5);
            var m = AppFixtures.Manifest(Installer());
            var session = new InstallSession(_deps, m, null);
            var events = new Collector();
            Task<InstallRunResult> run = session.RunAsync(m.Components, events, CancellationToken.None);
            WaitFor(() => events.Has(InstallEventKind.HandoffStarted)).GetAwaiter().GetResult();

            session.CheckInstallNow();
            WaitFor(() => events.Has(InstallEventKind.HandoffNotDetected)).GetAwaiter().GetResult();
            Assert.IsFalse(run.IsCompleted);

            InstallClient(ClientBytes);
            session.CheckInstallNow();
            Assert.IsTrue(run.Wait(TimeSpan.FromSeconds(10)), "a second click finds the install without waiting for the 5 minute poll");
            Assert.AreEqual(LauncherError.None, run.Result.Error);
        }

        [TestMethod]
        public void CancelDuringHandoff_IsCancelled()
        {
            var m = AppFixtures.Manifest(Installer());
            var session = new InstallSession(_deps, m, null);
            var events = new Collector();
            using (var cts = new CancellationTokenSource())
            {
                Task<InstallRunResult> run = session.RunAsync(m.Components, events, cts.Token);
                WaitFor(() => events.Has(InstallEventKind.HandoffStarted)).GetAwaiter().GetResult();
                cts.Cancel();
                InstallRunResult r = run.GetAwaiter().GetResult();
                Assert.AreEqual(LauncherError.Cancelled, r.Error);
                Assert.AreEqual("ac-client", r.ComponentId);
                Assert.AreEqual(InstallPhase.Idle, session.Phase);
            }
        }

        [TestMethod]
        public void RetryAfterCancelledHandoff_DetectsBeforeRelaunching()
        {
            var m = AppFixtures.Manifest(Installer());
            var session = new InstallSession(_deps, m, null);
            var events = new Collector();
            using (var cts = new CancellationTokenSource())
            {
                Task<InstallRunResult> run = session.RunAsync(m.Components, events, cts.Token);
                WaitFor(() => events.Has(InstallEventKind.HandoffStarted)).GetAwaiter().GetResult();
                cts.Cancel();
                Assert.AreEqual(LauncherError.Cancelled, run.GetAwaiter().GetResult().Error);
            }

            // The player finished the installer anyway, then clicked Retry.
            InstallClient(ClientBytes);
            InstallRunResult retry = session.RunAsync(m.Components, new Collector(), CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual(LauncherError.None, retry.Error);
            Assert.AreEqual(1, _starter.Starts.Count, "the installer must not be launched a second time");
            Assert.AreEqual(1, _downloads.ThreadIds.Count, "nor downloaded again");
        }

        [TestMethod]
        public void RetryAfterCancelledHandoff_RelaunchesWhenNotInstalled()
        {
            var m = AppFixtures.Manifest(Installer());
            var session = new InstallSession(_deps, m, null);
            var events = new Collector();
            using (var cts = new CancellationTokenSource())
            {
                Task<InstallRunResult> run = session.RunAsync(m.Components, events, cts.Token);
                WaitFor(() => events.Has(InstallEventKind.HandoffStarted)).GetAwaiter().GetResult();
                cts.Cancel();
                run.GetAwaiter().GetResult();
            }

            var second = new Collector();
            Task<InstallRunResult> again = session.RunAsync(m.Components, second, CancellationToken.None);
            WaitFor(() => second.Has(InstallEventKind.HandoffStarted)).GetAwaiter().GetResult();
            Assert.AreEqual(2, _starter.Starts.Count);
            InstallClient(ClientBytes);
            Assert.AreEqual(LauncherError.None, again.GetAwaiter().GetResult().Error);
        }

        [TestMethod]
        public void Detect_AcceptsTheC1Case_OnlyWhenAnOverlayReplacesTheExe()
        {
            InstallClient(new byte[] { 7, 7 });
            var withOverlay = AppFixtures.Manifest(Installer(), OverlayWithClient());
            Assert.AreEqual(_acFolder, new InstallSession(_deps, withOverlay, null).Detect(withOverlay.Components[0], CancellationToken.None));

            var alone = AppFixtures.Manifest(Installer());
            Assert.IsNull(new InstallSession(_deps, alone, null).Detect(alone.Components[0], CancellationToken.None));
        }

        [TestMethod]
        public void Detect_NothingInstalled_IsNull()
        {
            var m = AppFixtures.Manifest(Installer());
            Assert.IsNull(new InstallSession(_deps, m, null).Detect(m.Components[0], CancellationToken.None));
        }

        [TestMethod]
        public void PhaseDuringDownload_IsStoppable_AndCloseRulesMapIt()
        {
            InstallClient(ClientBytes);
            _downloads.Gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var m = AppFixtures.Manifest(Installer());
            var session = new InstallSession(_deps, m, _acFolder);
            Assert.AreEqual(InstallPhase.Idle, session.Phase);
            Task<InstallRunResult> run = session.RunAsync(m.Components, new Collector(), CancellationToken.None);
            WaitFor(() => _downloads.ThreadIds.Count == 1).GetAwaiter().GetResult();
            Assert.AreEqual(InstallPhase.Stoppable, session.Phase);
            Assert.AreEqual(CloseDecision.AskToStop, InstallCloseRules.Decide(session.Phase));
            _downloads.Gate.SetResult(true);
            run.GetAwaiter().GetResult();
            Assert.AreEqual(CloseDecision.Allow, InstallCloseRules.Decide(session.Phase));
        }

        [TestMethod]
        public void RequestStop_DuringDownloadCancels_AfterTheRunIsNotRunning()
        {
            InstallClient(ClientBytes);
            _downloads.Gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var m = AppFixtures.Manifest(Installer());
            var session = new InstallSession(_deps, m, _acFolder);
            using (var cts = new CancellationTokenSource())
            {
                Task<InstallRunResult> run = session.RunAsync(m.Components, new Collector(), cts.Token);
                WaitFor(() => _downloads.ThreadIds.Count == 1).GetAwaiter().GetResult();
                Assert.AreEqual(StopOutcome.Stopping, session.RequestStop(cts));
                Assert.IsTrue(cts.IsCancellationRequested);
                _downloads.Gate.SetResult(true);
                run.GetAwaiter().GetResult();
                Assert.AreEqual(StopOutcome.NotRunning, session.RequestStop(cts), "the prompt answered after Run returned closes at once");
            }
        }

        internal sealed class Collector : IProgress<InstallEvent>
        {
            private readonly ConcurrentQueue<InstallEvent> _events = new ConcurrentQueue<InstallEvent>();

            public void Report(InstallEvent value) => _events.Enqueue(value);

            public bool Has(InstallEventKind kind) => _events.Any(e => e.Kind == kind);

            public InstallEvent[] All => _events.ToArray();
        }

        internal sealed class FakeDownloader : IDownloader
        {
            private readonly byte[] _bytes;

            public FakeDownloader(byte[] bytes)
            {
                _bytes = bytes;
            }

            public ConcurrentQueue<int> ThreadIds { get; } = new ConcurrentQueue<int>();

            public TaskCompletionSource<bool> Gate { get; set; }

            public async Task<DownloadResult> DownloadAsync(DownloadRequest request, IProgress<ProgressReport> progress, CancellationToken cancellationToken)
            {
                ThreadIds.Enqueue(Thread.CurrentThread.ManagedThreadId);
                if (Gate != null)
                {
                    await Gate.Task.ConfigureAwait(false);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationPath));
                File.WriteAllBytes(request.DestinationPath, _bytes);
                return new DownloadResult(request.DestinationPath, _bytes.Length, Hasher.ComputeSha256Hex(_bytes), false);
            }
        }

        /// <summary>Records the start and returns a process that has "exited" at once (like a self-extractor).</summary>
        internal sealed class FakeStarter : IProcessStarter
        {
            public ConcurrentQueue<ProcessStartInfo> Starts { get; } = new ConcurrentQueue<ProcessStartInfo>();

            public IStartedProcess Start(ProcessStartInfo startInfo)
            {
                Starts.Enqueue(startInfo);
                return new ExitedProcess();
            }

            private sealed class ExitedProcess : IStartedProcess
            {
                public bool HasExited => true;

                public int? ExitCode => 0;

                public void Dispose()
                {
                }
            }
        }
    }
}
