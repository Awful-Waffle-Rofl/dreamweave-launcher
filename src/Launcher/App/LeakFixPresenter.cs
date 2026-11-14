using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Detection;
using DreamWeave.Launcher.Core.Manifests;
using DreamWeave.Launcher.Core.Telemetry;

namespace DreamWeave.Launcher.App
{
    /// <summary>What the launcher window's memory leak fix line shows right now.</summary>
    internal sealed class LeakFixView
    {
        public static readonly LeakFixView Hidden = new LeakFixView(false, string.Empty, false, null, null);

        public LeakFixView(bool visible, string text, bool clickable, string tooltip, string message)
        {
            Visible = visible;
            Text = text;
            Clickable = clickable;
            Tooltip = tooltip;
            Message = message;
        }

        /// <summary>False: show nothing at all.</summary>
        public bool Visible { get; }

        /// <summary>The line itself (the check-marked status, or the link text).</summary>
        public string Text { get; }

        /// <summary>True only for the "click here" link while nothing is running.</summary>
        public bool Clickable { get; }

        /// <summary>Discloses the manifest's source label; null when there is nothing to disclose.</summary>
        public string Tooltip { get; }

        /// <summary>The failure text of the last click, or null.</summary>
        public string Message { get; }
    }

    /// <summary>
    /// The optional memory leak fix (the manifest's <c>palette-leak-fix</c> byte patch) as seen from the
    /// launcher window. It never changes the route and never applies anything on its own: the only way to
    /// a write is <see cref="ApplyAsync"/>, which the player's click calls. The write is the wizard's own
    /// code path: an <see cref="InstallSession"/> over just this one component, which runs
    /// <c>BytePatcher.Apply</c> and, when the game folder needs administrator rights, the elevated
    /// verified placement, and records the step in telemetry.
    /// </summary>
    internal sealed class LeakFixPresenter
    {
        private readonly Func<LauncherManifest> _manifest;
        private readonly Func<string> _acFolder;
        private readonly InstallDependencies _deps;
        private ComponentState? _state;
        private string _message;
        private int _busy;
        private volatile InstallSession _session;

        public LeakFixPresenter(Func<LauncherManifest> manifest, Func<string> acFolder, InstallDependencies deps)
        {
            _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
            _acFolder = acFolder ?? throw new ArgumentNullException(nameof(acFolder));
            _deps = deps ?? throw new ArgumentNullException(nameof(deps));
        }

        /// <summary>The manifest's fix component, or null when the manifest has none.</summary>
        public ManifestComponent Component => _manifest()?.Components?.FirstOrDefault(c => c != null && c.Kind == ComponentKinds.BytePatch && c.Id == TelemetrySteps.PaletteLeakFix);

        /// <summary>True while a click is being applied.</summary>
        public bool IsBusy => Volatile.Read(ref _busy) != 0;

        /// <summary>What the running session is doing, or Idle when none is running.</summary>
        public InstallPhase Phase => _session?.Phase ?? InstallPhase.Idle;

        /// <summary>
        /// What closing the window should do: Block from the click until the line is current again (any running
        /// phase, the write included, and the final look), otherwise Allow.
        /// </summary>
        public CloseDecision CloseDecision => IsBusy || Phase != InstallPhase.Idle ? CloseDecision.Block : CloseDecision.Allow;

        /// <summary>Test seam: awaited at the start of every refresh, so a test can hold the final look after an apply.</summary>
        internal Func<Task> BeforeRefresh { get; set; }

        /// <summary>The last evaluated state, or null when it has not been evaluated or could not be.</summary>
        public ComponentState? State => _state;

        /// <summary>What the line shows now.</summary>
        public LeakFixView View
        {
            get
            {
                ManifestComponent c = Component;
                if (c == null)
                {
                    return LeakFixView.Hidden;
                }

                return ViewFor(_state, c, IsBusy, _message);
            }
        }

