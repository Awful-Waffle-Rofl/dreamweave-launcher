using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Cleanup;
using DreamWeave.Launcher.Core.Manifests;
using DreamWeave.Launcher.Core.Telemetry;

namespace DreamWeave.Launcher.App
{
    /// <summary>
    /// Opens the launcher download page in the default browser. This is the whole "update": the launcher
    /// never downloads, writes, renames or replaces its own exe.
    /// </summary>
    internal static class DownloadPageOpener
    {
        /// <summary>
        /// Validates <paramref name="url"/> as an absolute https URL again (it came from a manifest, possibly
        /// cached) and starts its escaped AbsoluteUri form (never the raw string) through the shell. Returns <see cref="LauncherError.None"/> when the page was
        /// opened, <see cref="LauncherError.ManifestInvalid"/> when the URL is not acceptable (nothing is
        /// started), or <see cref="LauncherError.Unknown"/> when the shell could not start it.
        /// </summary>
        public static LauncherError Open(IProcessLauncher processes, string url)
        {
            if (processes == null)
            {
                throw new ArgumentNullException(nameof(processes));
            }

            Uri uri;
            if (!LauncherUpdateInfo.TryGetDownloadUri(url, out uri))
            {
                return LauncherError.ManifestInvalid;
            }

            try
            {
                processes.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                return LauncherError.None;
            }
            catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException || ex is FileNotFoundException || ex is PlatformNotSupportedException)
            {
                return LauncherError.Unknown;
            }
        }

        /// <summary>
        /// The Update button's work: records the launcher-update telemetry step (Started, then Succeeded when the
        /// page opened or Failed with the error code) around <see cref="Open"/>. Returns the same code.
        /// </summary>
        public static LauncherError OpenAndRecord(IProcessLauncher processes, ITelemetrySink telemetry, string url)
        {
            if (telemetry == null)
            {
                throw new ArgumentNullException(nameof(telemetry));
            }

            telemetry.Record(TelemetrySteps.LauncherUpdate, TelemetryOutcome.Started);
            Stopwatch clock = Stopwatch.StartNew();
            LauncherError code = Open(processes, url);
            telemetry.Record(TelemetrySteps.LauncherUpdate, code == LauncherError.None ? TelemetryOutcome.Succeeded : TelemetryOutcome.Failed, code, clock.Elapsed);
            return code;
        }

        /// <summary>
        /// The status text for a failed <see cref="Open"/>. When the shell could not start the page, the player is
        /// given the (validated, escaped) URL to visit by hand; a refused URL is never echoed.
        /// </summary>
        public static string FailureMessage(LauncherError code, string url)
        {
            Uri uri;
            if (code == LauncherError.Unknown && LauncherUpdateInfo.TryGetDownloadUri(url, out uri))
            {
                return string.Format(System.Globalization.CultureInfo.InvariantCulture, UiText.OpenPageFailedFormat, uri.AbsoluteUri);
            }

            return ErrorMessages.For(code);
        }    }
}
