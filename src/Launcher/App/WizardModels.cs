using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Detection;
using DreamWeave.Launcher.Core.Downloads;
using DreamWeave.Launcher.Core.Install;
using DreamWeave.Launcher.Core.Manifests;

namespace DreamWeave.Launcher.App
{
    /// <summary>The Welcome screen's state.</summary>
    internal sealed class WelcomeModel
    {
        /// <summary>Creates the model from the manifest (may be null).</summary>
        /// <param name="manifest">Supplies the privacy link.</param>
        /// <param name="currentOptIn">
        /// The saved choice. A fresh install has none, so the box starts OFF; it is only ever pre-ticked
        /// for a player who already opted in, so the box never shows OFF while telemetry is on.
        /// </param>
        public WelcomeModel(LauncherManifest manifest, bool currentOptIn = false)
        {
            string privacy = manifest?.Links?.Privacy;
            PrivacyUrl = IsHttpsUrl(privacy) ? privacy : null;
            TelemetryOptIn = currentOptIn;
        }

        /// <summary>Telemetry opt-in checkbox. Starts OFF unless the player already opted in.</summary>
        public bool TelemetryOptIn { get; set; }

        /// <summary>The privacy link, or null to hide it (absent, empty or not an https URL).</summary>
        public string PrivacyUrl { get; }

        /// <summary>Whether the Privacy link is shown.</summary>
        public bool ShowPrivacyLink => PrivacyUrl != null;

        private static bool IsHttpsUrl(string value)
        {
            Uri u;
            return !string.IsNullOrWhiteSpace(value) && Uri.TryCreate(value, UriKind.Absolute, out u) && u.Scheme == Uri.UriSchemeHttps;
        }
    }

    /// <summary>Outcome of checking a candidate game folder on the Location screen.</summary>
    internal sealed class FolderCheck
    {
        internal FolderCheck(bool usable, bool hasClient, string message)
        {
            Usable = usable;
            HasClient = hasClient;
            Message = message;
        }

        /// <summary>The folder may be used.</summary>
        public bool Usable { get; }

        /// <summary>The folder already contains acclient.exe.</summary>
        public bool HasClient { get; }

        /// <summary>Plain-English line for the screen.</summary>
        public string Message { get; }
    }

    /// <summary>The Location screen's logic (pure apart from the injected file-system and disk queries).</summary>
    internal sealed class LocationPresenter
    {
        private readonly IAcFileSystem _fs;
        private readonly IDiskSpace _disk;
        private readonly Func<string, long, string, long, FreeSpacePreview> _preview;

        public LocationPresenter(IAcFileSystem fileSystem, IDiskSpace disk)
            : this(fileSystem, disk, null)
        {
        }

        /// <summary>Test constructor: <paramref name="preview"/> replaces Core's <see cref="AcWriter.PreviewFreeSpace(IDiskSpace, string, long, string, long)"/>.</summary>
        internal LocationPresenter(IAcFileSystem fileSystem, IDiskSpace disk, Func<string, long, string, long, FreeSpacePreview> preview)
        {
            _fs = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
            _disk = disk ?? throw new ArgumentNullException(nameof(disk));
            _preview = preview ?? ((a, ab, b, bb) => AcWriter.PreviewFreeSpace(_disk, a, ab, b, bb));
        }

        /// <summary>
        /// Validates <paramref name="folder"/>: it must be an absolute path on a local fixed drive
        /// (Core's <see cref="AcPathSafety.CheckLocalFixedPath"/> wording is used for the reason).
        /// </summary>
        public FolderCheck Check(string folder)
        {
            string reason = AcPathSafety.CheckLocalFixedPath(folder);
            if (reason != null)
            {
                return new FolderCheck(false, false, string.Format(CultureInfo.InvariantCulture, UiText.LocationRejectedFormat, reason));
            }

            string full;
            try
            {
                full = Path.GetFullPath(folder);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is System.Security.SecurityException)
            {
                return new FolderCheck(false, false, string.Format(CultureInfo.InvariantCulture, UiText.LocationRejectedFormat, "it is not a valid folder path"));
            }

            bool hasClient = _fs.DirectoryExists(full) && _fs.FileExists(Path.Combine(full, AcInstallLocator.ClientExeName));
            return new FolderCheck(true, hasClient, hasClient ? UiText.LocationFound : UiText.LocationNotInstalled);
        }

