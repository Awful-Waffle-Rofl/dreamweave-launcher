using System.Diagnostics;

namespace DreamWeave.Launcher.Core.Cleanup
{
    /// <summary>Starts processes for self-update relaunch and the AC client's uninstaller. Production implementation: <see cref="ProcessLauncher"/>.</summary>
    public interface IProcessLauncher
    {
        /// <summary>Starts the process and returns without waiting. Throws what <see cref="Process.Start(ProcessStartInfo)"/> throws.</summary>
        void Start(ProcessStartInfo startInfo);

        /// <summary>Starts the process, waits for it to exit and returns its exit code. Throws what <see cref="Process.Start(ProcessStartInfo)"/> throws.</summary>
        int Run(ProcessStartInfo startInfo);
    }

    /// <summary><see cref="IProcessLauncher"/> over <see cref="Process"/>.</summary>
    public sealed class ProcessLauncher : IProcessLauncher
    {
        /// <inheritdoc />
        public void Start(ProcessStartInfo startInfo)
        {
            using (Process.Start(startInfo))
            {
            }
        }

        /// <inheritdoc />
        public int Run(ProcessStartInfo startInfo)
        {
            using (Process p = Process.Start(startInfo))
            {
                if (p == null)
                {
                    // ShellExecute handed the request to an already running process; nothing to wait on.
                    return 0;
                }

                p.WaitForExit();
                return p.ExitCode;
            }
        }
    }
}
