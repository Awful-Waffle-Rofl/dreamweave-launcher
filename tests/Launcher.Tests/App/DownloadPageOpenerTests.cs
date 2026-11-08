using System;
using System.ComponentModel;
using DreamWeave.Launcher.App;
using DreamWeave.Launcher.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.App
{
    [TestClass]
    public class DownloadPageOpenerTests
    {
        [TestMethod]
        public void ValidHttpsPage_IsStartedThroughTheShell()
        {
            var processes = new FakeProcessLauncher();

            LauncherError code = DownloadPageOpener.Open(processes, "https://example.test/releases/latest");

            Assert.AreEqual(LauncherError.None, code);
            Assert.AreEqual(1, processes.Started.Count);
            Assert.IsTrue(processes.Started[0].UseShellExecute, "the default browser is reached through the shell");
            Assert.AreEqual("https://example.test/releases/latest", processes.Started[0].FileName);
            Assert.AreEqual(0, processes.Ran.Count);
        }

        [TestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("http://example.test/get")]
        [DataRow("file:///C:/Windows/System32/calc.exe")]
        [DataRow("C:\\Windows\\System32\\calc.exe")]
        [DataRow("calc.exe")]
        [DataRow("ms-settings:")]
        public void AnythingButAbsoluteHttps_IsRefusedAndNothingStarts(string url)
        {
            var processes = new FakeProcessLauncher();

            LauncherError code = DownloadPageOpener.Open(processes, url);

            Assert.AreEqual(LauncherError.ManifestInvalid, code);
            Assert.AreEqual(0, processes.Started.Count, "a refused URL must never reach the shell");
        }

        [TestMethod]
        public void ShellFailure_IsReportedAsUnknown()
        {
            var processes = new FakeProcessLauncher { StartThrow = new Win32Exception("no browser") };

            Assert.AreEqual(LauncherError.Unknown, DownloadPageOpener.Open(processes, "https://example.test/get"));
            Assert.AreEqual(1, processes.Started.Count);
        }
    }
}