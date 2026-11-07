using System;
using System.Diagnostics;

namespace DreamWeave.Launcher.Core.Downloads
{
    /// <summary>
    /// Turns a stream of absolute byte counts into throttled <see cref="ProgressReport"/>s for one
    /// download. Throughput is an exponential moving average over samples taken at least
    /// <see cref="SampleInterval"/> apart; ETA is the remaining bytes over that average. Reports are
    /// emitted at most once per <see cref="ReportInterval"/>, plus an unconditional one from
    /// <see cref="Complete"/>. <see cref="Report"/> and <see cref="Complete"/> are serialised by a
    /// lock, so a late callback from another thread cannot interleave with them.
    /// <para>
    /// The sink is invoked INSIDE that lock, on whatever thread the transport reports from, so it
    /// must return quickly and never block or wait on the UI thread. UI code should pass a
    /// <see cref="Progress{T}"/> created on the UI thread, which posts each report to the UI's
    /// synchronization context instead of running it inline.
    /// </para>
    /// </summary>
    public sealed class TransferProgress
    {
        /// <summary>Minimum spacing of throughput samples.</summary>
        public static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(500);

        /// <summary>Minimum spacing of reports (at most ~10 per second).</summary>
        public static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(100);

        /// <summary>Weight of the newest sample in the moving average.</summary>
        public const double Smoothing = 0.3;

        /// <summary>Message on download reports.</summary>
        public const string DownloadingMessage = "Downloading";

        private readonly string _stepId;
        private readonly long _total;
        private readonly IProgress<ProgressReport> _sink;
        private readonly Func<TimeSpan> _clock;
        private readonly object _gate = new object();

        private bool _started;
        private TimeSpan _sampleTime;
        private long _sampleBytes;
        private TimeSpan _lastReport;
        private bool _reportedOnce;
        private double? _ema;

        /// <summary>Creates a tracker that reports to <paramref name="sink"/> (which may be null).</summary>
        public TransferProgress(string stepId, long totalBytes, IProgress<ProgressReport> sink)
            : this(stepId, totalBytes, sink, StopwatchClock())
        {
        }

        /// <summary>Test constructor with an injectable monotonic clock.</summary>
        internal TransferProgress(string stepId, long totalBytes, IProgress<ProgressReport> sink, Func<TimeSpan> clock)
        {
            _stepId = stepId ?? string.Empty;
            _total = totalBytes;
            _sink = sink;
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        /// <summary>Latest absolute byte count passed to <see cref="Report"/>.</summary>
        public long BytesDone { get; private set; }

        /// <summary>Current smoothed throughput in bytes per second, or null before the first sample.</summary>
        public double? BytesPerSecond => _ema;

        /// <summary>
        /// Records that <paramref name="bytesDone"/> bytes of the file are now on disk. A value lower
        /// than the previous one (a restart from zero) is reported as-is and restarts the sampling
        /// window, keeping the smoothed rate.
        /// </summary>
        public void Report(long bytesDone)
        {
            lock (_gate)
            {
                ReportCore(bytesDone);
            }
        }

        private void ReportCore(long bytesDone)
        {
            TimeSpan now = _clock();
            if (!_started || bytesDone < _sampleBytes)
            {
                _started = true;
                _sampleTime = now;
                _sampleBytes = bytesDone;
            }
            else if (now - _sampleTime >= SampleInterval)
            {
                double seconds = (now - _sampleTime).TotalSeconds;
                double instant = (bytesDone - _sampleBytes) / seconds;
                _ema = _ema.HasValue ? (Smoothing * instant) + ((1 - Smoothing) * _ema.Value) : instant;
                _sampleTime = now;
                _sampleBytes = bytesDone;
            }

            BytesDone = bytesDone;
            if (!_reportedOnce || now - _lastReport >= ReportInterval)
            {
                Emit(now);
            }
        }

        /// <summary>Emits a final report with the current byte count, regardless of throttling.</summary>
        public void Complete()
        {
            lock (_gate)
            {
                Emit(_clock());
            }
        }

        private void Emit(TimeSpan now)
        {
            _reportedOnce = true;
            _lastReport = now;
            if (_sink == null)
            {
                return;
            }

            TimeSpan? eta = null;
            if (_ema.HasValue && _ema.Value > 0 && _total >= BytesDone)
            {
                double seconds = (_total - BytesDone) / _ema.Value;
                if (seconds < TimeSpan.MaxValue.TotalSeconds)
                {
                    eta = TimeSpan.FromSeconds(seconds);
                }
            }

            _sink.Report(new ProgressReport(_stepId, BytesDone, _total, _ema, eta, DownloadingMessage));
        }

        private static Func<TimeSpan> StopwatchClock()
        {
            Stopwatch sw = Stopwatch.StartNew();
            return () => sw.Elapsed;
        }
    }
}
