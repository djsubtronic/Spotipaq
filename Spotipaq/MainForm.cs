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
        readonly TopBar _top = new TopBar();
        readonly Sidebar _sidebar = new Sidebar();
        readonly NowPlayingBar _bar = new NowPlayingBar();
        readonly PageView _page;
        readonly Timer _tick = new Timer();

        // navigation
        readonly List<NavTarget> _history = new List<NavTarget>();
        int _historyIndex = -1;
        int _pageGeneration;

        // play queue
        List<Track> _queue = new List<Track>();
        List<int> _order = new List<int>();     // play order (shuffled or 0..n-1)
        int _orderPos = -1;
        bool _shuffle, _repeat;
        readonly Random _rng = new Random();

        // saved between runs
        readonly AppSettings _settings = AppSettings.Load();
        readonly Dictionary<string, bool> _libraryIds = new Dictionary<string, bool>();   // playlists already in your library
        List<Playlist> _myPlaylists = new List<Playlist>();
        bool _auto;                              // the current track was started by auto-advance (not by a click)
        int _skipStreak;                         // unplayable tracks skipped in a row
        Track _restored; int _restoredPos;      // last track from the previous run (shown paused in the bar until you press play)

        public MainForm(ISpotifyBackend backend, IPlayback player)
        {
            _backend = backend;
            _player = player;
            Text = "Spotipaq";
            BackColor = Theme.Main;
            ForeColor = Theme.Text;
            Font = Theme.Normal;
            ClientSize = new Size(1000, 680);
            MinimumSize = new Size(800, 560);
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            RestoreWindow();
            _shuffle = _settings.GetBool("shuffle", false);
            _repeat = _settings.GetBool("repeat", false);
            _player.Volume = Math.Max(0, Math.Min(100, _settings.GetInt("volume", 80)));
            int pos; _restored = _settings.GetTrack(out pos); _restoredPos = pos;

            _images = new ImageCache(backend, this);
            _page = new PageView(this);

            _sidebar.Width = 210; _sidebar.Dock = DockStyle.Left;
            _top.Dock = DockStyle.Top;
            _bar.Dock = DockStyle.Bottom;
            _page.Dock = DockStyle.Fill;
            _bar.Images = _images;
            Controls.Add(_page); Controls.Add(_sidebar); Controls.Add(_top); Controls.Add(_bar);   // z-order: fill added first

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
            _bar.RepeatToggled += delegate { _repeat = !_repeat; RefreshBar(); SaveSettings(false); };
            _bar.SeekRequested += delegate (object s, SeekEventArgs e) { if (_player.Current != null) _player.Seek(e.Ms); RefreshBar(); };
            _bar.LinkClicked += delegate (object s, NavEventArgs e) { Navigate(e.Target); };
            _bar.VolumeChanged += delegate { _player.Volume = _bar.Volume; _volumeDirty = true; };
            _player.StateChanged += delegate { SafeUi(RefreshAll); };
            _player.TrackEnded += delegate { SafeUi(delegate { _skipStreak = 0; Skip(1, false); }); };
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
            if (m.Msg != 0x020A) return false;                 // WM_MOUSEWHEEL
            IntPtr under = WindowFromPoint(Cursor.Position);
            if (under == IntPtr.Zero || under == m.HWnd) return false;
            if (under != _page.Handle && under != _sidebar.Handle) return false;
            SendMessage(under, m.Msg, m.WParam, m.LParam);
            return true;
        }

        bool _volumeDirty;

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); AppIcon.Apply(this); }

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
            _settings.Set("repeat", _repeat);
            if (full)
            {
                Rectangle b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                _settings.Set("win.x", b.X); _settings.Set("win.y", b.Y); _settings.Set("win.w", b.Width); _settings.Set("win.h", b.Height);
                _settings.Set("win.max", WindowState == FormWindowState.Maximized);
                NavTarget t = Current;
                if (t != null) { _settings.Set("page.kind", t.Kind.ToString()); _settings.Set("page.id", t.Id); }
                Track tr = _player.Current ?? _restored;
                if (tr != null) _settings.SetTrack(tr, _player.Current != null ? _player.PositionMs : _restoredPos);
            }
            _settings.Save();
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
            ContextMenuStrip m = new ContextMenuStrip();
            m.ShowImageMargin = false;
            m.Font = Theme.Normal;
            m.Items.Add("Open log file", null, delegate
            {
                Engine.Log.Flush();
                try { System.Diagnostics.Process.Start("notepad.exe", "\"" + Engine.Log.FilePath + "\""); } catch { }
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

        void SafeUi(MethodInvoker m)
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (InvokeRequired) { try { BeginInvoke(m); } catch (InvalidOperationException) { } }
            else m();
        }

        // ============================================================================ IPageHost
        public ImageCache Images { get { return _images; } }
        public Track PlayingTrack { get { return _player.Current; } }
        public bool IsPlaying { get { return _player.State == PlayState.Playing || _player.State == PlayState.Loading; } }

        NavTarget Current { get { return _historyIndex >= 0 ? _history[_historyIndex] : null; } }

        public void Navigate(NavTarget target)
        {
            if (target == null || target.SameAs(Current)) return;
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
                _queue = new List<Track>(); _queue.Add(t);
                BuildOrder(0);
                _orderPos = 0;
                _player.Play(t, at);
            }
            else if (_queue.Count > 0) PlayAt(Math.Max(0, _orderPos));
            RefreshAll();
        }


        // ============================================================================ context menus
        static ContextMenuStrip NewMenu()
        {
            ContextMenuStrip m = new ContextMenuStrip();
            m.ShowImageMargin = false;
            m.Font = Theme.Normal;
            return m;
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
            Track t = list[index];
            ContextMenuStrip m = NewMenu();
            m.Items.Add("Play", null, delegate { PlayList(list, index); });
            m.Items.Add("Play next", null, delegate { QueueTrack(t, true); });
            m.Items.Add("Add to queue", null, delegate { QueueTrack(t, false); });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Go to song radio", null, delegate { GoToRadio(t); });
            if (!String.IsNullOrEmpty(t.ArtistId)) { string id = t.ArtistId; m.Items.Add("Go to artist", null, delegate { Navigate(new NavTarget(PageKind.Artist, id)); }); }
            if (!String.IsNullOrEmpty(t.AlbumId)) { string id = t.AlbumId; m.Items.Add("Go to album", null, delegate { Navigate(new NavTarget(PageKind.Album, id)); }); }
            m.Items.Add(new ToolStripSeparator());
            NavTarget curPage = Current;
            if (curPage != null && curPage.Kind == PageKind.Songs)
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
            }
            RefreshBar();
        }

        void PlayAt(int orderPos)
        {
            if (orderPos < 0 || orderPos >= _order.Count) return;
            _orderPos = orderPos;
            _player.Play(_queue[_order[orderPos]], 0);
            RefreshAll();
        }

        void Skip(int delta, bool user)
        {
            if (_order.Count == 0) return;
            int next = _orderPos + delta;
            if (next >= _order.Count) { if (_repeat || user) next = 0; else { _player.Stop(); RefreshAll(); return; } }
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
            _bar.Repeat = _repeat;
            if (!_bar.DraggingVolume) _bar.Volume = _player.Volume;
            _bar.Invalidate();
            string title = t == null ? "Spotipaq" : t.Name + " - " + t.ArtistName;
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
                case PageKind.Album: LoadPage("album", delegate { return _backend.GetAlbum(t.Id); }, delegate (Album r, Exception e) { _page.SetBlocks(AlbumPage(r)); }); break;
                case PageKind.Artist: LoadPage("artist", delegate { return _backend.GetArtist(t.Id); }, delegate (ArtistPage r, Exception e) { _page.SetBlocks(ArtistPageBlocks(r)); }); break;
                case PageKind.Playlist: LoadPage("playlist", delegate { return _backend.GetPlaylist(t.Id); }, delegate (Playlist r, Exception e) { _page.SetBlocks(PlaylistPage(r)); }); break;
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
