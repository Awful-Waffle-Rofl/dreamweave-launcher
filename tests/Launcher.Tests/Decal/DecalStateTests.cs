using System.IO;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.State;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.Decal
{
    [TestClass]
    public class DecalStateTests
    {
        [TestMethod]
        public void LaunchWithDecal_RoundTrips()
        {
            using (var dir = new TempDir())
            {
                var paths = new AppPaths(dir.Path);
                StateStore.Open(paths).Update(s => s.LaunchWithDecal = true);

                Assert.IsTrue(StateStore.Open(paths).Read(s => s.LaunchWithDecal));
            }
        }

        [TestMethod]
        public void OldStateFileWithoutTheField_LoadsAsFalse_AndKeepsUnknownFields()
        {
            using (var dir = new TempDir())
            {
                var paths = new AppPaths(dir.Path);
                Directory.CreateDirectory(paths.DataDir);
                File.WriteAllText(paths.StateFile, "{ \"highestManifestSerial\": 7, \"installId\": \"22222222-3333-4444-5555-666666666666\" }");

                var store = StateStore.Open(paths);
                Assert.IsFalse(store.Read(s => s.LaunchWithDecal), "an old state file has no field: Decal is off");

                store.Update(s => s.LaunchWithDecal = true);
                Assert.IsTrue(StateStore.Open(paths).Read(s => s.LaunchWithDecal));
                Assert.AreEqual(7L, StateStore.Open(paths).HighestManifestSerial);
            }
        }
    }
}
