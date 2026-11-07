using System;
using System.IO;
using System.Text;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.SelfInstall;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.SelfInstall
{
    [TestClass]
    public class SelfInstallerTests
    {
        private static readonly byte[] V1 = Encoding.ASCII.GetBytes("launcher version one");
        private static readonly byte[] V2 = Encoding.ASCII.GetBytes("launcher version two, a little longer");

        private SelfInstallFixture fx;

        [TestInitialize]
        public void Setup() => fx = new SelfInstallFixture();

        [TestCleanup]
        public void Cleanup() => fx.Dispose();

        private SelfInstaller Installer(string version = "1.0.0") => new SelfInstaller(fx.Environment, version);

        [TestMethod]
        public void Install_CopiesExe_CreatesShortcut_AndRegisters()
        {
            string downloaded = fx.WriteDownloadedExe(V1);

            SelfInstallResult r = Installer("1.2.3").Install(downloaded);

            string target = Path.Combine(fx.Paths.InstallDir, "DreamWeaveLauncher.exe");
            Assert.AreEqual(target, r.ExePath);
            Assert.IsTrue(r.Copied);
            Assert.IsFalse(r.ReplacedExisting);
            CollectionAssert.AreEqual(V1, File.ReadAllBytes(target));
            Assert.IsFalse(File.Exists(target + ".new"));
            Assert.IsTrue(File.Exists(downloaded), "the downloaded copy is left alone");

            Assert.AreEqual(target, fx.Shortcuts.ReadTarget(Path.Combine(fx.Environment.ProgramsFolder, "DreamWeave Launcher.lnk")));

            string key = SelfInstallLayout.UninstallKeyPath;
            Assert.AreEqual(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\DreamWeaveLauncher", key);
            Assert.AreEqual("DreamWeave Launcher", fx.Registry.GetValue(key, "DisplayName"));
            Assert.AreEqual("1.2.3", fx.Registry.GetValue(key, "DisplayVersion"));
            Assert.AreEqual("DreamWeave", fx.Registry.GetValue(key, "Publisher"));
            Assert.AreEqual(fx.Paths.InstallDir, fx.Registry.GetValue(key, "InstallLocation"));
            Assert.AreEqual("\"" + target + "\" --uninstall", fx.Registry.GetValue(key, "UninstallString"));
            Assert.AreEqual(target + ",0", fx.Registry.GetValue(key, "DisplayIcon"));
            Assert.AreEqual(1, fx.Registry.GetValue(key, "NoModify"));
            Assert.AreEqual(1, fx.Registry.GetValue(key, "NoRepair"));
            Assert.AreEqual(1, fx.Registry.GetValue(key, "EstimatedSize"), "20 bytes rounds up to 1 KB");
            Assert.IsInstanceOfType(fx.Registry.GetValue(key, "InstallDate"), typeof(string));
        }

        [TestMethod]
        public void Install_Twice_IsIdempotent()
        {
            string downloaded = fx.WriteDownloadedExe(V1);
            Installer().Install(downloaded);
            SelfInstallResult second = Installer().Install(downloaded);

            Assert.IsTrue(second.ReplacedExisting);
            Assert.IsNull(second.LeftoverOldPath);
            CollectionAssert.AreEqual(V1, File.ReadAllBytes(second.ExePath));
            CollectionAssert.AreEquivalent(new[] { "DreamWeaveLauncher.exe" }, Array.ConvertAll(Directory.GetFiles(fx.Paths.InstallDir), Path.GetFileName));
            Assert.AreEqual(1, fx.Registry.Keys.Count);
            Assert.AreEqual(2, fx.Shortcuts.Writes);
        }

        [TestMethod]
        public void Install_Upgrade_ReplacesInstalledExe_AndVersion()
        {
            Installer("1.0.0").Install(fx.WriteDownloadedExe(V1, "a.exe"));
            SelfInstallResult r = Installer("2.0.0").Install(fx.WriteDownloadedExe(V2, "b.exe"));

            Assert.IsTrue(r.ReplacedExisting);
            CollectionAssert.AreEqual(V2, File.ReadAllBytes(r.ExePath));
            Assert.AreEqual("2.0.0", fx.Registry.GetValue(SelfInstallLayout.UninstallKeyPath, "DisplayVersion"));
            Assert.IsFalse(File.Exists(r.ExePath + ".old"));
        }

        [TestMethod]
        public void Install_FromTheInstalledExe_RefreshesRegistrationWithoutCopying()
        {
            Installer().Install(fx.WriteDownloadedExe(V1));
            fx.Registry.Keys.Clear();
            fx.FileSystem.Moves.Clear();

            SelfInstallResult r = Installer().Install(fx.Environment.InstalledExePath);

            Assert.IsFalse(r.Copied);
            Assert.AreEqual(0, fx.FileSystem.Moves.Count);
            Assert.AreEqual("DreamWeave Launcher", fx.Registry.GetValue(SelfInstallLayout.UninstallKeyPath, "DisplayName"));
        }

        [TestMethod]
        public void Install_UpgradeWhileOldCopyStillRunning_LeavesOldAside()
        {
            Installer().Install(fx.WriteDownloadedExe(V1, "a.exe"));
            string target = fx.Environment.InstalledExePath;

            // Simulate a running installed launcher: its image can be renamed but not deleted.
            fx.FileSystem.DeleteFailure = p => p.EndsWith(".old", StringComparison.OrdinalIgnoreCase) && File.Exists(p) ? new UnauthorizedAccessException("in use") : null;
            SelfInstallResult r = Installer().Install(fx.WriteDownloadedExe(V2, "b.exe"));

            Assert.AreEqual(target + ".old", r.LeftoverOldPath);
            CollectionAssert.AreEqual(V2, File.ReadAllBytes(target));
            CollectionAssert.AreEqual(V1, File.ReadAllBytes(target + ".old"));
        }

        [TestMethod]
        public void Install_SwapFailure_RollsBackToThePreviousExe()
        {
            Installer().Install(fx.WriteDownloadedExe(V1, "a.exe"));
            fx.FileSystem.Moves.Clear();
            fx.FileSystem.MoveFailure = (n, s, d) => n == 2 ? new IOException("injected") : null;

            LauncherException ex = Assert.ThrowsExactly<LauncherException>(() => Installer().Install(fx.WriteDownloadedExe(V2, "b.exe")));

            Assert.AreEqual(LauncherError.UpdateSwapFailed, ex.Code);
            string target = fx.Environment.InstalledExePath;
            CollectionAssert.AreEqual(V1, File.ReadAllBytes(target));
            Assert.IsFalse(File.Exists(target + ".new"));
            Assert.IsFalse(File.Exists(target + ".old"));
        }

        [TestMethod]
        public void Install_NoTarget_RemovesStaleOldAndNew()
        {
            string target = fx.Environment.InstalledExePath;
            Directory.CreateDirectory(fx.Paths.InstallDir);
            File.WriteAllText(target + ".old", "stale old");
            File.WriteAllText(target + ".new", "stale new");
            Assert.IsFalse(File.Exists(target));

            SelfInstallResult r = Installer().Install(fx.WriteDownloadedExe(V1));

            Assert.IsFalse(r.ReplacedExisting);
            CollectionAssert.AreEqual(V1, File.ReadAllBytes(target));
            Assert.IsFalse(File.Exists(target + ".old"), "stale .old cleaned up");
            Assert.IsFalse(File.Exists(target + ".new"), "stale .new cleaned up");
        }

        [TestMethod]
        public void Install_RegistryFailure_IsClassified()
        {
            fx.Registry.FailWrites = true;
            LauncherException ex = Assert.ThrowsExactly<LauncherException>(() => Installer().Install(fx.WriteDownloadedExe(V1)));
            Assert.AreEqual(LauncherError.RegistryWriteFailed, ex.Code);
        }

        [TestMethod]
        public void Install_MissingSource_Throws()
        {
            LauncherException ex = Assert.ThrowsExactly<LauncherException>(() => Installer().Install(Path.Combine(fx.Root, "nope.exe")));
            Assert.AreEqual(LauncherError.Unknown, ex.Code);
        }

        [TestMethod]
        public void Install_DoesNotCarryTheMarkOfTheWeb()
        {
            string downloaded = fx.WriteDownloadedExe(V1);
            try
            {
                AlternateStreams.Write(downloaded, "Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                Assert.Inconclusive("This volume does not support alternate data streams: " + ex.Message);
            }

            Assert.IsTrue(AlternateStreams.Exists(downloaded, "Zone.Identifier"), "fixture: source carries the stream");

            // Control: File.Copy (CopyFile) DOES carry the stream, so the assertion below discriminates.
            string control = Path.Combine(fx.Root, "control.exe");
            File.Copy(downloaded, control);
            Assert.IsTrue(AlternateStreams.Exists(control, "Zone.Identifier"), "control: File.Copy carries alternate data streams");

            SelfInstallResult r = Installer().Install(downloaded);

            Assert.IsFalse(AlternateStreams.Exists(r.ExePath, "Zone.Identifier"), "the installed exe must not carry the Mark of the Web");
            CollectionAssert.AreEqual(V1, File.ReadAllBytes(r.ExePath));
        }
    }
}
