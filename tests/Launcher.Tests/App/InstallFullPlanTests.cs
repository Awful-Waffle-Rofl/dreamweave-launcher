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
    /// A full first-run plan (installer, game data overlay, optional patch) through the real
    /// InstallSession, ZipOverlayInstaller, BytePatcher and evaluator. Small fixture files stand in
    /// for the real ones; the installer fake drops the stock exe and stock dats like the retail setup.
    /// </summary>
    [TestClass]
    public class InstallFullPlanTests
    {
        private static readonly byte[] InstallerBytes = { 1, 2, 3, 4, 5 };
        private static readonly byte[] Exe = Enumerable.Range(0, 64).Select(i => (byte)(i + 1)).ToArray();
        private static readonly byte[] StockDat = InstallData.Bytes("stock retail dat");
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

        private sealed class Starter : IProcessStarter
        {
            private readonly string _folder;

            public Starter(string folder)
            {
                _folder = folder;
            }

            public IStartedProcess Start(ProcessStartInfo startInfo)
            {
                // The retail setup: final exe (same bytes as the overlay's) plus the stock dats.
                File.WriteAllBytes(Path.Combine(_folder, "acclient.exe"), Exe);
                File.WriteAllBytes(Path.Combine(_folder, "client_portal.dat"), StockDat);
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

        private sealed class LateWriterEvents : IProgress<InstallEvent>
        {
            private readonly ConcurrentQueue<InstallEvent> _events = new ConcurrentQueue<InstallEvent>();
            private readonly string _folder;
            private bool _pending;

            public LateWriterEvents(string folder, bool lateWrite)
            {
                _folder = folder;
                _pending = lateWrite;
            }

            public InstallEvent[] All => _events.ToArray();

            public void Report(InstallEvent value)
            {
                _events.Enqueue(value);
                if (_pending && value.Kind == InstallEventKind.StepDone && value.ComponentId == "eor-dats")
                {
                    _pending = false;
                    File.WriteAllBytes(Path.Combine(_folder, "client_portal.dat"), StockDat);
                }
            }
        }

        [TestMethod]
        public void FullSelectedPlan_RunsEveryStepInOrder_AndEndsSatisfied()
        {
            RunFullPlan(false);
        }

        [TestMethod]
        public void InstallerStillWritingStockDats_AfterTheOverlay_IsRepairedBeforeDone()
        {
            // Detection fires as soon as the retail exe lands, while the retail setup is still copying
            // its stock dats. Those late writes land after the game data step and undo it.
            RunFullPlan(true);
        }

        private static void RunFullPlan(bool lateWrite)
        {
            using (var dir = new TempDir())
            {
                string ac = Path.Combine(dir.Path, "ac");
                Directory.CreateDirectory(ac);
                string zip = InstallData.Zip(Path.Combine(dir.Path, "dats.zip"), InstallData.Entry("client_portal.dat", EorDat), InstallData.Entry("acclient.exe", Exe));
                LauncherManifest m = Manifest();
                // The overlay's own size/hash describe the archive.
                var state = new StateStore(Path.Combine(dir.Path, "state.json"));
                var fs = new RealAcFileSystem();
                var evaluator = new ComponentEvaluator(new FileVerifier(fs, state));
                string saved = null;
                var deps = new InstallDependencies
                {
                    Paths = new AppPaths(Path.Combine(dir.Path, "root")),
                    Downloads = new Downloader(zip),
                    Disk = new FixedDiskSpace(long.MaxValue / 4),
                    Locator = new AcInstallLocator(new FakeRegistry(), fs, ac),
                    Evaluator = evaluator,
                    ReadSavedFolder = () => saved,
                    SaveFolder = f => saved = f,
                    Installers = new InstallerRunner(new Starter(ac)),
                    PollInterval = TimeSpan.FromMilliseconds(20),
                };

                // What the Components screen would select on a fresh machine.
                var first = evaluator.EvaluateAll(m, null);
                var selection = new ComponentSelection(m, first);
                selection.SetChecked("palette-leak-fix", true);
                List<ManifestComponent> plan = selection.Selected.Select(r => r.Component).ToList();
                CollectionAssert.AreEqual(new[] { "ac-client", "eor-dats", "palette-leak-fix" }, plan.Select(c => c.Id).ToArray(), "the selection must contain every step");

                var events = new LateWriterEvents(ac, lateWrite);
                var session = new InstallSession(deps, m, null);
                InstallRunResult r = session.RunAsync(plan, events, CancellationToken.None).GetAwaiter().GetResult();

                Assert.AreEqual(LauncherError.None, r.Error, r.ComponentId);
                var started = events.All.Where(e => e.Kind == InstallEventKind.StepStarted).Select(e => e.ComponentId).ToArray();
                CollectionAssert.AreEqual(new[] { "ac-client", "eor-dats", "palette-leak-fix" }, started);
                var done = events.All.Where(e => e.Kind == InstallEventKind.StepDone || e.Kind == InstallEventKind.StepSkipped).ToArray();
                Assert.AreEqual(3, done.Length);
                Assert.AreEqual(InstallEventKind.StepDone, done.Single(e => e.ComponentId == "eor-dats").Kind, "the game data step must do work, not be skipped");

                CollectionAssert.AreEqual(EorDat, File.ReadAllBytes(Path.Combine(ac, "client_portal.dat")), "stock dat must have been replaced");
                CollectionAssert.AreEqual(PatchedExe(), File.ReadAllBytes(Path.Combine(ac, "acclient.exe")));

                // The next start: a fresh store over the same state file, as the launcher would open it.
                var next = new ComponentEvaluator(new FileVerifier(fs, new StateStore(Path.Combine(dir.Path, "state.json"))));
                var after = next.EvaluateAll(m, ac);
                foreach (ComponentStatus s in after)
                {
                    Assert.AreEqual(ComponentState.Satisfied, s.State, s.Id + ": " + s.Message);
                }

                Assert.AreEqual(StartRoute.Launcher, RouteDecider.Decide(m, after));
            }
        }
    }
}