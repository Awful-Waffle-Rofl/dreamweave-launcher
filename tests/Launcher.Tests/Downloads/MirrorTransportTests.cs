extern alias MegaApi;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Downloads;
using DreamWeave.Launcher.Core.Manifests;
using MegaApi::CG.Web.MegaApiClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.Downloads
{
    /// <summary>The real mega and HTTP transports behind one DownloadService with a mirror.</summary>
    [TestClass]
    public class MirrorTransportTests
    {
        private const int Size = 200000;

        /// <summary>Writes the content and reports 100% after a delay, with no progress before it.</summary>
        private sealed class SlowStartMegaClient : IMegaClient
        {
            private readonly byte[] _content;
            private readonly TimeSpan _delay;

            public SlowStartMegaClient(byte[] content, TimeSpan delay)
            {
                _content = content;
                _delay = delay;
            }

            public Task LoginAnonymousAsync() => Task.CompletedTask;

            public Task<MegaFile> GetFileFromLinkAsync(Uri link) => Task.FromResult(new MegaFile("f", _content.Length, null));

            public async Task DownloadFileAsync(MegaFile file, string path, IProgress<double> percent, CancellationToken cancellationToken)
            {
                await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
                File.WriteAllBytes(path, _content);
                percent.Report(100);
            }

            public void Dispose()
            {
            }
        }

        /// <summary>Never answers login, or never starts the download, until cancelled.</summary>
        private sealed class HangingMegaClient : IMegaClient
        {
            private readonly bool _hangOnLogin;

            public HangingMegaClient(bool hangOnLogin)
            {
                _hangOnLogin = hangOnLogin;
            }

            public Task LoginAnonymousAsync() => _hangOnLogin ? new TaskCompletionSource<bool>().Task : Task.CompletedTask;

            public Task<MegaFile> GetFileFromLinkAsync(Uri link) => Task.FromResult(new MegaFile("f", Size, null));

            public Task DownloadFileAsync(MegaFile file, string path, IProgress<double> percent, CancellationToken cancellationToken)
            {
                return Task.Delay(Timeout.Infinite, cancellationToken);
            }

            public void Dispose()
            {
            }
        }

        /// <summary>Writes a small file in one go and closes it at once, like a fast retry would.</summary>
        private sealed class QuickWriteMegaClient : IMegaClient
        {
            private readonly byte[] _content;

            public QuickWriteMegaClient(byte[] content)
            {
                _content = content;
            }

            public Task LoginAnonymousAsync() => Task.CompletedTask;

            public Task<MegaFile> GetFileFromLinkAsync(Uri link) => Task.FromResult(new MegaFile("f", _content.Length, null));

            public Task DownloadFileAsync(MegaFile file, string path, IProgress<double> percent, CancellationToken cancellationToken)
            {
                return Task.Run(() =>
                {
                    using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                    {
                        fs.Write(_content, 0, _content.Length);
                    }

                    percent.Report(100);
                });
            }

            public void Dispose()
            {
            }
        }

        [TestMethod]
        [Timeout(180000)]
        public void LateCleanupOfAnAbandonedRead_NeverDeletesTheNextAttemptsPart()
        {
            // The abandoned read lets go just as the retry starts; the retry writes its part and closes it at
            // once. The late clean-up of the old read must not take the new file with it.
            byte[] content = DownloadFixtures.Content(50 * 1024);
            int losses = 0;
            for (int iteration = 0; iteration < 40; iteration++)
            {
                using (var dir = new TempDir())
                using (var release = new ManualResetEventSlim(false))
                {
                    var options = new MegaTransportOptions
                    {
                        InactivityTimeout = TimeSpan.FromMilliseconds(80),
                        FirstProgressTimeout = TimeSpan.FromSeconds(30),
                        AbandonGrace = TimeSpan.FromMilliseconds(20),
                        Retry = new RetryPolicy
                        {
                            MaxRetries = 6,
                            Delay = (d, ct) =>
                            {
                                release.Set();
                                return Task.CompletedTask;
                            },
                        },
                    };
                    int made = 0;
                    var transport = new MegaTransport(
                        () => Interlocked.Increment(ref made) == 1 ? (IMegaClient)new StallingMegaClient(content, release, TimeSpan.Zero) : new QuickWriteMegaClient(content),
                        options);
                    string dest = Path.Combine(dir.Path, "ac-client", "x.exe");
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    var request = new DownloadRequest("ac-client", Transports.Mega, new Uri("https://mega.nz/file/abc#key"), content.Length, Hasher.ComputeSha256Hex(content), dest);
                    transport.DownloadToPartAsync(request, new TransferProgress("ac-client", content.Length, null), CancellationToken.None).GetAwaiter().GetResult();

                    Thread.Sleep(60); // let any late clean-up run
                    if (!File.Exists(request.PartPath) || new FileInfo(request.PartPath).Length != content.Length)
                    {
                        losses++;
                    }
                }
            }

            Assert.AreEqual(0, losses, "the late clean-up removed the next attempt's part in " + losses + " of 40 runs");
        }

        private sealed class Rig : IDisposable
        {
            private readonly TempDir _dir = new TempDir();

            public Rig(Func<int, IMegaClient> megaFactory, MegaTransportOptions megaOptions, FakeHttpServer mirror, byte[] content)
            {
                Content = content;
                Staging = Path.Combine(_dir.Path, "staging");
                var mega = new MegaTransport(() =>
                {
                    IMegaClient c = megaFactory(Clients.Count);
                    Clients.Add(c);
                    return c;
                }, megaOptions);
                Service = new DownloadService(Staging, new FakeDiskSpace(long.MaxValue), new IDownloadTransport[] { mega, new HttpTransport(DownloadFixtures.Client(), new HttpTransportOptions { Retry = DownloadFixtures.InstantRetry(new List<TimeSpan>()) }) });
                Dest = Path.Combine(DownloadRequest.ComponentFolder(Staging, "ac-client"), "ac-client.exe");
                Request = new DownloadRequest("ac-client", Transports.Mega, new Uri("https://mega.nz/file/abc#key"), content.Length, Hasher.ComputeSha256Hex(content), Dest);
                if (mirror != null)
                {
                    Request.Alternates = new[] { new DownloadSource(Transports.Http, mirror.FileUri, "Mirror") };
                }
            }

            public byte[] Content { get; }

            public string Staging { get; }

            public string Dest { get; }

            public DownloadService Service { get; }

            public DownloadRequest Request { get; }

            public List<IMegaClient> Clients { get; } = new List<IMegaClient>();

            public LauncherError Run(CancellationToken ct = default(CancellationToken))
            {
                return DownloadFixtures.Run(() => Service.DownloadAsync(Request, null, ct));
            }

            public void Dispose() => _dir.Dispose();
        }

        private static MegaTransportOptions Options(TimeSpan inactivity, TimeSpan first)
        {
            return new MegaTransportOptions
            {
                InactivityTimeout = inactivity,
                FirstProgressTimeout = first,
                ApiTimeout = TimeSpan.FromMilliseconds(200),
                AbandonGrace = TimeSpan.FromMilliseconds(50),
                Retry = new RetryPolicy { MaxRetries = 0 },
            };
        }

        [TestMethod]
        [Timeout(60000)]
        public void StalledMegaRead_StillHoldingItsPart_SwitchesToTheHttpMirror()
        {
            // The abandoned mega read keeps its .part open (share Read); the mirror writes its own file.
            byte[] content = DownloadFixtures.Content(Size);
            using (var release = new ManualResetEventSlim(false))
            using (var server = new FakeHttpServer((ctx, i) => Serve.FileAsync(ctx, content)))
            using (var rig = new Rig(i => new StallingMegaClient(content, release, TimeSpan.Zero), Options(TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(300)), server, content))
            {
                try
                {
                    Assert.AreEqual(LauncherError.None, rig.Run());
                    CollectionAssert.AreEqual(content, File.ReadAllBytes(rig.Dest));
                    string folder = Path.GetDirectoryName(rig.Dest);
                    Assert.AreEqual(0, Directory.GetFiles(folder, "*.s*.part*").Length, "no mirror part or sidecar is left after success");
                }
                finally
                {
                    release.Set();
                }

                // Once the abandoned read lets go, its leftover primary part and sidecar are removed too.
                Stopwatch wait = Stopwatch.StartNew();
                while (File.Exists(rig.Dest + ".part") && wait.Elapsed < TimeSpan.FromSeconds(10))
                {
                    Thread.Sleep(20);
                }

                Assert.IsFalse(File.Exists(rig.Dest + ".part"), "the abandoned mega part is left in staging");
                Assert.IsFalse(File.Exists(rig.Dest + ".part.json"));
                Assert.AreEqual(1, Directory.GetFiles(Path.GetDirectoryName(rig.Dest)).Length, "only the verified file remains");
            }
        }

        [TestMethod]
        [Timeout(30000)]
        public void DeadMega_WithAMirror_FailsOverAfterOneFirstProgressTimeout_NotFour()
        {
            byte[] content = DownloadFixtures.Content(Size);
            var options = Options(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(600));
            options.Retry = new RetryPolicy { Delay = (d, ct) => Task.CompletedTask };
            using (var server = new FakeHttpServer((ctx, i) => Serve.FileAsync(ctx, content)))
            using (var rig = new Rig(i => new HangingMegaClient(false), options, server, content))
            {
                Stopwatch clock = Stopwatch.StartNew();
                Assert.AreEqual(LauncherError.None, rig.Run());
                Assert.AreEqual(1, rig.Clients.Count, "a dead primary must not be retried when a mirror is waiting");
                Assert.IsTrue(clock.ElapsedMilliseconds >= 550 && clock.ElapsedMilliseconds < 1800, "about one timeout, not four: " + clock.ElapsedMilliseconds + " ms");
                CollectionAssert.AreEqual(content, File.ReadAllBytes(rig.Dest));
            }
        }

        [TestMethod]
        [Timeout(30000)]
        public void DeadMega_WithNoMirror_StillRetries()
        {
            byte[] content = DownloadFixtures.Content(Size);
            var options = Options(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(300));
            options.Retry = new RetryPolicy { Delay = (d, ct) => Task.CompletedTask };
            using (var rig = new Rig(i => new HangingMegaClient(false), options, null, content))
            {
                Assert.AreEqual(LauncherError.NetworkUnavailable, rig.Run());
                Assert.AreEqual(4, rig.Clients.Count);
            }
        }

        [TestMethod]
        [Timeout(30000)]
        public void MegaSlowStart_IsAllowedBeyondTheInactivityWindow()
        {
            byte[] content = DownloadFixtures.Content(Size);
            using (var rig = new Rig(i => new SlowStartMegaClient(content, TimeSpan.FromMilliseconds(700)), Options(TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(10)), null, content))
            {
                Assert.AreEqual(LauncherError.None, rig.Run());
                Assert.AreEqual(1, rig.Clients.Count, "no stall retry before the first byte arrived");
            }
        }

        [TestMethod]
        public void MegaThatNeverStarts_FailsAfterTheFirstProgressTimeout()
        {
            byte[] content = DownloadFixtures.Content(Size);
            using (var rig = new Rig(i => new HangingMegaClient(false), Options(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500)), null, content))
            {
                Stopwatch clock = Stopwatch.StartNew();
                Assert.AreEqual(LauncherError.NetworkUnavailable, rig.Run());
                Assert.IsTrue(clock.ElapsedMilliseconds >= 450, "gave up before the first-progress window: " + clock.ElapsedMilliseconds);
            }
        }

        [TestMethod]
        public void MegaLoginThatNeverAnswers_FailsAsNetworkUnavailableAfterTheApiTimeout()
        {
            byte[] content = DownloadFixtures.Content(Size);
            using (var rig = new Rig(i => new HangingMegaClient(true), Options(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60)), null, content))
            {
                Stopwatch clock = Stopwatch.StartNew();
                Assert.AreEqual(LauncherError.NetworkUnavailable, rig.Run());
                Assert.IsTrue(clock.ElapsedMilliseconds >= 150 && clock.ElapsedMilliseconds < 5000, clock.ElapsedMilliseconds + " ms");
            }
        }

        [TestMethod]
        public void CorruptMegaDownload_WithAMirror_FallsBackAtOnceInsteadOfRetrying()
        {
            byte[] content = DownloadFixtures.Content(Size);
            var options = new MegaTransportOptions { Retry = new RetryPolicy { Delay = (d, ct) => Task.CompletedTask } };
            using (var server = new FakeHttpServer((ctx, i) => Serve.FileAsync(ctx, content)))
            using (var rig = new Rig(i => new FakeMegaClient(i, content, failAfterHalf: attempt => new DownloadException()), options, server, content))
            {
                Assert.AreEqual(LauncherError.None, rig.Run());
                Assert.AreEqual(1, rig.Clients.Count, "a corrupt mega file must not be retried when a mirror is available");
                CollectionAssert.AreEqual(content, File.ReadAllBytes(rig.Dest));
            }
        }

        [TestMethod]
        public void CorruptMegaDownload_WithNoMirror_StillRetries()
        {
            byte[] content = DownloadFixtures.Content(Size);
            var options = new MegaTransportOptions { Retry = new RetryPolicy { Delay = (d, ct) => Task.CompletedTask } };
            using (var rig = new Rig(i => new FakeMegaClient(i, content, failAfterHalf: attempt => new DownloadException()), options, null, content))
            {
                Assert.AreEqual(LauncherError.HashMismatch, rig.Run());
                Assert.AreEqual(4, rig.Clients.Count, "1 attempt plus 3 retries, as before");
            }
        }
    }
}