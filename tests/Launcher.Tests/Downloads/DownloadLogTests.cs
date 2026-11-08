using System;
using System.IO;
using System.Linq;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Downloads;
using DreamWeave.Launcher.Core.Manifests;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.Downloads
{
    /// <summary>The rolling download log at <see cref="AppPaths.LogFile"/>.</summary>
    [TestClass]
    public class DownloadLogTests
    {
        private static DownloadAttemptRecord Failed(string message, Exception inner = null)
        {
            return new DownloadAttemptRecord
            {
                ComponentId = "ac-client",
                SourceIndex = 0,
                Transport = Transports.Mega,
                Host = "mega.nz",
                Outcome = DownloadAttemptOutcome.Failed,
                Bytes = 2 * 1024 * 1024,
                Duration = TimeSpan.FromSeconds(2),
                Error = new LauncherException(LauncherError.NetworkUnavailable, message, inner),
            };
        }

        [TestMethod]
        public void LogFile_LivesInTheDataFolder_WhichUninstallDeletesWhole()
        {
            var paths = new AppPaths(@"C:\Root");
            Assert.AreEqual(Path.Combine(paths.DataDir, "launcher.log"), paths.LogFile);
            Assert.AreEqual(paths.DataDir, Path.GetDirectoryName(paths.LogFile));
        }

        [TestMethod]
        public void Line_CarriesComponentTransportHostOutcomeBytesDurationAndTheExceptionChain()
        {
            using (var dir = new TempDir())
            {
                var log = new FileDownloadLog(Path.Combine(dir.Path, "launcher.log"), userProfile: @"C:\Users\someone");
                log.Record(Failed("mega.nz stopped sending data.", new IOException("reading C:\\Users\\someone\\AppData\\x.part failed")));
                string line = File.ReadAllLines(log.FilePath).Single();
                StringAssert.Contains(line, "component=ac-client");
                StringAssert.Contains(line, "source=0");
                StringAssert.Contains(line, "transport=mega");
                StringAssert.Contains(line, "host=mega.nz");
                StringAssert.Contains(line, "outcome=Failed");
                StringAssert.Contains(line, "bytes=2097152");
                StringAssert.Contains(line, "duration=2.0s");
                StringAssert.Contains(line, "rate=1.00MB/s");
                StringAssert.Contains(line, "DreamWeave.Launcher.Core.LauncherException(NetworkUnavailable): mega.nz stopped sending data.");
                StringAssert.Contains(line, "<- System.IO.IOException: reading %USERPROFILE%\\AppData\\x.part failed");
                Assert.IsFalse(line.IndexOf("someone", StringComparison.OrdinalIgnoreCase) >= 0, "the profile folder (the Windows user name) leaked: " + line);
            }
        }

        [TestMethod]
        public void PastTheCap_TheFileRollsOver_AndOnlyOneOldFileIsKept()
        {
            using (var dir = new TempDir())
            {
                var log = new FileDownloadLog(Path.Combine(dir.Path, "launcher.log"), maxBytes: 2000);
                for (int i = 0; i < 60; i++)
                {
                    log.Record(Failed("attempt " + i));
                }

                Assert.IsTrue(new FileInfo(log.FilePath).Length <= 2000, "live file over the cap");
                Assert.IsTrue(File.Exists(log.OldPath), "no old file kept");
                Assert.IsTrue(new FileInfo(log.OldPath).Length <= 2000, "old file over the cap");
                CollectionAssert.AreEquivalent(new[] { "launcher.log", "launcher.log.1" }, Directory.GetFiles(dir.Path).Select(Path.GetFileName).ToArray());
                StringAssert.Contains(File.ReadAllLines(log.FilePath).Last(), "attempt 59");
            }
        }

        [TestMethod]
        public void AnUnwritableLog_NeverThrows()
        {
            using (var dir = new TempDir())
            {
                string path = Path.Combine(dir.Path, "launcher.log");
                Directory.CreateDirectory(path);
                new FileDownloadLog(path).Record(Failed("x"));
            }
        }
    }
}
