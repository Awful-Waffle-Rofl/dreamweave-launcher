using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DreamWeave.Launcher.App;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.State;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.App
{
    [TestClass]
    public class LauncherSlotsTests
    {
        private sealed class MemorySlots : ISlotAccountStore
        {
            public List<string> Saved { get; set; } = new List<string>();

            public int Saves { get; private set; }

            public IReadOnlyList<string> Load() => new List<string>(Saved);

            public void Save(IReadOnlyList<string> accounts)
            {
                Saved = new List<string>(accounts);
                Saves++;
            }
        }

        /// <summary>Starts nothing; fails any start whose arguments contain <see cref="FailFor"/>.</summary>
        private sealed class ScriptedStarter : IGameStarter
        {
            public List<string> Args { get; } = new List<string>();

            public string FailFor { get; set; }

            public DreamWeave.Launcher.Core.Launch.StartedClient Start(string acFolder, string args)
            {
                if (FailFor != null && args.Contains(FailFor))
                {
                    throw new LauncherException(LauncherError.ClientRequiresElevation, "scripted");
                }

                Args.Add(args);
                return new DreamWeave.Launcher.Core.Launch.StartedClient(Args.Count, IntPtr.Zero);
            }

            public DreamWeave.Launcher.Core.Launch.ClientLockOutcome ReleaseLock(DreamWeave.Launcher.Core.Launch.StartedClient client, System.Threading.CancellationToken cancel) => DreamWeave.Launcher.Core.Launch.ClientLockOutcome.Released;
        }

        private static LauncherPresenter Presenter(IGameStarter starter, LauncherPresenterTests.MemoryCredentials creds, ISlotAccountStore slots)
        {
            return new LauncherPresenter(creds, starter, NullTelemetrySink.Instance, () => AppFixtures.Sample(), () => @"C:\Games\AC", slots);
        }

        private static List<SlotInput> Rows(params string[] accountPassword)
        {
            var rows = new List<SlotInput>();
            for (int i = 0; i < accountPassword.Length; i += 2)
            {
                rows.Add(new SlotInput(accountPassword[i], accountPassword[i + 1]));
            }

            return rows;
        }

        [TestMethod]
        public void SlotMapping_PlayingARow_SavesEveryRowName_InSlotOrder()
        {
            var slots = new MemorySlots();
            LauncherPresenter p = Presenter(new ScriptedStarter(), new LauncherPresenterTests.MemoryCredentials(), slots);

            SlotResult r = p.PlaySlotAsync(1, Rows("mainacct", string.Empty, "altone", "pw1", string.Empty, string.Empty), false).GetAwaiter().GetResult();

            Assert.AreEqual(1, r.Slot);
            Assert.AreEqual(LauncherError.None, r.Result.Error);
            CollectionAssert.AreEqual(new[] { "mainacct", "altone", string.Empty }, slots.Saved.ToArray());
        }

        [TestMethod]
        public void SlotMapping_LoadFillsEachRow_WithItsOwnSavedPassword()
        {
            var creds = new LauncherPresenterTests.MemoryCredentials();
            creds.Save("mainacct", "pw-main");
            creds.Save("alttwo", "pw-two");
            var slots = new MemorySlots { Saved = new List<string> { "mainacct", string.Empty, "alttwo" } };
            LauncherPresenter p = Presenter(new ScriptedStarter(), creds, slots);

            IReadOnlyList<SlotFill> fills = p.LoadSlotsAsync().GetAwaiter().GetResult();

            Assert.AreEqual(LauncherPresenter.SlotCount, fills.Count);
            Assert.AreEqual("mainacct", fills[0].Account);
            Assert.AreEqual("pw-main", fills[0].SavedPassword);
            Assert.AreEqual(string.Empty, fills[1].Account);
            Assert.IsNull(fills[1].SavedPassword);
            Assert.AreEqual("alttwo", fills[2].Account);
            Assert.AreEqual("pw-two", fills[2].SavedPassword);
            Assert.AreEqual("mainacct", p.FilledForSlot(0));
            Assert.IsNull(p.FilledForSlot(1));
            Assert.AreEqual("alttwo", p.FilledForSlot(2));
        }

        [TestMethod]
        public void SlotMapping_WithNoRowNamesYet_TheFirstSavedAccountGoesToMain()
        {
            var creds = new LauncherPresenterTests.MemoryCredentials();
            creds.Save("veteran", "pw");
            LauncherPresenter p = Presenter(new ScriptedStarter(), creds, new MemorySlots());

            IReadOnlyList<SlotFill> fills = p.LoadSlotsAsync().GetAwaiter().GetResult();

            Assert.AreEqual("veteran", fills[0].Account);
            Assert.AreEqual("pw", fills[0].SavedPassword);
            Assert.AreEqual(string.Empty, fills[1].Account);
            Assert.AreEqual(string.Empty, fills[2].Account);
        }

        [TestMethod]
        public void SavedPassword_IsForgotten_PerRow()
        {
            LauncherPresenter p = Presenter(new ScriptedStarter(), new LauncherPresenterTests.MemoryCredentials(), new MemorySlots());
            p.MarkFilled(0, "alice");
            p.MarkFilled(2, "carol");

            Assert.IsTrue(p.AccountChanged(0, "alicex"), "editing Main clears Main's saved password");
            Assert.IsNull(p.FilledForSlot(0));
            Assert.AreEqual("carol", p.FilledForSlot(2), "another row's saved password is untouched");
            Assert.IsFalse(p.AccountChanged(2, "CAROL"));
        }

        [TestMethod]
        public void DuplicateAccount_IsRejectedInline_CaseInsensitively_AndNotLaunched()
        {
            var starter = new ScriptedStarter();
            LauncherPresenter p = Presenter(starter, new LauncherPresenterTests.MemoryCredentials(), new MemorySlots());
            List<SlotInput> rows = Rows("Hero", "pw1", "HERO", "pw2", "other", "pw3");

            string[] errors = p.ValidateSlots(rows);
            Assert.IsNull(errors[0], "the first use of a name is fine");
            Assert.AreEqual(UiText.DuplicateAccount, errors[1]);
            Assert.IsNull(errors[2]);

            SlotResult r = p.PlaySlotAsync(1, rows, false).GetAwaiter().GetResult();
            Assert.AreEqual(LauncherError.InvalidCredentials, r.Result.Error);
            Assert.AreEqual(UiText.DuplicateAccount, r.Result.Message);
            Assert.AreEqual(0, starter.Args.Count, "a duplicate row must not start a client");
        }

        [TestMethod]
        public void DuplicateAccount_IsFlagged_EvenBeforeItsPasswordIsTyped()
        {
            LauncherPresenter p = Presenter(new ScriptedStarter(), new LauncherPresenterTests.MemoryCredentials(), new MemorySlots());
            string[] errors = p.ValidateSlots(Rows("hero", "pw", "Hero", string.Empty, string.Empty, string.Empty));
            Assert.AreEqual(UiText.DuplicateAccount, errors[1]);
        }

        [TestMethod]
        public void PlayAll_SkipsRowsThatAreNotFullyFilled_AndStartsTheRestInOrder()
        {
            var starter = new ScriptedStarter();
            var waits = new List<TimeSpan>();
            LauncherPresenter p = Presenter(starter, new LauncherPresenterTests.MemoryCredentials(), new MemorySlots());

            IReadOnlyList<SlotResult> results = p.PlayAllAsync(
                Rows("mainacct", "pw-main", "altone", string.Empty, "alttwo", "pw-two"),
                false,
                null,
                null,
                (t, c) =>
                {
                    waits.Add(t);
                    return Task.FromResult(0);
                }).GetAwaiter().GetResult();

            Assert.AreEqual(2, starter.Args.Count, "the half-filled Alt 1 row is skipped");
            StringAssert.Contains(starter.Args[0], "mainacct");
            StringAssert.Contains(starter.Args[1], "alttwo");
            CollectionAssert.AreEqual(new[] { 0, 2 }, results.Select(x => x.Slot).ToArray());
            Assert.IsTrue(results.All(x => x.Result.Error == LauncherError.None));
            Assert.AreEqual(1, waits.Count, "one pause, between the two starts");
            Assert.AreEqual(TimeSpan.FromSeconds(3), waits[0]);
            Assert.IsFalse(p.IsStarting, "Play all releases the starting flag when done");
        }

        [TestMethod]
        public void PlayAll_WithOnlyOneFilledRow_DoesNotPause()
        {
            var starter = new ScriptedStarter();
            int waits = 0;
            LauncherPresenter p = Presenter(starter, new LauncherPresenterTests.MemoryCredentials(), new MemorySlots());

            p.PlayAllAsync(Rows(string.Empty, string.Empty, "altone", "pw", string.Empty, string.Empty), false, null, null, (t, c) =>
            {
                waits++;
                return Task.FromResult(0);
            }).GetAwaiter().GetResult();

            Assert.AreEqual(1, starter.Args.Count);
            Assert.AreEqual(0, waits);
        }

        [TestMethod]
        public void PlayAll_ContinuesAfterOneRowFails_AndReportsEachRow()
        {
            var starter = new ScriptedStarter { FailFor = "altone" };
            var reported = new List<SlotResult>();
            LauncherPresenter p = Presenter(starter, new LauncherPresenterTests.MemoryCredentials(), new MemorySlots());

            IReadOnlyList<SlotResult> results = p.PlayAllAsync(
                Rows("mainacct", "pw-main", "altone", "pw-one", "alttwo", "pw-two"),
                false,
                new SyncProgress(reported.Add),
                TimeSpan.Zero).GetAwaiter().GetResult();

            Assert.AreEqual(3, results.Count);
            Assert.AreEqual(LauncherError.None, results[0].Result.Error);
            Assert.AreEqual(LauncherError.ClientRequiresElevation, results[1].Result.Error);
            Assert.AreEqual(ErrorMessages.For(LauncherError.ClientRequiresElevation), results[1].Result.Message);
            Assert.AreEqual(LauncherError.None, results[2].Result.Error, "a failed row must not stop the rows after it");
            Assert.AreEqual(2, starter.Args.Count);
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, reported.Select(x => x.Slot).ToArray());
            Assert.IsFalse(results.Any(x => x.Result.Message != null && (x.Result.Message.Contains("pw-main") || x.Result.Message.Contains("pw-one") || x.Result.Message.Contains("pw-two"))), "no message carries a password");
        }

        [TestMethod]
        public void PlayAll_RejectsADuplicateRow_AndStillStartsTheOthers()
        {
            var starter = new ScriptedStarter();
            LauncherPresenter p = Presenter(starter, new LauncherPresenterTests.MemoryCredentials(), new MemorySlots());

            IReadOnlyList<SlotResult> results = p.PlayAllAsync(Rows("hero", "pw1", "HERO", "pw2", "other", "pw3"), false, null, TimeSpan.Zero).GetAwaiter().GetResult();

            Assert.AreEqual(2, starter.Args.Count);
            Assert.AreEqual(LauncherError.InvalidCredentials, results[1].Result.Error);
            Assert.AreEqual(UiText.DuplicateAccount, results[1].Result.Message);
            Assert.AreEqual(LauncherError.None, results[2].Result.Error);
        }

        [TestMethod]
        public void PlayAll_RemembersEachStartedRowsPassword_UnderItsOwnAccount()
        {
            var creds = new LauncherPresenterTests.MemoryCredentials();
            LauncherPresenter p = Presenter(new ScriptedStarter(), creds, new MemorySlots());

            p.PlayAllAsync(Rows("mainacct", "pw-main", "altone", "pw-one", string.Empty, string.Empty), true, null, TimeSpan.Zero).GetAwaiter().GetResult();

            Assert.AreEqual("pw-main", creds.Get("mainacct"));
            Assert.AreEqual("pw-one", creds.Get("altone"));
        }

        [TestMethod]
        public void PlayAll_WhileAnUpdateBlocksPlay_StartsNothing()
        {
            var starter = new ScriptedStarter();
            LauncherPresenter p = Presenter(starter, new LauncherPresenterTests.MemoryCredentials(), new MemorySlots());
            p.Update = UpdateGate.Evaluate(
                MandatoryUpdateManifest(),
                DreamWeave.Launcher.Core.Update.SemVersion.Parse("1.0.0", "running"));
            Assert.IsFalse(p.CanPlay);

            IReadOnlyList<SlotResult> results = p.PlayAllAsync(Rows("a", "b", string.Empty, string.Empty, string.Empty, string.Empty), false, null, TimeSpan.Zero).GetAwaiter().GetResult();

            Assert.AreEqual(0, starter.Args.Count);
            Assert.AreEqual(LauncherError.Cancelled, results.Single().Result.Error);
        }

        [TestMethod]
        public void PlayAll_SavesNamesBeforeTheFirstStart_AndBlocksOtherStartsDuringTheGap()
        {
            var starter = new ScriptedStarter();
            var slots = new MemorySlots();
            LauncherPresenter p = Presenter(starter, new LauncherPresenterTests.MemoryCredentials(), slots);
            bool checkedInGap = false;

            p.PlayAllAsync(Rows("mainacct", "pw1", "altone", "pw2", string.Empty, string.Empty), false, null, null, (t, c) =>
            {
                CollectionAssert.AreEqual(new[] { "mainacct", "altone", string.Empty }, slots.Saved.ToArray(), "names are saved before the batch runs on");
                Assert.IsTrue(p.IsStarting, "still starting inside the gap");
                Assert.IsFalse(p.CanPlay, "no other start may begin inside the gap");
                Assert.AreEqual(LauncherError.Cancelled, p.PlayAllAsync(Rows("x", "y", string.Empty, string.Empty, string.Empty, string.Empty), false, null, TimeSpan.Zero).GetAwaiter().GetResult().Single().Result.Error);
                Assert.AreEqual(LauncherError.Cancelled, p.PlaySlotAsync(0, Rows("x", "y", string.Empty, string.Empty, string.Empty, string.Empty), false).GetAwaiter().GetResult().Result.Error);
                checkedInGap = true;
                return Task.FromResult(0);
            }).GetAwaiter().GetResult();

            Assert.IsTrue(checkedInGap);
            Assert.AreEqual(2, starter.Args.Count, "only the batch rows started");
            Assert.IsFalse(p.IsStarting);
        }

        [TestMethod]
        public void PlaySlot_SavesNamesBeforeTheClientStarts()
        {
            var slots = new MemorySlots();
            List<string> seenAtStart = null;
            var starter = new HookStarter(() => seenAtStart = new List<string>(slots.Saved));
            LauncherPresenter p = Presenter(starter, new LauncherPresenterTests.MemoryCredentials(), slots);

            p.PlaySlotAsync(1, Rows("mainacct", string.Empty, "altone", "pw1", string.Empty, string.Empty), false).GetAwaiter().GetResult();

            Assert.IsNotNull(seenAtStart, "the client started");
            CollectionAssert.AreEqual(new[] { "mainacct", "altone", string.Empty }, seenAtStart.ToArray(), "the store already held the names when the starter ran");
            Assert.IsFalse(p.IsStarting);
        }

        private sealed class BlockingSlots : ISlotAccountStore
        {
            public System.Threading.ManualResetEventSlim InSave { get; } = new System.Threading.ManualResetEventSlim(false);

            public System.Threading.ManualResetEventSlim Release { get; } = new System.Threading.ManualResetEventSlim(false);

            public IReadOnlyList<string> Load() => new List<string>();

            public void Save(IReadOnlyList<string> accounts)
            {
                InSave.Set();
                Release.Wait(TimeSpan.FromSeconds(10));
            }
        }

        [TestMethod]
        public void StartInFlight_IsTrue_WhileTheNamesAreBeingSaved_ForPlayOneAndPlayAll()
        {
            foreach (bool all in new[] { false, true })
            {
                var slots = new BlockingSlots();
                var starter = new ScriptedStarter();
                LauncherPresenter p = Presenter(starter, new LauncherPresenterTests.MemoryCredentials(), slots);
                List<SlotInput> rows = Rows("mainacct", "pw", string.Empty, string.Empty, string.Empty, string.Empty);

                Task run = all ? (Task)p.PlayAllAsync(rows, false, null, TimeSpan.Zero) : p.PlaySlotAsync(0, rows, false);
                Assert.IsTrue(slots.InSave.Wait(TimeSpan.FromSeconds(10)), "the save began");
                Assert.IsTrue(p.IsStartInFlight, (all ? "Play all" : "Play one") + ": a close now must wait");
                Assert.AreEqual(0, starter.Args.Count, "the client has not started yet");

                slots.Release.Set();
                run.GetAwaiter().GetResult();
                Assert.AreEqual(1, starter.Args.Count);
                Assert.IsFalse(p.IsStartInFlight);
            }
        }

        private sealed class HookStarter : IGameStarter
        {
            private readonly Action _onStart;

            public HookStarter(Action onStart)
            {
                _onStart = onStart;
            }

            public DreamWeave.Launcher.Core.Launch.StartedClient Start(string acFolder, string args)
            {
                _onStart();
                return new DreamWeave.Launcher.Core.Launch.StartedClient(1, IntPtr.Zero);
            }

            public DreamWeave.Launcher.Core.Launch.ClientLockOutcome ReleaseLock(DreamWeave.Launcher.Core.Launch.StartedClient client, System.Threading.CancellationToken cancel) => DreamWeave.Launcher.Core.Launch.ClientLockOutcome.Released;
        }

        [TestMethod]
        public void PlayAll_CancelledDuringTheGap_DoesNotStartTheLaterRows()
        {
            var starter = new ScriptedStarter();
            LauncherPresenter p = Presenter(starter, new LauncherPresenterTests.MemoryCredentials(), new MemorySlots());

            IReadOnlyList<SlotResult> results = p.PlayAllAsync(Rows("a1", "p1", "a2", "p2", "a3", "p3"), false, null, null, (t, c) =>
            {
                p.CancelPending();
                return Task.FromResult(0);
            }).GetAwaiter().GetResult();

            Assert.AreEqual(1, starter.Args.Count, "the row that already started stays started; the rest are dropped");
            Assert.AreEqual(1, results.Count);
            Assert.IsFalse(p.IsStarting);
            Assert.IsFalse(p.IsStartInFlight);
        }

        [TestMethod]
        public void PlayAll_WithNoFilledRow_ReturnsNoResults()
        {
            var starter = new ScriptedStarter();
            LauncherPresenter p = Presenter(starter, new LauncherPresenterTests.MemoryCredentials(), new MemorySlots());
            Assert.AreEqual(0, p.PlayAllAsync(Rows("a", string.Empty, string.Empty, "p", string.Empty, string.Empty), false, null, TimeSpan.Zero).GetAwaiter().GetResult().Count);
            Assert.AreEqual(0, starter.Args.Count);
        }

        [TestMethod]
        public void PlaySlot_IsGatedByAMandatoryUpdate()
        {
            var starter = new ScriptedStarter();
            LauncherPresenter p = Presenter(starter, new LauncherPresenterTests.MemoryCredentials(), new MemorySlots());
            p.Update = UpdateGate.Evaluate(MandatoryUpdateManifest(), DreamWeave.Launcher.Core.Update.SemVersion.Parse("1.0.0", "running"));

            SlotResult r = p.PlaySlotAsync(0, Rows("a", "b", string.Empty, string.Empty, string.Empty, string.Empty), false).GetAwaiter().GetResult();

            Assert.AreEqual(LauncherError.Cancelled, r.Result.Error);
            Assert.AreEqual(0, starter.Args.Count);
        }

        private static DreamWeave.Launcher.Core.Manifests.LauncherManifest MandatoryUpdateManifest()
        {
            DreamWeave.Launcher.Core.Manifests.LauncherManifest m = AppFixtures.Sample();
            m.Launcher = new DreamWeave.Launcher.Core.Manifests.LauncherUpdateInfo { Version = "2.0.0", MinVersion = "2.0.0", DownloadPage = "https://example.test/download" };
            return m;
        }

        /// <summary>Reports on the calling thread, so a test sees every report before the awaited call returns.</summary>
        private sealed class SyncProgress : IProgress<SlotResult>
        {
            private readonly Action<SlotResult> _onReport;

            public SyncProgress(Action<SlotResult> onReport)
            {
                _onReport = onReport;
            }

            public void Report(SlotResult value) => _onReport(value);
        }

        // ---- state.json ----

        [TestMethod]
        public void StateFile_WrittenBeforeMultiClient_StillLoads_WithNoSlotAccounts()
        {
            using (var dir = new TempDir())
            {
                var paths = new AppPaths(dir.Path);
                Directory.CreateDirectory(paths.DataDir);
                File.WriteAllText(paths.StateFile, "{ \"highestManifestSerial\": 7, \"installId\": \"11111111-2222-3333-4444-555555555555\", \"acFolder\": \"C:\\\\Games\\\\AC\" }");

                var store = StateStore.Open(paths);

                Assert.AreEqual(7L, store.HighestManifestSerial);
                Assert.IsNotNull(store.Read(s => s.SlotAccounts));
                Assert.AreEqual(0, store.Read(s => s.SlotAccounts.Count));
                Assert.AreEqual(0, new StateSlotAccounts(store).Load().Count);
            }
        }

        [TestMethod]
        public void StateFile_SlotAccounts_RoundTrip_AndOldFieldsSurvive()
        {
            using (var dir = new TempDir())
            {
                var paths = new AppPaths(dir.Path);
                Directory.CreateDirectory(paths.DataDir);
                File.WriteAllText(paths.StateFile, "{ \"highestManifestSerial\": 7, \"installId\": \"11111111-2222-3333-4444-555555555555\", \"acFolder\": \"C:\\\\Games\\\\AC\" }");
                var store = StateStore.Open(paths);

                new StateSlotAccounts(store).Save(new[] { "mainacct", string.Empty, "alttwo" });

                var reopened = StateStore.Open(paths);
                CollectionAssert.AreEqual(new[] { "mainacct", string.Empty, "alttwo" }, new StateSlotAccounts(reopened).Load().ToArray());
                Assert.AreEqual(@"C:\Games\AC", reopened.Read(s => s.AcFolder));
                Assert.AreEqual(7L, reopened.HighestManifestSerial);
            }
        }

        [TestMethod]
        public void StateFile_NullSlotAccounts_IsTreatedAsEmpty()
        {
            using (var dir = new TempDir())
            {
                var paths = new AppPaths(dir.Path);
                Directory.CreateDirectory(paths.DataDir);
                File.WriteAllText(paths.StateFile, "{ \"installId\": \"11111111-2222-3333-4444-555555555555\", \"slotAccounts\": null }");

                var store = StateStore.Open(paths);

                Assert.AreEqual(0, new StateSlotAccounts(store).Load().Count);
            }
        }
    }
}
