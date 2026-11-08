using System;
using System.IO;
using System.Text.RegularExpressions;
using DreamWeave.Launcher.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.Cleanup
{
    /// <summary>Pins installer/DreamWeaveLauncher.iss to the code it mirrors and to the one-click setup rules (no wizard pages).</summary>
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

        /// <summary>The text of a Pascal function in [Code]: from its header to the next top-level function or procedure, or the end.</summary>
        private static string CodeBlock(string iss, string header)
        {
            int start = iss.IndexOf(header, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, "missing " + header);
            Match next = Regex.Match(iss.Substring(start + header.Length), @"^(function|procedure) ", RegexOptions.Multiline);
            return next.Success ? iss.Substring(start, header.Length + next.Index) : iss.Substring(start);
        }

        [TestMethod]
        public void DataFolderGuard_LivesInTheAlwaysRunHook_NotOnAWizardPage()
        {
            string iss = Iss();

            // With the directory page disabled a page-bound hook never runs, so /DIR= under the data folder would get through.
            StringAssert.Contains(iss, "function PrepareToInstall(var NeedsRestart: Boolean): String;");
            Assert.IsFalse(iss.Contains("NextButtonClick"), "the guard must not hang off a wizard page");
            Assert.IsFalse(iss.Contains("wpSelectDir"), "the guard must not depend on the directory page");

            string guard = CodeBlock(iss, "function PrepareToInstall(");
            StringAssert.Contains(guard, "Lowercase(RemoveBackslash(WizardDirValue))");
            StringAssert.Contains(guard, @"DataDirShown := ExpandConstant('{localappdata}\DreamWeave');");
            StringAssert.Contains(guard, "DataDirLower := Lowercase(RemoveBackslash(DataDirShown));");
            StringAssert.Contains(guard, @"(Dir = DataDirLower) or (Pos(DataDirLower + '\', Dir) = 1)");
            StringAssert.Contains(guard, "' + DataDirShown + '", "the message must show the original-case path");
            StringAssert.Contains(guard, "Result := '';", "no error unless the folder is under the data folder");
            StringAssert.Contains(guard, "Result := 'Setup cannot install into ' + DataDirShown + '. Run setup again without choosing that folder.';", "owner-approved wording");
            Assert.IsFalse(guard.Contains("DRAFT"), "the wording is approved");
            Assert.IsTrue(Regex.IsMatch(guard, @"\bthen\s+begin\s+(//[^\n]*\s+)*Result := '[^']*' \+ DataDirShown"), "the refusal text is the function result");
        }

        [TestMethod]
        public void MessageBoxes_AreSuppressible()
        {
            // A plain MsgBox is not suppressed by /SUPPRESSMSGBOXES and would block a silent run.
            // The data-folder refusal is returned from PrepareToInstall as an error string, so only the .NET gate shows a box.
            string iss = Iss();
            Assert.IsFalse(Regex.IsMatch(iss, @"(?<![A-Za-z])MsgBox\("), "use SuppressibleMsgBox");
            Assert.AreEqual(1, Regex.Matches(iss, @"SuppressibleMsgBox\(").Count);
            Assert.AreEqual(1, Regex.Matches(iss, @"MB_OK, IDOK\)").Count);
        }

        [TestMethod]
        public void NoWizardPages_EveryPageIsDisabled()
        {
            string iss = Iss();
            foreach (string name in new[] { "DisableWelcomePage", "DisableDirPage", "DisableProgramGroupPage", "DisableReadyPage", "DisableFinishedPage", "DisableStartupPrompt" })
            {
                Assert.AreEqual("yes", Directive(iss, name), name + " must be yes (one-click setup)");
            }
        }

        [TestMethod]
        public void NothingAddsAPage_NoTasks_NoInfoOrLicenseFile_NoCustomPages()
        {
            string iss = Iss();
            Assert.IsFalse(Regex.IsMatch(iss, @"^\[Tasks\]", RegexOptions.Multiline), "a Tasks section adds the Select Additional Tasks page");
            Assert.IsFalse(Regex.IsMatch(iss, @"Tasks:\s", RegexOptions.None), "an entry still names a task");
            Assert.IsFalse(Regex.IsMatch(iss, @"^\[(Components|Types)\]", RegexOptions.Multiline), "Types/Components add the Select Components page");
            foreach (string name in new[] { "InfoBeforeFile", "InfoAfterFile", "LicenseFile", "Password", "UserInfoPage" })
            {
                Assert.IsNull(Directive(iss, name), name + " would add a wizard page");
            }

            Assert.IsFalse(Regex.IsMatch(iss, @"Create(InputQuery|InputOption|InputDir|InputFile|OutputMsg|OutputProgress|OutputMarquee|Custom|Download)Page"), "custom wizard page");
            Assert.IsFalse(File.Exists(Path.Combine(RepoRoot(), "installer", "privacy.txt")), "the privacy page is gone; the launcher's welcome screen links the policy");
        }

        [TestMethod]
        public void Shortcuts_AreAlwaysCreated_StartMenuAndDesktop()
        {
            string icons = Section(Iss(), "Icons");
            StringAssert.Contains(icons, @"Name: ""{autoprograms}\DreamWeave Launcher""; Filename: ""{app}\DreamWeaveLauncher.exe""");
            Assert.IsTrue(Regex.IsMatch(icons, @"^Name: ""\{autodesktop\}\\DreamWeave Launcher""; Filename: ""\{app\}\\DreamWeaveLauncher\.exe""\s*$", RegexOptions.Multiline), "desktop shortcut must be unconditional");
        }

        [TestMethod]
        public void LauncherStartsAfterInstall_ButNotUnderSilentInstalls_AndNotElevated()
        {
            string run = Section(Iss(), "Run");
            var entries = new System.Collections.Generic.List<string>();
            foreach (string line in run.Split('\n'))
            {
                string entry = line.Trim();
                if (entry.Length > 0 && !entry.StartsWith(";", StringComparison.Ordinal))
                {
                    entries.Add(entry);
                }
            }

            Assert.AreEqual(1, entries.Count, "exactly one [Run] entry");
            Match flags = Regex.Match(entries[0], @"Flags:\s*([^;]*)$");
            Assert.IsTrue(flags.Success, "no Flags: " + entries[0]);
            string[] f = flags.Groups[1].Value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            CollectionAssert.Contains(f, "nowait");
            CollectionAssert.Contains(f, "skipifsilent");
            CollectionAssert.Contains(f, "runasoriginaluser");
            CollectionAssert.DoesNotContain(f, "postinstall");
            StringAssert.StartsWith(entries[0], @"Filename: ""{app}\DreamWeaveLauncher.exe"";");
            Assert.AreEqual("lowest", Directive(Iss(), "PrivilegesRequired"), "an elevated setup would start the launcher elevated");
        }

        [TestMethod]
        public void Upgrades_ReuseThePreviousFolder_AndRespectTheMutex()
        {
            string iss = Iss();
            Assert.AreEqual("yes", Directive(iss, "UsePreviousAppDir"));
            Assert.AreEqual(SetupConstants.SetupMutexName, Directive(iss, "AppMutex"));
            Assert.AreEqual("yes", Directive(iss, "DisableDirPage"), "no folder prompt on upgrade");
        }

        [TestMethod]
        public void ProgressWindow_IsModernDark_WithTheLogoInEveryDpiSize()
        {
            string iss = Iss();
            Assert.AreEqual("modern dark", Directive(iss, "WizardStyle"));
            string list = Directive(iss, "WizardSmallImageFile");
            Assert.IsNotNull(list, "WizardSmallImageFile");
            string[] files = list.Split(',');
            Assert.IsTrue(files.Length >= 2, "several sizes");
            byte[] pngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            foreach (string file in files)
            {
                string path = Path.Combine(RepoRoot(), "installer", file.Trim());
                Assert.IsTrue(File.Exists(path), "missing wizard image " + path);
                byte[] head = new byte[8];
                using (FileStream fs = File.OpenRead(path))
                {
                    Assert.AreEqual(8, fs.Read(head, 0, 8));
                }

                CollectionAssert.AreEqual(pngSignature, head, file + " is not a PNG");
            }
        }

        [TestMethod]
        public void SetupWindowTitle_StaysDreamWeaveLauncherSetup()
        {
            Assert.AreEqual("DreamWeave Launcher", Directive(Iss(), "AppName"));
            Assert.AreEqual("DreamWeave Launcher Setup", Directive(Iss(), "VersionInfoDescription"));
        }

        private static string Section(string iss, string name)
        {
            Match m = Regex.Match(iss, @"^\[" + name + @"\]\s*$(.*?)(?=^\[|\z)", RegexOptions.Multiline | RegexOptions.Singleline);
            Assert.IsTrue(m.Success, "missing section [" + name + "]");
            return m.Groups[1].Value;
        }

        [TestMethod]
        public void UpdateLeftovers_AreRemovedOnInstallAndUninstall()
        {
            string iss = Iss();
            foreach (string section in new[] { "InstallDelete", "UninstallDelete" })
            {
                string body = Section(iss, section);
                StringAssert.Contains(body, @"Type: files; Name: ""{app}\DreamWeaveLauncher.exe.old""", section);
                StringAssert.Contains(body, @"Type: files; Name: ""{app}\DreamWeaveLauncher.exe.new""", section);

                // The player may install into a folder holding their own files (and 8.3 short names make *.old match
                // more than it looks): no wildcard of any kind is allowed in a delete section.
                foreach (string line in body.Split('\n'))
                {
                    string entry = line.Trim();
                    if (entry.Length > 0 && !entry.StartsWith(";", StringComparison.Ordinal))
                    {
                        Assert.IsFalse(entry.Contains("*") || entry.Contains("?"), "wildcard in [" + section + "]: " + entry);
                    }
                }
            }

            StringAssert.Contains(Section(iss, "InstallDelete"), @"{userprograms}\DreamWeave Launcher.lnk");
            Assert.IsFalse(iss.Contains("until it is retired"));
            Assert.IsFalse(iss.Contains("sweeps them any more"));
        }

        [TestMethod]
        public void InstallsPerUser_IntoTheAutoProgramFilesFolder()
        {
            string iss = Iss();
            Assert.AreEqual("lowest", Directive(iss, "PrivilegesRequired"));
            Assert.AreEqual(@"{autopf}\" + SetupConstants.InstallFolderName, Directive(iss, "DefaultDirName"));
        }

        [TestMethod]
        public void Script_IsAscii()
        {
            foreach (string name in new[] { "DreamWeaveLauncher.iss" })
            {
                foreach (char c in ReadRepoFile("installer", name))
                {
                    Assert.IsTrue(c == '\r' || c == '\n' || c == '\t' || (c >= ' ' && c < 127), name + " has a non-ASCII or control character: U+" + ((int)c).ToString("X4"));
                }
            }
        }
    }
}
