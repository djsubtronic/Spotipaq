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
    // Title bar of the borderless window: icon + title on the left, minimize / maximize / close on the right,
    // drag to move (not while maximized), double-click to maximize / restore, right-click for the window menu.
    // ------------------------------------------------------------------------------------------------
    public sealed class TitleBar : Control
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ReleaseCapture();
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr icon, int w, int h, int step, IntPtr brush, int flags);

        public static readonly Color BarColor = Theme.Window;
        static readonly Color CloseHot = Color.FromArgb(0xda, 0x37, 0x3c);
        /// <summary>Right-click on the bar; carries the screen position for the window menu.</summary>
        public event EventHandler<MouseEventArgs> MenuRequested;
        readonly Form _form;
        int _hot = -1, _pressed = -1;
        const int BtnW = 26;                  // buttons 26 x 24: the old 24 x 22 scaled with the bar
        const int TextH = 22;                 // icon and title stay centred in the top 22 px

        public TitleBar(Form form)
        {
            _form = form;
            Height = 24;
            Dock = DockStyle.Top;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Opaque, true);
        }

        Rectangle BtnRect(int i) { return new Rectangle(Width - (3 - i) * BtnW, 0, BtnW, Height); }
        int BtnAt(int x, int y) { for (int i = 0; i < 3; i++) if (BtnRect(i).Contains(x, y)) return i; return -1; }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (SolidBrush b = new SolidBrush(BarColor)) g.FillRectangle(b, ClientRectangle);

            if (AppIcon.Small != IntPtr.Zero)
            {
                IntPtr dc = g.GetHdc();
                try { DrawIconEx(dc, 6, (TextH - 16) / 2, AppIcon.Small, 16, 16, 0, IntPtr.Zero, 3); }      // DI_NORMAL
                finally { g.ReleaseHdc(dc); }
            }
            // no text: the bar shows just the icon and the buttons (the window text still names the song on the taskbar)

            for (int i = 0; i < 3; i++)
            {
                Rectangle r = BtnRect(i);
                bool hot = _hot == i && (_pressed == -1 || _pressed == i);
                if (hot) using (SolidBrush b = new SolidBrush(i == 2 ? CloseHot : Theme.RowHover)) g.FillRectangle(b, r);
                int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
                using (Pen p = new Pen(hot && i == 2 ? Color.White : Theme.Text, 1f))
                {
                    if (i == 0) g.DrawLine(p, cx - 3, cy + 2, cx + 3, cy + 2);
                    else if (i == 1)
                    {
                        if (_form.WindowState == FormWindowState.Maximized)
                        {
                            g.DrawRectangle(p, cx - 3, cy - 1, 4, 4);
                            g.DrawLines(p, new Point[] { new Point(cx - 1, cy - 1), new Point(cx - 1, cy - 3), new Point(cx + 3, cy - 3), new Point(cx + 3, cy + 1), new Point(cx + 1, cy + 1) });
                        }
                        else g.DrawRectangle(p, cx - 3, cy - 3, 6, 6);
                    }
                    else
                    {
                        g.DrawLine(p, cx - 3, cy - 3, cx + 3, cy + 3);
                        g.DrawLine(p, cx - 3, cy + 3, cx + 3, cy - 3);
                    }
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = BtnAt(e.X, e.Y);
            if (h != _hot) { _hot = h; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hot != -1) { _hot = -1; Invalidate(); }
        }

        void ToggleMax()
        {
            _form.WindowState = _form.WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            int b = BtnAt(e.X, e.Y);
            if (b >= 0) { _pressed = b; Invalidate(); }          // acts on mouse up, if still over the same button
            else if (e.Clicks == 2) ToggleMax();
            else if (_form.WindowState != FormWindowState.Maximized)      // a maximized window stays put
            {
                ReleaseCapture();
                SendMessage(_form.Handle, 0x00A1, (IntPtr)2, IntPtr.Zero);        // WM_NCLBUTTONDOWN, HTCAPTION
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Right)
            {
                if (BtnAt(e.X, e.Y) < 0 && MenuRequested != null)
                {
                    Point s = PointToScreen(e.Location);
                    MenuRequested(this, new MouseEventArgs(MouseButtons.Right, 1, s.X, s.Y, 0));
                }
                return;
            }
            if (e.Button != MouseButtons.Left || _pressed < 0) return;
            int b = BtnAt(e.X, e.Y), was = _pressed;
            _pressed = -1;
            Invalidate();
            if (b != was) return;
            if (b == 0) _form.WindowState = FormWindowState.Minimized;
            else if (b == 1) ToggleMax();
            else _form.Close();
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Library card: "Your Library", three pills (Liked Songs / Artists / Albums), then the playlists with
    // their artwork. The header and pills stay put; the playlists scroll under them. Playlists can be dragged
    // into a different order.
    // ------------------------------------------------------------------------------------------------
    public sealed class Sidebar : Control
    {
        public event EventHandler<NavEventArgs> ItemClicked;
        /// <summary>Raised after the user drags a playlist to a new place; carries the playlist ids in their new order.</summary>
        public event EventHandler<OrderEventArgs> PlaylistsReordered;
        /// <summary>Right-click on a playlist.</summary>
        public event EventHandler<NavEventArgs> ItemContext;
        public ImageCache Images;

        readonly List<Playlist> _lists = new List<Playlist>();
        NavTarget _current;
        int _scroll, _hot = -1, _press = -1, _drop = -1;
        Point _downPt;
        bool _dragging;
        readonly System.Windows.Forms.Timer _autoScroll = new System.Windows.Forms.Timer();

        // rows are sized like the track list's: 36 px, a 28 px cover, the same gaps
        const int RowH = 36, ArtSize = 28, HeaderH = 42, PillH = 26, TopH = 82;      // TopH: where the playlists start
        static readonly string[] PillText = { "Liked Songs", "Artists", "Albums" };
        static readonly PageKind[] PillKind = { PageKind.Songs, PageKind.Artists, PageKind.Albums };
        static readonly Color PillBack = Color.FromArgb(39, 39, 39), PillHot = Color.FromArgb(45, 45, 45), PillDown = Color.FromArgb(32, 32, 32);
        static readonly Color HeaderText = Color.FromArgb(179, 179, 179);
        static int[] _pillTextW;
        int _hotPill = -1, _downPill = -1;

        // scrollbar: same look and behaviour as the main page's, darkened by the sidebar/main background ratio
        const int BarW = SlimScroll.W;
        readonly SlimScroll _bar;
        static readonly Color BarTrack = Color.FromArgb(23, 23, 23);
        static readonly Color BarThumb = Color.FromArgb(68, 68, 68);
        static readonly Color BarThumbDrag = Color.FromArgb(113, 113, 113);
        int ListH { get { return Math.Max(0, Height - TopH); } }
        int ContentHeight { get { return _lists.Count * RowH + 8; } }
        int MaxScroll { get { return Math.Max(0, ContentHeight - ListH); } }
        void ClampScroll() { _scroll = Math.Max(0, Math.Min(_scroll, MaxScroll)); }
        int ViewW { get { return Width - BarW; } }          // width available to the rows (the bar's strip is always reserved)

        public Sidebar()
        {
            _bar = new SlimScroll(this, delegate { return _scroll; }, delegate { return MaxScroll; },
                delegate (int v) { _scroll = v; ClampScroll(); }, RowH * 2, BarTrack, BarThumb, BarThumbDrag);
            _bar.TopInset = TopH;                           // the bar belongs to the playlists, not the header
            _bar.ViewInset = TopH;
            _bar.BottomInset = Theme.CardRadius;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Sidebar;
            _autoScroll.Interval = 40;
            _autoScroll.Tick += delegate
            {
                if (!_dragging) { _autoScroll.Stop(); return; }
                Point p = PointToClient(Cursor.Position);
                int step = p.Y < TopH + 24 ? -14 : p.Y > Height - 24 ? 14 : 0;
                if (step == 0) return;
                _scroll += step; ClampScroll();
                _drop = DropSlot(p.Y);
                Invalidate();
            };
        }

        public void SetPlaylists(List<Playlist> playlists)
        {
            _lists.Clear();
            if (playlists != null) _lists.AddRange(playlists);
            _hot = -1;
            ClampScroll();
            Invalidate();
        }

        public void SetCurrent(NavTarget t) { _current = t; Invalidate(); }

        // the playlist the current song was started from: its name goes green, with a speaker while it is sounding
        string _playingId; bool _playingNow;
        public void SetPlaying(string playlistId, bool playing)
        {
            if (playlistId == _playingId && playing == _playingNow) return;
            _playingId = playlistId; _playingNow = playing;
            Invalidate();
        }
        static readonly Color RowActive = Color.FromArgb(42, 42, 42);       // the playlist whose page is open
        static readonly Color RowHot = Color.FromArgb(31, 31, 31);          // under the pointer

        protected override void OnResize(EventArgs e) { base.OnResize(e); ClampScroll(); }

        int RowTop(int i) { return TopH + i * RowH - _scroll; }
        int RowAt(int y)
        {
            if (y < TopH) return -1;
            int i = (y - TopH + _scroll) / RowH;
            return i >= 0 && i < _lists.Count ? i : -1;
        }
        /// <summary>Insertion slot (a position between playlists) closest to y.</summary>
        int DropSlot(int y)
        {
            for (int k = 0; k < _lists.Count; k++)
                if (y < RowTop(k) + RowH / 2) return k;
            return _lists.Count;
        }

        // ---- pills
        Rectangle PillRect(int i)
        {
            if (_pillTextW == null)
            {
                int[] w = new int[PillText.Length];
                for (int k = 0; k < w.Length; k++) w[k] = Theme.Measure(PillText[k], Theme.SmallBold).Width;
                _pillTextW = w;
            }
            const int gap = 6, left = 12;
            int pad = 8, avail = ViewW - left - 4, total;
            do                                               // squeeze the padding if the card is narrow
            {
                total = gap * (PillText.Length - 1);
                for (int k = 0; k < PillText.Length; k++) total += _pillTextW[k] + 2 * pad;
            } while (total > avail && --pad > 1);
            int x = left;
            for (int k = 0; k < i; k++) x += _pillTextW[k] + 2 * pad + gap;
            return new Rectangle(x, HeaderH, _pillTextW[i] + 2 * pad, PillH);
        }
        int PillAt(Point p) { for (int i = 0; i < PillText.Length; i++) if (PillRect(i).Contains(p)) return i; return -1; }

        static string RowText(Playlist p) { return p.Name + (p.Owner == "Spotify" ? "  by Spotify" : ""); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Sidebar);
            Rectangle clip = e.ClipRectangle;            // hovering repaints a row or a pill, not the whole card

            // playlists
            for (int i = 0; i < _lists.Count; i++)
            {
                int y = RowTop(i);
                if (y + RowH < TopH || y > Height) continue;
                if (y + RowH <= clip.Top || y >= clip.Bottom) continue;
                Playlist p = _lists[i];
                bool hot = _dragging ? i == _press : i == _hot;
                bool current = _current != null && _current.Kind == PageKind.Playlist && _current.Id == p.Id;
                if (current || hot) Theme.FillRounded(g, new Rectangle(6, y + 1, ViewW - 8, RowH - 2), 4, current ? RowActive : RowHot);
                Theme.Art(g, Images == null ? null : Images.Get(p.ImageUrl, ArtSize, ArtSize), new Rectangle(14, y + (RowH - ArtSize) / 2, ArtSize, ArtSize), false);
                bool source = _playingId != null && _playingId == p.Id;
                Rectangle sp = new Rectangle(ViewW - 8 - 24, y, 22, RowH);
                bool speaker = source && _playingNow && (Glyphs.Draw(g, "ic_vol_fill", sp, Theme.Accent) || Glyphs.Draw(g, "ic_vol", sp, Theme.Accent));
                Theme.Text1(g, RowText(p), Theme.Normal, new Rectangle(14 + ArtSize + 10, y, ViewW - (14 + ArtSize + 10) - 8 - (speaker ? 26 : 0), RowH), source ? Theme.Accent : Theme.Text);
            }
            if (_dragging && _drop >= 0)
            {
                int ly = RowTop(_drop);
                if (ly >= TopH) using (SolidBrush b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, 8, ly - 1, ViewW - 20, 2);
            }
            _bar.Paint(g);

            // fixed top: covers whatever scrolled up under it
            if (clip.Top >= TopH) { Theme.CardCorners(g, ClientRectangle, Theme.CardRadius, Theme.Window); return; }
            using (SolidBrush b = new SolidBrush(Theme.Sidebar)) g.FillRectangle(b, 0, 0, Width, TopH);
            bool libIcon = Glyphs.Draw(g, "ic_library", new Rectangle(14, 6, 22, HeaderH - 8), HeaderText);      // same place as Home's
            Theme.Text1(g, "Your Library", Theme.NormalBold, new Rectangle(libIcon ? 44 : 14, 6, ViewW - 50, HeaderH - 8), HeaderText);
            for (int i = 0; i < PillText.Length; i++)
            {
                Rectangle r = PillRect(i);
                bool on = _current != null && _current.Kind == PillKind[i];
                Color back = on ? Color.White : i == _downPill ? PillDown : i == _hotPill ? PillHot : PillBack;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath path = Theme.Pill(r))
                using (SolidBrush b = new SolidBrush(back)) g.FillPath(b, path);
                g.SmoothingMode = SmoothingMode.None;
                Theme.Draw(g, PillText[i], Theme.SmallBold, r, on ? Color.Black : Theme.Text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            }
            Theme.CardCorners(g, ClientRectangle, Theme.CardRadius, Theme.Window);      // the library is a rounded card
        }

        void InvalidateRow(int i)
        {
            if (i < 0) return;
            int top = Math.Max(TopH, RowTop(i)), bottom = RowTop(i) + RowH;
            if (bottom > top) Invalidate(new Rectangle(0, top, Width, bottom - top));
        }
        void InvalidatePill(int i) { if (i >= 0) Invalidate(Rectangle.Inflate(PillRect(i), 1, 1)); }
        void SetHot(int row, int pill)
        {
            if (row != _hot) { InvalidateRow(_hot); InvalidateRow(row); _hot = row; }
            if (pill != _hotPill) { InvalidatePill(_hotPill); InvalidatePill(pill); _hotPill = pill; }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (_bar.MouseDown(e)) return;
            _downPill = PillAt(e.Location);
            _press = _downPill < 0 ? RowAt(e.Y) : -1;
            _downPt = e.Location;
            _dragging = false;
            if (_downPill >= 0) Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_bar.MouseMove(e) && _press < 0)
            {
                SetHot(-1, -1);
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
            int pill = PillAt(e.Location), row = pill < 0 ? RowAt(e.Y) : -1;
            SetHot(row, pill);
            Cursor = row >= 0 || pill >= 0 ? Cursors.Hand : Cursors.Default;
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            SetHot(-1, -1);
            if (_downPill >= 0) { InvalidatePill(_downPill); _downPill = -1; }
            _bar.MouseLeave();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_bar.MouseUp(e)) { base.OnMouseUp(e); return; }
            bool wasDrag = _dragging;
            int press = _press, slot = _drop, downPill = _downPill;
            _press = -1; _drop = -1; _downPill = -1; _dragging = false; Capture = false; _autoScroll.Stop();
            Invalidate();
            if (e.Button == MouseButtons.Right)
            {
                int ri = RowAt(e.Y);
                if (ri >= 0 && PillAt(e.Location) < 0 && ItemContext != null) ItemContext(this, new NavEventArgs(new NavTarget(PageKind.Playlist, _lists[ri].Id)));
                base.OnMouseUp(e);
                return;
            }
            if (e.Button != MouseButtons.Left) { base.OnMouseUp(e); return; }
            if (wasDrag)
            {
                if (press >= 0 && slot >= 0 && slot != press && slot != press + 1)
                {
                    Playlist moved = _lists[press];
                    _lists.RemoveAt(press);
                    _lists.Insert(slot > press ? slot - 1 : slot, moved);
                    List<string> ids = new List<string>();
                    foreach (Playlist p in _lists) ids.Add(p.Id);
                    if (PlaylistsReordered != null) PlaylistsReordered(this, new OrderEventArgs(ids));
                }
                base.OnMouseUp(e);
                return;
            }
            if (downPill >= 0)
            {
                if (PillAt(e.Location) == downPill && ItemClicked != null) ItemClicked(this, new NavEventArgs(new NavTarget(PillKind[downPill], null)));
                base.OnMouseUp(e);
                return;
            }
            int i = RowAt(e.Y);
            if (i >= 0 && i == press && ItemClicked != null) ItemClicked(this, new NavEventArgs(new NavTarget(PageKind.Playlist, _lists[i].Id)));
            base.OnMouseUp(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            _scroll -= e.Delta / 120 * RowH * 2;
            ClampScroll();
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
    // Home / search card: the small rounded card above the library - a Home row, and the search box under it.
    // (Back / forward and the profile button live in the page's own header, see PageView.)
    // ------------------------------------------------------------------------------------------------
    public sealed class TopBar : Control
    {
        public event EventHandler<SearchEventArgs> SearchRequested;
        public event EventHandler HomeClicked;
        public readonly TextBox SearchBox = new TextBox();
        Rectangle _searchFrame;
        // Home row (the page that used to be "Browse" in the library list): white while that page is open or hovered
        bool _homeActive, _homeHot;
        public bool HomeActive { get { return _homeActive; } set { if (value != _homeActive) { _homeActive = value; Invalidate(); } } }
        Rectangle HomeRect { get { return new Rectangle(0, 6, Width, 36); } }
        // search box: dark like the card, outlined; outline, lens and text brighten on hover / focus
        static readonly Color SearchIdle = Color.FromArgb(80, 80, 80), SearchActive = Color.FromArgb(170, 170, 170);      // the seek bar's rail grey / its played part
        /// <summary>The search box's outline, in this control's coordinates.</summary>
        public Rectangle SearchFrame { get { return _searchFrame; } }
        bool _searchHot;
        bool SearchLit { get { return _searchHot || SearchBox.Focused; } }
        void UpdateSearchHot()
        {
            bool over = IsHandleCreated && _searchFrame.Contains(PointToClient(Cursor.Position));
            if (over != _searchHot) { _searchHot = over; Invalidate(); }
        }

        public TopBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Sidebar;
            Height = 88;
            SearchBox.BorderStyle = BorderStyle.None;
            SearchBox.BackColor = Theme.Sidebar;
            SearchBox.ForeColor = SearchActive;                 // text: light grey unfocused, white while typing
            SearchBox.Font = Theme.Normal;
            SearchBox.GotFocus += delegate { SearchBox.ForeColor = Color.White; Invalidate(); };
            SearchBox.LostFocus += delegate { SearchBox.ForeColor = SearchActive; Invalidate(); };
            SearchBox.MouseEnter += delegate { UpdateSearchHot(); };
            SearchBox.MouseLeave += delegate { UpdateSearchHot(); };
            Menus.ForTextBox(SearchBox);
            SearchBox.TextChanged += delegate { Invalidate(); };       // the X comes and goes with the text
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

        protected override void OnLayout(LayoutEventArgs e)
        {
            _searchFrame = new Rectangle(12, 48, Math.Max(60, Width - 24), 26);
            int h = SearchBox.PreferredHeight;
            SearchBox.SetBounds(_searchFrame.Left + 26, _searchFrame.Top + (_searchFrame.Height - h) / 2, _searchFrame.Width - 26 - 28, h);     // room for the X
            base.OnLayout(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Sidebar);
            Rectangle hr = HomeRect;
            Color hc = _homeActive || _homeHot ? Theme.Text : Theme.SubText;
            if (!Glyphs.Draw(g, "ic_home", new Rectangle(14, hr.Top, 22, hr.Height), hc))
                using (SolidBrush hb = new SolidBrush(hc))       // fallback: a plain house shape
                    g.FillPolygon(hb, new Point[] { new Point(25, hr.Top + 9), new Point(33, hr.Top + 17), new Point(33, hr.Top + 27), new Point(17, hr.Top + 27), new Point(17, hr.Top + 17) });
            Theme.Text1(g, "Home", Theme.NormalBold, new Rectangle(44, hr.Top, Width - 56, hr.Height), hc);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color sc = SearchLit ? SearchActive : SearchIdle;
            using (GraphicsPath p = Theme.Pill(new Rectangle(_searchFrame.X, _searchFrame.Y, _searchFrame.Width - 1, _searchFrame.Height - 1)))
            using (Pen edge = new Pen(sc, 1f)) g.DrawPath(edge, p);
            using (Pen lens = new Pen(sc, 1.6f))
            {
                g.DrawEllipse(lens, _searchFrame.Left + 9, _searchFrame.Top + 7, 9, 9);
                g.DrawLine(lens, _searchFrame.Left + 17, _searchFrame.Top + 15, _searchFrame.Left + 21, _searchFrame.Top + 19);
            }
            if (SearchBox.TextLength > 0)                              // X: clears the box
                using (Pen x = new Pen(SearchActive, 1.6f))
                {
                    Rectangle xr = ClearRect;
                    int cx = xr.Left + xr.Width / 2, cy = xr.Top + xr.Height / 2;
                    g.DrawLine(x, cx - 4, cy - 4, cx + 4, cy + 4);
                    g.DrawLine(x, cx - 4, cy + 4, cx + 4, cy - 4);
                }
            g.SmoothingMode = SmoothingMode.None;
            Theme.CardCorners(g, ClientRectangle, Theme.CardRadius, Theme.Window);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            UpdateSearchHot();
            bool hh = HomeRect.Contains(e.Location);
            if (hh != _homeHot) { _homeHot = hh; Invalidate(); }
            Cursor = hh || (SearchBox.TextLength > 0 && ClearRect.Contains(e.Location)) ? Cursors.Hand : _searchHot ? Cursors.IBeam : Cursors.Default;
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e) { UpdateSearchHot(); if (_homeHot) { _homeHot = false; Invalidate(); } base.OnMouseLeave(e); }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && HomeRect.Contains(e.Location) && HomeClicked != null) HomeClicked(this, EventArgs.Empty);
            base.OnMouseUp(e);
        }
        Rectangle ClearRect { get { return new Rectangle(_searchFrame.Right - 28, _searchFrame.Top, 24, _searchFrame.Height); } }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && _searchFrame.Contains(e.Location))
            {
                if (SearchBox.TextLength > 0 && ClearRect.Contains(e.Location)) SearchBox.Text = "";
                SearchBox.Focus();                                     // the lens / rim count as the box
            }
            base.OnMouseDown(e);
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
        public event EventHandler TrackContext;                  // right-click on the song title
        public event EventHandler<NavEventArgs> LinkContext;     // right-click on the artist name
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
            BackColor = Theme.Window;
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
        // Title and artist sit in the top part of the bar, clear of the seek row's time label (which starts at y 48),
        // and end 20 px before the leftmost control (shuffle) however long they are.
        int TextRight { get { return ShuffleRect.Left - 20; } }
        Rectangle TitleRect { get { return new Rectangle(78, 12, Math.Max(10, TextRight - 78), 18); } }
        Rectangle ArtistRect { get { return new Rectangle(78, 30, Math.Max(10, TextRight - 78), 16); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Window);
            // Only what the repaint covers is drawn: the twice-a-second position update touches just the seek row.
            Rectangle clip = e.ClipRectangle;
            Color idle = Color.FromArgb(179, 179, 179);      // every control: grey at rest, white under the pointer

            if (Track != null)
            {
                if (clip.Left < 66 && clip.Top < 65)
                    Theme.Art(g, Images == null ? null : Images.Get(Track.AlbumImageUrl, 56, 56), new Rectangle(10, 9, 56, 56), false);
                if (clip.Left < TextRight && clip.Top < ArtistRect.Bottom)
                {
                    LinkText(g, Track.Name, Theme.NormalBold, TitleRect, Theme.Text, _hot == "title");
                    LinkText(g, Track.ArtistName, Theme.BarArtist, ArtistRect, Theme.SubText, _hot == "artist");
                }
            }

            if (clip.Top < 44 && clip.Right > Cx - 110 && clip.Left < Cx + 110) PaintControls(g, idle);

            // seek bar + times
            Rectangle s = SeekRect;
            if (clip.Bottom > s.Top - 4)
            {
                int dur = Track == null ? 0 : Track.DurationMs;
                int pos = _dragSeek ? _dragMs : PositionMs;
                Theme.Draw(g, Theme.Time(pos), Theme.Small, new Rectangle(s.Left - 50, s.Top - 2, 44, 18), Theme.SubText, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                Theme.Draw(g, Theme.Time(dur), Theme.Small, new Rectangle(s.Right + 6, s.Top - 2, 44, 18), Theme.SubText, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                Bar(g, s, dur <= 0 ? 0 : (float)pos / dur, _hot == "seek" || _dragSeek);
            }

            // volume
            Rectangle v = VolRect;
            if (clip.Top < v.Bottom + 6 && clip.Right > MuteRect.Left - 2)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Color cMute = _hot == "mute" ? Color.White : idle;
                if (!Glyphs.Draw(g, Volume <= 0 ? "ic_mute" : "ic_vol", MuteRect, cMute)) SpeakerGlyph(g, MuteRect, cMute, Volume <= 0);
                g.SmoothingMode = SmoothingMode.None;
                Bar(g, v, Volume / 100f, _hot == "vol" || _dragVol);
            }
        }

        int _seekSec = -1, _seekFx = -1;
        /// <summary>The position moved and nothing else did: repaints the seek row, and only if it would look different
        /// (the time text shows whole seconds, the bar whole pixels).</summary>
        public void InvalidateSeek()
        {
            Rectangle s = SeekRect;
            int dur = Track == null ? 0 : Track.DurationMs;
            int sec = PositionMs / 1000, fx = dur <= 0 ? 0 : (int)((long)s.Width * Math.Max(0, Math.Min(PositionMs, dur)) / dur);
            if (sec == _seekSec && fx == _seekFx) return;
            _seekSec = sec; _seekFx = fx;
            Invalidate(new Rectangle(s.Left - 52, s.Top - 4, s.Width + 104, 22));
        }

        // previous / play / next, shuffle, repeat
        void PaintControls(Graphics g, Color idle)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // play / pause: white outlined circle (1.0 style)
            // play / pause: a solid white disc, always white; it grows by a pixel all round under the pointer
            Rectangle disc = PlayRect;
            if (_hot == "play") disc.Inflate(1, 1);
            using (SolidBrush white = new SolidBrush(Color.White)) g.FillEllipse(white, disc);
            using (SolidBrush b = new SolidBrush(Theme.Window))
            {
                int cx = PlayRect.Left + 17, cy = PlayRect.Top + 17;
                if (Playing) { g.FillRectangle(b, cx - 5, cy - 6, 4, 12); g.FillRectangle(b, cx + 2, cy - 6, 4, 12); }
                else g.FillPolygon(b, new Point[] { new Point(cx - 4, cy - 7), new Point(cx + 7, cy), new Point(cx - 4, cy + 7) });
            }
            // icons from the exe (Glyphs); the hand-drawn shapes are the fallback if one is missing
            Color cPrev = _hot == "prev" ? Color.White : idle, cNext = _hot == "next" ? Color.White : idle;
            Color cShuffle = Shuffle ? Theme.Accent : _hot == "shuffle" ? Color.White : idle;
            Color cRepeat = RepeatMode != 0 ? Theme.Accent : _hot == "repeat" ? Color.White : idle;
            if (!Glyphs.Draw(g, "ic_prev", PrevRect, cPrev)) SkipGlyph(g, PrevRect, false, cPrev);
            if (!Glyphs.Draw(g, "ic_next", NextRect, cNext)) SkipGlyph(g, NextRect, true, cNext);
            if (!Glyphs.Draw(g, "ic_shuffle", ShuffleRect, cShuffle)) ShuffleGlyph(g, ShuffleRect, cShuffle);
            if (!Glyphs.Draw(g, RepeatMode == 2 ? "ic_repeat1" : "ic_repeat", RepeatRect, cRepeat)) RepeatGlyph(g, RepeatRect, RepeatMode, cRepeat);
            g.SmoothingMode = SmoothingMode.None;
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
            Theme.FillRounded(g, new Rectangle(r.Left, y, r.Width, 4), 2, Color.FromArgb(80, 80, 80));
            int fx = r.Left + (int)(r.Width * frac);
            if (fx > r.Left) Theme.FillRounded(g, new Rectangle(r.Left, y, Math.Max(4, fx - r.Left), 4), 2, hot ? Theme.Accent : Color.FromArgb(170, 170, 170));
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
        // speaker; with sound waves, or a cross when muted
        static void SpeakerGlyph(Graphics g, Rectangle r, Color c, bool muted)
        {
            using (SolidBrush b = new SolidBrush(c))
                g.FillPolygon(b, new Point[] { new Point(r.Left + 3, r.Top + 8), new Point(r.Left + 7, r.Top + 8), new Point(r.Left + 12, r.Top + 3), new Point(r.Left + 12, r.Top + 19), new Point(r.Left + 7, r.Top + 14), new Point(r.Left + 3, r.Top + 14) });
            using (Pen p = new Pen(c, 1.5f))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                if (muted)
                {
                    g.DrawLine(p, r.Left + 15, r.Top + 8, r.Left + 20, r.Top + 14);
                    g.DrawLine(p, r.Left + 20, r.Top + 8, r.Left + 15, r.Top + 14);
                }
                else
                {
                    g.DrawArc(p, r.Left + 9, r.Top + 6, 8, 10, -60, 120);
                    g.DrawArc(p, r.Left + 8, r.Top + 3, 13, 16, -55, 110);
                }
            }
        }

        // ---- mute: the speaker button drops the volume to 0 and brings the previous level back
        Rectangle MuteRect { get { Rectangle v = VolRect; return new Rectangle(v.Left - 28, v.Top - 6, 24, 22); } }
        int _beforeMute = 80;
        void ToggleMute()
        {
            if (Volume > 0) { _beforeMute = Volume; Volume = 0; }
            else Volume = _beforeMute > 0 ? _beforeMute : 80;
            if (VolumeChanged != null) VolumeChanged(this, EventArgs.Empty);
            Invalidate();
        }

        string HitAt(Point p)
        {
            if (PlayRect.Contains(p)) return "play";
            if (PrevRect.Contains(p)) return "prev";
            if (NextRect.Contains(p)) return "next";
            if (ShuffleRect.Contains(p)) return "shuffle";
            if (RepeatRect.Contains(p)) return "repeat";
            Rectangle s = SeekRect; s.Inflate(0, 4); if (s.Contains(p)) return "seek";
            if (MuteRect.Contains(p)) return "mute";
            Rectangle v = VolRect; v.Inflate(0, 6); if (v.Contains(p)) return "vol";
            if (Track != null && TitleRect.Contains(p) && p.X < TitleRect.Left + Theme.Measure(Track.Name, Theme.NormalBold).Width) return "title";
            if (Track != null && ArtistRect.Contains(p) && p.X < ArtistRect.Left + Theme.Measure(Track.ArtistName, Theme.BarArtist).Width) return "artist";
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
            Cursor = h != null && h != "seek" && h != "vol" ? Cursors.Hand : Cursors.Default;      // the sliders keep the arrow
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
            if (e.Button == MouseButtons.Right && Track != null)
            {
                string rh = HitAt(e.Location);
                if (rh == "title") Fire(TrackContext);
                else if (rh == "artist" && LinkContext != null && !String.IsNullOrEmpty(Track.ArtistId))
                    LinkContext(this, new NavEventArgs(new NavTarget(PageKind.Artist, Track.ArtistId)));
                base.OnMouseUp(e);
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            switch (HitAt(e.Location))
            {
                case "play": Fire(PlayPause); break;
                case "prev": Fire(Previous); break;
                case "next": Fire(Next); break;
                case "shuffle": Fire(ShuffleToggled); break;
                case "repeat": Fire(RepeatToggled); break;
                case "mute": ToggleMute(); break;
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