using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests
{
    /// <summary>
    /// The launcher must not install or persist itself (a real setup program owns that), and must not look
    /// like malware that does. This scans the shipped source for the mechanisms self-install used.
    /// </summary>
    [TestClass]
    public class PersistenceGuardTests
    {
        // NOTE: "CreateHeldForMove" is deliberately NOT banned yet. Self-update (SelfUpdater / ExeSwap) still
        // uses it. The follow-up PR that deletes SelfUpdater and ExeSwap adds it back to this list.
        private static readonly string[] BannedLiterals =
        {
            "cmd.exe",
            "ping.exe",
            "IShellLinkW",
        };

        // A registry WRITE API. Reads (OpenSubKey(path, false), GetValue) are fine.
        private static readonly Regex RegistryWrite = new Regex(
            @"\bSetValue\s*\(|\bCreateSubKey\s*\(|\bDeleteSubKey(Tree)?\s*\(|\bDeleteValue\s*\(|\bRegSetValueEx|\bRegCreateKeyEx|\bRegDeleteKey|OpenSubKey\s*\([^)]*,\s*(true|writable\s*:\s*true)",
            RegexOptions.Compiled);

        private static readonly Regex UninstallKey = new Regex(@"CurrentVersion\\+Uninstall", RegexOptions.Compiled);

        [TestMethod]
        public void Source_HasNoSelfInstallMechanisms()
        {
            List<string> hits = Scan(SourceRoot());
            Assert.AreEqual(0, hits.Count, "Persistence mechanisms found in src/:" + Environment.NewLine + string.Join(Environment.NewLine, hits));
        }

        [TestMethod]
        public void Scanner_FlagsEachBannedPattern()
        {
            // Positive controls: prove the scanner can fail, without touching src/.
            var temp = Path.Combine(Path.GetTempPath(), "dw-persistence-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                string[] samples =
                {
                    "var p = \"cmd.exe\";",
                    "var p = \"ping.exe\";",
                    "interface IShellLinkW { }",
                    "var k = @\"Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\X\"; key.SetValue(\"a\", \"b\");",
                    "var k = @\"Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\X\"; Registry.CurrentUser.CreateSubKey(k);",
                    "var k = @\"Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\X\"; Registry.CurrentUser.OpenSubKey(k, true);",
                };
                for (int i = 0; i < samples.Length; i++)
                {
                    File.WriteAllText(Path.Combine(temp, "Sample" + i + ".cs"), samples[i]);
                }

                Assert.AreEqual(samples.Length, Scan(temp).Count, "every sample line must be flagged");

                File.WriteAllText(Path.Combine(temp, "Clean.cs"), "var k = @\"Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\X\"; Registry.CurrentUser.OpenSubKey(k, false).GetValue(\"UninstallString\");");
                Assert.AreEqual(samples.Length, Scan(temp).Count, "a read of the Uninstall key is allowed");
            }
            finally
            {
                Directory.Delete(temp, true);
            }
        }

        private static List<string> Scan(string root)
        {
            var hits = new List<string>();
            foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
                {
                    continue;
                }

                string text = File.ReadAllText(file);
                var reasons = new List<string>();
                foreach (string banned in BannedLiterals)
                {
                    if (text.IndexOf(banned, StringComparison.Ordinal) >= 0)
                    {
                        reasons.Add("contains '" + banned + "'");
                    }
                }

                if (UninstallKey.IsMatch(text) && RegistryWrite.IsMatch(text))
                {
                    reasons.Add("writes near CurrentVersion\\Uninstall");
                }

                if (reasons.Count > 0)
                {
                    hits.Add(file + ": " + string.Join("; ", reasons));
                }
            }

            return hits;
        }

        private static string SourceRoot()
        {
            string dir = AppContext.BaseDirectory;
            while (dir != null)
            {
                string candidate = Path.Combine(dir, "src");
                if (Directory.Exists(candidate) && File.Exists(Path.Combine(dir, "DreamWeaveLauncher.sln")))
                {
                    return candidate;
                }

                dir = Path.GetDirectoryName(dir);
            }

            Assert.Fail("Could not find the repository's src folder by walking up from " + AppContext.BaseDirectory);
            return null;
        }
    }
}