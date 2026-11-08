using System;
using System.Collections.Generic;

namespace DreamWeave.Launcher.Core.Telemetry
{
    /// <summary>
    /// The fixed step ids <see cref="TelemetryClient.Record"/> accepts. Anything else (a typo, or text
    /// that should never be sent) is dropped; manifest component ids go through
    /// <see cref="TelemetryClient.RecordComponent"/>. Every value matches the contract's step pattern.
    /// </summary>
    public static class TelemetrySteps
    {
        /// <summary>Step id <c>manifest</c>.</summary>
        public const string Manifest = "manifest";
        /// <summary>Step id <c>ac-client</c>.</summary>
        public const string AcClient = "ac-client";
        /// <summary>Step id <c>eor-dats</c>.</summary>
        public const string EorDats = "eor-dats";
        /// <summary>Step id <c>palette-leak-fix</c>.</summary>
        public const string PaletteLeakFix = "palette-leak-fix";
        /// <summary>Step id <c>launch</c>.</summary>
        public const string Launch = "launch";
        /// <summary>Step id <c>launcher-update</c>.</summary>
        public const string LauncherUpdate = "launcher-update";
        /// <summary>Step id <c>self-install</c>. RETIRED, do not emit: the launcher no longer installs itself. Kept so the step id stays valid for the telemetry contract.</summary>
        public const string SelfInstall = "self-install";
        /// <summary>Step id <c>uninstall</c>.</summary>
        public const string Uninstall = "uninstall";

        private static readonly HashSet<string> All = new HashSet<string>(StringComparer.Ordinal)
        {
            Manifest, AcClient, EorDats, PaletteLeakFix, Launch, LauncherUpdate, SelfInstall, Uninstall,
        };

        /// <summary>True when <paramref name="step"/> is one of the fixed step ids.</summary>
        public static bool IsKnown(string step)
        {
            return step != null && All.Contains(step);
        }
    }
}