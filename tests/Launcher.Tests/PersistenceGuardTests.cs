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
        // The launcher also must not replace its own exe: the self-update machinery (SelfUpdater, ExeSwap, the
        // "--updated" relaunch switch, and the held-for-move file handle they needed) is gone for good. An update
        // is a download page opened in the browser.
        private static readonly string[] BannedLiterals =
        {
            "cmd.exe",
            "ping.exe",
            "IShellLinkW",
            "CreateHeldForMove",
            "SelfUpdater",
            "ExeSwap",
            "\"--updated\"",
        };

        // A registry WRITE API anywhere in src/. Reads (OpenSubKey(path, false), OpenSubKey(path), GetValue) are
        // fine. This is deliberately NOT tied to the Uninstall key string: a write through a constant
        // (CreateSubKey(SetupConstants.SetupUninstallKeyPath)) must fail too.
        private static readonly Regex RegistryWrite = new Regex(
            @"\bSetValue\s*\(|\bCreateSubKey\s*\(|\bDeleteSubKey(Tree)?\s*\(|\bDeleteValue\s*\(|\bRegSetValueEx|\bRegCreateKeyEx|\bRegDeleteKey|OpenSubKey\s*\((?:[^()]|\([^()]*\))*,(?>\s*)(?!false\b)",
            RegexOptions.Compiled);

        // String and char literals (kept) or comments (dropped). Strings come first so a "//" inside a URL
        // literal is not mistaken for a comment.
        private static readonly Regex StringsAndComments = new Regex(
            @"@""(?:""""|[^""])*""|""(?:\\.|[^""\\\n])*""|'(?:\\.|[^'\\\n])+'|//[^\n]*|/\*.*?\*/",
            RegexOptions.Compiled | RegexOptions.Singleline);

        // Files (relative to src/, forward slashes) that may write the registry. Empty on purpose: the setup
        // program owns every registry write. Adding a name here needs a reason in review.
        private static readonly string[] RegistryWriteAllowlist = new string[0];

        // A command interpreter reached by a name other than the banned "cmd.exe" literal.
        private static readonly Regex Shell = new Regex(@"ComSpec|""cmd""", RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
                    "FileStream s = fs.CreateHeldForMove(path);",
                    "var u = new SelfUpdater(paths);",
                    "ExeSwap.Swap(fs, exe);",
                    "var p = new ProcessStartInfo(exe, \"--updated\");",
                    "var k = @\"Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\X\"; key.SetValue(\"a\", \"b\");",
                    "var k = @\"Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\X\"; Registry.CurrentUser.CreateSubKey(k);",
                    "var k = @\"Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\X\"; Registry.CurrentUser.OpenSubKey(k, true);",
                    "Registry.CurrentUser.CreateSubKey(SetupConstants.SetupUninstallKeyPath);",
                    "key.SetValue(\"Run\", path);",
                    "key.DeleteValue(\"Run\");",
                    "key.DeleteSubKeyTree(name);",
                    "Registry.CurrentUser.OpenSubKey(k, writable: true);",
                    "RegSetValueEx(h, n, 0, 1, b, 4);",
                    "RegCreateKeyEx(h, n, 0, null, 0, 0, IntPtr.Zero, out k, out d);",
                    "var s = Environment.GetEnvironmentVariable(\"ComSpec\");",
                    "Process.Start(\"cmd\", \"/c calc\");",
                    "Registry.CurrentUser.OpenSubKey(Foo(x), true);",
                    "Registry.CurrentUser.OpenSubKey(Path.Combine(a, b), writable: true);",
                };
                for (int i = 0; i < samples.Length; i++)
                {
                    File.WriteAllText(Path.Combine(temp, "Sample" + i + ".cs"), samples[i]);
                }

                Assert.AreEqual(samples.Length, Scan(temp).Count, "every sample line must be flagged: " + string.Join(" | ", Scan(temp)));

                File.WriteAllText(Path.Combine(temp, "Clean.cs"), "var k = @\"Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\X\"; Registry.CurrentUser.OpenSubKey(k, false).GetValue(\"UninstallString\"); hive.OpenSubKey(p); x.GetStringValue(a, b);");
                Assert.AreEqual(samples.Length, Scan(temp).Count, "a read of the Uninstall key is allowed");

                // More clean controls: a nested call in the path argument of a read, and banned words in comments.
                File.WriteAllText(Path.Combine(temp, "Clean2.cs"), "Registry.CurrentUser.OpenSubKey(Path.Combine(a, b), false);");
                File.WriteAllText(Path.Combine(temp, "Clean3.cs"), "// cmd.exe ping.exe key.SetValue(a, b)\nvar url = \"http://x\"; /* CreateSubKey( ComSpec */ int y = 0;");
                Assert.AreEqual(samples.Length, Scan(temp).Count, "nested parentheses in a read and banned words in comments are allowed");

                // A banned call after a string containing // is still seen.
                File.WriteAllText(Path.Combine(temp, "Tricky.cs"), "var u = \"http://x\"; key.SetValue(a, b);");
                Assert.AreEqual(samples.Length + 1, Scan(temp).Count, "a // inside a string is not a comment");
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

                string text = StringsAndComments.Replace(File.ReadAllText(file), m => m.Value.StartsWith("/", StringComparison.Ordinal) ? " " : m.Value);
                var reasons = new List<string>();
                foreach (string banned in BannedLiterals)
                {
                    if (text.IndexOf(banned, StringComparison.Ordinal) >= 0)
                    {
                        reasons.Add("contains '" + banned + "'");
                    }
                }

                string relative = file.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar).Replace(Path.DirectorySeparatorChar, '/');
                if (RegistryWrite.IsMatch(text) && Array.IndexOf(RegistryWriteAllowlist, relative) < 0)
                {
                    reasons.Add("uses a registry write API");
                }

                if (Shell.IsMatch(text))
                {
                    reasons.Add("starts a command interpreter (ComSpec / \"cmd\")");
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