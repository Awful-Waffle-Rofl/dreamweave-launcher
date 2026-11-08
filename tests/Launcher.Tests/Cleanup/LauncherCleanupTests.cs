using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Cleanup;
using DreamWeave.Launcher.Core.Detection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.Cleanup
{
    [TestClass]
    public class LauncherCleanupTests
    {
        private CleanupFixture fx;
        private string installFolder;
        private string outsider;

        [TestInitialize]
        public void Setup()
        {
            fx = new CleanupFixture();

            Directory.CreateDirectory(Path.Combine(fx.Paths.StagingDir, "ac-client"));
            File.WriteAllText(fx.Paths.StateFile, "{}");
            File.WriteAllText(Path.Combine(fx.Paths.StagingDir, "ac-client", "setup.exe"), "x");

            // A sibling folder that stands in for the install folder and for another program's files:
            // cleanup must leave all of it alone (the setup program owns the install folder).
            installFolder = Path.Combine(fx.Root, "ProgramFiles", SetupConstants.InstallFolderName);
            Directory.CreateDirectory(installFolder);
            outsider = Path.Combine(installFolder, "DreamWeaveLauncher.exe");
            File.WriteAllText(outsider, "keep me");

            fx.Credentials.Save("alice", "pw1");
            fx.Credentials.Save("bob", "pw2");
        }

        [TestCleanup]
        public void Cleanup() => fx.Dispose();

        private CleanupResult Run(bool removeAc = false)
        {
            return new LauncherCleanup(fx.Environment).Run(new CleanupOptions { RemoveAcClient = removeAc });
        }

        [TestMethod]
        public void Run_RemovesCredentialsAndDataDir_AndNothingElse()
        {
            CleanupResult r = Run();

            Assert.IsTrue(r.Complete, string.Join("; ", r.Failures));
            Assert.AreEqual(LauncherError.None, r.Code);
            Assert.IsFalse(Directory.Exists(fx.Paths.DataDir), "DataDir removed");
            Assert.AreEqual(0, fx.Credentials.Saved.Count, "credentials removed");
            Assert.AreEqual(2, r.CredentialsRemoved);
            Assert.AreEqual("keep me", File.ReadAllText(outsider), "the install folder is not cleanup's to touch");
            Assert.AreEqual(AcClientUninstallOutcome.NotRequested, r.AcClient);
            Assert.AreEqual(0, fx.Processes.Ran.Count, "the AC client is never touched by default");
        }

        [TestMethod]
        public void Run_SecondRun_IsHarmless()
        {
            Run();
            CleanupResult again = Run();
            Assert.IsTrue(again.Complete, string.Join("; ", again.Failures));
        }

        [TestMethod]
        public void Run_PartialFailures_ContinueAndAreReported()
        {
            fx.Credentials.FailList = true;
            CleanupResult r = Run();

            Assert.AreEqual(1, r.Failures.Count, string.Join("; ", r.Failures));
            Assert.IsTrue(r.CredentialsLeftOver);
            Assert.IsFalse(Directory.Exists(fx.Paths.DataDir), "later steps still ran");
        }

        [TestMethod]
        public void Run_FailedDataFolder_IsReportedAsAFolderPath()
        {
            fx.FileSystem.TreeFailure = folder => string.Equals(folder, Path.GetFullPath(fx.Paths.DataDir), StringComparison.OrdinalIgnoreCase) ? folder + ": Access to the path is denied." : null;
            CleanupResult r = Run();
            Assert.IsFalse(r.Complete);
            Assert.AreEqual(LauncherError.UninstallIncomplete, r.Code);
            CollectionAssert.AreEqual(new[] { Path.GetFullPath(fx.Paths.DataDir) }, r.LeftoverFolders.ToArray());
            Assert.IsFalse(r.CredentialsLeftOver);
        }

        [TestMethod]
        public void Run_UnexpectedLeaf_IsRefused()
        {
            var odd = new CleanupEnvironment
            {
                Paths = new BadPaths(fx.Root).Paths,
                MachineRegistry = fx.Environment.MachineRegistry,
                FileSystem = fx.Environment.FileSystem,
                Processes = fx.Environment.Processes,
                Credentials = fx.Environment.Credentials,
            };

            Assert.ThrowsExactly<ArgumentException>(() => new LauncherCleanup(odd).Run(new CleanupOptions()));
            Assert.IsTrue(Directory.Exists(fx.Paths.DataDir), "nothing was deleted");
            Assert.AreEqual(2, fx.Credentials.Saved.Count, "credentials untouched too");
        }

        [TestMethod]
        public void Run_DoesNotFollowAJunctionOutOfDataDir()
        {
            string victim = Path.Combine(fx.Root, "victim");
            Directory.CreateDirectory(victim);
            File.WriteAllText(Path.Combine(victim, "precious.txt"), "keep");
            string link = Path.Combine(fx.Paths.DataDir, "link");
            if (!JunctionHelper.TryCreate(link, victim))
            {
                Assert.Inconclusive("Could not create a junction on this machine.");
            }

            CleanupResult r = Run();

            Assert.IsTrue(r.Complete, string.Join("; ", r.Failures));
            Assert.IsFalse(Directory.Exists(fx.Paths.DataDir));
            Assert.AreEqual("keep", File.ReadAllText(Path.Combine(victim, "precious.txt")), "the junction target must survive");
        }

        [TestMethod]
        public void RemoveAcClient_RunsTheClientsOwnUninstallString_Completed()
        {
            fx.MachineRegistry.Set(AcInstallLocator.UninstallRoot + "\\" + AcInstallLocator.UninstallGuid, "UninstallString", "MsiExec.exe /X{F0EE55BA-193D-4670-90C0-76E0E25F3A08}");

            CleanupResult r = Run(removeAc: true);

            Assert.AreEqual(AcClientUninstallOutcome.Completed, r.AcClient);
            Assert.AreEqual(1, fx.Processes.Ran.Count);
            Assert.AreEqual("MsiExec.exe", fx.Processes.Ran[0].FileName);
            Assert.AreEqual("/X{F0EE55BA-193D-4670-90C0-76E0E25F3A08}", fx.Processes.Ran[0].Arguments);
            Assert.IsTrue(fx.Processes.Ran[0].UseShellExecute, "ShellExecute so the client's uninstaller can elevate itself");
        }

        [TestMethod]
        public void RemoveAcClient_FallsBackToTheSingleExactDisplayNameMatch()
        {
            fx.MachineRegistry.AddUninstall("SomeKey", "Asheron" + (char)0x2019 + "s Call", @"C:\Turbine\Asheron's Call");
            fx.MachineRegistry.Set(AcInstallLocator.UninstallRoot + "\\SomeKey", "UninstallString", "\"C:\\Turbine\\Asheron's Call\\unins000.exe\" /SILENT");
            fx.MachineRegistry.AddUninstall("Decal", "Decal 2.9 for Asheron's Call", null);
            fx.MachineRegistry.Set(AcInstallLocator.UninstallRoot + "\\Decal", "UninstallString", "decal-uninstall.exe");

            CleanupResult r = Run(removeAc: true);

            Assert.AreEqual(AcClientUninstallOutcome.Completed, r.AcClient);
            Assert.AreEqual(@"C:\Turbine\Asheron's Call\unins000.exe", fx.Processes.Ran.Single().FileName);
            Assert.AreEqual("/SILENT", fx.Processes.Ran.Single().Arguments);
        }

        [TestMethod]
        public void RemoveAcClient_AmbiguousDisplayName_RunsNothing()
        {
            fx.MachineRegistry.AddUninstall("A", "Asheron's Call", null);
            fx.MachineRegistry.Set(AcInstallLocator.UninstallRoot + "\\A", "UninstallString", "a.exe");
            fx.MachineRegistry.AddUninstall("B", "Asheron's Call", null);
            fx.MachineRegistry.Set(AcInstallLocator.UninstallRoot + "\\B", "UninstallString", "b.exe");

            CleanupResult r = Run(removeAc: true);

            Assert.AreEqual(AcClientUninstallOutcome.NotFound, r.AcClient);
            Assert.AreEqual(0, fx.Processes.Ran.Count);
        }

        [TestMethod]
        public void RemoveAcClient_NotInstalled_IsNotFound()
        {
            Assert.AreEqual(AcClientUninstallOutcome.NotFound, Run(removeAc: true).AcClient);
        }

        [TestMethod]
        public void RemoveAcClient_UacDeclined_IsNotAFailure()
        {
            fx.MachineRegistry.Set(AcInstallLocator.UninstallRoot + "\\" + AcInstallLocator.UninstallGuid, "UninstallString", "MsiExec.exe /X{x}");
            fx.Processes.RunFailure = psi => new Win32Exception(1223);

            CleanupResult r = Run(removeAc: true);

            Assert.AreEqual(AcClientUninstallOutcome.Declined, r.AcClient);
            Assert.IsTrue(r.Complete, string.Join("; ", r.Failures));
        }

        [TestMethod]
        public void RemoveAcClient_NonZeroExit_IsFailed_AndOurOwnRemovalStillRan()
        {
            fx.MachineRegistry.Set(AcInstallLocator.UninstallRoot + "\\" + AcInstallLocator.UninstallGuid, "UninstallString", "MsiExec.exe /X{x}");
            fx.Processes.RunExitCode = psi => 1603;

            CleanupResult r = Run(removeAc: true);

            Assert.AreEqual(AcClientUninstallOutcome.Failed, r.AcClient);
            Assert.AreEqual(1603, r.AcClientExitCode);
            Assert.IsFalse(r.Complete);
            Assert.IsFalse(Directory.Exists(fx.Paths.DataDir), "our own removal still ran");
        }

        [TestMethod]
        public void RunSilent_NeverRunsTheAcUninstaller_EvenWhenOneIsRegistered()
        {
            fx.MachineRegistry.Set(AcInstallLocator.UninstallRoot + "\\" + AcInstallLocator.UninstallGuid, "UninstallString", "MsiExec.exe /X{x}");

            CleanupResult r = new LauncherCleanup(fx.Environment).RunSilent();

            Assert.IsNotNull(r);
            Assert.AreEqual(AcClientUninstallOutcome.NotRequested, r.AcClient);
            Assert.AreEqual(0, fx.Processes.Ran.Count, "silent mode must never run the AC uninstaller");
            Assert.AreEqual(0, fx.Processes.Started.Count);
            Assert.IsFalse(Directory.Exists(fx.Paths.DataDir));
            Assert.AreEqual(0, fx.Credentials.Saved.Count);
        }

        [TestMethod]
        public void RunSilent_RefusedFolder_ReturnsNullInsteadOfThrowing()
        {
            var odd = new CleanupEnvironment
            {
                Paths = new BadPaths(fx.Root).Paths,
                MachineRegistry = fx.Environment.MachineRegistry,
                FileSystem = fx.Environment.FileSystem,
                Processes = fx.Environment.Processes,
                Credentials = fx.Environment.Credentials,
            };

            Assert.IsNull(new LauncherCleanup(odd).RunSilent());
        }

        [TestMethod]
        [DataRow("MsiExec.exe /X{GUID}", "MsiExec.exe", "/X{GUID}")]
        [DataRow("\"C:\\Program Files (x86)\\AC\\unins.exe\" /S", "C:\\Program Files (x86)\\AC\\unins.exe", "/S")]
        [DataRow("C:\\Program Files (x86)\\AC\\unins.exe /S", "C:\\Program Files (x86)\\AC\\unins.exe", "/S")]
        [DataRow("C:\\AC\\unins.exe", "C:\\AC\\unins.exe", "")]
        [DataRow("rundll32 foo.dll,Uninstall", "rundll32", "foo.dll,Uninstall")]
        public void SplitCommandLine_Table(string line, string program, string args)
        {
            string p, a;
            LauncherCleanup.SplitCommandLine(line, out p, out a);
            Assert.AreEqual(program, p);
            Assert.AreEqual(args, a);
        }

        private sealed class BadPaths
        {
            public BadPaths(string root)
            {
                // The public AppPaths constructor always yields the expected leaf names, so a corrupted
                // DataDir ("...\Users") can only be produced through the backing field.
                Paths = new AppPaths(root);
                typeof(AppPaths).GetField("<DataDir>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    .SetValue(Paths, Path.Combine(root, "Users"));
            }

            public AppPaths Paths { get; }
        }
    }

    internal static class JunctionHelper
    {
        public static bool TryCreate(string link, string target)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(link));
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /c mklink /J \"" + link + "\" \"" + target + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using (Process p = Process.Start(psi))
            {
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit();
                return p.ExitCode == 0 && Directory.Exists(link) && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0;
            }
        }
    }
}