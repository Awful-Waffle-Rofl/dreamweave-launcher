using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Detection;
using DreamWeave.Launcher.Core.Downloads;
using DreamWeave.Launcher.Core.Install;
using DreamWeave.Launcher.Core.Manifests;
using DreamWeave.Launcher.Core.Telemetry;

namespace DreamWeave.Launcher.App
{
    /// <summary>What an <see cref="InstallEvent"/> reports.</summary>
    internal enum InstallEventKind
    {
        StepStarted,
        StepProgress,
        StepStatus,
        StepDone,
        StepSkipped,
        HandoffStarted,
        HandoffNotDetected,
        HandoffEnded,
    }

    /// <summary>One update from an <see cref="InstallSession"/>, delivered through an <see cref="IProgress{T}"/>.</summary>
    internal sealed class InstallEvent
    {
        public InstallEvent(InstallEventKind kind, string componentId, ProgressReport report = null, string text = null)
        {
            Kind = kind;
            ComponentId = componentId;
            Report = report;
            Text = text;
        }

        public InstallEventKind Kind { get; }

        public string ComponentId { get; }

        public ProgressReport Report { get; }

        public string Text { get; }
    }

    /// <summary>How a run ended.</summary>
    internal sealed class InstallRunResult
    {
        public InstallRunResult(LauncherError error, string componentId, string acFolder)
        {
            Error = error;
            ComponentId = componentId;
            AcFolder = acFolder;
        }

        /// <summary><see cref="LauncherError.None"/> when every component finished.</summary>
        public LauncherError Error { get; }

        /// <summary>The component that failed (or was running when cancelled), or null.</summary>
        public string ComponentId { get; }

        /// <summary>The game folder after the run (an installer may have changed it).</summary>
        public string AcFolder { get; }
    }

    /// <summary>
    /// Runs the selected components in manifest order, entirely off the UI thread, and reports through
    /// an <see cref="IProgress{T}"/> the caller created on the UI thread. Telemetry records each component
    /// (outcome and error code only). Cancelling leaves partial downloads in place to resume later.
    /// </summary>
    internal sealed class InstallSession
    {
        /// <summary>How often the installer hand-off polls detection.</summary>
        public static readonly TimeSpan HandoffPollInterval = TimeSpan.FromSeconds(2);

        private readonly AppServices _services;
        private readonly LauncherManifest _manifest;
        private readonly object _pokeGate = new object();
        private TaskCompletionSource<bool> _poke = NewPoke();
        private string _acFolder;

        public InstallSession(AppServices services, LauncherManifest manifest, string acFolder)
        {
            _services = services ?? throw new ArgumentNullException(nameof(services));
            _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
            _acFolder = acFolder;
        }

        /// <summary>"I finished": run a detection pass now instead of waiting for the next poll.</summary>
        public void CheckInstallNow()
        {
            lock (_pokeGate)
            {
                _poke.TrySetResult(true);
            }
        }

        /// <summary>Runs <paramref name="components"/>. Never throws for a classified failure; see the result.</summary>
        public async Task<InstallRunResult> RunAsync(IReadOnlyList<ManifestComponent> components, IProgress<InstallEvent> events, CancellationToken cancellationToken)
        {
            foreach (ManifestComponent c in components)
            {
                ITelemetrySink t = _services.TelemetrySink;
                t.RecordComponent(c.Id, TelemetryOutcome.Started);
                Stopwatch clock = Stopwatch.StartNew();
                events.Report(new InstallEvent(InstallEventKind.StepStarted, c.Id));
                try
                {
                    bool didWork = await RunOneAsync(c, events, cancellationToken).ConfigureAwait(false);
                    t.RecordComponent(c.Id, TelemetryOutcome.Succeeded, LauncherError.None, clock.Elapsed);
                    events.Report(new InstallEvent(didWork ? InstallEventKind.StepDone : InstallEventKind.StepSkipped, c.Id));
                }
                catch (Exception ex)
                {
                    LauncherError code = Classify(ex, cancellationToken);
                    t.RecordComponent(c.Id, code == LauncherError.Cancelled ? TelemetryOutcome.Cancelled : TelemetryOutcome.Failed, code, clock.Elapsed);
                    if (!(ex is LauncherException) && !(ex is OperationCanceledException) && !IsExpectedIo(ex))
                    {
                        // A programming error: surface it as Unknown rather than crash the wizard.
                        Trace.TraceError("Install step failed unexpectedly: " + ex.GetType().FullName);
                    }

                    return new InstallRunResult(code, c.Id, _acFolder);
                }
            }

            return new InstallRunResult(LauncherError.None, null, _acFolder);
        }

