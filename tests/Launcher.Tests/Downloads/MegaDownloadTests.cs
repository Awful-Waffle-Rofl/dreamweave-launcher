extern alias MegaApi;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Downloads;
using DreamWeave.Launcher.Core.Manifests;
using MegaApi::CG.Web.MegaApiClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.Downloads
{
    /// <summary>Scripted <see cref="IMegaClient"/>: writes the content in chunks, optionally failing partway.</summary>
    internal sealed class FakeMegaClient : IMegaClient
    {
        private readonly byte[] _content;
        private readonly long _reportedSize;
        private readonly Func<int, Exception> _failAfterHalf;
        private readonly Func<Exception> _failOnLink;
        private readonly int _attempt;

        public FakeMegaClient(int attempt, byte[] content, long? reportedSize = null, Func<int, Exception> failAfterHalf = null, Func<Exception> failOnLink = null)
        {
            _attempt = attempt;
            _content = content;
            _reportedSize = reportedSize ?? content.Length;
            _failAfterHalf = failAfterHalf;
            _failOnLink = failOnLink;
        }

        public bool DownloadCalled { get; private set; }

        public bool PartExistedWhenFailing { get; private set; }

        public Task LoginAnonymousAsync()
        {
            return Task.CompletedTask;
        }

        public Task<MegaFile> GetFileFromLinkAsync(Uri link)
        {
            Exception ex = _failOnLink?.Invoke();
            if (ex != null)
            {
                return Task.FromException<MegaFile>(ex);
            }

            return Task.FromResult(new MegaFile("ac-updates.zip", _reportedSize, null));
        }

        public Task DownloadFileAsync(MegaFile file, string path, IProgress<double> percent, CancellationToken cancellationToken)
        {
            DownloadCalled = true;
            return Task.Run(() =>
            {
                using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                {
                    int chunk = 64 * 1024;
                    for (int offset = 0; offset < _content.Length; offset += chunk)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int n = Math.Min(chunk, _content.Length - offset);
                        fs.Write(_content, offset, n);
                        percent.Report((offset + n) * 100.0 / _content.Length);
                        if (offset + n >= _content.Length / 2 && _failAfterHalf != null)
                        {
                            Exception ex = _failAfterHalf(_attempt);
                            if (ex != null)
                            {
                                fs.Flush();
                                PartExistedWhenFailing = File.Exists(path);
                                throw ex;
                            }
                        }
                    }
                }
            });
        }

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Models an unresponsive MegaApiClient read: opens the part exactly as the library does
    /// (CreateNew, share Read), writes half, then IGNORES the token and blocks until
    /// <c>release</c> is set. Optionally keeps invoking the percent callback after release,
    /// after closing the file.
    /// </summary>
    internal sealed class StallingMegaClient : IMegaClient
    {
        public const double LatePercent = 77.7;

        private readonly byte[] _content;
        private readonly ManualResetEventSlim _release;
        private readonly TimeSpan _reportAfterRelease;

        public StallingMegaClient(byte[] content, ManualResetEventSlim release, TimeSpan reportAfterRelease)
        {
            _content = content;
            _release = release;
            _reportAfterRelease = reportAfterRelease;
        }

        public int LateReports { get; private set; }

        public Task LoginAnonymousAsync() => Task.CompletedTask;

        public Task<MegaFile> GetFileFromLinkAsync(Uri link) => Task.FromResult(new MegaFile("ac-updates.zip", _content.Length, null));

        public Task DownloadFileAsync(MegaFile file, string path, IProgress<double> percent, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                {
                    fs.Write(_content, 0, _content.Length / 2);
                    fs.Flush();
                    Thread.Sleep(150); // outlast the tracker's 100 ms throttle so this report is emitted
                    percent.Report(50);
                    _release.Wait();
                }

                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.Elapsed < _reportAfterRelease)
                {
                    percent.Report(LatePercent);
                    LateReports++;
                    Thread.Sleep(10);
                }
            });
        }

        public void Dispose()
        {
        }
    }

    /// <summary>Writes a little, then waits on the token (a cooperative library read).</summary>
    internal sealed class CancellableMegaClient : IMegaClient
    {
        private readonly byte[] _content;
        private readonly Action _started;

        public CancellableMegaClient(byte[] content, Action started)
        {
            _content = content;
            _started = started;
        }

        public Task LoginAnonymousAsync() => Task.CompletedTask;

        public Task<MegaFile> GetFileFromLinkAsync(Uri link) => Task.FromResult(new MegaFile("ac-updates.zip", _content.Length, null));

        public Task DownloadFileAsync(MegaFile file, string path, IProgress<double> percent, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                {
                    fs.Write(_content, 0, 1000);
                    percent.Report(1);
                    _started();
                    Task.Delay(Timeout.Infinite, cancellationToken).Wait();
                }
            });
        }

        public void Dispose()
        {
        }
    }

    [TestClass]
    public class MegaDownloadTests
    {
        /// <summary>
        /// Stall rig: attempt 0 stalls and is abandoned while still holding the part; the second
        /// back-off wait releases it. Returns the options so a test can inspect the waits.
        /// </summary>
        private static MegaTransportOptions StallOptions(List<TimeSpan> waits, ManualResetEventSlim release)
        {
            return new MegaTransportOptions
            {
                InactivityTimeout = TimeSpan.FromMilliseconds(300),
                AbandonGrace = TimeSpan.FromMilliseconds(50),
                Retry = new RetryPolicy
                {
                    Delay = (d, ct) =>
                    {
                        int n;
                        lock (waits)
                        {
                            waits.Add(d);
                            n = waits.Count;
                        }

                        if (n == 2)
                        {
                            release.Set();
                        }

                        return n >= 2 ? Task.Delay(300, ct) : Task.CompletedTask;
                    },
                },
            };
        }

        [TestMethod]
        public void StallWatchdog_AbandonsHeldPart_RetriesAsTransient_AndSucceedsAfterRelease()
        {
            byte[] content = DownloadFixtures.Content(Size);
            var waits = new List<TimeSpan>();
            using (var release = new ManualResetEventSlim(false))
            using (var rig = new Rig(content, i => i == 0 ? (IMegaClient)new StallingMegaClient(content, release, TimeSpan.Zero) : new FakeMegaClient(i, content), StallOptions(waits, release)))
            {
                Assert.AreEqual(LauncherError.None, rig.Run(), "a part still held by the abandoned read must be retried, not fail as Unknown");
                Assert.AreEqual(2, waits.Count, "one wait after the stall, one after the still-held part");
                Assert.AreEqual(2, rig.Clients.Count, "the still-held attempt fails before creating a client");
                CollectionAssert.AreEqual(content, File.ReadAllBytes(rig.Request.DestinationPath));
            }
        }

        [TestMethod]
        public void AbandonedAttempt_LateProgressCallbacks_AreDropped()
        {
            byte[] content = DownloadFixtures.Content(Size);
            var waits = new List<TimeSpan>();
            StallingMegaClient stalled = null;
            using (var release = new ManualResetEventSlim(false))
            using (var rig = new Rig(
                content,
                i =>
                {
                    if (i == 0)
                    {
                        stalled = new StallingMegaClient(content, release, TimeSpan.FromMilliseconds(800));
                        return stalled;
                    }

                    return new FakeMegaClient(i, content);
                },
                StallOptions(waits, release)))
            {
                Assert.AreEqual(LauncherError.None, rig.Run());
                SpinWait.SpinUntil(() => stalled.LateReports > 0, 2000);
                Thread.Sleep(900);
                Assert.IsTrue(stalled.LateReports > 10, "the fake really did keep calling back: " + stalled.LateReports);
                long late = (long)Math.Round(StallingMegaClient.LatePercent / 100.0 * Size);
                List<ProgressReport> dl = rig.Progress.Downloading;
                Assert.IsTrue(dl.Any(r => r.BytesDone == Size / 2), "positive control: the attempt's own report before the stall got through");
                Assert.IsFalse(dl.Any(r => r.BytesDone == late), "a detached attempt reported after abandon");
                Assert.AreEqual(Size, dl.Last().BytesDone);
            }
        }

        [TestMethod]
        public void Cancellation_DeletesPart_AndReportsCancelled()
        {
            byte[] content = DownloadFixtures.Content(Size);
            using (var cts = new CancellationTokenSource())
            using (var rig = new Rig(content, i => new CancellableMegaClient(content, () => cts.CancelAfter(50))))
            {
                Assert.AreEqual(LauncherError.Cancelled, rig.Run(cts.Token));
                Assert.AreEqual(1, rig.Clients.Count);
                Assert.IsFalse(File.Exists(rig.Request.PartPath), "mega cannot resume, so a cancelled part is deleted");
                Assert.IsFalse(File.Exists(rig.Request.DestinationPath));
            }
        }

        private const int Size = 400000;

        internal static ApiException Api(ApiResultCode code)
        {
            return (ApiException)Activator.CreateInstance(typeof(ApiException), BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { code }, null);
        }

        private sealed class Rig : IDisposable
        {
            private readonly TempDir _dir = new TempDir();

            public Rig(byte[] content, Func<int, IMegaClient> factory, MegaTransportOptions options = null)
            {
                Content = content;
                Staging = Path.Combine(_dir.Path, "staging");
                var transport = new MegaTransport(
                    () =>
                    {
                        IMegaClient c = factory(Clients.Count);
                        Clients.Add(c);
                        return c;
                    },
                    options ?? new MegaTransportOptions { Retry = DownloadFixtures.InstantRetry(Waits) });
                Service = new DownloadService(Staging, new FakeDiskSpace(long.MaxValue), new IDownloadTransport[] { transport });
                Request = new DownloadRequest("eor-dats", Transports.Mega, new Uri("https://mega.nz/file/abc#key"), content.Length, Hasher.ComputeSha256Hex(content), Path.Combine(Staging, "ac-updates.zip"));
            }

            public byte[] Content { get; }

            public string Staging { get; }

            public List<IMegaClient> Clients { get; } = new List<IMegaClient>();

            public List<FakeMegaClient> Fakes => Clients.OfType<FakeMegaClient>().ToList();

            public List<TimeSpan> Waits { get; } = new List<TimeSpan>();

            public DownloadService Service { get; }

            public DownloadRequest Request { get; }

            public ListProgress Progress { get; } = new ListProgress();

            public DownloadResult Result { get; private set; }

            public LauncherError Run(CancellationToken token = default(CancellationToken))
            {
                return DownloadFixtures.Run(async () => { Result = await Service.DownloadAsync(Request, Progress, token).ConfigureAwait(false); });
            }

            public void Dispose()
            {
                _dir.Dispose();
            }
        }

        [TestMethod]
        public void Success_VerifiesHash_AndReportsBytesFromPercent()
        {
            byte[] content = DownloadFixtures.Content(Size);
            using (var rig = new Rig(content, i => new FakeMegaClient(i, content)))
            {
                Assert.AreEqual(LauncherError.None, rig.Run());
                CollectionAssert.AreEqual(content, File.ReadAllBytes(rig.Request.DestinationPath));
                Assert.IsFalse(File.Exists(rig.Request.PartPath));
                Assert.AreEqual(Hasher.ComputeSha256Hex(content), rig.Result.Sha256);
                List<ProgressReport> dl = rig.Progress.Downloading;
                Assert.AreEqual(Size, dl.Last().BytesDone);
                Assert.IsTrue(dl.All(r => r.BytesTotal == Size && r.StepId == "eor-dats"));
                for (int i = 1; i < dl.Count; i++)
                {
                    Assert.IsTrue(dl[i].BytesDone >= dl[i - 1].BytesDone);
                }
            }
        }

        [TestMethod]
        public void MidDownloadFailure_DeletesPart_AndRetriesFromZero()
        {
            byte[] content = DownloadFixtures.Content(Size);
            using (var rig = new Rig(content, i => new FakeMegaClient(i, content, failAfterHalf: attempt => attempt == 0 ? new IOException("connection reset", new System.Net.Sockets.SocketException(10054)) : null)))
            {
                Assert.AreEqual(LauncherError.None, rig.Run());
                Assert.AreEqual(2, rig.Clients.Count);
                Assert.IsTrue(rig.Fakes[0].PartExistedWhenFailing);
                Assert.AreEqual(1, rig.Waits.Count);
                CollectionAssert.AreEqual(content, File.ReadAllBytes(rig.Request.DestinationPath));
            }
        }

        [TestMethod]
        public void PersistentNetworkFailure_DeletesPart_AfterRetries()
        {
            byte[] content = DownloadFixtures.Content(Size);
            using (var rig = new Rig(content, i => new FakeMegaClient(i, content, failAfterHalf: attempt => new IOException("connection reset", new System.Net.Sockets.SocketException(10054)))))
            {
                Assert.AreEqual(LauncherError.NetworkUnavailable, rig.Run());
                Assert.AreEqual(4, rig.Clients.Count, "first attempt plus 3 retries");
                Assert.IsTrue(rig.Fakes.All(c => c.PartExistedWhenFailing));
                Assert.IsFalse(File.Exists(rig.Request.PartPath), "mega cannot resume, so the part is deleted");
                Assert.IsFalse(File.Exists(rig.Request.DestinationPath));
            }
        }

        [TestMethod]
        public void TransferQuota_ApiException_MapsToQuotaExceeded_WithoutRetry()
        {
            byte[] content = DownloadFixtures.Content(Size);
            using (var rig = new Rig(content, i => new FakeMegaClient(i, content, failAfterHalf: attempt => Api(ApiResultCode.QuotaExceeded))))
            {
                Assert.AreEqual(LauncherError.SourceQuotaExceeded, rig.Run());
                Assert.AreEqual(1, rig.Clients.Count);
                Assert.IsFalse(File.Exists(rig.Request.PartPath));
            }
        }

        [TestMethod]
        public void BandwidthLimit509_FromDataUrl_MapsToQuotaExceeded()
        {
            byte[] content = DownloadFixtures.Content(Size);
            Func<int, Exception> fail = attempt => new AggregateException(new HttpRequestException("Response status code does not indicate success: 509 (Bandwidth Limit Exceeded)."));
            using (var rig = new Rig(content, i => new FakeMegaClient(i, content, failAfterHalf: fail)))
            {
                Assert.AreEqual(LauncherError.SourceQuotaExceeded, rig.Run());
                Assert.AreEqual(1, rig.Clients.Count);
            }
        }

        [TestMethod]
        public void SizeMismatch_FailsBeforeDownloading()
        {
            byte[] content = DownloadFixtures.Content(Size);
            using (var rig = new Rig(content, i => new FakeMegaClient(i, content, reportedSize: Size + 1)))
            {
                Assert.AreEqual(LauncherError.SizeMismatch, rig.Run());
                Assert.AreEqual(1, rig.Clients.Count);
                Assert.IsFalse(rig.Fakes[0].DownloadCalled);
                Assert.IsFalse(File.Exists(rig.Request.PartPath));
            }
        }

        [TestMethod]
        public void DeadLink_IsSourceHttpError_WithoutRetry()
        {
            byte[] content = DownloadFixtures.Content(Size);
            using (var rig = new Rig(content, i => new FakeMegaClient(i, content, failOnLink: () => Api(ApiResultCode.ResourceNotExists))))
            {
                Assert.AreEqual(LauncherError.SourceHttpError, rig.Run());
                Assert.AreEqual(1, rig.Clients.Count);
            }
        }

        [TestMethod]
        public void MegaChecksumFailure_IsRetried_ThenHashMismatch()
        {
            byte[] content = DownloadFixtures.Content(Size);
            using (var rig = new Rig(content, i => new FakeMegaClient(i, content, failAfterHalf: attempt => new DownloadException())))
            {
                Assert.AreEqual(LauncherError.HashMismatch, rig.Run());
                Assert.AreEqual(4, rig.Clients.Count);
                Assert.IsFalse(File.Exists(rig.Request.PartPath));
            }
        }

        [TestMethod]
        public void WrongBytes_FailOurHashCheck_AndAreDeleted()
        {
            byte[] content = DownloadFixtures.Content(Size);
            byte[] other = DownloadFixtures.Content(Size, seed: 7);
            using (var rig = new Rig(content, i => new FakeMegaClient(i, other)))
            {
                Assert.AreEqual(LauncherError.HashMismatch, rig.Run());
                Assert.IsFalse(File.Exists(rig.Request.PartPath));
                Assert.IsFalse(File.Exists(rig.Request.DestinationPath));
            }
        }
    }
}
