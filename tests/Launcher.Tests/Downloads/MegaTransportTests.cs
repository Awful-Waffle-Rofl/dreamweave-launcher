using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Downloads;
using DreamWeave.Launcher.Core.Manifests;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.Downloads
{
    /// <summary>
    /// <see cref="MegaTransport"/> against a loopback storage server that serves an AES-CTR encrypted
    /// fixture with HTTP ranges, with throttling and failure injection per request.
    /// </summary>
    [TestClass]
    public class MegaTransportTests
    {
        private const int Size = 256 * 1024;
        private const int Chunk = 32 * 1024;

        /// <summary>A plaintext, its mega-style encryption and the link that decrypts it.</summary>
        internal sealed class MegaFixture
        {
            public MegaFixture(int size, int seed = 99)
            {
                Plain = DownloadFixtures.Content(size, seed);
                var raw = new byte[32];
                new Random(seed + 1).NextBytes(raw);
                Link = new Uri(MegaLink.Format("TESTFILE", raw));
                MegaLink parsed;
                Assert.IsTrue(MegaLink.TryParse(Link, out parsed));
                Encrypted = (byte[])Plain.Clone();
                using (var ctr = new MegaCtr(parsed.Key, parsed.Nonce))
                {
                    ctr.Transform(Encrypted, 0, Encrypted.Length, 0);
                }
            }

            public byte[] Plain { get; }

            public byte[] Encrypted { get; }

            public Uri Link { get; }
        }

        /// <summary>Hands out the storage server's URL; counts calls; can hang or fail.</summary>
        private sealed class FakeApi : IMegaApi
        {
            private int _calls;

            public Uri Url { get; set; }

            public long Size { get; set; }

            public Func<int, Exception> Fail { get; set; }

            public bool Hang { get; set; }

            public int Calls => Volatile.Read(ref _calls);

            public async Task<MegaDownloadTicket> GetDownloadAsync(string handle, CancellationToken cancellationToken)
            {
                int n = Interlocked.Increment(ref _calls);
                Assert.AreEqual("TESTFILE", handle);
                if (Hang)
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                }

                Exception e = Fail?.Invoke(n);
                if (e != null)
                {
                    throw e;
                }

                return new MegaDownloadTicket(Url, Size);
            }
        }

        /// <summary>Per-request behaviour of the storage server.</summary>
        private sealed class RangeServer : IDisposable
        {
            private readonly FakeHttpServer _server;
            private int _active;
            private int _maxActive;

            public RangeServer(byte[] body, Func<RangeCall, Task> behave = null)
            {
                Body = body;
                _server = new FakeHttpServer(async (ctx, i) =>
                {
                    int now = Interlocked.Increment(ref _active);
                    int seen;
                    while ((seen = Volatile.Read(ref _maxActive)) < now && Interlocked.CompareExchange(ref _maxActive, now, seen) != seen)
                    {
                    }

                    try
                    {
                        var call = new RangeCall(this, ctx, i);
                        if (behave != null)
                        {
                            await behave(call).ConfigureAwait(false);
                        }

                        if (!call.Handled)
                        {
                            await call.SendAsync().ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _active);
                    }
                });
            }

            public byte[] Body { get; }

            public Uri Url => _server.FileUri;

            public int MaxConcurrent => Volatile.Read(ref _maxActive);

            public ConcurrentQueue<long> Completed { get; } = new ConcurrentQueue<long>();

            public IReadOnlyList<SeenRequest> Requests => _server.Requests;

            public CancellationToken Stopping => _server.Stopping;

            public void Dispose() => _server.Dispose();
        }

        private sealed class RangeCall
        {
            private readonly RangeServer _owner;

            public RangeCall(RangeServer owner, HttpListenerContext ctx, int index)
            {
                _owner = owner;
                Context = ctx;
                Index = index;
                string range = ctx.Request.Headers["Range"];
                From = 0;
                To = owner.Body.Length - 1;
                if (range != null && range.StartsWith("bytes=", StringComparison.Ordinal))
                {
                    string[] parts = range.Substring(6).Split('-');
                    From = long.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
                    if (parts[1].Length > 0)
                    {
                        To = Math.Min(To, long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
                    }
                }
            }

            public HttpListenerContext Context { get; }

            public int Index { get; }

            public long From { get; }

            public long To { get; }

            public bool Handled { get; private set; }

            /// <summary>Sends the range (206) with optional pacing, truncation, a stall, or corruption.</summary>
            public async Task SendAsync(int delayBeforeMs = 0, int pacePerWriteMs = 0, int truncateAfter = -1, int stallAfter = -1, bool corrupt = false)
            {
                Handled = true;
                HttpListenerResponse resp = Context.Response;
                if (delayBeforeMs > 0)
                {
                    await Task.Delay(delayBeforeMs).ConfigureAwait(false);
                }

                int count = (int)(To - From + 1);
                var slice = new byte[count];
                Buffer.BlockCopy(_owner.Body, (int)From, slice, 0, count);
                if (corrupt)
                {
                    slice[count / 2] ^= 0xFF;
                }

                resp.StatusCode = 206;
                resp.Headers["Content-Range"] = "bytes " + From + "-" + To + "/" + _owner.Body.Length;
                resp.ContentLength64 = count;
                int sent = 0;
                const int Piece = 4096;
                while (sent < count)
                {
                    if (truncateAfter >= 0 && sent >= truncateAfter)
                    {
                        await resp.OutputStream.FlushAsync().ConfigureAwait(false);
                        await Task.Delay(50).ConfigureAwait(false);
                        resp.Abort();
                        return;
                    }

                    if (stallAfter >= 0 && sent >= stallAfter)
                    {
                        await resp.OutputStream.FlushAsync().ConfigureAwait(false);
                        try
                        {
                            await Task.Delay(Timeout.Infinite, _owner.Stopping).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                        }

                        resp.Abort();
                        return;
                    }

                    int n = Math.Min(Piece, count - sent);
                    try
                    {
                        await resp.OutputStream.WriteAsync(slice, sent, n).ConfigureAwait(false);
                        await resp.OutputStream.FlushAsync().ConfigureAwait(false);
                    }
                    catch (HttpListenerException)
                    {
                        return;
                    }

                    sent += n;
                    if (pacePerWriteMs > 0)
                    {
                        await Task.Delay(pacePerWriteMs).ConfigureAwait(false);
                    }
                }

                resp.Close();
                _owner.Completed.Enqueue(From);
            }

            public CancellationToken Stopping => _owner.Stopping;

            public Task StatusAsync(int status, string retryAfter = null)
            {
                Handled = true;
                return Serve.StatusAsync(Context, status, retryAfter);
            }
        }

        /// <summary>Counts responses that are open at the same time, as the transport sees them (request sent to response disposed).</summary>
        private sealed class CountingClient : DreamWeave.Launcher.Core.Net.IHttpStreamClient
        {
            private readonly DreamWeave.Launcher.Core.Net.IHttpStreamClient _inner;

            public CountingClient(TimeSpan? headerTimeout = null)
            {
                _inner = headerTimeout.HasValue
                    ? new DreamWeave.Launcher.Core.Net.HttpStreamClient(new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None }, true, headerTimeout.Value)
                    : (DreamWeave.Launcher.Core.Net.IHttpStreamClient)DownloadFixtures.Client();
            }
            private int _open;
            private int _max;

            public int MaxOpen => Volatile.Read(ref _max);

            public async Task<DreamWeave.Launcher.Core.Net.HttpStreamResponse> GetAsync(Uri uri, long? rangeFrom, long? rangeTo, string ifRange, CancellationToken cancellationToken)
            {
                int now = Interlocked.Increment(ref _open);
                int seen;
                while ((seen = Volatile.Read(ref _max)) < now && Interlocked.CompareExchange(ref _max, now, seen) != seen)
                {
                }

                DreamWeave.Launcher.Core.Net.HttpStreamResponse r;
                try
                {
                    r = await _inner.GetAsync(uri, rangeFrom, rangeTo, ifRange, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    Interlocked.Decrement(ref _open);
                    throw;
                }

                int released = 0;
                return new DreamWeave.Launcher.Core.Net.HttpStreamResponse(r.StatusCode, r.FinalUri, r.ContentLength, r.ContentRangeFrom, r.ContentRangeTo, r.ContentRangeTotal, r.ETag, r.LastModified, r.RetryAfter, r.OpenBodyAsync, new Releaser(() =>
                {
                    if (Interlocked.Exchange(ref released, 1) == 0)
                    {
                        Interlocked.Decrement(ref _open);
                    }

                    r.Dispose();
                }));
            }

            private sealed class Releaser : IDisposable
            {
                private readonly Action _a;

                public Releaser(Action a) => _a = a;

                public void Dispose() => _a();
            }
        }

        private sealed class Rig : IDisposable
        {
            private readonly TempDir _dir = new TempDir();

            public Rig(MegaFixture fixture, RangeServer storage, MegaTransportOptions options = null, FakeHttpServer mirror = null, TimeSpan? headerTimeout = null)
            {
                Http = new CountingClient(headerTimeout);
                Fixture = fixture;
                Api = new FakeApi { Url = storage?.Url ?? new Uri("http://localhost:1/none"), Size = fixture.Plain.Length };
                Options = options ?? Fast();
                Transport = new MegaTransport(Api, Http, Options);
                var transports = new List<IDownloadTransport> { Transport };
                if (mirror != null)
                {
                    transports.Add(new HttpTransport(DownloadFixtures.Client(), new HttpTransportOptions { Retry = DownloadFixtures.InstantRetry(new List<TimeSpan>()) }));
                }

                Staging = Path.Combine(_dir.Path, "staging");
                Service = new DownloadService(Staging, new FakeDiskSpace(long.MaxValue), transports, Log);
                Dest = Path.Combine(DownloadRequest.ComponentFolder(Staging, "eor-dats"), "eor-dats.zip");
                Request = new DownloadRequest("eor-dats", Transports.Mega, fixture.Link, fixture.Plain.Length, Hasher.ComputeSha256Hex(fixture.Plain), Dest);
                if (mirror != null)
                {
                    Request.Alternates = new[] { new DownloadSource(Transports.Http, mirror.FileUri, "Mirror") };
                }
            }

            public MegaFixture Fixture { get; }

            public FakeApi Api { get; }

            public MegaTransportOptions Options { get; }

            public MegaTransport Transport { get; }

            public CountingClient Http { get; }

            public DownloadService Service { get; }

            public string Staging { get; }

            public string Dest { get; }

            public DownloadRequest Request { get; }

            public ListProgress Progress { get; } = new ListProgress();

            public RecordingLog Log { get; } = new RecordingLog();

            public LauncherError Run(CancellationToken ct = default(CancellationToken))
            {
                return DownloadFixtures.Run(() => Service.DownloadAsync(Request, Progress, ct));
            }

            public void Dispose() => _dir.Dispose();
        }

        internal sealed class RecordingLog : IDownloadLog
        {
            public ConcurrentQueue<DownloadAttemptRecord> Records { get; } = new ConcurrentQueue<DownloadAttemptRecord>();

            public void Record(DownloadAttemptRecord record) => Records.Enqueue(record);
        }

        private static MegaTransportOptions Fast(TimeSpan? inactivity = null, TimeSpan? first = null)
        {
            return new MegaTransportOptions
            {
                Connections = 4,
                ChunkSize = Chunk,
                ReadBufferSize = 4096,
                InactivityTimeout = inactivity ?? TimeSpan.FromSeconds(10),
                FirstProgressTimeout = first ?? TimeSpan.FromSeconds(10),
                ApiTimeout = TimeSpan.FromSeconds(10),
                AbandonGrace = TimeSpan.FromSeconds(2),
                Retry = new RetryPolicy { Delay = (d, ct) => Task.CompletedTask },
                ChunkRetry = new RetryPolicy { MaxRetries = 3, Delay = (d, ct) => Task.CompletedTask },
            };
        }

        private static long[] RangeStarts(RangeServer s) => s.Requests.Select(r => long.Parse(r.Range.Substring(6).Split('-')[0], System.Globalization.CultureInfo.InvariantCulture)).ToArray();

        [TestMethod]
        [Timeout(30000)]
        public void ParallelRanges_DecryptToTheOriginal_OverSeveralConnections()
        {
            var f = new MegaFixture(Size);
            using (var storage = new RangeServer(f.Encrypted, c => c.SendAsync(pacePerWriteMs: 5)))
            using (var rig = new Rig(f, storage))
            {
                Assert.AreEqual(LauncherError.None, rig.Run());
                CollectionAssert.AreEqual(f.Plain, File.ReadAllBytes(rig.Dest));
                Assert.AreEqual(Size / Chunk, storage.Requests.Count, "one range per chunk");
                Assert.IsTrue(storage.MaxConcurrent >= 2, "the server never saw two ranges at once (max " + storage.MaxConcurrent + ")");
                Assert.AreEqual(4, rig.Http.MaxOpen, "the transport should keep exactly the configured 4 ranges open");
                Assert.AreEqual(1, rig.Api.Calls);
                List<long> bytes = rig.Progress.Downloading.Select(p => p.BytesDone).ToList();
                Assert.AreEqual((long)Size, bytes.Last());
                for (int i = 1; i < bytes.Count; i++)
                {
                    Assert.IsTrue(bytes[i] >= bytes[i - 1], "progress went backwards");
                }
            }
        }

        [TestMethod]
        [Timeout(30000)]
        public void OutOfOrderRangeCompletion_StillAssemblesTheFile()
        {
            var f = new MegaFixture(Size);
            using (var storage = new RangeServer(f.Encrypted, c => c.SendAsync(delayBeforeMs: c.From == 0 ? 600 : 0)))
            using (var rig = new Rig(f, storage))
            {
                Assert.AreEqual(LauncherError.None, rig.Run());
                CollectionAssert.AreEqual(f.Plain, File.ReadAllBytes(rig.Dest));
                long[] order = storage.Completed.ToArray();
                Assert.AreNotEqual(0L, order[0], "the first chunk was meant to finish late");
                Assert.AreEqual(Size / Chunk, order.Length);
            }
        }

        [TestMethod]
        [Timeout(30000)]
        public void OneRangeFailingMidBody_IsRetriedFromWhereItStopped()
        {
            var f = new MegaFixture(Size);
            int failed = 0;
            using (var storage = new RangeServer(f.Encrypted, c => c.From == Chunk && Interlocked.Exchange(ref failed, 1) == 0 ? c.SendAsync(truncateAfter: 8192) : c.SendAsync()))
            using (var rig = new Rig(f, storage))
            {
                Assert.AreEqual(LauncherError.None, rig.Run());
                CollectionAssert.AreEqual(f.Plain, File.ReadAllBytes(rig.Dest));
                Assert.AreEqual(1, rig.Api.Calls, "a chunk retry must not restart the attempt");
                long[] starts = RangeStarts(storage);
                Assert.AreEqual(Size / Chunk + 1, starts.Length);
                Assert.IsTrue(starts.Any(s => s > Chunk && s < 2 * Chunk), "the retry did not continue inside the chunk: " + string.Join(",", starts));
            }
        }

        [TestMethod]
        [Timeout(30000)]
        public void ARangeThatKeepsFailing_FailsTheAttempt_AfterItsChunkRetries()
        {
            var f = new MegaFixture(Size);
            using (var storage = new RangeServer(f.Encrypted, c => c.From == Chunk ? c.StatusAsync(503) : c.SendAsync()))
            using (var rig = new Rig(f, storage))
            {
                rig.Options.Retry = new RetryPolicy { MaxRetries = 0 };
                Assert.AreEqual(LauncherError.SourceHttpError, rig.Run());
                Assert.AreEqual(4, RangeStarts(storage).Count(s => s == Chunk), "1 try plus 3 chunk retries");
                Assert.IsFalse(File.Exists(rig.Dest + ".part"));
            }
        }

        [TestMethod]
        [Timeout(30000)]
        public void Http509FromStorage_IsQuotaExceeded_WithoutRetry()
        {
            var f = new MegaFixture(Size);
            using (var storage = new RangeServer(f.Encrypted, c => c.From == 2 * Chunk ? c.StatusAsync(509) : c.SendAsync()))
            using (var rig = new Rig(f, storage))
            {
                Assert.AreEqual(LauncherError.SourceQuotaExceeded, rig.Run());
                Assert.AreEqual(1, rig.Api.Calls, "quota is never retried");
                Assert.AreEqual(1, RangeStarts(storage).Count(s => s == 2 * Chunk), "the 509 chunk is not retried");
                Assert.IsFalse(File.Exists(rig.Dest + ".part"));
            }
        }

        [TestMethod]
        [DataRow(-17)]
        [DataRow(-4)]
        public void OverQuotaFromTheApi_IsQuotaExceeded_WithoutRetry(int code)
        {
            var f = new MegaFixture(Size);
            using (var storage = new RangeServer(f.Encrypted))
            using (var rig = new Rig(f, storage))
            {
                rig.Api.Fail = n => MegaErrorMapper.MapApiCode(code, out bool _);
                Assert.AreEqual(LauncherError.SourceQuotaExceeded, rig.Run());
                Assert.AreEqual(1, rig.Api.Calls);
                Assert.AreEqual(0, storage.Requests.Count);
            }
        }

        [TestMethod]
        [Timeout(30000)]
        public void StallAfterTheFirstByte_CountedAcrossAllConnections_RetriesTheAttempt()
        {
            var f = new MegaFixture(Size);
            using (var storage = new RangeServer(f.Encrypted))
            using (var rig = new Rig(f, null, Fast(inactivity: TimeSpan.FromMilliseconds(400), first: TimeSpan.FromSeconds(10))))
            {
                rig.Api.Url = storage.Url;
                using (var stalling = new RangeServer(f.Encrypted, c => c.SendAsync(stallAfter: 4096)))
                {
                    // Attempt 1 gets the stalling server, attempt 2 the good one.
                    rig.Api.Fail = n =>
                    {
                        rig.Api.Url = n == 1 ? stalling.Url : storage.Url;
                        return null;
                    };
                    Stopwatch clock = Stopwatch.StartNew();
                    Assert.AreEqual(LauncherError.None, rig.Run());
                    CollectionAssert.AreEqual(f.Plain, File.ReadAllBytes(rig.Dest));
                    Assert.AreEqual(2, rig.Api.Calls);
                    Assert.IsTrue(clock.ElapsedMilliseconds >= 350, "stall declared before the inactivity window: " + clock.ElapsedMilliseconds);
                }
            }
        }

        [TestMethod]
        [Timeout(30000)]
        public void SlowFirstByte_IsAllowedBeyondTheInactivityWindow()
        {
            var f = new MegaFixture(Size);
            using (var storage = new RangeServer(f.Encrypted, c => c.SendAsync(delayBeforeMs: c.Index < 4 ? 700 : 0)))
            using (var rig = new Rig(f, storage, Fast(inactivity: TimeSpan.FromMilliseconds(150), first: TimeSpan.FromSeconds(10))))
            {
                Assert.AreEqual(LauncherError.None, rig.Run());
                Assert.AreEqual(1, rig.Api.Calls, "no stall retry before the first byte arrived");
            }
        }

        [TestMethod]
        [Timeout(30000)]
        public void DeadMega_WithAMirror_FailsOverAfterOneFirstByteTimeout()
        {
            var f = new MegaFixture(Size);
            using (var storage = new RangeServer(f.Encrypted, c => c.SendAsync(stallAfter: 0)))
            using (var mirror = new FakeHttpServer((ctx, i) => Serve.FileAsync(ctx, f.Plain)))
            using (var rig = new Rig(f, storage, Fast(inactivity: TimeSpan.FromMilliseconds(100), first: TimeSpan.FromMilliseconds(600)), mirror))
            {
                Stopwatch clock = Stopwatch.StartNew();
                Assert.AreEqual(LauncherError.None, rig.Run());
                Assert.AreEqual(1, rig.Api.Calls, "a dead primary must not be retried when a mirror is waiting");
                Assert.IsTrue(clock.ElapsedMilliseconds >= 550 && clock.ElapsedMilliseconds < 5000, "about one timeout: " + clock.ElapsedMilliseconds + " ms");
                CollectionAssert.AreEqual(f.Plain, File.ReadAllBytes(rig.Dest));
            }
        }

        [TestMethod]
        [Timeout(30000)]
        public void DeadMega_WithNoMirror_StillRetries()
        {
            var f = new MegaFixture(Size);
            using (var storage = new RangeServer(f.Encrypted, c => c.SendAsync(stallAfter: 0)))
            using (var rig = new Rig(f, storage, Fast(inactivity: TimeSpan.FromMilliseconds(100), first: TimeSpan.FromMilliseconds(300))))
            {
                Assert.AreEqual(LauncherError.NetworkUnavailable, rig.Run());
                Assert.AreEqual(4, rig.Api.Calls, "1 attempt plus 3 retries");
            }
        }

        [TestMethod]
        [Timeout(30000)]
        public void ApiThatNeverAnswers_FailsAsNetworkUnavailable_AfterTheApiTimeout()
        {
            var f = new MegaFixture(Size);
            using (var rig = new Rig(f, null))
            {
                rig.Api.Hang = true;
                rig.Options.ApiTimeout = TimeSpan.FromMilliseconds(200);
                rig.Options.Retry = new RetryPolicy { MaxRetries = 0 };
                Stopwatch clock = Stopwatch.StartNew();
                Assert.AreEqual(LauncherError.NetworkUnavailable, rig.Run());
                Assert.IsTrue(clock.ElapsedMilliseconds >= 150 && clock.ElapsedMilliseconds < 5000, clock.ElapsedMilliseconds + " ms");
            }
        }

        [TestMethod]
        [Timeout(30000)]
        public void CancelMidFlight_ReportsCancelled_DeletesThePart_AndANewDownloadSucceedsAtOnce()
        {
            var f = new MegaFixture(Size);
            using (var storage = new RangeServer(f.Encrypted, c => c.SendAsync(pacePerWriteMs: 40)))
            using (var rig = new Rig(f, storage))
            using (var cts = new CancellationTokenSource())
            {
                var progress = new ListProgress(p =>
                {
                    if (p.BytesDone > 16 * 1024)
                    {
                        cts.Cancel();
                    }
                });
                LauncherError code = DownloadFixtures.Run(() => rig.Service.DownloadAsync(rig.Request, progress, cts.Token));
                Assert.AreEqual(LauncherError.Cancelled, code);
                Assert.IsFalse(File.Exists(rig.Dest + ".part"), "the part survived the cancel");
                Assert.IsFalse(File.Exists(rig.Dest));
                long last = progress.Downloading.Last().BytesDone;
                Thread.Sleep(300);
                Assert.AreEqual(last, progress.Downloading.Last().BytesDone, "an abandoned connection still reported progress");

                // The abandoned connections hold nothing: a new attempt starts and finishes at once.
                Assert.AreEqual(LauncherError.None, rig.Run());
                CollectionAssert.AreEqual(f.Plain, File.ReadAllBytes(rig.Dest));
            }
        }

        [TestMethod]
        [Timeout(30000)]
        public void CorruptRange_FailsTheHashCheck_AndFailsOverToTheMirror()
        {
            var f = new MegaFixture(Size);
            using (var storage = new RangeServer(f.Encrypted, c => c.SendAsync(corrupt: c.From == 3 * Chunk)))
            using (var mirror = new FakeHttpServer((ctx, i) => Serve.FileAsync(ctx, f.Plain)))
            using (var rig = new Rig(f, storage, null, mirror))
            {
                Assert.AreEqual(LauncherError.None, rig.Run());
                CollectionAssert.AreEqual(f.Plain, File.ReadAllBytes(rig.Dest));
                Assert.AreEqual(1, rig.Api.Calls);
                Assert.IsTrue(mirror.Requests.Count >= 1, "the mirror was not used");
                DownloadAttemptRecord[] records = rig.Log.Records.ToArray();
                Assert.AreEqual(LauncherError.HashMismatch, ((LauncherException)records[0].Error).Code);
                Assert.AreEqual((long)Size, records[0].Bytes, "the failed mega attempt logged 0 bytes although its part was deleted");
                Assert.AreEqual(DownloadAttemptOutcome.Succeeded, records[1].Outcome);
            }
        }

        [TestMethod]
        [Timeout(30000)]
        public void CorruptRange_WithNoMirror_IsHashMismatch_AndNothingIsKept()
        {
            var f = new MegaFixture(Size);
            using (var storage = new RangeServer(f.Encrypted, c => c.SendAsync(corrupt: c.From == 0)))
            using (var rig = new Rig(f, storage))
            {
                Assert.AreEqual(LauncherError.HashMismatch, rig.Run());
                Assert.IsFalse(File.Exists(rig.Dest));
                Assert.IsFalse(File.Exists(rig.Dest + ".part"));
            }
        }

        [TestMethod]
        public void SizeFromTheApi_ThatDisagrees_FailsBeforeAnyBodyRequest()
        {
            var f = new MegaFixture(Size);
            using (var storage = new RangeServer(f.Encrypted))
            using (var rig = new Rig(f, storage))
            {
                rig.Api.Size = Size + 1;
                Assert.AreEqual(LauncherError.SizeMismatch, rig.Run());
                Assert.AreEqual(0, storage.Requests.Count);
            }
        }

        [TestMethod]
        [Timeout(30000)]
        public void AStorageServerIgnoringRanges_IsASourceError()
        {
            var f = new MegaFixture(Size);
            using (var storage = new FakeHttpServer((ctx, i) => Serve.FileAsync(ctx, f.Encrypted, honourRanges: false)))
            using (var rig = new Rig(f, null))
            {
                rig.Api.Url = storage.FileUri;
                Assert.AreEqual(LauncherError.SourceHttpError, rig.Run());
            }
        }

        [TestMethod]
        [Timeout(30000)]
        public void TheStorageHost_IsLoggedPerAttempt()
        {
            var f = new MegaFixture(Size);
            using (var storage = new RangeServer(f.Encrypted))
            using (var rig = new Rig(f, storage))
            {
                Assert.AreEqual(LauncherError.None, rig.Run());
                DownloadAttemptRecord record = rig.Log.Records.Single();
                Assert.AreEqual("mega.nz", record.Host);
                Assert.AreEqual("localhost", record.StorageHost);
                StringAssert.Contains(new FileDownloadLog(Path.Combine(rig.Staging, "x.log")).Format(record), " storage=localhost ");
            }
        }

        [TestMethod]
        [Timeout(30000)]
        public void HeadersThatNeverArrive_WithAMirror_FailOverAfterOneAttempt()
        {
            var f = new MegaFixture(Size);
            using (var storage = new RangeServer(f.Encrypted, async c =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, c.Stopping).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }))
            using (var mirror = new FakeHttpServer((ctx, i) => Serve.FileAsync(ctx, f.Plain)))
            using (var rig = new Rig(f, storage, Fast(inactivity: TimeSpan.FromSeconds(10), first: TimeSpan.FromSeconds(20)), mirror, headerTimeout: TimeSpan.FromMilliseconds(200)))
            {
                Assert.AreEqual(LauncherError.None, rig.Run());
                Assert.AreEqual(1, rig.Api.Calls, "a primary that never sent a byte must not be retried while a mirror is waiting");
                Assert.IsTrue(mirror.Requests.Count >= 1, "the mirror was not used");
                CollectionAssert.AreEqual(f.Plain, File.ReadAllBytes(rig.Dest));
            }
        }

        [TestMethod]
        public void TheProductionHttpClient_WaitsForHeadersAsLongAsTheFirstByteWindow()
        {
            var t = new MegaTransport(new MegaTransportOptions { FirstProgressTimeout = TimeSpan.FromMinutes(7) });
            Assert.AreEqual(TimeSpan.FromMinutes(7), ((DreamWeave.Launcher.Core.Net.HttpStreamClient)t.Http).HeaderTimeout);
            Assert.AreEqual(new MegaTransportOptions().FirstProgressTimeout, ((DreamWeave.Launcher.Core.Net.HttpStreamClient)new MegaTransport().Http).HeaderTimeout);
        }

        private const int SmallChunk = 16 * 1024;

        private static MegaTransportOptions SmallChunks()
        {
            MegaTransportOptions o = Fast(inactivity: TimeSpan.FromMilliseconds(300), first: TimeSpan.FromSeconds(10));
            o.ChunkSize = SmallChunk;
            return o;
        }

        private static int RequestsInChunk(RangeServer s, long chunkStart) => RangeStarts(s).Count(x => x >= chunkStart && x < chunkStart + SmallChunk);

        [TestMethod]
        [Timeout(30000)]
        public void OneHungRange_AmongHealthyOnes_IsCutAndRetriedFromItsCursor_WithoutRestarting()
        {
            var f = new MegaFixture(Size);
            int hung = 0;
            using (var storage = new RangeServer(f.Encrypted, c => c.From == 2 * SmallChunk && Interlocked.Exchange(ref hung, 1) == 0 ? c.SendAsync(stallAfter: 4096) : c.SendAsync(pacePerWriteMs: 30)))
            using (var rig = new Rig(f, storage, SmallChunks()))
            {
                Assert.AreEqual(LauncherError.None, rig.Run());
                CollectionAssert.AreEqual(f.Plain, File.ReadAllBytes(rig.Dest));
                Assert.AreEqual(1, rig.Api.Calls, "one hung range restarted the whole download");
                Assert.AreEqual(2, RequestsInChunk(storage, 2 * SmallChunk), "exactly one extra request for the hung chunk");
                Assert.IsTrue(RangeStarts(storage).Contains(2 * SmallChunk + 4096), "the retry did not continue from the cursor");
                Assert.AreEqual(Size / SmallChunk + 1, storage.Requests.Count);
            }
        }

        [TestMethod]
        [Timeout(30000)]
        public void OneSlowButLivingRange_WhileOthersProgress_IsNeitherCutNorAStall()
        {
            var f = new MegaFixture(Size);
            using (var storage = new RangeServer(f.Encrypted, c => c.SendAsync(pacePerWriteMs: c.From == 0 ? 150 : 30)))
            using (var rig = new Rig(f, storage, SmallChunks()))
            {
                Assert.AreEqual(LauncherError.None, rig.Run());
                Assert.AreEqual(1, rig.Api.Calls);
                Assert.AreEqual(Size / SmallChunk, storage.Requests.Count, "a slow range that is still sending was cut");
            }
        }

        [TestMethod]
        [Timeout(30000)]
        public void AChunkBackOff_IsNotCountedAsInactivity()
        {
            var f = new MegaFixture(Size);
            int refused = 0;
            using (var storage = new RangeServer(f.Encrypted, c =>
            {
                if (c.From == 0 && Interlocked.Exchange(ref refused, 1) == 0)
                {
                    return c.StatusAsync(503, "1");
                }

                return c.SendAsync();
            }))
            using (var rig = new Rig(f, storage, Fast(inactivity: TimeSpan.FromMilliseconds(300), first: TimeSpan.FromSeconds(10))))
            {
                var waits = new List<TimeSpan>();
                rig.Options.ChunkRetry = new RetryPolicy
                {
                    MaxRetries = 3,
                    Delay = (d, ct) =>
                    {
                        lock (waits)
                        {
                            waits.Add(d);
                        }

                        return Task.Delay(TimeSpan.FromMilliseconds(900), ct);
                    },
                };
                Assert.AreEqual(LauncherError.None, rig.Run());
                Assert.AreEqual(1, rig.Api.Calls, "the back-off was taken for a stall");
                CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(1) }, waits, "the server's Retry-After was not honoured");
            }
        }

        [TestMethod]
        public void AnInvalidLink_IsManifestInvalid()
        {
            var f = new MegaFixture(Size);
            using (var rig = new Rig(f, null))
            {
                var bad = new DownloadRequest("eor-dats", Transports.Mega, new Uri("https://mega.nz/file/TESTFILE#short"), Size, Hasher.ComputeSha256Hex(f.Plain), rig.Dest);
                Assert.AreEqual(LauncherError.ManifestInvalid, DownloadFixtures.Run(() => rig.Service.DownloadAsync(bad, null, CancellationToken.None)));
                Assert.AreEqual(0, rig.Api.Calls);
            }
        }
    }

    /// <summary><see cref="MegaLink"/> and <see cref="MegaCtr"/>.</summary>
    [TestClass]
    public class MegaCryptoTests
    {
        [TestMethod]
        public void Link_KeyIsTheXorOfTheHalves_AndTheNonceIsBytes16To23()
        {
            byte[] raw = Enumerable.Range(0, 32).Select(i => (byte)(i * 7 + 1)).ToArray();
            MegaLink link;
            Assert.IsTrue(MegaLink.TryParse(new Uri(MegaLink.Format("AbC_d-1", raw)), out link));
            Assert.AreEqual("AbC_d-1", link.Handle);
            CollectionAssert.AreEqual(Enumerable.Range(0, 16).Select(i => (byte)(raw[i] ^ raw[i + 16])).ToArray(), link.Key);
            CollectionAssert.AreEqual(raw.Skip(16).Take(8).ToArray(), link.Nonce);
        }

        [TestMethod]
        [DataRow("https://mega.nz/file/abc")]
        [DataRow("https://mega.nz/file/abc#tooshort")]
        [DataRow("https://mega.nz/folder/abc#AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
        [DataRow("https://mega.nz/file/#AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
        public void NotAPublicFileLink_DoesNotParse(string url)
        {
            MegaLink link;
            Assert.IsFalse(MegaLink.TryParse(new Uri(url), out link));
        }

        [TestMethod]
        public void CounterBlock_IsTheNonceThenTheBigEndianBlockIndex()
        {
            byte[] key = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
            byte[] nonce = { 1, 2, 3, 4, 5, 6, 7, 8 };
            var zeros = new byte[48];
            using (var ctr = new MegaCtr(key, nonce))
            {
                ctr.Transform(zeros, 0, 48, 0x1000 * 16);
            }

            using (var aes = System.Security.Cryptography.Aes.Create())
            {
                aes.Mode = System.Security.Cryptography.CipherMode.ECB;
                aes.Padding = System.Security.Cryptography.PaddingMode.None;
                aes.Key = key;
                var counters = new byte[48];
                for (int b = 0; b < 3; b++)
                {
                    Buffer.BlockCopy(nonce, 0, counters, b * 16, 8);
                    long index = 0x1000 + b;
                    for (int i = 0; i < 8; i++)
                    {
                        counters[(b * 16) + 15 - i] = (byte)(index >> (8 * i));
                    }
                }

                byte[] expected = aes.CreateEncryptor().TransformFinalBlock(counters, 0, 48);
                CollectionAssert.AreEqual(expected, zeros);
            }
        }

        [TestMethod]
        public void AnyRangeDecryptsOnItsOwn_AtAnyOffset_AndTwiceIsIdentity()
        {
            byte[] key = Enumerable.Range(0, 16).Select(i => (byte)(i * 3)).ToArray();
            byte[] nonce = { 9, 8, 7, 6, 5, 4, 3, 2 };
            byte[] plain = DownloadFixtures.Content(200000);
            byte[] whole = (byte[])plain.Clone();
            using (var ctr = new MegaCtr(key, nonce))
            {
                ctr.Transform(whole, 0, whole.Length, 0);
                foreach (int start in new[] { 0, 1, 15, 16, 17, 65535, 65536, 70001, 199999 })
                {
                    int len = Math.Min(70000, plain.Length - start);
                    var piece = new byte[len];
                    Buffer.BlockCopy(whole, start, piece, 0, len);
                    ctr.Transform(piece, 0, len, start);
                    CollectionAssert.AreEqual(plain.Skip(start).Take(len).ToArray(), piece, "offset " + start);
                }
            }
        }
    }

    /// <summary><see cref="MegaApi"/>'s <c>g</c> command and <see cref="MegaErrorMapper"/>.</summary>
    [TestClass]
    public class MegaApiTests
    {
        private static LauncherError ParseCode(string json, out bool transient)
        {
            try
            {
                MegaApi.Parse(json);
            }
            catch (TransientDownloadException t)
            {
                transient = true;
                return t.Final.Code;
            }
            catch (LauncherException le)
            {
                transient = false;
                return le.Code;
            }

            Assert.Fail("expected a failure for " + json);
            transient = false;
            return LauncherError.None;
        }

        [TestMethod]
        public void Success_GivesTheStorageUrlAndSize()
        {
            MegaDownloadTicket t = MegaApi.Parse("[{\"s\":598884909,\"at\":\"xyz\",\"msd\":1,\"g\":\"https://gfs208n187.userstorage.mega.co.nz/dl/abc\"}]");
            Assert.AreEqual("gfs208n187.userstorage.mega.co.nz", t.Url.Host);
            Assert.AreEqual(598884909L, t.Size);
            Assert.AreEqual("gfs1.example", MegaApi.Parse("[{\"s\":5,\"g\":[\"http://gfs1.example/dl/x\",\"http://gfs2.example/dl/x\"]}]").Url.Host);
        }

        [TestMethod]
        [DataRow("[-17]", LauncherError.SourceQuotaExceeded, false)]
        [DataRow("[{\"e\":-17,\"s\":5}]", LauncherError.SourceQuotaExceeded, false)]
        [DataRow("[-4]", LauncherError.SourceQuotaExceeded, false)]
        [DataRow("-3", LauncherError.SourceHttpError, true)]
        [DataRow("[-18]", LauncherError.SourceHttpError, true)]
        [DataRow("[-9]", LauncherError.SourceHttpError, false)]
        [DataRow("[-16]", LauncherError.SourceHttpError, false)]
        [DataRow("not json", LauncherError.SourceHttpError, true)]
        [DataRow("[{\"s\":5}]", LauncherError.SourceHttpError, true)]
        public void Errors_AreClassified(string json, LauncherError code, bool transient)
        {
            bool t;
            Assert.AreEqual(code, ParseCode(json, out t));
            Assert.AreEqual(transient, t, json);
        }

        [TestMethod]
        [Timeout(30000)]
        public void TheRealClient_PostsTheGCommand_AndReadsTheAnswer()
        {
            string seenBody = null;
            string seenQuery = null;
            using (var server = new FakeHttpServer(async (ctx, i) =>
            {
                using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                {
                    seenBody = await reader.ReadToEndAsync().ConfigureAwait(false);
                }

                seenQuery = ctx.Request.Url.Query;
                byte[] answer = Encoding.UTF8.GetBytes("[{\"s\":42,\"g\":\"https://gfs1.example/dl/x\"}]");
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentLength64 = answer.Length;
                await ctx.Response.OutputStream.WriteAsync(answer, 0, answer.Length).ConfigureAwait(false);
                ctx.Response.Close();
            }))
            using (var api = new MegaApi(new HttpClientHandler(), true, server.BaseUri))
            {
                MegaDownloadTicket t = api.GetDownloadAsync("S8dSSCxJ", CancellationToken.None).GetAwaiter().GetResult();
                Assert.AreEqual(42L, t.Size);
                Assert.AreEqual("[{\"a\":\"g\",\"g\":1,\"ssl\":2,\"p\":\"S8dSSCxJ\"}]", seenBody);
                StringAssert.StartsWith(seenQuery, "?id=");
                Assert.AreEqual("/cs", server.Requests.Single().Path);
            }
        }

        [TestMethod]
        [Timeout(30000)]
        public void TheRealClient_MapsA503ToATransientError()
        {
            using (var server = new FakeHttpServer((ctx, i) => Serve.StatusAsync(ctx, 503)))
            using (var api = new MegaApi(new HttpClientHandler(), true, server.BaseUri))
            {
                var ex = Assert.ThrowsExactly<TransientDownloadException>(() => api.GetDownloadAsync("x", CancellationToken.None).GetAwaiter().GetResult());
                Assert.AreEqual(LauncherError.SourceHttpError, ex.Final.Code);
            }
        }

        [TestMethod]
        [DataRow(509, LauncherError.SourceQuotaExceeded, false)]
        [DataRow(429, LauncherError.SourceQuotaExceeded, false)]
        [DataRow(503, LauncherError.SourceHttpError, true)]
        [DataRow(408, LauncherError.SourceHttpError, true)]
        [DataRow(403, LauncherError.SourceHttpError, false)]
        public void HttpStatuses_AreClassified(int status, LauncherError code, bool transient)
        {
            bool t;
            Assert.AreEqual(code, MegaErrorMapper.MapStatus(status, out t).Code);
            Assert.AreEqual(transient, t);
        }

        [TestMethod]
        public void PartFileErrors_AreClassified()
        {
            bool t;
            Assert.AreEqual(LauncherError.DiskFull, MegaErrorMapper.Map(new IOException("full", unchecked((int)0x80070070)), out t).Code);
            Assert.IsFalse(t);
            MegaErrorMapper.Map(new IOException("in use", unchecked((int)0x80070020)), out t);
            Assert.IsTrue(t, "a sharing violation is transient");
            Assert.AreEqual(LauncherError.NetworkUnavailable, MegaErrorMapper.Map(new IOException("reset", new WebException("x")), out t).Code);
            Assert.IsTrue(t);
            Assert.AreEqual(LauncherError.AccessDenied, MegaErrorMapper.Map(new UnauthorizedAccessException(), out t).Code);
            Assert.AreEqual(LauncherError.NetworkUnavailable, MegaErrorMapper.Map(new HttpRequestException("x"), out t).Code);
            Assert.IsTrue(t);
        }
    }
}
