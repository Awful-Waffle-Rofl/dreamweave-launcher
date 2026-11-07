using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DreamWeave.Launcher.Core.Net;
using DreamWeave.Launcher.Core.State;
using Newtonsoft.Json.Linq;

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

        /// <summary>Source name, <c>&lt;name&gt; (refetch)</c> for a cache-busting retry, <c>cache</c>, or <c>cache-write</c>/<c>state-write</c> for a persistence note.</summary>
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
        /// signature-verified and never below the rollback floor.
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

        /// <summary>Every source tried, in order, including retries, the cache, and persistence notes.</summary>
        public IReadOnlyList<ManifestSourceAttempt> Attempts { get; }

        /// <summary>Whether the caller may download/install from <see cref="Manifest"/> (false on schema-unsupported or no manifest).</summary>
        public bool DownloadsAllowed => Manifest != null && Status != LauncherError.ManifestSchemaUnsupported;

        /// <summary>Whether a manifest is available to launch the game from.</summary>
        public bool LaunchAllowed => Manifest != null;
    }

    /// <summary>
    /// Fetches the manifest from each source in order, verifies it, and caches the verified pair.
    /// <list type="bullet">
    /// <item>Rollback floor = max(state's highest serial, the cached pair's serial if the cache
    /// verifies, the verifier's compiled-in floor). It is computed once per load and used for
    /// every source and for the cache fallback, so losing the state file alone cannot reopen a
    /// rollback while a valid cache exists.</item>
    /// <item>Every response is size-capped (<see cref="MaxManifestBytes"/>, <see cref="ManifestSignature.MaxFileBytes"/>).</item>
    /// <item>A source whose pair fails the signature check is refetched ONCE with a cache-busting
    /// query (a CDN can briefly serve a new manifest with the old signature); any other failure,
    /// including any unexpected exception, moves on to the next source.</item>
    /// <item>The first source that verifies wins. Its pair is written to <see cref="AppPaths.ManifestCacheFile"/>
    /// as ONE atomic file; only if that succeeds is a higher serial persisted to state, so the
    /// cache and the floor never disagree in a way that would strand the offline fallback.</item>
    /// <item>An authentic newer-schema manifest is neither cached nor persisted, so the cached
    /// schema-1 copy stays launchable; the result reports ManifestSchemaUnsupported.</item>
    /// <item>If no source verifies, the cached pair (re-verified against the floor) is used, with
    /// status ManifestUnavailable.</item>
    /// </list>
    /// </summary>
    public sealed class ManifestProvider
    {
        /// <summary>Hard cap on a manifest download.</summary>
        public const long MaxManifestBytes = 256 * 1024;

        /// <summary>Hard cap on the cache file (base64 of both files plus JSON framing).</summary>
        internal const long MaxCacheFileBytes = 512 * 1024;

        private const string CacheFormat = "dreamweave-manifest-cache-1";

        private readonly IHttpFetcher _fetcher;
        private readonly ManifestVerifier _verifier;
        private readonly AppPaths _paths;
        private readonly StateStore _state;
        private readonly IReadOnlyList<ManifestSource> _sources;

        /// <summary>Creates a provider.</summary>
        /// <param name="fetcher">HTTP transport.</param>
        /// <param name="verifier">Verifier holding the trusted keys and the compiled-in serial floor.</param>
        /// <param name="paths">Supplies the cache file location.</param>
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

        /// <summary>
        /// Loads the best available manifest. Source, cache and persistence failures never throw;
        /// they are reported through <see cref="ManifestLoadResult.Status"/> and
        /// <see cref="ManifestLoadResult.Attempts"/>.
        /// </summary>
        /// <exception cref="LauncherException">Only <see cref="LauncherError.Cancelled"/>, when <paramref name="cancellationToken"/> fires.</exception>
        public async Task<ManifestLoadResult> LoadAsync(CancellationToken cancellationToken)
        {
            var attempts = new List<ManifestSourceAttempt>();
            bool sawNewerSchema = false;

            // Floor: state, the cache's own serial (signature-checked only), the compiled floor.
            VerifiedManifest cacheAtStart = TryReadCache(0, null);
            long floor = Math.Max(_state.HighestManifestSerial, cacheAtStart == null ? 0 : cacheAtStart.Serial);
            floor = Math.Max(floor, _verifier.MinimumSerial);

            foreach (ManifestSource source in _sources)
            {
                VerifiedManifest verified = null;
                for (int pass = 0; pass < 2 && verified == null; pass++)
                {
                    ThrowIfCancelled(cancellationToken);
                    string name = pass == 0 ? source.Name : source.Name + " (refetch)";
                    try
                    {
                        Uri sigUri = pass == 0 ? source.SignatureUri : CacheBust(source.SignatureUri);
                        Uri manifestUri = pass == 0 ? source.ManifestUri : CacheBust(source.ManifestUri);
                        byte[] sig = await _fetcher.GetBytesAsync(sigUri, ManifestSignature.MaxFileBytes, cancellationToken).ConfigureAwait(false);
                        byte[] manifest = await _fetcher.GetBytesAsync(manifestUri, MaxManifestBytes, cancellationToken).ConfigureAwait(false);
                        verified = _verifier.Verify(manifest, sig, floor);
                        attempts.Add(new ManifestSourceAttempt(name, LauncherError.None, "Verified serial " + verified.Serial + "."));
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
                        attempts.Add(new ManifestSourceAttempt(name, ex.Code, ex.Detail));
                        if (ex.Code == LauncherError.ManifestSchemaUnsupported)
                        {
                            sawNewerSchema = true;
                        }

                        if (ex.Code != LauncherError.ManifestSignatureInvalid)
                        {
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        // Untrusted input must never crash the load: classify and move on.
                        attempts.Add(new ManifestSourceAttempt(name, LauncherError.ManifestInvalid, "Unexpected error reading the download list: " + ex.Message));
                        break;
                    }
                }

                if (verified != null)
                {
                    Accept(verified, attempts);
                    return new ManifestLoadResult(verified, LauncherError.None, false, source.Name, attempts.AsReadOnly());
                }
            }

            VerifiedManifest cached = TryReadCache(floor, attempts);
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

        private static Uri CacheBust(Uri uri)
        {
            var b = new UriBuilder(uri);
            string token = "cb=" + Guid.NewGuid().ToString("N");
            b.Query = string.IsNullOrEmpty(b.Query) || b.Query == "?" ? token : b.Query.TrimStart('?') + "&" + token;
            return b.Uri;
        }

        // A verified manifest is used even if caching or persisting fails (disk full, access
        // denied); each failure is recorded as an attempt note so the caller can log it.
        private void Accept(VerifiedManifest verified, List<ManifestSourceAttempt> attempts)
        {
            try
            {
                WriteCache(verified);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                // Do NOT advance the persisted floor past what the cache holds, or the offline
                // fallback would reject its own cached copy as a rollback.
                LauncherException le = ex as LauncherException ?? LauncherException.FromIo(ex, "caching the download list");
                attempts.Add(new ManifestSourceAttempt("cache-write", le.Code, le.Detail));
                return;
            }

            long serial = verified.Serial;
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
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                LauncherException le = ex as LauncherException ?? LauncherException.FromIo(ex, "saving launcher state");
                attempts.Add(new ManifestSourceAttempt("state-write", le.Code, le.Detail));
            }
        }

        private void WriteCache(VerifiedManifest verified)
        {
            var obj = new JObject
            {
                ["format"] = CacheFormat,
                ["manifestB64"] = Convert.ToBase64String(verified.GetManifestBytes()),
                ["sigB64"] = Convert.ToBase64String(verified.GetSignatureBytes()),
            };
            byte[] bytes = new UTF8Encoding(false).GetBytes(obj.ToString(Newtonsoft.Json.Formatting.None) + "\n");
            if (!SameBytes(_paths.ManifestCacheFile, bytes))
            {
                AtomicFile.WriteAllBytes(_paths.ManifestCacheFile, bytes);
            }

            // The pre-release two-file cache is never read; remove it once the new file exists.
            TryDelete(_paths.LegacyManifestCacheFile);
            TryDelete(_paths.LegacyManifestSigCacheFile);
        }

        /// <summary>Reads and verifies the cache pair against <paramref name="floor"/>; null on any problem (noted in <paramref name="attempts"/> when given).</summary>
        private VerifiedManifest TryReadCache(long floor, List<ManifestSourceAttempt> attempts)
        {
            try
            {
                if (!File.Exists(_paths.ManifestCacheFile))
                {
                    attempts?.Add(new ManifestSourceAttempt("cache", LauncherError.ManifestUnavailable, "No cached download list."));
                    return null;
                }

                if (new FileInfo(_paths.ManifestCacheFile).Length > MaxCacheFileBytes)
                {
                    throw new LauncherException(LauncherError.ManifestInvalid, "The cached download list is too large.");
                }

                JObject obj = ManifestParser.ReadRoot(File.ReadAllBytes(_paths.ManifestCacheFile));
                if ((string)obj["format"] != CacheFormat)
                {
                    throw new LauncherException(LauncherError.ManifestInvalid, "The cached download list has an unknown format.");
                }

                byte[] manifest = Convert.FromBase64String((string)obj["manifestB64"] ?? string.Empty);
                byte[] sig = Convert.FromBase64String((string)obj["sigB64"] ?? string.Empty);
                VerifiedManifest verified = _verifier.Verify(manifest, sig, floor);
                attempts?.Add(new ManifestSourceAttempt("cache", LauncherError.None, "Using cached serial " + verified.Serial + "."));
                return verified;
            }
            catch (LauncherException ex)
            {
                attempts?.Add(new ManifestSourceAttempt("cache", ex.Code, ex.Detail));
                return null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                LauncherException le = LauncherException.FromIo(ex, "reading the cached download list");
                attempts?.Add(new ManifestSourceAttempt("cache", le.Code, le.Detail));
                return null;
            }
            catch (Exception ex)
            {
                attempts?.Add(new ManifestSourceAttempt("cache", LauncherError.ManifestInvalid, "The cached download list is unreadable: " + ex.Message));
                return null;
            }
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

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
