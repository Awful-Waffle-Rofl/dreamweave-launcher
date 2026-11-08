using System;
using System.Linq;
using DreamWeave.Launcher.App;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Manifests;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.App
{
    [TestClass]
    public class ErrorMessagesTests
    {
        [TestMethod]
        public void EveryLauncherErrorValue_HasItsOwnMessage()
        {
            var missing = Enum.GetValues(typeof(LauncherError)).Cast<LauncherError>().Where(e => !ErrorMessages.Has(e)).ToList();
            Assert.AreEqual(0, missing.Count, "No message for: " + string.Join(", ", missing));
        }

        [TestMethod]
        public void Messages_AreNonEmptyAscii_AndQuotaUsesE1()
        {
            foreach (LauncherError e in Enum.GetValues(typeof(LauncherError)))
            {
                string m = ErrorMessages.For(e);
                Assert.IsFalse(string.IsNullOrWhiteSpace(m), e.ToString());
                Assert.IsTrue(m.All(c => c >= 0x20 && c <= 0x7E), e + " has a non-ASCII character");
            }

            Assert.AreEqual(UiText.MegaQuota, ErrorMessages.For(LauncherError.SourceQuotaExceeded));
            Assert.AreEqual(ErrorMessages.For(LauncherError.Unknown), ErrorMessages.For((LauncherError)12345));
        }
    }

    [TestClass]
    public class QuotaWordingTests
    {
        [TestMethod]
        public void MegaQuotaText_IsShownOnlyWhenMegaWasTheLastFailingSource()
        {
            Assert.AreEqual(UiText.MegaQuota, ErrorMessages.For(LauncherError.SourceQuotaExceeded, "mega"));
            Assert.AreEqual(UiText.MegaQuota, ErrorMessages.For(LauncherError.SourceQuotaExceeded, null), "unknown source keeps the old wording");
            Assert.AreEqual(UiText.SourceBusy, ErrorMessages.For(LauncherError.SourceQuotaExceeded, "http"));
            Assert.AreEqual(ErrorMessages.For(LauncherError.HashMismatch), ErrorMessages.For(LauncherError.HashMismatch, "http"), "other codes are unaffected");
            Assert.IsTrue(UiText.SourceBusy.All(c => c >= 0x20 && c <= 0x7E));
        }
    }

    [TestClass]
    public class ErrorDetailsTests
    {
        [TestMethod]
        public void CopyDetails_HasCodeVersionStepTime()
        {
            string text = ErrorDetails.Build(LauncherError.SourceQuotaExceeded, "1.0.0", "eor-dats", new DateTime(2026, 11, 7, 12, 30, 5, DateTimeKind.Utc));
            StringAssert.Contains(text, "SourceQuotaExceeded (12)");
            StringAssert.Contains(text, "Launcher version: 1.0.0");
            StringAssert.Contains(text, "Step: eor-dats");
            StringAssert.Contains(text, "2026-11-07 12:30:05");
        }

        [TestMethod]
        public void CopyDetails_ListsOnlyGameRelativeFileStates()
        {
            string text = ErrorDetails.Build(
                LauncherError.GameFilesChanged,
                "1.0.0",
                "eor-dats",
                DateTime.UtcNow,
                new[] { "client_portal.dat: Mismatch", "sub/x.dat: Missing", @"C:\Users\SomePlayer\x.dat: Mismatch", "/home/p/x.dat: Missing", "client_portal.dat: ran out of coffee", null });
            StringAssert.Contains(text, "File: client_portal.dat: Mismatch");
            StringAssert.Contains(text, "File: sub/x.dat: Missing");
            Assert.IsFalse(text.Contains("SomePlayer"), "an absolute path leaked");
            Assert.IsFalse(text.Contains("/home"), "a POSIX path leaked");
            Assert.IsFalse(text.Contains("coffee"), "free text leaked");
        }
        [TestMethod]
        public void CopyDetails_NeverCarriesAPathOrAccountName()
        {
            string[] hostile =
            {
                @"C:\Users\SomePlayer\AppData\Local\DreamWeave\staging\eor-dats\eor-dats.zip",
                "/home/someplayer/file",
                @"\\server\share",
                "MyAccountName",
                "eor dats",
                "acclient.exe",
            };
            foreach (string step in hostile)
            {
                string text = ErrorDetails.Build(LauncherError.HashMismatch, @"1.0.0 C:\x", step, DateTime.UtcNow);
                Assert.IsFalse(text.Contains(step), "step leaked: " + step);
                Assert.IsFalse(text.Contains(":\\"), "a drive path leaked");
                Assert.IsFalse(text.Contains("\\\\"), "a UNC path leaked");
                Assert.IsFalse(text.Contains("/"), "a POSIX path leaked");
                StringAssert.Contains(text, "Step: unknown");
                StringAssert.Contains(text, "Launcher version: unknown");
            }
        }
    }

    [TestClass]
    public class WelcomeModelTests
    {
        [TestMethod]
        public void TelemetryCheckbox_DefaultsOff()
        {
            Assert.IsFalse(new WelcomeModel(AppFixtures.Sample()).TelemetryOptIn);
            Assert.IsFalse(new WelcomeModel(null).TelemetryOptIn);
        }

        [TestMethod]
        public void TelemetryCheckbox_ReflectsAnEarlierOptIn()
        {
            Assert.IsTrue(new WelcomeModel(AppFixtures.Sample(), currentOptIn: true).TelemetryOptIn);
        }

        [TestMethod]
        public void PrivacyLink_HiddenUnlessHttpsUrlPresent()
        {
            LauncherManifest m = AppFixtures.Sample();
            Assert.IsFalse(new WelcomeModel(m).ShowPrivacyLink, "no links block");
            m.Links = new LinksInfo { Privacy = string.Empty };
            Assert.IsFalse(new WelcomeModel(m).ShowPrivacyLink, "empty");
            m.Links.Privacy = "http://example.test/privacy";
            Assert.IsFalse(new WelcomeModel(m).ShowPrivacyLink, "plain http");
            m.Links.Privacy = "https://example.test/privacy";
            Assert.IsTrue(new WelcomeModel(m).ShowPrivacyLink);
            Assert.AreEqual("https://example.test/privacy", new WelcomeModel(m).PrivacyUrl);
        }
    }
}
