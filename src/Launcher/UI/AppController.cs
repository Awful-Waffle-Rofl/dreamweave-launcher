using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DreamWeave.Launcher.App;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Detection;
using DreamWeave.Launcher.Core.Manifests;
using DreamWeave.Launcher.Core.Cleanup;
using DreamWeave.Launcher.Core.Telemetry;
using DreamWeave.Launcher.Core.Update;

namespace DreamWeave.Launcher.UI
{
    /// <summary>
    /// The normal start, once the services exist: load the manifest (with cache), configure telemetry,
    /// evaluate the components, then route to the Launcher window or the Wizard; check for a launcher
    /// update in the background. Runs on the UI thread and awaits every Core call off it.
    /// </summary>
    internal sealed class AppController
    {
        private readonly AppServices _services;
        private readonly SingleInstance _instance;
        private readonly MainWindow _window = new MainWindow();
        private ManifestLoadResult _load;
        private string _acFolder;
        private LauncherPresenter _presenter;
        private LauncherScreen _launcherScreen;
        private UpdateGate _gate = UpdateGate.None;

        public AppController(AppServices services, SingleInstance instance)
        {
            _services = services ?? throw new ArgumentNullException(nameof(services));
            _instance = instance;
        }

        /// <summary>Runs the message loop until the window closes.</summary>
        public void Run()
        {
            _window.Shown += (s, e) => StartAsync();
            Application.Run(_window);
        }

        private async void StartAsync()
        {
            ShowLoading();
            try
            {
                string seen = await _services.ReadStateAsync(s => s.LauncherVersionSeen);
                if (seen != LauncherInfo.Version)
                {
                    await _services.UpdateStateAsync(s => s.LauncherVersionSeen = LauncherInfo.Version);
                }
            }
            catch (LauncherException)
            {
                // Not fatal: only decides whether the first-time help starts expanded.
            }

            ManifestLoadResult load;
            try
            {
                load = await _services.Manifests.LoadAsync(CancellationToken.None);
            }
            catch (LauncherException ex)
            {
                ShowLoadFailed(ex.Code);
                return;
            }

            if (load.Manifest == null)
            {
                ShowLoadFailed(load.Status == LauncherError.None ? LauncherError.ManifestUnavailable : load.Status);
                return;
            }

            _load = load;
            _window.SetLinks(load.Manifest.Links?.Discord);
            try
            {
                await _services.Telemetry.ConfigureAsync(load.Manifest);
            }
            catch (Exception ex) when (ex is LauncherException || ex is IOException || ex is UnauthorizedAccessException)
            {
                // Telemetry stays off; it never stops the launcher.
            }

            _services.TelemetrySink.Record(TelemetrySteps.Manifest, load.Status == LauncherError.None ? TelemetryOutcome.Succeeded : TelemetryOutcome.Failed, load.Status);
            _gate = UpdateGate.Evaluate(load.Manifest, LauncherInfo.SemanticVersion);

            IReadOnlyList<ComponentStatus> statuses = null;
            try
            {
                AcInstallLocation where = await Task.Run(() => _services.Locator.Locate(_services.State));
                _acFolder = where.Folder;
                string folder = _acFolder;
                statuses = await Task.Run(() => _services.Evaluator.EvaluateAll(load.Manifest, folder));
            }
            catch (Exception ex) when (ex is LauncherException || ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                // Could not check: the wizard re-evaluates and shows the error with Retry.
                statuses = null;
            }

            if (RouteDecider.Decide(load.Manifest, statuses) == StartRoute.Launcher)
            {
                ShowLauncher();
            }
            else
            {
                new WizardController(_services, _window, _load, _acFolder, OnWizardFinished).Start();
            }
        }

        private void ShowLoading()
        {
            Page stack = UiKit.NewScreen(ScreenArt.Launcher);
            stack.Add(UiKit.Heading(LauncherInfo.ProductName));
            stack.Add(UiKit.Text(UiText.Loading));
            stack.Add(UiKit.Bar(ProgressBarStyle.Marquee));
            _window.ShowScreen(stack);
        }

        private void ShowLoadFailed(LauncherError code)
        {
            Page stack = UiKit.NewScreen(ScreenArt.Launcher);
            stack.Add(UiKit.Heading(LauncherInfo.ProductName));
            var errors = stack.Add(new ErrorPanel { ShowTryLater = true });
            errors.RetryClicked += (s, e) => StartAsync();
            errors.TryLaterClicked += (s, e) => _window.Close();
            errors.ShowError(code, TelemetrySteps.Manifest);
            _window.ShowScreen(stack);
        }

        private void OnWizardFinished(string acFolder)
        {
            _acFolder = acFolder ?? _acFolder;
            ShowLauncher();
        }

        private void ShowLauncher()
        {
            _presenter = new LauncherPresenter(_services.Credentials, new GameStarter(), _services.TelemetrySink, () => _services.Manifests.Manifest, () => _acFolder, new StateSlotAccounts(_services.State))
            {
                Update = _gate,
            };
            _launcherScreen = new LauncherScreen(_presenter, _services.Manifests.Manifest?.Notice);
            _window.FormClosing -= OnLauncherClosing;
            _window.FormClosing += OnLauncherClosing;
            _launcherScreen.UpdateClicked += (s, e) => ApplyUpdate();
            _launcherScreen.SettingsClicked += (s, e) => ShowSettings();
            _window.ShowScreen(_launcherScreen);
        }