        /// <summary>
        /// The free-space line, from Core's <see cref="AcWriter.PreviewFreeSpace(IDiskSpace, string, long, string, long)"/>:
        /// the downloads (<paramref name="downloadBytes"/>) are staged under <paramref name="stagingDir"/> and the
        /// rest of <paramref name="requiredBytes"/> is written to <paramref name="acFolder"/>. On one volume the
        /// combined need is shown; on two, each volume's own. Advisory only: each step checks again.
        /// </summary>
        public string FreeSpaceText(string stagingDir, string acFolder, long requiredBytes, long downloadBytes)
        {
            try
            {
                long downloads = Math.Max(0, downloadBytes);
                long rest = Math.Max(0, Math.Max(requiredBytes, downloads) - downloads);
                FreeSpacePreview p = _preview(stagingDir, downloads, acFolder, rest);
                if (p.SameVolume)
                {
                    long need = p.CombinedNeeded.Value;
                    string fmt = p.Enough ? UiText.FreeSpaceOkFormat : UiText.FreeSpaceLowFormat;
                    return string.Format(CultureInfo.InvariantCulture, fmt, ByteFormat.Size(p.FirstFree), ByteFormat.Size(need));
                }

                if (p.FirstFree < p.FirstNeeded)
                {
                    return string.Format(CultureInfo.InvariantCulture, UiText.FreeSpaceLowFormat, ByteFormat.Size(p.FirstFree), ByteFormat.Size(p.FirstNeeded));
                }

                if (p.SecondFree < p.SecondNeeded)
                {
                    return string.Format(CultureInfo.InvariantCulture, UiText.FreeSpaceLowFormat, ByteFormat.Size(p.SecondFree), ByteFormat.Size(p.SecondNeeded));
                }

                return string.Format(CultureInfo.InvariantCulture, UiText.FreeSpaceSplitFormat, ByteFormat.Size(p.FirstFree), ByteFormat.Size(p.FirstNeeded), ByteFormat.Size(p.SecondFree), ByteFormat.Size(p.SecondNeeded));
            }
            catch (LauncherException)
            {
                return UiText.FreeSpaceUnknown;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is OverflowException)
            {
                return UiText.FreeSpaceUnknown;
            }
        }
    }

    /// <summary>Formats a <see cref="ProgressReport"/> as "x of y - speed - about t left".</summary>
    internal static class ProgressText
    {
        public static string Describe(ProgressReport report)
        {
            if (report == null)
            {
                return string.Empty;
            }

            var parts = new List<string>();
            if (report.BytesTotal.HasValue && report.BytesTotal.Value > 0)
            {
                parts.Add(string.Format(CultureInfo.InvariantCulture, UiText.ProgressBytesFormat, ByteFormat.Size(report.BytesDone), ByteFormat.Size(report.BytesTotal.Value)));
            }
            else
            {
                parts.Add(ByteFormat.Size(report.BytesDone));
            }

            if (report.BytesPerSecond.HasValue && report.BytesPerSecond.Value > 0)
            {
                parts.Add(string.Format(CultureInfo.InvariantCulture, UiText.ProgressSpeedFormat, ByteFormat.Size((long)report.BytesPerSecond.Value)));
            }

            if (report.Eta.HasValue)
            {
                parts.Add(string.Format(CultureInfo.InvariantCulture, UiText.ProgressEtaFormat, ByteFormat.Duration(report.Eta.Value)));
            }

            return string.Join(" - ", parts);
        }
    }

    /// <summary>
    /// Aggregate progress across every selected component. Each component weighs its download size
    /// (a patch weighs 1); a finished component counts in full, the current one by the fraction of its
    /// latest report.
    /// </summary>
    internal sealed class AggregateProgress
    {
        private readonly Dictionary<string, long> _weights = new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly HashSet<string> _done = new HashSet<string>(StringComparer.Ordinal);
        private readonly long _total;
        private string _current;
        private double _currentFraction;

        public AggregateProgress(IEnumerable<ManifestComponent> components)
        {
            foreach (ManifestComponent c in components)
            {
                long w = c.Kind == ComponentKinds.BytePatch ? 1 : Math.Max(1, c.Size);
                _weights[c.Id] = w;
                _total += w;
            }
        }

        /// <summary>Marks <paramref name="componentId"/> as the one in progress.</summary>
        public void Begin(string componentId)
        {
            _current = componentId;
            _currentFraction = 0;
        }

        /// <summary>Feeds a report for the current component; reports for other steps are ignored.</summary>
        public void Report(ProgressReport report)
        {
            if (report == null || _current == null || report.StepId != _current)
            {
                return;
            }

            // Only the download phase moves the aggregate: the verify pass that follows re-reads the same
            // bytes under the same step id and would otherwise send the bar backwards.
            double? f = report.Fraction;
            if (f.HasValue && report.Message == Core.Downloads.TransferProgress.DownloadingMessage)
            {
                _currentFraction = Math.Max(_currentFraction, f.Value);
            }
        }

        /// <summary>Marks <paramref name="componentId"/> finished (done or skipped).</summary>
        public void Complete(string componentId)
        {
            _done.Add(componentId);
            if (_current == componentId)
            {
                _current = null;
                _currentFraction = 0;
            }
        }

        /// <summary>Overall fraction in [0, 1].</summary>
        public double Fraction
        {
            get
            {
                if (_total <= 0)
                {
                    return 0;
                }

                double sum = 0;
                foreach (string id in _done)
                {
                    long w;
                    if (_weights.TryGetValue(id, out w))
                    {
                        sum += w;
                    }
                }

                long cw;
                if (_current != null && _weights.TryGetValue(_current, out cw))
                {
                    sum += cw * _currentFraction;
                }

                double f = sum / _total;
                return f > 1 ? 1 : (f < 0 ? 0 : f);
            }
        }
    }
}
