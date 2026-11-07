using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Manifests;
using DreamWeave.Launcher.Core.Update;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.SelfInstall
{
    [TestClass]
    public class SemVersionTests
    {
        // Each row: lower, higher (strictly). Includes the full ordering example from semver.org section 11.
        [TestMethod]
        [DataRow("1.0.0", "2.0.0")]
        [DataRow("2.0.0", "2.1.0")]
        [DataRow("2.1.0", "2.1.1")]
        [DataRow("1.9.0", "1.10.0")]
        [DataRow("1.0.0-alpha", "1.0.0")]
        [DataRow("1.0.0-alpha", "1.0.0-alpha.1")]
        [DataRow("1.0.0-alpha.1", "1.0.0-alpha.beta")]
        [DataRow("1.0.0-alpha.beta", "1.0.0-beta")]
        [DataRow("1.0.0-beta", "1.0.0-beta.2")]
        [DataRow("1.0.0-beta.2", "1.0.0-beta.11")]
        [DataRow("1.0.0-beta.11", "1.0.0-rc.1")]
        [DataRow("1.0.0-rc.1", "1.0.0")]
        [DataRow("1.0.0-RC", "1.0.0-rc")]
        [DataRow("0.9.9", "1.0.0-alpha")]
        public void Ordering(string lower, string higher)
        {
            SemVersion a = SemVersion.Parse(lower, "a");
            SemVersion b = SemVersion.Parse(higher, "b");
            Assert.IsTrue(a < b, lower + " < " + higher);
            Assert.IsTrue(b > a, higher + " > " + lower);
            Assert.IsTrue(a.CompareTo(b) < 0);
            Assert.IsTrue(b.CompareTo(a) > 0);
            Assert.IsFalse(a.Equals(b));
        }

        [TestMethod]
        [DataRow("1.2.3", "1.2.3+build.5")]
        [DataRow("1.2.3", "v1.2.3")]
        [DataRow("1.2.3-beta.1+abc", "1.2.3-beta.1+def")]
        public void Equal_IgnoringBuildAndPrefix(string x, string y)
        {
            SemVersion a = SemVersion.Parse(x, "a");
            SemVersion b = SemVersion.Parse(y, "b");
            Assert.AreEqual(0, a.CompareTo(b));
            Assert.IsTrue(a.Equals(b));
            Assert.AreEqual(a.GetHashCode(), b.GetHashCode());
            Assert.IsTrue(a <= b && a >= b);
        }

        [TestMethod]
        [DataRow("")]
        [DataRow("1")]
        [DataRow("1.2")]
        [DataRow("1.2.3.4")]
        [DataRow("01.2.3")]
        [DataRow("1.2.3-")]
        [DataRow("1.2.3-01")]
        [DataRow("1.2.3-a..b")]
        [DataRow("1.2.3+")]
        [DataRow("1.2.x")]
        [DataRow("-1.2.3")]
        [DataRow(" 1.2.3")]
        [DataRow("1.2.3-beta_1")]
        [DataRow("99999999999.0.0")]
        public void Invalid_IsRejected(string text)
        {
            SemVersion v;
            Assert.IsFalse(SemVersion.TryParse(text, out v), text);
            LauncherException ex = Assert.ThrowsExactly<LauncherException>(() => SemVersion.Parse(text, "test"));
            Assert.AreEqual(LauncherError.VersionInvalid, ex.Code);
        }

        [TestMethod]
        public void ToString_DropsBuildMetadata()
        {
            Assert.AreEqual("1.2.3-rc.1", SemVersion.Parse("v1.2.3-rc.1+sha.abc", "x").ToString());
        }

        [TestMethod]
        public void FromAssembly_ParsesInformationalVersionOrFallsBack()
        {
            SemVersion v = SemVersion.FromAssembly(typeof(SemVersion).Assembly);
            Assert.IsNotNull(v);
            Assert.IsTrue(v >= SemVersion.Parse("0.0.0", "x"));
        }
    }

    [TestClass]
    public class UpdateCheckerTests
    {
        private static readonly string Sha = new string('a', 64);

        private static LauncherUpdateInfo Info(string version, string min = null, bool download = true)
        {
            return new LauncherUpdateInfo
            {
                Version = version,
                MinVersion = min,
                Url = download ? "https://example.test/DreamWeaveLauncher.exe" : string.Empty,
                Sha256 = download ? Sha : string.Empty,
                Size = download ? 1234 : 0,
            };
        }

        private static UpdateCheckResult Check(LauncherUpdateInfo info, string running)
        {
            return UpdateChecker.Check(info, SemVersion.Parse(running, "running"));
        }

        [TestMethod]
        [DataRow("1.0.0", "1.1.0", null, UpdateStatus.Optional)]
        [DataRow("1.0.0", "1.0.0", null, UpdateStatus.UpToDate)]
        [DataRow("1.0.0", "1.0.0+other", null, UpdateStatus.UpToDate)]
        [DataRow("1.1.0", "1.0.0", null, UpdateStatus.UpToDate)]
        [DataRow("1.0.0-beta.1", "1.0.0", null, UpdateStatus.Optional)]
        [DataRow("1.0.0", "1.0.0-beta.9", null, UpdateStatus.UpToDate)]
        [DataRow("1.0.0", "1.2.0", "1.1.0", UpdateStatus.Mandatory)]
        [DataRow("1.0.0", "1.2.0", "1.0.0", UpdateStatus.Optional)]
        [DataRow("1.1.0", "1.2.0", "1.0.0", UpdateStatus.Optional)]
        [DataRow("1.0.0-rc.1", "1.0.0", "1.0.0", UpdateStatus.Mandatory)]
        [DataRow("1.0.0", "1.0.0", "1.1.0", UpdateStatus.MandatoryUnavailable)]
        [DataRow("1.3.0", "1.2.0", "2.0.0", UpdateStatus.MandatoryUnavailable)]
        public void Table(string running, string latest, string min, UpdateStatus expected)
        {
            Assert.AreEqual(expected, Check(Info(latest, min), running).Status);
        }

        [TestMethod]
        public void Downgrade_IsNeverOffered_AndIsFlagged()
        {
            UpdateCheckResult r = Check(Info("1.0.0"), "1.5.0");
            Assert.AreEqual(UpdateStatus.UpToDate, r.Status);
            Assert.IsFalse(r.CanUpdate);
            Assert.IsTrue(r.DowngradeOffered);
        }

        [TestMethod]
        public void NewerVersion_WithoutDownload_IsNotInstallable()
        {
            Assert.AreEqual(UpdateStatus.UpToDate, Check(Info("2.0.0", null, download: false), "1.0.0").Status);
            Assert.AreEqual(UpdateStatus.MandatoryUnavailable, Check(Info("2.0.0", "2.0.0", download: false), "1.0.0").Status);
        }

        [TestMethod]
        public void MissingBlockOrVersion_IsUpToDate()
        {
            Assert.AreEqual(UpdateStatus.UpToDate, Check(null, "1.0.0").Status);
            Assert.AreEqual(UpdateStatus.UpToDate, Check(new LauncherUpdateInfo(), "1.0.0").Status);
        }

        [TestMethod]
        public void MinVersionAlone_AboveRunning_IsMandatoryUnavailable()
        {
            Assert.AreEqual(UpdateStatus.MandatoryUnavailable, Check(new LauncherUpdateInfo { MinVersion = "3.0.0" }, "1.0.0").Status);
        }

        [TestMethod]
        public void InvalidManifestVersion_Throws()
        {
            LauncherException ex = Assert.ThrowsExactly<LauncherException>(() => Check(Info("latest"), "1.0.0"));
            Assert.AreEqual(LauncherError.VersionInvalid, ex.Code);
            ex = Assert.ThrowsExactly<LauncherException>(() => Check(Info("1.1.0", "one"), "1.0.0"));
            Assert.AreEqual(LauncherError.VersionInvalid, ex.Code);
        }

        [TestMethod]
        public void MalformedDownloadFields_AreNotInstallable()
        {
            LauncherUpdateInfo bad = Info("2.0.0");
            bad.Sha256 = "nothex";
            Assert.AreEqual(UpdateStatus.UpToDate, Check(bad, "1.0.0").Status);

            bad = Info("2.0.0");
            bad.Size = 0;
            Assert.AreEqual(UpdateStatus.UpToDate, Check(bad, "1.0.0").Status);

            bad = Info("2.0.0");
            bad.Url = "not a url";
            Assert.AreEqual(UpdateStatus.UpToDate, Check(bad, "1.0.0").Status);
        }
    }
}
