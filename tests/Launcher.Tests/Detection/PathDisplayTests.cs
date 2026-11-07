using DreamWeave.Launcher.Core.Detection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.Detection
{
    [TestClass]
    public class PathDisplayTests
    {
        [DataTestMethod]
        [DataRow(@"\\?\UNC\server\share\ac", @"\\server\share\ac")]
        [DataRow(@"\\?\unc\server\share", @"\\server\share")]
        [DataRow(@"\\?\C:\Turbine\ac", @"C:\Turbine\ac")]
        [DataRow(@"C:\Turbine\ac", @"C:\Turbine\ac")]
        [DataRow(@"\\server\share\ac", @"\\server\share\ac")]
        [DataRow("", "")]
        public void ToDisplayPath_StripsExtendedPrefixes(string input, string expected)
        {
            Assert.AreEqual(expected, AcPathSafety.ToDisplayPath(input));
        }

        [TestMethod]
        public void ToDisplayPath_Null_IsNull()
        {
            Assert.IsNull(AcPathSafety.ToDisplayPath(null));
        }

        [DataTestMethod]
        [DataRow(@"\\server\share\ac", true)]
        [DataRow(@"\\?\UNC\server\share\ac", true)]
        [DataRow(@"\\?\C:\ac", false)]
        [DataRow(@"\\.\C:\ac", false)]
        [DataRow(@"C:\ac", false)]
        public void IsNetworkPath_RecognisesUncForms(string path, bool expected)
        {
            Assert.AreEqual(expected, AcPathSafety.IsNetworkPath(path));
        }

        [DataTestMethod]
        [DataRow(@"\\server\share\0.part")]
        [DataRow(@"\\?\UNC\server\share\0.part")]
        public void NetworkPath_RefusalSaysNetworkPath(string path)
        {
            string reason = AcPathSafety.CheckLocalFixedPath(path);
            Assert.IsNotNull(reason);
            StringAssert.Contains(reason, "network path");
        }

        [DataTestMethod]
        [DataRow(@"\\?\C:\x\0.part")]
        [DataRow(@"\\.\C:\x\0.part")]
        public void DevicePath_RefusalDoesNotSayNetworkPath(string path)
        {
            string reason = AcPathSafety.CheckLocalFixedPath(path);
            Assert.IsNotNull(reason);
            Assert.IsFalse(reason.Contains("network path"), reason);
        }
    }
}