        /// <summary>
        /// The pure state mapping: Satisfied is the check-marked status, Missing is the link (or the busy
        /// text while a click runs), and every other state, or none, shows nothing: the patcher would refuse
        /// a file it was not made for, so it is not offered.
        /// </summary>
        internal static LeakFixView ViewFor(ComponentState? state, ManifestComponent component, bool busy, string message)
        {
            if (component == null)
            {
                return LeakFixView.Hidden;
            }

            if (state == null)
            {
                return HiddenWith(message);
            }

            switch (state.Value)
            {
                case ComponentState.Satisfied:
                    return new LeakFixView(true, UiText.LeakFixCheck + " " + UiText.LeakFixInPlace, false, null, null);
                case ComponentState.Missing:
                    string tooltip = string.IsNullOrWhiteSpace(component.SourceLabel)
                        ? null
                        : string.Format(System.Globalization.CultureInfo.InvariantCulture, UiText.LeakFixSourceFormat, component.SourceLabel.TrimEnd('.', ' '));
                    return new LeakFixView(true, busy ? UiText.LeakFixApplying : UiText.LeakFixApply, !busy, tooltip, busy ? null : message);
                default:
                    return HiddenWith(message);
            }
        }

        // The line itself is gone, but the answer to the player's last click must not vanish with it.
        private static LeakFixView HiddenWith(string message)
        {
            return message == null ? LeakFixView.Hidden : new LeakFixView(false, string.Empty, false, null, message);
        }

        /// <summary>
        /// The player's text for a failed click, per code. This window has no Retry button, so none of the
        /// wizard's "click Retry" wording is reused. Only a code that is known to leave the file alone says
        /// "nothing was changed": a failure from the final check can come after the patch was written.
        /// </summary>
        internal static string MessageFor(LauncherError code)
        {
            switch (code)
            {
                case LauncherError.GameRunning:
                    return UiText.LeakFixGameRunning;
                case LauncherError.ElevationDeclined:
                    return UiText.LeakFixDeclined;
                case LauncherError.ElevationRequired:
                case LauncherError.ElevatedTaskInvalid:
                    return UiText.LeakFixNeedsAdmin;
                case LauncherError.DiskFull:
                    return UiText.LeakFixDiskFull;
                default:
                    return UiText.LeakFixGeneric;
            }
        }
        /// <summary>Evaluates the component against the game folder (off the UI thread). Any failure to look hides the line.</summary>
        public async Task RefreshAsync()
        {
            ManifestComponent c = Component;
            LauncherManifest m = _manifest();
            string folder = _acFolder();
            if (c == null || m == null || string.IsNullOrWhiteSpace(folder))
            {
                _state = null;
                return;
            }

            try
            {
                Func<Task> hook = BeforeRefresh;
                if (hook != null)
                {
                    await hook().ConfigureAwait(true);
                }

                ComponentStatus status = await Task.Run(() => _deps.Evaluator.Evaluate(c, m, folder, null, CancellationToken.None)).ConfigureAwait(true);
                _state = status.State;
            }
            catch (Exception ex) when (ex is LauncherException || ex is System.IO.IOException || ex is UnauthorizedAccessException)
            {
                _state = null;
            }
        }

        /// <summary>
        /// Applies the fix when it is currently offered (state Missing) and nothing else is running; otherwise
        /// returns <see cref="LauncherError.None"/> without touching anything. The busy flag is set before the
        /// first await, so a caller that refreshes its controls right after this call sees the busy state.
        /// </summary>
        public Task<LauncherError> ApplyAsync()
        {
            if (_state != ComponentState.Missing || Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            {
                return Task.FromResult(LauncherError.None);
            }

            _message = null;
            return ApplyCoreAsync();
        }

        private async Task<LauncherError> ApplyCoreAsync()
        {
            LauncherError error = LauncherError.None;
            try
            {
                ManifestComponent c = Component;
                LauncherManifest m = _manifest();
                string folder = _acFolder();
                if (c == null || m == null)
                {
                    return LauncherError.None;
                }

                try
                {
                    _session = new InstallSession(_deps, m, folder);
                    InstallRunResult result = await _session.RunAsync(new[] { c }, new Progress<InstallEvent>(), CancellationToken.None).ConfigureAwait(true);
                    error = result.Error;
                    _message = error == LauncherError.None ? null : MessageFor(error);
                }
                catch (Exception ex) when (ex is LauncherException || ex is System.IO.IOException || ex is UnauthorizedAccessException)
                {
                    error = ex is LauncherException le ? le.Code : LauncherError.Unknown;
                    _message = MessageFor(error);
                }

                // Whatever happened, the file is the truth: look again so the line matches it. Still busy
                // until it has, so a second click cannot read the old state and start another session.
                await RefreshAsync().ConfigureAwait(true);
                return error;
            }
            finally
            {
                _session = null;
                Volatile.Write(ref _busy, 0);
            }
        }
    }
}