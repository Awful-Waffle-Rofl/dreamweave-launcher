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
using DreamWeave.Launcher.Core.Telemetry;
using DreamWeave.Launcher.Tests.Detection;
using DreamWeave.Launcher.Tests.Install;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.App
{
    /// <summary>
    /// A full first-run plan (installer, game data overlay, optional patch) through the real
    /// InstallSession, ZipOverlayInstaller, BytePatcher and evaluator. Small fixture files stand in
    /// for the real ones; the installer fake drops the stock exe and stock dats like the retail setup.
    /// </summary>
    [TestClass]
    public class InstallFullPlanTests
    {
        private static readonly byte[] InstallerBytes = { 1, 2, 3, 4, 5 };
        private static readonly byte[] Exe = Enumerable.Range(0, 64).Select(i => (byte)(i + 1)).ToArray();
        private static readonly byte[] StockDat = InstallData.Bytes("stock retail dat, 40 bytes of it, roughly.");
        private static readonly byte[] EorDat = InstallData.Bytes("end of retail dat, different and longer");

        private static byte[] PatchedExe()
        {
            byte[] p = (byte[])Exe.Clone();
            p[10] = 0x90;
            p[11] = 0x90;
            return p;
        }

        private static LauncherManifest Manifest()
        {
            var installer = new ManifestComponent
            {
                Id = "ac-client", Kind = ComponentKinds.Installer, Required = true, Title = "client",
                Transport = Transports.Http, Url = "https://example.test/i.exe",
                Size = InstallerBytes.Length, Sha256 = Hasher.ComputeSha256Hex(InstallerBytes),
                SatisfiedWhen = new List<SatisfiedWhenRule> { new SatisfiedWhenRule { Path = "acclient.exe", Sha256 = new List<string> { Hasher.ComputeSha256Hex(Exe), Hasher.ComputeSha256Hex(PatchedExe()) } } },
            };
            ManifestComponent overlay = InstallData.Overlay("eor-dats", InstallData.File("client_portal.dat", EorDat), InstallData.File("acclient.exe", Exe));
            overlay.Required = true;
            overlay.Title = "dats";
            var patch = new ManifestComponent
            {
                Id = "palette-leak-fix", Kind = ComponentKinds.BytePatch, Required = false, Title = "patch", Target = "acclient.exe",
                InputSha256 = Hasher.ComputeSha256Hex(Exe), OutputSha256 = Hasher.ComputeSha256Hex(PatchedExe()),
                Patches = new List<BytePatch> { new BytePatch { Offset = 10, Expect = BitConverter.ToString(Exe, 10, 2).Replace("-", string.Empty), Replace = "9090" } },
            };
            return InstallData.Manifest(installer, overlay, patch);
        }

        private sealed class Telemetry : ITelemetrySink
        {
            public ConcurrentQueue<string> Lines { get; } = new ConcurrentQueue<string>();

            public void Record(string step, TelemetryOutcome outcome, LauncherError error = LauncherError.None, TimeSpan? duration = null)
            {
                Lines.Enqueue(step + ":" + outcome + ":" + error);
            }

            public void RecordComponent(string componentId, TelemetryOutcome outcome, LauncherError error = LauncherError.None, TimeSpan? duration = null)
            {
                Lines.Enqueue(componentId + ":" + outcome + ":" + error);
            }

            public string[] For(string id) => Lines.Where(l => l.StartsWith(id + ":", StringComparison.Ordinal)).ToArray();
        }

        private sealed class Starter : IProcessStarter
        {
            private readonly string _folder;
            private readonly Action<string> _afterExe;

            public Starter(string folder, Action<string> afterExe)
            {
                _folder = folder;
                _afterExe = afterExe;
            }

            /// <summary>The id the fake "started" process reports (null: ShellExecute returned no handle).</summary>
            public int? Pid { get; set; }

            /// <summary>Runs first in Start, before any file is written (puts the installer's processes in the fake table).</summary>
            public Action OnStart { get; set; }

            public ConcurrentQueue<string> Log { get; set; }

            /// <summary>False: the fake installer writes nothing (still on its first pages).</summary>
            public bool WriteFiles { get; set; } = true;

            public IStartedProcess Start(ProcessStartInfo startInfo)
            {
                Log?.Enqueue("installer-started");
                OnStart?.Invoke();
                if (!WriteFiles)
                {
                    return new Done(Pid);
                }

                // The retail setup: the final exe first, then the stock dats.
                File.WriteAllBytes(Path.Combine(_folder, "acclient.exe"), Exe);
                if (_afterExe != null)
                {
                    _afterExe(_folder);
                }
                else
                {
                    File.WriteAllBytes(Path.Combine(_folder, "client_portal.dat"), StockDat);
                }

                return new Done(Pid);
            }

            private sealed class Done : IStartedProcess
            {
                public Done(int? id)
                {
                    Id = id;
                }

                public bool HasExited => true;

                public int? ExitCode => 0;

                public int? Id { get; }

                public void Dispose()
                {
                }
            }
        }

        private sealed class Downloader : IDownloader
        {
            private readonly string _zip;

            public Downloader(string zip)
            {
                _zip = zip;
            }

            public ConcurrentQueue<string> Log { get; set; }

            /// <summary>When set, a download of this file name waits for the gate.</summary>
            public string GatedFile { get; set; }

            public TaskCompletionSource<bool> Gate { get; set; }

            /// <summary>When set, a download of this file name fails with this code.</summary>
            public string FailFile { get; set; }

            public LauncherError FailWith { get; set; }

            /// <summary>Transport the scripted failure names (DownloadService sets it the same way).</summary>
            public string FailTransport { get; set; }

            public async Task<DownloadResult> DownloadAsync(DownloadRequest request, IProgress<ProgressReport> progress, CancellationToken cancellationToken)
            {
                string name = Path.GetFileName(request.DestinationPath);
                if (Gate != null && name == GatedFile)
                {
                    await Gate.Task.ConfigureAwait(false);
                }

                if (name == FailFile)
                {
                    var ex = new LauncherException(FailWith, "scripted");
                    if (FailTransport != null)
                    {
                        ex.Data["transport"] = FailTransport;
                    }

                    throw ex;
                }

                Log?.Enqueue("downloaded:" + name);
                Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationPath));
                byte[] bytes = request.DestinationPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? File.ReadAllBytes(_zip) : InstallerBytes;
                File.WriteAllBytes(request.DestinationPath, bytes);
                return new DownloadResult(request.DestinationPath, bytes.Length, Hasher.ComputeSha256Hex(bytes), false);
            }
        }

        private sealed class Hooked : IProgress<InstallEvent>
        {
            private readonly ConcurrentQueue<InstallEvent> _events = new ConcurrentQueue<InstallEvent>();
            private readonly Action<InstallEvent> _hook;

            public Hooked(Action<InstallEvent> hook)
            {
                _hook = hook;
            }

            public InstallEvent[] All => _events.ToArray();

            public void Report(InstallEvent value)
            {
                _events.Enqueue(value);
                _hook?.Invoke(value);
            }
        }

        private sealed class Rig : IDisposable
        {
            private readonly TempDir _dir = new TempDir();

            public Rig(Action<string> afterExe = null)
            {
                Ac = Path.Combine(_dir.Path, "ac");
                Directory.CreateDirectory(Ac);
                string zip = InstallData.Zip(Path.Combine(_dir.Path, "dats.zip"), InstallData.Entry("client_portal.dat", EorDat), InstallData.Entry("acclient.exe", Exe));
                Manifest = InstallFullPlanTests.Manifest();
                var state = new StateStore(Path.Combine(_dir.Path, "state.json"));
                var fs = new RealAcFileSystem();
                Evaluator = new ComponentEvaluator(new FileVerifier(fs, state));
                Fs = fs;
                Downloads = new Downloader(zip) { Log = Log };
                Starter = new Starter(Ac, afterExe) { Log = Log };
                Deps = new InstallDependencies
                {
                    Paths = new AppPaths(Path.Combine(_dir.Path, "root")),
                    Downloads = Downloads,
                    Processes = Processes,
                    InstallLog = line => InstallLogLines.Enqueue(line),
                    Disk = new FixedDiskSpace(long.MaxValue / 4),
                    Locator = new AcInstallLocator(new FakeRegistry(), fs, Ac),
                    Evaluator = Evaluator,
                    Telemetry = Telemetry,
                    ReadSavedFolder = () => Saved,
                    SaveFolder = f => Saved = f,
                    Installers = new InstallerRunner(Starter),
                    PollInterval = TimeSpan.FromMilliseconds(20),
                    SettleInterval = TimeSpan.FromMilliseconds(30),
                    ProcessPollInterval = TimeSpan.FromMilliseconds(10),
                    ClosedEarlyWindow = TimeSpan.FromMilliseconds(30),
                    VerifyRetryDelay = TimeSpan.Zero,
                };
            }

            public string Ac { get; }

            /// <summary>Ordered record of downloads and the installer start.</summary>
            public ConcurrentQueue<string> Log { get; } = new ConcurrentQueue<string>();

            public Downloader Downloads { get; }

            public Starter Starter { get; }

            public FakeProcessTable Processes { get; } = new FakeProcessTable();

            /// <summary>Lines the session wrote to launcher.log.</summary>
            public ConcurrentQueue<string> InstallLogLines { get; } = new ConcurrentQueue<string>();

            public string StatePath => Path.Combine(_dir.Path, "state.json");

            public LauncherManifest Manifest { get; }

            public ComponentEvaluator Evaluator { get; }

            public RealAcFileSystem Fs { get; }

            public InstallDependencies Deps { get; }

            public Telemetry Telemetry { get; } = new Telemetry();

            public string Saved { get; set; }

            public List<ManifestComponent> FullPlan()
            {
                // What the Components screen selects on a fresh machine with the palette fix ticked.
                var selection = new ComponentSelection(Manifest, Evaluator.EvaluateAll(Manifest, null));
                selection.SetChecked("palette-leak-fix", true);
                List<ManifestComponent> plan = selection.Selected.Select(r => r.Component).ToList();
                CollectionAssert.AreEqual(new[] { "ac-client", "eor-dats", "palette-leak-fix" }, plan.Select(c => c.Id).ToArray(), "the selection must contain every step");
                return plan;
            }

            public InstallRunResult Run(Hooked events, CancellationToken ct = default(CancellationToken))
            {
                return new InstallSession(Deps, Manifest, null).RunAsync(FullPlan(), events, ct).GetAwaiter().GetResult();
            }

            public void AssertAllSatisfied()
            {
                var next = new ComponentEvaluator(new FileVerifier(Fs, new StateStore(StatePath)));
                var after = next.EvaluateAll(Manifest, Ac);
                foreach (ComponentStatus s in after)
                {
                    Assert.AreEqual(ComponentState.Satisfied, s.State, s.Id + ": " + s.Message);
                }

                Assert.AreEqual(StartRoute.Launcher, RouteDecider.Decide(Manifest, after));
            }

            public string Dat => Path.Combine(Ac, "client_portal.dat");

            public void Dispose() => _dir.Dispose();
        }

        private static string[] Started(Hooked e) => e.All.Where(x => x.Kind == InstallEventKind.StepStarted).Select(x => x.ComponentId).ToArray();

        [TestMethod]
        public void WritableFolder_FullPlan_NeverRequestsElevation()
        {
            using (var rig = new Rig())
            {
                var requests = new ConcurrentQueue<PlacementRequest>();
                rig.Deps.RunElevated = request =>
                {
                    requests.Enqueue(request);
                    return LauncherError.Unknown;
                };

                InstallRunResult r = rig.Run(new Hooked(null));

                Assert.AreEqual(LauncherError.None, r.Error, r.ComponentId);
                Assert.AreEqual(0, requests.Count, "a writable game folder must never ask for administrator rights");
                Assert.IsFalse(Directory.Exists(VerifiedPlacementRunner.ReadyFolder(rig.Deps.Paths, "eor-dats")));
                Assert.IsFalse(Directory.Exists(VerifiedPlacementRunner.ReadyFolder(rig.Deps.Paths, "palette-leak-fix")));
                rig.AssertAllSatisfied();
            }
        }

        [TestMethod]
        public void UnwritableFolder_ReadyFolderIsKeptAfterAFailedPlacement_AndRemovedAfterASuccessfulOne()
        {
            using (var rig = new Rig())
            {
                // The client is already installed (stock dats); only the game data step runs.
                File.WriteAllBytes(Path.Combine(rig.Ac, "acclient.exe"), Exe);
                File.WriteAllBytes(rig.Dat, StockDat);
                List<ManifestComponent> plan = rig.Manifest.Components.Where(c => c.Id == "eor-dats").ToList();
                string ready = VerifiedPlacementRunner.ReadyFolder(rig.Deps.Paths, "eor-dats");
                string reason;
                DenyWrite deny = DenyWrite.TryApply(rig.Ac, out reason);
                if (deny == null)
                {
                    Assert.Inconclusive(reason);
                }

                try
                {
                    var requests = new ConcurrentQueue<PlacementRequest>();
                    rig.Deps.RunElevated = request =>
                    {
                        requests.Enqueue(request);
                        return LauncherError.HashMismatch;
                    };

                    InstallRunResult failed = new InstallSession(rig.Deps, rig.Manifest, rig.Ac).RunAsync(plan, new Hooked(null), CancellationToken.None).GetAwaiter().GetResult();

                    Assert.AreEqual(LauncherError.HashMismatch, failed.Error, failed.Error + " at " + failed.ComponentId + ": " + string.Join(" | ", failed.FileDetails));
                    Assert.AreEqual(1, requests.Count);
                    Assert.AreEqual("eor-dats", requests.Single().ComponentId);
                    Assert.AreEqual(rig.Deps.Paths.Root, requests.Single().AppRoot);
                    CollectionAssert.AreEqual(EorDat, File.ReadAllBytes(Path.Combine(ready, "client_portal.dat")), "the verified copy stays for the retry");

                    // The retry's helper "places" the staged file (as the elevated process would) and succeeds.
                    rig.Deps.RunElevated = request =>
                    {
                        deny.Dispose();
                        deny = null;
                        File.Copy(Path.Combine(request.ReadyFolder, "client_portal.dat"), rig.Dat, true);
                        return LauncherError.None;
                    };

                    InstallRunResult ok = new InstallSession(rig.Deps, rig.Manifest, rig.Ac).RunAsync(plan, new Hooked(null), CancellationToken.None).GetAwaiter().GetResult();

                    Assert.AreEqual(LauncherError.None, ok.Error, ok.ComponentId);
                    CollectionAssert.AreEqual(EorDat, File.ReadAllBytes(rig.Dat));
                    Assert.IsFalse(Directory.Exists(ready), "removed once the placement is borne out");
                }
                finally
                {
                    deny?.Dispose();
                }
            }
        }

        [TestMethod]
        public void FullSelectedPlan_RunsEveryStepInOrder_AndEndsSatisfied()
        {
            using (var rig = new Rig())
            {
                var events = new Hooked(null);
                InstallRunResult r = rig.Run(events);

                Assert.AreEqual(LauncherError.None, r.Error, r.ComponentId);
                CollectionAssert.AreEqual(new[] { "ac-client", "eor-dats", "palette-leak-fix" }, Started(events));
                var done = events.All.Where(e => e.Kind == InstallEventKind.StepDone || e.Kind == InstallEventKind.StepSkipped).ToArray();
                Assert.AreEqual(3, done.Length);
                Assert.AreEqual(InstallEventKind.StepDone, done.Single(e => e.ComponentId == "eor-dats").Kind, "the game data step must do work, not be skipped");
                CollectionAssert.AreEqual(EorDat, File.ReadAllBytes(rig.Dat), "stock dat must have been replaced");
                CollectionAssert.AreEqual(PatchedExe(), File.ReadAllBytes(Path.Combine(rig.Ac, "acclient.exe")));
                rig.AssertAllSatisfied();
                Assert.AreEqual(0, Directory.GetFiles(Path.Combine(rig.Deps.Paths.StagingDir, "eor-dats"), "*.zip").Length, "the staged archive is removed once the plan has verified");
            }
        }

        [TestMethod]
        public void LateStockWrite_AfterTheOverlay_IsRepairedOnceAndRecorded()
        {
            using (var rig = new Rig())
            {
                bool pending = true;
                var events = new Hooked(e =>
                {
                    if (pending && e.Kind == InstallEventKind.StepDone && e.ComponentId == "eor-dats")
                    {
                        pending = false;
                        File.WriteAllBytes(rig.Dat, StockDat);
                    }
                });
                InstallRunResult r = rig.Run(events);

                Assert.AreEqual(LauncherError.None, r.Error);
                CollectionAssert.AreEqual(EorDat, File.ReadAllBytes(rig.Dat));
                rig.AssertAllSatisfied();
                Assert.AreEqual(2, Started(events).Count(id => id == "eor-dats"), "a re-run reports StepStarted");
                CollectionAssert.AreEqual(
                    new[] { "eor-dats:Started:None", "eor-dats:Succeeded:None", "eor-dats:Started:None", "eor-dats:Succeeded:None" },
                    rig.Telemetry.For("eor-dats"),
                    "a re-run records Started and then its outcome");
                Assert.IsTrue(events.All.Any(x => x.Kind == InstallEventKind.StepStatus && x.Text == UiText.VerifyingGameFiles), "the final check has its own text");
                Assert.IsTrue(events.All.Any(x => x.Kind == InstallEventKind.StepProgress && x.ComponentId == "eor-dats"), "hashing during the final check reports progress");
            }
        }

        [TestMethod]
        public void PersistentLateWriter_FailsAsGameFilesChanged_WithTheFilesListed()
        {
            using (var rig = new Rig())
            {
                var events = new Hooked(e =>
                {
                    if (e.Kind == InstallEventKind.StepDone && e.ComponentId == "eor-dats")
                    {
                        File.WriteAllBytes(rig.Dat, StockDat);
                    }
                });
                InstallRunResult r = rig.Run(events);

                Assert.AreEqual(LauncherError.GameFilesChanged, r.Error);
                Assert.AreEqual("eor-dats", r.ComponentId);
                CollectionAssert.Contains(r.FileDetails.ToList(), "client_portal.dat: Mismatch");
                Assert.IsTrue(rig.Telemetry.For("eor-dats").Last().EndsWith(":Failed:GameFilesChanged", StringComparison.Ordinal));
                Assert.IsTrue(File.Exists(Directory.GetFiles(Path.Combine(rig.Deps.Paths.StagingDir, "eor-dats"), "*.zip").FirstOrDefault() ?? string.Empty), "the archive is kept for the retry");

                string copy = ErrorDetails.Build(r.Error, "1.0.0", "eor-dats", DateTime.UtcNow, r.FileDetails);
                StringAssert.Contains(copy, "File: client_portal.dat: Mismatch");
                StringAssert.Contains(copy, "GameFilesChanged (108)");
            }
        }

        [TestMethod]
        public void UnreadableFile_AtTheFinalCheck_IsRetriedOnceThenFailsAsGameFilesChanged()
        {
            using (var rig = new Rig())
            {
                FileStream hold = null;
                var events = new Hooked(e =>
                {
                    if (e.Kind == InstallEventKind.StepDone && e.ComponentId == "palette-leak-fix" && hold == null)
                    {
                        hold = new FileStream(rig.Dat, FileMode.Open, FileAccess.Read, FileShare.None);
                    }
                });
                try
                {
                    InstallRunResult r = rig.Run(events);
                    Assert.AreEqual(LauncherError.GameFilesChanged, r.Error);
                    Assert.AreEqual("eor-dats", r.ComponentId);
                    Assert.IsTrue(r.FileDetails.Any(d => d.StartsWith("client_portal.dat: ", StringComparison.Ordinal)));
                }
                finally
                {
                    hold?.Dispose();
                }
            }
        }

        [TestMethod]
        public void CancelDuringTheFinalCheck_ReturnsCancelled_AndRecordsIt()
        {
            using (var rig = new Rig())
            using (var cts = new CancellationTokenSource())
            {
                var events = new Hooked(e =>
                {
                    if (e.Kind == InstallEventKind.StepDone && e.ComponentId == "palette-leak-fix")
                    {
                        cts.Cancel();
                    }
                });
                InstallRunResult r = rig.Run(events, cts.Token);

                Assert.AreEqual(LauncherError.Cancelled, r.Error);
                Assert.IsTrue(rig.Telemetry.Lines.Contains("eor-dats:Cancelled:Cancelled"), string.Join(",", rig.Telemetry.Lines));
            }
        }

        [TestMethod]
        public void OverlayWaits_UntilTheInstallersChunkedDatWritesStop()
        {
            // The retail setup keeps copying client_portal.dat (chunk by chunk, file held open) for a
            // while after acclient.exe has appeared; the overlay must not start until that stops.
            long writerDone = 0;
            using (var rig = new Rig(folder =>
            {
                var fs = new FileStream(Path.Combine(folder, "client_portal.dat"), FileMode.Create, FileAccess.Write, FileShare.Read);
                fs.Write(StockDat, 0, 8);
                fs.Flush(true);
                Task.Run(() =>
                {
                    for (int at = 8; at < StockDat.Length; at += 8)
                    {
                        Thread.Sleep(80);
                        fs.Write(StockDat, at, Math.Min(8, StockDat.Length - at));
                        fs.Flush(true);
                    }

                    fs.Dispose();
                    Interlocked.Exchange(ref writerDone, 1);
                });
            }))
            {
                long doneAtOverlayStart = -1;
                var events = new Hooked(e =>
                {
                    if (e.Kind == InstallEventKind.StepStarted && e.ComponentId == "eor-dats")
                    {
                        doneAtOverlayStart = Interlocked.Read(ref writerDone);
                    }
                });
                InstallRunResult r = rig.Run(events);

                Assert.AreEqual(LauncherError.None, r.Error, r.ComponentId);
                Assert.AreEqual(1L, doneAtOverlayStart, "the overlay started while the installer was still writing");
                Assert.IsTrue(events.All.Any(x => x.Kind == InstallEventKind.StepStatus && x.Text == UiText.WaitingForInstallerFiles));
                rig.AssertAllSatisfied();
            }
        }

        private static void WaitUntil(Func<bool> condition, string what)
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (!condition() && clock.Elapsed < TimeSpan.FromSeconds(15))
            {
                Thread.Sleep(10);
            }

            Assert.IsTrue(condition(), "timed out waiting for " + what);
        }

        private static bool OverlayStarted(Hooked e) => e.All.Any(x => x.Kind == InstallEventKind.StepStarted && x.ComponentId == "eor-dats");

        private static bool Has(Hooked e, InstallEventKind kind, string text = null) => e.All.Any(x => x.Kind == kind && (text == null || x.Text == text));

        /// <summary>
        /// Starts the run, waits until the hand-off has polled a while, checks the overlay has not
        /// started, then calls <paramref name="release"/> and expects a clean finish.
        /// </summary>
        private static void AssertOverlayHeldUntilReleased(Rig rig, Action release)
        {
            var events = new Hooked(null);
            Task<InstallRunResult> run = new InstallSession(rig.Deps, rig.Manifest, null).RunAsync(rig.FullPlan(), events, CancellationToken.None);
            WaitUntil(() => Has(events, InstallEventKind.StepStatus, UiText.WaitingForInstallerFiles), "the settle wait");
            int seen = rig.Processes.Snapshots;
            WaitUntil(() => rig.Processes.Snapshots >= seen + 8, "more polls");
            Assert.IsFalse(OverlayStarted(events), "the overlay started while the installer was still running");
            Assert.IsFalse(run.IsCompleted, "the run ended while the installer was still running: " + (run.IsCompleted ? run.Result.Error.ToString() : string.Empty));

            release();
            Assert.IsTrue(run.Wait(TimeSpan.FromSeconds(15)), "did not proceed once the installer was gone");
            Assert.AreEqual(LauncherError.None, run.Result.Error, run.Result.ComponentId);
            Assert.IsTrue(OverlayStarted(events));
            rig.AssertAllSatisfied();
        }

        [TestMethod]
        public void InstallerStillRunning_BlocksTheOverlay_UntilItExits()
        {
            using (var rig = new Rig())
            {
                rig.Starter.Pid = 4242;
                rig.Starter.OnStart = () => rig.Processes.Set((4242, 900, "setup-root.exe"));
                AssertOverlayHeldUntilReleased(rig, () => rig.Processes.Set());
            }
        }

        [TestMethod]
        public void InstallerWithoutAProcessId_IsFoundByItsStagedFileName()
        {
            using (var rig = new Rig())
            {
                rig.Starter.Pid = null;
                rig.Starter.OnStart = () => rig.Processes.Set((4242, 900, "ac-client.exe"));
                AssertOverlayHeldUntilReleased(rig, () => rig.Processes.Set());
            }
        }

        [TestMethod]
        public void SelfExtractorExited_WhileItsChildRuns_StillBlocks()
        {
            using (var rig = new Rig())
            {
                // By the first poll the extractor (4242) is already gone; only its child, whose parent id still names it, runs.
                rig.Starter.Pid = 4242;
                rig.Starter.OnStart = () => rig.Processes.Set((5000, 4242, "setup.exe"));
                AssertOverlayHeldUntilReleased(rig, () => rig.Processes.Set());
            }
        }

        [TestMethod]
        public void InstallShieldHelperStillRunning_Blocks()
        {
            using (var rig = new Rig())
            {
                // Started by COM, so not a descendant of anything the launcher started.
                rig.Starter.Pid = 4242;
                rig.Starter.OnStart = () => rig.Processes.Set((77, 600, "IDriver.exe"), (600, 4, "svchost.exe"));
                AssertOverlayHeldUntilReleased(rig, () => rig.Processes.Set((600, 4, "svchost.exe")));
            }
        }

        [TestMethod]
        public void SlowInstallerHoldingADat_PastTheSettleTimeout_KeepsWaitingAndAsksThePlayer()
        {
            // The VM case: the installer still has client_portal.dat open well after acclient.exe appeared,
            // for longer than the settle timeout. Before the fix the overlay started and failed GameRunning.
            FileStream held = null;
            using (var rig = new Rig(folder =>
            {
                held = new FileStream(Path.Combine(folder, "client_portal.dat"), FileMode.Create, FileAccess.Write, FileShare.Read);
                held.Write(StockDat, 0, 8);
                held.Flush(true);
            }))
            {
                rig.Deps.SettleTimeout = TimeSpan.FromMilliseconds(200);
                var events = new Hooked(null);
                Task<InstallRunResult> run = new InstallSession(rig.Deps, rig.Manifest, null).RunAsync(rig.FullPlan(), events, CancellationToken.None);
                try
                {
                    WaitUntil(() => Has(events, InstallEventKind.StepStatus, UiText.InstallerStillRunning), "the finish-the-installer status");
                    Thread.Sleep(400);
                    Assert.IsFalse(OverlayStarted(events), "the overlay started past the settle timeout");
                    Assert.IsFalse(run.IsCompleted);
                    Assert.IsTrue(Has(events, InstallEventKind.HandoffStarted) && !Has(events, InstallEventKind.HandoffEnded), "the I finished box stays up while waiting");

                    // The installer finishes writing and closes the file.
                    held.Write(StockDat, 8, StockDat.Length - 8);
                    held.Dispose();
                    held = null;
                    Assert.IsTrue(run.Wait(TimeSpan.FromSeconds(15)));
                    Assert.AreEqual(LauncherError.None, run.Result.Error, run.Result.ComponentId);
                    rig.AssertAllSatisfied();
                }
                finally
                {
                    held?.Dispose();
                }
            }
        }

        [TestMethod]
        public void SettleWait_PastTheTimeout_WithFilesStillChanging_DoesNotCarryOn()
        {
            bool stop = false;
            using (var rig = new Rig(folder =>
            {
                File.WriteAllBytes(Path.Combine(folder, "client_portal.dat"), StockDat);
                Task.Run(() =>
                {
                    string exe = Path.Combine(folder, "acclient.exe");
                    for (int i = 1; !Volatile.Read(ref stop); i++)
                    {
                        try
                        {
                            File.SetLastWriteTimeUtc(exe, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(i));
                        }
                        catch (IOException)
                        {
                        }

                        Thread.Sleep(10);
                    }
                });
            }))
            {
                rig.Deps.SettleTimeout = TimeSpan.FromMilliseconds(200);
                var events = new Hooked(null);
                Task<InstallRunResult> run = new InstallSession(rig.Deps, rig.Manifest, null).RunAsync(rig.FullPlan(), events, CancellationToken.None);
                try
                {
                    WaitUntil(() => Has(events, InstallEventKind.StepStatus, UiText.InstallerStillRunning), "the finish-the-installer status");
                    Thread.Sleep(400);
                    Assert.IsFalse(OverlayStarted(events), "carried on past the timeout");
                }
                finally
                {
                    Volatile.Write(ref stop, true);
                }

                Assert.IsTrue(run.Wait(TimeSpan.FromSeconds(15)));
                Assert.AreEqual(LauncherError.None, run.Result.Error, run.Result.ComponentId);
            }
        }

        [TestMethod]
        public void IFinished_WithADatStillLocked_RechecksButNeverSkipsTheInUseProbe()
        {
            FileStream held = null;
            using (var rig = new Rig(folder =>
            {
                File.WriteAllBytes(Path.Combine(folder, "client_portal.dat"), StockDat);
                held = new FileStream(Path.Combine(folder, "client_portal.dat"), FileMode.Open, FileAccess.Read, FileShare.Read);
            }))
            {
                // Only clicks drive the settle wait: its own interval is far away.
                rig.Deps.SettleInterval = TimeSpan.FromMinutes(5);
                rig.Deps.SettleTimeout = TimeSpan.FromMinutes(5);
                var events = new Hooked(null);
                var session = new InstallSession(rig.Deps, rig.Manifest, null);
                Task<InstallRunResult> run = session.RunAsync(rig.FullPlan(), events, CancellationToken.None);
                try
                {
                    WaitUntil(() => Has(events, InstallEventKind.StepStatus, UiText.WaitingForInstallerFiles), "the settle wait");
                    session.CheckInstallNow();
                    WaitUntil(() => Has(events, InstallEventKind.HandoffNotDetected, UiText.InstallerStillRunning), "the still-running answer to I finished");
                    Assert.IsFalse(OverlayStarted(events), "I finished skipped the in-use probe");

                    held.Dispose();
                    held = null;
                    session.CheckInstallNow();
                    Assert.IsTrue(run.Wait(TimeSpan.FromSeconds(15)), "a click once the file is free goes on without the 5 minute interval");
                    Assert.AreEqual(LauncherError.None, run.Result.Error, run.Result.ComponentId);
                }
                finally
                {
                    held?.Dispose();
                }
            }
        }

        [TestMethod]
        public void LockedFileAtOverlayTime_GivesTheInstallerStillRunningMessage()
        {
            FileStream held = null;
            using (var rig = new Rig())
            {
                var events = new Hooked(e =>
                {
                    if (e.Kind == InstallEventKind.StepStarted && e.ComponentId == "eor-dats" && held == null)
                    {
                        // A helper of the installer is still around when the game data step hits the lock.
                        rig.Processes.Set((77, 600, "IDriver.exe"));
                        held = new FileStream(rig.Dat, FileMode.Open, FileAccess.Read, FileShare.None);
                    }
                });
                try
                {
                    InstallRunResult r = rig.Run(events);
                    Assert.AreEqual(LauncherError.InstallerStillRunning, r.Error);
                    Assert.AreEqual("eor-dats", r.ComponentId);
                    Assert.AreEqual("eor-dats", r.RetryFromId);
                    Assert.AreEqual(
                        "A game file is still in use, probably by the Asheron's Call installer. Finish the installer, then click Retry.",
                        ErrorMessages.For(r.Error, r.SourceTransport));
                }
                finally
                {
                    held?.Dispose();
                }
            }
        }

        [TestMethod]
        public void EveryDownload_IsVerified_BeforeTheInstallerStarts()
        {
            using (var rig = new Rig())
            {
                rig.Downloads.GatedFile = "eor-dats.zip";
                rig.Downloads.Gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var events = new Hooked(null);
                Task<InstallRunResult> run = new InstallSession(rig.Deps, rig.Manifest, null).RunAsync(rig.FullPlan(), events, CancellationToken.None);
                WaitUntil(() => rig.Log.Contains("downloaded:ac-client.exe"), "the installer download");
                WaitUntil(() => Has(events, InstallEventKind.StageStarted) && events.All.Any(x => x.Kind == InstallEventKind.StageStarted && x.ComponentId == "eor-dats"), "the game data download");
                Thread.Sleep(200);
                Assert.IsFalse(rig.Log.Contains("installer-started"), "the installer started before every download was verified");
                Assert.IsFalse(Has(events, InstallEventKind.StepStarted), "a component was installed before every download was verified");

                rig.Downloads.Gate.SetResult(true);
                Assert.IsTrue(run.Wait(TimeSpan.FromSeconds(15)));
                Assert.AreEqual(LauncherError.None, run.Result.Error, run.Result.ComponentId);
                CollectionAssert.AreEqual(new[] { "downloaded:ac-client.exe", "downloaded:eor-dats.zip", "installer-started" }, rig.Log.ToArray());

                List<InstallEvent> all = events.All.ToList();
                int lastStaged = all.FindLastIndex(x => x.Kind == InstallEventKind.StageDone);
                int firstApplied = all.FindIndex(x => x.Kind == InstallEventKind.StepStarted);
                Assert.IsTrue(lastStaged >= 0 && lastStaged < firstApplied, "every StageDone must precede the first StepStarted");
                CollectionAssert.AreEqual(new[] { "ac-client", "eor-dats" }, all.Where(x => x.Kind == InstallEventKind.StageDone).Select(x => x.ComponentId).ToArray(), "a patch has no download phase");
                Assert.IsTrue(all.FindIndex(x => x.Kind == InstallEventKind.HandoffEnded) < all.FindIndex(x => x.Kind == InstallEventKind.StepStarted && x.ComponentId == "eor-dats"), "no overlay before the hand-off has ended");
                rig.AssertAllSatisfied();
            }
        }

        [TestMethod]
        public void DownloadPhaseFailure_NeverStartsTheInstaller_AndRetriesFromTheFirstComponent()
        {
            using (var rig = new Rig())
            {
                rig.Downloads.FailFile = "eor-dats.zip";
                rig.Downloads.FailWith = LauncherError.SourceQuotaExceeded;
                var session = new InstallSession(rig.Deps, rig.Manifest, null);
                InstallRunResult r = session.RunAsync(rig.FullPlan(), new Hooked(null), CancellationToken.None).GetAwaiter().GetResult();
                Assert.AreEqual(LauncherError.SourceQuotaExceeded, r.Error);
                Assert.AreEqual("eor-dats", r.ComponentId, "the failed row is the game data one");
                Assert.AreEqual("ac-client", r.RetryFromId, "nothing was installed, so Retry starts from the installer");
                Assert.IsFalse(rig.Log.Contains("installer-started"), "the installer window must not appear when a download failed");

                rig.Downloads.FailFile = null;
                InstallRunResult retry = session.RunAsync(rig.FullPlan(), new Hooked(null), CancellationToken.None).GetAwaiter().GetResult();
                Assert.AreEqual(LauncherError.None, retry.Error, retry.ComponentId);
                rig.AssertAllSatisfied();
            }
        }
        [TestMethod]
        public void TreeGone_HelperAlive_IFinished_Proceeds_AndLogsTheHelpers()
        {
            using (var rig = new Rig())
            {
                // The wizard has closed (no tracked process) but a COM-started helper lingers.
                rig.Deps.SettleInterval = TimeSpan.FromMinutes(5);
                rig.Starter.Pid = 4242;
                rig.Starter.OnStart = () => rig.Processes.Set((77, 600, "IDriver.exe"), (600, 4, "svchost.exe"));
                var events = new Hooked(null);
                var session = new InstallSession(rig.Deps, rig.Manifest, null);
                Task<InstallRunResult> run = session.RunAsync(rig.FullPlan(), events, CancellationToken.None);
                WaitUntil(() => Has(events, InstallEventKind.StepStatus, UiText.WaitingForInstallerFiles), "the settle wait");
                Assert.IsFalse(OverlayStarted(events));

                session.CheckInstallNow();
                Assert.IsTrue(run.Wait(TimeSpan.FromSeconds(15)), "I finished with only a helper left did not proceed");
                Assert.AreEqual(LauncherError.None, run.Result.Error, run.Result.ComponentId);
                string line = rig.InstallLogLines.SingleOrDefault(l => l.Contains("proceeding"));
                Assert.IsNotNull(line, "the decision must be logged");
                StringAssert.Contains(line, "IDriver.exe");
                StringAssert.Contains(line, "I finished");
                rig.AssertAllSatisfied();
            }
        }

        [TestMethod]
        public void TreeGone_HelperAlive_WithoutAClick_KeepsWaiting()
        {
            using (var rig = new Rig())
            using (var cts = new CancellationTokenSource())
            {
                rig.Deps.SettleTimeout = TimeSpan.FromMilliseconds(200);
                rig.Starter.Pid = 4242;
                rig.Starter.OnStart = () => rig.Processes.Set((77, 600, "ISBEW64.exe"));
                var events = new Hooked(null);
                Task<InstallRunResult> run = new InstallSession(rig.Deps, rig.Manifest, null).RunAsync(rig.FullPlan(), events, cts.Token);
                WaitUntil(() => Has(events, InstallEventKind.StepStatus, UiText.InstallerStillRunning + " Still running: ISBEW64.exe"), "the finish-the-installer status naming the helper");
                int seen = rig.Processes.Snapshots;
                WaitUntil(() => rig.Processes.Snapshots >= seen + 8, "more polls");
                Assert.IsFalse(OverlayStarted(events), "a helper alone must still block the automatic path");
                Assert.IsFalse(rig.InstallLogLines.Any(l => l.Contains("proceeding")));
                StringAssert.Contains(rig.InstallLogLines.Single(l => l.Contains("settle timeout")), "ISBEW64.exe");

                cts.Cancel();
                Assert.AreEqual(LauncherError.Cancelled, run.GetAwaiter().GetResult().Error);
            }
        }

        [TestMethod]
        public void TreeAlive_IFinished_KeepsWaiting()
        {
            using (var rig = new Rig())
            using (var cts = new CancellationTokenSource())
            {
                rig.Deps.SettleInterval = TimeSpan.FromMinutes(5);
                rig.Starter.Pid = 4242;
                rig.Starter.OnStart = () => rig.Processes.Set((4242, 900, "setup-root.exe"), (77, 600, "IDriver.exe"));
                var events = new Hooked(null);
                var session = new InstallSession(rig.Deps, rig.Manifest, null);
                Task<InstallRunResult> run = session.RunAsync(rig.FullPlan(), events, cts.Token);
                WaitUntil(() => Has(events, InstallEventKind.StepStatus, UiText.WaitingForInstallerFiles), "the settle wait");

                session.CheckInstallNow();
                WaitUntil(() => Has(events, InstallEventKind.HandoffNotDetected, UiText.InstallerStillRunning + " Still running: setup-root.exe, IDriver.exe"), "the still-running answer naming the processes");
                Thread.Sleep(200);
                Assert.IsFalse(OverlayStarted(events), "I finished went on while the installer itself still ran");
                Assert.IsFalse(rig.InstallLogLines.Any(l => l.Contains("proceeding")));
                StringAssert.Contains(rig.InstallLogLines.First(l => l.Contains("I finished refused")), "setup-root.exe");

                cts.Cancel();
                Assert.AreEqual(LauncherError.Cancelled, run.GetAwaiter().GetResult().Error);
            }
        }

        [TestMethod]
        public void TreeExits_FinishArrivesWithinOneSettlePlusOneProcessPoll()
        {
            // Scaled timings: the settle gap is long next to the process poll, so the old loop (one look per
            // settle interval, then a second one a whole interval later) would take about two settle gaps.
            TimeSpan settle = TimeSpan.FromMilliseconds(1000);
            TimeSpan poll = TimeSpan.FromMilliseconds(50);
            using (var rig = new Rig())
            {
                rig.Deps.SettleInterval = settle;
                rig.Deps.ProcessPollInterval = poll;
                rig.Starter.Pid = 4242;
                rig.Starter.OnStart = () => rig.Processes.Set((4242, 900, "setup-root.exe"));
                var exited = new Stopwatch();
                long endedAt = -1;
                var events = new Hooked(e =>
                {
                    if (e.Kind == InstallEventKind.HandoffEnded && Interlocked.Read(ref endedAt) < 0)
                    {
                        Interlocked.Exchange(ref endedAt, exited.ElapsedMilliseconds);
                    }
                });
                Task<InstallRunResult> run = new InstallSession(rig.Deps, rig.Manifest, null).RunAsync(rig.FullPlan(), events, CancellationToken.None);
                WaitUntil(() => Has(events, InstallEventKind.StepStatus, UiText.WaitingForInstallerFiles), "the settle wait");
                int seen = rig.Processes.Snapshots;
                WaitUntil(() => rig.Processes.Snapshots >= seen + 3, "a few process polls");
                Assert.IsFalse(Has(events, InstallEventKind.HandoffEnded), "finished while the installer tree ran");

                // The installer closes just after a poll: the worst case for the old loop.
                seen = rig.Processes.Snapshots;
                WaitUntil(() => rig.Processes.Snapshots > seen, "the next poll");
                exited.Start();
                rig.Processes.Set();
                Assert.IsTrue(run.Wait(TimeSpan.FromSeconds(15)));
                Assert.AreEqual(LauncherError.None, run.Result.Error, run.Result.ComponentId);

                long ms = Interlocked.Read(ref endedAt);
                // Bound sits well below the old cadence (~2000 ms here), with ~600 ms slack over the ~1050 ms
                // expected so a loaded CI machine does not flake while still catching a regression to the old loop.
                Assert.IsTrue(ms >= settle.TotalMilliseconds - 100, "finished on a single file look: " + ms + " ms");
                Assert.IsTrue(ms <= settle.TotalMilliseconds + poll.TotalMilliseconds + 600, "took longer than one settle plus one process poll: " + ms + " ms");
                rig.AssertAllSatisfied();
            }
        }

        [TestMethod]
        public void ActivityBetweenTheTwoFileLooks_RestartsTheSettle()
        {
            // The PR #19 bug class: a file look, then installer activity, then quiet must demand a FULL fresh
            // settle after the activity. Guards the `if (blocked) { previous = null; }` branch; removing that
            // line lets the stale first snapshot match a look taken right after the activity and finish early.
            using (var rig = new Rig())
            {
                rig.Deps.SettleInterval = TimeSpan.FromMilliseconds(600);
                rig.Deps.ProcessPollInterval = TimeSpan.FromMilliseconds(20);

                // The installer wrote its files and left no process behind, so the first file look is taken at once.
                var sinceClear = new Stopwatch();
                long endedAt = -1;
                var events = new Hooked(e =>
                {
                    if (e.Kind == InstallEventKind.HandoffEnded && Interlocked.Read(ref endedAt) < 0)
                    {
                        Interlocked.Exchange(ref endedAt, sinceClear.ElapsedMilliseconds);
                    }
                });
                Task<InstallRunResult> run = new InstallSession(rig.Deps, rig.Manifest, null).RunAsync(rig.FullPlan(), events, CancellationToken.None);
                WaitUntil(() => Has(events, InstallEventKind.StepStatus, UiText.WaitingForInstallerFiles), "the settle wait");

                // Let the first file look land, then run deep into the settle gap before any activity appears.
                int seen = rig.Processes.Snapshots;
                WaitUntil(() => rig.Processes.Snapshots >= seen + 2, "the first file look");
                Thread.Sleep(400);

                // A COM-started helper shows up for ~100 ms, well into the gap, then goes.
                rig.Processes.Set((77, 600, "IDriver.exe"));
                Thread.Sleep(100);
                Assert.IsFalse(run.IsCompleted, "finished while a helper ran between the two looks");
                Assert.IsFalse(OverlayStarted(events), "the overlay started while a helper ran");
                rig.Processes.Set();
                sinceClear.Start();

                Assert.IsTrue(run.Wait(TimeSpan.FromSeconds(15)));
                Assert.AreEqual(LauncherError.None, run.Result.Error, run.Result.ComponentId);
                long ms = Interlocked.Read(ref endedAt);

                // A full quiet settle must pass AFTER the helper went, not be satisfied by the pre-activity look.
                Assert.IsTrue(ms >= rig.Deps.SettleInterval.TotalMilliseconds - 150, "the settle did not restart after the activity: " + ms + " ms");
                rig.AssertAllSatisfied();
            }
        }

        [TestMethod]
        public void HelperAlive_WithStableFiles_NeverFinishesOnItsOwn_UnderTheShortSettle()
        {
            using (var rig = new Rig())
            using (var cts = new CancellationTokenSource())
            {
                // Many short settle gaps pass with the files untouched: a COM-started helper alone still blocks.
                rig.Deps.SettleInterval = TimeSpan.FromMilliseconds(50);
                rig.Deps.ProcessPollInterval = TimeSpan.FromMilliseconds(10);
                rig.Starter.Pid = 4242;
                rig.Starter.OnStart = () => rig.Processes.Set((77, 600, "IDriver.exe"), (600, 4, "svchost.exe"));
                var events = new Hooked(null);
                Task<InstallRunResult> run = new InstallSession(rig.Deps, rig.Manifest, null).RunAsync(rig.FullPlan(), events, cts.Token);
                WaitUntil(() => Has(events, InstallEventKind.StepStatus, UiText.WaitingForInstallerFiles), "the settle wait");
                int seen = rig.Processes.Snapshots;
                WaitUntil(() => rig.Processes.Snapshots >= seen + 40, "many process polls");
                Thread.Sleep(500);
                Assert.IsFalse(run.IsCompleted);
                Assert.IsFalse(Has(events, InstallEventKind.HandoffEnded), "the hand-off ended while a helper ran");
                Assert.IsFalse(OverlayStarted(events), "the overlay started while a helper ran");

                // The helper goes: now it finishes on its own, promptly.
                rig.Processes.Set((600, 4, "svchost.exe"));
                Assert.IsTrue(run.Wait(TimeSpan.FromSeconds(15)));
                Assert.AreEqual(LauncherError.None, run.Result.Error, run.Result.ComponentId);
                rig.AssertAllSatisfied();
            }
        }

        [TestMethod]
        public void MegaQuota_InTheDownloadPhase_ShowsTheQuotaMessage_BeforeTheInstallerOpens()
        {
            using (var rig = new Rig())
            {
                rig.Downloads.FailFile = "eor-dats.zip";
                rig.Downloads.FailWith = LauncherError.SourceQuotaExceeded;
                rig.Downloads.FailTransport = Transports.Mega;
                var events = new Hooked(null);
                InstallRunResult r = new InstallSession(rig.Deps, rig.Manifest, null).RunAsync(rig.FullPlan(), events, CancellationToken.None).GetAwaiter().GetResult();

                Assert.AreEqual(LauncherError.SourceQuotaExceeded, r.Error);
                Assert.AreEqual("eor-dats", r.ComponentId);
                Assert.AreEqual(Transports.Mega, r.SourceTransport);
                Assert.AreEqual(UiText.MegaQuota, ErrorMessages.For(r.Error, r.SourceTransport));
                StringAssert.EndsWith(UiText.MegaQuota, "and click Retry.");
                Assert.AreEqual("ac-client", r.RetryFromId, "Retry re-runs the plan from the start; the installer download is found in place");
                Assert.IsFalse(rig.Log.Contains("installer-started"), "the installer window must not open after a failed download");
                Assert.IsFalse(Has(events, InstallEventKind.HandoffStarted));
                Assert.IsFalse(Has(events, InstallEventKind.StepStarted), "nothing is installed before every download is verified");
            }
        }
        [TestMethod]
        public void LockedFileAtOverlayTime_WithNothingOfTheInstallerRunning_StaysGameRunning()
        {
            FileStream held = null;
            using (var rig = new Rig())
            {
                var events = new Hooked(e =>
                {
                    if (e.Kind == InstallEventKind.StepStarted && e.ComponentId == "eor-dats" && held == null)
                    {
                        // Empty process table: the installer is gone, so the holder is a game left running.
                        held = new FileStream(rig.Dat, FileMode.Open, FileAccess.Read, FileShare.None);
                    }
                });
                try
                {
                    InstallRunResult r = rig.Run(events);
                    Assert.AreEqual(LauncherError.GameRunning, r.Error);
                    Assert.AreEqual("eor-dats", r.ComponentId);
                    Assert.AreEqual("A game file is in use, probably by a running Asheron's Call. Close the game and click Retry.", ErrorMessages.For(r.Error, r.SourceTransport));
                }
                finally
                {
                    held?.Dispose();
                }
            }
        }

        [TestMethod]
        public void GameClientStartedByTheInstaller_IsNamed_AndThePlayerIsAskedToCloseTheGame()
        {
            using (var rig = new Rig())
            using (var cts = new CancellationTokenSource())
            {
                // The finish page started acclient.exe as the installer's child; the installer itself has exited.
                rig.Deps.SettleTimeout = TimeSpan.FromMilliseconds(200);
                rig.Starter.Pid = 4242;
                rig.Starter.OnStart = () => rig.Processes.Set((5000, 4242, "acclient.exe"));
                var events = new Hooked(null);
                var session = new InstallSession(rig.Deps, rig.Manifest, null);
                Task<InstallRunResult> run = session.RunAsync(rig.FullPlan(), events, cts.Token);
                WaitUntil(() => Has(events, InstallEventKind.StepStatus, "Close Asheron's Call, then click \"I finished\"."), "the close-the-game status");
                StringAssert.Contains(rig.InstallLogLines.Single(l => l.Contains("settle timeout")), "acclient.exe");

                session.CheckInstallNow();
                WaitUntil(() => Has(events, InstallEventKind.HandoffNotDetected, UiText.CloseGameThenFinished), "the close-the-game answer to I finished");
                StringAssert.Contains(rig.InstallLogLines.First(l => l.Contains("I finished refused")), "acclient.exe");
                Assert.IsFalse(OverlayStarted(events));

                cts.Cancel();
                Assert.AreEqual(LauncherError.Cancelled, run.GetAwaiter().GetResult().Error);
            }
        }

        [TestMethod]
        public void RetryAfterStopDuringTheHandoff_WithTheTreeAlive_DoesNotStartASecondInstaller()
        {
            using (var rig = new Rig())
            {
                // The installer is still on its first pages: nothing written yet, its process alive.
                rig.Starter.Pid = 4242;
                rig.Starter.WriteFiles = false;
                rig.Starter.OnStart = () => rig.Processes.Set((4242, 900, "setup-root.exe"));
                var session = new InstallSession(rig.Deps, rig.Manifest, null);
                using (var cts = new CancellationTokenSource())
                {
                    var first = new Hooked(null);
                    Task<InstallRunResult> run = session.RunAsync(rig.FullPlan(), first, cts.Token);
                    WaitUntil(() => Has(first, InstallEventKind.HandoffStarted), "the hand-off");
                    cts.Cancel();
                    Assert.AreEqual(LauncherError.Cancelled, run.GetAwaiter().GetResult().Error);
                }

                var second = new Hooked(null);
                Task<InstallRunResult> retry = session.RunAsync(rig.FullPlan(), second, CancellationToken.None);
                WaitUntil(() => Has(second, InstallEventKind.HandoffStarted), "the resumed hand-off");
                Thread.Sleep(100);
                Assert.AreEqual(1, rig.Log.Count(l => l == "installer-started"), "a second installer was started beside the first");

                // The first installer finishes.
                File.WriteAllBytes(Path.Combine(rig.Ac, "acclient.exe"), Exe);
                File.WriteAllBytes(rig.Dat, StockDat);
                rig.Processes.Set();
                Assert.IsTrue(retry.Wait(TimeSpan.FromSeconds(15)));
                Assert.AreEqual(LauncherError.None, retry.Result.Error, retry.Result.ComponentId);
                Assert.AreEqual(1, rig.Log.Count(l => l == "installer-started"));
                rig.AssertAllSatisfied();
            }
        }

        [TestMethod]
        public void OverlayRetry_WhileTheInstallerTreeRuns_RefusesBeforeWritingTheGameFolder()
        {
            FileStream held = null;
            using (var rig = new Rig())
            {
                var session = new InstallSession(rig.Deps, rig.Manifest, null);
                var events = new Hooked(e =>
                {
                    if (e.Kind == InstallEventKind.StepStarted && e.ComponentId == "eor-dats" && held == null)
                    {
                        rig.Processes.Set((77, 600, "IDriver.exe"));
                        held = new FileStream(rig.Dat, FileMode.Open, FileAccess.Read, FileShare.None);
                    }
                });
                InstallRunResult r;
                try
                {
                    r = session.RunAsync(rig.FullPlan(), events, CancellationToken.None).GetAwaiter().GetResult();
                }
                finally
                {
                    held?.Dispose();
                }

                Assert.AreEqual(LauncherError.InstallerStillRunning, r.Error);
                Assert.AreEqual("eor-dats", r.RetryFromId);

                // Retry from the game data step while a copy of the installer runs again (found by its staged name).
                rig.Processes.Set((9000, 1, "ac-client.exe"));
                List<ManifestComponent> remaining = rig.FullPlan().Skip(1).ToList();
                InstallRunResult retry = session.RunAsync(remaining, new Hooked(null), CancellationToken.None).GetAwaiter().GetResult();
                Assert.AreEqual(LauncherError.InstallerStillRunning, retry.Error);
                Assert.AreEqual("eor-dats", retry.ComponentId);
                CollectionAssert.AreEqual(StockDat, File.ReadAllBytes(rig.Dat), "the game folder was written while the installer ran");
            }
        }

        [TestMethod]
        public void UnreadableProcessList_BlocksTheAutomaticPath_ButIFinishedProceeds_AndLogsIt()
        {
            using (var rig = new Rig())
            {
                rig.Deps.SettleInterval = TimeSpan.FromMilliseconds(20);
                rig.Starter.Pid = 4242;
                rig.Starter.OnStart = () => rig.Processes.Throw = true;
                var events = new Hooked(null);
                var session = new InstallSession(rig.Deps, rig.Manifest, null);
                Task<InstallRunResult> run = session.RunAsync(rig.FullPlan(), events, CancellationToken.None);
                WaitUntil(() => Has(events, InstallEventKind.StepStatus, UiText.WaitingForInstallerFiles), "the settle wait");
                int seen = rig.Processes.Snapshots;
                WaitUntil(() => rig.Processes.Snapshots >= seen + 8, "more polls");
                Assert.IsFalse(OverlayStarted(events), "an unreadable process list let the automatic path go on");

                session.CheckInstallNow();
                Assert.IsTrue(run.Wait(TimeSpan.FromSeconds(15)), "I finished with settled files must not be trapped by an unreadable process list");
                Assert.AreEqual(LauncherError.None, run.Result.Error, run.Result.ComponentId);
                StringAssert.Contains(rig.InstallLogLines.Single(l => l.Contains("proceeding")), "process list could not be read");
                rig.AssertAllSatisfied();
            }
        }
        /// <summary>Starts a run whose installer stays on its first pages (tree alive, nothing written), then stops it.</summary>
        private static InstallSession StartedThenStopped(Rig rig)
        {
            rig.Starter.Pid = 4242;
            rig.Starter.WriteFiles = false;
            rig.Starter.OnStart = () => rig.Processes.Set((4242, 900, "setup-root.exe"));
            var session = new InstallSession(rig.Deps, rig.Manifest, null);
            using (var cts = new CancellationTokenSource())
            {
                var first = new Hooked(null);
                Task<InstallRunResult> run = session.RunAsync(rig.FullPlan(), first, cts.Token);
                WaitUntil(() => Has(first, InstallEventKind.HandoffStarted), "the hand-off");
                cts.Cancel();
                Assert.AreEqual(LauncherError.Cancelled, run.GetAwaiter().GetResult().Error);
            }

            return session;
        }

        [TestMethod]
        public void ResumedInstaller_ClosedBeforeFinishing_FailsPromptly_ThenRetryStartsOneNewInstaller()
        {
            using (var rig = new Rig())
            {
                rig.Deps.ClosedEarlyWindow = TimeSpan.FromMilliseconds(500);
                InstallSession session = StartedThenStopped(rig);

                var second = new Hooked(null);
                Task<InstallRunResult> retry = session.RunAsync(rig.FullPlan(), second, CancellationToken.None);
                WaitUntil(() => Has(second, InstallEventKind.HandoffStarted), "the resumed hand-off");

                // The player closes the installer without finishing it: gone for a whole settle interval.
                int before = rig.Processes.Snapshots;
                Stopwatch gone = Stopwatch.StartNew();
                rig.Processes.Set();
                Assert.IsTrue(retry.Wait(TimeSpan.FromSeconds(5)), "the resumed wait did not notice the installer was gone");
                gone.Stop();
                Assert.IsTrue(gone.Elapsed >= TimeSpan.FromMilliseconds(450), "failed before a whole settle interval of empty polls: " + gone.ElapsedMilliseconds + " ms");
                Assert.IsTrue(gone.Elapsed < TimeSpan.FromMilliseconds(500 + 1500), "took far longer than the settle interval plus a poll: " + gone.ElapsedMilliseconds + " ms");
                Assert.IsTrue(rig.Processes.Snapshots - before > 1, "failed on a single empty poll");
                InstallRunResult r = retry.Result;
                Assert.AreEqual(LauncherError.InstallerClosedEarly, r.Error);
                Assert.AreEqual("ac-client", r.ComponentId);
                Assert.AreEqual("ac-client", r.RetryFromId);
                Assert.AreEqual("The Asheron's Call installer closed before it finished. Click Retry to run it again.", ErrorMessages.For(r.Error, r.SourceTransport));
                Assert.AreEqual(1, rig.Log.Count(l => l == "installer-started"));
                Assert.IsFalse(OverlayStarted(second));

                // Retry: exactly one new installer, which this time runs to the end.
                rig.Starter.WriteFiles = true;
                rig.Starter.OnStart = () => rig.Processes.Set();
                InstallRunResult third = session.RunAsync(rig.FullPlan(), new Hooked(null), CancellationToken.None).GetAwaiter().GetResult();
                Assert.AreEqual(LauncherError.None, third.Error, third.ComponentId);
                Assert.AreEqual(2, rig.Log.Count(l => l == "installer-started"), "the Retry must start exactly one new installer");
                rig.AssertAllSatisfied();
            }
        }

        [TestMethod]
        public void ResumedInstaller_EmptyForOnePoll_ThenBack_DoesNotFail()
        {
            using (var rig = new Rig())
            {
                rig.Deps.ClosedEarlyWindow = TimeSpan.FromMilliseconds(500);
                InstallSession session = StartedThenStopped(rig);

                var second = new Hooked(null);
                Task<InstallRunResult> retry = session.RunAsync(rig.FullPlan(), second, CancellationToken.None);
                WaitUntil(() => Has(second, InstallEventKind.HandoffStarted), "the resumed hand-off");

                // One poll sees nothing: the setup handed off to its engine, which is not a descendant and only
                // shows up a moment later. (A tracked id that vanished is dropped for good, so "back" is the
                // engine, as it would be for real.)
                int seen = rig.Processes.Snapshots;
                rig.Processes.Set();
                WaitUntil(() => rig.Processes.Snapshots >= seen + 1, "one empty poll");
                rig.Processes.Set((77, 600, "IDriver.exe"));
                Thread.Sleep(1500);
                Assert.IsFalse(retry.IsCompleted, "a single empty poll failed the resumed wait: " + (retry.IsCompleted ? retry.Result.Error.ToString() : string.Empty));

                // The installer then finishes normally.
                File.WriteAllBytes(Path.Combine(rig.Ac, "acclient.exe"), Exe);
                File.WriteAllBytes(rig.Dat, StockDat);
                rig.Processes.Set();
                Assert.IsTrue(retry.Wait(TimeSpan.FromSeconds(15)));
                Assert.AreEqual(LauncherError.None, retry.Result.Error, retry.Result.ComponentId);
                Assert.AreEqual(1, rig.Log.Count(l => l == "installer-started"));
                rig.AssertAllSatisfied();
            }
        }

        [TestMethod]
        public void UnreadableProcessListAtRetry_ResumesInsteadOfStartingASecondInstaller()
        {
            using (var rig = new Rig())
            {
                InstallSession session = StartedThenStopped(rig);
                rig.Processes.Throw = true;

                var second = new Hooked(null);
                Task<InstallRunResult> retry = session.RunAsync(rig.FullPlan(), second, CancellationToken.None);
                WaitUntil(() => Has(second, InstallEventKind.HandoffStarted), "the resumed hand-off");
                Thread.Sleep(100);
                Assert.AreEqual(1, rig.Log.Count(l => l == "installer-started"), "an unreadable process list let a second installer start");
                Assert.IsFalse(retry.IsCompleted);

                // The first installer finishes, then the list can be read again and shows nothing.
                File.WriteAllBytes(Path.Combine(rig.Ac, "acclient.exe"), Exe);
                File.WriteAllBytes(rig.Dat, StockDat);
                rig.Processes.Set();
                rig.Processes.Throw = false;
                Assert.IsTrue(retry.Wait(TimeSpan.FromSeconds(15)));
                Assert.AreEqual(LauncherError.None, retry.Result.Error, retry.Result.ComponentId);
                Assert.AreEqual(1, rig.Log.Count(l => l == "installer-started"));
                rig.AssertAllSatisfied();
            }
        }
    }

    [TestClass]
    public class InstallerProcessWatchTests
    {
        [TestMethod]
        public void TracksGrandchildren_AcrossExitedParents_AndDropsTheDead()
        {
            var table = new FakeProcessTable();
            var watch = new InstallerProcessWatch(table, 10, null);
            table.Set((10, 1, "extract.exe"), (11, 10, "setup.exe"));
            Assert.IsTrue(watch.IsRunning());

            // The extractor and setup are gone; setup's child (parent id 11) is still at work.
            table.Set((12, 11, "engine.exe"), (99, 1, "explorer.exe"));
            Assert.IsTrue(watch.IsRunning(), "an orphaned grandchild was missed");
            StringAssert.Contains(watch.LastReason, "engine.exe");

            table.Set((99, 1, "explorer.exe"));
            Assert.IsFalse(watch.IsRunning());

            // A later process that reuses a dead id's parent link is not adopted: 11 was dropped.
            table.Set((13, 11, "unrelated.exe"), (99, 1, "explorer.exe"));
            Assert.IsFalse(watch.IsRunning(), "a dropped id was still tracked");
        }

        [TestMethod]
        public void HelperNames_MatchWholeNamesAnyCase()
        {
            Assert.IsTrue(InstallerProcessWatch.IsHelper("idriver.EXE"));
            Assert.IsTrue(InstallerProcessWatch.IsHelper("ISBEW64.exe"));
            Assert.IsTrue(InstallerProcessWatch.IsHelper("_INS5576._MP"));
            Assert.IsFalse(InstallerProcessWatch.IsHelper("NotIDriver.exe"));
            Assert.IsFalse(InstallerProcessWatch.IsHelper("acclient.exe"));
        }

        [TestMethod]
        public void AFailingProcessList_CountsAsRunning()
        {
            var watch = new InstallerProcessWatch(new ThrowingTable(), 10, null);
            Assert.IsTrue(watch.IsRunning());
        }

        [TestMethod]
        public void RealToolhelpTable_ListsThisProcessWithItsParent()
        {
            int me = Process.GetCurrentProcess().Id;
            ProcessEntry self = ToolhelpProcessTable.Instance.Snapshot().Single(p => p.Pid == me);
            Assert.IsTrue(self.ParentPid > 0);
            Assert.IsFalse(string.IsNullOrEmpty(self.ExeName));
        }

        private sealed class ThrowingTable : IProcessTable
        {
            public IReadOnlyList<ProcessEntry> Snapshot() => throw new System.ComponentModel.Win32Exception(5);
        }
    }
}