        private async Task<bool> RunOneAsync(ManifestComponent c, IProgress<InstallEvent> events, CancellationToken ct)
        {
            var progress = new ForwardingProgress(c.Id, events);
            switch (c.Kind)
            {
                case ComponentKinds.Installer:
                    await RunInstallerAsync(c, progress, events, ct).ConfigureAwait(false);
                    return true;
                case ComponentKinds.ZipOverlay:
                    return await RunOverlayAsync(c, progress, events, ct).ConfigureAwait(false);
                case ComponentKinds.BytePatch:
                    return await RunPatchAsync(c, events, ct).ConfigureAwait(false);
                default:
                    throw new LauncherException(LauncherError.ManifestInvalid, "Unknown component kind.");
            }
        }

        private async Task RunInstallerAsync(ManifestComponent c, IProgress<ProgressReport> progress, IProgress<InstallEvent> events, CancellationToken ct)
        {
            DownloadResult file = await DownloadAsync(c, c.Id + ".exe", progress, ct).ConfigureAwait(false);

            // InstallerRunner.Start re-hashes the staged installer through a deny-write handle and keeps
            // that handle open until the (elevated) process exists, so the bytes that run are the bytes verified.
            events.Report(new InstallEvent(InstallEventKind.StepStatus, c.Id, text: UiText.Verifying));
            InstallerProcess process = await Task.Run(() => new InstallerRunner().Start(c, file.Path, progress, ct), ct).ConfigureAwait(false);
            using (process)
            {
                events.Report(new InstallEvent(InstallEventKind.HandoffStarted, c.Id, text: UiText.InstallerHandoff));
                try
                {
                    string folder = await WaitForInstallAsync(c, events, ct).ConfigureAwait(false);
                    _acFolder = folder;
                    await _services.UpdateStateAsync(s => s.AcFolder = folder).ConfigureAwait(false);
                }
                finally
                {
                    events.Report(new InstallEvent(InstallEventKind.HandoffEnded, c.Id));
                }
            }

            TryDeleteStaged(file.Path);
        }

        /// <summary>
        /// Polls every <see cref="HandoffPollInterval"/> (or at once when "I finished" is clicked) until
        /// the client is detected. The installer process exiting is never the signal: an InstallShield
        /// self-extractor exits early. Detected means: a folder was located and the installer component
        /// is satisfied, or differs only in files a zip overlay in this manifest replaces (C1).
        /// </summary>
        private async Task<string> WaitForInstallAsync(ManifestComponent c, IProgress<InstallEvent> events, CancellationToken ct)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                Task<bool> poke;
                lock (_pokeGate)
                {
                    if (_poke.Task.IsCompleted)
                    {
                        _poke = NewPoke();
                    }

                    poke = _poke.Task;
                }

                string found = await Task.Run(() => Detect(c, ct), ct).ConfigureAwait(false);
                if (found != null)
                {
                    return found;
                }

