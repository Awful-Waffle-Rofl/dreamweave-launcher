using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DreamWeave.Launcher.Core.Net;
using DreamWeave.Launcher.Core.State;

namespace DreamWeave.Launcher.Core.Manifests
{
    /// <summary>One place a manifest and its detached signature can be fetched from.</summary>
    public sealed class ManifestSource
    {
        /// <summary>Creates a source.</summary>
        public ManifestSource(string name, Uri manifestUri, Uri signatureUri)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            ManifestUri = manifestUri ?? throw new ArgumentNullException(nameof(manifestUri));
            SignatureUri = signatureUri ?? throw new ArgumentNullException(nameof(signatureUri));
        }

        /// <summary>Source whose signature lives at the manifest URL plus <c>.sig</c>.</summary>
        public static ManifestSource FromManifestUrl(string name, string manifestUrl)
        {
            return new ManifestSource(name, new Uri(manifestUrl, UriKind.Absolute), new Uri(manifestUrl + ".sig", UriKind.Absolute));
        }

        /// <summary>Short name for logs.</summary>
        public string Name { get; }

        /// <summary>Manifest URL.</summary>
        public Uri ManifestUri { get; }

        /// <summary>Signature URL.</summary>
        public Uri SignatureUri { get; }
    }

    /// <summary>The compiled-in manifest locations, tried in order. Transport is untrusted; the signature is the gate.</summary>
    public static class ManifestSources
    {
        /// <summary>Primary: the <c>manifest</c> branch of the launcher repo, served raw by GitHub.</summary>
        public const string PrimaryManifestUrl = "https://raw.githubusercontent.com/Awful-Waffle-Rofl/dreamweave-launcher/manifest/manifest.json";

        /// <summary>Secondary mirror. Placeholder: empty until a mirror exists, and an empty value is skipped.</summary>
        public const string SecondaryManifestUrl = "";

        /// <summary>The production source list (empty entries removed).</summary>
        public static IReadOnlyList<ManifestSource> Default
        {
            get
            {
                var list = new List<ManifestSource> { ManifestSource.FromManifestUrl("primary", PrimaryManifestUrl) };
                if (!string.IsNullOrEmpty(SecondaryManifestUrl))
                {
                    list.Add(ManifestSource.FromManifestUrl("secondary", SecondaryManifestUrl));
                }

                return list;
            }
        }
    }

    /// <summary>What happened when one source was tried.</summary>
    public sealed class ManifestSourceAttempt
    {
        internal ManifestSourceAttempt(string sourceName, LauncherError error, string detail)
        {
            SourceName = sourceName;
            Error = error;
            Detail = detail;
        }

        /// <summary>Source name (or <c>cache</c>).</summary>
        public string SourceName { get; }

        /// <summary><see cref="LauncherError.None"/> on success.</summary>
        public LauncherError Error { get; }

        /// <summary>Plain-English detail.</summary>
        public string Detail { get; }
    }

    /// <summary>Outcome of <see cref="ManifestProvider.LoadAsync"/>.</summary>
    public sealed class ManifestLoadResult
    {
        internal ManifestLoadResult(VerifiedManifest manifest, LauncherError status, bool fromCache, string sourceName, IReadOnlyList<ManifestSourceAttempt> attempts)
        {
            Verified = manifest;
            Status = status;
            FromCache = fromCache;
            SourceName = sourceName;
            Attempts = attempts;
        }

        /// <summary>
        /// The manifest to use, or null when neither a source nor the cache produced one. Always
        /// signature-verified and never a rollback.
        /// </summary>
        public VerifiedManifest Verified { get; }

        /// <summary>Shortcut for <c>Verified?.Manifest</c>.</summary>
        public LauncherManifest Manifest => Verified?.Manifest;

        /// <summary>
        /// <see cref="LauncherError.None"/>: fresh manifest from a source.
        /// <see cref="LauncherError.ManifestUnavailable"/>: no source worked; <see cref="Manifest"/> is the cached copy if one verified, else null.
        /// <see cref="LauncherError.ManifestSchemaUnsupported"/>: a source served an authentic manifest with a newer schema; <see cref="Manifest"/> is the cached copy if any, for launching only.
        /// </summary>
        public LauncherError Status { get; }

        /// <summary>True when <see cref="Manifest"/> came from the local cache.</summary>
        public bool FromCache { get; }

        /// <summary>Name of the source that supplied <see cref="Manifest"/> (<c>cache</c> for the cache), or null.</summary>
        public string SourceName { get; }

        /// <summary>Every source tried, in order, including the cache.</summary>
        public IReadOnlyList<ManifestSourceAttempt> Attempts { get; }

        /// <summary>Whether the caller may download/install from <see cref="Manifest"/> (false on schema-unsupported or no manifest).</summary>
        public bool DownloadsAllowed => Manifest != null && Status != LauncherError.ManifestSchemaUnsupported;

        /// <summary>Whether a manifest is available to launch the game from.</summary>
        public bool LaunchAllowed => Manifest != null;
    }

    /// <summary>
    /// Fetches the manifest from each source in order, verifies it, and caches the verified
    /// pair. Rules:
    /// - every response is size-capped (<see cref="MaxManifestBytes"/>, <see cref="ManifestSignature.MaxFileBytes"/>);
    /// - a source that fails for ANY reason (network, bad signature, rollback, invalid) is skipped;
    /// - the first source that verifies wins: its pair is written to the cache (manifest then
    ///   signature, each atomically) and only then is a higher serial persisted to state;
    /// - an authentic but newer-schema manifest is neither cached nor persisted (so the cached
    ///   schema-1 copy stays launchable), and the result reports ManifestSchemaUnsupported;
    /// - if no source verifies, the cached pair is re-verified against the persisted serial and
    ///   used, with status ManifestUnavailable.
    /// </summary>
    public sealed class ManifestProvider
    {
        /// <summary>Hard cap on a manifest download.</summary>
        public const long MaxManifestBytes = 256 * 1024;

        private readonly IHttpFetcher _fetcher;
        private readonly ManifestVerifier _verifier;
        private readonly AppPaths _paths;
        private readonly StateStore _state;
        private readonly IReadOnlyList<ManifestSource> _sources;

        /// <summary>Creates a provider.</summary>
        /// <param name="fetcher">HTTP transport.</param>
        /// <param name="verifier">Verifier holding the trusted keys.</param>
        /// <param name="paths">Supplies the cache file locations.</param>
        /// <param name="state">Supplies and persists the highest accepted serial.</param>
        /// <param name="sources">Sources to try in order; null means <see cref="ManifestSources.Default"/>.</param>
        public ManifestProvider(IHttpFetcher fetcher, ManifestVerifier verifier, AppPaths paths, StateStore state, IEnumerable<ManifestSource> sources = null)
        {
            _fetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));
            _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
            _paths = paths ?? throw new ArgumentNullException(nameof(paths));
            _state = state ?? throw new ArgumentNullException(nameof(state));
            _sources = (sources ?? ManifestSources.Default).ToList();
        }

        /// <summary>Loads the best available manifest. Never throws for source or cache failures.</summary>
        /// <exception cref="LauncherException">Cancelled, when <paramref name="cancellationToken"/> fires; or a state/cache write failure.</exception>
        public async Task<ManifestLoadResult> LoadAsync(CancellationToken cancellationToken)
        {
            var attempts = new List<ManifestSourceAttempt>();
            bool sawNewerSchema = false;

            foreach (ManifestSource source in _sources)
            {
                ThrowIfCancelled(cancellationToken);
                VerifiedManifest verified;
                try
                {
                    byte[] sig = await _fetcher.GetBytesAsync(source.SignatureUri, ManifestSignature.MaxFileBytes, cancellationToken).ConfigureAwait(false);
                    byte[] manifest = await _fetcher.GetBytesAsync(source.ManifestUri, MaxManifestBytes, cancellationToken).ConfigureAwait(false);
                    verified = _verifier.Verify(manifest, sig, _state.HighestManifestSerial);
                }
                catch (OperationCanceledException ex)
                {
                    throw new LauncherException(LauncherError.Cancelled, "Cancelled.", ex);
                }
                catch (LauncherException ex) when (ex.Code == LauncherError.Cancelled)
                {
                    throw;
                }
                catch (LauncherException ex)
                {
                    if (ex.Code == LauncherError.ManifestSchemaUnsupported)
                    {
                        sawNewerSchema = true;
                    }

                    attempts.Add(new ManifestSourceAttempt(source.Name, ex.Code, ex.Detail));
                    continue;
                }

                attempts.Add(new ManifestSourceAttempt(source.Name, LauncherError.None, "Verified serial " + verified.Serial + "."));
                Accept(verified, attempts);
                return new ManifestLoadResult(verified, LauncherError.None, false, source.Name, attempts.AsReadOnly());
            }

            VerifiedManifest cached = TryLoadCache(attempts);
            LauncherError status = sawNewerSchema ? LauncherError.ManifestSchemaUnsupported : LauncherError.ManifestUnavailable;
            return new ManifestLoadResult(cached, status, cached != null, cached != null ? "cache" : null, attempts.AsReadOnly());
        }

        private static void ThrowIfCancelled(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new LauncherException(LauncherError.Cancelled, "Cancelled.");
            }
        }

        // A verified manifest is used even if caching or persisting fails (disk full, access
        // denied); the failure is recorded as an attempt note so the caller can log it.
        private void Accept(VerifiedManifest verified, List<ManifestSourceAttempt> attempts)
        {
            try
            {
                WriteCacheIfChanged(verified);
            }
            catch (LauncherException ex)
            {
                attempts.Add(new ManifestSourceAttempt("cache-write", ex.Code, ex.Detail));
            }

            long serial = verified.Serial;
            if (serial > _state.HighestManifestSerial)
            {
                try
                {
                    _state.Update(s =>
                    {
                        if (serial > s.HighestManifestSerial)
                        {
                            s.HighestManifestSerial = serial;
                        }
                    });
                }
                catch (LauncherException ex)
                {
                    attempts.Add(new ManifestSourceAttempt("state-write", ex.Code, ex.Detail));
                }
            }
        }

        private void WriteCacheIfChanged(VerifiedManifest verified)
        {
            byte[] manifest = verified.GetManifestBytes();
            byte[] sig = verified.GetSignatureBytes();
            if (SameBytes(_paths.ManifestCacheFile, manifest) && SameBytes(_paths.ManifestSigCacheFile, sig))
            {
                return;
            }

            // A crash between the two writes leaves a mismatched pair, which simply fails
            // verification on the next cache read: safe, never trusted.
            AtomicFile.WriteAllBytes(_paths.ManifestCacheFile, manifest);
            AtomicFile.WriteAllBytes(_paths.ManifestSigCacheFile, sig);
        }

        private VerifiedManifest TryLoadCache(List<ManifestSourceAttempt> attempts)
        {
            try
            {
                if (!File.Exists(_paths.ManifestCacheFile) || !File.Exists(_paths.ManifestSigCacheFile))
                {
                    attempts.Add(new ManifestSourceAttempt("cache", LauncherError.ManifestUnavailable, "No cached download list."));
                    return null;
                }

                byte[] manifest = ReadCapped(_paths.ManifestCacheFile, MaxManifestBytes);
                byte[] sig = ReadCapped(_paths.ManifestSigCacheFile, ManifestSignature.MaxFileBytes);
                VerifiedManifest verified = _verifier.Verify(manifest, sig, _state.HighestManifestSerial);
                attempts.Add(new ManifestSourceAttempt("cache", LauncherError.None, "Using cached serial " + verified.Serial + "."));
                return verified;
            }
            catch (LauncherException ex)
            {
                attempts.Add(new ManifestSourceAttempt("cache", ex.Code, ex.Detail));
                return null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                LauncherException le = LauncherException.FromIo(ex, "reading the cached download list");
                attempts.Add(new ManifestSourceAttempt("cache", le.Code, le.Detail));
                return null;
            }
        }

        private static byte[] ReadCapped(string path, long max)
        {
            var info = new FileInfo(path);
            if (info.Length > max)
            {
                throw new LauncherException(LauncherError.ResponseTooLarge, Path.GetFileName(path) + " is larger than " + max + " bytes.");
            }

            return File.ReadAllBytes(path);
        }

        private static bool SameBytes(string path, byte[] expected)
        {
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length != expected.Length)
                {
                    return false;
                }

                return Hasher.FixedTimeEquals(File.ReadAllBytes(path), expected);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }
    }
}
