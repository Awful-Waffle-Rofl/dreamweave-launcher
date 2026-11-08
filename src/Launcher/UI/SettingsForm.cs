using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Forms;
using DreamWeave.Launcher.App;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Detection;

namespace DreamWeave.Launcher.UI
{
    /// <summary>
    /// Settings: game folder, Verify files, telemetry toggle, Check for updates, Uninstall, version.
    /// The form only collects input; the owner (<see cref="AppController"/>) supplies each action.
    /// </summary>
    internal sealed class SettingsForm : Form
    {
        private readonly AppServices _services;
        private readonly Label _message;

        public SettingsForm(AppServices services, string acFolder, Func<Task<IReadOnlyList<ComponentStatus>>> verify, Func<Task<string>> checkForUpdates, Action uninstall)
        {
            _services = services;
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = UiText.SettingsTitle;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(520, 400);
            Theme.Apply(this);

            VStack stack = UiKit.Stack();
            Controls.Add(stack);

            UiKit.Add(stack, UiKit.Heading(UiText.SettingsTitle));
            UiKit.Add(stack, UiKit.Text(UiText.SettingsFolder, true));
            TextBox folder = UiKit.Input();
            folder.ReadOnly = true;
            folder.Text = acFolder ?? string.Empty;
            Button browse = UiKit.Button(UiText.Browse);
            UiKit.Add(stack, UiKit.PathInput(folder, browse));

            Button verifyButton = UiKit.Button(UiText.SettingsVerify);
            UiKit.Add(stack, UiKit.ButtonRow(verifyButton));

            var telemetry = new ThemedCheckBox { Text = UiText.SettingsTelemetry, Enabled = false };
            UiKit.Add(stack, telemetry);

            Button updates = UiKit.Button(UiText.SettingsCheckUpdates);
            Button uninstallButton = UiKit.Button(UiText.SettingsUninstall);
            UiKit.Add(stack, UiKit.ButtonRow(updates, uninstallButton));

            _message = UiKit.Add(stack, UiKit.Text(string.Empty));
            UiKit.Add(stack, UiKit.Muted(string.Format(CultureInfo.InvariantCulture, UiText.SettingsVersionFormat, LauncherInfo.Version)));
            Button close = UiKit.Button(UiText.Close, (s, e) => Close(), true);
            UiKit.Add(stack, UiKit.ButtonRow(close));
            CancelButton = close;

            var location = new LocationPresenter(services.AcFileSystem, services.Disk);
            browse.Click += async (s, e) =>
            {
                string chosen;
                using (var dlg = new FolderBrowserDialog { Description = UiText.LocationPickFolder, ShowNewFolderButton = false, SelectedPath = folder.Text })
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK)
                    {
                        return;
                    }

                    chosen = dlg.SelectedPath;
                }

                FolderCheck check = await Task.Run(() => location.Check(chosen));
                if (!check.Usable)
                {
                    _message.Text = check.Message;
                    return;
                }

                if (!check.HasClient)
                {
                    _message.Text = UiText.SettingsFolderNoClient;
                    return;
                }

                try
                {
                    string full = System.IO.Path.GetFullPath(chosen);
                    await services.UpdateStateAsync(st => st.AcFolder = full);
                    folder.Text = full;
                    FolderChanged?.Invoke(this, full);
                    _message.Text = check.Message;
                }
                catch (LauncherException ex)
                {
                    _message.Text = ErrorMessages.For(ex.Code);
                }
            };

            verifyButton.Click += async (s, e) =>
            {
                verifyButton.Enabled = false;
                _message.Text = UiText.Checking;
                IReadOnlyList<ComponentStatus> statuses = await verify();
                if (IsDisposed)
                {
                    return;
                }

                verifyButton.Enabled = true;
                if (statuses == null)
                {
                    _message.Text = UiText.SettingsVerifyOk;
                    return;
                }

                VerifyFoundProblems = statuses;
                Close();
            };

            updates.Click += async (s, e) =>
            {
                updates.Enabled = false;
                string text = await checkForUpdates();
                if (IsDisposed)
                {
                    return;
                }

                updates.Enabled = true;
                _message.Text = text ?? UiText.SettingsUpToDate;
            };

            uninstallButton.Click += (s, e) =>
            {
                Close();
                uninstall();
            };

