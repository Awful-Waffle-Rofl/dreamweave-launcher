using System;
using System.IO;
using System.Linq;
using System.Text;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.SelfInstall;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.SelfInstall
{
    /// <summary>The structured leftover data the uninstall window shows instead of the raw failure lines.</summary>
    [TestClass]
    public class SelfUninstallerLeftoverTests
    {
        private SelfInstallFixture fx;
        private string exe;

        [TestInitialize]
        public void Setup()
        {
            fx = new SelfInstallFixture();
            new SelfInstaller(fx.Environment, "1.0.0").Install(fx.WriteDownloadedExe(Encoding.ASCII.GetBytes("exe")));
            exe = fx.Environment.InstalledExePath;
            Directory.CreateDirectory(fx.Paths.DataDir);
            File.WriteAllText(fx.Paths.StateFile, "{}");
            fx.Credentials.Save("alice", "pw1");
        }

        [TestCleanup]
        public void Cleanup() => fx.Dispose();

        [TestMethod]
        public void FullSuccess_HasNoLeftovers()
        {
            UninstallResult r = new SelfUninstaller(fx.Environment).Uninstall(new UninstallOptions { CurrentExePath = exe });
            Assert.IsTrue(r.Complete, string.Join("; ", r.Failures));
            Assert.AreEqual(0, r.LeftoverFolders.Count);
            Assert.IsFalse(r.CredentialsLeftOver);
        }

        [TestMethod]
        public void FailedDataFolder_IsReportedAsAFolderPath()
        {
            fx.FileSystem.TreeFailure = folder => string.Equals(folder, Path.GetFullPath(fx.Paths.DataDir), StringComparison.OrdinalIgnoreCase) ? folder + ": Access to the path is denied." : null;
            UninstallResult r = new SelfUninstaller(fx.Environment).Uninstall(new UninstallOptions { CurrentExePath = exe });
            Assert.IsFalse(r.Complete);
            CollectionAssert.AreEqual(new[] { Path.GetFullPath(fx.Paths.DataDir) }, r.LeftoverFolders.ToArray());
            Assert.IsFalse(r.CredentialsLeftOver);
        }

        [TestMethod]
        public void FailedSelfDelete_LeavesTheInstallFolder()
        {
            fx.SelfDelete.Result = false;
            UninstallResult r = new SelfUninstaller(fx.Environment).Uninstall(new UninstallOptions { CurrentExePath = exe });
            CollectionAssert.AreEqual(new[] { Path.GetFullPath(fx.Paths.InstallDir) }, r.LeftoverFolders.ToArray());
        }

        [TestMethod]
        public void UnlistableCredentials_SetTheFlag()
        {
            fx.Credentials.FailList = true;
            UninstallResult r = new SelfUninstaller(fx.Environment).Uninstall(new UninstallOptions { CurrentExePath = exe });
            Assert.IsTrue(r.CredentialsLeftOver);
            Assert.AreEqual(LauncherError.UninstallIncomplete, r.Code);
        }
    }
}
