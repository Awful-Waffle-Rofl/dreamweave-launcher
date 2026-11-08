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
            TableLayoutPanel stack = UiKit.Stack();
            UiKit.Add(stack, UiKit.Heading(UiText.WelcomeTitle));
            UiKit.Add(stack, UiKit.Text(UiText.WelcomeIntro));
            UiKit.Add(stack, UiKit.Text(UiText.WelcomeSources));
            var optIn = new CheckBox { Text = UiText.TelemetryOptIn, Checked = model.TelemetryOptIn, AutoSize = true, MaximumSize = new System.Drawing.Size(560, 0), UseMnemonic = false };
            optIn.CheckedChanged += (s, e) => model.TelemetryOptIn = optIn.Checked;
            UiKit.Add(stack, optIn);
            if (model.ShowPrivacyLink)
            {
                var link = new LinkLabel { Text = UiText.Privacy, AutoSize = true };
                link.LinkClicked += (s, e) => OpenUrl(model.PrivacyUrl);
                UiKit.Add(stack, link);
            }

            Button next = UiKit.Button(UiText.Next);
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
            UiKit.Add(stack, UiKit.ButtonRow(next));
            _window.ShowScreen(stack);
        }

        // ---- 2. Location ----

        private async void ShowLocation()
        {
            var presenter = new LocationPresenter(_services.AcFileSystem, _services.Disk);
            TableLayoutPanel stack = UiKit.Stack();
            UiKit.Add(stack, UiKit.Heading(UiText.LocationTitle));
            UiKit.Add(stack, UiKit.Text(UiText.LocationIntro));
            var row = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var path = new TextBox { Dock = DockStyle.Fill };
            Button browse = UiKit.Button(UiText.Browse);
            row.Controls.Add(path, 0, 0);
            row.Controls.Add(browse, 1, 0);
            UiKit.Add(stack, row);
            Label check = UiKit.Add(stack, UiKit.Text(string.Empty));
            Label space = UiKit.Add(stack, UiKit.Text(string.Empty));
            Button back = UiKit.Button(UiText.Back, (s, e) => ShowWelcome());
            Button next = UiKit.Button(UiText.Next);
            next.Enabled = false;
            UiKit.Add(stack, UiKit.ButtonRow(back, next));
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
            TableLayoutPanel stack = UiKit.Stack();
            UiKit.Add(stack, UiKit.Heading(UiText.ComponentsTitle));
            Label checking = UiKit.Add(stack, UiKit.Text(UiText.Checking));
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
                    UiKit.Add(stack, errors);
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
            blocked.ForeColor = System.Drawing.Color.Firebrick;
            Button back = UiKit.Button(UiText.Back, (s, e) => ShowLocation());
            Button next = UiKit.Button(UiText.Next);
            Button checkAgain = UiKit.Button(UiText.CheckAgain, (s, e) => ShowComponents(null));

            void Refresh()
            {
                foreach (ComponentRow r in selection.Rows)
                {
                    statusLabels[r.Id].Text = r.SizeText + " - " + r.StatusText;
                }

                string reason = selection.NextBlockedReason;
                blocked.Text = reason ?? string.Empty;
                blocked.Visible = reason != null;
                next.Enabled = reason == null;
                checkAgain.Visible = selection.OffersCheckAgain;
            }

            foreach (ComponentRow r in selection.Rows)
            {
                var box = new GroupBox { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(8) };
                var inner = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
                var cb = new CheckBox { Text = r.Title, Checked = r.Checked, Enabled = r.CanToggle, AutoSize = true, UseMnemonic = false, Font = new System.Drawing.Font(box.Font, System.Drawing.FontStyle.Bold) };
                string id = r.Id;
                cb.CheckedChanged += (s, e) =>
                {
                    selection.SetChecked(id, cb.Checked);
                    Refresh();
                };
                inner.Controls.Add(cb);
                inner.Controls.Add(UiKit.Text(r.SourceText));
                Label status = UiKit.Text(string.Empty);
                statusLabels[r.Id] = status;
                inner.Controls.Add(status);
                if (r.Note != null)
                {
                    inner.Controls.Add(UiKit.Text(r.Note));
                }

                box.Controls.Add(inner);
                UiKit.Add(stack, box);
            }

            UiKit.Add(stack, blocked);
            UiKit.Add(stack, UiKit.ButtonRow(back, checkAgain, next));
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
            TableLayoutPanel stack = UiKit.Stack();
            UiKit.Add(stack, UiKit.Heading(UiText.ProgressTitle));
            var stepLabels = new Dictionary<string, Label>();
            foreach (ManifestComponent c in components)
            {
                stepLabels[c.Id] = UiKit.Add(stack, UiKit.Text((c.Title ?? c.Id) + " - " + UiText.StepPending));
            }

            Label current = UiKit.Add(stack, UiKit.Text(string.Empty));
            Label sourceNote = UiKit.Add(stack, UiKit.Text(string.Empty));
            var currentBar = UiKit.Add(stack, UiKit.Bar(ProgressBarStyle.Blocks, 1000));
            var totalBar = UiKit.Add(stack, UiKit.Bar(ProgressBarStyle.Blocks, 1000));

            var handoff = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill, Visible = false };
            handoff.Controls.Add(UiKit.Text(UiText.InstallerHandoff, true));
            Label notDetected = UiKit.Text(string.Empty);
            handoff.Controls.Add(notDetected);
            Button finished = UiKit.Button(UiText.IFinished);
            handoff.Controls.Add(UiKit.ButtonRow(finished));
            UiKit.Add(stack, handoff);

            var errors = UiKit.Add(stack, new ErrorPanel { ShowTryLater = true });
            Button cancel = UiKit.Button(UiText.Cancel);
            UiKit.Add(stack, UiKit.ButtonRow(cancel));
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

            string TitleOf(string id) => components.FirstOrDefault(c => c.Id == id)?.Title ?? id;

            void OnEvent(InstallEvent ev)
            {
                if (stack.IsDisposed)
                {
                    return;
                }

                Label step;
                stepLabels.TryGetValue(ev.ComponentId ?? string.Empty, out step);
                switch (ev.Kind)
                {
                    case InstallEventKind.StepStarted:
                        aggregate.Begin(ev.ComponentId);
                        if (step != null)
                        {
                            step.Text = TitleOf(ev.ComponentId) + " - " + UiText.StepRunning;
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
                        break;
                    case InstallEventKind.StepStatus:
                        current.Text = ev.Text;
                        break;
                    case InstallEventKind.StepDone:
                    case InstallEventKind.StepSkipped:
                        aggregate.Complete(ev.ComponentId);
                        if (step != null)
                        {
                            step.Text = TitleOf(ev.ComponentId) + " - " + (ev.Kind == InstallEventKind.StepDone ? UiText.StepDone : UiText.StepSkipped);
                        }

                        currentBar.Value = 1000;
                        break;
                    case InstallEventKind.HandoffStarted:
                        notDetected.Text = string.Empty;
                        handoff.Visible = true;
                        break;
                    case InstallEventKind.HandoffNotDetected:
                        notDetected.Text = ev.Text;
                        break;
                    case InstallEventKind.HandoffEnded:
                        handoff.Visible = false;
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
                _acFolder = result.AcFolder;
                if (result.Error == LauncherError.None)
                {
                    _window.CloseGuard = null;
                    ShowDone();
                    return;
                }

                int failed = components.ToList().FindIndex(c => c.Id == result.ComponentId);
                startIndex = failed < 0 ? 0 : failed;
                if (result.ComponentId != null && stepLabels.TryGetValue(result.ComponentId, out Label failedLabel))
                {
                    failedLabel.Text = TitleOf(result.ComponentId) + " - " + UiText.StepFailed;
                }

                errors.ShowError(result.Error, TelemetryStepFor(result.ComponentId), result.FileDetails);
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
            TableLayoutPanel stack = UiKit.Stack();
            UiKit.Add(stack, UiKit.Heading(UiText.DoneTitle));
            UiKit.Add(stack, UiKit.Text(UiText.DoneBody));
            string folder = _acFolder;
            UiKit.Add(stack, UiKit.ButtonRow(UiKit.Button(UiText.Next, (s, e) => _finished(folder))));
            _window.ShowScreen(stack);
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
