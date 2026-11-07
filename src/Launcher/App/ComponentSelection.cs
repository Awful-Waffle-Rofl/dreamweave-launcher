using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DreamWeave.Launcher.Core.Detection;
using DreamWeave.Launcher.Core.Manifests;

namespace DreamWeave.Launcher.App
{
    /// <summary>What a component row says about itself.</summary>
    internal enum RowStatus
    {
        /// <summary>Already satisfied: greyed, checked, nothing to do.</summary>
        Done = 0,

        /// <summary>Present but differs from the published version.</summary>
        NeedsRepair = 1,

        /// <summary>Not installed; will be downloaded when checked.</summary>
        WillDownload = 2,

        /// <summary>A patch that can be applied (no download).</summary>
        WillApply = 3,

        /// <summary>Does not apply to the current files (a patch whose target is absent or different).</summary>
        NotApplicable = 4,

        /// <summary>Files could not be read (in use, access denied).</summary>
        Unknown = 5,
    }

    /// <summary>One row of the wizard's Components screen.</summary>
    internal sealed class ComponentRow
    {
        internal ComponentRow(ManifestComponent component, ComponentStatus status, RowStatus rowStatus, bool initiallyChecked, bool canToggle, bool coveredByOverlay)
        {
            Component = component;
            Status = status;
            RowStatus = rowStatus;
            Checked = initiallyChecked;
            CanToggle = canToggle;
            CoveredByOverlay = coveredByOverlay;
        }

        /// <summary>The manifest component.</summary>
        public ManifestComponent Component { get; }

        /// <summary>Its evaluated status (never null; a missing evaluation is Unknown).</summary>
        public ComponentStatus Status { get; }

        /// <summary>Component id.</summary>
        public string Id => Component.Id;

        /// <summary>Title from the manifest.</summary>
        public string Title => Component.Title ?? Component.Id;

        /// <summary>"Downloaded from: &lt;sourceLabel&gt;".</summary>
        public string SourceText => string.Format(CultureInfo.InvariantCulture, UiText.DownloadedFromFormat, Component.SourceLabel ?? string.Empty);

        /// <summary>Human size of what would be downloaded.</summary>
        public string SizeText => Component.Kind == ComponentKinds.BytePatch || Component.Size <= 0 ? UiText.SizeSmall : ByteFormat.Size(Component.Size);

        /// <summary>The row's status.</summary>
        public RowStatus RowStatus { get; }

        /// <summary>Whether the component is selected for this run (Done rows are checked and greyed).</summary>
        public bool Checked { get; set; }

        /// <summary>False for Done rows, which are greyed out.</summary>
        public bool CanToggle { get; }

        /// <summary>
        /// True for an installer whose only problem is a file that a zip overlay in the same manifest
        /// replaces (the C1 case: acclient.exe differs, and the game-data step puts the right one in place).
        /// </summary>
        public bool CoveredByOverlay { get; }

        /// <summary>
        /// Extra explanatory text for the row (C1), or null. The source label is already shown on its
        /// own line, and the manifest has no description field, so nothing else goes here.
        /// </summary>
        public string Note => CoveredByOverlay ? UiText.ClientExeMismatch : null;

        /// <summary>Whether this row has work to do when checked.</summary>
        public bool HasWork => RowStatus != RowStatus.Done && RowStatus != RowStatus.NotApplicable;

        /// <summary>Plain status label.</summary>
        public string StatusText
        {
            get
            {
                switch (RowStatus)
                {
                    case RowStatus.Done: return UiText.StatusDone;
                    case RowStatus.NeedsRepair: return Checked ? UiText.StatusNeedsRepair : UiText.StatusNeedsRepair + " - " + UiText.StatusNotSelected;
                    case RowStatus.WillDownload: return Checked ? UiText.StatusWillDownload : UiText.StatusNotSelected;
                    case RowStatus.WillApply: return Checked ? UiText.StatusWillApply : UiText.StatusNotSelected;
                    case RowStatus.NotApplicable: return UiText.StatusNotApplicable;
                    default: return UiText.StatusUnknown;
                }
            }
        }
    }

