// MockBackend.cs - fake, deterministic library + a silent "player", so every screen can be built and
// tested on the Compaq before the real Spotify engine exists. Artwork is drawn procedurally.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;

namespace Spotipaq
{
    public sealed class MockBackend : ISpotifyBackend
    {
        static readonly string[] ArtistNames = {
            "The Velvet Static", "Nightjar Avenue", "Copperline", "Mara Lune", "Glasshouse Echo", "Iron Meridian",
            "Saint Kestrel", "Low Orbit Choir", "Dune Telegraph", "Harbour Lights", "Paper Comets", "Ashgrove" };
        static readonly string[] Words = {
            "Midnight", "Static", "Gold", "River", "Signal", "Glass", "Northern", "Echo", "Wire", "Summer", "Ghost",
            "Satellite", "Dust", "Neon", "Harbour", "Ember", "Silver", "Thunder", "Paper", "Velvet", "Hollow", "Crown" };
        static readonly string[] Nouns = {
            "Lines", "Roads", "Hearts", "Days", "Machines", "Skies", "Waves", "Fires", "Lights", "Dreams", "Rooms",
            "Bridges", "Stars", "Tides", "Voices", "Engines" };

        readonly List<Artist> _artists = new List<Artist>();
        readonly List<Album> _albums = new List<Album>();
        readonly List<Playlist> _playlists = new List<Playlist>();
        readonly Dictionary<string, Album> _albumById = new Dictionary<string, Album>();
        readonly Dictionary<string, Artist> _artistById = new Dictionary<string, Artist>();

        public MockBackend()
        {
            Random r = new Random(1056);
            for (int a = 0; a < ArtistNames.Length; a++)
            {
                Artist artist = new Artist();
                artist.Id = "ar" + a;
                artist.Name = ArtistNames[a];
                artist.ImageUrl = "mock:artist:" + a;
                artist.BannerUrl = "mock:banner:" + a;
                artist.Followers = r.Next(20000, 4000000);
                _artists.Add(artist);
                _artistById[artist.Id] = artist;
                int albumCount = 2 + r.Next(3);
                for (int b = 0; b < albumCount; b++)
                {
                    Album album = new Album();
                    album.Id = "al" + a + "_" + b;
                    album.Name = Title(r);
                    album.ArtistId = artist.Id;
                    album.ArtistName = artist.Name;
                    album.Year = 1998 + r.Next(28);
                    album.Type = b == albumCount - 1 && r.Next(2) == 0 ? "SINGLE" : "ALBUM";
                    album.ImageUrl = "mock:album:" + a * 10 + b;
                    int tracks = album.Type == "SINGLE" ? 1 + r.Next(2) : 8 + r.Next(6);
                    for (int t = 0; t < tracks; t++)
                    {
                        Track track = new Track();
                        track.Id = album.Id + "_t" + t;
                        track.Name = t == 0 && album.Type == "SINGLE" ? album.Name : Title(r);
                        track.ArtistId = artist.Id;
                        track.ArtistName = artist.Name;
                        track.AlbumId = album.Id;
                        track.AlbumName = album.Name;
                        track.AlbumImageUrl = album.ImageUrl;
                        track.Number = t + 1;
                        track.DurationMs = (150 + r.Next(240)) * 1000 + r.Next(1000);
                        track.Explicit = r.Next(6) == 0;
                        track.Popularity = r.Next(15, 100);
                        track.PlayCount = (long)track.Popularity * track.Popularity * r.Next(500, 40000);
                        album.Tracks.Add(track);
                    }
                    _albums.Add(album);
                    _albumById[album.Id] = album;
                }
            }
            string[] lists = { "Mix01", "Mix02", "Your Top Songs 2026", "Missed Hits", "On Repeat", "Heavy Rotation", "Late Night Drive", "Download" };
            for (int p = 0; p < lists.Length; p++)
            {
                Playlist pl = new Playlist();
                pl.Id = "pl" + p;
                pl.Name = lists[p];
                pl.Owner = p % 3 == 2 ? "Spotify" : "sr1056";
                pl.Description = p % 3 == 2 ? "Picked for you, updated daily." : "";
                pl.ImageUrl = "mock:playlist:" + p;
                int n = 12 + r.Next(25);
                for (int i = 0; i < n; i++)
                {
                    Album al = _albums[r.Next(_albums.Count)];
                    pl.Tracks.Add(al.Tracks[r.Next(al.Tracks.Count)]);
                }
                _playlists.Add(pl);
            }
        }

