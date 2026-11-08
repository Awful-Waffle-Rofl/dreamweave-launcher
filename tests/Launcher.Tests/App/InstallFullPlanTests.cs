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

            public IStartedProcess Start(ProcessStartInfo startInfo)
            {
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

                return new Done();
            }

            private sealed class Done : IStartedProcess
            {
                public bool HasExited => true;

                public int? ExitCode => 0;

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

            public Task<DownloadResult> DownloadAsync(DownloadRequest request, IProgress<ProgressReport> progress, CancellationToken cancellationToken)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationPath));
                byte[] bytes = request.DestinationPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? File.ReadAllBytes(_zip) : InstallerBytes;
                File.WriteAllBytes(request.DestinationPath, bytes);
                return Task.FromResult(new DownloadResult(request.DestinationPath, bytes.Length, Hasher.ComputeSha256Hex(bytes), false));
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
                Deps = new InstallDependencies
                {
                    Paths = new AppPaths(Path.Combine(_dir.Path, "root")),
                    Downloads = new Downloader(zip),
                    Disk = new FixedDiskSpace(long.MaxValue / 4),
                    Locator = new AcInstallLocator(new FakeRegistry(), fs, Ac),
                    Evaluator = Evaluator,
                    Telemetry = Telemetry,
                    ReadSavedFolder = () => Saved,
                    SaveFolder = f => Saved = f,
                    Installers = new InstallerRunner(new Starter(Ac, afterExe)),
                    PollInterval = TimeSpan.FromMilliseconds(20),
                    SettleInterval = TimeSpan.FromMilliseconds(30),
                    VerifyRetryDelay = TimeSpan.Zero,
                };
            }

            public string Ac { get; }

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

        [TestMethod]
        public void SettleWait_TimesOut_AndCarriesOn()
        {
            // A watched file whose stamp keeps changing never settles; after the timeout the run goes on.
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
                rig.Deps.SettleTimeout = TimeSpan.FromMilliseconds(400);
                Stopwatch clock = Stopwatch.StartNew();
                long overlayStartedAtMs = -1;
                var events = new Hooked(e =>
                {
                    if (e.Kind == InstallEventKind.StepStarted && e.ComponentId == "eor-dats")
                    {
                        overlayStartedAtMs = clock.ElapsedMilliseconds;
                    }
                });
                try
                {
                    InstallRunResult r = rig.Run(events);
                    Assert.AreEqual(LauncherError.None, r.Error, r.ComponentId);
                    Assert.IsTrue(overlayStartedAtMs >= 350, "carried on before the timeout: " + overlayStartedAtMs + " ms");
                }
                finally
                {
                    Volatile.Write(ref stop, true);
                }
            }
        }
    }
}