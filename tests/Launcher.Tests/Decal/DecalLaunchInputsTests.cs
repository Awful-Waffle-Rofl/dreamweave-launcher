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

                int code = DecalLaunchInputs.Validate(dir.Path, inject, () => dir.Path, out DecalLaunchInputs inputs, out string token);

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

                int code = DecalLaunchInputs.Validate(gone, inject, () => dir.Path, out DecalLaunchInputs inputs, out string token);

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

                int code = DecalLaunchInputs.Validate(dir.Path, inject, () => dir.Path, out DecalLaunchInputs inputs, out string token);

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

                int code = DecalLaunchInputs.Validate(dir.Path, @"Decal\Inject.dll", () => dir.Path, out DecalLaunchInputs inputs, out string token);

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

                int code = DecalLaunchInputs.Validate(dir.Path, other, () => dir.Path, out DecalLaunchInputs inputs, out string token);

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

                int code = DecalLaunchInputs.Validate(dir.Path, inject, () => dir.Path, out DecalLaunchInputs inputs, out string token);

                Assert.AreEqual(DecalLaunchResult.ExitInjectInvalid, code);
                Assert.AreEqual(DecalLaunchResult.TokenInjectMissing, token);
                Assert.IsNull(inputs);
            }
        }

        private static bool TryMakeJunction(string link, string target)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + target + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    return p.ExitCode == 0 && Directory.Exists(link);
                }
            }
            catch (System.Exception)
            {
                return false;
            }
        }

        private static bool TryMakeFileSymlink(string link, string target)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c mklink \"" + link + "\" \"" + target + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    return p.ExitCode == 0 && File.Exists(link);
                }
            }
            catch (System.Exception)
            {
                return false;
            }
        }

        [TestMethod]
        public void InjectInADifferentFolderThanTheRegistryAgentPath_IsRejected()
        {
            using (var dir = new TempDir())
            {
                MakeFile(dir.Path, "acclient.exe");
                string other = Path.Combine(dir.Path, "other");
                Directory.CreateDirectory(other);
                string inject = MakeFile(other, "Inject.dll");
                MakeFile(dir.Path, "Inject.dll");

                int code = DecalLaunchInputs.Validate(dir.Path, inject, () => dir.Path, out DecalLaunchInputs inputs, out string token);

                Assert.AreEqual(DecalLaunchResult.ExitInjectInvalid, code);
                Assert.AreEqual(DecalLaunchResult.TokenInjectNotRegistered, token);
                Assert.IsNull(inputs);
            }
        }

        [TestMethod]
        public void InjectPathWithDotDotSegment_IsRejected()
        {
            using (var dir = new TempDir())
            {
                MakeFile(dir.Path, "acclient.exe");
                MakeFile(dir.Path, "Inject.dll");
                string sub = Path.Combine(dir.Path, "sub");
                Directory.CreateDirectory(sub);
                string dotted = Path.Combine(sub, "..", "Inject.dll");

                int code = DecalLaunchInputs.Validate(dir.Path, dotted, () => dir.Path, out DecalLaunchInputs inputs, out string token);

                Assert.AreEqual(DecalLaunchResult.ExitInjectInvalid, code);
                Assert.AreEqual(DecalLaunchResult.TokenInjectPathInvalid, token);
                Assert.IsNull(inputs);
            }
        }

        [TestMethod]
        public void RegistryAgentPathMissingOrThrowing_IsRejected()
        {
            using (var dir = new TempDir())
            {
                MakeFile(dir.Path, "acclient.exe");
                string inject = MakeFile(dir.Path, "Inject.dll");

                foreach (System.Func<string> reader in new System.Func<string>[] { () => null, () => "  ", () => throw new System.InvalidOperationException() })
                {
                    int code = DecalLaunchInputs.Validate(dir.Path, inject, reader, out DecalLaunchInputs inputs, out string token);

                    Assert.AreEqual(DecalLaunchResult.ExitInjectInvalid, code);
                    Assert.AreEqual(DecalLaunchResult.TokenInjectNotRegistered, token);
                    Assert.IsNull(inputs);
                }
            }
        }

        [TestMethod]
        public void RegistryAgentPath_MatchesCaseInsensitivelyWithTrailingSlash()
        {
            using (var dir = new TempDir())
            {
                MakeFile(dir.Path, "acclient.exe");
                string inject = MakeFile(dir.Path, "Inject.dll");

                int code = DecalLaunchInputs.Validate(dir.Path, inject, () => dir.Path.ToUpperInvariant() + "\\", out DecalLaunchInputs inputs, out string token);

                Assert.AreEqual(DecalLaunchResult.ExitSuccess, code, token);
                Assert.IsNotNull(inputs);
            }
        }

        [TestMethod]
        public void GameFolderThatIsAJunction_IsAllowed()
        {
            using (var dir = new TempDir())
            {
                string real = Path.Combine(dir.Path, "real");
                Directory.CreateDirectory(real);
                MakeFile(real, "acclient.exe");
                string inject = MakeFile(dir.Path, "Inject.dll");
                string link = Path.Combine(dir.Path, "gamelink");
                if (!TryMakeJunction(link, real))
                {
                    Assert.Inconclusive("Could not create a junction on this machine.");
                }

                try
                {
                    int code = DecalLaunchInputs.Validate(link, inject, () => dir.Path, out DecalLaunchInputs inputs, out string token);

                    Assert.AreEqual(DecalLaunchResult.ExitSuccess, code, token);
                    Assert.IsNotNull(inputs);
                }
                finally
                {
                    Directory.Delete(link);
                }
            }
        }

        [TestMethod]
        public void InjectFolderThatIsAJunction_IsRejected()
        {
            using (var dir = new TempDir())
            {
                MakeFile(dir.Path, "acclient.exe");
                string real = Path.Combine(dir.Path, "real");
                Directory.CreateDirectory(real);
                MakeFile(real, "Inject.dll");
                string link = Path.Combine(dir.Path, "decallink");
                if (!TryMakeJunction(link, real))
                {
                    Assert.Inconclusive("Could not create a junction on this machine.");
                }

                try
                {
                    int code = DecalLaunchInputs.Validate(dir.Path, Path.Combine(link, "Inject.dll"), () => link, out DecalLaunchInputs inputs, out string token);

                    Assert.AreEqual(DecalLaunchResult.ExitReparsePoint, code);
                    Assert.AreEqual(DecalLaunchResult.TokenInjectReparsePoint, token);
                    Assert.IsNull(inputs);
                }
                finally
                {
                    Directory.Delete(link);
                }
            }
        }

        [TestMethod]
        public void AcclientThatIsALink_IsRejected()
        {
            using (var dir = new TempDir())
            {
                string real = MakeFile(dir.Path, "real-acclient.exe");
                string inject = MakeFile(dir.Path, "Inject.dll");
                string game = Path.Combine(dir.Path, "game");
                Directory.CreateDirectory(game);
                if (!TryMakeFileSymlink(Path.Combine(game, "acclient.exe"), real))
                {
                    Assert.Inconclusive("Could not create a file symbolic link on this machine.");
                }

                int code = DecalLaunchInputs.Validate(game, inject, () => dir.Path, out DecalLaunchInputs inputs, out string token);

                Assert.AreEqual(DecalLaunchResult.ExitReparsePoint, code);
                Assert.AreEqual(DecalLaunchResult.TokenAcclientReparsePoint, token);
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
