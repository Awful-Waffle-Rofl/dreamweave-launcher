using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using DreamWeave.Launcher.App;
using DreamWeave.Launcher.Core;

namespace DreamWeave.Launcher.UI
{
    /// <summary>The log-in-and-play screen. All decisions are the <see cref="LauncherPresenter"/>'s.</summary>
    internal sealed class LauncherScreen : TableLayoutPanel
    {
        private readonly LauncherPresenter _presenter;
        private readonly ComboBox _account;
        private readonly TextBox _password;
        private readonly CheckBox _remember;
        private readonly Label _rule;
        private readonly Label _help;
        private readonly Label _status;
        private readonly Button _play;
        private readonly TableLayoutPanel _banner;
        private readonly Label _bannerText;
        private readonly Button _update;
        private readonly Button _later;
        private bool _laterClicked;

        public LauncherScreen(LauncherPresenter presenter, bool firstRun, string notice)
        {
            _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
            Dock = DockStyle.Fill;
            ColumnCount = 1;
            Padding = new Padding(UiKit.Gutter);
            AutoScroll = true;
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            GrowStyle = TableLayoutPanelGrowStyle.AddRows;

            AddRow(UiKit.Heading(presenter.ServerName));
            Label address = UiKit.Text(presenter.ServerAddress);
            address.Font = new Font(address.Font.FontFamily, address.Font.Size - 1);
            address.ForeColor = SystemColors.GrayText;
            AddRow(address);
            if (!string.IsNullOrWhiteSpace(notice))
            {
                AddRow(UiKit.Text(notice, true));
            }

            _banner = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill, Visible = false, BackColor = SystemColors.Info, Padding = new Padding(6) };
            _bannerText = UiKit.Text(string.Empty);
            _update = UiKit.Button(UiText.Update, (s, e) => UpdateClicked?.Invoke(this, EventArgs.Empty));
            _later = UiKit.Button(UiText.Later, (s, e) =>
            {
                _laterClicked = true;
                RefreshState();
            });
            _banner.Controls.Add(_bannerText);
            _banner.Controls.Add(UiKit.ButtonRow(_update, _later));
            AddRow(_banner);

            AddRow(UiKit.Text(UiText.AccountLabel));
            _account = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown, MaxLength = 50 };
            AddRow(_account);
            AddRow(UiKit.Text(UiText.PasswordLabel));
            _password = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true, MaxLength = 64 };
            AddRow(_password);
            _remember = new CheckBox { Text = UiText.RememberPassword, AutoSize = true };
            AddRow(_remember);
            _rule = UiKit.Text(string.Empty);
            _rule.ForeColor = Color.Firebrick;
            AddRow(_rule);

            var firstTime = new LinkLabel { Text = UiText.FirstTimeLink, AutoSize = true };
            AddRow(firstTime);
            _help = UiKit.Text(UiText.FirstTimeHelp);
            _help.Visible = firstRun;
            AddRow(_help);
            firstTime.LinkClicked += (s, e) => _help.Visible = !_help.Visible;

            _status = UiKit.Text(string.Empty);
            AddRow(_status);
            Button settings = UiKit.Button(UiText.Settings, (s, e) => SettingsClicked?.Invoke(this, EventArgs.Empty));
            _play = UiKit.Button(UiText.Play, OnPlay);
            AddRow(UiKit.ButtonRow(settings, _play));

            _account.TextChanged += (s, e) => Revalidate();
            _password.TextChanged += (s, e) => Revalidate();
            _account.SelectedIndexChanged += async (s, e) =>
            {
                string name = _account.SelectedItem as string;
                string saved = await _presenter.GetSavedPasswordAsync(name);
                if (IsDisposed || name != _account.SelectedItem as string)
                {
                    return;
                }

                if (saved != null)
                {
                    _password.Text = saved;
                    _remember.Checked = true;
                }
            };
            LoadAccounts();
            RefreshState();
        }

        public event EventHandler UpdateClicked;

        public event EventHandler SettingsClicked;

        /// <summary>Re-reads the presenter (update gate, Play availability).</summary>
        public void RefreshState()
        {
            UpdateGate gate = _presenter.Update;
            bool showBanner = gate.BannerText != null && !(gate.OffersLater && _laterClicked);
            _banner.Visible = showBanner;
            _bannerText.Text = gate.BannerText ?? string.Empty;
            _update.Visible = gate.OffersUpdate;
            _later.Visible = gate.OffersLater;
            _play.Enabled = _presenter.CanPlay;
            _play.Text = _presenter.IsStarting ? UiText.Starting : UiText.Play;
        }

        /// <summary>Shows a status line (plain text).</summary>
        public void ShowStatus(string text)
        {
            _status.Text = text ?? string.Empty;
        }

        private async void LoadAccounts()
        {
            IReadOnlyList<string> accounts = await _presenter.LoadAccountsAsync();
            if (IsDisposed)
            {
                return;
            }

            _account.Items.Clear();
            foreach (string a in accounts)
            {
                _account.Items.Add(a);
            }

            if (accounts.Count > 0 && string.IsNullOrEmpty(_account.Text))
            {
                _account.SelectedIndex = 0;
            }
        }

        private void Revalidate()
        {
            if (_account.Text.Length == 0 && _password.Text.Length == 0)
            {
                _rule.Text = string.Empty;
                return;
            }

            _rule.Text = _account.Text.Length == 0 || _password.Text.Length == 0 ? string.Empty : _presenter.Validate(_account.Text, _password.Text) ?? string.Empty;
        }

        private async void OnPlay(object sender, EventArgs e)
        {
            _status.Text = string.Empty;
            string user = _account.Text;
            string pass = _password.Text;
            bool remember = _remember.Checked;
            Task<PlayResult> play = _presenter.PlayAsync(user, pass, remember);
            RefreshState();
            PlayResult result = await play;
            if (IsDisposed)
            {
                return;
            }

            RefreshState();
            if (result.Error == LauncherError.InvalidCredentials)
            {
                _rule.Text = result.Message;
                return;
            }

            _status.Text = result.Error == LauncherError.None
                ? result.Message + (result.PasswordNotSaved ? " " + ErrorMessages.For(LauncherError.CredentialStoreUnavailable) : string.Empty)
                : result.Message;
            if (result.Error == LauncherError.None && remember && !_account.Items.Contains(user))
            {
                _account.Items.Add(user);
            }
        }

        private void AddRow(Control c)
        {
            RowCount++;
            RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(c, 0, RowCount - 1);
        }
    }
}
