using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Detection;
using DreamWeave.Launcher.Core.SelfInstall;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.SelfInstall
{
    [TestClass]
    public class SelfUninstallerTests
    {
        private SelfInstallFixture fx;
        private string exe;
        private string outsider;

        [TestInitialize]
        public void Setup()
        {
            fx = new SelfInstallFixture();
            new SelfInstaller(fx.Environment, "1.0.0").Install(fx.WriteDownloadedExe(Encoding.ASCII.GetBytes("exe")));
            exe = fx.Environment.InstalledExePath;

            Directory.CreateDirectory(Path.Combine(fx.Paths.StagingDir, "ac-client"));
            File.WriteAllText(fx.Paths.StateFile, "{}");
            File.WriteAllText(Path.Combine(fx.Paths.StagingDir, "ac-client", "setup.exe"), "x");
            File.WriteAllText(exe + ".old", "stale");

            // Things that are NOT ours and must survive: a sibling program, a sibling Start-menu entry, another app's key.
            outsider = Path.Combine(fx.Paths.Root, "Programs", "Other App", "other.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(outsider));
            File.WriteAllText(outsider, "keep me");
            File.WriteAllText(Path.Combine(fx.Environment.ProgramsFolder, "Other.lnk"), "keep me");
            fx.Registry.SetValues(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\OtherApp", new System.Collections.Generic.Dictionary<string, object> { { "DisplayName", "Other" } });

            fx.Credentials.Save("alice", "pw1");
            fx.Credentials.Save("bob", "pw2");
        }

        [TestCleanup]
        public void Cleanup() => fx.Dispose();

        private UninstallResult Uninstall(bool removeAc = false, string running = null)
        {
            return new SelfUninstaller(fx.Environment).Uninstall(new UninstallOptions { CurrentExePath = running ?? exe, RemoveAcClient = removeAc });
        }

        [TestMethod]
        public void Uninstall_RemovesEverythingOurs_AndNothingElse()
        {
            UninstallResult r = Uninstall();

            Assert.IsTrue(r.Complete, string.Join("; ", r.Failures));
            Assert.AreEqual(LauncherError.None, r.Code);
            Assert.IsFalse(Directory.Exists(fx.Paths.DataDir), "DataDir removed");
            Assert.IsFalse(File.Exists(fx.Environment.ShortcutPath), "shortcut removed");
            Assert.IsNull(fx.Registry.GetValue(SelfInstallLayout.UninstallKeyPath, "DisplayName"), "uninstall key removed");
            Assert.AreEqual(0, fx.Credentials.Saved.Count);
            Assert.AreEqual(2, r.CredentialsRemoved);

            // The running exe is the only thing left in InstallDir, handed to the self-delete helper.
            CollectionAssert.AreEqual(new[] { exe }, Directory.GetFiles(fx.Paths.InstallDir));
            Assert.IsTrue(r.SelfDeleteScheduled);
            Assert.AreEqual(1, fx.SelfDelete.Calls.Count);
            Assert.AreEqual(exe, fx.SelfDelete.Calls[0].Item1);
            Assert.AreEqual(fx.Paths.InstallDir, fx.SelfDelete.Calls[0].Item2);

            Assert.AreEqual("keep me", File.ReadAllText(outsider));
            Assert.IsTrue(File.Exists(Path.Combine(fx.Environment.ProgramsFolder, "Other.lnk")));
            Assert.AreEqual("Other", fx.Registry.GetValue(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\OtherApp", "DisplayName"));

            Assert.AreEqual(AcClientUninstallOutcome.NotRequested, r.AcClient);
            Assert.AreEqual(0, fx.Processes.Ran.Count, "the AC client is never touched by default");
        }

        [TestMethod]
        public void Uninstall_FromOutsideInstallDir_RemovesInstallDirCompletely()
        {
            string elsewhere = fx.WriteDownloadedExe(Encoding.ASCII.GetBytes("dl"));
            UninstallResult r = Uninstall(running: elsewhere);

            Assert.IsTrue(r.Complete, string.Join("; ", r.Failures));
            Assert.IsFalse(Directory.Exists(fx.Paths.InstallDir));
            Assert.IsFalse(r.SelfDeleteScheduled);
            Assert.AreEqual(0, fx.SelfDelete.Calls.Count);
            Assert.IsTrue(File.Exists(elsewhere), "a copy outside InstallDir is not ours to delete");
        }

        [TestMethod]
        public void Uninstall_SecondRun_IsHarmless()
        {
            Uninstall();
            UninstallResult again = Uninstall();
            Assert.IsTrue(again.Complete, string.Join("; ", again.Failures));
        }

        [TestMethod]
        public void Uninstall_ForeignShortcutWithOurName_IsKept()
        {
            File.WriteAllText(fx.Environment.ShortcutPath, @"C:\Somewhere\Else\thing.exe");
            Uninstall();
            Assert.IsTrue(File.Exists(fx.Environment.ShortcutPath));
        }

        [TestMethod]
        public void Uninstall_SelfDeleteRefused_IsReported()
        {
            fx.SelfDelete.Result = false;
            UninstallResult r = Uninstall();
            Assert.IsFalse(r.Complete);
            Assert.AreEqual(LauncherError.UninstallIncomplete, r.Code);
            Assert.IsTrue(r.Failures.Any(f => f.Contains(exe)));
        }

        [TestMethod]
        public void Uninstall_PartialFailures_ContinueAndAreReported()
        {
            fx.Credentials.FailList = true;
            fx.Registry.FailWrites = true;
            UninstallResult r = Uninstall();

            Assert.AreEqual(2, r.Failures.Count, string.Join("; ", r.Failures));
            Assert.IsFalse(Directory.Exists(fx.Paths.DataDir), "later steps still ran");
            Assert.IsFalse(File.Exists(fx.Environment.ShortcutPath));
        }

        [TestMethod]
        public void Uninstall_UnexpectedFolders_AreRefused()
        {
            var env = fx.Environment;
            var odd = new SelfInstallEnvironment
            {
                Paths = new BadPaths(fx.Root).Paths,
                UserRegistry = env.UserRegistry,
                MachineRegistry = env.MachineRegistry,
                Shortcuts = env.Shortcuts,
                ProgramsFolder = env.ProgramsFolder,
                FileSystem = env.FileSystem,
                Processes = env.Processes,
                Credentials = env.Credentials,
                SelfDelete = env.SelfDelete,
            };

            Assert.ThrowsExactly<ArgumentException>(() => new SelfUninstaller(odd).Uninstall(new UninstallOptions()));
            Assert.IsTrue(Directory.Exists(fx.Paths.DataDir), "nothing was deleted");
        }

        [TestMethod]
        public void Uninstall_DoesNotFollowAJunctionOutOfDataDir()
        {
            string victim = Path.Combine(fx.Root, "victim");
            Directory.CreateDirectory(victim);
            File.WriteAllText(Path.Combine(victim, "precious.txt"), "keep");
            string link = Path.Combine(fx.Paths.DataDir, "link");
            if (!JunctionHelper.TryCreate(link, victim))
            {
                Assert.Inconclusive("Could not create a junction on this machine.");
            }

            UninstallResult r = Uninstall();

            Assert.IsTrue(r.Complete, string.Join("; ", r.Failures));
            Assert.IsFalse(Directory.Exists(fx.Paths.DataDir));
            Assert.AreEqual("keep", File.ReadAllText(Path.Combine(victim, "precious.txt")), "the junction target must survive");
        }

        [TestMethod]
        public void RemoveAcClient_RunsTheClientsOwnUninstallString()
        {
            fx.MachineRegistry.Set(AcInstallLocator.UninstallRoot + "\\" + AcInstallLocator.UninstallGuid, "UninstallString", "MsiExec.exe /X{F0EE55BA-193D-4670-90C0-76E0E25F3A08}");

            UninstallResult r = Uninstall(removeAc: true);

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

            UninstallResult r = Uninstall(removeAc: true);

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

            UninstallResult r = Uninstall(removeAc: true);

            Assert.AreEqual(AcClientUninstallOutcome.NotFound, r.AcClient);
            Assert.AreEqual(0, fx.Processes.Ran.Count);
        }

        [TestMethod]
        public void RemoveAcClient_NotInstalled_IsNotFound()
        {
            Assert.AreEqual(AcClientUninstallOutcome.NotFound, Uninstall(removeAc: true).AcClient);
        }

        [TestMethod]
        public void RemoveAcClient_UacDeclined_IsNotAFailure()
        {
            fx.MachineRegistry.Set(AcInstallLocator.UninstallRoot + "\\" + AcInstallLocator.UninstallGuid, "UninstallString", "MsiExec.exe /X{x}");
            fx.Processes.RunFailure = psi => new Win32Exception(1223);

            UninstallResult r = Uninstall(removeAc: true);

            Assert.AreEqual(AcClientUninstallOutcome.Declined, r.AcClient);
            Assert.IsTrue(r.Complete, string.Join("; ", r.Failures));
        }

        [TestMethod]
        public void RemoveAcClient_NonZeroExit_IsReported()
        {
            fx.MachineRegistry.Set(AcInstallLocator.UninstallRoot + "\\" + AcInstallLocator.UninstallGuid, "UninstallString", "MsiExec.exe /X{x}");
            fx.Processes.RunExitCode = psi => 1603;

            UninstallResult r = Uninstall(removeAc: true);

            Assert.AreEqual(AcClientUninstallOutcome.Failed, r.AcClient);
            Assert.AreEqual(1603, r.AcClientExitCode);
            Assert.IsFalse(r.Complete);
            Assert.IsFalse(Directory.Exists(fx.Paths.DataDir), "our own removal still ran");
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
            SelfUninstaller.SplitCommandLine(line, out p, out a);
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
