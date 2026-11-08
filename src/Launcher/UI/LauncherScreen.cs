using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using DreamWeave.Launcher.App;
using DreamWeave.Launcher.Core;

namespace DreamWeave.Launcher.UI
{
    /// <summary>
    /// One client row: its name (Main, Alt 1, Alt 2), an account box, a password box and a Play button,
    /// with an inline error line beneath. The account and password boxes are stacked so the row stays
    /// narrow enough for the card.
    /// </summary>
    internal sealed class SlotRow : Control
    {
        private readonly Label _name;
        private readonly TextBox _account;
        private readonly TextBox _password;
        private readonly FlatButton _play;
        private readonly WrapLabel _error;
        private string _validation;
        private string _launch;

        public SlotRow(string name)
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Spacing.SetMargin(this, new Padding(0, 0, 0, 8));
            TabStop = false;
            _name = new Label { Text = name, AutoSize = false, Font = Theme.Slot, ForeColor = Theme.Gold, BackColor = Color.Transparent, UseMnemonic = false, TextAlign = ContentAlignment.MiddleLeft };
            _account = UiKit.Input(UiText.AccountLabel, false, 50);
            _password = UiKit.Input(UiText.PasswordLabel, true, 64);
            _play = UiKit.Button(UiText.Play);
            Spacing.SetMargin(_play, Padding.Empty);
            _error = UiKit.Text(string.Empty);
            _error.ForeColor = Theme.Err;
            Controls.Add(_name);
            Controls.Add(_account);
            Controls.Add(_password);
            Controls.Add(_play);
            Controls.Add(_error);
            _account.TextChanged += (s, e) => OnEdited();
            _password.TextChanged += (s, e) => OnEdited();
            _play.Click += (s, e) => PlayClicked?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Raised when the player edits either box.</summary>
        public event EventHandler Edited;

        /// <summary>Raised when this row's Play is clicked.</summary>
        public event EventHandler PlayClicked;

        public string Account
        {
            get => _account.Text;
            set => _account.Text = value ?? string.Empty;
        }

        public string Password
        {
            get => _password.Text;
            set => _password.Text = value ?? string.Empty;
        }

        public bool PlayEnabled
        {
            get => _play.Enabled;
            set => _play.Enabled = value;
        }

        /// <summary>The inline error from checking the boxes (null: none).</summary>
        public void SetValidation(string text)
        {
            _validation = text;
            ShowError();
        }

        /// <summary>The inline error from the last start attempt (null: none).</summary>
        public void SetLaunchError(string text)
        {
            _launch = text;
            ShowError();
        }

        /// <summary>
        /// Enter inside this row's account or password box plays this row only. <paramref name="boxFocused"/>
        /// says whether one of those boxes has the focus.
        /// </summary>
        internal bool HandleEnter(bool boxFocused)
        {
            if (!boxFocused || !_play.Enabled)
            {
                return false;
            }

            _play.PerformClick();
            return true;
        }

        /// <summary>Gives the account box the keyboard focus.</summary>
        internal void FocusAccountBox()
        {
            _account.Focus();
        }

        internal bool AccountBoxFocused => _account.Focused;

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // Key presses in the child boxes bubble up here before the form's default button sees them.
            if (keyData == Keys.Enter && HandleEnter(_account.Focused || _password.Focused))
            {
                return true;
            }

            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void ShowError()
        {
            string text = _launch ?? _validation ?? string.Empty;
            if (_error.Text == text)
            {
                return;
            }

            _error.Text = text;

            // The row's own height changed: lay out the stack that holds it, so the row grows and the card regrows.
            PerformLayout();
            Parent?.PerformLayout(this, "Error");
        }

        private void OnEdited()
        {
            _launch = null;
            Edited?.Invoke(this, EventArgs.Empty);
        }

        private int BoxHeight => _account.GetPreferredSize(new Size(Width, 0)).Height;

        private int FieldsHeight => Math.Max(BoxHeight, _play.GetPreferredSize(new Size(Width, 0)).Height);

        private int ErrorHeight(int width)
        {
            int h = _error.GetPreferredSize(new Size(Math.Max(1, width), 0)).Height;
            return h > 0 ? h + LogicalToDeviceUnits(2) : 0;
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            int w = proposedSize.Width > 0 && proposedSize.Width < 100000 ? proposedSize.Width : Math.Max(1, Width);
            return new Size(w, FieldsHeight + ErrorHeight(w));
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            int nameW = LogicalToDeviceUnits(46);
            int gap = LogicalToDeviceUnits(6);
            Size play = _play.GetPreferredSize(new Size(Width, 0));
            int fields = FieldsHeight;
            int boxH = BoxHeight;
            int boxY = (fields - boxH) / 2;
            int boxW = Math.Max(1, (Width - nameW - gap - gap - gap - play.Width) / 2);
            _name.SetBounds(0, 0, nameW, fields);
            _account.SetBounds(nameW + gap, boxY, boxW, boxH);
            _password.SetBounds(nameW + gap + boxW + gap, boxY, boxW, boxH);
            _play.SetBounds(Width - play.Width, (fields - play.Height) / 2, play.Width, play.Height);
            int eh = ErrorHeight(Width);
            _error.SetBounds(0, fields + LogicalToDeviceUnits(2), Width, Math.Max(0, eh - LogicalToDeviceUnits(2)));
        }
    }
    /// <summary>The log-in-and-play screen. All decisions are the <see cref="LauncherPresenter"/>'s.</summary>
    internal sealed class LauncherScreen : Page
    {
        /// <summary>The Decal download page (https only), shown beside the "Launch with Decal" box.</summary>
        internal const string DecalDownloadUrl = "https://www.decaldev.com/";

