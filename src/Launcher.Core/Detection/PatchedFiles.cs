using System;
using System.Collections.Generic;
using System.Linq;
using DreamWeave.Launcher.Core.Manifests;

namespace DreamWeave.Launcher.Core.Detection
{
    /// <summary>
    /// Finds the byte patch that legitimately turns a manifest file entry into a different file on disk.
    /// A patch counts only when it targets the entry's path (case-insensitive, either slash) AND its
    /// <c>inputSha256</c> equals the entry's own sha256, so a patch made for some other build of the
    /// file grants nothing. Derived from the manifest alone; no ids or hashes are hardcoded.
    /// </summary>
    internal static class PatchedFiles
    {
        /// <summary>The output hash of the patch that rewrites <paramref name="entrySha256"/> at <paramref name="path"/>, or null.</summary>
        internal static string OutputFor(IEnumerable<ManifestComponent> components, string path, string entrySha256)
        {
            ManifestComponent patch = Find(components, path, entrySha256);
            return patch == null ? null : patch.OutputSha256;
        }

        /// <summary>The patch component described by <see cref="OutputFor"/>, or null.</summary>
        internal static ManifestComponent Find(IEnumerable<ManifestComponent> components, string path, string entrySha256)
        {
            if (components == null || string.IsNullOrEmpty(entrySha256))
            {
                return null;
            }

            string want = Canonical(path);
            return components.FirstOrDefault(p =>
                p.Kind == ComponentKinds.BytePatch
                && !string.IsNullOrEmpty(p.Target)
                && Hasher.IsSha256Hex(p.OutputSha256)
                && Hasher.FixedTimeEquals(p.InputSha256, entrySha256)
                && string.Equals(Canonical(p.Target), want, StringComparison.OrdinalIgnoreCase));
        }

        private static string Canonical(string path)
        {
            return (path ?? string.Empty).Replace('/', '\\');
        }
    }
}