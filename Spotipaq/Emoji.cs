// Emoji.cs - graphical emoji. XP has no colour emoji font, so text drawing (Theme.Draw) asks Match() where the
// emoji are and draws pictures from a sprite sheet instead.
//
//   resources\emoji.png      24 px cells, 64 per row - the same sheet and index DiscordXP uses; scaled down for
//                            ordinary text
//   resources\emoji_big.png  the same cells at 28 px, for the big page titles (drawn 1:1 there)
//   resources\emoji.txt      index: "E <tab> code points in hex, dash separated <tab> cell [<tab> T]"
//
// All three are embedded in the exe. Every emoji that has been shown is kept, at the size it was shown at, in
// cache\emoji.dat, and that file is read at start-up alongside the index: emoji seen before come straight from
// it and the sheets are never touched. Only an emoji not seen before needs a sheet, which is then decoded on a
// low-priority background thread and kept as its 8-bit colour indexes (about 5 MB for both, where full colour
// would be 21), and let go again after a minute in which nothing new was needed from it. Until a picture is
// ready its emoji is left blank, then SheetLoaded fires and the window repaints with it.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Spotipaq
{
    public static class Emoji
    {
        public const int Cell = 24;         // emoji.png
        public const int BigCell = 28;      // emoji_big.png
        const int Cols = 64;

        static readonly object _sync = new object();
        static bool _loaded;
        static readonly Sheet[] _sheet = new Sheet[2];               // 0 = emoji.png, 1 = emoji_big.png
        static readonly int[] _sheetState = new int[2];              // 0 not loaded, 1 loading, 2 done (_sheet may be null: missing)
        static readonly int[] _sheetUsed = new int[2];               // Environment.TickCount when a picture was last cut from it
        const int ReleaseAfterMs = 60000;
        static Timer _releaseTimer;
        /// <summary>Pictures became available (a sheet was decoded, or the saved ones were read). Raised on a
        /// background thread.</summary>
        public static event EventHandler SheetLoaded;
        /// <summary>True while pictures are on their way (the saved ones being read, or a sheet being decoded):
        /// ones that aren't there yet will be shortly.</summary>
        public static bool Loading
        {
            get
            {
                return !_diskReady || Thread.VolatileRead(ref _sheetState[0]) == 1 || Thread.VolatileRead(ref _sheetState[1]) == 1;
            }
        }
        static readonly Dictionary<string, int> _map = new Dictionary<string, int>();
        static readonly Dictionary<string, bool> _textDefault = new Dictionary<string, bool>();
        static readonly Dictionary<int, Bitmap> _cache = new Dictionary<int, Bitmap>();     // (size << 16 | cell) -> picture; under _sync

        /// <summary>A decoded sprite sheet: one colour index per pixel and the palette as premultiplied ARGB, or
        /// (if GDI+ didn't hand it over paletted) the pixels themselves.</summary>
        sealed class Sheet
        {
            public int Width, Height;
            public byte[] Index;        // Width x Height, with Palette
            public int[] Palette;       // 256 entries, premultiplied
            public int[] Pixels;        // Width x Height premultiplied, when Index is null

            public int At(int x, int y)
            {
                int i = y * Width + x;
                return Index != null ? Palette[Index[i]] : Pixels[i];
            }
        }

        static string KeyOf(string hexDashed)
        {
            StringBuilder sb = new StringBuilder();
            string[] parts = hexDashed.Split('-');
            for (int i = 0; i < parts.Length; i++)
            {
                int cp = Convert.ToInt32(parts[i], 16);
                if (cp == 0xFE0F) continue;
                sb.Append(char.ConvertFromUtf32(cp));
            }
            return sb.ToString();
        }

        /// <summary>Reads the index and the saved pictures in the background at start-up, so the first emoji
        /// neither makes the window wait nor needs a sheet.</summary>
        public static void Warm()
        {
            if (Interlocked.Exchange(ref _warmStarted, 1) != 0) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                Load();
                LoadSaved();
            });
        }

        static void Load()
        {
            if (_loaded) return;
            lock (_sync)
            {
                if (_loaded) return;
                LoadIndex();
                _loaded = true;
            }
        }

        static void LoadIndex()
        {
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("emoji.txt"))
                {
                    if (s == null) return;
                    using (StreamReader r = new StreamReader(s, Encoding.UTF8))
                    {
                        string line;
                        while ((line = r.ReadLine()) != null)
                        {
                            string[] f = line.Split('\t');
                            if (f.Length < 3 || f[0] != "E") continue;
                            string key = KeyOf(f[1]);
                            _map[key] = int.Parse(f[2]);
                            if (f.Length > 3 && f[3] == "T") _textDefault[key] = true;
                        }
                    }
                }
            }
            catch (Exception) { }
        }

        // ---- pictures already made, on disk -----------------------------------------------------------------------
        // cache\emoji.dat: "SPE1", a stamp of the embedded sheets (so a new build with different sheets starts
        // afresh), then one record per picture: key (size << 16 | cell), side, side x side x 4 bytes PArgb.
        // New pictures are added to the end; the file starts again if it ever passes MaxSavedBytes.

        const long MaxSavedBytes = 8 * 1024 * 1024;
        static volatile bool _diskReady;
        static int _warmStarted;
        static bool _askedEarly;                 // a picture was asked for before the saved ones were in: repaint after
        static int _stamp;
        static readonly object _fileLock = new object();
        static readonly Queue<byte[]> _toSave = new Queue<byte[]>();
        static bool _saving;

        static string SavedPath
        {
            get { return Path.Combine(Path.Combine(Spotipaq.Engine.Log.Folder, "cache"), "emoji.dat"); }
        }

        static int Stamp()
        {
            int stamp = 17;
            Assembly a = Assembly.GetExecutingAssembly();
            foreach (string name in new string[] { "emoji.png", "emoji_big.png", "emoji.txt" })
            {
                try
                {
                    using (Stream s = a.GetManifestResourceStream(name))
                        stamp = stamp * 31 + (s == null ? 0 : (int)s.Length);
                }
                catch (Exception) { }
            }
            return stamp;
        }

        static void LoadSaved()
        {
            int count = 0;
            try
            {
                _stamp = Stamp();
                string path = SavedPath;
                if (!File.Exists(path)) return;
                byte[] d = File.ReadAllBytes(path);
                bool ok = d.Length >= 8 && d.Length <= MaxSavedBytes && d[0] == (byte)'S' && d[1] == (byte)'P' && d[2] == (byte)'E' && d[3] == (byte)'1'
                    && BitConverter.ToInt32(d, 4) == _stamp;
                if (!ok) { try { File.Delete(path); } catch { } return; }
                Dictionary<int, Bitmap> found = new Dictionary<int, Bitmap>();
                int p = 8;
                while (p + 8 <= d.Length)
                {
                    int key = BitConverter.ToInt32(d, p), side = BitConverter.ToInt32(d, p + 4);
                    if (side < 4 || side > 128 || (key >> 16) != side || p + 8 + side * side * 4 > d.Length) break;     // a cut-off last record: ignore the rest
                    found[key] = FromBytes(d, p + 8, side);
                    p += 8 + side * side * 4;
                }
                lock (_sync)
                {
                    foreach (KeyValuePair<int, Bitmap> kv in found)
                        if (!_cache.ContainsKey(kv.Key)) { _cache[kv.Key] = kv.Value; count++; }
                }
            }
            catch (Exception ex) { Spotipaq.Engine.Log.Write("Saved emoji not read: " + ex.Message); }
            finally
            {
                bool repaint;
                lock (_sync) { _diskReady = true; repaint = _askedEarly; }
                if (repaint) Raise();
            }
        }

        static Bitmap FromBytes(byte[] d, int offset, int side)
        {
            Bitmap bmp = new Bitmap(side, side, PixelFormat.Format32bppPArgb);
            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, side, side), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try
            {
                for (int y = 0; y < side; y++)
                    Marshal.Copy(d, offset + y * side * 4, new IntPtr(bd.Scan0.ToInt64() + (long)y * bd.Stride), side * 4);
            }
            finally { bmp.UnlockBits(bd); }
            return bmp;
        }

        /// <summary>Adds a new picture to the end of the file, on a pool thread, so painting never waits on the disk.</summary>
        static void Save(int key, Bitmap b)
        {
            int side = b.Width;
            byte[] rec = new byte[8 + side * side * 4];
            BitConverter.GetBytes(key).CopyTo(rec, 0);
            BitConverter.GetBytes(side).CopyTo(rec, 4);
            BitmapData bd = b.LockBits(new Rectangle(0, 0, side, side), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                for (int y = 0; y < side; y++)
                    Marshal.Copy(new IntPtr(bd.Scan0.ToInt64() + (long)y * bd.Stride), rec, 8 + y * side * 4, side * 4);
            }
            finally { b.UnlockBits(bd); }
            lock (_fileLock)
            {
                _toSave.Enqueue(rec);
                if (_saving) return;
                _saving = true;
            }
            ThreadPool.QueueUserWorkItem(delegate { WriteSaved(); });
        }

        static void WriteSaved()
        {
            while (true)
            {
                List<byte[]> recs = new List<byte[]>();
                lock (_fileLock)
                {
                    while (_toSave.Count > 0) recs.Add(_toSave.Dequeue());
                    if (recs.Count == 0) { _saving = false; return; }
                }
                try
                {
                    string path = SavedPath;
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    FileInfo fi = new FileInfo(path);
                    bool fresh = !fi.Exists || fi.Length < 8 || fi.Length > MaxSavedBytes;
                    using (FileStream fs = new FileStream(path, fresh ? FileMode.Create : FileMode.Append, FileAccess.Write, FileShare.Read))
                    {
                        if (fresh)
                        {
                            fs.Write(new byte[] { (byte)'S', (byte)'P', (byte)'E', (byte)'1' }, 0, 4);
                            fs.Write(BitConverter.GetBytes(_stamp), 0, 4);
                        }
                        foreach (byte[] r in recs) fs.Write(r, 0, r.Length);
                    }
                }
                catch (Exception ex) { Spotipaq.Engine.Log.Write("Emoji not saved: " + ex.Message); }
            }
        }

        static void Raise()
        {
            EventHandler h = SheetLoaded;
            if (h != null) try { h(null, EventArgs.Empty); } catch (Exception) { }
        }

        // ---- the sheets, only for emoji not seen before -----------------------------------------------------------

        // Kept as the PNG's own colour indexes: no conversion of the whole sheet to 32-bit, and a quarter of the
        // memory. Pictures are cut from it pixel by pixel through the palette.
        static Sheet LoadSheet(string name)
        {
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
                {
                    if (s == null) return null;
                    using (Bitmap src = new Bitmap(s))
                    {
                        Sheet sh = new Sheet();
                        sh.Width = src.Width; sh.Height = src.Height;
                        Rectangle all = new Rectangle(0, 0, sh.Width, sh.Height);
                        if (src.PixelFormat == PixelFormat.Format8bppIndexed)
                        {
                            Color[] pal = src.Palette.Entries;
                            sh.Palette = new int[256];
                            for (int i = 0; i < pal.Length && i < 256; i++) sh.Palette[i] = Premultiply(pal[i]);
                            sh.Index = new byte[sh.Width * sh.Height];
                            BitmapData bd = src.LockBits(all, ImageLockMode.ReadOnly, PixelFormat.Format8bppIndexed);
                            try
                            {
                                for (int y = 0; y < sh.Height; y++)
                                    Marshal.Copy(new IntPtr(bd.Scan0.ToInt64() + (long)y * bd.Stride), sh.Index, y * sh.Width, sh.Width);
                            }
                            finally { src.UnlockBits(bd); }
                        }
                        else
                        {
                            sh.Pixels = new int[sh.Width * sh.Height];
                            BitmapData bd = src.LockBits(all, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                            try
                            {
                                for (int y = 0; y < sh.Height; y++)
                                    Marshal.Copy(new IntPtr(bd.Scan0.ToInt64() + (long)y * bd.Stride), sh.Pixels, y * sh.Width, sh.Width);
                            }
                            finally { src.UnlockBits(bd); }
                        }
                        return sh;
                    }
                }
            }
            catch (Exception) { return null; }
        }

        static int Premultiply(Color c)
        {
            int a = c.A;
            if (a == 255) return c.ToArgb();
            if (a == 0) return 0;
            int r = (c.R * a + 127) / 255, g = (c.G * a + 127) / 255, b = (c.B * a + 127) / 255;
            return (a << 24) | (r << 16) | (g << 8) | b;
        }

        /// <summary>The sheet, or null while it is still loading (pending = true) or if it is missing. The first call
        /// starts the load on a low-priority background thread.</summary>
        static Sheet GetSheet(int which, out bool pending)
        {
            int st = Thread.VolatileRead(ref _sheetState[which]);
            if (st == 0 && Interlocked.CompareExchange(ref _sheetState[which], 1, 0) == 0)
            {
                Thread t = new Thread(delegate ()
                {
                    Sheet sh = LoadSheet(which == 0 ? "emoji.png" : "emoji_big.png");
                    lock (_sync)
                    {
                        _sheet[which] = sh;
                        _sheetUsed[which] = Environment.TickCount;
                        if (sh != null && _releaseTimer == null)
                            _releaseTimer = new Timer(delegate { ReleaseIdle(); }, null, ReleaseAfterMs / 2, ReleaseAfterMs / 2);
                    }
                    Thread.VolatileWrite(ref _sheetState[which], 2);
                    Raise();
                });
                t.IsBackground = true; t.Priority = ThreadPriority.BelowNormal; t.Name = "Emoji sheet";
                t.Start();
                st = 1;
            }
            pending = st != 2;
            if (pending) return null;
            lock (_sync)
            {
                if (Thread.VolatileRead(ref _sheetState[which]) == 2)
                {
                    _sheetUsed[which] = Environment.TickCount;
                    return _sheet[which];
                }
            }
            return GetSheet(which, out pending);         // let go of just now (see ReleaseIdle): load it again
        }

        // Timer thread: lets go of a sheet nothing new has been cut from for a minute. A missing sheet (null) is
        // remembered as missing rather than tried again.
        static void ReleaseIdle()
        {
            lock (_sync)
            {
                bool any = false;
                for (int w = 0; w < 2; w++)
                {
                    if (_sheet[w] == null || Thread.VolatileRead(ref _sheetState[w]) != 2) continue;
                    if (unchecked(Environment.TickCount - _sheetUsed[w]) >= ReleaseAfterMs)
                    {
                        _sheet[w] = null;
                        Thread.VolatileWrite(ref _sheetState[w], 0);
                    }
                    else any = true;
                }
                if (!any && _sheet[0] == null && _sheet[1] == null && _releaseTimer != null)
                {
                    _releaseTimer.Dispose();
                    _releaseTimer = null;
                }
            }
        }

        static bool Continues(char next, char prev)
        {
            return next == 0xFE0F || next == 0x200D || next == 0x20E3 || next == 0xD83C || next == 0xDB40 || prev == 0x200D;
        }

        /// <summary>Cell of the longest emoji sequence starting at s[i], and its length in chars; -1 if there is none.</summary>
        public static int Match(string s, int i, out int len)
        {
            len = 0;
            if (i >= s.Length) return -1;
            char c = s[i];
            if (c < 0x203C && !((c >= '0' && c <= '9') || c == '#' || c == '*')) return -1;
            if (c >= 0x2000 && c < 0x2190 && c != 0x203C && c != 0x2049 && c != 0x2122 && c != 0x2139) return -1;
            if (c >= 0x2E80 && c < 0xD800 && c != 0x3030 && c != 0x303D && c != 0x3297 && c != 0x3299) return -1;
            if (c >= 0xDC00 && c < 0xE000) return -1;
            if (c >= 0xE000) return -1;
            Load();
            if (_map.Count == 0) return -1;

            char[] buf = new char[40];
            int n = 0, j = i, best = -1, bestLen = 0;
            while (j < s.Length && n < 36)
            {
                char ch = s[j];
                if (ch == 0xFE0F) { j++; continue; }
                buf[n++] = ch; j++;
                if (char.IsHighSurrogate(ch) && j < s.Length) { buf[n++] = s[j]; j++; }
                string key = new string(buf, 0, n);
                int cell;
                if (_map.TryGetValue(key, out cell))
                {
                    bool ok = true;
                    if (_textDefault.ContainsKey(key) && !(i + 1 < s.Length && s[i + 1] == 0xFE0F)) ok = false;
                    if (ok) { best = cell; bestLen = j - i; }
                }
                if (j >= s.Length) break;
                if (s[j] == 0xFE0F && best >= 0 && bestLen == j - i) { bestLen = j + 1 - i; }      // a trailing FE0F belongs to the sequence
                if (!Continues(s[j], ch)) break;
            }
            len = bestLen;
            return best;
        }

        /// <summary>The emoji in the given cell as a size x size picture, or null when it isn't ready yet (see
        /// Loading) or the sheets are missing. Sizes above 24 come from the big sheet, the rest from the small one;
        /// each is made once, kept, and saved for next time.</summary>
        public static Bitmap Get(int cell, int size)
        {
            if (cell < 0 || size < 4 || size > 128) return null;
            int key = (size << 16) | cell;
            Bitmap b;
            lock (_sync)
            {
                if (_cache.TryGetValue(key, out b)) return b;
                if (!_diskReady) _askedEarly = true;
            }
            if (!_diskReady) { Warm(); return null; }      // the saved ones are still being read
            Sheet sheet = null; int cs = Cell;
            bool pending = false, p;
            if (size > Cell) { sheet = GetSheet(1, out p); pending |= p; cs = BigCell; }
            if (sheet == null && !pending) { sheet = GetSheet(0, out p); pending |= p; cs = Cell; }
            if (pending) return null;                    // not remembered: ask again once the sheet is in (see SheetLoaded)
            bool made = false;
            if (sheet != null)
            {
                int sx = (cell % Cols) * cs, sy = (cell / Cols) * cs;
                if (sy + cs <= sheet.Height && sx + cs <= sheet.Width)
                {
                    try { b = Cut(sheet, sx, sy, cs, size); made = b != null; }
                    catch (Exception) { b = null; }
                }
            }
            lock (_sync) _cache[key] = b;
            if (made) try { Save(key, b); } catch (Exception) { }
            return b;
        }

        // The cell straight out of the sheet at its own size, then scaled if a different size is wanted (scaling
        // just the one cell, so nothing bleeds in from its neighbours).
        static Bitmap Cut(Sheet sheet, int sx, int sy, int cs, int size)
        {
            Bitmap cellPic = new Bitmap(cs, cs, PixelFormat.Format32bppPArgb);
            int[] row = new int[cs];
            BitmapData bd = cellPic.LockBits(new Rectangle(0, 0, cs, cs), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try
            {
                for (int y = 0; y < cs; y++)
                {
                    for (int x = 0; x < cs; x++) row[x] = sheet.At(sx + x, sy + y);
                    Marshal.Copy(row, 0, new IntPtr(bd.Scan0.ToInt64() + (long)y * bd.Stride), cs);
                }
            }
            finally { cellPic.UnlockBits(bd); }
            if (size == cs) return cellPic;
            using (cellPic)
            {
                Bitmap b = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(b))
                using (ImageAttributes ia = new ImageAttributes())
                {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    ia.SetWrapMode(WrapMode.TileFlipXY);            // the edges stay clean
                    g.DrawImage(cellPic, new Rectangle(0, 0, size, size), 0, 0, cs, cs, GraphicsUnit.Pixel, ia);
                }
                return b;
            }
        }

        /// <summary>The text without its emoji (for the window / taskbar title, which Windows draws itself).</summary>
        public static string Strip(string s)
        {
            if (String.IsNullOrEmpty(s)) return s;
            StringBuilder sb = null;
            for (int i = 0; i < s.Length; i++)
            {
                int len;
                if (Match(s, i, out len) >= 0 && len > 0)
                {
                    if (sb == null) { sb = new StringBuilder(s.Length); sb.Append(s, 0, i); }
                    i += len - 1;
                    continue;
                }
                if (sb != null) sb.Append(s[i]);
            }
            if (sb == null) return s;
            string t = sb.ToString();
            while (t.IndexOf("  ") >= 0) t = t.Replace("  ", " ");
            return t.Trim();
        }
    }
}