        private readonly LauncherPresenter _presenter;
        private readonly SlotRow[] _rows = new SlotRow[LauncherPresenter.SlotCount];
        private readonly ThemedCheckBox _remember;
        private readonly ThemedCheckBox _decal;
        private readonly TintedBox _info;
        private readonly WrapLabel _status;
        private readonly FlatButton _playAll;
        private readonly TintedBox _banner;
        private readonly WrapLabel _bannerText;
        private readonly FlatButton _update;
        private readonly FlatButton _later;
        private bool _laterClicked;
        private bool _loading = true;

        /// <summary>The three client rows (read by layout tests).</summary>
        internal IReadOnlyList<SlotRow> Rows => _rows;

        /// <summary>The Remember passwords box (read by layout tests).</summary>
        internal ThemedCheckBox RememberBox => _remember;

        /// <summary>The "Launch with Decal" box, or null when Decal was not detected (read by tests).</summary>
        internal ThemedCheckBox DecalBox => _decal;

        /// <summary>Raised when the player toggles "Launch with Decal"; the handler persists the choice.</summary>
        public event EventHandler DecalChanged;

        /// <summary>True once the saved row names have been filled in.</summary>
        internal bool Loaded => !_loading;

        public override IButtonControl DefaultButton => _playAll;

        public LauncherScreen(LauncherPresenter presenter, string notice)
            : base(ScreenArt.Launcher)
        {
            _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
            _presenter.StatusChanged = ShowStatusFromWorker;

            TitleBlock title = UiKit.Heading(presenter.ServerName);
            title.Aside = presenter.ServerAddress;
            Add(title);
            if (!string.IsNullOrWhiteSpace(notice))
            {
                Add(UiKit.Text(notice, true));
            }

            _banner = new TintedBox { Visible = false };
            _bannerText = UiKit.Text(string.Empty);
            _update = UiKit.Button(UiText.Update, (s, e) => UpdateClicked?.Invoke(this, EventArgs.Empty), true);
            _later = UiKit.Button(UiText.Later, (s, e) =>
            {
                _laterClicked = true;
                RefreshState();
            });
            _banner.Controls.Add(_bannerText);
            _banner.Controls.Add(UiKit.ButtonRow(_later, _update));
            Add(_banner);

            _info = new TintedBox();
            _info.Controls.Add(UiKit.Text(UiText.NewHereInfo));
            Add(_info);

            string[] names = { UiText.SlotMain, UiText.SlotAlt1, UiText.SlotAlt2 };
            for (int i = 0; i < _rows.Length; i++)
            {
                int slot = i;
                SlotRow row = new SlotRow(names[i]);
                row.Edited += (s, e) => OnRowEdited(slot);
                row.PlayClicked += (s, e) => OnPlaySlot(slot);
                _rows[i] = row;
                Add(row);
            }

            if (_presenter.DecalAvailable)
            {
                _decal = new ThemedCheckBox { Text = UiText.LaunchWithDecal, Checked = _presenter.LaunchWithDecal };
                _decal.CheckedChanged += (s, e) =>
                {
                    _presenter.LaunchWithDecal = _decal.Checked;
                    DecalChanged?.Invoke(this, EventArgs.Empty);
                };
                LinkLabel decalLink = UiKit.Link(UiText.DecalGetItLink, () => WizardController.OpenUrl(DecalDownloadUrl));
                var decalRow = new FlowLayoutPanel
                {
                    FlowDirection = FlowDirection.LeftToRight,
                    AutoSize = true,
                    WrapContents = false,
                    BackColor = Color.Transparent,
                    Margin = new Padding(0),
                };
                decalRow.Controls.Add(_decal);
                decalRow.Controls.Add(decalLink);
                Add(decalRow);
            }

            _remember = new ThemedCheckBox { Text = UiText.RememberPasswords };
            SetLeft(_remember);
            _status = UiKit.Muted(string.Empty);
            Add(_status);

            FlatButton settings = UiKit.Button(UiText.Settings, (s, e) => SettingsClicked?.Invoke(this, EventArgs.Empty));
            _playAll = UiKit.Button(UiText.PlayAll, OnPlayAll, true);
            SetButtons(settings, _playAll);

            LoadRows();
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
            bool can = _presenter.CanPlay;
            foreach (SlotRow r in _rows)
            {
                r.PlayEnabled = can;
            }

            _playAll.Enabled = can;
            _playAll.Text = _presenter.IsStarting ? UiText.Starting : UiText.PlayAll;
            _info.Visible = !(_rows[0].Account.Length > 0 && _rows[0].Password.Length > 0);
        }

