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
    }
}