    /// <summary>
    /// The Components screen's logic: builds one row per manifest component and decides whether Next
    /// is allowed. Rules:
    /// <list type="bullet">
    /// <item>Satisfied rows are Done: checked, greyed, no work.</item>
    /// <item>Optional components start unchecked, whatever the manifest's <c>defaultSelected</c> says.</item>
    /// <item>Required components with work start checked, EXCEPT an installer that is NeedsRepair only
    /// because files that a zip overlay in the same manifest replaces differ (acclient.exe, C1): the
    /// 600 MB installer is not auto-checked, because the overlay fixes that file and players swap exes.</item>
    /// <item>Next is blocked while a required component is neither satisfied nor checked, unless it is
    /// such an overlay-covered installer and the covering overlay is checked or already satisfied.</item>
    /// </list>
    /// </summary>
    internal sealed class ComponentSelection
    {
        private readonly LauncherManifest _manifest;
        private readonly bool _downloadsAllowed;

        /// <summary>Builds the rows.</summary>
        /// <param name="manifest">The verified manifest.</param>
        /// <param name="statuses">Evaluation of the manifest's components.</param>
        /// <param name="downloadsAllowed"><see cref="ManifestLoadResult.DownloadsAllowed"/>: false blocks every download.</param>
        public ComponentSelection(LauncherManifest manifest, IReadOnlyList<ComponentStatus> statuses, bool downloadsAllowed = true)
        {
            _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
            _downloadsAllowed = downloadsAllowed;
            var byId = new Dictionary<string, ComponentStatus>(StringComparer.Ordinal);
            foreach (ComponentStatus s in statuses ?? new ComponentStatus[0])
            {
                if (s?.Id != null)
                {
                    byId[s.Id] = s;
                }
            }

            var rows = new List<ComponentRow>();
            foreach (ManifestComponent c in manifest.Components)
            {
                ComponentStatus status;
                if (!byId.TryGetValue(c.Id, out status))
                {
                    status = new ComponentStatus(c.Id, c.Kind, ComponentState.Unknown, null, 0, null);
                }

                rows.Add(BuildRow(manifest, c, status));
            }

            Rows = rows;
        }

        /// <summary>One row per manifest component, in manifest order.</summary>
        public IReadOnlyList<ComponentRow> Rows { get; }

        /// <summary>The rows that will run: checked, with work to do, in manifest order.</summary>
        public IReadOnlyList<ComponentRow> Selected => Rows.Where(r => r.Checked && r.HasWork).ToList();

        /// <summary>Total download size of <see cref="Selected"/>.</summary>
        public long SelectedDownloadBytes => Selected.Where(r => r.Component.Kind != ComponentKinds.BytePatch).Sum(r => Math.Max(0, r.Component.Size));

        /// <summary>Why Next is blocked, in plain English, or null when it is allowed.</summary>
        public string NextBlockedReason
        {
            get
            {
                if (_manifest.SkippedRequired.Count > 0 || !_downloadsAllowed)
                {
                    return UiText.NeedsNewerLauncher;
                }

                foreach (ComponentRow r in Rows)
                {
                    if (!r.Component.Required || r.RowStatus == RowStatus.Done || r.Checked)
                    {
                        continue;
                    }

                    if (r.CoveredByOverlay && CoveringOverlayWillBeRight(r))
                    {
                        continue;
                    }

                    if (r.RowStatus == RowStatus.Unknown)
                    {
                        return string.Format(CultureInfo.InvariantCulture, UiText.StatusUnknownBlockedFormat, r.Title);
                    }

                    return string.Format(CultureInfo.InvariantCulture, UiText.RequiredNotSelectedFormat, r.Title);
                }

                return null;
            }
        }

        /// <summary>True when Next may be clicked.</summary>
        public bool CanContinue => NextBlockedReason == null;

        /// <summary>
        /// True when an unchecked required component could not be checked (Unknown): the screen offers
        /// "Check again", which evaluates the files once more.
        /// </summary>
        public bool OffersCheckAgain => Rows.Any(r => r.Component.Required && r.RowStatus == RowStatus.Unknown && !r.Checked);

        /// <summary>Sets a row's checkbox (ignored for rows that cannot be toggled).</summary>
        public void SetChecked(string componentId, bool value)
        {
            ComponentRow row = Rows.FirstOrDefault(r => r.Id == componentId);
            if (row != null && row.CanToggle)
            {
                row.Checked = value;
            }
        }

        private bool CoveringOverlayWillBeRight(ComponentRow installer)
        {
            IEnumerable<string> bad = MismatchedPaths(installer.Status);
            return bad.All(path => Rows.Any(o =>
                o.Component.Kind == ComponentKinds.ZipOverlay
                && (o.Checked || o.RowStatus == RowStatus.Done)
                && OverlayProvides(o.Component, path)));
        }

