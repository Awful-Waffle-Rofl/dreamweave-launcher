using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.SelfInstall;
using DreamWeave.Launcher.Core.Update;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.SelfInstall
{
    /// <summary>The self-delete helper, run for real with cmd.exe against throwaway files in a temp folder.</summary>
    [TestClass]
    public class SelfDeleteSchedulerTests
    {
        private TempDir temp;

        [TestInitialize]
        public void Setup() => temp = new TempDir();

        [TestCleanup]
        public void Cleanup() => temp.Dispose();

        private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (clock.Elapsed < timeout)
            {
                if (condition())
                {
                    return true;
                }

                Thread.Sleep(100);
            }

            return condition();
        }

        [TestMethod]
        public void Script_IsAscii_Crlf_AndTakesPathsAsArguments()
        {
            string s = SelfDeleteScheduler.ScriptText;
            Assert.IsTrue(s.All(c => c < 128), "ASCII only");
            Assert.IsFalse(s.Replace("\r\n", string.Empty).Contains("\n"), "CRLF only");
            StringAssert.Contains(s, "\"%~1\"");
            StringAssert.Contains(s, "rmdir \"%~2\"");
            Assert.IsFalse(s.Contains("/s"), "rmdir must never be recursive");
        }

        [TestMethod]
        [DataRow(@"C:\Users\100%\x.exe")]
        [DataRow("C:\\a\"b\\x.exe")]
        [DataRow("C:\\a\nb\\x.exe")]
        public void UnsafePaths_AreRefused_WithoutStartingAnything(string exe)
        {
            var processes = new FakeProcessLauncher();
            Assert.IsFalse(SelfDeleteScheduler.IsSafeArgument(exe));
            Assert.IsFalse(new SelfDeleteScheduler(processes, temp.Path).Schedule(exe, @"C:\I"));
            Assert.AreEqual(0, processes.Started.Count);
            Assert.AreEqual(0, Directory.GetFiles(temp.Path).Length, "no script written");
        }

        [TestMethod]
        public void BuildStartInfo_UsesSystemCmd_Hidden()
        {
            ProcessStartInfo psi = SelfDeleteScheduler.BuildStartInfo(@"C:\T\s.cmd", @"C:\I\DreamWeaveLauncher.exe", @"C:\I");
            Assert.AreEqual(Path.Combine(Environment.SystemDirectory, "cmd.exe"), psi.FileName);
            Assert.AreEqual("/d /s /c \"\"C:\\T\\s.cmd\" \"C:\\I\\DreamWeaveLauncher.exe\" \"C:\\I\"\"", psi.Arguments);
            Assert.IsTrue(psi.CreateNoWindow);
            Assert.IsFalse(psi.UseShellExecute);
        }

        [TestMethod]
        public void Schedule_DeletesALockedExeOnceReleased_ThenTheEmptyFolder_ThenItself()
        {
            // A folder name with spaces, an ampersand and a non-ASCII letter, as a user profile may have.
            string install = Path.Combine(temp.Path, "Dream Weave & Co " + (char)0x00E9);
            Directory.CreateDirectory(install);
            string exe = Path.Combine(install, "DreamWeaveLauncher.exe");
            File.WriteAllText(exe, "pretend exe");
            string scripts = Path.Combine(temp.Path, "scripts");
            Directory.CreateDirectory(scripts);

            var holder = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                Assert.IsTrue(new SelfDeleteScheduler(new ProcessLauncher(), scripts).Schedule(exe, install));
                Thread.Sleep(1500);
                Assert.IsTrue(File.Exists(exe), "still locked, so not yet deleted");
            }
            finally
            {
                holder.Dispose();
            }

            Assert.IsTrue(WaitUntil(() => !Directory.Exists(install), TimeSpan.FromSeconds(20)), "exe and folder removed after release");
            Assert.IsTrue(WaitUntil(() => Directory.GetFiles(scripts).Length == 0, TimeSpan.FromSeconds(10)), "the script deleted itself");
        }

        [TestMethod]
        public void Schedule_NonEmptyFolder_IsLeftInPlace()
        {
            string install = Path.Combine(temp.Path, "install");
            Directory.CreateDirectory(install);
            string exe = Path.Combine(install, "DreamWeaveLauncher.exe");
            File.WriteAllText(exe, "pretend exe");
            File.WriteAllText(Path.Combine(install, "someone-elses.txt"), "keep");
            string scripts = Path.Combine(temp.Path, "scripts");
            Directory.CreateDirectory(scripts);

            Assert.IsTrue(new SelfDeleteScheduler(new ProcessLauncher(), scripts).Schedule(exe, install));

            Assert.IsTrue(WaitUntil(() => !File.Exists(exe) && Directory.GetFiles(scripts).Length == 0, TimeSpan.FromSeconds(20)));
            Assert.IsTrue(File.Exists(Path.Combine(install, "someone-elses.txt")), "rmdir is not recursive");
        }
    }

    /// <summary>The real IShellLinkW writer, aimed at a temp folder (never the real Start menu).</summary>
    [TestClass]
    public class ShellLinkWriterTests
    {
        [TestMethod]
        public void Write_ThenReadTarget_RoundTrips_AndOverwrites()
        {
            using (var temp = new TempDir())
            {
                string target1 = Path.Combine(temp.Path, "one.exe");
                string target2 = Path.Combine(temp.Path, "two.exe");
                File.WriteAllText(target1, "x");
                File.WriteAllText(target2, "x");
                string lnk = Path.Combine(temp.Path, "Programs", "DreamWeave Launcher.lnk");
                var writer = new ShellLinkWriter();

                writer.Write(lnk, target1, temp.Path, "test");
                Assert.AreEqual(target1, writer.ReadTarget(lnk), true);

                writer.Write(lnk, target2, temp.Path, "test");
                Assert.AreEqual(target2, writer.ReadTarget(lnk), true);
            }
        }

        [TestMethod]
        public void ReadTarget_Missing_IsNull()
        {
            Assert.IsNull(new ShellLinkWriter().ReadTarget(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".lnk")));
        }
    }

    [TestClass]
    public class SelfFileSystemTests
    {
        [TestMethod]
        public void DeleteTree_RemovesNestedReadOnlyFiles_AndPreservesOneFile()
        {
            using (var temp = new TempDir())
            {
                string root = Path.Combine(temp.Path, "root");
                string deep = Path.Combine(root, "a", "b");
                Directory.CreateDirectory(deep);
                string ro = Path.Combine(deep, "ro.txt");
                File.WriteAllText(ro, "x");
                File.SetAttributes(ro, FileAttributes.ReadOnly);
                string keep = Path.Combine(root, "keep.exe");
                File.WriteAllText(keep, "x");

                var failures = new List<string>();
                new SelfFileSystem().DeleteTree(root, keep, failures);

                Assert.AreEqual(0, failures.Count, string.Join("; ", failures));
                Assert.IsTrue(File.Exists(keep));
                Assert.IsFalse(Directory.Exists(Path.Combine(root, "a")));
            }
        }

        [TestMethod]
        public void DeleteTree_OnAJunctionRoot_RemovesOnlyTheLink()
        {
            using (var temp = new TempDir())
            {
                string target = Path.Combine(temp.Path, "target");
                Directory.CreateDirectory(target);
                File.WriteAllText(Path.Combine(target, "precious.txt"), "keep");
                string link = Path.Combine(temp.Path, "link");
                if (!JunctionHelper.TryCreate(link, target))
                {
                    Assert.Inconclusive("Could not create a junction on this machine.");
                }

                var failures = new List<string>();
                new SelfFileSystem().DeleteTree(link, null, failures);

                Assert.AreEqual(0, failures.Count, string.Join("; ", failures));
                Assert.IsFalse(Directory.Exists(link));
                Assert.IsTrue(File.Exists(Path.Combine(target, "precious.txt")));
            }
        }

        [TestMethod]
        public void DeleteTree_LockedFile_IsReported_AndTheRestContinues()
        {
            using (var temp = new TempDir())
            {
                string root = Path.Combine(temp.Path, "root");
                Directory.CreateDirectory(root);
                string locked = Path.Combine(root, "locked.txt");
                string other = Path.Combine(root, "other.txt");
                File.WriteAllText(locked, "x");
                File.WriteAllText(other, "x");

                var failures = new List<string>();
                using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    new SelfFileSystem().DeleteTree(root, null, failures);
                }

                Assert.IsFalse(File.Exists(other));
                Assert.IsTrue(failures.Any(f => f.StartsWith(locked, StringComparison.OrdinalIgnoreCase)), string.Join("; ", failures));
            }
        }
    }

    [TestClass]
    public class WinTrustVerifierTests
    {
        [TestMethod]
        public void UnsignedFile_IsRejected()
        {
            using (var temp = new TempDir())
            {
                string path = Path.Combine(temp.Path, "unsigned.exe");
                File.WriteAllBytes(path, Encoding.ASCII.GetBytes("MZ not really an exe"));
                using (FileStream held = new SelfFileSystem().OpenReadLocked(path))
                {
                    LauncherException ex = Assert.ThrowsExactly<LauncherException>(() => new WinTrustVerifier().Verify(path, held, "CN=Anyone"));
                    Assert.AreEqual(LauncherError.UpdateSignatureInvalid, ex.Code);
                }
            }
        }

        [TestMethod]
        public void SignedFile_SubjectMustMatchExactly()
        {
            // No signed fixture ships with the repo (v1 is unsigned). Use an embedded-signed binary
            // already on the machine when there is one; otherwise this test cannot run.
            string candidate = FindEmbeddedSignedExe();
            if (candidate == null)
            {
                Assert.Inconclusive("No signed fixture: no embedded-Authenticode-signed exe found on this machine to verify against.");
            }

            using (FileStream held = new SelfFileSystem().OpenReadLocked(candidate))
            {
                string subject;
                using (var cert = new System.Security.Cryptography.X509Certificates.X509Certificate2(System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(candidate)))
                {
                    subject = cert.Subject;
                }

                new WinTrustVerifier().Verify(candidate, held, subject);

                LauncherException ex = Assert.ThrowsExactly<LauncherException>(() => new WinTrustVerifier().Verify(candidate, held, subject + "x"));
                Assert.AreEqual(LauncherError.UpdateSignatureInvalid, ex.Code);
                StringAssert.Contains(ex.Detail, "is signed by");
            }
        }

        private static string FindEmbeddedSignedExe()
        {
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string[] candidates =
            {
                Path.Combine(pf, "dotnet", "dotnet.exe"),
                Path.Combine(pf, "Git", "cmd", "git.exe"),
                Path.Combine(pf, "PowerShell", "7", "pwsh.exe"),
            };

            foreach (string c in candidates)
            {
                if (!File.Exists(c))
                {
                    continue;
                }

                using (FileStream fs = File.OpenRead(c))
                {
                    if (WinTrustVerifier.VerifyTrust(c, fs) == 0)
                    {
                        return c;
                    }
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Opt-in integration test against the REAL HKCU, under a unique throwaway key that is always
    /// removed. Set DWL_REGISTRY_INTEGRATION_TESTS=1 to run it; otherwise it is Inconclusive.
    /// </summary>
    [TestClass]
    public class UserRegistryIntegrationTests
    {
        [TestMethod]
        public void SetValues_GetValue_DeleteKeyTree_RoundTrip()
        {
            if (Environment.GetEnvironmentVariable("DWL_REGISTRY_INTEGRATION_TESTS") != "1")
            {
                Assert.Inconclusive("Opt-in: set DWL_REGISTRY_INTEGRATION_TESTS=1 to write a throwaway key under HKCU\\Software\\DreamWeaveLauncherTests.");
            }

            string key = @"Software\DreamWeaveLauncherTests\" + Guid.NewGuid().ToString("N");
            var reg = new UserRegistry();
            try
            {
                reg.SetValues(key, new Dictionary<string, object> { { "DisplayName", "Test" }, { "NoModify", 1 } });
                Assert.AreEqual("Test", reg.GetValue(key, "DisplayName"));
                Assert.AreEqual(1, reg.GetValue(key, "NoModify"));
                Assert.IsTrue(reg.DeleteKeyTree(key));
                Assert.IsNull(reg.GetValue(key, "DisplayName"));
                Assert.IsFalse(reg.DeleteKeyTree(key));
            }
            finally
            {
                reg.DeleteKeyTree(key);
                using (Microsoft.Win32.RegistryKey parent = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\DreamWeaveLauncherTests", true))
                {
                    if (parent != null && parent.SubKeyCount == 0 && parent.ValueCount == 0)
                    {
                        Microsoft.Win32.Registry.CurrentUser.DeleteSubKey(@"Software\DreamWeaveLauncherTests", false);
                    }
                }
            }
        }
    }
}
