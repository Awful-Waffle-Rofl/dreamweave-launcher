using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DreamWeave.Launcher.Core.Manifests
{
    /// <summary>
    /// Turns manifest bytes into a <see cref="LauncherManifest"/>. Internal on purpose: the only
    /// public route to a manifest is <see cref="ManifestVerifier"/>, which calls this strictly AFTER
    /// the signature over the exact bytes has verified. ManifestTool's <c>sign</c> and
    /// <c>verify</c> go through the same verifier, so they run every rule here too.
    /// </summary>
    internal static class ManifestParser
    {
        public static LauncherManifest Parse(byte[] verifiedBytes)
        {
            JObject root = ReadRoot(verifiedBytes);

            long? schema = TryReadInteger(root["schema"]);
            if (schema != LauncherManifest.SupportedSchema)
            {
                JToken schemaToken = root["schema"];
                string seen = schemaToken == null ? "none" : schemaToken.ToString(Formatting.None);
                throw new LauncherException(
                    LauncherError.ManifestSchemaUnsupported,
                    "The server's download list uses a newer format (schema " + seen + ") than this launcher understands. Update the launcher to download files; launching the game still works.");
            }

            JToken serialToken = root["serial"];
            if (serialToken == null || serialToken.Type != JTokenType.Integer || TryReadInteger(serialToken) == null)
            {
                throw Invalid("its serial is missing or not a whole number.");
            }

            LauncherManifest manifest;
            try
            {
                manifest = root.ToObject<LauncherManifest>(JsonSerializer.Create(ManifestSerializerSettings()));
            }
            catch (Exception ex) when (ex is JsonException || ex is FormatException || ex is InvalidCastException || ex is ArgumentException || ex is OverflowException)
            {
                throw Invalid("it could not be read: " + ex.Message, ex);
            }

            if (manifest == null)
            {
                throw Invalid("it is empty.");
            }

            if (manifest.Serial < 1)
            {
                throw Invalid("its serial is below 1.");
            }

            var warnings = new List<string>();
            var skippedRequired = new List<string>();
            var kept = new List<ManifestComponent>();
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ManifestComponent c in manifest.Components ?? new List<ManifestComponent>())
            {
                if (c == null)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(c.Id))
                {
                    throw Invalid("a component has no id.");
                }

                if (!seenIds.Add(c.Id))
                {
                    throw Invalid("component id '" + c.Id + "' appears twice.");
                }

                if (!ComponentKinds.Known.Contains(c.Kind ?? string.Empty, StringComparer.Ordinal))
                {
                    warnings.Add("Skipping component '" + c.Id + "' with unknown kind '" + (c.Kind ?? "(none)") + "'" + (c.Required ? " (marked required)." : "."));
                    if (c.Required)
                    {
                        skippedRequired.Add(c.Id);
                    }

                    continue;
                }

                c.SatisfiedWhen = (c.SatisfiedWhen ?? new List<SatisfiedWhenRule>()).Where(r => r != null).ToList();
                c.Files = (c.Files ?? new List<ManifestFile>()).Where(f => f != null).ToList();
                c.Patches = (c.Patches ?? new List<BytePatch>()).Where(p => p != null).ToList();
                foreach (SatisfiedWhenRule rule in c.SatisfiedWhen)
                {
                    rule.Sha256 = rule.Sha256 ?? new List<string>();
                }

                kept.Add(c);
            }

            manifest.Components = kept;
            manifest.Launch = manifest.Launch ?? new LaunchInfo();
            manifest.Warnings = warnings.AsReadOnly();
            manifest.SkippedRequired = skippedRequired.AsReadOnly();
            Validate(manifest);
            return manifest;
        }

        /// <summary>
        /// Semantic rules for a schema-1 manifest, applied after the kind filter (so components of
        /// unknown kinds are not checked). Any violation throws <see cref="LauncherError.ManifestInvalid"/>.
        /// </summary>
        internal static void Validate(LauncherManifest m)
        {
            if (m.Server == null)
            {
                throw Invalid("it has no server block.");
            }

            if (string.IsNullOrWhiteSpace(m.Server.Host))
            {
                throw Invalid("its server host is empty.");
            }

            if (!IsValidServerHost(m.Server.Host))
            {
                throw Invalid("its server host '" + m.Server.Host + "' is not a host name, IPv4 address or bracketed IPv6 address.");
            }

            if (m.Server.Port < 1 || m.Server.Port > 65535)
            {
                throw Invalid("its server port " + m.Server.Port + " is outside 1-65535.");
            }

            if (m.Launcher != null)
            {
                bool hasUrl = !string.IsNullOrEmpty(m.Launcher.Url);
                bool hasSha = !string.IsNullOrEmpty(m.Launcher.Sha256);
                if (hasUrl || hasSha)
                {
                    RequireSha(m.Launcher.Sha256, "launcher.sha256");
                }
            }

            foreach (ManifestComponent c in m.Components)
            {
                string where = "component '" + c.Id + "'";
                switch (c.Kind)
                {
                    case ComponentKinds.Installer:
                    case ComponentKinds.ZipOverlay:
                        if (string.IsNullOrWhiteSpace(c.Url))
                        {
                            throw Invalid(where + " has no url.");
                        }

                        RequireSha(c.Sha256, where + " sha256");
                        break;

                    case ComponentKinds.BytePatch:
                        RequireRelativePath(c.Target, where + " target");
                        RequireSha(c.InputSha256, where + " inputSha256");
                        RequireSha(c.OutputSha256, where + " outputSha256");
                        if (c.Patches.Count == 0)
                        {
                            throw Invalid(where + " has no patches.");
                        }

                        break;
                }

                foreach (SatisfiedWhenRule rule in c.SatisfiedWhen)
                {
                    RequireRelativePath(rule.Path, where + " satisfiedWhen path");
                    if (rule.Sha256.Count == 0)
                    {
                        throw Invalid(where + " satisfiedWhen rule for '" + rule.Path + "' lists no hashes.");
                    }

                    foreach (string h in rule.Sha256)
                    {
                        RequireSha(h, where + " satisfiedWhen sha256");
                    }
                }

                foreach (ManifestFile f in c.Files)
                {
                    RequireRelativePath(f.Path, where + " file path");
                    RequireSha(f.Sha256, where + " file '" + f.Path + "' sha256");
                }

                foreach (BytePatch p in c.Patches)
                {
                    if (p.Offset < 0)
                    {
                        throw Invalid(where + " has a patch at a negative offset.");
                    }

                    byte[] expect;
                    byte[] replace;
                    try
                    {
                        expect = p.GetExpectBytes();
                        replace = p.GetReplaceBytes();
                    }
                    catch (FormatException)
                    {
                        throw Invalid(where + " has a patch whose expect/replace is not hex.");
                    }

                    if (expect.Length == 0 || expect.Length != replace.Length)
                    {
                        throw Invalid(where + " has a patch whose expect/replace are empty or differ in length.");
                    }
                }
            }
        }

        /// <summary>Reads the root object with depth and date handling locked down; a leading UTF-8 BOM is tolerated.</summary>
        public static JObject ReadRoot(byte[] bytes)
        {
            int offset = (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) ? 3 : 0;
            string text;
            try
            {
                text = new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset);
            }
            catch (DecoderFallbackException ex)
            {
                throw Invalid("it is not valid UTF-8.", ex);
            }

            try
            {
                using (var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None, MaxDepth = JsonDefaults.MaxDepth })
                {
                    JToken token = JToken.ReadFrom(reader);
                    if (reader.Read())
                    {
                        throw Invalid("it has trailing content after the JSON object.");
                    }

                    var obj = token as JObject;
                    if (obj == null)
                    {
                        throw Invalid("it is not a JSON object.");
                    }

                    return obj;
                }
            }
            catch (JsonException ex)
            {
                throw Invalid("it is not valid JSON: " + ex.Message, ex);
            }
        }

        /// <summary>The token as a 64-bit integer, or null when it is absent, not an integer, or out of range.</summary>
        private static long? TryReadInteger(JToken token)
        {
            if (token == null || token.Type != JTokenType.Integer)
            {
                return null;
            }

            try
            {
                return token.Value<long>();
            }
            catch (Exception ex) when (ex is OverflowException || ex is InvalidCastException || ex is FormatException)
            {
                return null;
            }
        }

        private static void RequireSha(string value, string what)
        {
            if (!Hasher.IsSha256Hex(value))
            {
                throw Invalid(what + " is not a 64-character hex SHA-256.");
            }
        }

        /// <summary>
        /// A path must pass <see cref="Detection.AcPathSafety.CheckRelativePath"/>: non-empty,
        /// relative, no drive/stream colon, wildcard, control or reserved character, no empty or
        /// '..' segment, no segment ending in a dot or space, and no Windows device name
        /// (CON, PRN, AUX, NUL, COM0-9, LPT0-9 and the superscript 1-3 forms, with or without an extension).
        /// </summary>
        private static void RequireRelativePath(string path, string what)
        {
            string reason = Detection.AcPathSafety.CheckRelativePath(path);
            if (reason != null)
            {
                throw Invalid(what + " '" + path + "' is not allowed: " + reason + ".");
            }
        }

        /// <summary>
        /// The server host is substituted UNQUOTED into the client command line (<c>-h {host}:{port}</c>),
        /// so it must be exactly one of: a DNS host name (letters, digits, hyphens; dot-separated
        /// labels of 1-63 characters not starting or ending with a hyphen; at most 253 characters),
        /// a dotted-quad IPv4 address, or a bracketed IPv6 address (<c>[2001:db8::1]</c>, no zone id).
        /// Anything else (spaces, quotes, an unbracketed IPv6, a port, a scheme) is rejected.
        /// </summary>
        internal static bool IsValidServerHost(string host)
        {
            if (string.IsNullOrEmpty(host) || host.Length > 253)
            {
                return false;
            }

            if (host[0] == '[')
            {
                if (host.Length < 4 || host[host.Length - 1] != ']')
                {
                    return false;
                }

                string inner = host.Substring(1, host.Length - 2);
                System.Net.IPAddress v6;
                return inner.IndexOf('%') < 0
                    && inner.All(ch => ch == ':' || ch == '.' || Uri.IsHexDigit(ch))
                    && System.Net.IPAddress.TryParse(inner, out v6)
                    && v6.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
            }

            if (host.All(ch => ch == '.' || (ch >= '0' && ch <= '9')))
            {
                string[] octets = host.Split('.');
                return octets.Length == 4 && octets.All(o =>
                    o.Length >= 1 && o.Length <= 3 && (o.Length == 1 || o[0] != '0') && int.Parse(o, System.Globalization.CultureInfo.InvariantCulture) <= 255);
            }

            foreach (string label in host.Split('.'))
            {
                if (label.Length < 1 || label.Length > 63 || label[0] == '-' || label[label.Length - 1] == '-')
                {
                    return false;
                }

                foreach (char ch in label)
                {
                    bool ok = (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == '-';
                    if (!ok)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static JsonSerializerSettings ManifestSerializerSettings()
        {
            var s = JsonDefaults.Create();
            s.ObjectCreationHandling = ObjectCreationHandling.Replace;
            return s;
        }

        private static LauncherException Invalid(string why, Exception inner = null)
        {
            return new LauncherException(LauncherError.ManifestInvalid, "The server's download list is signed but " + why, inner);
        }
    }
}
