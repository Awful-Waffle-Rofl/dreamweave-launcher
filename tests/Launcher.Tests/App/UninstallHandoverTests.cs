using System;
using System.ComponentModel;
using System.IO;
using DreamWeave.Launcher.App;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Cleanup;
using CleanupProcessLauncher = DreamWeave.Launcher.Tests.Cleanup.FakeProcessLauncher;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.App
{
    /// <summary>The HKCU UninstallString is untrusted: only a real unins000.exe next to the launcher may be run.</summary>
    [TestClass]
    public class UninstallHandoverTests
    {
        private TempDir temp;
        private string appDir;
        private string runningExe;
        private string unins;
        private readonly SelfFileSystem fs = new SelfFileSystem();

        [TestInitialize]
        public void Setup()
        {
            temp = new TempDir();
            appDir = Path.Combine(temp.Path, "Program Files", "DreamWeave Launcher");
            Directory.CreateDirectory(appDir);
            runningExe = Path.Combine(appDir, "DreamWeaveLauncher.exe");
            unins = Path.Combine(appDir, "unins000.exe");
            File.WriteAllText(runningExe, "x");
            File.WriteAllText(unins, "x");
        }

        [TestCleanup]
        public void Cleanup() => temp.Dispose();

        private bool Resolves(string uninstallString)
        {
            string p, a;
            return SetupUninstallLocator.TryResolve(uninstallString, fs, runningExe, out p, out a);
        }

        private void AssertFallsBack(string uninstallString, string why)
        {
            bool usesSetup;
            System.Diagnostics.ProcessStartInfo psi = UninstallHandover.ChooseUninstallStart(uninstallString, fs, runningExe, out usesSetup);
            Assert.IsFalse(usesSetup, why);
            Assert.AreEqual(runningExe, psi.FileName, why);
            Assert.AreEqual("--cleanup --handover", psi.Arguments, why);
            Assert.IsFalse(psi.UseShellExecute);
        }

        [TestMethod]
        public void ValidPathWithSpaces_UsesTheSetupUninstaller()
        {
            bool usesSetup;
            System.Diagnostics.ProcessStartInfo psi = UninstallHandover.ChooseUninstallStart("\"" + unins + "\"", fs, runningExe, out usesSetup);
            Assert.IsTrue(usesSetup, "a path with spaces in the exe dir is the normal case");
            Assert.AreEqual(unins, psi.FileName);
            Assert.AreEqual(string.Empty, psi.Arguments);
            Assert.IsTrue(psi.UseShellExecute);
        }

        [TestMethod]
        public void ValidPath_CaseAndDigitsAreFlexible()
        {
            string unins001 = Path.Combine(appDir, "UNINS001.EXE");
            File.WriteAllText(unins001, "x");
            Assert.IsTrue(Resolves("\"" + unins001 + "\""));
        }

        [TestMethod]
        public void UnquotedPathWithSpaces_StillResolves()
        {
            Assert.IsTrue(Resolves(unins));
        }

        [TestMethod]
        public void MissingFile_FallsBack()
        {
            File.Delete(unins);
            AssertFallsBack("\"" + unins + "\"", "the file does not exist");
        }

        [TestMethod]
        public void NonUninsName_FallsBack()
        {
            string other = Path.Combine(appDir, "setup.exe");
            File.WriteAllText(other, "x");
            AssertFallsBack("\"" + other + "\"", "not an unins### name");
            string longer = Path.Combine(appDir, "unins0000.exe");
            File.WriteAllText(longer, "x");
            AssertFallsBack("\"" + longer + "\"", "four digits");
        }

        [TestMethod]
        public void RelativePath_FallsBack()
        {
            AssertFallsBack("unins000.exe", "relative");
            AssertFallsBack("\"..\\unins000.exe\"", "relative");
        }

        [TestMethod]
        public void RelativePath_FallsBack_EvenWhenTheWorkingDirectoryIsTheExeFolder()
        {
            string saved = Environment.CurrentDirectory;
            try
            {
                Environment.CurrentDirectory = appDir;
                AssertFallsBack("unins000.exe", "relative names resolve against the working directory, which anyone can choose");
                AssertFallsBack("\".\\unins000.exe\"", "relative");
            }
            finally
            {
                Environment.CurrentDirectory = saved;
            }
        }

        [TestMethod]
        public void CmdSlashC_FallsBack()
        {
            AssertFallsBack("cmd /c calc", "a shell command line");
            AssertFallsBack("cmd.exe /c \"" + unins + "\"", "a shell wrapping the real file");
        }

        [TestMethod]
        public void SystemExe_FallsBack()
        {
            AssertFallsBack(@"C:\Windows\System32\cmd.exe", "a system exe");
            AssertFallsBack(@"""C:\Windows\System32\unins000.exe""", "right name, wrong folder");
        }

        [TestMethod]
        public void UninsInAnotherFolder_FallsBack()
        {
            string elsewhere = Path.Combine(temp.Path, "Elsewhere");
            Directory.CreateDirectory(elsewhere);
            string evil = Path.Combine(elsewhere, "unins000.exe");
            File.WriteAllText(evil, "x");
            AssertFallsBack("\"" + evil + "\"", "not next to the running launcher");
            AssertFallsBack("\"" + Path.Combine(appDir, "..", "Elsewhere", "unins000.exe") + "\"", "dot-dot into another folder");
        }

        [TestMethod]
        public void UninsInASubfolder_FallsBack()
        {
            string sub = Path.Combine(appDir, "sub");
            Directory.CreateDirectory(sub);
            string nested = Path.Combine(sub, "unins000.exe");
            File.WriteAllText(nested, "x");
            AssertFallsBack("\"" + nested + "\"", "must be IN the exe folder, not below it");
        }

        [TestMethod]
        public void ExtraArguments_FallBack()
        {
            AssertFallsBack("\"" + unins + "\" /VERYSILENT", "arguments are not part of the registered command");
        }

        [TestMethod]
        public void NullOrBlank_FallsBack()
        {
            AssertFallsBack(null, "no registration");
            AssertFallsBack("  ", "blank");
        }

        [TestMethod]
        public void TryStart_SetupStartFailsWithWin32_RetriesTheCleanupHandoverOnce()
        {
            var processes = new CleanupProcessLauncher();
            processes.StartFailure = psi => psi.FileName == unins ? new Win32Exception(5) : null;

            Assert.IsTrue(UninstallHandover.TryStart(processes, fs, "\"" + unins + "\"", runningExe));

            Assert.AreEqual(2, processes.Started.Count);
            Assert.AreEqual(unins, processes.Started[0].FileName);
            Assert.AreEqual(runningExe, processes.Started[1].FileName);
            Assert.AreEqual("--cleanup --handover", processes.Started[1].Arguments);
        }

        [TestMethod]
        public void TryStart_EverythingFails_ReturnsFalse_AfterTryingEachOnce()
        {
            var processes = new CleanupProcessLauncher { StartFailure = psi => new Win32Exception(5) };
            Assert.IsFalse(UninstallHandover.TryStart(processes, fs, "\"" + unins + "\"", runningExe));
            Assert.AreEqual(2, processes.Started.Count);
        }

        [TestMethod]
        public void TryStart_NoSetupRegistered_StartsOnlyTheCleanupHandover()
        {
            var processes = new CleanupProcessLauncher();
            Assert.IsTrue(UninstallHandover.TryStart(processes, fs, null, runningExe));
            Assert.AreEqual(1, processes.Started.Count);
            Assert.AreEqual("--cleanup --handover", processes.Started[0].Arguments);
        }
    }
}