using System;
using DreamWeave.Launcher.Core.Manifests;

namespace DreamWeave.Launcher.Core.Update
{
    /// <summary>What the manifest's <c>launcher</c> block means for the running launcher.</summary>
    public enum UpdateStatus
    {
        /// <summary>No newer launcher is published (or the published one is not newer). Carry on.</summary>
        UpToDate = 0,

        /// <summary>A newer launcher is available; the player may update now or later.</summary>
        Optional = 1,

        /// <summary>The running launcher is below <c>minVersion</c> and a newer launcher is available: update before doing anything else.</summary>
        Mandatory = 2,

        /// <summary>
        /// The running launcher is below <c>minVersion</c> but the manifest offers nothing it may install
        /// (no download, or only a version that is not newer). Block downloads and tell the player to
        /// get the launcher again from the website.
        /// </summary>
        MandatoryUnavailable = 3,
    }

    /// <summary>The result of <see cref="UpdateChecker.Check"/>.</summary>
    public sealed class UpdateCheckResult
    {
        internal UpdateCheckResult(UpdateStatus status, SemVersion running, SemVersion available, SemVersion minimum, bool downgradeOffered)
        {
            Status = status;
            Running = running;
            Available = available;
            Minimum = minimum;
            DowngradeOffered = downgradeOffered;
        }

        /// <summary>The decision.</summary>
        public UpdateStatus Status { get; }

        /// <summary>The running launcher's version.</summary>
        public SemVersion Running { get; }

        /// <summary>The manifest's <c>launcher.version</c>, or null when absent.</summary>
        public SemVersion Available { get; }

        /// <summary>The manifest's <c>launcher.minVersion</c>, or null when absent.</summary>
        public SemVersion Minimum { get; }

        /// <summary>True when the manifest offered a LOWER version than the one running (refused; worth logging).</summary>
        public bool DowngradeOffered { get; }

        /// <summary>True for <see cref="UpdateStatus.Optional"/> and <see cref="UpdateStatus.Mandatory"/>.</summary>
        public bool CanUpdate => Status == UpdateStatus.Optional || Status == UpdateStatus.Mandatory;
    }

    /// <summary>
    /// Decides whether the launcher should update itself, from the signed manifest's <c>launcher</c>
    /// block and the running version. Never offers a downgrade: an update is installable only when
    /// <c>version</c> is strictly newer than the running version (semver precedence, so
    /// <c>1.2.0-beta.1</c> &lt; <c>1.2.0</c>) and a download (url, sha256, size) is published.
    /// </summary>
    /// <remarks>
    /// The floor is the RUNNING version, not <see cref="State.LauncherState.LauncherVersionSeen"/>:
    /// that field records the last version that ran (it detects a completed update), and a player
    /// who deliberately runs an older exe must still be able to update it forward. Downgrade attacks
    /// are already blocked upstream, because the manifest is signed and its serial is monotonic.
    /// </remarks>
    public static class UpdateChecker
    {
        /// <summary>Evaluates <paramref name="info"/> (may be null) against <paramref name="runningVersion"/>.</summary>
        /// <exception cref="LauncherException">
        /// <see cref="LauncherError.VersionInvalid"/> when the running version, or a non-empty
        /// <c>version</c> / <c>minVersion</c> in the manifest, is not a semantic version.
        /// </exception>
        public static UpdateCheckResult Check(LauncherUpdateInfo info, SemVersion runningVersion)
        {
            if (runningVersion == null)
            {
                throw new ArgumentNullException(nameof(runningVersion));
            }

            SemVersion available = null;
            SemVersion minimum = null;
            if (info != null && !string.IsNullOrWhiteSpace(info.Version))
            {
                available = SemVersion.Parse(info.Version, "manifest's launcher version");
            }

            if (info != null && !string.IsNullOrWhiteSpace(info.MinVersion))
            {
                minimum = SemVersion.Parse(info.MinVersion, "manifest's minimum launcher version");
            }

            bool downgrade = available != null && available < runningVersion;
            bool installable = available != null && available > runningVersion && HasDownload(info);
            bool mandatory = minimum != null && minimum > runningVersion;

            UpdateStatus status;
            if (mandatory)
            {
                status = installable ? UpdateStatus.Mandatory : UpdateStatus.MandatoryUnavailable;
            }
            else
            {
                status = installable ? UpdateStatus.Optional : UpdateStatus.UpToDate;
            }

            return new UpdateCheckResult(status, runningVersion, available, minimum, downgrade);
        }

        /// <summary>True when the block names a download the updater can fetch and verify.</summary>
        internal static bool HasDownload(LauncherUpdateInfo info)
        {
            return info != null
                && !string.IsNullOrWhiteSpace(info.Url)
                && Uri.IsWellFormedUriString(info.Url, UriKind.Absolute)
                && Hasher.IsSha256Hex(info.Sha256)
                && info.Size > 0;
        }
    }
}
