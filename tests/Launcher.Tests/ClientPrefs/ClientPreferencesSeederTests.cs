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
        public void Seeds_WhenDirectoryExistsButFileAbsent()
        {
            // Pins that the guard is on the FILE, not the directory: a prior run (or the client itself)
            // may have created the folder and put unrelated files in it with no preferences file yet.
            using (var dir = new TempDir())
            {
                string subdir = Path.Combine(dir.Path, "Asheron's Call");
                Directory.CreateDirectory(subdir);
                File.WriteAllText(Path.Combine(subdir, "ac.log"), "unrelated");

                ClientPreferencesSeedResult result = new ClientPreferencesSeeder(dir.Path).Seed();

                Assert.AreEqual(ClientPreferencesSeedStatus.Seeded, result.Status, "the folder existing must not count as the file existing");
                CollectionAssert.AreEqual(ClientPreferencesSeeder.DefaultPreferenceBytes(), File.ReadAllBytes(Target(dir.Path)));
                Assert.IsTrue(File.Exists(Path.Combine(subdir, "ac.log")), "the unrelated file is untouched");
            }
        }

        [TestMethod]
        public void RealFileSystem_WriteNew_RefusesExistingFile_WithoutTruncating()
        {
            // The structural guard: the real seam never overwrites or truncates, even when the fast-path
            // existence check is bypassed entirely.
            using (var dir = new TempDir())
            {
                string target = Target(dir.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                byte[] existing = Encoding.ASCII.GetBytes("FullScreen=True original\r\n");
                File.WriteAllBytes(target, existing);

                var fs = new ClientPreferencesFileSystem();
                Assert.ThrowsExactly<IOException>(() => fs.WriteNew(target, new byte[] { 1, 2, 3 }));
                CollectionAssert.AreEqual(existing, File.ReadAllBytes(target), "the existing file was neither overwritten nor truncated");
            }
        }

        [TestMethod]
        public void WriteRaceLostAfterFastPath_ReportedAsAlreadyPresent_NotFailed()
        {
            // The fast-path says absent, but the exclusive create loses a race and throws "already exists":
            // that is AlreadyPresent (nothing was changed), not a failure.
            var fs = new ThrowingFileSystem
            {
                OnExists = () => false,
                OnWrite = () => throw new IOException("the file already exists", unchecked((int)0x80070050)),
            };
            var seeder = new ClientPreferencesSeeder(@"C:\Users\p\Documents", fs);

            ClientPreferencesSeedResult result = seeder.Seed();

            Assert.AreEqual(ClientPreferencesSeedStatus.AlreadyPresent, result.Status);
            Assert.IsNull(result.Error);
        }

        [TestMethod]
        public void BadDocumentsPath_ReturnsFailure_WithoutThrowing()
        {
            // Path.Combine throws on an invalid path on net48; it must surface as Failed, never an exception.
            ClientPreferencesSeedResult result = new ClientPreferencesSeeder("C:\\bad|dir").Seed();

            Assert.AreEqual(ClientPreferencesSeedStatus.Failed, result.Status);
            Assert.IsNotNull(result.Error);
        }

        [TestMethod]
        public void Template_HasExpectedLineCount_AndSectionOrderIntact()
        {
            string text = Encoding.ASCII.GetString(ClientPreferencesSeeder.DefaultPreferenceBytes());

            // 52 CRLF-terminated lines (a trailing CRLF, so Split yields 52 lines then one empty tail).
            string[] parts = text.Split(new[] { "\r\n" }, StringSplitOptions.None);
            Assert.AreEqual(53, parts.Length, "52 lines plus the empty tail after the trailing CRLF");
            Assert.AreEqual(string.Empty, parts[52], "the file ends with a trailing CRLF");

            string[] sectionsInOrder = { "[Render]", "[Net]", "[Sound]", "[Input]", "[Camera]", "[Misc]", "[UI]", "[Display]", "[International]" };
            int at = -1;
            foreach (string section in sectionsInOrder)
            {
                int found = text.IndexOf(section + "\r\n", StringComparison.Ordinal);
                Assert.IsTrue(found > at, "section " + section + " is present and after the previous one");
                at = found;
            }

            StringAssert.Contains(text, "[Display]\r\nRefreshRate=Auto\r\nResolution=1024x768\r\nFullScreen=False\r\nSyncToRefresh=False\r\n");
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

        /// <summary>A file-system seam whose create (or directory create) throws on demand.</summary>
        private sealed class ThrowingFileSystem : IClientPreferencesFileSystem
        {
            public Func<bool> OnExists { get; set; }

            public Action OnCreateDirectory { get; set; }

            public Action OnWrite { get; set; }

            public bool FileExists(string path) => OnExists != null && OnExists();

            public void CreateDirectory(string path) => OnCreateDirectory?.Invoke();

            public void WriteNew(string path, byte[] contents) => OnWrite?.Invoke();
        }
    }
}
