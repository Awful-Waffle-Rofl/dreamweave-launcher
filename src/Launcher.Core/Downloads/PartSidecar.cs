using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace DreamWeave.Launcher.Core.Downloads
{
    /// <summary>
    /// The <c>&lt;name&gt;.part.json</c> file next to an HTTP <c>.part</c>: which source and which
    /// pinned file the partial bytes belong to, plus the server's validators for If-Range. A part
    /// file without a matching sidecar is never resumed.
    /// </summary>
    [JsonObject(MemberSerialization.OptIn)]
    internal sealed class PartSidecar
    {
        private const int MaxSidecarBytes = 64 * 1024;

        [JsonProperty("url")]
        public string Url { get; set; }

        [JsonProperty("expectedSha256")]
        public string ExpectedSha256 { get; set; }

        [JsonProperty("expectedSize")]
        public long ExpectedSize { get; set; }

        [JsonProperty("etag")]
        public string ETag { get; set; }

        [JsonProperty("lastModified")]
        public string LastModified { get; set; }

        /// <summary>True when this sidecar describes exactly <paramref name="request"/>.</summary>
        public bool Matches(DownloadRequest request)
        {
            return string.Equals(Url, request.Url.AbsoluteUri, StringComparison.Ordinal)
                && Hasher.FixedTimeEquals(ExpectedSha256, request.ExpectedSha256)
                && ExpectedSize == request.ExpectedSize;
        }

        /// <summary>
        /// The If-Range value to send: a strong ETag when there is one, else Last-Modified, else null
        /// (weak ETags are not allowed in If-Range).
        /// </summary>
        public string IfRangeValidator()
        {
            if (!string.IsNullOrEmpty(ETag) && !ETag.StartsWith("W/", StringComparison.Ordinal))
            {
                return ETag;
            }

            return string.IsNullOrEmpty(LastModified) ? null : LastModified;
        }

        /// <summary>Reads a sidecar, or returns null when it is missing, too large, or unreadable.</summary>
        public static PartSidecar TryRead(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > MaxSidecarBytes)
                {
                    return null;
                }

                string json = File.ReadAllText(path, Encoding.UTF8);
                return JsonConvert.DeserializeObject<PartSidecar>(json, JsonDefaults.Create());
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
            {
                return null;
            }
        }

        /// <summary>Atomically writes the sidecar.</summary>
        public void Write(string path)
        {
            string json = JsonConvert.SerializeObject(this, JsonDefaults.Create());
            AtomicFile.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(json));
        }
    }
}
