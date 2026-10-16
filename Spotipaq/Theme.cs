// Theme.cs - colours, fonts and shared drawing helpers (Spotify 1.0-era dark theme, green accent).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Spotipaq
{
    public static class Theme
    {
        public static readonly Color Accent = Color.FromArgb(29, 185, 84);        // #1DB954
        public static readonly Color AccentHover = Color.FromArgb(30, 215, 96);   // #1ED760
        public static readonly Color Main = Color.FromArgb(24, 24, 24);           // content area
        public static readonly Color Sidebar = Color.FromArgb(18, 18, 18);
        public static readonly Color Bar = Color.FromArgb(40, 40, 40);            // top + bottom bars
        public static readonly Color RowHover = Color.FromArgb(51, 51, 51);
        public static readonly Color RowSelected = Color.FromArgb(64, 64, 64);
        public static readonly Color Line = Color.FromArgb(40, 40, 40);
        public static readonly Color Text = Color.White;
        public static readonly Color SubText = Color.FromArgb(160, 160, 160);
        public static readonly Color DimText = Color.FromArgb(110, 110, 110);
        public static readonly Color Placeholder = Color.FromArgb(62, 62, 62);

        // Text: Segoe UI Variable's "Text" optical size; big titles: its "Display" size. GDI+ (.NET 2.0) can't pick
        // variable-font axes, but the named instances appear as separate families. Fallbacks only matter on
        // machines without it.
        public static readonly string FontFamilyName = PickFamily("Segoe UI Variable Text", "Segoe UI Variable", "Segoe UI", "Tahoma");
        public static readonly string DisplayFamilyName = PickFamily("Segoe UI Variable Display", "Segoe UI Variable", "Segoe UI", "Tahoma");
        public static readonly Font Small = Make(8.25f, FontStyle.Regular);
        public static readonly Font SmallBold = Make(8.25f, FontStyle.Bold);
        public static readonly Font Normal = Make(9f, FontStyle.Regular);
        public static readonly Font NormalBold = Make(9f, FontStyle.Bold);
        public static readonly Font Heading = Make(12f, FontStyle.Bold);
        public static readonly Font Huge = new Font(DisplayFamilyName, 20f, FontStyle.Bold);
        public static readonly Font Label = Make(7.5f, FontStyle.Bold);           // "ALBUM", section headers

        static string PickFamily(params string[] wanted)
        {
            Dictionary<string, bool> have = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            using (InstalledFontCollection fonts = new InstalledFontCollection())
                foreach (FontFamily f in fonts.Families) have[f.Name] = true;
            foreach (string name in wanted) if (have.ContainsKey(name)) return name;
            return "Tahoma";
        }
        static Font Make(float size, FontStyle style) { return new Font(FontFamilyName, size, style); }

        public const TextFormatFlags LeftEllipsis = TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

        public static void Text1(Graphics g, string s, Font f, Rectangle r, Color c)
        {
            Draw(g, s, f, r, c, LeftEllipsis);
        }

        // ---- text with symbol / emoji fallback ----
        // Segoe UI Variable has no emoji or symbols. Plain strings go through TextRenderer (fast, crisp);
        // strings containing such characters are split into runs and the symbol runs are drawn with GDI+,
        // which does its own glyph fallback. Optionally put a font name in %APPDATA%\Spotipaq\symbolfont.txt.
        static readonly string SymbolFamilyName = PickSymbolFamily();
        static readonly Dictionary<Font, Font> _symFonts = new Dictionary<Font, Font>();

        static string PickSymbolFamily()
        {
            string wanted = null;
            try
            {
                string f = System.IO.Path.Combine(Spotipaq.Engine.Log.Folder, "symbolfont.txt");
                if (System.IO.File.Exists(f)) wanted = System.IO.File.ReadAllText(f).Trim();
            }
            catch { }
            string pick;
            if (!String.IsNullOrEmpty(wanted)) pick = PickFamily(wanted, "Segoe UI Emoji", "Segoe UI Symbol", "Arial Unicode MS", "Lucida Sans Unicode");
            else pick = PickFamily("Segoe UI Emoji", "Segoe UI Symbol", "Arial Unicode MS", "Lucida Sans Unicode");
            try { Spotipaq.Engine.Log.Write("Symbol/emoji font: " + pick); } catch { }
            return pick;
        }

        static bool IsSpecial(char c)
        {
            return (c >= 0xD800 && c <= 0xDFFF) || (c >= 0x2190 && c <= 0x2BFF) || (c >= 0x2E80 && c < 0xD800) ||
                   (c >= 0xFE00 && c <= 0xFE0F) || c == 0x200D || (c >= 0x2600 && c <= 0x27BF);
        }

        static bool IsInvisible(char c) { return (c >= 0xFE00 && c <= 0xFE0F) || c == 0x200D; }

        static bool HasSpecial(string s)
        {
            for (int i = 0; i < s.Length; i++) if (IsSpecial(s[i])) return true;
            return false;
        }

        static Font SymbolFont(Font f)
        {
            Font r;
            lock (_symFonts)
            {
                if (!_symFonts.TryGetValue(f, out r))
                {
                    try { r = new Font(SymbolFamilyName, f.Size, f.Style); } catch { r = f; }
                    _symFonts[f] = r;
                }
            }
            return r;
        }

        sealed class Run { public string Text; public bool Special; public int Width; }

        static int RunWidth(Graphics g, string s, Font f, bool special)
        {
            if (s.Length == 0) return 0;
            if (!special) return TextRenderer.MeasureText(g, s, f, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width;
            using (StringFormat sf = new StringFormat(StringFormat.GenericTypographic))
            {
                sf.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                return (int)Math.Ceiling(g.MeasureString(s, SymbolFont(f), PointF.Empty, sf).Width);
            }
        }

        static List<Run> SplitRuns(Graphics g, string text, Font f)
        {
            List<Run> runs = new List<Run>();
            StringBuilder sb = new StringBuilder();
            bool special = false;
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (IsInvisible(ch)) continue;
                bool sp = IsSpecial(ch);
                if (sp != special && sb.Length > 0) { runs.Add(new Run { Text = sb.ToString(), Special = special }); sb.Length = 0; }
                special = sp;
                sb.Append(ch);
            }
            if (sb.Length > 0) runs.Add(new Run { Text = sb.ToString(), Special = special });
            foreach (Run r in runs) r.Width = RunWidth(g, r.Text, f, r.Special);
            return runs;
        }

        public static Size Measure(string text, Font f)
        {
            if (String.IsNullOrEmpty(text) || !HasSpecial(text)) return TextRenderer.MeasureText(text ?? "", f);
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
            {
                int w = 4;
                foreach (Run r in SplitRuns(g, text, f)) w += r.Width;
                return new Size(w, TextRenderer.MeasureText("Ag", f).Height);
            }
        }

        public static void Draw(Graphics g, string text, Font f, Rectangle r, Color c, TextFormatFlags flags)
        {
            if (String.IsNullOrEmpty(text) || !HasSpecial(text)) { TextRenderer.DrawText(g, text ?? "", f, r, c, flags); return; }

            List<Run> runs = SplitRuns(g, text, f);
            int total = 0;
            foreach (Run run in runs) total += run.Width;
            int pad = 2;
            int avail = r.Width - pad * 2;
            if (total > avail && (flags & TextFormatFlags.EndEllipsis) != 0)
            {
                int ell = RunWidth(g, "...", f, false);
                int budget = avail - ell, used = 0;
                List<Run> kept = new List<Run>();
                foreach (Run run in runs)
                {
                    if (used + run.Width <= budget) { kept.Add(run); used += run.Width; continue; }
                    string s = run.Text;
                    while (s.Length > 0 && used + RunWidth(g, s, f, run.Special) > budget)
                    {
                        s = s.Substring(0, s.Length - 1);
                        if (s.Length > 0 && Char.IsHighSurrogate(s[s.Length - 1])) s = s.Substring(0, s.Length - 1);
                    }
                    if (s.Length > 0) { Run part = new Run(); part.Text = s; part.Special = run.Special; part.Width = RunWidth(g, s, f, run.Special); kept.Add(part); used += part.Width; }
                    break;
                }
                Run dots = new Run(); dots.Text = "..."; dots.Special = false; dots.Width = ell; kept.Add(dots);
                runs = kept; total = used + ell;
            }
            int x = r.Left + pad;
            if ((flags & TextFormatFlags.Right) != 0) x = r.Right - pad - total;
            else if ((flags & TextFormatFlags.HorizontalCenter) != 0) x = r.Left + (r.Width - total) / 2;
            int lineH = TextRenderer.MeasureText("Ag", f).Height;
            int y = (flags & TextFormatFlags.VerticalCenter) != 0 ? r.Top + (r.Height - lineH) / 2 : r.Top;

            Region oldClip = g.Clip;
            g.SetClip(r, CombineMode.Intersect);
            TextRenderingHint oldHint = g.TextRenderingHint;
            foreach (Run run in runs)
            {
                if (run.Special)
                {
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    using (SolidBrush b = new SolidBrush(c))
                    using (StringFormat sf = new StringFormat(StringFormat.GenericTypographic))
                    {
                        sf.FormatFlags |= StringFormatFlags.NoWrap;
                        Font sym = SymbolFont(f);
                        float symH = sym.GetHeight(g);
                        g.DrawString(run.Text, sym, b, x, y + (lineH - symH) / 2f, sf);
                    }
                }
                else
                    TextRenderer.DrawText(g, run.Text, f, new Point(x, y), c, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                x += run.Width;
            }
            g.TextRenderingHint = oldHint;
            g.Clip = oldClip;
        }


        public static string Time(int ms)
        {
            int s = Math.Max(0, ms / 1000);
            return s >= 3600 ? String.Format("{0}:{1:00}:{2:00}", s / 3600, s / 60 % 60, s % 60)
                             : String.Format("{0}:{1:00}", s / 60, s % 60);
        }

        public static GraphicsPath Pill(Rectangle r)
        {
            GraphicsPath p = new GraphicsPath();
            int d = r.Height;
            p.AddArc(r.Left, r.Top, d, d, 90, 180);
            p.AddArc(r.Right - d, r.Top, d, d, 270, 180);
            p.CloseFigure();
            return p;
        }

        /// <summary>Green filled pill (PLAY) or outlined pill (SAVE / FOLLOW) with centred caption.</summary>
        public static void PillButton(Graphics g, Rectangle r, string text, bool filled, bool hot)
        {
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath p = Pill(r))
            {
                if (filled)
                    using (SolidBrush b = new SolidBrush(hot ? AccentHover : Accent)) g.FillPath(b, p);
                else
                    using (Pen pen = new Pen(hot ? Color.White : Color.FromArgb(180, 180, 180), 1.5f)) g.DrawPath(pen, p);
            }
            g.SmoothingMode = old;
            Draw(g, text, SmallBold, r, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        /// <summary>Draws an image scaled to fill r (centre-cropped), or a grey record placeholder.</summary>
        public static void Art(Graphics g, Image img, Rectangle r, bool round)
        {
            if (img == null)
            {
                using (SolidBrush b = new SolidBrush(Placeholder))
                {
                    if (round) { g.SmoothingMode = SmoothingMode.AntiAlias; g.FillEllipse(b, r); g.SmoothingMode = SmoothingMode.None; }
                    else g.FillRectangle(b, r);
                }
                int d = Math.Max(4, r.Width / 5);
                using (SolidBrush hole = new SolidBrush(Color.FromArgb(40, 40, 40)))
                    g.FillEllipse(hole, r.Left + (r.Width - d) / 2, r.Top + (r.Height - d) / 2, d, d);
                return;
            }
            if (!round) { g.DrawImage(img, r); return; }
            using (GraphicsPath clip = new GraphicsPath())
            {
                clip.AddEllipse(r);
                Region old = g.Clip;
                g.SetClip(clip, CombineMode.Intersect);
                g.DrawImage(img, r);
                g.Clip = old;
            }
        }
    }

    /// <summary>
    /// Artwork cache. Images load on ONE background thread (the P2 shouldn't decode several JPEGs at once)
    /// and are kept pre-scaled per size, so painting is a plain unscaled blit.
    /// </summary>
    public sealed class ImageCache
    {
        public event EventHandler Loaded;               // raised (on the UI thread) when any image arrives
        readonly ISpotifyBackend _backend;
        readonly Control _ui;
        readonly Dictionary<string, Bitmap> _scaled = new Dictionary<string, Bitmap>();
        readonly Dictionary<string, Bitmap> _originals = new Dictionary<string, Bitmap>();
        readonly Dictionary<string, bool> _requested = new Dictionary<string, bool>();
        readonly Queue<string> _queue = new Queue<string>();
        readonly object _lock = new object();
        readonly List<string> _order = new List<string>();
        const int MaxScaled = 400;

        public ImageCache(ISpotifyBackend backend, Control ui)
        {
            _backend = backend;
            _ui = ui;
            Thread t = new Thread(Worker);
            t.IsBackground = true;
            t.Priority = ThreadPriority.BelowNormal;
            t.Name = "Artwork loader";
            t.Start();
        }

        /// <summary>Returns the image at exactly w x h, or null (then queues the download).</summary>
        public Bitmap Get(string url, int w, int h)
        {
            if (String.IsNullOrEmpty(url)) return null;
            string key = url + "@" + w + "x" + h;
            Bitmap bmp;
            if (_scaled.TryGetValue(key, out bmp)) return bmp;
            Bitmap original;
            lock (_lock) _originals.TryGetValue(url, out original);
            if (original != null)
            {
                bmp = Scale(original, w, h);
                _scaled[key] = bmp;
                _order.Add(key);
                if (_order.Count > MaxScaled) { Bitmap old; if (_scaled.TryGetValue(_order[0], out old)) { _scaled.Remove(_order[0]); old.Dispose(); } _order.RemoveAt(0); }
                return bmp;
            }
            lock (_lock)
            {
                if (!_requested.ContainsKey(url)) { _requested[url] = true; _queue.Enqueue(url); Monitor.Pulse(_lock); }
            }
            return null;
        }

        static Bitmap Scale(Bitmap src, int w, int h)
        {
            Bitmap dst = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(dst))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                // centre-crop to the target aspect ratio
                float sa = (float)src.Width / src.Height, da = (float)w / h;
                RectangleF from = sa > da ? new RectangleF((src.Width - src.Height * da) / 2, 0, src.Height * da, src.Height)
                                          : new RectangleF(0, (src.Height - src.Width / da) / 2, src.Width, src.Width / da);
                g.DrawImage(src, new Rectangle(0, 0, w, h), from, GraphicsUnit.Pixel);
            }
            return dst;
        }

        void Worker()
        {
            while (true)
            {
                string url;
                lock (_lock)
                {
                    while (_queue.Count == 0) Monitor.Wait(_lock);
                    url = _queue.Dequeue();
                }
                Bitmap bmp = null;
                try { bmp = _backend.LoadImage(url); } catch { }
                if (bmp == null) continue;
                lock (_lock) _originals[url] = bmp;
                try
                {
                    if (_ui.IsHandleCreated && !_ui.IsDisposed)
                        _ui.BeginInvoke(new MethodInvoker(delegate () { if (Loaded != null) Loaded(this, EventArgs.Empty); }));
                }
                catch (InvalidOperationException) { }
            }
        }
    }

    /// <summary>Gives the window its proper small (16px) and large icon straight from the exe, so XP doesn't rescale one size.</summary>
    public static class AppIcon
    {
        [DllImport("shell32.dll", CharSet = CharSet.Auto)] static extern int ExtractIconEx(string file, int index, IntPtr[] large, IntPtr[] small, int count);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        static IntPtr _small, _large;
        static bool _loaded;

        public static void Apply(Form f)
        {
            try
            {
                if (!_loaded)
                {
                    _loaded = true;
                    IntPtr[] l = new IntPtr[1], s = new IntPtr[1];
                    if (ExtractIconEx(Application.ExecutablePath, 0, l, s, 1) > 0) { _large = l[0]; _small = s[0]; }
                }
                if (f.IsHandleCreated)
                {
                    if (_small != IntPtr.Zero) SendMessage(f.Handle, 0x80, IntPtr.Zero, _small);   // WM_SETICON, ICON_SMALL
                    if (_large != IntPtr.Zero) SendMessage(f.Handle, 0x80, new IntPtr(1), _large); // ICON_BIG
                }
            }
            catch { }
        }
    }
}
