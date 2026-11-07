using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DreamWeave.Launcher.Core.Manifests;

namespace DreamWeave.Launcher.Core.Detection
{
    /// <summary>Where one component stands against the files on disk.</summary>
    public enum ComponentState
    {
        /// <summary>Everything the component provides is present and correct (a patch: applied).</summary>
        Satisfied = 0,

        /// <summary>Some files are present but differ from the published version.</summary>
        NeedsRepair = 1,

        /// <summary>Nothing is installed (a patch: applicable but not applied).</summary>
        Missing = 2,

        /// <summary>The component does not apply to the current files (a patch whose target is absent or in an unknown state).</summary>
        NotApplicable = 3,

        /// <summary>The state could not be determined: an unknown component kind, or files that could not be read.</summary>
        Unknown = 4,
    }

    /// <summary>State of one file within a component.</summary>
    public enum FileState
    {
        /// <summary>Present and matches.</summary>
        Ok = 0,

        /// <summary>Present but different from the published version.</summary>
        Mismatch = 1,

        /// <summary>Not on disk.</summary>
        Missing = 2,

        /// <summary>In use by another program; could not be read.</summary>
        Locked = 3,

        /// <summary>Windows denied access.</summary>
        AccessDenied = 4,

        /// <summary>Some other read failure.</summary>
        Error = 5,
    }

    /// <summary>Per-file detail for a <see cref="ComponentStatus"/>.</summary>
    public sealed class FileDetail
    {
        /// <summary>Creates a detail row.</summary>
        public FileDetail(string path, FileState state, long? expectedSize, long? actualSize, string actualSha256, string note)
        {
            Path = path;
            State = state;
            ExpectedSize = expectedSize;
            ActualSize = actualSize;
            ActualSha256 = actualSha256;
            Note = note;
        }

        /// <summary>Manifest-relative path.</summary>
        public string Path { get; }

        /// <summary>Outcome for this file.</summary>
        public FileState State { get; }

        /// <summary>Size the manifest expects, when it states one.</summary>
        public long? ExpectedSize { get; }

        /// <summary>Size found on disk, when known.</summary>
        public long? ActualSize { get; }

        /// <summary>Hash found on disk, when it was computed.</summary>
        public string ActualSha256 { get; }

        /// <summary>Short plain-English note, or null.</summary>
        public string Note { get; }
    }

    /// <summary>Evaluation of one manifest component.</summary>
    public sealed class ComponentStatus
    {
        /// <summary>Creates a status.</summary>
        public ComponentStatus(string id, string kind, ComponentState state, IReadOnlyList<FileDetail> files, long bytesToDownload, string message)
        {
            Id = id;
            Kind = kind;
            State = state;
            Files = files ?? new FileDetail[0];
            BytesToDownload = bytesToDownload;
            Message = message;
        }

        /// <summary>Component id.</summary>
        public string Id { get; }

        /// <summary>Component kind.</summary>
        public string Kind { get; }

        /// <summary>Overall state.</summary>
        public ComponentState State { get; }

        /// <summary>Per-file detail, in manifest order.</summary>
        public IReadOnlyList<FileDetail> Files { get; }

        /// <summary>
        /// Size of the archive or installer that would have to be downloaded to fix this component
        /// (0 when it is satisfied, not applicable, or a patch). An upper bound: the download
        /// engine may be able to fetch less.
        /// </summary>
        public long BytesToDownload { get; }

        /// <summary>One plain-English line for the player.</summary>
        public string Message { get; }
    }

    /// <summary>
    /// Compares each manifest component with the files in the game folder. Pure over
    /// <see cref="FileVerifier"/>; never writes into the game folder.
    /// </summary>
    public sealed class ComponentEvaluator
    {
        private readonly FileVerifier _verifier;

        /// <summary>Creates an evaluator.</summary>
        public ComponentEvaluator(FileVerifier verifier)
        {
            _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        }

        /// <summary>Evaluates every component, in manifest order, sharing one hash per file across components.</summary>
        /// <param name="manifest">The verified manifest.</param>
        /// <param name="acFolder">The game folder, or null when none was found.</param>
        /// <exception cref="LauncherException">ManifestInvalid when a manifest path is unsafe.</exception>
        public IReadOnlyList<ComponentStatus> EvaluateAll(LauncherManifest manifest, string acFolder, IProgress<ProgressReport> progress = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (manifest == null)
            {
                throw new ArgumentNullException(nameof(manifest));
            }

            var session = new Session(_verifier, manifest, acFolder, progress, cancellationToken);
            return manifest.Components.Select(c => session.Evaluate(c)).ToList();
        }

        /// <summary>Evaluates one component of <paramref name="manifest"/>.</summary>
        public ComponentStatus Evaluate(ManifestComponent component, LauncherManifest manifest, string acFolder, IProgress<ProgressReport> progress = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (component == null)
            {
                throw new ArgumentNullException(nameof(component));
            }

            if (manifest == null)
            {
                throw new ArgumentNullException(nameof(manifest));
            }

            return new Session(_verifier, manifest, acFolder, progress, cancellationToken).Evaluate(component);
        }

