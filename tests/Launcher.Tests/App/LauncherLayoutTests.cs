using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using DreamWeave.Launcher.App;
using DreamWeave.Launcher.Core.Manifests;
using DreamWeave.Launcher.Core.Update;
using DreamWeave.Launcher.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.App
{
    /// <summary>
    /// Hosts the real MainWindow and LauncherScreen on an STA thread and checks that every row and the
    /// button row stay reachable. Runs at the machine's DPI only: a 150% scale cannot be simulated here.
    /// </summary>
    [TestClass]
    public class LauncherLayoutTests
    {
        private sealed class MemorySlots : ISlotAccountStore
        {
            public IReadOnlyList<string> Load() => new List<string>();

            public void Save(IReadOnlyList<string> accounts)
            {
            }
        }

        private sealed class NoStart : IGameStarter
        {
            public void Start(string acFolder, string args)
            {
            }
        }

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

        private static LauncherManifest ManifestWithOptionalUpdate()
        {
            LauncherManifest m = AppFixtures.Sample();
            m.Launcher = new LauncherUpdateInfo { Version = "1.1.0", MinVersion = "1.0.0", Url = "https://example.test/x.exe", Sha256 = new string('A', 64), Size = 10 };
            return m;
        }

        private static void WithScreen(string notice, bool banner, Action<MainWindow, LauncherScreen> body, LauncherPresenterTests.MemoryCredentials creds = null)
        {
            RunSta(() =>
            {
                LauncherManifest m = banner ? ManifestWithOptionalUpdate() : AppFixtures.Sample();
                var presenter = new LauncherPresenter(creds ?? new LauncherPresenterTests.MemoryCredentials(), new NoStart(), NullTelemetrySink.Instance, () => m, () => @"C:\Games\AC", new MemorySlots());
                if (banner)
                {
                    presenter.Update = UpdateGate.Evaluate(m, SemVersion.Parse("1.0.0", "running"));
                }

                using (var window = new MainWindow())
                {
                    window.StartPosition = FormStartPosition.Manual;
                    window.Location = new Point(20, 20);
                    window.Show();
                    var screen = new LauncherScreen(presenter, notice);
                    window.ShowScreen(screen);
                    Pump(() => screen.Loaded);
                    body(window, screen);
                    window.Close();
                }
            });
        }

        private static void AssertEverythingReachable(MainWindow window, LauncherScreen screen, string state)
        {
            Rectangle card = window.Card.Bounds;
            Assert.AreEqual(window.WantedCardHeight(), card.Height, state + ": the card has the height its content asks for (capped to the body)");
            Assert.IsTrue(window.Card.Parent.ClientRectangle.Contains(card), state + ": the card lies inside the body area " + card);

            VStack body = screen.Body;
            Assert.IsTrue(screen.Footer.Bounds.Bottom <= screen.ClientSize.Height, state + ": the button row is inside the card");
            Assert.IsTrue(screen.Footer.Top >= body.Bottom - 1, state + ": the button row is below the body");
            Assert.IsTrue(screen.RememberBox.Bottom <= screen.ClientSize.Height, state + ": Remember is inside the card");

            foreach (SlotRow row in screen.Rows)
            {
                int bottom = row.Bottom - body.AutoScrollPosition.Y;
                bool visible = row.Top >= 0 && row.Bottom <= body.ClientSize.Height;
                bool scrollable = body.AutoScroll && body.AutoScrollMinSize.Height >= bottom;
                Assert.IsTrue(visible || scrollable, state + ": row at " + row.Top + ".." + row.Bottom + " is neither visible (body " + body.ClientSize.Height + ") nor covered by the scroll range " + body.AutoScrollMinSize.Height);
                Assert.AreEqual(row.GetPreferredSize(new Size(row.Width, 0)).Height, row.Height, state + ": the row has the height it asks for");
            }
        }

        [TestMethod]
        public void Layout_Default_EverythingReachable()
        {
            WithScreen(null, false, (w, s) => AssertEverythingReachable(w, s, "default"));
        }

        [TestMethod]
        public void Layout_Notice_EverythingReachable()
        {
            WithScreen("The server restarts at midnight for an update.", false, (w, s) => AssertEverythingReachable(w, s, "notice"));
        }

        [TestMethod]
        public void Layout_UpdateBanner_EverythingReachable()
        {
            WithScreen(null, true, (w, s) => AssertEverythingReachable(w, s, "banner"));
        }

        [TestMethod]
        public void Layout_NoticeAndBanner_EverythingReachable()
        {
            WithScreen("The server restarts at midnight for an update.", true, (w, s) => AssertEverythingReachable(w, s, "notice+banner"));
        }

        [TestMethod]
        public void Layout_DuplicateError_GrowsTheRow_AndEverythingStaysReachable()
        {
            WithScreen(null, false, (w, s) =>
            {
                SlotRow alt = s.Rows[1];
                int before = alt.Height;
                s.Rows[0].Account = "hero";
                alt.Account = "HERO";
                Pump(() => false, 150);

                Assert.IsTrue(alt.Height > before, "the row grew to show its error (was " + before + ", now " + alt.Height + ")");
                AssertEverythingReachable(w, s, "duplicate error");
            });
        }

        [TestMethod]
        public void Enter_InARowBox_PlaysThatRowOnly_ElsewhereItIsNotHandledByTheRow()
        {
            WithScreen(null, false, (w, s) =>
            {
                var played = new List<int>();
                for (int i = 0; i < s.Rows.Count; i++)
                {
                    int slot = i;
                    s.Rows[i].PlayClicked += (x, y) => played.Add(slot);
                }

                Assert.IsTrue(s.Rows[1].HandleEnter(true));
                Assert.IsFalse(s.Rows[2].HandleEnter(false), "focus elsewhere: the row leaves Enter to the default button");
                CollectionAssert.AreEqual(new[] { 1 }, played);
                Assert.IsNotNull(s.DefaultButton, "Play all stays the form default");
            });
        }

        [TestMethod]
        public void Enter_WithTheFocusInARowBox_RoutesThroughProcessCmdKey_AndPlaysThatRow()
        {
            WithScreen(null, false, (w, s) =>
            {
                var played = new List<int>();
                for (int i = 0; i < s.Rows.Count; i++)
                {
                    int slot = i;
                    s.Rows[i].PlayClicked += (x, y) => played.Add(slot);
                }

                SlotRow row = s.Rows[2];
                row.FocusAccountBox();
                Pump(() => row.AccountBoxFocused, 1000);
                Assert.IsTrue(row.AccountBoxFocused, "the account box took the focus");

                System.Reflection.MethodInfo m = typeof(SlotRow).GetMethod("ProcessCmdKey", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var msg = default(Message);
                object[] args = { msg, Keys.Enter };
                bool handled = (bool)m.Invoke(row, args);

                Assert.IsTrue(handled, "Enter in the row's box is handled by the row");
                CollectionAssert.AreEqual(new[] { 2 }, played, "only that row played");
            });
        }

        [TestMethod]
        public void EditingAnAccount_UntickRemember_OnlyWhenNoRowKeepsASavedPassword()
        {
            var creds = new LauncherPresenterTests.MemoryCredentials();
            creds.Save("hero", "pw");
            WithScreen(null, false, (w, s) =>
            {
                Assert.IsTrue(s.RememberBox.Checked, "a saved password ticks Remember on load");
                s.Rows[0].Account = "heroes";
                Pump(() => false, 100);
                Assert.AreEqual(string.Empty, s.Rows[0].Password, "the other account's saved password is dropped");
                Assert.IsFalse(s.RememberBox.Checked, "no row keeps a saved password, so Remember is unticked");
            }, creds);
        }
    }
}