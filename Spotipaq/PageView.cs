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
        /// <summary>The pointer to show at p (block coordinates); a hand over links unless a block says otherwise.</summary>
        public virtual Cursor CursorAt(Point p, PageView view) { return IsLink(p) ? Cursors.Hand : Cursors.Default; }
        /// <summary>The picture this page takes its colour from (its cover / the artist's photo), or null.</summary>
        public virtual string KeyImageUrl { get { return null; } }
        /// <summary>For a page's header block: how far down the block its artwork and buttons end. The page's top bar
        /// is fully solid once that line has scrolled up to the bar's bottom edge. 0 = not a header.</summary>
        public virtual int HeaderContentBottom { get { return 0; } }
        /// <summary>Where the page's shared text box goes while this block is using it (block coordinates; the box is
        /// centred vertically in this area). See PageView.ActivateInput.</summary>
        public virtual Rectangle InputBounds { get { return Rectangle.Empty; } }
        /// <summary>True for a block that wants the page's search box (in the top bar); what is typed arrives in InputChanged.</summary>
        public virtual bool WantsSearch { get { return false; } }
        /// <summary>The text in the shared box changed while this block owns it.</summary>
        public virtual void InputChanged(string text, PageView view) { }
        /// <summary>Set by MouseMove / MouseLeave when they return true: the part of the block that changed (block
        /// coordinates), so only that is repainted. Left empty, the whole block is.</summary>
        public Rectangle Dirty;
        /// <summary>A strip of the block (PinHeight px, starting PinTop px down it) that stays in view, pinned under the
        /// page's top bar, once it has scrolled up to it - the track list's column headers. 0 = nothing to pin.</summary>
        public virtual int PinTop { get { return 0; } }
        public virtual int PinHeight { get { return 0; } }
        /// <summary>Paints the pinned strip at r (screen coordinates).</summary>
        public virtual void PaintPinned(Graphics g, Rectangle r, PageView view) { }
        /// <summary>Drops the block's selection (a clicked row), if it has one. True = it had one, repaint.</summary>
        public virtual bool ClearSelection() { return false; }
    }

    public sealed class PageView : Control
    {
        readonly List<Block> _blocks = new List<Block>();
        readonly IPageHost _host;
        int _scroll, _contentHeight, _layoutWidth = -1;
        Block _hot;
        readonly SlimScroll _bar;
        const int BarW = SlimScroll.W;

        // ---- header overlay at the top of the card: back / forward on the left, the profile button on the right.
        // Clear over the page at first; once the page is scrolled it becomes a solid bar in the page's key colour.
        public const int HeaderH = 52;
        public bool CanBack, CanForward;
        public event EventHandler Back, Forward;
        public ContextMenuStrip UserMenu;      // shown by the profile button
        string _user = "";
        public void SetUser(string name) { _user = name ?? ""; }
        public string User { get { return _user; } }
        int _hdrHot;                           // 1 back, 2 forward, 3 profile, 4 search box, -1 elsewhere on the header
        int _solidAt = 120;                    // scroll position at which the bar has faded in completely
        Rectangle BackRect { get { return new Rectangle(16, 10, 32, 32); } }
        Rectangle ForwardRect { get { return new Rectangle(56, 10, 32, 32); } }
        Rectangle UserRect { get { return new Rectangle(ClientSize.Width - BarW - 16 - 32, 10, 32, 32); } }
        int HeaderAt(Point p)
        {
            if (SearchShown && SearchRect.Contains(p)) return 4;      // wherever the box currently is, in the bar or below it
            if (p.Y >= HeaderH) return 0;
            return BackRect.Contains(p) ? 1 : ForwardRect.Contains(p) ? 2 : UserRect.Contains(p) ? 3 : -1;
        }

        // ---- the page's key colour, taken from its cover art: a gradient behind the top of the page (drawn once
        // into a bitmap, then just copied while scrolling) and the solid header
        const int GradH = 248;
        string _keyUrl;
        bool _haveKey;
        Color _key;
        Bitmap _grad;

        void DropGradient() { if (_grad != null) { _grad.Dispose(); _grad = null; } }

        // ---- "Search in playlist", on pages whose track list asks for one (Block.WantsSearch). It sits in the row the
        // list leaves free above its column headers and scrolls with the page - until it reaches the top bar, where it
        // stays, docked beside the profile button (same x all along, so it only ever moves straight up). The text box
        // itself only exists on screen while it is being typed in or holds text; otherwise just the frame is drawn.
        readonly TextBox _input = new TextBox();
        Block _inputOwner, _searchBlock;
        bool _inputWanted, _inputQuiet;
        public bool InputFocused { get { return _input.Focused; } }
        public bool InputWanted(Block b) { return _inputOwner == b && _inputWanted; }
        public string InputText(Block b) { return _inputOwner == b ? _input.Text : ""; }

        Rectangle SearchRect
        {
            get
            {
                int right = UserRect.Left - 12, left = ForwardRect.Right + 16;
                int w = Math.Max(0, Math.Min(204, right - left));
                // In its own row while that is clear of the top bar; the moment it would touch the bar it snaps
                // to its docked place (never half in, half out).
                int y = _searchBlock == null ? DockY : _searchBlock.Top - _scroll + 7;
                if (y < HeaderH) y = DockY;
                return new Rectangle(right - w, y, w, 32);
            }
        }
        const int DockY = 10;                  // where the box rests in the top bar
        bool SearchShown { get { return _searchBlock != null && SearchRect.Width >= 100; } }
        bool SearchHasText { get { return _searchBlock != null && InputText(_searchBlock).Length > 0; } }

        int HeaderAlpha { get { return _scroll <= 0 ? 0 : _scroll >= _solidAt ? 255 : (int)(255L * _scroll / _solidAt); } }
        Color HeaderColor { get { return _haveKey ? _key : Color.FromArgb(32, 32, 32); } }

        /// <summary>The colour of the search box: what is behind it (the gradient, under however much of the bar has
        /// faded in), lightened a little - what a translucent white box would look like. The text box uses it too.</summary>
        Color SearchBack()
        {
            Color under = Theme.Main;
            Rectangle box = SearchRect;
            int y = box.Top + box.Height / 2 + _scroll;
            if (_haveKey && y < GradH)
            {
                double t = Math.Max(0, y) / (double)GradH;
                under = Color.FromArgb((int)(_key.R + (Theme.Main.R - _key.R) * t), (int)(_key.G + (Theme.Main.G - _key.G) * t), (int)(_key.B + (Theme.Main.B - _key.B) * t));
            }
            int a = box.Top < HeaderH ? HeaderAlpha : 0; Color bar = HeaderColor;      // the bar only counts once the box is in it
            under = Color.FromArgb(under.R + (bar.R - under.R) * a / 255, under.G + (bar.G - under.G) * a / 255, under.B + (bar.B - under.B) * a / 255);
            return Color.FromArgb(under.R + (255 - under.R) / 10, under.G + (255 - under.G) / 10, under.B + (255 - under.B) / 10);
        }

        static readonly Color SearchSoft = Color.FromArgb(179, 179, 179);
        void PaintSearch(Graphics g)
        {
            if (!SearchShown) return;
            Rectangle s = SearchRect;
            Theme.FillRounded(g, s, 4, SearchBack());
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen lens = new Pen(SearchSoft, 1.6f))
            {
                g.DrawEllipse(lens, s.Left + 10, s.Top + 10, 9, 9);
                g.DrawLine(lens, s.Left + 18, s.Top + 18, s.Left + 22, s.Top + 22);
            }
            if (SearchHasText)
                using (Pen x = new Pen(_hdrHot == 4 ? Color.White : SearchSoft, 1.6f))
                {
                    int cx = s.Right - 16, cy = s.Top + s.Height / 2;
                    g.DrawLine(x, cx - 4, cy - 4, cx + 4, cy + 4);
                    g.DrawLine(x, cx - 4, cy + 4, cx + 4, cy - 4);
                }
            g.SmoothingMode = SmoothingMode.None;
            if (!InputWanted(_searchBlock))
                Theme.Text1(g, "Search in playlist", Theme.Normal, new Rectangle(s.Left + 32, s.Top, s.Width - 40, s.Height), SearchSoft);
        }

        public void ActivateInput(Block b)
        {
            if (_inputOwner != b) { _inputQuiet = true; _input.Text = ""; _inputQuiet = false; }
            _inputOwner = b; _inputWanted = true;
            EnsureLayout();
            SyncInput();
            _input.Focus();
            Invalidate();
        }

        public void ClearInput(Block b)
        {
            if (_inputOwner != b) return;
            _input.Text = "";
            ActivateInput(b);
        }

        // keeps the text box inside the search frame, in the frame's colour
        void SyncInput()
        {
            if (_inputOwner == null || !_inputWanted || !SearchShown) { if (_input.Visible) _input.Visible = false; return; }
            Rectangle f = SearchRect;
            int h = _input.PreferredHeight;
            Rectangle s = new Rectangle(f.Left + 32, f.Top + (f.Height - h) / 2, f.Width - 32 - 30, h);
            Color back = SearchBack();
            if (_input.BackColor != back) _input.BackColor = back;
            if (_input.Bounds != s) _input.Bounds = s;
            if (!_input.Visible) _input.Visible = true;
        }

        void SetUpInput()
        {
            _input.BorderStyle = BorderStyle.None;
            _input.Font = Theme.Normal;
            _input.ForeColor = Color.White;
            _input.BackColor = Theme.Main;
            _input.Visible = false;
            _input.TabStop = false;
            Menus.ForTextBox(_input);
            _input.TextChanged += delegate
            {
                if (_inputQuiet) return;
                if (_inputOwner != null) _inputOwner.InputChanged(_input.Text, this);
                Invalidate();
            };
            _input.LostFocus += delegate { if (_input.Text.Length == 0 && _inputWanted) { _inputWanted = false; Invalidate(); } };
            _input.KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Escape) return;
                if (_input.Text.Length > 0) _input.Text = ""; else Focus();      // Esc: clear, then leave the box
                e.Handled = true; e.SuppressKeyPress = true;
            };
            Controls.Add(_input);
        }

        void EnsureKey()
        {
            if (_haveKey || _keyUrl == null) return;
            Bitmap bmp = _host.Images.Get(_keyUrl, 16, 16);      // null while loading: the cache repaints us when it arrives
            if (bmp == null) return;
            double r = 0, g = 0, b = 0; int n = 0;
            try
            {
                for (int y = 0; y < bmp.Height; y++)
                    for (int x = 0; x < bmp.Width; x++)
                    {
                        Color c = bmp.GetPixel(x, y);
                        if (c.A < 200) continue;                 // the rounded corners
                        r += c.R; g += c.G; b += c.B; n++;
                    }
            }
            catch (Exception) { n = 0; }
            if (n == 0) { _keyUrl = null; return; }
            r /= n; g /= n; b /= n;
            double mean = (r + g + b) / 3;                       // a little more saturated than the plain average...
            r = Math.Max(0, mean + (r - mean) * 1.3); g = Math.Max(0, mean + (g - mean) * 1.3); b = Math.Max(0, mean + (b - mean) * 1.3);
            double k = 96.0 / Math.Max(1.0, Math.Max(r, Math.Max(g, b)));      // ...and always dark: brightest channel 96
            _key = Color.FromArgb(Math.Min(255, (int)(r * k)), Math.Min(255, (int)(g * k)), Math.Min(255, (int)(b * k)));
            _haveKey = true;
            DropGradient();
        }

        void PaintGradient(Graphics g)
        {
            if (!_haveKey || _scroll >= GradH) return;
            int w = Math.Max(64, (ClientSize.Width + 63) / 64 * 64);      // 64 px steps: resizing doesn't rebuild it every pixel
            if (_grad == null || _grad.Width != w)
            {
                DropGradient();
                _grad = new Bitmap(w, GradH, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                using (Graphics bg = Graphics.FromImage(_grad))
                using (LinearGradientBrush lb = new LinearGradientBrush(new Rectangle(0, -1, w, GradH + 2), _key, Theme.Main, LinearGradientMode.Vertical))
                    bg.FillRectangle(lb, 0, 0, w, GradH);
            }
            // opaque and unscaled: a straight copy, no blending or filtering
            CompositingMode cm = g.CompositingMode; InterpolationMode im = g.InterpolationMode;
            g.CompositingMode = CompositingMode.SourceCopy; g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.DrawImage(_grad, 0, -_scroll, _grad.Width, _grad.Height);
            g.CompositingMode = cm; g.InterpolationMode = im;
        }

        void PaintHeader(Graphics g)
        {
            // fades in with the scroll: clear at the top, solid once the header's artwork and buttons reach its bottom edge
            int alpha = HeaderAlpha;
            if (alpha > 0)
                using (SolidBrush b = new SolidBrush(Color.FromArgb(alpha, HeaderColor))) g.FillRectangle(b, 0, 0, ClientSize.Width, HeaderH);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RoundButton(g, BackRect, _hdrHot == 1 && CanBack);
            RoundButton(g, ForwardRect, _hdrHot == 2 && CanForward);
            RoundButton(g, UserRect, _hdrHot == 3);
            Chevron(g, BackRect, true, CanBack, _hdrHot == 1);
            Chevron(g, ForwardRect, false, CanForward, _hdrHot == 2);
            Rectangle u = UserRect;
            int cx = u.Left + u.Width / 2, cy = u.Top + u.Height / 2;
            using (Pen p = new Pen(_hdrHot == 3 ? Color.White : Color.FromArgb(200, 200, 200), 1.6f))
            {
                g.DrawEllipse(p, cx - 4, cy - 8, 8, 8);                    // head
                g.DrawArc(p, cx - 7, cy + 2, 14, 12, 180, 180);            // shoulders
            }
            g.SmoothingMode = SmoothingMode.None;
        }

        static void RoundButton(Graphics g, Rectangle r, bool hot)
        {
            using (SolidBrush b = new SolidBrush(Color.FromArgb(hot ? 210 : 150, 0, 0, 0))) g.FillEllipse(b, r);
        }

        static void Chevron(Graphics g, Rectangle r, bool left, bool enabled, bool hot)
        {
            Color c = !enabled ? Color.FromArgb(95, 95, 95) : hot ? Color.White : Color.FromArgb(200, 200, 200);
            int cx = r.Left + r.Width / 2, cy = r.Top + r.Height / 2, d = left ? 1 : -1;
            using (Pen p = new Pen(c, 1.8f))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                g.DrawLine(p, cx + 2 * d, cy - 6, cx - 3 * d, cy);
                g.DrawLine(p, cx - 3 * d, cy, cx + 2 * d, cy + 6);
            }
        }

        public PageView(IPageHost host)
        {
            _host = host;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Theme.Main;
            ForeColor = Theme.Text;
            _bar = new SlimScroll(this, delegate { return _scroll; }, delegate { return MaxScroll; },
                delegate (int v) { _scroll = v; ClampScroll(); }, 80,
                Color.Transparent, Color.FromArgb(90, 90, 90), Color.FromArgb(150, 150, 150));      // no track: the page shows through
            SetUpInput();
            _bar.TopInset = HeaderH;                   // the up arrow starts just under the header...
            _bar.BottomInset = Theme.CardRadius;       // ...and the down arrow ends where the bottom corner starts to curve
        }

        public IPageHost Host { get { return _host; } }

        public void SetBlocks(IList<Block> blocks)
        {
            _blocks.Clear();
            _blocks.AddRange(blocks);
            string url = null;
            foreach (Block b in _blocks) { url = b.KeyImageUrl; if (!String.IsNullOrEmpty(url)) break; url = null; }
            if (url != _keyUrl) { _keyUrl = url; _haveKey = false; DropGradient(); }
            _inputOwner = null; _inputWanted = false;                   // a new page: the search box starts empty and closed
            _searchBlock = null;
            foreach (Block b in _blocks) if (b.WantsSearch) { _searchBlock = b; break; }
            _inputQuiet = true; _input.Text = ""; _inputQuiet = false;
            if (_input.Visible) _input.Visible = false;
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
            int y = HeaderH;                   // the page starts below the header overlay
            foreach (Block b in _blocks) { b.Top = y; b.Height = b.Layout(w); y += b.Height; }
            _contentHeight = y + 24;
            _solidAt = 120;
            foreach (Block b in _blocks)
                if (b.HeaderContentBottom > 0) { _solidAt = Math.Max(1, b.Top + b.HeaderContentBottom - HeaderH); break; }
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
            EnsureKey();
            SyncInput();
            PaintGradient(g);
            Rectangle clip = e.ClipRectangle;
            foreach (Block b in _blocks)
            {
                Rectangle r = new Rectangle(0, b.Top - _scroll, _layoutWidth, b.Height);
                if (r.Bottom < clip.Top || r.Top > clip.Bottom) continue;
                b.Paint(g, r, this);
            }
            Block pin = Pinned();
            if (pin != null && clip.Top < HeaderH + pin.PinHeight && clip.Bottom > HeaderH)
                pin.PaintPinned(g, new Rectangle(0, HeaderH, _layoutWidth, pin.PinHeight), this);
            if (clip.Top < HeaderH) PaintHeader(g);
            PaintSearch(g);         // over the bar when docked
            _bar.Paint(g);          // thin scrollbar (Spotify-style), only when needed
            Theme.CardCorners(g, ClientRectangle, Theme.CardRadius, Theme.Window);      // the page is a rounded card
        }

        Block BlockAt(int contentY, out Point local, int x)
        {
            foreach (Block b in _blocks)
                if (contentY >= b.Top && contentY < b.Top + b.Height) { local = new Point(x, contentY - b.Top); return b; }
            local = Point.Empty;
            return null;
        }

        /// <summary>The block whose pinned strip (see Block.PinHeight) is showing under the top bar right now, if any:
        /// its own copy has scrolled up behind the bar and there is still some of the block left below.</summary>
        Block Pinned()
        {
            foreach (Block b in _blocks)
            {
                if (b.PinHeight <= 0) continue;
                if (b.Top + b.PinTop - _scroll < HeaderH && b.Top + b.Height - _scroll > HeaderH + b.PinHeight) return b;
            }
            return null;
        }

        /// <summary>The block under a point of the control and the point in its coordinates; a pinned strip answers
        /// as the part of its block it shows.</summary>
        Block HitBlock(Point p, out Point local)
        {
            Block pin = Pinned();
            if (pin != null && p.Y >= HeaderH && p.Y < HeaderH + pin.PinHeight) { local = new Point(p.X, pin.PinTop + p.Y - HeaderH); return pin; }
            return BlockAt(p.Y + _scroll, out local, p.X);
        }

        // repaints only what a block says changed (its Dirty area, else the block), not the whole page
        void InvalidateBlock(Block b)
        {
            Rectangle d = b.Dirty;
            b.Dirty = Rectangle.Empty;
            if (d.IsEmpty) d = new Rectangle(0, 0, 0, b.Height);
            Invalidate(new Rectangle(0, b.Top - _scroll + d.Top, ClientSize.Width, d.Height));
            if (b.PinHeight > 0 && Pinned() == b) Invalidate(new Rectangle(0, HeaderH, ClientSize.Width, b.PinHeight));
        }

        void LeaveHot()
        {
            if (_hot == null) return;
            _hot.Dirty = Rectangle.Empty;
            if (_hot.MouseLeave()) InvalidateBlock(_hot);
            _hot = null;
        }

        protected override void OnMouseWheel(MouseEventArgs e) { ScrollBy(-e.Delta / 120 * 80); base.OnMouseWheel(e); }

        /// <summary>A selected row only stays lit while the list is what is being used: called when anything else is clicked.</summary>
        public void ClearSelection() { ClearSelectionExcept(null); }
        void ClearSelectionExcept(Block keep)
        {
            bool changed = false;
            foreach (Block b in _blocks) if (b != keep && b.ClearSelection()) changed = true;
            if (changed) Invalidate();
        }
        protected override void OnLostFocus(EventArgs e) { ClearSelection(); base.OnLostFocus(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            if (_bar.MouseDown(e)) return;
            Point at = Point.Empty;
            bool onHeader = HeaderAt(e.Location) != 0;
            ClearSelectionExcept(onHeader ? null : HitBlock(e.Location, out at));      // a click anywhere but in the list itself
            if (onHeader) return;              // header buttons and the search box act on mouse up
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_bar.MouseUp(e)) return;
            int hh = HeaderAt(e.Location);
            if (hh != 0)
            {
                if (e.Button != MouseButtons.Left) return;
                if (hh == 1 && CanBack && Back != null) Back(this, EventArgs.Empty);
                else if (hh == 2 && CanForward && Forward != null) Forward(this, EventArgs.Empty);
                else if (hh == 3 && UserMenu != null) UserMenu.Show(this, new Point(UserRect.Right, UserRect.Bottom + 4), ToolStripDropDownDirection.BelowLeft);
                else if (hh == 4)
                {
                    if (e.X >= SearchRect.Right - 30 && SearchHasText) ClearInput(_searchBlock);      // the X
                    else ActivateInput(_searchBlock);
                }
                return;
            }
            Point local;
            Block b = HitBlock(e.Location, out local);
            if (b != null) b.Click(local, e.Button, this);
            base.OnMouseUp(e);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            if (HeaderAt(e.Location) != 0 || _bar.Over(e.Location)) return;      // two quick clicks on the scrollbar are not a double-click on the row behind it
            Point local;
            Block b = HitBlock(e.Location, out local);
            if (b != null && e.Button == MouseButtons.Left) b.DoubleClick(local, this);
            base.OnMouseDoubleClick(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_bar.MouseMove(e))
            {
                LeaveHot();
                SetHeaderHot(0);
                Cursor = Cursors.Default;
                return;
            }
            int hh = HeaderAt(e.Location);
            SetHeaderHot(hh);
            if (hh != 0)
            {
                LeaveHot();
                if (hh == 4) Cursor = e.X >= SearchRect.Right - 30 && SearchHasText ? Cursors.Hand : Cursors.IBeam;      // a text field, except over its X
                else Cursor = hh == 3 || (hh == 1 && CanBack) || (hh == 2 && CanForward) ? Cursors.Hand : Cursors.Default;
                return;
            }
            Point local;
            Block b = HitBlock(e.Location, out local);
            if (_hot != b) LeaveHot();
            _hot = b;
            if (b != null)
            {
                b.Dirty = Rectangle.Empty;
                if (b.MouseMove(local, this)) InvalidateBlock(b);
            }
            Cursor = b != null ? b.CursorAt(local, this) : Cursors.Default;
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            LeaveHot();
            _bar.MouseLeave();
            SetHeaderHot(0);
            base.OnMouseLeave(e);
        }

        void SetHeaderHot(int h)
        {
            if (h == _hdrHot) return;
            bool search = h == 4 || _hdrHot == 4;
            _hdrHot = h;
            Invalidate(new Rectangle(0, 0, ClientSize.Width, HeaderH));
            if (search) Invalidate(SearchRect);          // the box may be below the bar
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

    public delegate bool StateGetter();

    public sealed class PageButton
    {
        public string Text; public bool Filled; public int Width; public EventHandler Action;
        /// <summary>null = a pill with the text; "play" = a round green play button; otherwise the name of an icon
        /// (see Glyphs) shown inside a ring. Round buttons are as tall as they are wide.</summary>
        public string Icon;
        /// <summary>Set for a toggle: the icon is drawn bare (no ring), green while this returns true.</summary>
        public StateGetter On;
        public PageButton(string text, bool filled, int width, EventHandler action) { Text = text; Filled = filled; Width = width; Action = action; }
        public PageButton(string text, bool filled, int width, string icon, EventHandler action) : this(text, filled, width, action) { Icon = icon; }
        public int Height { get { return Icon != null ? Width : 30; } }

        public void Paint(Graphics g, Rectangle r, bool hot)
        {
            if (Icon == null) { Theme.PillButton(g, r, Text, Filled, hot); return; }
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (Icon == "play")
            {
                Rectangle d = r;
                if (hot) d.Inflate(1, 1);                          // grows a pixel under the pointer
                using (SolidBrush b = new SolidBrush(hot ? Theme.AccentHover : Theme.Accent)) g.FillEllipse(b, d);
                int cx = r.Left + r.Width / 2, cy = r.Top + r.Height / 2, k = r.Width / 5;
                using (SolidBrush b = new SolidBrush(Color.Black))
                    g.FillPolygon(b, new Point[] { new Point(cx - k / 2 - 1, cy - k), new Point(cx + k, cy), new Point(cx - k / 2 - 1, cy + k) });
            }
            else if (On != null)
            {
                Color c = On() ? (hot ? Theme.AccentHover : Theme.Accent) : hot ? Color.White : Color.FromArgb(179, 179, 179);
                if (!Glyphs.Draw(g, Icon, r, c))
                    Theme.Draw(g, Text.Length > 0 ? Text.Substring(0, 1) : "", Theme.NormalBold, r, c, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
            else
            {
                Color c = hot ? Color.White : Color.FromArgb(179, 179, 179);
                using (Pen ring = new Pen(c, 1.5f)) g.DrawEllipse(ring, r.Left + 1, r.Top + 1, r.Width - 3, r.Height - 3);
                if (!Glyphs.Draw(g, Icon, r, c))
                    Theme.Draw(g, Text.Length > 0 ? Text.Substring(0, 1) : "", Theme.SmallBold, r, c, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
            g.SmoothingMode = old;
        }

        /// <summary>Height of a row of buttons: the tallest one.</summary>
        public static int RowHeight(List<PageButton> buttons) { int h = 30; foreach (PageButton b in buttons) h = Math.Max(h, b.Height); return h; }
    }

    /// <summary>Album / playlist header: artwork, small type label, huge title, subtitle, buttons.</summary>
    public sealed class HeaderBlock : Block
    {
        readonly string _label, _title, _description, _imageUrl;
        readonly string _byName; readonly NavTarget _byTarget;
        readonly string[] _bright; readonly string _dim;
        readonly List<PageButton> _buttons;
        int _hotButton = -1; bool _hotBy;
        const int Art = 160, Pad = 30, PadY = 8, TextX = Pad + Art + 24;
        const int MetaX = TextX + 3;                    // lines up with the padded text above it
        const int DotGap = 6, DotSize = 4;              // the dot between the parts of the bottom line
        static readonly Color Bright = Color.White;

        /// <summary>label (small caps), title, an optional description under it, then one line: byName (a link when
        /// byTarget is set) and the "bright" parts in white with dots between them, followed by "dim" in grey.</summary>
        public HeaderBlock(string label, string title, string description, string byName, NavTarget byTarget,
            string[] bright, string dim, string imageUrl, List<PageButton> buttons)
        {
            _label = label; _title = title; _description = description; _byName = byName; _byTarget = byTarget;
            _bright = bright ?? new string[0]; _dim = dim; _imageUrl = imageUrl; _buttons = buttons;
        }

        public override int Layout(int width) { return Art + PadY + 10; }
        public override string KeyImageUrl { get { return _imageUrl; } }
        public override int HeaderContentBottom { get { return PadY + Art; } }       // the cover's and the buttons' bottom edge

        bool HasDescription { get { return !String.IsNullOrEmpty(_description); } }
        int MetaY { get { return PadY + (HasDescription ? 86 : 66); } }

        // Exact text widths, so the differently coloured parts of the bottom line sit flush against each other.
        // Measuring the text twice over and once, and taking the difference, leaves only what the text itself
        // advances by: whatever padding the measurement adds is in both and cancels out.
        const TextFormatFlags Plain = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        static int TextW(string s, Font f)
        {
            if (String.IsNullOrEmpty(s)) return 0;
            if (Theme.IsRich(s)) return Theme.Measure(s, f).Width - 4;          // symbols / emoji: laid out by Theme, 2 px either side
            Size big = new Size(100000, 100);
            return TextRenderer.MeasureText(s + s, f, big, Plain).Width - TextRenderer.MeasureText(s, f, big, Plain).Width;
        }
        /// <summary>Draws one part of the bottom line at x and returns where the next one starts.</summary>
        static int Part(Graphics g, string s, Font f, int x, int y, int right, Color c)
        {
            if (String.IsNullOrEmpty(s) || x >= right) return x;
            int w = TextW(s, f);
            int dx = Theme.IsRich(s) ? 2 : 0;                                   // Theme's own layout starts 2 px in
            Theme.Draw(g, s, f, new Rectangle(x - dx, y, Math.Min(w + 16, right - x) + dx, 20), c, Theme.LeftEllipsis | TextFormatFlags.NoPadding);
            return x + w;
        }

        Rectangle ButtonRect(int i)
        {
            int x = TextX;
            for (int j = 0; j < i; j++) x += _buttons[j].Width + 12;
            int rowH = PageButton.RowHeight(_buttons), h = _buttons[i].Height;      // bottom of the row lines up with the cover's
            return new Rectangle(x, PadY + Art - rowH + (rowH - h) / 2, _buttons[i].Width, h);
        }
        Rectangle ByRect()
        {
            return new Rectangle(MetaX, MetaY, TextW(_byName, Theme.NormalBold), 20);
        }

        public override void Paint(Graphics g, Rectangle r, PageView view)
        {
            Rectangle art = new Rectangle(r.Left + Pad, r.Top + PadY, Art, Art);
            Theme.Art(g, view.Host.Images.Get(_imageUrl, Art, Art), art, false);
            // (the page's key colour comes from this same picture, see KeyImageUrl)
            int x = r.Left + TextX, w = r.Width - TextX - Pad;
            Theme.Text1(g, _label, Theme.Label, new Rectangle(x, r.Top + PadY + 2, w, 16), Theme.SubText);
            Theme.Text1(g, _title, Theme.Huge, new Rectangle(x - 2, r.Top + PadY + 20, w, 40), Theme.Text);
            if (HasDescription)
                Theme.Text1(g, _description, Theme.Normal, new Rectangle(x, r.Top + PadY + 64, w, 20), Theme.SubText);

            // creator • 12 songs, 48 min
            int y = r.Top + MetaY, cx = r.Left + MetaX, right = x + w;
            bool first = true;
            if (!String.IsNullOrEmpty(_byName))
            {
                int end = Part(g, _byName, Theme.NormalBold, cx, y, right, Bright);
                if (_hotBy) using (Pen u = new Pen(Bright)) g.DrawLine(u, cx, y + 17, Math.Min(end, right) - 1, y + 17);
                cx = end; first = false;
            }
            foreach (string part in _bright)
            {
                if (String.IsNullOrEmpty(part)) continue;
                if (!first && cx + DotGap * 2 + DotSize < right)
                {
                    SmoothingMode sm = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (SolidBrush dot = new SolidBrush(Bright)) g.FillEllipse(dot, cx + DotGap, y + 8, DotSize, DotSize);
                    g.SmoothingMode = sm;
                    cx += DotGap * 2 + DotSize;
                }
                cx = Part(g, part, Theme.Normal, cx, y, right, Bright);
                first = false;
            }
            Part(g, _dim, Theme.Normal, cx, y, right, Theme.SubText);

            for (int i = 0; i < _buttons.Count; i++)
            {
                Rectangle b = ButtonRect(i); b.Offset(r.Left, r.Top);
                _buttons[i].Paint(g, b, i == _hotButton);
            }
        }

        public override bool MouseMove(Point p, PageView view)
        {
            int hot = -1;
            for (int i = 0; i < _buttons.Count; i++) if (ButtonRect(i).Contains(p)) hot = i;
            bool by = _byTarget != null && !String.IsNullOrEmpty(_byName) && ByRect().Contains(p);
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
        public override string KeyImageUrl { get { return _artist.ImageUrl; } }
        public override int HeaderContentBottom { get { return 200; } }               // the photo's and the buttons' bottom edge
        Rectangle ButtonRect(int i)
        {
            int x = 180;
            for (int j = 0; j < i; j++) x += _buttons[j].Width + 12;
            int h = _buttons[i].Height;
            return new Rectangle(x, 185 - h / 2, _buttons[i].Width, h);
        }
        public override void Paint(Graphics g, Rectangle r, PageView view)
        {
            // width rounded up to 64px steps, so resizing the window doesn't rescale the banner every pixel
            int bw = Math.Max(64, (r.Width + 63) / 64 * 64), bh = H - 40;
            Bitmap banner = view.Host.Images.Get(_artist.BannerUrl, bw, bh);
            if (banner != null) g.DrawImage(banner, r.Left, r.Top, bw, bh);
            else using (SolidBrush b = new SolidBrush(Color.FromArgb(48, 48, 48))) g.FillRectangle(b, r.Left, r.Top, bw, bh);
            using (LinearGradientBrush fade = new LinearGradientBrush(new Rectangle(r.Left, r.Top, bw, bh + 1),
                Color.FromArgb(60, 0, 0, 0), Color.FromArgb(235, Theme.Main), LinearGradientMode.Vertical))
                g.FillRectangle(fade, r.Left, r.Top, bw, bh);
            Rectangle avatar = new Rectangle(r.Left + 30, r.Top + 70, 130, 130);
            Theme.Art(g, view.Host.Images.Get(_artist.ImageUrl, 130, 130), avatar, true);
            Theme.Text1(g, "ARTIST", Theme.Label, new Rectangle(r.Left + 180, r.Top + 80, 200, 16), Theme.SubText);
            Theme.Text1(g, _artist.Name, Theme.Huge, new Rectangle(r.Left + 177, r.Top + 98, r.Width - 200, 56), Theme.Text);
            for (int i = 0; i < _buttons.Count; i++)
            {
                Rectangle b = ButtonRect(i); b.Offset(r.Left, r.Top);
                _buttons[i].Paint(g, b, i == _hot);
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

    [Flags] public enum TrackColumns { None = 0, Number = 1, Artist = 2, Album = 4, Popularity = 8, PlayCount = 16, Art = 32, Search = 64 }

    /// <summary>The track table. Only visible rows are painted; double-click plays from that row.
    /// With the Artist column the title cell has two lines (title, artist under it) and rows are taller.
    /// With Search there is a row above the column headers for the page's "Search in playlist" box, which filters the rows as you type.</summary>
    public sealed class TrackListBlock : Block
    {
        readonly List<Track> _orig; List<Track> _tracks; readonly TrackColumns _cols; readonly bool _header;
        string _sortKey, _hotHead;            // clicked column header: "song" "artist" "album" "time" "pop"
        bool _sortDesc;
        int _hotRow = -1, _selected = -1, _width;
        string _hotLink;                      // "artist" / "album" while over a link
        int _linkRow = -1, _linkArtistW, _linkAlbumW;      // text widths of the row under the pointer (measured once per row)
        string _filter = "";                  // what was typed into the search box
        const int HeadH = 32, Pad = 30, ToolsH = 46;
        readonly int _rowH, _artSize;
        static readonly Color Soft = Color.FromArgb(179, 179, 179);

        public TrackListBlock(List<Track> tracks, TrackColumns cols, bool header)
        {
            _orig = tracks; _tracks = tracks; _cols = cols; _header = header;
            _rowH = TwoLine ? 48 : 36;
            _artSize = TwoLine ? 36 : 28;
        }

        bool Has(TrackColumns c) { return (_cols & c) != 0; }
        bool TwoLine { get { return Has(TrackColumns.Artist); } }
        public override bool WantsSearch { get { return Has(TrackColumns.Search); } }
        int Tools { get { return WantsSearch ? ToolsH : 0; } }      // the row above the column headers the page puts its search box in
        int HeadHeight { get { return Tools + (_header ? HeadH : 0); } }
        public override int Layout(int width)
        {
            _width = width;
            return HeadHeight + Math.Max(_tracks.Count, _filter.Length > 0 ? 1 : 0) * _rowH + 8;
        }

        // column x positions for the current width
        void Columns(int width, out int num, out int art, out int title, out int album, out int extra, out int dur)
        {
            num = Pad;
            art = Pad + (Has(TrackColumns.Number) ? 38 : 0);
            title = art + (Has(TrackColumns.Art) ? _artSize + 12 : 0);
            dur = width - Pad - (Has(TrackColumns.Popularity) ? 110 : Has(TrackColumns.PlayCount) ? 130 : 60);
            extra = dur + 56;
            int free = dur - 20 - title;
            album = Has(TrackColumns.Album) ? title + free / 2 : -1;
        }

        // ---- the page's search box: filter the rows
        public override void InputChanged(string text, PageView view)
        {
            _filter = (text ?? "").Trim();
            Rebuild();
            _hotRow = -1;
            view.Relayout();
        }

        public override void Paint(Graphics g, Rectangle r, PageView view)
        {
            int num, art, title, album, extra, dur;
            Columns(r.Width, out num, out art, out title, out album, out extra, out dur);
            Rectangle clip = Rectangle.Round(g.ClipBounds);
            if (_header && clip.Top < r.Top + HeadHeight) PaintHead(g, r.Left, r.Top + Tools, r.Width);
            if (_tracks.Count == 0 && _filter.Length > 0)
            {
                Theme.Text1(g, "No songs match \"" + _filter + "\"", Theme.Normal, new Rectangle(r.Left + Pad, r.Top + HeadHeight, r.Width - 2 * Pad, _rowH), Theme.SubText);
                return;
            }
            Track playing = view.Host.PlayingTrack;
            int first = Math.Max(0, (clip.Top - r.Top - HeadHeight) / _rowH);
            int titleW = (album > 0 ? album : dur) - title - 12;
            for (int i = first; i < _tracks.Count; i++)
            {
                Rectangle row = new Rectangle(r.Left + Pad - 10, r.Top + HeadHeight + i * _rowH, r.Width - 2 * Pad + 20, _rowH);
                if (row.Top > clip.Bottom) break;
                Track t = _tracks[i];
                bool isPlaying = playing != null && playing.Id == t.Id;
                bool selected = i == _selected;             // a clicked row, until something else is clicked (see ClearSelection)
                if (selected || i == _hotRow)
                    using (SolidBrush b = new SolidBrush(selected ? Theme.RowSelected : Theme.RowHover)) g.FillRectangle(b, row);
                Color main = isPlaying ? Theme.Accent : Theme.Text, sub = Theme.SubText;      // only the title turns green
                int cy = row.Top;
                if (Has(TrackColumns.Number))
                {
                    Rectangle nr = new Rectangle(r.Left + num, cy, 30, _rowH);
                    if (i == _hotRow || isPlaying) DrawPlayGlyph(g, nr, isPlaying && view.Host.IsPlaying, isPlaying);
                    else Theme.Draw(g, (t.Number > 0 && !Has(TrackColumns.Art) ? t.Number : i + 1).ToString(), Theme.Normal, nr, Theme.SubText,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
                if (Has(TrackColumns.Art))
                    Theme.Art(g, view.Host.Images.Get(t.AlbumImageUrl, _artSize, _artSize), new Rectangle(r.Left + art, cy + (_rowH - _artSize) / 2, _artSize, _artSize), false);
                // title (and, on two-line rows, the artist under it)
                Rectangle line1 = TwoLine ? new Rectangle(r.Left + title, cy + 6, titleW, 18) : new Rectangle(r.Left + title, cy, titleW, _rowH);
                Theme.Text1(g, t.Name, Theme.Normal, new Rectangle(line1.Left, line1.Top, line1.Width - (t.Explicit ? 66 : 0), line1.Height), main);
                if (t.Explicit)
                {
                    Rectangle ex = new Rectangle(line1.Left + Math.Min(titleW - 60, Theme.Measure(t.Name, Theme.Normal).Width + 8), line1.Top + (line1.Height - 14) / 2, 54, 14);
                    using (Pen p = new Pen(Theme.DimText)) g.DrawRectangle(p, ex);
                    Theme.Draw(g, "EXPLICIT", Theme.Label, ex, Theme.DimText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
                if (TwoLine) Link(g, t.ArtistName, Theme.Small, new Rectangle(r.Left + title, cy + 24, titleW, 16), sub, i == _hotRow && _hotLink == "artist");
                if (album > 0) Link(g, t.AlbumName, Theme.Normal, new Rectangle(r.Left + album, cy, dur - album - 12, _rowH), sub, i == _hotRow && _hotLink == "album");
                Theme.Text1(g, Theme.Time(t.DurationMs), Theme.Normal, new Rectangle(r.Left + dur, cy, 50, _rowH), sub);
                if (Has(TrackColumns.Popularity)) PopularityBars(g, r.Left + extra, cy + _rowH / 2 - 5, t.Popularity);
                if (Has(TrackColumns.PlayCount))
                    Theme.Draw(g, t.PlayCount.ToString("n0"), Theme.Normal, new Rectangle(r.Left + extra, cy, 110, _rowH), Theme.SubText,
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            }
        }

        // ---- column headers: in the list, and again pinned under the page's top bar once they have scrolled up to it
        public override int PinTop { get { return Tools; } }
        public override int PinHeight { get { return _header ? HeadH : 0; } }
        static readonly Color PinnedBack = Color.FromArgb(24, 24, 24);

        public override void PaintPinned(Graphics g, Rectangle r, PageView view)
        {
            using (SolidBrush b = new SolidBrush(PinnedBack)) g.FillRectangle(b, 0, r.Top, view.ClientSize.Width, r.Height);
            PaintHead(g, r.Left, r.Top, r.Width);
        }

        /// <summary>The header row, HeadH tall, with its top at (left, top).</summary>
        void PaintHead(Graphics g, int left, int top, int width)
        {
            int num, art, title, album, extra, dur;
            Columns(width, out num, out art, out title, out album, out extra, out dur);
            int y = top + 8;
            if (Has(TrackColumns.Number)) HeadLabel(g, "num", "#", left + num + 10, y, false);
            bool byArtist = _sortKey == "artist";
            HeadLabel(g, "song", byArtist ? "Artist" : "Title", left + (Has(TrackColumns.Art) ? art : title), y, _sortKey == "song" || byArtist);
            if (album > 0) HeadLabel(g, "album", "Album", left + album, y, _sortKey == "album");
            // duration: a clock
            Color cc = _sortKey == "time" || _hotHead == "time" ? Theme.Text : Soft;
            if (Glyphs.Draw(g, "ic_clock", new Rectangle(left + dur + 6, y, 16, 18), cc))
            { if (_sortKey == "time") SortArrow(g, left + dur + 26, y + 8); }
            else HeadLabel(g, "time", "Time", left + dur, y, _sortKey == "time");
            if (Has(TrackColumns.Popularity)) HeadLabel(g, "pop", "Popularity", left + extra, y, _sortKey == "pop");
            using (Pen p = new Pen(Theme.Line)) g.DrawLine(p, left + Pad, top + HeadH - 2, left + width - Pad, top + HeadH - 2);
        }

        // ---- sorting by clicking the column headers ----
        void HeadLabel(Graphics g, string key, string text, int x, int y, bool sorted)
        {
            Color c = sorted || _hotHead == key ? Theme.Text : Soft;
            Theme.Text1(g, text, Theme.Small, new Rectangle(x, y, 200, 18), c);
            if (sorted) SortArrow(g, x + Theme.Measure(text, Theme.Small).Width + 2, y + 8);
        }

        // green triangle beside the sorted column: up = ascending, down = descending
        void SortArrow(Graphics g, int ax, int ay)
        {
            using (SolidBrush b = new SolidBrush(Theme.Accent))
                g.FillPolygon(b, _sortDesc ? new Point[] { new Point(ax, ay - 2), new Point(ax + 8, ay - 2), new Point(ax + 4, ay + 3) }
                                           : new Point[] { new Point(ax, ay + 3), new Point(ax + 8, ay + 3), new Point(ax + 4, ay - 2) });
        }

        string HeadAt(Point p)
        {
            if (!_header || p.Y < Tools || p.Y >= HeadHeight) return null;
            int num, art, title, album, extra, dur;
            Columns(_width, out num, out art, out title, out album, out extra, out dur);
            if (Has(TrackColumns.Number) && p.X >= num && p.X < art) return "num";
            if (p.X >= dur && p.X < dur + 50) return "time";
            if (Has(TrackColumns.Popularity) && p.X >= extra && p.X < extra + 90) return "pop";
            if (album > 0 && p.X >= album && p.X < dur - 12) return "album";
            if (p.X >= art && p.X < (album > 0 ? album : dur) - 12) return "song";
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

        /// <summary>Click on a header. The title column steps through Title A-Z, Title Z-A, Artist A-Z, Artist Z-A, then
        /// the original order; the others go ascending, descending, original. "#" always restores the original order.</summary>
        void SortBy(string key)
        {
            if (key == "num") { _sortKey = null; _sortDesc = false; }
            else if (key == "song")
            {
                if (_sortKey != "song" && _sortKey != "artist") { _sortKey = "song"; _sortDesc = false; }
                else if (!_sortDesc) _sortDesc = true;
                else if (_sortKey == "song" && TwoLine) { _sortKey = "artist"; _sortDesc = false; }
                else { _sortKey = null; _sortDesc = false; }
            }
            else if (_sortKey != key) { _sortKey = key; _sortDesc = key == "pop"; }
            else if (!_sortDesc) _sortDesc = true;
            else { _sortKey = null; _sortDesc = false; }
            Rebuild();
        }

        static bool Matches(string s, string f) { return s != null && s.IndexOf(f, StringComparison.CurrentCultureIgnoreCase) >= 0; }

        /// <summary>Works out the rows to show: the original list, narrowed by the search text, in the chosen order.</summary>
        void Rebuild()
        {
            Track keep = _selected >= 0 && _selected < _tracks.Count ? _tracks[_selected] : null;
            List<Track> list = _orig;
            if (_filter.Length > 0)
            {
                list = new List<Track>();
                foreach (Track t in _orig)
                    if (Matches(t.Name, _filter) || Matches(t.ArtistName, _filter) || Matches(t.AlbumName, _filter)) list.Add(t);
            }
            if (_sortKey != null)
            {
                Dictionary<Track, int> index = new Dictionary<Track, int>();
                for (int i = 0; i < _orig.Count; i++) index[_orig[i]] = i;
                List<Track> copy = new List<Track>(list);
                string k = _sortKey; bool desc = _sortDesc;
                copy.Sort(delegate (Track a, Track b)
                {
                    int r = CompareBy(a, b, k);
                    if (r == 0) return index[a].CompareTo(index[b]);   // keep ties in the original order
                    return desc ? -r : r;
                });
                list = copy;
            }
            _tracks = list;
            _linkRow = -1;
            _selected = keep == null ? -1 : _tracks.IndexOf(keep);
        }

        static void Link(Graphics g, string text, Font f, Rectangle r, Color c, bool hot)
        {
            Theme.Text1(g, text, f, r, hot ? Theme.Text : c);
            if (hot)
            {
                int w = Math.Min(r.Width, Theme.Measure(text, f).Width), uy = r.Top + r.Height / 2 + f.Height / 2;
                using (Pen p = new Pen(Theme.Text)) g.DrawLine(p, r.Left + 3, uy, r.Left + w - 4, uy);
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

        int RowAt(Point p) { int i = (p.Y - HeadHeight) / _rowH; return p.Y < HeadHeight || i < 0 || i >= _tracks.Count ? -1 : i; }

        string LinkAt(Point p, int row)
        {
            if (row < 0) return null;
            int num, art, title, album, extra, dur;
            Columns(_width, out num, out art, out title, out album, out extra, out dur);
            Track t = _tracks[row];
            if (row != _linkRow)                        // measured when the pointer enters a row, not on every move
            {
                _linkRow = row;
                _linkArtistW = TwoLine ? Theme.Measure(t.ArtistName, Theme.Small).Width : 0;
                _linkAlbumW = album > 0 ? Theme.Measure(t.AlbumName, Theme.Normal).Width : 0;
            }
            int ry = p.Y - HeadHeight - row * _rowH, titleEnd = (album > 0 ? album : dur) - 12;
            if (TwoLine && ry >= 24 && ry < 42 && p.X >= title && p.X < title + _linkArtistW && p.X < titleEnd) return "artist";
            if (album > 0 && p.X >= album && p.X < album + _linkAlbumW && p.X < dur) return "album";
            return null;
        }

        public override bool ClearSelection() { if (_selected < 0) return false; _selected = -1; return true; }

        Rectangle RowRect(int i) { return i < 0 ? Rectangle.Empty : new Rectangle(0, HeadHeight + i * _rowH, _width, _rowH); }
        static Rectangle Join(Rectangle a, Rectangle b) { return a.IsEmpty ? b : b.IsEmpty ? a : Rectangle.Union(a, b); }

        public override bool MouseMove(Point p, PageView view)
        {
            int row = RowAt(p);
            string link = LinkAt(p, row), head = HeadAt(p);
            bool c = row != _hotRow || link != _hotLink || head != _hotHead;
            if (c)                                      // repaint just the rows / header / search box that changed
            {
                Rectangle d = Rectangle.Empty;
                if (row != _hotRow || link != _hotLink) d = Join(RowRect(_hotRow), RowRect(row));
                if (head != _hotHead) d = Join(d, new Rectangle(0, Tools, _width, HeadH));
                Dirty = d;
            }
            _hotRow = row; _hotLink = link; _hotHead = head;
            return c;
        }
        public override bool MouseLeave()
        {
            bool c = _hotRow >= 0 || _hotHead != null;
            Rectangle d = RowRect(_hotRow);
            if (_hotHead != null) d = Join(d, new Rectangle(0, Tools, _width, HeadH));
            Dirty = d;
            _hotRow = -1; _hotLink = null; _hotHead = null;
            return c;
        }
        public override bool IsLink(Point p) { return _hotLink != null; }
        public override Cursor CursorAt(Point p, PageView view)
        {
            return _hotLink != null ? Cursors.Hand : Cursors.Default;      // column headers keep the arrow
        }

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
            int num, art, title, album, extra, dur;
            Columns(_width, out num, out art, out title, out album, out extra, out dur);
            Track playing = view.Host.PlayingTrack;
            // single click on the play glyph of the hovered row plays it (or pauses the current one)
            if (b == MouseButtons.Left && Has(TrackColumns.Number) && p.X >= num && p.X < num + 30)
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
                        else
                        {
                            g.SmoothingMode = SmoothingMode.AntiAlias;
                            using (GraphicsPath gp = Theme.Rounded(new Rectangle(art.Left, art.Top, art.Width - 1, art.Height - 1), Theme.ArtRadius(art.Width)))
                                g.DrawPath(p, gp);
                            g.SmoothingMode = SmoothingMode.None;
                        }
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