            Load += async (s, e) =>
            {
                bool optIn = false;
                try
                {
                    optIn = await services.ReadStateAsync(st => st.TelemetryOptIn);
                }
                catch (LauncherException)
                {
                }

                if (IsDisposed)
                {
                    return;
                }

                telemetry.Checked = optIn;
                telemetry.Enabled = true;
                bool reverting = false;
                telemetry.CheckedChanged += async (s2, e2) =>
                {
                    if (reverting)
                    {
                        return;
                    }

                    bool wanted = telemetry.Checked;
                    telemetry.Enabled = false;
                    LauncherError failure = LauncherError.None;
                    try
                    {
                        await _services.Telemetry.SetOptInAsync(wanted);
                    }
                    catch (LauncherException ex)
                    {
                        failure = ex.Code;
                    }

                    if (IsDisposed)
                    {
                        return;
                    }

                    if (failure != LauncherError.None)
                    {
                        _message.Text = ErrorMessages.For(failure);
                    }

                    // A failed opt-IN leaves telemetry off, so the box goes back to unticked. A failed
                    // opt-OUT still leaves telemetry off (the client drops its queue and writes its opt-out
                    // marker before the state write that failed), so the unticked box is already the truth.
                    if (failure != LauncherError.None && wanted)
                    {
                        reverting = true;
                        telemetry.Checked = false;
                        reverting = false;
                    }

                    telemetry.Enabled = true;
                };
            };
        }

        /// <summary>Raised when the player picks a new valid game folder.</summary>
        public event EventHandler<string> FolderChanged;

        /// <summary>Set when Verify files found something wrong; the owner routes to the wizard's Components screen.</summary>
        public IReadOnlyList<ComponentStatus> VerifyFoundProblems { get; private set; }
    }

    /// <summary>The <c>--uninstall</c> window: confirm (with the optional client checkbox), then run, then report.</summary>
    internal sealed class UninstallForm : Form
    {
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            Assets.Release();
        }

        /// <summary>How long the result stays on screen before the window closes itself (the self-delete helper waits about 60 s).</summary>
        internal static readonly TimeSpan AutoCloseAfter = TimeSpan.FromSeconds(20);

        public UninstallForm(Func<bool, Task<Core.SelfInstall.UninstallResult>> uninstall)
        {
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = UiText.UninstallTitle;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(520, 300);
            Theme.Apply(this);

            VStack stack = UiKit.Stack();
            Controls.Add(stack);
            UiKit.Add(stack, UiKit.Heading(UiText.UninstallTitle));
            Label body = UiKit.Add(stack, UiKit.Text(UiText.UninstallConfirm));
            var alsoClient = UiKit.Add(stack, new ThemedCheckBox { Text = UiText.UninstallAlsoClient, Checked = false });
            Button go = UiKit.Button(UiText.UninstallButton, null, true);
            Button cancel = UiKit.Button(UiText.Cancel, (s, e) => Close());
            UiKit.Add(stack, UiKit.ButtonRow(cancel, go));
            CancelButton = cancel;
            bool running = false;
            FormClosing += (s, e) => e.Cancel = running;

            go.Click += async (s, e) =>
            {
                running = true;
                go.Enabled = false;
                cancel.Enabled = false;
                alsoClient.Enabled = false;
                ControlBox = false;
                body.Text = UiText.UninstallRunning;
                Core.SelfInstall.UninstallResult result = null;
                try
                {
                    result = await uninstall(alsoClient.Checked);
                }
                catch (Exception ex) when (ex is ArgumentException || ex is LauncherException || ex is System.IO.IOException || ex is UnauthorizedAccessException)
                {
                    // Refused outright (for example unexpected folders): the caller's exit code stays Unknown.
                }
                finally
                {
                    running = false;
                }

                if (IsDisposed)
                {
                    return;
                }

                ControlBox = true;
                if (UninstallReport.CloseImmediately(result))
                {
                    // Exit at once: the self-delete helper waits only about a minute for this process to end.
                    Close();
                    return;
                }

                body.Text = string.Join(Environment.NewLine, UninstallReport.Lines(result));
                alsoClient.Visible = false;
                go.Visible = false;
                cancel.Text = UiText.Close;
                cancel.Enabled = true;

                // Close on its own after a while, well inside the self-delete helper's one-minute window.
                var timer = new System.Windows.Forms.Timer { Interval = (int)AutoCloseAfter.TotalMilliseconds };
                timer.Tick += (s2, e2) =>
                {
                    timer.Stop();
                    timer.Dispose();
                    Close();
                };
                timer.Start();
            };
        }
    }
}
