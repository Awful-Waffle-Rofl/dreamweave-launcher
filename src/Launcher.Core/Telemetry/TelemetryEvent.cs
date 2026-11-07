using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace DreamWeave.Launcher.Core.Telemetry
{
    /// <summary>How an install step ended (the contract's <c>outcome</c>).</summary>
    public enum TelemetryOutcome
    {
        /// <summary>The step began.</summary>
        Started,

        /// <summary>The step finished successfully.</summary>
        Succeeded,

        /// <summary>The step failed; the event's error code says why.</summary>
        Failed,

        /// <summary>The player cancelled the step.</summary>
        Cancelled,
    }

    /// <summary>One queued event: the contract's event fields plus the envelope values that must stay with it across restarts.</summary>
    internal sealed class TelemetryEvent
    {
        public const int MaxSeq = 10000;
        public const long MaxDurationMs = 86400000;
        public const int MinOsBuild = 7600;
        public const int MaxOsBuild = 99999;

        private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

        /// <summary>Contract: <c>^[a-z][a-z0-9-]{1,31}\z</c>.</summary>
        public static readonly Regex StepPattern = new Regex(@"^[a-z][a-z0-9-]{1,31}\z", RegexOptions.CultureInvariant, RegexTimeout);

        /// <summary>Contract: <c>^[0-9]{1,4}\.[0-9]{1,4}\.[0-9]{1,4}(-[0-9A-Za-z.]{1,20})?\z</c>.</summary>
        public static readonly Regex VersionPattern = new Regex(@"^[0-9]{1,4}\.[0-9]{1,4}\.[0-9]{1,4}(-[0-9A-Za-z.]{1,20})?\z", RegexOptions.CultureInvariant, RegexTimeout);

        public Guid RunId { get; set; }

        public string LauncherVersion { get; set; }

        public int OsBuild { get; set; }

        public int Seq { get; set; }

        public string Step { get; set; }

        public string Outcome { get; set; }

        public int Error { get; set; }

        public long? DurationMs { get; set; }

        public DateTime AtUtc { get; set; }

        /// <summary>Events that share this key can travel in one request (same envelope).</summary>
        public string EnvelopeKey => RunId.ToString("D") + "|" + LauncherVersion + "|" + OsBuild.ToString(CultureInfo.InvariantCulture);

        public static string OutcomeToWire(TelemetryOutcome outcome)
        {
            switch (outcome)
            {
                case TelemetryOutcome.Started: return "started";
                case TelemetryOutcome.Succeeded: return "succeeded";
                case TelemetryOutcome.Failed: return "failed";
                case TelemetryOutcome.Cancelled: return "cancelled";
                default: return null;
            }
        }

        public static bool IsStepValid(string step)
        {
            return step != null && StepPattern.IsMatch(step);
        }

        public static bool IsVersionValid(string version)
        {
            return version != null && VersionPattern.IsMatch(version);
        }

        public static bool IsOutcomeValid(string outcome)
        {
            return outcome == "started" || outcome == "succeeded" || outcome == "failed" || outcome == "cancelled";
        }

        /// <summary>The event object exactly as the contract's <c>events[]</c> element.</summary>
        public JObject ToWire()
        {
            return new JObject
            {
                ["seq"] = Seq,
                ["step"] = Step,
                ["outcome"] = Outcome,
                ["error"] = Error,
                ["durationMs"] = DurationMs.HasValue ? new JValue(DurationMs.Value) : JValue.CreateNull(),
            };
        }

        /// <summary>The queue-file form (wire fields plus envelope values and the record time).</summary>
        public JObject ToStored()
        {
            JObject o = ToWire();
            o["runId"] = RunId.ToString("D");
            o["v"] = LauncherVersion;
            o["os"] = OsBuild;
            o["at"] = AtUtc.ToString("o", CultureInfo.InvariantCulture);
            return o;
        }

        /// <summary>Parses a queue-file entry, re-validating every field (the file is user-writable). Null when invalid.</summary>
        public static TelemetryEvent FromStored(JToken token)
        {
            var o = token as JObject;
            if (o == null)
            {
                return null;
            }

            try
            {
                if (!TryInt(o["seq"], 0, MaxSeq, out int seq) ||
                    !TryInt(o["error"], 0, 65535, out int error) ||
                    !TryInt(o["os"], MinOsBuild, MaxOsBuild, out int os))
                {
                    return null;
                }

                string step = Str(o["step"]);
                string outcome = Str(o["outcome"]);
                string version = Str(o["v"]);
                if (!IsStepValid(step) || !IsOutcomeValid(outcome) || !IsVersionValid(version))
                {
                    return null;
                }

                if (!Guid.TryParseExact(Str(o["runId"]) ?? string.Empty, "D", out Guid run))
                {
                    return null;
                }

                long? duration = null;
                JToken d = o["durationMs"];
                if (d != null && d.Type != JTokenType.Null)
                {
                    if (d.Type != JTokenType.Integer)
                    {
                        return null;
                    }

                    long dv = d.Value<long>();
                    if (dv < 0 || dv > MaxDurationMs)
                    {
                        return null;
                    }

                    duration = dv;
                }

                if (!DateTime.TryParse(Str(o["at"]) ?? string.Empty, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime at))
                {
                    return null;
                }

                return new TelemetryEvent
                {
                    RunId = run,
                    LauncherVersion = version,
                    OsBuild = os,
                    Seq = seq,
                    Step = step,
                    Outcome = outcome,
                    Error = error,
                    DurationMs = duration,
                    AtUtc = at.Kind == DateTimeKind.Utc ? at : at.ToUniversalTime(),
                };
            }
            catch (Exception ex) when (ex is FormatException || ex is InvalidCastException || ex is OverflowException || ex is RegexMatchTimeoutException)
            {
                return null;
            }
        }

        private static string Str(JToken t)
        {
            return t != null && t.Type == JTokenType.String ? (string)t : null;
        }

        private static bool TryInt(JToken t, int min, int max, out int value)
        {
            value = 0;
            if (t == null || t.Type != JTokenType.Integer)
            {
                return false;
            }

            long v = t.Value<long>();
            if (v < min || v > max)
            {
                return false;
            }

            value = (int)v;
            return true;
        }
    }
}
