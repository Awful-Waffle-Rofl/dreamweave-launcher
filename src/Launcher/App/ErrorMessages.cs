using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using DreamWeave.Launcher.Core;

namespace DreamWeave.Launcher.App
{
    /// <summary>
    /// The single table that turns a <see cref="LauncherError"/> into the plain-English message the
    /// player sees. Exception text and stack traces are never shown; a code with no entry would be a
    /// bug, and a test reflects over the enum so a new member without a message fails the build.
    /// </summary>
    internal static class ErrorMessages
    {
        private static readonly Dictionary<LauncherError, string> Table = new Dictionary<LauncherError, string>
        {
            { LauncherError.None, "Done." },
            { LauncherError.NetworkUnavailable, "The Internet could not be reached. Check your connection and try again." },
            { LauncherError.SourceHttpError, "The download site answered with an error. It may be down for a while; try again later." },
            { LauncherError.SourceQuotaExceeded, UiText.MegaQuota },
            { LauncherError.ResponseTooLarge, "The download site sent an unexpected answer. Try again later." },
            { LauncherError.HashMismatch, "A downloaded file failed its integrity check and was deleted. Click Retry to download it again." },
            { LauncherError.SizeMismatch, "A downloaded file was the wrong size and was deleted. Click Retry to download it again." },
            { LauncherError.ManifestSignatureInvalid, "The download list could not be verified, so it was not used. Try again later." },
            { LauncherError.ManifestRollback, "The download list offered was older than one already used, so it was refused. Try again later." },
            { LauncherError.ManifestSchemaUnsupported, "This launcher is too old to read the current download list. Update the launcher to install game files." },
            { LauncherError.ManifestUnavailable, "The download list could not be loaded. Check your Internet connection and try again." },
            { LauncherError.ManifestInvalid, "The download list contains an entry this launcher cannot use. Try again later." },
            { LauncherError.DiskFull, "There is not enough free disk space. Free up some space and click Retry." },
            { LauncherError.AccessDenied, "Windows denied access to a file or folder the launcher needs. Check the folder's permissions and try again." },
            { LauncherError.StateLocked, "Another copy of the launcher is busy with its settings. Close it and try again." },
            { LauncherError.ElevationDeclined, "Administrator permission was declined, so this step could not finish. Click Retry and choose Yes when Windows asks." },
            { LauncherError.InstallerFailed, "The Asheron's Call installer did not finish. Click Retry to run it again." },
            { LauncherError.PatchInputMismatch, "acclient.exe is not the version this fix was made for, so it was not changed." },
            { LauncherError.InvalidCredentials, "The account name or password cannot be used. Check the rules shown under the fields." },
            { LauncherError.ClientNotFound, "acclient.exe was not found in the game folder. Use Verify files in Settings to repair the install." },
            { LauncherError.LaunchFailed, "Windows could not start Asheron's Call. Use Verify files in Settings, then try again." },
            { LauncherError.ClientRequiresElevation, "acclient.exe is set to run as administrator. Turn off \"Run this program as an administrator\" in its Properties, Compatibility tab, then try again." },
            { LauncherError.CredentialStoreUnavailable, "Windows could not save or read the password. You can still play by typing it each time." },
            { LauncherError.Cancelled, UiText.CancelledResumable },
            { LauncherError.GameRunning, "A game file is in use, probably by a running Asheron's Call. Close the game and click Retry." },
            { LauncherError.ElevationRequired, "The game folder needs administrator permission to change. Click Retry and choose Yes when Windows asks." },
            { LauncherError.ReparsePointInPath, "The game folder contains a junction or link where a file should be, so nothing was written. Use a normal folder and try again." },
            { LauncherError.ArchiveInvalid, "The downloaded game data archive is not usable. Click Retry to download it again." },
            { LauncherError.PatchOutputMismatch, "The fix did not produce the expected result, so acclient.exe was left unchanged." },
            { LauncherError.ElevatedTaskInvalid, "The administrator step could not read its instructions, so nothing was changed. Click Retry." },
            { LauncherError.Unknown, "Something unexpected went wrong. Click Retry, or Copy details to report it." },
            { LauncherError.UpdateSignatureInvalid, "The launcher update is not signed by DreamWeave, so it was not installed." },
            { LauncherError.UpdateNotNewer, "The offered launcher update is not newer than this one, so it was not installed." },
            { LauncherError.UpdateSwapFailed, "The launcher update could not be installed. The current launcher is unchanged." },
            { LauncherError.UpdateRollbackFailed, "The launcher update failed and the launcher could not be restored. Download DreamWeave Launcher again from the website." },
            { LauncherError.VersionInvalid, "The launcher update information could not be read. Try again later." },
            { LauncherError.ShortcutFailed, "The Start menu shortcut could not be created or removed." },
            { LauncherError.RegistryWriteFailed, "The Apps and Features entry could not be written or removed." },
            { LauncherError.UninstallIncomplete, "Uninstall finished, but some items could not be removed." },
            { LauncherError.GameFilesChanged, "Some game files changed after they were installed, usually because another installer was still running. Close any other installers and click Retry." },
        };

