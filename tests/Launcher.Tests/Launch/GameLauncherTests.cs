using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Detection;
using DreamWeave.Launcher.Core.Launch;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.Launch
{
    [TestClass]
    public class GameLauncherTests
    {
        private sealed class FakeStarter : IProcessStarter
        {
            public List<ProcessStartInfo> Started { get; } = new List<ProcessStartInfo>();

            public int NextId { get; set; } = 4242;

            public StartedClient Last { get; private set; }

            public Exception Throw { get; set; }

            public StartedClient Start(ProcessStartInfo startInfo)
            {
                if (Throw != null)
                {
                    throw Throw;
                }

                Started.Add(startInfo);
                Last = new StartedClient(NextId, new IntPtr(NextId + 1));
                return Last;
            }
        }

        private sealed class FakeFs : IAcFileSystem
        {
            public HashSet<string> Files { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public bool DirectoryExists(string path)
            {
                return true;
            }

            public bool FileExists(string path)
            {
                return Files.Contains(path);
            }

            public FileStamp GetStamp(string path)
            {
                return null;
            }

            public Stream OpenRead(string path)
            {
                throw new NotSupportedException();
            }
        }

        private const string Folder = @"C:\Games\Asheron's Call";
        private static readonly string Exe = Folder + @"\acclient.exe";

        private static GameLauncher Make(FakeStarter s, out FakeFs fs, bool exeExists = true)
        {
            fs = new FakeFs();
            if (exeExists)
            {
                fs.Files.Add(Exe);
            }

            return new GameLauncher(s, fs);
        }

        [TestMethod]
        public void Launch_StartsClientWithWorkingDirectoryAndArgs()
        {
            var s = new FakeStarter();
            FakeFs fs;
            StartedClient client = Make(s, out fs).Launch(Folder + "\\", "-a \"x\" -v \"y\"");
            Assert.AreEqual(4242, client.Id);
            Assert.AreSame(s.Last, client, "the starter's own client (with its CreateProcess handle) is handed back, not rebuilt from the id");
            Assert.AreEqual(1, s.Started.Count);
            ProcessStartInfo psi = s.Started[0];
            Assert.AreEqual(Exe, psi.FileName);
            Assert.AreEqual(Folder, psi.WorkingDirectory);
            Assert.AreEqual("-a \"x\" -v \"y\"", psi.Arguments);
            Assert.IsFalse(psi.UseShellExecute);
            Assert.AreNotEqual("runas", psi.Verb);
        }

        [TestMethod]
        public void Launch_MissingExe_ThrowsClientNotFound_AndStartsNothing()
        {
            var s = new FakeStarter();
            FakeFs fs;
            LauncherException ex = Assert.ThrowsExactly<LauncherException>(() => Make(s, out fs, false).Launch(Folder, "-a x"));
            Assert.AreEqual(LauncherError.ClientNotFound, ex.Code);
            Assert.AreEqual(0, s.Started.Count);
        }

        [TestMethod]
        [DataRow(2, LauncherError.ClientNotFound)]
        [DataRow(3, LauncherError.ClientNotFound)]
        [DataRow(5, LauncherError.AccessDenied)]
        [DataRow(740, LauncherError.ClientRequiresElevation)]
        [DataRow(193, LauncherError.LaunchFailed)]
        public void Launch_Win32Errors_AreMapped(int native, LauncherError expected)
        {
            var s = new FakeStarter { Throw = new Win32Exception(native) };
            FakeFs fs;
            LauncherException ex = Assert.ThrowsExactly<LauncherException>(() => Make(s, out fs).Launch(Folder, "-v \"hunter2\""));
            Assert.AreEqual(expected, ex.Code);
            Assert.IsFalse(ex.Detail.Contains("hunter2"));
        }

        [TestMethod]
        public void Launch_EmptyFolder_Throws()
        {
            var s = new FakeStarter();
            FakeFs fs;
            Assert.ThrowsExactly<ArgumentException>(() => Make(s, out fs).Launch("", "x"));
        }

        [TestMethod]
        public void Launch_NullArgs_BecomesEmptyString()
        {
            var s = new FakeStarter();
            FakeFs fs;
            Make(s, out fs).Launch(Folder, null);
            Assert.AreEqual(string.Empty, s.Started[0].Arguments);
        }
    }
}
