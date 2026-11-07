using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace DreamWeave.Launcher.Core.Manifests
{
    /// <summary>
    /// The single gate between untrusted manifest bytes and a usable <see cref="LauncherManifest"/>.
    /// Order of checks (each failure throws <see cref="LauncherException"/>):
    /// 1. the signature file parses and names a key in the trusted set (empty set = always fail);
    /// 2. the ECDSA P-256 signature verifies over the EXACT manifest bytes;
    /// 3. only then are the bytes parsed; a schema other than 1 is ManifestSchemaUnsupported;
    /// 4. the serial must be at least <c>minimumSerial</c> (equal is allowed so a re-fetch of the
    ///    current manifest succeeds; lower is ManifestRollback).
    /// The verifier is pure: persisting the accepted serial is the caller's job
    /// (<see cref="ManifestProvider"/> does it). ManifestTool uses this same class, so the signer
    /// and the launcher cannot drift.
    /// </summary>
    public sealed class ManifestVerifier
    {
        private readonly Dictionary<string, TrustedKey> _keys = new Dictionary<string, TrustedKey>(StringComparer.Ordinal);

        /// <summary>Creates a verifier trusting exactly <paramref name="trustedKeys"/>.</summary>
        public ManifestVerifier(IEnumerable<TrustedKey> trustedKeys)
        {
            if (trustedKeys == null)
            {
                throw new ArgumentNullException(nameof(trustedKeys));
            }

            foreach (TrustedKey k in trustedKeys)
            {
                if (k != null)
                {
                    _keys[k.KeyId] = k;
                }
            }
        }

        /// <summary>A verifier over the compiled-in <see cref="TrustedKeys.Production"/> set.</summary>
        public static ManifestVerifier CreateProduction()
        {
            return new ManifestVerifier(TrustedKeys.Production);
        }

        /// <summary>Number of trusted keys (0 means every manifest is rejected).</summary>
        public int TrustedKeyCount => _keys.Count;

        /// <summary>Checks only the signature (steps 1 and 2). Returns the parsed signature on success.</summary>
        /// <exception cref="LauncherException">ManifestSignatureInvalid.</exception>
        public ManifestSignature VerifySignature(byte[] manifestBytes, byte[] signatureFileBytes)
        {
            if (manifestBytes == null)
            {
                throw new ArgumentNullException(nameof(manifestBytes));
            }

            ManifestSignature sig = ManifestSignature.Parse(signatureFileBytes);
            TrustedKey key;
            if (!_keys.TryGetValue(sig.KeyId, out key))
            {
                throw new LauncherException(
                    LauncherError.ManifestSignatureInvalid,
                    _keys.Count == 0
                        ? "The server's download list cannot be checked: this launcher build has no trusted signing keys."
                        : "The server's download list is signed by an unknown key (" + sig.KeyId + ").");
            }

            bool ok;
            try
            {
                using (ECDsa ec = key.CreateEcdsa())
                {
                    ok = ec.VerifyData(manifestBytes, sig.GetSignatureBytes(), HashAlgorithmName.SHA256);
                }
            }
            catch (CryptographicException)
            {
                ok = false;
            }

            if (!ok)
            {
                throw new LauncherException(LauncherError.ManifestSignatureInvalid, "The server's download list failed its security check: the signature does not match. It may have been tampered with.");
            }

            return sig;
        }

        /// <summary>Full verification (steps 1 to 4).</summary>
        /// <param name="manifestBytes">Exact manifest file bytes.</param>
        /// <param name="signatureFileBytes">Exact signature file bytes.</param>
        /// <param name="minimumSerial">Highest serial already accepted (pass 0 when there is no history).</param>
        /// <exception cref="LauncherException">ManifestSignatureInvalid, ManifestSchemaUnsupported, ManifestInvalid or ManifestRollback.</exception>
        public VerifiedManifest Verify(byte[] manifestBytes, byte[] signatureFileBytes, long minimumSerial)
        {
            ManifestSignature sig = VerifySignature(manifestBytes, signatureFileBytes);
            LauncherManifest manifest = ManifestParser.Parse(manifestBytes);
            if (manifest.Serial < minimumSerial)
            {
                throw new LauncherException(
                    LauncherError.ManifestRollback,
                    "The server's download list is older (serial " + manifest.Serial + ") than one this launcher already accepted (serial " + minimumSerial + "), so it was refused.");
            }

            return new VerifiedManifest(manifest, manifestBytes, signatureFileBytes, sig.KeyId);
        }
    }

    /// <summary>A manifest that passed <see cref="ManifestVerifier.Verify"/>, with the exact bytes that were verified.</summary>
    public sealed class VerifiedManifest
    {
        private readonly byte[] _manifestBytes;
        private readonly byte[] _signatureBytes;

        internal VerifiedManifest(LauncherManifest manifest, byte[] manifestBytes, byte[] signatureBytes, string keyId)
        {
            Manifest = manifest;
            _manifestBytes = (byte[])manifestBytes.Clone();
            _signatureBytes = (byte[])signatureBytes.Clone();
            KeyId = keyId;
        }

        /// <summary>The parsed manifest.</summary>
        public LauncherManifest Manifest { get; }

        /// <summary>Id of the key whose signature verified.</summary>
        public string KeyId { get; }

        /// <summary>Shortcut for <c>Manifest.Serial</c>.</summary>
        public long Serial => Manifest.Serial;

        /// <summary>The exact verified manifest bytes (copy).</summary>
        public byte[] GetManifestBytes() => (byte[])_manifestBytes.Clone();

        /// <summary>The exact signature file bytes (copy).</summary>
        public byte[] GetSignatureBytes() => (byte[])_signatureBytes.Clone();
    }
}