                Task delay = Task.Delay(HandoffPollInterval, ct);
                Task first = await Task.WhenAny(delay, poke).ConfigureAwait(false);
                if (first == poke)
                {
                    found = await Task.Run(() => Detect(c, ct), ct).ConfigureAwait(false);
                    if (found != null)
                    {
                        return found;
                    }

                    events.Report(new InstallEvent(InstallEventKind.HandoffNotDetected, c.Id, text: UiText.HandoffNotDetected));
                }
                else
                {
                    await delay.ConfigureAwait(false);
                }
            }
        }

        private string Detect(ManifestComponent c, CancellationToken ct)
        {
            try
            {
                string saved = _services.State.Read(s => s.AcFolder);
                AcInstallLocation where = _services.Locator.LocateFrom(saved);
                if (!where.Found)
                {
                    return null;
                }

                ComponentStatus status = _services.Evaluator.Evaluate(c, _manifest, where.Folder, null, ct);
                if (status.State == ComponentState.Satisfied || (status.State == ComponentState.NeedsRepair && ComponentSelection.IsCoveredByOverlay(_manifest, status)))
                {
                    return where.Folder;
                }

                return null;
            }
            catch (Exception ex) when (IsExpectedIo(ex) || (ex is LauncherException le && le.Code != LauncherError.Cancelled))
            {
                return null;
            }
        }

        private async Task<bool> RunOverlayAsync(ManifestComponent c, IProgress<ProgressReport> progress, IProgress<InstallEvent> events, CancellationToken ct)
        {
            string folder = RequireFolder();
            DownloadResult file = await DownloadAsync(c, c.Id + ".zip", progress, ct).ConfigureAwait(false);
            events.Report(new InstallEvent(InstallEventKind.StepStatus, c.Id, text: UiText.Installing));
            var installer = new ZipOverlayInstaller(_services.Paths, _services.Disk);
            ZipOverlayResult result = await Task.Run(() => installer.Install(_manifest, c, file.Path, folder, progress, ct), ct).ConfigureAwait(false);
            if (result.Outcome == InstallOutcome.ElevationRequired)
            {
                events.Report(new InstallEvent(InstallEventKind.StepStatus, c.Id, text: UiText.AskingPermission));
                await RunElevatedAsync(result.TaskPath, result.CleanUp).ConfigureAwait(false);
                await RequireSatisfiedAsync(c, folder, ct).ConfigureAwait(false);
            }

            TryDeleteStaged(file.Path);
            return result.Outcome != InstallOutcome.NothingToDo;
        }

        private async Task<bool> RunPatchAsync(ManifestComponent c, IProgress<InstallEvent> events, CancellationToken ct)
        {
            string folder = RequireFolder();

            // Evaluated now, not when the screen was built: the game data step may just have put the
            // patch's input file in place.
            ComponentStatus status = await Task.Run(() => _services.Evaluator.Evaluate(c, _manifest, folder, null, ct), ct).ConfigureAwait(false);
            if (status.State != ComponentState.Missing)
            {
                return false;
            }

            events.Report(new InstallEvent(InstallEventKind.StepStatus, c.Id, text: UiText.Installing));
            var patcher = new BytePatcher(_services.Paths, _services.Disk);
            BytePatchResult result = await Task.Run(() => patcher.Apply(c, folder), ct).ConfigureAwait(false);
            if (result.Outcome == InstallOutcome.ElevationRequired)
            {
                events.Report(new InstallEvent(InstallEventKind.StepStatus, c.Id, text: UiText.AskingPermission));
                await RunElevatedAsync(result.TaskPath, result.CleanUp).ConfigureAwait(false);
                await RequireSatisfiedAsync(c, folder, ct).ConfigureAwait(false);
            }

            return result.Outcome != InstallOutcome.NothingToDo;
        }

        private async Task RunElevatedAsync(string taskPath, Action cleanUp)
        {
            LauncherError code = await Task.Run(() => _services.Elevated.Run(taskPath, cleanUp)).ConfigureAwait(false);
            if (code != LauncherError.None)
            {
                throw new LauncherException(code, ErrorMessages.For(code));
            }
        }

        // The helper's exit code is trusted only as far as the files bear it out.
        private async Task RequireSatisfiedAsync(ManifestComponent c, string folder, CancellationToken ct)
        {
            ComponentStatus after = await Task.Run(() => _services.Evaluator.Evaluate(c, _manifest, folder, null, ct), ct).ConfigureAwait(false);
            if (after.State != ComponentState.Satisfied)
            {
                throw new LauncherException(LauncherError.Unknown, "The administrator step finished but the files are not in place.");
            }
        }

        private Task<DownloadResult> DownloadAsync(ManifestComponent c, string fileName, IProgress<ProgressReport> progress, CancellationToken ct)
        {
            DownloadRequest request = DownloadRequest.FromComponent(c, _services.Paths, fileName);
            return _services.Downloads.DownloadAsync(request, progress, ct);
        }

        private string RequireFolder()
        {
            if (string.IsNullOrWhiteSpace(_acFolder))
            {
                throw new LauncherException(LauncherError.ClientNotFound, "No Asheron's Call folder is known.");
            }

            return _acFolder;
        }

        private static void TryDeleteStaged(string path)
        {
            try
            {
                if (path != null && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex) when (IsExpectedIo(ex))
            {
                // A leftover staged file is only wasted space; the next download re-verifies it anyway.
            }
        }

        private static LauncherError Classify(Exception ex, CancellationToken ct)
        {
            if (ex is LauncherException le)
            {
                return le.Code;
            }

            if (ex is OperationCanceledException && ct.IsCancellationRequested)
            {
                return LauncherError.Cancelled;
            }

            if (IsExpectedIo(ex))
            {
                return LauncherException.FromIo(ex, "installing").Code;
            }

            return LauncherError.Unknown;
        }

        private static bool IsExpectedIo(Exception ex)
        {
            return ex is IOException || ex is UnauthorizedAccessException;
        }

        private static TaskCompletionSource<bool> NewPoke()
        {
            return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        /// <summary>Wraps component progress reports as <see cref="InstallEvent"/>s.</summary>
        private sealed class ForwardingProgress : IProgress<ProgressReport>
        {
            private readonly string _componentId;
            private readonly IProgress<InstallEvent> _events;

            public ForwardingProgress(string componentId, IProgress<InstallEvent> events)
            {
                _componentId = componentId;
                _events = events;
            }

            public void Report(ProgressReport value)
            {
                _events.Report(new InstallEvent(InstallEventKind.StepProgress, _componentId, value));
            }
        }
    }
}
