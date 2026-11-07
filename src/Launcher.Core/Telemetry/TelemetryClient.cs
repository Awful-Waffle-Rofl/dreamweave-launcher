using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DreamWeave.Launcher.Core.Manifests;
using DreamWeave.Launcher.Core.Net;
using DreamWeave.Launcher.Core.State;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DreamWeave.Launcher.Core.Telemetry
{
    /// <summary>
    /// Opt-in, anonymous, fire-and-forget usage telemetry (contract: <c>launcher-telemetry-v1</c>).
    /// <para>
    /// <b>Gate.</b> Nothing is recorded, queued, written or sent unless BOTH the player opted in
    /// (<see cref="LauncherState.TelemetryOptIn"/>) AND the manifest's <c>telemetry.endpoint</c> is
    /// non-null and passes <see cref="TelemetryEndpoint"/>. Until <see cref="Configure"/> /
    /// <see cref="ConfigureAsync"/> has run, the client is off.
    /// </para>
    /// <para>
    /// <b>Threading.</b> <see cref="Record"/> never blocks and never throws: it takes a short
    /// in-memory lock and signals a background flusher. Everything that touches the disk, the
    /// network or the <see cref="StateStore"/> runs on that flusher or on a thread-pool task
    /// (<see cref="ConfigureAsync"/>, <see cref="SetOptInAsync"/>); the blocking variants must not
    /// be called from the UI thread.
    /// </para>
    /// <para>
    /// <b>Queue.</b> Events wait in memory and in <c>telemetry-queue.json</c> under the launcher
    /// data folder (written atomically), so a failed or interrupted send survives a restart. At most
    /// <see cref="MaxQueuedEvents"/> are kept (the oldest are dropped) and an event older than
    /// <see cref="MaxEventAge"/> is dropped, whether it is found at start-up or later.
    /// </para>
    /// <para>
    /// <b>Sending.</b> Batches of up to <see cref="MaxBatchEvents"/> events from one run, flushed a few
    /// seconds after the first unsent event and at least a few seconds apart. 202 (any 2xx) removes
    /// the batch. 429 and 503 wait <c>Retry-After</c> (clamped; 60 s when absent) and retry the batch
    /// ONCE; if that also answers 429/503 the events stay queued for a later start. Every other
    /// status (400, 413, 415, other 4xx/5xx, redirects) DROPS the batch and is never retried.
    /// A network failure keeps the events and pauses sending for a minute. Nothing loops.
    /// </para>
    /// </summary>
    public sealed class TelemetryClient : IDisposable
    {
        /// <summary>Most events kept in the queue; the oldest are dropped first.</summary>
        public const int MaxQueuedEvents = 200;

        /// <summary>Most events in one request (contract limit).</summary>
        public const int MaxBatchEvents = 50;

        /// <summary>Largest request body (contract limit: 16 KiB).</summary>
        public const int MaxBodyBytes = 16384;

        /// <summary>Events older than this are dropped unsent.</summary>
        public static readonly TimeSpan MaxEventAge = TimeSpan.FromDays(7);

        /// <summary>File name of the queue inside <see cref="AppPaths.DataDir"/>.</summary>
        public const string QueueFileName = "telemetry-queue.json";

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        private readonly string _queuePath;
        private readonly StateStore _store;
        private readonly IJsonPoster _poster;
        private readonly ITelemetryClock _clock;
        private readonly TelemetryOptions _options;
        private readonly IEnumerable<string> _allowedHosts;
        private readonly Guid _runId = Guid.NewGuid();
        private readonly string _launcherVersion;
        private readonly int _osBuild;

        // Lock order: _ioGate, then _gate. Record() takes only _gate, and only briefly.
        private readonly object _gate = new object();
        private readonly object _ioGate = new object();
        private readonly SemaphoreSlim _sendGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _signal = new SemaphoreSlim(0, int.MaxValue);
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        private List<TelemetryEvent> _queue = new List<TelemetryEvent>();
        private bool _optIn;
        private Uri _endpoint;
        private bool _loaded;
        private int _nextSeq;
        private Task _worker;
        private bool _disposed;
        private DateTime _notBeforeUtc = DateTime.MinValue;

        /// <summary>Creates a client for production use. It is OFF until <see cref="ConfigureAsync"/> is called.</summary>
        /// <param name="paths">Where the queue file lives (<see cref="AppPaths.DataDir"/>).</param>
        /// <param name="store">The launcher state (opt-in flag and install id).</param>
        /// <param name="allowedHosts">Extra exact endpoint hosts trusted regardless of the server's domain (may be null).</param>
        public TelemetryClient(AppPaths paths, StateStore store, IEnumerable<string> allowedHosts = null)
            : this(paths, store, allowedHosts, new TelemetryOptions())
        {
        }

        internal TelemetryClient(AppPaths paths, StateStore store, IEnumerable<string> allowedHosts, TelemetryOptions options)
        {
            if (paths == null)
            {
                throw new ArgumentNullException(nameof(paths));
            }

            _store = store ?? throw new ArgumentNullException(nameof(store));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _allowedHosts = allowedHosts;
            _queuePath = Path.Combine(paths.DataDir, QueueFileName);
            _poster = options.Poster ?? new JsonPoster();
            _clock = options.Clock ?? new SystemTelemetryClock();
            string version = options.LauncherVersion ?? ReadLauncherVersion();
            _launcherVersion = TelemetryEvent.IsVersionValid(version) ? version : null;
            _osBuild = options.OsBuild ?? ReadOsBuild();
        }

        /// <summary>This process's run id (a new GUID per process).</summary>
        public Guid RunId => _runId;

        /// <summary>True when events are currently being recorded (opted in, valid endpoint, usable version and OS build).</summary>
        public bool IsEnabled
        {
            get
            {
                lock (_gate)
                {
                    return IsEnabledLocked();
                }
            }
        }

        /// <summary>Number of events currently queued (for diagnostics and tests).</summary>
        internal int QueuedCount
        {
            get
            {
                lock (_gate)
                {
                    return _queue.Count;
                }
            }
        }

        /// <summary>
        /// Queues one event. Returns immediately, never blocks on disk or network and never throws.
        /// A no-op unless telemetry is enabled; an event with an invalid step is dropped.
        /// </summary>
        /// <param name="step">Step id, <c>^[a-z][a-z0-9-]{1,31}$</c> (for example <c>manifest</c> or a component id).</param>
        /// <param name="outcome">How the step ended.</param>
        /// <param name="error">The failure classification; its numeric value is sent. Use <see cref="LauncherError.None"/> when there is none.</param>
        /// <param name="duration">How long the step took, or null if not measured.</param>
        public void Record(string step, TelemetryOutcome outcome, LauncherError error = LauncherError.None, TimeSpan? duration = null)
        {
            try
            {
                string wireOutcome = TelemetryEvent.OutcomeToWire(outcome);
                int code = (int)error;
                if (wireOutcome == null || !TelemetryEvent.IsStepValid(step) || code < 0 || code > 65535)
                {
                    return;
                }

                long? ms = null;
                if (duration.HasValue)
                {
                    double total = duration.Value.TotalMilliseconds;
                    ms = total < 0 ? 0 : (total > TelemetryEvent.MaxDurationMs ? TelemetryEvent.MaxDurationMs : (long)total);
                }

                lock (_gate)
                {
                    if (_disposed || !IsEnabledLocked() || _nextSeq > TelemetryEvent.MaxSeq)
                    {
                        return;
                    }

                    _queue.Add(new TelemetryEvent
                    {
                        RunId = _runId,
                        LauncherVersion = _launcherVersion,
                        OsBuild = _osBuild,
                        Seq = _nextSeq++,
                        Step = step,
                        Outcome = wireOutcome,
                        Error = code,
                        DurationMs = ms,
                        AtUtc = _clock.UtcNow,
                    });
                    TrimLocked(_clock.UtcNow);
                }

                _signal.Release();
            }
            catch (Exception)
            {
                // Telemetry must never disturb the launcher.
            }
        }

        /// <summary>
        /// Reads the opt-in flag from the state store on a thread-pool thread, then applies it with
        /// the manifest's endpoint. Call again whenever a new manifest is accepted.
        /// </summary>
        public Task ConfigureAsync(LauncherManifest manifest)
        {
            return Task.Run(() => Configure(_store.Read(s => s.TelemetryOptIn), manifest));
        }

        /// <summary>
        /// Applies the opt-in flag and manifest; loads the persisted queue when this turns telemetry on.
        /// Does file I/O: background threads only.
        /// </summary>
        public void Configure(bool optIn, LauncherManifest manifest)
        {
            string endpoint = manifest?.Telemetry?.Endpoint;
            string serverHost = manifest?.Server?.Host;
            Uri validated = TelemetryEndpoint.Validate(endpoint, serverHost, _allowedHosts, _options.AllowLoopbackHttp);
            lock (_ioGate)
            {
                lock (_gate)
                {
                    _optIn = optIn;
                    _endpoint = validated;
                }

                if (IsEnabled)
                {
                    LoadQueueLocked();
                    StartWorker();
                    if (QueuedCount > 0)
                    {
                        _signal.Release();
                    }
                }
            }
        }

        /// <summary><see cref="SetOptIn"/> on a thread-pool thread, so a UI handler can await it.</summary>
        public Task SetOptInAsync(bool optIn)
        {
            return Task.Run(() => SetOptIn(optIn));
        }

        /// <summary>
        /// Turns telemetry on or off and persists the choice. Turning it OFF first stops recording and
        /// deletes the queue (memory and file) so nothing further leaves the machine, then writes
        /// <c>TelemetryOptIn = false</c> and a brand-new <see cref="LauncherState.InstallId"/> (the old
        /// id, which the server has already seen, is discarded and never reused). A request already in
        /// flight when this is called cannot be recalled. Blocks (state lock, file I/O): background threads only.
        /// </summary>
        /// <exception cref="LauncherException">When the state file cannot be written; the local queue is already gone in that case.</exception>
        public void SetOptIn(bool optIn)
        {
            if (!optIn)
            {
                lock (_ioGate)
                {
                    lock (_gate)
                    {
                        _optIn = false;
                        _queue.Clear();
                        _loaded = false;
                    }

                    DeleteQueueFiles();
                }

                _store.Update(s =>
                {
                    s.TelemetryOptIn = false;
                    s.InstallId = Guid.NewGuid();
                });
                return;
            }

            _store.Update(s => s.TelemetryOptIn = true);
            lock (_ioGate)
            {
                lock (_gate)
                {
                    _optIn = true;
                }

                if (IsEnabled)
                {
                    LoadQueueLocked();
                    StartWorker();
                }
            }
        }

        /// <summary>
        /// Sends everything that is queued now (no debounce), and persists the result. For app exit and
        /// tests; the background flusher does the same after a short delay. Honours the same retry rules.
        /// </summary>
        public async Task FlushNowAsync(CancellationToken cancellationToken)
        {
            await RunCycleAsync(false, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Stops the flusher and writes the queue to disk one last time. Waits at most about two seconds.</summary>
        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            _cts.Cancel();
            try
            {
                _worker?.Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception)
            {
            }

            PersistQueue();
            (_poster as IDisposable)?.Dispose();
        }

        private bool IsEnabledLocked()
        {
            return _optIn && _endpoint != null && _launcherVersion != null && _osBuild >= TelemetryEvent.MinOsBuild && _osBuild <= TelemetryEvent.MaxOsBuild;
        }

        private void StartWorker()
        {
            lock (_gate)
            {
                if (_worker != null || !_options.StartFlusher || _disposed)
                {
                    return;
                }

                _worker = Task.Run(WorkerLoopAsync);
            }
        }

        private async Task WorkerLoopAsync()
        {
            CancellationToken token = _cts.Token;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await _signal.WaitAsync(token).ConfigureAwait(false);
                    PersistQueue();
                    await _clock.DelayAsync(_options.Debounce, token).ConfigureAwait(false);
                    while (_signal.Wait(0))
                    {
                    }

                    await RunCycleAsync(true, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception)
                {
                    // Never let the flusher die on an unexpected error; wait for the next signal.
                }
            }
        }

        private enum BatchResult
        {
            Accepted,
            Dropped,
            Keep,
        }

        private async Task RunCycleAsync(bool respectCooldown, CancellationToken token)
        {
            await _sendGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (respectCooldown)
                {
                    TimeSpan wait = _notBeforeUtc - _clock.UtcNow;
                    if (wait > TimeSpan.Zero)
                    {
                        await _clock.DelayAsync(wait, token).ConfigureAwait(false);
                    }
                }

                bool first = true;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    List<TelemetryEvent> batch;
                    Uri endpoint;
                    lock (_gate)
                    {
                        if (!IsEnabledLocked())
                        {
                            return;
                        }

                        TrimLocked(_clock.UtcNow);
                        endpoint = _endpoint;
                        batch = TakeBatchLocked();
                    }

                    if (batch.Count == 0)
                    {
                        PersistQueue();
                        return;
                    }

                    if (!first)
                    {
                        await _clock.DelayAsync(_options.MinRequestInterval, token).ConfigureAwait(false);
                    }

                    first = false;
                    BatchResult result;
                    try
                    {
                        result = await SendBatchAsync(endpoint, batch, token).ConfigureAwait(false);
                    }
                    catch (LauncherException ex) when (ex.Code != LauncherError.Cancelled)
                    {
                        // Network trouble: keep the events, pause, and stop this cycle.
                        _notBeforeUtc = _clock.UtcNow + _options.FailureCooldown;
                        PersistQueue();
                        return;
                    }

                    if (result == BatchResult.Keep)
                    {
                        PersistQueue();
                        return;
                    }

                    lock (_gate)
                    {
                        var sent = new HashSet<TelemetryEvent>(batch);
                        _queue.RemoveAll(sent.Contains);
                    }

                    PersistQueue();
                }
            }
            finally
            {
                _sendGate.Release();
            }
        }

        /// <summary>Oldest event's envelope group, up to 50 events and 16 KiB, in queue order.</summary>
        private List<TelemetryEvent> TakeBatchLocked()
        {
            var batch = new List<TelemetryEvent>();
            if (_queue.Count == 0)
            {
                return batch;
            }

            string key = _queue[0].EnvelopeKey;
            foreach (TelemetryEvent e in _queue)
            {
                if (e.EnvelopeKey != key || batch.Count >= MaxBatchEvents)
                {
                    break;
                }

                batch.Add(e);
            }

            return batch;
        }

        private async Task<BatchResult> SendBatchAsync(Uri endpoint, List<TelemetryEvent> batch, CancellationToken token)
        {
            Guid installId = _store.InstallId;
            byte[] body = BuildBody(installId, batch);
            while (body.Length > MaxBodyBytes && batch.Count > 1)
            {
                batch.RemoveRange(batch.Count / 2, batch.Count - batch.Count / 2);
                body = BuildBody(installId, batch);
            }

            if (body.Length > MaxBodyBytes)
            {
                return BatchResult.Dropped;
            }

            for (int attempt = 0; ; attempt++)
            {
                HttpPostResult result = await _poster.PostJsonAsync(endpoint, body, token).ConfigureAwait(false);
                if (result.IsSuccess)
                {
                    return BatchResult.Accepted;
                }

                if (result.Status != 429 && result.Status != 503)
                {
                    // 400, 413, 415, other 4xx/5xx and redirects (never followed): drop, never retry.
                    return BatchResult.Dropped;
                }

                TimeSpan wait = result.RetryAfter ?? _options.DefaultRetryAfter;
                if (attempt >= 1 || wait > _options.MaxRetryAfter)
                {
                    _notBeforeUtc = _clock.UtcNow + (wait > _options.MaxRetryAfter ? _options.MaxRetryAfter : wait);
                    return BatchResult.Keep;
                }

                if (wait < TimeSpan.Zero)
                {
                    wait = TimeSpan.Zero;
                }

                await _clock.DelayAsync(wait, token).ConfigureAwait(false);
                lock (_gate)
                {
                    if (!IsEnabledLocked())
                    {
                        return BatchResult.Keep;
                    }
                }
            }
        }

        private byte[] BuildBody(Guid installId, List<TelemetryEvent> batch)
        {
            var events = new JArray();
            foreach (TelemetryEvent e in batch)
            {
                events.Add(e.ToWire());
            }

            TelemetryEvent head = batch[0];
            var envelope = new JObject
            {
                ["schema"] = 1,
                ["installId"] = installId.ToString("D"),
                ["runId"] = head.RunId.ToString("D"),
                ["launcherVersion"] = head.LauncherVersion,
                ["osBuild"] = head.OsBuild,
                ["events"] = events,
            };
            return Utf8NoBom.GetBytes(envelope.ToString(Formatting.None));
        }

        /// <summary>Drops events past the age limit, then the oldest beyond the count cap. Caller holds <c>_gate</c>.</summary>
        private void TrimLocked(DateTime nowUtc)
        {
            DateTime cutoff = nowUtc - MaxEventAge;
            if (_queue.Any(e => e.AtUtc < cutoff))
            {
                _queue.RemoveAll(e => e.AtUtc < cutoff);
            }

            if (_queue.Count > MaxQueuedEvents)
            {
                _queue.RemoveRange(0, _queue.Count - MaxQueuedEvents);
            }
        }

        /// <summary>Reads the queue file once and puts its events BEFORE anything recorded since. Caller holds <c>_ioGate</c>.</summary>
        private void LoadQueueLocked()
        {
            if (_loaded)
            {
                return;
            }

            var loaded = new List<TelemetryEvent>();
            try
            {
                if (File.Exists(_queuePath))
                {
                    JObject root = JsonConvert.DeserializeObject<JObject>(File.ReadAllText(_queuePath, Encoding.UTF8), JsonDefaults.Create());
                    if (root?["events"] is JArray arr)
                    {
                        foreach (JToken t in arr)
                        {
                            TelemetryEvent e = TelemetryEvent.FromStored(t);
                            if (e != null)
                            {
                                loaded.Add(e);
                            }
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException)
            {
                // Unreadable or corrupt: start empty; the next persist replaces the file.
                loaded.Clear();
            }

            lock (_gate)
            {
                loaded.AddRange(_queue);
                _queue = loaded;
                _loaded = true;
                TrimLocked(_clock.UtcNow);
            }
        }

        /// <summary>Writes the queue file (or deletes it when empty), unless telemetry is off. Never throws.</summary>
        private void PersistQueue()
        {
            try
            {
                lock (_ioGate)
                {
                    List<TelemetryEvent> snapshot;
                    lock (_gate)
                    {
                        if (!_optIn || !_loaded && _queue.Count == 0)
                        {
                            return;
                        }

                        TrimLocked(_clock.UtcNow);
                        snapshot = new List<TelemetryEvent>(_queue);
                    }

                    if (snapshot.Count == 0)
                    {
                        if (File.Exists(_queuePath))
                        {
                            File.Delete(_queuePath);
                        }

                        return;
                    }

                    var arr = new JArray();
                    foreach (TelemetryEvent e in snapshot)
                    {
                        arr.Add(e.ToStored());
                    }

                    var root = new JObject { ["version"] = 1, ["events"] = arr };
                    AtomicFile.WriteAllBytes(_queuePath, Utf8NoBom.GetBytes(root.ToString(Formatting.None)));
                }
            }
            catch (Exception)
            {
                // Persistence is best effort; the in-memory queue still works.
            }
        }

        /// <summary>Deletes the queue file and any leftover temp siblings. Caller holds <c>_ioGate</c>.</summary>
        private void DeleteQueueFiles()
        {
            try
            {
                string dir = Path.GetDirectoryName(_queuePath);
                if (dir != null && Directory.Exists(dir))
                {
                    foreach (string f in Directory.GetFiles(dir, QueueFileName + "*"))
                    {
                        File.Delete(f);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // A locked file is retried by nothing; the in-memory queue is already empty and
                // the file is ignored once telemetry is off. Surface nothing: opt-out must not fail.
            }
        }

        /// <summary>
        /// The launcher version for the envelope: the entry assembly's informational version with any
        /// <c>+metadata</c> removed, else its <c>major.minor.build</c>. Null when neither matches the contract pattern.
        /// </summary>
        internal static string ReadLauncherVersion()
        {
            try
            {
                Assembly asm = Assembly.GetEntryAssembly() ?? typeof(TelemetryClient).Assembly;
                var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
                if (info != null)
                {
                    string v = info.InformationalVersion;
                    int plus = v.IndexOf('+');
                    if (plus >= 0)
                    {
                        v = v.Substring(0, plus);
                    }

                    if (TelemetryEvent.IsVersionValid(v))
                    {
                        return v;
                    }
                }

                Version ver = asm.GetName().Version;
                string fallback = ver.Major.ToString(CultureInfo.InvariantCulture) + "." + ver.Minor.ToString(CultureInfo.InvariantCulture) + "." + Math.Max(0, ver.Build).ToString(CultureInfo.InvariantCulture);
                return TelemetryEvent.IsVersionValid(fallback) ? fallback : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// The Windows build NUMBER only (for example 26200): registry
        /// <c>HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\CurrentBuildNumber</c> in the native view,
        /// falling back to <see cref="Environment.OSVersion"/>. The registry is preferred because
        /// <c>Environment.OSVersion</c> reports a compatibility-shimmed version on Windows 8.1+ unless the
        /// executable's manifest declares supported OSes. No edition, language, hardware or name is read.
        /// A value outside 7600..99999 disables telemetry rather than being sent.
        /// </summary>
        internal static int ReadOsBuild()
        {
            try
            {
                using (RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32))
                using (RegistryKey key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    if (key?.GetValue("CurrentBuildNumber") is string s && int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out int build))
                    {
                        return build;
                    }
                }
            }
            catch (Exception)
            {
            }

            return Environment.OSVersion.Version.Build;
        }
    }

    /// <summary>Tunables and test seams for <see cref="TelemetryClient"/>; production uses the defaults.</summary>
    internal sealed class TelemetryOptions
    {
        /// <summary>Wait after the first unsent event before flushing, so nearby events share a request.</summary>
        public TimeSpan Debounce { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>Minimum gap between two requests (server budget: 10 requests/minute per client).</summary>
        public TimeSpan MinRequestInterval { get; set; } = TimeSpan.FromSeconds(6);

        /// <summary>Wait used when a 429/503 carries no usable <c>Retry-After</c>.</summary>
        public TimeSpan DefaultRetryAfter { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>Longest <c>Retry-After</c> honoured in-process; a longer one leaves the events queued.</summary>
        public TimeSpan MaxRetryAfter { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>Pause after a network failure.</summary>
        public TimeSpan FailureCooldown { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>False in tests that drive flushing by hand.</summary>
        public bool StartFlusher { get; set; } = true;

        /// <summary>Tests only: accept an http loopback endpoint.</summary>
        public bool AllowLoopbackHttp { get; set; }

        public IJsonPoster Poster { get; set; }

        public ITelemetryClock Clock { get; set; }

        public string LauncherVersion { get; set; }

        public int? OsBuild { get; set; }
    }
}
