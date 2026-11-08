using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using DreamWeave.Launcher.App;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Install;
using DreamWeave.Launcher.Core.Cleanup;
using DreamWeave.Launcher.Core.Update;
using DreamWeave.Launcher.UI;

namespace DreamWeave.Launcher
{
    internal static class Program
    {

        [STAThread]
        private static int Main(string[] args)
        {
            StartupArgs parsed = StartupArgs.Parse(args);
            switch (parsed.Mode)
            {
                case StartupMode.PlaceVerified:
                    // No UI, no mutex (the parent holds it and is waiting on this process). The exit code
                    // is the LauncherError value; VerifiedPlacementRunner never throws.
                    return VerifiedPlacementRunner.Run(parsed.AppRoot, parsed.AcFolder, parsed.ComponentId);
                case StartupMode.Invalid:
                    // Unknown switch, or --place-verified without its arguments: fail, never fall through to a mode
                    // whose exit code 0 could be read as success.
                    return (int)LauncherError.Unknown;
            }

            // Held for the whole run (separate from the single-instance lock) so the setup program can detect us.
            SetupGuard.Hold();
            try
            {
                if (parsed.IsSilent)
                {
                    // No window, no AC client removal, always exit 0: nothing in here may throw or show UI.
                    return SilentCleanup.Run(AppPaths.CreateDefault, CleanupEnvironment.CreateDefault, SingleInstance.Acquire);
                }

                return RunWithUi(parsed);
            }
            finally
            {
                SetupGuard.Release();
            }
        }

        private static int RunWithUi(StartupArgs parsed)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Never show WinForms' default exception dialog: it prints exception text and a stack trace.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) =>
                MessageBox.Show(ErrorMessages.For(LauncherError.Unknown), LauncherInfo.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);

            using (SingleInstance instance = SingleInstance.Acquire(parsed.MutexWait))
            {
                if (!instance.Owned)
                {
                    if (parsed.HandsOverToRunningInstance)
                    {
                        SingleInstance.SignalRunningInstance();
                        return 0;
                    }

                    MessageBox.Show(UiText.AlreadyRunningBusy, LauncherInfo.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return (int)LauncherError.StateLocked;
                }

                return parsed.Mode == StartupMode.Cleanup ? RunCleanup(parsed) : RunInteractive(parsed, instance);
            }
        }

        /// <summary>
        /// <c>--cleanup</c>. Deliberately builds NO <see cref="AppServices"/>: opening the state store or
        /// the telemetry client would write to the data folder that is about to be deleted. Never touches
        /// shortcuts, the registry or the install folder (the setup program owns those).
        /// </summary>
        private static int RunCleanup(StartupArgs parsed)
        {
            AppPaths paths = AppPaths.CreateDefault();
            // 0 when the player cancels at the confirmation; once cleanup starts, Unknown until a result arrives.
            int exit = 0;
            var form = new UninstallForm(removeClient =>
            {
                exit = (int)LauncherError.Unknown;
                var options = new CleanupOptions { RemoveAcClient = removeClient };

                // Off the UI thread: the AC client's uninstaller is waited on.
                return Task.Run(() =>
                {
                    CleanupResult result = new LauncherCleanup(CleanupEnvironment.CreateDefault(paths)).Run(options);
                    exit = (int)result.Code;
                    return result;
                });
            });
            Application.Run(form);
            return exit;
        }

        private static int RunInteractive(StartupArgs parsed, SingleInstance instance)
        {
            AppPaths paths = AppPaths.CreateDefault();
            string running = ExePaths.Running();
            var fs = new SelfFileSystem();

            if (parsed.Mode == StartupMode.Updated)
            {
                // The previous exe has released the mutex (we hold it now); remove its .old copy.
                SelfUpdater.CompleteUpdate(running, fs, TimeSpan.FromSeconds(10));
            }
            else
            {
                // Best effort: sweep a stale .old left by an earlier update.
                SelfUpdater.CompleteUpdate(running, fs, TimeSpan.Zero);
            }

            AppServices services;
            try
            {
                // No window exists yet; the store open can wait on another instance's lock file.
                services = Task.Run(() => AppServices.Create(paths, running)).GetAwaiter().GetResult();
            }
            catch (LauncherException ex)
            {
                MessageBox.Show(ex.Code == LauncherError.StateLocked ? UiText.StateLockedAtStart : ErrorMessages.For(ex.Code), LauncherInfo.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return (int)ex.Code;
            }

            try
            {
                new AppController(services, instance).Run();
                return 0;
            }
            finally
            {
                services.Shutdown();
            }
        }
    }
}
