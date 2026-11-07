using System;
using System.Runtime.InteropServices;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Launch;
using DreamWeave.Launcher.Core.Manifests;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.Launch
{
    [TestClass]
    public class LaunchArgsBuilderTests
    {
        private const string T = LaunchInfo.DefaultArgTemplate;

        private static string Ok(string user = "alice", string pass = "secret", string host = "play.acdreamweave.com", int port = 9000, string extra = "")
        {
            return LaunchArgsBuilder.Build(T, user, pass, host, port, extra);
        }

        private static void AssertInvalid(Action a)
        {
            LauncherException ex = Assert.ThrowsExactly<LauncherException>(a);
            Assert.AreEqual(LauncherError.InvalidCredentials, ex.Code);
            Assert.IsFalse(string.IsNullOrWhiteSpace(ex.Detail));
        }

        private static void AssertManifest(Action a)
        {
            LauncherException ex = Assert.ThrowsExactly<LauncherException>(a);
            Assert.AreEqual(LauncherError.ManifestInvalid, ex.Code);
            Assert.IsFalse(string.IsNullOrWhiteSpace(ex.Detail));
        }

        [TestMethod]
        public void DefaultTemplate_ProducesExactCommandLine()
        {
            Assert.AreEqual("-a \"alice\" -v \"secret\" -h play.acdreamweave.com:9000 -rodat on", Ok());
        }

        [TestMethod]
        public void ExtraArgs_AreAppendedAfterTemplate()
        {
            Assert.AreEqual("-a \"alice\" -v \"secret\" -h play.acdreamweave.com:9000 -rodat on -foo bar", Ok(extra: " -foo bar "));
        }

        [TestMethod]
        public void PasswordWithSpaceAndSymbols_IsKeptVerbatim()
        {
            Assert.IsTrue(Ok(pass: "pa ss!x{user}").Contains("-v \"pa ss!x{user}\""));
        }

        [TestMethod]
        public void ValueLookingLikePlaceholder_IsNotReExpanded()
        {
            string s = Ok(user: "{pass}", pass: "{host}");
            Assert.IsTrue(s.StartsWith("-a \"{pass}\" -v \"{host}\" "));
        }

        [TestMethod]
        public void TemplateWithoutPass_OmitsThePassword()
        {
            string s = LaunchArgsBuilder.Build("-a \"{user}\" -h {host}:{port}", "alice", "hunter2", "localhost", 9050, null);
            Assert.AreEqual("-a \"alice\" -h localhost:9050", s);
            Assert.IsFalse(s.Contains("hunter2"));
        }

        [TestMethod]
        public void BuildRedacted_MasksPassword()
        {
            string s = LaunchArgsBuilder.BuildRedacted(T, "alice", "hunter2", "localhost", 9050, "");
            Assert.IsFalse(s.Contains("hunter2"));
            Assert.IsTrue(s.Contains("-v \"********\""));
        }

        [TestMethod]
        public void BuildRedacted_StillValidates()
        {
            AssertInvalid(() => LaunchArgsBuilder.BuildRedacted(T, "alice", "a\"b", "localhost", 9050, ""));
        }

        [TestMethod]
        public void RejectionMessages_NeverContainThePassword()
        {
            LauncherException ex = Assert.ThrowsExactly<LauncherException>(() => Ok(pass: "my\"SECRETpw"));
            Assert.IsFalse(ex.Detail.Contains("SECRETpw"));
            ex = Assert.ThrowsExactly<LauncherException>(() => Ok(pass: new string('p', 65)));
            Assert.IsFalse(ex.Detail.Contains("ppp"));
        }

        [TestMethod]
        public void User_QuoteRejected()
        {
            AssertInvalid(() => Ok(user: "al\"ice"));
        }

        [TestMethod]
        public void Pass_QuoteRejected()
        {
            AssertInvalid(() => Ok(pass: "se\"cret"));
        }

        [TestMethod]
        [DataRow("al\nice")]
        [DataRow("al\0ice")]
        [DataRow("al\tice")]
        public void User_ControlOrTabRejected(string user)
        {
            AssertInvalid(() => Ok(user: user));
        }

        [TestMethod]
        [DataRow("se\ncret")]
        [DataRow("se\rcret")]
        [DataRow("se\0cret")]
        [DataRow("se\tcret")]
        public void Pass_ControlRejected(string pass)
        {
            AssertInvalid(() => Ok(pass: pass));
        }

        [TestMethod]
        [DataRow("al ice")]
        [DataRow(" alice")]
        [DataRow("alice ")]
        public void User_WhitespaceRejected(string user)
        {
            AssertInvalid(() => Ok(user: user));
        }

        [TestMethod]
        public void Pass_SpaceAllowed()
        {
            Assert.IsTrue(Ok(pass: "pa ss").Contains("\"pa ss\""));
        }

        [TestMethod]
        public void TrailingBackslash_Rejected()
        {
            AssertInvalid(() => Ok(pass: "abc\\"));
            AssertInvalid(() => Ok(user: "abc\\"));
            Assert.IsTrue(Ok(pass: "a\\bc").Contains("a\\bc"));
        }

        [TestMethod]
        [DataRow(null)]
        [DataRow("")]
        public void EmptyUserOrPass_Rejected(string v)
        {
            AssertInvalid(() => Ok(user: v));
            AssertInvalid(() => Ok(pass: v));
        }

        [TestMethod]
        public void Lengths_AreEnforcedAtTheBoundary()
        {
            Assert.IsNotNull(Ok(user: new string('u', LaunchArgsBuilder.MaxUserLength)));
            AssertInvalid(() => Ok(user: new string('u', LaunchArgsBuilder.MaxUserLength + 1)));
            Assert.IsNotNull(Ok(pass: new string('p', LaunchArgsBuilder.MaxPasswordLength)));
            AssertInvalid(() => Ok(pass: new string('p', LaunchArgsBuilder.MaxPasswordLength + 1)));
        }

        [TestMethod]
        [DataRow("play.acdreamweave.com")]
        [DataRow("localhost")]
        [DataRow("my-host.example.org")]
        [DataRow("127.0.0.1")]
        [DataRow("192.168.1.20")]
        public void ValidHosts_Accepted(string host)
        {
            Assert.IsTrue(Ok(host: host).Contains("-h " + host + ":9000"));
        }

        [TestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("bad host")]
        [DataRow("host\"x")]
        [DataRow("host;calc")]
        [DataRow("-bad.example.com")]
        [DataRow("bad-.example.com")]
        [DataRow("a..b")]
        [DataRow("host.")]
        [DataRow("999.1.1.1")]
        [DataRow("1.2.3")]
        [DataRow("1.2.3.4.5")]
        [DataRow("[::1]")]
        [DataRow("[2001:db8::1]")]
        [DataRow("[::1")]
        [DataRow("[]")]
        [DataRow("[1.2.3.4]")]
        [DataRow("[nothex]")]
        [DataRow("[fe80::1%5]")]
        [DataRow("host:9000")]
        public void InvalidHosts_Rejected(string host)
        {
            AssertManifest(() => Ok(host: host));
        }

        [TestMethod]
        public void OverlongHost_Rejected()
        {
            AssertManifest(() => Ok(host: new string('a', 64) + ".com"));
            AssertManifest(() => Ok(host: string.Join(".", new[] { new string('a', 60), new string('b', 60), new string('c', 60), new string('d', 60), new string('e', 60) })));
        }

        [TestMethod]
        [DataRow(1)]
        [DataRow(9000)]
        [DataRow(65535)]
        public void ValidPorts_Accepted(int port)
        {
            Assert.IsTrue(Ok(port: port).Contains(":" + port + " "));
        }

        [TestMethod]
        [DataRow(0)]
        [DataRow(-1)]
        [DataRow(65536)]
        public void InvalidPorts_Rejected(int port)
        {
            AssertManifest(() => Ok(port: port));
        }

        [TestMethod]
        [DataRow("-a {usr}")]
        [DataRow("-a {} ")]
        [DataRow("-a {USER}")]
        [DataRow("-a \"{user}\" -x {nope}")]
        public void UnknownPlaceholder_Rejected(string template)
        {
            LauncherException ex = Assert.ThrowsExactly<LauncherException>(() => LaunchArgsBuilder.Build(template, "alice", "secret", "localhost", 9000, ""));
            Assert.AreEqual(LauncherError.ManifestInvalid, ex.Code);
            Assert.IsTrue(ex.Detail.Contains("placeholder"));
        }

        [TestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("   ")]
        public void EmptyTemplate_Rejected(string template)
        {
            AssertManifest(() => LaunchArgsBuilder.Build(template, "alice", "secret", "localhost", 9000, ""));
        }

        [TestMethod]
        public void ExtraArgs_ControlCharsRejected()
        {
            AssertManifest(() => Ok(extra: "-x\n-y"));
        }

        // C# escape text on purpose: the source stays ASCII.
        [TestMethod]
        [DataRow("p\u00E4ss")]
        [DataRow("\u4E2D")]
        [DataRow("a\uD83D\uDE00b")]
        [DataRow("\u00A0x")]
        [DataRow("x\u007F")]
        public void NonAscii_RejectedInBothFields(string value)
        {
            AssertInvalid(() => Ok(user: value));
            AssertInvalid(() => Ok(pass: value));
        }

        [TestMethod]
        public void PrintableAsciiBoundaries_Accepted()
        {
            Assert.IsNotNull(Ok(user: "!~", pass: " ~!"));
        }

        [TestMethod]
        public void UnquotedPassPlaceholder_RejectsPasswordWithSpace()
        {
            AssertInvalid(() => LaunchArgsBuilder.Build("-a \"{user}\" -v {pass} -h {host}:{port}", "alice", "pa ss", "localhost", 9000, ""));
            Assert.AreEqual("-a \"alice\" -v pass -h localhost:9000", LaunchArgsBuilder.Build("-a \"{user}\" -v {pass} -h {host}:{port}", "alice", "pass", "localhost", 9000, ""));
            AssertInvalid(() => LaunchArgsBuilder.BuildRedacted("-v {pass}", "alice", "pa ss", "localhost", 9000, ""));
        }

        [DllImport("shell32.dll", SetLastError = true)]
        private static extern IntPtr CommandLineToArgvW([MarshalAs(UnmanagedType.LPWStr)] string commandLine, out int argc);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr mem);

        private static string[] Argv(string commandLine)
        {
            int argc;
            IntPtr p = CommandLineToArgvW(commandLine, out argc);
            Assert.AreNotEqual(IntPtr.Zero, p);
            try
            {
                var result = new string[argc];
                for (int i = 0; i < argc; i++)
                {
                    result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(p, i * IntPtr.Size));
                }

                return result;
            }
            finally
            {
                LocalFree(p);
            }
        }

        [TestMethod]
        public void AdversarialValues_RoundTripThroughCommandLineToArgv()
        {
            string[] users = { "a\\b", "a\\\\b", "{pass}", "%PATH%", "{", "}{", "a^b", "a&b", "x|y", "{user}{host}", "-a" };
            string[] passes = { "a\\b", "a\\\\b", "a\\\\ b", "a\\ b", "{pass}", "%PATH%", "{", "}", "a^b", "a&b", "x|y<z>", "p a s s", "-v", "\\\\x" };
            foreach (string user in users)
            {
                foreach (string pass in passes)
                {
                    string line = LaunchArgsBuilder.Build(T, user, pass, "play.acdreamweave.com", 9000, "");
                    // The first token is parsed as the program name, so prepend one.
                    string[] argv = Argv("acclient.exe " + line);
                    CollectionAssert.AreEqual(
                        new[] { "acclient.exe", "-a", user, "-v", pass, "-h", "play.acdreamweave.com:9000", "-rodat", "on" },
                        argv,
                        "user=[" + user + "] pass=[" + pass + "]");
                }
            }
        }

        [TestMethod]
        [DataRow("abc\\")]
        [DataRow("abc\\\\")]
        [DataRow("\\")]
        public void TrailingBackslashValues_AreRejected(string value)
        {
            AssertInvalid(() => Ok(user: value));
            AssertInvalid(() => Ok(pass: value));
        }
    }
}