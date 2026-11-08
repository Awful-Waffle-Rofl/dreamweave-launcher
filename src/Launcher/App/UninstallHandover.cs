using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Cleanup;

namespace DreamWeave.Launcher.App
{
    /// <summary>
    /// How Settings > Uninstall hands over: to the setup program's uninstaller when its registered
    /// command passes <see cref="SetupUninstallLocator.TryResolve"/>, otherwise to this exe started with
    /// <c>--cleanup --handover</c>.
    /// </summary>
    internal static class UninstallHandover
    {
        /// <summary>The start to try first. <paramref name="usesSetup"/> says which kind it is.</summary>
        public static ProcessStartInfo ChooseUninstallStart(string uninstallString, ISelfFileSystem fileSystem, string runningExe, out bool usesSetup)
        {
            string program, arguments;
            if (SetupUninstallLocator.TryResolve(uninstallString, fileSystem, runningExe, out program, out arguments))
            {
                usesSetup = true;
                return new ProcessStartInfo(program, arguments) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(program) };
            }

            usesSetup = false;
            return CleanupStart(runningExe);
        }

        /// <summary>This exe started with <c>--cleanup --handover</c>.</summary>
        public static ProcessStartInfo CleanupStart(string runningExe)
        {
            return new ProcessStartInfo(runningExe, SetupConstants.CleanupSwitch + " " + StartupArgs.HandoverSwitch)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(runningExe),
            };
        }

        /// <summary>
        /// Starts the chosen handover. If the setup uninstaller cannot be started, the <c>--cleanup</c>
        /// handover is tried once instead. False when nothing could be started.
        /// </summary>
        public static bool TryStart(IProcessLauncher processes, ISelfFileSystem fileSystem, string uninstallString, string runningExe)
        {
            bool usesSetup;
            var attempts = new List<ProcessStartInfo> { ChooseUninstallStart(uninstallString, fileSystem, runningExe, out usesSetup) };
            if (usesSetup)
            {
                attempts.Add(CleanupStart(runningExe));
            }

            foreach (ProcessStartInfo attempt in attempts)
            {
                try
                {
                    processes.Start(attempt);
                    return true;
                }
                catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException || ex is IOException)
                {
                    // Try the next one, if any.
                }
            }

            return false;
        }
    }
}