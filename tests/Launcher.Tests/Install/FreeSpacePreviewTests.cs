using System;
using System.Collections.Generic;
using System.IO;
using DreamWeave.Launcher.Core.Downloads;
using DreamWeave.Launcher.Core.Install;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.Install
{
    /// <summary>The UI's non-throwing preview must agree with RequireFreeSpaceCombined's rules.</summary>
    [TestClass]
    public class FreeSpacePreviewTests
    {
        private const long Margin = AcWriter.FreeSpaceMargin;

        private sealed class KeyedDiskSpace : IDiskSpace
        {
            private readonly Dictionary<string, long> _free = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            public KeyedDiskSpace Set(string folder, long free)
            {
                _free[folder] = free;
                return this;
            }

            public long GetFreeBytes(string directory) => _free[directory];
        }

        [TestMethod]
        public void SameVolume_NeedsTheSumPlusOneMargin()
        {
            using (var dir = new TempDir())
            {
                string staging = Path.Combine(dir.Path, "staging");
                string game = Path.Combine(dir.Path, "ac");
                Directory.CreateDirectory(staging);
                Directory.CreateDirectory(game);
                Func<string, string> oneVolume = f => "vol-1";

                FreeSpacePreview tight = AcWriter.PreviewFreeSpace(new KeyedDiskSpace().Set(staging, Margin + 999).Set(game, Margin + 999), staging, 600, game, 400, oneVolume);
                Assert.IsTrue(tight.SameVolume);
                Assert.AreEqual(Margin + 1000, tight.CombinedNeeded);
                Assert.AreEqual(Margin + 600, tight.FirstNeeded);
                Assert.AreEqual(Margin + 400, tight.SecondNeeded);
                Assert.IsFalse(tight.Enough, "each alone fits, the sum does not");

                FreeSpacePreview enough = AcWriter.PreviewFreeSpace(new KeyedDiskSpace().Set(staging, Margin + 1000).Set(game, Margin + 1000), staging, 600, game, 400, oneVolume);
                Assert.IsTrue(enough.Enough);
            }
        }

        [TestMethod]
        public void SeparateVolumes_CheckEachOnItsOwn()
        {
            using (var dir = new TempDir())
            {
                string staging = Path.Combine(dir.Path, "staging");
                string game = Path.Combine(dir.Path, "ac");
                Directory.CreateDirectory(staging);
                Directory.CreateDirectory(game);
                Func<string, string> twoVolumes = f => f.EndsWith("staging", StringComparison.OrdinalIgnoreCase) ? "vol-1" : "vol-2";

                FreeSpacePreview p = AcWriter.PreviewFreeSpace(new KeyedDiskSpace().Set(staging, Margin + 600).Set(game, Margin + 399), staging, 600, game, 400, twoVolumes);
                Assert.IsFalse(p.SameVolume);
                Assert.IsNull(p.CombinedNeeded);
                Assert.IsFalse(p.Enough, "the game volume is one byte short");

                p = AcWriter.PreviewFreeSpace(new KeyedDiskSpace().Set(staging, Margin + 600).Set(game, Margin + 400), staging, 600, game, 400, twoVolumes);
                Assert.IsTrue(p.Enough);
            }
        }

        [TestMethod]
        public void UnresolvedVolume_IsTreatedAsSeparate_AndMissingFolderUsesNearestExisting()
        {
            using (var dir = new TempDir())
            {
                string staging = Path.Combine(dir.Path, "staging");
                Directory.CreateDirectory(staging);
                string notYet = Path.Combine(dir.Path, "Turbine", "Asheron's Call");

                FreeSpacePreview p = AcWriter.PreviewFreeSpace(new KeyedDiskSpace().Set(staging, Margin + 10).Set(dir.Path, Margin + 10), staging, 10, notYet, 10, f => null);
                Assert.IsFalse(p.SameVolume);
                Assert.AreEqual(Margin + 10, p.SecondFree, "free space of the nearest existing folder");
                Assert.IsTrue(p.Enough);
            }
        }

        [TestMethod]
        public void RealVolumeIdentity_SameTempVolume_IsSameVolume()
        {
            using (var dir = new TempDir())
            {
                string a = Path.Combine(dir.Path, "a");
                string b = Path.Combine(dir.Path, "b");
                Directory.CreateDirectory(a);
                Directory.CreateDirectory(b);
                FreeSpacePreview p = AcWriter.PreviewFreeSpace(new KeyedDiskSpace().Set(a, long.MaxValue / 4).Set(b, long.MaxValue / 4), a, 1, b, 1);
                Assert.IsTrue(p.SameVolume);
            }
        }
    }
}
