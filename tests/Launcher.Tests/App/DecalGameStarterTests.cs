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
        public void NotRegisteredToken_IsDecalNotFound()
        {
            AssertError(() => Starter().Interpret(new HelperOutput(DecalLaunchResult.ExitInjectInvalid, string.Empty, DecalLaunchResult.TokenInjectNotRegistered)), LauncherError.DecalNotFound);
        }

        [TestMethod]
        public void AcclientReparseToken_IsReparsePointInPath()
        {
            AssertError(() => Starter().Interpret(new HelperOutput(DecalLaunchResult.ExitReparsePoint, string.Empty, DecalLaunchResult.TokenAcclientReparsePoint)), LauncherError.ReparsePointInPath);
        }

        [TestMethod]
        public void InjectReparseToken_IsReparsePointInPath()
        {
            AssertError(() => Starter().Interpret(new HelperOutput(DecalLaunchResult.ExitReparsePoint, string.Empty, DecalLaunchResult.TokenInjectReparsePoint)), LauncherError.ReparsePointInPath);
        }

        [TestMethod]
        public void ElevationToken_IsClientRequiresElevation()
        {
            AssertError(() => Starter().Interpret(new HelperOutput(DecalLaunchResult.ExitElevationRequired, string.Empty, DecalLaunchResult.TokenElevationRequired)), LauncherError.ClientRequiresElevation);
        }

        [TestMethod]
        public void AccessDeniedToken_IsAccessDenied()
        {
            AssertError(() => Starter().Interpret(new HelperOutput(DecalLaunchResult.ExitAccessDenied, string.Empty, DecalLaunchResult.TokenAccessDenied)), LauncherError.AccessDenied);
        }

        [TestMethod]
        public void CreateProcessFailedToken_IsLaunchFailed()
        {
            AssertError(() => Starter().Interpret(new HelperOutput(DecalLaunchResult.ExitCreateProcessFailed, string.Empty, DecalLaunchResult.TokenCreateProcessFailed)), LauncherError.LaunchFailed);
        }

        [TestMethod]
        public void BadUsageToken_IsDecalHelperFailed()
        {
            AssertError(() => Starter().Interpret(new HelperOutput(DecalLaunchResult.ExitBadUsage, string.Empty, DecalLaunchResult.TokenBadUsage)), LauncherError.DecalHelperFailed);
        }

        [TestMethod]
        public void UnknownToken_IsDecalHelperFailed()
        {
            AssertError(() => Starter().Interpret(new HelperOutput(99, string.Empty, "something-new")), LauncherError.DecalHelperFailed);
        }

        [TestMethod]
        public void EmptyStderr_OnFailure_IsDecalHelperFailed()
        {
            AssertError(() => Starter().Interpret(new HelperOutput(1, string.Empty, string.Empty)), LauncherError.DecalHelperFailed);
        }

        [TestMethod]
        public void NoFailureMapsToTheStartedWithoutDecalCode()
        {
            // 75 is only the started-without-Decal notice; no helper failure throws it.
            string[] tokens =
            {
                DecalLaunchResult.TokenBadUsage, DecalLaunchResult.TokenGameFolderMissing, DecalLaunchResult.TokenAcclientMissing,
                DecalLaunchResult.TokenInjectPathInvalid, DecalLaunchResult.TokenInjectMissing, DecalLaunchResult.TokenInjectNotRegistered,
                DecalLaunchResult.TokenAcclientReparsePoint, DecalLaunchResult.TokenInjectReparsePoint, DecalLaunchResult.TokenCreateProcessFailed,
                DecalLaunchResult.TokenElevationRequired, DecalLaunchResult.TokenAccessDenied, DecalLaunchResult.TokenInjectFailed, "unknown", string.Empty,
            };
            foreach (string token in tokens)
            {
                try
                {
                    Starter().Interpret(new HelperOutput(1, string.Empty, token));
                    Assert.Fail("expected a LauncherException for '" + token + "'");
                }
                catch (LauncherException ex)
                {
                    Assert.AreNotEqual(LauncherError.DecalInjectFailed, ex.Code, "token '" + token + "'");
                }
            }
        }

        [TestMethod]
        public void Start_WhenHelperCannotRun_IsDecalHelperFailed()
        {
            var starter = new DecalGameStarter(@"C:\app\DreamWeaveDecalLaunch.exe", @"C:\Decal\Inject.dll", null,
                (folder, inject, args) => throw new System.ComponentModel.Win32Exception(2));

            AssertError(() => starter.Start(@"C:\Games\AC", "-h host"), LauncherError.DecalHelperFailed);
        }

        [TestMethod]
        public void Start_WhenHelperNeverFinishes_ReturnsDecalHelperTimeoutWithinTheBackstop()
        {
            var release = new System.Threading.ManualResetEventSlim(false);
            try
            {
                var starter = new DecalGameStarter(@"C:\app\DreamWeaveDecalLaunch.exe", @"C:\Decal\Inject.dll", null,
                    (folder, inject, args) =>
                    {
                        release.Wait();
                        return new HelperOutput(0, string.Empty, string.Empty);
                    },
                    200);

                var clock = System.Diagnostics.Stopwatch.StartNew();
                AssertError(() => starter.Start(@"C:\Games\AC", "-h host"), LauncherError.DecalHelperTimeout);

                Assert.IsTrue(clock.ElapsedMilliseconds < 10000, "Start returned in " + clock.ElapsedMilliseconds + " ms");
            }
            finally
            {
                release.Set();
            }
        }

        [TestMethod]
        public void Start_WhenRunnerReportsATimeout_IsDecalHelperTimeout()
        {
            var starter = new DecalGameStarter(@"C:\app\DreamWeaveDecalLaunch.exe", @"C:\Decal\Inject.dll", null,
                (folder, inject, args) => throw new System.TimeoutException());

            AssertError(() => starter.Start(@"C:\Games\AC", "-h host"), LauncherError.DecalHelperTimeout);
        }

        private sealed class BrokenPipeWriter : System.IO.StringWriter
        {
            public int Disposed;

            public override void Write(string value)
            {
                throw new System.IO.IOException("The pipe is being closed.");
            }

            protected override void Dispose(bool disposing)
            {
                Disposed++;
                base.Dispose(disposing);
            }
        }

        [TestMethod]
        public void WriteStdin_IgnoresABrokenPipe_SoTheTokenIsStillRead()
        {
            var writer = new BrokenPipeWriter();

            DecalGameStarter.WriteStdin(writer, "-h host -a user:secret");

            Assert.AreEqual(1, writer.Disposed, "the stream is still closed");
            // The helper's real answer then maps normally.
            AssertError(() => Starter().Interpret(new HelperOutput(DecalLaunchResult.ExitElevationRequired, string.Empty, DecalLaunchResult.TokenElevationRequired)), LauncherError.ClientRequiresElevation);
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
