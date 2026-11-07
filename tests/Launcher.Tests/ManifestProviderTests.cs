using System.IO;
using System.Linq;
using System.Threading;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Manifests;
using DreamWeave.Launcher.Core.State;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests
{
    [TestClass]
    public class ManifestProviderTests
    {
        private const string PrimaryUrl = "https://primary.example.test/manifest.json";
        private const string SecondaryUrl = "https://secondary.example.test/manifest.json";

        private static SigningKey _key;

        [ClassInitialize]
        public static void Init(TestContext context)
        {
            _key = SigningKey.Generate();
        }

        private static ManifestSource[] Sources()
        {
            return new[]
            {
                ManifestSource.FromManifestUrl("primary", PrimaryUrl),
                ManifestSource.FromManifestUrl("secondary", SecondaryUrl),
            };
        }

        private static void Serve(FakeHttpFetcher fetcher, string url, long serial, int schema = 1)
        {
            byte[] manifest = TestManifests.Build(serial, schema);
            fetcher.Serve(url, manifest);
            fetcher.Serve(url + ".sig", _key.Sign(manifest).ToFileBytes());
        }

        private static ManifestLoadResult Load(FakeHttpFetcher fetcher, AppPaths paths, StateStore state)
        {
            var provider = new ManifestProvider(fetcher, new ManifestVerifier(new[] { _key.PublicKey }), paths, state, Sources());
            return provider.LoadAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        [TestMethod]
        public void Primary_Verifies_CachesAndPersistsSerial()
        {
            using (var dir = new TempDir())
            {
                var paths = new AppPaths(dir.Path);
                var state = StateStore.Open(paths);
                var fetcher = new FakeHttpFetcher();
                Serve(fetcher, PrimaryUrl, 3);

                ManifestLoadResult r = Load(fetcher, paths, state);

                Assert.AreEqual(LauncherError.None, r.Status);
                Assert.AreEqual("primary", r.SourceName);
                Assert.IsFalse(r.FromCache);
                Assert.IsTrue(r.DownloadsAllowed);
                Assert.AreEqual(3L, r.Manifest.Serial);
                Assert.AreEqual(3L, state.HighestManifestSerial);
                Assert.AreEqual(3L, new StateStore(paths.StateFile).HighestManifestSerial, "serial must be persisted to disk");
                CollectionAssert.AreEqual(TestManifests.Build(3), File.ReadAllBytes(paths.ManifestCacheFile));
                Assert.IsTrue(File.Exists(paths.ManifestSigCacheFile));
            }
        }

        [TestMethod]
        public void PrimaryDown_FallsBackToSecondary()
        {
            using (var dir = new TempDir())
            {
                var paths = new AppPaths(dir.Path);
                var state = StateStore.Open(paths);
                var fetcher = new FakeHttpFetcher();
                fetcher.Fail(PrimaryUrl + ".sig", LauncherError.SourceHttpError);
                Serve(fetcher, SecondaryUrl, 4);

                ManifestLoadResult r = Load(fetcher, paths, state);

                Assert.AreEqual(LauncherError.None, r.Status);
                Assert.AreEqual("secondary", r.SourceName);
                Assert.AreEqual(4L, r.Manifest.Serial);
                Assert.AreEqual(LauncherError.SourceHttpError, r.Attempts[0].Error);
            }
        }

        [TestMethod]
        public void PrimaryTampered_FallsBackToSecondary()
        {
            using (var dir = new TempDir())
            {
                var paths = new AppPaths(dir.Path);
                var state = StateStore.Open(paths);
                var fetcher = new FakeHttpFetcher();
                Serve(fetcher, PrimaryUrl, 9);
                fetcher.Serve(PrimaryUrl, TestManifests.Build(10)); // signature is for serial 9
                Serve(fetcher, SecondaryUrl, 8);

                ManifestLoadResult r = Load(fetcher, paths, state);

                Assert.AreEqual("secondary", r.SourceName);
                Assert.AreEqual(LauncherError.ManifestSignatureInvalid, r.Attempts[0].Error);
                Assert.AreEqual(8L, state.HighestManifestSerial);
            }
        }

        [TestMethod]
        public void AllSourcesDown_UsesReverifiedCache()
        {
            using (var dir = new TempDir())
            {
                var paths = new AppPaths(dir.Path);
                var state = StateStore.Open(paths);
                var online = new FakeHttpFetcher();
                Serve(online, PrimaryUrl, 6);
                Assert.AreEqual(LauncherError.None, Load(online, paths, state).Status);

                ManifestLoadResult r = Load(new FakeHttpFetcher(), paths, state);

                Assert.AreEqual(LauncherError.ManifestUnavailable, r.Status);
                Assert.IsTrue(r.FromCache);
                Assert.AreEqual("cache", r.SourceName);
                Assert.AreEqual(6L, r.Manifest.Serial);
                Assert.IsTrue(r.LaunchAllowed);
                Assert.AreEqual(3, r.Attempts.Count);
            }
        }

        [TestMethod]
        public void TamperedCache_IsNotUsed()
        {
            using (var dir = new TempDir())
            {
                var paths = new AppPaths(dir.Path);
                var state = StateStore.Open(paths);
                var online = new FakeHttpFetcher();
                Serve(online, PrimaryUrl, 6);
                Load(online, paths, state);
                File.WriteAllBytes(paths.ManifestCacheFile, TestManifests.Build(7));

                ManifestLoadResult r = Load(new FakeHttpFetcher(), paths, state);

                Assert.IsNull(r.Manifest);
                Assert.AreEqual(LauncherError.ManifestUnavailable, r.Status);
                Assert.AreEqual(LauncherError.ManifestSignatureInvalid, r.Attempts.Last().Error);
            }
        }

        [TestMethod]
        public void RolledBackSources_AreRefused_CacheStillServes()
        {
            using (var dir = new TempDir())
            {
                var paths = new AppPaths(dir.Path);
                var state = StateStore.Open(paths);
                var online = new FakeHttpFetcher();
                Serve(online, PrimaryUrl, 6);
                Load(online, paths, state);

                var stale = new FakeHttpFetcher();
                Serve(stale, PrimaryUrl, 5);
                Serve(stale, SecondaryUrl, 5);
                ManifestLoadResult r = Load(stale, paths, state);

                Assert.AreEqual(LauncherError.ManifestRollback, r.Attempts[0].Error);
                Assert.AreEqual(LauncherError.ManifestRollback, r.Attempts[1].Error);
                Assert.IsTrue(r.FromCache);
                Assert.AreEqual(6L, r.Manifest.Serial);
                Assert.AreEqual(6L, state.HighestManifestSerial);
            }
        }

        [TestMethod]
        public void NewerSchema_BlocksDownloads_KeepsCacheAndSerial()
        {
            using (var dir = new TempDir())
            {
                var paths = new AppPaths(dir.Path);
                var state = StateStore.Open(paths);
                var online = new FakeHttpFetcher();
                Serve(online, PrimaryUrl, 6);
                Load(online, paths, state);

                var future = new FakeHttpFetcher();
                Serve(future, PrimaryUrl, 20, schema: 2);
                ManifestLoadResult r = Load(future, paths, state);

                Assert.AreEqual(LauncherError.ManifestSchemaUnsupported, r.Status);
                Assert.IsFalse(r.DownloadsAllowed);
                Assert.IsTrue(r.LaunchAllowed);
                Assert.AreEqual(6L, r.Manifest.Serial);
                Assert.AreEqual(6L, state.HighestManifestSerial, "an unsupported manifest must not advance the serial");
                CollectionAssert.AreEqual(TestManifests.Build(6), File.ReadAllBytes(paths.ManifestCacheFile));
            }
        }

        [TestMethod]
        public void NothingAnywhere_ReturnsNullManifest()
        {
            using (var dir = new TempDir())
            {
                var paths = new AppPaths(dir.Path);
                ManifestLoadResult r = Load(new FakeHttpFetcher(), paths, StateStore.Open(paths));

                Assert.IsNull(r.Manifest);
                Assert.IsFalse(r.LaunchAllowed);
                Assert.AreEqual(LauncherError.ManifestUnavailable, r.Status);
            }
        }

        [TestMethod]
        public void Fetches_AreSizeCapped()
        {
            using (var dir = new TempDir())
            {
                var paths = new AppPaths(dir.Path);
                var fetcher = new FakeHttpFetcher();
                Serve(fetcher, PrimaryUrl, 1);
                Load(fetcher, paths, StateStore.Open(paths));

                Assert.AreEqual(ManifestSignature.MaxFileBytes, fetcher.Requests.Single(q => q.Item1.AbsoluteUri.EndsWith(".sig")).Item2);
                Assert.AreEqual(ManifestProvider.MaxManifestBytes, fetcher.Requests.Single(q => q.Item1.AbsoluteUri == PrimaryUrl).Item2);
            }
        }

        [TestMethod]
        public void OversizeManifest_IsRejected()
        {
            using (var dir = new TempDir())
            {
                var paths = new AppPaths(dir.Path);
                var fetcher = new FakeHttpFetcher();
                byte[] huge = new byte[ManifestProvider.MaxManifestBytes + 1];
                fetcher.Serve(PrimaryUrl, huge);
                fetcher.Serve(PrimaryUrl + ".sig", _key.Sign(huge).ToFileBytes());

                ManifestLoadResult r = Load(fetcher, paths, StateStore.Open(paths));

                Assert.AreEqual(LauncherError.ResponseTooLarge, r.Attempts[0].Error);
                Assert.IsNull(r.Manifest);
            }
        }

        [TestMethod]
        public void DefaultSources_PointAtManifestBranch()
        {
            ManifestSource primary = ManifestSources.Default[0];
            Assert.AreEqual("https://raw.githubusercontent.com/Awful-Waffle-Rofl/dreamweave-launcher/manifest/manifest.json", primary.ManifestUri.AbsoluteUri);
            Assert.AreEqual(primary.ManifestUri.AbsoluteUri + ".sig", primary.SignatureUri.AbsoluteUri);
        }
    }
}
