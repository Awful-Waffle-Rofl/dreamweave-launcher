using System;
using System.IO;
using System.Linq;
using System.Text;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Manifests;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DreamWeave.Launcher.Tests
{
    /// <summary>One row per ManifestParser.Validate rule: each mutates the (valid) sample and must be rejected as ManifestInvalid.</summary>
    [TestClass]
    public class ManifestValidationTests
    {
        private static SigningKey _key;

        [ClassInitialize]
        public static void Init(TestContext context)
        {
            _key = SigningKey.Generate();
        }

        private static JObject Sample()
        {
            return JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "manifest.sample.json")));
        }

        private static JObject Component(JObject root, string id)
        {
            return root["components"].Children<JObject>().Single(c => (string)c["id"] == id);
        }

        private static LauncherManifest Verify(byte[] bytes)
        {
            return new ManifestVerifier(new[] { _key.PublicKey }).Verify(bytes, _key.Sign(bytes).ToFileBytes(), 0).Manifest;
        }

        private static byte[] Bytes(JObject root)
        {
            return new UTF8Encoding(false).GetBytes(root.ToString(Formatting.Indented));
        }

        private static void Mutate(JObject m, string rule)
        {
            JObject installer = Component(m, "ac-client");
            JObject zip = Component(m, "eor-dats");
            JObject patch = Component(m, "palette-leak-fix");
            switch (rule)
            {
                case "server-missing": m.Remove("server"); break;
                case "host-empty": m["server"]["host"] = " "; break;
                case "port-zero": m["server"]["port"] = 0; break;
                case "port-too-high": m["server"]["port"] = 65536; break;
                case "launcher-url-without-sha": m["launcher"]["url"] = "https://example.test/l.exe"; break;
                case "launcher-sha-bad": m["launcher"]["url"] = "https://example.test/l.exe"; m["launcher"]["sha256"] = "ABC"; break;
                case "component-sha-bad": installer["sha256"] = new string('Z', 64); break;
                case "component-sha-short": zip["sha256"] = "66E9"; break;
                case "file-sha-bad": zip["files"][0]["sha256"] = ""; break;
                case "satisfied-sha-bad": installer["satisfiedWhen"][0]["sha256"][1] = "nope"; break;
                case "satisfied-no-hashes": installer["satisfiedWhen"][0]["sha256"] = new JArray(); break;
                case "input-sha-bad": patch["inputSha256"] = ""; break;
                case "output-sha-bad": patch["outputSha256"] = new string('1', 63); break;
                case "installer-no-url": installer["url"] = ""; break;
                case "zip-no-url": zip.Remove("url"); break;
                case "patch-length-differs": patch["patches"][0]["replace"] = "9090"; break;
                case "patch-empty": patch["patches"][0]["expect"] = ""; patch["patches"][0]["replace"] = ""; break;
                case "patch-not-hex": patch["patches"][0]["expect"] = "GG4024"; break;
                case "patch-negative-offset": patch["patches"][0]["offset"] = -1; break;
                case "patch-none": patch["patches"] = new JArray(); break;
                case "target-missing": patch.Remove("target"); break;
                case "path-rooted": zip["files"][0]["path"] = "\\Windows\\client_cell_1.dat"; break;
                case "path-drive": patch["target"] = "C:acclient.exe"; break;
                case "path-dotdot": installer["satisfiedWhen"][0]["path"] = "..\\acclient.exe"; break;
                case "path-dotdot-forward": zip["files"][1]["path"] = "sub/../../x.dat"; break;
                case "path-empty-segment": zip["files"][2]["path"] = "a//b.dat"; break;
                case "serial-fraction": m["serial"] = 1.5; break;
                case "serial-string": m["serial"] = "1"; break;
                case "serial-zero": m["serial"] = 0; break;
                default: throw new ArgumentException("unknown rule " + rule);
            }
        }

        [TestMethod]
        [DataRow("server-missing")]
        [DataRow("host-empty")]
        [DataRow("port-zero")]
        [DataRow("port-too-high")]
        [DataRow("launcher-url-without-sha")]
        [DataRow("launcher-sha-bad")]
        [DataRow("component-sha-bad")]
        [DataRow("component-sha-short")]
        [DataRow("file-sha-bad")]
        [DataRow("satisfied-sha-bad")]
        [DataRow("satisfied-no-hashes")]
        [DataRow("input-sha-bad")]
        [DataRow("output-sha-bad")]
        [DataRow("installer-no-url")]
        [DataRow("zip-no-url")]
        [DataRow("patch-length-differs")]
        [DataRow("patch-empty")]
        [DataRow("patch-not-hex")]
        [DataRow("patch-negative-offset")]
        [DataRow("patch-none")]
        [DataRow("target-missing")]
        [DataRow("path-rooted")]
        [DataRow("path-drive")]
        [DataRow("path-dotdot")]
        [DataRow("path-dotdot-forward")]
        [DataRow("path-empty-segment")]
        [DataRow("serial-fraction")]
        [DataRow("serial-string")]
        [DataRow("serial-zero")]
        public void Rule_RejectsAsManifestInvalid(string rule)
        {
            JObject m = Sample();
            Mutate(m, rule);

            var ex = Assert.ThrowsExactly<LauncherException>(() => Verify(Bytes(m)));
            Assert.AreEqual(LauncherError.ManifestInvalid, ex.Code, rule + ": " + ex.Detail);
        }

        [TestMethod]
        public void Control_UnmutatedSampleRoundTripPasses()
        {
            // Positive control: the same serialize-and-sign path accepts the unmodified sample,
            // so every rejection above is caused by its mutation.
            Assert.AreEqual(3, Verify(Bytes(Sample())).Components.Count);
        }

        [TestMethod]
        public void LauncherShaMayBeEmpty_OnlyWhileUrlIsEmpty()
        {
            JObject m = Sample();
            Assert.AreEqual(string.Empty, (string)m["launcher"]["url"]);
            Assert.AreEqual(string.Empty, (string)m["launcher"]["sha256"]);
            Verify(Bytes(m));

            m["launcher"]["url"] = "https://example.test/l.exe";
            m["launcher"]["sha256"] = new string('C', 64);
            Verify(Bytes(m));
        }

        [TestMethod]
        public void OverflowSerial_IsInvalid_OverflowSchema_IsUnsupported()
        {
            var serialEx = Assert.ThrowsExactly<LauncherException>(() => Verify(TestManifests.BuildRaw("99999999999999999999999", "1")));
            Assert.AreEqual(LauncherError.ManifestInvalid, serialEx.Code);

            var schemaEx = Assert.ThrowsExactly<LauncherException>(() => Verify(TestManifests.BuildRaw("1", "99999999999999999999999")));
            Assert.AreEqual(LauncherError.ManifestSchemaUnsupported, schemaEx.Code);
        }

        [TestMethod]
        public void UnknownKindComponents_AreNotValidated_ButRequiredOnesAreListed()
        {
            JObject m = Sample();
            ((JArray)m["components"]).Add(JObject.Parse("{ \"id\": \"future\", \"kind\": \"teleporter\", \"required\": true, \"sha256\": \"not-a-hash\" }"));
            ((JArray)m["components"]).Add(JObject.Parse("{ \"id\": \"future-optional\", \"kind\": \"teleporter\", \"required\": false }"));

            LauncherManifest parsed = Verify(Bytes(m));

            CollectionAssert.AreEqual(new[] { "future" }, parsed.SkippedRequired.ToArray());
            Assert.AreEqual(2, parsed.Warnings.Count);
            Assert.AreEqual(3, parsed.Components.Count);
        }
    }
}
