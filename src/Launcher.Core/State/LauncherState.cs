using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DreamWeave.Launcher.Core.State
{
    /// <summary>
    /// The launcher's persisted per-user state (state.json). Access it only through
    /// <see cref="StateStore.Read{T}"/> / <see cref="StateStore.Update"/>; do not keep references
    /// to it or its collections outside those callbacks. Fields this version does not know about
    /// are preserved verbatim on save, so an older launcher never destroys a newer one's data.
    /// </summary>
    [JsonObject(MemberSerialization.OptIn)]
    public sealed class LauncherState
    {
        /// <summary>Highest manifest serial ever accepted; a lower serial is a rollback and is refused.</summary>
        [JsonProperty("highestManifestSerial")]
        public long HighestManifestSerial { get; set; }

        /// <summary>The Asheron's Call client folder the player chose (or that was detected), or null.</summary>
        [JsonProperty("acFolder")]
        public string AcFolder { get; set; }

        /// <summary>Whether the player opted in to anonymous telemetry. Defaults to false (opt-in only).</summary>
        [JsonProperty("telemetryOptIn")]
        public bool TelemetryOptIn { get; set; }

        /// <summary>Random per-install identifier, created once on first run and never changed.</summary>
        [JsonProperty("installId")]
        public Guid InstallId { get; set; }

        /// <summary>The launcher version that last ran, used to detect a completed self-update.</summary>
        [JsonProperty("launcherVersionSeen")]
        public string LauncherVersionSeen { get; set; }

        /// <summary>
        /// Cache of file hashes keyed by full path (case-insensitive): a file whose size and
        /// last-write time still match its entry is not re-hashed.
        /// </summary>
        [JsonProperty("fileVerifyCache", ObjectCreationHandling = ObjectCreationHandling.Reuse)]
        public Dictionary<string, FileVerifyEntry> FileVerifyCache { get; } = new Dictionary<string, FileVerifyEntry>(StringComparer.OrdinalIgnoreCase);

        [JsonExtensionData]
        private IDictionary<string, JToken> _unknownFields = new Dictionary<string, JToken>();
    }

    /// <summary>One <see cref="LauncherState.FileVerifyCache"/> entry.</summary>
    [JsonObject(MemberSerialization.OptIn)]
    public sealed class FileVerifyEntry
    {
        /// <summary>File length in bytes when hashed.</summary>
        [JsonProperty("size")]
        public long Size { get; set; }

        /// <summary>File last-write time (UTC) when hashed.</summary>
        [JsonProperty("lastWriteUtc")]
        public DateTime LastWriteUtc { get; set; }

        /// <summary>SHA-256 as 64 uppercase hex characters (see <see cref="Hasher"/>).</summary>
        [JsonProperty("sha256")]
        public string Sha256 { get; set; }
    }
}
