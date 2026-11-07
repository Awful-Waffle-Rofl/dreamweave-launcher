using System;
using System.Linq;
using System.Text;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Manifests;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests
{
    [TestClass]
    public class ManifestVerifierTests
    {
        private static SigningKey _key;
        private static SigningKey _otherKey;

        [ClassInitialize]
        public static void Init(TestContext context)
        {
            _key = SigningKey.Generate();
            _otherKey = SigningKey.Generate();
        }

        private static ManifestVerifier Trusting(params SigningKey[] keys)
        {
            return new ManifestVerifier(keys.Select(k => k.PublicKey));
        }

        private static LauncherError CodeOf(Action action)
        {
            var ex = Assert.ThrowsExactly<LauncherException>(action);
            return ex.Code;
        }

        [TestMethod]
        public void SignVerify_RoundTrip()
        {
            byte[] manifest = TestManifests.Build(5);
            byte[] sig = _key.Sign(manifest).ToFileBytes();

            VerifiedManifest v = Trusting(_key).Verify(manifest, sig, 0);

            Assert.AreEqual(5L, v.Serial);
            Assert.AreEqual(_key.PublicKey.KeyId, v.KeyId);
            Assert.AreEqual("play.example.test", v.Manifest.Server.Host);
            Assert.AreEqual(1, v.Manifest.Components.Count);
            CollectionAssert.AreEqual(manifest, v.GetManifestBytes());
        }

        [TestMethod]
        public void OneByteTamper_IsRejected()
        {
            byte[] manifest = TestManifests.Build(5);
            byte[] sig = _key.Sign(manifest).ToFileBytes();
            int at = Encoding.ASCII.GetString(manifest).IndexOf("9000", StringComparison.Ordinal);
            manifest[at] = (byte)'8';

            Assert.AreEqual(LauncherError.ManifestSignatureInvalid, CodeOf(() => Trusting(_key).Verify(manifest, sig, 0)));
        }

        [TestMethod]
        public void TrailingNewlineChange_IsRejected()
        {
            // Exact bytes: even a line-ending change must invalidate the signature.
            byte[] manifest = TestManifests.Build(5);
            byte[] sig = _key.Sign(manifest).ToFileBytes();
            byte[] crlf = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(manifest).Replace("\n", "\r\n"));

            Assert.AreEqual(LauncherError.ManifestSignatureInvalid, CodeOf(() => Trusting(_key).Verify(crlf, sig, 0)));
        }

        [TestMethod]
        public void WrongKey_IsRejected()
        {
            // Signed by _otherKey but claiming _key's id.
            byte[] manifest = TestManifests.Build(5);
            byte[] raw = _otherKey.Sign(manifest).GetSignatureBytes();
            byte[] sig = new ManifestSignature(_key.PublicKey.KeyId, raw).ToFileBytes();

            Assert.AreEqual(LauncherError.ManifestSignatureInvalid, CodeOf(() => Trusting(_key).Verify(manifest, sig, 0)));
        }

        [TestMethod]
        public void UnknownKeyId_IsRejected()
        {
            byte[] manifest = TestManifests.Build(5);
            byte[] sig = _otherKey.Sign(manifest).ToFileBytes();

            Assert.AreEqual(LauncherError.ManifestSignatureInvalid, CodeOf(() => Trusting(_key).Verify(manifest, sig, 0)));
        }

        [TestMethod]
        public void EmptyTrustedSet_FailsClosed()
        {
            byte[] manifest = TestManifests.Build(5);
            byte[] sig = _key.Sign(manifest).ToFileBytes();

            Assert.AreEqual(LauncherError.ManifestSignatureInvalid, CodeOf(() => new ManifestVerifier(new TrustedKey[0]).Verify(manifest, sig, 0)));
        }

        [TestMethod]
        public void ProductionSet_IsEmptyAndFailsClosed()
        {
            byte[] manifest = TestManifests.Build(5);
            byte[] sig = _key.Sign(manifest).ToFileBytes();
            ManifestVerifier production = ManifestVerifier.CreateProduction();

            Assert.AreEqual(0, production.TrustedKeyCount);
            Assert.AreEqual(LauncherError.ManifestSignatureInvalid, CodeOf(() => production.Verify(manifest, sig, 0)));
        }

        [TestMethod]
        public void LowerSerial_IsRollback_EqualIsAccepted()
        {
            byte[] manifest = TestManifests.Build(5);
            byte[] sig = _key.Sign(manifest).ToFileBytes();
            ManifestVerifier verifier = Trusting(_key);

            Assert.AreEqual(LauncherError.ManifestRollback, CodeOf(() => verifier.Verify(manifest, sig, 6)));
            Assert.AreEqual(5L, verifier.Verify(manifest, sig, 5).Serial);
        }

        [TestMethod]
        public void UnknownSchema_IsSchemaUnsupported()
        {
            byte[] manifest = TestManifests.Build(5, schema: 2);
            byte[] sig = _key.Sign(manifest).ToFileBytes();

            Assert.AreEqual(LauncherError.ManifestSchemaUnsupported, CodeOf(() => Trusting(_key).Verify(manifest, sig, 0)));
        }

        [TestMethod]
        public void UnsignedGarbage_IsSignatureInvalidNotParseError()
        {
            // Parse only after the signature verifies: garbage with a bad signature must report the signature.
            byte[] garbage = Encoding.ASCII.GetBytes("{ not json");
            byte[] sig = _key.Sign(TestManifests.Build(5)).ToFileBytes();

            Assert.AreEqual(LauncherError.ManifestSignatureInvalid, CodeOf(() => Trusting(_key).Verify(garbage, sig, 0)));
        }

        [TestMethod]
        public void UnknownFields_AreIgnored_UnknownKindsSkippedWithWarning()
        {
            byte[] manifest = TestManifests.Build(
                7,
                extraTopLevel: "\"futureField\": { \"nested\": [1, 2, 3] },\n  \"components2\": 1,\n  \"links\": { \"help\": \"https://example.test/help\", \"newLink\": \"x\" }");
            string text = Encoding.UTF8.GetString(manifest).Replace(
                "\"sha256\": \"" + new string('A', 64) + "\" }",
                "\"sha256\": \"" + new string('A', 64) + "\", \"futureComponentField\": true },\n    { \"id\": \"hologram\", \"kind\": \"teleporter\", \"required\": false }");
            manifest = Encoding.UTF8.GetBytes(text);
            byte[] sig = _key.Sign(manifest).ToFileBytes();

            VerifiedManifest v = Trusting(_key).Verify(manifest, sig, 0);

            Assert.AreEqual(7L, v.Serial);
            Assert.AreEqual("https://example.test/help", v.Manifest.Links.Help);
            Assert.AreEqual(1, v.Manifest.Components.Count);
            Assert.AreEqual("ac-client", v.Manifest.Components[0].Id);
            Assert.AreEqual(1, v.Manifest.Warnings.Count);
            StringAssert.Contains(v.Manifest.Warnings[0], "teleporter");
        }

        [TestMethod]
        public void MissingLaunch_DefaultsToVerifiedArgTemplate()
        {
            byte[] manifest = TestManifests.Build(5);
            VerifiedManifest v = Trusting(_key).Verify(manifest, _key.Sign(manifest).ToFileBytes(), 0);

            Assert.AreEqual("-a \"{user}\" -v \"{pass}\" -h {host}:{port} -rodat on", v.Manifest.Launch.ArgTemplate);
        }

        [TestMethod]
        public void WrongAlgorithm_IsRejected()
        {
            byte[] manifest = TestManifests.Build(5);
            string sigText = Encoding.UTF8.GetString(_key.Sign(manifest).ToFileBytes()).Replace("ES256-P1363", "ES256-DER");

            Assert.AreEqual(LauncherError.ManifestSignatureInvalid, CodeOf(() => Trusting(_key).Verify(manifest, Encoding.UTF8.GetBytes(sigText), 0)));
        }

        [TestMethod]
        public void TrustedKey_RejectsMismatchedKeyId_AndRoundTripsText()
        {
            TrustedKey pub = _key.PublicKey;
            Assert.AreEqual(pub.KeyId, TrustedKey.Parse(pub.ToPublicString()).KeyId);
            StringAssert.StartsWith(pub.KeyId, "dw-");
            Assert.AreEqual(19, pub.KeyId.Length);

            string mismatched = _otherKey.PublicKey.KeyId + pub.ToPublicString().Substring(pub.KeyId.Length);
            Assert.ThrowsExactly<ArgumentException>(() => TrustedKey.Parse(mismatched));
        }

        [TestMethod]
        public void SigningKey_JsonRoundTrip_SignsVerifiably()
        {
            SigningKey reloaded = SigningKey.FromJson(_key.ToJson());
            byte[] manifest = TestManifests.Build(9);

            Assert.AreEqual(_key.PublicKey.KeyId, reloaded.PublicKey.KeyId);
            Assert.AreEqual(9L, Trusting(_key).Verify(manifest, reloaded.Sign(manifest).ToFileBytes(), 0).Serial);
        }
    }
}
