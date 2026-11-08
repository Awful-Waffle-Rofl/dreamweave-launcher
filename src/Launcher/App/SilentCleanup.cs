using System;
using System.IO;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Cleanup;
using DreamWeave.Launcher.Core.Downloads;

namespace DreamWeave.Launcher.App
{
    /// <summary>
    /// <c>--cleanup --silent</c>, run by the setup program's uninstaller. It must never fail the uninstall,
    /// show a window or touch the AC client, so every step (path lookup, environment construction, the
    /// single-instance lock, the cleanup itself) sits inside one catch-all and the exit code is always 0.
    /// </summary>
    internal static class SilentCleanup
    {
        /// <summary>How long to wait for a launcher that is still shutting down to release the single-instance lock.</summary>
        public static readonly TimeSpan LockWait = StartupArgs.SilentWait;

        /// <summary>Always returns 0.</summary>
        public static int Run(Func<AppPaths> pathsFactory, Func<AppPaths, CleanupEnvironment> environmentFactory, Func<TimeSpan, SingleInstance> acquire)
        {
            AppPaths paths = null;
            try
            {
                paths = pathsFactory();
                using (SingleInstance instance = acquire(LockWait))
                {
                    if (!instance.Owned)
                    {
                        // Never delete the data of a launcher that is still open.
                        Log(paths, "silent cleanup skipped: the launcher was still running after " + (int)LockWait.TotalSeconds + " s");
                        return 0;
                    }

                    CleanupResult result = new LauncherCleanup(environmentFactory(paths)).RunSilent();
                    if (result == null)
                    {
                        Log(paths, "silent cleanup refused: unexpected folder");
                    }
                    else if (!result.Complete)
                    {
                        Log(paths, "silent cleanup incomplete: " + result.LeftoverFolders.Count + " folder(s) left, passwords left over: " + result.CredentialsLeftOver);
                    }
                }
            }
            catch (Exception ex)
            {
                // The type only; messages can carry paths and account names.
                Log(paths, "silent cleanup failed: " + ex.GetType().Name);
            }

            return 0;
        }

        /// <summary>
        /// Notes <paramref name="message"/> in launcher.log, but only if the log still exists (a complete
        /// cleanup deletes it with the data folder). Never throws.
        /// </summary>
        private static void Log(AppPaths paths, string message)
        {
            try
            {
                if (paths != null && File.Exists(paths.LogFile))
                {
                    new FileDownloadLog(paths.LogFile).Note("cleanup", message);
                }
            }
            catch (Exception)
            {
                // Logging never changes the exit code.
            }
        }
    }
}