using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Detection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.Detection
{
    [TestClass]
    public class AcPathSafetyTests
    {
        private const string Root = @"C:\Turbine\Asheron's Call";

        [DataTestMethod]
        [DataRow("acclient.exe")]
        [DataRow("client_portal.dat")]
        [DataRow("Plugins/Sub Dir/file.name.with.dots.txt")]
        [DataRow(@"a\b\c.dat")]
        [DataRow("CONSOLE.txt")]
        [DataRow("comx.dat")]
        [DataRow("LPT10.txt")]
        [DataRow("..foo")]
        public void Valid_Paths_Resolve_Under_The_Folder(string rel)
        {
            string full = AcPathSafety.Resolve(Root, rel);
            Assert.IsTrue(full.StartsWith(Root + "\\", System.StringComparison.OrdinalIgnoreCase), full);
            Assert.IsTrue(AcPathSafety.IsSafeRelativePath(rel));
        }

        [TestMethod]
        public void Nested_Path_Uses_Backslashes()
        {
            Assert.AreEqual(Root + @"\Plugins\x\y.dll", AcPathSafety.Resolve(Root, "Plugins/x/y.dll"));
        }

        [TestMethod]
        public void Folder_With_Trailing_Slash_Works()
        {
            Assert.AreEqual(Root + @"\a.dat", AcPathSafety.Resolve(Root + "\\", "a.dat"));
        }

        [DataTestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("   ")]
        [DataRow(@"\Windows\System32\x.dll")]
        [DataRow("/etc/passwd")]
        [DataRow(@"C:\Windows\x.dll")]
        [DataRow("C:x.dll")]
        [DataRow("a.dat:stream")]
        [DataRow(@"\\server\share\x")]
        [DataRow("../x.dat")]
        [DataRow(@"..\x.dat")]
        [DataRow(@"a\..\..\x.dat")]
        [DataRow(@"a\..")]
        [DataRow("CON")]
        [DataRow("con")]
        [DataRow("PRN")]
        [DataRow("AUX")]
        [DataRow("NUL")]
        [DataRow("nul.txt")]
        [DataRow("COM1")]
        [DataRow("COM9.log")]
        [DataRow("LPT1")]
        [DataRow("LPT9.dat")]
        [DataRow(@"sub\CON.dat")]
        [DataRow(@"CON\x.dat")]
        [DataRow("CON .txt")]
        [DataRow("file.")]
        [DataRow("file ")]
        [DataRow(@"dir.\file.dat")]
        [DataRow(@"dir \file.dat")]
        [DataRow(".")]
        [DataRow("COM\u00B9")]
        [DataRow("com\u00B2.txt")]
        [DataRow("COM\u00B3")]
        [DataRow("LPT\u00B9.dat")]
        [DataRow("sub\\LPT\u00B2")]
        [DataRow("LPT\u00B3")]
        [DataRow("*.dat")]
        [DataRow("client_?.dat")]
        [DataRow("a<b.dat")]
        [DataRow("a|b.dat")]
        [DataRow("a\"b.dat")]
        [DataRow("a\tb.dat")]
        [DataRow("a//b.dat")]
        [DataRow("a/")]
        public void Unsafe_Paths_Are_Rejected_As_ManifestInvalid(string rel)
        {
            LauncherException ex = Assert.ThrowsExactly<LauncherException>(() => AcPathSafety.Resolve(Root, rel));
            Assert.AreEqual(LauncherError.ManifestInvalid, ex.Code);
            Assert.IsFalse(AcPathSafety.IsSafeRelativePath(rel));
        }

        [TestMethod]
        public void Empty_Folder_Is_An_Argument_Error()
        {
            Assert.ThrowsExactly<System.ArgumentException>(() => AcPathSafety.Resolve("", "a.dat"));
        }
    }
}
