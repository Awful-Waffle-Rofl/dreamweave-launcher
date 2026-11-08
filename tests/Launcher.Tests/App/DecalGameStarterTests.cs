using DreamWeave.DecalLaunch;
using DreamWeave.Launcher.App;
using DreamWeave.Launcher.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamWeave.Launcher.Tests.App
{
    /// <summary>
    /// The pure mapping from the helper's exit code and streams to a result or a launcher error. The
    /// process-by-id lookup lives here and nowhere the client-lock code can reach.
    /// </summary>
    [TestClass]
    public class DecalGameStarterTests
    {
        private static DecalGameStarter Starter()
        {
            return new DecalGameStarter(@"C:\app\DreamWeaveDecalLaunch.exe", @"C:\Decal\Inject.dll", null, null);
        }

        [TestMethod]
        public void Success_WithPid_StartsWithoutInjectionFailure()
        {
            // A pid that is almost certainly not a live process: the by-id lookup fails and the result
            // still reports the client as started (it exited between start and lookup).
            DecalStartResult r = Starter().Interpret(new HelperOutput(0, DecalLaunchResult.PidLine(999999), string.Empty));

            Assert.AreEqual(999999, r.Client.Id);
            Assert.IsFalse(r.InjectionFailed);
            r.Client.Dispose();
        }

        [TestMethod]
        public void Success_WithInjectFailedToken_ReportsInjectionFailed()
        {
            DecalStartResult r = Starter().Interpret(new HelperOutput(0, DecalLaunchResult.PidLine(999998), DecalLaunchResult.TokenInjectFailed + "\n"));

            Assert.AreEqual(999998, r.Client.Id);
            Assert.IsTrue(r.InjectionFailed);
            r.Client.Dispose();
        }

        [TestMethod]
        public void Success_WithoutPid_IsLaunchFailed()
        {
            AssertError(() => Starter().Interpret(new HelperOutput(0, string.Empty, string.Empty)), LauncherError.LaunchFailed);
        }

        [TestMethod]
        public void AcclientMissingToken_IsClientNotFound()
        {
            AssertError(() => Starter().Interpret(new HelperOutput(DecalLaunchResult.ExitGameFolderInvalid, string.Empty, DecalLaunchResult.TokenAcclientMissing)), LauncherError.ClientNotFound);
        }

        [TestMethod]
        public void InjectMissingToken_IsDecalNotFound()
        {
            AssertError(() => Starter().Interpret(new HelperOutput(DecalLaunchResult.ExitInjectInvalid, string.Empty, DecalLaunchResult.TokenInjectMissing)), LauncherError.DecalNotFound);
        }

        [TestMethod]
        public void InjectPathInvalidToken_IsDecalNotFound()
        {
            AssertError(() => Starter().Interpret(new HelperOutput(DecalLaunchResult.ExitInjectInvalid, string.Empty, DecalLaunchResult.TokenInjectPathInvalid)), LauncherError.DecalNotFound);
        }

        [TestMethod]
        public void ReparseToken_IsReparsePointInPath()
        {
            AssertError(() => Starter().Interpret(new HelperOutput(DecalLaunchResult.ExitReparsePoint, string.Empty, DecalLaunchResult.TokenReparsePoint)), LauncherError.ReparsePointInPath);
        }

        [TestMethod]
        public void CreateProcessFailedToken_IsLaunchFailed()
        {
            AssertError(() => Starter().Interpret(new HelperOutput(DecalLaunchResult.ExitCreateProcessFailed, string.Empty, DecalLaunchResult.TokenCreateProcessFailed)), LauncherError.LaunchFailed);
        }

        [TestMethod]
        public void UnknownFailure_IsDecalInjectFailed()
        {
            AssertError(() => Starter().Interpret(new HelperOutput(2, string.Empty, DecalLaunchResult.TokenBadUsage)), LauncherError.DecalInjectFailed);
        }

        [TestMethod]
        public void Start_WhenHelperCannotRun_IsDecalInjectFailed()
        {
            var starter = new DecalGameStarter(@"C:\app\DreamWeaveDecalLaunch.exe", @"C:\Decal\Inject.dll", null,
                (folder, inject, args) => throw new System.ComponentModel.Win32Exception(2));

            AssertError(() => starter.Start(@"C:\Games\AC", "-h host"), LauncherError.DecalInjectFailed);
        }

        [TestMethod]
        public void Start_PassesFolderAndInject_AndTheArgsReachTheHelper()
        {
            string seenFolder = null;
            string seenInject = null;
            string seenArgs = null;
            var starter = new DecalGameStarter(@"C:\app\DreamWeaveDecalLaunch.exe", @"C:\Decal\Inject.dll", null,
                (folder, inject, args) =>
                {
                    seenFolder = folder;
                    seenInject = inject;
                    seenArgs = args;
                    return new HelperOutput(0, DecalLaunchResult.PidLine(999997), string.Empty);
                });

            DecalStartResult r = starter.Start(@"C:\Games\AC", "-h host -a user:secret");

            Assert.AreEqual(@"C:\Games\AC", seenFolder);
            Assert.AreEqual(@"C:\Decal\Inject.dll", seenInject);
            Assert.AreEqual("-h host -a user:secret", seenArgs);
            r.Client.Dispose();
        }

        [TestMethod]
        public void QuoteArg_DoublesTrailingBackslashes()
        {
            Assert.AreEqual("\"C:\\Games\\AC\\\\\"", DecalGameStarter.QuoteArg(@"C:\Games\AC\"));
            Assert.AreEqual("\"C:\\Decal\\Inject.dll\"", DecalGameStarter.QuoteArg(@"C:\Decal\Inject.dll"));
        }

        private static void AssertError(System.Action act, LauncherError expected)
        {
            try
            {
                act();
                Assert.Fail("expected a LauncherException with " + expected);
            }
            catch (LauncherException ex)
            {
                Assert.AreEqual(expected, ex.Code);
            }
        }
    }
}
