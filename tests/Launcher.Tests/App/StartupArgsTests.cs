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
        public void ElevatedCopy_WithTask_SkipsMutex()
        {
            StartupArgs a = StartupArgs.Parse(new[] { "--elevated-copy", @"C:\data\staging\elevated-task-1.json" });
            Assert.AreEqual(StartupMode.ElevatedCopy, a.Mode);
            Assert.AreEqual(@"C:\data\staging\elevated-task-1.json", a.TaskPath);
            Assert.IsFalse(a.UsesMutex, "the elevated helper must never wait on the mutex its parent holds");
            Assert.IsFalse(a.HandsOverToRunningInstance);
        }

        [TestMethod]
        public void ElevatedCopy_WithoutTask_IsInvalid()
        {
            Assert.AreEqual(StartupMode.Invalid, StartupArgs.Parse(new[] { "--elevated-copy" }).Mode);
            Assert.AreEqual(StartupMode.Invalid, StartupArgs.Parse(new[] { "--elevated-copy", " " }).Mode);
        }

        [TestMethod]
        public void Uninstall_FromAppsAndFeatures_DoesNotWait()
        {
            StartupArgs a = StartupArgs.Parse(new[] { "--uninstall" });
            Assert.AreEqual(StartupMode.Uninstall, a.Mode);
            Assert.IsTrue(a.UsesMutex);
            Assert.IsFalse(a.HandsOverToRunningInstance);
            Assert.IsFalse(a.IsHandover);
            Assert.AreEqual(TimeSpan.Zero, a.MutexWait, "an open launcher window is reported at once");
        }

        [TestMethod]
        public void Uninstall_FromSettings_WaitsForTheHandover()
        {
            StartupArgs a = StartupArgs.Parse(new[] { "--uninstall", "--handover" });
            Assert.AreEqual(StartupMode.Uninstall, a.Mode);
            Assert.IsTrue(a.IsHandover);
            Assert.AreEqual(StartupArgs.HandoverWait, a.MutexWait);
            Assert.AreEqual(TimeSpan.FromSeconds(30), StartupArgs.HandoverWait);
        }

        [TestMethod]
        public void Updated_WaitsForMutex_DoesNotHandOver()
        {
            StartupArgs a = StartupArgs.Parse(new[] { "--updated" });
            Assert.AreEqual(StartupMode.Updated, a.Mode);
            Assert.IsTrue(a.UsesMutex);
            Assert.IsFalse(a.HandsOverToRunningInstance, "--updated must wait for the old process, not hand over to it");
            Assert.AreEqual(StartupArgs.HandoverWait, a.MutexWait);
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
            Assert.AreEqual(StartupMode.Invalid, StartupArgs.Parse(new[] { "--UNINSTALL" }).Mode);
            Assert.IsFalse(StartupArgs.Parse(new[] { "--bogus" }).UsesMutex);
        }
    }
}
