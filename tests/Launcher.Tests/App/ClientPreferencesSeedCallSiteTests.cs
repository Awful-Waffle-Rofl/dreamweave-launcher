using System;
using System.Collections.Generic;
using DreamWeave.Launcher.App;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.ClientPrefs;
using DreamWeave.Launcher.Core.Launch;
using DreamWeave.Launcher.Core.Manifests;
using DreamWeave.Launcher.Core.Update;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.App
{
    /// <summary>
    /// The chosen call site is Play (just before the client starts), so the windowed default is seeded
    /// whether or not the player ever used our installer flow for the client. These tests pin that the
    /// site runs the seeder exactly once per successful start, on both the plain and the Decal path, and
    /// never on a start that does not happen.
    /// </summary>
    [TestClass]
    public class ClientPreferencesSeedCallSiteTests
    {
        private sealed class CountingSeeder : IClientPreferencesSeeder
        {
            public int Calls { get; private set; }

            public ClientPreferencesSeedResult Seed()
            {
                Calls++;
                return ClientPreferencesSeedResult.AlreadyPresent("x");
            }
        }

        private sealed class FakeDecalStarter : IDecalGameStarter
        {
            public bool Available => true;

            public List<KeyValuePair<string, string>> Starts { get; } = new List<KeyValuePair<string, string>>();

            public DecalStartResult Start(string acFolder, string args)
            {
                Starts.Add(new KeyValuePair<string, string>(acFolder, args));
                return new DecalStartResult(new StartedClient(3000 + Starts.Count, IntPtr.Zero), false);
            }
        }

        private static LauncherPresenter Presenter(LauncherManifest m, CountingSeeder seeder, IGameStarter starter = null, IDecalGameStarter decal = null, bool launchWithDecal = false)
        {
            return new LauncherPresenter(
                new LauncherPresenterTests.MemoryCredentials(),
                starter ?? new LauncherPresenterTests.FakeStarter(),
                NullTelemetrySink.Instance,
                () => m,
                () => @"C:\Games\AC",
                null,
                decal,
                seeder)
            {
                LaunchWithDecal = launchWithDecal,
            };
        }

        [TestMethod]
        public void Play_SeedsClientPreferences_ExactlyOnce()
        {
            var seeder = new CountingSeeder();
            var starter = new LauncherPresenterTests.FakeStarter();
            LauncherPresenter p = Presenter(AppFixtures.Sample(), seeder, starter);

            PlayResult r = p.PlayAsync("player", "secret", false).GetAwaiter().GetResult();

            Assert.AreEqual(LauncherError.None, r.Error);
            Assert.AreEqual(1, starter.Starts.Count, "the client started");
            Assert.AreEqual(1, seeder.Calls, "the seeder ran exactly once before the start");
        }

        [TestMethod]
        public void DecalPlay_AlsoSeeds_ExactlyOnce()
        {
            var seeder = new CountingSeeder();
            var normal = new LauncherPresenterTests.FakeStarter();
            var decal = new FakeDecalStarter();
            LauncherPresenter p = Presenter(AppFixtures.Sample(), seeder, normal, decal, launchWithDecal: true);

            PlayResult r = p.PlayAsync("player", "secret", false).GetAwaiter().GetResult();

            Assert.AreEqual(LauncherError.None, r.Error);
            Assert.AreEqual(1, decal.Starts.Count, "the Decal path started the client");
            Assert.AreEqual(0, normal.Starts.Count);
            Assert.AreEqual(1, seeder.Calls, "the single choke point seeds on the Decal path too");
        }

        [TestMethod]
        public void BlockedPlay_DoesNotSeed()
        {
            LauncherManifest m = AppFixtures.Sample();
            m.Launcher = new LauncherUpdateInfo { Version = "2.0.0", MinVersion = "2.0.0", DownloadPage = "https://example.test/download" };
            var seeder = new CountingSeeder();
            LauncherPresenter p = Presenter(m, seeder);
            p.Update = UpdateGate.Evaluate(m, SemVersion.Parse("1.0.0", "running"));
            Assert.IsFalse(p.CanPlay);

            PlayResult r = p.PlayAsync("player", "secret", false).GetAwaiter().GetResult();

            Assert.AreNotEqual(LauncherError.None, r.Error);
            Assert.AreEqual(0, seeder.Calls, "a start that never happens does not seed");
        }

        [TestMethod]
        public void InvalidInput_DoesNotSeed()
        {
            var seeder = new CountingSeeder();
            LauncherPresenter p = Presenter(AppFixtures.Sample(), seeder);

            PlayResult r = p.PlayAsync("player", "bad\"quote", false).GetAwaiter().GetResult();

            Assert.AreEqual(LauncherError.InvalidCredentials, r.Error);
            Assert.AreEqual(0, seeder.Calls, "seeding happens only after validation passes");
        }
    }
}
