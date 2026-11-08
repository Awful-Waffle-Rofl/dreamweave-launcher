using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DreamWeave.Launcher.App;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Detection;
using DreamWeave.Launcher.Core.Manifests;

namespace DreamWeave.Launcher.UI
{
    /// <summary>
    /// Drives the setup wizard's screens inside the main window: Welcome, Location, Components,
    /// Progress (with the installer hand-off), Done. Logic lives in the App presenters; this class only
    /// builds controls and moves between them. All Core work is awaited off the UI thread.
    /// </summary>
    internal sealed class WizardController
    {
        private readonly AppServices _services;
        private readonly MainWindow _window;
        private readonly ManifestLoadResult _load;
        private readonly Action<string> _finished;
        private string _acFolder;

        public WizardController(AppServices services, MainWindow window, ManifestLoadResult load, string acFolder, Action<string> finished)
        {
            _services = services;
            _window = window;
            _load = load;
            _acFolder = acFolder;
            _finished = finished;
        }

        private LauncherManifest Manifest => _load.Manifest;

        /// <summary>Starts at the Welcome screen.</summary>
        public void Start()
        {
            ShowWelcome();
        }

        /// <summary>Starts at the Components screen (Verify files found a problem).</summary>
        public void StartAtComponents(IReadOnlyList<ComponentStatus> statuses)
        {
            // An empty list means the check itself failed: evaluate again so the error and Retry show.
            ShowComponents(statuses != null && statuses.Count > 0 ? statuses : null);
        }

        // ---- 1. Welcome ----

        private async void ShowWelcome()
        {
            bool current = false;
            try
            {
                current = await _services.ReadStateAsync(s => s.TelemetryOptIn);
            }
            catch (LauncherException)
            {
            }

            var model = new WelcomeModel(Manifest, current);
            Page stack = UiKit.NewScreen(ScreenArt.Welcome);
            stack.Add(UiKit.Heading(UiText.WelcomeTitle));
            stack.Add(UiKit.Text(UiText.WelcomeIntro));
            stack.Add(UiKit.Text(UiText.WelcomeSources));
            stack.Add(new Checklist(UiText.WelcomeNextHeading, UiText.WelcomeStepFolder, UiText.WelcomeStepDownloads, UiText.WelcomeStepLogin));
            var optIn = new ThemedCheckBox { Text = UiText.TelemetryOptIn, Checked = model.TelemetryOptIn };
            optIn.CheckedChanged += (s, e) => model.TelemetryOptIn = optIn.Checked;
            stack.Add(optIn);
            if (model.ShowPrivacyLink)
            {
                stack.SetLeft(UiKit.Link(UiText.Privacy, () => OpenUrl(model.PrivacyUrl)));
            }

            Button next = UiKit.Button(UiText.Next, null, true);
            next.Click += async (s, e) =>
            {
                next.Enabled = false;
                if (model.TelemetryOptIn != current)
                {
                    try
                    {
                        await _services.Telemetry.SetOptInAsync(model.TelemetryOptIn);
                    }
                    catch (LauncherException)
                    {
                        // The choice is retried from Settings; setup carries on either way.
                    }
                }

                ShowLocation();
            };
            stack.SetButtons(next);
            _window.ShowScreen(stack);
        }

        // ---- 2. Location ----