        static string Title(Random r)
        {
            return r.Next(3) == 0 ? Words[r.Next(Words.Length)] : Words[r.Next(Words.Length)] + " " + Nouns[r.Next(Nouns.Length)];
        }

        static void Latency() { Thread.Sleep(120); }        // makes "Loading..." states visible

        public UserInfo GetUser() { UserInfo u = new UserInfo(); u.Name = "sr1056"; u.Premium = true; return u; }
        public List<Playlist> GetPlaylists() { Latency(); return new List<Playlist>(_playlists); }

        public List<BrowseSection> GetBrowse()
        {
            Latency();
            List<BrowseSection> list = new List<BrowseSection>();
            BrowseSection a = new BrowseSection(); a.Title = "New Releases";
            BrowseSection b = new BrowseSection(); b.Title = "Made For You";
            BrowseSection c = new BrowseSection(); c.Title = "Popular Albums";
            for (int i = 0; i < _albums.Count; i++)
            {
                if (_albums[i].Year >= 2020 && a.Albums.Count < 10) a.Albums.Add(_albums[i]);
                else if (i % 3 == 0 && c.Albums.Count < 10) c.Albums.Add(_albums[i]);
            }
            b.Playlists.AddRange(_playlists);
            list.Add(a); list.Add(b); list.Add(c);
            return list;
        }

        public string GetSongRadio(string trackId) { return null; }
        public void SetSaved(string uri, bool saved) { }

        public Playlist GetPlaylist(string id)
        {
            Latency();
            foreach (Playlist p in _playlists) if (p.Id == id) return p;
            return null;
        }

        public Album GetAlbum(string id) { Latency(); Album a; return _albumById.TryGetValue(id, out a) ? a : null; }

        public ArtistPage GetArtist(string id)
        {
            Latency();
            Artist artist;
            if (!_artistById.TryGetValue(id, out artist)) return null;
            ArtistPage page = new ArtistPage();
            page.Artist = artist;
            List<Track> all = new List<Track>();
            foreach (Album al in _albums)
            {
                if (al.ArtistId != id) continue;
                if (al.Type == "SINGLE") page.Singles.Add(al); else page.Albums.Add(al);
                all.AddRange(al.Tracks);
            }
            all.Sort(delegate (Track x, Track y) { return y.PlayCount.CompareTo(x.PlayCount); });
            for (int i = 0; i < all.Count && i < 10; i++) page.Popular.Add(all[i]);
            int idx = _artists.IndexOf(artist);
            for (int i = 1; i <= 6; i++) page.Related.Add(_artists[(idx + i * 5) % _artists.Count]);
            return page;
        }

        public SearchResults Search(string query)
        {
            Latency();
            SearchResults s = new SearchResults();
            s.Query = query;
            string q = query.ToLowerInvariant();
            foreach (Artist a in _artists) if (a.Name.ToLowerInvariant().Contains(q)) s.Artists.Add(a);
            foreach (Album al in _albums)
            {
                if (al.Name.ToLowerInvariant().Contains(q) || al.ArtistName.ToLowerInvariant().Contains(q)) s.Albums.Add(al);
                foreach (Track t in al.Tracks)
                    if (s.Tracks.Count < 50 && (t.Name.ToLowerInvariant().Contains(q) || t.ArtistName.ToLowerInvariant().Contains(q)))
                        s.Tracks.Add(t);
            }
            foreach (Playlist p in _playlists) if (p.Name.ToLowerInvariant().Contains(q)) s.Playlists.Add(p);
            return s;
        }

        public List<Track> GetSavedTracks()
        {
            Latency();
            List<Track> list = new List<Track>();
            for (int i = 0; i < _albums.Count; i += 2) list.AddRange(_albums[i].Tracks.GetRange(0, Math.Min(3, _albums[i].Tracks.Count)));
            return list;
        }

        public List<Album> GetSavedAlbums()
        {
            Latency();
            List<Album> list = new List<Album>();
            for (int i = 0; i < _albums.Count; i += 3) list.Add(_albums[i]);
            return list;
        }

        public List<Artist> GetFollowedArtists() { Latency(); return new List<Artist>(_artists); }

