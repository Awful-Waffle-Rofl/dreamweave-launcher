using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using DreamWeave.Launcher.App;
using DreamWeave.Launcher.Core;

namespace DreamWeave.Launcher.UI
{
    /// <summary>Small factory helpers so every screen lays out and looks the same.</summary>
    internal static class UiKit
    {
        public const int Gutter = 20;

        /// <summary>A themed vertical stack that fills its parent (used by the plain dialogs).</summary>
        public static VStack Stack()
        {
            return new VStack { Dock = DockStyle.Fill, Padding = new Padding(Gutter) };
        }

        /// <summary>Adds <paramref name="c"/> as the next row of <paramref name="stack"/>.</summary>
        public static T Add<T>(VStack stack, T c) where T : Control
        {
            stack.Controls.Add(c);
            return c;
        }

        /// <summary>A new wizard/launcher screen with the art it wants behind its card.</summary>
        public static Page NewScreen(ScreenArt art)
        {
            return new Page(art);
        }

        /// <summary>
        /// A full-width progress bar of fixed height (<see cref="ThemedBar.LogicalHeight"/> scaled to the
        /// DPI of the window it is in). Never docked to fill: that renders as a block as tall as the window.
        /// </summary>
        public static ThemedBar Bar(ProgressBarStyle style = ProgressBarStyle.Blocks, int maximum = 100)
        {
            return new ThemedBar { Style = style, Maximum = maximum };
        }

        /// <summary>A wrapping label (body text colour).</summary>
        public static WrapLabel Text(string text, bool bold = false, float sizeDelta = 0)
        {
            var l = new WrapLabel { Text = text ?? string.Empty };
            if (bold || sizeDelta != 0)
            {
                l.Font = new Font(Theme.BodyFamily, 9.75f + sizeDelta, bold ? FontStyle.Bold : FontStyle.Regular);
            }

            return l;
        }

        /// <summary>A wrapping label in the secondary (muted) colour.</summary>
        public static WrapLabel Muted(string text)
        {
            var l = Text(text);
            l.ForeColor = Theme.Muted;
            return l;
        }

        /// <summary>A screen title with the gold rule beneath.</summary>
        public static TitleBlock Heading(string text)
        {
            return new TitleBlock(text);
        }

        /// <summary>A primary (gold) or secondary (outlined) button sized to its text.</summary>
        public static FlatButton Button(string text, EventHandler onClick = null, bool primary = false)
        {
            var b = new FlatButton(primary) { Text = text };
            if (onClick != null)
            {
                b.Click += onClick;
            }

            return b;
        }

        /// <summary>A text link in the accent colour.</summary>
        public static LinkLabel Link(string text, Action onClick)
        {
            var l = new LinkLabel
            {
                Text = text,
                AutoSize = true,
                BackColor = Color.Transparent,
                LinkColor = Theme.Gold,
                ActiveLinkColor = Theme.Lighten(Theme.Gold, 0.2),
                VisitedLinkColor = Theme.Gold,
                LinkBehavior = LinkBehavior.HoverUnderline,
                UseMnemonic = false,
                Font = Theme.Body,
                Margin = new Padding(0, 4, 0, 4),
            };
            l.LinkClicked += (s, e) => onClick();
            return l;
        }

        /// <summary>A text box with a button to its right.</summary>
        public static PathRow PathInput(TextBox box, Button button)
        {
            return new PathRow(box, button);
        }

        /// <summary>A right-aligned row of buttons; the last one is rightmost.</summary>
        public static FlowLayoutPanel ButtonRow(params Control[] buttons)
        {
            var f = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                WrapContents = false,
                BackColor = Color.Transparent,
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

        /// <summary>A themed text box.</summary>
        public static TextBox Input(string cue = null, bool password = false, int maxLength = 0)
        {
            var t = Theme.StyleInput(new TextBox { Font = Theme.Body, Margin = new Padding(0) });
            t.UseSystemPasswordChar = password;
            if (maxLength > 0)
            {
                t.MaxLength = maxLength;
            }

            if (!string.IsNullOrEmpty(cue))
            {
                t.HandleCreated += (s, e) => SendMessage(t.Handle, EmSetCueBanner, new IntPtr(1), cue);
                if (t.IsHandleCreated)
                {
                    SendMessage(t.Handle, EmSetCueBanner, new IntPtr(1), cue);
                }
            }

            return t;
        }

        private const int EmSetCueBanner = 0x1501;

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

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
    internal sealed class ErrorPanel : VStack
    {
        private readonly WrapLabel _message;
        private readonly FlatButton _retry;
        private readonly FlatButton _later;
        private LauncherError _code;
        private string _step;
        private DateTime _whenUtc;
        private IReadOnlyList<string> _files;
        private string _transport;

        public ErrorPanel()
        {
            Visible = false;
            Margin = new Padding(0);
            _message = UiKit.Text(string.Empty);
            _message.ForeColor = Theme.Err;
            _retry = UiKit.Button(UiText.Retry, (s, e) => RetryClicked?.Invoke(this, EventArgs.Empty), true);
            _later = UiKit.Button(UiText.TryLater, (s, e) => TryLaterClicked?.Invoke(this, EventArgs.Empty));
            Button copy = UiKit.Button(UiText.CopyDetails, (s, e) =>
            {
                UiKit.CopyToClipboard(ErrorDetails.Build(_code, LauncherInfo.Version, _step, _whenUtc, _files));
                _message.Text = ErrorMessages.For(_code, _transport) + " " + UiText.DetailsCopied;
            });
            Controls.Add(_message);
            Controls.Add(UiKit.ButtonRow(_later, copy, _retry));
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
        public void ShowError(LauncherError code, string step, IReadOnlyList<string> files = null, string sourceTransport = null)
        {
            _files = files;
            _transport = sourceTransport;
            _code = code;
            _step = step;
            _whenUtc = DateTime.UtcNow;
            _message.Text = ErrorMessages.For(code, sourceTransport);
            Visible = true;
        }

        /// <summary>Hides the panel.</summary>
        public void Clear()
        {
            Visible = false;
        }
    }
}