        private sealed class Session
        {
            private readonly FileVerifier _verifier;
            private readonly LauncherManifest _manifest;
            private readonly string _folder;
            private readonly IProgress<ProgressReport> _progress;
            private readonly CancellationToken _ct;
            private readonly Dictionary<string, FileCheckResult> _memo = new Dictionary<string, FileCheckResult>(StringComparer.OrdinalIgnoreCase);

            public Session(FileVerifier verifier, LauncherManifest manifest, string folder, IProgress<ProgressReport> progress, CancellationToken ct)
            {
                _verifier = verifier;
                _manifest = manifest;
                _folder = folder;
                _progress = progress;
                _ct = ct;
            }

            public ComponentStatus Evaluate(ManifestComponent c)
            {
                switch (c.Kind)
                {
                    case ComponentKinds.Installer:
                        return Installer(c);
                    case ComponentKinds.ZipOverlay:
                        return ZipOverlay(c);
                    case ComponentKinds.BytePatch:
                        return BytePatch(c);
                    default:
                        return new ComponentStatus(c.Id, c.Kind, ComponentState.Unknown, null, 0, "This launcher does not know how to check this kind of component.");
                }
            }

            private ComponentStatus Installer(ManifestComponent c)
            {
                if (_folder == null)
                {
                    return new ComponentStatus(c.Id, c.Kind, ComponentState.Missing, null, c.Size, "No Asheron's Call install was found.");
                }

                string exe = AcPathSafety.Resolve(_folder, AcInstallLocator.ClientExeName);
                if (!_verifier.FileSystem.FileExists(exe))
                {
                    return new ComponentStatus(c.Id, c.Kind, ComponentState.Missing, null, c.Size, "The game folder has no " + AcInstallLocator.ClientExeName + ".");
                }

                var details = new List<FileDetail>();
                foreach (SatisfiedWhenRule rule in c.SatisfiedWhen)
                {
                    FileCheckResult r = Check(rule.Path, null);
                    var accepted = (rule.Sha256 ?? new List<string>()).Select(h => (h ?? string.Empty).ToUpperInvariant()).ToList();
                    details.Add(Detail(rule.Path, r, null, h => accepted.Contains(h), null));
                }

                ComponentState state;
                if (details.Any(d => d.State == FileState.Missing))
                {
                    state = ComponentState.Missing;
                }
                else if (details.Any(d => d.State == FileState.Mismatch))
                {
                    state = ComponentState.NeedsRepair;
                }
                else if (details.Any(IsUnreadable))
                {
                    state = ComponentState.Unknown;
                }
                else
                {
                    state = ComponentState.Satisfied;
                }

                return Finish(c, state, details, c.Size, "client files");
            }

            private ComponentStatus ZipOverlay(ManifestComponent c)
            {
                if (_folder == null)
                {
                    return new ComponentStatus(c.Id, c.Kind, ComponentState.Missing, null, c.Size, "No Asheron's Call install was found.");
                }

                var details = new List<FileDetail>();
                foreach (ManifestFile f in c.Files)
                {
                    string expected = (f.Sha256 ?? string.Empty).ToUpperInvariant();
                    ManifestComponent patch = PatchTargeting(f.Path);

                    // A patch target may legitimately carry the patch's output hash, so it must be hashed
                    // even when its own size is odd; any other file with the wrong size is not read at all.
                    FileCheckResult r = Check(f.Path, patch == null ? (long?)f.Size : null);
                    string output = patch == null ? null : (patch.OutputSha256 ?? string.Empty).ToUpperInvariant();
                    string note = null;
                    FileDetail d = Detail(f.Path, r, f.Size, h =>
                    {
                        if (h == expected)
                        {
                            return true;
                        }

                        if (output != null && h == output)
                        {
                            note = "Patched by " + patch.Id + ".";
                            return true;
                        }

                        return false;
                    }, null);
                    details.Add(note == null || d.State != FileState.Ok ? d : new FileDetail(d.Path, d.State, d.ExpectedSize, d.ActualSize, d.ActualSha256, note));
                }

                ComponentState state;
                bool anyProblem = details.Any(d => d.State == FileState.Missing || d.State == FileState.Mismatch);
                if (!anyProblem)
                {
                    state = details.Any(IsUnreadable) ? ComponentState.Unknown : ComponentState.Satisfied;
                }
                else if (details.All(d => d.State == FileState.Missing))
                {
                    state = ComponentState.Missing;
                }
                else
                {
                    state = ComponentState.NeedsRepair;
                }

                return Finish(c, state, details, c.Size, "game data files");
            }

