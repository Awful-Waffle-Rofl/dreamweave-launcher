using System;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace DreamWeave.Launcher.Core.Manifests
{
    /// <summary>
    /// A private ECDSA P-256 manifest-signing key. Used only by ManifestTool and tests; the
    /// shipped launcher never holds one. Key file format (JSON, UTF-8):
    /// <c>{ "format": "dreamweave-manifest-key-1", "keyId": "dw-...", "curve": "P-256",
    /// "d": "&lt;base64&gt;", "x": "&lt;base64&gt;", "y": "&lt;base64&gt;" }</c>.
    /// The production key file must stay OFFLINE (never committed, never on a server).
    /// </summary>
    public sealed class SigningKey
    {
        /// <summary>Key file format marker.</summary>
        public const string FileFormat = "dreamweave-manifest-key-1";

        private readonly byte[] _d;

        private SigningKey(byte[] d, byte[] x, byte[] y)
        {
            if (d == null || d.Length != TrustedKey.CoordinateLength)
            {
                throw new ArgumentException("P-256 private scalar must be 32 bytes.");
            }

            _d = (byte[])d.Clone();
            PublicKey = new TrustedKey(TrustedKey.ComputeKeyId(x, y), x, y);
        }

        /// <summary>The matching public key (what goes into <see cref="TrustedKeys"/>).</summary>
        public TrustedKey PublicKey { get; }

        /// <summary>Generates a new random key.</summary>
        public static SigningKey Generate()
        {
            using (ECDsa ec = ECDsa.Create(ECCurve.NamedCurves.nistP256))
            {
                ECParameters p = ec.ExportParameters(true);
                return new SigningKey(PadLeft(p.D), PadLeft(p.Q.X), PadLeft(p.Q.Y));
            }
        }

        /// <summary>Loads a key file's text.</summary>
        /// <exception cref="ArgumentException">On a malformed file or a key id that does not match the key.</exception>
        public static SigningKey FromJson(string json)
        {
            JObject o;
            try
            {
                o = JObject.Parse(json);
            }
            catch (Newtonsoft.Json.JsonException ex)
            {
                throw new ArgumentException("Key file is not valid JSON.", ex);
            }

            if ((string)o["format"] != FileFormat || (string)o["curve"] != "P-256")
            {
                throw new ArgumentException("Not a " + FileFormat + " P-256 key file.");
            }

            byte[] d = TrustedKey.DecodeCoordinate((string)o["d"], "d");
            byte[] x = TrustedKey.DecodeCoordinate((string)o["x"], "x");
            byte[] y = TrustedKey.DecodeCoordinate((string)o["y"], "y");
            var key = new SigningKey(d, x, y);
            string claimed = (string)o["keyId"];
            if (claimed != key.PublicKey.KeyId)
            {
                throw new ArgumentException("Key file keyId does not match its key.");
            }

            return key;
        }

        /// <summary>Key file text (contains the PRIVATE key).</summary>
        public string ToJson()
        {
            var o = new JObject
            {
                ["format"] = FileFormat,
                ["keyId"] = PublicKey.KeyId,
                ["curve"] = "P-256",
                ["d"] = Convert.ToBase64String(_d),
                ["x"] = Convert.ToBase64String(PublicKey.GetX()),
                ["y"] = Convert.ToBase64String(PublicKey.GetY()),
            };
            return o.ToString(Newtonsoft.Json.Formatting.Indented).Replace("\r\n", "\n") + "\n";
        }

        /// <summary>Signs the exact manifest bytes.</summary>
        public ManifestSignature Sign(byte[] manifestBytes)
        {
            if (manifestBytes == null)
            {
                throw new ArgumentNullException(nameof(manifestBytes));
            }

            var p = new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                D = (byte[])_d.Clone(),
                Q = new ECPoint { X = PublicKey.GetX(), Y = PublicKey.GetY() },
            };
            using (ECDsa ec = ECDsa.Create(p))
            {
                // .NET's ECDsa.SignData returns IEEE P1363 (r || s), 64 bytes for P-256.
                byte[] sig = ec.SignData(manifestBytes, HashAlgorithmName.SHA256);
                return new ManifestSignature(PublicKey.KeyId, sig);
            }
        }

        private static byte[] PadLeft(byte[] value)
        {
            if (value.Length == TrustedKey.CoordinateLength)
            {
                return value;
            }

            if (value.Length > TrustedKey.CoordinateLength)
            {
                throw new CryptographicException("Unexpected P-256 parameter length.");
            }

            var padded = new byte[TrustedKey.CoordinateLength];
            Buffer.BlockCopy(value, 0, padded, TrustedKey.CoordinateLength - value.Length, value.Length);
            return padded;
        }
    }
}
