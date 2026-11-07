using System;
using System.Collections.Concurrent;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using DreamWeave.Launcher.Core.Manifests;

namespace DreamWeave.Launcher.Core.Downloads
{
    /// <summary>Tunables for <see cref="MegaTransport"/>.</summary>
    public sealed class MegaTransportOptions
    {
        /// <summary>No progress for this long cancels the attempt and retries.</summary>
        public TimeSpan InactivityTimeout { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>How long to wait for a cancelled MegaApiClient download to unwind before abandoning it.</summary>
        public TimeSpan AbandonGrace { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>Retry budget and back-off (applies to network-class failures only).</summary>
        public RetryPolicy Retry { get; set; } = new RetryPolicy();
    }

    /// <summary>
    /// mega.nz public-link download through <see cref="IMegaClient"/>: anonymous login, link
    /// resolution, a size check against the manifest BEFORE any body bytes are fetched, then the
    /// decrypting download into <see cref="DownloadRequest.PartPath"/>. Progress bytes are the
    /// library's percent x the node size. MegaApiClient has no resume or offset API, so every attempt
    /// starts from zero and any failure (including cancellation) deletes the part. Quota errors are
    /// not retried (mega's anonymous quota recovers over hours, not seconds); network-class errors are.
    /// </summary>
    public sealed class MegaTransport : IDownloadTransport
    {
        private readonly Func<IMegaClient> _clientFactory;
        private readonly MegaTransportOptions _options;

        // Part paths whose last attempt was abandoned while its library read was still running.
        // Instance state (not per call), so a cancel during a stall followed by an immediate new
        // DownloadAsync still treats the held part as transient. Cleared once an attempt deletes the part.
        private readonly ConcurrentDictionary<string, bool> _possiblyHeld = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Production constructor (MegaApiClient).</summary>
        public MegaTransport()
            : this(() => new MegaApiClientAdapter())
        {
        }

        /// <summary>Constructor for tests: <paramref name="clientFactory"/> is called once per attempt.</summary>
        public MegaTransport(Func<IMegaClient> clientFactory, MegaTransportOptions options = null)
        {
            _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
            _options = options ?? new MegaTransportOptions();
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
            string key = Path.GetFullPath(request.PartPath);
            while (true)
            {
                var attempt = new Attempt(_possiblyHeld.ContainsKey(key));
                try
                {
                    await AttemptAsync(request, progress, attempt, cancellationToken).ConfigureAwait(false);
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
                    if (!transient || retriesUsed >= _options.Retry.MaxRetries)
                    {
                        throw mapped;
                    }

                    await _options.Retry.WaitAsync(retriesUsed, null, cancellationToken).ConfigureAwait(false);
                    retriesUsed++;
                }
                finally
                {
                    attempt.Detach();
                }
            }
        }

        private async Task AttemptAsync(DownloadRequest request, TransferProgress progress, Attempt attempt, CancellationToken cancellationToken)
        {
            if (!PartFiles.TryDelete(request.PartPath))
            {
                if (attempt.PreviousAbandoned)
                {
                    // The abandoned MegaApiClient read still holds the part (CreateNew, share Read).
                    // Transient, so the back-off gives that read time to unwind.
                    throw new LauncherException(LauncherError.NetworkUnavailable, "mega.nz is still finishing the previous attempt.");
                }

                PartFiles.Delete(request.PartPath, "clearing an old partial download");
            }

            bool ignored;
            _possiblyHeld.TryRemove(Path.GetFullPath(request.PartPath), out ignored);
            using (IMegaClient client = _clientFactory())
            {
                await WithCancellation(client.LoginAnonymousAsync(), cancellationToken).ConfigureAwait(false);
                MegaFile file = await WithCancellation(client.GetFileFromLinkAsync(request.Url), cancellationToken).ConfigureAwait(false);
                if (file == null || file.Size != request.ExpectedSize)
                {
                    throw new LauncherException(
                        LauncherError.SizeMismatch,
                        "The file on mega.nz is " + (file == null ? 0 : file.Size) + " bytes, but " + request.ExpectedSize + " bytes were expected.");
                }

                attempt.Report(progress, 0);
                var clock = Stopwatch.StartNew();
                long lastActivityTicks = clock.ElapsedTicks;
                long lastBytes = 0;
                var percent = new PercentProgress(p =>
                {
                    double clamped = p < 0 ? 0 : (p > 100 ? 100 : p);
                    long bytes = (long)Math.Round(clamped / 100.0 * file.Size);
                    if (bytes != Interlocked.Exchange(ref lastBytes, bytes))
                    {
                        Interlocked.Exchange(ref lastActivityTicks, clock.ElapsedTicks);
                    }

                    attempt.Report(progress, bytes);
                });

                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    Task download = client.DownloadFileAsync(file, request.PartPath, percent, linked.Token);
                    TimeSpan poll = TimeSpan.FromTicks(Math.Max(TimeSpan.FromMilliseconds(10).Ticks, Math.Min(TimeSpan.FromSeconds(1).Ticks, _options.InactivityTimeout.Ticks / 4)));
                    while (true)
                    {
                        Task first = await Task.WhenAny(download, Task.Delay(poll)).ConfigureAwait(false);
                        if (first == download)
                        {
                            break;
                        }

                        bool stalled = TimeSpan.FromSeconds((clock.ElapsedTicks - Interlocked.Read(ref lastActivityTicks)) / (double)Stopwatch.Frequency) > _options.InactivityTimeout;
                        if (cancellationToken.IsCancellationRequested || stalled)
                        {
                            // Detach first: from here on the library's callbacks must not touch the
                            // shared tracker, which the next attempt will be driving.
                            attempt.Detach();
                            linked.Cancel();
                            await Task.WhenAny(download, Task.Delay(_options.AbandonGrace)).ConfigureAwait(false);
                            if (!download.IsCompleted)
                            {
                                _possiblyHeld[Path.GetFullPath(request.PartPath)] = true;
                            }

                            Observe(download);
                            cancellationToken.ThrowIfCancellationRequested();
                            throw new LauncherException(LauncherError.NetworkUnavailable, "mega.nz stopped sending data.");
                        }
                    }

                    await download.ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Per-attempt state. Once detached, progress callbacks from that attempt's (possibly still
        /// running) library task are dropped; the check and the report happen under one lock, so no
        /// report from a detached attempt can land after <see cref="Detach"/> returns.
        /// </summary>
        private sealed class Attempt
        {
            private readonly object _gate = new object();
            private bool _detached;

            public Attempt(bool previousAbandoned)
            {
                PreviousAbandoned = previousAbandoned;
            }

            public bool PreviousAbandoned { get; }

            public void Report(TransferProgress progress, long bytes)
            {
                lock (_gate)
                {
                    if (!_detached)
                    {
                        progress.Report(bytes);
                    }
                }
            }

            public void Detach()
            {
                lock (_gate)
                {
                    _detached = true;
                }
            }
        }
        private static async Task<T> WithCancellation<T>(Task<T> task, CancellationToken cancellationToken)
        {
            await WithCancellation((Task)task, cancellationToken).ConfigureAwait(false);
            return await task.ConfigureAwait(false);
        }

        private static async Task WithCancellation(Task task, CancellationToken cancellationToken)
        {
            if (!cancellationToken.CanBeCanceled)
            {
                await task.ConfigureAwait(false);
                return;
            }

            var cancelled = new TaskCompletionSource<bool>();
            using (cancellationToken.Register(() => cancelled.TrySetResult(true)))
            {
                Task first = await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false);
                if (first != task)
                {
                    Observe(task);
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }

            await task.ConfigureAwait(false);
        }

        private static void Observe(Task task)
        {
            task.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        }

        /// <summary>Synchronous <see cref="IProgress{T}"/> (no SynchronizationContext hop, unlike <see cref="Progress{T}"/>).</summary>
        private sealed class PercentProgress : IProgress<double>
        {
            private readonly Action<double> _callback;

            public PercentProgress(Action<double> callback)
            {
                _callback = callback;
            }

            public void Report(double value)
            {
                _callback(value);
            }
        }
    }
}
