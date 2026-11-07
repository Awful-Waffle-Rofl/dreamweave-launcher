using System;

namespace DreamWeave.Launcher.Core
{
    /// <summary>
    /// An immutable progress snapshot for one step of work (a download, a hash, an extract).
    /// Reported through <see cref="IProgress{T}"/>; consumers must not assume reports arrive on
    /// any particular thread.
    /// </summary>
    public sealed class ProgressReport
    {
        /// <summary>Creates a progress snapshot.</summary>
        /// <param name="stepId">Stable identifier of the step (for example a manifest component id plus a phase).</param>
        /// <param name="bytesDone">Bytes processed so far (never negative).</param>
        /// <param name="bytesTotal">Total bytes when known, otherwise null.</param>
        /// <param name="bytesPerSecond">Recent throughput when known, otherwise null.</param>
        /// <param name="eta">Estimated time remaining when known, otherwise null.</param>
        /// <param name="message">Short plain-English status line for the UI (may be empty, never null).</param>
        public ProgressReport(string stepId, long bytesDone, long? bytesTotal, double? bytesPerSecond, TimeSpan? eta, string message)
        {
            StepId = stepId ?? string.Empty;
            BytesDone = bytesDone < 0 ? 0 : bytesDone;
            BytesTotal = bytesTotal;
            BytesPerSecond = bytesPerSecond;
            Eta = eta;
            Message = message ?? string.Empty;
        }

        /// <summary>Stable identifier of the step this report belongs to.</summary>
        public string StepId { get; }

        /// <summary>Bytes processed so far.</summary>
        public long BytesDone { get; }

        /// <summary>Total bytes for the step, or null when unknown.</summary>
        public long? BytesTotal { get; }

        /// <summary>Recent throughput in bytes per second, or null when unknown.</summary>
        public double? BytesPerSecond { get; }

        /// <summary>Estimated time remaining, or null when unknown.</summary>
        public TimeSpan? Eta { get; }

        /// <summary>Short plain-English status line.</summary>
        public string Message { get; }

        /// <summary>Completed fraction in [0, 1], or null when the total is unknown or zero.</summary>
        public double? Fraction
        {
            get
            {
                if (!BytesTotal.HasValue || BytesTotal.Value <= 0)
                {
                    return null;
                }

                double f = (double)BytesDone / BytesTotal.Value;
                return f > 1 ? 1 : f;
            }
        }
    }
}