        private static ComponentRow BuildRow(LauncherManifest manifest, ManifestComponent c, ComponentStatus status)
        {
            RowStatus rs;
            switch (status.State)
            {
                case ComponentState.Satisfied:
                    return new ComponentRow(c, status, RowStatus.Done, true, false, false);
                case ComponentState.NeedsRepair:
                    rs = RowStatus.NeedsRepair;
                    break;
                case ComponentState.Missing:
                    rs = c.Kind == ComponentKinds.BytePatch ? RowStatus.WillApply : RowStatus.WillDownload;
                    break;
                case ComponentState.NotApplicable:
                    // A patch whose target is absent now may become applicable once the game data is in place.
                    rs = c.Kind == ComponentKinds.BytePatch ? RowStatus.WillApply : RowStatus.NotApplicable;
                    break;
                default:
                    rs = RowStatus.Unknown;
                    break;
            }

            bool covered = c.Kind == ComponentKinds.Installer
                && status.State == ComponentState.NeedsRepair
                && IsCoveredByOverlay(manifest, status);

            bool initiallyChecked;
            if (!c.Required)
            {
                initiallyChecked = false;
            }
            else if (covered || rs == RowStatus.Unknown || rs == RowStatus.NotApplicable)
            {
                initiallyChecked = false;
            }
            else
            {
                initiallyChecked = true;
            }

            return new ComponentRow(c, status, rs, initiallyChecked, true, covered);
        }

        /// <summary>True when every mismatched file of <paramref name="status"/> is provided by some zip overlay in <paramref name="manifest"/>.</summary>
        internal static bool IsCoveredByOverlay(LauncherManifest manifest, ComponentStatus status)
        {
            List<string> bad = MismatchedPaths(status).ToList();
            if (bad.Count == 0 || status.Files.Any(f => f.State == FileState.Missing || f.State == FileState.Locked || f.State == FileState.AccessDenied || f.State == FileState.Error))
            {
                return false;
            }

            return bad.All(path => manifest.Components.Any(o => o.Kind == ComponentKinds.ZipOverlay && OverlayProvides(o, path)));
        }

        private static IEnumerable<string> MismatchedPaths(ComponentStatus status)
        {
            return status.Files.Where(f => f.State == FileState.Mismatch).Select(f => f.Path);
        }

        private static bool OverlayProvides(ManifestComponent overlay, string path)
        {
            string want = Canonical(path);
            return overlay.Files != null && overlay.Files.Any(f => string.Equals(Canonical(f.Path), want, StringComparison.OrdinalIgnoreCase));
        }

        private static string Canonical(string path)
        {
            return (path ?? string.Empty).Replace('\\', '/');
        }
    }

    /// <summary>Byte counts and durations as short text.</summary>
    internal static class ByteFormat
    {
        /// <summary>For example <c>571 MB</c> or <c>4.2 GB</c>.</summary>
        public static string Size(long bytes)
        {
            if (bytes < 0)
            {
                bytes = 0;
            }

            const double Kb = 1024, Mb = Kb * 1024, Gb = Mb * 1024;
            if (bytes >= Gb)
            {
                return (bytes / Gb).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
            }

            if (bytes >= Mb)
            {
                return (bytes / Mb).ToString("0", CultureInfo.InvariantCulture) + " MB";
            }

            if (bytes >= Kb)
            {
                return (bytes / Kb).ToString("0", CultureInfo.InvariantCulture) + " KB";
            }

            return bytes.ToString(CultureInfo.InvariantCulture) + " bytes";
        }

        /// <summary>For example <c>3 min</c>, <c>45 s</c> or <c>1 h 5 min</c>.</summary>
        public static string Duration(TimeSpan t)
        {
            if (t < TimeSpan.Zero)
            {
                t = TimeSpan.Zero;
            }

            if (t.TotalHours >= 1)
            {
                return ((int)t.TotalHours).ToString(CultureInfo.InvariantCulture) + " h " + t.Minutes.ToString(CultureInfo.InvariantCulture) + " min";
            }

            if (t.TotalMinutes >= 1)
            {
                return ((int)Math.Ceiling(t.TotalMinutes)).ToString(CultureInfo.InvariantCulture) + " min";
            }

            return ((int)Math.Ceiling(t.TotalSeconds)).ToString(CultureInfo.InvariantCulture) + " s";
        }
    }
}
