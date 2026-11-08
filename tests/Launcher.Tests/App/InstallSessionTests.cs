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
                Processes = new FakeProcessTable(),
                PollInterval = TimeSpan.FromMilliseconds(20),
                SettleInterval = TimeSpan.FromMilliseconds(5),
                ProcessPollInterval = TimeSpan.FromMilliseconds(5),
                ClosedEarlyWindow = TimeSpan.FromMilliseconds(5),
                VerifyRetryDelay = TimeSpan.Zero,
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

        private sealed class FallbackDownloader : IDownloader
        {
            private readonly string _label;
            private readonly LauncherError _fail;

            public FallbackDownloader(string label, LauncherError fail = LauncherError.None)
            {
                _label = label;
                _fail = fail;
            }

            public Task<DownloadResult> DownloadAsync(DownloadRequest request, IProgress<ProgressReport> progress, CancellationToken cancellationToken)
            {
                // What DownloadService reports: the primary starts, fails, and the alternate takes over.
                request.OnSourceStarted?.Invoke(new SourceAttempt(0, new DownloadSource(request.Transport, request.Url, request.SourceLabel), null));
                request.OnFallback?.Invoke(_label);
                request.OnSourceStarted?.Invoke(new SourceAttempt(1, new DownloadSource(Transports.Http, new Uri("https://mirror.example.test/a.exe"), _label), new LauncherException(LauncherError.NetworkUnavailable, "mega.nz stopped sending data.")));
                if (_fail != LauncherError.None)
                {
                    var ex = new LauncherException(_fail, "scripted");
                    ex.Data["transport"] = "http";
                    throw ex;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationPath));
                File.WriteAllBytes(request.DestinationPath, InstallerBytes);
                return Task.FromResult(new DownloadResult(request.DestinationPath, InstallerBytes.Length, Hasher.ComputeSha256Hex(InstallerBytes), false));
            }
        }

        [TestMethod]
        public void FallbackFromTheDownloader_ReachesTheUiAsASourceChangedEvent()
        {
            InstallClient(ClientBytes);
            _deps.Downloads = new FallbackDownloader("Original Turbine installer, from the Internet Archive");
            var m = AppFixtures.Manifest(Installer());
            var events = new Collector();
            InstallRunResult r = new InstallSession(_deps, m, _acFolder).RunAsync(m.Components, events, CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual(LauncherError.None, r.Error);
            InstallEvent changed = events.All.Last(e => e.Kind == InstallEventKind.SourceChanged);
            Assert.AreEqual("ac-client", changed.ComponentId);
            Assert.AreEqual("Trying another source: Original Turbine installer, from the Internet Archive" + Environment.NewLine + "The previous source did not work: it could not be reached or stopped sending data.", changed.Text);
        }

        [TestMethod]
        public void EveryDownload_ShowsItsPrimarySourceBeforeAnyFallback()
        {
            InstallClient(ClientBytes);
            _deps.Downloads = new FallbackDownloader("Mirror");
            ManifestComponent c = Installer();
            c.SourceLabel = "Original Turbine installer (mega.nz)";
            var m = AppFixtures.Manifest(c);
            var events = new Collector();
            new InstallSession(_deps, m, _acFolder).RunAsync(m.Components, events, CancellationToken.None).GetAwaiter().GetResult();
            List<InstallEvent> sources = events.All.Where(e => e.Kind == InstallEventKind.SourceChanged).ToList();
            Assert.AreEqual(2, sources.Count);
            Assert.AreEqual("Downloading from: Original Turbine installer (mega.nz)", sources[0].Text);
            StringAssert.StartsWith(sources[1].Text, "Trying another source: Mirror");
        }

        [TestMethod]
        public void FallbackReason_IsPlainWords_ForDamagedQuotaAndUnusableSources()
        {
            ManifestComponent c = Installer();
            var mirror = new DownloadSource(Transports.Http, new Uri("https://mirror.example.test/a.exe"), "Mirror");
            string Reason(LauncherError code) => InstallSession.SourceText(c, new SourceAttempt(1, mirror, new LauncherException(code, "technical detail 123 bytes"))).Split(new[] { Environment.NewLine }, StringSplitOptions.None)[1];
            Assert.AreEqual("The previous source did not work: the file it sent was damaged or incomplete.", Reason(LauncherError.HashMismatch));
            Assert.AreEqual("The previous source did not work: the file it sent was damaged or incomplete.", Reason(LauncherError.SizeMismatch));
            Assert.AreEqual("The previous source did not work: its free download limit has been reached for your connection.", Reason(LauncherError.SourceQuotaExceeded));
            Assert.AreEqual("The previous source did not work: this launcher cannot use that source.", Reason(LauncherError.ManifestInvalid));
            Assert.AreEqual("The previous source did not work: it could not be reached or stopped sending data.", Reason(LauncherError.NetworkUnavailable));
            Assert.AreEqual("The previous source did not work: the file is no longer available there.", Reason(LauncherError.SourceHttpError));
            Assert.AreEqual("The previous source did not work: it failed.", Reason(LauncherError.Unknown));
            foreach (LauncherError code in Enum.GetValues(typeof(LauncherError)).Cast<LauncherError>())
            {
                StringAssert.DoesNotMatch(Reason(code), new System.Text.RegularExpressions.Regex("technical detail"), "the raw Detail reached the UI for " + code);
            }
        }

        [TestMethod]
        public void FallbackWithoutALabel_NamesTheComponentInstead()
        {
            InstallClient(ClientBytes);
            _deps.Downloads = new FallbackDownloader(null);
            var m = AppFixtures.Manifest(Installer());
            var events = new Collector();
            new InstallSession(_deps, m, _acFolder).RunAsync(m.Components, events, CancellationToken.None).GetAwaiter().GetResult();
            StringAssert.StartsWith(events.All.Last(e => e.Kind == InstallEventKind.SourceChanged).Text, "Trying another source: Asheron's Call client" + Environment.NewLine);
        }

        [TestMethod]
        public void QuotaFromAnHttpSource_CarriesItsTransport_AndGetsTheGenericWording()
        {
            _deps.Downloads = new FallbackDownloader("x", LauncherError.SourceQuotaExceeded);
            var m = AppFixtures.Manifest(Installer());
            InstallRunResult r = new InstallSession(_deps, m, _acFolder).RunAsync(m.Components, new Collector(), CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual(LauncherError.SourceQuotaExceeded, r.Error);
            Assert.AreEqual("http", r.SourceTransport);
            Assert.AreEqual(UiText.SourceBusy, ErrorMessages.For(r.Error, r.SourceTransport));
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
        public void ShippedInstallDependencies_HaveTheExpectedFinishWaitDefaults()
        {
            // Every other test overrides these, so the values the player actually gets are pinned only here.
            var deps = new InstallDependencies();
            Assert.AreEqual(TimeSpan.FromSeconds(2), deps.SettleInterval, "SettleInterval");
            Assert.AreEqual(TimeSpan.FromMilliseconds(500), deps.ProcessPollInterval, "ProcessPollInterval");
            Assert.AreEqual(TimeSpan.FromSeconds(10), deps.ClosedEarlyWindow, "ClosedEarlyWindow");
            Assert.AreEqual(TimeSpan.FromSeconds(2), deps.PollInterval, "PollInterval (detection poll)");
            Assert.AreEqual(TimeSpan.FromMinutes(5), deps.SettleTimeout, "SettleTimeout");
        }

        [TestMethod]
        public void InstallerExplanation_IsRaisedBeforeTheLaunchCall_AndStaysUpWhileItBlocks()
        {
            var m = AppFixtures.Manifest(Installer());
            var session = new InstallSession(_deps, m, null);
            var events = new Collector();
            InstallEventKind[] seenAtLaunch = null;
            using (var release = new ManualResetEventSlim(false))
            {
                // The launch call blocks, as ShellExecute does while the Windows permission prompt is open.
                _starter.OnStart = () =>
                {
                    seenAtLaunch = events.All.Select(e => e.Kind).ToArray();
                    release.Wait(TimeSpan.FromSeconds(15));
                };
                Task<InstallRunResult> run = session.RunAsync(m.Components, events, CancellationToken.None);
                WaitFor(() => Volatile.Read(ref seenAtLaunch) != null).GetAwaiter().GetResult();

                CollectionAssert.Contains(seenAtLaunch, InstallEventKind.HandoffStarting, "the explanation must be raised before the launch call");
                CollectionAssert.DoesNotContain(seenAtLaunch, InstallEventKind.HandoffStarted, "I finished belongs after the launch");
                Assert.AreEqual(UiText.InstallerStarting, events.All.Single(e => e.Kind == InstallEventKind.HandoffStarting).Text);
                Thread.Sleep(100);
                Assert.IsFalse(events.Has(InstallEventKind.HandoffEnded), "the explanation must stay up while the launch call blocks");
                Assert.AreEqual(InstallPhase.Stoppable, session.Phase, "Cancel stays available while the launch call blocks");

                release.Set();
                WaitFor(() => events.Has(InstallEventKind.HandoffStarted)).GetAwaiter().GetResult();
                InstallClient(ClientBytes);
                Assert.AreEqual(LauncherError.None, run.GetAwaiter().GetResult().Error);
            }

            List<InstallEventKind> kinds = events.All.Select(e => e.Kind).ToList();
            int starting = kinds.IndexOf(InstallEventKind.HandoffStarting);
            Assert.IsTrue(starting < kinds.IndexOf(InstallEventKind.HandoffStarted) && kinds.IndexOf(InstallEventKind.HandoffStarted) < kinds.IndexOf(InstallEventKind.HandoffEnded));
            Assert.IsTrue(starting < kinds.IndexOf(InstallEventKind.StepStatus), "the explanation goes up before the integrity check too");
        }

        [TestMethod]
        public void LaunchDeclined_TakesTheExplanationDown()
        {
            _starter.OnStart = () => throw new System.ComponentModel.Win32Exception(InstallerRunner.ErrorCancelled);
            var m = AppFixtures.Manifest(Installer());
            var events = new Collector();
            InstallRunResult r = new InstallSession(_deps, m, null).RunAsync(m.Components, events, CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual(LauncherError.ElevationDeclined, r.Error);
            List<InstallEventKind> kinds = events.All.Select(e => e.Kind).ToList();
            Assert.IsTrue(kinds.IndexOf(InstallEventKind.HandoffStarting) >= 0 && kinds.IndexOf(InstallEventKind.HandoffStarting) < kinds.IndexOf(InstallEventKind.HandoffEnded), "a failed launch must take the explanation down");
            Assert.IsFalse(kinds.Contains(InstallEventKind.HandoffStarted));
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

            /// <summary>Runs inside the launch call, after it is recorded (may block, as a permission prompt does, or throw).</summary>
            public Action OnStart { get; set; }

            public IStartedProcess Start(ProcessStartInfo startInfo)
            {
                Starts.Enqueue(startInfo);
                OnStart?.Invoke();
                return new ExitedProcess();
            }

            private sealed class ExitedProcess : IStartedProcess
            {
                public bool HasExited => true;

                public int? ExitCode => 0;

                public int? Id => null;

                public void Dispose()
                {
                }
            }
        }
    }
}
