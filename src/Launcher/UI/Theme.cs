using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace DreamWeave.Launcher.UI
{
    /// <summary>Which background art a screen uses.</summary>
    internal enum ScreenArt
    {
        None = 0,
        Welcome = 1,
        Install = 2,
        Launcher = 3,
    }

    /// <summary>
    /// The launcher's colours, fonts and drawing helpers. The colours are the DreamWeave website's
    /// (market.css): ground, panel, input, text, text-2, muted, faint, gold, gold-border, ok, err.
    /// </summary>
    internal static class Theme
    {
        public static readonly Color Ground = ColorTranslator.FromHtml("#191714");
        public static readonly Color Panel = ColorTranslator.FromHtml("#1f1c18");
        public static readonly Color Input = ColorTranslator.FromHtml("#262219");
        public static readonly Color Text = ColorTranslator.FromHtml("#efe9df");
        public static readonly Color Text2 = ColorTranslator.FromHtml("#d9cdb8");
        public static readonly Color Muted = ColorTranslator.FromHtml("#a89f92");
        public static readonly Color Faint = ColorTranslator.FromHtml("#7d7569");
        public static readonly Color Gold = ColorTranslator.FromHtml("#e1ad66");
        public static readonly Color GoldBorder = ColorTranslator.FromHtml("#b68235");
        public static readonly Color Ok = ColorTranslator.FromHtml("#8fbf8a");
        public static readonly Color Err = ColorTranslator.FromHtml("#d97a6a");

        /// <summary>The text colour at 12% alpha.</summary>
        public static readonly Color Hairline = Color.FromArgb(31, Text);

        public const string SerifFamily = "Georgia";
        public const string BodyFamily = "Segoe UI";

        public static readonly Font Body = new Font(BodyFamily, 9.75f, FontStyle.Regular);
        public static readonly Font BodyBold = new Font(BodyFamily, 9.75f, FontStyle.Bold);
        public static readonly Font Small = new Font(BodyFamily, 9f, FontStyle.Regular);
        public static readonly Font Title = new Font(SerifFamily, 20f, FontStyle.Regular);
        public static readonly Font Brand = new Font(SerifFamily, 18f, FontStyle.Regular);
        public static readonly Font Slot = new Font(SerifFamily, 12f, FontStyle.Regular);

        public const TextFormatFlags WrapFlags = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.Top;

        /// <summary><paramref name="c"/> with alpha <paramref name="fraction"/> (0 to 1).</summary>
        public static Color Alpha(Color c, double fraction)
        {
            return Color.FromArgb((int)Math.Round(255 * Math.Max(0, Math.Min(1, fraction))), c);
        }

        /// <summary><paramref name="fg"/> drawn at <paramref name="fraction"/> opacity over <paramref name="bg"/>, as an opaque colour (TextRenderer ignores alpha).</summary>
        public static Color Blend(Color fg, Color bg, double fraction)
        {
            return Color.FromArgb(
                (int)Math.Round(fg.R * fraction + bg.R * (1 - fraction)),
                (int)Math.Round(fg.G * fraction + bg.G * (1 - fraction)),
                (int)Math.Round(fg.B * fraction + bg.B * (1 - fraction)));
        }

        /// <summary><paramref name="c"/> moved <paramref name="fraction"/> of the way to white.</summary>
        public static Color Lighten(Color c, double fraction)
        {
            return Blend(Color.White, c, fraction);
        }

        /// <summary>A rounded rectangle path inside <paramref name="r"/>.</summary>
        public static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = Math.Max(1, radius * 2);
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// <summary>Fills and outlines a rounded rectangle (the outline is inset half a pixel so it is not clipped).</summary>
        public static void FillRound(Graphics g, Rectangle r, int radius, Color fill, Color? border)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle inner = new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1);
            using (GraphicsPath path = RoundRect(inner, radius))
            {
                if (fill.A > 0)
                {
                    using (var b = new SolidBrush(fill))
                    {
                        g.FillPath(b, path);
                    }
                }

                if (border.HasValue)
                {
                    using (var pen = new Pen(border.Value, 1))
                    {
                        g.DrawPath(pen, path);
                    }
                }
            }
        }

        /// <summary>Gives a form the dark theme and the app icon.</summary>
        public static void Apply(Form f)
        {
            f.BackColor = Ground;
            f.ForeColor = Text;
            f.Font = Body;
            Icon icon = Assets.AppIcon;
            if (icon != null)
            {
                f.Icon = icon;
            }

            // A dark title bar where Windows supports it; older builds ignore the attribute.
            f.HandleCreated += (s, e) =>
            {
                try
                {
                    int on = 1;
                    DwmSetWindowAttribute(f.Handle, 20, ref on, sizeof(int));
                }
                catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
                {
                }
            };
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        /// <summary>Styles a text box: flat single border, input fill, light text.</summary>
        public static T StyleInput<T>(T box) where T : TextBoxBase
        {
            box.BorderStyle = BorderStyle.FixedSingle;
            box.BackColor = Input;
            box.ForeColor = Text;
            return box;
        }
    }

    /// <summary>
    /// Margins and paddings written in logical (96 DPI) pixels. They are re-applied, scaled with
    /// <see cref="Control.LogicalToDeviceUnits(int)"/>, whenever the control gets a parent or its DPI changes,
    /// so controls created after the window scale like the ones created with it.
    /// </summary>
    internal static class Spacing
    {
        private sealed class Logical
        {
            public Padding? Margin;
            public Padding? Padding;
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, Logical> Table = new System.Runtime.CompilerServices.ConditionalWeakTable<Control, Logical>();

        public static void SetMargin(Control c, Padding logical)
        {
            Get(c).Margin = logical;
            Apply(c);
        }

        public static void SetPadding(Control c, Padding logical)
        {
            Get(c).Padding = logical;
            Apply(c);
        }

        private static Logical Get(Control c)
        {
            Logical l;
            if (!Table.TryGetValue(c, out l))
            {
                l = new Logical();
                Table.Add(c, l);
                c.ParentChanged += (s, e) => Apply(c);
                c.DpiChangedAfterParent += (s, e) => Apply(c);
            }

            return l;
        }

        private static Padding Scale(Control c, Padding p)
        {
            return new Padding(c.LogicalToDeviceUnits(p.Left), c.LogicalToDeviceUnits(p.Top), c.LogicalToDeviceUnits(p.Right), c.LogicalToDeviceUnits(p.Bottom));
        }

        private static void Apply(Control c)
        {
            Logical l;
            if (!Table.TryGetValue(c, out l))
            {
                return;
            }

            if (l.Margin.HasValue)
            {
                c.Margin = Scale(c, l.Margin.Value);
            }

            if (l.Padding.HasValue)
            {
                c.Padding = Scale(c, l.Padding.Value);
            }
        }
    }
    /// <summary>
    /// The embedded art and icons. Each is decoded once on first use and released by
    /// <see cref="Release"/> when the window that used them closes.
    /// </summary>
    internal static class Assets
    {
        private static readonly object Gate = new object();
        private static readonly Bitmap[] Art = new Bitmap[4];
        private static Bitmap _logo;
        private static Icon _icon;

        /// <summary>The background image for <paramref name="art"/> (null for None or if it cannot be read).</summary>
        public static Bitmap ArtFor(ScreenArt art)
        {
            string name;
            switch (art)
            {
                case ScreenArt.Welcome: name = "art-welcome.jpg"; break;
                case ScreenArt.Install: name = "art-install.jpg"; break;
                case ScreenArt.Launcher: name = "art-launcher.jpg"; break;
                default: return null;
            }

            lock (Gate)
            {
                return Art[(int)art] ?? (Art[(int)art] = LoadBitmap(name));
            }
        }

        /// <summary>The 64 px logo.</summary>
        public static Bitmap Logo
        {
            get
            {
                lock (Gate)
                {
                    return _logo ?? (_logo = LoadBitmap("logo-64.png"));
                }
            }
        }

        /// <summary>The application icon (also the exe's icon), or null if it cannot be read.</summary>
        public static Icon AppIcon
        {
            get
            {
                lock (Gate)
                {
                    if (_icon == null)
                    {
                        using (Stream s = Open("dreamweave.ico"))
                        {
                            if (s != null)
                            {
                                try
                                {
                                    _icon = new Icon(s, 48, 48);
                                }
                                catch (ArgumentException)
                                {
                                    _icon = null;
                                }
                            }
                        }
                    }

                    return _icon;
                }
            }
        }

        /// <summary>Disposes everything loaded so far; a later use loads it again.</summary>
        public static void Release()
        {
            lock (Gate)
            {
                for (int i = 0; i < Art.Length; i++)
                {
                    Art[i]?.Dispose();
                    Art[i] = null;
                }

                _logo?.Dispose();
                _logo = null;
                _icon?.Dispose();
                _icon = null;
            }
        }

        private static Stream Open(string name)
        {
            return typeof(Assets).Assembly.GetManifestResourceStream("Assets." + name);
        }

        private static Bitmap LoadBitmap(string name)
        {
            using (Stream s = Open(name))
            {
                if (s == null)
                {
                    return null;
                }

                // Copied so the bitmap does not depend on the resource stream staying open.
                using (Image img = Image.FromStream(s))
                {
                    var copy = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppPArgb);
                    using (Graphics g = Graphics.FromImage(copy))
                    {
                        g.DrawImage(img, 0, 0, img.Width, img.Height);
                    }

                    return copy;
                }
            }
        }
    }
}
