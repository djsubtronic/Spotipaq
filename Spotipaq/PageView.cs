// PageView.cs - the main content area. A page is ONE owner-drawn, double-buffered control holding a vertical
// stack of "blocks" (header, section title, track list, tile grid...). No child controls/HWNDs per row or
// tile: on the Pentium II that keeps scrolling and hover cheap (one paint, only the visible rows).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
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
        /// <summary>How far above its own top the block paints (a banner that runs up behind the page's top bar).</summary>
        public virtual int Bleed { get { return 0; } }
        /// <summary>For the block that gives the page its key colour: where the header ends and the darker wash under
        /// it begins, in block coordinates (see PageView's "colour wash"). -1 = the plain older look: one fade from
        /// the top of the page.</summary>
        public virtual int WashTop { get { return -1; } }
        /// <summary>The page's key colour if it is known outright (Spotify's own pick for the cover); Empty = work it
        /// out from KeyImageUrl.</summary>
        public virtual Color KeyColor { get { return Color.Empty; } }
        /// <summary>Drops the block's selection (a clicked row), if it has one. True = it had one, repaint.</summary>
        public virtual bool ClearSelection() { return false; }
        /// <summary>The part of the block that animates while music plays (the playing song's bars, see Equalizer),
        /// in block coordinates; Empty = nothing. Asked each animation frame, so it should be cheap.</summary>
        public virtual Rectangle AnimatedRect(PageView view) { return Rectangle.Empty; }
        /// <summary>Paints just AnimatedRect's content, over the page's plain background already drawn there
        /// (r = the block's on-screen bounds, as in Paint). By default the whole block's Paint.</summary>
        public virtual void PaintAnimated(Graphics g, Rectangle r, PageView view) { Paint(g, r, view); }
        /// <summary>The solid colour behind AnimatedRect (a selected row's highlight); Empty = the page's own background.</summary>
        public virtual Color AnimatedBack(PageView view) { return Color.Empty; }
        /// <summary>Frees anything the block keeps drawn in advance (see TrackListBlock's row pictures). Called when
        /// the page is replaced or laid out again; the block draws it afresh when next painted.</summary>
        public virtual void DropCache() { }
        /// <summary>The part of the block (block coordinates; may start above it, see Bleed) whose look follows the scroll
        /// position until the top bar is solid - a header dimming into the bar's colour. Repainted on every scroll
        /// step while that lasts, rather than moved. Empty = none.</summary>
        public virtual Rectangle ScrollLinked { get { return Rectangle.Empty; } }
        /// <summary>For a page's header block: the title and play button the top bar shows (as on Spotify) once the
        /// block's own play button has scrolled up under the bar - StickyFrom is that button's bottom edge, in block
        /// coordinates. Null title = nothing to show.</summary>
        public virtual string StickyTitle { get { return null; } }
        public virtual PageButton StickyButton { get { return null; } }
        public virtual int StickyFrom { get { return 0; } }
        /// <summary>Screen rows (Top / Height; r = the block's screen bounds) this paint will cover completely,
        /// right across the page, with opaque pixels - the page then skips painting its background there.</summary>
        public virtual Rectangle OpaqueRows(Rectangle r, PageView view) { return Rectangle.Empty; }
    }

    /// <summary>
    /// The playing song's little bouncing bars, in place of its track number (as on Spotify). Not tied to the
    /// sound: each bar loops its own short pattern, at a different length, so together they look random.
    ///
    /// Heights follow the clock, not the frame count, so the look stays the same at any frame rate. PageView's
    /// timer draws each frame straight onto the window, changing only the pixels whose bars grew or shrank
    /// (see PageView.AnimateEqualizer); a normal repaint draws them with Draw, which also notes what is on
    /// screen so the next frame knows what to change.
    /// </summary>
    public static class Equalizer
    {
        public const int Interval = 65;         // ms per frame (about 15 a second)
        const int GlideMs = 100;                // a bar's move from one height of its pattern to the next

        // heights in 16ths of the full height; pattern lengths 7, 9, 11, 13 so the four rarely line up
        static readonly byte[][] Patterns = {
            new byte[] { 6, 12, 16, 11, 7, 4, 9 },
            new byte[] { 14, 9, 5, 8, 13, 16, 12, 7, 10 },
            new byte[] { 4, 8, 13, 16, 10, 6, 11, 15, 9, 5, 7 },
            new byte[] { 11, 15, 10, 5, 3, 8, 14, 16, 12, 7, 4, 6, 9 }
        };
        public const int Count = 4;
        public const int BarW = 2, Gap = 1, MaxH = 13;

        // what was last drawn where (screen rectangle of the cell, bar heights in px); Empty = not known
        public static Rectangle ShownCell = Rectangle.Empty;
        public static readonly int[] Shown = new int[Count];

        /// <summary>The bars' heights now, in pixels.</summary>
        public static void Heights(int[] into)
        {
            long ms = Environment.TickCount & 0x7FFFFFFF;
            long step = ms / GlideMs;
            double t = (ms % GlideMs) / (double)GlideMs;
            t = t * t * (3 - 2 * t);                    // ease in and out
            for (int i = 0; i < Count; i++)
            {
                byte[] p = Patterns[i];
                double from = p[step % p.Length], to = p[(step + 1) % p.Length];
                into[i] = Math.Max(2, (int)Math.Round((from + (to - from) * t) * MaxH / 16));
            }
        }

        /// <summary>Left edge of the first bar and the bars' bottom line, for a cell.</summary>
        public static void Place(Rectangle cell, out int x, out int bottom)
        {
            int w = Count * BarW + (Count - 1) * Gap;
            x = cell.Left + (cell.Width - w) / 2;
            bottom = cell.Top + (cell.Height + MaxH) / 2;
        }

        /// <summary>The bars' own box in a cell.</summary>
        public static Rectangle Box(Rectangle cell)
        {
            int x, bottom;
            Place(cell, out x, out bottom);
            return new Rectangle(x, bottom - MaxH, Count * BarW + (Count - 1) * Gap, MaxH);
        }

        static readonly int[] _now = new int[Count];

        /// <summary>Draws the bars as they are now in a cell (screen coordinates).</summary>
        public static void Draw(Graphics g, Rectangle cell, Color colour)
        {
            Heights(_now);
            int x, bottom;
            Place(cell, out x, out bottom);
            using (SolidBrush b = new SolidBrush(colour))
                for (int i = 0; i < Count; i++)
                    g.FillRectangle(b, x + i * (BarW + Gap), bottom - _now[i], BarW, _now[i]);
            // only trusted as "on screen" when this paint covers the whole box (a clipped paint may not show it)
            if (g.ClipBounds.Contains(Box(cell)))
            {
                ShownCell = cell;
                Array.Copy(_now, Shown, Count);
            }
        }
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
        public const int HeaderH = 40;
        public bool CanBack, CanForward;
        public event EventHandler Back, Forward;
        public ContextMenuStrip UserMenu;      // shown by the profile button
        string _user = "";
        public void SetUser(string name) { _user = name ?? ""; }
        public string User { get { return _user; } }
        int _hdrHot;                           // 1 back, 2 forward, 3 profile, 4 search box, -1 elsewhere on the header
        int _solidAt = 120;                    // scroll position at which the bar has faded in completely
        const int BtnD = 24, BtnY = (HeaderH - BtnD) / 2;       // the round buttons: diameter, and centred in the bar
        Rectangle BackRect { get { return new Rectangle(16, BtnY, BtnD, BtnD); } }
        Rectangle ForwardRect { get { return new Rectangle(16 + BtnD + 8, BtnY, BtnD, BtnD); } }
        Rectangle UserRect { get { return new Rectangle(ClientSize.Width - BarW - 16 - BtnD, BtnY, BtnD, BtnD); } }
        int HeaderAt(Point p)
        {
            if (SearchShown && SearchRect.Contains(p)) return 4;      // wherever the box currently is, in the bar or below it
            if (p.Y >= HeaderH) return 0;
            if (Sticky() != null && StickyPlayRect.Contains(p)) return 5;
            return BackRect.Contains(p) ? 1 : ForwardRect.Contains(p) ? 2 : UserRect.Contains(p) ? 3 : -1;
        }

        // ---- once the page's own play button has scrolled up under the top bar, the bar shows a play button and the
        // page's title beside back / forward (as on Spotify)
        const int StickyD = 32;
        Rectangle StickyPlayRect { get { return new Rectangle(ForwardRect.Right + 10, (HeaderH - StickyD) / 2, StickyD, StickyD); } }
        Block Sticky()
        {
            foreach (Block b in _blocks)
                if (b.StickyTitle != null && b.StickyButton != null && _scroll >= b.Top + b.StickyFrom - HeaderH) return b;
            return null;
        }

        // ---- the page's key colour, taken from its cover art: a gradient behind the top of the page (drawn once
        // into a bitmap, then just copied while scrolling) and the solid header
        const int GradH = 248;
        string _keyUrl;
        bool _haveKey;
        Color _key;
        Bitmap _grad;

        void DropGradient() { if (_grad != null) { _grad.Dispose(); _grad = null; } _gradColumn = null; _washVersion++; }

        /// <summary>Changes whenever the page's background (the colour wash) does: what blocks drew over it in advance
        /// must then be drawn again.</summary>
        public int WashVersion { get { return _washVersion; } }
        int _washVersion;

        // ---- "Search in playlist", on pages whose track list asks for one (Block.WantsSearch). It sits in the row the
        // list leaves free above its column headers and scrolls with the page - until it reaches the top bar, where it
        // stays, docked beside the profile button (same x all along, so it only ever moves straight up). The text box
        // itself only exists on screen while it is being typed in or holds text; otherwise just the frame is drawn.
        readonly System.Windows.Forms.TextBox _input = new System.Windows.Forms.TextBox();
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
                return new Rectangle(right - w, y, w, SearchH);
            }
        }
        const int SearchH = 24;
        const int DockY = (HeaderH - SearchH) / 2;      // where the box rests in the top bar
        bool SearchShown { get { return _searchBlock != null && SearchRect.Width >= 100; } }
        bool SearchHasText { get { return _searchBlock != null && InputText(_searchBlock).Length > 0; } }

        int HeaderAlpha { get { return _scroll <= 0 ? 0 : _scroll >= _solidAt ? 255 : (int)(255L * _scroll / _solidAt); } }

        /// <summary>How far the page is scrolled, in pixels.</summary>
        public int ScrollY { get { return _scroll; } }

        /// <summary>When g paints into the page's off-screen copy: its pixels (24-bit B,G,R, top row first), for
        /// native code to draw into directly; size = the copy's size. False otherwise.</summary>
        public bool CopyBits(Graphics g, out IntPtr bits, out int stride, out Size size)
        {
            bits = IntPtr.Zero; stride = 0; size = ClientSize;
            if (!_copy.Owns(g)) return false;
            bits = _copy.Bits(out stride);
            return bits != IntPtr.Zero;
        }
        /// <summary>How solid the top bar is right now (0 clear - 255 solid), and its colour: a header dims into that
        /// colour at the same rate, so the two meet without a seam.</summary>
        public int HeaderFade { get { return HeaderAlpha; } }
        public Color HeaderTint { get { return HeaderColor; } }
        Color HeaderColor { get { return !_haveKey ? Color.FromArgb(32, 32, 32) : Modern ? Shade(_key, 40) : Shade(_key, 69); } }

        /// <summary>The colour of the search box: what is behind it (the gradient, under however much of the bar has
        /// faded in), lightened a little - what a translucent white box would look like. The text box uses it too.</summary>
        Color SearchBack()
        {
            Rectangle box = SearchRect;
            Color under = WashAt(box.Top + box.Height / 2 + _scroll);
            int a = box.Top < HeaderH ? HeaderAlpha : 0; Color bar = HeaderColor;      // the bar only counts once the box is in it
            under = Color.FromArgb(under.R + (bar.R - under.R) * a / 255, under.G + (bar.G - under.G) * a / 255, under.B + (bar.B - under.B) * a / 255);
            return Color.FromArgb(under.R + (255 - under.R) / 10, under.G + (255 - under.G) / 10, under.B + (255 - under.B) / 10);
        }

        static readonly Color SearchSoft = Color.FromArgb(179, 179, 179);
        void PaintSearch(Graphics g)
        {
            if (!SearchShown) return;
            Rectangle s = SearchRect;
            if (!g.IsVisible(s)) return;                 // a partial repaint elsewhere (hovering rows): nothing to do
            Theme.FillRounded(g, s, 4, SearchBack());
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen lens = new Pen(SearchSoft, 1.5f))
            {
                g.DrawEllipse(lens, s.Left + 9, s.Top + 7, 7.5f, 7.5f);
                g.DrawLine(lens, s.Left + 15.6f, s.Top + 13.6f, s.Left + 19, s.Top + 17);
            }
            if (SearchHasText)
                using (Pen x = new Pen(_hdrHot == 4 ? Color.White : SearchSoft, 1.6f))
                {
                    float cx = s.Right - 14, cy = s.Top + s.Height / 2f;
                    g.DrawLine(x, cx - 3.5f, cy - 3.5f, cx + 3.5f, cy + 3.5f);
                    g.DrawLine(x, cx - 3.5f, cy + 3.5f, cx + 3.5f, cy - 3.5f);
                }
            g.SmoothingMode = SmoothingMode.None;
            if (!InputWanted(_searchBlock))
                Theme.Text1(g, "Search in playlist", Theme.Normal, new Rectangle(s.Left + 28, s.Top, s.Width - 36, s.Height), SearchSoft);
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
            Rectangle s = new Rectangle(f.Left + 28, f.Top + (f.Height - h) / 2, f.Width - 28 - 26, h);
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
            if (_haveKey) return;
            if (_keyBlock != null && !_keyBlock.KeyColor.IsEmpty) { _key = _keyBlock.KeyColor; _haveKey = true; DropGradient(); return; }
            if (_keyUrl == null) return;
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
            double k = 140.0 / Math.Max(1.0, Math.Max(r, Math.Max(g, b)));     // ...at a fixed strength: brightest channel 140, about what Spotify's own picks have
            _key = Color.FromArgb(Math.Min(255, (int)(r * k)), Math.Min(255, (int)(g * k)), Math.Min(255, (int)(b * k)));
            _haveKey = true;
            DropGradient();
        }

        // The colour wash behind the top of the page, the way Spotify's client does it from one key colour:
        //   - behind the header: the key colour at the top, darkening to half by the header's bottom edge
        //     (a banner header covers this part with its picture, darkened the same way);
        //   - below the header: a darker shade (40%) fading out to the page colour over 232 px - the buttons, the
        //     column headers and the first rows sit on this;
        //   - the top bar, once scrolled: that same 40% shade.
        // Pages whose header doesn't say where it ends (artist) keep the plain older look: one fade from the top.
        Block _keyBlock;
        const int WashH = 232;
        bool Modern { get { return _keyBlock != null && _keyBlock.WashTop >= 0; } }
        int WashTop { get { return Modern ? _keyBlock.Top + _keyBlock.WashTop : 0; } }
        static Color Shade(Color c, int percent) { return Color.FromArgb(c.R * percent / 100, c.G * percent / 100, c.B * percent / 100); }
        static Color Mix(Color a, Color b, double t)
        {
            if (t < 0) t = 0; else if (t > 1) t = 1;
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }
        /// <summary>The wash's colour at a page y (the page colour below it).</summary>
        Color WashAt(int y)
        {
            if (!_haveKey) return Theme.Main;
            if (!Modern) return Mix(Shade(_key, 69), Theme.Main, y / (double)GradH);
            int top = WashTop;
            if (y < top) return Mix(_key, Shade(_key, 50), y / (double)Math.Max(1, top));
            return Mix(Shade(_key, 40), Theme.Main, (y - top) / (double)WashH);
        }

        void PaintGradient(Graphics g)
        {
            if (!_haveKey) return;
            int gh = Modern ? WashTop + WashH : GradH;
            if (_scroll >= gh) return;
            EnsureGradient();
            FillWashRows(g, new Rectangle(0, 0, ClientSize.Width, gh - _scroll), _scroll, 0, Theme.Main);      // see FillWashRows
        }

        /// <summary>(Re)builds the gradient bitmap for the current width, if the page has a key colour.</summary>
        void EnsureGradient()
        {
            if (!_haveKey) return;
            bool modern = Modern;
            int top = WashTop, gh = modern ? top + WashH : GradH;
            int w = Math.Max(64, (ClientSize.Width + 63) / 64 * 64);      // 64 px steps: resizing doesn't rebuild it every pixel
            if (_grad == null || _grad.Width != w || _grad.Height != gh)
            {
                DropGradient();
                _grad = new Bitmap(w, gh, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                using (Graphics bg = Graphics.FromImage(_grad))
                {
                    if (!modern) Fade(bg, 0, gh, w, Shade(_key, 69), Theme.Main);
                    else
                    {
                        if (top > 0) Fade(bg, 0, top, w, _key, Shade(_key, 50));
                        Fade(bg, top, WashH, w, Shade(_key, 40), Theme.Main);
                    }
                }
            }
        }

        /// <summary>Paints dst (in g's own coordinates, e.g. a bitmap a block draws a row into) with the page's
        /// background as it is at content row contentY and below - the same pixels the page itself shows there.</summary>
        public void PaintBackdrop(Graphics g, Rectangle dst, int contentY)
        {
            FillWashRows(g, dst, contentY, 0, Theme.Main);
        }

        /// <summary>
        /// Fills dst (in g's coordinates, within g's clip) with the page's background as it is from content row
        /// contentTop down - the colour wash, then the plain page colour - faded alpha/255 of the way to tint.
        /// Every row of the wash is one colour, so this is plain GDI fills, one per run of equal rows. Drawing the
        /// wash's bitmap instead cost tens of milliseconds per paint on the Pentium II, however small the area:
        /// GDI+ processes the whole picture each time.
        /// </summary>
        public void FillWashRows(Graphics g, Rectangle dst, int contentTop, int alpha, Color tint)
        {
            Rectangle a = Rectangle.Intersect(dst, Rectangle.Round(g.ClipBounds));   // plain GDI ignores g's clip
            if (a.Width <= 0 || a.Height <= 0) return;
            EnsureGradient();
            IntPtr dc = g.GetHdc();
            try
            {
                IntPtr brush = GetStockObject(DC_BRUSH);
                int y = a.Top;
                while (y < a.Bottom)
                {
                    Color c = WashDimmed(contentTop + y - dst.Top, alpha, tint);
                    int end = y + 1;
                    while (end < a.Bottom && WashDimmed(contentTop + end - dst.Top, alpha, tint) == c) end++;
                    SetDCBrushColor(dc, ColorTranslator.ToWin32(c));
                    RECT r;
                    r.Left = a.Left; r.Right = a.Right; r.Top = y; r.Bottom = end;
                    FillRect(dc, ref r, brush);
                    y = end;
                }
            }
            finally { g.ReleaseHdc(dc); }
        }

        Color WashDimmed(int contentY, int alpha, Color tint)
        {
            Color under = WashColour(contentY);
            if (alpha <= 0) return under;
            return Color.FromArgb(under.R + (tint.R - under.R) * alpha / 255,
                under.G + (tint.G - under.G) * alpha / 255, under.B + (tint.B - under.B) * alpha / 255);
        }

        /// <summary>The page's background colour at content row y: the wash, or the plain page colour.</summary>
        Color WashColour(int contentY)
        {
            if (!_haveKey || _grad == null) return Theme.Main;
            if (_gradColumn == null || _gradColumn.Length != _grad.Height)
            {
                _gradColumn = new Color[_grad.Height];
                for (int i = 0; i < _gradColumn.Length; i++) _gradColumn[i] = _grad.GetPixel(0, i);
            }
            return contentY >= 0 && contentY < _gradColumn.Length ? _gradColumn[contentY] : Theme.Main;
        }
        /// <summary>Paints a strip of the control with exactly what the page's background is there right now - the
        /// colour wash at the current scroll position, or the plain page colour past it. For things pinned over the
        /// scrolling content (the column headers): they hide the rows passing behind without showing as a bar.</summary>
        public void FillWash(Graphics g, Rectangle r)
        {
            FillWashRows(g, r, r.Top + _scroll, 0, Theme.Main);
        }
        static void Fade(Graphics g, int y, int h, int w, Color from, Color to)
        {
            using (LinearGradientBrush lb = new LinearGradientBrush(new Rectangle(0, y - 1, w, h + 2), from, to, LinearGradientMode.Vertical))
                g.FillRectangle(lb, 0, y, w, h);
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
            using (Pen p = new Pen(_hdrHot == 3 ? Color.White : Color.FromArgb(200, 200, 200), 1.4f))
            {
                g.DrawEllipse(p, cx - 3, cy - 7, 6, 6);                    // head
                g.DrawArc(p, cx - 5.5f, cy + 1, 11, 10, 180, 180);         // shoulders
            }
            g.SmoothingMode = SmoothingMode.None;
            Block sticky = Sticky();
            if (sticky != null)
            {
                Rectangle play = StickyPlayRect;
                sticky.StickyButton.Paint(g, play, _hdrHot == 5);          // the same green button (grows a pixel under the pointer)
                // the title, up to the search box when it is docked in the bar, else the profile button
                int right = (SearchShown && SearchRect.Top < HeaderH ? SearchRect.Left : UserRect.Left) - 12, x = play.Right + 12;
                if (right > x) Theme.Text1(g, sticky.StickyTitle, Theme.BarTitle, new Rectangle(x, 0, right - x, HeaderH), Theme.Text);
            }
        }

        static void RoundButton(Graphics g, Rectangle r, bool hot)
        {
            if (hot) r.Inflate(1, 1);                  // grows a pixel under the pointer, like the play buttons
            using (SolidBrush b = new SolidBrush(Color.FromArgb(hot ? 210 : 150, 0, 0, 0))) g.FillEllipse(b, r);
        }

        static void Chevron(Graphics g, Rectangle r, bool left, bool enabled, bool hot)
        {
            Color c = !enabled ? Color.FromArgb(95, 95, 95) : hot ? Color.White : Color.FromArgb(200, 200, 200);
            int cx = r.Left + r.Width / 2, cy = r.Top + r.Height / 2, d = left ? 1 : -1;
            using (Pen p = new Pen(c, 1.6f))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                g.DrawLine(p, cx + 1.5f * d, cy - 4.5f, cx - 2.5f * d, cy);
                g.DrawLine(p, cx - 2.5f * d, cy, cx + 1.5f * d, cy + 4.5f);
            }
        }

        public PageView(IPageHost host)
        {
            _host = host;
            // no OptimizedDoubleBuffer: the page keeps its own off-screen copy (_copy), which also serves as the buffer
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            SetStyle(ControlStyles.OptimizedDoubleBuffer, false);
            _copy = new ScrollBuffer(this);
            BackColor = Theme.Main;
            ForeColor = Theme.Text;
            _bar = new SlimScroll(this, delegate { return _scroll; }, delegate { return MaxScroll; },
                delegate (int v) { ScrollTo(v); }, 80,
                Color.Transparent, Color.FromArgb(90, 90, 90), Color.FromArgb(150, 150, 150));      // no track: the page shows through
            _bar.OwnerRepaints = true;                 // ScrollTo moves the pixels and repaints the bar itself
            SetUpInput();
            _bar.TopInset = HeaderH;                   // the up arrow starts just under the header...
            _bar.BottomInset = Theme.CardRadius;       // ...and the down arrow ends where the bottom corner starts to curve
            _eqTimer.Interval = Equalizer.Interval;
            _eqTimer.Tick += delegate { AnimateEqualizer(); };
            _eqTimer.Start();
        }

        // ---- the playing song's bars (Equalizer). Each frame changes only the bars' pixels that need it, drawn
        // straight onto the window (plain GDI, no repaint of the page). Where that isn't safe - the row partly under
        // the top bar or pinned headers, or what is on screen isn't known - the cell is repainted instead
        // (OnPaint's quick path: background + bars only).
        readonly Timer _eqTimer = new Timer();
        Block _eqBlock;                     // a quick repaint of the bars is waiting: this block,
        Rectangle _eqRect;                  // ...at this rectangle (screen)
        readonly int[] _eqNow = new int[Equalizer.Count];
        Color[] _gradColumn;                // the page gradient's colour at each row of it (it only varies downwards)

        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("user32.dll")] static extern int FillRect(IntPtr dc, ref RECT r, IntPtr brush);
        [DllImport("gdi32.dll")] static extern IntPtr GetStockObject(int obj);
        [DllImport("gdi32.dll")] static extern int SetDCBrushColor(IntPtr dc, int colour);
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        const int DC_BRUSH = 18;

        void AnimateEqualizer()
        {
            if (!Visible || !_host.IsPlaying || _host.PlayingTrack == null) return;
            Form f = FindForm();
            if (f != null && f.WindowState == FormWindowState.Minimized) return;
            Block pin = Pinned();
            int top = HeaderH + (pin != null ? pin.PinHeight : 0);         // below the top bar and any pinned headers
            foreach (Block b in _blocks)
            {
                Rectangle a = b.AnimatedRect(this);
                if (a.IsEmpty) continue;
                Rectangle s = new Rectangle(a.Left, b.Top - _scroll + a.Top, a.Width, a.Height);
                if (s.Bottom <= HeaderH || s.Top >= ClientSize.Height) continue;      // scrolled out of sight
                bool open = s.Top >= top && s.Bottom <= ClientSize.Height;
                if (open && Equalizer.ShownCell == s && IsHandleCreated)
                {
                    DrawBarChanges(s, b.AnimatedBack(this));
                    continue;
                }
                Equalizer.ShownCell = Rectangle.Empty;
                if (open && _eqBlock == null) { _eqBlock = b; _eqRect = s; }
                Invalidate(s);
            }
        }

        // Grows / shrinks each bar on screen by the pixels that changed since the last frame.
        void DrawBarChanges(Rectangle cell, Color back)
        {
            Equalizer.Heights(_eqNow);
            int[] was = Equalizer.Shown;
            bool same = true;
            for (int i = 0; i < Equalizer.Count; i++) if (_eqNow[i] != was[i]) { same = false; break; }
            if (same) return;
            IntPtr dc = GetDC(Handle);
            if (dc == IntPtr.Zero) return;
            try { DrawBars(dc, cell, back, was); }
            finally { ReleaseDC(Handle, dc); }
            // the page's off-screen copy has to show the same, or scrolling would bring back old bars
            IntPtr copy = _copy.GetHdc();
            if (copy != IntPtr.Zero)
                try { DrawBars(copy, cell, back, was); }
                finally { _copy.ReleaseHdc(copy); }
            Array.Copy(_eqNow, was, Equalizer.Count);
        }

        // the bars' changes since "was" (the heights on screen), onto a device context
        void DrawBars(IntPtr dc, Rectangle cell, Color back, int[] was)
        {
            int x, bottom;
            Equalizer.Place(cell, out x, out bottom);
            {
                IntPtr brush = GetStockObject(DC_BRUSH);
                int green = ColorTranslator.ToWin32(Theme.Accent);
                for (int i = 0; i < Equalizer.Count; i++)
                {
                    int bx = x + i * (Equalizer.BarW + Equalizer.Gap), h = _eqNow[i], o = was[i];
                    if (h > o)          // grew: the new top in green
                    {
                        SetDCBrushColor(dc, green);
                        Fill(dc, brush, bx, bottom - h, bottom - o);
                    }
                    else if (h < o)     // shrank: the old top back to the background
                    {
                        if (back != Color.Empty || !InGradient(bottom - o))
                        {
                            SetDCBrushColor(dc, ColorTranslator.ToWin32(back != Color.Empty ? back : Theme.Main));
                            Fill(dc, brush, bx, bottom - o, bottom - h);
                        }
                        else
                            for (int y = bottom - o; y < bottom - h; y++)       // the gradient, row by row
                            {
                                SetDCBrushColor(dc, ColorTranslator.ToWin32(GradientAt(y)));
                                Fill(dc, brush, bx, y, y + 1);
                            }
                    }
                }
            }
        }

        /// <summary>Paints screen rows screenTop..screenBottom, right across the page (within g's clip), with the page's
        /// background (the colour wash) faded alpha/255 of the way to tint - the same result as a see-through fill over
        /// the wash, with no blending (see FillWashRows).</summary>
        public void PaintDimmedWash(Graphics g, int screenTop, int screenBottom, int alpha, Color tint)
        {
            if (screenBottom <= screenTop) return;
            FillWashRows(g, new Rectangle(0, screenTop, ClientSize.Width, screenBottom - screenTop), screenTop + _scroll, alpha, tint);
        }

        static void Fill(IntPtr dc, IntPtr brush, int x, int top, int bottom)
        {
            RECT r;
            r.Left = x; r.Right = x + Equalizer.BarW; r.Top = top; r.Bottom = bottom;
            FillRect(dc, ref r, brush);
        }

        // is screen row y over the page's gradient (see PaintGradient)?
        bool InGradient(int y) { return _haveKey && _grad != null && y + _scroll >= 0 && y + _scroll < _grad.Height; }

        Color GradientAt(int y) { return WashColour(y + _scroll); }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _eqTimer.Dispose(); _copy.Dispose();
                foreach (Block b in _blocks) b.DropCache();
            }
            base.Dispose(disposing);
        }

        public IPageHost Host { get { return _host; } }

        public void SetBlocks(IList<Block> blocks) { SetBlocks(blocks, false); }

        /// <summary>True while the page's search box is open or holds text (a filter is being used).</summary>
        public bool SearchInUse { get { return _inputWanted || _input.Text.Length > 0; } }

        /// <summary>keepScroll: the same page, updated (a playlist that changed since it was saved): stays where it
        /// was scrolled to, as far as the new content allows.</summary>
        public void SetBlocks(IList<Block> blocks, bool keepScroll)
        {
            foreach (Block b in _blocks) b.DropCache();
            _blocks.Clear();
            _blocks.AddRange(blocks);
            string url = null;
            _keyBlock = null;
            foreach (Block b in _blocks) { url = b.KeyImageUrl; if (!String.IsNullOrEmpty(url)) { _keyBlock = b; break; } url = null; }
            _keyUrl = url; _haveKey = false;             // worked out again for every page (cheap: the picture is cached)
            DropGradient();
            _inputOwner = null; _inputWanted = false;                   // a new page: the search box starts empty and closed
            _searchBlock = null;
            foreach (Block b in _blocks) if (b.WantsSearch) { _searchBlock = b; break; }
            _inputQuiet = true; _input.Text = ""; _inputQuiet = false;
            if (_input.Visible) _input.Visible = false;
            if (!keepScroll) _scroll = 0;           // (kept: EnsureLayout clamps it to the new content)
            _hot = null;
            _layoutWidth = -1;
            Invalidate();
        }

        public void Relayout() { _layoutWidth = -1; DropCaches(); Invalidate(); }

        /// <summary>Repaints the whole page with nothing reused from before (e.g. emoji pictures have just arrived).</summary>
        public void Redraw() { DropCaches(); Invalidate(); }

        void DropCaches() { foreach (Block b in _blocks) b.DropCache(); }

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

        public void ScrollBy(int dy) { ScrollTo(_scroll + dy); }

        // ---- scrolling. Instead of repainting the whole page on every step (every row's text, the artwork, the
        // header...), the pixels already on screen are moved (PixelScroll) and only what that can't cover is painted:
        // the strip that comes into view, plus the parts that stay put while the page moves under them - the top bar
        // (it fades in with the scroll), the pinned column headers, the search box, the scrollbar and the rounded
        // bottom corners. The picture ends up exactly as a full repaint would draw it.
        int _scrolledAt;                       // Environment.TickCount of the last step (0 = none yet)

        /// <summary>True while the page is being scrolled (and briefly after): repaints that can wait, should.</summary>
        public bool Scrolling { get { return _scrolledAt != 0 && unchecked(Environment.TickCount - _scrolledAt) < 250; } }

        void ScrollTo(int target)
        {
            if (IsHandleCreated) Update();     // anything already waiting is painted at the old position first
            EnsureLayout();
            int old = _scroll;
            Block pinWas = Pinned();
            Rectangle searchWas = SearchShown ? SearchRect : Rectangle.Empty;
            _scroll = target;
            ClampScroll();
            int dy = _scroll - old;
            if (dy == 0) return;
            _scrolledAt = Environment.TickCount;
            if (_scrolledAt == 0) _scrolledAt = 1;
            _eqBlock = null;
            Block pinNow = Pinned();
            int pinH = Math.Max(pinWas != null ? pinWas.PinHeight : 0, pinNow != null ? pinNow.PinHeight : 0);
            int w = ClientSize.Width, h = ClientSize.Height;
            int top = Math.Min(h, HeaderH + pinH), bottom = Math.Max(top, h - Theme.CardRadius);
            Rectangle area = new Rectangle(0, top, Math.Max(0, w - BarW), bottom - top);
            // A header that dims (and zooms) with the scroll: its part in view is repainted, not moved, until the
            // top bar is solid - from then on it is the bar's plain colour and moves like the rest. So the pixels that
            // move (in the copy and on screen) start below it: moving it too would be wasted work, and on screen it
            // would show for a moment before its new picture arrived - the banner jumping and coming back. Below
            // means below where it ends before or after this step, whichever is higher: what comes into view under
            // it is then the header's own new rows.
            List<Rectangle> parts = new List<Rectangle>();
            Rectangle move = area;
            if (old < _solidAt || _scroll < _solidAt)
                foreach (Block b in _blocks)
                {
                    Rectangle linked = b.ScrollLinked;
                    if (linked.IsEmpty) continue;
                    Rectangle band = new Rectangle(0, top, w, bottom - top);
                    Rectangle now = new Rectangle(0, b.Top - _scroll + linked.Top, w, linked.Height);
                    Rectangle was = new Rectangle(0, b.Top - old + linked.Top, w, linked.Height);
                    now.Intersect(band); was.Intersect(band);
                    if (now.Height > 0) parts.Add(now);
                    int under = Math.Min(now.Height > 0 ? now.Bottom : top, was.Height > 0 ? was.Bottom : top);
                    if ((now.Height > 0 && now.Top <= move.Top) || (was.Height > 0 && was.Top <= move.Top))
                        if (under > move.Top) move = new Rectangle(area.Left, under, area.Width, Math.Max(0, area.Bottom - under));
                }
            // all the work happens in the page's off-screen copy; the screen only gets the finished step
            if (!_copy.Shift(move, dy))
            {
                Equalizer.ShownCell = Rectangle.Empty;
                Invalidate();
                Update();
                return;
            }
            // the playing song's bars moved with everything else (still known on screen if they stayed inside the area)
            Rectangle cell = Equalizer.ShownCell;
            if (!cell.IsEmpty)
            {
                Rectangle moved = cell;
                moved.Offset(0, -dy);
                Equalizer.ShownCell = move.Contains(cell) && move.Contains(moved) ? moved : Rectangle.Empty;
            }
            // What has to be painted afresh (everything else was moved):
            parts.Add(dy > 0 ? new Rectangle(0, move.Bottom - dy, w, dy)          // the strip that came into view
                             : new Rectangle(0, move.Top, w, -dy));
            parts.Add(new Rectangle(0, 0, w, top));                             // top bar, pinned headers, top corners
            parts.Add(new Rectangle(area.Right, top, w - area.Right, bottom - top));     // the scrollbar's strip
            parts.Add(new Rectangle(0, bottom, w, h - bottom));                 // bottom corners
            if (!searchWas.IsEmpty)
            {
                searchWas.Offset(0, -dy);      // where the shift took the box's old picture (it may have docked since)
                parts.Add(searchWas);
            }
            if (SearchShown) parts.Add(SearchRect);
            // ...as few paints as possible (each one re-runs everything under it), into the copy; then the screen
            // moves its own pixels and takes just these pieces from the copy
            List<Rectangle> pieces = ScrollBuffer.Coalesce(parts, ClientSize);
            ClipPainter paint = PaintPage;
            foreach (Rectangle p in pieces) _copy.Draw(p, paint);
            _copy.PresentScrolled(move, dy, pieces);
        }

        // the page keeps its own off-screen copy of what it shows (see ScrollBuffer): every paint goes through it
        readonly ScrollBuffer _copy;

        protected override void OnPaintBackground(PaintEventArgs e) { }        // OnPaint covers everything

        protected override void OnPaint(PaintEventArgs e)
        {
            // An animation frame of the playing song's bars, with nothing else to repaint: the background and the
            // bars only - not the whole row (its cover, text) and the page's overlays, 20 times a second.
            Block eq = _eqBlock;
            _eqBlock = null;
            if (eq != null && e.ClipRectangle == _eqRect && _blocks.Contains(eq) && _copy.Ready)
            {
                Equalizer.ShownCell = Rectangle.Empty;
                _copy.Paint(e.Graphics, e.ClipRectangle, delegate (Graphics qg, Rectangle qclip)
                {
                    qg.Clear(Theme.Main);
                    PaintGradient(qg);
                    eq.PaintAnimated(qg, new Rectangle(0, eq.Top - _scroll, _layoutWidth, eq.Height), this);
                });
                return;
            }
            if (!_copy.Paint(e.Graphics, e.ClipRectangle, PaintPage)) PaintPage(e.Graphics, e.ClipRectangle);
        }

        /// <summary>Paints the part of the page inside clip (client coordinates) with g.</summary>
        void PaintPage(Graphics g, Rectangle clip)
        {
            // whatever this paint puts over the bars' cell, the next frame can't assume what is there (Equalizer.Draw
            // notes it again if it draws them)
            if (!Equalizer.ShownCell.IsEmpty && clip.IntersectsWith(Equalizer.ShownCell)) Equalizer.ShownCell = Rectangle.Empty;
            EnsureLayout();
            bool hadKey = _haveKey;
            EnsureKey();
            // the colour has only just been worked out, during a paint of part of the page: the rest needs it too
            if (_haveKey && !hadKey && clip != ClientRectangle) Invalidate();
            SyncInput();
            PaintBackground(g, clip);
            foreach (Block b in _blocks)
            {
                Rectangle r = new Rectangle(0, b.Top - _scroll, _layoutWidth, b.Height);
                if (r.Bottom < clip.Top || r.Top - b.Bleed > clip.Bottom) continue;
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

        /// <summary>The page's background (its colour wash, then the plain page colour) in clip - except rows a block
        /// is about to cover completely anyway (a header's banner): painting under those was wasted work.</summary>
        void PaintBackground(Graphics g, Rectangle clip)
        {
            List<Rectangle> covered = new List<Rectangle>();
            foreach (Block b in _blocks)
            {
                Rectangle r = new Rectangle(0, b.Top - _scroll, _layoutWidth, b.Height);
                if (r.Bottom < clip.Top || r.Top - b.Bleed > clip.Bottom) continue;
                Rectangle o = b.OpaqueRows(r, this);
                if (o.Height > 0) covered.Add(o);
            }
            int w = ClientSize.Width, y = clip.Top;
            while (y < clip.Bottom)
            {
                bool skip = false;
                foreach (Rectangle o in covered) if (y >= o.Top && y < o.Bottom) { y = o.Bottom; skip = true; break; }
                if (skip) continue;
                int end = clip.Bottom;
                foreach (Rectangle o in covered) if (o.Top > y && o.Top < end) end = o.Top;
                FillWashRows(g, new Rectangle(0, y, w, end - y), y + _scroll, 0, Theme.Main);
                y = end;
            }
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
                else if (hh == 5)
                {
                    Block sb = Sticky();
                    if (sb != null && sb.StickyButton.Action != null) sb.StickyButton.Action(this, EventArgs.Empty);
                }
                else if (hh == 4)
                {
                    if (e.X >= SearchRect.Right - 26 && SearchHasText) ClearInput(_searchBlock);      // the X
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
                if (hh == 4) Cursor = e.X >= SearchRect.Right - 26 && SearchHasText ? Cursors.Hand : Cursors.IBeam;      // a text field, except over its X
                else Cursor = hh == 3 || hh == 5 || (hh == 1 && CanBack) || (hh == 2 && CanForward) ? Cursors.Hand : Cursors.Default;
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
        const int DotGap = 6, DotSize = 4;              // the dot between the parts of the bottom line
        static readonly Color Bright = Color.White;

        /// <summary>Set for playlists that come with a wide banner picture (radios, mixes, "This Is", editorial):
        /// the header is then the banner across the page, running up behind the top bar, with the texts over its
        /// lower left and the buttons in a row underneath - no square cover.</summary>
        public string BannerUrl;
        /// <summary>Plain text before the creator's name on the bottom line ("Made for ").</summary>
        public string ByPrefix;
        bool Banner { get { return !String.IsNullOrEmpty(BannerUrl); } }
        const int BannerH = 250;                                    // from the very top of the page
        const int BannerBottom = BannerH - PageView.HeaderH;        // in block coordinates (the block starts under the top bar)
        // As the page scrolls, the banner picture zooms out and holds its place on screen while its box scrolls up
        // (as on Spotify: measured from its client, the picture barely moves while the box goes). It is fetched this
        // much bigger than the box, so zoomed out fully it still fills it.
        const int ZoomPercent = 5;              // the picture shrinks by this much over the header's scroll
        const int PanPercent = 100;             // how far the picture moves down inside its box, per pixel scrolled
        /// <summary>The header area that dims as the page scrolls: the banner (from the very top of the page), or the
        /// coloured area behind the cover and texts.</summary>
        public override Rectangle ScrollLinked { get { return new Rectangle(0, -Bleed, 1, Bleed + WashTop); } }

        public override Rectangle OpaqueRows(Rectangle r, PageView view)
        {
            int a = view.HeaderFade;
            int dimTop = Math.Max(PageView.HeaderH, r.Top - Bleed), dimBottom = r.Top + WashTop;
            if (a >= 255 || (!Banner && a > 0))          // PaintDim's opaque fills
                return dimBottom > dimTop ? new Rectangle(0, dimTop, 1, dimBottom - dimTop) : Rectangle.Empty;
            if (Banner)
            {
                // the banner itself, once its picture is in (the same size PaintBanner asks for)
                int bw = Math.Max(64, (view.ClientSize.Width + 63) / 64 * 64);
                int zw = (bw * (100 + ZoomPercent) + 99) / 100, zh = (BannerH * (100 + ZoomPercent) + 99) / 100;
                if (view.Host.Images.Get(BannerUrl, zw, zh, true) != null)
                    return new Rectangle(0, r.Top - PageView.HeaderH, 1, BannerH);
            }
            return Rectangle.Empty;
        }

        int PlayIndex { get { for (int i = 0; i < _buttons.Count; i++) if (_buttons[i].Icon == "play") return i; return -1; } }
        public override string StickyTitle { get { return PlayIndex >= 0 ? _title : null; } }
        public override PageButton StickyButton { get { int i = PlayIndex; return i >= 0 ? _buttons[i] : null; } }
        public override int StickyFrom { get { int i = PlayIndex; return i >= 0 ? ButtonRect(i).Bottom : 0; } }
        static readonly Color OnPhoto = Color.FromArgb(228, 228, 228), OnPhotoDim = Color.FromArgb(205, 205, 205);
        public override int Bleed { get { return Banner ? PageView.HeaderH : 0; } }
        public override int WashTop { get { return Banner ? BannerBottom : Art + PadY + 10; } }      // the banner's bottom edge / the header's
        /// <summary>The page colour, when Spotify supplied one for this cover.</summary>
        public Color Key;
        public override Color KeyColor { get { return Key; } }

        /// <summary>label (small caps), title, an optional description under it, then one line: byName (a link when
        /// byTarget is set) and the "bright" parts in white with dots between them, followed by "dim" in grey.</summary>
        public HeaderBlock(string label, string title, string description, string byName, NavTarget byTarget,
            string[] bright, string dim, string imageUrl, List<PageButton> buttons)
        {
            _label = label; _title = title; _description = description; _byName = byName; _byTarget = byTarget;
            _bright = bright ?? new string[0]; _dim = dim; _imageUrl = imageUrl; _buttons = buttons;
        }

        public override int Layout(int width) { return Banner ? BannerBottom + 10 + PageButton.RowHeight(_buttons) + 10 : Art + PadY + 10; }
        public override string KeyImageUrl { get { return _imageUrl; } }
        public override int HeaderContentBottom { get { return Banner ? BannerBottom : PadY + Art; } }       // the cover's and the buttons' bottom edge / the banner's

        bool HasDescription { get { return !String.IsNullOrEmpty(_description); } }
        // where the texts go (block coordinates): beside the cover, or over the bottom left of the banner
        int TextLeft { get { return Banner ? Pad : TextX; } }
        int MetaLeft { get { return TextLeft + 3; } }               // lines up with the padded text above it
        int MetaY { get { return Banner ? BannerBottom - 30 : PadY + (HasDescription ? 86 : 66); } }
        int DescY { get { return Banner ? BannerBottom - 52 : PadY + 64; } }
        int TitleY { get { return Banner ? BannerBottom - (HasDescription ? 96 : 74) : PadY + 20; } }

        // Exact text widths, so the differently coloured parts of the bottom line sit flush against each other.
        // Measuring the text twice over and once, and taking the difference, leaves only what the text itself
        // advances by: whatever padding the measurement adds is in both and cancels out.
        const TextFormatFlags Plain = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        static int TextW(string s, Font f)
        {
            if (String.IsNullOrEmpty(s)) return 0;
            // measured once per text and font: the header is painted again on every scroll step while it is in view
            string key = f.Name + "/" + f.Size + "/" + (int)f.Style + "/" + s;
            int w;
            if (_widths.TryGetValue(key, out w)) return w;
            if (Theme.IsRich(s)) w = Theme.Measure(s, f).Width - 4;          // symbols / emoji: laid out by Theme, 2 px either side
            else
            {
                Size big = new Size(100000, 100);
                w = TextRenderer.MeasureText(s + s, f, big, Plain).Width - TextRenderer.MeasureText(s, f, big, Plain).Width;
            }
            if (_widths.Count > 500) _widths.Clear();
            _widths[key] = w;
            return w;
        }
        static readonly Dictionary<string, int> _widths = new Dictionary<string, int>();
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
            int x = TextLeft;
            for (int j = 0; j < i; j++) x += _buttons[j].Width + 12;
            int rowH = PageButton.RowHeight(_buttons), h = _buttons[i].Height;
            if (Banner) return new Rectangle(x, BannerBottom + 6 + (rowH - h) / 2, _buttons[i].Width, h);      // a row under the banner
            return new Rectangle(x, PadY + Art - 4 - rowH + (rowH - h) / 2, _buttons[i].Width, h);                 // bottom of the row lines up with the cover's
        }
        Rectangle ByRect()
        {
            return new Rectangle(MetaLeft + TextW(ByPrefix, Theme.Normal), MetaY, TextW(_byName, Theme.NormalBold), 20);
        }

        // The banner: scaled to fill the page's width (centre-cropped by the image cache), from the very top of the
        // page, darkened towards the bottom so the texts stay readable.
        // ---- the banner as one picture (the "frame"): the scaled photo, the fade that keeps the texts readable and the
        // scroll dimming, put together in GDI+'s own fast pixel format and then copied to the page in one go. Built once
        // per scroll position - a scroll step repaints the header in several pieces (the header area, the strip under
        // the top bar, the scrollbar's strip...), and each used to scale the photo and blend the fades again, onto the
        // screen's 16-bit pixels.
        Bitmap _frame;                  // GDI+ version (when SpotipaqNative.dll is older than version 3)
        DibPicture _dib;                // native version: spq_banner draws it, BitBlt copies it
        bool _frameValid, _frameNative;
        int _frameW;
        Image _frameSrc;
        int _frameScroll = -1, _frameFade = -1;
        Color _frameTint;

        public override void DropCache()
        {
            if (_frame != null) { _frame.Dispose(); _frame = null; }
            if (_dib != null) { _dib.Dispose(); _dib = null; }
            _frameSrc = null;
            _frameValid = false;
        }

        // ---- the native frame (SpotipaqNative.dll 3+): spq_banner does the zoom, the fade and the dimming in one
        // integer pass straight into a 24-bit GDI picture. GDI+ needed several passes, each converting every pixel to
        // its own format and back - 15-50 ms a scroll step on the Pentium II, plus 2-13 ms more to copy the result.
        [DllImport("SpotipaqNative.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int spq_banner(IntPtr src, int srcW, int srcH, int srcStride, IntPtr dst, int dstStride,
            int x0, int x1, int y0, int y1, int fx0, int fy0, int fstep, int[] keep, int[] add);
        static int _native = -1;        // -1 not checked yet, 0 no (old DLL or it failed), 1 yes
        static bool NativeOk
        {
            get
            {
                // version 4: spq_banner with a column range (MMX). An older DLL's spq_banner takes other arguments,
                // so it is not called at all: the GDI+ frame is used instead.
                if (_native < 0) _native = Spotipaq.Engine.Native.Version() >= 4 ? 1 : 0;
                return _native == 1;
            }
        }

        /// <summary>The native version of the banner frame, in a picture of its own (when painting somewhere other
        /// than the page's off-screen copy, which PaintBanner otherwise draws into directly).</summary>
        bool BuildFrameNative(Bitmap banner, int bw, int zw, int s, int fade, Color tint)
        {
            int from = Math.Min(BannerH, s);           // rows above are off the top of the page
            if (from >= BannerH) return true;
            if (_dib == null || _dib.Width != bw || _dib.Height != BannerH)
            {
                if (_dib != null) _dib.Dispose();
                _dib = DibPicture.Create(bw, BannerH);
                if (_dib == null) return false;
            }
            return RunNative(banner, bw, zw, s, fade, tint, _dib.Bits, _dib.Stride, 0, bw, from, BannerH);
        }

        /// <summary>spq_banner for banner columns x0..x1-1 and rows y0..y1-1 into dst (banner row 0, column 0;
        /// 24-bit, stride bytes per row).</summary>
        bool RunNative(Bitmap banner, int bw, int zw, int s, int fade, Color tint, IntPtr dst, int stride, int x0, int x1, int y0, int y1)
        {
            if (x1 <= x0 || y1 <= y0) return true;
            // where the picture sits: the same zoom and pan as BuildFrame (see there)
            double k, sx;
            int pan;
            if (s == 0) { k = 1; sx = (zw - bw) / 2; pan = 0; }
            else
            {
                double t = Math.Min(1.0, s / (double)BannerBottom);
                double scale = 1.0 - (1.0 - 100.0 / (100 + ZoomPercent)) * t;
                k = 1.0 / scale;
                sx = (zw - bw * k) / 2.0;
                pan = Math.Min(s, s * PanPercent / 100);
            }
            int fx0 = (int)Math.Round((sx + 0.5 * k - 0.5) * 65536), fy0 = (int)Math.Round(((0.5 - pan) * k - 0.5) * 65536);
            int fstep = (int)Math.Round(k * 65536);
            // each row's overlay, as BuildFrame blends it: the readability fade (black at 20 -> the page colour at
            // 120 over the banner's height) and, from the top bar's bottom edge down, the scroll dimming
            int[] keep = new int[BannerH], add = new int[BannerH * 3];
            int dimTop = Math.Max(Math.Min(BannerH, s), PageView.HeaderH + s);
            Color m = Theme.Main;
            for (int y = y0; y < y1; y++)
            {
                double t = (y + 0.5) / (BannerH + 1);
                double a1 = (20 + 100 * t) / 255.0, d = y >= dimTop ? fade / 255.0 : 0;
                int kf = (int)Math.Round((1 - a1) * (1 - d) * 256), cap = 255 * (256 - kf);
                keep[y] = kf;
                add[y * 3 + 0] = Math.Min(cap, (int)Math.Round(256 * (m.B * t * a1 * (1 - d) + tint.B * d)));
                add[y * 3 + 1] = Math.Min(cap, (int)Math.Round(256 * (m.G * t * a1 * (1 - d) + tint.G * d)));
                add[y * 3 + 2] = Math.Min(cap, (int)Math.Round(256 * (m.R * t * a1 * (1 - d) + tint.R * d)));
            }
            System.Drawing.Imaging.BitmapData bd = banner.LockBits(new Rectangle(0, 0, banner.Width, banner.Height),
                System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            try
            {
                return spq_banner(bd.Scan0, bd.Width, bd.Height, bd.Stride, dst, stride,
                    x0, x1, y0, y1, fx0, fy0, fstep, keep, add) == 1;
            }
            catch (Exception) { _native = 0; return false; }      // e.g. an old DLL without spq_banner: GDI+ from now on
            finally { banner.UnlockBits(bd); }
        }

        /// <summary>Paints the banner, dimmed for the scroll. False = the dimming is left to PaintDim (no picture yet,
        /// or fully dimmed, when nothing of the picture shows).</summary>
        bool PaintBanner(Graphics g, Rectangle r, PageView view)
        {
            // width rounded up to 64px steps, so resizing the window doesn't rescale the banner every pixel
            int bw = Math.Max(64, (view.ClientSize.Width + 63) / 64 * 64), top = r.Top - PageView.HeaderH;
            int fade = view.HeaderFade;
            if (fade >= 255) return false;
            int zw = (bw * (100 + ZoomPercent) + 99) / 100, zh = (BannerH * (100 + ZoomPercent) + 99) / 100;
            Bitmap banner = view.Host.Images.Get(BannerUrl, zw, zh, true);      // keep the top of the picture, cut the bottom
            if (banner == null)
            {
                // still loading: the fade alone over the page's colour, as before
                using (LinearGradientBrush fb = new LinearGradientBrush(new Rectangle(r.Left, top, bw, BannerH + 1),
                    Color.FromArgb(0, 0, 0, 0), Color.FromArgb(60, Theme.Main), LinearGradientMode.Vertical))
                    g.FillRectangle(fb, r.Left, top, bw, BannerH);
                return false;
            }
            int s = Math.Max(0, view.ScrollY);
            Color tint = view.HeaderTint;
            // Painting into the page's off-screen copy (scrolling, hover...): the native routine draws just this
            // paint's part of the banner straight into it - nothing kept, nothing copied afterwards.
            IntPtr bits; int stride; Size size;
            if (NativeOk && view.CopyBits(g, out bits, out stride, out size))
            {
                Rectangle part = Rectangle.Intersect(Rectangle.Round(g.ClipBounds), new Rectangle(Point.Empty, size));
                part.Intersect(new Rectangle(r.Left, Math.Max(0, top), bw, top + BannerH - Math.Max(0, top)));
                // banner row 0, column 0 in the copy (above the copy's top when scrolled; only rows on it are written)
                IntPtr origin = new IntPtr(bits.ToInt64() + (long)top * stride + (long)r.Left * 3);
                bool done = part.Width <= 0 || part.Height <= 0 ||
                    RunNative(banner, bw, zw, s, fade, tint, origin, stride, part.Left - r.Left, part.Right - r.Left, part.Top - top, part.Bottom - top);
                if (done) return true;
            }
            if (!_frameValid || _frameW != bw || _frameSrc != banner || _frameScroll != s || _frameFade != fade || _frameTint != tint)
            {
                _frameNative = NativeOk && BuildFrameNative(banner, bw, zw, s, fade, tint);
                if (!_frameNative) BuildFrame(banner, bw, zw, zh, s, fade, tint);
                _frameValid = true; _frameW = bw;
                _frameSrc = banner; _frameScroll = s; _frameFade = fade; _frameTint = tint;
            }
            if (_frameNative)
            {
                // plain GDI copy of just the part inside this paint
                Rectangle to = Rectangle.Intersect(new Rectangle(r.Left, top, bw, BannerH), Rectangle.Round(g.ClipBounds));
                if (to.Width > 0 && to.Height > 0)
                {
                    IntPtr dc = g.GetHdc();
                    try { ScrollBuffer.Copy(dc, to, _dib.Dc, to.X - r.Left, to.Y - top); }
                    finally { g.ReleaseHdc(dc); }
                }
            }
            else
            {
                CompositingMode cm = g.CompositingMode; InterpolationMode im = g.InterpolationMode;
                g.CompositingMode = CompositingMode.SourceCopy; g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.DrawImage(_frame, new Rectangle(r.Left, top, bw, BannerH), 0, 0, bw, BannerH, GraphicsUnit.Pixel);
                g.CompositingMode = cm; g.InterpolationMode = im;
            }
            return true;
        }

        void BuildFrame(Bitmap banner, int bw, int zw, int zh, int s, int fade, Color tint)
        {
            if (_frame == null || _frame.Width != bw || _frame.Height != BannerH)
            {
                if (_frame != null) _frame.Dispose();
                _frame = new Bitmap(bw, BannerH, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            }
            _frameSrc = banner; _frameScroll = s; _frameFade = fade; _frameTint = tint;
            using (Graphics fg = Graphics.FromImage(_frame))
            {
                // only the rows still on the page (the frame's top is s pixels above the window)
                int from = Math.Min(BannerH, s);
                if (from >= BannerH) return;
                fg.SetClip(new Rectangle(0, from, bw, BannerH - from));
                fg.CompositingMode = CompositingMode.SourceCopy;
                if (s == 0)
                {
                    // at rest: the middle of the picture at its own size, a straight copy
                    fg.InterpolationMode = InterpolationMode.NearestNeighbor;
                    fg.DrawImage(banner, new Rectangle(0, 0, bw, BannerH), new Rectangle((zw - bw) / 2, 0, bw, BannerH), GraphicsUnit.Pixel);
                }
                else
                {
                    // scrolled: zoomed out by up to ZoomPercent (fully once the banner's bottom reaches the top bar),
                    // kept centred across, its top edge moved down by the scroll (PanPercent) - the part of the box
                    // above that is off the top of the page
                    double t = Math.Min(1.0, s / (double)BannerBottom);
                    double scale = 1.0 - (1.0 - 100.0 / (100 + ZoomPercent)) * t;      // drawn size / picture size
                    int pan = Math.Min(s, s * PanPercent / 100), h = BannerH - pan;
                    if (h > 0)
                    {
                        float k = (float)(1.0 / scale);                 // picture pixels per frame pixel
                        float sw = bw * k, sh = h * k, sx = (zw - sw) / 2f;
                        fg.InterpolationMode = InterpolationMode.Bilinear;
                        fg.PixelOffsetMode = PixelOffsetMode.Half;     // lines up with the straight copy at rest
                        // drawn one pixel bigger all round: the filter's soft outer edge lands outside the frame
                        fg.DrawImage(banner, new Rectangle(-1, pan - 1, bw + 2, h + 2), sx - k, -k, sw + 2 * k, sh + 2 * k, GraphicsUnit.Pixel);
                        fg.PixelOffsetMode = PixelOffsetMode.Default;
                    }
                }
                fg.CompositingMode = CompositingMode.SourceOver;
                // The fade towards the bottom (so the texts stay readable) and, from the top bar's bottom edge down, the
                // scroll dimming (see PaintDim; the bar does its own strip) - in ONE blend: the two layers' combined
                // colour and strength at the gradient's ends, so each pixel is blended once instead of twice.
                Rectangle span = new Rectangle(0, 0, bw, BannerH + 1);
                Color fadeTop = Color.FromArgb(20, 0, 0, 0), fadeBottom = Color.FromArgb(120, Theme.Main);
                int dimTop = Math.Max(from, PageView.HeaderH + s);
                if (dimTop > from)
                    using (LinearGradientBrush fb = new LinearGradientBrush(span, fadeTop, fadeBottom, LinearGradientMode.Vertical))
                        fg.FillRectangle(fb, 0, from, bw, Math.Min(BannerH, dimTop) - from);
                if (dimTop < BannerH)
                    using (LinearGradientBrush fb = new LinearGradientBrush(span, Under(fadeTop, fade, tint), Under(fadeBottom, fade, tint), LinearGradientMode.Vertical))
                        fg.FillRectangle(fb, 0, dimTop, bw, BannerH - dimTop);
            }
        }

        /// <summary>One see-through colour with the same effect as layer (a see-through colour) followed by tint at
        /// strength dim (0-255) over it.</summary>
        static Color Under(Color layer, int dim, Color tint)
        {
            if (dim <= 0) return layer;
            double a1 = layer.A / 255.0, d = dim / 255.0;
            double a = a1 + d - a1 * d;                             // how much of the picture both together hide
            if (a <= 0) return Color.FromArgb(0, 0, 0, 0);
            double k1 = a1 * (1 - d) / a, k2 = d / a;               // each layer's share of the colour put over it
            return Color.FromArgb((int)Math.Round(a * 255),
                Math.Min(255, (int)Math.Round(layer.R * k1 + tint.R * k2)),
                Math.Min(255, (int)Math.Round(layer.G * k1 + tint.G * k2)),
                Math.Min(255, (int)Math.Round(layer.B * k1 + tint.B * k2)));
        }

        public override void Paint(Graphics g, Rectangle r, PageView view)
        {
            bool banner = Banner;
            if (!banner) PaintDim(g, r, view, true);                    // over the page's colour wash
            else if (!PaintBanner(g, r, view)) PaintDim(g, r, view, view.HeaderFade >= 255);
            if (!banner) Theme.Art(g, view.Host.Images.Get(_imageUrl, Art, Art), new Rectangle(r.Left + Pad, r.Top + PadY, Art, Art), false);
            // (the page's key colour comes from the cover picture either way, see KeyImageUrl)
            int x = r.Left + TextLeft, w = r.Width - TextLeft - Pad;
            Color soft = OnPhoto;                        // on the picture, or on the key colour
            Theme.Text1(g, _label, Theme.Label, new Rectangle(x, r.Top + TitleY - 18, w, 16), soft);
            Theme.Text1(g, _title, Theme.Huge, new Rectangle(x - 2, r.Top + TitleY, w, 40), Theme.Text);
            if (HasDescription)
                Theme.Text1(g, _description, Theme.Normal, new Rectangle(x, r.Top + DescY, w, 20), soft);

            // [Made for] creator • 12 songs, 48 min
            int y = r.Top + MetaY, cx = r.Left + MetaLeft, right = x + w;
            bool first = true;
            if (!String.IsNullOrEmpty(_byName))
            {
                cx = Part(g, ByPrefix, Theme.Normal, cx, y, right, soft);
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
            Part(g, _dim, Theme.Normal, cx, y, right, OnPhotoDim);

            for (int i = 0; i < _buttons.Count; i++)
            {
                Rectangle b = ButtonRect(i); b.Offset(r.Left, r.Top);
                _buttons[i].Paint(g, b, i == _hotButton);
            }
        }

        /// <summary>As the page scrolls, the whole header area (banner, or the colour behind the cover) fades into the
        /// top bar's colour at the same rate as the bar itself, so the two meet without a seam; it is all the bar's
        /// colour once the bar is solid. Under the cover and the texts, which stay as they are. The strip behind the
        /// top bar is left to the bar (its own fade is the same one).</summary>
        void PaintDim(Graphics g, Rectangle r, PageView view, bool overWash)
        {
            int a = view.HeaderFade;
            if (a <= 0) return;
            int top = Math.Max(PageView.HeaderH, r.Top - Bleed), bottom = r.Top + WashTop;
            if (bottom <= top) return;
            if (overWash) { view.PaintDimmedWash(g, top, bottom, a, view.HeaderTint); return; }      // the fast way
            using (SolidBrush b = new SolidBrush(Color.FromArgb(a, view.HeaderTint)))
                g.FillRectangle(b, 0, top, view.ClientSize.Width, bottom - top);
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
        const int HeadH = 32, Pad = 30, ToolsH = 38;       // ToolsH: the 24 px search box with 7 px above and below
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
            if (!EnsureAtlas(r.Width, view)) return;
            // rows on screen right now, for choosing which pictures to keep
            _shownFirst = Math.Max(0, (PageView.HeaderH - r.Top - HeadHeight) / _rowH);
            _shownLast = (view.ClientSize.Height - r.Top - HeadHeight) / _rowH;
            // 1. every row in the paint has an up-to-date picture in the atlas
            List<int> rows = new List<int>();
            List<int> slots = new List<int>();
            List<bool> withBars = new List<bool>();
            for (int i = first; i < _tracks.Count; i++)
            {
                int rowTop = r.Top + HeadHeight + i * _rowH;
                if (rowTop > clip.Bottom) break;
                Track t = _tracks[i];
                bool isPlaying = playing != null && playing.Id == t.Id;
                // the playing song's bars animate, so they are drawn over the row's picture rather than into it
                bool bars = Has(TrackColumns.Number) && isPlaying && view.Host.IsPlaying && i != _hotRow;
                Image cover = Has(TrackColumns.Art) ? view.Host.Images.Get(t.AlbumImageUrl, _artSize, _artSize) : null;
                rows.Add(i); slots.Add(RowPicture(i, t, isPlaying, bars, cover, r.Width, view)); withBars.Add(bars);
            }
            // 2. copied in with plain GDI, only the part inside the paint (GDI+ would process each whole picture)
            if (rows.Count > 0)
            {
                Graphics ag = _atlas.Graphics;
                IntPtr dst = g.GetHdc();
                IntPtr src = ag.GetHdc();
                try
                {
                    for (int k = 0; k < rows.Count; k++)
                    {
                        int rowTop = r.Top + HeadHeight + rows[k] * _rowH;
                        Rectangle to = Rectangle.Intersect(new Rectangle(r.Left, rowTop, r.Width, _rowH), clip);
                        ScrollBuffer.Copy(dst, to, src, to.X - r.Left, slots[k] * _rowH + to.Y - rowTop);
                    }
                }
                finally { ag.ReleaseHdc(src); g.ReleaseHdc(dst); }
            }
            // 3. the playing song's bars on top
            for (int k = 0; k < rows.Count; k++)
                if (withBars[k]) Equalizer.Draw(g, new Rectangle(r.Left + num, r.Top + HeadHeight + rows[k] * _rowH, 30, _rowH), Theme.Accent);
            TrimPictures();
        }

        // ---- row pictures. Drawing a row means four or five lines of text, the cover and the badges; on the Pentium II
        // that is what made scrolling a long list slow. Each row is drawn once into its own slot of one off-screen
        // picture (the "atlas", in the screen's pixel format, with the page's background behind it), and painting
        // just copies it - drawn again only when what the row shows changes (the pointer, the selection, the playing
        // song, its cover arriving, the page's colour). Only the rows on screen and a few either side have a slot:
        // about two screens' worth, a couple of MB.
        sealed class RowPic { public int Slot; public Track Track; public int State; public Image Cover; public int Wash; public bool Drawn; }
        readonly Dictionary<int, RowPic> _pics = new Dictionary<int, RowPic>();
        readonly Stack<int> _freeSlots = new Stack<int>();
        BufferedGraphicsContext _atlasCtx;
        BufferedGraphics _atlas;
        int _atlasW, _atlasSlots, _shownFirst, _shownLast;
        const int KeepAround = 4;           // rows kept either side of those on screen

        public override void DropCache()
        {
            if (_atlas != null) { _atlas.Dispose(); _atlas = null; }
            if (_atlasCtx != null) { _atlasCtx.Dispose(); _atlasCtx = null; }
            _pics.Clear();
            _freeSlots.Clear();
            _atlasW = 0; _atlasSlots = 0;
        }

        bool EnsureAtlas(int width, PageView view)
        {
            int slots = view.ClientSize.Height / _rowH + 3 + 2 * KeepAround;
            if (_atlas != null && _atlasW == width && _atlasSlots >= slots) return true;
            DropCache();
            if (width <= 0 || !view.IsHandleCreated) return false;
            _atlasCtx = new BufferedGraphicsContext();
            _atlasCtx.MaximumBuffer = new Size(width + 1, slots * _rowH + 1);        // kept, not a fresh one per use
            using (Graphics sg = view.CreateGraphics()) _atlas = _atlasCtx.Allocate(sg, new Rectangle(0, 0, width, slots * _rowH));
            _atlasW = width; _atlasSlots = slots;
            for (int s = slots - 1; s >= 0; s--) _freeSlots.Push(s);
            return true;
        }

        /// <summary>The atlas slot holding row i's picture, drawn first if it isn't up to date.</summary>
        int RowPicture(int i, Track t, bool isPlaying, bool bars, Image cover, int width, PageView view)
        {
            bool hot = i == _hotRow;
            int state = (i == _selected ? 1 : 0) | (hot ? 2 : 0) | (isPlaying ? 4 : 0) | (bars ? 8 : 0) |
                        (hot && isPlaying && view.Host.IsPlaying ? 16 : 0) |           // play or pause glyph
                        (hot && _hotLink == "artist" ? 32 : 0) | (hot && _hotLink == "album" ? 64 : 0);
            RowPic p;
            if (!_pics.TryGetValue(i, out p))
            {
                if (_freeSlots.Count == 0) FreeFarthest(i);
                p = new RowPic();
                p.Slot = _freeSlots.Pop();
                _pics[i] = p;
            }
            if (p.Drawn && p.Track == t && p.State == state && p.Cover == cover && p.Wash == view.WashVersion) return p.Slot;
            p.Track = t; p.State = state; p.Cover = cover; p.Wash = view.WashVersion; p.Drawn = true;
            Graphics ag = _atlas.Graphics;
            Rectangle slot = new Rectangle(0, p.Slot * _rowH, width, _rowH);
            ag.SetClip(slot);
            view.PaintBackdrop(ag, slot, Top + HeadHeight + i * _rowH);
            PaintRow(ag, i, t, 0, slot.Top, width, isPlaying, bars, cover, view);
            ag.ResetClip();
            return p.Slot;
        }

        /// <summary>Frees the slot of the kept row farthest from row i (all slots taken).</summary>
        void FreeFarthest(int i)
        {
            int worst = -1, worstD = -1;
            foreach (int k in _pics.Keys) { int d = Math.Abs(k - i); if (d > worstD) { worstD = d; worst = k; } }
            if (worst < 0) return;
            _freeSlots.Push(_pics[worst].Slot);
            _pics.Remove(worst);
        }

        /// <summary>Frees the slots of rows well away from the screen.</summary>
        void TrimPictures()
        {
            int lo = _shownFirst - KeepAround, hi = _shownLast + KeepAround;
            if (_pics.Count <= hi - lo + 1) return;
            List<int> drop = null;
            foreach (int k in _pics.Keys) if (k < lo || k > hi) { if (drop == null) drop = new List<int>(); drop.Add(k); }
            if (drop == null) return;
            foreach (int k in drop) { _freeSlots.Push(_pics[k].Slot); _pics.Remove(k); }
        }

        /// <summary>One row, with its top-left corner at (left, cy) and the block's width (the number column is left
        /// empty when the bars go there - they are drawn on top, see Paint).</summary>
        void PaintRow(Graphics g, int i, Track t, int left, int cy, int width, bool isPlaying, bool bars, Image cover, PageView view)
        {
            int num, art, title, album, extra, dur;
            Columns(width, out num, out art, out title, out album, out extra, out dur);
            int titleW = (album > 0 ? album : dur) - title - 12;
            {
                Rectangle row = new Rectangle(left + Pad - 10, cy, width - 2 * Pad + 20, _rowH);
                bool selected = i == _selected;             // a clicked row, until something else is clicked (see ClearSelection)
                if (selected || i == _hotRow)
                    using (SolidBrush b = new SolidBrush(selected ? Theme.RowSelected : Theme.RowHover)) g.FillRectangle(b, row);
                Color main = isPlaying ? Theme.Accent : Theme.Text, sub = Theme.SubText;      // only the title turns green
                if (Has(TrackColumns.Number))
                {
                    Rectangle nr = new Rectangle(left + num, cy, 30, _rowH);
                    // as on Spotify: the pointer's row shows play / pause; the playing song bouncing bars (drawn by
                    // Paint, over this), or (paused) its number in green; any other row its number
                    if (i == _hotRow) DrawPlayGlyph(g, nr, isPlaying && view.Host.IsPlaying, isPlaying);
                    else if (bars) { }
                    else Theme.Draw(g, (t.Number > 0 && !Has(TrackColumns.Art) ? t.Number : i + 1).ToString(), Theme.Normal, nr,
                        isPlaying ? Theme.Accent : Theme.SubText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
                if (Has(TrackColumns.Art))
                    Theme.Art(g, cover, new Rectangle(left + art, cy + (_rowH - _artSize) / 2, _artSize, _artSize), false);
                // title (and, on two-line rows, the artist under it)
                Rectangle line1 = TwoLine ? new Rectangle(left + title, cy + 6, titleW, 18) : new Rectangle(left + title, cy, titleW, _rowH);
                Theme.Text1(g, t.Name, Theme.Normal, new Rectangle(line1.Left, line1.Top, line1.Width - (t.Explicit ? 66 : 0), line1.Height), main);
                if (t.Explicit)
                {
                    Rectangle ex = new Rectangle(line1.Left + Math.Min(titleW - 60, Theme.Measure(t.Name, Theme.Normal).Width + 8), line1.Top + (line1.Height - 14) / 2, 54, 14);
                    using (Pen p = new Pen(Theme.DimText)) g.DrawRectangle(p, ex);
                    Theme.Draw(g, "EXPLICIT", Theme.Label, ex, Theme.DimText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
                if (TwoLine) Link(g, t.ArtistName, Theme.Small, new Rectangle(left + title, cy + 24, titleW, 16), sub, i == _hotRow && _hotLink == "artist");
                if (album > 0) Link(g, t.AlbumName, Theme.Normal, new Rectangle(left + album, cy, dur - album - 12, _rowH), sub, i == _hotRow && _hotLink == "album");
                Theme.Text1(g, Theme.Time(t.DurationMs), Theme.Normal, new Rectangle(left + dur, cy, 50, _rowH), sub);
                if (Has(TrackColumns.Popularity)) PopularityBars(g, left + extra, cy + _rowH / 2 - 5, t.Popularity);
                if (Has(TrackColumns.PlayCount))
                    Theme.Draw(g, t.PlayCount.ToString("n0"), Theme.Normal, new Rectangle(left + extra, cy, 110, _rowH), Theme.SubText,
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            }
        }

        // ---- column headers: in the list, and again pinned under the page's top bar once they have scrolled up to it
        public override int PinTop { get { return Tools; } }
        public override int PinHeight { get { return _header ? HeadH : 0; } }

        public override void PaintPinned(Graphics g, Rectangle r, PageView view)
        {
            view.FillWash(g, new Rectangle(0, r.Top, view.ClientSize.Width, r.Height));      // the page's own background: no visible bar
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

        // the playing song's bars (Equalizer), unless the pointer is on its row (then it shows play / pause)
        List<Track> _eqList;                // the playing song's row, found once per list / song
        string _eqId;
        int _eqRow = -1;
        public override Rectangle AnimatedRect(PageView view)
        {
            Track playing = view.Host.PlayingTrack;
            if (playing == null || !Has(TrackColumns.Number)) return Rectangle.Empty;
            if (!ReferenceEquals(_eqList, _tracks) || _eqId != playing.Id)
            {
                _eqList = _tracks; _eqId = playing.Id; _eqRow = -1;
                for (int i = 0; i < _tracks.Count; i++) if (_tracks[i].Id == playing.Id) { _eqRow = i; break; }
            }
            if (_eqRow < 0 || _eqRow == _hotRow) return Rectangle.Empty;
            int num, art, title, album, extra, dur;
            Columns(_width, out num, out art, out title, out album, out extra, out dur);
            return new Rectangle(num, HeadHeight + _eqRow * _rowH, 30, _rowH);
        }

        public override Color AnimatedBack(PageView view) { return _eqRow >= 0 && _eqRow == _selected ? Theme.RowSelected : Color.Empty; }

        // one frame of the bars alone (PageView's quick path): the row's highlight if it is selected, then the bars
        public override void PaintAnimated(Graphics g, Rectangle r, PageView view)
        {
            if (_eqRow < 0 || _eqRow >= _tracks.Count || !ReferenceEquals(_eqList, _tracks)) { Paint(g, r, view); return; }
            Rectangle cell = AnimatedRect(view);
            if (cell.IsEmpty) return;
            cell.Offset(r.Left, r.Top);
            if (_eqRow == _selected)
                using (SolidBrush b = new SolidBrush(Theme.RowSelected)) g.FillRectangle(b, cell);
            if (view.Host.IsPlaying) Equalizer.Draw(g, cell, Theme.Accent);
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