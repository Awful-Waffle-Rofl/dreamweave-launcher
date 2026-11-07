using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using DreamWeave.Launcher.App;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Install;
using DreamWeave.Launcher.Core.SelfInstall;
using DreamWeave.Launcher.Core.Update;
using DreamWeave.Launcher.UI;

namespace DreamWeave.Launcher
{
    internal static class Program
    {
        /// <summary>How long <c>--updated</c> and <c>--uninstall</c> wait for the previous launcher to release the mutex.</summary>
        internal static readonly TimeSpan HandoverWait = TimeSpan.FromSeconds(30);

        [STAThread]
        private static int Main(string[] args)
        {
            StartupArgs parsed = StartupArgs.Parse(args);
            switch (parsed.Mode)
            {
                case StartupMode.ElevatedCopy:
                    // No UI, no mutex (the parent holds it and is waiting on this process). The exit code
                    // is the LauncherError value; ElevatedTaskRunner never throws.
                    return ElevatedTaskRunner.Run(parsed.TaskPath);
                case StartupMode.Invalid:
                    // Unknown switch, or --elevated-copy without a task: fail, never fall through to a mode
                    // whose exit code 0 could be read as success.
                    return (int)LauncherError.Unknown;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Never show WinForms' default exception dialog: it prints exception text and a stack trace.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) =>
                MessageBox.Show(ErrorMessages.For(LauncherError.Unknown), LauncherInfo.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);

            using (SingleInstance instance = SingleInstance.Acquire(parsed.HandsOverToRunningInstance ? TimeSpan.Zero : HandoverWait))
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

                return parsed.Mode == StartupMode.Uninstall ? RunUninstall() : RunInteractive(parsed, instance);
            }
        }

        /// <summary>
        /// <c>--uninstall</c>. Deliberately builds NO <see cref="AppServices"/>: opening the state store or
        /// the telemetry client would write to the data folder that is about to be deleted.
        /// </summary>
        private static int RunUninstall()
        {
            AppPaths paths = AppPaths.CreateDefault();
            string running = ExePaths.Running();
            int exit = 0;
            var form = new UninstallForm(removeClient =>
            {
                var options = new UninstallOptions
                {
                    CurrentExePath = ExePaths.UninstallCurrentExePath(running, paths),
                    RemoveAcClient = removeClient,
                };

                // Off the UI thread: the AC client's uninstaller is waited on.
                return Task.Run(() =>
                {
                    UninstallResult result = new SelfUninstaller(SelfInstallEnvironment.CreateDefault(paths)).Uninstall(options);
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
                if (parsed.Mode == StartupMode.Normal && !ExePaths.IsInstalledCopy(running, paths) && OfferSelfInstall(services, running, instance))
                {
                    return 0;
                }

                new AppController(services, instance).Run();
                return 0;
            }
            finally
            {
                services.Shutdown();
            }
        }

        /// <summary>
        /// Offers to install the running (downloaded) exe for the current user. On success the installed
        /// copy is started and true is returned, so this process exits; false means carry on from here.
        /// </summary>
        private static bool OfferSelfInstall(AppServices services, string running, SingleInstance instance)
        {
            if (MessageBox.Show(UiText.SelfInstallPrompt, LauncherInfo.ProductName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return false;
            }

            SelfInstallResult result;
            try
            {
                result = Task.Run(() => new SelfInstaller(services.InstallEnvironment, LauncherInfo.Version).Install(running)).GetAwaiter().GetResult();
            }
            catch (LauncherException)
            {
                MessageBox.Show(UiText.SelfInstallFailed, LauncherInfo.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            try
            {
                // The installed copy takes the mutex itself; release it first so it does not hand straight back to us.
                services.Shutdown();
                instance.Release();
                services.InstallEnvironment.Processes.Start(new ProcessStartInfo(result.ExePath)
                {
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(result.ExePath),
                });
                return true;
            }
            catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException || ex is IOException)
            {
                MessageBox.Show(UiText.SelfInstallStartFailed, LauncherInfo.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return true;
            }
        }
    }
}
