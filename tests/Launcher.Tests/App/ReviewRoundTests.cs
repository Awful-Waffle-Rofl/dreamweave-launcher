using System;
using System.IO;
using System.Linq;
using System.Threading;
using DreamWeave.Launcher.App;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Install;
using DreamWeave.Launcher.Core.Manifests;
using DreamWeave.Launcher.Core.Cleanup;
using DreamWeave.Launcher.Core.Update;
using DreamWeave.Launcher.Tests.Detection;
using DreamWeave.Launcher.Tests.Install;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.App
{
    [TestClass]
    public class AppServicesShutdownTests
    {
        [TestMethod]
        public void ShutdownTwice_FlushesOnce()
        {
            using (var dir = new TempDir())
            {
                AppServices services = AppServices.Create(new AppPaths(dir.Path), Path.Combine(dir.Path, "DreamWeaveLauncher.exe"));
                int flushes = 0;
                services.FlushForShutdown = () => flushes++;
                services.Shutdown();
                services.Shutdown();
                services.Dispose();
                Assert.AreEqual(1, flushes);
            }
        }
    }

    [TestClass]
    public class UninstallReportTests
    {
        private static CleanupResult Result(string[] failures, string[] leftovers, bool credentialsLeft, AcClientUninstallOutcome ac = AcClientUninstallOutcome.NotRequested)
        {
            return new CleanupResult(failures, ac, null, 0, leftovers, credentialsLeft);
        }

        [TestMethod]
        public void Incomplete_ShowsCodeHeaderFoldersAndCredentialLine_NeverRawFailureText()
        {
            string data = @"C:\Users\p\AppData\Local\DreamWeave";
            CleanupResult r = Result(
                new[]
                {
                    data + @"\state.json: The process cannot access the file because it is being used by another process.",
                    "The saved password for 'alice' could not be removed: Element not found.",
                },
                new[] { data },
                true);
            var lines = UninstallReport.Lines(r);
            CollectionAssert.AreEqual(
                new[] { ErrorMessages.For(LauncherError.UninstallIncomplete), UiText.UninstallLeftOver, data, UiText.UninstallCredentialsLeft },
                lines.ToArray());
            string all = string.Join("\n", lines);
            Assert.IsFalse(all.Contains("alice"), "account name leaked");
            Assert.IsFalse(all.Contains("being used by another process"), "exception text leaked");
            Assert.IsFalse(all.Contains("Element not found"), "exception text leaked");
            Assert.AreEqual("Some saved passwords could not be removed. Open Windows Credential Manager and remove entries starting with DreamWeave/.", UiText.UninstallCredentialsLeft);
            Assert.IsFalse(UninstallReport.CloseImmediately(r));
        }

        [TestMethod]
        public void Complete_ClosesAtOnce_UnlessTheClientNeedsMentioning()
        {
            Assert.IsTrue(UninstallReport.CloseImmediately(Result(new string[0], new string[0], false)));
            CleanupResult notFound = Result(new string[0], new string[0], false, AcClientUninstallOutcome.NotFound);
            Assert.IsFalse(UninstallReport.CloseImmediately(notFound));
            CollectionAssert.AreEqual(new[] { UiText.UninstallDone, UiText.UninstallClientNotFound }, UninstallReport.Lines(notFound).ToArray());
            Assert.AreEqual(ErrorMessages.For(LauncherError.Unknown), UninstallReport.Lines(null).Single());
            Assert.IsFalse(UninstallReport.CloseImmediately(null));
        }
    }

    [TestClass]
    public class LocationPresenterTests
    {
        private static FreeSpacePreview Preview(bool same, long firstFree, long firstNeed, long secondFree, long secondNeed, long? combined)
        {
            return new FreeSpacePreview(same, firstFree, firstNeed, secondFree, secondNeed, combined);
        }

        [TestMethod]
        public void Check_RefusesNetworkAndRelativePaths_WithCoreWording()
        {
            var p = new LocationPresenter(new FakeFs(), new FixedDiskSpace(0));
            FolderCheck unc = p.Check(@"\\server\share\AC");
            Assert.IsFalse(unc.Usable);
            StringAssert.Contains(unc.Message, "it is a network path");
            Assert.IsFalse(p.Check(@"Turbine\AC").Usable);
            Assert.IsFalse(p.Check(string.Empty).Usable);
        }

        [TestMethod]
        public void Check_ReportsWhetherTheClientIsThere()
        {
            var fs = new FakeFs();
            fs.AddDir(@"C:\Games\AC");
            fs.SetFile(@"C:\Games\AC\acclient.exe", new byte[] { 1 });
            var p = new LocationPresenter(fs, new FixedDiskSpace(0));
            FolderCheck found = p.Check(@"C:\Games\AC");
            Assert.IsTrue(found.Usable && found.HasClient);
            Assert.AreEqual(UiText.LocationFound, found.Message);
            FolderCheck fresh = p.Check(@"C:\Games\New");
            Assert.IsTrue(fresh.Usable);
            Assert.IsFalse(fresh.HasClient);
            Assert.AreEqual(UiText.LocationNotInstalled, fresh.Message);
        }

        [TestMethod]
        public void FreeSpace_SameVolume_UsesTheCombinedNeed_AndPassesDownloadsAndRest()
        {
            long gotDownloads = -1, gotRest = -1;
            var p = new LocationPresenter(new FakeFs(), new FixedDiskSpace(0), (a, ab, b, bb) =>
            {
                gotDownloads = ab;
                gotRest = bb;
                return Preview(true, 5L << 30, 0, 5L << 30, 0, 4L << 30);
            });
            string text = p.FreeSpaceText("staging", "ac", 4L << 30, 1L << 30);
            Assert.AreEqual(1L << 30, gotDownloads);
            Assert.AreEqual(3L << 30, gotRest);
            Assert.AreEqual(string.Format(UiText.FreeSpaceOkFormat, "5.0 GB", "4.0 GB"), text);

            p = new LocationPresenter(new FakeFs(), new FixedDiskSpace(0), (a, ab, b, bb) => Preview(true, 3L << 30, 0, 3L << 30, 0, 4L << 30));
            Assert.AreEqual(string.Format(UiText.FreeSpaceLowFormat, "3.0 GB", "4.0 GB"), p.FreeSpaceText("s", "a", 4L << 30, 1L << 30));
        }

        [TestMethod]
        public void FreeSpace_SeparateVolumes_ReportsTheShortVolume()
        {
            var p = new LocationPresenter(new FakeFs(), new FixedDiskSpace(0), (a, ab, b, bb) => Preview(false, 2L << 30, 1L << 30, 1L << 30, 3L << 30, null));
            Assert.AreEqual(string.Format(UiText.FreeSpaceLowFormat, "1.0 GB", "3.0 GB"), p.FreeSpaceText("s", "a", 4L << 30, 1L << 30));

            p = new LocationPresenter(new FakeFs(), new FixedDiskSpace(0), (a, ab, b, bb) => Preview(false, 2L << 30, 1L << 30, 4L << 30, 3L << 30, null));
            Assert.AreEqual(string.Format(UiText.FreeSpaceSplitFormat, "2.0 GB", "1.0 GB", "4.0 GB", "3.0 GB"), p.FreeSpaceText("s", "a", 4L << 30, 1L << 30));
        }

        [TestMethod]
        public void FreeSpace_UnreadableVolume_IsUnknown()
        {
            var p = new LocationPresenter(new FakeFs(), new FixedDiskSpace(0), (a, ab, b, bb) => { throw new LauncherException(LauncherError.Unknown, "x"); });
            Assert.AreEqual(UiText.FreeSpaceUnknown, p.FreeSpaceText("s", "a", 1, 1));
        }
    }

    [TestClass]
    public class SingleInstanceTests
    {
        [TestMethod]
        public void MutexName_IsPerUserInTheSessionNamespace()
        {
            string sid;
            using (var id = System.Security.Principal.WindowsIdentity.GetCurrent())
            {
                sid = id.User.Value;
            }

            Assert.AreEqual(@"Local\DreamWeaveLauncher-" + sid, SingleInstance.MutexName);
        }

        [TestMethod]
        public void SecondHolder_IsRefused_ThenGetsItAfterRelease()
        {
            string name = @"Local\DreamWeaveLauncher-test-" + Guid.NewGuid().ToString("N");
            using (SingleInstance first = SingleInstance.Acquire(name, TimeSpan.Zero))
            {
                Assert.IsTrue(first.Owned);
                bool otherOwned = true;
                RunOnOtherThread(() =>
                {
                    using (SingleInstance second = SingleInstance.Acquire(name, TimeSpan.Zero))
                    {
                        otherOwned = second.Owned;
                    }
                });
                Assert.IsFalse(otherOwned, "a second launcher must not get the lock");

                first.Release();
                RunOnOtherThread(() =>
                {
                    using (SingleInstance second = SingleInstance.Acquire(name, TimeSpan.Zero))
                    {
                        otherOwned = second.Owned;
                    }
                });
                Assert.IsTrue(otherOwned);
            }
        }

        [TestMethod]
        public void AbandonedMutex_CountsAsTaken()
        {
            string name = @"Local\DreamWeaveLauncher-test-" + Guid.NewGuid().ToString("N");
            SingleInstance crashed = null;

            // A thread that takes the lock and ends without releasing it: the mutex is abandoned.
            RunOnOtherThread(() => crashed = SingleInstance.Acquire(name, TimeSpan.Zero));
            Assert.IsTrue(crashed.Owned);
            using (SingleInstance next = SingleInstance.Acquire(name, TimeSpan.FromSeconds(5)))
            {
                Assert.IsTrue(next.Owned);
            }

            GC.KeepAlive(crashed);
        }

        private static void RunOnOtherThread(Action action)
        {
            var t = new Thread(() => action());
            t.Start();
            Assert.IsTrue(t.Join(TimeSpan.FromSeconds(10)));
        }
    }

    [TestClass]
    public class UpdateGateTests
    {
        private static LauncherManifest With(string version, string min, bool download = true)
        {
            LauncherManifest m = AppFixtures.Sample();
            m.Launcher = new LauncherUpdateInfo
            {
                Version = version,
                MinVersion = min,
                DownloadPage = download ? "https://example.test/download" : string.Empty,
            };
            return m;
        }

        private static readonly SemVersion Running = SemVersion.Parse("1.2.0", "running");

        [TestMethod]
        public void Evaluate_MapsEveryStatus()
        {
            UpdateGate same = UpdateGate.Evaluate(With("1.2.0", "1.0.0"), Running);
            Assert.AreEqual(UpdateStatus.UpToDate, same.Status);
            Assert.IsNull(same.BannerText);
            Assert.IsFalse(same.BlocksPlay || same.OffersUpdate || same.OffersLater);

            UpdateGate optional = UpdateGate.Evaluate(With("1.3.0", "1.0.0"), Running);
            Assert.AreEqual(UpdateStatus.Optional, optional.Status);
            Assert.AreEqual("1.3.0", optional.AvailableVersion);
            StringAssert.Contains(optional.BannerText, "1.3.0");
            Assert.IsTrue(optional.OffersUpdate && optional.OffersLater && !optional.BlocksPlay);
            Assert.AreEqual("https://example.test/download", optional.DownloadPage);

            UpdateGate mandatory = UpdateGate.Evaluate(With("1.3.0", "1.3.0"), Running);
            Assert.AreEqual(UpdateStatus.Mandatory, mandatory.Status);
            Assert.IsTrue(mandatory.BlocksPlay && mandatory.OffersUpdate && !mandatory.OffersLater);

            UpdateGate unavailable = UpdateGate.Evaluate(With("1.3.0", "1.3.0", download: false), Running);
            Assert.AreEqual(UpdateStatus.MandatoryUnavailable, unavailable.Status);
            Assert.IsTrue(unavailable.BlocksPlay && !unavailable.OffersUpdate);
            Assert.AreEqual(UiText.UpdateUnavailable, unavailable.BannerText);

            UpdateGate downgrade = UpdateGate.Evaluate(With("1.1.0", "1.0.0"), Running);
            Assert.AreEqual(UpdateStatus.UpToDate, downgrade.Status);
        }

        [TestMethod]
        public void Evaluate_InvalidVersionOrMissingInput_IsNone()
        {
            Assert.AreSame(UpdateGate.None, UpdateGate.Evaluate(With("not-a-version", "1.0.0"), Running));
            Assert.AreSame(UpdateGate.None, UpdateGate.Evaluate(null, Running));
            Assert.AreSame(UpdateGate.None, UpdateGate.Evaluate(With("2.0.0", "2.0.0"), null));
        }
    }

    [TestClass]
    public class ProgressTextTests
    {
        [TestMethod]
        public void Describe_BytesSpeedEta()
        {
            var r = new ProgressReport("eor-dats", 100L << 20, 500L << 20, 2.0 * (1 << 20), TimeSpan.FromSeconds(200), "Downloading");
            Assert.AreEqual("100 MB of 500 MB - 2 MB/s - about 4 min left", ProgressText.Describe(r));
        }

        [TestMethod]
        public void Describe_UnknownTotalAndNoRate()
        {
            Assert.AreEqual("512 KB", ProgressText.Describe(new ProgressReport("x", 512 * 1024, null, null, null, string.Empty)));
            Assert.AreEqual("10 bytes of 1 KB - about 45 s left", ProgressText.Describe(new ProgressReport("x", 10, 1024, 0, TimeSpan.FromSeconds(44.2), string.Empty)));
            Assert.AreEqual(string.Empty, ProgressText.Describe(null));
        }

        [TestMethod]
        public void ByteFormat_Durations()
        {
            Assert.AreEqual("1 h 5 min", ByteFormat.Duration(TimeSpan.FromMinutes(65)));
            Assert.AreEqual("0 s", ByteFormat.Duration(TimeSpan.FromSeconds(-3)));
            Assert.AreEqual("under 1 KB", UiText.SizeSmall);
        }
    }
}
