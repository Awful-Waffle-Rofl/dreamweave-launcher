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

        /// <summary>The game folder is being written: Cancel is unavailable until <see cref="CriticalEnded"/>.</summary>
        CriticalStarted,
        CriticalEnded,
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

    /// <summary>What an install is doing right now, for the close-window decision.</summary>
    internal enum InstallPhase
    {
        /// <summary>Nothing is running.</summary>
        Idle = 0,

        /// <summary>Downloading, verifying or waiting for the installer: safe to stop (downloads resume).</summary>
        Stoppable = 1,

        /// <summary>Writing into the game folder (overlay commit, patch, elevated copy): must not be interrupted.</summary>
        Critical = 2,
    }

    /// <summary>What closing the window should do.</summary>
    internal enum CloseDecision
    {
        /// <summary>Close normally.</summary>
        Allow = 0,

        /// <summary>Ask "Stop the install?"; on yes cancel, and close once the run has returned.</summary>
        AskToStop = 1,

        /// <summary>Refuse to close until the step finishes.</summary>
        Block = 2,
    }

    /// <summary>The close-window rule, kept apart from the form so it can be tested.</summary>
    internal static class InstallCloseRules
    {
        public static CloseDecision Decide(InstallPhase phase)
        {
            switch (phase)
            {
                case InstallPhase.Stoppable:
                    return CloseDecision.AskToStop;
                case InstallPhase.Critical:
                    return CloseDecision.Block;
                default:
                    return CloseDecision.Allow;
            }
        }
    }

    /// <summary>What a Stop or Cancel request did.</summary>
    internal enum StopOutcome
    {
        /// <summary>The run was asked to stop; it returns Cancelled shortly.</summary>
        Stopping = 0,

        /// <summary>The game folder is being written: nothing was cancelled.</summary>
        Blocked = 1,

        /// <summary>No run is in progress: there is nothing to wait for.</summary>
        NotRunning = 2,
    }

    /// <summary>
    /// Owns the install phase and decides every Stop or Cancel request. A stop and entry into the
    /// Critical phase take the same lock, so a stop can never land after a commit has started: either
    /// the stop wins and the commit is refused, or the commit wins and the stop is blocked.
    /// </summary>
    internal sealed class InstallStopGate
    {
        private readonly object _gate = new object();
        private int _phase;
        private bool _stopPending;

        public InstallPhase Phase => (InstallPhase)Volatile.Read(ref _phase);

        /// <summary>Moves to Idle or Stoppable. Critical is entered only through <see cref="EnterCritical"/>.</summary>
        public void SetPhase(InstallPhase phase)
        {
            if (phase == InstallPhase.Critical)
            {
                throw new ArgumentException("Use EnterCritical.", nameof(phase));
            }

            lock (_gate)
            {
                Volatile.Write(ref _phase, (int)phase);
            }
        }

        /// <summary>Starts a write into the game folder, unless a stop has already been asked for.</summary>
        public void EnterCritical(CancellationToken ct)
        {
            lock (_gate)
            {
                if (_stopPending || ct.IsCancellationRequested)
                {
                    throw new LauncherException(LauncherError.Cancelled, "Stopped before writing to the game folder.");
                }

                Volatile.Write(ref _phase, (int)InstallPhase.Critical);
            }
        }

        /// <summary>Cancels <paramref name="cts"/> unless the game folder is being written.</summary>
        public StopOutcome RequestStop(CancellationTokenSource cts)
        {
            StopOutcome outcome;
            lock (_gate)
            {
                InstallPhase phase = Phase;
                if (phase == InstallPhase.Critical)
                {
                    return StopOutcome.Blocked;
                }

                outcome = phase == InstallPhase.Idle ? StopOutcome.NotRunning : StopOutcome.Stopping;
                _stopPending = true;
            }

            // Cancelled outside the lock: token callbacks run synchronously and must not hold it.
            try
            {
                cts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                lock (_gate)
                {
                    _stopPending = false;
                }
            }

            return outcome;
        }
    }

    /// <summary>Everything an <see cref="InstallSession"/> touches, so tests can substitute each piece.</summary>
    internal sealed class InstallDependencies
    {
        public AppPaths Paths { get; set; }

        public IDownloader Downloads { get; set; }

        public IDiskSpace Disk { get; set; }

        public AcInstallLocator Locator { get; set; }

        public ComponentEvaluator Evaluator { get; set; }

        public ITelemetrySink Telemetry { get; set; } = NullTelemetrySink.Instance;

        /// <summary>Reads the game folder saved in state (may return null).</summary>
        public Func<string> ReadSavedFolder { get; set; } = () => null;

        /// <summary>Saves a detected game folder to state (blocking; called on a pool thread).</summary>
        public Action<string> SaveFolder { get; set; } = f => { };

        /// <summary>Runs the elevated copy for a task file, then the clean-up; returns its error code.</summary>
        public Func<string, Action, LauncherError> RunElevated { get; set; }

        /// <summary>Starts verified installers (the real one by default; tests pass a fake process starter).</summary>
        public InstallerRunner Installers { get; set; } = new InstallerRunner();

        /// <summary>How often the installer hand-off polls detection.</summary>
        public TimeSpan PollInterval { get; set; } = InstallSession.HandoffPollInterval;

        /// <summary>The production wiring over the process's <see cref="AppServices"/>.</summary>
        public static InstallDependencies From(AppServices s)
        {
            return new InstallDependencies
            {
                Paths = s.Paths,
                Downloads = s.Downloads,
                Disk = s.Disk,
                Locator = s.Locator,
                Evaluator = s.Evaluator,
                Telemetry = s.TelemetrySink,
                ReadSavedFolder = () => s.State.Read(st => st.AcFolder),
                SaveFolder = folder => s.State.Update(st => st.AcFolder = folder),
                RunElevated = (task, cleanUp) => s.Elevated.Run(task, cleanUp),
            };
        }
    }

    /// <summary>
    /// Runs the selected components in manifest order, entirely off the UI thread, and reports through
    /// an <see cref="IProgress{T}"/> the caller created on the UI thread. Telemetry records each component
    /// (outcome and error code only). Cancelling leaves partial downloads in place to resume later.
    /// One session serves every Retry of the same Progress screen, so it remembers which installers it
    /// has already launched: a retry detects the install first and only relaunches when it is not there.
    /// </summary>
    internal sealed class InstallSession
    {
        /// <summary>How often the installer hand-off polls detection.</summary>
        public static readonly TimeSpan HandoffPollInterval = TimeSpan.FromSeconds(2);

        private readonly InstallDependencies _deps;
        private readonly LauncherManifest _manifest;
        private readonly object _pokeGate = new object();
        private readonly HashSet<string> _launchedInstallers = new HashSet<string>(StringComparer.Ordinal);
        private TaskCompletionSource<bool> _poke = NewPoke();
        private string _acFolder;
        private readonly InstallStopGate _stop = new InstallStopGate();

        public InstallSession(InstallDependencies deps, LauncherManifest manifest, string acFolder)
        {
            _deps = deps ?? throw new ArgumentNullException(nameof(deps));
            _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
            _acFolder = acFolder;
        }

        /// <summary>What the session is doing now (read from any thread).</summary>
        public InstallPhase Phase => _stop.Phase;

        /// <summary>
        /// Stop or Cancel from the player: cancels <paramref name="cts"/> unless the game folder is being
        /// written right now, in which case nothing is cancelled and <see cref="StopOutcome.Blocked"/> is returned.
        /// </summary>
        public StopOutcome RequestStop(CancellationTokenSource cts) => _stop.RequestStop(cts);

        /// <summary>"I finished": run a detection pass now instead of waiting for the next poll.</summary>
        public void CheckInstallNow()
        {
            lock (_pokeGate)
            {
                _poke.TrySetResult(true);
            }
        }

        /// <summary>
        /// Runs <paramref name="components"/> on the thread pool (the first download never starts on the
        /// caller's thread). Never throws for a classified failure; see the result.
        /// </summary>
        public Task<InstallRunResult> RunAsync(IReadOnlyList<ManifestComponent> components, IProgress<InstallEvent> events, CancellationToken cancellationToken)
        {
            return Task.Run(() => RunCoreAsync(components, events, cancellationToken));
        }

        private async Task<InstallRunResult> RunCoreAsync(IReadOnlyList<ManifestComponent> components, IProgress<InstallEvent> events, CancellationToken cancellationToken)
        {
            try
            {
                foreach (ManifestComponent c in components)
                {
                    ITelemetrySink t = _deps.Telemetry;
                    t.RecordComponent(c.Id, TelemetryOutcome.Started);
                    Stopwatch clock = Stopwatch.StartNew();
                    SetPhase(InstallPhase.Stoppable);
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
            finally
            {
                SetPhase(InstallPhase.Idle);
            }
        }

        private async Task<bool> RunOneAsync(ManifestComponent c, IProgress<InstallEvent> events, CancellationToken ct)
        {
            var progress = new ForwardingProgress(c.Id, events);
            switch (c.Kind)
            {
                case ComponentKinds.Installer:
                    return await RunInstallerAsync(c, progress, events, ct).ConfigureAwait(false);
                case ComponentKinds.ZipOverlay:
                    return await RunOverlayAsync(c, progress, events, ct).ConfigureAwait(false);
                case ComponentKinds.BytePatch:
                    return await RunPatchAsync(c, events, ct).ConfigureAwait(false);
                default:
                    throw new LauncherException(LauncherError.ManifestInvalid, "Unknown component kind.");
            }
        }

        private async Task<bool> RunInstallerAsync(ManifestComponent c, IProgress<ProgressReport> progress, IProgress<InstallEvent> events, CancellationToken ct)
        {
            if (_launchedInstallers.Contains(c.Id))
            {
                // A retry after the hand-off was stopped: the player may have finished the installer meanwhile.
                string already = await Task.Run(() => Detect(c, ct), ct).ConfigureAwait(false);
                if (already != null)
                {
                    await AdoptFolderAsync(already).ConfigureAwait(false);
                    return true;
                }
            }

            DownloadResult file = await DownloadAsync(c, c.Id + ".exe", progress, ct).ConfigureAwait(false);

            // InstallerRunner.Start re-hashes the staged installer through a deny-write handle and keeps
            // that handle open until the (elevated) process exists, so the bytes that run are the bytes verified.
            events.Report(new InstallEvent(InstallEventKind.StepStatus, c.Id, text: UiText.Verifying));
            InstallerProcess process = await Task.Run(() => _deps.Installers.Start(c, file.Path, progress, ct), ct).ConfigureAwait(false);
            _launchedInstallers.Add(c.Id);
            using (process)
            {
                events.Report(new InstallEvent(InstallEventKind.HandoffStarted, c.Id, text: UiText.InstallerHandoff));
                try
                {
                    string folder = await WaitForInstallAsync(c, events, ct).ConfigureAwait(false);
                    await AdoptFolderAsync(folder).ConfigureAwait(false);
                }
                finally
                {
                    events.Report(new InstallEvent(InstallEventKind.HandoffEnded, c.Id));
                }
            }

            TryDeleteStaged(file.Path);
            return true;
        }

        private async Task AdoptFolderAsync(string folder)
        {
            _acFolder = folder;
            await Task.Run(() => _deps.SaveFolder(folder)).ConfigureAwait(false);
        }

        /// <summary>
        /// Polls every <see cref="InstallDependencies.PollInterval"/> (or at once when "I finished" is
        /// clicked) until the client is detected. The installer process exiting is never the signal: an
        /// InstallShield self-extractor exits early.
        /// </summary>
        private async Task<string> WaitForInstallAsync(ManifestComponent c, IProgress<InstallEvent> events, CancellationToken ct)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                Task<bool> poke;
                lock (_pokeGate)
                {
                    poke = _poke.Task;
                }

                string found = await Task.Run(() => Detect(c, ct), ct).ConfigureAwait(false);
                if (found != null)
                {
                    return found;
                }

                Task delay = Task.Delay(_deps.PollInterval, ct);
                Task first = await Task.WhenAny(delay, poke).ConfigureAwait(false);
                if (first == poke)
                {
                    // Consume the click BEFORE detecting, so a click during this pass is not lost.
                    lock (_pokeGate)
                    {
                        if (_poke.Task == poke)
                        {
                            _poke = NewPoke();
                        }
                    }

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

        /// <summary>
        /// Detected means: a folder was located and the installer component is satisfied, or differs
        /// only in files a zip overlay in this manifest replaces (C1). Null for "not yet".
        /// </summary>
        internal string Detect(ManifestComponent c, CancellationToken ct)
        {
            try
            {
                AcInstallLocation where = _deps.Locator.LocateFrom(_deps.ReadSavedFolder());
                if (!where.Found)
                {
                    return null;
                }

                ComponentStatus status = _deps.Evaluator.Evaluate(c, _manifest, where.Folder, null, ct);
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
            var installer = new ZipOverlayInstaller(_deps.Paths, _deps.Disk);
            EnterCritical(c.Id, events, ct);
            try
            {
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
            finally
            {
                SetPhase(InstallPhase.Stoppable);
                events.Report(new InstallEvent(InstallEventKind.CriticalEnded, c.Id));
            }
        }

        private async Task<bool> RunPatchAsync(ManifestComponent c, IProgress<InstallEvent> events, CancellationToken ct)
        {
            string folder = RequireFolder();

            // Evaluated now, not when the screen was built: the game data step may just have put the
            // patch's input file in place.
            ComponentStatus status = await Task.Run(() => _deps.Evaluator.Evaluate(c, _manifest, folder, null, ct), ct).ConfigureAwait(false);
            if (status.State != ComponentState.Missing)
            {
                return false;
            }

            events.Report(new InstallEvent(InstallEventKind.StepStatus, c.Id, text: UiText.Installing));
            var patcher = new BytePatcher(_deps.Paths, _deps.Disk);
            EnterCritical(c.Id, events, ct);
            try
            {
                BytePatchResult result = await Task.Run(() => patcher.Apply(c, folder), ct).ConfigureAwait(false);
                if (result.Outcome == InstallOutcome.ElevationRequired)
                {
                    events.Report(new InstallEvent(InstallEventKind.StepStatus, c.Id, text: UiText.AskingPermission));
                    await RunElevatedAsync(result.TaskPath, result.CleanUp).ConfigureAwait(false);
                    await RequireSatisfiedAsync(c, folder, ct).ConfigureAwait(false);
                }

                return result.Outcome != InstallOutcome.NothingToDo;
            }
            finally
            {
                SetPhase(InstallPhase.Stoppable);
                events.Report(new InstallEvent(InstallEventKind.CriticalEnded, c.Id));
            }
        }

        private async Task RunElevatedAsync(string taskPath, Action cleanUp)
        {
            if (_deps.RunElevated == null)
            {
                throw new LauncherException(LauncherError.ElevationRequired, ErrorMessages.For(LauncherError.ElevationRequired));
            }

            LauncherError code = await Task.Run(() => _deps.RunElevated(taskPath, cleanUp)).ConfigureAwait(false);
            if (code != LauncherError.None)
            {
                throw new LauncherException(code, ErrorMessages.For(code));
            }
        }

        // The helper's exit code is trusted only as far as the files bear it out.
        private async Task RequireSatisfiedAsync(ManifestComponent c, string folder, CancellationToken ct)
        {
            ComponentStatus after = await Task.Run(() => _deps.Evaluator.Evaluate(c, _manifest, folder, null, ct), ct).ConfigureAwait(false);
            if (after.State != ComponentState.Satisfied)
            {
                throw new LauncherException(LauncherError.Unknown, "The administrator step finished but the files are not in place.");
            }
        }

        private Task<DownloadResult> DownloadAsync(ManifestComponent c, string fileName, IProgress<ProgressReport> progress, CancellationToken ct)
        {
            DownloadRequest request = DownloadRequest.FromComponent(c, _deps.Paths, fileName);
            return _deps.Downloads.DownloadAsync(request, progress, ct);
        }

        private string RequireFolder()
        {
            if (string.IsNullOrWhiteSpace(_acFolder))
            {
                throw new LauncherException(LauncherError.ClientNotFound, "No Asheron's Call folder is known.");
            }

            return _acFolder;
        }

        private void SetPhase(InstallPhase phase)
        {
            _stop.SetPhase(phase);
        }

        private void EnterCritical(string componentId, IProgress<InstallEvent> events, CancellationToken ct)
        {
            _stop.EnterCritical(ct);
            events.Report(new InstallEvent(InstallEventKind.CriticalStarted, componentId));
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
