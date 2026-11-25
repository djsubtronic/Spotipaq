// Theme.cs - colours, fonts and shared drawing helpers (Spotify 1.0-era dark theme, green accent).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Drawing.Imaging;
using System.IO;
using System.Security.Cryptography;
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
        public static readonly Color Window = Color.Black;                        // behind the cards: title bar, player bar, gaps
        public static readonly Color Main = Color.FromArgb(18, 18, 18);           // content card
        public const int CardRadius = 8;                                          // corner radius of the cards
        public static readonly Color Sidebar = Color.FromArgb(18, 18, 18);
        public static readonly Color Bar = Color.FromArgb(40, 40, 40);            // top + bottom bars
        public static readonly Color RowHover = Color.FromArgb(51, 51, 51);
        public static readonly Color RowSelected = Color.FromArgb(90, 90, 90);
        public static readonly Color Line = Color.FromArgb(40, 40, 40);
        public static readonly Color Text = Color.White;
        public static readonly Color SubText = Color.FromArgb(160, 160, 160);
        public static readonly Color DimText = Color.FromArgb(110, 110, 110);
        public static readonly Color Placeholder = Color.FromArgb(62, 62, 62);

        // First choice: Figtree (the closest free match to Spotify's own typeface), if installed. Otherwise as before -
        // Text: Segoe UI Variable's "Text" optical size; big titles: its "Display" size. GDI+ (.NET 2.0) can't pick
        // variable-font axes, but the named instances appear as separate families. Fallbacks only matter on
        // machines without it.
        public static readonly string FontFamilyName = PickFamily("Figtree", "Segoe UI Variable Text", "Segoe UI Variable", "Segoe UI", "Tahoma");
        public static readonly string DisplayFamilyName = PickFamily("Figtree", "Segoe UI Variable Display", "Segoe UI Variable", "Segoe UI", "Tahoma");
        // Sizes are in pixels, each one pixel up from the old point sizes (8.25 pt = 11 px, 9 pt = 12 px, 12 pt = 16 px,
        // 7.5 pt = 10 px). The big page titles (Huge) are unchanged.
        public static readonly Font Small = Make(12f, FontStyle.Regular);
        public static readonly Font SmallBold = Make(12f, FontStyle.Bold);
        // the artist line in the player bar: one pixel smaller than Small
        public static readonly Font BarArtist = Make(11f, FontStyle.Regular);
        public static readonly Font Normal = Make(13f, FontStyle.Regular);
        public static readonly Font NormalBold = Make(13f, FontStyle.Bold);
        public static readonly Font Heading = Make(17f, FontStyle.Bold);
        public static readonly Font BarTitle = Make(20f, FontStyle.Bold);       // the page title in the top bar, once scrolled
        public static readonly Font Huge = new Font(DisplayFamilyName, 20f, FontStyle.Bold);
        public static readonly Font Label = Make(11f, FontStyle.Bold);           // "ALBUM", section headers

        static string PickFamily(params string[] wanted)
        {
            Dictionary<string, bool> have = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            using (InstalledFontCollection fonts = new InstalledFontCollection())
                foreach (FontFamily f in fonts.Families) have[f.Name] = true;
            foreach (string name in wanted) if (have.ContainsKey(name)) return name;
            return "Tahoma";
        }
        static Font Make(float pixels, FontStyle style) { return new Font(FontFamilyName, pixels, style, GraphicsUnit.Pixel); }

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
                   (c >= 0xFE00 && c <= 0xFE0F) || c == 0x200D || (c >= 0x2600 && c <= 0x27BF) || c == 0x20E3;
        }

        /// <summary>True when the text has symbols or emoji, i.e. Draw lays it out run by run instead of handing
        /// the whole string to TextRenderer.</summary>
        public static bool IsRich(string s) { return !String.IsNullOrEmpty(s) && HasSpecial(s); }

        /// <summary>Side of the emoji pictures that go with a font: a little taller than its letters. Sizes near
        /// a sprite sheet's cell snap to it so those are drawn unscaled (the big titles use the 28 px sheet 1:1).</summary>
        static int EmojiSize(Font f)
        {
            float em = f.Unit == GraphicsUnit.Pixel ? f.Size : f.SizeInPoints * 96f / 72f;
            int s = (int)Math.Round(em * 1.17f);
            if (s >= 25 && s <= 34) return Emoji.BigCell;
            if (s >= 22 && s < 25) return Emoji.Cell;
            return s;
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
                    try { r = new Font(SymbolFamilyName, f.SizeInPoints, f.Style); } catch { r = f; }     // in points, whatever unit f was made in
                    _symFonts[f] = r;
                }
            }
            return r;
        }

        sealed class Run { public string Text; public bool Special; public int Width; public int Cell = -1; }      // Cell >= 0: an emoji picture

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
                int elen, cell = Emoji.Match(text, i, out elen);
                if (cell >= 0 && elen > 0)                       // an emoji (possibly several characters) -> one picture
                {
                    if (sb.Length > 0) { runs.Add(new Run { Text = sb.ToString(), Special = special }); sb.Length = 0; }
                    runs.Add(new Run { Text = text.Substring(i, elen), Special = true, Cell = cell });
                    i += elen - 1;
                    continue;
                }
                if (IsInvisible(ch)) continue;
                bool sp = IsSpecial(ch);
                if (sp != special && sb.Length > 0) { runs.Add(new Run { Text = sb.ToString(), Special = special }); sb.Length = 0; }
                special = sp;
                sb.Append(ch);
            }
            if (sb.Length > 0) runs.Add(new Run { Text = sb.ToString(), Special = special });
            int esize = EmojiSize(f);
            foreach (Run r in runs) r.Width = r.Cell >= 0 ? esize + 2 : RunWidth(g, r.Text, f, r.Special);
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
            // PreserveGraphicsClipping: GDI text otherwise ignores the clip and draws its whole string. That never
            // showed while paints went straight to the screen (Windows clips those itself), but the page and the
            // sidebar paint parts of themselves into their off-screen copies (ScrollBuffer), where it would spill.
            if (String.IsNullOrEmpty(text) || !HasSpecial(text)) { TextRenderer.DrawText(g, text ?? "", f, r, c, flags | TextFormatFlags.PreserveGraphicsClipping); return; }

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
                    string s = run.Cell >= 0 ? "" : run.Text;          // a picture goes whole or not at all
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
            int esize = EmojiSize(f);
            foreach (Run run in runs)
            {
                if (run.Cell >= 0)
                {
                    Bitmap pic = Emoji.Get(run.Cell, esize);
                    if (pic != null)
                    {
                        g.DrawImageUnscaled(pic, x + 1, y + (lineH - esize) / 2);
                        x += run.Width;
                        continue;
                    }
                    if (Emoji.Loading) { x += run.Width; continue; }      // its sheet is still being decoded: leave the space
                }
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
                    TextRenderer.DrawText(g, run.Text, f, new Point(x, y), c, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.PreserveGraphicsClipping);
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

        /// <summary>Turns a control into a rounded card: paints the four corners outside the rounding in the colour
        /// behind it (call last in OnPaint, with the control's client rectangle).</summary>
        public static void CardCorners(Graphics g, Rectangle r, int rad, Color outside)
        {
            if (r.Width < rad * 2 || r.Height < rad * 2) return;
            int d = rad * 2, x = r.Left, y = r.Top, x2 = r.Right, y2 = r.Bottom;
            RectangleF cb = g.ClipBounds;                // a partial repaint that touches no corner: nothing to do
            if ((cb.Left >= x + rad && cb.Right <= x2 - rad) || (cb.Top >= y + rad && cb.Bottom <= y2 - rad)) return;
            SmoothingMode sm = g.SmoothingMode; PixelOffsetMode po = g.PixelOffsetMode;
            g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.Half;
            using (SolidBrush b = new SolidBrush(outside))
            using (GraphicsPath p = new GraphicsPath())
            {
                p.StartFigure(); p.AddLine(x, y + rad, x, y); p.AddLine(x, y, x + rad, y); p.AddArc(x, y, d, d, 270, -90); p.CloseFigure();
                p.StartFigure(); p.AddLine(x2 - rad, y, x2, y); p.AddLine(x2, y, x2, y + rad); p.AddArc(x2 - d, y, d, d, 0, -90); p.CloseFigure();
                p.StartFigure(); p.AddLine(x2, y2 - rad, x2, y2); p.AddLine(x2, y2, x2 - rad, y2); p.AddArc(x2 - d, y2 - d, d, d, 90, -90); p.CloseFigure();
                p.StartFigure(); p.AddLine(x + rad, y2, x, y2); p.AddLine(x, y2, x, y2 - rad); p.AddArc(x, y2 - d, d, d, 180, -90); p.CloseFigure();
                g.FillPath(b, p);
            }
            g.SmoothingMode = sm; g.PixelOffsetMode = po;
        }

        /// <summary>Rectangle with rounded corners of the given radius.</summary>
        public static GraphicsPath Rounded(Rectangle r, int rad)
        {
            GraphicsPath p = new GraphicsPath();
            int d = rad * 2;
            p.AddArc(r.Left, r.Top, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// <summary>Fills a gently rounded rectangle (smooth corners, crisp straight edges).</summary>
        public static void FillRounded(Graphics g, Rectangle r, int rad, Color c)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            rad = Math.Min(rad, Math.Min(r.Width, r.Height) / 2);
            using (SolidBrush b = new SolidBrush(c))
            {
                if (rad <= 0) { g.FillRectangle(b, r); return; }
                SmoothingMode sm = g.SmoothingMode; PixelOffsetMode po = g.PixelOffsetMode;
                g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.Half;
                using (GraphicsPath p = Rounded(r, rad)) g.FillPath(b, p);
                g.SmoothingMode = sm; g.PixelOffsetMode = po;
            }
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

        /// <summary>Corner radius for square artwork of this size: 2 px on the small row thumbnails, up to 6 px on big covers.</summary>
        public static int ArtRadius(int size) { return Math.Max(2, Math.Min(6, size / 14)); }

        /// <summary>Draws an image scaled to fill r (centre-cropped), or a grey record placeholder. Square artwork has
        /// slightly rounded corners: ImageCache cuts them into the thumbnail once, so drawing costs nothing extra.</summary>
        public static void Art(Graphics g, Image img, Rectangle r, bool round)
        {
            if (img == null)
            {
                if (!round) FillRounded(g, r, ArtRadius(r.Width), Placeholder);
                else using (SolidBrush b = new SolidBrush(Placeholder))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias; g.FillEllipse(b, r); g.SmoothingMode = SmoothingMode.None;
                }
                int d = Math.Max(4, r.Width / 5);
                using (SolidBrush hole = new SolidBrush(Color.FromArgb(40, 40, 40)))
                    g.FillEllipse(hole, r.Left + (r.Width - d) / 2, r.Top + (r.Height - d) / 2, d, d);
                return;
            }
            if (!round)
            {
                if (img.Width == r.Width && img.Height == r.Height)      // already the right size: copy, don't filter
                {
                    InterpolationMode im = g.InterpolationMode;
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.DrawImage(img, r);
                    g.InterpolationMode = im;
                }
                else g.DrawImage(img, r);
                return;
            }
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
    /// Asynchronous, disk-backed artwork cache. Only scaled 32-bit thumbnails are held
    /// in memory (4 MiB). The compressed disk cache persists across runs (256 MiB).
    /// Image decoding, scaling and file operations all run on one BelowNormal worker.
    /// Get is called on the UI thread; Loaded is raised on that same thread.
    /// </summary>
    /// <summary>
    /// Artwork: downloaded, decoded and shrunk on a background thread, kept in memory (a few MB, least recently used
    /// first out) and on disk. What makes it quick on the Pentium II:
    ///   - the disk keeps each picture as downloaded AND every size it has been shown at, so a picture seen before
    ///     comes back as one small file read - no decoding, no shrinking;
    ///   - small sizes (rows, the sidebar) download Spotify's own small version of a cover where its address allows;
    ///   - JPEGs decode straight to 1/2, 1/4 or 1/8 of their size when that is all that is needed (spq_jpeg_decode in
    ///     SpotipaqNative.dll; GDI+ for anything else);
    ///   - a picture wanted at several sizes is decoded once for all of them;
    ///   - what was asked for most recently comes first: the page on screen now, not one left behind;
    ///   - downloads reuse the connection to the image server (SpotipaqNative keeps it open between requests).
    /// </summary>
    public sealed class ImageCache
    {
        public event EventHandler Loaded;
        readonly ISpotifyBackend _backend;
        readonly Control _ui;
        readonly Dictionary<string, Bitmap> _scaled = new Dictionary<string, Bitmap>();
        readonly LinkedList<string> _lru = new LinkedList<string>();
        readonly Dictionary<string, LinkedListNode<string>> _nodes = new Dictionary<string, LinkedListNode<string>>();
        readonly Dictionary<string, bool> _requested = new Dictionary<string, bool>();        // asked for, not yet published
        readonly List<ArtRequest> _pending = new List<ArtRequest>();                           // ...and not yet started
        readonly Dictionary<string, ArtRequest> _pendingByKey = new Dictionary<string, ArtRequest>();
        readonly object _lock = new object();
        readonly string _folder;
        const long MaxDiskBytes = 256L * 1024 * 1024;
        const long MaxMemoryBytes = 4L * 1024 * 1024;
        const int RecentMs = 200;                  // asks this close to the newest one count as "the same screen"
        long _memoryBytes;
        int _writesSinceTrim, _seq;

        sealed class ArtRequest
        {
            public string Url; public int Width; public int Height; public string Key; public bool FromTop;
            public int Seq;                        // order first asked for
            public int Asked;                      // Environment.TickCount of the latest ask (every paint that still wants it)
        }

        public ImageCache(ISpotifyBackend backend, Control ui)
        {
            _backend = backend;
            _ui = ui;
            _folder = Path.Combine(Spotipaq.Engine.Log.Folder, "artcache");
            Thread t = new Thread(Worker);
            t.IsBackground = true;
            t.Priority = ThreadPriority.BelowNormal;
            t.Name = "Artwork loader";
            t.Start();
        }

        /// <summary>Never accesses disk, decodes or scales on the UI thread.</summary>
        public Bitmap Get(string url, int w, int h) { return Get(url, w, h, false); }

        /// <summary>fromTop: when the picture is taller than the box, keep its top and cut the bottom (banners),
        /// instead of cutting both ends equally.</summary>
        public Bitmap Get(string url, int w, int h, bool fromTop)
        {
            if (String.IsNullOrEmpty(url) || w <= 0 || h <= 0) return null;
            string key = url + "@" + w + "x" + h + (fromTop ? "t" : "");
            Bitmap bmp;
            if (_scaled.TryGetValue(key, out bmp))
            {
                LinkedListNode<string> node;
                if (_nodes.TryGetValue(key, out node))
                {
                    _lru.Remove(node);
                    _lru.AddLast(node);
                }
                return bmp;
            }
            lock (_lock)
            {
                ArtRequest r;
                if (_pendingByKey.TryGetValue(key, out r)) r.Asked = Environment.TickCount;      // still wanted: on screen now
                else if (!_requested.ContainsKey(key))
                {
                    _requested[key] = true;
                    r = new ArtRequest { Url = url, Width = w, Height = h, Key = key, FromTop = fromTop, Seq = ++_seq, Asked = Environment.TickCount };
                    _pending.Add(r);
                    _pendingByKey[key] = r;
                    Monitor.Pulse(_lock);
                }
            }
            return null;
        }

        static long BitmapBytes(Bitmap bmp) { return (long)bmp.Width * bmp.Height * 4; }

        // UI thread only. Never dispose a bitmap concurrently with its being painted.
        void Publish(ArtRequest req, Bitmap bmp)
        {
            if (_ui.IsDisposed) { bmp.Dispose(); return; }
            lock (_lock) _requested.Remove(req.Key);
            if (_scaled.ContainsKey(req.Key)) { bmp.Dispose(); return; }
            long size = BitmapBytes(bmp);
            if (size > MaxMemoryBytes) { bmp.Dispose(); return; }
            _scaled[req.Key] = bmp;
            _nodes[req.Key] = _lru.AddLast(req.Key);
            _memoryBytes += size;
            while (_memoryBytes > MaxMemoryBytes && _lru.Count > 0)
            {
                string oldest = _lru.First.Value;
                _lru.RemoveFirst();
                _nodes.Remove(oldest);
                Bitmap old = _scaled[oldest];
                _scaled.Remove(oldest);
                _memoryBytes -= BitmapBytes(old);
                old.Dispose();
            }
            if (Loaded != null) Loaded(this, EventArgs.Empty);
        }

        /// <summary>The disk cache's file name for a text (an address, or an address and a size).</summary>
        static string FileName(string text, string extension)
        {
            byte[] data = Encoding.UTF8.GetBytes(text);
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(data);
                StringBuilder hex = new StringBuilder(68);
                for (int i = 0; i < hash.Length; i++) hex.Append(hash[i].ToString("x2"));
                return hex.ToString() + extension;
            }
        }

        static Bitmap Scale(Bitmap src, int w, int h, bool fromTop)
        {
            Bitmap dst = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(dst))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                float sa = (float)src.Width / src.Height, da = (float)w / h;
                RectangleF from = sa > da ? new RectangleF((src.Width - src.Height * da) / 2, 0, src.Height * da, src.Height)
                                          : new RectangleF(0, fromTop ? 0 : (src.Height - src.Width / da) / 2, src.Width, src.Width / da);
                g.DrawImage(src, new Rectangle(0, 0, w, h), from, GraphicsUnit.Pixel);
            }
            if (w != h) return dst;                    // banners stay square-cornered
            // square artwork: round the corners here, once, on the loader thread (transparent, anti-aliased)
            Bitmap round = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            try
            {
                using (Graphics g = Graphics.FromImage(round))
                using (TextureBrush tb = new TextureBrush(dst))
                using (GraphicsPath p = Theme.Rounded(new Rectangle(0, 0, w, h), Theme.ArtRadius(w)))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.FillPath(tb, p);
                }
            }
            catch (Exception) { round.Dispose(); return dst; }
            dst.Dispose();
            return round;
        }

        // ---- the worker -------------------------------------------------------------------------------------

        /// <summary>The next picture to load, with every size of it that is waiting: of the requests asked for most
        /// recently (the screen as it is now), the one asked for first (top of the page first).</summary>
        List<ArtRequest> NextBatch()
        {
            lock (_lock)
            {
                while (_pending.Count == 0) Monitor.Wait(_lock);
                int newest = _pending[0].Asked;
                foreach (ArtRequest r in _pending) if (unchecked(r.Asked - newest) > 0) newest = r.Asked;
                ArtRequest pick = null;
                foreach (ArtRequest r in _pending)
                    if (unchecked(newest - r.Asked) <= RecentMs && (pick == null || r.Seq < pick.Seq)) pick = r;
                List<ArtRequest> batch = new List<ArtRequest>();
                for (int i = _pending.Count - 1; i >= 0; i--)
                    if (_pending[i].Url == pick.Url) { batch.Add(_pending[i]); _pendingByKey.Remove(_pending[i].Key); _pending.RemoveAt(i); }
                return batch;
            }
        }

        void Worker()
        {
            try { Directory.CreateDirectory(_folder); TrimDisk(); }
            catch (Exception ex) { Spotipaq.Engine.Log.Write("Artwork cache init failed: " + ex.Message); }
            while (true)
            {
                List<ArtRequest> batch = NextBatch();
                string url = batch[0].Url;
                try
                {
                    // 1. sizes already on disk: no decoding at all
                    List<ArtRequest> left = new List<ArtRequest>();
                    foreach (ArtRequest req in batch)
                    {
                        Bitmap ready = LoadScaled(req);
                        if (ready != null) Deliver(req, ready); else left.Add(req);
                    }
                    if (left.Count == 0) continue;

                    // 2. the picture itself, decoded once, only as big as the biggest size still wanted needs
                    int needW = 0, needH = 0;
                    foreach (ArtRequest req in left) { needW = Math.Max(needW, req.Width); needH = Math.Max(needH, req.Height); }
                    Bitmap original = LoadOriginal(url, needW, needH);
                    try
                    {
                        foreach (ArtRequest req in left)
                        {
                            Bitmap result = original != null ? Scale(original, req.Width, req.Height, req.FromTop) : null;
                            if (result == null) { lock (_lock) _requested.Remove(req.Key); continue; }
                            SaveScaled(req, result);
                            Deliver(req, result);
                        }
                    }
                    finally { if (original != null) original.Dispose(); }
                }
                catch (Exception ex)
                {
                    Spotipaq.Engine.Log.Write("Artwork load failed: " + ex.Message);
                    lock (_lock) foreach (ArtRequest req in batch) _requested.Remove(req.Key);
                }
            }
        }

        // hands a finished picture to the UI thread (or drops it if the window is gone)
        void Deliver(ArtRequest req, Bitmap bmp)
        {
            try
            {
                if (_ui.IsHandleCreated && !_ui.IsDisposed)
                {
                    _ui.BeginInvoke(new MethodInvoker(delegate { Publish(req, bmp); }));
                    return;
                }
            }
            catch (InvalidOperationException) { }
            bmp.Dispose();
            lock (_lock) _requested.Remove(req.Key);
        }

        // ---- the picture as downloaded ---------------------------------------------------------------------------

        /// <summary>The picture, decoded no bigger than needed for needW x needH (cropped to that shape). Null if it
        /// can't be had.</summary>
        Bitmap LoadOriginal(string url, int needW, int needH)
        {
            // several addresses on separate lines: the first one that can be had (a playlist's own cover, then its
            // first song's art - see SpotifyBackend.GetPlaylist)
            if (url.IndexOf('\n') >= 0)
            {
                foreach (string one in url.Split('\n'))
                {
                    try { Bitmap b = LoadOriginal(one, needW, needH); if (b != null) return b; }
                    catch (Exception ex) { Spotipaq.Engine.Log.Write("Artwork unavailable, trying the next choice: " + ex.Message); }
                }
                return null;
            }
            if (!url.StartsWith("http", StringComparison.Ordinal)) return _backend.LoadImage(url);     // the mock backend's pictures
            byte[] data = null;
            string small = SmallerVersion(url, Math.Max(needW, needH));
            if (small != null) data = LoadBytes(small);
            if (data == null) data = LoadBytes(url);
            if (data == null) return null;
            Bitmap bmp = DecodeNative(data, needW, needH);
            if (bmp != null) return bmp;
            using (MemoryStream ms = new MemoryStream(data))
            using (Image img = Image.FromStream(ms))
                return new Bitmap(img);
        }

        /// <summary>For small sizes, the address of Spotify's own small version of the same picture, when its address
        /// says which size it is: album covers (640 / 300 / 64 px, the size is part of the picture's id) and playlist
        /// mosaics (640 / 300 / 60). Null = use the address as it is.</summary>
        static string SmallerVersion(string url, int need)
        {
            const string Small = "ab67616d00004851";                                   // album cover, 64 px
            if (need <= 64)
                foreach (string big in new string[] { "ab67616d0000b273", "ab67616d00001e02" })   // 640, 300 px
                {
                    int i = url.IndexOf(big, StringComparison.Ordinal);
                    if (i >= 0) return url.Substring(0, i) + Small + url.Substring(i + big.Length);
                }
            if (need <= 60)
                foreach (string big in new string[] { "https://mosaic.scdn.co/640/", "https://mosaic.scdn.co/300/" })
                    if (url.StartsWith(big, StringComparison.Ordinal)) return "https://mosaic.scdn.co/60/" + url.Substring(big.Length);
            return null;
        }

        /// <summary>The file as downloaded: from the disk cache, else downloaded and kept there exactly as it came
        /// (not re-encoded). Null if it can't be had.</summary>
        byte[] LoadBytes(string url)
        {
            string path = Path.Combine(_folder, FileName(url, ".jpg"));
            if (File.Exists(path))
            {
                try
                {
                    byte[] cached = File.ReadAllBytes(path);
                    // Touch at most daily to avoid a disk write on every cache hit.
                    if ((DateTime.UtcNow - File.GetLastWriteTimeUtc(path)).TotalDays >= 1) File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                    if (cached.Length > 0) return cached;
                }
                catch (Exception) { }
                try { File.Delete(path); } catch { }
            }
            Spotipaq.Engine.HttpResult r = Spotipaq.Engine.Native.Get(url, null);
            if (!r.Ok || r.Body == null || r.Body.Length == 0) return null;
            WriteFile(path, r.Body, r.Body.Length);
            return r.Body;
        }

        // Writes atomically; an interrupted run cannot poison the cache.
        void WriteFile(string path, byte[] data, int length)
        {
            try
            {
                string temp = path + ".tmp";
                using (FileStream fs = new FileStream(temp, FileMode.Create, FileAccess.Write)) fs.Write(data, 0, length);
                if (File.Exists(path)) File.Delete(temp);
                else File.Move(temp, path);
                if (++_writesSinceTrim >= 32) { _writesSinceTrim = 0; TrimDisk(); }
            }
            catch (Exception ex) { Spotipaq.Engine.Log.Write("Artwork cache write failed: " + ex.Message); }
        }

        // ---- native JPEG decoding (SpotipaqNative.dll 5+) --------------------------------------------------------
        [DllImport("SpotipaqNative.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int spq_jpeg_decode(byte[] data, int len, int needW, int needH, out IntPtr px, out int w, out int h, out int stride);
        [DllImport("SpotipaqNative.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern void spq_free(IntPtr p);
        [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
        static extern void CopyMemory(IntPtr dst, IntPtr src, int length);
        static int _nativeJpeg = -1;          // -1 not checked yet, 0 no, 1 yes

        /// <summary>The JPEG decoded straight to 1/2, 1/4 or 1/8 of its size where that still covers needW x needH;
        /// null if this decoder can't (not a baseline JPEG, an old DLL...) - GDI+ then decodes it.</summary>
        static Bitmap DecodeNative(byte[] data, int needW, int needH)
        {
            if (_nativeJpeg < 0) _nativeJpeg = Spotipaq.Engine.Native.Version() >= 5 ? 1 : 0;
            if (_nativeJpeg == 0) return null;
            IntPtr px; int w, h, stride;
            try { if (spq_jpeg_decode(data, data.Length, needW, needH, out px, out w, out h, out stride) != 1) return null; }
            catch (Exception) { _nativeJpeg = 0; return null; }
            try
            {
                Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);         // opaque: the same as straight ARGB
                BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
                try
                {
                    for (int y = 0; y < h; y++)
                        CopyMemory(new IntPtr(bd.Scan0.ToInt64() + (long)y * bd.Stride), new IntPtr(px.ToInt64() + (long)y * stride), w * 4);
                }
                finally { bmp.UnlockBits(bd); }
                return bmp;
            }
            finally { spq_free(px); }
        }

        // ---- finished sizes on disk ------------------------------------------------------------------------------
        // A picture at a size it has been shown at before, exactly as it was made (shrunk, corners rounded), as raw
        // pixels: reading it back is all there is to do. "SPQ1", width, height, then width x height x 4 bytes (PArgb).

        string ScaledPath(ArtRequest req) { return Path.Combine(_folder, FileName(req.Key, ".px")); }

        Bitmap LoadScaled(ArtRequest req)
        {
            string path = ScaledPath(req);
            if (!File.Exists(path)) return null;
            try
            {
                byte[] d = File.ReadAllBytes(path);
                if (d.Length < 12 || d[0] != (byte)'S' || d[1] != (byte)'P' || d[2] != (byte)'Q' || d[3] != (byte)'1') throw new IOException("not a picture");
                int w = BitConverter.ToInt32(d, 4), h = BitConverter.ToInt32(d, 8);
                if (w != req.Width || h != req.Height || d.Length != 12 + w * h * 4) throw new IOException("wrong size");
                Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
                BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
                try
                {
                    for (int y = 0; y < h; y++) Marshal.Copy(d, 12 + y * w * 4, new IntPtr(bd.Scan0.ToInt64() + (long)y * bd.Stride), w * 4);
                }
                finally { bmp.UnlockBits(bd); }
                if ((DateTime.UtcNow - File.GetLastWriteTimeUtc(path)).TotalDays >= 1) File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                return bmp;
            }
            catch (Exception)
            {
                try { File.Delete(path); } catch { }
                return null;
            }
        }

        void SaveScaled(ArtRequest req, Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            byte[] d = new byte[12 + w * h * 4];
            d[0] = (byte)'S'; d[1] = (byte)'P'; d[2] = (byte)'Q'; d[3] = (byte)'1';
            BitConverter.GetBytes(w).CopyTo(d, 4);
            BitConverter.GetBytes(h).CopyTo(d, 8);
            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                for (int y = 0; y < h; y++) Marshal.Copy(new IntPtr(bd.Scan0.ToInt64() + (long)y * bd.Stride), d, 12 + y * w * 4, w * 4);
            }
            finally { bmp.UnlockBits(bd); }
            WriteFile(ScaledPath(req), d, d.Length);
        }

        // Worker thread only; startup and every 32 new files, not per frame. Pictures as downloaded and finished
        // sizes share the one budget, oldest out first.
        void TrimDisk()
        {
            FileInfo[] files = new DirectoryInfo(_folder).GetFiles();
            long total = 0;
            foreach (FileInfo f in files) total += f.Length;
            if (total <= MaxDiskBytes) return;
            Array.Sort(files, delegate (FileInfo a, FileInfo b) { return a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc); });
            foreach (FileInfo f in files)
            {
                if (total <= MaxDiskBytes) break;
                try { long n = f.Length; f.Delete(); total -= n; }
                catch { }
            }
        }
    }

    /// <summary>The player bar's icons: white shapes embedded in the exe (resources\ic_*.png, from the open-source
    /// Lucide set), tinted to whatever colour is asked for. Each colour is tinted once and kept.</summary>
    public static class Glyphs
    {
        static readonly Dictionary<string, Bitmap> _masks = new Dictionary<string, Bitmap>();
        static readonly Dictionary<string, Bitmap> _tinted = new Dictionary<string, Bitmap>();

        /// <summary>Draws the icon centred in r. False when it isn't in the exe (the caller then draws its own shape).</summary>
        public static bool Draw(Graphics g, string name, Rectangle r, Color c)
        {
            Bitmap mask;
            if (!_masks.TryGetValue(name, out mask))
            {
                mask = null;
                try
                {
                    using (Stream st = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream(name + ".png"))
                        if (st != null) using (Image img = Image.FromStream(st)) mask = new Bitmap(img);
                }
                catch (Exception) { mask = null; }
                _masks[name] = mask;
            }
            if (mask == null) return false;
            string key = name + "#" + c.ToArgb();
            Bitmap bmp;
            if (!_tinted.TryGetValue(key, out bmp))
            {
                bmp = new Bitmap(mask.Width, mask.Height, PixelFormat.Format32bppArgb);
                for (int y = 0; y < mask.Height; y++)
                    for (int x = 0; x < mask.Width; x++)
                        bmp.SetPixel(x, y, Color.FromArgb(mask.GetPixel(x, y).A, c));
                _tinted[key] = bmp;
            }
            g.DrawImage(bmp, r.Left + (r.Width - bmp.Width) / 2, r.Top + (r.Height - bmp.Height) / 2, bmp.Width, bmp.Height);
            return true;
        }
    }

    public delegate int ScrollGetter();

    /// <summary>The slim vertical scrollbar the page and the sidebar share: up / down buttons, rounded thumb, drag,
    /// click on the track to page. The owner keeps the scroll position; this draws at its right edge and handles the mouse.</summary>
    public sealed class SlimScroll
    {
        public const int W = 8;
        const int BtnH = 11, MinThumb = 30;
        readonly Control _c;
        readonly ScrollGetter _get, _max;
        readonly Action<int> _set;                 // sets the position (the owner clamps it)
        readonly int _line;                        // pixels per button step
        readonly Color _track, _thumb, _lit;
        readonly System.Windows.Forms.Timer _repeat = new System.Windows.Forms.Timer();
        bool _drag, _engaged;                      // _engaged: the current press started on the bar
        int _dragOffset;
        int _held, _hot;                           // -1 up button, +1 down button, 0 neither
        bool _hotThumb;                            // pointer over the thumb
        /// <summary>Space left free above and below the bar (the page keeps clear of its header and its rounded bottom corner).</summary>
        public int TopInset, BottomInset;
        /// <summary>Part of the owner's height that doesn't show scrolled content (a fixed header above the list).</summary>
        public int ViewInset;
        /// <summary>True when the owner's set delegate repaints what the scroll changed itself (moving the pixels already
        /// on screen, see PixelScroll), bar included: then the bar doesn't repaint the whole owner after each step.</summary>
        public bool OwnerRepaints;

        public SlimScroll(Control owner, ScrollGetter get, ScrollGetter max, Action<int> set, int line, Color track, Color thumb, Color lit)
        {
            _c = owner; _get = get; _max = max; _set = set; _line = line; _track = track; _thumb = thumb; _lit = lit;
            _repeat.Tick += delegate
            {
                if (_held == 0) { _repeat.Stop(); return; }
                _repeat.Interval = 50;
                if (_hot != _held) return;         // pointer slid off the button: wait for it to come back
                _set(_get() + _held * _line);
                if (!OwnerRepaints) _c.Invalidate();
            };
        }

        public bool Shown { get { return _max() > 0; } }
        int H { get { return _c.ClientSize.Height; } }
        int View { get { return Math.Max(1, H - ViewInset); } }       // how much scrolled content is visible at once
        int X { get { return _c.ClientSize.Width - W; } }
        int Top { get { return TopInset; } }                          // the bar runs from Top to Bottom
        int Bottom { get { return Math.Max(Top + 2 * BtnH + 1, H - BottomInset); } }
        int TrackH { get { return Math.Max(1, Bottom - Top - 2 * BtnH); } }

        Rectangle Thumb()
        {
            int max = _max(), track = TrackH;
            int th = Math.Min(track, Math.Max(MinThumb, (int)((long)track * View / Math.Max(1, max + View))));
            int ty = Top + BtnH + (max <= 0 ? 0 : (int)((long)(track - th) * _get() / max));
            return new Rectangle(X + 1, ty, W - 2, th);
        }

        bool Contains(Point p) { return Shown && p.X >= X && p.Y >= Top && p.Y < Bottom; }
        /// <summary>True when the point is on the scrollbar.</summary>
        public bool Over(Point p) { return Contains(p); }
        // a change of the bar's own look (hover, release) repaints its strip only
        void InvalidateBar() { _c.Invalidate(new Rectangle(X, Top, W, Math.Max(1, Bottom - Top))); }
        int ButtonAt(Point p) { if (!Contains(p)) return 0; return p.Y < Top + BtnH ? -1 : p.Y >= Bottom - BtnH ? 1 : 0; }

        public void Paint(Graphics g)
        {
            if (!Shown) return;
            // A transparent track colour means no track at all: whatever the owner painted (the page's gradient, its
            // solid header) shows through, and the arrows are drawn light so they stand out on any of it.
            bool clear = _track.A == 0;
            if (!clear) using (SolidBrush t = new SolidBrush(_track)) g.FillRectangle(t, X, Top, W, Bottom - Top);
            Color arrow = clear ? Color.FromArgb(190, 190, 190) : _thumb, arrowLit = clear ? Color.White : _lit;
            // thumb and arrows: lighter under the pointer, white while pressed / dragged
            Arrow(g, true, _held == -1 ? Color.White : _hot == -1 ? arrowLit : arrow);
            Arrow(g, false, _held == 1 ? Color.White : _hot == 1 ? arrowLit : arrow);
            using (SolidBrush tb = new SolidBrush(_drag ? Color.White : _hotThumb ? _lit : _thumb)) g.FillRectangle(tb, Thumb());       // flat, square-cornered
        }

        // 6 px wide, 3 rows, one pixel narrower per side each row
        void Arrow(Graphics g, bool up, Color c)
        {
            int top = (up ? Top : Bottom - BtnH) + (BtnH - 3) / 2;
            using (SolidBrush b = new SolidBrush(c))
                for (int i = 0; i < 3; i++)
                {
                    int row = up ? i : 2 - i;
                    g.FillRectangle(b, X + 3 - row, top + i, 2 + 2 * row, 1);
                }
        }

        /// <summary>True when the press was on the scrollbar (the owner should do nothing else with it).</summary>
        public bool MouseDown(MouseEventArgs e)
        {
            if (!Contains(e.Location)) return false;
            _engaged = true;
            if (e.Button != MouseButtons.Left) return true;
            int btn = ButtonAt(e.Location);
            if (btn != 0)
            {
                _held = btn; _hot = btn;
                _set(_get() + btn * _line);
                _repeat.Interval = 350;            // hold: repeats after a short pause
                _repeat.Start();
                _c.Capture = true;
            }
            else
            {
                Rectangle t = Thumb();
                if (e.Y >= t.Top && e.Y < t.Bottom) { _drag = true; _dragOffset = e.Y - t.Top; _c.Capture = true; }
                else _set(_get() + (e.Y < t.Top ? -View : View));
            }
            if (OwnerRepaints) InvalidateBar();    // the press itself only changes the bar's look
            else _c.Invalidate();
            return true;
        }

        /// <summary>True while the scrollbar is using the mouse or the pointer is over it.</summary>
        public bool MouseMove(MouseEventArgs e)
        {
            if (_drag)
            {
                int room = Math.Max(1, TrackH - Thumb().Height);
                _set((int)((long)(e.Y - _dragOffset - BtnH - Top) * _max() / room));
                if (!OwnerRepaints) _c.Invalidate();
                return true;
            }
            int btn = ButtonAt(e.Location);
            bool onThumb = false;
            if (btn == 0 && Contains(e.Location)) { Rectangle t = Thumb(); onThumb = e.Y >= t.Top && e.Y < t.Bottom; }
            if (btn != _hot || onThumb != _hotThumb) { _hot = btn; _hotThumb = onThumb; InvalidateBar(); }
            return _held != 0 || Contains(e.Location);
        }

        /// <summary>True when the release ends a press that began on the scrollbar.</summary>
        public bool MouseUp(MouseEventArgs e)
        {
            if (!_engaged) return false;
            _engaged = false;
            if (_drag || _held != 0)
            {
                _drag = false; _held = 0;
                _repeat.Stop();
                _c.Capture = false;
            }
            InvalidateBar();
            return true;
        }

        public void MouseLeave() { if (_hot != 0 || _hotThumb) { _hot = 0; _hotThumb = false; InvalidateBar(); } }
    }

    /// <summary>Paints part of a control (clip, client coordinates) with g.</summary>
    public delegate void ClipPainter(Graphics g, Rectangle clip);

    /// <summary>
    /// A control's own off-screen copy of everything it shows, kept up to date by every paint (the control paints
    /// into it, then the changed part is copied to the screen). Scrolling then works on the copy: the pixels that stay
    /// in view are moved inside it, only what that can't give is painted (the strip coming into view, the parts
    /// that stay put), and the finished picture goes to the screen in one copy. No full repaint per step - on the
    /// Pentium II a full repaint of a list (dozens of lines of text) is what made keyboard and scrollbar scrolling
    /// choppy - and no half-done step is ever on screen (moving the screen's own pixels first showed old strips, and
    /// things that hold still jumping, until each piece was painted).
    /// </summary>
    public sealed class ScrollBuffer : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")]
        static extern bool ScrollDC(IntPtr dc, int dx, int dy, ref RECT scroll, ref RECT clip, IntPtr updateRegion, IntPtr updateRect);
        [DllImport("user32.dll")]
        static extern int ScrollWindowEx(IntPtr hwnd, int dx, int dy, ref RECT scroll, ref RECT clip, IntPtr updateRegion, IntPtr updateRect, uint flags);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
        [DllImport("gdi32.dll")] static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
        [DllImport("gdi32.dll")] static extern int CombineRgn(IntPtr dst, IntPtr a, IntPtr b, int mode);
        [DllImport("gdi32.dll")] static extern int SelectClipRgn(IntPtr dc, IntPtr rgn);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        const uint SRCCOPY = 0x00CC0020;
        const int RGN_DIFF = 4, NULLREGION = 1;

        readonly Control _c;
        // The copy is a plain 24-bit GDI picture (DibPicture) with a GDI+ Graphics on its device context - the same
        // arrangement WinForms' own double buffering uses, but with the pixels reachable: native code (the header
        // banner, spq_banner) can draw straight into it instead of into a picture of its own that then needs copying.
        DibPicture _dib;
        Graphics _g;
        Size _size;
        bool _valid;                            // the copy holds the whole control (it has been painted in full once)

        public ScrollBuffer(Control c) { _c = c; }

        Graphics Get()
        {
            Size s = _c.ClientSize;
            if (s.Width <= 0 || s.Height <= 0 || !_c.IsHandleCreated) return null;
            if (_g != null && _size == s) return _g;
            Drop();
            _dib = DibPicture.Create(s.Width, s.Height);
            if (_dib == null) return null;
            _g = Graphics.FromHdc(_dib.Dc);
            _size = s;
            _valid = false;
            return _g;
        }

        void Drop()
        {
            if (_g != null) { _g.Dispose(); _g = null; }
            if (_dib != null) { _dib.Dispose(); _dib = null; }
            _valid = false;
        }

        /// <summary>True when the copy is complete, so scrolling can work on it.</summary>
        public bool Ready { get { return Get() != null && _valid; } }

        /// <summary>True when g is the Graphics drawing into the copy (see Bits).</summary>
        public bool Owns(Graphics g) { return g != null && _g != null && ReferenceEquals(g, _g); }

        /// <summary>The copy's pixels, for native code to write straight into: 24-bit B,G,R, top row first, stride
        /// bytes per row. Everything GDI+ and GDI had queued for it is finished first.</summary>
        public IntPtr Bits(out int stride)
        {
            stride = 0;
            if (_g == null) return IntPtr.Zero;
            _g.Flush(FlushIntention.Sync);
            stride = _dib.Stride;
            return _dib.Bits;
        }

        /// <summary>For WM_PAINT: paints clip into the copy (all of it the first time) and copies it to the screen
        /// through target. False = no copy to be had; the caller paints target itself.</summary>
        public bool Paint(Graphics target, Rectangle clip, ClipPainter painter)
        {
            Graphics b = Get();
            if (b == null) return false;
            if (!_valid) { clip = new Rectangle(Point.Empty, _size); _valid = true; }
            Draw(clip, painter);
            // to the screen: plain GDI, just the painted part (the paint's own clipping limits it further)
            IntPtr dst = target.GetHdc();
            IntPtr src = _g.GetHdc();
            try { Copy(dst, clip, src, clip.X, clip.Y); }
            finally { _g.ReleaseHdc(src); target.ReleaseHdc(dst); }
            return true;
        }

        /// <summary>Paints part of the control into the copy only (see Present).</summary>
        public void Draw(Rectangle r, ClipPainter painter)
        {
            Graphics g = Get();
            if (g == null) return;
            r.Intersect(new Rectangle(Point.Empty, _size));
            if (r.Width <= 0 || r.Height <= 0) return;
            g.SetClip(r);
            painter(g, r);
            g.ResetClip();
        }

        /// <summary>Moves the pixels inside area up by dy (down if negative), in the copy. False = too far to be
        /// worth it (the whole area changes), or no copy.</summary>
        public bool Shift(Rectangle area, int dy)
        {
            if (!Ready || area.Width <= 0 || area.Height <= 0 || Math.Abs(dy) >= area.Height) return false;
            RECT r;
            r.Left = area.Left; r.Top = area.Top; r.Right = area.Right; r.Bottom = area.Bottom;
            RECT clip = r;
            IntPtr dc = _g.GetHdc();
            try { return ScrollDC(dc, 0, -dy, ref r, ref clip, IntPtr.Zero, IntPtr.Zero); }
            finally { _g.ReleaseHdc(dc); }
        }

        /// <summary>Plain GDI copy of a rectangle between two device contexts (no GDI+: it would process the whole
        /// source picture however little of it is copied).</summary>
        public static void Copy(IntPtr dst, Rectangle to, IntPtr src, int sx, int sy)
        {
            if (to.Width > 0 && to.Height > 0) BitBlt(dst, to.X, to.Y, to.Width, to.Height, src, sx, sy, SRCCOPY);
        }

        /// <summary>Copies the whole copy to the screen, in one go.</summary>
        public void Present()
        {
            if (_g == null || !_c.IsHandleCreated) return;
            IntPtr hwnd = _c.Handle;
            IntPtr screen = GetDC(hwnd);
            if (screen == IntPtr.Zero) return;
            IntPtr src = _g.GetHdc();
            try { BitBlt(screen, 0, 0, _size.Width, _size.Height, src, 0, 0, SRCCOPY); }
            finally { _g.ReleaseHdc(src); ReleaseDC(hwnd, screen); }
        }

        /// <summary>Puts a finished scroll step on screen without sending the whole picture: the screen moves its own
        /// pixels inside area by dy (the video card does that, in its own memory), then only the pieces painted for
        /// this step are copied from the copy. Both are straight copies of finished pixels, so nothing half-done
        /// shows. Whatever the screen couldn't move itself (parts covered by another window) also comes from the copy.</summary>
        public void PresentScrolled(Rectangle area, int dy, IList<Rectangle> pieces)
        {
            if (_g == null || !_c.IsHandleCreated) return;
            if (area.Width <= 0 || area.Height <= 0) { Present(); return; }       // nothing to move: the copy, whole
            IntPtr hwnd = _c.Handle;
            IntPtr left = CreateRectRgn(0, 0, 0, 0);      // what the screen couldn't move itself
            try
            {
                RECT r;
                r.Left = area.Left; r.Top = area.Top; r.Right = area.Right; r.Bottom = area.Bottom;
                RECT clip = r;
                int kind = ScrollWindowEx(hwnd, 0, -dy, ref r, ref clip, left, IntPtr.Zero, 0);
                if (kind == 0) { Present(); return; }
                IntPtr screen = GetDC(hwnd);
                if (screen == IntPtr.Zero) { Present(); return; }
                Graphics g = _g;
                IntPtr src = g.GetHdc();
                try
                {
                    foreach (Rectangle p in pieces)
                    {
                        BitBlt(screen, p.X, p.Y, p.Width, p.Height, src, p.X, p.Y, SRCCOPY);
                        IntPtr pr = CreateRectRgn(p.Left, p.Top, p.Right, p.Bottom);
                        kind = CombineRgn(left, left, pr, RGN_DIFF);
                        DeleteObject(pr);
                    }
                    if (kind > NULLREGION)
                    {
                        SelectClipRgn(screen, left);
                        BitBlt(screen, 0, 0, _size.Width, _size.Height, src, 0, 0, SRCCOPY);
                    }
                }
                finally { g.ReleaseHdc(src); ReleaseDC(hwnd, screen); }
            }
            finally { DeleteObject(left); }
        }

        /// <summary>Turns a list of areas to repaint into as few paints as possible, with nothing painted twice: areas
        /// right across the control that touch or overlap become one, and narrower ones lose the rows those already
        /// cover. Every paint of a page re-runs everything under it (a header's texts, its picture), so one tall
        /// paint is much cheaper than several pieces of the same area.</summary>
        public static List<Rectangle> Coalesce(IList<Rectangle> areas, Size client)
        {
            Rectangle all = new Rectangle(Point.Empty, client);
            List<Rectangle> wide = new List<Rectangle>(), narrow = new List<Rectangle>();
            foreach (Rectangle a0 in areas)
            {
                Rectangle a = a0;
                a.Intersect(all);
                if (a.Width <= 0 || a.Height <= 0) continue;
                if (a.Left == 0 && a.Right == client.Width) wide.Add(a); else narrow.Add(a);
            }
            wide.Sort(delegate (Rectangle x, Rectangle y) { return x.Top.CompareTo(y.Top); });
            List<Rectangle> result = new List<Rectangle>();
            foreach (Rectangle a in wide)
            {
                int n = result.Count;
                if (n > 0 && a.Top <= result[n - 1].Bottom) result[n - 1] = Rectangle.Union(result[n - 1], a);
                else result.Add(a);
            }
            int wideCount = result.Count;
            foreach (Rectangle a in narrow)
            {
                // the rows of a that no wide area covers, as separate pieces
                int y = a.Top;
                while (y < a.Bottom)
                {
                    bool covered = false;
                    for (int i = 0; i < wideCount; i++)
                        if (y >= result[i].Top && y < result[i].Bottom) { y = result[i].Bottom; covered = true; break; }
                    if (covered) continue;
                    int end = a.Bottom;
                    for (int i = 0; i < wideCount; i++)
                        if (result[i].Top > y && result[i].Top < end) end = result[i].Top;
                    Rectangle piece = new Rectangle(a.Left, y, a.Width, end - y);
                    bool inside = false;
                    for (int i = wideCount; i < result.Count; i++) if (result[i].Contains(piece)) { inside = true; break; }
                    if (!inside) result.Add(piece);
                    y = end;
                }
            }
            return result;
        }

        /// <summary>The copy's device context, for plain GDI drawing that must go into it as well as onto the screen
        /// (IntPtr.Zero when there is none); hand it back with ReleaseHdc.</summary>
        public IntPtr GetHdc() { return Ready ? _g.GetHdc() : IntPtr.Zero; }
        public void ReleaseHdc(IntPtr dc) { if (dc != IntPtr.Zero && _g != null) _g.ReleaseHdc(dc); }

        public void Dispose() { Drop(); }
    }

    /// <summary>A plain GDI picture (a 24-bit DIB section with its own device context) whose pixels native code can
    /// write directly, and which copies to the page with BitBlt - only the pixels copied, unlike GDI+ drawing.</summary>
    public sealed class DibPicture : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }
        [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool GdiFlush();

        public readonly int Width, Height, Stride;
        readonly IntPtr _bmp, _bits, _dc, _old;
        bool _gone;

        DibPicture(int w, int h, IntPtr bmp, IntPtr bits, IntPtr dc, IntPtr old)
        {
            Width = w; Height = h; Stride = (w * 3 + 3) & ~3;
            _bmp = bmp; _bits = bits; _dc = dc; _old = old;
        }

        /// <summary>A new w x h picture, rows top-down, B,G,R per pixel; null if Windows can't make one.</summary>
        public static DibPicture Create(int w, int h)
        {
            if (w <= 0 || h <= 0) return null;
            BITMAPINFOHEADER bi = new BITMAPINFOHEADER();
            bi.biSize = 40; bi.biWidth = w; bi.biHeight = -h;      // negative: the first row is the top one
            bi.biPlanes = 1; bi.biBitCount = 24;
            IntPtr bits;
            IntPtr bmp = CreateDIBSection(IntPtr.Zero, ref bi, 0, out bits, IntPtr.Zero, 0);
            if (bmp == IntPtr.Zero || bits == IntPtr.Zero) { if (bmp != IntPtr.Zero) DeleteObject(bmp); return null; }
            IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
            if (dc == IntPtr.Zero) { DeleteObject(bmp); return null; }
            return new DibPicture(w, h, bmp, bits, dc, SelectObject(dc, bmp));
        }

        /// <summary>The pixels, for native code to write (anything GDI still had queued for them is finished first).</summary>
        public IntPtr Bits { get { GdiFlush(); return _bits; } }
        /// <summary>The device context the picture is selected into, to BitBlt from.</summary>
        public IntPtr Dc { get { return _dc; } }

        public void Dispose()
        {
            if (_gone) return;
            _gone = true;
            SelectObject(_dc, _old);
            DeleteDC(_dc);
            DeleteObject(_bmp);
        }
    }

    public static class PixelScroll
    {
        /// <summary>The pixel format of the screen, for bitmaps that are only ever copied to it (no conversion, and
        /// half the memory on a 16-bit desktop). 32-bit without alpha otherwise - GDI text can't draw on alpha bitmaps.</summary>
        public static readonly PixelFormat ScreenFormat = PickFormat();

        static PixelFormat PickFormat()
        {
            int bpp = 32;
            try { bpp = Screen.PrimaryScreen.BitsPerPixel; } catch (Exception) { }
            if (bpp == 16) return PixelFormat.Format16bppRgb565;
            if (bpp == 15) return PixelFormat.Format16bppRgb555;
            if (bpp == 24) return PixelFormat.Format24bppRgb;
            return PixelFormat.Format32bppRgb;
        }
    }

    /// <summary>Dark look for every ContextMenuStrip in the app (set once as ToolStripManager.Renderer).</summary>
    public sealed class DarkMenuRenderer : ToolStripRenderer
    {
        public static readonly Color Back = Color.FromArgb(40, 40, 40);
        static readonly Color Border = Color.FromArgb(70, 70, 70), Hot = Color.FromArgb(64, 64, 64), Sep = Color.FromArgb(62, 62, 62);
        static readonly Color ItemText = Color.FromArgb(215, 215, 215);

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using (SolidBrush b = new SolidBrush(Back)) e.Graphics.FillRectangle(b, e.AffectedBounds);
        }
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using (Pen p = new Pen(Border)) e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }
        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            using (SolidBrush b = new SolidBrush(Hot)) e.Graphics.FillRectangle(b, 2, 0, e.Item.Width - 4, e.Item.Height);
        }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            Color c = !e.Item.Enabled ? Theme.DimText : e.Item.Selected ? Color.White : ItemText;
            TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, e.TextRectangle, c, e.TextFormat);
        }
        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using (Pen p = new Pen(Sep)) e.Graphics.DrawLine(p, 8, y, e.Item.Width - 9, y);
        }
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = e.Item != null && e.Item.Selected ? Color.White : ItemText;
            base.OnRenderArrow(e);
        }
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            Rectangle r = e.ImageRectangle;
            int cx = r.Left + r.Width / 2, cy = r.Top + r.Height / 2;
            SmoothingMode old = e.Graphics.SmoothingMode;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen p = new Pen(Theme.Accent, 1.8f))
                e.Graphics.DrawLines(p, new Point[] { new Point(cx - 4, cy), new Point(cx - 1, cy + 3), new Point(cx + 4, cy - 3) });
            e.Graphics.SmoothingMode = old;
        }
    }

    public static class Menus
    {
        static bool _installed;

        /// <summary>A dark context menu. Also makes the dark renderer the app-wide default the first time.</summary>
        public static ContextMenuStrip New()
        {
            if (!_installed) { _installed = true; ToolStripManager.Renderer = new DarkMenuRenderer(); }
            ContextMenuStrip m = new ContextMenuStrip();
            m.ShowImageMargin = false;
            m.Font = Theme.Normal;
            m.BackColor = DarkMenuRenderer.Back;
            m.ForeColor = Theme.Text;
            return m;
        }

        /// <summary>Replaces a text box's (light, system-drawn) right-click menu with a dark one offering the same commands.</summary>
        public static void ForTextBox(TextBox box)
        {
            ContextMenuStrip m = New();
            ToolStripItem undo = m.Items.Add("Undo", null, delegate { box.Undo(); });
            m.Items.Add(new ToolStripSeparator());
            ToolStripItem cut = m.Items.Add("Cut", null, delegate { box.Cut(); });
            ToolStripItem copy = m.Items.Add("Copy", null, delegate { box.Copy(); });
            ToolStripItem paste = m.Items.Add("Paste", null, delegate { box.Paste(); });
            ToolStripItem del = m.Items.Add("Delete", null, delegate { box.SelectedText = ""; });
            m.Items.Add(new ToolStripSeparator());
            ToolStripItem all = m.Items.Add("Select All", null, delegate { box.SelectAll(); });
            m.Opening += delegate
            {
                if (!box.Focused) box.Focus();
                bool sel = box.SelectionLength > 0, canPaste = false;
                try { canPaste = Clipboard.ContainsText(); } catch { }
                undo.Enabled = box.CanUndo;
                cut.Enabled = sel; copy.Enabled = sel; del.Enabled = sel;
                paste.Enabled = canPaste;
                all.Enabled = box.TextLength > 0 && box.SelectionLength < box.TextLength;
            };
            box.ContextMenuStrip = m;
        }
    }

    /// <summary>Gives the window its proper small (16px) and large icon straight from the exe, so XP doesn't rescale one size.</summary>
    public static class AppIcon
    {
        [DllImport("shell32.dll", CharSet = CharSet.Auto)] static extern int ExtractIconEx(string file, int index, IntPtr[] large, IntPtr[] small, int count);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern IntPtr CreateIconFromResourceEx(byte[] bits, int size, bool icon, int ver, int cx, int cy, int flags);
        static IntPtr _small, _large;
        static bool _loaded;
        const int SmallBits = 32;      // taskbar icon: 32 = alpha-blended 16x16 frame, 24 = the matte frame

        /// <summary>The small (16x16) icon chosen for the taskbar; Zero until Apply has run.</summary>
        public static IntPtr Small { get { return _small; } }

        // HICON from the .ico frame with the given size and bit depth, picked here instead of by Windows or .NET:
        // on a 24-bit display XP ignores the 32-bit alpha frame and would otherwise fall back to the rough 8-bit one.
        static IntPtr Frame(byte[] ico, int size, int bits)
        {
            int n = ico[4] | (ico[5] << 8), best = -1;
            for (int i = 0; i < n; i++)
            {
                int e = 6 + 16 * i;
                int w = ico[e] == 0 ? 256 : ico[e], bpp = ico[e + 6] | (ico[e + 7] << 8);
                if (w == size && bpp == bits) { best = e; break; }
            }
            if (best < 0) return IntPtr.Zero;
            int len = BitConverter.ToInt32(ico, best + 8), off = BitConverter.ToInt32(ico, best + 12);
            byte[] img = new byte[len];
            Array.Copy(ico, off, img, 0, len);
            return CreateIconFromResourceEx(img, len, true, 0x00030000, size, size, 0);
        }

        public static void Apply(Form f)
        {
            try
            {
                if (!_loaded)
                {
                    _loaded = true;
                    IntPtr[] l = new IntPtr[1], s = new IntPtr[1];
                    if (ExtractIconEx(Application.ExecutablePath, 0, l, s, 1) > 0) { _large = l[0]; _small = s[0]; }
                    try
                    {
                        using (System.IO.Stream st = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("spotipaq.ico"))
                            if (st != null)
                            {
                                byte[] ico = new byte[st.Length];
                                int got = 0;
                                while (got < ico.Length) { int r = st.Read(ico, got, ico.Length - got); if (r <= 0) break; got += r; }
                                IntPtr h = Frame(ico, 16, SmallBits);
                                if (h == IntPtr.Zero) h = Frame(ico, 16, 24);
                                if (h != IntPtr.Zero) _small = h;
                            }
                    }
                    catch { }
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