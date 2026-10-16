// Chrome.cs - the fixed parts of the window: left sidebar, top bar (back/forward + search + user)
// and the bottom "now playing" bar. All owner-drawn, double-buffered, no per-item child controls.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Spotipaq
{
    // ------------------------------------------------------------------------------------------------
    // Sidebar
    // ------------------------------------------------------------------------------------------------
    public sealed class SidebarItem
    {
        public string Text; public NavTarget Target; public bool IsHeader; public char Icon;
        public bool Library, Collapsible;     // Library: belongs to the collapsible "YOUR LIBRARY" group
        public SidebarItem(string text, NavTarget target, bool header, char icon) { Text = text; Target = target; IsHeader = header; Icon = icon; }
    }

    public sealed class Sidebar : Control
    {
        public event EventHandler<NavEventArgs> ItemClicked;
        /// <summary>Raised after the user drags a playlist to a new place; carries the playlist ids in their new order.</summary>
        public event EventHandler<OrderEventArgs> PlaylistsReordered;
        public event EventHandler LibraryToggled;
        public bool LibraryCollapsed;
        /// <summary>Right-click on an item.</summary>
        public event EventHandler<NavEventArgs> ItemContext;
        readonly List<SidebarItem> _items = new List<SidebarItem>();
        int _press = -1, _drop = -1;
        Point _downPt;
        bool _dragging;
        readonly System.Windows.Forms.Timer _autoScroll = new System.Windows.Forms.Timer();
        NavTarget _current;
        int _hot = -1, _scroll;
        const int ItemH = 30, HeadH = 34;

        // custom scrollbar: same look and behaviour as the main page's, darkened by the sidebar/main background ratio (18/24 = 0.75)
        const int BarW = 8;
        bool _dragThumb;
        int _dragOffset;
        static readonly Color BarTrack = Color.FromArgb(23, 23, 23);        // main: 30
        static readonly Color BarThumb = Color.FromArgb(68, 68, 68);        // main: 90
        static readonly Color BarThumbDrag = Color.FromArgb(113, 113, 113); // main: 150
        int MaxScroll { get { return Math.Max(0, ContentHeight - Height); } }
        void ClampScroll() { _scroll = Math.Max(0, Math.Min(_scroll, MaxScroll)); }
        int ViewW { get { return Width - BarW; } }          // width available to the items (the bar's strip is always reserved, like the main page)

        Rectangle Thumb()
        {
            int h = Height;
            int th = Math.Max(30, (int)((long)h * h / Math.Max(1, ContentHeight)));
            int ty = MaxScroll == 0 ? 0 : (int)((long)(h - th) * _scroll / MaxScroll);
            return new Rectangle(Width - BarW + 1, ty, BarW - 2, th);
        }

        public Sidebar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Sidebar;
            _autoScroll.Interval = 40;
            _autoScroll.Tick += delegate
            {
                if (!_dragging) { _autoScroll.Stop(); return; }
                Point p = PointToClient(Cursor.Position);
                int step = p.Y < 24 ? -14 : p.Y > Height - 24 ? 14 : 0;
                if (step == 0) return;
                _scroll = Math.Max(0, Math.Min(_scroll + step, Math.Max(0, ContentHeight - Height)));
                _drop = DropSlot(p.Y);
                Invalidate();
            };
        }

        bool IsPlaylistItem(int i) { return i >= 0 && i < _items.Count && !_items[i].IsHeader && _items[i].Target != null && _items[i].Target.Kind == PageKind.Playlist; }

        int FirstPlaylist() { for (int i = 0; i < _items.Count; i++) if (IsPlaylistItem(i)) return i; return _items.Count; }

        /// <summary>Insertion slot (a position in _items, between playlists) closest to y.</summary>
        int DropSlot(int y)
        {
            int first = FirstPlaylist();
            for (int k = first; k < _items.Count; k++)
                if (y < ItemTop(k) + ItemH / 2) return k;
            return _items.Count;
        }

        public void SetPlaylists(List<Playlist> playlists)
        {
            _items.Clear();
            _items.Add(new SidebarItem("Browse", new NavTarget(PageKind.Browse, null), false, 'B'));
            SidebarItem lib = new SidebarItem("YOUR LIBRARY", null, true, ' '); lib.Collapsible = true;
            _items.Add(lib);
            SidebarItem it1 = new SidebarItem("Liked Songs", new NavTarget(PageKind.Songs, null), false, 'S'); it1.Library = true; _items.Add(it1);
            SidebarItem it2 = new SidebarItem("Saved Albums", new NavTarget(PageKind.Albums, null), false, 'A'); it2.Library = true; _items.Add(it2);
            SidebarItem it3 = new SidebarItem("Saved Artists", new NavTarget(PageKind.Artists, null), false, 'R'); it3.Library = true; _items.Add(it3);
            _items.Add(new SidebarItem("PLAYLISTS", null, true, ' '));
            if (playlists != null)
                foreach (Playlist p in playlists)
                    _items.Add(new SidebarItem(p.Name + (p.Owner == "Spotify" ? "  by Spotify" : ""), new NavTarget(PageKind.Playlist, p.Id), false, 'P'));
            ClampScroll();
            Invalidate();
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); ClampScroll(); }

        public void SetCurrent(NavTarget t) { _current = t; Invalidate(); }

        bool Hidden(int i) { return LibraryCollapsed && _items[i].Library; }
        int ItemTop(int i) { int y = 8 - _scroll; for (int j = 0; j < i; j++) { if (Hidden(j)) continue; y += _items[j].IsHeader ? HeadH : ItemH; } return y; }
        int ContentHeight { get { return ItemTop(_items.Count) + _scroll + 8; } }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Sidebar);
            for (int i = 0; i < _items.Count; i++)
            {
                SidebarItem it = _items[i];
                if (Hidden(i)) continue;
                int y = ItemTop(i), h = it.IsHeader ? HeadH : ItemH;
                if (y + h < 0 || y > Height) continue;
                if (it.IsHeader)
                {
                    bool hotHead = it.Collapsible && i == _hotHeader;
                    Theme.Text1(g, it.Text, Theme.Label, new Rectangle(16, y + 14, ViewW - 24, 18), hotHead ? Theme.SubText : Theme.DimText);
                    if (it.Collapsible)
                    {
                        int cx = ViewW - 22, cy = y + 23;
                        using (SolidBrush cb = new SolidBrush(hotHead ? Theme.SubText : Theme.DimText))
                            g.FillPolygon(cb, LibraryCollapsed ? new Point[] { new Point(cx, cy - 4), new Point(cx + 4, cy), new Point(cx, cy + 4) }
                                                               : new Point[] { new Point(cx - 3, cy - 2), new Point(cx + 5, cy - 2), new Point(cx + 1, cy + 3) });
                    }
                    continue;
                }
                if (_dragging && i == _press) using (SolidBrush b = new SolidBrush(Theme.RowHover)) g.FillRectangle(b, 0, y, ViewW, h);
                bool current = _current != null && it.Target.Kind == _current.Kind && it.Target.Id == _current.Id;
                if (current) using (SolidBrush b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, 0, y + 4, 4, h - 8);
                Color c = current || i == _hot ? Theme.Text : Theme.SubText;
                DrawIcon(g, it.Icon, new Rectangle(16, y + 7, 16, 16), c);
                Theme.Text1(g, it.Text, Theme.Normal, new Rectangle(40, y, ViewW - 48, h), c);
            }
            if (_dragging && _drop >= 0)
            {
                int ly = ItemTop(_drop);
                using (SolidBrush b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, 8, ly - 1, ViewW - 20, 2);
            }
            if (MaxScroll > 0)
            {
                using (SolidBrush track = new SolidBrush(BarTrack)) g.FillRectangle(track, Width - BarW, 0, BarW, Height);
                using (SolidBrush tb = new SolidBrush(_dragThumb ? BarThumbDrag : BarThumb)) g.FillRectangle(tb, Thumb());
            }
            using (Pen p = new Pen(Color.FromArgb(44, 44, 44))) g.DrawLine(p, Width - 1, 0, Width - 1, Height);
        }

        // Tiny vector icons so nothing depends on fonts/images that XP may not have.
        static void DrawIcon(Graphics g, char kind, Rectangle r, Color c)
        {
            using (Pen p = new Pen(c, 1.4f))
            using (SolidBrush b = new SolidBrush(c))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                switch (kind)
                {
                    case 'B': g.DrawEllipse(p, r.Left + 1, r.Top + 1, 13, 13); g.FillEllipse(b, r.Left + 6, r.Top + 6, 3, 3); break;     // compass-ish
                    case 'S': g.DrawEllipse(p, r.Left + 1, r.Top + 9, 6, 5); g.DrawLine(p, r.Left + 7, r.Top + 11, r.Left + 7, r.Top + 1); g.DrawLine(p, r.Left + 7, r.Top + 1, r.Left + 13, r.Top + 3); break;
                    case 'A': g.DrawEllipse(p, r.Left + 1, r.Top + 1, 13, 13); g.DrawEllipse(p, r.Left + 6, r.Top + 6, 3, 3); break;
                    case 'R': g.DrawEllipse(p, r.Left + 4, r.Top + 1, 7, 7); g.DrawArc(p, r.Left + 1, r.Top + 9, 13, 10, 180, 180); break;
                    default: for (int i = 0; i < 3; i++) g.DrawLine(p, r.Left + 1, r.Top + 3 + i * 5, r.Left + 14, r.Top + 3 + i * 5); break;
                }
                g.SmoothingMode = SmoothingMode.None;
            }
        }

        int ItemAt(int y) { for (int i = 0; i < _items.Count; i++) { if (Hidden(i)) continue; int t = ItemTop(i), h = _items[i].IsHeader ? HeadH : ItemH; if (y >= t && y < t + h) return _items[i].IsHeader ? -1 : i; } return -1; }

        int _hotHeader = -1;
        int CollapsibleHeaderAt(int y) { for (int i = 0; i < _items.Count; i++) { if (!_items[i].IsHeader || !_items[i].Collapsible) continue; int t = ItemTop(i); if (y >= t && y < t + HeadH) return i; } return -1; }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (MaxScroll > 0 && e.X >= Width - BarW)
            {
                Rectangle t = Thumb();
                if (t.Contains(e.Location)) { _dragThumb = true; _dragOffset = e.Y - t.Top; Capture = true; }
                else { _scroll += e.Y < t.Top ? -Height : Height; ClampScroll(); Invalidate(); }
                return;
            }
            int i = ItemAt(e.Y);
            _press = IsPlaylistItem(i) ? i : -1;
            _downPt = e.Location;
            _dragging = false;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_dragThumb)
            {
                int viewH = Height, th = Thumb().Height;
                _scroll = (int)((long)(e.Y - _dragOffset) * MaxScroll / Math.Max(1, viewH - th));
                ClampScroll();
                Invalidate();
                return;
            }
            if (MaxScroll > 0 && e.X >= Width - BarW && _press < 0)
            {
                if (_hot != -1 || _hotHeader != -1) { _hot = -1; _hotHeader = -1; Invalidate(); }
                Cursor = Cursors.Default;
                base.OnMouseMove(e);
                return;
            }
            if (_press >= 0 && (e.Button & MouseButtons.Left) != 0)
            {
                if (!_dragging && Math.Abs(e.Y - _downPt.Y) > 6) { _dragging = true; Capture = true; _autoScroll.Start(); }
                if (_dragging)
                {
                    int slot = DropSlot(e.Y);
                    if (slot != _drop) { _drop = slot; Invalidate(); }
                    Cursor = Cursors.SizeNS;
                    base.OnMouseMove(e);
                    return;
                }
            }
            int h = ItemAt(e.Y), hh = CollapsibleHeaderAt(e.Y);
            if (h != _hot || hh != _hotHeader) { _hot = h; _hotHeader = hh; Invalidate(); }
            Cursor = h >= 0 || hh >= 0 ? Cursors.Hand : Cursors.Default; base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { _hot = -1; _hotHeader = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_dragThumb) { _dragThumb = false; Capture = false; Invalidate(); base.OnMouseUp(e); return; }
            bool wasDrag = _dragging;
            int press = _press, slot = _drop;
            _press = -1; _drop = -1; _dragging = false; Capture = false; _autoScroll.Stop();
            if (e.Button == MouseButtons.Right)
            {
                int ri = ItemAt(e.Y);
                if (ri >= 0 && ItemContext != null) ItemContext(this, new NavEventArgs(_items[ri].Target));
                base.OnMouseUp(e);
                return;
            }
            if (e.Button != MouseButtons.Left) { base.OnMouseUp(e); return; }
            if (wasDrag)
            {
                if (press >= 0 && slot >= 0 && slot != press && slot != press + 1)
                {
                    SidebarItem moved = _items[press];
                    _items.RemoveAt(press);
                    _items.Insert(slot > press ? slot - 1 : slot, moved);
                    List<string> ids = new List<string>();
                    for (int k = 0; k < _items.Count; k++) if (IsPlaylistItem(k)) ids.Add(_items[k].Target.Id);
                    if (PlaylistsReordered != null) PlaylistsReordered(this, new OrderEventArgs(ids));
                }
                Invalidate();
                base.OnMouseUp(e);
                return;
            }
            if (press < 0 && CollapsibleHeaderAt(e.Y) >= 0)
            {
                LibraryCollapsed = !LibraryCollapsed;
                _scroll = Math.Max(0, Math.Min(_scroll, Math.Max(0, ContentHeight - Height)));
                Invalidate();
                if (LibraryToggled != null) LibraryToggled(this, EventArgs.Empty);
                base.OnMouseUp(e);
                return;
            }
            int i = ItemAt(e.Y);
            if (i >= 0 && i == press && ItemClicked != null) ItemClicked(this, new NavEventArgs(_items[i].Target));
            else if (i >= 0 && press < 0 && ItemClicked != null) ItemClicked(this, new NavEventArgs(_items[i].Target));
            base.OnMouseUp(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            _scroll = Math.Max(0, Math.Min(_scroll - e.Delta / 120 * 60, Math.Max(0, ContentHeight - Height)));
            Invalidate();
            base.OnMouseWheel(e);
        }
    }

    public sealed class OrderEventArgs : EventArgs
    {
        public readonly List<string> Ids;
        public OrderEventArgs(List<string> ids) { Ids = ids; }
    }

    public sealed class NavEventArgs : EventArgs
    {
        public readonly NavTarget Target;
        public NavEventArgs(NavTarget t) { Target = t; }
    }

    // ------------------------------------------------------------------------------------------------
    // Top bar: < >  [ search ]                                   user v
    // ------------------------------------------------------------------------------------------------
    public sealed class TopBar : Control
    {
        public event EventHandler Back, Forward;
        public event EventHandler<SearchEventArgs> SearchRequested;
        public readonly TextBox SearchBox = new TextBox();
        public bool CanBack, CanForward;
        public ContextMenuStrip UserMenu;   // shown when the name / arrow at the top right is clicked
        string _user = "";
        int _hot;   // 1 back, 2 forward
        Rectangle _searchFrame;

        public TopBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bar;
            Height = 50;
            SearchBox.BorderStyle = BorderStyle.None;
            SearchBox.BackColor = Color.White;
            SearchBox.ForeColor = Color.Black;
            SearchBox.Font = Theme.Normal;
            SearchBox.KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter && SearchBox.Text.Trim().Length > 0 && SearchRequested != null)
                {
                    SearchRequested(this, new SearchEventArgs(SearchBox.Text.Trim()));
                    e.Handled = true; e.SuppressKeyPress = true;
                }
            };
            Controls.Add(SearchBox);
        }

        public void SetUser(string name) { _user = name ?? ""; Invalidate(); }

        protected override void OnLayout(LayoutEventArgs e)
        {
            _searchFrame = new Rectangle(80, 12, Math.Min(260, Width - 260), 26);
            int h = SearchBox.PreferredHeight;
            SearchBox.SetBounds(_searchFrame.Left + 26, _searchFrame.Top + (_searchFrame.Height - h) / 2, _searchFrame.Width - 36, h);
            base.OnLayout(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Bar);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Chevron(g, 22, 25, true, CanBack, _hot == 1);
            Chevron(g, 52, 25, false, CanForward, _hot == 2);
            using (GraphicsPath p = Theme.Pill(_searchFrame))
            using (SolidBrush white = new SolidBrush(Color.White)) g.FillPath(white, p);
            using (Pen lens = new Pen(Color.FromArgb(90, 90, 90), 1.6f))
            {
                g.DrawEllipse(lens, _searchFrame.Left + 9, _searchFrame.Top + 7, 9, 9);
                g.DrawLine(lens, _searchFrame.Left + 17, _searchFrame.Top + 15, _searchFrame.Left + 21, _searchFrame.Top + 19);
            }
            g.SmoothingMode = SmoothingMode.None;
            Rectangle user = new Rectangle(Width - 190, 0, 150, Height);
            Theme.Draw(g, _user, Theme.Normal, user, _hot == 3 ? Color.White : Theme.Text, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            using (Pen p = new Pen(Theme.SubText, 1.5f)) { g.DrawLine(p, Width - 30, 22, Width - 25, 27); g.DrawLine(p, Width - 25, 27, Width - 20, 22); }
        }

        static void Chevron(Graphics g, int cx, int cy, bool left, bool enabled, bool hot)
        {
            Color c = !enabled ? Color.FromArgb(80, 80, 80) : hot ? Color.White : Color.FromArgb(190, 190, 190);
            using (Pen p = new Pen(c, 2f))
            {
                int d = left ? 1 : -1;
                g.DrawLine(p, cx + 4 * d, cy - 8, cx - 4 * d, cy);
                g.DrawLine(p, cx - 4 * d, cy, cx + 4 * d, cy + 8);
            }
        }

        int HitAt(Point p) { if (p.X >= 8 && p.X < 38) return 1; if (p.X >= 38 && p.X < 68) return 2; if (p.X >= Width - 190) return 3; return 0; }
        protected override void OnMouseMove(MouseEventArgs e) { int h = HitAt(e.Location); if (h != _hot) { _hot = h; Invalidate(); } Cursor = h == 3 ? Cursors.Hand : Cursors.Default; base.OnMouseMove(e); }
        protected override void OnMouseLeave(EventArgs e) { _hot = 0; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            int h = HitAt(e.Location);
            if (h == 1 && CanBack && Back != null) Back(this, EventArgs.Empty);
            if (h == 2 && CanForward && Forward != null) Forward(this, EventArgs.Empty);
            if (h == 3 && UserMenu != null) UserMenu.Show(this, new Point(Width - 10, Height - 4), ToolStripDropDownDirection.BelowLeft);
            base.OnMouseUp(e);
        }
    }

    public sealed class SearchEventArgs : EventArgs
    {
        public readonly string Query;
        public SearchEventArgs(string q) { Query = q; }
    }

    // ------------------------------------------------------------------------------------------------
    // Now-playing bar
    // ------------------------------------------------------------------------------------------------
    public sealed class NowPlayingBar : Control
    {
        public event EventHandler PlayPause, Next, Previous, ShuffleToggled, RepeatToggled;
        public event EventHandler<SeekEventArgs> SeekRequested;
        public event EventHandler<NavEventArgs> LinkClicked;
        public event EventHandler VolumeChanged;

        public Track Track;
        public bool Playing, Shuffle;
        public int RepeatMode;      // 0 off, 1 repeat all, 2 repeat one
        public int PositionMs;
        public int Volume = 80;
        public bool DraggingVolume { get { return _dragVol; } }
        public ImageCache Images;

        string _hot;                 // "play","prev","next","shuffle","repeat","seek","vol","title","artist"
        bool _dragSeek, _dragVol;
        int _dragMs;

        public NowPlayingBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bar;
            Height = 74;
        }

        int Cx { get { return Width / 2; } }
        Rectangle PlayRect { get { return new Rectangle(Cx - 17, 8, 34, 34); } }
        Rectangle PrevRect { get { return new Rectangle(Cx - 60, 13, 24, 24); } }
        Rectangle NextRect { get { return new Rectangle(Cx + 36, 13, 24, 24); } }
        Rectangle ShuffleRect { get { return new Rectangle(Cx - 104, 13, 24, 24); } }
        Rectangle RepeatRect { get { return new Rectangle(Cx + 80, 13, 24, 24); } }
        Rectangle SeekRect { get { int w = Math.Min(560, Width - 520); return new Rectangle(Cx - w / 2, 50, Math.Max(80, w), 14); } }
        Rectangle VolRect { get { return new Rectangle(Width - 120, 31, 96, 12); } }
        Rectangle TitleRect { get { return new Rectangle(78, 18, Math.Max(10, Cx - 140 - 78), 18); } }
        Rectangle ArtistRect { get { return new Rectangle(78, 38, Math.Max(10, Cx - 140 - 78), 18); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Bar);
            using (Pen top = new Pen(Color.FromArgb(20, 20, 20))) g.DrawLine(top, 0, 0, Width, 0);

            if (Track != null)
            {
                Theme.Art(g, Images == null ? null : Images.Get(Track.AlbumImageUrl, 56, 56), new Rectangle(10, 9, 56, 56), false);
                LinkText(g, Track.Name, Theme.NormalBold, TitleRect, Theme.Text, _hot == "title");
                LinkText(g, Track.ArtistName, Theme.Small, ArtistRect, Theme.SubText, _hot == "artist");
            }

            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color idle = Color.FromArgb(190, 190, 190);
            // play / pause: white outlined circle (1.0 style)
            using (Pen ring = new Pen(_hot == "play" ? Color.White : idle, 1.6f)) g.DrawEllipse(ring, PlayRect);
            using (SolidBrush b = new SolidBrush(_hot == "play" ? Color.White : idle))
            {
                int cx = PlayRect.Left + 17, cy = PlayRect.Top + 17;
                if (Playing) { g.FillRectangle(b, cx - 6, cy - 7, 4, 14); g.FillRectangle(b, cx + 2, cy - 7, 4, 14); }
                else g.FillPolygon(b, new Point[] { new Point(cx - 4, cy - 8), new Point(cx + 8, cy), new Point(cx - 4, cy + 8) });
            }
            SkipGlyph(g, PrevRect, false, _hot == "prev" ? Color.White : idle);
            SkipGlyph(g, NextRect, true, _hot == "next" ? Color.White : idle);
            ShuffleGlyph(g, ShuffleRect, Shuffle ? Theme.Accent : _hot == "shuffle" ? Color.White : idle);
            RepeatGlyph(g, RepeatRect, RepeatMode, RepeatMode != 0 ? Theme.Accent : _hot == "repeat" ? Color.White : idle);
            g.SmoothingMode = SmoothingMode.None;

            // seek bar + times
            Rectangle s = SeekRect;
            int dur = Track == null ? 0 : Track.DurationMs;
            int pos = _dragSeek ? _dragMs : PositionMs;
            Theme.Draw(g, Theme.Time(pos), Theme.Small, new Rectangle(s.Left - 50, s.Top - 2, 44, 18), Theme.SubText, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            Theme.Draw(g, Theme.Time(dur), Theme.Small, new Rectangle(s.Right + 6, s.Top - 2, 44, 18), Theme.SubText, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            Bar(g, s, dur <= 0 ? 0 : (float)pos / dur, _hot == "seek" || _dragSeek);

            // volume
            Rectangle v = VolRect;
            SpeakerGlyph(g, new Rectangle(v.Left - 26, v.Top - 6, 22, 22), idle);
            Bar(g, v, Volume / 100f, _hot == "vol" || _dragVol);
        }

        static void LinkText(Graphics g, string text, Font f, Rectangle r, Color c, bool hot)
        {
            Theme.Text1(g, text, f, r, hot ? Theme.Text : c);
            if (hot)
            {
                int w = Math.Min(r.Width, Theme.Measure(text, f).Width);
                using (Pen p = new Pen(Theme.Text)) g.DrawLine(p, r.Left + 3, r.Bottom - 2, r.Left + w - 4, r.Bottom - 2);
            }
        }

        static void Bar(Graphics g, Rectangle r, float frac, bool hot)
        {
            frac = Math.Max(0f, Math.Min(1f, frac));
            int y = r.Top + r.Height / 2 - 2;
            using (SolidBrush rail = new SolidBrush(Color.FromArgb(80, 80, 80))) g.FillRectangle(rail, r.Left, y, r.Width, 4);
            int fx = r.Left + (int)(r.Width * frac);
            using (SolidBrush played = new SolidBrush(hot ? Theme.Accent : Color.FromArgb(170, 170, 170))) g.FillRectangle(played, r.Left, y, fx - r.Left, 4);
            if (hot)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (SolidBrush knob = new SolidBrush(Color.White)) g.FillEllipse(knob, fx - 6, y - 4, 12, 12);
                g.SmoothingMode = SmoothingMode.None;
            }
        }

        static void SkipGlyph(Graphics g, Rectangle r, bool forward, Color c)
        {
            int cx = r.Left + 12, cy = r.Top + 12;
            using (SolidBrush b = new SolidBrush(c))
            {
                if (forward) { g.FillPolygon(b, new Point[] { new Point(cx - 6, cy - 7), new Point(cx + 4, cy), new Point(cx - 6, cy + 7) }); g.FillRectangle(b, cx + 4, cy - 7, 2, 14); }
                else { g.FillPolygon(b, new Point[] { new Point(cx + 6, cy - 7), new Point(cx - 4, cy), new Point(cx + 6, cy + 7) }); g.FillRectangle(b, cx - 6, cy - 7, 2, 14); }
            }
        }
        static void ArrowHead(Graphics g, Pen p, int x, int y, int dx, int dy)
        {
            g.DrawLine(p, x - dx - dy, y - dy + dx, x, y);
            g.DrawLine(p, x - dx + dy, y - dy - dx, x, y);
        }
        static void ShuffleGlyph(Graphics g, Rectangle r, Color c)
        {
            using (Pen p = new Pen(c, 1.7f))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;

                // upper path: left -> right/down
                g.DrawLine(p, r.Left + 4, r.Top + 8, r.Left + 10, r.Top + 8);
                g.DrawLine(p, r.Left + 10, r.Top + 8, r.Left + 18, r.Top + 16);
                ArrowHead(g, p, r.Left + 19, r.Top + 16, 3, 0);

                // lower path: left -> right/up
                g.DrawLine(p, r.Left + 4, r.Top + 16, r.Left + 10, r.Top + 16);
                g.DrawLine(p, r.Left + 10, r.Top + 16, r.Left + 18, r.Top + 8);
                ArrowHead(g, p, r.Left + 19, r.Top + 8, 3, 0);
            }
        }
        static void RepeatGlyph(Graphics g, Rectangle r, int mode, Color c)
        {
            // Compact repeat arrows, visually balanced with the shuffle glyph.
            using (Pen p = new Pen(c, 1.7f))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;

                // Upper run points right.
                g.DrawLine(p, r.Left + 5, r.Top + 7, r.Left + 19, r.Top + 7);
                g.DrawLine(p, r.Left + 5, r.Top + 7, r.Left + 5, r.Top + 10);
                ArrowHead(g, p, r.Left + 20, r.Top + 7, 3, 0);

                // Lower run points left.
                g.DrawLine(p, r.Left + 19, r.Top + 17, r.Left + 5, r.Top + 17);
                g.DrawLine(p, r.Left + 19, r.Top + 17, r.Left + 19, r.Top + 14);
                ArrowHead(g, p, r.Left + 4, r.Top + 17, -3, 0);
            }
            if (mode == 2)
            {
                // Small, simple vector 1 centred between the two runs; no foot.
                using (Pen one = new Pen(c, 1.25f))
                {
                    one.StartCap = LineCap.Round; one.EndCap = LineCap.Round;
                    g.DrawLine(one, r.Left + 11, r.Top + 12, r.Left + 12, r.Top + 11);
                    g.DrawLine(one, r.Left + 12, r.Top + 11, r.Left + 12, r.Top + 14);
                }
            }
        }
        static void SpeakerGlyph(Graphics g, Rectangle r, Color c)
        {
            using (SolidBrush b = new SolidBrush(c))
                g.FillPolygon(b, new Point[] { new Point(r.Left + 3, r.Top + 8), new Point(r.Left + 7, r.Top + 8), new Point(r.Left + 12, r.Top + 3), new Point(r.Left + 12, r.Top + 19), new Point(r.Left + 7, r.Top + 14), new Point(r.Left + 3, r.Top + 14) });
            using (Pen p = new Pen(c, 1.3f)) g.DrawArc(p, r.Left + 9, r.Top + 6, 8, 10, -60, 120);
        }

        string HitAt(Point p)
        {
            if (PlayRect.Contains(p)) return "play";
            if (PrevRect.Contains(p)) return "prev";
            if (NextRect.Contains(p)) return "next";
            if (ShuffleRect.Contains(p)) return "shuffle";
            if (RepeatRect.Contains(p)) return "repeat";
            Rectangle s = SeekRect; s.Inflate(0, 4); if (s.Contains(p)) return "seek";
            Rectangle v = VolRect; v.Inflate(0, 6); if (v.Contains(p)) return "vol";
            if (Track != null && TitleRect.Contains(p) && p.X < TitleRect.Left + Theme.Measure(Track.Name, Theme.NormalBold).Width) return "title";
            if (Track != null && ArtistRect.Contains(p) && p.X < ArtistRect.Left + Theme.Measure(Track.ArtistName, Theme.Small).Width) return "artist";
            return null;
        }

        int MsAt(int x) { Rectangle s = SeekRect; int dur = Track == null ? 0 : Track.DurationMs; return (int)((long)dur * Math.Max(0, Math.Min(s.Width, x - s.Left)) / Math.Max(1, s.Width)); }
        void SetVolumeAt(int x) { Rectangle v = VolRect; Volume = Math.Max(0, Math.Min(100, (x - v.Left) * 100 / Math.Max(1, v.Width))); if (VolumeChanged != null) VolumeChanged(this, EventArgs.Empty); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            string h = HitAt(e.Location);
            if (h == "seek" && Track != null) { _dragSeek = true; _dragMs = MsAt(e.X); Capture = true; Invalidate(); }
            if (h == "vol") { _dragVol = true; SetVolumeAt(e.X); Capture = true; Invalidate(); }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_dragSeek) { _dragMs = MsAt(e.X); Invalidate(); return; }
            if (_dragVol) { SetVolumeAt(e.X); Invalidate(); return; }
            string h = HitAt(e.Location);
            if (h != _hot) { _hot = h; Invalidate(); }
            Cursor = h != null ? Cursors.Hand : Cursors.Default;
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { if (!_dragSeek && !_dragVol) { _hot = null; Invalidate(); } base.OnMouseLeave(e); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_dragSeek)
            {
                _dragSeek = false; Capture = false;
                if (SeekRequested != null) SeekRequested(this, new SeekEventArgs(_dragMs));
                Invalidate(); return;
            }
            if (_dragVol) { _dragVol = false; Capture = false; Invalidate(); return; }
            if (e.Button != MouseButtons.Left) return;
            switch (HitAt(e.Location))
            {
                case "play": Fire(PlayPause); break;
                case "prev": Fire(Previous); break;
                case "next": Fire(Next); break;
                case "shuffle": Fire(ShuffleToggled); break;
                case "repeat": Fire(RepeatToggled); break;
                case "title": if (LinkClicked != null && Track != null) LinkClicked(this, new NavEventArgs(new NavTarget(PageKind.Album, Track.AlbumId))); break;
                case "artist": if (LinkClicked != null && Track != null) LinkClicked(this, new NavEventArgs(new NavTarget(PageKind.Artist, Track.ArtistId))); break;
            }
            base.OnMouseUp(e);
        }

        void Fire(EventHandler h) { if (h != null) h(this, EventArgs.Empty); }
    }

    public sealed class SeekEventArgs : EventArgs
    {
        public readonly int Ms;
        public SeekEventArgs(int ms) { Ms = ms; }
    }
}