        /// <summary>Shows a status line (plain text).</summary>
        public void ShowStatus(string text)
        {
            _status.Text = text ?? string.Empty;
        }

        /// <summary>A status line from a thread-pool thread (the presenter's lock-release wait).</summary>
        private void ShowStatusFromWorker(string text)
        {
            if (IsDisposed || !IsHandleCreated)
            {
                return;
            }

            try
            {
                BeginInvoke((Action)(() =>
                {
                    if (!IsDisposed)
                    {
                        ShowStatus(text);
                    }
                }));
            }
            catch (InvalidOperationException)
            {
                // The window is closing.
            }
        }

        private async void LoadRows()
        {
            IReadOnlyList<SlotFill> fills = await _presenter.LoadSlotsAsync();
            if (IsDisposed)
            {
                return;
            }

            for (int i = 0; i < _rows.Length && i < fills.Count; i++)
            {
                _rows[i].Account = fills[i].Account;
                if (fills[i].SavedPassword != null)
                {
                    _rows[i].Password = fills[i].SavedPassword;
                    _remember.Checked = true;
                }
            }

            _loading = false;
            Revalidate();
            RefreshState();
        }

        private IReadOnlyList<SlotInput> Inputs()
        {
            return _rows.Select(r => new SlotInput(r.Account, r.Password)).ToList();
        }

        private void OnRowEdited(int slot)
        {
            if (_loading)
            {
                return;
            }

            // Another account's saved password must never be sent for, or saved under, this name.
            if (_presenter.AccountChanged(slot, _rows[slot].Account))
            {
                _rows[slot].Password = string.Empty;
                if (Enumerable.Range(0, _rows.Length).All(i => _presenter.FilledForSlot(i) == null))
                {
                    _remember.Checked = false;
                }
            }

            Revalidate();
            RefreshState();
        }

        private void Revalidate()
        {
            string[] errors = _presenter.ValidateSlots(Inputs());
            for (int i = 0; i < _rows.Length; i++)
            {
                _rows[i].SetValidation(errors[i]);
            }
        }

        private async void OnPlaySlot(int slot)
        {
            _status.Text = string.Empty;
            foreach (SlotRow r in _rows)
            {
                r.SetLaunchError(null);
            }

            Task<SlotResult> play = _presenter.PlaySlotAsync(slot, Inputs(), _remember.Checked);
            RefreshState();
            SlotResult result = await play;
            if (IsDisposed)
            {
                return;
            }

            ShowResults(new[] { result });
            RefreshState();
        }

        private async void OnPlayAll(object sender, EventArgs e)
        {
            _status.Text = string.Empty;
            foreach (SlotRow r in _rows)
            {
                r.SetLaunchError(null);
            }

            Task<IReadOnlyList<SlotResult>> run = _presenter.PlayAllAsync(Inputs(), _remember.Checked, null, null, null, default(System.Threading.CancellationToken));
            RefreshState();
            IReadOnlyList<SlotResult> results = await run;
            if (IsDisposed)
            {
                return;
            }

            if (results.Count == 0)
            {
                _status.Text = UiText.FillInFirst;
            }
            else
            {
                ShowResults(results);
            }

            RefreshState();
        }

        /// <summary>Per-row errors go under their rows; one status line (and one not-saved warning) covers the batch.</summary>
        private void ShowResults(IEnumerable<SlotResult> results)
        {
            bool started = false;
            bool notSaved = false;
            string blocked = null;
            foreach (SlotResult result in results)
            {
                PlayResult r = result.Result;
                if (r.Error == LauncherError.None)
                {
                    started = true;
                    notSaved |= r.PasswordNotSaved;
                    if (!string.IsNullOrEmpty(r.Notice))
                    {
                        _rows[result.Slot].SetLaunchError(r.Notice);
                    }
                }
                else if (r.Error == LauncherError.Cancelled)
                {
                    blocked = r.Message;
                }
                else if (!string.IsNullOrEmpty(r.Message))
                {
                    _rows[result.Slot].SetLaunchError(r.Message);
                }
            }

            if (started)
            {
                _status.Text = UiText.GameStarted + (notSaved ? " " + ErrorMessages.For(LauncherError.CredentialStoreUnavailable) : string.Empty);
            }
            else if (!string.IsNullOrEmpty(blocked))
            {
                _status.Text = blocked;
            }
        }
    }
}