using System;
using DreamWeave.Launcher.App;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.App
{
    [TestClass]
    public class StartupArgsTests
    {
        [TestMethod]
        public void NoArguments_IsNormal_TakesMutexAndHandsOver()
        {
            StartupArgs a = StartupArgs.Parse(new string[0]);
            Assert.AreEqual(StartupMode.Normal, a.Mode);
            Assert.IsTrue(a.UsesMutex);
            Assert.IsTrue(a.HandsOverToRunningInstance);
            Assert.AreEqual(StartupMode.Normal, StartupArgs.Parse(null).Mode);
        }

        [TestMethod]
        public void PlaceVerified_WithItsThreeArguments_SkipsMutex()
        {
            StartupArgs a = StartupArgs.Parse(new[] { "--place-verified", @"C:\Users\p\AppData\Local", @"C:\Turbine\Asheron's Call", "eor-dats" });
            Assert.AreEqual(StartupMode.PlaceVerified, a.Mode);
            Assert.AreEqual(@"C:\Users\p\AppData\Local", a.AppRoot);
            Assert.AreEqual(@"C:\Turbine\Asheron's Call", a.AcFolder);
            Assert.AreEqual("eor-dats", a.ComponentId);
            Assert.IsFalse(a.UsesMutex, "the elevated helper must never wait on the mutex its parent holds");
            Assert.IsFalse(a.HandsOverToRunningInstance);
        }

        [TestMethod]
        public void PlaceVerified_WithoutExactlyThreeArguments_IsInvalid()
        {
            Assert.AreEqual(StartupMode.Invalid, StartupArgs.Parse(new[] { "--place-verified" }).Mode);
            Assert.AreEqual(StartupMode.Invalid, StartupArgs.Parse(new[] { "--place-verified", @"C:\a", @"C:\ac" }).Mode);
            Assert.AreEqual(StartupMode.Invalid, StartupArgs.Parse(new[] { "--place-verified", @"C:\a", " ", "eor-dats" }).Mode);
            Assert.AreEqual(StartupMode.Invalid, StartupArgs.Parse(new[] { "--place-verified", @"C:\a", @"C:\ac", "eor-dats", "extra" }).Mode);
        }

        [TestMethod]
        public void RetiredElevatedCopy_IsInvalid()
        {
            StartupArgs a = StartupArgs.Parse(new[] { "--elevated-copy", @"C:\data\staging\elevated-task-1.json" });
            Assert.AreEqual(StartupMode.Invalid, a.Mode);
            Assert.IsFalse(a.UsesMutex);
            Assert.AreEqual(StartupMode.Invalid, StartupArgs.Parse(new[] { "--elevated-copy" }).Mode);
            Assert.AreEqual(StartupMode.Invalid, StartupArgs.Parse(new[] { "--elevated-copy", @"C:\a", @"C:\ac", "eor-dats" }).Mode, "the old switch is not an alias of the new one");
        }

        [TestMethod]
        public void Cleanup_FromSetup_DoesNotWait_AndShowsTheWindow()
        {
            StartupArgs a = StartupArgs.Parse(new[] { "--cleanup" });
            Assert.AreEqual(StartupMode.Cleanup, a.Mode);
            Assert.IsTrue(a.UsesMutex);
            Assert.IsFalse(a.HandsOverToRunningInstance);
            Assert.IsFalse(a.IsHandover);
            Assert.IsFalse(a.IsSilent);
            Assert.AreEqual(TimeSpan.Zero, a.MutexWait, "an open launcher window is reported at once");
        }

        [TestMethod]
        public void Cleanup_Silent_HasNoWindowFlagSet()
        {
            StartupArgs a = StartupArgs.Parse(new[] { "--cleanup", "--silent" });
            Assert.AreEqual(StartupMode.Cleanup, a.Mode);
            Assert.IsTrue(a.IsSilent);
            Assert.IsFalse(a.IsHandover);
            Assert.AreEqual(TimeSpan.FromSeconds(15), a.MutexWait, "the old launcher may still be shutting down");
        }

        [TestMethod]
        public void Cleanup_FromSettings_WaitsForTheHandover()
        {
            StartupArgs a = StartupArgs.Parse(new[] { "--cleanup", "--handover" });
            Assert.AreEqual(StartupMode.Cleanup, a.Mode);
            Assert.IsTrue(a.IsHandover);
            Assert.IsFalse(a.IsSilent);
            Assert.AreEqual(StartupArgs.HandoverWait, a.MutexWait);
            Assert.AreEqual(TimeSpan.FromSeconds(30), StartupArgs.HandoverWait);
        }

        [TestMethod]
        public void Cleanup_WithAnUnknownExtraArgument_IsInvalid()
        {
            Assert.AreEqual(StartupMode.Invalid, StartupArgs.Parse(new[] { "--cleanup", "--bogus" }).Mode);
            Assert.AreEqual(StartupMode.Invalid, StartupArgs.Parse(new[] { "--cleanup", "--silent", "--bogus" }).Mode);
        }

        [TestMethod]
        public void Silent_WithoutCleanup_IsInvalid()
        {
            Assert.AreEqual(StartupMode.Invalid, StartupArgs.Parse(new[] { "--silent" }).Mode);
        }

        [TestMethod]
        public void RetiredUninstallSwitch_IsInvalid()
        {
            Assert.AreEqual(StartupMode.Invalid, StartupArgs.Parse(new[] { "--uninstall" }).Mode);
            Assert.AreEqual(StartupMode.Invalid, StartupArgs.Parse(new[] { "--uninstall", "--handover" }).Mode);
        }
        [TestMethod]
        public void RetiredUpdatedSwitch_IsInvalid()
        {
            StartupArgs a = StartupArgs.Parse(new[] { "--updated" });
            Assert.AreEqual(StartupMode.Invalid, a.Mode);
            Assert.IsFalse(a.UsesMutex);
        }

        [TestMethod]
        public void Normal_DoesNotWait()
        {
            Assert.AreEqual(TimeSpan.Zero, StartupArgs.Parse(new string[0]).MutexWait);
        }

        [TestMethod]
        public void UnknownOrMiscasedSwitch_IsInvalid()
        {
            Assert.AreEqual(StartupMode.Invalid, StartupArgs.Parse(new[] { "--bogus" }).Mode);
            Assert.AreEqual(StartupMode.Invalid, StartupArgs.Parse(new[] { "--CLEANUP" }).Mode);
            Assert.IsFalse(StartupArgs.Parse(new[] { "--bogus" }).UsesMutex);
        }
    }
}
