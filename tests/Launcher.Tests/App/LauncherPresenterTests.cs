using System.Collections.Generic;
using System.Threading.Tasks;
using DreamWeave.Launcher.App;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Credentials;
using DreamWeave.Launcher.Core.Manifests;
using DreamWeave.Launcher.Core.Update;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.App
{
    [TestClass]
    public class LauncherPresenterTests
    {
        private static LauncherManifest WithLauncherBlock(string version, string minVersion)
        {
            LauncherManifest m = AppFixtures.Sample();
            m.Launcher = new LauncherUpdateInfo { Version = version, MinVersion = minVersion, Url = "https://example.test/DreamWeaveLauncher.exe", Sha256 = new string('A', 64), Size = 10 };
            return m;
        }

        private static LauncherPresenter Presenter(LauncherManifest m, FakeStarter starter, MemoryCredentials creds)
        {
            return new LauncherPresenter(creds, starter, NullTelemetrySink.Instance, () => m, () => @"C:\Games\AC");
        }

        [TestMethod]
        public void MandatoryUpdate_BlocksPlay_AndOffersUpdateWithoutLater()
        {
            LauncherManifest m = WithLauncherBlock("2.0.0", "2.0.0");
            var starter = new FakeStarter();
            LauncherPresenter p = Presenter(m, starter, new MemoryCredentials());
            Assert.IsTrue(p.CanPlay, "before the check nothing blocks");
            p.Update = UpdateGate.Evaluate(m, SemVersion.Parse("1.0.0", "running"));
            Assert.AreEqual(UpdateStatus.Mandatory, p.Update.Status);
            Assert.IsFalse(p.CanPlay);
            Assert.IsTrue(p.Update.OffersUpdate);
            Assert.IsFalse(p.Update.OffersLater);

            PlayResult r = p.PlayAsync("player", "secret", false).GetAwaiter().GetResult();
            Assert.AreNotEqual(LauncherError.None, r.Error);
            Assert.AreEqual(0, starter.Starts.Count, "a blocked Play must not start the client");
        }

        [TestMethod]
        public void MandatoryButUnavailable_AlsoBlocksPlay()
        {
            LauncherManifest m = WithLauncherBlock("2.0.0", "2.0.0");
            m.Launcher.Url = string.Empty;
            LauncherPresenter p = Presenter(m, new FakeStarter(), new MemoryCredentials());
            p.Update = UpdateGate.Evaluate(m, SemVersion.Parse("1.0.0", "running"));
            Assert.AreEqual(UpdateStatus.MandatoryUnavailable, p.Update.Status);
            Assert.IsFalse(p.CanPlay);
            Assert.IsFalse(p.Update.OffersUpdate);
        }

        [TestMethod]
        public void OptionalUpdate_DoesNotBlockPlay_OffersUpdateAndLater()
        {
            LauncherManifest m = WithLauncherBlock("1.1.0", "1.0.0");
            LauncherPresenter p = Presenter(m, new FakeStarter(), new MemoryCredentials());
            p.Update = UpdateGate.Evaluate(m, SemVersion.Parse("1.0.0", "running"));
            Assert.AreEqual(UpdateStatus.Optional, p.Update.Status);
            Assert.IsTrue(p.CanPlay);
            Assert.IsTrue(p.Update.OffersUpdate);
            Assert.IsTrue(p.Update.OffersLater);
        }

        [TestMethod]
        public void Validate_ReportsBuilderRules_InPlainWords()
        {
            LauncherPresenter p = Presenter(AppFixtures.Sample(), new FakeStarter(), new MemoryCredentials());
            Assert.IsNull(p.Validate("player", "pass word"));
            StringAssert.Contains(p.Validate("player", "bad\"quote"), "double quote");
            StringAssert.Contains(p.Validate("player", "trailing\\"), "backslash");
            StringAssert.Contains(p.Validate("pl\u00e9yer", "secret"), "letters, digits");
            StringAssert.Contains(p.Validate("player", "tab\there"), "control");
            Assert.IsFalse(p.Validate("player", "bad\"quote").Contains("bad\"quote"), "the rule never echoes the password");
        }

        [TestMethod]
        public void Play_StartsClient_AndRemembersPassword()
        {
            var starter = new FakeStarter();
            var creds = new MemoryCredentials();
            LauncherPresenter p = Presenter(AppFixtures.Sample(), starter, creds);
            PlayResult r = p.PlayAsync("player", "secret", true).GetAwaiter().GetResult();
            Assert.AreEqual(LauncherError.None, r.Error);
            Assert.AreEqual(1, starter.Starts.Count);
            Assert.AreEqual(@"C:\Games\AC", starter.Starts[0].Key);
            StringAssert.Contains(starter.Starts[0].Value, "-h play.example.test:9000");
            Assert.AreEqual("secret", creds.Get("player"));
            Assert.IsFalse(p.IsStarting);
        }

        [TestMethod]
        public void Play_LaunchFailure_MapsToTableMessage()
        {
            var starter = new FakeStarter { Fail = LauncherError.ClientRequiresElevation };
            LauncherPresenter p = Presenter(AppFixtures.Sample(), starter, new MemoryCredentials());
            PlayResult r = p.PlayAsync("player", "secret", false).GetAwaiter().GetResult();
            Assert.AreEqual(LauncherError.ClientRequiresElevation, r.Error);
            Assert.AreEqual(ErrorMessages.For(LauncherError.ClientRequiresElevation), r.Message);
            Assert.IsTrue(p.CanPlay, "Play is enabled again after a failed start");
        }

        [TestMethod]
        public void SavedPassword_IsForgotten_WhenTheAccountNameChanges()
        {
            LauncherPresenter p = Presenter(AppFixtures.Sample(), new FakeStarter(), new MemoryCredentials());
            Assert.IsFalse(p.AccountChanged("alice"), "nothing filled yet: nothing to clear");

            p.MarkFilled("alice");
            Assert.AreEqual("alice", p.FilledFor);
            Assert.IsFalse(p.AccountChanged("alice"));
            Assert.IsFalse(p.AccountChanged("ALICE"), "account names are not case-sensitive");
            Assert.AreEqual("alice", p.FilledFor);

            Assert.IsTrue(p.AccountChanged("alicex"), "an edit away from the filled name must clear the password");
            Assert.IsNull(p.FilledFor);
            Assert.IsFalse(p.AccountChanged("bob"), "already cleared: a second change clears nothing more");
        }

        [TestMethod]
        public void SavedPassword_IsForgotten_WhenAnotherAccountIsPicked()
        {
            LauncherPresenter p = Presenter(AppFixtures.Sample(), new FakeStarter(), new MemoryCredentials());
            p.MarkFilled("alice");
            Assert.IsTrue(p.AccountChanged("carol"), "picking an account with no saved password must not keep alice's");
            Assert.IsTrue(!p.AccountChanged(null) && p.FilledFor == null);
            p.MarkFilled("alice");
            Assert.IsTrue(p.AccountChanged(string.Empty), "clearing the name clears the password too");
        }

        internal sealed class FakeStarter : IGameStarter
        {
            public List<KeyValuePair<string, string>> Starts { get; } = new List<KeyValuePair<string, string>>();

            public LauncherError Fail { get; set; }

            /// <summary>What <see cref="ReleaseLock"/> reports.</summary>
            public DreamWeave.Launcher.Core.Launch.ClientLockOutcome LockOutcome { get; set; } = DreamWeave.Launcher.Core.Launch.ClientLockOutcome.Released;

            /// <summary>Clients returned by <see cref="Start"/>, in order.</summary>
            public List<DreamWeave.Launcher.Core.Launch.StartedClient> Clients { get; } = new List<DreamWeave.Launcher.Core.Launch.StartedClient>();

            /// <summary>The owner (stand-in for the Process) of each client returned by <see cref="Start"/>, in order.</summary>
            public List<CountingOwner> Owners { get; } = new List<CountingOwner>();

            /// <summary>Clients passed to <see cref="ReleaseLock"/>, in order.</summary>
            public List<DreamWeave.Launcher.Core.Launch.StartedClient> Released { get; } = new List<DreamWeave.Launcher.Core.Launch.StartedClient>();

            /// <summary>Called inside <see cref="ReleaseLock"/> with its token.</summary>
            public System.Action<System.Threading.CancellationToken> OnRelease { get; set; }

            public DreamWeave.Launcher.Core.Launch.StartedClient Start(string acFolder, string args)
            {
                if (Fail != LauncherError.None)
                {
                    throw new LauncherException(Fail, "scripted");
                }

                Starts.Add(new KeyValuePair<string, string>(acFolder, args));
                var owner = new CountingOwner();
                Owners.Add(owner);
                var client = new DreamWeave.Launcher.Core.Launch.StartedClient(1000 + Starts.Count, new System.IntPtr(0x7000 + Starts.Count), owner);
                Clients.Add(client);
                return client;
            }

            public DreamWeave.Launcher.Core.Launch.ClientLockOutcome ReleaseLock(DreamWeave.Launcher.Core.Launch.StartedClient client, System.Threading.CancellationToken cancel)
            {
                Released.Add(client);
                OnRelease?.Invoke(cancel);
                return LockOutcome;
            }
        }

        /// <summary>Counts its disposals (the StartedClient's Process in production).</summary>
        internal sealed class CountingOwner : System.IDisposable
        {
            public int Disposals { get; private set; }

            public void Dispose() => Disposals++;
        }

        internal sealed class MemoryCredentials : ICredentialStore
        {
            private readonly Dictionary<string, string> _saved = new Dictionary<string, string>();

            public void Save(string account, string password) => _saved[account] = password;

            public string Get(string account) => _saved.TryGetValue(account, out string p) ? p : null;

            public bool Delete(string account) => _saved.Remove(account);

            public IReadOnlyList<string> ListAccounts() => new List<string>(_saved.Keys);
        }
    }
}
