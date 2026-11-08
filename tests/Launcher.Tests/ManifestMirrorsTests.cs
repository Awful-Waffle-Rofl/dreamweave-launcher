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
    /// <summary>The optional <c>mirrors</c> array on a component (additive under schema 1).</summary>
    [TestClass]
    public class ManifestMirrorsTests
    {
        private static JObject Sample()
        {
            return JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "manifest.sample.json")));
        }

        private static JObject Installer(JObject root)
        {
            return root["components"].Children<JObject>().Single(c => (string)c["id"] == "ac-client");
        }

        private static LauncherManifest Parse(JObject root)
        {
            return ManifestParser.Parse(new UTF8Encoding(false).GetBytes(root.ToString(Formatting.Indented)));
        }

        private static JObject Mirror(string transport, string url, string label = "A mirror")
        {
            return new JObject { ["transport"] = transport, ["url"] = url, ["sourceLabel"] = label };
        }

        [TestMethod]
        public void ManifestWithoutMirrors_ParsesWithAnEmptyList()
        {
            JObject root = Sample();
            Installer(root).Remove("mirrors");
            ManifestComponent c = Parse(root).Components.Single(x => x.Id == "ac-client");
            Assert.AreEqual(0, c.Mirrors.Count);
        }

        [TestMethod]
        public void ValidMirrors_AreKeptInOrder()
        {
            JObject root = Sample();
            Installer(root)["mirrors"] = new JArray(
                Mirror("http", "https://example.test/a.exe", "First"),
                Mirror("mega", "https://mega.nz/file/AbCdEfGh#key_-1234567890", "Second"));
            ManifestComponent c = Parse(root).Components.Single(x => x.Id == "ac-client");
            CollectionAssert.AreEqual(new[] { "First", "Second" }, c.Mirrors.Select(m => m.SourceLabel).ToArray());
            Assert.AreEqual(Transports.Mega, c.Mirrors[1].Transport);
        }

        [TestMethod]
        [DataRow("http", "http://example.test/a.exe")]
        [DataRow("http", "ftp://example.test/a.exe")]
        [DataRow("http", "a.exe")]
        [DataRow("http", "")]
        [DataRow("mega", "http://mega.nz/file/AbCdEfGh#key")]
        [DataRow("mega", "https://example.test/file/AbCdEfGh#key")]
        [DataRow("mega", "https://mega.nz/folder/AbCdEfGh#key")]
        [DataRow("mega", "https://mega.nz/file/AbCdEfGh")]
        [DataRow("carrier-pigeon", "https://example.test/a.exe")]
        [DataRow("http", "https://user:pw@example.test/a.exe")]
        public void BadMirror_IsRejectedAsManifestInvalid(string transport, string url)
        {
            JObject root = Sample();
            Installer(root)["mirrors"] = new JArray(Mirror(transport, url));
            var ex = Assert.ThrowsExactly<LauncherException>(() => Parse(root));
            Assert.AreEqual(LauncherError.ManifestInvalid, ex.Code, ex.Detail);
        }
    }
}