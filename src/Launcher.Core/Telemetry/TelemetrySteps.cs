using System;
using System.Collections.Generic;

namespace DreamWeave.Launcher.Core.Telemetry
{
    /// <summary>
    /// The fixed step ids <see cref="TelemetryClient.Record"/> accepts. Anything else (a typo, or text
    /// that should never be sent) is dropped; manifest component ids go through
    /// <see cref="TelemetryClient.RecordComponent"/>. Every value matches the contract's step pattern.
    /// </summary>
    internal static class TelemetrySteps
    {
        public const string Manifest = "manifest";
        public const string AcClient = "ac-client";
        public const string EorDats = "eor-dats";
        public const string PaletteLeakFix = "palette-leak-fix";
        public const string Launch = "launch";
        public const string LauncherUpdate = "launcher-update";
        public const string SelfInstall = "self-install";
        public const string Uninstall = "uninstall";

        private static readonly HashSet<string> All = new HashSet<string>(StringComparer.Ordinal)
        {
            Manifest, AcClient, EorDats, PaletteLeakFix, Launch, LauncherUpdate, SelfInstall, Uninstall,
        };

        public static bool IsKnown(string step)
        {
            return step != null && All.Contains(step);
        }
    }
}