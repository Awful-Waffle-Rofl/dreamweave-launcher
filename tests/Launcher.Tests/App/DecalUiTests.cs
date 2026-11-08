using System;
using System.Collections.Generic;
using System.Threading;
using DreamWeave.Launcher.App;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Launch;
using DreamWeave.Launcher.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.App
{
    [TestClass]
    public class DecalUiTests
    {
        private sealed class NoStart : IGameStarter
        {
            public StartedClient Start(string acFolder, string args) => new StartedClient(1, IntPtr.Zero);

            public ClientLockOutcome ReleaseLock(StartedClient client, CancellationToken cancel) => ClientLockOutcome.Released;
        }

        private sealed class FakeDecal : IDecalGameStarter
        {
            public bool Available { get; set; }

            public DecalStartResult Start(string acFolder, string args) => new DecalStartResult(new StartedClient(1, IntPtr.Zero), false);
        }

        private static void RunSta(Action body)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try
                {
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

        private static LauncherPresenter Presenter(bool decalAvailable, bool launchWithDecal)
        {
            return new LauncherPresenter(
                new LauncherPresenterTests.MemoryCredentials(),
                new NoStart(),
                NullTelemetrySink.Instance,
                () => AppFixtures.Sample(),
                () => @"C:\Games\AC",
                null,
                new FakeDecal { Available = decalAvailable })
            {
                LaunchWithDecal = launchWithDecal,
            };
        }

        [TestMethod]
        public void DecalBox_IsShown_WhenDecalIsDetected()
        {
            RunSta(() =>
            {
                var screen = new LauncherScreen(Presenter(true, true), null);
                Assert.IsNotNull(screen.DecalBox, "the box exists when Decal is detected");
                Assert.IsTrue(screen.DecalBox.Checked, "it reflects the saved preference");
                Assert.AreEqual(UiText.LaunchWithDecal, screen.DecalBox.Text);
                Assert.IsNull(screen.DecalLink, "no discovery link when Decal is already detected");
                screen.Dispose();
            });
        }

        [TestMethod]
        public void DecalBox_IsHidden_WhenDecalIsNotDetected()
        {
            RunSta(() =>
            {
                var screen = new LauncherScreen(Presenter(false, true), null);
                Assert.IsNull(screen.DecalBox, "no box when Decal is not detected");
                Assert.IsNotNull(screen.DecalLink, "the discovery link is shown when Decal is not detected");
                Assert.AreEqual(UiText.DecalGetItLink, screen.DecalLink.Text);
                Assert.IsTrue(screen.DecalLink.Visible);
                screen.Dispose();
            });
        }

        [TestMethod]
        public void TogglingTheBox_UpdatesThePresenter_AndRaisesDecalChanged()
        {
            RunSta(() =>
            {
                LauncherPresenter p = Presenter(true, false);
                var screen = new LauncherScreen(p, null);
                int changed = 0;
                screen.DecalChanged += (s, e) => changed++;

                screen.DecalBox.Checked = true;

                Assert.IsTrue(p.LaunchWithDecal, "the presenter follows the box");
                Assert.AreEqual(1, changed, "a toggle raises DecalChanged so the choice is persisted");
                screen.Dispose();
            });
        }
    }
}
