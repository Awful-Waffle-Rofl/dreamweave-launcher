using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Manifests;
using DreamWeave.Launcher.Core.State;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

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

        private static ManifestLoadResult Load(FakeHttpFetcher fetcher, AppPaths paths, StateStore state, long compiledFloor = 1)
        {
            var provider = new ManifestProvider(fetcher, new ManifestVerifier(new[] { _key.PublicKey }, compiledFloor), paths, state, Sources());
            return provider.LoadAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        private static ManifestLoadResult LoadOffline(AppPaths paths, StateStore state)
        {
            return Load(new FakeHttpFetcher(), paths, state);
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
                Assert.IsTrue(File.Exists(paths.ManifestCacheFile));
                CollectionAssert.AreEqual(TestManifests.Build(3), LoadOffline(paths, state).Verified.GetManifestBytes());
            }
        }

        [TestMethod]
        public void Cache_IsOneFileHoldingBothParts()
        {
            using (var dir = new TempDir())
            {
                var paths = new AppPaths(dir.Path);
                var fetcher = new FakeHttpFetcher();
                Serve(fetcher, PrimaryUrl, 3);
                Load(fetcher, paths, StateStore.Open(paths));

                JObject cache = JObject.Parse(File.ReadAllText(paths.ManifestCacheFile));
                CollectionAssert.AreEqual(TestManifests.Build(3), Convert.FromBase64String((string)cache["manifestB64"]));
                Assert.IsNotNull(ManifestSignature.Parse(Convert.FromBase64String((string)cache["sigB64"])));
                Assert.AreEqual(@"manifest.cache.json", Path.GetFileName(paths.ManifestCacheFile));
            }
        }

        [TestMethod]
        public void LegacyTwoFileCache_IsIgnoredAndRemoved()
        {
            using (var dir = new TempDir())
            {
                var paths = new AppPaths(dir.Path);
                var state = StateStore.Open(paths);
                byte[] legacy = TestManifests.Build(4);
                File.WriteAllBytes(Path.Combine(paths.DataDir, "manifest.json"), legacy);
                File.WriteAllBytes(Path.Combine(paths.DataDir, "manifest.json.sig"), _key.Sign(legacy).ToFileBytes());

                Assert.IsNull(LoadOffline(paths, state).Manifest, "the legacy pair must not be read");

                var fetcher = new FakeHttpFetcher();
                Serve(fetcher, PrimaryUrl, 5);
                Load(fetcher, paths, state);
                Assert.IsFalse(File.Exists(Path.Combine(paths.DataDir, "manifest.json")));
                Assert.IsFalse(File.Exists(Path.Combine(paths.DataDir, "manifest.json.sig")));
            }
        }

        [TestMethod]
        public void FailedCacheWrite_KeepsPreviousSerialForOfflineLoad()
        {
            using (var dir = new TempDir())
            {
                var paths = new AppPaths(dir.Path);
                var state = StateStore.Open(paths);
                var first = new FakeHttpFetcher();
                Serve(first, PrimaryUrl, 6);
                Assert.AreEqual(LauncherError.None, Load(first, paths, state).Status);

                var second = new FakeHttpFetcher();
                Serve(second, PrimaryUrl, 7);
                ManifestLoadResult r;
                using (new FileStream(paths.ManifestCacheFile, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    r = Load(second, paths, state);
                }

                Assert.AreEqual(7L, r.Manifest.Serial, "the fresh manifest is still used in memory");
                Assert.IsTrue(r.Attempts.Any(a => a.SourceName == "cache-write" && a.Error != LauncherError.None));
                Assert.AreEqual(6L, state.HighestManifestSerial, "the floor must not pass what the cache holds");

                ManifestLoadResult offline = LoadOffline(paths, state);
                Assert.IsTrue(offline.FromCache);
                Assert.AreEqual(6L, offline.Manifest.Serial);
            }
        }

        [TestMethod]
        public void InSessionAcceptance_RefusesLowerSerial_EvenWhenNothingPersisted()
        {
            using (var dir = new TempDir())
            {
                var paths = new AppPaths(dir.Path);
                var state = StateStore.Open(paths);
                var fetcher = new FakeHttpFetcher();
                var provider = new ManifestProvider(fetcher, new ManifestVerifier(new[] { _key.PublicKey }), paths, state, Sources());

                // Create the cache file (serial 1) so it can be locked, then lock it for the session.
                Serve(fetcher, PrimaryUrl, 1);
                Assert.AreEqual(LauncherError.None, provider.LoadAsync(CancellationToken.None).GetAwaiter().GetResult().Status);
                using (new FileStream(paths.ManifestCacheFile, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    Serve(fetcher, PrimaryUrl, 7);
                    ManifestLoadResult seven = provider.LoadAsync(CancellationToken.None).GetAwaiter().GetResult();
                    Assert.AreEqual(7L, seven.Manifest.Serial);
                    Assert.IsTrue(seven.Attempts.Any(a => a.SourceName == "cache-write" && a.Error != LauncherError.None), "precondition: 7 was not cached");
                    Assert.AreEqual(1L, state.HighestManifestSerial, "precondition: 7 was not persisted");

                    Serve(fetcher, PrimaryUrl, 6);
                    ManifestLoadResult six = provider.LoadAsync(CancellationToken.None).GetAwaiter().GetResult();

                    Assert.AreEqual(LauncherError.ManifestRollback, six.Attempts[0].Error);
                    Assert.IsNull(six.Manifest, "serial 6 must not be accepted after 7 in the same session");
                }
            }
        }

        [TestMethod]
        public void ConcurrentLoad_ThatVerifiedAgainstOlderFloor_IsRefusedAtAccept()
        {
            using (var dir = new TempDir())
            {
                var paths = new AppPaths(dir.Path);
                var state = StateStore.Open(paths);
                byte[] m6 = TestManifests.Build(6);
                byte[] m7 = TestManifests.Build(7);
                var gate = new System.Threading.Tasks.TaskCompletionSource<bool>();
                var fetcher = new GatedFetcher(gate.Task, _key.Sign(m6).ToFileBytes(), m6, _key.Sign(m7).ToFileBytes(), m7);
                var provider = new ManifestProvider(fetcher, new ManifestVerifier(new[] { _key.PublicKey }), paths, state, new[] { ManifestSource.FromManifestUrl("primary", PrimaryUrl) });

                // Load A computes its floor (1), then blocks inside its first fetch.
                System.Threading.Tasks.Task<ManifestLoadResult> first = System.Threading.Tasks.Task.Run(() => provider.LoadAsync(CancellationToken.None));
                Assert.IsTrue(fetcher.FirstCallEntered.Wait(TimeSpan.FromSeconds(10)), "precondition: load A reached the gated fetch");

                // Load B runs to completion and accepts 7.
                ManifestLoadResult second = provider.LoadAsync(CancellationToken.None).GetAwaiter().GetResult();
                Assert.AreEqual(7L, second.Manifest.Serial);

                // Release A: it gets serial 6, which verifies against its stale floor of 1.
                gate.SetResult(true);
                ManifestLoadResult a = first.GetAwaiter().GetResult();

                Assert.AreEqual(LauncherError.ManifestRollback, a.Attempts[0].Error, a.Attempts[0].Detail);
                Assert.IsFalse(a.Attempts.Any(x => x.SourceName == "primary" && x.Error == LauncherError.None), "serial 6 must not be accepted from the source");

                // A then falls back to the cache, which load B filled with 7: that is the correct
                // outcome (never 6). With no cache it would be null.
                Assert.IsTrue(a.FromCache);
                Assert.AreEqual(7L, a.Manifest.Serial);
                Assert.AreEqual(7L, state.HighestManifestSerial, "serial 6 must not have been persisted over 7");
            }
        }

        /// <summary>
        /// Call 0 (load A's signature) waits on a gate; calls 1-2 (load B) return the serial-7 pair;
        /// call 3 (load A's manifest, after the gate opens) returns serial 6. Calls after that are
        /// offline. Order is fixed because load B runs to completion while load A is parked on call 0.
        /// </summary>
        private sealed class GatedFetcher : DreamWeave.Launcher.Core.Net.IHttpFetcher
        {
            private readonly System.Threading.Tasks.Task _gate;
            private readonly byte[][] _bodies;
            private int _calls = -1;

            public GatedFetcher(System.Threading.Tasks.Task gate, byte[] sig6, byte[] m6, byte[] sig7, byte[] m7)
            {
                _gate = gate;
                _bodies = new[] { sig6, sig7, m7, m6 };
            }

            public ManualResetEventSlim FirstCallEntered { get; } = new ManualResetEventSlim(false);

            public async System.Threading.Tasks.Task<byte[]> GetBytesAsync(Uri uri, long maxBytes, CancellationToken cancellationToken)
            {
                int n = Interlocked.Increment(ref _calls);
                if (n == 0)
                {
                    FirstCallEntered.Set();
                    await _gate.ConfigureAwait(false);
                }

                if (n >= _bodies.Length)
                {
                    throw new LauncherException(LauncherError.NetworkUnavailable, "offline");
                }

                return _bodies[n];
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
                Assert.AreEqual("secondary", r.Attempts[1].SourceName, "a non-signature failure is not refetched");
            }
        }

        [TestMethod]
        public void PrimaryTampered_RefetchedOnce_ThenFallsBackToSecondary()
        {
            using (var dir = new TempDir())
            {
                var paths = new AppPaths(dir.Path);
                var state = StateStore.Open(paths);
                var fetcher = new FakeHttpFetcher();
                Serve(fetcher, PrimaryUrl, 9);
                fetcher.Serve(PrimaryUrl, TestManifests.Build(10)); // signature is for serial 9, every time
                Serve(fetcher, SecondaryUrl, 8);

                ManifestLoadResult r = Load(fetcher, paths, state);

                Assert.AreEqual("secondary", r.SourceName);
                Assert.AreEqual(LauncherError.ManifestSignatureInvalid, r.Attempts[0].Error);
                Assert.AreEqual("primary (refetch)", r.Attempts[1].SourceName);
                Assert.AreEqual(LauncherError.ManifestSignatureInvalid, r.Attempts[1].Error);
                Assert.AreEqual(8L, state.HighestManifestSerial);
            }
        }

        [TestMethod]
        public void MismatchedPair_ThenConsistentPair_RefetchSucceeds()
        {
            using (var dir = new TempDir())
            {
                var paths = new AppPaths(dir.Path);
                var state = StateStore.Open(paths);
                var fetcher = new FakeHttpFetcher();
                byte[] oldManifest = TestManifests.Build(4);
                byte[] newManifest = TestManifests.Build(5);
                // First fetch: new manifest with the old signature (CDN mid-update). Second: consistent.
                fetcher.Serve(PrimaryUrl + ".sig", _key.Sign(oldManifest).ToFileBytes(), _key.Sign(newManifest).ToFileBytes());
                fetcher.Serve(PrimaryUrl, newManifest, newManifest);

                ManifestLoadResult r = Load(fetcher, paths, state);

                Assert.AreEqual(LauncherError.None, r.Status);
                Assert.AreEqual("primary", r.SourceName);
                Assert.AreEqual(5L, r.Manifest.Serial);
                Assert.AreEqual(LauncherError.ManifestSignatureInvalid, r.Attempts[0].Error);
                Assert.AreEqual("primary (refetch)", r.Attempts[1].SourceName);
                Assert.AreEqual(LauncherError.None, r.Attempts[1].Error);
                Uri refetched = fetcher.Requests[2].Item1;
                StringAssert.Contains(refetched.Query, "cb=", "the refetch must be cache-busted");
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

                ManifestLoadResult r = LoadOffline(paths, state);

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
                JObject cache = JObject.Parse(File.ReadAllText(paths.ManifestCacheFile));
                cache["manifestB64"] = Convert.ToBase64String(TestManifests.Build(7));
                File.WriteAllText(paths.ManifestCacheFile, cache.ToString());

                ManifestLoadResult r = LoadOffline(paths, state);

                Assert.IsNull(r.Manifest);
                Assert.AreEqual(LauncherError.ManifestUnavailable, r.Status);
                Assert.AreEqual(LauncherError.ManifestSignatureInvalid, r.Attempts.Last().Error);
            }
        }

        [TestMethod]
        public void GarbageCache_IsReportedNotThrown()
        {
            using (var dir = new TempDir())
            {
                var paths = new AppPaths(dir.Path);
                var state = StateStore.Open(paths);
                File.WriteAllText(paths.ManifestCacheFile, "{\"format\":\"dreamweave-manifest-cache-1\",\"manifestB64\":\"@@@\",\"sigB64\":\"x\"}");

                ManifestLoadResult r = LoadOffline(paths, state);

                Assert.IsNull(r.Manifest);
                Assert.AreEqual(LauncherError.ManifestInvalid, r.Attempts.Last().Error);
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
        public void CorruptState_WithValidCache_StillRejectsLowerSerial()
        {
            using (var dir = new TempDir())
            {
                var paths = new AppPaths(dir.Path);
                var online = new FakeHttpFetcher();
                Serve(online, PrimaryUrl, 6);
                Load(online, paths, StateStore.Open(paths));

                File.WriteAllText(paths.StateFile, "{ corrupt");
                var freshState = StateStore.Open(paths);
                Assert.AreEqual(0L, freshState.HighestManifestSerial, "precondition: state was lost");

                var stale = new FakeHttpFetcher();
                Serve(stale, PrimaryUrl, 5);
                ManifestLoadResult r = Load(stale, paths, freshState);

                Assert.AreEqual(LauncherError.ManifestRollback, r.Attempts[0].Error);
                Assert.AreEqual(6L, r.Manifest.Serial);
                Assert.IsTrue(r.FromCache);
            }
        }

        [TestMethod]
        public void FreshInstall_CompiledFloor5_RejectsSerial3()
        {
            using (var dir = new TempDir())
            {
                var paths = new AppPaths(dir.Path);
                var fetcher = new FakeHttpFetcher();
                Serve(fetcher, PrimaryUrl, 3);

                ManifestLoadResult r = Load(fetcher, paths, StateStore.Open(paths), compiledFloor: 5);

                Assert.IsNull(r.Manifest);
                Assert.AreEqual(LauncherError.ManifestRollback, r.Attempts[0].Error);
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
                Assert.AreEqual(6L, LoadOffline(paths, state).Manifest.Serial, "the cache must still hold serial 6");
            }
        }

        [TestMethod]
        public void OverflowSchema_IsClassified_NotThrown()
        {
            using (var dir = new TempDir())
            {
                var paths = new AppPaths(dir.Path);
                var fetcher = new FakeHttpFetcher();
                byte[] manifest = TestManifests.BuildRaw("1", "99999999999999999999999");
                fetcher.Serve(PrimaryUrl, manifest);
                fetcher.Serve(PrimaryUrl + ".sig", _key.Sign(manifest).ToFileBytes());

                ManifestLoadResult r = Load(fetcher, paths, StateStore.Open(paths));

                Assert.AreEqual(LauncherError.ManifestSchemaUnsupported, r.Attempts[0].Error);
                Assert.IsNull(r.Manifest);
            }
        }

        [TestMethod]
        public void UnexpectedException_IsRecordedAsInvalid_AndNextSourceTried()
        {
            using (var dir = new TempDir())
            {
                var paths = new AppPaths(dir.Path);
                var fetcher = new FakeHttpFetcher();
                fetcher.Throw(PrimaryUrl + ".sig", new InvalidOperationException("boom"));
                Serve(fetcher, SecondaryUrl, 2);

                ManifestLoadResult r = Load(fetcher, paths, StateStore.Open(paths));

                Assert.AreEqual(LauncherError.ManifestInvalid, r.Attempts[0].Error);
                Assert.AreEqual("secondary", r.SourceName);
            }
        }

        [TestMethod]
        public void Cancellation_Throws()
        {
            using (var dir = new TempDir())
            using (var cts = new CancellationTokenSource())
            {
                var paths = new AppPaths(dir.Path);
                var provider = new ManifestProvider(new FakeHttpFetcher(), new ManifestVerifier(new[] { _key.PublicKey }), paths, StateStore.Open(paths), Sources());
                cts.Cancel();

                var ex = Assert.ThrowsExactly<LauncherException>(() => provider.LoadAsync(cts.Token).GetAwaiter().GetResult());
                Assert.AreEqual(LauncherError.Cancelled, ex.Code);
            }
        }

        [TestMethod]
        public void NothingAnywhere_ReturnsNullManifest()
        {
            using (var dir = new TempDir())
            {
                var paths = new AppPaths(dir.Path);
                ManifestLoadResult r = LoadOffline(paths, StateStore.Open(paths));

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
