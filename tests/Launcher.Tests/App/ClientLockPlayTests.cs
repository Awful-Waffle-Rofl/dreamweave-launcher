using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DreamWeave.Launcher.App;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Launch;
using DreamWeave.Launcher.Core.Telemetry;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.App
{
    /// <summary>How Play and Play all use the client lock release.</summary>
    [TestClass]
    public class ClientLockPlayTests
    {
        private static LauncherPresenter Presenter(IGameStarter starter)
        {
            return new LauncherPresenter(new LauncherPresenterTests.MemoryCredentials(), starter, NullTelemetrySink.Instance, () => AppFixtures.Sample(), () => @"C:\Games\AC");
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
        public void LockTexts_AreThePlainAsciiOwnerWording()
        {
            Assert.AreEqual("Started, but a second client may be refused. Close the other client to play this one.", UiText.SecondClientMayBeRefused);
            Assert.AreEqual("Starting {0}...", UiText.StartingSlotFormat);
            foreach (char c in UiText.SecondClientMayBeRefused + UiText.StartingSlotFormat)
            {
                Assert.IsTrue(c >= 0x20 && c < 0x7F, "ASCII only");
            }
        }

        // Mutation checked: always passing a null notice (dropping the lockOutcome check) fails this test.
        [TestMethod]
        public void Play_WhenTheLockIsNotReleased_StartsAndCarriesTheRowMessage()
        {
            foreach (ClientLockOutcome failed in new[] { ClientLockOutcome.NotFound, ClientLockOutcome.Failed, ClientLockOutcome.Unsupported, ClientLockOutcome.ClientExited, ClientLockOutcome.HeldElsewhere, ClientLockOutcome.Cancelled })
            {
                var starter = new LauncherPresenterTests.FakeStarter { LockOutcome = failed };
                SlotResult r = Presenter(starter).PlaySlotAsync(1, Rows("main", "pw", "alt", "pw2"), false).GetAwaiter().GetResult();

                Assert.AreEqual(LauncherError.None, r.Result.Error, failed + ": the client still started");
                Assert.AreEqual(UiText.SecondClientMayBeRefused, r.Result.Notice, failed.ToString());
            }
        }

        [TestMethod]
        public void Play_WhenTheLockIsReleased_HasNoRowMessage()
        {
            var starter = new LauncherPresenterTests.FakeStarter();
            SlotResult r = Presenter(starter).PlaySlotAsync(0, Rows("main", "pw"), false).GetAwaiter().GetResult();

            Assert.AreEqual(LauncherError.None, r.Result.Error);
            Assert.IsNull(r.Result.Notice);
        }

        // Mutation checked: releasing a StartedClient rebuilt from the id (new StartedClient(client.Id,
        // client.Handle)) instead of the one Start returned fails this test.
        [TestMethod]
        public void Play_ReleasesExactlyTheClientThatStartReturned()
        {
            var starter = new LauncherPresenterTests.FakeStarter();
            Presenter(starter).PlayAllAsync(Rows("a1", "p1", "a2", "p2"), false, null, TimeSpan.Zero).GetAwaiter().GetResult();

            Assert.AreEqual(2, starter.Released.Count);
            Assert.AreSame(starter.Clients[0], starter.Released[0]);
            Assert.AreSame(starter.Clients[1], starter.Released[1]);
        }

        // Mutation checked: dropping the ReportStatus call before the release fails this test.
        [TestMethod]
        public void Play_ShowsTheRowBeingStarted_WhileTheReleaseWaits()
        {
            var starter = new LauncherPresenterTests.FakeStarter();
            LauncherPresenter p = Presenter(starter);
            var lines = new List<string>();
            string atRelease = null;
            p.StatusChanged = line => lines.Add(line);
            starter.OnRelease = c => atRelease = lines.Count > 0 ? lines[lines.Count - 1] : null;

            p.PlaySlotAsync(1, Rows("main", "pw", "alt", "pw2"), false).GetAwaiter().GetResult();

            Assert.AreEqual("Starting Alt 1...", atRelease, "the line is up before the wait begins");
        }

        // Mutation checked: passing CancellationToken.None to ReleaseLock (ignoring StopWaiting) fails this test.
        [TestMethod]
        public void StopWaiting_CancelsTheReleaseWaitInFlight()
        {
            var starter = new LauncherPresenterTests.FakeStarter();
            LauncherPresenter p = Presenter(starter);
            var entered = new ManualResetEventSlim();
            bool cancelled = false;
            starter.OnRelease = c =>
            {
                entered.Set();
                cancelled = c.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
            };

            Task<SlotResult> run = p.PlaySlotAsync(0, Rows("main", "pw"), false);
            Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(10)));
            p.StopWaiting();
            run.GetAwaiter().GetResult();

            Assert.IsTrue(cancelled, "the window close reached the release wait");
            Assert.IsFalse(p.IsStartInFlight);
        }

        private sealed class GatedStarter : IGameStarter
        {
            public ManualResetEventSlim FirstReleaseEntered { get; } = new ManualResetEventSlim();

            public ManualResetEventSlim LetFirstReleaseFinish { get; } = new ManualResetEventSlim();

            public int Starts;

            public StartedClient Start(string acFolder, string args)
            {
                return new StartedClient(Interlocked.Increment(ref Starts), IntPtr.Zero);
            }

            public ClientLockOutcome ReleaseLock(StartedClient client, CancellationToken cancel)
            {
                if (client.Id == 1)
                {
                    FirstReleaseEntered.Set();
                    LetFirstReleaseFinish.Wait(TimeSpan.FromSeconds(10));
                }

                return ClientLockOutcome.Released;
            }
        }

        // Mutation checked: running the release fire-and-forget (Task.Run without waiting) lets the
        // second client start while the first still holds its lock, and fails this test.
        [TestMethod]
        public void PlayAll_DoesNotStartTheNextClient_UntilThePreviousLockIsReleased()
        {
            var starter = new GatedStarter();
            LauncherPresenter p = Presenter(starter);

            Task<IReadOnlyList<SlotResult>> run = p.PlayAllAsync(Rows("a1", "p1", "a2", "p2"), false, null, TimeSpan.Zero);

            Assert.IsTrue(starter.FirstReleaseEntered.Wait(TimeSpan.FromSeconds(10)), "the first release began");
            Thread.Sleep(300);
            Assert.AreEqual(1, Volatile.Read(ref starter.Starts), "the second client waits for the first release");

            starter.LetFirstReleaseFinish.Set();
            IReadOnlyList<SlotResult> results = run.GetAwaiter().GetResult();

            Assert.AreEqual(2, starter.Starts);
            Assert.AreEqual(2, results.Count);
        }
    }
}
