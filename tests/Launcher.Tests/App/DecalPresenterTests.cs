using System.Collections.Generic;
using DreamWeave.Launcher.App;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Launch;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.App
{
    [TestClass]
    public class DecalPresenterTests
    {
        private sealed class FakeDecalStarter : IDecalGameStarter
        {
            public bool Available { get; set; } = true;

            public bool InjectionFailed { get; set; }

            public LauncherError Fail { get; set; }

            public List<KeyValuePair<string, string>> Starts { get; } = new List<KeyValuePair<string, string>>();

            public DecalStartResult Start(string acFolder, string args)
            {
                if (Fail != LauncherError.None)
                {
                    throw new LauncherException(Fail, "scripted");
                }

                Starts.Add(new KeyValuePair<string, string>(acFolder, args));
                return new DecalStartResult(new StartedClient(2000 + Starts.Count, System.IntPtr.Zero), InjectionFailed);
            }
        }

        private static LauncherPresenter Presenter(LauncherPresenterTests.FakeStarter normal, FakeDecalStarter decal, bool launchWithDecal)
        {
            return new LauncherPresenter(
                new LauncherPresenterTests.MemoryCredentials(),
                normal,
                NullTelemetrySink.Instance,
                () => AppFixtures.Sample(),
                () => @"C:\Games\AC",
                null,
                decal)
            {
                LaunchWithDecal = launchWithDecal,
            };
        }

        [TestMethod]
        public void DecalRun_StartsThroughDecal_AndNeverReleasesTheLock()
        {
            var normal = new LauncherPresenterTests.FakeStarter();
            var decal = new FakeDecalStarter();
            LauncherPresenter p = Presenter(normal, decal, true);

            PlayResult r = p.PlayAsync("player", "secret", false).GetAwaiter().GetResult();

            Assert.AreEqual(LauncherError.None, r.Error);
            Assert.AreEqual(1, decal.Starts.Count, "the Decal starter runs the client");
            Assert.AreEqual(0, normal.Starts.Count, "the plain starter is not used");
            Assert.AreEqual(0, normal.Released.Count, "a Decal start never releases the single-instance lock");
            StringAssert.Contains(decal.Starts[0].Value, "-h play.example.test:9000");
            Assert.IsNull(r.Notice);
        }

        [TestMethod]
        public void PlainRun_IsUnchanged_WhenDecalBoxIsOff()
        {
            var normal = new LauncherPresenterTests.FakeStarter();
            var decal = new FakeDecalStarter();
            LauncherPresenter p = Presenter(normal, decal, false);

            PlayResult r = p.PlayAsync("player", "secret", false).GetAwaiter().GetResult();

            Assert.AreEqual(LauncherError.None, r.Error);
            Assert.AreEqual(0, decal.Starts.Count);
            Assert.AreEqual(1, normal.Starts.Count);
            Assert.AreEqual(1, normal.Released.Count, "the plain path still releases the lock");
        }

        [TestMethod]
        public void DecalUnavailable_FallsBackToPlainPath_EvenWhenBoxIsOn()
        {
            var normal = new LauncherPresenterTests.FakeStarter();
            var decal = new FakeDecalStarter { Available = false };
            LauncherPresenter p = Presenter(normal, decal, true);

            Assert.IsFalse(p.DecalAvailable);
            PlayResult r = p.PlayAsync("player", "secret", false).GetAwaiter().GetResult();

            Assert.AreEqual(LauncherError.None, r.Error);
            Assert.AreEqual(0, decal.Starts.Count);
            Assert.AreEqual(1, normal.Starts.Count);
            Assert.AreEqual(1, normal.Released.Count);
        }

        [TestMethod]
        public void DecalInjectionFailed_StillStarts_AndShowsTheNotice()
        {
            var decal = new FakeDecalStarter { InjectionFailed = true };
            LauncherPresenter p = Presenter(new LauncherPresenterTests.FakeStarter(), decal, true);

            PlayResult r = p.PlayAsync("player", "secret", false).GetAwaiter().GetResult();

            Assert.AreEqual(LauncherError.None, r.Error);
            Assert.AreEqual(UiText.DecalNotLoaded, r.Notice);
        }

        [TestMethod]
        public void DecalHardFailure_MapsToTheErrorTableMessage()
        {
            var decal = new FakeDecalStarter { Fail = LauncherError.DecalNotFound };
            LauncherPresenter p = Presenter(new LauncherPresenterTests.FakeStarter(), decal, true);

            PlayResult r = p.PlayAsync("player", "secret", false).GetAwaiter().GetResult();

            Assert.AreEqual(LauncherError.DecalNotFound, r.Error);
            Assert.AreEqual(ErrorMessages.For(LauncherError.DecalNotFound), r.Message);
            Assert.IsTrue(p.CanPlay, "Play is enabled again after a failed Decal start");
        }
    }
}
