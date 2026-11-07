using System.Globalization;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Manifests;
using DreamWeave.Launcher.Core.Update;

namespace DreamWeave.Launcher.App
{
    /// <summary>What the update check means for the launcher window.</summary>
    internal sealed class UpdateGate
    {
        private UpdateGate(UpdateStatus status, string availableVersion)
        {
            Status = status;
            AvailableVersion = availableVersion;
        }

        /// <summary>No update information (not checked yet, or nothing to offer).</summary>
        public static UpdateGate None { get; } = new UpdateGate(UpdateStatus.UpToDate, null);

        /// <summary>The checker's decision.</summary>
        public UpdateStatus Status { get; }

        /// <summary>The offered version, or null.</summary>
        public string AvailableVersion { get; }

        /// <summary>Play is blocked: the running launcher is below the manifest's minimum.</summary>
        public bool BlocksPlay => Status == UpdateStatus.Mandatory || Status == UpdateStatus.MandatoryUnavailable;

        /// <summary>An Update button should be offered.</summary>
        public bool OffersUpdate => Status == UpdateStatus.Mandatory || Status == UpdateStatus.Optional;

        /// <summary>A Later button should be offered (optional updates only).</summary>
        public bool OffersLater => Status == UpdateStatus.Optional;

        /// <summary>Banner text, or null when nothing should be shown.</summary>
        public string BannerText
        {
            get
            {
                switch (Status)
                {
                    case UpdateStatus.Optional:
                        return string.Format(CultureInfo.InvariantCulture, UiText.UpdateOptionalFormat, AvailableVersion);
                    case UpdateStatus.Mandatory:
                        return string.Format(CultureInfo.InvariantCulture, UiText.UpdateMandatoryFormat, AvailableVersion);
                    case UpdateStatus.MandatoryUnavailable:
                        return UiText.UpdateUnavailable;
                    default:
                        return null;
                }
            }
        }

        /// <summary>Wraps a checker result.</summary>
        public static UpdateGate From(UpdateCheckResult result)
        {
            return result == null ? None : new UpdateGate(result.Status, result.Available?.ToString());
        }

        /// <summary>
        /// Runs <see cref="UpdateChecker.Check"/> for <paramref name="manifest"/> against
        /// <paramref name="running"/>. An unreadable version in the manifest is not a reason to block
        /// play, so <see cref="LauncherError.VersionInvalid"/> yields <see cref="None"/>.
        /// </summary>
        public static UpdateGate Evaluate(LauncherManifest manifest, SemVersion running)
        {
            if (manifest == null || running == null)
            {
                return None;
            }

            try
            {
                return From(UpdateChecker.Check(manifest.Launcher, running));
            }
            catch (LauncherException ex) when (ex.Code == LauncherError.VersionInvalid)
            {
                return None;
            }
        }
    }
}
