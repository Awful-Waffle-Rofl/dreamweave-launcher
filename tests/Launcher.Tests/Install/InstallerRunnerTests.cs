using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Detection;
using DreamWeave.Launcher.Core.Install;
using DreamWeave.Launcher.Core.Manifests;
using DreamWeave.Launcher.Core.State;
using DreamWeave.Launcher.Tests.Detection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.Install
{
    [TestClass]
    public class InstallerRunnerTests
    {
        private static readonly byte[] InstallerBytes = InstallData.Bytes("pretend InstallShield setup payload");

        private static ManifestComponent Component(byte[] content, bool elevate = true)
        {
            return new ManifestComponent
            {
                Id = "ac-client",
                Kind = ComponentKinds.Installer,
                Size = content.Length,
                Sha256 = InstallData.Hash(content),
                Elevate = elevate,
                Url = "https://example.test/setup.exe",
            };
        }

        private static string Stage(TempDir dir, byte[] content)
        {
            string staging = Path.Combine(dir.Path, "staging");
            Directory.CreateDirectory(staging);
            string exe = Path.Combine(staging, "ac1install.exe");
            File.WriteAllBytes(exe, content);
            return exe;
        }

        [TestMethod]
        public void HashMismatch_UnderHandle_DoesNotStart()
        {
            using (var dir = new TempDir())
            {
                string exe = Stage(dir, InstallerBytes);
                byte[] other = (byte[])InstallerBytes.Clone();
                other[0] ^= 0xFF;
                var starter = new FakeStarter();

                var ex = Assert.ThrowsExactly<LauncherException>(() => new InstallerRunner(starter).Start(Component(other), exe, Path.GetDirectoryName(exe)));

                Assert.AreEqual(LauncherError.HashMismatch, ex.Code);
                Assert.AreEqual(0, starter.Calls.Count, "a file that fails its hash must never be started");
            }
        }

        [TestMethod]
        public void SizeMismatch_DoesNotStart()
        {
            using (var dir = new TempDir())
            {
                string exe = Stage(dir, InstallerBytes);
                ManifestComponent c = Component(InstallerBytes);
                c.Size += 1;
                var starter = new FakeStarter();

                var ex = Assert.ThrowsExactly<LauncherException>(() => new InstallerRunner(starter).Start(c, exe, Path.GetDirectoryName(exe)));

                Assert.AreEqual(LauncherError.SizeMismatch, ex.Code);
                Assert.AreEqual(0, starter.Calls.Count);
            }
        }

        [TestMethod]
        public void Handle_IsHeldDuringStart_AndReleasedAfter()
        {
            using (var dir = new TempDir())
            {
                string exe = Stage(dir, InstallerBytes);
                string staging = Path.GetDirectoryName(exe);
                bool writeBlocked = false;
                bool deleteBlocked = false;
                bool readAllowed = false;
                var starter = new FakeStarter
                {
                    OnStart = psi =>
                    {
                        try
                        {
                            using (new FileStream(psi.FileName, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
                            {
                            }
                        }
                        catch (IOException)
                        {
                            writeBlocked = true;
                        }

                        try
                        {
                            File.Delete(psi.FileName);
                        }
                        catch (IOException)
                        {
                            deleteBlocked = true;
                        }

                        using (new FileStream(psi.FileName, FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            readAllowed = true;
                        }
                    },
                };

                using (InstallerProcess p = new InstallerRunner(starter).Start(Component(InstallerBytes), exe, staging))
                {
                    Assert.IsNotNull(p);
                }

                Assert.IsTrue(writeBlocked, "a write-open must fail while the installer is being started");
                Assert.IsTrue(deleteBlocked, "a delete must fail while the installer is being started");
                Assert.IsTrue(readAllowed, "the shell must still be able to open the file for reading");
                ProcessStartInfo started = starter.Calls[0];
                Assert.AreEqual("runas", started.Verb);
                Assert.IsTrue(started.UseShellExecute);
                Assert.AreEqual(Path.GetFullPath(staging), started.WorkingDirectory);
                Assert.AreEqual(Path.GetFullPath(exe), started.FileName);

                using (new FileStream(exe, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    // Released after start.
                }
            }
        }

        [TestMethod]
        public void NotElevated_Component_HasNoRunasVerb()
        {
            using (var dir = new TempDir())
            {
                string exe = Stage(dir, InstallerBytes);
                var starter = new FakeStarter();
                new InstallerRunner(starter).Start(Component(InstallerBytes, elevate: false), exe, Path.GetDirectoryName(exe)).Dispose();
                Assert.AreEqual(string.Empty, starter.Calls[0].Verb ?? string.Empty);
            }
        }

        [TestMethod]
        public void UacDeclined_1223_IsElevationDeclined()
        {
            using (var dir = new TempDir())
            {
                string exe = Stage(dir, InstallerBytes);
                var starter = new FakeStarter { Throw = new Win32Exception(1223) };

                var ex = Assert.ThrowsExactly<LauncherException>(() => new InstallerRunner(starter).Start(Component(InstallerBytes), exe, Path.GetDirectoryName(exe)));

                Assert.AreEqual(LauncherError.ElevationDeclined, ex.Code);
            }
        }

        [TestMethod]
        public void OtherStartFailure_IsInstallerFailed()
        {
            using (var dir = new TempDir())
            {
                string exe = Stage(dir, InstallerBytes);
                var starter = new FakeStarter { Throw = new Win32Exception(2) };

                var ex = Assert.ThrowsExactly<LauncherException>(() => new InstallerRunner(starter).Start(Component(InstallerBytes), exe, Path.GetDirectoryName(exe)));

                Assert.AreEqual(LauncherError.InstallerFailed, ex.Code);
            }
        }

        [TestMethod]
        public void DllNextToInstaller_DoesNotStart()
        {
            using (var dir = new TempDir())
            {
                string exe = Stage(dir, InstallerBytes);
                File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(exe), "version.dll"), new byte[] { 1 });
                var starter = new FakeStarter();

                var ex = Assert.ThrowsExactly<LauncherException>(() => new InstallerRunner(starter).Start(Component(InstallerBytes), exe, Path.GetDirectoryName(exe)));

                Assert.AreEqual(LauncherError.InstallerFailed, ex.Code);
                Assert.AreEqual(0, starter.Calls.Count);
            }
        }

        [TestMethod]
        public void WaitForInstall_CompletesWhenFolderGainsAcceptedClient()
        {
            using (var dir = new TempDir())
            {
                string ac = Path.Combine(dir.Path, "Turbine", "Asheron's Call");
                byte[] good = InstallData.Bytes("the accepted acclient.exe");
                ManifestComponent c = Component(InstallerBytes);
                c.SatisfiedWhen.Add(new SatisfiedWhenRule { Path = "acclient.exe", Sha256 = { InstallData.Hash(good) } });
                LauncherManifest m = InstallData.Manifest(c);
                var state = new StateStore(Path.Combine(dir.Path, "state.json"));
                var fs = new RealAcFileSystem();
                var locator = new AcInstallLocator(new FakeRegistry(), fs, ac);
                var evaluator = new ComponentEvaluator(new FileVerifier(fs, state));
                string exe = Stage(dir, InstallerBytes);

                using (InstallerProcess p = new InstallerRunner(new FakeStarter()).Start(c, exe, Path.GetDirectoryName(exe)))
                {
                    Assert.IsTrue(p.HasExited, "the fake exits at once, as an InstallShield extractor may; the wait must not care");
                    Task<string> wait = p.WaitForInstallAsync(locator, evaluator, m, () => null, TimeSpan.FromMilliseconds(10), CancellationToken.None);
                    Thread.Sleep(100);
                    Assert.IsFalse(wait.IsCompleted, "no folder yet");

                    Directory.CreateDirectory(ac);
                    File.WriteAllBytes(Path.Combine(ac, "acclient.exe"), InstallData.Bytes("half-written client"));
                    Thread.Sleep(100);
                    Assert.IsFalse(wait.IsCompleted, "a client with an unaccepted hash is not an install");

                    File.WriteAllBytes(Path.Combine(ac, "acclient.exe"), good);
                    Assert.IsTrue(wait.Wait(TimeSpan.FromSeconds(10)), "the wait must finish once the accepted client appears");
                    Assert.AreEqual(ac, wait.Result);
                }
            }
        }

        [TestMethod]
        public void WaitForInstall_Cancellation_IsCancelled()
        {
            using (var cts = new CancellationTokenSource())
            {
                Task<string> wait = InstallerProcess.WaitForInstallAsync(() => null, TimeSpan.FromMilliseconds(10), cts.Token);
                cts.CancelAfter(50);
                var agg = Assert.ThrowsExactly<AggregateException>(() => wait.Wait(TimeSpan.FromSeconds(10)));
                Assert.AreEqual(LauncherError.Cancelled, ((LauncherException)agg.InnerException).Code);
            }
        }
    }
}