        private async void ShowLocation()
        {
            var presenter = new LocationPresenter(_services.AcFileSystem, _services.Disk);
            Page stack = UiKit.NewScreen(ScreenArt.Install);
            stack.Add(UiKit.Heading(UiText.LocationTitle));
            stack.Add(UiKit.Text(UiText.LocationIntro));
            TextBox path = UiKit.Input();
            Button browse = UiKit.Button(UiText.Browse);
            stack.Add(UiKit.PathInput(path, browse));
            Label check = stack.Add(UiKit.Text(string.Empty));
            Label space = stack.Add(UiKit.Muted(string.Empty));
            Button back = UiKit.Button(UiText.Back, (s, e) => ShowWelcome());
            Button next = UiKit.Button(UiText.Next, null, true);
            next.Enabled = false;
            stack.SetButtons(back, next);
            _window.ShowScreen(stack);

            int version = 0;
            async void Revalidate()
            {
                int mine = ++version;
                string text = path.Text;
                next.Enabled = false;
                Tuple<FolderCheck, string> r = await Task.Run(() =>
                {
                    FolderCheck fc = presenter.Check(text);
                    string fs = fc.Usable ? presenter.FreeSpaceText(_services.Paths.StagingDir, text, Manifest.RequiredFreeBytes, Manifest.Components.Sum(c => Math.Max(0, c.Size))) : string.Empty;
                    return Tuple.Create(fc, fs);
                });
                if (mine != version || stack.IsDisposed)
                {
                    return;
                }

                check.Text = r.Item1.Message;
                check.ForeColor = r.Item1.Usable ? Theme.Ok : Theme.Err;
                space.Text = r.Item2;
                next.Enabled = r.Item1.Usable;
            }

            path.TextChanged += (s, e) => Revalidate();
            browse.Click += (s, e) =>
            {
                using (var dlg = new FolderBrowserDialog { Description = UiText.LocationPickFolder, ShowNewFolderButton = true, SelectedPath = path.Text })
                {
                    if (dlg.ShowDialog(_window) == DialogResult.OK)
                    {
                        path.Text = dlg.SelectedPath;
                    }
                }
            };
            next.Click += async (s, e) =>
            {
                next.Enabled = false;
                string folder = System.IO.Path.GetFullPath(path.Text);
                _acFolder = folder;
                try
                {
                    await _services.UpdateStateAsync(st => st.AcFolder = folder);
                }
                catch (LauncherException)
                {
                    // Not fatal: the folder is still used for this run.
                }

                ShowComponents(null);
            };

            string initial = _acFolder;
            if (string.IsNullOrEmpty(initial))
            {
                AcInstallLocation found = await Task.Run(() => _services.Locator.Locate(_services.State));
                initial = found.Found ? found.Folder : AcInstallLocator.DefaultInstallFolder;
            }

            if (!stack.IsDisposed)
            {
                path.Text = initial;
            }
        }

        // ---- 3. Components ----

        private async void ShowComponents(IReadOnlyList<ComponentStatus> known)
        {
            Page stack = UiKit.NewScreen(ScreenArt.Install);
            stack.Add(UiKit.Heading(UiText.ComponentsTitle));
            Label checking = stack.Add(UiKit.Muted(UiText.Checking));
            var errors = new ErrorPanel { ShowTryLater = true };
            _window.ShowScreen(stack);

            IReadOnlyList<ComponentStatus> statuses = known;
            if (statuses == null)
            {
                try
                {
                    string folder = _acFolder;
                    statuses = await Task.Run(() => _services.Evaluator.EvaluateAll(Manifest, folder));
                }
                catch (Exception ex) when (ex is LauncherException || ex is System.IO.IOException || ex is UnauthorizedAccessException)
                {
                    if (stack.IsDisposed)
                    {
                        return;
                    }

                    LauncherError code = ex is LauncherException le ? le.Code : LauncherError.AccessDenied;
                    checking.Visible = false;
                    stack.Add(errors);
                    errors.RetryClicked += (s, e) => ShowComponents(null);
                    errors.TryLaterClicked += (s, e) => _window.Close();
                    errors.ShowError(code, TelemetryStepFor(null));
                    return;
                }
            }

            if (stack.IsDisposed)
            {
                return;
            }

            checking.Visible = false;
            var selection = new ComponentSelection(Manifest, statuses, _load.DownloadsAllowed);
            var statusLabels = new Dictionary<string, Label>();
            Label blocked = UiKit.Text(string.Empty);
            blocked.ForeColor = Theme.Err;
            Button back = UiKit.Button(UiText.Back, (s, e) => ShowLocation());
            Button next = UiKit.Button(UiText.Next, null, true);
            Button checkAgain = UiKit.Button(UiText.CheckAgain, (s, e) => ShowComponents(null));

            void Refresh()
            {
                foreach (ComponentRow r in selection.Rows)
                {
                    statusLabels[r.Id].Text = r.SizeText + " - " + r.StatusText;
                    statusLabels[r.Id].ForeColor = StatusColor(r);
                }

                string reason = selection.NextBlockedReason;
                blocked.Text = reason ?? string.Empty;
                blocked.Visible = reason != null;
                next.Enabled = reason == null;
                checkAgain.Visible = selection.OffersCheckAgain;
            }

            foreach (ComponentRow r in selection.Rows)
            {
                var box = new RowBox();
                var cb = new ThemedCheckBox { Text = r.Title, Checked = r.Checked, Enabled = r.CanToggle, Font = Theme.BodyBold };
                string id = r.Id;
                cb.CheckedChanged += (s, e) =>
                {
                    selection.SetChecked(id, cb.Checked);
                    Refresh();
                };
                box.Controls.Add(cb);
                box.Controls.Add(UiKit.Muted(r.SourceText));
                Label status = UiKit.Text(string.Empty);
                statusLabels[r.Id] = status;
                box.Controls.Add(status);
                if (r.Note != null)
                {
                    box.Controls.Add(UiKit.Muted(r.Note));
                }

                stack.Add(box);
            }

            stack.Add(blocked);
            stack.SetButtons(back, checkAgain, next);
            next.Click += (s, e) =>
            {
                IReadOnlyList<ComponentRow> chosen = selection.Selected;
                if (chosen.Count == 0)
                {
                    ShowDone();
                }
                else
                {
                    ShowProgress(chosen.Select(c => c.Component).ToList());
                }
            };
            Refresh();
        }

