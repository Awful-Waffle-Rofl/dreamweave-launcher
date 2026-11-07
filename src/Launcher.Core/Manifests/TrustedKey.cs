using System;
using System.Security.Cryptography;

namespace DreamWeave.Launcher.Core.Manifests
{
    /// <summary>
    /// A public ECDSA P-256 key the launcher trusts to sign manifests. Its <see cref="KeyId"/> is
    /// DERIVED from the key (<c>dw-</c> + the first 8 bytes, lowercase hex, of SHA-256 over the
    /// uncompressed point <c>04 || X || Y</c>), and the constructor refuses an id that does not
    /// match, so a key id can never be paired with the wrong key by a copy-paste slip.
    /// Text form (see <see cref="ToPublicString"/>): <c>keyId:X:Y</c> with X and Y in standard base64.
    /// </summary>
    public sealed class TrustedKey
    {
        /// <summary>Length of each P-256 coordinate in bytes.</summary>
        public const int CoordinateLength = 32;

        private readonly byte[] _x;
        private readonly byte[] _y;

        /// <summary>Creates a trusted key from base64 coordinates, validating the id and the point.</summary>
        /// <exception cref="ArgumentException">Malformed coordinates, a key id that does not match, or a point not on P-256.</exception>
        public TrustedKey(string keyId, string xBase64, string yBase64)
            : this(keyId, DecodeCoordinate(xBase64, "x"), DecodeCoordinate(yBase64, "y"))
        {
        }

        /// <summary>Creates a trusted key from raw coordinates, validating the id and the point.</summary>
        public TrustedKey(string keyId, byte[] x, byte[] y)
        {
            if (x == null || x.Length != CoordinateLength || y == null || y.Length != CoordinateLength)
            {
                throw new ArgumentException("P-256 coordinates must be 32 bytes each.");
            }

            string derived = ComputeKeyId(x, y);
            if (!string.Equals(keyId, derived, StringComparison.Ordinal))
            {
                throw new ArgumentException("Key id '" + keyId + "' does not match the key (expected '" + derived + "').", nameof(keyId));
            }

            _x = (byte[])x.Clone();
            _y = (byte[])y.Clone();
            KeyId = derived;

            try
            {
                using (CreateEcdsa())
                {
                }
            }
            catch (CryptographicException ex)
            {
                throw new ArgumentException("The coordinates are not a valid P-256 public key.", ex);
            }
        }

        /// <summary>The derived key id.</summary>
        public string KeyId { get; }

        /// <summary>X coordinate (copy).</summary>
        public byte[] GetX() => (byte[])_x.Clone();

        /// <summary>Y coordinate (copy).</summary>
        public byte[] GetY() => (byte[])_y.Clone();

        /// <summary>Derives the key id for a P-256 public point.</summary>
        public static string ComputeKeyId(byte[] x, byte[] y)
        {
            var point = new byte[1 + (2 * CoordinateLength)];
            point[0] = 0x04;
            Buffer.BlockCopy(x, 0, point, 1, CoordinateLength);
            Buffer.BlockCopy(y, 0, point, 1 + CoordinateLength, CoordinateLength);
            byte[] digest = Hasher.ComputeSha256(point);
            var shortId = new byte[8];
            Buffer.BlockCopy(digest, 0, shortId, 0, shortId.Length);
            return "dw-" + Hasher.ToHex(shortId).ToLowerInvariant();
        }

        /// <summary>Parses the <c>keyId:X:Y</c> text form.</summary>
        /// <exception cref="ArgumentException">On any malformed or mismatched input.</exception>
        public static TrustedKey Parse(string text)
        {
            string[] parts = (text ?? string.Empty).Trim().Split(':');
            if (parts.Length != 3)
            {
                throw new ArgumentException("Expected 'keyId:X:Y'.", nameof(text));
            }

            return new TrustedKey(parts[0], parts[1], parts[2]);
        }

        /// <summary>The <c>keyId:X:Y</c> text form.</summary>
        public string ToPublicString()
        {
            return KeyId + ":" + Convert.ToBase64String(_x) + ":" + Convert.ToBase64String(_y);
        }

        /// <summary>A fresh verifier instance (ECDsa objects are not shared across threads).</summary>
        internal ECDsa CreateEcdsa()
        {
            var p = new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = (byte[])_x.Clone(), Y = (byte[])_y.Clone() },
            };
            return ECDsa.Create(p);
        }

        internal static byte[] DecodeCoordinate(string base64, string name)
        {
            try
            {
                byte[] b = Convert.FromBase64String(base64 ?? string.Empty);
                if (b.Length != CoordinateLength)
                {
                    throw new ArgumentException("Coordinate " + name + " must decode to 32 bytes.");
                }

                return b;
            }
            catch (FormatException ex)
            {
                throw new ArgumentException("Coordinate " + name + " is not valid base64.", ex);
            }
        }
    }
}
