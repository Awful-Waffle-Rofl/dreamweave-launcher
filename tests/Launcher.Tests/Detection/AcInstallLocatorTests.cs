using System.IO;
using DreamWeave.Launcher.Core.Detection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.Detection
{
    [TestClass]
    public class AcInstallLocatorTests
    {
        private const string Saved = @"D:\Games\AC";
        private const string ByGuid = @"C:\Turbine\Asheron's Call";
        private const string ByName = @"E:\Other\AC";
        private const string Default = @"C:\Default\AC";

        private static FakeFs FsWithClients(params string[] folders)
        {
            var fs = new FakeFs();
            foreach (string f in folders)
            {
                fs.SetFile(Path.Combine(f, "acclient.exe"), new byte[] { 1 });
            }

            return fs;
        }

        private static FakeRegistry Registry(string guidLocation, string nameLocation)
        {
            var reg = new FakeRegistry();
            if (guidLocation != null)
            {
                reg.AddUninstall(AcInstallLocator.UninstallGuid, "Asheron's Call", guidLocation);
            }

            if (nameLocation != null)
            {
                reg.AddUninstall("{SOME-OTHER-GUID}", "Asheron's Call", nameLocation);
            }

            return reg;
        }

        [TestMethod]
        public void Saved_Folder_Wins_When_Valid()
        {
            var loc = new AcInstallLocator(Registry(ByGuid, ByName), FsWithClients(Saved, ByGuid, ByName, Default), Default).LocateFrom(Saved);
            Assert.IsTrue(loc.Found);
            Assert.AreEqual(Saved, loc.Folder);
            Assert.AreEqual(AcInstallSource.SavedFolder, loc.Source);
        }

        [TestMethod]
        public void Invalid_Saved_Folder_Falls_Through_To_Guid_Key()
        {
            var loc = new AcInstallLocator(Registry(ByGuid, ByName), FsWithClients(ByGuid, ByName, Default), Default).LocateFrom(Saved);
            Assert.AreEqual(ByGuid, loc.Folder);
            Assert.AreEqual(AcInstallSource.UninstallKeyGuid, loc.Source);
            StringAssert.Contains(string.Join("|", loc.Notes), "saved folder");
        }

        [TestMethod]
        public void Guid_Key_Pointing_At_Missing_Folder_Falls_Through_To_DisplayName_Match()
        {
            var loc = new AcInstallLocator(Registry(ByGuid, ByName), FsWithClients(ByName, Default), Default).LocateFrom(null);
            Assert.AreEqual(ByName, loc.Folder);
            Assert.AreEqual(AcInstallSource.UninstallKeyDisplayName, loc.Source);
        }

        [TestMethod]
        public void DisplayName_Match_Ignores_Case_And_Typographic_Apostrophe()
        {
            var reg = new FakeRegistry();
            reg.AddUninstall("{X}", "ASHERON" + (char)0x2019 + "S CALL", ByName);
            var loc = new AcInstallLocator(reg, FsWithClients(ByName), Default).LocateFrom(null);
            Assert.AreEqual(AcInstallSource.UninstallKeyDisplayName, loc.Source);
        }

        [TestMethod]
        public void Unrelated_Uninstall_Entries_Are_Ignored()
        {
            var reg = new FakeRegistry();
            reg.AddUninstall("{X}", "Some Other Game", ByName);
            var loc = new AcInstallLocator(reg, FsWithClients(ByName), Default).LocateFrom(null);
            Assert.IsFalse(loc.Found);
            Assert.AreEqual(AcInstallSource.NotFound, loc.Source);
        }

        [TestMethod]
        public void Default_Folder_Is_Last_Resort()
        {
            var loc = new AcInstallLocator(Registry(null, null), FsWithClients(Default), Default).LocateFrom(null);
            Assert.AreEqual(Default, loc.Folder);
            Assert.AreEqual(AcInstallSource.DefaultFolder, loc.Source);
        }

        [TestMethod]
        public void Nothing_Valid_Returns_NotFound_With_Notes()
        {
            var loc = new AcInstallLocator(Registry(ByGuid, null), new FakeFs(), Default).LocateFrom(Saved);
            Assert.IsFalse(loc.Found);
            Assert.IsNull(loc.Folder);
            Assert.IsTrue(loc.Notes.Count >= 3);
        }

        [TestMethod]
        public void Quoted_And_Trailing_Slash_InstallLocation_Is_Normalized()
        {
            var reg = Registry("\"" + ByGuid + "\\\"", null);
            var loc = new AcInstallLocator(reg, FsWithClients(ByGuid), Default).LocateFrom(null);
            Assert.AreEqual(ByGuid, loc.Folder);
        }

        [TestMethod]
        public void Lower_Case_Drive_InstallLocation_Is_Found_And_Usable()
        {
            // The real registry value is "c:\Turbine\..." (lower-case drive).
            var reg = Registry(@"c:\Turbine\Asheron's Call", null);
            var loc = new AcInstallLocator(reg, FsWithClients(@"C:\Turbine\Asheron's Call"), Default).LocateFrom(null);
            Assert.IsTrue(loc.Found);
            Assert.AreEqual(AcInstallSource.UninstallKeyGuid, loc.Source);
            string exe = AcPathSafety.Resolve(loc.Folder, "acclient.exe");
            Assert.IsTrue(exe.EndsWith(@"\acclient.exe", System.StringComparison.OrdinalIgnoreCase));
        }

        [TestMethod]
        public void Relative_InstallLocation_Is_Rejected()
        {
            var reg = Registry(@"Turbine\AC", null);
            var loc = new AcInstallLocator(reg, FsWithClients(@"Turbine\AC"), Default).LocateFrom(null);
            Assert.IsFalse(loc.Found);
        }

        [TestMethod]
        public void ValidateFolder_Needs_Directory_And_Client_Exe()
        {
            var fs = FsWithClients(Saved);
            fs.AddDir(@"D:\Empty");
            var locator = new AcInstallLocator(new FakeRegistry(), fs, Default);
            Assert.IsTrue(locator.ValidateFolder(Saved));
            Assert.IsTrue(locator.ValidateFolder(Saved + "\\"));
            Assert.IsFalse(locator.ValidateFolder(@"D:\Empty"));
            Assert.IsFalse(locator.ValidateFolder(@"D:\Nope"));
            Assert.IsFalse(locator.ValidateFolder(null));
            Assert.IsFalse(locator.ValidateFolder("  "));
        }

        [TestMethod]
        public void Locate_Reads_Saved_Folder_From_State()
        {
            using (var dir = new TempDir())
            {
                var state = DetectionData.NewState(dir);
                state.Update(s => s.AcFolder = Saved);
                var loc = new AcInstallLocator(new FakeRegistry(), FsWithClients(Saved), Default).Locate(state);
                Assert.AreEqual(AcInstallSource.SavedFolder, loc.Source);
            }
        }

        [TestMethod]
        public void Real_Registry_Reader_Reads_Without_Throwing()
        {
            var reg = new RegistryReader();
            Assert.IsTrue(reg.GetSubKeyNames(@"SOFTWARE\Microsoft").Count > 0);
            Assert.IsNull(reg.GetStringValue(@"SOFTWARE\DreamWeaveLauncherTests\DoesNotExist", "x"));
            Assert.AreEqual(0, reg.GetSubKeyNames(@"SOFTWARE\DreamWeaveLauncherTests\DoesNotExist").Count);
        }
    }
}
