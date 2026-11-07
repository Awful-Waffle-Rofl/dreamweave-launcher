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
        [DataRow("play acdreamweave.com")]
        [DataRow("\"play.acdreamweave.com\"")]
        [DataRow("play.acdreamweave.com:9000")]
        [DataRow("http://play.acdreamweave.com")]
        [DataRow("::1")]
        [DataRow("2001:db8::1")]
        [DataRow("[::1]")]
        [DataRow("[2001:db8::1]")]
        [DataRow("[::1%3]")]
        [DataRow("[zz::1]")]
        [DataRow("[1.2.3.4]")]
        [DataRow("-bad.example")]
        [DataRow("bad-.example")]
        [DataRow("a..b")]
        [DataRow("trailing.dot.")]
        [DataRow("under_score.example")]
        [DataRow("1.2.3")]
        [DataRow("256.1.1.1")]
        [DataRow("01.2.3.4")]
        [DataRow("1234.1.1.1")]
        [DataRow("1.2.3.4.5")]
        [DataRow("123")]
        [DataRow("host\tname")]
        [DataRow("play.acdreamweave.com -x")]
        public void ServerHost_Invalid_IsRejected(string host)
        {
            JObject m = Sample();
            m["server"]["host"] = host;

            var ex = Assert.ThrowsExactly<LauncherException>(() => Verify(Bytes(m)));
            Assert.AreEqual(LauncherError.ManifestInvalid, ex.Code, host + ": " + ex.Detail);
        }

        [TestMethod]
        [DataRow("play.acdreamweave.com")]
        [DataRow("localhost")]
        [DataRow("xn--bcher-kva.example")]
        [DataRow("127.0.0.1")]
        [DataRow("0.0.0.0")]
        public void ServerHost_Valid_IsAccepted(string host)
        {
            JObject m = Sample();
            m["server"]["host"] = host;

            Assert.AreEqual(host, Verify(Bytes(m)).Server.Host);
        }

        [TestMethod]
        public void ServerHost_LengthBoundaries()
        {
            string label63 = new string('a', 63);
            string label64 = new string('a', 64);
            // 63 + 1 + 63 + 1 + 63 + 1 + 61 = 253 characters; one more = 254.
            string host253 = label63 + "." + label63 + "." + label63 + "." + new string('b', 61);
            string host254 = label63 + "." + label63 + "." + label63 + "." + new string('b', 62);
            Assert.AreEqual(253, host253.Length);
            Assert.AreEqual(254, host254.Length);

            Assert.IsTrue(ManifestParser.IsValidServerHost(label63 + ".example"), "63-char label");
            Assert.IsFalse(ManifestParser.IsValidServerHost(label64 + ".example"), "64-char label");
            Assert.IsTrue(ManifestParser.IsValidServerHost(host253), "253-char host");
            Assert.IsFalse(ManifestParser.IsValidServerHost(host254), "254-char host");

            // The same boundaries through the full signed verify path.
            JObject ok = Sample();
            ok["server"]["host"] = host253;
            Assert.AreEqual(host253, Verify(Bytes(ok)).Server.Host);
            JObject bad = Sample();
            bad["server"]["host"] = label64 + ".example";
            Assert.AreEqual(LauncherError.ManifestInvalid, Assert.ThrowsExactly<LauncherException>(() => Verify(Bytes(bad))).Code);
        }

        [TestMethod]
        [DataRow("CON")]
        [DataRow("prn.dat")]
        [DataRow("sub/AUX")]
        [DataRow("nul.txt")]
        [DataRow("COM0")]
        [DataRow("com9.dat")]
        [DataRow("LPT0")]
        [DataRow("lpt5.log")]
        [DataRow("COM\u00B9")]
        [DataRow("LPT\u00B3.dat")]
        [DataRow("CONIN$")]
        [DataRow("dir./file.dat")]
        [DataRow("file.dat.")]
        [DataRow("file.dat ")]
        [DataRow("dir /file.dat")]
        [DataRow("*.dat")]
        [DataRow("file?.dat")]
        [DataRow("file.dat:stream")]
        [DataRow("a<b.dat")]
        public void FilePath_Unsafe_IsRejected(string path)
        {
            JObject m = Sample();
            Component(m, "eor-dats")["files"][0]["path"] = path;

            var ex = Assert.ThrowsExactly<LauncherException>(() => Verify(Bytes(m)));
            Assert.AreEqual(LauncherError.ManifestInvalid, ex.Code, path + ": " + ex.Detail);
        }

        [TestMethod]
        [DataRow("client_cell_1.dat")]
        [DataRow("sub/dir/file.dat")]
        [DataRow("CONSOLE.dat")]
        [DataRow("COM10.dat")]
        [DataRow("console")]
        [DataRow("lpt.dat")]
        public void FilePath_SafeLookalikes_AreAccepted(string path)
        {
            JObject m = Sample();
            Component(m, "eor-dats")["files"][0]["path"] = path;

            Assert.AreEqual(path, Verify(Bytes(m)).Components.Single(c => c.Id == "eor-dats").Files[0].Path);
        }

        [TestMethod]
        public void TargetAndSatisfiedWhenPaths_UseTheSameRules()
        {
            JObject a = Sample();
            Component(a, "palette-leak-fix")["target"] = "NUL";
            Assert.AreEqual(LauncherError.ManifestInvalid, Assert.ThrowsExactly<LauncherException>(() => Verify(Bytes(a))).Code);

            JObject b = Sample();
            Component(b, "ac-client")["satisfiedWhen"][0]["path"] = "acclient.exe.";
            Assert.AreEqual(LauncherError.ManifestInvalid, Assert.ThrowsExactly<LauncherException>(() => Verify(Bytes(b))).Code);
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
