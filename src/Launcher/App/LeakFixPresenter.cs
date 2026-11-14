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
            if (component == null || state == null)
            {
                return LeakFixView.Hidden;
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
                    return LeakFixView.Hidden;
            }
        }

        /// <summary>
        /// The player's text for a failed click. The wizard's messages say "Retry", a button this window
        /// does not have, so those are replaced by one that points at the link.
        /// </summary>
        internal static string MessageFor(LauncherError code)
        {
            if (code == LauncherError.GameRunning || code == LauncherError.InstallerStillRunning)
            {
                return UiText.LeakFixGameRunning;
            }

            string table = ErrorMessages.For(code);
            return table.IndexOf("Retry", StringComparison.Ordinal) >= 0 ? UiText.LeakFixTryAgain : table;
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

                var session = new InstallSession(_deps, m, folder);
                InstallRunResult result = await session.RunAsync(new[] { c }, new Progress<InstallEvent>(), CancellationToken.None).ConfigureAwait(true);
                error = result.Error;
                _message = error == LauncherError.None ? null : MessageFor(error);
            }
            catch (Exception ex) when (ex is LauncherException || ex is System.IO.IOException || ex is UnauthorizedAccessException)
            {
                error = ex is LauncherException le ? le.Code : LauncherError.Unknown;
                _message = MessageFor(error);
            }
            finally
            {
                Volatile.Write(ref _busy, 0);
            }

            // Whatever happened, the file is the truth: look again so the line matches it.
            await RefreshAsync().ConfigureAwait(true);
            return error;
        }
    }
}