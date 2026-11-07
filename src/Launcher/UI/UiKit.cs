using System;
using System.Drawing;
using System.Windows.Forms;
using DreamWeave.Launcher.App;
using DreamWeave.Launcher.Core;

namespace DreamWeave.Launcher.UI
{
    /// <summary>Small factory helpers so every screen lays out the same way.</summary>
    internal static class UiKit
    {
        public const int Gutter = 12;

        /// <summary>A vertical, auto-sizing stack that fills its parent's width.</summary>
        public static TableLayoutPanel Stack()
        {
            var t = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                AutoScroll = true,
                Padding = new Padding(Gutter),
                GrowStyle = TableLayoutPanelGrowStyle.AddRows,
            };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            return t;
        }

        /// <summary>Adds <paramref name="c"/> as the next row of <paramref name="stack"/>.</summary>
        public static T Add<T>(TableLayoutPanel stack, T c) where T : Control
        {
            stack.RowCount++;
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            stack.Controls.Add(c, 0, stack.RowCount - 1);
            return c;
        }

        /// <summary>A wrapping label.</summary>
        public static Label Text(string text, bool bold = false, float sizeDelta = 0)
        {
            var l = new Label
            {
                Text = text ?? string.Empty,
                AutoSize = true,
                MaximumSize = new Size(560, 0),
                Margin = new Padding(0, 4, 0, 4),
                UseMnemonic = false,
            };
            if (bold || sizeDelta != 0)
            {
                l.Font = new Font(l.Font.FontFamily, l.Font.Size + sizeDelta, bold ? FontStyle.Bold : FontStyle.Regular);
            }

            return l;
        }

        /// <summary>A heading label.</summary>
        public static Label Heading(string text)
        {
            return Text(text, true, 3);
        }

        /// <summary>A button sized to its text.</summary>
        public static Button Button(string text, EventHandler onClick = null)
        {
            var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8, 2, 8, 2), UseMnemonic = false };
            if (onClick != null)
            {
                b.Click += onClick;
            }

            return b;
        }

        /// <summary>A right-aligned row of buttons.</summary>
        public static FlowLayoutPanel ButtonRow(params Control[] buttons)
        {
            var f = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                Dock = DockStyle.Fill,
                WrapContents = false,
                Margin = new Padding(0, 8, 0, 0),
            };
            for (int i = buttons.Length - 1; i >= 0; i--)
            {
                if (buttons[i] != null)
                {
                    f.Controls.Add(buttons[i]);
                }
            }

            return f;
        }

        /// <summary>Puts <paramref name="text"/> on the clipboard, ignoring a clipboard held by another program.</summary>
        public static void CopyToClipboard(string text)
        {
            try
            {
                Clipboard.SetText(text);
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
            }
        }
    }

    /// <summary>
    /// The failure panel: the plain message from <see cref="ErrorMessages"/>, then Retry, Try later and
    /// Copy details. Copy details carries the code name, launcher version, step and time only.
    /// </summary>
    internal sealed class ErrorPanel : TableLayoutPanel
    {
        private readonly Label _message;
        private readonly Button _retry;
        private readonly Button _later;
        private LauncherError _code;
        private string _step;
        private DateTime _whenUtc;

        public ErrorPanel()
        {
            Dock = DockStyle.Fill;
            AutoSize = true;
            ColumnCount = 1;
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Visible = false;
            _message = UiKit.Text(string.Empty);
            _message.ForeColor = Color.Firebrick;
            _retry = UiKit.Button(UiText.Retry, (s, e) => RetryClicked?.Invoke(this, EventArgs.Empty));
            _later = UiKit.Button(UiText.TryLater, (s, e) => TryLaterClicked?.Invoke(this, EventArgs.Empty));
            Button copy = UiKit.Button(UiText.CopyDetails, (s, e) =>
            {
                UiKit.CopyToClipboard(ErrorDetails.Build(_code, LauncherInfo.Version, _step, _whenUtc));
                _message.Text = ErrorMessages.For(_code) + " " + UiText.DetailsCopied;
            });
            RowCount = 2;
            RowStyles.Add(new RowStyle(SizeType.AutoSize));
            RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(_message, 0, 0);
            Controls.Add(UiKit.ButtonRow(_retry, _later, copy), 0, 1);
        }

        public event EventHandler RetryClicked;

        public event EventHandler TryLaterClicked;

        /// <summary>Whether the Try later button is shown.</summary>
        public bool ShowTryLater
        {
            get => _later.Visible;
            set => _later.Visible = value;
        }

        /// <summary>Shows <paramref name="code"/> for <paramref name="step"/> (a fixed step or component id).</summary>
        public void ShowError(LauncherError code, string step)
        {
            _code = code;
            _step = step;
            _whenUtc = DateTime.UtcNow;
            _message.Text = ErrorMessages.For(code);
            Visible = true;
        }

        /// <summary>Hides the panel.</summary>
        public void Clear()
        {
            Visible = false;
        }
    }
}
