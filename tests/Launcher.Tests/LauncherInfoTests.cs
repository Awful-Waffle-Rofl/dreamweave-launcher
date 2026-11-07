using DreamWeave.Launcher.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests
{
    [TestClass]
    public class LauncherInfoTests
    {
        [TestMethod]
        public void ProductName_IsDreamWeaveLauncher()
        {
            Assert.AreEqual("DreamWeave Launcher", LauncherInfo.ProductName);
        }

        [TestMethod]
        public void Version_ComesFromDirectoryBuildProps()
        {
            // 0.0.0 would mean no <Version> reached the assembly's informational version.
            Assert.AreNotEqual("0.0.0", LauncherInfo.Version);
            Assert.AreEqual(LauncherInfo.SemanticVersion.ToString(), LauncherInfo.Version);
            Assert.IsFalse(LauncherInfo.Version.Contains("+"), "build metadata must be dropped");
        }
    }
}
