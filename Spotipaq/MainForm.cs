// MainForm.cs - window layout, navigation history, page building and the play queue.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Spotipaq
{
    public sealed class MainForm : Form, IPageHost, IMessageFilter
    {
        readonly ISpotifyBackend _backend;
        readonly IPlayback _player;
        readonly ImageCache _images;
        readonly TitleBar _title;
        readonly TopBar _top = new TopBar();
        readonly Sidebar _sidebar = new Sidebar();
        readonly NowPlayingBar _bar = new NowPlayingBar();
        readonly PageView _page;
        readonly Timer _tick = new Timer();

        // An external URL waits for the corresponding page data before starting playback.
        string _linkTargetKind, _linkTargetId, _linkTrackId;
        int _linkSerial;

        // navigation
        readonly List<NavTarget> _history = new List<NavTarget>();
        int _historyIndex = -1;
        int _pageGeneration;

        // play queue
        List<Track> _queue = new List<Track>();
        List<int> _order = new List<int>();     // play order (shuffled or 0..n-1)
        int _orderPos = -1;
        const int RepeatOff = 0, RepeatAll = 1, RepeatOne = 2;
        bool _shuffle;
        int _repeatMode;
        readonly Random _rng = new Random();

        // saved between runs
        readonly AppSettings _settings = AppSettings.Load();
        readonly Dictionary<string, bool> _libraryIds = new Dictionary<string, bool>();   // playlists already in your library
        List<Playlist> _myPlaylists = new List<Playlist>();
        bool _auto;                              // the current track was started by auto-advance (not by a click)
        int _skipStreak;                         // unplayable tracks skipped in a row
        Track _restored; int _restoredPos;      // last track from the previous run (shown paused in the bar until you press play)
        bool _queueDirty;                        // the queue or its play order changed since queue.txt was last written

        public MainForm(ISpotifyBackend backend, IPlayback player)
        {
            _backend = backend;
            _player = player;
            Text = "Spotipaq";
            FormBorderStyle = FormBorderStyle.None;            // own title bar (TitleBar); edges still resize, see WndProc
            Padding = new Padding(FrameW);                     // the strip the form itself owns: resize hit-testing
            BackColor = Theme.Main;
            ForeColor = Theme.Text;
            Font = Theme.Normal;
            ClientSize = new Size(1000, 680);
            MinimumSize = new Size(800, 560);
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            RestoreWindow();
            MaximizedBounds = Screen.FromRectangle(Bounds).WorkingArea;      // a borderless form would otherwise cover the taskbar
            _title = new TitleBar(this);
            _title.MenuRequested += delegate (object s, MouseEventArgs e) { ShowWindowMenu(new Point(e.X, e.Y)); };
            _shuffle = _settings.GetBool("shuffle", false);
            int savedRepeat = _settings.GetInt("repeatMode", -1);
            _repeatMode = savedRepeat >= 0 ? Math.Max(RepeatOff, Math.Min(RepeatOne, savedRepeat))
                                          : (_settings.GetBool("repeat", false) ? RepeatAll : RepeatOff);
            _player.Volume = Math.Max(0, Math.Min(100, _settings.GetInt("volume", 80)));
            int pos; _restored = _settings.GetTrack(out pos); _restoredPos = pos;
            RestoreQueue();

            _images = new ImageCache(backend, this);
            _page = new PageView(this);

            _sidebar.Width = 218; _sidebar.Dock = DockStyle.Left;
            _top.Dock = DockStyle.Top;
            _bar.Dock = DockStyle.Bottom;
            _page.Dock = DockStyle.Fill;
            _bar.Images = _images;
            Controls.Add(_page); Controls.Add(_sidebar); Controls.Add(_top); Controls.Add(_bar); Controls.Add(_title);   // z-order: fill added first, title bar docks first

            _images.Loaded += delegate { _page.Invalidate(); _bar.Invalidate(); };
            _sidebar.ItemClicked += delegate (object s, NavEventArgs e) { Navigate(e.Target); };
            _sidebar.ItemContext += delegate (object s, NavEventArgs e) { ShowNavMenu(e.Target); };
            _sidebar.LibraryCollapsed = _settings.GetBool("sidebar.libraryCollapsed", false);
            _sidebar.LibraryToggled += delegate { _settings.Set("sidebar.libraryCollapsed", _sidebar.LibraryCollapsed); _settings.Save(); };
            _sidebar.PlaylistsReordered += delegate (object s, OrderEventArgs e) { _settings.Set("playlistOrder", String.Join(",", e.Ids.ToArray())); _settings.Save(); };
            _top.Back += delegate { Go(_historyIndex - 1); };
            _top.Forward += delegate { Go(_historyIndex + 1); };
            BuildUserMenu();
            _top.SearchRequested += delegate (object s, SearchEventArgs e) { Navigate(new NavTarget(PageKind.Search, e.Query)); };
            _bar.PlayPause += delegate { TogglePlayPause(); };
            _bar.Next += delegate { Skip(1, true); };
            _bar.Previous += delegate { Previous(); };
            _bar.ShuffleToggled += delegate { SetShuffle(!_shuffle); };
            _bar.RepeatToggled += delegate { _repeatMode = (_repeatMode + 1) % 3; RefreshBar(); SaveSettings(false); };
            _bar.SeekRequested += delegate (object s, SeekEventArgs e) { if (_player.Current != null) _player.Seek(e.Ms); RefreshBar(); };
            _bar.LinkClicked += delegate (object s, NavEventArgs e) { Navigate(e.Target); };
            _bar.TrackContext += delegate { ShowNowPlayingMenu(); };
            _bar.LinkContext += delegate (object s, NavEventArgs e) { ShowNavMenu(e.Target); };
            _bar.VolumeChanged += delegate { _player.Volume = _bar.Volume; _volumeDirty = true; };
            _player.StateChanged += delegate { SafeUi(RefreshAll); };
            _player.TrackEnded += delegate
            {
                SafeUi(delegate
                {
                    _skipStreak = 0;
                    if (_repeatMode == RepeatOne && _player.Current != null)
                    {
                        _auto = true;
                        _player.Play(_player.Current, 0);
                        RefreshAll();
                    }
                    else Skip(1, false);
                });
            };
            _player.TrackUnplayable += delegate
            {
                SafeUi(delegate
                {
                    // Never leave an active playlist stranded after a terminal load/stream failure.
                    if (_queue.Count == 0 || ++_skipStreak > Math.Min(25, Math.Max(1, _queue.Count))) { _skipStreak = 0; return; }
                    Engine.Log.Write("Skipping failed track");
                    Skip(1, false);
                });
            };

            _tick.Interval = 500;                              // the only periodic UI work: the position text
            _tick.Tick += delegate
            {
                if (_player.State == PlayState.Playing) RefreshBar();
                if (_volumeDirty && (MouseButtons & MouseButtons.Left) == 0) { _volumeDirty = false; SaveSettings(false); }
                if (_mediaKeysMissing && ++_mediaKeyRetry >= 10) { _mediaKeyRetry = 0; RegisterMediaKeys(); }     // another player (Winamp...) may let go of them later
            };
            _tick.Start();
            Application.AddMessageFilter(this);
        }

        // XP sends the mouse wheel to the focused control; route it to whatever is under the cursor instead
        // (so the track list scrolls while you're typing in the search box, like the real client).
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point p);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        public bool PreFilterMessage(ref Message m)
        {
            // The owner-drawn bars never take the keyboard focus, so a click on them would leave it in the search box:
            // hand it to the page instead (clicks on the search box's own outline / lens keep it).
            if ((m.Msg == 0x0201 || m.Msg == 0x0204) && _top.SearchBox.Focused &&            // WM_LBUTTONDOWN / WM_RBUTTONDOWN
                (m.HWnd == _title.Handle || m.HWnd == _sidebar.Handle || m.HWnd == _bar.Handle ||
                 (m.HWnd == _top.Handle && !_top.SearchFrame.Contains(_top.PointToClient(Cursor.Position)))))
                _page.Focus();
            if (m.Msg != 0x020A) return false;                 // WM_MOUSEWHEEL
            IntPtr under = WindowFromPoint(Cursor.Position);
            if (under == IntPtr.Zero || under == m.HWnd) return false;
            if (under != _page.Handle && under != _sidebar.Handle) return false;
            SendMessage(under, m.Msg, m.WParam, m.LParam);
            return true;
        }

        bool _volumeDirty;

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); AppIcon.Apply(this); RegisterMediaKeys(); }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            UnregisterMediaKeys();
            base.OnHandleDestroyed(e);
        }

        // ---- global media keys (play/pause, next, previous, stop), work whichever program has focus.
        // A key can only belong to one program at a time: if another player already owns it the registration fails
        // quietly and is retried every few seconds, so Spotipaq picks the key up once the other player lets go.
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        const int WM_HOTKEY = 0x0312;
        static readonly uint[] MediaVk = { 0xB3, 0xB0, 0xB1, 0xB2 };        // VK_MEDIA_PLAY_PAUSE, NEXT_TRACK, PREV_TRACK, STOP
        const int HotkeyBase = 0x5B10;                                       // our own hotkey ids
        readonly bool[] _mediaKeyOk = new bool[4];
        bool _mediaKeysMissing = true;
        int _mediaKeyRetry;

        void RegisterMediaKeys()
        {
            if (!IsHandleCreated) return;
            bool missing = false;
            for (int i = 0; i < MediaVk.Length; i++)
            {
                if (_mediaKeyOk[i]) continue;
                try { _mediaKeyOk[i] = RegisterHotKey(Handle, HotkeyBase + i, 0, MediaVk[i]); }
                catch (Exception) { _mediaKeyOk[i] = false; }
                if (!_mediaKeyOk[i]) missing = true;
            }
            _mediaKeysMissing = missing;
        }

        void UnregisterMediaKeys()
        {
            if (!IsHandleCreated) return;
            for (int i = 0; i < MediaVk.Length; i++)
                if (_mediaKeyOk[i]) { try { UnregisterHotKey(Handle, HotkeyBase + i); } catch (Exception) { } _mediaKeyOk[i] = false; }
            _mediaKeysMissing = true;
        }

        void OnMediaKey(int which)
        {
            switch (which)
            {
                case 0: TogglePlayPause(); break;
                case 1: Skip(1, true); break;
                case 2: Previous(); break;
                case 3: _player.Stop(); RefreshAll(); break;
            }
        }

        // ---- borderless window: styles, edge resizing, frame, and the window (taskbar) menu
        const int FrameW = 3;
        const int WM_NCHITTEST = 0x0084, WM_SYSMENU = 0x0313, WM_SYSCOMMAND = 0x0112;
        const int HTCLIENT = 1, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
        const int SC_SIZE = 0xF000, SC_MOVE = 0xF010, SC_MINIMIZE = 0xF020, SC_MAXIMIZE = 0xF030, SC_CLOSE = 0xF060, SC_RESTORE = 0xF120;
        const int CmdPrevious = 1, CmdPlayPause = 2, CmdNext = 3;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        struct MENUITEMINFO
        {
            public int cbSize, fMask, fType, fState, wID;
            public IntPtr hSubMenu, hbmpChecked, hbmpUnchecked, dwItemData;
            public string dwTypeData;
            public int cch;
            public IntPtr hbmpItem;
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr menu);
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern bool InsertMenuItemW(IntPtr menu, int item, bool byPosition, ref MENUITEMINFO info);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetMenuDefaultItem(IntPtr menu, int item, int byPosition);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int TrackPopupMenu(IntPtr menu, int flags, int x, int y, int reserved, IntPtr hWnd, IntPtr rect);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);

        // glyph: one of Windows' own system-menu pictures (HBMMENU_POPUP_CLOSE 8, RESTORE 9, MAXIMIZE 10, MINIMIZE 11), or 0
        static void AddMenuItem(IntPtr menu, int id, string text, bool enabled, int glyph)
        {
            MENUITEMINFO mi = new MENUITEMINFO();
            mi.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(MENUITEMINFO));
            mi.fMask = 0x2 | 0x40 | 0x1;                      // MIIM_ID | MIIM_STRING | MIIM_STATE
            mi.wID = id;
            mi.dwTypeData = text;
            mi.fState = enabled ? 0 : 0x3;                    // MFS_GRAYED
            if (glyph != 0) { mi.fMask |= 0x80; mi.hbmpItem = (IntPtr)glyph; }      // MIIM_BITMAP
            InsertMenuItemW(menu, -1, true, ref mi);
        }
        static void AddMenuSeparator(IntPtr menu)
        {
            MENUITEMINFO mi = new MENUITEMINFO();
            mi.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(MENUITEMINFO));
            mi.fMask = 0x100;                                 // MIIM_FTYPE
            mi.fType = 0x800;                                 // MFT_SEPARATOR
            InsertMenuItemW(menu, -1, true, ref mi);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.Style |= 0x00020000;          // WS_MINIMIZEBOX: lets a taskbar click minimise a borderless window
                cp.ExStyle |= 0x00040000;        // WS_EX_APPWINDOW
                return cp;
            }
        }

        // the frame strip around the content (the children cover everything else)
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using (SolidBrush b = new SolidBrush(TitleBar.BarColor)) e.Graphics.FillRectangle(b, ClientRectangle);
        }

        // The window menu: on a right-click of the taskbar button (WM_SYSMENU) or of the title bar. A borderless form
        // has no system menu for Windows to show, and giving it one (WS_SYSMENU) would break the edge resizing, so the
        // request is answered with a native menu of our own that mirrors the standard one - same entries, same order,
        // Windows' own glyphs - with the playback commands on top.
        void ShowWindowMenu(Point screen)
        {
            bool min = WindowState == FormWindowState.Minimized;
            bool max = WindowState == FormWindowState.Maximized;
            bool normal = !min && !max;
            bool anything = _player.Current != null || _restored != null || _queue.Count > 0;
            IntPtr menu = CreatePopupMenu();
            if (menu == IntPtr.Zero) return;
            try
            {
                AddMenuItem(menu, CmdPrevious, "Pre&vious", anything, 0);
                AddMenuItem(menu, CmdPlayPause, IsPlaying ? "&Pause" : "&Play", anything, 0);
                AddMenuItem(menu, CmdNext, "Ne&xt", anything, 0);
                AddMenuSeparator(menu);
                AddMenuItem(menu, SC_RESTORE, "&Restore", min || max, 9);
                AddMenuItem(menu, SC_MOVE, "&Move", normal, 0);
                AddMenuItem(menu, SC_SIZE, "&Size", normal, 0);
                AddMenuItem(menu, SC_MINIMIZE, "Mi&nimize", !min, 11);
                AddMenuItem(menu, SC_MAXIMIZE, "Ma&ximize", !max, 10);
                AddMenuSeparator(menu);
                AddMenuItem(menu, SC_CLOSE, "&Close\tAlt+F4", true, 8);
                SetMenuDefaultItem(menu, min ? SC_RESTORE : SC_CLOSE, 0);       // Windows shows the default entry in bold
                SetForegroundWindow(Handle);                                    // else the menu won't close on a click elsewhere
                int cmd = TrackPopupMenu(menu, 0x2 | 0x100, screen.X, screen.Y, 0, Handle, IntPtr.Zero);     // TPM_RIGHTBUTTON | TPM_RETURNCMD
                switch (cmd)
                {
                    case CmdPrevious: Previous(); break;
                    case CmdPlayPause: TogglePlayPause(); break;
                    case CmdNext: Skip(1, true); break;
                    case SC_RESTORE:
                        if (WindowState == FormWindowState.Minimized) ShowWindow(Handle, 9);     // SW_RESTORE
                        else if (WindowState == FormWindowState.Maximized) WindowState = FormWindowState.Normal;
                        break;
                    case SC_MOVE:
                    case SC_SIZE:
                        SendMessage(Handle, WM_SYSCOMMAND, (IntPtr)cmd, IntPtr.Zero);            // the standard keyboard move / size mode
                        break;
                    case SC_MINIMIZE: WindowState = FormWindowState.Minimized; break;
                    case SC_MAXIMIZE:
                        if (WindowState == FormWindowState.Minimized) ShowWindow(Handle, 9);
                        if (WindowState != FormWindowState.Maximized) WindowState = FormWindowState.Maximized;
                        break;
                    case SC_CLOSE: Close(); break;
                }
            }
            finally { DestroyMenu(menu); }
        }

        protected override void WndProc(ref Message m)
        {
            // WinForms re-sends its own (shrunken) small icon now and then; always answer with the 16x16 frame AppIcon picked
            if (m.Msg == 0x0080 && m.WParam == IntPtr.Zero && AppIcon.Small != IntPtr.Zero) m.LParam = AppIcon.Small;
            if (m.Msg == WM_HOTKEY)
            {
                int which = m.WParam.ToInt32() - HotkeyBase;
                if (which >= 0 && which < MediaVk.Length) { OnMediaKey(which); return; }
            }
            if (m.Msg == WM_SYSMENU)                 // the taskbar button was right-clicked
            {
                int pos = unchecked((int)(long)m.LParam);
                ShowWindowMenu(new Point((short)(pos & 0xFFFF), (short)((pos >> 16) & 0xFFFF)));
                return;
            }
            base.WndProc(ref m);
            if (m.Msg == WM_NCHITTEST && (int)m.Result == HTCLIENT && WindowState == FormWindowState.Normal)
            {
                int lp = unchecked((int)(long)m.LParam);
                Point pt = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                const int b = 6;
                bool l = pt.X < b, r = pt.X >= ClientSize.Width - b;
                bool t = pt.Y < b, bt = pt.Y >= ClientSize.Height - b;
                int code = HTCLIENT;
                if (t && l) code = HTTOPLEFT;
                else if (t && r) code = HTTOPRIGHT;
                else if (bt && l) code = HTBOTTOMLEFT;
                else if (bt && r) code = HTBOTTOMRIGHT;
                else if (l) code = HTLEFT;
                else if (r) code = HTRIGHT;
                else if (t) code = HTTOP;
                else if (bt) code = HTBOTTOM;
                if (code != HTCLIENT) m.Result = (IntPtr)code;
            }
        }

        void RestoreWindow()
        {
            int w = _settings.GetInt("win.w", 0), h = _settings.GetInt("win.h", 0), x = _settings.GetInt("win.x", 0), y = _settings.GetInt("win.y", 0);
            if (w < MinimumSize.Width || h < MinimumSize.Height) return;
            Rectangle r = new Rectangle(x, y, w, h);
            bool visible = false;
            foreach (Screen sc in Screen.AllScreens)
            {
                Rectangle inter = Rectangle.Intersect(sc.WorkingArea, r);
                if (inter.Width >= 100 && inter.Height >= 60) visible = true;
            }
            if (!visible) return;
            StartPosition = FormStartPosition.Manual;
            Bounds = r;
        }

        /// <summary>Writes everything worth remembering. Called on close and when a setting changes.</summary>
        void SaveSettings(bool full)
        {
            _settings.Set("volume", _bar.Volume);
            _settings.Set("shuffle", _shuffle);
            _settings.Set("repeat", _repeatMode != RepeatOff);
            _settings.Set("repeatMode", _repeatMode);
            if (full)
            {
                TrackWindow();
                Rectangle b = _lastNormal;
                if (b.Width > 0) { _settings.Set("win.x", b.X); _settings.Set("win.y", b.Y); _settings.Set("win.w", b.Width); _settings.Set("win.h", b.Height); }
                _settings.Set("win.max", _lastMax);
                NavTarget t = Current;
                if (t != null) { _settings.Set("page.kind", t.Kind.ToString()); _settings.Set("page.id", t.Id); }
                Track tr = _player.Current ?? _restored;
                if (tr != null) _settings.SetTrack(tr, _player.Current != null ? _player.PositionMs : _restoredPos);
                _settings.Set("queue.pos", _orderPos);
                if (_queueDirty && _queue.Count > 0) { AppSettings.SaveQueue(_queue, _order); _queueDirty = false; }
            }
            _settings.Save();
        }

        // Last known non-minimized window state. Closing while minimized (or during logoff) would otherwise
        // report Minimized and lose the maximized flag.
        Rectangle _lastNormal; bool _lastMax;

        void TrackWindow()
        {
            if (!IsHandleCreated) return;
            if (WindowState == FormWindowState.Minimized) return;
            _lastMax = WindowState == FormWindowState.Maximized;
            if (WindowState == FormWindowState.Normal) MaximizedBounds = Screen.FromControl(this).WorkingArea;   // where "maximize" goes from here
            Rectangle b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            if (b.Width >= MinimumSize.Width && b.Height >= MinimumSize.Height) _lastNormal = b;
        }

        protected override void OnResize(EventArgs e)
        {
            // no resize frame while maximized: the content goes right to the screen edges
            Padding want = WindowState == FormWindowState.Maximized ? Padding.Empty : new Padding(FrameW);
            if (Padding != want) Padding = want;
            base.OnResize(e);
            if (_title != null) _title.Invalidate();           // maximize <-> restore glyph
            TrackWindow();
        }

        protected override void OnMove(EventArgs e)
        {
            base.OnMove(e);
            TrackWindow();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveSettings(true);
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Application.RemoveMessageFilter(this);
            _tick.Stop();
            _player.Stop();
            base.OnFormClosed(e);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            if (_settings.GetBool("win.max", false)) WindowState = FormWindowState.Maximized;
            TrackWindow();
            _sidebar.SetPlaylists(null);
            _top.SetUser("Connecting...");
            _bar.Volume = _player.Volume;
            RefreshBar();
            Async.Run(this, delegate { return _backend.GetUser(); }, delegate (UserInfo u, Exception err)
            {
                if (u != null) _top.SetUser(u.Name);
                else
                {
                    _top.SetUser("Offline");
                    if (err != null)
                        MessageBox.Show(this, "Couldn't connect to Spotify:\r\n\r\n" + err.Message + "\r\n\r\nDetails: " + Engine.Log.FilePath,
                            "Spotipaq", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            });
            Async.Run(this, delegate { return _backend.GetPlaylists(); }, delegate (List<Playlist> p, Exception err)
            {
                if (p != null) { _myPlaylists = p; _libraryIds.Clear(); foreach (Playlist x in p) _libraryIds[x.Id] = true; }
                _sidebar.SetPlaylists(Ordered(p)); _sidebar.SetCurrent(Current);
            });
            NavTarget start = new NavTarget(PageKind.Browse, null);
            try
            {
                string kind = _settings.GetString("page.kind", null);
                if (kind != null) start = new NavTarget((PageKind)Enum.Parse(typeof(PageKind), kind), _settings.GetString("page.id", null));
            }
            catch { }
            Navigate(start);
        }

        /// <summary>Applies the playlist order you set by dragging. Playlists it hasn't seen yet go on top, in Spotify's order.</summary>
        List<Playlist> Ordered(List<Playlist> list)
        {
            string saved = _settings.GetString("playlistOrder", "");
            if (list == null || saved.Length == 0) return list;
            string[] ids = saved.Split(',');
            List<Playlist> result = new List<Playlist>(), rest = new List<Playlist>(list);
            List<Playlist> fresh = new List<Playlist>();
            Dictionary<string, int> rank = new Dictionary<string, int>();
            for (int i = 0; i < ids.Length; i++) rank[ids[i]] = i;
            foreach (Playlist p in list) if (!rank.ContainsKey(p.Id)) fresh.Add(p);
            List<Playlist> known = new List<Playlist>();
            foreach (Playlist p in list) if (rank.ContainsKey(p.Id)) known.Add(p);
            known.Sort(delegate (Playlist a, Playlist b) { return rank[a.Id].CompareTo(rank[b.Id]); });
            result.AddRange(fresh); result.AddRange(known);
            return result;
        }

        /// <summary>Set when the user chose "Log out"; Program forgets the login and starts over.</summary>
        public bool LogOutRequested;

        void BuildUserMenu()
        {
            ContextMenuStrip m = Menus.New();
            m.Items.Add("Open log file", null, delegate
            {
                Engine.Log.Flush();
                try { System.Diagnostics.Process.Start("notepad.exe", "\"" + Engine.Log.FilePath + "\""); } catch { }
            });
            m.Items.Add("Open Spotify link from clipboard", null, delegate
            {
                try
                {
                    if (Clipboard.ContainsText()) OpenSpotifyLink(Clipboard.GetText());
                    else MessageBox.Show(this, "Copy a Spotify link first.", "Spotipaq",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "Spotipaq"); }
            });
            m.Items.Add("Open Spotipaq folder", null, delegate
            {
                try { System.Diagnostics.Process.Start("explorer.exe", "\"" + Engine.Log.Folder + "\""); } catch { }
            });
            m.Items.Add("Send login to another device...", null, delegate
            {
                Engine.LoginCredentials c = Engine.CredentialStore.Load();
                if (c == null) { MessageBox.Show(this, "You're not logged in on this computer.", "Spotipaq", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                using (SendLoginForm f = new SendLoginForm(c)) f.ShowDialog(this);
            });
            m.Items.Add("Reset playlist order", null, delegate
            {
                _settings.Set("playlistOrder", null); _settings.Save();
                _sidebar.SetPlaylists(_myPlaylists); _sidebar.SetCurrent(Current);
            });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Log out", null, delegate
            {
                if (MessageBox.Show(this, "Log out of Spotify on this computer?", "Spotipaq", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                LogOutRequested = true;
                Close();
            });
            _top.UserMenu = m;
        }

        /// <summary>Brings back the previous run's play queue, so play / next / previous carry on through the same
        /// playlist. Kept only if it still contains the restored track; otherwise that track plays on its own.</summary>
        void RestoreQueue()
        {
            if (_restored == null) return;
            List<Track> queue; List<int> order;
            if (!AppSettings.LoadQueue(out queue, out order)) return;
            int at = _settings.GetInt("queue.pos", -1);
            if (at < 0 || at >= order.Count || queue[order[at]].Id != _restored.Id)
            {
                at = -1;
                for (int i = 0; i < order.Count; i++)
                    if (queue[order[i]].Id == _restored.Id) { at = i; break; }
                if (at < 0) return;
            }
            _queue = queue; _order = order; _orderPos = at;
        }

        void SafeUi(MethodInvoker m)
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (InvokeRequired) { try { BeginInvoke(m); } catch (InvalidOperationException) { } }
            else m();
        }

        /// <summary>Called on the UI thread when the application receives a Spotify link.</summary>
        public void OpenSpotifyLink(string link)
        {
            string kind, id;
            if (!TrySpotifyLink(link, out kind, out id))
            {
                MessageBox.Show(this, "Not a supported Spotify track, album, artist or playlist link.",
                    "Spotipaq", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!(_backend is Engine.SpotifyBackend))
            {
                MessageBox.Show(this, "Sign in to Spotify to open links.", "Spotipaq",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Show(); Activate();
            _linkSerial++;
            _linkTargetKind = kind; _linkTargetId = id; _linkTrackId = null;
            if (kind == "track")
            {
                int serial = _linkSerial;
                Async.Run(this, delegate { return ((Engine.SpotifyBackend)_backend).GetLinkedTrack(id); },
                    delegate (Track track, Exception err)
                    {
                        if (serial != _linkSerial) return;
                        if (track == null) { LinkedError(err); return; }
                        if (String.IsNullOrEmpty(track.AlbumId))
                        {
                            List<Track> one = new List<Track>(); one.Add(track);
                            _linkTargetKind = null;
                            PlayList(one, 0);
                            return;
                        }
                        _linkTargetKind = "album";
                        _linkTargetId = track.AlbumId;
                        _linkTrackId = id;
                        NavigateLinked(new NavTarget(PageKind.Album, track.AlbumId));
                    });
            }
            else
                NavigateLinked(new NavTarget(kind == "album" ? PageKind.Album :
                    kind == "artist" ? PageKind.Artist : PageKind.Playlist, id));
        }

        void NavigateLinked(NavTarget target)
        {
            if (target.SameAs(Current)) ShowPage(target);
            else Navigate(target);
        }

        void LinkedError(Exception error)
        {
            _linkTargetKind = null;
            MessageBox.Show(this, error == null ? "Could not open Spotify link." : error.Message,
                "Spotipaq", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        void PlayLinkedPage(string kind, string id, List<Track> tracks)
        {
            if (_linkTargetKind != kind || _linkTargetId != id) return;
            string trackId = _linkTrackId;
            _linkTargetKind = null; _linkTrackId = null;
            if (tracks == null || tracks.Count == 0) return;
            int index = 0;
            if (trackId != null)
                for (int i = 0; i < tracks.Count; i++)
                    if (tracks[i].Id == trackId) { index = i; break; }
            PlayList(tracks, index);
        }

        static bool TrySpotifyLink(string raw, out string kind, out string id)
        {
            kind = id = null;
            if (String.IsNullOrEmpty(raw)) return false;
            string[] parts = null;
            raw = raw.Trim().Trim('"');
            if (raw.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase))
                parts = raw.Substring(8).Split(':');
            else
            {
                Uri uri;
                if (!Uri.TryCreate(raw, UriKind.Absolute, out uri) ||
                    (uri.Scheme != "https" && uri.Scheme != "http") ||
                    !(uri.Host.Equals("open.spotify.com", StringComparison.OrdinalIgnoreCase) ||
                      uri.Host.Equals("play.spotify.com", StringComparison.OrdinalIgnoreCase))) return false;
                string path = uri.AbsolutePath.Trim('/');
                if (path.StartsWith("intl-", StringComparison.OrdinalIgnoreCase))
                    path = path.Substring(path.IndexOf('/') + 1);
                parts = path.Split('/');
            }
            if (parts.Length < 2) return false;
            kind = parts[0].ToLowerInvariant(); id = parts[1];
            if (kind != "track" && kind != "album" && kind != "artist" && kind != "playlist") return false;
            if (id.Length != 22) return false;
            foreach (char ch in id)
                if (!((ch >= '0' && ch <= '9') || (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z'))) return false;
            return true;
        }

        // ============================================================================ IPageHost
        public ImageCache Images { get { return _images; } }
        public Track PlayingTrack { get { return _player.Current; } }
        public bool IsPlaying { get { return _player.State == PlayState.Playing || _player.State == PlayState.Loading; } }

        NavTarget Current { get { return _historyIndex >= 0 ? _history[_historyIndex] : null; } }

        public void Navigate(NavTarget target)
        {
            if (target == null || target.SameAs(Current)) return;
            if (_linkTargetKind != null &&
                !(target.Id == _linkTargetId && target.Kind.ToString().Equals(_linkTargetKind, StringComparison.OrdinalIgnoreCase)))
                _linkTargetKind = null;
            _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);   // drop "forward"
            _history.Add(target);
            if (_history.Count > 50) _history.RemoveAt(0);
            Go(_history.Count - 1);
        }

        void Go(int index)
        {
            if (index < 0 || index >= _history.Count) return;
            _historyIndex = index;
            _top.CanBack = index > 0;
            _top.CanForward = index < _history.Count - 1;
            _top.Invalidate();
            _sidebar.SetCurrent(Current);
            ShowPage(Current);
        }

        public void PlayList(List<Track> tracks, int index)
        {
            if (tracks == null || index < 0 || index >= tracks.Count) return;
            _restored = null;
            _auto = false; _skipStreak = 0;
            _queue = new List<Track>(tracks);
            BuildOrder(index);
            _queueDirty = true;
            PlayAt(_orderPos);
        }

        public void TogglePlayPause()
        {
            if (_player.State == PlayState.Playing) _player.Pause();
            else if (_player.State == PlayState.Paused) _player.Resume();
            else if (_restored != null)
            {
                Track t = _restored; int at = _restoredPos;
                _restored = null;
                if (_queue.Count > 0 && _orderPos >= 0 && _orderPos < _order.Count && _queue[_order[_orderPos]].Id == t.Id)
                    t = _queue[_order[_orderPos]];          // the queue from last time came back: carry on inside it
                else
                {
                    _queue = new List<Track>(); _queue.Add(t);
                    BuildOrder(0);
                    _orderPos = 0;
                    _queueDirty = true;
                }
                _player.Play(t, at);
            }
            else if (_queue.Count > 0) PlayAt(Math.Max(0, _orderPos));
            RefreshAll();
        }


        // ============================================================================ context menus
        static ContextMenuStrip NewMenu()
        {
            return Menus.New();
        }

        static void CopyText(string s) { try { Clipboard.SetText(s); } catch { } }

        static string WebLink(NavTarget t)
        {
            switch (t.Kind)
            {
                case PageKind.Album: return "https://open.spotify.com/album/" + t.Id;
                case PageKind.Artist: return "https://open.spotify.com/artist/" + t.Id;
                case PageKind.Playlist: return "https://open.spotify.com/playlist/" + t.Id;
            }
            return null;
        }

        public void ShowTrackMenu(List<Track> list, int index)
        {
            if (list == null || index < 0 || index >= list.Count) return;
            NavTarget curPage = Current;
            ShowTrackMenu(list[index], delegate { PlayList(list, index); }, curPage != null && curPage.Kind == PageKind.Songs);
        }

        /// <summary>Right-click on the song title in the now-playing bar: same menu as a song row.
        /// "Play" restarts the current song without touching the queue.</summary>
        void ShowNowPlayingMenu()
        {
            Track t = _bar.Track;
            if (t == null || String.IsNullOrEmpty(t.Id)) return;
            NavTarget curPage = Current;
            ShowTrackMenu(t, delegate
            {
                if (_player.Current != null)
                {
                    _player.Seek(0);
                    if (_player.State == PlayState.Paused) _player.Resume();
                }
                else if (_restored != null)
                {
                    _restoredPos = 0;
                    TogglePlayPause();
                }
                RefreshBar();
            }, curPage != null && curPage.Kind == PageKind.Songs);
        }

        void ShowTrackMenu(Track t, EventHandler play, bool liked)
        {
            ContextMenuStrip m = NewMenu();
            m.Items.Add("Play", null, play);
            m.Items.Add("Play next", null, delegate { QueueTrack(t, true); });
            m.Items.Add("Add to queue", null, delegate { QueueTrack(t, false); });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Go to song radio", null, delegate { GoToRadio(t); });
            if (!String.IsNullOrEmpty(t.ArtistId)) { string id = t.ArtistId; m.Items.Add("Go to artist", null, delegate { Navigate(new NavTarget(PageKind.Artist, id)); }); }
            if (!String.IsNullOrEmpty(t.AlbumId)) { string id = t.AlbumId; m.Items.Add("Go to album", null, delegate { Navigate(new NavTarget(PageKind.Album, id)); }); }
            m.Items.Add(new ToolStripSeparator());
            if (liked)
                m.Items.Add("Remove from Liked Songs", null, delegate { SetSavedAsync("spotify:track:" + t.Id, false, null); });
            else
                m.Items.Add("Save to Liked Songs", null, delegate { SetSavedAsync("spotify:track:" + t.Id, true, null); });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Copy song link", null, delegate { CopyText("https://open.spotify.com/track/" + t.Id); });
            m.Items.Add("Copy Spotify URI", null, delegate { CopyText("spotify:track:" + t.Id); });
            m.Show(Cursor.Position);
        }

        public void ShowNavMenu(NavTarget target)
        {
            if (target == null) return;
            string link = WebLink(target);
            if (link == null) return;
            string uri = null, kind = null; bool inLibrary = false; string name = null;
            NavTarget cur = Current;
            switch (target.Kind)
            {
                case PageKind.Playlist:
                    uri = "spotify:playlist:" + target.Id; kind = "playlist"; inLibrary = _libraryIds.ContainsKey(target.Id);
                    foreach (Playlist q in _myPlaylists) if (q.Id == target.Id) name = q.Name;
                    break;
                case PageKind.Album: uri = "spotify:album:" + target.Id; kind = "album"; inLibrary = cur != null && cur.Kind == PageKind.Albums; break;
                case PageKind.Artist: uri = "spotify:artist:" + target.Id; kind = "artist"; inLibrary = cur != null && cur.Kind == PageKind.Artists; break;
            }
            ContextMenuStrip m = NewMenu();
            m.Items.Add("Open", null, delegate { Navigate(target); });
            if (uri != null)
            {
                if (inLibrary)
                    m.Items.Add(kind == "artist" ? "Remove from library (unfollow)" : "Remove from library", null, delegate
                    {
                        if (kind == "playlist" && MessageBox.Show(this, "Remove \"" + (name ?? "this playlist") + "\" from your library?", "Spotipaq", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                        SetSavedAsync(uri, false, name);
                    });
                else
                    m.Items.Add(kind == "artist" ? "Follow" : "Save to library", null, delegate { SetSavedAsync(uri, true, name); });
            }
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Copy link", null, delegate { CopyText(link); });
            m.Show(Cursor.Position);
        }

        /// <summary>SAVE on a playlist that isn't in your library yet (e.g. a song radio).</summary>
        void SavePlaylistToLibrary(Playlist p) { SetSavedAsync("spotify:playlist:" + p.Id, true, p.Name); }

        /// <summary>Saves / removes an item in your Spotify library, then refreshes whatever is showing.</summary>
        void SetSavedAsync(string uri, bool saved, string name)
        {
            string id = uri.Substring(uri.LastIndexOf(':') + 1);
            bool playlist = uri.IndexOf(":playlist:", StringComparison.Ordinal) > 0;
            Async.Run(this, delegate { _backend.SetSaved(uri, saved); return "ok"; }, delegate (string ok, Exception err)
            {
                if (ok == null)
                {
                    MessageBox.Show(this, err == null ? "Something went wrong." : err.Message, "Spotipaq", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (playlist)
                {
                    if (saved)
                    {
                        _libraryIds[id] = true;
                        Playlist entry = new Playlist(); entry.Id = id; entry.Name = name ?? "Playlist"; entry.Owner = "";
                        _myPlaylists.Insert(0, entry);
                    }
                    else
                    {
                        _libraryIds.Remove(id);
                        _myPlaylists.RemoveAll(delegate (Playlist q) { return q.Id == id; });
                    }
                    _sidebar.SetPlaylists(Ordered(_myPlaylists));
                    _sidebar.SetCurrent(Current);
                }
                NavTarget cur = Current;
                bool refresh = cur != null && ((playlist && cur.Kind == PageKind.Playlist && cur.Id == id) ||
                    (uri.IndexOf(":track:", StringComparison.Ordinal) > 0 && cur.Kind == PageKind.Songs) ||
                    (uri.IndexOf(":album:", StringComparison.Ordinal) > 0 && cur.Kind == PageKind.Albums) ||
                    (uri.IndexOf(":artist:", StringComparison.Ordinal) > 0 && cur.Kind == PageKind.Artists));
                if (refresh) ShowPage(cur);
            });
        }

        void GoToRadio(Track t)
        {
            Async.Run(this, delegate { return _backend.GetSongRadio(t.Id); }, delegate (string id, Exception err)
            {
                if (id != null) Navigate(new NavTarget(PageKind.Playlist, id));
                else MessageBox.Show(this, err != null ? err.Message : "Song radio isn't available for this song.", "Spotipaq", MessageBoxButtons.OK, MessageBoxIcon.Information);
            });
        }

        /// <summary>Puts a song straight after the current one (next = true) or at the end of the queue.</summary>
        void QueueTrack(Track t, bool next)
        {
            if (_queue.Count == 0 || _player.Current == null)
            {
                List<Track> one = new List<Track>(); one.Add(t);
                PlayList(one, 0);
                return;
            }
            _queue.Add(t);
            int qi = _queue.Count - 1;
            if (next) _order.Insert(Math.Min(_order.Count, _orderPos + 1), qi); else _order.Add(qi);
            _queueDirty = true;
        }

        // ============================================================================ queue
        void BuildOrder(int first)
        {
            _order = new List<int>();
            if (!_shuffle)
            {
                for (int i = 0; i < _queue.Count; i++) _order.Add(i);
                _orderPos = first;
                return;
            }
            for (int i = 0; i < _queue.Count; i++) if (i != first) _order.Add(i);
            for (int i = _order.Count - 1; i > 0; i--) { int j = _rng.Next(i + 1); int t = _order[i]; _order[i] = _order[j]; _order[j] = t; }
            _order.Insert(0, first);                      // the chosen track plays first, then the shuffle
            _orderPos = 0;
        }

        void SetShuffle(bool on)
        {
            _shuffle = on;
            SaveSettings(false);
            if (_queue.Count > 0 && _orderPos >= 0)
            {
                int current = _order[_orderPos];
                BuildOrder(current);
                _queueDirty = true;
            }
            RefreshBar();
        }

        void PlayAt(int orderPos)
        {
            if (orderPos < 0 || orderPos >= _order.Count) return;
            _orderPos = orderPos;
            _restored = null;
            _player.Play(_queue[_order[orderPos]], 0);
            RefreshAll();
        }

        void Skip(int delta, bool user)
        {
            if (_order.Count == 0) return;
            int next = _orderPos + delta;
            if (next >= _order.Count) { if (_repeatMode == RepeatAll || user) next = 0; else { _player.Stop(); RefreshAll(); return; } }
            if (next < 0) next = _order.Count - 1;
            _auto = !user;
            if (user) _skipStreak = 0;
            PlayAt(next);
        }

        void Previous()
        {
            if (_player.Current != null && _player.PositionMs > 3000) { _player.Seek(0); RefreshBar(); }
            else Skip(-1, true);
        }

        void RefreshAll() { RefreshBar(); _page.Invalidate(); }

        void RefreshBar()
        {
            Track t = _player.Current ?? _restored;
            _bar.Track = t;
            _bar.Playing = IsPlaying;
            _bar.PositionMs = _player.Current != null ? _player.PositionMs : _restoredPos;
            _bar.Shuffle = _shuffle;
            _bar.RepeatMode = _repeatMode;
            if (!_bar.DraggingVolume) _bar.Volume = _player.Volume;
            _bar.Invalidate();
            string title = t == null ? "Spotipaq" : t.ArtistName + " - " + t.Name;
            if (Text != title) Text = title;
        }

        // ============================================================================ keyboard
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            bool typing = _top.SearchBox.Focused;
            if (keyData == Keys.Space && !typing) { TogglePlayPause(); return true; }
            if (keyData == (Keys.Control | Keys.Right)) { Skip(1, true); return true; }
            if (keyData == (Keys.Control | Keys.Left)) { Previous(); return true; }
            if (keyData == (Keys.Alt | Keys.Left)) { Go(_historyIndex - 1); return true; }
            if (keyData == (Keys.Alt | Keys.Right)) { Go(_historyIndex + 1); return true; }
            if (keyData == (Keys.Control | Keys.L) || keyData == (Keys.Control | Keys.F)) { _top.SearchBox.Focus(); _top.SearchBox.SelectAll(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ============================================================================ pages
        void ShowLoading(string what)
        {
            List<Block> b = new List<Block>();
            b.Add(new TextBlock("Loading " + what + "..."));
            _page.SetBlocks(b);
        }

        void ShowError(Exception err)
        {
            List<Block> b = new List<Block>();
            b.Add(new SectionBlock("Something went wrong", true));
            b.Add(new TextBlock(err == null ? "Nothing was returned." : err.Message));
            _page.SetBlocks(b);
        }

        /// <summary>Loads a page's data off the UI thread; ignores the result if the user moved on meanwhile.</summary>
        void LoadPage<T>(string what, Work<T> work, Done<T> build) where T : class
        {
            int generation = ++_pageGeneration;
            ShowLoading(what);
            Async.Run(this, work, delegate (T result, Exception err)
            {
                if (generation != _pageGeneration) return;
                if (result == null) ShowError(err);
                else build(result, null);
            });
        }

        void ShowPage(NavTarget t)
        {
            switch (t.Kind)
            {
                case PageKind.Browse: LoadPage("Browse", delegate { return _backend.GetBrowse(); }, delegate (List<BrowseSection> r, Exception e) { _page.SetBlocks(BrowsePage(r)); }); break;
                case PageKind.Search: LoadPage("results", delegate { return _backend.Search(t.Id); }, delegate (SearchResults r, Exception e) { _page.SetBlocks(SearchPage(r)); }); break;
                case PageKind.Album: LoadPage("album", delegate { return _backend.GetAlbum(t.Id); }, delegate (Album r, Exception e) { _page.SetBlocks(AlbumPage(r)); PlayLinkedPage("album", t.Id, r.Tracks); }); break;
                case PageKind.Artist: LoadPage("artist", delegate { return _backend.GetArtist(t.Id); }, delegate (ArtistPage r, Exception e) { _page.SetBlocks(ArtistPageBlocks(r)); PlayLinkedPage("artist", t.Id, r.Popular); }); break;
                case PageKind.Playlist: LoadPage("playlist", delegate { return _backend.GetPlaylist(t.Id); }, delegate (Playlist r, Exception e) { _page.SetBlocks(PlaylistPage(r)); PlayLinkedPage("playlist", t.Id, r.Tracks); }); break;
                case PageKind.Songs: LoadPage("songs", delegate { return _backend.GetSavedTracks(); }, delegate (List<Track> r, Exception e) { _page.SetBlocks(SongsPage(r)); }); break;
                case PageKind.Albums: LoadPage("albums", delegate { return _backend.GetSavedAlbums(); }, delegate (List<Album> r, Exception e) { _page.SetBlocks(AlbumsPage(r)); }); break;
                case PageKind.Artists: LoadPage("artists", delegate { return _backend.GetFollowedArtists(); }, delegate (List<Artist> r, Exception e) { _page.SetBlocks(ArtistsPage(r)); }); break;
            }
            _page.Focus();
        }

        static Tile AlbumTile(Album a) { return new Tile(a.Name, a.ArtistName + (a.Year > 0 ? " · " + a.Year : ""), a.ImageUrl, false, new NavTarget(PageKind.Album, a.Id)); }
        static Tile PlaylistTile(Playlist p) { return new Tile(p.Name, "By " + p.Owner, p.ImageUrl, false, new NavTarget(PageKind.Playlist, p.Id)); }
        static Tile ArtistTile(Artist a) { return new Tile(a.Name, "Artist", a.ImageUrl, true, new NavTarget(PageKind.Artist, a.Id)); }

        static int TotalMinutes(List<Track> tracks) { long ms = 0; foreach (Track t in tracks) ms += t.DurationMs; return (int)(ms / 60000); }
        static string Length(List<Track> tracks)
        {
            int min = TotalMinutes(tracks);
            string songs = tracks.Count + (tracks.Count == 1 ? " song" : " songs");
            return min >= 60 ? songs + ", " + min / 60 + " hr " + min % 60 + " min" : songs + ", " + min + " min";
        }

        List<Block> BrowsePage(List<BrowseSection> sections)
        {
            List<Block> b = new List<Block>();
            b.Add(new SectionBlock("Browse", true));
            foreach (BrowseSection s in sections)
            {
                List<Tile> tiles = new List<Tile>();
                foreach (Album a in s.Albums) tiles.Add(AlbumTile(a));
                foreach (Playlist p in s.Playlists) tiles.Add(PlaylistTile(p));
                if (tiles.Count == 0) continue;
                b.Add(new SectionBlock(s.Title.ToUpperInvariant(), false));
                b.Add(new TileGridBlock(tiles, 2));
            }
            return b;
        }

        List<Block> SearchPage(SearchResults r)
        {
            List<Block> b = new List<Block>();
            b.Add(new SectionBlock("Results for “" + r.Query + "”", true));
            if (r.Tracks.Count + r.Artists.Count + r.Albums.Count + r.Playlists.Count == 0)
            {
                b.Add(new TextBlock("No results found. Check the spelling or try something else."));
                return b;
            }
            if (r.Tracks.Count > 0)
            {
                b.Add(new SectionBlock("SONGS", false));
                b.Add(new TrackListBlock(r.Tracks, TrackColumns.Artist | TrackColumns.Album | TrackColumns.Art, false));
            }
            if (r.Artists.Count > 0)
            {
                List<Tile> t = new List<Tile>(); foreach (Artist a in r.Artists) t.Add(ArtistTile(a));
                b.Add(new SectionBlock("ARTISTS", false)); b.Add(new TileGridBlock(t, 1));
            }
            if (r.Albums.Count > 0)
            {
                List<Tile> t = new List<Tile>(); foreach (Album a in r.Albums) t.Add(AlbumTile(a));
                b.Add(new SectionBlock("ALBUMS", false)); b.Add(new TileGridBlock(t, 2));
            }
            if (r.Playlists.Count > 0)
            {
                List<Tile> t = new List<Tile>(); foreach (Playlist p in r.Playlists) t.Add(PlaylistTile(p));
                b.Add(new SectionBlock("PLAYLISTS", false)); b.Add(new TileGridBlock(t, 1));
            }
            return b;
        }

        List<Block> AlbumPage(Album a)
        {
            List<Block> b = new List<Block>();
            List<PageButton> buttons = new List<PageButton>();
            buttons.Add(new PageButton("PLAY", true, 104, delegate { PlayList(a.Tracks, 0); }));
            buttons.Add(new PageButton("SAVE", false, 100, null));
            b.Add(new HeaderBlock(a.Type, a.Name, a.ArtistName, new NavTarget(PageKind.Artist, a.ArtistId),
                (a.Year > 0 ? a.Year + " • " : "") + Length(a.Tracks), a.ImageUrl, buttons));
            b.Add(new TrackListBlock(a.Tracks, TrackColumns.Number | TrackColumns.Popularity, true));
            return b;
        }

        string OwnerName(Playlist p)
        {
            foreach (Playlist q in _myPlaylists) if (q.Id == p.Id && !String.IsNullOrEmpty(q.Owner)) return q.Owner;
            return p.Owner;
        }

        List<Block> PlaylistPage(Playlist p)
        {
            List<Block> b = new List<Block>();
            List<PageButton> buttons = new List<PageButton>();
            buttons.Add(new PageButton("PLAY", true, 104, delegate { PlayList(p.Tracks, 0); }));
            if (_libraryIds.ContainsKey(p.Id)) buttons.Add(new PageButton("SHARE", false, 100, delegate { CopyText("https://open.spotify.com/playlist/" + p.Id); }));
            else buttons.Add(new PageButton("SAVE", false, 100, delegate { SavePlaylistToLibrary(p); }));
            b.Add(new HeaderBlock("PLAYLIST", p.Name, null, null,
                "Created by " + OwnerName(p) + " • " + Length(p.Tracks), p.ImageUrl, buttons));
            if (!String.IsNullOrEmpty(p.Description)) b.Add(new TextBlock(p.Description));
            b.Add(new TrackListBlock(p.Tracks, TrackColumns.Number | TrackColumns.Artist | TrackColumns.Album | TrackColumns.Art, true));
            return b;
        }

        List<Block> ArtistPageBlocks(ArtistPage page)
        {
            List<Block> b = new List<Block>();
            List<PageButton> buttons = new List<PageButton>();
            buttons.Add(new PageButton("PLAY", true, 104, delegate { PlayList(page.Popular, 0); }));
            buttons.Add(new PageButton("FOLLOW", false, 100, null));
            b.Add(new ArtistHeaderBlock(page.Artist, buttons));
            if (page.Popular.Count > 0)
            {
                b.Add(new SectionBlock("POPULAR", false));
                b.Add(new TrackListBlock(page.Popular.Count > 5 ? page.Popular.GetRange(0, 5) : page.Popular,
                    TrackColumns.Number | TrackColumns.Art | TrackColumns.PlayCount, false));
            }
            AddTiles(b, "ALBUMS", page.Albums, 0);
            AddTiles(b, "SINGLES", page.Singles, 0);
            if (page.Related.Count > 0)
            {
                List<Tile> t = new List<Tile>(); foreach (Artist a in page.Related) t.Add(ArtistTile(a));
                b.Add(new SectionBlock("RELATED ARTISTS", false)); b.Add(new TileGridBlock(t, 1));
            }
            return b;
        }

        static void AddTiles(List<Block> b, string title, List<Album> albums, int rows)
        {
            if (albums.Count == 0) return;
            List<Tile> t = new List<Tile>(); foreach (Album a in albums) t.Add(AlbumTile(a));
            b.Add(new SectionBlock(title, false)); b.Add(new TileGridBlock(t, rows));
        }

        List<Block> SongsPage(List<Track> tracks)
        {
            List<Block> b = new List<Block>();
            b.Add(new SectionBlock("Liked Songs", true));
            b.Add(new TrackListBlock(tracks, TrackColumns.Art | TrackColumns.Artist | TrackColumns.Album, true));
            return b;
        }

        List<Block> AlbumsPage(List<Album> albums)
        {
            List<Block> b = new List<Block>();
            b.Add(new SectionBlock("Saved Albums", true));
            List<Tile> t = new List<Tile>(); foreach (Album a in albums) t.Add(AlbumTile(a));
            b.Add(new TileGridBlock(t, 0));
            return b;
        }

        List<Block> ArtistsPage(List<Artist> artists)
        {
            List<Block> b = new List<Block>();
            b.Add(new SectionBlock("Saved Artists", true));
            List<Tile> t = new List<Tile>(); foreach (Artist a in artists) t.Add(ArtistTile(a));
            b.Add(new TileGridBlock(t, 0));
            return b;
        }
    }
}