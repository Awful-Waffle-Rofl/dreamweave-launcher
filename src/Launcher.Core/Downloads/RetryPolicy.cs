using System;
using System.Threading;
using System.Threading.Tasks;

namespace DreamWeave.Launcher.Core.Downloads
{
    /// <summary>
    /// Retry budget and back-off shared by the transports: up to <see cref="MaxRetries"/> retries
    /// after the first attempt, waiting <see cref="BaseDelay"/> x 2^n (n = retries already used), or
    /// the server's Retry-After when it sent one, capped at <see cref="MaxDelay"/>. The wait itself
    /// is injectable so tests run without real sleeps.
    /// </summary>
    public sealed class RetryPolicy
    {
        /// <summary>Production defaults: 3 retries, 2 s base (2, 4, 8 s), Retry-After honoured up to 2 minutes.</summary>
        public RetryPolicy()
        {
        }

        /// <summary>Retries after the first attempt.</summary>
        public int MaxRetries { get; set; } = 3;

        /// <summary>First back-off delay.</summary>
        public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(2);

        /// <summary>Upper bound on any single wait, including a server's Retry-After.</summary>
        public TimeSpan MaxDelay { get; set; } = TimeSpan.FromMinutes(2);

        /// <summary>The wait primitive (defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>).</summary>
        public Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = (d, ct) => Task.Delay(d, ct);

        /// <summary>The wait before retry number <paramref name="retriesUsed"/> + 1.</summary>
        public TimeSpan DelayFor(int retriesUsed, TimeSpan? retryAfter)
        {
            TimeSpan d = retryAfter ?? TimeSpan.FromTicks(BaseDelay.Ticks * (1L << Math.Min(retriesUsed, 20)));
            if (d < TimeSpan.Zero)
            {
                d = TimeSpan.Zero;
            }

            return d > MaxDelay ? MaxDelay : d;
        }

        /// <summary>Waits before the next retry, mapping cancellation to <see cref="LauncherError.Cancelled"/>.</summary>
        internal async Task WaitAsync(int retriesUsed, TimeSpan? retryAfter, CancellationToken cancellationToken)
        {
            try
            {
                await Delay(DelayFor(retriesUsed, retryAfter), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                throw new LauncherException(LauncherError.Cancelled, "Cancelled.", ex);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                throw new LauncherException(LauncherError.Cancelled, "Cancelled.");
            }
        }
    }

    /// <summary>
    /// A failure worth retrying. <see cref="Final"/> is what the caller sees if the retry budget
    /// runs out; <see cref="RetryAfter"/> is the server's requested wait, if any.
    /// </summary>
    internal sealed class TransientDownloadException : Exception
    {
        public TransientDownloadException(LauncherException final, TimeSpan? retryAfter = null)
            : base(final.Detail, final)
        {
            Final = final;
            RetryAfter = retryAfter;
        }

        public LauncherException Final { get; }

        public TimeSpan? RetryAfter { get; }
    }
}
