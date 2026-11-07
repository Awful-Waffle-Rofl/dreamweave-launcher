using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;

namespace DreamWeave.Launcher.Core.Manifests
{
    /// <summary>
    /// Schema-1 launcher manifest: what to download, how to verify it, how to launch, and the
    /// self-update pointer. Only ever produced by <see cref="ManifestVerifier"/> from bytes whose
    /// signature has already verified. Unknown JSON members are ignored, so new optional fields
    /// can be added without a schema bump; bump <see cref="Schema"/> only for a change an older
    /// launcher must NOT act on (it then blocks downloads and launches from its cache).
    /// </summary>
    /// <remarks>
    /// Compatibility contract (see Docs/manifest-compat.md): <c>manifest.json</c> stays schema 1
    /// and changes only additively for the life of every v1 launcher, because a v1 launcher that
    /// sees any other schema can no longer read the self-update pointer. A breaking schema ships
    /// as a NEW file (<c>manifest.v2.json</c> plus its <c>.sig</c>) that newer launchers try first,
    /// falling back to <c>manifest.json</c>; the schema-1 file keeps pointing old launchers at a
    /// release that understands v2.
    /// </remarks>
    [JsonObject(MemberSerialization.OptIn)]
    public sealed class LauncherManifest
    {
        /// <summary>The only schema this launcher understands.</summary>
        public const int SupportedSchema = 1;

        /// <summary>Schema version; must equal <see cref="SupportedSchema"/>.</summary>
        [JsonProperty("schema")]
        public int Schema { get; set; }

        /// <summary>Monotonic release number (at least 1). A lower serial than one already accepted is a rollback.</summary>
        [JsonProperty("serial")]
        public long Serial { get; set; }

        /// <summary>Issue time as an ISO-8601 UTC string (informational). See <see cref="IssuedUtc"/>.</summary>
        [JsonProperty("issued")]
        public string Issued { get; set; }

        /// <summary>The game server to connect to.</summary>
        [JsonProperty("server")]
        public ServerInfo Server { get; set; }

        /// <summary>How to build the acclient.exe command line.</summary>
        [JsonProperty("launch")]
        public LaunchInfo Launch { get; set; }

        /// <summary>Self-update pointer for the launcher exe.</summary>
        [JsonProperty("launcher")]
        public LauncherUpdateInfo Launcher { get; set; }

        /// <summary>Free disk space (bytes) required on the install drive before downloading.</summary>
        [JsonProperty("requiredFreeBytes")]
        public long RequiredFreeBytes { get; set; }

        /// <summary>
        /// Installable components, in manifest order. After parsing this holds only components
        /// whose <see cref="ManifestComponent.Kind"/> is in <see cref="ComponentKinds.Known"/>;
        /// unknown kinds are dropped and recorded in <see cref="Warnings"/>.
        /// </summary>
        [JsonProperty("components")]
        public List<ManifestComponent> Components { get; set; } = new List<ManifestComponent>();

        /// <summary>Telemetry settings.</summary>
        [JsonProperty("telemetry")]
        public TelemetryInfo Telemetry { get; set; }

        /// <summary>Links shown in the UI.</summary>
        [JsonProperty("links")]
        public LinksInfo Links { get; set; }

        /// <summary>Optional plain-text notice to show the player (maintenance, news), or null.</summary>
        [JsonProperty("notice")]
        public string Notice { get; set; }

        /// <summary>Non-fatal problems found while parsing (for the launcher log). Never null.</summary>
        public IReadOnlyList<string> Warnings { get; internal set; } = new string[0];

        /// <summary>
        /// Ids of components marked <c>required</c> that were skipped because their kind is unknown
        /// to this launcher. Non-empty means this launcher cannot fully install the server's client
        /// setup; callers should prompt for a launcher update. Never null.
        /// </summary>
        public IReadOnlyList<string> SkippedRequired { get; internal set; } = new string[0];

