using System;
using System.IO;
using System.Text.RegularExpressions;
using DreamWeave.Launcher.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.Cleanup
{
    /// <summary>Pins installer/DreamWeaveLauncher.iss and installer/privacy.txt to the code and docs they mirror.</summary>
    [TestClass]
    public class SetupScriptConsistencyTests
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

        private static string Iss()
        {
            return ReadRepoFile("installer", "DreamWeaveLauncher.iss");
        }

        /// <summary>The value of a <c>[Setup]</c> directive (the text after <c>Name=</c> on its own line), or null.</summary>
        private static string Directive(string iss, string name)
        {
            Match m = Regex.Match(iss, @"^" + Regex.Escape(name) + @"=(.*?)\s*$", RegexOptions.Multiline);
            return m.Success ? m.Groups[1].Value : null;
        }

        [TestMethod]
        public void AppId_EqualsSetupConstants_WithLiteralBraceEscape()
        {
            // Inno reads a doubled opening brace as a literal brace.
            Assert.AreEqual("{" + SetupConstants.SetupAppId, Directive(Iss(), "AppId"));
        }

        [TestMethod]
        public void AppMutex_EqualsSetupConstants()
        {
            Assert.AreEqual(SetupConstants.SetupMutexName, Directive(Iss(), "AppMutex"));
        }

        [TestMethod]
        public void UninstallCleanup_UsesTheSwitchLiterals()
        {
            string iss = Iss();
            StringAssert.Contains(iss, "RunLauncherCleanup('" + SetupConstants.CleanupSwitch + " " + SetupConstants.SilentSwitch + "')");
            StringAssert.Contains(iss, "RunLauncherCleanup('" + SetupConstants.CleanupSwitch + "')");
            StringAssert.Contains(iss, "usUninstall");
            StringAssert.Contains(iss, "ewWaitUntilTerminated");
        }

        [TestMethod]
        public void UninstallCleanup_SilentUninstallRunsTheSilentForm_InteractiveRunsThePlainFormFirst()
        {
            // "if UninstallSilent() then <silent run> else if <plain run> = 120 then <silent run>": inverting the test fails this.
            string iss = Iss();
            Assert.IsTrue(
                Regex.IsMatch(iss, @"if UninstallSilent\(\) then\s+RunLauncherCleanup\('--cleanup --silent'\)\s+else if RunLauncherCleanup\('--cleanup'\) = " + SetupConstants.CleanupCancelledExitCode + @" then\s+(//[^\r\n]*\s+)*RunLauncherCleanup\('--cleanup --silent'\);"),
                "the uninstall step must run --cleanup --silent when silent, else --cleanup and, on the cancelled exit code, --cleanup --silent");
            Assert.IsFalse(iss.Contains("not UninstallSilent"));
        }

        [TestMethod]
        public void CancelExitCode_IsDistinctAndOnlyForTheUninstallersPlainCleanup()
        {
            Assert.AreEqual(SetupConstants.CleanupCancelledExitCode, DreamWeave.Launcher.App.StartupArgs.Parse(new[] { "--cleanup" }).CancelExitCode);
            Assert.AreEqual(0, DreamWeave.Launcher.App.StartupArgs.Parse(new[] { "--cleanup", "--handover" }).CancelExitCode, "Settings hand-over: cancelling just returns");
            foreach (LauncherError e in Enum.GetValues(typeof(LauncherError)))
            {
                Assert.AreNotEqual(SetupConstants.CleanupCancelledExitCode, (int)e, "the cancelled code must not collide with " + e);
            }
        }

        [TestMethod]
        public void DotNetGate_Is48OrNewer_AndBlocksSetup()
        {
            string iss = Iss();
            StringAssert.Contains(iss, "Net48Release = 528040;");
            StringAssert.Contains(iss, "Result := IsNet48Installed();");
            StringAssert.Contains(iss, "Result := Release >= Net48Release;");
        }

        [TestMethod]
        public void SetupStaysUninstallable()
        {
            string iss = Iss();
            Assert.IsNull(Directive(iss, "CreateUninstallRegKey"), "CreateUninstallRegKey would remove the Settings > Apps entry the launcher reads");
            string uninstallable = Directive(iss, "Uninstallable");
            Assert.IsTrue(uninstallable == null || uninstallable == "yes", "Uninstallable must be absent or yes");
        }

        [TestMethod]
        public void DirPage_RejectsTheLauncherDataFolder()
        {
            string iss = Iss();
            StringAssert.Contains(iss, "function NextButtonClick(CurPageID: Integer): Boolean;");
            StringAssert.Contains(iss, "CurPageID = wpSelectDir");
            StringAssert.Contains(iss, "Lowercase(RemoveBackslash(WizardDirValue))");
            StringAssert.Contains(iss, @"ExpandConstant('{localappdata}\DreamWeave')");
            StringAssert.Contains(iss, @"(Dir = DataDir) or (Pos(DataDir + '\', Dir) = 1)");
            StringAssert.Contains(iss, "Result := False;");
        }

        [TestMethod]
        public void InstallsPerUser_IntoTheAutoProgramFilesFolder()
        {
            string iss = Iss();
            Assert.AreEqual("lowest", Directive(iss, "PrivilegesRequired"));
            Assert.AreEqual(@"{autopf}\" + SetupConstants.InstallFolderName, Directive(iss, "DefaultDirName"));
        }

        [TestMethod]
        public void ShipsOnlyTheExeAndItsConfig()
        {
            string iss = Iss();
            MatchCollection sources = Regex.Matches(iss, @"^Source:\s*""([^""]+)""", RegexOptions.Multiline);
            Assert.AreEqual(2, sources.Count);
            Assert.IsTrue(sources[0].Groups[1].Value.EndsWith(@"\DreamWeaveLauncher.exe", StringComparison.Ordinal));
            Assert.IsTrue(sources[1].Groups[1].Value.EndsWith(@"\DreamWeaveLauncher.exe.config", StringComparison.Ordinal));
            Assert.IsFalse(iss.IndexOf(".pdb", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        [TestMethod]
        public void Script_IsAscii()
        {
            foreach (string name in new[] { "DreamWeaveLauncher.iss", "privacy.txt" })
            {
                foreach (char c in ReadRepoFile("installer", name))
                {
                    Assert.IsTrue(c == '\r' || c == '\n' || c == '\t' || (c >= ' ' && c < 127), name + " has a non-ASCII or control character: U+" + ((int)c).ToString("X4"));
                }
            }
        }

        /// <summary>Markdown markers removed, blank lines dropped and runs of whitespace collapsed, so only the words are compared.</summary>
        private static string Normalize(string text)
        {
            string t = text.Replace("\r\n", "\n");
            t = Regex.Replace(t, @"^#+\s+", string.Empty, RegexOptions.Multiline);
            t = t.Replace("`", string.Empty);
            return Regex.Replace(t, @"\s+", " ").Trim();
        }

        [TestMethod]
        public void PrivacyText_MatchesThePrivacyDoc()
        {
            Assert.AreEqual(
                Normalize(ReadRepoFile("Docs", "privacy.md")),
                Normalize(ReadRepoFile("installer", "privacy.txt")),
                "installer/privacy.txt must carry the same words as Docs/privacy.md (headings without '#', no backticks).");
        }
    }
}
