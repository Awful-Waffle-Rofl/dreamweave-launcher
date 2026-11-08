using System;
using System.IO;
using System.Text;
using DreamWeave.Launcher.Core.ClientPrefs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.ClientPrefs
{
    [TestClass]
    public class ClientPreferencesSeederTests
    {
        private static string Target(string documents)
        {
            return Path.Combine(documents, "Asheron's Call", "UserPreferences.ini");
        }

        [TestMethod]
        public void TargetPath_IsDocumentsAsheronsCallUserPreferencesIni()
        {
            var seeder = new ClientPreferencesSeeder(@"C:\Users\p\Documents");
            Assert.AreEqual(@"C:\Users\p\Documents\Asheron's Call\UserPreferences.ini", seeder.TargetPath);
        }

        [TestMethod]
        public void Seeds_WhenAbsent_WritesWindowedTemplate_CrlfNoBom()
        {
            using (var dir = new TempDir())
            {
                var seeder = new ClientPreferencesSeeder(dir.Path);
                ClientPreferencesSeedResult result = seeder.Seed();

                Assert.AreEqual(ClientPreferencesSeedStatus.Seeded, result.Status);
                string target = Target(dir.Path);
                Assert.AreEqual(target, result.TargetPath);
                Assert.IsTrue(File.Exists(target), "the preferences file was written");

                byte[] bytes = File.ReadAllBytes(target);

                // No byte-order mark: the first bytes are the first section header.
                Assert.IsFalse(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "no UTF-8 BOM");
                Assert.AreEqual((byte)'[', bytes[0], "starts at the first section header");

                string text = Encoding.ASCII.GetString(bytes);

                // Every newline is a CRLF: stripping CRLF leaves no stray CR or LF behind.
                string withoutCrlf = text.Replace("\r\n", string.Empty);
                Assert.IsFalse(withoutCrlf.Contains("\n"), "no lone LF");
                Assert.IsFalse(withoutCrlf.Contains("\r"), "no lone CR");

                StringAssert.Contains(text, "[Display]\r\n");
                StringAssert.Contains(text, "FullScreen=False");
                Assert.IsFalse(text.Contains("FullScreen=True"), "the default is windowed, not full-screen");

                // The written bytes are exactly the captured template.
                CollectionAssert.AreEqual(ClientPreferencesSeeder.DefaultPreferenceBytes(), bytes, "content equals the template");
                Assert.AreEqual(ClientPreferencesSeeder.DefaultPreferences, text);
            }
        }

        [TestMethod]
        public void DoesNothing_WhenPresent_LeavesFileByteForByteUnchanged()
        {
            using (var dir = new TempDir())
            {
                string target = Target(dir.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target));

                // A returning player's file, with the opposite setting: it must survive untouched.
                byte[] existing = Encoding.ASCII.GetBytes("[Display]\r\nFullScreen=True\r\nResolution=1920x1080\r\n");
                File.WriteAllBytes(target, existing);

                var seeder = new ClientPreferencesSeeder(dir.Path);
                ClientPreferencesSeedResult result = seeder.Seed();

                Assert.AreEqual(ClientPreferencesSeedStatus.AlreadyPresent, result.Status);
                Assert.AreEqual(target, result.TargetPath);
                CollectionAssert.AreEqual(existing, File.ReadAllBytes(target), "an existing file is never overwritten or merged");
            }
        }

        [TestMethod]
        public void Seeds_CreatesTheAsheronsCallSubdirectory_WhenMissing()
        {
            using (var dir = new TempDir())
            {
                string subdir = Path.Combine(dir.Path, "Asheron's Call");
                Assert.IsFalse(Directory.Exists(subdir), "the subfolder does not exist yet");

                ClientPreferencesSeedResult result = new ClientPreferencesSeeder(dir.Path).Seed();

                Assert.AreEqual(ClientPreferencesSeedStatus.Seeded, result.Status);
                Assert.IsTrue(Directory.Exists(subdir), "the Asheron's Call subfolder was created");
                Assert.IsTrue(File.Exists(Target(dir.Path)));
            }
        }

        [TestMethod]
        public void IoFailure_ReturnsFailure_AndDoesNotThrow()
        {
            var fs = new ThrowingFileSystem { OnWrite = () => throw new UnauthorizedAccessException("denied") };
            var seeder = new ClientPreferencesSeeder(@"C:\Users\p\Documents", fs);

            ClientPreferencesSeedResult result = seeder.Seed();

            Assert.AreEqual(ClientPreferencesSeedStatus.Failed, result.Status);
            Assert.IsInstanceOfType(result.Error, typeof(UnauthorizedAccessException));
            Assert.AreEqual(@"C:\Users\p\Documents\Asheron's Call\UserPreferences.ini", result.TargetPath);
        }

        [TestMethod]
        public void Logs_OneLine_ForEachOutcome()
        {
            using (var dir = new TempDir())
            {
                var lines = new System.Collections.Generic.List<string>();
                var seeder = new ClientPreferencesSeeder(dir.Path, null, lines.Add);

                Assert.AreEqual(ClientPreferencesSeedStatus.Seeded, seeder.Seed().Status);
                Assert.AreEqual(ClientPreferencesSeedStatus.AlreadyPresent, seeder.Seed().Status);

                Assert.AreEqual(2, lines.Count, "one log line per attempt");
                StringAssert.Contains(lines[0], "seeded");
                StringAssert.Contains(lines[1], "already present");
            }
        }

        /// <summary>A file-system seam whose write (or directory create) throws on demand.</summary>
        private sealed class ThrowingFileSystem : IClientPreferencesFileSystem
        {
            public Func<bool> OnExists { get; set; }

            public Action OnCreateDirectory { get; set; }

            public Action OnWrite { get; set; }

            public bool FileExists(string path) => OnExists != null && OnExists();

            public void CreateDirectory(string path) => OnCreateDirectory?.Invoke();

            public void WriteAllBytes(string path, byte[] contents) => OnWrite?.Invoke();
        }
    }
}
