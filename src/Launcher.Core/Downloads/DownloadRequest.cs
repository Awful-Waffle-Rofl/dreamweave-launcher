using System;
using System.Collections.Generic;
using System.IO;
using DreamWeave.Launcher.Core.Manifests;

namespace DreamWeave.Launcher.Core.Downloads
{
    /// <summary>One place the bytes can be fetched from.</summary>
    public sealed class DownloadSource
    {
        /// <summary>Creates a source.</summary>
        public DownloadSource(string transport, Uri url, string label)
        {
            Transport = transport;
            Url = url;
            Label = label;
        }

        /// <summary>A <see cref="Transports"/> value.</summary>
        public string Transport { get; }

        /// <summary>Absolute source URL.</summary>
        public Uri Url { get; }

        /// <summary>Player-facing description, or null.</summary>
        public string Label { get; }
    }

    /// <summary>Which source a download is about to try (see <see cref="DownloadRequest.OnSourceStarted"/>).</summary>
    public sealed class SourceAttempt
    {
        /// <summary>Creates a notice.</summary>
        public SourceAttempt(int index, DownloadSource source, LauncherException previousFailure)
        {
            Index = index;
            Source = source ?? throw new ArgumentNullException(nameof(source));
            PreviousFailure = previousFailure;
        }

        /// <summary>0 for the primary source, 1.. for each alternate in manifest order.</summary>
        public int Index { get; }

        /// <summary>The source about to be tried.</summary>
        public DownloadSource Source { get; }

        /// <summary>Why the previous source was given up (null for the primary).</summary>
        public LauncherException PreviousFailure { get; }

        /// <summary>True for an alternate (a fallback), false for the primary.</summary>
        public bool IsFallback => Index > 0;
    }

    /// <summary>
    /// One file to download and verify. Every field comes from the signed manifest except
    /// <see cref="DestinationPath"/>, which the caller chooses under <see cref="AppPaths.StagingDir"/>.
    /// </summary>
    public sealed class DownloadRequest
    {
        /// <summary>Creates a request.</summary>
        /// <param name="stepId">Progress step id (normally the manifest component id).</param>
        /// <param name="transport">A <see cref="Transports"/> value.</param>
        /// <param name="url">Absolute source URL (an http(s) URL, or a mega.nz public file link).</param>
        /// <param name="expectedSize">Exact file length in bytes (at least 1).</param>
        /// <param name="expectedSha256">Exact SHA-256 as 64 hex characters.</param>
        /// <param name="destinationPath">Final file path; must be inside the staging folder.</param>
        public DownloadRequest(string stepId, string transport, Uri url, long expectedSize, string expectedSha256, string destinationPath)
        {
            StepId = stepId ?? string.Empty;
            Transport = transport;
            Url = url;
            ExpectedSize = expectedSize;
            ExpectedSha256 = expectedSha256;
            DestinationPath = destinationPath;
        }

        /// <summary>
        /// Fallback sources tried in order after this request's own transport/URL fails in a way another
        /// source could fix. Each must yield the same <see cref="ExpectedSize"/> and <see cref="ExpectedSha256"/>.
        /// </summary>
        public IReadOnlyList<DownloadSource> Alternates { get; set; } = new DownloadSource[0];

        /// <summary>Called with the player-facing label of an alternate source just before it is tried (may run on a pool thread).</summary>
        public Action<string> OnFallback { get; set; }

        /// <summary>
        /// Called just before EVERY source is tried, the primary included, with which source it is and, for
        /// an alternate, why the one before it failed (may run on a pool thread).
        /// </summary>
        public Action<SourceAttempt> OnSourceStarted { get; set; }

        /// <summary>Player-facing description of the primary source (the manifest component's sourceLabel), or null.</summary>
        public string SourceLabel { get; set; }

        /// <summary>
        /// Distinguishes this source's in-progress files from every other source's (empty for the primary,
        /// <c>.s1</c>, <c>.s2</c> ... for mirrors), so a read of one source that is still running can never
        /// hold the file another source writes.
        /// </summary>
        public string PartSuffix { get; set; } = string.Empty;

        /// <summary>True while at least one more source remains after this one; a transport may then fail fast instead of retrying a corrupt download.</summary>
        public bool FallbackAvailable { get; set; }

