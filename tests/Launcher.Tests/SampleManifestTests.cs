using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Manifests;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace DreamWeave.Launcher.Tests
{
    [TestClass]
    public class SampleManifestTests
    {
        private static byte[] SampleBytes()
        {
            return File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "manifest.sample.json"));
        }

        private static LauncherManifest ParseSample()
        {
            // The sample is unsigned; sign it with a throwaway key so it goes through the real gate.
            byte[] bytes = SampleBytes();
            SigningKey key = SigningKey.Generate();
            return new ManifestVerifier(new[] { key.PublicKey }).Verify(bytes, key.Sign(bytes).ToFileBytes(), 0).Manifest;
        }

        [TestMethod]
        public void Sample_ParsesWithAllComponentsAndNoWarnings()
        {
            LauncherManifest m = ParseSample();

            Assert.AreEqual(1, m.Schema);
            Assert.AreEqual("play.acdreamweave.com", m.Server.Host);
            Assert.AreEqual(9000, m.Server.Port);
            Assert.AreEqual(LaunchInfo.DefaultArgTemplate, m.Launch.ArgTemplate);
            CollectionAssert.AreEqual(new[] { "ac-client", "eor-dats", "palette-leak-fix" }, m.Components.Select(c => c.Id).ToArray());
            Assert.AreEqual(0, m.Warnings.Count);
            Assert.AreEqual(new DateTimeOffset(2026, 11, 7, 0, 0, 0, TimeSpan.Zero), m.IssuedUtc);
        }

        [TestMethod]
        public void Sample_EveryComponentSha256Is64Hex()
        {
            JObject root = JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "manifest.sample.json")));
            var values = new List<string>();
            foreach (JProperty p in ((JContainer)root["components"]).Descendants().OfType<JProperty>().Where(p => p.Name.IndexOf("sha256", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                if (p.Value.Type == JTokenType.Array)
                {
                    values.AddRange(p.Value.Values<string>());
                }
                else
                {
                    values.Add((string)p.Value);
                }
            }

            // ac-client: 1 + 2 satisfiedWhen; eor-dats: 1 + 4 files; palette-leak-fix: input + output.
            Assert.AreEqual(10, values.Count, "expected 10 component-level hash values");
            foreach (string v in values)
            {
                Assert.IsTrue(Hasher.IsSha256Hex(v), "not 64 hex: '" + v + "'");
            }

            // The launcher block names the page where players get the latest setup; the old url/sha256/size members are gone.
            LauncherUpdateInfo launcher = ParseSample().Launcher;
            Assert.AreEqual("https://github.com/Awful-Waffle-Rofl/dreamweave-launcher/releases/latest", launcher.DownloadPage);
            Assert.IsTrue(LauncherUpdateInfo.IsValidDownloadPage(launcher.DownloadPage));
            Assert.IsNull(root["launcher"]["url"], "the sample must not advertise the obsolete self-update fields");
        }

        [TestMethod]
        public void Sample_PaletteFix_IsOptionalDefaultOn_AtVerifiedFileOffsets()
        {
            ManifestComponent fix = ParseSample().Components.Single(c => c.Id == "palette-leak-fix");

            Assert.AreEqual(ComponentKinds.BytePatch, fix.Kind);
            Assert.IsFalse(fix.Required);
            Assert.IsTrue(fix.DefaultSelected, "owner ruling 2026-11-07: the palette fix defaults to checked");
            Assert.AreEqual(2, fix.Patches.Count);
            Assert.AreEqual(0x13effeL, fix.Patches[0].Offset);
            Assert.AreEqual(0x13f19cL, fix.Patches[1].Offset);
            CollectionAssert.AreEqual(new byte[] { 0xFF, 0x40, 0x24 }, fix.Patches[0].GetExpectBytes());
            CollectionAssert.AreEqual(new byte[] { 0xFF, 0x46, 0x24 }, fix.Patches[1].GetExpectBytes());
            foreach (BytePatch p in fix.Patches)
            {
                CollectionAssert.AreEqual(new byte[] { 0x90, 0x90, 0x90 }, p.GetReplaceBytes());
            }
        }

        [TestMethod]
        public void Sample_HasNoCarriageReturnsOrBom()
        {
            byte[] bytes = SampleBytes();
            Assert.IsFalse(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "BOM");
            Assert.IsFalse(bytes.Contains((byte)'\r'), "CR found: signed manifests are compared byte-for-byte, keep LF");
        }
    }
}
