using System.IO;
using DreamWeave.DecalLaunch;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.Decal
{
    /// <summary>
    /// The helper's argument and validation layer, tested as a pure function: it must reject bad inputs
    /// and never start anything. Launching is not exercised here.
    /// </summary>
    [TestClass]
    public class DecalLaunchInputsTests
    {
        private static string MakeFile(string dir, string name)
        {
            string path = Path.Combine(dir, name);
            File.WriteAllBytes(path, new byte[] { 0 });
            return path;
        }

        [TestMethod]
        public void ValidFolderAndInject_Succeeds()
        {
            using (var dir = new TempDir())
            {
                MakeFile(dir.Path, "acclient.exe");
                string inject = MakeFile(dir.Path, "Inject.dll");

                int code = DecalLaunchInputs.Validate(dir.Path, inject, out DecalLaunchInputs inputs, out string token);

                Assert.AreEqual(DecalLaunchResult.ExitSuccess, code);
                Assert.IsNull(token);
                Assert.IsNotNull(inputs);
                Assert.AreEqual(Path.Combine(dir.Path, "acclient.exe"), inputs.AcclientPath);
                Assert.AreEqual(inject, inputs.InjectDllPath);
            }
        }

        [TestMethod]
        public void MissingGameFolder_IsRejected()
        {
            using (var dir = new TempDir())
            {
                string inject = MakeFile(dir.Path, "Inject.dll");
                string gone = Path.Combine(dir.Path, "no-such-folder");

                int code = DecalLaunchInputs.Validate(gone, inject, out DecalLaunchInputs inputs, out string token);

                Assert.AreEqual(DecalLaunchResult.ExitGameFolderInvalid, code);
                Assert.AreEqual(DecalLaunchResult.TokenGameFolderMissing, token);
                Assert.IsNull(inputs);
            }
        }

        [TestMethod]
        public void GameFolderWithoutAcclient_IsRejected()
        {
            using (var dir = new TempDir())
            {
                string inject = MakeFile(dir.Path, "Inject.dll");

                int code = DecalLaunchInputs.Validate(dir.Path, inject, out DecalLaunchInputs inputs, out string token);

                Assert.AreEqual(DecalLaunchResult.ExitGameFolderInvalid, code);
                Assert.AreEqual(DecalLaunchResult.TokenAcclientMissing, token);
                Assert.IsNull(inputs);
            }
        }

        [TestMethod]
        public void RelativeInjectPath_IsRejected()
        {
            using (var dir = new TempDir())
            {
                MakeFile(dir.Path, "acclient.exe");
                MakeFile(dir.Path, "Inject.dll");

                int code = DecalLaunchInputs.Validate(dir.Path, @"Decal\Inject.dll", out DecalLaunchInputs inputs, out string token);

                Assert.AreEqual(DecalLaunchResult.ExitInjectInvalid, code);
                Assert.AreEqual(DecalLaunchResult.TokenInjectPathInvalid, token);
                Assert.IsNull(inputs);
            }
        }

        [TestMethod]
        public void InjectPathNotNamedInjectDll_IsRejected()
        {
            using (var dir = new TempDir())
            {
                MakeFile(dir.Path, "acclient.exe");
                string other = MakeFile(dir.Path, "Evil.dll");

                int code = DecalLaunchInputs.Validate(dir.Path, other, out DecalLaunchInputs inputs, out string token);

                Assert.AreEqual(DecalLaunchResult.ExitInjectInvalid, code);
                Assert.AreEqual(DecalLaunchResult.TokenInjectPathInvalid, token);
                Assert.IsNull(inputs);
            }
        }

        [TestMethod]
        public void InjectDllMissingOnDisk_IsRejected()
        {
            using (var dir = new TempDir())
            {
                MakeFile(dir.Path, "acclient.exe");
                string inject = Path.Combine(dir.Path, "Inject.dll");

                int code = DecalLaunchInputs.Validate(dir.Path, inject, out DecalLaunchInputs inputs, out string token);

                Assert.AreEqual(DecalLaunchResult.ExitInjectInvalid, code);
                Assert.AreEqual(DecalLaunchResult.TokenInjectMissing, token);
                Assert.IsNull(inputs);
            }
        }

        [TestMethod]
        public void PidLine_RoundTrips()
        {
            Assert.AreEqual(4321, DecalLaunchResult.ParsePid(DecalLaunchResult.PidLine(4321)));
            Assert.AreEqual(0, DecalLaunchResult.ParsePid("PID=0"));
            Assert.AreEqual(0, DecalLaunchResult.ParsePid("PID=-5"));
            Assert.AreEqual(0, DecalLaunchResult.ParsePid("nope"));
            Assert.AreEqual(0, DecalLaunchResult.ParsePid(null));
        }
    }
}
