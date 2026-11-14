using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using DreamWeave.Launcher.App;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Detection;
using DreamWeave.Launcher.Core.Install;
using DreamWeave.Launcher.Core.Manifests;
using DreamWeave.Launcher.Core.State;
using DreamWeave.Launcher.Core.Telemetry;
using DreamWeave.Launcher.Tests.Install;
using DreamWeave.Launcher.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.App
{
    /// <summary>
    /// The memory leak fix line on the launcher window: the state mapping, and the click applying it through
    /// the wizard's own <see cref="InstallSession"/> over real temp files (real evaluator, real BytePatcher).
    /// </summary>
    [TestClass]
    public class LeakFixTests
    {
        private const string Secret = "SecretAccountName";

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
            Buffer.BlockCopy(data, offset, part, 0, count);
            return Hasher.ToHex(part);
        }

        /// <summary>ac-client, eor-dats (lists acclient.exe) and the fix, shaped like the live manifest (ac-client accepts both exe hashes).</summary>
        private static LauncherManifest Manifest(bool withFix = true)
        {
            byte[] o = Original();
            string inHash = InstallData.Hash(o);
            string outHash = InstallData.Hash(Patched());
            var client = new ManifestComponent
            {
                Id = "ac-client",
                Kind = ComponentKinds.Installer,
                Required = true,
                Size = 10,
                SatisfiedWhen = new List<SatisfiedWhenRule> { new SatisfiedWhenRule { Path = "acclient.exe", Sha256 = new List<string> { inHash, outHash } } },
            };
            var dats = new ManifestComponent
            {
                Id = "eor-dats",
                Kind = ComponentKinds.ZipOverlay,
                Required = true,
                Size = 10,
                Files = new List<ManifestFile> { new ManifestFile { Path = "acclient.exe", Size = o.Length, Sha256 = inHash } },
            };
            var fix = new ManifestComponent
            {
                Id = "palette-leak-fix",
                Kind = ComponentKinds.BytePatch,
                Required = false,
                DefaultSelected = true,
                Title = "Asheron's Call Client Memory Leak Fix (optional)",
                SourceLabel = "Two 3-byte changes from github.com/example/leak-fix",
                Target = "acclient.exe",
                InputSha256 = inHash,
                OutputSha256 = outHash,
                Patches = new List<BytePatch>
                {
                    new BytePatch { Offset = 5, Expect = Hex(o, 5, 3), Replace = "909090" },
                    new BytePatch { Offset = 40, Expect = Hex(o, 40, 2), Replace = "CCCC" },
                },
            };
            return withFix ? AppFixtures.Manifest(client, dats, fix) : AppFixtures.Manifest(client, dats);
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
        }

        private sealed class Fixture : IDisposable
        {
            private readonly TempDir _dir = new TempDir();

            public Fixture(byte[] exe, bool withFix = true)
            {
                Ac = Path.Combine(_dir.Path, "ac");
                Directory.CreateDirectory(Ac);
                if (exe != null)
                {
                    File.WriteAllBytes(Exe, exe);
                }

                Manifest = LeakFixTests.Manifest(withFix);
                var fs = new RealAcFileSystem();
                var state = new StateStore(Path.Combine(_dir.Path, "state.json"));
                Evaluator = new ComponentEvaluator(new FileVerifier(fs, state));
                Deps = new InstallDependencies
                {
                    Paths = new AppPaths(Path.Combine(_dir.Path, "root")),
                    Disk = new FixedDiskSpace(long.MaxValue / 4),
                    Evaluator = Evaluator,
                    Telemetry = Sink,
                    VerifyRetryDelay = TimeSpan.Zero,
                };
                Presenter = new LeakFixPresenter(() => Manifest, () => Ac, Deps);
            }

            public string Ac { get; }

            public string Exe => Path.Combine(Ac, "acclient.exe");

            public LauncherManifest Manifest { get; }

            public ComponentEvaluator Evaluator { get; }

            public InstallDependencies Deps { get; }

            public Telemetry Sink { get; } = new Telemetry();

            public LeakFixPresenter Presenter { get; }

            public string Root => _dir.Path;

            public void Dispose() => _dir.Dispose();
        }

        private static ComponentState StateOf(Fixture f, string id)
        {
            return f.Evaluator.EvaluateAll(f.Manifest, f.Ac).Single(s => s.Id == id).State;
        }

        [TestMethod]
        public void ViewFor_MapsEveryComponentState()
        {
            ManifestComponent c = Manifest().Components.Single(x => x.Id == "palette-leak-fix");
            foreach (ComponentState state in Enum.GetValues(typeof(ComponentState)))
            {
                LeakFixView v = LeakFixPresenter.ViewFor(state, c, false, null);
                switch (state)
                {
                    case ComponentState.Satisfied:
                        Assert.IsTrue(v.Visible, "Satisfied");
                        Assert.AreEqual(UiText.LeakFixCheck + " " + UiText.LeakFixInPlace, v.Text);
                        Assert.IsFalse(v.Clickable, "the status is not a link");
                        break;
                    case ComponentState.Missing:
                        Assert.IsTrue(v.Visible, "Missing");
                        Assert.AreEqual(UiText.LeakFixApply, v.Text);
                        Assert.IsTrue(v.Clickable);
                        StringAssert.Contains(v.Tooltip, c.SourceLabel, "the source label is disclosed");
                        break;
                    default:
                        Assert.IsFalse(v.Visible, state + " must hide the line");
                        Assert.IsFalse(v.Clickable, state + " must not be offered");
                        break;
                }
            }
        }

        [TestMethod]
        public void ViewFor_NoStateOrNoComponent_IsHidden()
        {
            ManifestComponent c = Manifest().Components.Single(x => x.Id == "palette-leak-fix");
            Assert.IsFalse(LeakFixPresenter.ViewFor(null, c, false, null).Visible, "not evaluated yet");
            Assert.IsFalse(LeakFixPresenter.ViewFor(ComponentState.Missing, null, false, null).Visible);
        }

        [TestMethod]
        public void ViewFor_Busy_ShowsTheBusyTextAndIsNotClickable()
        {
            ManifestComponent c = Manifest().Components.Single(x => x.Id == "palette-leak-fix");
            LeakFixView v = LeakFixPresenter.ViewFor(ComponentState.Missing, c, true, null);
            Assert.IsTrue(v.Visible);
            Assert.AreEqual(UiText.LeakFixApplying, v.Text);
            Assert.IsFalse(v.Clickable);
        }

        [TestMethod]
        public void ManifestWithoutTheComponent_ShowsNothing()
        {
            using (var f = new Fixture(Original(), withFix: false))
            {
                f.Presenter.RefreshAsync().GetAwaiter().GetResult();
                Assert.IsNull(f.Presenter.Component);
                Assert.IsFalse(f.Presenter.View.Visible);
                Assert.AreEqual(LauncherError.None, f.Presenter.ApplyAsync().GetAwaiter().GetResult(), "there is nothing to apply");
                CollectionAssert.AreEqual(Original(), File.ReadAllBytes(f.Exe));
            }
        }

        [TestMethod]
        public void Refresh_OnUnpatchedExe_OffersTheLink_AndNeverWritesByItself()
        {
            using (var f = new Fixture(Original()))
            {
                f.Presenter.RefreshAsync().GetAwaiter().GetResult();
                Assert.AreEqual(ComponentState.Missing, f.Presenter.State);
                Assert.IsTrue(f.Presenter.View.Clickable);
                CollectionAssert.AreEqual(Original(), File.ReadAllBytes(f.Exe), "looking is not applying");
                Assert.AreEqual(0, f.Sink.Lines.Count, "no step ran");
            }
        }

        [TestMethod]
        public void Refresh_OnPatchedExe_ShowsTheCheck_AndTheLinkDoesNothing()
        {
            using (var f = new Fixture(Patched()))
            {
                f.Presenter.RefreshAsync().GetAwaiter().GetResult();
                Assert.AreEqual(ComponentState.Satisfied, f.Presenter.State);
                Assert.IsFalse(f.Presenter.View.Clickable);
                Assert.AreEqual(LauncherError.None, f.Presenter.ApplyAsync().GetAwaiter().GetResult());
                CollectionAssert.AreEqual(Patched(), File.ReadAllBytes(f.Exe));
                Assert.AreEqual(0, f.Sink.Lines.Count);
            }
        }

        [TestMethod]
        public void Refresh_OnAnUnrecognisedOrMissingExe_HidesTheLine_AndApplyRefuses()
        {
            byte[] other = Original();
            other[0] ^= 0xFF;
            using (var f = new Fixture(other))
            {
                f.Presenter.RefreshAsync().GetAwaiter().GetResult();
                Assert.AreEqual(ComponentState.NotApplicable, f.Presenter.State);
                Assert.IsFalse(f.Presenter.View.Visible);
                f.Presenter.ApplyAsync().GetAwaiter().GetResult();
                CollectionAssert.AreEqual(other, File.ReadAllBytes(f.Exe), "an exe the fix was not made for is left alone");
            }

            using (var f = new Fixture(null))
            {
                f.Presenter.RefreshAsync().GetAwaiter().GetResult();
                Assert.IsFalse(f.Presenter.View.Visible);
            }
        }

        [TestMethod]
        public void Apply_Success_PatchesTheExe_FlipsToSatisfied_AndRecordsTheStep()
        {
            using (var f = new Fixture(Original()))
            {
                f.Presenter.RefreshAsync().GetAwaiter().GetResult();
                LauncherError error = f.Presenter.ApplyAsync().GetAwaiter().GetResult();

                Assert.AreEqual(LauncherError.None, error);
                CollectionAssert.AreEqual(Patched(), File.ReadAllBytes(f.Exe));
                Assert.AreEqual(ComponentState.Satisfied, f.Presenter.State);
                Assert.IsFalse(f.Presenter.IsBusy);
                LeakFixView v = f.Presenter.View;
                Assert.AreEqual(UiText.LeakFixCheck + " " + UiText.LeakFixInPlace, v.Text);
                Assert.IsFalse(v.Clickable);
                Assert.IsNull(v.Message);
                CollectionAssert.Contains(f.Sink.Lines.ToList(), TelemetrySteps.PaletteLeakFix + ":Succeeded:None");
                Assert.AreEqual(1, f.Sink.Lines.Count(l => l.StartsWith(TelemetrySteps.PaletteLeakFix + ":Started", StringComparison.Ordinal)));
                Assert.IsFalse(InstallData.AnyTempLeft(f.Ac), "no temp files left in the game folder");
            }
        }

        [TestMethod]
        public void Apply_Sets_Busy_Before_Its_First_Await()
        {
            using (var f = new Fixture(Original()))
            {
                f.Presenter.RefreshAsync().GetAwaiter().GetResult();
                var task = f.Presenter.ApplyAsync();
                Assert.IsTrue(f.Presenter.IsBusy || task.IsCompleted, "the busy flag is up as soon as the click returns");
                Assert.AreEqual(LauncherError.None, task.GetAwaiter().GetResult());
                Assert.IsFalse(f.Presenter.IsBusy);
            }
        }

        [TestMethod]
        public void Apply_AfterSuccess_NextLauncherStartStillRoutesToTheLauncher()
        {
            using (var f = new Fixture(Original()))
            {
                Assert.AreEqual(StartRoute.Launcher, RouteDecider.Decide(f.Manifest, f.Evaluator.EvaluateAll(f.Manifest, f.Ac)), "before");
                f.Presenter.RefreshAsync().GetAwaiter().GetResult();
                Assert.AreEqual(LauncherError.None, f.Presenter.ApplyAsync().GetAwaiter().GetResult());

                // A fresh evaluator over the same files stands in for the next launcher start.
                var next = new ComponentEvaluator(new FileVerifier(new RealAcFileSystem(), new StateStore(Path.Combine(f.Root, "state2.json"))));
                IReadOnlyList<ComponentStatus> all = next.EvaluateAll(f.Manifest, f.Ac);
                Assert.AreEqual(ComponentState.Satisfied, all.Single(s => s.Id == "ac-client").State);
                Assert.AreEqual(ComponentState.Satisfied, all.Single(s => s.Id == "eor-dats").State, "a patched acclient.exe must not read as NeedsRepair");
                Assert.AreEqual(ComponentState.Satisfied, all.Single(s => s.Id == "palette-leak-fix").State);
                Assert.AreEqual(StartRoute.Launcher, RouteDecider.Decide(f.Manifest, all), "after");
            }
        }

        [TestMethod]
        public void Apply_WhileTheGameHoldsTheExe_ChangesNothing_AndSaysSo_WithoutPaths()
        {
            using (var f = new Fixture(Original()))
            {
                f.Presenter.RefreshAsync().GetAwaiter().GetResult();
                using (InstallData.HoldLikeGame(f.Exe))
                {
                    LauncherError error = f.Presenter.ApplyAsync().GetAwaiter().GetResult();
                    Assert.AreEqual(LauncherError.GameRunning, error);
                }

                Assert.AreEqual(ComponentState.Missing, f.Presenter.State, "still not applied");
                LeakFixView v = f.Presenter.View;
                Assert.IsTrue(v.Clickable, "the player can try again once the game is closed");
                Assert.AreEqual(UiText.LeakFixGameRunning, v.Message);
                AssertNoLeak(v.Message, f);
                CollectionAssert.AreEqual(Original(), File.ReadAllBytes(f.Exe));
                Assert.IsFalse(InstallData.AnyTempLeft(f.Ac));

                // And it works once the game is closed.
                Assert.AreEqual(LauncherError.None, f.Presenter.ApplyAsync().GetAwaiter().GetResult());
                Assert.IsNull(f.Presenter.View.Message, "the old message is cleared by the next click");
                Assert.AreEqual(ComponentState.Satisfied, f.Presenter.State);
            }
        }

        [TestMethod]
        public void MessageFor_EveryErrorCode_HasNoPathUserNameOrRetryButton()
        {
            foreach (LauncherError code in Enum.GetValues(typeof(LauncherError)))
            {
                string text = LeakFixPresenter.MessageFor(code);
                Assert.IsFalse(string.IsNullOrWhiteSpace(text), code.ToString());
                Assert.IsFalse(text.Contains(":\\") || text.Contains("\\\\"), code + " carries a path: " + text);
                Assert.IsFalse(text.IndexOf(Environment.UserName, StringComparison.OrdinalIgnoreCase) >= 0 && Environment.UserName.Length > 2, code + " carries the user name");
                Assert.IsFalse(text.Contains("Retry"), code + " points at a Retry button this window does not have: " + text);
            }
        }

        private static void AssertNoLeak(string text, Fixture f)
        {
            Assert.IsFalse(text.Contains(f.Root), "no temp path");
            Assert.IsFalse(text.Contains(f.Ac), "no game folder path");
            Assert.IsFalse(text.Contains(":\\"), "no drive path");
            Assert.IsFalse(text.Contains(Secret));
        }

        // ---- the screen ----

        private static void RunSta(Action body)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                    body();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (failure != null)
            {
                throw new AssertFailedException("STA body failed: " + failure, failure);
            }
        }

        private static void Pump(Func<bool> until, int maxMs = 5000)
        {
            DateTime end = DateTime.UtcNow.AddMilliseconds(maxMs);
            while (DateTime.UtcNow < end && !until())
            {
                Application.DoEvents();
                Thread.Sleep(15);
            }

            for (int i = 0; i < 8; i++)
            {
                Application.DoEvents();
                Thread.Sleep(15);
            }
        }

        private sealed class NoStart : IGameStarter
        {
            public DreamWeave.Launcher.Core.Launch.StartedClient Start(string acFolder, string args)
            {
                return new DreamWeave.Launcher.Core.Launch.StartedClient(1, IntPtr.Zero);
            }

            public DreamWeave.Launcher.Core.Launch.ClientLockOutcome ReleaseLock(DreamWeave.Launcher.Core.Launch.StartedClient client, CancellationToken cancel) => DreamWeave.Launcher.Core.Launch.ClientLockOutcome.Released;
        }

        private sealed class MemorySlots : ISlotAccountStore
        {
            public IReadOnlyList<string> Load() => new List<string>();

            public void Save(IReadOnlyList<string> accounts)
            {
            }
        }

        private static void WithScreen(Fixture f, bool attach, Action<MainWindow, LauncherScreen> body)
        {
            RunSta(() =>
            {
                var presenter = new LauncherPresenter(new LauncherPresenterTests.MemoryCredentials(), new NoStart(), NullTelemetrySink.Instance, () => f.Manifest, () => f.Ac, new MemorySlots());
                using (var window = new MainWindow())
                {
                    window.StartPosition = FormStartPosition.Manual;
                    window.Location = new Point(20, 20);
                    window.Show();
                    var screen = new LauncherScreen(presenter, null, attach ? f.Presenter : null);
                    window.ShowScreen(screen);
                    Pump(() => screen.Loaded && (!attach || f.Presenter.State != null || f.Presenter.Component == null));
                    body(window, screen);
                    window.Close();
                }
            });
        }

        [TestMethod]
        public void Screen_Unpatched_ShowsTheEnabledLink_AndStaysReachable()
        {
            using (var f = new Fixture(Original()))
            {
                WithScreen(f, true, (w, s) =>
                {
                    Assert.IsTrue(s.LeakFixLink.Visible);
                    Assert.IsTrue(s.LeakFixLink.Enabled);
                    Assert.AreEqual(UiText.LeakFixApply, s.LeakFixLink.Text);
                    Assert.IsFalse(s.LeakFixText.Visible);
                    Assert.IsTrue(s.Footer.Bounds.Bottom <= s.ClientSize.Height, "the button row is inside the card");
                    Assert.IsTrue(w.Card.Parent.ClientRectangle.Contains(w.Card.Bounds), "the card lies inside the body area");
                });
            }
        }

        [TestMethod]
        public void Screen_Patched_ShowsTheCheckLine_AndNoLink()
        {
            using (var f = new Fixture(Patched()))
            {
                WithScreen(f, true, (w, s) =>
                {
                    Assert.IsFalse(s.LeakFixLink.Visible);
                    Assert.IsTrue(s.LeakFixText.Visible);
                    Assert.AreEqual(UiText.LeakFixCheck + " " + UiText.LeakFixInPlace, s.LeakFixText.Text);
                });
            }
        }

        [TestMethod]
        public void Screen_WithoutTheComponent_OrWithoutAPresenter_ShowsNothing()
        {
            using (var f = new Fixture(Original(), withFix: false))
            {
                WithScreen(f, true, (w, s) =>
                {
                    Assert.IsFalse(s.LeakFixLink.Visible);
                    Assert.IsFalse(s.LeakFixText.Visible);
                });
                WithScreen(f, false, (w, s) =>
                {
                    Assert.IsFalse(s.LeakFixLink.Visible);
                    Assert.IsFalse(s.LeakFixText.Visible);
                });
            }
        }
    }
}