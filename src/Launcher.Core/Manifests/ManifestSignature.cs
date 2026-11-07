using System;
using System.Text;
using Newtonsoft.Json.Linq;

namespace DreamWeave.Launcher.Core.Manifests
{
    /// <summary>
    /// The detached signature file (<c>manifest.json.sig</c>):
    /// <c>{ "keyId": "dw-...", "alg": "ES256-P1363", "sig": "&lt;base64&gt;" }</c>.
    /// <c>sig</c> is a 64-byte IEEE P1363 (r || s) ECDSA P-256 signature over SHA-256 of the EXACT
    /// manifest file bytes; the manifest is never re-serialized before signing or verifying.
    /// </summary>
    public sealed class ManifestSignature
    {
        /// <summary>The only supported algorithm identifier.</summary>
        public const string AlgorithmEs256P1363 = "ES256-P1363";

        /// <summary>Raw P1363 signature length for P-256.</summary>
        public const int SignatureLength = 64;

        /// <summary>Upper bound on a signature file's size; anything larger is rejected unread.</summary>
        public const int MaxFileBytes = 4096;

        private readonly byte[] _sig;

        /// <summary>Creates a signature value.</summary>
        public ManifestSignature(string keyId, byte[] signature)
        {
            if (string.IsNullOrEmpty(keyId))
            {
                throw new ArgumentException("Key id required.", nameof(keyId));
            }

            if (signature == null || signature.Length != SignatureLength)
            {
                throw new ArgumentException("Signature must be 64 bytes.", nameof(signature));
            }

            KeyId = keyId;
            _sig = (byte[])signature.Clone();
        }

        /// <summary>Id of the key that made the signature.</summary>
        public string KeyId { get; }

        /// <summary>Always <see cref="AlgorithmEs256P1363"/>.</summary>
        public string Algorithm => AlgorithmEs256P1363;

        /// <summary>The raw signature (copy).</summary>
        public byte[] GetSignatureBytes() => (byte[])_sig.Clone();

        /// <summary>
        /// Parses a signature file. Strict: wrong algorithm, missing fields, bad base64, wrong
        /// length or oversize input all throw <see cref="LauncherError.ManifestSignatureInvalid"/>.
        /// </summary>
        public static ManifestSignature Parse(byte[] fileBytes)
        {
            if (fileBytes == null || fileBytes.Length == 0 || fileBytes.Length > MaxFileBytes)
            {
                throw Bad("the signature file is empty or too large.");
            }

            JObject obj;
            try
            {
                obj = ManifestParser.ReadRoot(fileBytes);
            }
            catch (LauncherException ex)
            {
                throw Bad("the signature file is not valid JSON.", ex);
            }

            string keyId = StringField(obj, "keyId");
            string alg = StringField(obj, "alg");
            string sig = StringField(obj, "sig");
            if (keyId == null || alg == null || sig == null)
            {
                throw Bad("the signature file is missing keyId, alg or sig.");
            }

            if (!string.Equals(alg, AlgorithmEs256P1363, StringComparison.Ordinal))
            {
                throw Bad("the signature algorithm '" + alg + "' is not supported.");
            }

            byte[] raw;
            try
            {
                raw = Convert.FromBase64String(sig);
            }
            catch (FormatException ex)
            {
                throw Bad("the signature is not valid base64.", ex);
            }

            if (raw.Length != SignatureLength)
            {
                throw Bad("the signature has the wrong length.");
            }

            return new ManifestSignature(keyId, raw);
        }

        /// <summary>Serializes to signature-file bytes (UTF-8, no BOM, trailing LF).</summary>
        public byte[] ToFileBytes()
        {
            var obj = new JObject
            {
                ["keyId"] = KeyId,
                ["alg"] = AlgorithmEs256P1363,
                ["sig"] = Convert.ToBase64String(_sig),
            };
            return new UTF8Encoding(false).GetBytes(obj.ToString(Newtonsoft.Json.Formatting.Indented).Replace("\r\n", "\n") + "\n");
        }

        private static string StringField(JObject obj, string name)
        {
            JToken t = obj[name];
            return t != null && t.Type == JTokenType.String ? (string)t : null;
        }

        private static LauncherException Bad(string why, Exception inner = null)
        {
            return new LauncherException(LauncherError.ManifestSignatureInvalid, "The server's download list failed its security check: " + why, inner);
        }
    }
}
