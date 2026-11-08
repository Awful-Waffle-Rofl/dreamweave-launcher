using DreamWeave.Launcher.Core.Detection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.Detection
{
    [TestClass]
    public class DecalLocatorTests
    {
        private const string AgentFolder = @"C:\Program Files (x86)\Decal 3.0\";
        private static readonly string InjectDll = AgentFolder + "Inject.dll";

        private static DecalLocator Make(FakeRegistry reg, FakeFs fs)
        {
            return new DecalLocator(reg, fs, _ => "2.9.8.3");
        }

        [TestMethod]
        public void Present_ReturnsInjectDllPathAndVersion()
        {
            var reg = new FakeRegistry();
            reg.Set(DecalLocator.AgentKeyPath, DecalLocator.AgentPathValue, AgentFolder);
            var fs = new FakeFs();
            fs.SetFile(InjectDll, new byte[] { 1, 2, 3 });

            DecalLocation loc = Make(reg, fs).Locate();

            Assert.IsNotNull(loc);
            Assert.AreEqual(InjectDll, loc.InjectDllPath);
            Assert.AreEqual("2.9.8.3", loc.FileVersion);
        }

        [TestMethod]
        public void Absent_WhenRegistryKeyMissing_ReturnsNull()
        {
            var reg = new FakeRegistry();
            var fs = new FakeFs();
            fs.SetFile(InjectDll, new byte[] { 1 });

            Assert.IsNull(Make(reg, fs).Locate());
        }

        [TestMethod]
        public void Absent_WhenAgentPathBlank_ReturnsNull()
        {
            var reg = new FakeRegistry();
            reg.Set(DecalLocator.AgentKeyPath, DecalLocator.AgentPathValue, "   ");
            Assert.IsNull(Make(reg, new FakeFs()).Locate());
        }

        [TestMethod]
        public void FileMissing_WhenKeyPresentButInjectDllAbsent_ReturnsNull()
        {
            var reg = new FakeRegistry();
            reg.Set(DecalLocator.AgentKeyPath, DecalLocator.AgentPathValue, AgentFolder);

            Assert.IsNull(Make(reg, new FakeFs()).Locate());
        }

        [TestMethod]
        public void Present_WithoutTrailingSeparator_StillFindsInjectDll()
        {
            var reg = new FakeRegistry();
            reg.Set(DecalLocator.AgentKeyPath, DecalLocator.AgentPathValue, @"C:\Program Files (x86)\Decal 3.0");
            var fs = new FakeFs();
            fs.SetFile(@"C:\Program Files (x86)\Decal 3.0\Inject.dll", new byte[] { 1 });

            DecalLocation loc = Make(reg, fs).Locate();

            Assert.IsNotNull(loc);
            Assert.AreEqual(@"C:\Program Files (x86)\Decal 3.0\Inject.dll", loc.InjectDllPath);
        }

        [TestMethod]
        public void VersionReaderThrowing_DoesNotHideAPresentDecal()
        {
            var reg = new FakeRegistry();
            reg.Set(DecalLocator.AgentKeyPath, DecalLocator.AgentPathValue, AgentFolder);
            var fs = new FakeFs();
            fs.SetFile(InjectDll, new byte[] { 1 });

            var locator = new DecalLocator(reg, fs, _ => throw new System.IO.IOException("boom"));
            DecalLocation loc = locator.Locate();

            Assert.IsNotNull(loc);
            Assert.AreEqual(InjectDll, loc.InjectDllPath);
            Assert.IsNull(loc.FileVersion);
        }
    }
}
