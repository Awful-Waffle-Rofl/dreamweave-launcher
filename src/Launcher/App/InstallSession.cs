using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
        /// <summary>The download phase started fetching this component (it is applied later, after every download).</summary>
        StageStarted,

        /// <summary>This component's download is complete and verified; it waits for the install phase.</summary>
        StageDone,

        /// <summary>The install phase started this component.</summary>
        StepStarted,
        StepProgress,
        StepStatus,

        /// <summary>A download fell back to another source; <see cref="InstallEvent.Text"/> is the player-facing line.</summary>
        SourceChanged,
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
        public InstallRunResult(LauncherError error, string componentId, string acFolder, IReadOnlyList<string> fileDetails = null, string sourceTransport = null, string retryFromId = null)
        {
            RetryFromId = retryFromId ?? componentId;
            SourceTransport = sourceTransport;
            Error = error;
            ComponentId = componentId;
            AcFolder = acFolder;
            FileDetails = fileDetails ?? new string[0];
        }

        /// <summary>
        /// The component a Retry starts from. A failure in the download phase happens before anything
        /// was installed, so the retry starts from the first component again (finished downloads are
        /// found in place); a failure in the install phase retries from the failed component.
        /// </summary>
        public string RetryFromId { get; }

        /// <summary>Transport ("mega" or "http") of the last download source that failed, or null.</summary>
        public string SourceTransport { get; }

        /// <summary>Game-relative file names with their state ("client_portal.dat: Mismatch") for a failure about installed files; empty otherwise.</summary>
        public IReadOnlyList<string> FileDetails { get; }

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

        /// <summary>Gap between the two polls that must see identical files before the installer counts as finished copying.</summary>
        public TimeSpan SettleInterval { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>
        /// How long the installer may stay busy (still running, or its files still changing) before the
        /// player is told to finish it. The wait goes on after that: the game data step never starts
        /// while the installer is running.
        /// </summary>
        public TimeSpan SettleTimeout { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>Lists running processes, to tell whether the started installer (and its helpers) are still running.</summary>
        public IProcessTable Processes { get; set; } = ToolhelpProcessTable.Instance;

        /// <summary>Writes one line to launcher.log about an install decision (never throws).</summary>
        public Action<string> InstallLog { get; set; } = line => { };

        /// <summary>Wait before re-checking a file that could not be read (locked by a scanner, say) during the final check.</summary>
        public TimeSpan VerifyRetryDelay { get; set; } = TimeSpan.FromSeconds(3);

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
                InstallLog = line => new FileDownloadLog(s.Paths.LogFile).Note("install", line),
            };
        }
    }

    /// <summary>
    /// Runs the selected components entirely off the UI thread, in phases, and reports through an
    /// <see cref="IProgress{T}"/> the caller created on the UI thread:
    /// <list type="number">
    /// <item>Download phase: every component that needs a download is downloaded and verified into
    /// staging, in manifest order. Nothing is installed yet, so every download (and its source
    /// trouble) is over before the game installer's window appears.</item>
    /// <item>Install phase, in manifest order: the game installer hand-off, which ends only once the
    /// installer is no longer running (process tree and InstallShield helpers) AND its files have
    /// settled; then the game data overlay; then the patch.</item>
    /// <item>The final check (<c>VerifyPlanAsync</c>).</item>
    /// </list>
    /// Telemetry records each component (outcome and error code only). Cancelling leaves partial
    /// downloads in place to resume later. One session serves every Retry of the same Progress screen,
    /// so it remembers which installers it has already launched: a retry detects the install first and
    /// only relaunches when it is not there.
    /// </summary>
    internal sealed class InstallSession
    {
        /// <summary>How often the installer hand-off polls detection.</summary>
        public static readonly TimeSpan HandoffPollInterval = TimeSpan.FromSeconds(2);

        private readonly InstallDependencies _deps;
        private readonly LauncherManifest _manifest;
        private readonly object _pokeGate = new object();
        private readonly HashSet<string> _launchedInstallers = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, InstallerProcessWatch> _watches = new Dictionary<string, InstallerProcessWatch>(StringComparer.Ordinal);
        private TaskCompletionSource<bool> _poke = NewPoke();
        private readonly List<string> _stagedUntilVerified = new List<string>();
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
                ITelemetrySink t = _deps.Telemetry;
                var staged = new Dictionary<string, StagedComponent>(StringComparer.Ordinal);
                var clocks = new Dictionary<string, Stopwatch>(StringComparer.Ordinal);
                SetPhase(InstallPhase.Stoppable);

                // 1. Download phase: nothing is installed until every download has been verified.
                foreach (ManifestComponent c in components)
                {
                    if (!NeedsDownload(c))
                    {
                        continue;
                    }

                    t.RecordComponent(c.Id, TelemetryOutcome.Started);
                    clocks[c.Id] = Stopwatch.StartNew();
                    try
                    {
                        staged[c.Id] = await StageAsync(c, events, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // Nothing was installed yet: a Retry starts from the first component again.
                        return Failed(c, ex, clocks[c.Id], cancellationToken, components[0].Id);
                    }
                }

                // 2 and 3. Install phase: the installer hand-off, then the overlay, then the patch.
                foreach (ManifestComponent c in components)
                {
                    Stopwatch clock;
                    if (!clocks.TryGetValue(c.Id, out clock))
                    {
                        t.RecordComponent(c.Id, TelemetryOutcome.Started);
                        clock = Stopwatch.StartNew();
                    }

                    SetPhase(InstallPhase.Stoppable);
                    events.Report(new InstallEvent(InstallEventKind.StepStarted, c.Id));
                    try
                    {
                        StagedComponent s;
                        staged.TryGetValue(c.Id, out s);
                        bool didWork = await ApplyAsync(c, s, events, cancellationToken).ConfigureAwait(false);
                        t.RecordComponent(c.Id, TelemetryOutcome.Succeeded, LauncherError.None, clock.Elapsed);
                        events.Report(new InstallEvent(didWork ? InstallEventKind.StepDone : InstallEventKind.StepSkipped, c.Id));
                    }
                    catch (Exception ex)
                    {
                        return Failed(c, ex, clock, cancellationToken, c.Id);
                    }
                }

                InstallRunResult verified = await VerifyPlanAsync(components, events, cancellationToken).ConfigureAwait(false);
                if (verified.Error == LauncherError.None)
                {
                    // Kept until now so a repair does not have to download the archive again.
                    foreach (string kept in _stagedUntilVerified)
                    {
                        TryDeleteStaged(kept);
                    }

                    _stagedUntilVerified.Clear();
                }

                return verified;
            }
            finally
            {
                SetPhase(InstallPhase.Idle);
            }
        }

        private InstallRunResult Failed(ManifestComponent c, Exception ex, Stopwatch clock, CancellationToken ct, string retryFromId)
        {
            LauncherError code = Classify(ex, ct);
            _deps.Telemetry.RecordComponent(c.Id, code == LauncherError.Cancelled ? TelemetryOutcome.Cancelled : TelemetryOutcome.Failed, code, clock.Elapsed);
            if (!(ex is LauncherException) && !(ex is OperationCanceledException) && !IsExpectedIo(ex))
            {
                // A programming error: surface it as Unknown rather than crash the wizard.
                Trace.TraceError("Install step failed unexpectedly: " + ex.GetType().FullName);
            }

            return new InstallRunResult(code, c.Id, _acFolder, null, (ex as LauncherException)?.Data["transport"] as string, retryFromId);
        }

        /// <summary>
        /// After every step has run, checks the files once more and re-runs a non-installer step whose
        /// result no longer holds. The retail installer can still be copying its stock files after
        /// detection first sees the client, and a late write undoes the game data step; "Done" must
        /// mean the files are right, not that every step returned. A file that cannot be read gets one
        /// more look after a short wait. A second failure is reported as <see cref="LauncherError.GameFilesChanged"/>
        /// with the files and their states.
        /// </summary>
        private async Task<InstallRunResult> VerifyPlanAsync(IReadOnlyList<ManifestComponent> components, IProgress<InstallEvent> events, CancellationToken ct)
        {
            ITelemetrySink t = _deps.Telemetry;
            foreach (ManifestComponent c in components)
            {
                if (c.Kind == ComponentKinds.Installer)
                {
                    continue;
                }

                for (int attempt = 0; attempt < 2; attempt++)
                {
                    if (ct.IsCancellationRequested)
                    {
                        t.RecordComponent(c.Id, TelemetryOutcome.Cancelled, LauncherError.Cancelled, TimeSpan.Zero);
                        return new InstallRunResult(LauncherError.Cancelled, c.Id, _acFolder);
                    }

                    events.Report(new InstallEvent(InstallEventKind.StepStatus, c.Id, text: UiText.VerifyingGameFiles));
                    ComponentStatus status;
                    try
                    {
                        status = await EvaluateForVerifyAsync(c, events, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        LauncherError code = Classify(ex, ct);
                        t.RecordComponent(c.Id, code == LauncherError.Cancelled ? TelemetryOutcome.Cancelled : TelemetryOutcome.Failed, code, TimeSpan.Zero);
                        return new InstallRunResult(code, c.Id, _acFolder);
                    }

                    bool unreadable = status.State == ComponentState.Unknown;
                    if (!unreadable && status.State != ComponentState.Missing && status.State != ComponentState.NeedsRepair)
                    {
                        break;
                    }

                    if (unreadable || attempt == 1)
                    {
                        t.RecordComponent(c.Id, TelemetryOutcome.Failed, LauncherError.GameFilesChanged, TimeSpan.Zero);
                        return new InstallRunResult(LauncherError.GameFilesChanged, c.Id, _acFolder, FileDetailsOf(status));
                    }

                    // A re-run is a step like any other: it starts, reports, and records its outcome.
                    t.RecordComponent(c.Id, TelemetryOutcome.Started);
                    Stopwatch clock = Stopwatch.StartNew();
                    events.Report(new InstallEvent(InstallEventKind.StepStarted, c.Id));
                    try
                    {
                        bool didWork = await RunOneAsync(c, events, ct).ConfigureAwait(false);
                        t.RecordComponent(c.Id, TelemetryOutcome.Succeeded, LauncherError.None, clock.Elapsed);
                        events.Report(new InstallEvent(didWork ? InstallEventKind.StepDone : InstallEventKind.StepSkipped, c.Id));
                    }
                    catch (Exception ex)
                    {
                        LauncherError code = Classify(ex, ct);
                        t.RecordComponent(c.Id, code == LauncherError.Cancelled ? TelemetryOutcome.Cancelled : TelemetryOutcome.Failed, code, clock.Elapsed);
                        return new InstallRunResult(code, c.Id, _acFolder);
                    }
                }
            }

            return new InstallRunResult(LauncherError.None, null, _acFolder);
        }

        private async Task<ComponentStatus> EvaluateForVerifyAsync(ManifestComponent c, IProgress<InstallEvent> events, CancellationToken ct)
        {
            string folder = _acFolder;
            var progress = new ForwardingProgress(c.Id, events);
            ComponentStatus status = await Task.Run(() => _deps.Evaluator.Evaluate(c, _manifest, folder, progress, ct), ct).ConfigureAwait(false);
            if (status.State == ComponentState.Unknown)
            {
                // Locked by a scanner or the like: look once more before calling it a failure.
                await Task.Delay(_deps.VerifyRetryDelay, ct).ConfigureAwait(false);
                status = await Task.Run(() => _deps.Evaluator.Evaluate(c, _manifest, folder, progress, ct), ct).ConfigureAwait(false);
            }

            return status;
        }

        private static IReadOnlyList<string> FileDetailsOf(ComponentStatus status)
        {
            return status.Files
                .Where(f => f.State != FileState.Ok)
                .Select(f => f.Path + ": " + f.State)
                .ToList();
        }

        /// <summary>
        /// After the retail installer is first detected, waits until the installer has really finished, so
        /// the game data step never races the installer's own copying. Finished means BOTH:
        /// <list type="bullet">
        /// <item>no process of the installer is running: neither the started process nor any of its
        /// descendants, nor a known InstallShield helper (see <see cref="InstallerProcessWatch"/>);
        /// the started process exiting alone is never the signal, because the self-extractor exits early;</item>
        /// <item>the files this manifest cares about (every overlay file and every satisfiedWhen path that
        /// exists) have settled: two polls, <see cref="InstallDependencies.SettleInterval"/> apart and
        /// both made with no installer process running, with identical (size, last write) stamps and
        /// every exclusive open succeeding.</item>
        /// </list>
        /// There is no carrying on regardless: after <see cref="InstallDependencies.SettleTimeout"/> the
        /// player is asked to finish the installer, and the wait goes on. "I finished" runs one check at
        /// once with the same in-use and settle conditions, which it never skips. The one thing a click
        /// relaxes: InstallShield helpers alone (outside the tracked installer tree) no longer block,
        /// because a helper can linger after the wizard closes. That decision is logged with the helper
        /// names. Helpers alone still block the automatic path.
        /// </summary>
        private async Task WaitForInstallerDoneAsync(ManifestComponent installer, string folder, InstallerProcessWatch watch, IProgress<InstallEvent> events, CancellationToken ct)
        {
            var relative = new List<string>();
            foreach (ManifestComponent comp in _manifest.Components)
            {
                relative.AddRange(comp.Files.Select(f => f.Path));
                relative.AddRange(comp.SatisfiedWhen.Select(r => r.Path));
            }

            List<string> targets = relative.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            events.Report(new InstallEvent(InstallEventKind.StepStatus, installer.Id, text: UiText.WaitingForInstallerFiles));
            Stopwatch clock = Stopwatch.StartNew();
            string previous = null;
            bool poked = false;
            bool warned = false;

            // One look: the files' stamps, or null while the installer (as far as this look allows) runs or a file is in use.
            string Look(bool helpersAllowed, out IReadOnlyList<string> helpers)
            {
                helpers = new string[0];
                if (watch != null)
                {
                    InstallerActivity activity = watch.Check();
                    helpers = watch.LastHelpers;
                    if (activity == InstallerActivity.Tree || (activity == InstallerActivity.HelpersOnly && !helpersAllowed))
                    {
                        return null;
                    }
                }

                return SettleSnapshot(folder, targets);
            }

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                Task<bool> poke;
                lock (_pokeGate)
                {
                    poke = _poke.Task;
                }

                IReadOnlyList<string> helpers = null;
                bool clicked = poked;
                string snapshot = await Task.Run(() => Look(clicked, out helpers), ct).ConfigureAwait(false);
                if (snapshot != null && snapshot == previous && (!poked || helpers.Count == 0))
                {
                    // An automatic pass never reaches here with helpers running; a click with none needs no note.
                    return;
                }

                if (poked && snapshot != null)
                {
                    // "I finished" with the installer tree gone and the files openable: one confirming look a
                    // short gap later, rather than make the player wait a full settle interval.
                    await Task.Delay(_deps.PollInterval, ct).ConfigureAwait(false);
                    IReadOnlyList<string> confirmHelpers = null;
                    string confirm = await Task.Run(() => Look(true, out confirmHelpers), ct).ConfigureAwait(false);
                    if (confirm != null && confirm == snapshot)
                    {
                        if (confirmHelpers.Count > 0)
                        {
                            _deps.InstallLog("installer-handoff component=" + installer.Id + " proceeding on I finished: installer tree gone and files settled, InstallShield helper processes still running: " + string.Join(", ", confirmHelpers));
                        }

                        return;
                    }

                    snapshot = confirm;
                }

                // A look that allowed helpers must not pair with a later automatic look that does not.
                previous = poked ? null : snapshot;
                if (poked)
                {
                    events.Report(new InstallEvent(InstallEventKind.HandoffNotDetected, installer.Id, text: UiText.InstallerStillRunning));
                }
                else if (!warned && clock.Elapsed >= _deps.SettleTimeout)
                {
                    warned = true;
                    events.Report(new InstallEvent(InstallEventKind.StepStatus, installer.Id, text: UiText.InstallerStillRunning));
                }

                Task delay = Task.Delay(_deps.SettleInterval, ct);
                Task first = await Task.WhenAny(delay, poke).ConfigureAwait(false);
                poked = first == poke;
                if (poked)
                {
                    // Consumed BEFORE the check, so a click during the check is not lost.
                    lock (_pokeGate)
                    {
                        if (_poke.Task == poke)
                        {
                            _poke = NewPoke();
                        }
                    }
                }
                else
                {
                    await delay.ConfigureAwait(false);
                }
            }
        }

        /// <summary>Stamps of the existing targets, or null when any of them could not be opened exclusively.</summary>
        private static string SettleSnapshot(string folder, List<string> relativePaths)
        {
            var sb = new System.Text.StringBuilder();
            foreach (string rel in relativePaths)
            {
                string full = AcPathSafety.Resolve(folder, rel);
                var info = new FileInfo(full);
                if (!info.Exists)
                {
                    continue;
                }

                try
                {
                    using (new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.None))
                    {
                    }

                    info.Refresh();
                }
                catch (IOException)
                {
                    return null;
                }
                catch (UnauthorizedAccessException)
                {
                    return null;
                }

                sb.Append(rel).Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks).Append(';');
            }

            return sb.ToString();
        }

        /// <summary>What the download phase left for the install phase.</summary>
        private sealed class StagedComponent
        {
            /// <summary>The verified download in staging, or null when nothing was downloaded.</summary>
            public DownloadResult File { get; set; }

            /// <summary>For an installer launched earlier in this session: the folder it was detected in (no download, no relaunch).</summary>
            public string DetectedFolder { get; set; }
        }

        /// <summary>A patch has nothing to download; installers and overlays do.</summary>
        private static bool NeedsDownload(ManifestComponent c)
        {
            return c.Kind == ComponentKinds.Installer || c.Kind == ComponentKinds.ZipOverlay;
        }

        /// <summary>Download phase for one component: downloads and verifies it into staging. Installs nothing.</summary>
        private async Task<StagedComponent> StageAsync(ManifestComponent c, IProgress<InstallEvent> events, CancellationToken ct)
        {
            if (c.Kind == ComponentKinds.Installer && _launchedInstallers.Contains(c.Id))
            {
                // A retry after the hand-off was stopped: the player may have finished the installer meanwhile.
                string already = await Task.Run(() => Detect(c, ct), ct).ConfigureAwait(false);
                if (already != null)
                {
                    return new StagedComponent { DetectedFolder = already };
                }
            }

            events.Report(new InstallEvent(InstallEventKind.StageStarted, c.Id));
            var progress = new ForwardingProgress(c.Id, events);
            string fileName = c.Id + (c.Kind == ComponentKinds.Installer ? ".exe" : ".zip");
            DownloadResult file = await DownloadAsync(c, fileName, progress, events, ct).ConfigureAwait(false);
            events.Report(new InstallEvent(InstallEventKind.StageDone, c.Id));
            return new StagedComponent { File = file };
        }

        /// <summary>Install phase for one component, from what <see cref="StageAsync"/> staged (null for a patch).</summary>
        private async Task<bool> ApplyAsync(ManifestComponent c, StagedComponent staged, IProgress<InstallEvent> events, CancellationToken ct)
        {
            var progress = new ForwardingProgress(c.Id, events);
            try
            {
                switch (c.Kind)
                {
                    case ComponentKinds.Installer:
                        return await RunInstallerAsync(c, staged, progress, events, ct).ConfigureAwait(false);
                    case ComponentKinds.ZipOverlay:
                        return await RunOverlayAsync(c, staged.File, progress, events, ct).ConfigureAwait(false);
                    case ComponentKinds.BytePatch:
                        return await RunPatchAsync(c, events, ct).ConfigureAwait(false);
                    default:
                        throw new LauncherException(LauncherError.ManifestInvalid, "Unknown component kind.");
                }
            }
            catch (LauncherException ex) when (ex.Code == LauncherError.GameRunning && c.Kind != ComponentKinds.Installer && _launchedInstallers.Count > 0)
            {
                // The installer ran in this session: it, not a game client, is the likely holder.
                throw new LauncherException(LauncherError.InstallerStillRunning, ex.Message, ex);
            }
        }

        /// <summary>A re-run during the final check: download (normally found in staging at once), then install.</summary>
        private async Task<bool> RunOneAsync(ManifestComponent c, IProgress<InstallEvent> events, CancellationToken ct)
        {
            StagedComponent staged = NeedsDownload(c) ? await StageAsync(c, events, ct).ConfigureAwait(false) : null;
            return await ApplyAsync(c, staged, events, ct).ConfigureAwait(false);
        }

        private async Task<bool> RunInstallerAsync(ManifestComponent c, StagedComponent staged, IProgress<ProgressReport> progress, IProgress<InstallEvent> events, CancellationToken ct)
        {
            if (staged.DetectedFolder != null)
            {
                // Launched by an earlier run of this session and detected since: still wait until it is gone.
                InstallerProcessWatch earlier;
                _watches.TryGetValue(c.Id, out earlier);
                events.Report(new InstallEvent(InstallEventKind.HandoffStarted, c.Id, text: UiText.InstallerHandoff));
                try
                {
                    await WaitForInstallerDoneAsync(c, staged.DetectedFolder, earlier, events, ct).ConfigureAwait(false);
                    await AdoptFolderAsync(staged.DetectedFolder).ConfigureAwait(false);
                }
                finally
                {
                    events.Report(new InstallEvent(InstallEventKind.HandoffEnded, c.Id));
                }

                return true;
            }

            DownloadResult file = staged.File;

            // InstallerRunner.Start re-hashes the staged installer through a deny-write handle and keeps
            // that handle open until the (elevated) process exists, so the bytes that run are the bytes verified.
            events.Report(new InstallEvent(InstallEventKind.StepStatus, c.Id, text: UiText.Verifying));
            InstallerProcess process = await Task.Run(() => _deps.Installers.Start(c, file.Path, progress, ct), ct).ConfigureAwait(false);
            _launchedInstallers.Add(c.Id);
            using (process)
            {
                // Rooted at the started process, plus anything running under the staged file's name.
                var watch = new InstallerProcessWatch(_deps.Processes, process.ProcessId, Path.GetFileName(file.Path));
                _watches[c.Id] = watch;
                events.Report(new InstallEvent(InstallEventKind.HandoffStarted, c.Id, text: UiText.InstallerHandoff));
                try
                {
                    string folder = await WaitForInstallAsync(c, events, ct).ConfigureAwait(false);
                    await WaitForInstallerDoneAsync(c, folder, watch, events, ct).ConfigureAwait(false);
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

        private async Task<bool> RunOverlayAsync(ManifestComponent c, DownloadResult file, IProgress<ProgressReport> progress, IProgress<InstallEvent> events, CancellationToken ct)
        {
            string folder = RequireFolder();
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

                // Kept until the final check passes, so a repair does not download it again.
                _stagedUntilVerified.Add(file.Path);
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

        private Task<DownloadResult> DownloadAsync(ManifestComponent c, string fileName, IProgress<ProgressReport> progress, IProgress<InstallEvent> events, CancellationToken ct)
        {
            DownloadRequest request = DownloadRequest.FromComponent(c, _deps.Paths, fileName);
            request.OnSourceStarted = attempt => events.Report(new InstallEvent(InstallEventKind.SourceChanged, c.Id, text: SourceText(c, attempt)));
            return _deps.Downloads.DownloadAsync(request, progress, ct);
        }

        /// <summary>
        /// The progress row's source line: which source is in use and, after a fallback, why the
        /// previous one was given up.
        /// </summary>
        internal static string SourceText(ManifestComponent c, SourceAttempt attempt)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string label = string.IsNullOrWhiteSpace(attempt.Source.Label) ? c.Title ?? c.Id : attempt.Source.Label;
            if (!attempt.IsFallback)
            {
                return string.Format(inv, UiText.DownloadingFromFormat, label);
            }

            return string.Format(inv, UiText.FallbackSourceFormat, label)
                + Environment.NewLine
                + string.Format(inv, UiText.FallbackReasonFormat, FallbackReason(attempt.PreviousFailure));
        }

        private static string FallbackReason(LauncherException failure)
        {
            if (failure == null)
            {
                return UiText.FallbackReasonUnknown;
            }

            switch (failure.Code)
            {
                case LauncherError.HashMismatch:
                case LauncherError.SizeMismatch:
                    return UiText.FallbackReasonDamaged;
                case LauncherError.SourceQuotaExceeded:
                    return UiText.FallbackReasonQuota;
                case LauncherError.ManifestInvalid:
                    return UiText.FallbackReasonUnusable;
                case LauncherError.NetworkUnavailable:
                    return UiText.FallbackReasonNetwork;
                case LauncherError.SourceHttpError:
                    return UiText.FallbackReasonUnavailable;
                default:
                    // Never the failure's Detail: that is technical text, and launcher.log already records it.
                    return UiText.FallbackReasonUnknown;
            }
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
