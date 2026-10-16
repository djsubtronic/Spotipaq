// PageView.cs - the main content area. A page is ONE owner-drawn, double-buffered control holding a vertical
// stack of "blocks" (header, section title, track list, tile grid...). No child controls/HWNDs per row or
// tile: on the Pentium II that keeps scrolling and hover cheap (one paint, only the visible rows).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Spotipaq
{
    /// <summary>What blocks need from the main window.</summary>
    public interface IPageHost
    {
        ImageCache Images { get; }
        Track PlayingTrack { get; }
        bool IsPlaying { get; }
        void Navigate(NavTarget target);
        void PlayList(List<Track> tracks, int index);
        void TogglePlayPause();
        void ShowTrackMenu(List<Track> list, int index);     // right-click on a song
        void ShowNavMenu(NavTarget target);                  // right-click on an album / artist / playlist
    }

    public abstract class Block
    {
        public int Top, Height;                       // in content coordinates (set by PageView.Relayout)
        public abstract int Layout(int width);
        public abstract void Paint(Graphics g, Rectangle r, PageView view);   // r = on-screen bounds
        public virtual bool MouseMove(Point p, PageView view) { return false; } // true = repaint
        public virtual bool MouseLeave() { return false; }
        public virtual void Click(Point p, MouseButtons b, PageView view) { }
        public virtual void DoubleClick(Point p, PageView view) { }
        public virtual bool IsLink(Point p) { return false; }
    }

    public sealed class PageView : Control
    {
        readonly List<Block> _blocks = new List<Block>();
        readonly IPageHost _host;
        int _scroll, _contentHeight, _layoutWidth = -1;
        Block _hot;
        bool _dragThumb;
        int _dragOffset;
        const int BarW = 8;

        public PageView(IPageHost host)
        {
            _host = host;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Theme.Main;
            ForeColor = Theme.Text;
        }

        public IPageHost Host { get { return _host; } }

        public void SetBlocks(IList<Block> blocks)
        {
            _blocks.Clear();
            _blocks.AddRange(blocks);
            _scroll = 0;
            _hot = null;
            _layoutWidth = -1;
            Invalidate();
        }

        public void Relayout() { _layoutWidth = -1; Invalidate(); }

        void EnsureLayout()
        {
            int w = ClientSize.Width - BarW;
            if (w == _layoutWidth) return;
            _layoutWidth = w;
            int y = 0;
            foreach (Block b in _blocks) { b.Top = y; b.Height = b.Layout(w); y += b.Height; }
            _contentHeight = y + 24;
            ClampScroll();
        }

        int MaxScroll { get { return Math.Max(0, _contentHeight - ClientSize.Height); } }
        void ClampScroll() { _scroll = Math.Max(0, Math.Min(_scroll, MaxScroll)); }

        public void ScrollBy(int dy) { _scroll += dy; ClampScroll(); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            EnsureLayout();
            Graphics g = e.Graphics;
            g.Clear(Theme.Main);
            Rectangle clip = e.ClipRectangle;
            foreach (Block b in _blocks)
            {
                Rectangle r = new Rectangle(0, b.Top - _scroll, _layoutWidth, b.Height);
                if (r.Bottom < clip.Top || r.Top > clip.Bottom) continue;
                b.Paint(g, r, this);
            }
            // thin scrollbar (Spotify-style), only when needed
            if (MaxScroll > 0)
            {
                Rectangle thumb = Thumb();
                using (SolidBrush track = new SolidBrush(Color.FromArgb(30, 30, 30))) g.FillRectangle(track, ClientSize.Width - BarW, 0, BarW, ClientSize.Height);
                using (SolidBrush tb = new SolidBrush(_dragThumb ? Color.FromArgb(150, 150, 150) : Color.FromArgb(90, 90, 90))) g.FillRectangle(tb, thumb);
            }
        }

        Rectangle Thumb()
        {
            int h = ClientSize.Height;
            int th = Math.Max(30, (int)((long)h * h / Math.Max(1, _contentHeight)));
            int ty = MaxScroll == 0 ? 0 : (int)((long)(h - th) * _scroll / MaxScroll);
            return new Rectangle(ClientSize.Width - BarW + 1, ty, BarW - 2, th);
        }

        Block BlockAt(int contentY, out Point local, int x)
        {
            foreach (Block b in _blocks)
                if (contentY >= b.Top && contentY < b.Top + b.Height) { local = new Point(x, contentY - b.Top); return b; }
            local = Point.Empty;
            return null;
        }

        protected override void OnMouseWheel(MouseEventArgs e) { ScrollBy(-e.Delta / 120 * 80); base.OnMouseWheel(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            if (MaxScroll > 0 && e.X >= ClientSize.Width - BarW)
            {
                Rectangle t = Thumb();
                if (t.Contains(e.Location)) { _dragThumb = true; _dragOffset = e.Y - t.Top; Capture = true; }
                else ScrollBy(e.Y < t.Top ? -ClientSize.Height : ClientSize.Height);
                return;
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_dragThumb) { _dragThumb = false; Capture = false; Invalidate(); return; }
            Point local;
            Block b = BlockAt(e.Y + _scroll, out local, e.X);
            if (b != null) b.Click(local, e.Button, this);
            base.OnMouseUp(e);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            Point local;
            Block b = BlockAt(e.Y + _scroll, out local, e.X);
            if (b != null && e.Button == MouseButtons.Left) b.DoubleClick(local, this);
            base.OnMouseDoubleClick(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_dragThumb)
            {
                int h = ClientSize.Height, th = Thumb().Height;
                _scroll = (int)((long)(e.Y - _dragOffset) * MaxScroll / Math.Max(1, h - th));
                ClampScroll();
                Invalidate();
                return;
            }
            Point local;
            Block b = BlockAt(e.Y + _scroll, out local, e.X);
            bool repaint = false;
            if (_hot != null && _hot != b) repaint |= _hot.MouseLeave();
            _hot = b;
            if (b != null) repaint |= b.MouseMove(local, this);
            Cursor = b != null && b.IsLink(local) ? Cursors.Hand : Cursors.Default;
            if (repaint) Invalidate();
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_hot != null && _hot.MouseLeave()) Invalidate();
            _hot = null;
            base.OnMouseLeave(e);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            Keys k = keyData & Keys.KeyCode;
            return k == Keys.Up || k == Keys.Down || k == Keys.PageUp || k == Keys.PageDown || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Down) ScrollBy(40);
            else if (e.KeyCode == Keys.Up) ScrollBy(-40);
            else if (e.KeyCode == Keys.PageDown) ScrollBy(ClientSize.Height - 40);
            else if (e.KeyCode == Keys.PageUp) ScrollBy(-(ClientSize.Height - 40));
            else if (e.KeyCode == Keys.Home) ScrollBy(-_contentHeight);
            else if (e.KeyCode == Keys.End) ScrollBy(_contentHeight);
            base.OnKeyDown(e);
        }
    }

    // =====================================================================================================
    // Blocks
    // =====================================================================================================

    public sealed class PageButton
    {
        public string Text; public bool Filled; public int Width; public EventHandler Action;
        public PageButton(string text, bool filled, int width, EventHandler action) { Text = text; Filled = filled; Width = width; Action = action; }
    }

    /// <summary>Album / playlist header: artwork, small type label, huge title, subtitle, buttons.</summary>
    public sealed class HeaderBlock : Block
    {
        readonly string _label, _title, _imageUrl;
        readonly string _byName; readonly NavTarget _byTarget; readonly string _subtitle;
        readonly List<PageButton> _buttons;
        int _hotButton = -1; bool _hotBy;
        const int Art = 160, Pad = 30, PadY = 22, TextX = Pad + Art + 24;

        public HeaderBlock(string label, string title, string byName, NavTarget byTarget, string subtitle, string imageUrl, List<PageButton> buttons)
        { _label = label; _title = title; _byName = byName; _byTarget = byTarget; _subtitle = subtitle; _imageUrl = imageUrl; _buttons = buttons; }

        public override int Layout(int width) { return Art + PadY + 10; }

        Rectangle ButtonRect(int i)
        {
            int x = TextX;
            for (int j = 0; j < i; j++) x += _buttons[j].Width + 12;
            return new Rectangle(x, PadY + Art - 30, _buttons[i].Width, 30);
        }
        Rectangle ByRect(Graphics g)
        {
            return new Rectangle(TextX + 18, PadY + 66, Theme.Measure(_byName ?? "", Theme.NormalBold).Width, 20);
        }

        public override void Paint(Graphics g, Rectangle r, PageView view)
        {
            Rectangle art = new Rectangle(r.Left + Pad, r.Top + PadY, Art, Art);
            Theme.Art(g, view.Host.Images.Get(_imageUrl, Art, Art), art, false);
            int x = r.Left + TextX, w = r.Width - TextX - Pad;
            Theme.Text1(g, _label, Theme.Label, new Rectangle(x, r.Top + PadY + 2, w, 16), Theme.SubText);
            Theme.Text1(g, _title, Theme.Huge, new Rectangle(x - 2, r.Top + PadY + 20, w, 40), Theme.Text);
            int y = r.Top + PadY + 66;
            if (!String.IsNullOrEmpty(_byName))
            {
                Theme.Text1(g, "By", Theme.Normal, new Rectangle(x, y, 20, 20), Theme.SubText);
                int bw = Theme.Measure(_byName, Theme.NormalBold).Width;
                Color c = _hotBy ? Theme.Text : Color.FromArgb(230, 230, 230);
                Theme.Text1(g, _byName, Theme.NormalBold, new Rectangle(x + 18, y, bw + 4, 20), c);
                if (_hotBy) using (Pen u = new Pen(c)) g.DrawLine(u, x + 21, y + 16, x + 18 + bw - 4, y + 16);
                Theme.Text1(g, " • " + _subtitle, Theme.Normal, new Rectangle(x + 18 + bw - 4, y, w - bw, 20), Theme.SubText);
            }
            else Theme.Text1(g, _subtitle, Theme.Normal, new Rectangle(x, y, w, 20), Theme.SubText);
            for (int i = 0; i < _buttons.Count; i++)
            {
                Rectangle b = ButtonRect(i); b.Offset(r.Left, r.Top);
                Theme.PillButton(g, b, _buttons[i].Text, _buttons[i].Filled, i == _hotButton);
            }
        }

        public override bool MouseMove(Point p, PageView view)
        {
            int hot = -1;
            for (int i = 0; i < _buttons.Count; i++) if (ButtonRect(i).Contains(p)) hot = i;
            bool by = _byTarget != null && new Rectangle(TextX + 18, PadY + 66, Theme.Measure(_byName ?? "", Theme.NormalBold).Width, 20).Contains(p);
            bool changed = hot != _hotButton || by != _hotBy;
            _hotButton = hot; _hotBy = by;
            return changed;
        }
        public override bool MouseLeave() { bool c = _hotButton >= 0 || _hotBy; _hotButton = -1; _hotBy = false; return c; }
        public override bool IsLink(Point p) { return _hotButton >= 0 || _hotBy; }
        public override void Click(Point p, MouseButtons b, PageView view)
        {
            if (b != MouseButtons.Left) return;
            if (_hotButton >= 0 && _buttons[_hotButton].Action != null) _buttons[_hotButton].Action(this, EventArgs.Empty);
            else if (_hotBy) view.Host.Navigate(_byTarget);
        }
    }

    /// <summary>Artist header: wide darkened banner, round portrait, name, followers, PLAY / FOLLOW.</summary>
    public sealed class ArtistHeaderBlock : Block
    {
        readonly Artist _artist; readonly List<PageButton> _buttons; int _hot = -1;
        const int H = 280;
        public ArtistHeaderBlock(Artist artist, List<PageButton> buttons) { _artist = artist; _buttons = buttons; }
        public override int Layout(int width) { return H; }
        Rectangle ButtonRect(int i)
        {
            int x = 180;
            for (int j = 0; j < i; j++) x += _buttons[j].Width + 12;
            return new Rectangle(x, 170, _buttons[i].Width, 30);
        }
        public override void Paint(Graphics g, Rectangle r, PageView view)
        {
            // width rounded up to 64px steps, so resizing the window doesn't rescale the banner every pixel
            int bw = Math.Max(64, (r.Width + 63) / 64 * 64), bh = H - 40;
            Bitmap banner = view.Host.Images.Get(_artist.BannerUrl, bw, bh);
            if (banner != null) g.DrawImage(banner, r.Left, r.Top, bw, bh);
            else using (SolidBrush b = new SolidBrush(Color.FromArgb(48, 48, 48))) g.FillRectangle(b, r.Left, r.Top, bw, bh);
            using (LinearGradientBrush fade = new LinearGradientBrush(new Rectangle(r.Left, r.Top, bw, bh + 1),
                Color.FromArgb(60, 0, 0, 0), Color.FromArgb(235, 24, 24, 24), LinearGradientMode.Vertical))
                g.FillRectangle(fade, r.Left, r.Top, bw, bh);
            Rectangle avatar = new Rectangle(r.Left + 30, r.Top + 70, 130, 130);
            Theme.Art(g, view.Host.Images.Get(_artist.ImageUrl, 130, 130), avatar, true);
            Theme.Text1(g, "ARTIST", Theme.Label, new Rectangle(r.Left + 180, r.Top + 80, 200, 16), Theme.SubText);
            Theme.Text1(g, _artist.Name, Theme.Huge, new Rectangle(r.Left + 177, r.Top + 98, r.Width - 200, 56), Theme.Text);
            for (int i = 0; i < _buttons.Count; i++)
            {
                Rectangle b = ButtonRect(i); b.Offset(r.Left, r.Top);
                Theme.PillButton(g, b, _buttons[i].Text, _buttons[i].Filled, i == _hot);
            }
            Theme.Text1(g, _artist.Followers.ToString("n0") + " followers", Theme.Small,
                new Rectangle(r.Left + 180, r.Top + 210, 300, 18), Theme.SubText);
        }
        public override bool MouseMove(Point p, PageView view)
        {
            int hot = -1;
            for (int i = 0; i < _buttons.Count; i++) if (ButtonRect(i).Contains(p)) hot = i;
            bool c = hot != _hot; _hot = hot; return c;
        }
        public override bool MouseLeave() { bool c = _hot >= 0; _hot = -1; return c; }
        public override bool IsLink(Point p) { return _hot >= 0; }
        public override void Click(Point p, MouseButtons b, PageView view)
        {
            if (b == MouseButtons.Left && _hot >= 0 && _buttons[_hot].Action != null) _buttons[_hot].Action(this, EventArgs.Empty);
        }
    }

    /// <summary>Section heading ("POPULAR", "ALBUMS", "Search results for ..."), optionally big.</summary>
    public sealed class SectionBlock : Block
    {
        readonly string _text; readonly bool _big;
        public SectionBlock(string text, bool big) { _text = text; _big = big; }
        public override int Layout(int width) { return _big ? 64 : 44; }
        public override void Paint(Graphics g, Rectangle r, PageView view)
        {
            if (_big) Theme.Text1(g, _text, Theme.Huge, new Rectangle(r.Left + 27, r.Top + 12, r.Width - 54, 50), Theme.Text);
            else
            {
                Theme.Text1(g, _text, Theme.Label, new Rectangle(r.Left + 30, r.Top + 18, r.Width - 60, 18), Theme.SubText);
                using (Pen p = new Pen(Theme.Line)) g.DrawLine(p, r.Left + 30, r.Bottom - 2, r.Right - 30, r.Bottom - 2);
            }
        }
    }

    public sealed class TextBlock : Block
    {
        readonly string _text;
        public TextBlock(string text) { _text = text; }
        public override int Layout(int width) { return 38; }
        public override void Paint(Graphics g, Rectangle r, PageView view)
        {
            Theme.Text1(g, _text, Theme.Normal, new Rectangle(r.Left + 30, r.Top + 2, r.Width - 60, 28), Theme.SubText);
        }
    }

    [Flags] public enum TrackColumns { None = 0, Number = 1, Artist = 2, Album = 4, Popularity = 8, PlayCount = 16, Art = 32 }

    /// <summary>The track table. Only visible rows are painted; double-click plays from that row.</summary>
    public sealed class TrackListBlock : Block
    {
        readonly List<Track> _orig; List<Track> _tracks; readonly TrackColumns _cols; readonly bool _header;
        string _sortKey, _hotHead;            // clicked column header: "song" "artist" "album" "time" "pop"
        bool _sortDesc;
        int _hotRow = -1, _selected = -1, _width;
        string _hotLink;                      // "artist" / "album" while over a link
        const int RowH = 36, HeadH = 32, Pad = 30;

        public TrackListBlock(List<Track> tracks, TrackColumns cols, bool header) { _orig = tracks; _tracks = tracks; _cols = cols; _header = header; }

        int HeadHeight { get { return _header ? HeadH : 0; } }
        public override int Layout(int width) { _width = width; return HeadHeight + _tracks.Count * RowH + 8; }

        // column x positions for the current width
        void Columns(int width, out int num, out int plus, out int title, out int artist, out int album, out int extra, out int dur)
        {
            num = Pad; plus = Pad + ((_cols & TrackColumns.Number) != 0 ? 34 : 0);
            title = plus + 30 + ((_cols & TrackColumns.Art) != 0 ? 40 : 0);
            dur = width - Pad - ((_cols & TrackColumns.Popularity) != 0 ? 110 : (_cols & TrackColumns.PlayCount) != 0 ? 130 : 60);
            extra = dur + 56;
            int free = dur - 20 - title;
            int n = 1 + ((_cols & TrackColumns.Artist) != 0 ? 1 : 0) + ((_cols & TrackColumns.Album) != 0 ? 1 : 0);
            int each = free / n;
            artist = (_cols & TrackColumns.Artist) != 0 ? title + each : -1;
            album = (_cols & TrackColumns.Album) != 0 ? (artist > 0 ? artist + each : title + each) : -1;
        }

        public override void Paint(Graphics g, Rectangle r, PageView view)
        {
            int num, plus, title, artist, album, extra, dur;
            Columns(r.Width, out num, out plus, out title, out artist, out album, out extra, out dur);
            Rectangle clip = Rectangle.Round(g.ClipBounds);
            if (_header)
            {
                int y = r.Top + 8;
                if ((_cols & TrackColumns.Number) != 0) HeadLabel(g, "num", "#", r.Left + num + 4, y);
                HeadLabel(g, "song", "SONG", r.Left + title, y);
                if (artist > 0) HeadLabel(g, "artist", "ARTIST", r.Left + artist, y);
                if (album > 0) HeadLabel(g, "album", "ALBUM", r.Left + album, y);
                HeadLabel(g, "time", "TIME", r.Left + dur, y);
                if ((_cols & TrackColumns.Popularity) != 0) HeadLabel(g, "pop", "POPULARITY", r.Left + extra, y);
                using (Pen p = new Pen(Theme.Line)) g.DrawLine(p, r.Left + Pad, r.Top + HeadH - 2, r.Right - Pad, r.Top + HeadH - 2);
            }
            Track playing = view.Host.PlayingTrack;
            int first = Math.Max(0, (clip.Top - r.Top - HeadHeight) / RowH);
            for (int i = first; i < _tracks.Count; i++)
            {
                Rectangle row = new Rectangle(r.Left + Pad - 10, r.Top + HeadHeight + i * RowH, r.Width - 2 * Pad + 20, RowH);
                if (row.Top > clip.Bottom) break;
                Track t = _tracks[i];
                bool isPlaying = playing != null && playing.Id == t.Id;
                if (i == _selected || i == _hotRow)
                    using (SolidBrush b = new SolidBrush(i == _selected ? Theme.RowSelected : Theme.RowHover)) g.FillRectangle(b, row);
                Color main = isPlaying ? Theme.Accent : Theme.Text, sub = isPlaying ? Theme.Accent : Theme.SubText;
                int cy = row.Top;
                if ((_cols & TrackColumns.Number) != 0)
                {
                    Rectangle nr = new Rectangle(r.Left + num, cy, 30, RowH);
                    if (i == _hotRow || isPlaying) DrawPlayGlyph(g, nr, isPlaying && view.Host.IsPlaying, isPlaying);
                    else Theme.Draw(g, (t.Number > 0 && (_cols & TrackColumns.Art) == 0 ? t.Number : i + 1).ToString(), Theme.Normal, nr, Theme.SubText,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
                // "+" save glyph
                using (Pen p = new Pen(Theme.SubText, 1.5f))
                {
                    int px = r.Left + plus + 10, py = cy + RowH / 2;
                    g.DrawLine(p, px - 6, py, px + 6, py); g.DrawLine(p, px, py - 6, px, py + 6);
                }
                if ((_cols & TrackColumns.Art) != 0)
                    Theme.Art(g, view.Host.Images.Get(t.AlbumImageUrl, 28, 28), new Rectangle(r.Left + plus + 32, cy + 4, 28, 28), false);
                int titleW = (artist > 0 ? artist : album > 0 ? album : dur) - title - 12;
                Theme.Text1(g, t.Name, Theme.Normal, new Rectangle(r.Left + title, cy, titleW - (t.Explicit ? 66 : 0), RowH), main);
                if (t.Explicit)
                {
                    Rectangle ex = new Rectangle(r.Left + title + Math.Min(titleW - 60, Theme.Measure(t.Name, Theme.Normal).Width + 8), cy + 11, 54, 14);
                    using (Pen p = new Pen(Theme.DimText)) g.DrawRectangle(p, ex);
                    Theme.Draw(g, "EXPLICIT", Theme.Label, ex, Theme.DimText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
                if (artist > 0) Link(g, t.ArtistName, new Rectangle(r.Left + artist, cy, (album > 0 ? album : dur) - artist - 12, RowH), sub, i == _hotRow && _hotLink == "artist");
                if (album > 0) Link(g, t.AlbumName, new Rectangle(r.Left + album, cy, dur - album - 12, RowH), sub, i == _hotRow && _hotLink == "album");
                Theme.Text1(g, Theme.Time(t.DurationMs), Theme.Normal, new Rectangle(r.Left + dur, cy, 50, RowH), sub);
                if ((_cols & TrackColumns.Popularity) != 0) PopularityBars(g, r.Left + extra, cy + RowH / 2 - 5, t.Popularity);
                if ((_cols & TrackColumns.PlayCount) != 0)
                    Theme.Draw(g, t.PlayCount.ToString("n0"), Theme.Normal, new Rectangle(r.Left + extra, cy, 110, RowH), Theme.SubText,
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                using (Pen p = new Pen(Theme.Line)) g.DrawLine(p, row.Left + 10, row.Bottom - 1, row.Right - 10, row.Bottom - 1);
            }
        }


        // ---- sorting by clicking the column headers ----
        void HeadLabel(Graphics g, string key, string text, int x, int y)
        {
            bool sorted = _sortKey == key;
            Color c = sorted || _hotHead == key ? Theme.Text : Theme.SubText;
            Theme.Text1(g, text, Theme.Label, new Rectangle(x, y, 200, 18), c);
            if (!sorted) return;
            int ax = x + Theme.Measure(text, Theme.Label).Width + 2, ay = y + 8;
            using (SolidBrush b = new SolidBrush(Theme.Accent))
                g.FillPolygon(b, _sortDesc ? new Point[] { new Point(ax, ay - 2), new Point(ax + 8, ay - 2), new Point(ax + 4, ay + 3) }
                                           : new Point[] { new Point(ax, ay + 3), new Point(ax + 8, ay + 3), new Point(ax + 4, ay - 2) });
        }

        string HeadAt(Point p)
        {
            if (!_header || p.Y < 0 || p.Y >= HeadH) return null;
            int num, plus, title, artist, album, extra, dur;
            Columns(_width, out num, out plus, out title, out artist, out album, out extra, out dur);
            if ((_cols & TrackColumns.Number) != 0 && p.X >= num && p.X < plus) return "num";
            if (p.X >= dur && p.X < dur + 50) return "time";
            if ((_cols & TrackColumns.Popularity) != 0 && p.X >= extra && p.X < extra + 90) return "pop";
            if (album > 0 && p.X >= album && p.X < dur - 12) return "album";
            if (artist > 0 && p.X >= artist && p.X < (album > 0 ? album : dur) - 12) return "artist";
            if (p.X >= title && p.X < (artist > 0 ? artist : album > 0 ? album : dur) - 12) return "song";
            return null;
        }

        static int CompareBy(Track a, Track b, string key)
        {
            switch (key)
            {
                case "song": return String.Compare(a.Name ?? "", b.Name ?? "", StringComparison.CurrentCultureIgnoreCase);
                case "artist": return String.Compare(a.ArtistName ?? "", b.ArtistName ?? "", StringComparison.CurrentCultureIgnoreCase);
                case "album": return String.Compare(a.AlbumName ?? "", b.AlbumName ?? "", StringComparison.CurrentCultureIgnoreCase);
                case "time": return a.DurationMs.CompareTo(b.DurationMs);
                case "pop": return a.Popularity.CompareTo(b.Popularity);
            }
            return 0;
        }

        /// <summary>Click on a header: ascending, then descending, then back to the original order. "#" always restores the original order.</summary>
        void SortBy(string key)
        {
            if (key == "num") { _sortKey = null; _sortDesc = false; }
            else if (_sortKey != key) { _sortKey = key; _sortDesc = key == "pop"; }
            else if (!_sortDesc) _sortDesc = true;
            else { _sortKey = null; _sortDesc = false; }

            Track keep = _selected >= 0 && _selected < _tracks.Count ? _tracks[_selected] : null;
            if (_sortKey == null) _tracks = _orig;
            else
            {
                Dictionary<Track, int> index = new Dictionary<Track, int>();
                for (int i = 0; i < _orig.Count; i++) index[_orig[i]] = i;
                List<Track> copy = new List<Track>(_orig);
                string k = _sortKey; bool desc = _sortDesc;
                copy.Sort(delegate (Track a, Track b)
                {
                    int r = CompareBy(a, b, k);
                    if (r == 0) return index[a].CompareTo(index[b]);   // keep ties in the original order
                    return desc ? -r : r;
                });
                _tracks = copy;
            }
            _selected = keep == null ? -1 : _tracks.IndexOf(keep);
        }

        static void Link(Graphics g, string text, Rectangle r, Color c, bool hot)
        {
            Theme.Text1(g, text, Theme.Normal, r, hot ? Theme.Text : c);
            if (hot)
            {
                int w = Math.Min(r.Width, Theme.Measure(text, Theme.Normal).Width);
                using (Pen p = new Pen(Theme.Text)) g.DrawLine(p, r.Left + 3, r.Top + r.Height / 2 + 7, r.Left + w - 4, r.Top + r.Height / 2 + 7);
            }
        }

        static void DrawPlayGlyph(Graphics g, Rectangle r, bool pause, bool green)
        {
            int cx = r.Left + r.Width / 2, cy = r.Top + r.Height / 2;
            using (SolidBrush b = new SolidBrush(green ? Theme.Accent : Theme.Text))
            {
                if (pause) { g.FillRectangle(b, cx - 5, cy - 6, 4, 12); g.FillRectangle(b, cx + 1, cy - 6, 4, 12); }
                else { SmoothingMode m = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias; g.FillPolygon(b, new Point[] { new Point(cx - 4, cy - 7), new Point(cx + 7, cy), new Point(cx - 4, cy + 7) }); g.SmoothingMode = m; }
            }
        }

        static void PopularityBars(Graphics g, int x, int y, int popularity)
        {
            int lit = (popularity + 9) / 10;
            for (int i = 0; i < 10; i++)
                using (SolidBrush b = new SolidBrush(i < lit ? Color.FromArgb(200, 200, 200) : Color.FromArgb(70, 70, 70)))
                    g.FillRectangle(b, x + i * 4, y, 2, 10);
        }

        int RowAt(Point p) { int i = (p.Y - HeadHeight) / RowH; return p.Y < HeadHeight || i < 0 || i >= _tracks.Count ? -1 : i; }

        string LinkAt(Point p, int row)
        {
            if (row < 0) return null;
            int num, plus, title, artist, album, extra, dur;
            Columns(_width, out num, out plus, out title, out artist, out album, out extra, out dur);
            Track t = _tracks[row];
            if (artist > 0 && p.X >= artist && p.X < artist + Theme.Measure(t.ArtistName, Theme.Normal).Width && p.X < (album > 0 ? album : dur)) return "artist";
            if (album > 0 && p.X >= album && p.X < album + Theme.Measure(t.AlbumName, Theme.Normal).Width && p.X < dur) return "album";
            return null;
        }

        public override bool MouseMove(Point p, PageView view)
        {
            int row = RowAt(p);
            string link = LinkAt(p, row), head = HeadAt(p);
            bool c = row != _hotRow || link != _hotLink || head != _hotHead;
            _hotRow = row; _hotLink = link; _hotHead = head;
            return c;
        }
        public override bool MouseLeave() { bool c = _hotRow >= 0 || _hotHead != null; _hotRow = -1; _hotLink = null; _hotHead = null; return c; }
        public override bool IsLink(Point p) { return _hotLink != null || _hotHead != null; }

        public override void Click(Point p, MouseButtons b, PageView view)
        {
            if (b == MouseButtons.Left)
            {
                string head = HeadAt(p);
                if (head != null) { SortBy(head); view.Invalidate(); return; }
            }
            int row = RowAt(p);
            if (row < 0) return;
            if (b == MouseButtons.Right)
            {
                _selected = row;
                view.Invalidate();
                view.Host.ShowTrackMenu(_tracks, row);
                return;
            }
            if (b == MouseButtons.Left && _hotLink == "artist") { view.Host.Navigate(new NavTarget(PageKind.Artist, _tracks[row].ArtistId)); return; }
            if (b == MouseButtons.Left && _hotLink == "album") { view.Host.Navigate(new NavTarget(PageKind.Album, _tracks[row].AlbumId)); return; }
            int num, plus, title, artist, album, extra, dur;
            Columns(_width, out num, out plus, out title, out artist, out album, out extra, out dur);
            Track playing = view.Host.PlayingTrack;
            // single click on the play glyph of the hovered row plays it (or pauses the current one)
            if (b == MouseButtons.Left && (_cols & TrackColumns.Number) != 0 && p.X >= num && p.X < num + 30)
            {
                if (playing != null && playing.Id == _tracks[row].Id) view.Host.TogglePlayPause();
                else view.Host.PlayList(_tracks, row);
            }
            _selected = row;
            view.Invalidate();
        }

        public override void DoubleClick(Point p, PageView view)
        {
            int row = RowAt(p);
            if (row >= 0 && _hotLink == null) view.Host.PlayList(_tracks, row);
        }
    }

    public sealed class Tile
    {
        public string Title, Subtitle, ImageUrl; public bool Round; public NavTarget Target;
        public Tile(string title, string subtitle, string imageUrl, bool round, NavTarget target)
        { Title = title; Subtitle = subtitle; ImageUrl = imageUrl; Round = round; Target = target; }
    }

    /// <summary>Grid of album / playlist / artist tiles (artwork + two lines of text).</summary>
    public sealed class TileGridBlock : Block
    {
        readonly List<Tile> _tiles; readonly int _maxRows;
        int _cols = 1, _size = 150, _hot = -1;
        const int Pad = 30, Gap = 20, TextH = 44;

        public TileGridBlock(List<Tile> tiles, int maxRows) { _tiles = tiles; _maxRows = maxRows; }

        int Count { get { return _maxRows > 0 ? Math.Min(_tiles.Count, _maxRows * _cols) : _tiles.Count; } }

        public override int Layout(int width)
        {
            int avail = width - 2 * Pad;
            _cols = Math.Max(1, (avail + Gap) / (150 + Gap));
            _size = Math.Min(190, (avail - (_cols - 1) * Gap) / _cols) / 10 * 10;   // 10px steps: fewer rescales
            int rows = (Count + _cols - 1) / _cols;
            return rows * (_size + TextH + Gap) + 10;
        }

        Rectangle TileRect(int i) { return new Rectangle(Pad + (i % _cols) * (_size + Gap), 10 + (i / _cols) * (_size + TextH + Gap), _size, _size + TextH); }

        public override void Paint(Graphics g, Rectangle r, PageView view)
        {
            Rectangle clip = Rectangle.Round(g.ClipBounds);
            for (int i = 0; i < Count; i++)
            {
                Rectangle t = TileRect(i); t.Offset(r.Left, r.Top);
                if (t.Bottom < clip.Top || t.Top > clip.Bottom) continue;
                Tile tile = _tiles[i];
                Rectangle art = new Rectangle(t.Left, t.Top, _size, _size);
                Theme.Art(g, view.Host.Images.Get(tile.ImageUrl, _size, _size), art, tile.Round);
                if (i == _hot)
                    using (Pen p = new Pen(Theme.Accent, 2f))
                    {
                        if (tile.Round) { g.SmoothingMode = SmoothingMode.AntiAlias; g.DrawEllipse(p, art); g.SmoothingMode = SmoothingMode.None; }
                        else g.DrawRectangle(p, art.Left, art.Top, art.Width - 1, art.Height - 1);
                    }
                TextFormatFlags f = Theme.LeftEllipsis | (tile.Round ? TextFormatFlags.HorizontalCenter : 0);
                Theme.Draw(g, tile.Title, Theme.NormalBold, new Rectangle(t.Left, art.Bottom + 6, _size, 18), i == _hot ? Theme.AccentHover : Theme.Text, f);
                Theme.Draw(g, tile.Subtitle, Theme.Small, new Rectangle(t.Left, art.Bottom + 24, _size, 16), Theme.SubText, f);
            }
        }

        int TileAt(Point p) { for (int i = 0; i < Count; i++) if (TileRect(i).Contains(p)) return i; return -1; }
        public override bool MouseMove(Point p, PageView view) { int h = TileAt(p); bool c = h != _hot; _hot = h; return c; }
        public override bool MouseLeave() { bool c = _hot >= 0; _hot = -1; return c; }
        public override bool IsLink(Point p) { return _hot >= 0; }
        public override void Click(Point p, MouseButtons b, PageView view)
        {
            int i = TileAt(p);
            if (b == MouseButtons.Left && i >= 0 && _tiles[i].Target != null) view.Host.Navigate(_tiles[i].Target);
            else if (b == MouseButtons.Right && i >= 0 && _tiles[i].Target != null) view.Host.ShowNavMenu(_tiles[i].Target);
        }
    }
}
