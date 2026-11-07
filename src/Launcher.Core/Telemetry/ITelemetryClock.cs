using System;
using System.Threading;
using System.Threading.Tasks;

namespace DreamWeave.Launcher.Core.Telemetry
{
    /// <summary>Time source and delay for the telemetry flusher; tests inject a fake so waits cost nothing.</summary>
    internal interface ITelemetryClock
    {
        /// <summary>The current UTC time.</summary>
        DateTime UtcNow { get; }

        /// <summary>Completes after <paramref name="delay"/> (or throws <see cref="OperationCanceledException"/>).</summary>
        Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
    }

    internal sealed class SystemTelemetryClock : ITelemetryClock
    {
        public DateTime UtcNow => DateTime.UtcNow;

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            return delay <= TimeSpan.Zero ? Task.FromResult(0) : Task.Delay(delay, cancellationToken);
        }
    }
}