            private ComponentStatus BytePatch(ManifestComponent c)
            {
                if (_folder == null)
                {
                    return new ComponentStatus(c.Id, c.Kind, ComponentState.NotApplicable, null, 0, "No Asheron's Call install was found.");
                }

                FileCheckResult r = Check(c.Target, null);
                string input = (c.InputSha256 ?? string.Empty).ToUpperInvariant();
                string output = (c.OutputSha256 ?? string.Empty).ToUpperInvariant();
                FileDetail detail;
                ComponentState state;
                string message;

                switch (r.Status)
                {
                    case FileCheckStatus.Hashed:
                        if (r.Sha256 == output)
                        {
                            state = ComponentState.Satisfied;
                            message = "Applied.";
                            detail = new FileDetail(c.Target, FileState.Ok, null, r.Size, r.Sha256, "Already patched.");
                        }
                        else if (r.Sha256 == input)
                        {
                            state = ComponentState.Missing;
                            message = "Not applied.";
                            detail = new FileDetail(c.Target, FileState.Ok, null, r.Size, r.Sha256, "Unpatched; the patch can be applied.");
                        }
                        else
                        {
                            state = ComponentState.NotApplicable;
                            message = "Not applicable to the current " + c.Target + ".";
                            detail = new FileDetail(c.Target, FileState.Mismatch, null, r.Size, r.Sha256, "Not the file this patch was made for.");
                        }

                        break;
                    case FileCheckStatus.Missing:
                        state = ComponentState.NotApplicable;
                        message = c.Target + " is not installed.";
                        detail = new FileDetail(c.Target, FileState.Missing, null, null, null, r.Message);
                        break;
                    default:
                        state = ComponentState.Unknown;
                        message = c.Target + " could not be read.";
                        detail = Detail(c.Target, r, null, h => false, null);
                        break;
                }

                return new ComponentStatus(c.Id, c.Kind, state, new[] { detail }, 0, message);
            }

            private FileCheckResult Check(string relativePath, long? expectedSize)
            {
                string full = AcPathSafety.Resolve(_folder, relativePath);
                string key = full + "|" + (expectedSize.HasValue ? expectedSize.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "-");
                FileCheckResult hit;
                if (_memo.TryGetValue(key, out hit))
                {
                    return hit;
                }

                // A size-gated check that came back Hashed is reusable for the ungated one and vice versa,
                // so only the exact key is memoised; the verifier's own cache covers the rest.
                FileCheckResult r = _verifier.Verify(full, expectedSize, _progress, _ct);
                _memo[key] = r;
                return r;
            }

            private ManifestComponent PatchTargeting(string path)
            {
                string want = Canonical(path);
                return _manifest.Components.FirstOrDefault(p =>
                    p.Kind == ComponentKinds.BytePatch
                    && !string.IsNullOrEmpty(p.Target)
                    && !string.IsNullOrEmpty(p.OutputSha256)
                    && string.Equals(Canonical(p.Target), want, StringComparison.OrdinalIgnoreCase));
            }

            private static string Canonical(string path)
            {
                return (path ?? string.Empty).Replace('/', '\\');
            }

            private static bool IsUnreadable(FileDetail d)
            {
                return d.State == FileState.Locked || d.State == FileState.AccessDenied || d.State == FileState.Error;
            }

            private static FileDetail Detail(string path, FileCheckResult r, long? expectedSize, Func<string, bool> accepts, string note)
            {
                switch (r.Status)
                {
                    case FileCheckStatus.Hashed:
                        return accepts(r.Sha256)
                            ? new FileDetail(path, FileState.Ok, expectedSize, r.Size, r.Sha256, note)
                            : new FileDetail(path, FileState.Mismatch, expectedSize, r.Size, r.Sha256, "Differs from the published version.");
                    case FileCheckStatus.SizeMismatch:
                        return new FileDetail(path, FileState.Mismatch, expectedSize, r.Size, null, r.Message);
                    case FileCheckStatus.Missing:
                        return new FileDetail(path, FileState.Missing, expectedSize, null, null, r.Message);
                    case FileCheckStatus.Locked:
                        return new FileDetail(path, FileState.Locked, expectedSize, null, null, r.Message);
                    case FileCheckStatus.AccessDenied:
                        return new FileDetail(path, FileState.AccessDenied, expectedSize, null, null, r.Message);
                    default:
                        return new FileDetail(path, FileState.Error, expectedSize, null, null, r.Message);
                }
            }

            private static ComponentStatus Finish(ManifestComponent c, ComponentState state, List<FileDetail> details, long downloadBytes, string what)
            {
                int bad = details.Count(d => d.State == FileState.Missing || d.State == FileState.Mismatch);
                int unreadable = details.Count(IsUnreadable);
                string message;
                switch (state)
                {
                    case ComponentState.Satisfied:
                        message = "Up to date.";
                        break;
                    case ComponentState.NeedsRepair:
                        message = bad + " of " + details.Count + " " + what + " differ from the published version. "
                            + "The game can change its data files while it runs, so this is not necessarily damage; repairing is your choice.";
                        break;
                    case ComponentState.Missing:
                        message = "Not installed.";
                        break;
                    default:
                        message = unreadable + " of " + details.Count + " " + what + " could not be read (in use or access denied), so this could not be checked.";
                        break;
                }

                bool needsDownload = state == ComponentState.Missing || state == ComponentState.NeedsRepair;
                return new ComponentStatus(c.Id, c.Kind, state, details, needsDownload ? downloadBytes : 0, message);
            }
        }
    }
}
