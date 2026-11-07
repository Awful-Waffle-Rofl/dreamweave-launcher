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
    /// the signature over the exact bytes has verified.
    /// </summary>
    internal static class ManifestParser
    {
        public static LauncherManifest Parse(byte[] verifiedBytes)
        {
            JObject root = ReadRoot(verifiedBytes);

            JToken schemaToken = root["schema"];
            if (schemaToken == null || schemaToken.Type != JTokenType.Integer || (long)schemaToken != LauncherManifest.SupportedSchema)
            {
                string seen = schemaToken == null ? "none" : schemaToken.ToString(Formatting.None);
                throw new LauncherException(
                    LauncherError.ManifestSchemaUnsupported,
                    "The server's download list uses a newer format (schema " + seen + ") than this launcher understands. Update the launcher to download files; launching the game still works.");
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

            if (manifest.Serial < 1)
            {
                throw Invalid("its serial is missing or below 1.", null);
            }

            var warnings = new List<string>();
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
                    throw Invalid("a component has no id.", null);
                }

                if (!seenIds.Add(c.Id))
                {
                    throw Invalid("component id '" + c.Id + "' appears twice.", null);
                }

                if (!ComponentKinds.Known.Contains(c.Kind ?? string.Empty, StringComparer.Ordinal))
                {
                    warnings.Add("Skipping component '" + c.Id + "' with unknown kind '" + (c.Kind ?? "(none)") + "'" + (c.Required ? " (marked required)." : "."));
                    continue;
                }

                c.SatisfiedWhen = c.SatisfiedWhen ?? new List<SatisfiedWhenRule>();
                c.Files = c.Files ?? new List<ManifestFile>();
                c.Patches = c.Patches ?? new List<BytePatch>();
                foreach (SatisfiedWhenRule rule in c.SatisfiedWhen.Where(r => r != null))
                {
                    rule.Sha256 = rule.Sha256 ?? new List<string>();
                }

                kept.Add(c);
            }

            manifest.Components = kept;
            manifest.Launch = manifest.Launch ?? new LaunchInfo();
            manifest.Warnings = warnings.AsReadOnly();
            return manifest;
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
                        throw Invalid("it has trailing content after the JSON object.", null);
                    }

                    var obj = token as JObject;
                    if (obj == null)
                    {
                        throw Invalid("it is not a JSON object.", null);
                    }

                    return obj;
                }
            }
            catch (JsonException ex)
            {
                throw Invalid("it is not valid JSON: " + ex.Message, ex);
            }
        }

        private static JsonSerializerSettings ManifestSerializerSettings()
        {
            var s = JsonDefaults.Create();
            s.ObjectCreationHandling = ObjectCreationHandling.Replace;
            return s;
        }

        private static LauncherException Invalid(string why, Exception inner)
        {
            return new LauncherException(LauncherError.ManifestInvalid, "The server's download list is signed but " + why, inner);
        }
    }
}
