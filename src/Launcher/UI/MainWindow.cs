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
        private readonly Panel _content;

        public MainWindow()
        {
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = LauncherInfo.ProductName;
            ClientSize = new Size(640, 480);
            MinimumSize = new Size(560, 420);
            StartPosition = FormStartPosition.CenterScreen;

            _content = new Panel { Dock = DockStyle.Fill };
            var footer = new Label
            {
                Text = UiText.Disclaimer,
                AutoSize = false,
                Height = 28,
                Dock = DockStyle.Bottom,
                Padding = new Padding(UiKit.Gutter, 4, UiKit.Gutter, 4),
                ForeColor = SystemColors.GrayText,
                UseMnemonic = false,
            };
            Controls.Add(_content);
            Controls.Add(footer);
        }

        /// <summary>Replaces the content area with <paramref name="screen"/>.</summary>
        public void ShowScreen(Control screen)
        {
            _content.SuspendLayout();
            var old = new Control[_content.Controls.Count];
            _content.Controls.CopyTo(old, 0);
            _content.Controls.Clear();
            foreach (Control c in old)
            {
                c.Dispose();
            }

            screen.Dock = DockStyle.Fill;
            _content.Controls.Add(screen);
            _content.ResumeLayout();
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
