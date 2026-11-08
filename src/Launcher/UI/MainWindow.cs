using System;
using System.Drawing;
using System.Windows.Forms;
using DreamWeave.Launcher.App;
using DreamWeave.Launcher.Core;

namespace DreamWeave.Launcher.UI
{
    /// <summary>
    /// The one top-level window. Screens (wizard pages, the launcher, the loading page) are swapped in
    /// and out of its content area; the footer carries D1. A second launcher start broadcasts
    /// <see cref="SingleInstance.ActivateMessage"/>, which brings this window to the front.
    /// </summary>
    internal sealed class MainWindow : Form
    {
        private const int HeaderHeight = 56;
        private const int FooterHeight = 28;
        private const int CardWidth = 420;
        private const int CardMargin = 20;
        private const int CardPadding = 20;

        private readonly BodyPanel _body;
        private readonly ArtCard _card;
        private readonly HeaderBar _header;
        private Page _screen;

        public MainWindow()
        {
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = LauncherInfo.ProductName;
            ClientSize = new Size(900, 560);
            MinimumSize = new Size(820, 520);
            StartPosition = FormStartPosition.CenterScreen;
            DoubleBuffered = true;
            Theme.Apply(this);

            _body = new BodyPanel { Dock = DockStyle.Fill };
            _card = new ArtCard { Visible = false };
            _body.Controls.Add(_card);
            _body.Resize += (s, e) => PlaceCard();

            var footer = new Label
            {
                Text = UiText.Disclaimer,
                AutoSize = false,
                Height = LogicalToDeviceUnits(FooterHeight),
                Dock = DockStyle.Bottom,
                TextAlign = ContentAlignment.MiddleLeft,
                BackColor = Theme.Ground,
                ForeColor = Theme.Faint,
                UseMnemonic = false,
            };
            Spacing.SetPadding(footer, new Padding(UiKit.Gutter, 0, UiKit.Gutter, 0));
            _header = new HeaderBar { Dock = DockStyle.Top, Height = LogicalToDeviceUnits(HeaderHeight) };
            _header.LinkClicked += WizardController.OpenUrl;
            SetLinks(null);
            Controls.Add(_body);
            Controls.Add(footer);
            Controls.Add(_header);
        }

        /// <summary>Shows Website and Wiki always, and Discord when the manifest carries an invite.</summary>
        public void SetLinks(string discordUrl)
        {
            var links = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, string>>
            {
                new System.Collections.Generic.KeyValuePair<string, string>(UiText.LinkWebsite, UiText.WebsiteUrl),
                new System.Collections.Generic.KeyValuePair<string, string>(UiText.LinkWiki, UiText.WikiUrl),
            };
            if (!string.IsNullOrWhiteSpace(discordUrl))
            {
                links.Add(new System.Collections.Generic.KeyValuePair<string, string>(UiText.LinkDiscord, discordUrl));
            }

            _header.SetLinks(links);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            Assets.Release();
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            _header.Height = LogicalToDeviceUnits(HeaderHeight);
            PlaceCard();
        }

        private void PlaceCard()
        {
            if (_screen == null || _screen.IsDisposed)
            {
                _card.Visible = false;
                return;
            }

            int margin = LogicalToDeviceUnits(CardMargin);
            int pad = LogicalToDeviceUnits(CardPadding);
            int width = LogicalToDeviceUnits(CardWidth);
            int height = WantedCardHeight();
            int x = Math.Max(0, _body.Width - margin - width);
            int y = Math.Max(0, (_body.Height - height) / 2);
            _card.Padding = new Padding(pad + 1);
            _card.SetBounds(x, y, Math.Min(width, _body.Width), height);
            _card.Visible = true;
        }

        /// <summary>While an install runs: what closing the window should do (null: close normally).</summary>
        public Func<CloseDecision> CloseGuard { get; set; }

        /// <summary>Called when the player confirms "Stop the install?"; the owner cancels and closes once the run returns.</summary>
        public Action StopRequested { get; set; }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            CloseDecision decision = CloseGuard?.Invoke() ?? CloseDecision.Allow;
            if (decision == CloseDecision.Block)
            {
                e.Cancel = true;
                MessageBox.Show(this, UiText.CloseBlocked, LauncherInfo.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (decision == CloseDecision.AskToStop)
            {
                e.Cancel = true;
                if (MessageBox.Show(this, UiText.StopInstallPrompt, LauncherInfo.ProductName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    StopRequested?.Invoke();
                }

                return;
            }

            base.OnFormClosing(e);
        }

        /// <summary>Replaces the card's content (and the art behind it) with <paramref name="screen"/>.</summary>
        public void ShowScreen(Page screen)
        {
            _card.SuspendLayout();
            var old = new Control[_card.Controls.Count];
            _card.Controls.CopyTo(old, 0);
            _card.Controls.Clear();
            foreach (Control c in old)
            {
                c.Dispose();
            }

            if (_screen != null)
            {
                _screen.WantedHeightChanged -= OnWantedHeightChanged;
            }

            _screen = screen;
            AcceptButton = screen.DefaultButton;
            _body.Art = screen.Art;
            screen.Dock = DockStyle.Fill;
            screen.WantedHeightChanged += OnWantedHeightChanged;
            _card.Controls.Add(screen);
            PlaceCard();
            _card.ResumeLayout(true);
            _card.Invalidate(true);
        }

        /// <summary>The height the card should have: what the page asks for, capped to the body.</summary>
        internal int WantedCardHeight()
        {
            int margin = LogicalToDeviceUnits(CardMargin);
            int pad = LogicalToDeviceUnits(CardPadding);
            int width = LogicalToDeviceUnits(CardWidth);
            int maxHeight = Math.Max(0, _body.Height - 2 * margin);
            int wanted = _screen.WantedHeight(Math.Max(1, width - 2 * pad - 2)) + 2 * pad + 2;
            return Math.Min(wanted, maxHeight);
        }

        /// <summary>The card (layout tests read its bounds).</summary>
        internal ArtCard Card => _card;

        /// <summary>The page currently shown, or null.</summary>
        internal Page CurrentPage => _screen;

        private void OnWantedHeightChanged(object sender, EventArgs e)
        {
            if (sender == _screen)
            {
                PlaceCard();
            }
        }

        /// <summary>Runs <paramref name="action"/> on the UI thread (no-op once the window is gone).</summary>
        public void Post(Action action)
        {
            if (IsDisposed || !IsHandleCreated)
            {
                return;
            }

            BeginInvoke(action);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == SingleInstance.ActivateMessage && SingleInstance.ActivateMessage != 0)
            {
                if (WindowState == FormWindowState.Minimized)
                {
                    WindowState = FormWindowState.Normal;
                }

                Show();
                Activate();
                BringToFront();
                return;
            }

            base.WndProc(ref m);
        }
    }
}