        // ---- procedural artwork: gradient + initials (albums/playlists), round-friendly portraits, wide banners ----
        public Bitmap LoadImage(string url)
        {
            Thread.Sleep(40);
            if (url == null || !url.StartsWith("mock:")) return null;
            string[] parts = url.Split(':');
            int seed = parts[2].GetHashCode() ^ parts[1].GetHashCode();
            Random r = new Random(seed);
            bool banner = parts[1] == "banner";
            int w = banner ? 800 : 300, h = banner ? 260 : 300;
            Color c1 = Color.FromArgb(40 + r.Next(180), 40 + r.Next(180), 40 + r.Next(180));
            Color c2 = Color.FromArgb(r.Next(90), r.Next(90), r.Next(90));
            Bitmap bmp = new Bitmap(w, h);
            using (Graphics g = Graphics.FromImage(bmp))
            using (LinearGradientBrush lg = new LinearGradientBrush(new Rectangle(0, 0, w, h), c1, c2, 35f + r.Next(60)))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.FillRectangle(lg, 0, 0, w, h);
                for (int i = 0; i < 6; i++)
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(30 + r.Next(40), 255, 255, 255)))
                    {
                        int d = 40 + r.Next(w / 2);
                        g.FillEllipse(b, r.Next(w) - d / 2, r.Next(h) - d / 2, d, d);
                    }
                if (!banner)
                {
                    string label = parts[1] == "artist" ? Initials(ArtistNames[Math.Abs(seed) % ArtistNames.Length]) : parts[1].Substring(0, 1).ToUpperInvariant() + parts[2];
                    using (Font f = new Font(Theme.FontFamilyName, 48f, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (SolidBrush white = new SolidBrush(Color.FromArgb(220, 255, 255, 255)))
                    {
                        StringFormat sf = new StringFormat(); sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;
                        g.DrawString(label, f, white, new RectangleF(0, 0, w, h), sf);
                    }
                }
            }
            return bmp;
        }

        static string Initials(string name)
        {
            string s = "";
            foreach (string part in name.Split(' ')) if (part.Length > 0 && s.Length < 2) s += part[0];
            return s;
        }
    }

    /// <summary>Silent stand-in for the real audio engine: a clock that advances while "playing".</summary>
    public sealed class MockPlayback : IPlayback
    {
        public event EventHandler StateChanged;
        public event EventHandler TrackEnded;
        public event EventHandler TrackUnplayable { add { } remove { } }
        PlayState _state = PlayState.Stopped;
        Track _current;
        int _baseMs, _baseTick;
        int _volume = 80;
        readonly System.Threading.Timer _timer;

        public MockPlayback() { _timer = new System.Threading.Timer(Tick, null, 250, 250); }

        public PlayState State { get { return _state; } }
        public Track Current { get { return _current; } }
        public int Volume { get { return _volume; } set { _volume = Math.Max(0, Math.Min(100, value)); } }
        public int PositionMs
        {
            get
            {
                if (_state != PlayState.Playing) return _baseMs;
                return Math.Min(_current == null ? 0 : _current.DurationMs, _baseMs + unchecked(Environment.TickCount - _baseTick));
            }
        }

        public void Play(Track track, int startMs)
        {
            _current = track; _baseMs = startMs; _baseTick = Environment.TickCount;
            Set(PlayState.Playing);
        }
        public void Pause() { if (_state == PlayState.Playing) { _baseMs = PositionMs; Set(PlayState.Paused); } }
        public void Resume() { if (_state == PlayState.Paused) { _baseTick = Environment.TickCount; Set(PlayState.Playing); } }
        public void Seek(int ms) { _baseMs = Math.Max(0, ms); _baseTick = Environment.TickCount; Fire(StateChanged); }
        public void Stop() { _current = null; _baseMs = 0; Set(PlayState.Stopped); }

        void Set(PlayState s) { _state = s; Fire(StateChanged); }
        void Fire(EventHandler h) { if (h != null) h(this, EventArgs.Empty); }

        void Tick(object o)
        {
            if (_state == PlayState.Playing && _current != null && PositionMs >= _current.DurationMs)
            {
                _baseMs = _current.DurationMs;
                _state = PlayState.Paused;
                Fire(TrackEnded);
            }
        }
    }
}
