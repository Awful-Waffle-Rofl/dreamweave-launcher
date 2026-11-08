using System;
using System.Collections.Generic;
using System.ComponentModel;
using DreamWeave.Launcher.App;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Telemetry;
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

        [TestMethod]
        [DataRow("https://example.test/caf\u00e9", "https://example.test/caf%C3%A9")]
        [DataRow("https://example.test/a|b", "https://example.test/a%7Cb")]
        [DataRow("https://EXAMPLE.test/x", "https://example.test/x")]
        public void TheEscapedAbsoluteUri_NotTheRawString_IsStarted(string raw, string expected)
        {
            var processes = new FakeProcessLauncher();

            Assert.AreEqual(LauncherError.None, DownloadPageOpener.Open(processes, raw));
            Assert.AreEqual(expected, processes.Started[0].FileName);
        }

        [TestMethod]
        [DataRow("https://example.test/x\" -arg")]
        [DataRow("https://example.test/x\r\nHost: evil")]
        [DataRow("https://example.test/x\ty")]
        [DataRow("https://example.test/x y")]
        [DataRow(" https://example.test/x")]
        [DataRow("https://example.test/x\u0001")]
        public void QuoteWhitespaceOrControlCharacter_IsRefused(string url)
        {
            var processes = new FakeProcessLauncher();

            Assert.AreEqual(LauncherError.ManifestInvalid, DownloadPageOpener.Open(processes, url));
            Assert.AreEqual(0, processes.Started.Count);
        }

        [TestMethod]
        public void OpenAndRecord_Success_RecordsStartedThenSucceeded()
        {
            var processes = new FakeProcessLauncher();
            var sink = new RecordingSink();

            LauncherError code = DownloadPageOpener.OpenAndRecord(processes, sink, "https://example.test/get");

            Assert.AreEqual(LauncherError.None, code);
            CollectionAssert.AreEqual(new[] { "launcher-update:Started:None", "launcher-update:Succeeded:None" }, sink.Lines);
        }

        [TestMethod]
        public void OpenAndRecord_ShellFailure_RecordsStartedThenFailedUnknown()
        {
            var processes = new FakeProcessLauncher { StartThrow = new Win32Exception("no browser") };
            var sink = new RecordingSink();

            LauncherError code = DownloadPageOpener.OpenAndRecord(processes, sink, "https://example.test/get");

            Assert.AreEqual(LauncherError.Unknown, code);
            CollectionAssert.AreEqual(new[] { "launcher-update:Started:None", "launcher-update:Failed:Unknown" }, sink.Lines);
        }

        [TestMethod]
        public void OpenAndRecord_RefusedUrl_RecordsFailedManifestInvalid_AndStartsNothing()
        {
            var processes = new FakeProcessLauncher();
            var sink = new RecordingSink();

            LauncherError code = DownloadPageOpener.OpenAndRecord(processes, sink, "http://example.test/get");

            Assert.AreEqual(LauncherError.ManifestInvalid, code);
            Assert.AreEqual(0, processes.Started.Count);
            CollectionAssert.AreEqual(new[] { "launcher-update:Started:None", "launcher-update:Failed:ManifestInvalid" }, sink.Lines);
        }

        [TestMethod]
        public void FailureMessage_ShellFailure_ShowsTheValidatedUrl()
        {
            string text = DownloadPageOpener.FailureMessage(LauncherError.Unknown, "https://example.test/caf\u00e9");

            Assert.AreEqual("Could not open your browser. Visit https://example.test/caf%C3%A9 to download the latest launcher.", text);
        }

        [TestMethod]
        public void FailureMessage_RefusedUrl_IsNeverEchoed()
        {
            string text = DownloadPageOpener.FailureMessage(LauncherError.ManifestInvalid, "http://evil.test/x");

            Assert.AreEqual(ErrorMessages.For(LauncherError.ManifestInvalid), text);
            Assert.IsFalse(text.Contains("evil.test"));
        }

        private sealed class RecordingSink : ITelemetrySink
        {
            public List<string> Lines { get; } = new List<string>();

            public void Record(string step, TelemetryOutcome outcome, LauncherError error = LauncherError.None, TimeSpan? duration = null)
            {
                Lines.Add(step + ":" + outcome + ":" + error);
            }

            public void RecordComponent(string componentId, TelemetryOutcome outcome, LauncherError error = LauncherError.None, TimeSpan? duration = null)
            {
                Lines.Add(componentId + ":" + outcome + ":" + error);
            }
        }    }
}