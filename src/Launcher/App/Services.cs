using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Install;
using DreamWeave.Launcher.Core.Manifests;
using DreamWeave.Launcher.Core.Cleanup;
using DreamWeave.Launcher.Core.Telemetry;

namespace DreamWeave.Launcher.App
{
    /// <summary>The telemetry calls the presenters make (outcome and error code only).</summary>
    internal interface ITelemetrySink
    {
        /// <summary>Records a fixed step (a <see cref="TelemetrySteps"/> constant).</summary>
        void Record(string step, TelemetryOutcome outcome, LauncherError error = LauncherError.None, TimeSpan? duration = null);

        /// <summary>Records a manifest component step.</summary>
        void RecordComponent(string componentId, TelemetryOutcome outcome, LauncherError error = LauncherError.None, TimeSpan? duration = null);
    }

    /// <summary><see cref="ITelemetrySink"/> over the process's one <see cref="TelemetryClient"/>.</summary>
    internal sealed class TelemetrySink : ITelemetrySink
    {
        private readonly TelemetryClient _client;

        public TelemetrySink(TelemetryClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
        }

        public void Record(string step, TelemetryOutcome outcome, LauncherError error = LauncherError.None, TimeSpan? duration = null)
        {
            _client.Record(step, outcome, error, duration);
        }

        public void RecordComponent(string componentId, TelemetryOutcome outcome, LauncherError error = LauncherError.None, TimeSpan? duration = null)
        {
            _client.RecordComponent(componentId, outcome, error, duration);
        }
    }

    /// <summary>A sink that drops everything (tests, and modes that must not write telemetry).</summary>
    internal sealed class NullTelemetrySink : ITelemetrySink
    {
        public static readonly NullTelemetrySink Instance = new NullTelemetrySink();

        public void Record(string step, TelemetryOutcome outcome, LauncherError error = LauncherError.None, TimeSpan? duration = null)
        {
        }

        public void RecordComponent(string componentId, TelemetryOutcome outcome, LauncherError error = LauncherError.None, TimeSpan? duration = null)
        {
        }
    }

    /// <summary>
    /// The process's one manifest loader. Calls to <see cref="LoadAsync"/> are serialized (the
    /// provider's contract), each runs on a background thread, and the last result that carried a
    /// manifest is kept: once the rollback floor has risen in-session an offline reload can return no
    /// manifest, and the app must keep using the good one it has.
    /// </summary>
    internal sealed class ManifestService
    {
        private readonly Func<CancellationToken, Task<ManifestLoadResult>> _load;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private ManifestLoadResult _lastGood;
        private ManifestLoadResult _latest;

        /// <summary>Wraps <paramref name="provider"/>.</summary>
        public ManifestService(ManifestProvider provider)
            : this(provider == null ? (Func<CancellationToken, Task<ManifestLoadResult>>)null : provider.LoadAsync)
        {
        }

        /// <summary>Wraps a load function (tests).</summary>
        internal ManifestService(Func<CancellationToken, Task<ManifestLoadResult>> load)
        {
            _load = load ?? throw new ArgumentNullException(nameof(load));
        }

        /// <summary>The last result that carried a manifest, or null.</summary>
        public ManifestLoadResult LastGood => Volatile.Read(ref _lastGood);

        /// <summary>The most recent result, whatever it held, or null before the first load.</summary>
        public ManifestLoadResult Latest => Volatile.Read(ref _latest);

        /// <summary>The manifest to use: <see cref="LastGood"/>'s, or null.</summary>
        public LauncherManifest Manifest => LastGood?.Manifest;

        /// <summary>
        /// Loads once (waiting for any load in progress first). Returns the result to act on: the fresh
        /// result when it carries a manifest, otherwise the last good one when there is one (with the
        /// fresh one in <see cref="Latest"/>), otherwise the fresh one.
        /// </summary>
        public async Task<ManifestLoadResult> LoadAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ManifestLoadResult fresh = await Task.Run(() => _load(cancellationToken), cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref _latest, fresh);
                if (fresh != null && fresh.Manifest != null)
                {
                    Volatile.Write(ref _lastGood, fresh);
                    return fresh;
                }

                return LastGood ?? fresh;
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    /// <summary>
    /// Runs the launcher's own <c>--elevated-copy &lt;task&gt;</c> mode with verb <c>runas</c>, waits for it,
    /// maps its exit code back to a <see cref="LauncherError"/>, and always calls the result's clean-up.
    /// </summary>
    internal sealed class ElevatedCopyRunner
    {
        private const int ErrorCancelled = 1223;
        private readonly IProcessLauncher _processes;
        private readonly string _exePath;

        /// <summary>Creates a runner that starts <paramref name="exePath"/> (the running launcher exe).</summary>
        public ElevatedCopyRunner(IProcessLauncher processes, string exePath)
        {
            _processes = processes ?? throw new ArgumentNullException(nameof(processes));
            if (string.IsNullOrEmpty(exePath))
            {
                throw new ArgumentException("The launcher exe path is required.", nameof(exePath));
            }

            _exePath = exePath;
        }

        /// <summary>The start info used for <paramref name="taskPath"/>.</summary>
        internal ProcessStartInfo BuildStartInfo(string taskPath)
        {
            return new ProcessStartInfo(_exePath, StartupArgs.ElevatedCopySwitch + " \"" + taskPath + "\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(_exePath),
            };
        }

        /// <summary>
        /// Runs the helper for <paramref name="taskPath"/> (blocking: background threads only) and then
        /// <paramref name="cleanUp"/>, whatever happened. Returns <see cref="LauncherError.None"/> on success.
        /// </summary>
        public LauncherError Run(string taskPath, Action cleanUp)
        {
            if (string.IsNullOrEmpty(taskPath))
            {
                throw new ArgumentException("A task file is required.", nameof(taskPath));
            }

            try
            {
                if (taskPath.IndexOf('"') >= 0)
                {
                    return LauncherError.ElevatedTaskInvalid;
                }

                int exit;
                try
                {
                    exit = _processes.Run(BuildStartInfo(taskPath));
                }
                catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
                {
                    return LauncherError.ElevationDeclined;
                }
                catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException || ex is IOException)
                {
                    return LauncherError.Unknown;
                }

                return ExitCodeToError(exit);
            }
            finally
            {
                try
                {
                    cleanUp?.Invoke();
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    // Clean-up is best effort; leftovers sit in the staging folder.
                }
            }
        }

        /// <summary>Maps a helper exit code to its <see cref="LauncherError"/>; an undefined value is Unknown.</summary>
        internal static LauncherError ExitCodeToError(int exitCode)
        {
            return Enum.IsDefined(typeof(LauncherError), exitCode) ? (LauncherError)exitCode : LauncherError.Unknown;
        }
    }

    /// <summary>Path helpers for the running exe.</summary>
    internal static class ExePaths
    {
        /// <summary>Full path of the running exe.</summary>
        public static string Running()
        {
            using (Process p = Process.GetCurrentProcess())
            {
                return Path.GetFullPath(p.MainModule.FileName);
            }
        }
    }
}
