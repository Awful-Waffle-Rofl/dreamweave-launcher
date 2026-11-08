using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.Cleanup
{
    /// <summary>
    /// installer/shipped-files.txt is the one list of files the build produces and the setup ships. These tests fail when
    /// the .iss [Files] section, check-build-output.ps1, build.yml or the real Launcher build output disagree with it.
    /// </summary>
    [TestClass]
    public class ShippedFilesTests
    {
        private static string RepoRoot()
        {
            string dir = AppContext.BaseDirectory;
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir, "DreamWeaveLauncher.sln")))
                {
                    return dir;
                }

                dir = Path.GetDirectoryName(dir);
            }

            Assert.Fail("Could not find the repository root by walking up from " + AppContext.BaseDirectory);
            return null;
        }

        private static string ReadRepoFile(params string[] parts)
        {
            string path = Path.Combine(RepoRoot(), Path.Combine(parts));
            Assert.IsTrue(File.Exists(path), "Missing " + path);
            return File.ReadAllText(path);
        }

        private sealed class Manifest
        {
            public List<string> Ship = new List<string>();
            public List<string> BuildOnly = new List<string>();
            public long MaxExeBytes;
            public long MaxTotalBytes;
        }

        private static Manifest Load()
        {
            var m = new Manifest();
            foreach (string raw in ReadRepoFile("installer", "shipped-files.txt").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                string[] parts = Regex.Split(line, @"\s+");
                Assert.AreEqual(2, parts.Length, "bad line: " + line);
                switch (parts[0])
                {
                    case "ship": m.Ship.Add(parts[1]); break;
                    case "build-only": m.BuildOnly.Add(parts[1]); break;
                    case "max-exe-bytes": m.MaxExeBytes = long.Parse(parts[1]); break;
                    case "max-total-bytes": m.MaxTotalBytes = long.Parse(parts[1]); break;
                    default: Assert.Fail("unknown keyword: " + line); break;
                }
            }

            return m;
        }

        [TestMethod]
        public void List_IsWellFormed()
        {
            Manifest m = Load();
            CollectionAssert.Contains(m.Ship, "DreamWeaveLauncher.exe");
            CollectionAssert.Contains(m.Ship, "DreamWeaveLauncher.exe.config");
            Assert.AreEqual(m.Ship.Count, m.Ship.Distinct(StringComparer.OrdinalIgnoreCase).Count(), "duplicate ship entry");
            Assert.AreEqual(0, m.Ship.Intersect(m.BuildOnly, StringComparer.OrdinalIgnoreCase).Count(), "a file is both ship and build-only");
            foreach (string name in m.Ship.Concat(m.BuildOnly))
            {
                Assert.IsTrue(Regex.IsMatch(name, @"^[A-Za-z0-9_.\-]+$"), "not a plain file name: " + name);
                Assert.IsFalse(name.Contains("*") || name.Contains("?"), "wildcard: " + name);
            }

            foreach (string name in m.Ship)
            {
                Assert.IsFalse(name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase), "never ship " + name);
            }

            Assert.IsTrue(m.MaxExeBytes > 0 && m.MaxTotalBytes >= m.MaxExeBytes, "size caps");
        }

        [TestMethod]
        public void Iss_FilesSection_HasOneExplicitSourceLinePerShippedFile_AndNothingElse()
        {
            Manifest m = Load();
            string iss = ReadRepoFile("installer", "DreamWeaveLauncher.iss");
            Match section = Regex.Match(iss, @"^\[Files\]\s*$(.*?)(?=^\[|\z)", RegexOptions.Multiline | RegexOptions.Singleline);
            Assert.IsTrue(section.Success, "missing [Files]");

            var sources = new List<string>();
            foreach (string line in section.Groups[1].Value.Split('\n'))
            {
                string entry = line.Trim();
                if (entry.Length == 0 || entry.StartsWith(";", StringComparison.Ordinal))
                {
                    continue;
                }

                Match mm = Regex.Match(entry, @"^Source:\s*""\{#ExeDir\}\\([^""\\]+)"";\s*DestDir:\s*""\{app\}"";\s*Flags:\s*ignoreversion$");
                Assert.IsTrue(mm.Success, "[Files] entry is not an explicit '{#ExeDir}\\<name>' line to {app}: " + entry);
                Assert.IsFalse(entry.Contains("*") || entry.Contains("?"), "wildcard in [Files]: " + entry);
                Assert.IsFalse(entry.Contains("recursesubdirs"), "recursesubdirs in [Files]: " + entry);
                sources.Add(mm.Groups[1].Value);
            }

            CollectionAssert.AreEquivalent(m.Ship, sources, "[Files] must list exactly the 'ship' entries of installer/shipped-files.txt");
            Assert.IsFalse(iss.IndexOf(".pdb", StringComparison.OrdinalIgnoreCase) >= 0, "the .iss must not mention a .pdb");
        }

        [TestMethod]
        public void CheckScriptAndWorkflows_ReadTheList_AndCarryNoCopyOfIt()
        {
            string script = ReadRepoFile("tools", "release", "check-build-output.ps1");
            StringAssert.Contains(script, "installer/shipped-files.txt");
            foreach (string forbidden in new[] { ".dll", ".pdb", ".config", "Newtonsoft", "Launcher.Core", "3145728" })
            {
                Assert.IsFalse(script.Contains(forbidden), "check-build-output.ps1 hard-codes " + forbidden);
            }

            foreach (string wf in new[] { "build.yml", "release.yml" })
            {
                string yml = ReadRepoFile(".github", "workflows", wf);
                StringAssert.Contains(yml, "check-build-output.ps1", wf);
                Assert.IsFalse(yml.Contains("-notcontains"), wf + " has an inline allowlist");
                Assert.IsFalse(yml.Contains("3145728"), wf + " has an inline size cap");
                Assert.IsFalse(yml.Contains("DreamWeaveLauncher.pdb"), wf + " names a build file");
            }
        }

        [TestMethod]
        public void BuildOutput_MatchesTheList_WhenTheLauncherIsBuilt()
        {
            // The test assembly sits in tests/Launcher.Tests/bin/<Configuration>/<tfm>; the launcher in
            // src/Launcher/bin/<same Configuration>/<tfm>. Not built in this configuration: inconclusive, not a pass.
            string baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string tfm = Path.GetFileName(baseDir);
            string config = Path.GetFileName(Path.GetDirectoryName(baseDir));
            string outDir = Path.Combine(RepoRoot(), "src", "Launcher", "bin", config, tfm);
            if (!File.Exists(Path.Combine(outDir, "DreamWeaveLauncher.exe")))
            {
                string message = "No launcher build output at " + outDir;
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")))
                {
                    // CI always builds first: a missing output there means this check would silently not run.
                    Assert.Fail(message);
                }

                Assert.Inconclusive(message);
            }

            Manifest m = Load();
            var present = new DirectoryInfo(outDir).GetFileSystemInfos().Select(f => f.Name).ToList();
            var allowed = m.Ship.Concat(m.BuildOnly).ToList();
            CollectionAssert.IsSubsetOf(m.Ship, present, "a shipped file is missing from the build output");
            CollectionAssert.IsSubsetOf(present, allowed, "the build output holds a file that is not in installer/shipped-files.txt");

            long total = m.Ship.Sum(n => new FileInfo(Path.Combine(outDir, n)).Length);
            Assert.IsTrue(new FileInfo(Path.Combine(outDir, "DreamWeaveLauncher.exe")).Length <= m.MaxExeBytes, "exe over its cap");
            Assert.IsTrue(total <= m.MaxTotalBytes, "shipped files over the total cap: " + total);
        }

        /// <summary>Markdown markers removed and runs of whitespace collapsed, so only the words are compared.</summary>
        private static string Normalize(string text)
        {
            string t = text.Replace("\r\n", "\n");
            t = Regex.Replace(t, @"^#+\s+", string.Empty, RegexOptions.Multiline);
            t = t.Replace("`", string.Empty);
            return Regex.Replace(t, @"\s+", " ").Trim();
        }

        [TestMethod]
        public void NoticesText_IsTheShippedSectionOfTheMarkdownNotices_AndNamesOnlyShippedThirdPartyFiles()
        {
            string md = ReadRepoFile("THIRD-PARTY-NOTICES.md").Replace("\r\n", "\n");
            int start = md.IndexOf("## Shipped beside the exe", StringComparison.Ordinal);
            int end = md.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0 && end > start, "the shipped section of THIRD-PARTY-NOTICES.md was not found");
            string expected = Normalize("# Third-party notices\n\n" + md.Substring(start, end - start));
            string txt = ReadRepoFile("src", "Launcher", "THIRD-PARTY-NOTICES.txt");
            Assert.AreEqual(expected, Normalize(txt), "src/Launcher/THIRD-PARTY-NOTICES.txt must carry the same words as the shipped section of THIRD-PARTY-NOTICES.md (headings without '#', no backticks).");

            foreach (string retired in new[] { "Costura", "Fody" })
            {
                Assert.IsFalse(txt.Contains(retired), "retired package in the shipped notices: " + retired);
            }

            // Every shipped third-party DLL (anything but our own files) has a notice entry.
            foreach (string name in Load().Ship.Where(n => n.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !n.StartsWith("Launcher.", StringComparison.Ordinal)))
            {
                StringAssert.Contains(txt, name, "no notice for the shipped " + name);
            }
        }

        [TestMethod]
        public void NoticesText_IsShippedBesideTheExe()
        {
            CollectionAssert.Contains(Load().Ship, "THIRD-PARTY-NOTICES.txt");
            StringAssert.Contains(ReadRepoFile("src", "Launcher", "Launcher.csproj"), "THIRD-PARTY-NOTICES.txt");
            foreach (char c in ReadRepoFile("src", "Launcher", "THIRD-PARTY-NOTICES.txt"))
            {
                Assert.IsTrue(c == '\r' || c == '\n' || c == '\t' || (c >= ' ' && c < 127), "non-ASCII or control character: U+" + ((int)c).ToString("X4"));
            }
        }
        [TestMethod]
        public void List_IsAscii()
        {
            foreach (char c in ReadRepoFile("installer", "shipped-files.txt"))
            {
                Assert.IsTrue(c == '\r' || c == '\n' || (c >= ' ' && c < 127), "non-ASCII or control character: U+" + ((int)c).ToString("X4"));
            }
        }
    }
}