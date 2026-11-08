using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using DreamWeave.Launcher.Core.Manifests;
using DreamWeave.Launcher.Core.Net;

namespace DreamWeave.Launcher.Core.Downloads
{
    /// <summary>Tunables for <see cref="MegaTransport"/>.</summary>
    public sealed class MegaTransportOptions
    {
        /// <summary>Once bytes have started to arrive, no new bytes on ANY connection for this long cancels the attempt and retries.</summary>
        public TimeSpan InactivityTimeout { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>
        /// How long to wait for the FIRST body byte on any connection. mega.nz can take minutes to begin
        /// a large file, so this is far longer than <see cref="InactivityTimeout"/>.
        /// </summary>
        public TimeSpan FirstProgressTimeout { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>The API call that hands out the download URL must answer within this time, or the attempt fails as NetworkUnavailable.</summary>
        public TimeSpan ApiTimeout { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>How long to wait for the connections of a cancelled or stalled attempt to unwind before the part file is closed under them.</summary>
        public TimeSpan AbandonGrace { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>Retry budget and back-off for a whole attempt (network-class failures only).</summary>
        public RetryPolicy Retry { get; set; } = new RetryPolicy();

        /// <summary>Parallel HTTP range requests against the storage server.</summary>
        public int Connections { get; set; } = 6;

        /// <summary>Bytes per range request (each connection takes the next chunk when it finishes one).</summary>
        public long ChunkSize { get; set; } = 8L * 1024 * 1024;

        /// <summary>
        /// Back-off for retrying ONE failed chunk before the whole attempt fails; its <see cref="RetryPolicy.MaxRetries"/>
        /// is the number of retries per chunk. A retry continues from the chunk's last received byte.
        /// </summary>
        public RetryPolicy ChunkRetry { get; set; } = new RetryPolicy { MaxRetries = 3, BaseDelay = TimeSpan.FromSeconds(1) };

        /// <summary>Size of each body read.</summary>
        public int ReadBufferSize { get; set; } = 64 * 1024;
    }

    /// <summary>
    /// mega.nz public-link download. The link's handle goes to mega's API (<see cref="IMegaApi"/>), which
    /// hands out a temporary storage URL and the size; the size is checked against the manifest BEFORE any
    /// body bytes are fetched. The encrypted body is then fetched as <see cref="MegaTransportOptions.Connections"/>
    /// parallel HTTP range requests of <see cref="MegaTransportOptions.ChunkSize"/>, each decrypted with
    /// AES-128-CTR at its own offset (<see cref="MegaCtr"/>) and written at that offset into
    /// <see cref="DownloadRequest.PartPath"/>. One storage connection can be slow (about 1 MB/s has been
    /// measured from a single mega server while parallel ranges ran at over 30 MB/s); parallel ranges make the
    /// speed the sum of the connections.
    /// <para>
    /// Integrity is the manifest's size and SHA-256, checked by <see cref="DownloadService"/> after the
    /// transport returns; mega's own chunk MAC is not computed. A failed chunk is retried from where it
    /// stopped (<see cref="MegaTransportOptions.ChunkRetry"/>); a whole attempt starts from zero. The stall
    /// watchdog counts bytes across ALL connections. Quota (HTTP 509, or the API's over-quota codes) is
    /// never retried. Every connection of an attempt is cancelled and awaited (up to
    /// <see cref="MegaTransportOptions.AbandonGrace"/>) and the part file is closed under a lock before the
    /// attempt returns; a read that is still unwinding after that can no longer write or report progress,
    /// so a newer attempt's part file is never touched.
    /// </para>
    /// </summary>
    public sealed class MegaTransport : IDownloadTransport
    {
        private const string NeverStartedKey = "neverStarted";

        private readonly IMegaApi _api;
        private readonly IHttpStreamClient _http;
        private readonly MegaTransportOptions _options;

        /// <summary>Production constructor: mega's real API and a real HTTP client.</summary>
        public MegaTransport()
            : this(new MegaApi(), new HttpStreamClient())
        {
        }

        /// <summary>Constructor for tests and custom wiring.</summary>
        public MegaTransport(IMegaApi api, IHttpStreamClient http, MegaTransportOptions options = null)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _options = options ?? new MegaTransportOptions();
            if (_options.Connections < 1 || _options.ChunkSize < 16 || _options.ReadBufferSize < 16)
            {
                throw new ArgumentException("Connections, ChunkSize and ReadBufferSize must be positive.", nameof(options));
            }
        }

        /// <inheritdoc />
        public string Name => Transports.Mega;

        /// <inheritdoc />
        public long GetResumableBytes(DownloadRequest request)
        {
            return 0;
        }

        /// <inheritdoc />
        public async Task DownloadToPartAsync(DownloadRequest request, TransferProgress progress, CancellationToken cancellationToken)
        {
            int retriesUsed = 0;
            while (true)
            {
                TimeSpan? retryAfter;
                try
                {
                    await AttemptAsync(request, progress, cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (Exception ex)
                {
                    PartFiles.TryDelete(request.PartPath);
                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw new LauncherException(LauncherError.Cancelled, "Cancelled.", ex);
                    }

                    bool transient;
                    LauncherException mapped = MegaErrorMapper.Map(ex, out transient);
                    retryAfter = (ex as TransientDownloadException)?.RetryAfter;
                    if (request.FallbackAvailable && (mapped.Code == LauncherError.HashMismatch || mapped.Data.Contains(NeverStartedKey)))
                    {
                        // It never started sending, and another source is waiting: move on instead of retrying.
                        throw mapped;
                    }

                    if (!transient || retriesUsed >= _options.Retry.MaxRetries)
                    {
                        throw mapped;
                    }
                }

                await _options.Retry.WaitAsync(retriesUsed, retryAfter, cancellationToken).ConfigureAwait(false);
                retriesUsed++;
            }
        }

        private async Task AttemptAsync(DownloadRequest request, TransferProgress progress, CancellationToken cancellationToken)
        {
            MegaLink link;
            if (!MegaLink.TryParse(request.Url, out link))
            {
                throw new LauncherException(LauncherError.ManifestInvalid, "The mega.nz link in the manifest is not a valid file link.");
            }

            PartFiles.Delete(request.PartPath, "clearing an old partial download");
            MegaDownloadTicket ticket = await GetTicketAsync(link.Handle, cancellationToken).ConfigureAwait(false);
            request.ServedFromHost = ticket.Url.Host;
            if (ticket.Size != request.ExpectedSize)
            {
                throw new LauncherException(
                    LauncherError.SizeMismatch,
                    "The file on mega.nz is " + ticket.Size + " bytes, but " + request.ExpectedSize + " bytes were expected.");
            }

            var job = new Job(this, request, ticket, link, progress, cancellationToken);
            await job.RunAsync().ConfigureAwait(false);
        }

        private async Task<MegaDownloadTicket> GetTicketAsync(string handle, CancellationToken cancellationToken)
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                Task<MegaDownloadTicket> call = _api.GetDownloadAsync(handle, cts.Token);
                var cancelled = new TaskCompletionSource<bool>();
                using (cancellationToken.Register(() => cancelled.TrySetResult(true)))
                {
                    Task first = await Task.WhenAny(call, Task.Delay(_options.ApiTimeout), cancelled.Task).ConfigureAwait(false);
                    if (first != call)
                    {
                        cts.Cancel();
                        Observe(call);
                        cancellationToken.ThrowIfCancellationRequested();
                        throw new LauncherException(LauncherError.NetworkUnavailable, "mega.nz did not answer in time.");
                    }
                }

                try
                {
                    return await call.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new LauncherException(LauncherError.NetworkUnavailable, "mega.nz did not answer in time.");
                }
            }
        }

        private static void Observe(Task task)
        {
            task.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        }

        private static void RaiseConnectionLimit(Uri url, int connections)
        {
            try
            {
                // .NET Framework allows 2 connections per host by default; the ranges need one each.
                ServicePoint point = ServicePointManager.FindServicePoint(url);
                if (point.ConnectionLimit < connections)
                {
                    point.ConnectionLimit = connections;
                }
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is NotSupportedException)
            {
            }
        }

        /// <summary>One attempt: the part file, the chunk queue, the connections and the watchdog.</summary>
        private sealed class Job
        {
            private readonly MegaTransport _owner;
            private readonly DownloadRequest _request;
            private readonly MegaDownloadTicket _ticket;
            private readonly MegaLink _link;
            private readonly TransferProgress _progress;
            private readonly CancellationToken _outer;
            private readonly MegaTransportOptions _o;
            private readonly CancellationTokenSource _cts;
            private readonly Stopwatch _clock = Stopwatch.StartNew();
            private readonly object _gate = new object();
            private readonly long _chunks;
            private FileStream _file;
            private bool _closed;
            private bool _started;
            private long _received;
            private long _lastActivityTicks;
            private long _next = -1;
            private Exception _failure;

            public Job(MegaTransport owner, DownloadRequest request, MegaDownloadTicket ticket, MegaLink link, TransferProgress progress, CancellationToken outer)
            {
                _owner = owner;
                _request = request;
                _ticket = ticket;
                _link = link;
                _progress = progress;
                _outer = outer;
                _o = owner._options;
                _cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
                _chunks = (request.ExpectedSize + _o.ChunkSize - 1) / _o.ChunkSize;
            }

            public async Task RunAsync()
            {
                try
                {
                    await RunCoreAsync().ConfigureAwait(false);
                }
                finally
                {
                    _cts.Dispose();
                }
            }

            private async Task RunCoreAsync()
            {
                RaiseConnectionLimit(_ticket.Url, _o.Connections);
                _file = new FileStream(_request.PartPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                bool stalled = false;
                bool neverStarted = false;
                Task all;
                try
                {
                    _file.SetLength(_request.ExpectedSize);
                    lock (_gate)
                    {
                        _progress.Report(0);
                    }

                    int workers = (int)Math.Min(_o.Connections, Math.Max(1, _chunks));
                    all = Task.WhenAll(Enumerable.Range(0, workers).Select(_ => Task.Run(WorkerAsync)).ToArray());
                    TimeSpan poll = TimeSpan.FromTicks(Math.Max(TimeSpan.FromMilliseconds(10).Ticks, Math.Min(TimeSpan.FromSeconds(1).Ticks, _o.InactivityTimeout.Ticks / 4)));
                    while (true)
                    {
                        Task first = await Task.WhenAny(all, Task.Delay(poll)).ConfigureAwait(false);
                        if (first == all || Volatile.Read(ref _failure) != null)
                        {
                            break;
                        }

                        bool started;
                        lock (_gate)
                        {
                            started = _started;
                        }

                        TimeSpan limit = started ? _o.InactivityTimeout : _o.FirstProgressTimeout;
                        double idleSeconds = (_clock.ElapsedTicks - Interlocked.Read(ref _lastActivityTicks)) / (double)Stopwatch.Frequency;
                        if (_outer.IsCancellationRequested || idleSeconds > limit.TotalSeconds)
                        {
                            stalled = !_outer.IsCancellationRequested;
                            neverStarted = !started;
                            break;
                        }
                    }

                    if (!all.IsCompleted)
                    {
                        _cts.Cancel();
                        await Task.WhenAny(all, Task.Delay(_o.AbandonGrace)).ConfigureAwait(false);
                        Observe(all);
                    }
                }
                finally
                {
                    Close();
                }

                _outer.ThrowIfCancellationRequested();
                if (stalled)
                {
                    var stall = new LauncherException(LauncherError.NetworkUnavailable, "mega.nz stopped sending data.");
                    if (neverStarted)
                    {
                        stall.Data[NeverStartedKey] = true;
                    }

                    throw stall;
                }

                Exception failure = Volatile.Read(ref _failure);
                if (failure != null)
                {
                    ExceptionDispatchInfo.Capture(failure).Throw();
                }

                await all.ConfigureAwait(false);
                if (Interlocked.Read(ref _received) != _request.ExpectedSize)
                {
                    throw new TransientDownloadException(new LauncherException(LauncherError.NetworkUnavailable, "mega.nz ended the download early."));
                }
            }

            /// <summary>After this returns no connection of this attempt can write to the part or report progress.</summary>
            private void Close()
            {
                lock (_gate)
                {
                    _closed = true;
                    if (_file != null)
                    {
                        try
                        {
                            _file.Dispose();
                        }
                        catch (IOException)
                        {
                        }

                        _file = null;
                    }
                }
            }

            private async Task WorkerAsync()
            {
                try
                {
                    using (var ctr = new MegaCtr(_link.Key, _link.Nonce))
                    {
                        var buffer = new byte[_o.ReadBufferSize];
                        while (!_cts.IsCancellationRequested)
                        {
                            long index = Interlocked.Increment(ref _next);
                            if (index >= _chunks)
                            {
                                return;
                            }

                            long start = index * _o.ChunkSize;
                            long end = Math.Min(_request.ExpectedSize, start + _o.ChunkSize) - 1;
                            await FetchChunkAsync(ctr, buffer, start, end).ConfigureAwait(false);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // The first failure wins and stops the other connections. Anything after a cancel
                    // (player, stall, or another connection's failure) is only the unwinding.
                    if (!_cts.IsCancellationRequested && Interlocked.CompareExchange(ref _failure, ex, null) == null)
                    {
                        try
                        {
                            _cts.Cancel();
                        }
                        catch (ObjectDisposedException)
                        {
                        }
                    }
                }
            }

            private async Task FetchChunkAsync(MegaCtr ctr, byte[] buffer, long start, long end)
            {
                var cursor = new Cursor { Position = start };
                int retriesUsed = 0;
                while (cursor.Position <= end)
                {
                    long before = cursor.Position;
                    TimeSpan? retryAfter = null;
                    try
                    {
                        await FetchRangeAsync(ctr, buffer, cursor, end).ConfigureAwait(false);
                        if (cursor.Position <= end)
                        {
                            throw new TransientDownloadException(new LauncherException(LauncherError.NetworkUnavailable, "The connection to mega.nz was interrupted."));
                        }

                        return;
                    }
                    catch (Exception ex) when (!_cts.IsCancellationRequested)
                    {
                        bool transient;
                        MegaErrorMapper.Map(ex, out transient);
                        if (cursor.Position - before >= 1024 * 1024)
                        {
                            // A connection that delivered a real amount before dropping earns a fresh budget.
                            retriesUsed = 0;
                        }

                        if (!transient || retriesUsed >= _o.ChunkRetry.MaxRetries)
                        {
                            throw;
                        }

                        retryAfter = (ex as TransientDownloadException)?.RetryAfter;
                    }

                    await _o.ChunkRetry.WaitAsync(retriesUsed, retryAfter, _cts.Token).ConfigureAwait(false);
                    retriesUsed++;
                }
            }

            private async Task FetchRangeAsync(MegaCtr ctr, byte[] buffer, Cursor cursor, long end)
            {
                using (HttpStreamResponse response = await _owner._http.GetAsync(_ticket.Url, cursor.Position, end, null, _cts.Token).ConfigureAwait(false))
                {
                    if (response.StatusCode == 206)
                    {
                        if (response.ContentRangeFrom.HasValue && response.ContentRangeFrom.Value != cursor.Position)
                        {
                            throw new TransientDownloadException(new LauncherException(LauncherError.SourceHttpError, "mega.nz sent the wrong part of the file."));
                        }
                    }
                    else if (response.StatusCode == 200)
                    {
                        if (cursor.Position != 0)
                        {
                            throw new LauncherException(LauncherError.SourceHttpError, "mega.nz ignored a range request.");
                        }
                    }
                    else
                    {
                        bool transient;
                        LauncherException mapped = MegaErrorMapper.MapStatus(response.StatusCode, out transient);
                        if (transient)
                        {
                            throw new TransientDownloadException(mapped, response.RetryAfter);
                        }

                        throw mapped;
                    }

                    // Disposing the response is what unblocks a read that is waiting on a silent connection.
                    using (_cts.Token.Register(response.Dispose))
                    using (Stream body = await response.OpenBodyAsync().ConfigureAwait(false))
                    {
                        while (cursor.Position <= end)
                        {
                            int want = (int)Math.Min(buffer.Length, end - cursor.Position + 1);
                            int n;
                            try
                            {
                                n = await body.ReadAsync(buffer, 0, want, _cts.Token).ConfigureAwait(false);
                            }
                            catch (IOException ex)
                            {
                                throw new TransientDownloadException(new LauncherException(LauncherError.NetworkUnavailable, "The connection to mega.nz was interrupted.", ex));
                            }

                            if (n <= 0)
                            {
                                return;
                            }

                            Interlocked.Exchange(ref _lastActivityTicks, _clock.ElapsedTicks);
                            ctr.Transform(buffer, 0, n, cursor.Position);
                            lock (_gate)
                            {
                                if (_closed)
                                {
                                    throw new OperationCanceledException();
                                }

                                _file.Position = cursor.Position;
                                _file.Write(buffer, 0, n);
                                _started = true;
                                _received += n;
                                _progress.Report(_received);
                            }

                            cursor.Position += n;
                        }
                    }
                }
            }

            private sealed class Cursor
            {
                public long Position;
            }
        }
    }
}
