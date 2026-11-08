using System;
using System.Collections.Generic;
using System.Threading;
using DreamWeave.Launcher.App;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Cleanup;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.Cleanup
{
    /// <summary>The values the Inno Setup script is checked against, the HKCU read, and the setup guard mutex.</summary>
    [TestClass]
    public class SetupIntegrationTests
    {
        private sealed class FakeUserRegistry : IUserRegistryReader
        {
            public Dictionary<string, string> Values { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public string GetStringValue(string subKeyPath, string valueName)
            {
                string v;
                return Values.TryGetValue(subKeyPath + "|" + valueName, out v) ? v : null;
            }
        }

        [TestMethod]
        public void SetupConstants_AreTheAgreedValues()
        {
            Assert.AreEqual("DreamWeaveLauncherSetupGuard", SetupConstants.SetupMutexName);
            Assert.AreEqual("--cleanup", SetupConstants.CleanupSwitch);
            Assert.AreEqual("--silent", SetupConstants.SilentSwitch);
            Assert.AreEqual("DreamWeave Launcher", SetupConstants.InstallFolderName);
            Guid parsed;
            parsed = Guid.Parse(SetupConstants.SetupAppId);
            Assert.AreEqual(SetupConstants.SetupAppId, parsed.ToString("B").ToUpperInvariant(), "braces are part of the id");
        }

        [TestMethod]
        public void SetupUninstallKey_IsTheInnoIs1KeyUnderHkcu()
        {
            string expected = string.Join(@"\", "Software", "Microsoft", "Windows", "CurrentVersion", "Uninstall", Guid.Parse(SetupConstants.SetupAppId).ToString("B").ToUpperInvariant() + "_is1");
            Assert.AreEqual(SetupConstants.SetupUninstallKeyPath, expected);
        }

        [TestMethod]
        public void FindUninstallString_ReadsTheSetupKey()
        {
            var reg = new FakeUserRegistry();
            reg.Values[SetupConstants.SetupUninstallKeyPath + "|UninstallString"] = "\"C:\\Program Files\\DreamWeave Launcher\\unins000.exe\"";
            Assert.AreEqual("\"C:\\Program Files\\DreamWeave Launcher\\unins000.exe\"", SetupUninstallLocator.FindUninstallString(reg));
        }

        [TestMethod]
        public void FindUninstallString_AbsentOrBlank_IsNull()
        {
            var reg = new FakeUserRegistry();
            Assert.IsNull(SetupUninstallLocator.FindUninstallString(reg));
            reg.Values[SetupConstants.SetupUninstallKeyPath + "|UninstallString"] = "  ";
            Assert.IsNull(SetupUninstallLocator.FindUninstallString(reg));
        }

        [TestMethod]
        public void SetupGuard_HoldsTheNamedMutexUntilReleased()
        {
            SetupGuard.Release();
            try
            {
                Mutex probe;
                Assert.IsFalse(Mutex.TryOpenExisting(SetupConstants.SetupMutexName, out probe), "fixture: nothing holds the guard yet");

                SetupGuard.Hold();
                SetupGuard.Hold();
                Assert.IsTrue(SetupGuard.IsHeld);
                Assert.IsTrue(Mutex.TryOpenExisting(SetupConstants.SetupMutexName, out probe), "an AppMutex check must see it");
                probe.Dispose();

                SetupGuard.Release();
                SetupGuard.Release();
                Assert.IsFalse(SetupGuard.IsHeld);
                Assert.IsFalse(Mutex.TryOpenExisting(SetupConstants.SetupMutexName, out probe), "released: the uninstaller can proceed");
            }
            finally
            {
                SetupGuard.Release();
            }
        }
    }
}