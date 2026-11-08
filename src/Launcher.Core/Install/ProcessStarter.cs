using System;
using System.Diagnostics;

namespace DreamWeave.Launcher.Core.Install
{
    /// <summary>A process the launcher started (or a stand-in for one in tests).</summary>
    public interface IStartedProcess : IDisposable
    {
        /// <summary>True once the process has exited. False when it cannot be observed.</summary>
        bool HasExited { get; }

        /// <summary>Exit code once <see cref="HasExited"/> is true, otherwise null.</summary>
        int? ExitCode { get; }

        /// <summary>The process id, or null when Windows did not report one (ShellExecute can return no handle).</summary>
        int? Id { get; }
    }

    /// <summary>
    /// Starts processes. The production implementation is <see cref="ShellProcessStarter"/>; tests
    /// substitute a fake so no real installer ever runs.
    /// </summary>
    public interface IProcessStarter
    {
        /// <summary>
        /// Starts <paramref name="startInfo"/>. Returns null when Windows reports success without a
        /// process handle (ShellExecute can do that). Throws <see cref="System.ComponentModel.Win32Exception"/>
        /// on failure; native error 1223 (ERROR_CANCELLED) means the player declined the UAC prompt.
        /// </summary>
        IStartedProcess Start(ProcessStartInfo startInfo);
    }

    /// <summary><see cref="IProcessStarter"/> over <see cref="Process.Start(ProcessStartInfo)"/>.</summary>
    public sealed class ShellProcessStarter : IProcessStarter
    {
        /// <inheritdoc />
        public IStartedProcess Start(ProcessStartInfo startInfo)
        {
            if (startInfo == null)
            {
                throw new ArgumentNullException(nameof(startInfo));
            }

            Process p = Process.Start(startInfo);
            return p == null ? null : new StartedProcess(p);
        }

        private sealed class StartedProcess : IStartedProcess
        {
            private readonly Process _process;

            public StartedProcess(Process process)
            {
                _process = process;
            }

            public bool HasExited
            {
                get
                {
                    try
                    {
                        return _process.HasExited;
                    }
                    catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception || ex is NotSupportedException)
                    {
                        return false;
                    }
                }
            }

            public int? ExitCode
            {
                get
                {
                    try
                    {
                        return _process.HasExited ? _process.ExitCode : (int?)null;
                    }
                    catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception || ex is NotSupportedException)
                    {
                        return null;
                    }
                }
            }

            public int? Id
            {
                get
                {
                    try
                    {
                        // From the ShellExecute handle (GetProcessId); works for an elevated child too.
                        return _process.Id;
                    }
                    catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception || ex is NotSupportedException)
                    {
                        return null;
                    }
                }
            }

            public void Dispose()
            {
                _process.Dispose();
            }
        }
    }
}