        /// <summary>The player-facing message for <paramref name="code"/>; an unlisted value falls back to the Unknown message.</summary>
        public static string For(LauncherError code)
        {
            string text;
            return Table.TryGetValue(code, out text) ? text : Table[LauncherError.Unknown];
        }

        /// <summary>True when <paramref name="code"/> has its own entry (tests use this to catch new enum members).</summary>
        public static bool Has(LauncherError code)
        {
            return Table.ContainsKey(code);
        }
    }

    /// <summary>
    /// The text "Copy details" puts on the clipboard: the error code name, the launcher version, the
    /// step and the time. It never carries a path, an account name or exception text: the step is
    /// accepted only when it is a fixed short id (the telemetry step pattern), otherwise it is replaced.
    /// </summary>
    internal static class ErrorDetails
    {
        private static readonly Regex FilePattern = new Regex(@"^[A-Za-z0-9_][A-Za-z0-9_.\- /\\]{0,100}: (Ok|Mismatch|Missing|Locked|AccessDenied|Error)\z", RegexOptions.CultureInvariant);
        private static readonly Regex StepPattern = new Regex(@"^[a-z][a-z0-9-]{1,31}\z", RegexOptions.CultureInvariant);

        /// <summary>Builds the clipboard text.</summary>
        public static string Build(LauncherError code, string launcherVersion, string step, DateTime utcNow, IReadOnlyList<string> files = null)
        {
            string safeStep = step != null && StepPattern.IsMatch(step) ? step : "unknown";
            string safeVersion = launcherVersion != null && Regex.IsMatch(launcherVersion, @"^[0-9A-Za-z.\-]{1,40}\z") ? launcherVersion : "unknown";
            string codeName = Enum.IsDefined(typeof(LauncherError), code) ? code.ToString() : "Unknown";
            var sb = new StringBuilder();
            sb.Append(LauncherInfo.ProductName).Append(" error report").Append("\r\n");
            sb.Append("Error: ").Append(codeName).Append(" (").Append(((int)code).ToString(CultureInfo.InvariantCulture)).Append(")\r\n");
            sb.Append("Launcher version: ").Append(safeVersion).Append("\r\n");
            sb.Append("Step: ").Append(safeStep).Append("\r\n");
            sb.Append("Time (UTC): ").Append(utcNow.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append("\r\n");

            // Game-relative file names and states only (for example "client_portal.dat: Mismatch"); anything
            // that is not exactly that shape, such as an absolute path, is dropped.
            foreach (string line in (files ?? new string[0]).Where(l => l != null && FilePattern.IsMatch(l)).Take(12))
            {
                sb.Append("File: ").Append(line).Append("\r\n");
            }

            return sb.ToString();
        }
    }
}