        /// <summary><see cref="Issued"/> parsed as a UTC timestamp, or null when absent or unparseable.</summary>
        public DateTimeOffset? IssuedUtc
        {
            get
            {
                DateTimeOffset value;
                if (!string.IsNullOrEmpty(Issued)
                    && DateTimeOffset.TryParse(Issued, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value))
                {
                    return value;
                }

                return null;
            }
        }
    }

    /// <summary>The game server endpoint.</summary>
    [JsonObject(MemberSerialization.OptIn)]
    public sealed class ServerInfo
    {
        /// <summary>Display name of the server.</summary>
        [JsonProperty("name")]
        public string Name { get; set; }

        /// <summary>Host name or IP. Passed to the client as-is: acclient resolves host names itself.</summary>
        [JsonProperty("host")]
        public string Host { get; set; }

        /// <summary>UDP port.</summary>
        [JsonProperty("port")]
        public int Port { get; set; }
    }

    /// <summary>
    /// Client command-line template. Placeholders: <c>{user}</c>, <c>{pass}</c>, <c>{host}</c>,
    /// <c>{port}</c>. The default wraps user and password in double quotes, which acclient accepts
    /// (a password containing a space arrives intact). The argument builder MUST REJECT a user
    /// name or password containing a double quote or any control character rather than try to
    /// escape it: acclient's own command-line parsing is not documented, so no escaping scheme
    /// can be trusted to round-trip.
    /// </summary>
    [JsonObject(MemberSerialization.OptIn)]
    public sealed class LaunchInfo
    {
        /// <summary>Default template, verified against the End-of-Retail client (login packet received).</summary>
        public const string DefaultArgTemplate = "-a \"{user}\" -v \"{pass}\" -h {host}:{port} -rodat on";

        /// <summary>Template with <c>{user}</c>, <c>{pass}</c>, <c>{host}</c>, <c>{port}</c> placeholders.</summary>
        [JsonProperty("argTemplate")]
        public string ArgTemplate { get; set; } = DefaultArgTemplate;

        /// <summary>Extra arguments appended verbatim after the expanded template (may be empty).</summary>
        [JsonProperty("extraArgs")]
        public string ExtraArgs { get; set; } = string.Empty;
    }

    /// <summary>Self-update pointer for the launcher exe itself.</summary>
    [JsonObject(MemberSerialization.OptIn)]
    public sealed class LauncherUpdateInfo
    {
        /// <summary>Latest launcher version (semantic version string).</summary>
        [JsonProperty("version")]
        public string Version { get; set; }

        /// <summary>Oldest launcher version still allowed to run; older ones must update first.</summary>
        [JsonProperty("minVersion")]
        public string MinVersion { get; set; }

        /// <summary>Download URL of the latest exe; empty when no update is published.</summary>
        [JsonProperty("url")]
        public string Url { get; set; }

        /// <summary>SHA-256 of the latest exe (64 hex); empty when <see cref="Url"/> is empty.</summary>
        [JsonProperty("sha256")]
        public string Sha256 { get; set; }

        /// <summary>Length of the latest exe in bytes.</summary>
        [JsonProperty("size")]
        public long Size { get; set; }

        /// <summary>Expected Authenticode signer subject if the exe is code-signed, otherwise null (hash only).</summary>
        [JsonProperty("authenticodeSubject")]
        public string AuthenticodeSubject { get; set; }
    }

    /// <summary>
    /// One installable component. <see cref="Kind"/> selects which fields apply:
    /// <see cref="ComponentKinds.Installer"/> uses Transport/Url/Size/Sha256/Elevate/Args/SatisfiedWhen;
    /// <see cref="ComponentKinds.ZipOverlay"/> uses Transport/Url/Size/Sha256/Files;
    /// <see cref="ComponentKinds.BytePatch"/> uses Target/InputSha256/OutputSha256/Patches.
    /// All SHA-256 values are 64 hex characters; compare with <see cref="Hasher.FixedTimeEquals(string, string)"/>.
    /// </summary>
    [JsonObject(MemberSerialization.OptIn)]
    public sealed class ManifestComponent
    {
        /// <summary>Stable unique id (for example <c>ac-client</c>); also used in progress step ids and telemetry.</summary>
        [JsonProperty("id")]
        public string Id { get; set; }

