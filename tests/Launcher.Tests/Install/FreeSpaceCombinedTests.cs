using System.IO;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Install;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.Install
{
    [TestClass]
    public class FreeSpaceCombinedTests
    {
        private const long Margin = AcWriter.FreeSpaceMargin;

        [TestMethod]
        public void SameVolume_RequiresTheSumOfBothPlusOneMargin()
        {
            string staging = @"C:\dw-test\staging";
            string game = @"C:\dw-test\ac";

            // Each need alone (600 + margin, 400 + margin) fits in 1000 + margin - 1, the sum does not.
            var ex = Assert.ThrowsExactly<LauncherException>(() =>
                AcWriter.RequireFreeSpaceCombined(new FixedDiskSpace(Margin + 999), staging, 600, "staging", game, 400, "game"));
            Assert.AreEqual(LauncherError.DiskFull, ex.Code);

            AcWriter.RequireFreeSpaceCombined(new FixedDiskSpace(Margin + 1000), staging, 600, "staging", game, 400, "game");
        }

        [TestMethod]
        public void SameVolume_IsDecidedByTheResolvedRoot_NotTheSpelling()
        {
            // A relative-looking spelling and a different case still resolve to the same root.
            string here = Path.GetFullPath(".");
            string root = Path.GetPathRoot(here);
            string upper = root.ToUpperInvariant() + @"dw-test\a";
            string lower = root.ToLowerInvariant() + @"dw-test\..\dw-test\b";

            Assert.ThrowsExactly<LauncherException>(() =>
                AcWriter.RequireFreeSpaceCombined(new FixedDiskSpace(Margin + 999), upper, 600, "a", lower, 400, "b"));
        }

        [TestMethod]
        public void DifferentVolumes_AreCheckedSeparately()
        {
            // Each volume only needs its own bytes: 600 + margin fits in 700 + margin, though the sum would not.
            AcWriter.RequireFreeSpaceCombined(new FixedDiskSpace(Margin + 700), @"C:\dw-test\staging", 600, "staging", @"D:\dw-test\ac", 600, "game");

            Assert.ThrowsExactly<LauncherException>(() =>
                AcWriter.RequireFreeSpaceCombined(new FixedDiskSpace(Margin + 599), @"C:\dw-test\staging", 600, "staging", @"D:\dw-test\ac", 600, "game"));
        }
    }
}
