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
        public static readonly Color RowSelected = Color.FromArgb(64, 64, 64);
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
        public static readonly Font Small = Make(8.25f, FontStyle.Regular);
        public static readonly Font SmallBold = Make(8.25f, FontStyle.Bold);
        // the artist line in the player bar: one pixel smaller than Small (10 px against 11; text sizes go in whole pixels)
        public static readonly Font BarArtist = new Font(FontFamilyName, 10f, FontStyle.Regular, GraphicsUnit.Pixel);
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

        /// <summary>Turns a control into a rounded card: paints the four corners outside the rounding in the colour
        /// behind it (call last in OnPaint, with the control's client rectangle).</summary>
        public static void CardCorners(Graphics g, Rectangle r, int rad, Color outside)
        {
            if (r.Width < rad * 2 || r.Height < rad * 2) return;
            int d = rad * 2, x = r.Left, y = r.Top, x2 = r.Right, y2 = r.Bottom;
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
    /// Asynchronous, disk-backed artwork cache. Only scaled 32-bit thumbnails are held
    /// in memory (4 MiB). The compressed disk cache persists across runs (256 MiB).
    /// Image decoding, scaling and file operations all run on one BelowNormal worker.
    /// Get is called on the UI thread; Loaded is raised on that same thread.
    /// </summary>
    public sealed class ImageCache
    {
        public event EventHandler Loaded;
        readonly ISpotifyBackend _backend;
        readonly Control _ui;
        readonly Dictionary<string, Bitmap> _scaled = new Dictionary<string, Bitmap>();
        readonly LinkedList<string> _lru = new LinkedList<string>();
        readonly Dictionary<string, LinkedListNode<string>> _nodes = new Dictionary<string, LinkedListNode<string>>();
        readonly Dictionary<string, bool> _requested = new Dictionary<string, bool>();
        readonly Queue<ArtRequest> _queue = new Queue<ArtRequest>();
        readonly object _lock = new object();
        readonly string _folder;
        const long MaxDiskBytes = 256L * 1024 * 1024;
        const long MaxMemoryBytes = 4L * 1024 * 1024;
        long _memoryBytes;
        int _writesSinceTrim;

        sealed class ArtRequest { public string Url; public int Width; public int Height; public string Key; }

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
        public Bitmap Get(string url, int w, int h)
        {
            if (String.IsNullOrEmpty(url) || w <= 0 || h <= 0) return null;
            string key = url + "@" + w + "x" + h;
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
                if (!_requested.ContainsKey(key))
                {
                    _requested[key] = true;
                    _queue.Enqueue(new ArtRequest { Url = url, Width = w, Height = h, Key = key });
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

        static string FileName(string url)
        {
            byte[] data = Encoding.UTF8.GetBytes(url);
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(data);
                StringBuilder hex = new StringBuilder(68);
                for (int i = 0; i < hash.Length; i++) hex.Append(hash[i].ToString("x2"));
                return hex.ToString() + ".jpg";
            }
        }

        static Bitmap Scale(Bitmap src, int w, int h)
        {
            Bitmap dst = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(dst))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                float sa = (float)src.Width / src.Height, da = (float)w / h;
                RectangleF from = sa > da ? new RectangleF((src.Width - src.Height * da) / 2, 0, src.Height * da, src.Height)
                                          : new RectangleF(0, (src.Height - src.Width / da) / 2, src.Width, src.Width / da);
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

        void Worker()
        {
            try { Directory.CreateDirectory(_folder); TrimDisk(); }
            catch (Exception ex) { Spotipaq.Engine.Log.Write("Artwork cache init failed: " + ex.Message); }
            while (true)
            {
                ArtRequest req;
                lock (_lock)
                {
                    while (_queue.Count == 0) Monitor.Wait(_lock);
                    req = _queue.Dequeue();
                }
                Bitmap result = null;
                try
                {
                    string path = Path.Combine(_folder, FileName(req.Url));
                    Bitmap original = null;
                    if (File.Exists(path))
                    {
                        try
                        {
                            // Clone so the cached file is never locked while in use.
                            using (Image cached = Image.FromFile(path)) original = new Bitmap(cached);
                            // Touch at most daily to avoid a disk write on every cache hit.
                            if ((DateTime.UtcNow - File.GetLastWriteTimeUtc(path)).TotalDays >= 1)
                                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                        }
                        catch
                        {
                            if (original != null) original.Dispose();
                            original = null;
                            try { File.Delete(path); } catch { }
                        }
                    }
                    if (original == null)
                    {
                        original = _backend.LoadImage(req.Url);
                        if (original != null)
                        {
                            try
                            {
                                // Write atomically; an interrupted run cannot poison the cache.
                                string temp = path + ".tmp";
                                original.Save(temp, ImageFormat.Jpeg);
                                if (File.Exists(path)) File.Delete(temp);
                                else File.Move(temp, path);
                                if (++_writesSinceTrim >= 32) { _writesSinceTrim = 0; TrimDisk(); }
                            }
                            catch (Exception ex) { Spotipaq.Engine.Log.Write("Artwork cache write failed: " + ex.Message); }
                        }
                    }
                    if (original != null)
                    {
                        using (original) result = Scale(original, req.Width, req.Height);
                    }
                }
                catch (Exception ex) { Spotipaq.Engine.Log.Write("Artwork load failed: " + ex.Message); }

                if (result == null)
                {
                    lock (_lock) _requested.Remove(req.Key);
                    continue;
                }
                try
                {
                    if (_ui.IsHandleCreated && !_ui.IsDisposed)
                    {
                        Bitmap ready = result;
                        _ui.BeginInvoke(new MethodInvoker(delegate { Publish(req, ready); }));
                        result = null;
                    }
                }
                catch (InvalidOperationException) { }
                if (result != null)
                {
                    result.Dispose();
                    lock (_lock) _requested.Remove(req.Key);
                }
            }
        }

        // Worker thread only; startup and every 32 new cached images, not per frame.
        void TrimDisk()
        {
            FileInfo[] files = new DirectoryInfo(_folder).GetFiles("*.jpg");
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

        public SlimScroll(Control owner, ScrollGetter get, ScrollGetter max, Action<int> set, int line, Color track, Color thumb, Color lit)
        {
            _c = owner; _get = get; _max = max; _set = set; _line = line; _track = track; _thumb = thumb; _lit = lit;
            _repeat.Tick += delegate
            {
                if (_held == 0) { _repeat.Stop(); return; }
                _repeat.Interval = 50;
                if (_hot != _held) return;         // pointer slid off the button: wait for it to come back
                _set(_get() + _held * _line);
                _c.Invalidate();
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
            _c.Invalidate();
            return true;
        }

        /// <summary>True while the scrollbar is using the mouse or the pointer is over it.</summary>
        public bool MouseMove(MouseEventArgs e)
        {
            if (_drag)
            {
                int room = Math.Max(1, TrackH - Thumb().Height);
                _set((int)((long)(e.Y - _dragOffset - BtnH - Top) * _max() / room));
                _c.Invalidate();
                return true;
            }
            int btn = ButtonAt(e.Location);
            bool onThumb = false;
            if (btn == 0 && Contains(e.Location)) { Rectangle t = Thumb(); onThumb = e.Y >= t.Top && e.Y < t.Bottom; }
            if (btn != _hot || onThumb != _hotThumb) { _hot = btn; _hotThumb = onThumb; _c.Invalidate(); }
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
            _c.Invalidate();
            return true;
        }

        public void MouseLeave() { if (_hot != 0 || _hotThumb) { _hot = 0; _hotThumb = false; _c.Invalidate(); } }
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