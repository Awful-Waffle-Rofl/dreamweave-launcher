using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DreamWeave.Launcher.UI
{
    /// <summary>A label that wraps at the width it is given and reports the height that needs.</summary>
    internal class WrapLabel : Label
    {
        public WrapLabel()
        {
            AutoSize = false;
            UseMnemonic = false;
            BackColor = Color.Transparent;
            ForeColor = Theme.Text2;
            Font = Theme.Body;
            Margin = new Padding(0, 2, 0, 2);
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            int w = proposedSize.Width > 0 && proposedSize.Width < 100000 ? proposedSize.Width : Math.Max(1, Width);
            if (string.IsNullOrEmpty(Text))
            {
                return new Size(w, 0);
            }

            Size s = TextRenderer.MeasureText(Text, Font, new Size(w - Padding.Horizontal, int.MaxValue), Theme.WrapFlags);
            return new Size(w, s.Height + Padding.Vertical + 2);
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            Parent?.PerformLayout(this, "Text");
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            Parent?.PerformLayout(this, "Font");
        }
    }

    /// <summary>
    /// A top-to-bottom stack: every visible child gets the full width and its preferred height at that
    /// width (its Margin around it). Children with no height (an empty <see cref="WrapLabel"/>) take no
    /// room. Transparent, so a card or art behind it shows through.
    /// </summary>
    internal class VStack : Panel
    {
        public VStack()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
            Font = Theme.Body;
        }

        /// <summary>The height the children need at <paramref name="width"/>, padding included.</summary>
        public int Measure(int width)
        {
            return Place(width, false);
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            int w = proposedSize.Width > 0 && proposedSize.Width < 100000 ? proposedSize.Width : Math.Max(1, Width);
            return new Size(w, Measure(w));
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            int content = Place(ClientSize.Width, true);
            if (AutoScroll)
            {
                AutoScrollMinSize = new Size(0, content);
            }
        }

        private int Place(int width, bool apply)
        {
            int offset = apply && AutoScroll ? AutoScrollPosition.Y : 0;
            int x = Padding.Left;
            int inner = Math.Max(1, width - Padding.Horizontal);
            int y = Padding.Top;
            foreach (Control c in Controls)
            {
                if (!c.Visible)
                {
                    continue;
                }

                Padding m = c.Margin;
                int cw = Math.Max(1, inner - m.Horizontal);
                int ch = c.GetPreferredSize(new Size(cw, 0)).Height;
                if (ch <= 0)
                {
                    continue;
                }

                if (apply)
                {
                    c.SetBounds(x + m.Left, y + m.Top + offset, cw, ch);
                }

                y += m.Top + ch + m.Bottom;
            }

            return y + Padding.Bottom;
        }
    }

    /// <summary>A stack that draws a hairline under itself (a list row).</summary>
    internal sealed class RowBox : VStack
    {
        public RowBox()
        {
            Padding = new Padding(0, 4, 0, 6);
            Margin = new Padding(0);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Theme.Hairline, 1))
            {
                e.Graphics.DrawLine(pen, 0, ClientSize.Height - 1, ClientSize.Width, ClientSize.Height - 1);
            }
        }
    }

    /// <summary>A stack with a translucent gold tint and a gold border (the "New here?" box, the update banner).</summary>
    internal sealed class TintedBox : VStack
    {
        public TintedBox()
        {
            Padding = new Padding(12, 8, 12, 8);
            Margin = new Padding(0, 4, 0, 8);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme.FillRound(e.Graphics, ClientRectangle, LogicalToDeviceUnits(6), Theme.Alpha(Theme.Gold, 0.10), Theme.GoldBorder);
            base.OnPaint(e);
        }
    }

    /// <summary>A screen title: Georgia 20 pt with a short gold rule beneath.</summary>
    internal sealed class TitleBlock : Control
    {
        private string _aside = string.Empty;

        public TitleBlock(string text)
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
            BackColor = Color.Transparent;
            ForeColor = Theme.Text;
            Font = Theme.Title;
            Text = text ?? string.Empty;
            Margin = new Padding(0, 0, 0, 10);
            TabStop = false;
        }

        /// <summary>Small muted text drawn at the right end of the title line (the server address).</summary>
        public string Aside
        {
            get => _aside;
            set
            {
                _aside = value ?? string.Empty;
                Invalidate();
            }
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            int w = proposedSize.Width > 0 && proposedSize.Width < 100000 ? proposedSize.Width : Math.Max(1, Width);
            Size s = TextRenderer.MeasureText(Text, Font, new Size(w, int.MaxValue), Theme.WrapFlags);
            return new Size(w, s.Height + LogicalToDeviceUnits(10));
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            Parent?.PerformLayout(this, "Text");
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Size s = TextRenderer.MeasureText(Text, Font, new Size(Width, int.MaxValue), Theme.WrapFlags);
            TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(0, 0, Width, s.Height), Theme.Text, Theme.WrapFlags);
            if (_aside.Length > 0)
            {
                Size a = TextRenderer.MeasureText(_aside, Theme.Small, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                TextRenderer.DrawText(e.Graphics, _aside, Theme.Small, new Rectangle(Width - a.Width, s.Height - a.Height - LogicalToDeviceUnits(4), a.Width, a.Height), Theme.Muted, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            }
            int ruleY = s.Height + LogicalToDeviceUnits(5);
            using (var b = new SolidBrush(Theme.Gold))
            {
                e.Graphics.FillRectangle(b, 0, ruleY, LogicalToDeviceUnits(40), Math.Max(2, LogicalToDeviceUnits(2)));
            }
        }
    }

    /// <summary>Owner-drawn flat button: gold primary, hairline secondary.</summary>
    internal sealed class FlatButton : Button
    {
        private bool _hover;
        private bool _down;

        public FlatButton(bool primary)
        {
            Primary = primary;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            SetStyle(ControlStyles.Opaque, false);
            FlatStyle = FlatStyle.Flat;
            BackColor = Color.Transparent;
            Font = Theme.Body;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            UseMnemonic = false;
            UseVisualStyleBackColor = false;
            Margin = new Padding(8, 0, 0, 0);
            Cursor = Cursors.Hand;
        }

        /// <summary>True for the gold, filled call-to-action; false for the outlined button.</summary>
        public bool Primary { get; }

        public override Size GetPreferredSize(Size proposedSize)
        {
            Size text = TextRenderer.MeasureText(Text ?? string.Empty, Primary ? Theme.BodyBold : Theme.Body, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            return new Size(Math.Max(LogicalToDeviceUnits(96), text.Width + LogicalToDeviceUnits(32)), LogicalToDeviceUnits(32));
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hover = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = false;
            _down = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs mevent)
        {
            _down = true;
            Invalidate();
            base.OnMouseDown(mevent);
        }

        protected override void OnMouseUp(MouseEventArgs mevent)
        {
            _down = false;
            Invalidate();
            base.OnMouseUp(mevent);
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            Cursor = Enabled ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            Parent?.PerformLayout(this, "Text");
            Invalidate();
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Rectangle r = ClientRectangle;
            int radius = LogicalToDeviceUnits(4);
            double a = Enabled ? 1.0 : 0.4;
            Color textColor;
            if (Primary)
            {
                Color fill = _down ? Theme.GoldBorder : _hover ? Theme.Lighten(Theme.Gold, 0.08) : Theme.Gold;
                Theme.FillRound(g, r, radius, Theme.Alpha(fill, a), null);
                textColor = Theme.Blend(Theme.Ground, Theme.Gold, a);
            }
            else
            {
                Color wash = _hover || _down ? Theme.Alpha(Theme.Text, _down ? 0.10 : 0.06) : Color.Transparent;
                Theme.FillRound(g, r, radius, wash.A == 0 ? wash : Theme.Alpha(wash, wash.A / 255.0 * a), Theme.Alpha(Theme.Text, 0.12 + (_hover ? 0.10 : 0)));
                textColor = Theme.Blend(Theme.Text2, Theme.Panel, a);
            }

            if (Focused && ShowFocusCues)
            {
                Rectangle f = r;
                f.Inflate(-LogicalToDeviceUnits(3), -LogicalToDeviceUnits(3));
                using (var pen = new Pen(Primary ? Theme.Ground : Theme.Gold, 1) { DashStyle = DashStyle.Dot })
                {
                    g.DrawRectangle(pen, f.X, f.Y, f.Width - 1, f.Height - 1);
                }
            }

            TextRenderer.DrawText(g, Text ?? string.Empty, Primary ? Theme.BodyBold : Theme.Body, r, textColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }
    }

    /// <summary>Owner-drawn check box: wraps its text, gold when ticked.</summary>
    internal sealed class ThemedCheckBox : CheckBox
    {
        private bool _hover;

        public ThemedCheckBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            SetStyle(ControlStyles.Opaque, false);
            FlatStyle = FlatStyle.Flat;
            BackColor = Color.Transparent;
            ForeColor = Theme.Text2;
            Font = Theme.Body;
            AutoSize = false;
            UseMnemonic = false;
            Margin = new Padding(0, 2, 0, 2);
            Cursor = Cursors.Hand;
        }

        private int Box => LogicalToDeviceUnits(16);

        private int TextLeft => Box + LogicalToDeviceUnits(8);

        public override Size GetPreferredSize(Size proposedSize)
        {
            int w = proposedSize.Width > 0 && proposedSize.Width < 100000 ? proposedSize.Width : Math.Max(1, Width);
            Size s = TextRenderer.MeasureText(Text ?? string.Empty, Font, new Size(Math.Max(1, w - TextLeft), int.MaxValue), Theme.WrapFlags);
            return new Size(w, Math.Max(Box, s.Height + 2) + 2);
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            Parent?.PerformLayout(this, "Text");
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hover = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnCheckedChanged(EventArgs e)
        {
            base.OnCheckedChanged(e);
            Invalidate();
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            Cursor = Enabled ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            int box = Box;
            var boxRect = new Rectangle(0, 1, box, box);
            Color edge = !Enabled ? Theme.Faint : Checked ? Theme.Gold : (_hover || Focused) ? Theme.Gold : Theme.Muted;
            Theme.FillRound(g, boxRect, LogicalToDeviceUnits(3), Theme.Input, edge);
            if (Checked)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var pen = new Pen(Enabled ? Theme.Gold : Theme.Faint, Math.Max(2, LogicalToDeviceUnits(2))) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    float u = box / 16f;
                    g.DrawLines(pen, new[] { new PointF(boxRect.X + 3.5f * u, boxRect.Y + 8.5f * u), new PointF(boxRect.X + 6.5f * u, boxRect.Y + 11.5f * u), new PointF(boxRect.X + 12.5f * u, boxRect.Y + 4.5f * u) });
                }
            }

            Color text = Enabled ? ForeColor : Theme.Faint;
            TextRenderer.DrawText(g, Text ?? string.Empty, Font, new Rectangle(TextLeft, 0, Math.Max(1, Width - TextLeft), Height), text, Theme.WrapFlags);
        }
    }

    /// <summary>Owner-drawn progress bar: input track, gold fill, gold-border outline. Fixed height.</summary>
    internal sealed class ThemedBar : Control
    {
        /// <summary>Height of the bar at 96 DPI.</summary>
        public const int LogicalHeight = 10;

        private readonly Timer _timer = new Timer { Interval = 30 };
        private int _value;
        private int _maximum = 100;
        private ProgressBarStyle _style = ProgressBarStyle.Blocks;
        private float _phase;

        public ThemedBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Margin = new Padding(0, 6, 0, 6);
            TabStop = false;
            _timer.Tick += (s, e) =>
            {
                _phase = (_phase + 0.015f) % 1f;
                Invalidate();
            };
        }

        public int Minimum { get; set; }

        public int Maximum
        {
            get => _maximum;
            set
            {
                _maximum = Math.Max(1, value);
                Invalidate();
            }
        }

        public int Value
        {
            get => _value;
            set
            {
                _value = Math.Max(Minimum, Math.Min(Maximum, value));
                Invalidate();
            }
        }

        public ProgressBarStyle Style
        {
            get => _style;
            set
            {
                _style = value;
                UpdateTimer();
                Invalidate();
            }
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            int w = proposedSize.Width > 0 && proposedSize.Width < 100000 ? proposedSize.Width : Math.Max(1, Width);
            return new Size(w, LogicalToDeviceUnits(LogicalHeight));
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            UpdateTimer();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            _timer.Stop();
            base.OnHandleDestroyed(e);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            UpdateTimer();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer.Stop();
                _timer.Dispose();
            }

            base.Dispose(disposing);
        }

        private void UpdateTimer()
        {
            if (_style == ProgressBarStyle.Marquee && Visible && IsHandleCreated)
            {
                _timer.Start();
            }
            else
            {
                _timer.Stop();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Rectangle r = new Rectangle(0, 0, Width, Height);
            int radius = Math.Max(2, Height / 3);
            Theme.FillRound(g, r, radius, Theme.Input, Theme.GoldBorder);
            Rectangle inner = Rectangle.Inflate(r, -2, -2);
            if (inner.Width <= 0 || inner.Height <= 0)
            {
                return;
            }

            Rectangle fill;
            if (_style == ProgressBarStyle.Marquee)
            {
                int seg = Math.Max(inner.Width / 4, 8);
                int travel = inner.Width + seg;
                int x = inner.X - seg + (int)(travel * _phase);
                int left = Math.Max(inner.X, x);
                int right = Math.Min(inner.Right, x + seg);
                fill = right > left ? new Rectangle(left, inner.Y, right - left, inner.Height) : Rectangle.Empty;
            }
            else
            {
                double fraction = (double)(_value - Minimum) / Math.Max(1, Maximum - Minimum);
                fill = new Rectangle(inner.X, inner.Y, (int)Math.Round(inner.Width * fraction), inner.Height);
            }

            if (fill.Width > 0)
            {
                Theme.FillRound(g, new Rectangle(fill.X, fill.Y, fill.Width + 1, fill.Height + 1), Math.Max(1, radius - 2), Theme.Gold, null);
            }
        }
    }

    /// <summary>One line of a list: a title on the left, a coloured status on the right, a hairline beneath.</summary>
    internal sealed class StatusRow : Control
    {
        private string _title = string.Empty;
        private string _status = string.Empty;
        private Color _statusColor = Theme.Muted;

        public StatusRow()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Font = Theme.Body;
            Margin = new Padding(0);
            TabStop = false;
        }

        public string Title
        {
            get => _title;
            set
            {
                _title = value ?? string.Empty;
                Invalidate();
            }
        }

        public string Status
        {
            get => _status;
            set
            {
                _status = value ?? string.Empty;
                Invalidate();
            }
        }

        public Color StatusColor
        {
            get => _statusColor;
            set
            {
                _statusColor = value;
                Invalidate();
            }
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            int w = proposedSize.Width > 0 && proposedSize.Width < 100000 ? proposedSize.Width : Math.Max(1, Width);
            return new Size(w, TextRenderer.MeasureText("Ag", Font).Height + LogicalToDeviceUnits(14));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
            Rectangle body = new Rectangle(0, 0, Width, Height - 1);
            int statusW = Math.Min(TextRenderer.MeasureText(_status, Font, new Size(int.MaxValue, int.MaxValue), flags).Width, body.Width * 2 / 3);
            TextRenderer.DrawText(g, _status, Font, new Rectangle(body.Right - statusW, 0, statusW, body.Height), _statusColor, flags | TextFormatFlags.Right);
            TextRenderer.DrawText(g, _title, Font, new Rectangle(0, 0, Math.Max(1, body.Width - statusW - LogicalToDeviceUnits(12)), body.Height), Theme.Text2, flags);
            using (var pen = new Pen(Theme.Hairline, 1))
            {
                g.DrawLine(pen, 0, Height - 1, Width, Height - 1);
            }
        }
    }

    /// <summary>The window's top band: logo, product name and the Website / Wiki / Discord links.</summary>
    internal sealed class HeaderBar : Control
    {
        private sealed class Item
        {
            public string Text;
            public string Url;
            public Rectangle Bounds;
        }

        private readonly List<Item> _items = new List<Item>();
        private Item _hot;

        public HeaderBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Panel;
            TabStop = false;
        }

        /// <summary>Raised with the link's address when one is clicked.</summary>
        public event Action<string> LinkClicked;

        /// <summary>Replaces the links (text and https address pairs).</summary>
        public void SetLinks(IEnumerable<KeyValuePair<string, string>> links)
        {
            _items.Clear();
            foreach (KeyValuePair<string, string> l in links)
            {
                _items.Add(new Item { Text = l.Key, Url = l.Value });
            }

            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Item hit = _items.Find(i => i.Bounds.Contains(e.Location));
            if (hit != _hot)
            {
                _hot = hit;
                Cursor = hit != null ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hot != null)
            {
                _hot = null;
                Cursor = Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            Item hit = _items.Find(i => i.Bounds.Contains(e.Location));
            if (hit != null && e.Button == MouseButtons.Left)
            {
                LinkClicked?.Invoke(hit.Url);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Panel);
            using (var pen = new Pen(Theme.GoldBorder, 1))
            {
                g.DrawLine(pen, 0, Height - 1, Width, Height - 1);
            }

            int pad = LogicalToDeviceUnits(20);
            int logo = LogicalToDeviceUnits(36);
            int logoY = (Height - 1 - logo) / 2;
            Bitmap img = Assets.Logo;
            if (img != null)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(img, new Rectangle(pad, logoY, logo, logo));
            }

            int textX = pad + logo + LogicalToDeviceUnits(12);
            TextRenderer.DrawText(g, "DreamWeave", Theme.Brand, new Rectangle(textX, 0, Width, Height - 1), Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);

            int right = Width - pad;
            int gap = LogicalToDeviceUnits(22);
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                Item it = _items[i];
                Font f = it == _hot ? new Font(Theme.Body, FontStyle.Underline) : Theme.Body;
                Size s = TextRenderer.MeasureText(it.Text, f, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                it.Bounds = new Rectangle(right - s.Width, (Height - 1 - s.Height) / 2, s.Width, s.Height);
                TextRenderer.DrawText(g, it.Text, f, it.Bounds, Theme.Gold, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                if (f != Theme.Body)
                {
                    f.Dispose();
                }

                right -= s.Width + gap;
            }
        }
    }

    /// <summary>
    /// The area between header and footer: paints the current screen's art ("cover", anchored left and
    /// centred) with a left-to-right ground wash and a bottom fade, and hosts the card.
    /// </summary>
    internal sealed class BodyPanel : Panel
    {
        private ScreenArt _art = ScreenArt.None;
        private Bitmap _cache;

        public BodyPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Ground;
        }

        public ScreenArt Art
        {
            get => _art;
            set
            {
                if (_art != value)
                {
                    _art = value;
                    DropCache();
                    Invalidate();
                }
            }
        }

        /// <summary>Paints the part of the backdrop under <paramref name="area"/> (body coordinates) at the origin of <paramref name="g"/>.</summary>
        public void PaintBackdrop(Graphics g, Rectangle area)
        {
            EnsureCache();
            if (_cache == null)
            {
                g.Clear(Theme.Ground);
                return;
            }

            g.DrawImage(_cache, new Rectangle(0, 0, area.Width, area.Height), area, GraphicsUnit.Pixel);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            EnsureCache();
            if (_cache != null)
            {
                e.Graphics.DrawImageUnscaled(_cache, 0, 0);
            }
            else
            {
                e.Graphics.Clear(Theme.Ground);
            }
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            DropCache();
            base.OnSizeChanged(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DropCache();
            }

            base.Dispose(disposing);
        }

        private void DropCache()
        {
            _cache?.Dispose();
            _cache = null;
        }

        private void EnsureCache()
        {
            if (_cache != null || Width <= 0 || Height <= 0)
            {
                return;
            }

            var bmp = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Theme.Ground);
                Bitmap art = Assets.ArtFor(_art);
                if (art != null)
                {
                    double scale = Math.Max((double)Width / art.Width, (double)Height / art.Height);
                    int w = (int)Math.Ceiling(art.Width * scale);
                    int h = (int)Math.Ceiling(art.Height * scale);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(art, new Rectangle(0, (Height - h) / 2, w, h));
                }

                var all = new Rectangle(0, 0, Width, Height);
                using (var wash = new LinearGradientBrush(all, Theme.Alpha(Theme.Ground, 0.15), Theme.Alpha(Theme.Ground, 0.80), LinearGradientMode.Horizontal))
                {
                    g.FillRectangle(wash, all);
                }

                int fadeH = Math.Max(1, Height * 2 / 5);
                var fade = new Rectangle(0, Height - fadeH, Width, fadeH);
                using (var bottom = new LinearGradientBrush(new Rectangle(fade.X, fade.Y - 1, fade.Width, fade.Height + 2), Theme.Alpha(Theme.Ground, 0), Theme.Ground, LinearGradientMode.Vertical))
                {
                    g.FillRectangle(bottom, fade);
                }
            }

            _cache = bmp;
        }
    }

    /// <summary>The translucent card on the art. Paints the art behind itself, then its own fill, so transparent children show the card.</summary>
    internal sealed class ArtCard : Panel
    {
        public ArtCard()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint, true);
            BackColor = Theme.Panel;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (Parent is BodyPanel body)
            {
                body.PaintBackdrop(e.Graphics, Bounds);
            }
            else
            {
                e.Graphics.Clear(Theme.Ground);
            }

            Theme.FillRound(e.Graphics, ClientRectangle, LogicalToDeviceUnits(8), Theme.Alpha(Theme.Panel, 0.88), Theme.GoldBorder);
        }
    }

    /// <summary>
    /// One screen's content: a body (a <see cref="VStack"/>) and a button row pinned to the bottom,
    /// plus the art it wants behind it.
    /// </summary>
    internal class Page : Panel
    {
        private int _reported = -1;

        public Page(ScreenArt art)
        {
            Art = art;
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = Color.Transparent;
            Body = new VStack();
            Footer = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                BackColor = Color.Transparent,
                Margin = new Padding(0),
            };
            Controls.Add(Body);
            Controls.Add(Footer);
            Body.Layout += (s, e) => ReportWanted();
        }

        public ScreenArt Art { get; }

        private Control _left;

        /// <summary>A control pinned to the left end of the button row (a link or a check box).</summary>
        public void SetLeft(Control c)
        {
            if (_left != null)
            {
                Controls.Remove(_left);
            }

            _left = c;
            if (c != null)
            {
                Controls.Add(c);
            }

            PerformLayout();
        }

        public VStack Body { get; }

        public FlowLayoutPanel Footer { get; }

        /// <summary>Raised when the height this screen wants changes.</summary>
        public event EventHandler WantedHeightChanged;

        /// <summary>Adds a row to the body.</summary>
        public T Add<T>(T c) where T : Control
        {
            Body.Controls.Add(c);
            return c;
        }

        /// <summary>Sets the navigation buttons, left to right as given; the last is rightmost.</summary>
        public void SetButtons(params Control[] buttons)
        {
            Footer.Controls.Clear();
            for (int i = buttons.Length - 1; i >= 0; i--)
            {
                if (buttons[i] != null)
                {
                    Footer.Controls.Add(buttons[i]);
                }
            }

            PerformLayout();
        }

        /// <summary>The height the content needs at <paramref name="width"/>.</summary>
        public int WantedHeight(int width)
        {
            int footer = FooterHeight(width);
            return Body.Measure(width) + (footer > 0 ? footer + LogicalToDeviceUnits(8) : 0);
        }

        private int FooterWidth()
        {
            return Footer.Controls.Count == 0 ? 0 : Footer.GetPreferredSize(new Size(0, 0)).Width;
        }

        private int LeftWidth(int width)
        {
            return Math.Max(1, width - FooterWidth() - LogicalToDeviceUnits(12));
        }

        private int FooterHeight(int width)
        {
            int h = Footer.Controls.Count == 0 ? 0 : Footer.GetPreferredSize(new Size(0, 0)).Height;
            if (_left != null && _left.Visible)
            {
                h = Math.Max(h, _left.GetPreferredSize(new Size(LeftWidth(width), 0)).Height);
            }

            return h;
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            int w = ClientSize.Width;
            int fh = FooterHeight(w);
            int fw = FooterWidth();
            Footer.SetBounds(w - fw, ClientSize.Height - fh, fw, Footer.Controls.Count == 0 ? 0 : Footer.GetPreferredSize(new Size(0, 0)).Height);
            if (_left != null)
            {
                int lw = LeftWidth(w);
                int lh = _left.GetPreferredSize(new Size(lw, 0)).Height;
                _left.SetBounds(0, ClientSize.Height - fh + (fh - lh) / 2, lw, lh);
            }
            Body.SetBounds(0, 0, w, Math.Max(0, ClientSize.Height - fh - (fh > 0 ? LogicalToDeviceUnits(8) : 0)));
            ReportWanted();
        }

        private void ReportWanted()
        {
            int wanted = WantedHeight(ClientSize.Width);
            if (wanted != _reported)
            {
                _reported = wanted;
                WantedHeightChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>A heading and a few lines, each led by a drawn gold bullet.</summary>
    internal sealed class Checklist : Control
    {
        private readonly string _heading;
        private readonly string[] _items;

        public Checklist(string heading, params string[] items)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Font = Theme.Body;
            _heading = heading ?? string.Empty;
            _items = items ?? new string[0];
            Margin = new Padding(0, 6, 0, 6);
            TabStop = false;
        }

        private int Indent => LogicalToDeviceUnits(20);

        private int HeadingHeight => TextRenderer.MeasureText("Ag", Theme.BodyBold).Height + LogicalToDeviceUnits(6);

        public override Size GetPreferredSize(Size proposedSize)
        {
            int w = proposedSize.Width > 0 && proposedSize.Width < 100000 ? proposedSize.Width : Math.Max(1, Width);
            int h = _heading.Length > 0 ? HeadingHeight : 0;
            foreach (string item in _items)
            {
                h += ItemHeight(item, w) + LogicalToDeviceUnits(3);
            }

            return new Size(w, h);
        }

        private int ItemHeight(string item, int width)
        {
            return TextRenderer.MeasureText(item, Font, new Size(Math.Max(1, width - Indent), int.MaxValue), Theme.WrapFlags).Height + 2;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            int y = 0;
            if (_heading.Length > 0)
            {
                TextRenderer.DrawText(g, _heading, Theme.BodyBold, new Rectangle(0, 0, Width, HeadingHeight), Theme.Gold, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                y = HeadingHeight;
            }

            g.SmoothingMode = SmoothingMode.AntiAlias;
            int dot = Math.Max(5, LogicalToDeviceUnits(6));
            foreach (string item in _items)
            {
                int h = ItemHeight(item, Width);
                int line = TextRenderer.MeasureText("Ag", Font).Height;
                using (var b = new SolidBrush(Theme.Gold))
                {
                    g.FillEllipse(b, 1, y + (line - dot) / 2 + 1, dot, dot);
                }

                TextRenderer.DrawText(g, item, Font, new Rectangle(Indent, y, Math.Max(1, Width - Indent), h), Theme.Text2, Theme.WrapFlags);
                y += h + LogicalToDeviceUnits(3);
            }
        }
    }

    /// <summary>A text box with a button to its right, on one line.</summary>
    internal sealed class PathRow : Control
    {
        private readonly TextBox _box;
        private readonly Button _button;

        public PathRow(TextBox box, Button button)
        {
            _box = box;
            _button = button;
            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Margin = new Padding(0, 6, 0, 6);
            TabStop = false;
            Controls.Add(_box);
            Controls.Add(_button);
        }

        private int RowHeight(int width)
        {
            return Math.Max(_box.GetPreferredSize(new Size(width, 0)).Height, _button.GetPreferredSize(new Size(width, 0)).Height);
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            int w = proposedSize.Width > 0 && proposedSize.Width < 100000 ? proposedSize.Width : Math.Max(1, Width);
            return new Size(w, RowHeight(w));
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            int h = RowHeight(Width);
            Size b = _button.GetPreferredSize(new Size(Width, 0));
            _button.Margin = new Padding(0);
            _button.SetBounds(Width - b.Width, (h - b.Height) / 2, b.Width, b.Height);
            int gap = LogicalToDeviceUnits(8);
            int boxH = _box.GetPreferredSize(new Size(Width, 0)).Height;
            _box.SetBounds(0, (h - boxH) / 2, Math.Max(1, Width - b.Width - gap), boxH);
        }
    }
}