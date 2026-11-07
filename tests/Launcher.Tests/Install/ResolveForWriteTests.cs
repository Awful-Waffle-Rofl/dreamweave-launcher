using System.IO;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Detection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.Install
{
    [TestClass]
    public class ResolveForWriteTests
    {
        [TestMethod]
        public void PlainPaths_ResolveLikeResolve()
        {
            using (var dir = new TempDir())
            {
                File.WriteAllBytes(Path.Combine(dir.Path, "a.dat"), new byte[] { 1 });
                Assert.AreEqual(AcPathSafety.Resolve(dir.Path, "a.dat"), AcPathSafety.ResolveForWrite(dir.Path, "a.dat"));
                Assert.AreEqual(AcPathSafety.Resolve(dir.Path, "missing/deeper/x.dat"), AcPathSafety.ResolveForWrite(dir.Path, "missing/deeper/x.dat"));
            }
        }

        [TestMethod]
        public void LexicallyUnsafe_IsManifestInvalid()
        {
            using (var dir = new TempDir())
            {
                Assert.AreEqual(LauncherError.ManifestInvalid, Assert.ThrowsExactly<LauncherException>(() => AcPathSafety.ResolveForWrite(dir.Path, "../x.dat")).Code);
            }
        }

        [TestMethod]
        public void JunctionSegment_IsRefused_ButJunctionedRootIsAllowed()
        {
            using (var dir = new TempDir())
            {
                string ac = Path.Combine(dir.Path, "ac");
                Directory.CreateDirectory(ac);
                string reason;
                if (!InstallData.TryMakeJunction(Path.Combine(ac, "sub"), Path.Combine(dir.Path, "elsewhere"), out reason))
                {
                    Assert.Inconclusive(reason);
                }

                var ex = Assert.ThrowsExactly<LauncherException>(() => AcPathSafety.ResolveForWrite(ac, "sub/x.dat"));
                Assert.AreEqual(LauncherError.ReparsePointInPath, ex.Code);

                // The game folder itself may be a link (a moved install): only segments below it are checked.
                string linkedRoot = Path.Combine(dir.Path, "linked-ac");
                if (!InstallData.TryMakeJunction(linkedRoot, ac, out reason))
                {
                    Assert.Inconclusive(reason);
                }

                Assert.AreEqual(Path.Combine(linkedRoot, "a.dat"), AcPathSafety.ResolveForWrite(linkedRoot, "a.dat"));
            }
        }
    }
}
