// Emoji.cs - graphical emoji. XP has no colour emoji font, so text drawing (Theme.Draw) asks Match() where the
// emoji are and draws pictures from a sprite sheet instead.
//
//   resources\emoji.png      24 px cells, 64 per row - the same sheet and index DiscordXP uses; scaled down for
//                            ordinary text
//   resources\emoji_big.png  the same cells at 28 px, for the big page titles (drawn 1:1 there)
//   resources\emoji.txt      index: "E <tab> code points in hex, dash separated <tab> cell [<tab> T]"
//
// All three are embedded in the exe. A sheet is only decoded the first time something needs it.
// Matching ignores U+FE0F, except for symbols that are text by default ("T"): those only become an emoji when
// followed by FE0F.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Text;

namespace Spotipaq
{
    public static class Emoji
    {
        public const int Cell = 24;         // emoji.png
        public const int BigCell = 28;      // emoji_big.png
        const int Cols = 64;

        static bool _loaded, _triedSmall, _triedBig;
        static Bitmap _small, _big;
        static readonly Dictionary<string, int> _map = new Dictionary<string, int>();
        static readonly Dictionary<string, bool> _textDefault = new Dictionary<string, bool>();
        static readonly Dictionary<int, Bitmap> _cache = new Dictionary<int, Bitmap>();     // (size << 16 | cell) -> picture

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

        static void Load()
        {
            if (_loaded) return;
            _loaded = true;
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

        // decoded once into a plain 32-bit bitmap (a stream-backed one would be decoded again on every draw)
        static Bitmap LoadSheet(string name)
        {
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
                {
                    if (s == null) return null;
                    using (Bitmap src = new Bitmap(s))
                    {
                        Bitmap a = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppPArgb);
                        using (Graphics g = Graphics.FromImage(a))
                        {
                            g.CompositingMode = CompositingMode.SourceCopy;
                            g.DrawImage(src, 0, 0, src.Width, src.Height);
                        }
                        return a;
                    }
                }
            }
            catch (Exception) { return null; }
        }

        static Bitmap Small { get { if (!_triedSmall) { _triedSmall = true; _small = LoadSheet("emoji.png"); } return _small; } }
        static Bitmap Big { get { if (!_triedBig) { _triedBig = true; _big = LoadSheet("emoji_big.png"); } return _big; } }

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

        /// <summary>The emoji in the given cell as a size x size picture, or null when the sheets are missing.
        /// Sizes above 24 come from the big sheet, the rest from the small one; each is scaled once and kept.</summary>
        public static Bitmap Get(int cell, int size)
        {
            if (cell < 0 || size < 4 || size > 128) return null;
            int key = (size << 16) | cell;
            Bitmap b;
            if (_cache.TryGetValue(key, out b)) return b;
            Bitmap sheet = null; int cs = Cell;
            if (size > Cell) { sheet = Big; cs = BigCell; }
            if (sheet == null) { sheet = Small; cs = Cell; }
            if (sheet != null)
            {
                int sx = (cell % Cols) * cs, sy = (cell / Cols) * cs;
                if (sy + cs <= sheet.Height)
                {
                    try
                    {
                        b = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
                        using (Graphics g = Graphics.FromImage(b))
                        using (ImageAttributes ia = new ImageAttributes())
                        {
                            g.CompositingMode = CompositingMode.SourceCopy;
                            g.PixelOffsetMode = PixelOffsetMode.Half;
                            g.InterpolationMode = size == cs ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
                            ia.SetWrapMode(WrapMode.TileFlipXY);            // no bleed from the neighbouring cells
                            g.DrawImage(sheet, new Rectangle(0, 0, size, size), sx, sy, cs, cs, GraphicsUnit.Pixel, ia);
                        }
                    }
                    catch (Exception) { b = null; }
                }
            }
            _cache[key] = b;
            return b;
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
