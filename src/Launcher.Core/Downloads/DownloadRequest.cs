using System;
using System.IO;
using DreamWeave.Launcher.Core.Manifests;

namespace DreamWeave.Launcher.Core.Downloads
{
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

        /// <summary>Path of the in-progress file: <c>&lt;DestinationPath&gt;.part</c>.</summary>
        public string PartPath => DestinationPath + ".part";

        /// <summary>Path of the HTTP resume sidecar: <c>&lt;DestinationPath&gt;.part.json</c>.</summary>
        public string SidecarPath => PartPath + ".json";

        /// <summary>
        /// Builds a request for a downloadable manifest component, saved as
        /// <paramref name="fileName"/> inside <see cref="AppPaths.StagingDir"/>.
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

            return new DownloadRequest(component.Id, component.Transport, url, component.Size, component.Sha256, Path.Combine(paths.StagingDir, fileName));
        }
    }
}
