// Model.cs - plain data objects shared by the UI and every backend (mock now, real Spotify later).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Spotipaq
{
    public sealed class Artist
    {
        public string Id, Name;
        public string ImageUrl;          // square portrait
        public string BannerUrl;         // wide header image (artist page)
        public long Followers;
    }

    public sealed class Album
    {
        public string Id, Name, ArtistId, ArtistName;
        public string ImageUrl;
        public int Year;
        public string Type = "ALBUM";    // ALBUM / SINGLE / EP / COMPILATION
        public List<Track> Tracks = new List<Track>();
    }

    public sealed class Track
    {
        public string Id, Name;
        public string ArtistId, ArtistName;
        public string AlbumId, AlbumName, AlbumImageUrl;
        public int DurationMs;
        public int Number;               // position on its album
        public bool Explicit;
        public int Popularity;           // 0..100 (drawn as the little bar meter)
        public long PlayCount;           // shown on artist "Popular" lists
    }

    public sealed class Playlist
    {
        public string Id, Name, Owner, Description;
        public string ImageUrl;
        public string HeaderImageUrl;      // wide banner picture (radios, mixes, "This Is", editorial playlists), or null
        public string MadeFor;             // user name, for playlists Spotify generated for someone
        public string Color;               // "#RRGGBB": the colour Spotify itself picked for the cover (page background), or null
        public List<Track> Tracks = new List<Track>();
    }

    public sealed class ArtistPage
    {
        public Artist Artist;
        public List<Track> Popular = new List<Track>();
        public List<Album> Albums = new List<Album>();
        public List<Album> Singles = new List<Album>();
        public List<Artist> Related = new List<Artist>();
    }

    public sealed class SearchResults
    {
        public string Query;
        public List<Track> Tracks = new List<Track>();
        public List<Artist> Artists = new List<Artist>();
        public List<Album> Albums = new List<Album>();
        public List<Playlist> Playlists = new List<Playlist>();
    }

    public sealed class BrowseSection
    {
        public string Title;
        public List<Album> Albums = new List<Album>();
        public List<Playlist> Playlists = new List<Playlist>();
    }

    public sealed class UserInfo
    {
        public string Name;
        public bool Premium;
    }

    /// <summary>Where the main area is: one entry of the back/forward history.</summary>
    public enum PageKind { Browse, Search, Album, Artist, Playlist, Songs, Albums, Artists }

    public sealed class NavTarget
    {
        public PageKind Kind;
        public string Id;                // album/artist/playlist id, or the search query
        public NavTarget(PageKind kind, string id) { Kind = kind; Id = id; }
        public bool SameAs(NavTarget o) { return o != null && o.Kind == Kind && o.Id == Id; }
    }

    /// <summary>Small key=value file in %APPDATA%\Spotipaq\settings.ini: window, page, volume, shuffle, last track, playlist order.</summary>
    public sealed class AppSettings
    {
        static string PathName { get { return Path.Combine(Spotipaq.Engine.Log.Folder, "settings.ini"); } }
        readonly Dictionary<string, string> _v = new Dictionary<string, string>();

        public static AppSettings Load()
        {
            AppSettings s = new AppSettings();
            try
            {
                if (File.Exists(PathName))
                    foreach (string line in File.ReadAllLines(PathName))
                    {
                        int eq = line.IndexOf('=');
                        if (eq > 0) s._v[line.Substring(0, eq).Trim()] = line.Substring(eq + 1);
                    }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Spotipaq.Engine.Log.Folder);
                List<string> lines = new List<string>();
                foreach (KeyValuePair<string, string> kv in _v) lines.Add(kv.Key + "=" + kv.Value);
                string tmp = PathName + ".tmp";
                File.WriteAllLines(tmp, lines.ToArray());
                if (File.Exists(PathName)) File.Delete(PathName);
                File.Move(tmp, PathName);
            }
            catch { }
        }

        public string GetString(string key, string def) { string v; return _v.TryGetValue(key, out v) ? v : def; }
        public void Set(string key, string value) { if (value == null) _v.Remove(key); else _v[key] = value.Replace("\r", " ").Replace("\n", " "); }
        public int GetInt(string key, int def) { int r; return Int32.TryParse(GetString(key, null), NumberStyles.Integer, CultureInfo.InvariantCulture, out r) ? r : def; }
        public void Set(string key, int value) { _v[key] = value.ToString(CultureInfo.InvariantCulture); }
        public bool GetBool(string key, bool def) { string v = GetString(key, null); return v == null ? def : v == "1"; }
        public void Set(string key, bool value) { _v[key] = value ? "1" : "0"; }

        public void SetTrack(Track t, int positionMs)
        {
            if (t == null) return;
            Set("track.id", t.Id); Set("track.name", t.Name); Set("track.artistId", t.ArtistId); Set("track.artist", t.ArtistName);
            Set("track.albumId", t.AlbumId); Set("track.album", t.AlbumName); Set("track.image", t.AlbumImageUrl);
            Set("track.duration", t.DurationMs); Set("track.explicit", t.Explicit); Set("track.pos", positionMs);
        }

        // ---- play queue (queue.txt next to settings.ini): line 1 = version, line 2 = play order, then one tab-separated track per line
        static string QueuePath { get { return Path.Combine(Spotipaq.Engine.Log.Folder, "queue.txt"); } }

        static string Field(string s) { return s == null ? "" : s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' '); }
        static string OrNull(string s) { return s.Length == 0 ? null : s; }

        public static void SaveQueue(List<Track> queue, List<int> order)
        {
            try
            {
                Directory.CreateDirectory(Spotipaq.Engine.Log.Folder);
                string tmp = QueuePath + ".tmp";
                using (StreamWriter w = new StreamWriter(tmp, false, new System.Text.UTF8Encoding(false)))
                {
                    w.WriteLine("1");
                    string[] o = new string[order.Count];
                    for (int i = 0; i < o.Length; i++) o[i] = order[i].ToString(CultureInfo.InvariantCulture);
                    w.WriteLine(String.Join(",", o));
                    foreach (Track t in queue)
                        w.WriteLine(String.Join("\t", new string[] {
                            Field(t.Id), Field(t.Name), Field(t.ArtistId), Field(t.ArtistName),
                            Field(t.AlbumId), Field(t.AlbumName), Field(t.AlbumImageUrl),
                            t.DurationMs.ToString(CultureInfo.InvariantCulture), t.Number.ToString(CultureInfo.InvariantCulture),
                            t.Explicit ? "1" : "0", t.Popularity.ToString(CultureInfo.InvariantCulture),
                            t.PlayCount.ToString(CultureInfo.InvariantCulture) }));
                }
                if (File.Exists(QueuePath)) File.Delete(QueuePath);
                File.Move(tmp, QueuePath);
            }
            catch { }
        }

        /// <summary>False (and nothing usable) when there is no saved queue or the file doesn't add up.</summary>
        public static bool LoadQueue(out List<Track> queue, out List<int> order)
        {
            queue = new List<Track>(); order = new List<int>();
            try
            {
                if (!File.Exists(QueuePath)) return false;
                string[] lines = File.ReadAllLines(QueuePath);
                if (lines.Length < 3 || lines[0] != "1") return false;
                for (int i = 2; i < lines.Length; i++)
                {
                    if (lines[i].Length == 0) continue;
                    string[] f = lines[i].Split('\t');
                    if (f.Length < 12 || f[0].Length == 0) return false;
                    Track t = new Track();
                    t.Id = f[0]; t.Name = f[1]; t.ArtistId = OrNull(f[2]); t.ArtistName = f[3];
                    t.AlbumId = OrNull(f[4]); t.AlbumName = f[5]; t.AlbumImageUrl = OrNull(f[6]);
                    t.DurationMs = Int32.Parse(f[7], CultureInfo.InvariantCulture); t.Number = Int32.Parse(f[8], CultureInfo.InvariantCulture);
                    t.Explicit = f[9] == "1"; t.Popularity = Int32.Parse(f[10], CultureInfo.InvariantCulture);
                    t.PlayCount = Int64.Parse(f[11], CultureInfo.InvariantCulture);
                    queue.Add(t);
                }
                foreach (string s in lines[1].Split(','))
                {
                    int n = Int32.Parse(s, CultureInfo.InvariantCulture);
                    if (n < 0 || n >= queue.Count) return false;
                    order.Add(n);
                }
                return queue.Count > 0 && order.Count > 0;
            }
            catch { return false; }
        }

        public Track GetTrack(out int positionMs)
        {
            positionMs = GetInt("track.pos", 0);
            string id = GetString("track.id", null);
            if (String.IsNullOrEmpty(id)) return null;
            Track t = new Track();
            t.Id = id; t.Name = GetString("track.name", ""); t.ArtistId = GetString("track.artistId", null); t.ArtistName = GetString("track.artist", "");
            t.AlbumId = GetString("track.albumId", null); t.AlbumName = GetString("track.album", ""); t.AlbumImageUrl = GetString("track.image", null);
            t.DurationMs = GetInt("track.duration", 0); t.Explicit = GetBool("track.explicit", false);
            return t;
        }
    }
}