        // ---- 4 and 5. Progress, with the installer hand-off ----

        private void ShowProgress(IReadOnlyList<ManifestComponent> components)
        {
            Page stack = UiKit.NewScreen(ScreenArt.Install);
            stack.Add(UiKit.Heading(UiText.ProgressTitle));
            var stepLabels = new Dictionary<string, StatusRow>();
            foreach (ManifestComponent c in components)
            {
                stepLabels[c.Id] = stack.Add(new StatusRow { Title = c.Title ?? c.Id, Status = UiText.StepPending, StatusColor = Theme.Muted });
            }

            Label current = stack.Add(UiKit.Text(string.Empty));
            Label sourceNote = stack.Add(UiKit.Muted(string.Empty));
            var currentBar = stack.Add(UiKit.Bar(ProgressBarStyle.Blocks, 1000));
            var totalBar = stack.Add(UiKit.Bar(ProgressBarStyle.Blocks, 1000));

            var handoff = new TintedBox { Visible = false };
            Label handoffText = UiKit.Text(UiText.InstallerHandoff);
            handoff.Controls.Add(handoffText);

            // What Cancel means while the installer is open: shown from before the launch call to the end.
            handoff.Controls.Add(UiKit.Text(UiText.HandoffCancelNote));
            Label notDetected = UiKit.Text(string.Empty);
            notDetected.ForeColor = Theme.Err;
            handoff.Controls.Add(notDetected);
            Button finished = UiKit.Button(UiText.IFinished, null, true);
            Control finishedRow = UiKit.ButtonRow(finished);
            handoff.Controls.Add(finishedRow);
            stack.Add(handoff);

            var errors = stack.Add(new ErrorPanel { ShowTryLater = true });
            Button cancel = UiKit.Button(UiText.Cancel);
            stack.SetButtons(cancel);
            _window.ShowScreen(stack);

            var aggregate = new AggregateProgress(components);
            int startIndex = 0;
            CancellationTokenSource cts = null;
            bool closeWhenStopped = false;

            // One session for every Retry of this screen: it remembers which installers it launched.
            var session = new InstallSession(InstallDependencies.From(_services), Manifest, _acFolder);
            _window.CloseGuard = () => InstallCloseRules.Decide(session.Phase);
            _window.StopRequested = () =>
            {
                // The answer can come after the phase changed under the open prompt.
                switch (session.RequestStop(cts))
                {
                    case StopOutcome.Blocked:
                        MessageBox.Show(_window, UiText.CloseBlocked, LauncherInfo.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                        break;
                    case StopOutcome.NotRunning:
                        // Run already returned: nothing to wait for. Posted, because this runs inside FormClosing.
                        _window.CloseGuard = null;
                        _window.Post(_window.Close);
                        break;
                    default:
                        closeWhenStopped = true;
                        cancel.Enabled = false;
                        break;
                }
            };

            void OnEvent(InstallEvent ev)
            {
                if (stack.IsDisposed)
                {
                    return;
                }

                StatusRow step;
                stepLabels.TryGetValue(ev.ComponentId ?? string.Empty, out step);
                switch (ev.Kind)
                {
                    case InstallEventKind.StageStarted:
                        aggregate.Begin(ev.ComponentId);
                        if (step != null)
                        {
                            step.Status = UiText.StepDownloading;
                            step.StatusColor = Theme.Gold;
                        }

                        current.Text = string.Empty;
                        sourceNote.Text = string.Empty;
                        currentBar.Value = 0;
                        break;
                    case InstallEventKind.StageDone:
                        // The download is the component's weight in the total bar; installing it adds nothing.
                        aggregate.Complete(ev.ComponentId);
                        if (step != null)
                        {
                            step.Status = UiText.StepDownloaded;
                            step.StatusColor = Theme.Muted;
                        }

                        currentBar.Value = 1000;
                        break;
                    case InstallEventKind.StepStarted:
                        aggregate.Begin(ev.ComponentId);
                        if (step != null)
                        {
                            step.Status = UiText.StepRunning;
                            step.StatusColor = Theme.Gold;
                        }

                        current.Text = string.Empty;
                        sourceNote.Text = string.Empty;
                        currentBar.Value = 0;
                        break;
                    case InstallEventKind.StepProgress:
                        aggregate.Report(ev.Report);
                        string phase = ev.Report.Message == Core.Downloads.TransferProgress.DownloadingMessage ? string.Empty : UiText.Verifying + " ";
                        current.Text = phase + ProgressText.Describe(ev.Report);
                        currentBar.Value = (int)Math.Round((ev.Report.Fraction ?? 0) * 1000);
                        break;
                    case InstallEventKind.SourceChanged:
                        sourceNote.Text = ev.Text;
                        // Colour only: a fallback (two lines) reads as a warning, the plain source line stays muted.
                        sourceNote.ForeColor = (ev.Text ?? string.Empty).Contains(Environment.NewLine) ? Theme.Gold : Theme.Muted;
                        break;
                    case InstallEventKind.StepStatus:
                        current.Text = ev.Text;
                        break;
                    case InstallEventKind.StepDone:
                    case InstallEventKind.StepSkipped:
                        aggregate.Complete(ev.ComponentId);
                        if (step != null)
                        {
                            step.Status = ev.Kind == InstallEventKind.StepDone ? UiText.StepDone : UiText.StepSkipped;
                            step.StatusColor = Theme.Ok;
                        }

                        currentBar.Value = 1000;
                        break;
                    case InstallEventKind.HandoffStarting:
                        // Before the launch call: the installer (or the Windows permission prompt) is on its way.
                        // "I finished" stays hidden until the installer has actually been started.
                        handoffText.Text = ev.Text ?? UiText.InstallerStarting;
                        finishedRow.Visible = false;
                        notDetected.Text = string.Empty;
                        handoff.Visible = true;
                        totalBar.Style = ProgressBarStyle.Marquee;
                        break;
                    case InstallEventKind.HandoffStarted:
                        handoffText.Text = UiText.InstallerHandoff;
                        finishedRow.Visible = true;
                        notDetected.Text = string.Empty;
                        handoff.Visible = true;

                        // The installer's own progress is unknown: a moving bar, not a nearly full one.
                        totalBar.Style = ProgressBarStyle.Marquee;
                        break;
                    case InstallEventKind.HandoffNotDetected:
                        notDetected.Text = ev.Text;
                        break;
                    case InstallEventKind.HandoffEnded:
                        handoff.Visible = false;
                        totalBar.Style = ProgressBarStyle.Blocks;
                        break;
                    case InstallEventKind.CriticalStarted:
                        cancel.Enabled = false;
                        break;
                    case InstallEventKind.CriticalEnded:
                        // Events arrive late: only re-enable while the run is still stoppable and not already stopping.
                        cancel.Enabled = session.Phase == InstallPhase.Stoppable && !closeWhenStopped && cts != null && !cts.IsCancellationRequested;
                        break;
                }

                totalBar.Value = (int)Math.Round(aggregate.Fraction * 1000);
            }

            async void Run()
            {
                errors.Clear();
                cancel.Enabled = true;
                cts = new CancellationTokenSource();
                var events = new Progress<InstallEvent>(OnEvent);
                List<ManifestComponent> remaining = components.Skip(startIndex).ToList();
                InstallRunResult result = await Task.Run(() => session.RunAsync(remaining, events, cts.Token));
                if (closeWhenStopped)
                {
                    _window.CloseGuard = null;
                    _window.Close();
                    return;
                }

                if (stack.IsDisposed)
                {
                    return;
                }

                cancel.Enabled = false;
                handoff.Visible = false;
                totalBar.Style = ProgressBarStyle.Blocks;
                _acFolder = result.AcFolder;
                if (result.Error == LauncherError.None)
                {
                    _window.CloseGuard = null;
                    ShowDone();
                    return;
                }

                // A download-phase failure retries from the run's first component: nothing was installed yet.
                int retryFrom = components.ToList().FindIndex(c => c.Id == result.RetryFromId);
                startIndex = retryFrom < 0 ? 0 : retryFrom;
                if (result.ComponentId != null && stepLabels.TryGetValue(result.ComponentId, out StatusRow failedLabel))
                {
                    failedLabel.Status = UiText.StepFailed;
                    failedLabel.StatusColor = Theme.Err;
                }

                errors.ShowError(result.Error, TelemetryStepFor(result.ComponentId), result.FileDetails, result.SourceTransport);
            }

            finished.Click += (s, e) => session.CheckInstallNow();
            cancel.Click += (s, e) =>
            {
                // Ignored while the game folder is being written; the button is disabled then anyway.
                if (session.RequestStop(cts) != StopOutcome.Blocked)
                {
                    cancel.Enabled = false;
                }
            };
            errors.RetryClicked += (s, e) => Run();
            errors.TryLaterClicked += (s, e) => _window.Close();
            Run();
        }

        // ---- 6. Done ----

        private void ShowDone()
        {
            Page stack = UiKit.NewScreen(ScreenArt.Welcome);
            stack.Add(UiKit.Heading(UiText.DoneTitle));
            stack.Add(UiKit.Text(UiText.DoneBody));
            string folder = _acFolder;
            stack.SetButtons(UiKit.Button(UiText.Next, (s, e) => _finished(folder), true));
            _window.ShowScreen(stack);
        }

        private static System.Drawing.Color StatusColor(ComponentRow r)
        {
            if (!r.Checked && r.RowStatus != RowStatus.Done)
            {
                return Theme.Muted;
            }

            switch (r.RowStatus)
            {
                case RowStatus.Done: return Theme.Ok;
                case RowStatus.NeedsRepair:
                case RowStatus.Unknown: return Theme.Err;
                default: return Theme.Muted;
            }
        }

        private static string TelemetryStepFor(string componentId)
        {
            return componentId ?? Core.Telemetry.TelemetrySteps.Manifest;
        }

        internal static void OpenUrl(string url)
        {
            try
            {
                Uri u;
                if (Uri.TryCreate(url, UriKind.Absolute, out u) && (u.Scheme == Uri.UriSchemeHttps))
                {
                    using (Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }))
                    {
                    }
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException)
            {
                // No browser: nothing useful to show.
            }
        }
    }
}
