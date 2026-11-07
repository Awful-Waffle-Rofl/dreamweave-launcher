using System;
using System.Collections.Generic;
using System.IO;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Downloads;
using DreamWeave.Launcher.Core.Install;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.Install
{
    [TestClass]
    public class FreeSpaceCombinedTests
    {
        private const long Margin = AcWriter.FreeSpaceMargin;

        /// <summary>Free bytes keyed by the exact folder asked about (the folders in these tests exist).</summary>
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

        private static void Run(IDiskSpace disk, string first, long firstBytes, string second, long secondBytes, Func<string, string> volumeId)
        {
            AcWriter.RequireFreeSpaceCombined(disk, first, firstBytes, "staging", second, secondBytes, "game", volumeId);
        }

        [TestMethod]
        public void SameVolume_RequiresTheSumOfBothPlusOneMargin()
        {
            using (var dir = new TempDir())
            {
                string staging = Path.Combine(dir.Path, "staging");
                string game = Path.Combine(dir.Path, "ac");
                Directory.CreateDirectory(staging);
                Directory.CreateDirectory(game);
                Func<string, string> oneVolume = f => "vol-1";

                // Each need alone (600 + margin, 400 + margin) fits in 1000 + margin - 1, the sum does not.
                var tight = new KeyedDiskSpace().Set(staging, Margin + 999).Set(game, Margin + 999);
                var ex = Assert.ThrowsExactly<LauncherException>(() => Run(tight, staging, 600, game, 400, oneVolume));
                Assert.AreEqual(LauncherError.DiskFull, ex.Code);

                var enough = new KeyedDiskSpace().Set(staging, Margin + 1000).Set(game, Margin + 1000);
                Run(enough, staging, 600, game, 400, oneVolume);
            }
        }

        [TestMethod]
        public void MountPointOntoAnotherVolume_SecondFolderShortOfSpace_StillThrowsDiskFull()
        {
            using (var dir = new TempDir())
            {
                // Same lexical root, different volumes (the game folder is a mount point). The first
                // volume would hold the sum; the second cannot hold its own bytes.
                string staging = Path.Combine(dir.Path, "staging");
                string game = Path.Combine(dir.Path, "ac");
                Directory.CreateDirectory(staging);
                Directory.CreateDirectory(game);
                var disk = new KeyedDiskSpace().Set(staging, Margin + 10000).Set(game, Margin + 399);

                var ex = Assert.ThrowsExactly<LauncherException>(() => Run(disk, staging, 600, game, 400, f => f));

                Assert.AreEqual(LauncherError.DiskFull, ex.Code);
            }
        }

        [TestMethod]
        public void UnresolvedVolumeIdentity_IsTreatedAsDifferentVolumes()
        {
            using (var dir = new TempDir())
            {
                string staging = Path.Combine(dir.Path, "staging");
                string game = Path.Combine(dir.Path, "ac");
                Directory.CreateDirectory(staging);
                Directory.CreateDirectory(game);

                // Singles pass, the sum would not: no combined check may run when identity is unknown.
                var disk = new KeyedDiskSpace().Set(staging, Margin + 700).Set(game, Margin + 700);
                Run(disk, staging, 600, game, 600, f => null);
            }
        }

        [TestMethod]
        public void RealVolumeIdentity_MatchesWithinOneVolume_AndForMissingChildren()
        {
            using (var dir = new TempDir())
            {
                string a = Path.Combine(dir.Path, "a");
                Directory.CreateDirectory(a);

                string id = AcWriter.VolumeIdentity(a);

                Assert.IsNotNull(id, "the temp folder's volume must resolve");
                StringAssert.StartsWith(id, @"\\?\Volume{");
                Assert.AreEqual(id, AcWriter.VolumeIdentity(Path.Combine(dir.Path, "missing", "deeper")));
            }
        }

        [TestMethod]
        public void MarginAddition_IsChecked()
        {
            using (var dir = new TempDir())
            {
                Assert.ThrowsExactly<OverflowException>(() => AcWriter.RequireFreeSpace(new FixedDiskSpace(long.MaxValue), dir.Path, long.MaxValue, "anywhere"));
            }
        }
    }
}