        /// <summary>Component kind; an open string (see <see cref="ComponentKinds"/>).</summary>
        [JsonProperty("kind")]
        public string Kind { get; set; }

        /// <summary>The player cannot deselect a required component.</summary>
        [JsonProperty("required")]
        public bool Required { get; set; }

        /// <summary>Initial checkbox state for an optional component.</summary>
        [JsonProperty("defaultSelected")]
        public bool DefaultSelected { get; set; }

        /// <summary>Player-facing name.</summary>
        [JsonProperty("title")]
        public string Title { get; set; }

        /// <summary>Player-facing description of where the bytes come from (shown before opt-in).</summary>
        [JsonProperty("sourceLabel")]
        public string SourceLabel { get; set; }

        /// <summary>Download transport (see <see cref="Transports"/>).</summary>
        [JsonProperty("transport")]
        public string Transport { get; set; }

        /// <summary>Download URL for the transport.</summary>
        [JsonProperty("url")]
        public string Url { get; set; }

        /// <summary>Length of the downloaded file in bytes.</summary>
        [JsonProperty("size")]
        public long Size { get; set; }

        /// <summary>SHA-256 of the downloaded file.</summary>
        [JsonProperty("sha256")]
        public string Sha256 { get; set; }

        /// <summary>Installer: run it elevated (UAC).</summary>
        [JsonProperty("elevate")]
        public bool Elevate { get; set; }

        /// <summary>Installer: command-line arguments, or null for none.</summary>
        [JsonProperty("args")]
        public string Args { get; set; }

        /// <summary>Installer: the component counts as installed when ANY rule matches.</summary>
        [JsonProperty("satisfiedWhen")]
        public List<SatisfiedWhenRule> SatisfiedWhen { get; set; } = new List<SatisfiedWhenRule>();

        /// <summary>Zip overlay: the files extracted into the AC folder, each verified after extraction.</summary>
        [JsonProperty("files")]
        public List<ManifestFile> Files { get; set; } = new List<ManifestFile>();

        /// <summary>Byte patch: path of the target file relative to the AC folder.</summary>
        [JsonProperty("target")]
        public string Target { get; set; }

        /// <summary>Byte patch: required SHA-256 of the target before patching.</summary>
        [JsonProperty("inputSha256")]
        public string InputSha256 { get; set; }

        /// <summary>Byte patch: required SHA-256 of the target after patching.</summary>
        [JsonProperty("outputSha256")]
        public string OutputSha256 { get; set; }

        /// <summary>Byte patch: in-place replacements at FILE offsets (not virtual addresses).</summary>
        [JsonProperty("patches")]
        public List<BytePatch> Patches { get; set; } = new List<BytePatch>();
    }

    /// <summary>An installer's "already installed" test: the file at <see cref="Path"/> hashes to any of <see cref="Sha256"/>.</summary>
    [JsonObject(MemberSerialization.OptIn)]
    public sealed class SatisfiedWhenRule
    {
        /// <summary>Path relative to the AC folder.</summary>
        [JsonProperty("path")]
        public string Path { get; set; }

        /// <summary>Accepted SHA-256 values.</summary>
        [JsonProperty("sha256")]
        public List<string> Sha256 { get; set; } = new List<string>();
    }

    /// <summary>A file a zip overlay places into the AC folder.</summary>
    [JsonObject(MemberSerialization.OptIn)]
    public sealed class ManifestFile
    {
        /// <summary>Path relative to the AC folder (and inside the archive).</summary>
        [JsonProperty("path")]
        public string Path { get; set; }