        // Closing during Play all drops the rows not yet started. A client that is mid-start is never
        // interrupted: the close waits for it, then goes ahead.
        private async void OnLauncherClosing(object sender, FormClosingEventArgs e)
        {
            LauncherPresenter presenter = _presenter;
            if (presenter == null)
            {
                return;
            }

            presenter.CancelPending();
            if (!presenter.IsStartInFlight)
            {
                return;
            }

            e.Cancel = true;
            _window.FormClosing -= OnLauncherClosing;
            await presenter.WhenNoStartInFlightAsync();
            if (!_window.IsDisposed)
            {
                _window.Close();
            }
        }

        private void ShowSettings()
        {
            using (var form = new SettingsForm(_services, _acFolder, VerifyAsync, CheckForUpdatesAsync, StartUninstall))
            {
                form.FolderChanged += (s, folder) => _acFolder = folder;
                form.ShowDialog(_window);
                if (form.VerifyFoundProblems != null)
                {
                    new WizardController(_services, _window, _load, _acFolder, OnWizardFinished).StartAtComponents(form.VerifyFoundProblems);
                }
            }
        }

        /// <summary>
        /// Verify files: drops the hash cache (the equivalent of an ignore-cache evaluation) and evaluates
        /// every component again. Returns null when everything required is satisfied, otherwise the statuses.
        /// </summary>
        private async Task<IReadOnlyList<ComponentStatus>> VerifyAsync()
        {
            LauncherManifest manifest = _services.Manifests.Manifest;
            if (manifest == null)
            {
                return null;
            }

            string folder = _acFolder;
            try
            {
                await _services.UpdateStateAsync(s => s.FileVerifyCache.Clear());
                IReadOnlyList<ComponentStatus> statuses = await Task.Run(() => _services.Evaluator.EvaluateAll(manifest, folder));
                return RouteDecider.Decide(manifest, statuses) == StartRoute.Launcher ? null : statuses;
            }
            catch (LauncherException)
            {
                return new ComponentStatus[0];
            }
        }

        private async Task<string> CheckForUpdatesAsync()
        {
            try
            {
                ManifestLoadResult load = await _services.Manifests.LoadAsync(CancellationToken.None);
                if (load?.Manifest != null)
                {
                    _load = load;
                    await _services.Telemetry.ConfigureAsync(load.Manifest);
                }
            }
            catch (LauncherException)
            {
            }

            _gate = UpdateGate.Evaluate(_services.Manifests.Manifest, LauncherInfo.SemanticVersion);
            if (_presenter != null)
            {
                _presenter.Update = _gate;
                _launcherScreen?.RefreshState();
            }

            return _gate.BannerText;
        }

        private async void ApplyUpdate()
        {
            LauncherManifest manifest = _services.Manifests.Manifest;
            if (manifest == null || !_gate.OffersUpdate)
            {
                return;
            }

            _launcherScreen?.ShowStatus(UiText.Updating);
            ITelemetrySink t = _services.TelemetrySink;
            t.Record(TelemetrySteps.LauncherUpdate, TelemetryOutcome.Started);
            Stopwatch clock = Stopwatch.StartNew();
            var updater = new SelfUpdater(_services.Paths, LauncherInfo.SemanticVersion, _services.RunningExePath, _services.Downloads, new WinTrustVerifier(), _services.FileSystem, _services.Processes);
            var progress = new Progress<ProgressReport>(r => _launcherScreen?.ShowStatus(UiText.Updating + " " + ProgressText.Describe(r)));
            try
            {
                // The new process (started with --updated) waits a bounded time for this one's mutex.
                await Task.Run(() => updater.ApplyAsync(manifest, progress, CancellationToken.None));
                t.Record(TelemetrySteps.LauncherUpdate, TelemetryOutcome.Succeeded, LauncherError.None, clock.Elapsed);
                _instance?.Release();
                _window.Close();
            }
            catch (LauncherException ex)
            {
                t.Record(TelemetrySteps.LauncherUpdate, TelemetryOutcome.Failed, ex.Code, clock.Elapsed);
                _launcherScreen?.ShowStatus(ErrorMessages.For(ex.Code));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                LauncherError code = LauncherException.FromIo(ex, "updating").Code;
                t.Record(TelemetrySteps.LauncherUpdate, TelemetryOutcome.Failed, code, clock.Elapsed);
                _launcherScreen?.ShowStatus(ErrorMessages.For(code));
            }
        }

        /// <summary>
        /// Hands uninstall over to the setup program when its registered command (HKCU, Inno Setup's
        /// <c>{AppId}_is1</c> key, read only) passes <see cref="SetupUninstallLocator.TryResolve"/>, otherwise
        /// (a development or portable run, or a command that does not check out) to this exe started with
        /// <c>--cleanup --handover</c>. Only once a start succeeded does it flush and stop telemetry, release
        /// the mutexes and close.
        /// </summary>
        private void StartUninstall()
        {
            string setupUninstall = SetupUninstallLocator.FindUninstallString(new UserRegistryReader());

            // The setup uninstaller refuses to run while the guard mutex exists, so let go of it first.
            SetupGuard.Release();
            if (!UninstallHandover.TryStart(_services.Processes, _services.FileSystem, setupUninstall, _services.RunningExePath))
            {
                // Nothing was started: this launcher keeps running with its services intact.
                SetupGuard.Hold();
                _launcherScreen?.ShowStatus(ErrorMessages.For(LauncherError.Unknown));
                return;
            }

            try
            {
                _services.Shutdown();
            }
            finally
            {
                // The started process is already waiting on us (the setup uninstaller retries its AppMutex
                // check; --cleanup --handover waits up to 30 s for the single-instance mutex): release it
                // even if the flush failed.
                _instance?.Release();
                _window.Close();
            }
        }
    }
}