        /// <summary>
        /// The same file fetched from one specific source: same size, hash and destination, no alternates,
        /// and its own part file (<paramref name="index"/> 0 is the primary).
        /// </summary>
        public DownloadRequest ForSource(DownloadSource source, int index = 1, bool fallbackAvailable = false)
        {
            return new DownloadRequest(StepId, source.Transport, source.Url, ExpectedSize, ExpectedSha256, DestinationPath)
            {
                PartSuffix = index <= 0 ? string.Empty : ".s" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                FallbackAvailable = fallbackAvailable,
            };
        }

        /// <summary>Progress step id stamped on every report for this download.</summary>
        public string StepId { get; }

        /// <summary>The transport to use (a <see cref="Transports"/> value).</summary>
        public string Transport { get; }

        /// <summary>Source URL.</summary>
        public Uri Url { get; }

        /// <summary>Exact expected length in bytes.</summary>
        public long ExpectedSize { get; }

        /// <summary>Exact expected SHA-256 (64 hex characters).</summary>
        public string ExpectedSha256 { get; }

        /// <summary>Final path of the verified file.</summary>
        public string DestinationPath { get; }

        /// <summary>Path of the in-progress file: <c>&lt;DestinationPath&gt;&lt;PartSuffix&gt;.part</c>.</summary>
        public string PartPath => DestinationPath + PartSuffix + ".part";

        /// <summary>Path of the HTTP resume sidecar: <c>&lt;DestinationPath&gt;.part.json</c>.</summary>
        public string SidecarPath => PartPath + ".json";

        /// <summary>
        /// Builds a request for a downloadable manifest component, saved as
        /// <paramref name="fileName"/> in that component's own folder,
        /// <c>StagingDir\&lt;component id&gt;\</c> (see <see cref="ComponentFolder"/>).
        /// </summary>
        public static DownloadRequest FromComponent(ManifestComponent component, AppPaths paths, string fileName)
        {
            if (component == null)
            {
                throw new ArgumentNullException(nameof(component));
            }

            if (paths == null)
            {
                throw new ArgumentNullException(nameof(paths));
            }

            if (string.IsNullOrEmpty(fileName) || fileName != Path.GetFileName(fileName))
            {
                throw new ArgumentException("A bare file name is required.", nameof(fileName));
            }

            Uri url;
            if (!Uri.TryCreate(component.Url, UriKind.Absolute, out url))
            {
                throw new LauncherException(LauncherError.ManifestInvalid, "The download address for '" + component.Id + "' is not valid.");
            }

            var request = new DownloadRequest(component.Id, component.Transport, url, component.Size, component.Sha256, Path.Combine(ComponentFolder(paths.StagingDir, component.Id), fileName));
            var alternates = new List<DownloadSource>();
            foreach (ManifestMirror m in component.Mirrors ?? new List<ManifestMirror>())
            {
                Uri mirrorUrl;
                if (m != null && Uri.TryCreate(m.Url, UriKind.Absolute, out mirrorUrl))
                {
                    alternates.Add(new DownloadSource(m.Transport, mirrorUrl, m.SourceLabel));
                }
            }

            request.Alternates = alternates;
            request.SourceLabel = component.SourceLabel;
            return request;
        }

        /// <summary>
        /// The folder a component's download (and its <c>.part</c> and sidecar) lives in:
        /// <c>&lt;stagingDir&gt;\&lt;componentId&gt;</c>. One folder per component means nothing another
        /// component downloaded (for example a DLL) can ever sit beside an installer.
        /// </summary>
        /// <exception cref="LauncherException"><see cref="LauncherError.ManifestInvalid"/> when the id is not a safe folder name.</exception>
        public static string ComponentFolder(string stagingDir, string componentId)
        {
            if (!IsSafeFolderName(componentId))
            {
                throw new LauncherException(LauncherError.ManifestInvalid, "The component id '" + componentId + "' cannot be used as a folder name.");
            }

            return Path.Combine(stagingDir, componentId);
        }

        /// <summary>True for a non-empty single path segment with no invalid characters, not <c>.</c> or <c>..</c>.</summary>
        internal static bool IsSafeFolderName(string name)
        {
            return !string.IsNullOrWhiteSpace(name)
                && name != "." && name != ".."
                && name.Trim() == name
                && !name.EndsWith(".", StringComparison.Ordinal)
                && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
                && name.IndexOf(Path.DirectorySeparatorChar) < 0
                && name.IndexOf(Path.AltDirectorySeparatorChar) < 0;
        }
    }
}