        /// <summary>Length in bytes.</summary>
        [JsonProperty("size")]
        public long Size { get; set; }

        /// <summary>SHA-256.</summary>
        [JsonProperty("sha256")]
        public string Sha256 { get; set; }
    }

    /// <summary>One in-place byte replacement. <see cref="Expect"/> and <see cref="Replace"/> are hex strings of equal length.</summary>
    [JsonObject(MemberSerialization.OptIn)]
    public sealed class BytePatch
    {
        /// <summary>File offset (decimal in JSON) of the first byte.</summary>
        [JsonProperty("offset")]
        public long Offset { get; set; }

        /// <summary>Bytes that must be present before patching, as hex.</summary>
        [JsonProperty("expect")]
        public string Expect { get; set; }

        /// <summary>Bytes written in their place, as hex.</summary>
        [JsonProperty("replace")]
        public string Replace { get; set; }

        /// <summary>Decodes <see cref="Expect"/>.</summary>
        public byte[] GetExpectBytes() => HexBytes.Parse(Expect);

        /// <summary>Decodes <see cref="Replace"/>.</summary>
        public byte[] GetReplaceBytes() => HexBytes.Parse(Replace);
    }

    /// <summary>Telemetry settings.</summary>
    [JsonObject(MemberSerialization.OptIn)]
    public sealed class TelemetryInfo
    {
        /// <summary>HTTPS endpoint for opt-in telemetry, or null to disable telemetry entirely.</summary>
        [JsonProperty("endpoint")]
        public string Endpoint { get; set; }
    }

    /// <summary>Links shown in the UI (empty string = hide).</summary>
    [JsonObject(MemberSerialization.OptIn)]
    public sealed class LinksInfo
    {
        /// <summary>Privacy policy.</summary>
        [JsonProperty("privacy")]
        public string Privacy { get; set; }

        /// <summary>Help page.</summary>
        [JsonProperty("help")]
        public string Help { get; set; }

        /// <summary>Discord invite.</summary>
        [JsonProperty("discord")]
        public string Discord { get; set; }
    }

    /// <summary>Known values of <see cref="ManifestComponent.Kind"/>.</summary>
    public static class ComponentKinds
    {
        /// <summary>Download and run an installer.</summary>
        public const string Installer = "installer";

        /// <summary>Download an archive and extract verified files over the AC folder.</summary>
        public const string ZipOverlay = "zip-overlay";

        /// <summary>Patch bytes of a file already in the AC folder.</summary>
        public const string BytePatch = "byte-patch";

        /// <summary>Every kind this launcher can act on.</summary>
        public static readonly IReadOnlyList<string> Known = new[] { Installer, ZipOverlay, BytePatch };
    }

    /// <summary>Known values of <see cref="ManifestComponent.Transport"/>.</summary>
    public static class Transports
    {
        /// <summary>Plain HTTP(S) GET.</summary>
        public const string Http = "http";

        /// <summary>mega.nz public file link.</summary>
        public const string Mega = "mega";
    }

    /// <summary>Hex string to bytes.</summary>
    public static class HexBytes
    {
        /// <summary>Parses an even-length hex string (either case, no separators).</summary>
        /// <exception cref="FormatException">On odd length or a non-hex character.</exception>
        public static byte[] Parse(string hex)
        {
            if (hex == null || hex.Length % 2 != 0)
            {
                throw new FormatException("Hex string must have an even number of characters.");
            }

            var result = new byte[hex.Length / 2];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = (byte)((Nibble(hex[2 * i]) << 4) | Nibble(hex[(2 * i) + 1]));
            }

            return result;
        }

        private static int Nibble(char c)
        {
            if (c >= '0' && c <= '9')
            {
                return c - '0';
            }

            if (c >= 'a' && c <= 'f')
            {
                return c - 'a' + 10;
            }

            if (c >= 'A' && c <= 'F')
            {
                return c - 'A' + 10;
            }

            throw new FormatException("Invalid hex character.");
        }
    }
}
