using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DreamWeave.Launcher.App;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Detection;
using DreamWeave.Launcher.Core.Downloads;
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

        [TestMethod]
        public void MessageFor_NamesItsOwnCause_AndOnlyClaimsNothingChangedWhereThatIsTrue()
        {
            StringAssert.Contains(LeakFixPresenter.MessageFor(LauncherError.DiskFull), "disk space");
            StringAssert.Contains(LeakFixPresenter.MessageFor(LauncherError.ElevationDeclined), "declined");
            StringAssert.Contains(LeakFixPresenter.MessageFor(LauncherError.ElevationRequired), "administrator");
            Assert.AreEqual(LeakFixPresenter.MessageFor(LauncherError.ElevationRequired), LeakFixPresenter.MessageFor(LauncherError.ElevatedTaskInvalid));
            Assert.AreEqual(UiText.LeakFixGameRunning, LeakFixPresenter.MessageFor(LauncherError.GameRunning));
            Assert.AreNotEqual(LeakFixPresenter.MessageFor(LauncherError.DiskFull), LeakFixPresenter.MessageFor(LauncherError.ElevationDeclined));

            // The final check can fail after the patch was written, so these must not say the file is untouched.
            foreach (LauncherError code in new[] { LauncherError.GameFilesChanged, LauncherError.Unknown, LauncherError.PatchOutputMismatch, LauncherError.InstallerStillRunning })
            {
                string text = LeakFixPresenter.MessageFor(code);
                Assert.IsFalse(text.IndexOf("nothing was changed", StringComparison.OrdinalIgnoreCase) >= 0, code + ": " + text);
                Assert.AreEqual(UiText.LeakFixGeneric, text, code + " has no dedicated text");
            }
        }

        [TestMethod]
        public void ViewFor_AFailureMessageSurvivesAHiddenState()
        {
            ManifestComponent c = Manifest().Components.Single(x => x.Id == "palette-leak-fix");
            foreach (ComponentState? state in new ComponentState?[] { null, ComponentState.NotApplicable, ComponentState.Unknown, ComponentState.NeedsRepair })
            {
                LeakFixView v = LeakFixPresenter.ViewFor(state, c, false, "failed text");
                Assert.IsFalse(v.Visible, "the line stays hidden for " + state);
                Assert.AreEqual("failed text", v.Message, "but the message to the click stays for " + state);
            }

            Assert.IsNull(LeakFixPresenter.ViewFor(ComponentState.NotApplicable, c, false, null).Message);
        }

        [TestMethod]
        public void Apply_Failure_ThenTheFileChangesUnderneath_KeepsTheMessageVisible()
        {
            using (var f = new Fixture(Original()))
            {
                f.Presenter.RefreshAsync().GetAwaiter().GetResult();
                byte[] other = new byte[70];
                using (FileStream held = InstallData.HoldLikeGame(f.Exe))
                {
                    // After the failed click and before the final look the file becomes one the fix does not fit.
                    f.Presenter.BeforeRefresh = () =>
                    {
                        held.Position = 0;
                        held.Write(other, 0, other.Length);
                        held.Flush();
                        return Task.FromResult(true);
                    };
                    Assert.AreEqual(LauncherError.GameRunning, f.Presenter.ApplyAsync().GetAwaiter().GetResult());
                }

                Assert.AreEqual(ComponentState.NotApplicable, f.Presenter.State);
                LeakFixView v = f.Presenter.View;
                Assert.IsFalse(v.Visible, "nothing to offer for this exe");
                Assert.AreEqual(UiText.LeakFixGameRunning, v.Message, "but the player still sees why the click did nothing");
            }
        }

        [TestMethod]
        public void Apply_StaysBusyThroughTheFinalLook_SoASecondClickCannotStartASecondSession()
        {
            using (var f = new Fixture(Original()))
            {
                f.Presenter.RefreshAsync().GetAwaiter().GetResult();
                var entered = new TaskCompletionSource<bool>();
                var release = new TaskCompletionSource<bool>();
                f.Presenter.BeforeRefresh = async () =>
                {
                    entered.TrySetResult(true);
                    await release.Task.ConfigureAwait(false);
                };

                Task<LauncherError> first = f.Presenter.ApplyAsync();
                Assert.IsTrue(entered.Task.Wait(10000), "the first apply reached the final look");
                Assert.IsTrue(f.Presenter.IsBusy, "still busy: the state has not been re-read yet");
                Assert.AreEqual(ComponentState.Missing, f.Presenter.State, "the stale state a second click would have trusted");
                Assert.AreEqual(CloseDecision.Block, f.Presenter.CloseDecision, "the window must not close before the line is current");

                Task<LauncherError> second = f.Presenter.ApplyAsync();
                Assert.IsTrue(second.Wait(10000));
                Assert.AreEqual(1, Started(f), "the second click started no second session");

                release.SetResult(true);
                Assert.AreEqual(LauncherError.None, first.GetAwaiter().GetResult());
                Assert.AreEqual(ComponentState.Satisfied, f.Presenter.State);
                Assert.IsFalse(f.Presenter.IsBusy);
                Assert.AreEqual(CloseDecision.Allow, f.Presenter.CloseDecision);
                Assert.AreEqual(1, Started(f));
            }
        }

        [TestMethod]
        public void CloseDecision_IsBlock_WhileTheGameFolderIsBeingWritten_AndAllowOnceDone()
        {
            using (var f = new Fixture(Original()))
            {
                var disk = new BlockingDisk();
                f.Deps.Disk = disk;
                f.Presenter.RefreshAsync().GetAwaiter().GetResult();
                Assert.AreEqual(CloseDecision.Allow, f.Presenter.CloseDecision, "idle");
                Assert.AreEqual(InstallPhase.Idle, f.Presenter.Phase);

                Task<LauncherError> apply = f.Presenter.ApplyAsync();
                Assert.IsTrue(disk.Entered.Wait(10000), "the patcher is mid-write (it checks free space after entering the critical phase)");
                Assert.AreEqual(InstallPhase.Critical, f.Presenter.Phase);
                Assert.AreEqual(CloseDecision.Block, f.Presenter.CloseDecision);

                disk.Release.Set();
                Assert.AreEqual(LauncherError.None, apply.GetAwaiter().GetResult());
                Assert.AreEqual(InstallPhase.Idle, f.Presenter.Phase);
                Assert.AreEqual(CloseDecision.Allow, f.Presenter.CloseDecision);
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
            WithScreen(f, attach, new NoStart(), (w, s, p) => body(w, s));
        }

        private static void WithScreen(Fixture f, bool attach, IGameStarter starter, Action<MainWindow, LauncherScreen, LauncherPresenter> body)
        {
            RunSta(() =>
            {
                var presenter = new LauncherPresenter(new LauncherPresenterTests.MemoryCredentials(), starter, NullTelemetrySink.Instance, () => f.Manifest, () => f.Ac, new MemorySlots());
                using (var window = new MainWindow())
                {
                    window.StartPosition = FormStartPosition.Manual;
                    window.Location = new Point(20, 20);
                    window.Show();
                    var screen = new LauncherScreen(presenter, null, attach ? f.Presenter : null);
                    window.ShowScreen(screen);
                    Pump(() => screen.Loaded && (!attach || f.Presenter.State != null || f.Presenter.Component == null));
                    body(window, screen, presenter);
                    window.Close();
                }
            });
        }

        /// <summary>Raises the link's click handler the way a mouse click does (even when the link is disabled, to prove the handler guards itself).</summary>
        private static void Click(LinkLabel link)
        {
            MethodInfo m = typeof(LinkLabel).GetMethod("OnLinkClicked", BindingFlags.Instance | BindingFlags.NonPublic);
            m.Invoke(link, new object[] { new LinkLabelLinkClickedEventArgs(new LinkLabel.Link()) });
        }

        private sealed class BlockingStart : IGameStarter
        {
            public ManualResetEventSlim Release { get; } = new ManualResetEventSlim(false);

            public DreamWeave.Launcher.Core.Launch.StartedClient Start(string acFolder, string args)
            {
                Release.Wait(10000);
                return new DreamWeave.Launcher.Core.Launch.StartedClient(1, IntPtr.Zero);
            }

            public DreamWeave.Launcher.Core.Launch.ClientLockOutcome ReleaseLock(DreamWeave.Launcher.Core.Launch.StartedClient client, CancellationToken cancel) => DreamWeave.Launcher.Core.Launch.ClientLockOutcome.Released;
        }

        private sealed class BlockingDisk : IDiskSpace
        {
            public ManualResetEventSlim Entered { get; } = new ManualResetEventSlim(false);

            public ManualResetEventSlim Release { get; } = new ManualResetEventSlim(false);

            public long GetFreeBytes(string directory)
            {
                Entered.Set();
                Release.Wait(10000);
                return long.MaxValue / 4;
            }
        }

        private static int Started(Fixture f)
        {
            return f.Sink.Lines.Count(l => l.StartsWith(TelemetrySteps.PaletteLeakFix + ":Started", StringComparison.Ordinal));
        }

        private static bool PlayAllEnabled(LauncherScreen s) => ((Control)s.DefaultButton).Enabled;
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

        [TestMethod]
        public void Screen_Click_PatchesTheExe_AndShowsTheCheckLine()
        {
            using (var f = new Fixture(Original()))
            {
                WithScreen(f, true, (w, s) =>
                {
                    Assert.IsTrue(s.LeakFixLink.Visible);
                    Click(s.LeakFixLink);
                    Pump(() => !f.Presenter.IsBusy);

                    CollectionAssert.AreEqual(Patched(), File.ReadAllBytes(f.Exe), "the click applied the fix");
                    Assert.IsFalse(s.LeakFixLink.Visible);
                    Assert.IsTrue(s.LeakFixText.Visible);
                    Assert.AreEqual(UiText.LeakFixCheck + " " + UiText.LeakFixInPlace, s.LeakFixText.Text);
                    Assert.IsTrue(PlayAllEnabled(s), "Play is back");
                });
            }
        }

        [TestMethod]
        public void Screen_WhileApplying_BothPlayButtonsAndTheLinkAreDisabled()
        {
            using (var f = new Fixture(Original()))
            {
                var entered = new TaskCompletionSource<bool>();
                var release = new TaskCompletionSource<bool>();
                WithScreen(f, true, (w, s) =>
                {
                    f.Presenter.BeforeRefresh = async () =>
                    {
                        entered.TrySetResult(true);
                        await release.Task;
                    };
                    Assert.IsTrue(s.Rows.All(r => r.PlayEnabled) && PlayAllEnabled(s), "positive control: Play is enabled before the click");
                    Click(s.LeakFixLink);
                    Pump(() => entered.Task.IsCompleted);

                    Assert.IsTrue(f.Presenter.IsBusy);
                    Assert.IsTrue(s.Rows.All(r => !r.PlayEnabled), "the row Play buttons are disabled while the fix is written");
                    Assert.IsFalse(PlayAllEnabled(s), "Play all is disabled while the fix is written");
                    Assert.IsFalse(s.LeakFixLink.Enabled);
                    Assert.AreEqual(UiText.LeakFixApplying, s.LeakFixLink.Text);

                    release.SetResult(true);
                    Pump(() => !f.Presenter.IsBusy);
                    Assert.IsTrue(s.Rows.All(r => r.PlayEnabled) && PlayAllEnabled(s), "Play is back afterwards");
                });
            }
        }

        [TestMethod]
        public void Screen_HeldExe_ShowsTheRedMessage_AndKeepsTheLinkEnabled()
        {
            using (var f = new Fixture(Original()))
            using (InstallData.HoldLikeGame(f.Exe))
            {
                WithScreen(f, true, (w, s) =>
                {
                    Click(s.LeakFixLink);
                    Pump(() => !f.Presenter.IsBusy);

                    Assert.IsTrue(s.LeakFixText.Visible);
                    Assert.AreEqual(UiText.LeakFixGameRunning, s.LeakFixText.Text);
                    Assert.AreEqual(Theme.Err, s.LeakFixText.ForeColor);
                    Assert.IsTrue(s.LeakFixLink.Visible);
                    Assert.IsTrue(s.LeakFixLink.Enabled, "the player can try again");
                    Assert.AreEqual(UiText.LeakFixApply, s.LeakFixLink.Text);
                });
            }
        }

        [TestMethod]
        public void Screen_ExeTheFixDoesNotFit_HidesBothControls()
        {
            byte[] other = Original();
            other[0] ^= 0xFF;
            using (var f = new Fixture(other))
            {
                WithScreen(f, true, (w, s) =>
                {
                    Assert.IsFalse(s.LeakFixLink.Visible);
                    Assert.IsFalse(s.LeakFixText.Visible);
                });
            }
        }

        [TestMethod]
        public void Screen_DoubleClick_RecordsExactlyOneStartedStep()
        {
            using (var f = new Fixture(Original()))
            {
                WithScreen(f, true, (w, s) =>
                {
                    Click(s.LeakFixLink);
                    Click(s.LeakFixLink);
                    Pump(() => !f.Presenter.IsBusy);

                    Assert.AreEqual(1, Started(f));
                    CollectionAssert.AreEqual(Patched(), File.ReadAllBytes(f.Exe));
                });
            }
        }

        [TestMethod]
        public void Screen_WhileAClientIsStarting_TheLinkIsDisabled()
        {
            using (var f = new Fixture(Original()))
            {
                var starter = new BlockingStart();
                WithScreen(f, true, starter, (w, s, p) =>
                {
                    Assert.IsTrue(s.LeakFixLink.Enabled, "positive control");
                    Task<SlotResult> play = p.PlaySlotAsync(0, new[] { new SlotInput("hero", "pw"), new SlotInput(string.Empty, string.Empty), new SlotInput(string.Empty, string.Empty) }, false);
                    Pump(() => p.IsStarting);
                    Assert.IsTrue(p.IsStarting);
                    s.RefreshState();

                    Assert.IsFalse(s.LeakFixLink.Enabled, "no write while a client is starting");
                    starter.Release.Set();
                    play.GetAwaiter().GetResult();
                    s.RefreshState();
                    Assert.IsTrue(s.LeakFixLink.Enabled);
                });
            }
        }

        [TestMethod]
        public void Screen_ClickWhileAClientIsStarting_AppliesNothing()
        {
            using (var f = new Fixture(Original()))
            {
                var starter = new BlockingStart();
                WithScreen(f, true, starter, (w, s, p) =>
                {
                    Task<SlotResult> play = p.PlaySlotAsync(0, new[] { new SlotInput("hero", "pw"), new SlotInput(string.Empty, string.Empty), new SlotInput(string.Empty, string.Empty) }, false);
                    Pump(() => p.IsStarting);
                    Click(s.LeakFixLink);
                    Pump(() => false, 200);

                    Assert.IsFalse(f.Presenter.IsBusy);
                    Assert.AreEqual(0, Started(f), "the handler refused");
                    CollectionAssert.AreEqual(Original(), File.ReadAllBytes(f.Exe), "the game's exe was not written while it starts");
                    starter.Release.Set();
                    play.GetAwaiter().GetResult();
                });
            }
        }
    }
}