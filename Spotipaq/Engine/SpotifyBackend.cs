// SpotifyBackend.cs - the real backend. Stage 2: real playlists, albums, artists, search, library, images.
// Playback is still the mock until stage 3.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Spotipaq.Engine
{
    public sealed class SpotifyBackend : ISpotifyBackend
    {
        const string HashSearch = "fcad5a3e0d5af727fb76966f06971c19cfa2275e6ff7671196753e008611873c";
        const string HashLibTracks = "087278b20b743578a6262c2b0b4bcd20d879c503cc359a2285baf083ef944240";
        const string HashLibrary = "9f4da031f81274d572cfedaf6fc57a737c84b43d572952200b2c36aaa8fec1c6";
        const string HashArtistOverview = "1ac33ddab5d39a3a9c27802774e6d78b9405cc188c6f75aed007df2a32737c72";
        const string HashHome = "63c412a34a2071adfd99b804ea2fe1d8e9c5fd7d248e29ca54cc97a7ca06b561";
        const string Alphabet = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";

        readonly SpotifyEngine _engine;
        readonly Spclient _sp;
        readonly MockBackend _mock = new MockBackend();

        public Spclient Sp { get { return _sp; } }

        public SpotifyBackend(SpotifyEngine engine) { _engine = engine; _sp = new Spclient(engine); }

        public UserInfo GetUser()
        {
            ApSession s = _engine.EnsureConnected();
            UserInfo u = new UserInfo();
            u.Name = s.Username;
            u.Premium = s.AccountType == null || s.AccountType == "premium";   // unknown until the AP reports it
            return u;
        }

        // ---------- helpers ----------

        static string Base62(byte[] gid)
        {
            byte[] n = (byte[])gid.Clone();
            char[] outc = new char[22];
            for (int i = 21; i >= 0; i--)
            {
                int rem = 0;
                for (int j = 0; j < n.Length; j++)
                {
                    int cur = rem * 256 + n[j];
                    n[j] = (byte)(cur / 62);
                    rem = cur % 62;
                }
                outc[i] = Alphabet[rem];
            }
            return new string(outc);
        }

        static string LastPart(string uri) { if (uri == null) return null; int i = uri.LastIndexOf(':'); return i < 0 ? uri : uri.Substring(i + 1); }
        static string Uri(string type, string id) { return "spotify:" + type + ":" + id; }

        /// <summary>Picks the image whose width is closest to target from repeated Image messages.</summary>
        static string PickImage(List<ProtoField> images, int target)
        {
            string best = null; int bestDiff = int.MaxValue;
            foreach (ProtoField f in images)
            {
                Dictionary<int, List<ProtoField>> d = f.Message.ReadAll();
                ProtoField? id = ProtoReader.First(d, 1);
                if (id == null || id.Value.Data.Length == 0) continue;
                ProtoField? w = ProtoReader.First(d, 3);
                int width = w == null ? 300 : ProtoReader.Zigzag(w.Value.Int);
                int diff = Math.Abs(width - target);
                if (diff < bestDiff) { bestDiff = diff; best = "https://i.scdn.co/image/" + Spclient.Hex(id.Value.Data); }
            }
            return best;
        }

        static string GroupImage(Dictionary<int, List<ProtoField>> d, int groupField, int plainField, int target)
        {
            ProtoField? g = ProtoReader.First(d, groupField);
            if (g != null)
            {
                Dictionary<int, List<ProtoField>> gd = g.Value.Message.ReadAll();
                if (gd.ContainsKey(1)) { string u = PickImage(gd[1], target); if (u != null) return u; }
            }
            if (plainField > 0 && d.ContainsKey(plainField)) return PickImage(d[plainField], target);
            return null;
        }

        static string Str(Dictionary<int, List<ProtoField>> d, int n) { ProtoField? f = ProtoReader.First(d, n); return f == null ? null : TextUtil.Decode(f.Value.Text); }
        static int Sint(Dictionary<int, List<ProtoField>> d, int n) { ProtoField? f = ProtoReader.First(d, n); return f == null ? 0 : ProtoReader.Zigzag(f.Value.Int); }

        static Artist ParseArtistRef(ProtoField f)
        {
            Dictionary<int, List<ProtoField>> d = f.Message.ReadAll();
            Artist a = new Artist();
            ProtoField? gid = ProtoReader.First(d, 1);
            if (gid != null && gid.Value.Data.Length == 16) a.Id = Base62(gid.Value.Data);
            a.Name = Str(d, 2);
            a.ImageUrl = GroupImage(d, 17, 11, 300);
            return a;
        }

        Track ParseTrack(byte[] data)
        {
            Dictionary<int, List<ProtoField>> d = new ProtoReader(data).ReadAll();
            Track t = new Track();
            ProtoField? gid = ProtoReader.First(d, 1);
            if (gid != null && gid.Value.Data.Length == 16) t.Id = Base62(gid.Value.Data);
            t.Name = Str(d, 2);
            t.Number = Sint(d, 5);
            t.DurationMs = Sint(d, 7);
            t.Popularity = Sint(d, 8);
            ProtoField? ex = ProtoReader.First(d, 9);
            t.Explicit = ex != null && ex.Value.Int != 0;
            if (d.ContainsKey(4) && d[4].Count > 0)
            {
                List<string> names = new List<string>();
                foreach (ProtoField af in d[4])
                {
                    Artist a = ParseArtistRef(af);
                    if (a.Name != null) names.Add(a.Name);
                    if (t.ArtistId == null) t.ArtistId = a.Id;
                }
                t.ArtistName = String.Join(", ", names.ToArray());
            }
            ProtoField? alb = ProtoReader.First(d, 3);
            if (alb != null)
            {
                Dictionary<int, List<ProtoField>> ad = alb.Value.Message.ReadAll();
                ProtoField? ag = ProtoReader.First(ad, 1);
                if (ag != null && ag.Value.Data.Length == 16) t.AlbumId = Base62(ag.Value.Data);
                t.AlbumName = Str(ad, 2);
                t.AlbumImageUrl = GroupImage(ad, 17, 9, 300);
            }
            return t;
        }

        List<Track> FetchTracks(IList<string> ids)
        {
            List<string> uris = new List<string>();
            foreach (string id in ids) uris.Add(Uri("track", id));
            Dictionary<string, byte[]> meta = _sp.ExtMeta(10, uris);
            List<Track> list = new List<Track>();
            foreach (string u in uris)
            {
                byte[] b;
                if (meta.TryGetValue(u, out b))
                {
                    try { list.Add(ParseTrack(b)); } catch (Exception ex) { Log.Write("Bad track metadata: " + ex.Message); }
                }
                else
                {
                    Track gone = new Track(); gone.Id = LastPart(u); gone.Name = "(unavailable)"; gone.ArtistName = ""; gone.AlbumName = "";
                    list.Add(gone);
                }
            }
            return list;
        }

        Album ParseAlbumHead(byte[] data, out List<string> trackGids)
        {
            Dictionary<int, List<ProtoField>> d = new ProtoReader(data).ReadAll();
            Album a = new Album();
            ProtoField? gid = ProtoReader.First(d, 1);
            if (gid != null && gid.Value.Data.Length == 16) a.Id = Base62(gid.Value.Data);
            a.Name = Str(d, 2);
            if (d.ContainsKey(3) && d[3].Count > 0)
            {
                Artist ar = ParseArtistRef(d[3][0]);
                a.ArtistId = ar.Id; a.ArtistName = ar.Name;
                if (d[3].Count > 1) { List<string> n = new List<string>(); foreach (ProtoField f in d[3]) n.Add(ParseArtistRef(f).Name); a.ArtistName = String.Join(", ", n.ToArray()); }
            }
            ProtoField? type = ProtoReader.First(d, 4);
            int tv = type == null ? 1 : (int)type.Value.Int;
            a.Type = tv == 2 ? "SINGLE" : tv == 3 ? "COMPILATION" : tv == 4 ? "EP" : "ALBUM";
            ProtoField? date = ProtoReader.First(d, 6);
            if (date != null) a.Year = Sint(date.Value.Message.ReadAll(), 1);
            a.ImageUrl = GroupImage(d, 17, 9, 300);
            trackGids = new List<string>();
            if (d.ContainsKey(11))
            {
                List<KeyValuePair<int, ProtoField>> discs = new List<KeyValuePair<int, ProtoField>>();
                foreach (ProtoField disc in d[11])
                {
                    Dictionary<int, List<ProtoField>> dd = disc.Message.ReadAll();
                    discs.Add(new KeyValuePair<int, ProtoField>(Sint(dd, 1), disc));
                }
                discs.Sort(delegate (KeyValuePair<int, ProtoField> x, KeyValuePair<int, ProtoField> y) { return x.Key.CompareTo(y.Key); });
                foreach (KeyValuePair<int, ProtoField> disc in discs)
                {
                    Dictionary<int, List<ProtoField>> dd = disc.Value.Message.ReadAll();
                    if (!dd.ContainsKey(3)) continue;
                    foreach (ProtoField tf in dd[3])
                    {
                        ProtoField? tg = ProtoReader.First(tf.Message.ReadAll(), 1);
                        if (tg != null && tg.Value.Data.Length == 16) trackGids.Add(Base62(tg.Value.Data));
                    }
                }
            }
            return a;
        }

        static List<string> GroupGids(Dictionary<int, List<ProtoField>> d, int groupField)
        {
            List<string> ids = new List<string>();
            if (!d.ContainsKey(groupField)) return ids;
            foreach (ProtoField g in d[groupField])
            {
                Dictionary<int, List<ProtoField>> gd = g.Message.ReadAll();
                if (!gd.ContainsKey(1)) continue;
                foreach (ProtoField al in gd[1])
                {
                    ProtoField? gid = ProtoReader.First(al.Message.ReadAll(), 1);
                    if (gid != null && gid.Value.Data.Length == 16) { string id = Base62(gid.Value.Data); if (!ids.Contains(id)) ids.Add(id); }
                }
            }
            return ids;
        }

        static string Src(JToken sources, int target)
        {
            JArray a = sources as JArray;
            if (a == null || a.Count == 0) return null;
            string best = null; int bestDiff = int.MaxValue;
            foreach (JToken s in a)
            {
                string url = (string)s["url"];
                if (url == null) continue;
                JToken w = s["width"];
                int width = (w == null || w.Type == JTokenType.Null) ? target : (int)w;
                int diff = Math.Abs(width - target);
                if (diff < bestDiff) { bestDiff = diff; best = url; }
            }
            return best;
        }

        static string J(JToken t, string path)
        {
            if (t == null) return null;
            try { JToken r = t.SelectToken(path); return r == null || r.Type == JTokenType.Null ? null : TextUtil.Decode((string)r); } catch { return null; }
        }

        static Track JsonTrack(JToken d, string uri)
        {
            Track t = new Track();
            t.Id = LastPart(uri ?? J(d, "uri"));
            t.Name = J(d, "name");
            JToken dur = d.SelectToken("duration.totalMilliseconds");
            t.DurationMs = dur == null ? 0 : (int)dur;
            JToken num = d["trackNumber"];
            t.Number = num == null ? 0 : (int)num;
            t.Explicit = J(d, "contentRating.label") == "EXPLICIT";
            t.AlbumId = LastPart(J(d, "albumOfTrack.uri"));
            t.AlbumName = J(d, "albumOfTrack.name");
            t.AlbumImageUrl = Src(d.SelectToken("albumOfTrack.coverArt.sources"), 300);
            JArray ars = d.SelectToken("artists.items") as JArray;
            if (ars != null)
            {
                List<string> names = new List<string>();
                foreach (JToken a in ars) { names.Add(J(a, "profile.name")); if (t.ArtistId == null) t.ArtistId = LastPart(J(a, "uri")); }
                t.ArtistName = String.Join(", ", names.ToArray());
            }
            return t;
        }

        static Album JsonAlbum(JToken d)
        {
            Album a = new Album();
            a.Id = LastPart(J(d, "uri"));
            a.Name = J(d, "name");
            a.ImageUrl = Src(d.SelectToken("coverArt.sources"), 300);
            JArray ars = d.SelectToken("artists.items") as JArray;
            if (ars != null && ars.Count > 0)
            {
                a.ArtistId = LastPart(J(ars[0], "uri"));
                List<string> names = new List<string>();
                foreach (JToken x in ars) names.Add(J(x, "profile.name"));
                a.ArtistName = String.Join(", ", names.ToArray());
            }
            JToken y = d.SelectToken("date.year");
            if (y != null) a.Year = (int)y;
            else { string iso = J(d, "date.isoString"); int yy; if (iso != null && iso.Length >= 4 && Int32.TryParse(iso.Substring(0, 4), out yy)) a.Year = yy; }
            string ty = J(d, "type");
            if (ty != null) a.Type = ty.ToUpperInvariant();
            return a;
        }

        static Artist JsonArtist(JToken d)
        {
            Artist a = new Artist();
            a.Id = LastPart(J(d, "uri"));
            a.Name = J(d, "profile.name");
            a.ImageUrl = Src(d.SelectToken("visuals.avatarImage.sources"), 300);
            return a;
        }

        static Playlist JsonPlaylist(JToken d)
        {
            Playlist p = new Playlist();
            p.Id = LastPart(J(d, "uri"));
            p.Name = J(d, "name");
            p.Description = TextUtil.Clean(J(d, "description"));
            p.Owner = J(d, "ownerV2.data.name") ?? J(d, "owner.name");
            p.ImageUrl = Src(d.SelectToken("images.items[0].sources"), 300);
            return p;
        }

        // ---------- sidebar + playlists ----------

        public List<Playlist> GetPlaylists()
        {
            try
            {
                List<Playlist> fromLibrary = new List<Playlist>();
                foreach (JToken it in LibraryItems("Playlists"))
                {
                    JToken d = it.SelectToken("item.data");
                    string uri = d == null ? null : J(d, "uri");
                    if (uri == null || uri.IndexOf(":playlist:", StringComparison.Ordinal) < 0) continue;   // folders, Liked Songs...
                    Playlist p = JsonPlaylist(d);
                    if (String.IsNullOrEmpty(p.Name)) p.Name = "Playlist";
                    fromLibrary.Add(p);
                }
                if (fromLibrary.Count > 0) return fromLibrary;
            }
            catch (Exception ex) { Log.Write("Library list failed, using the playlist list: " + ex.Message); }
            return GetPlaylistsFromRootlist();
        }

        List<Playlist> GetPlaylistsFromRootlist()
        {
            string user = _engine.EnsureConnected().Username;
            HttpResult r = _sp.Sp("GET", "/playlist/v2/user/" + user + "/rootlist?decorate=revision%2Cattributes%2Clength%2Cowner%2Ccapabilities&from=0&length=200", null, null);
            if (!r.Ok) throw new InvalidOperationException("Couldn't load your playlists (HTTP " + r.Status + ")");
            Dictionary<int, List<ProtoField>> top = new ProtoReader(r.Body).ReadAll();
            List<Playlist> list = new List<Playlist>();
            ProtoField? contents = ProtoReader.First(top, 5);
            if (contents == null) return list;
            Dictionary<int, List<ProtoField>> c = contents.Value.Message.ReadAll();
            List<ProtoField> items = c.ContainsKey(3) ? c[3] : new List<ProtoField>();
            List<ProtoField> metas = c.ContainsKey(4) ? c[4] : new List<ProtoField>();
            for (int i = 0; i < items.Count; i++)
            {
                string uri = Str(items[i].Message.ReadAll(), 1);
                if (uri == null || uri.IndexOf(":playlist:", StringComparison.Ordinal) < 0) continue;   // folders etc.
                Playlist p = new Playlist();
                p.Id = LastPart(uri);
                if (i < metas.Count)
                {
                    Dictionary<int, List<ProtoField>> m = metas[i].Message.ReadAll();
                    ProtoField? at = ProtoReader.First(m, 2);
                    if (at != null) p.Name = Str(at.Value.Message.ReadAll(), 1);
                    p.Owner = Str(m, 5);
                }
                if (String.IsNullOrEmpty(p.Name)) p.Name = "Playlist";
                list.Add(p);
            }
            return list;
        }

        public void SetSaved(string uri, bool saved)
        {
            string method = saved ? "PUT" : "DELETE";
            HttpResult r = _sp.Api(method, "/me/library?uris=" + System.Uri.EscapeDataString(uri));
            if (!r.Ok && (r.Status == 404 || r.Status == 405 || r.Status == 400))
            {
                // older Web API shape
                string[] parts = uri.Split(':');
                string kind = parts.Length > 2 ? parts[1] : "", id = parts.Length > 2 ? parts[2] : "";
                string path = null;
                if (kind == "track") path = "/me/tracks?ids=" + id;
                else if (kind == "album") path = "/me/albums?ids=" + id;
                else if (kind == "artist") path = "/me/following?type=artist&ids=" + id;
                else if (kind == "playlist") path = "/playlists/" + id + "/followers";
                if (path != null) r = _sp.Api(method, path);
            }
            if (!r.Ok)
            {
                string detail = "";
                try { detail = r.Text != null && r.Text.Length > 0 ? " - " + (r.Text.Length > 160 ? r.Text.Substring(0, 160) : r.Text) : ""; } catch { }
                throw new InvalidOperationException("Spotify wouldn't " + (saved ? "save" : "remove") + " that (HTTP " + r.Status + ")" + detail);
            }
            Log.Write((saved ? "Saved " : "Removed ") + uri);
        }

        public string GetSongRadio(string trackId)
        {
            HttpResult r = _sp.Sp("GET", "/inspiredby-mix/v2/seed_to_playlist/spotify:track:" + trackId + "?response-format=json", null, null);
            if (!r.Ok) throw new InvalidOperationException("Spotify didn't make a radio for this song (HTTP " + r.Status + ")");
            JObject o = JObject.Parse(r.Text);
            JToken items = o["mediaItems"];
            string uri = items != null && items.HasValues ? (string)items[0]["uri"] : null;
            if (String.IsNullOrEmpty(uri)) throw new InvalidOperationException("Spotify didn't make a radio for this song");
            return uri.Substring(uri.LastIndexOf(':') + 1);
        }

        public Playlist GetPlaylist(string id)
        {
            HttpResult r = _sp.Sp("GET", "/playlist/v2/playlist/" + id, null, null);
            if (!r.Ok) throw new InvalidOperationException("Couldn't load the playlist (HTTP " + r.Status + ")");
            Dictionary<int, List<ProtoField>> top = new ProtoReader(r.Body).ReadAll();
            Playlist p = new Playlist();
            p.Id = id;
            p.Owner = Str(top, 16);
            ProtoField? at = ProtoReader.First(top, 3);
            Dictionary<int, List<ProtoField>> attrs = null;
            if (at != null)
            {
                Dictionary<int, List<ProtoField>> a = at.Value.Message.ReadAll();
                attrs = a;
                p.Name = Str(a, 1); p.Description = TextUtil.Clean(Str(a, 2));
            }
            List<string> ids = new List<string>();
            ProtoField? contents = ProtoReader.First(top, 5);
            if (contents != null)
            {
                Dictionary<int, List<ProtoField>> c = contents.Value.Message.ReadAll();
                if (c.ContainsKey(3))
                    foreach (ProtoField it in c[3])
                    {
                        string uri = Str(it.Message.ReadAll(), 1);
                        if (uri != null && uri.StartsWith("spotify:track:", StringComparison.Ordinal) && ids.Count < 500) ids.Add(LastPart(uri));
                    }
            }
            p.Tracks = FetchTracks(ids);
            string cover = attrs != null ? PlaylistCover(attrs) : null;
            if (cover != null && ImageExists(cover)) { p.ImageUrl = cover; Log.Write("Playlist cover: its own picture"); }
            else
            {
                Log.Write("Playlist cover: " + (cover == null ? "none set, using first song's art" : "picture unavailable, using first song's art"));
                if (p.Tracks.Count > 0) p.ImageUrl = p.Tracks[0].AlbumImageUrl;
            }
            return p;
        }

        /// <summary>The playlist's own cover: a ready-made image URL in its attributes, or the 20-byte image id in field 3.</summary>
        static string PlaylistCover(Dictionary<int, List<ProtoField>> attrs)
        {
            foreach (KeyValuePair<int, List<ProtoField>> kv in attrs)
                foreach (ProtoField f in kv.Value)
                {
                    if (f.Data == null || f.Data.Length < 12 || f.Data.Length > 300) continue;
                    ProtoReader inner = new ProtoReader(f.Data);
                    string direct = f.Text;
                    if (direct.StartsWith("https://i.scdn.co/image/", StringComparison.Ordinal) || direct.StartsWith("https://mosaic.scdn.co/", StringComparison.Ordinal)) return direct;
                    try
                    {
                        foreach (KeyValuePair<int, List<ProtoField>> kv2 in inner.ReadAll())
                            foreach (ProtoField g in kv2.Value)
                            {
                                string u = g.Data == null ? "" : g.Text;
                                if (u.StartsWith("https://i.scdn.co/image/", StringComparison.Ordinal) || u.StartsWith("https://mosaic.scdn.co/", StringComparison.Ordinal)) return u;
                            }
                    }
                    catch { }
                }
            ProtoField? pic = ProtoReader.First(attrs, 3);
            if (pic != null && pic.Value.Data != null && pic.Value.Data.Length == 20) return "https://i.scdn.co/image/" + Spclient.Hex(pic.Value.Data);
            return null;
        }

        static bool ImageExists(string url)
        {
            try { HttpResult r = Native.Http("GET", url, "Range: bytes=0-0\r\n", null); return r.Status == 200 || r.Status == 206; }
            catch { return false; }
        }

        // ---------- albums + artists ----------

        public Album GetAlbum(string id)
        {
            Dictionary<string, byte[]> meta = _sp.ExtMeta(9, new string[] { Uri("album", id) });
            byte[] b;
            if (!meta.TryGetValue(Uri("album", id), out b)) throw new InvalidOperationException("Album not available");
            List<string> gids;
            Album a = ParseAlbumHead(b, out gids);
            a.Id = id;
            a.Tracks = FetchTracks(gids);
            for (int i = 0; i < a.Tracks.Count; i++)
            {
                Track t = a.Tracks[i];
                t.AlbumId = id; t.AlbumName = a.Name; t.AlbumImageUrl = a.ImageUrl;
                if (t.Number <= 0) t.Number = i + 1;
            }
            return a;
        }

        public ArtistPage GetArtist(string id)
        {
            Dictionary<string, byte[]> meta = _sp.ExtMeta(8, new string[] { Uri("artist", id) });
            byte[] b;
            if (!meta.TryGetValue(Uri("artist", id), out b)) throw new InvalidOperationException("Artist not available");
            Dictionary<int, List<ProtoField>> d = new ProtoReader(b).ReadAll();
            ArtistPage page = new ArtistPage();
            Artist art = new Artist();
            art.Id = id; art.Name = Str(d, 2);
            art.ImageUrl = GroupImage(d, 17, 11, 300);
            art.BannerUrl = GroupImage(d, 17, 11, 640);
            try
            {
                JObject v = new JObject(); v["uri"] = Uri("artist", id);
                JObject data = _sp.Pathfinder("queryArtistOverview", HashArtistOverview, v);
                string hdr = Src(data.SelectToken("artistUnion.headerImage.data.sources"), 1500);
                if (hdr != null) art.BannerUrl = hdr;
            }
            catch (Exception ex) { Log.Write("Artist header image: " + ex.Message); }
            page.Artist = art;

            // popular tracks: the group for our country, else the first
            List<string> top = new List<string>();
            if (d.ContainsKey(4))
            {
                ProtoField chosen = d[4][0];
                foreach (ProtoField tt in d[4]) if (Str(tt.Message.ReadAll(), 1) == _sp.Country) { chosen = tt; break; }
                Dictionary<int, List<ProtoField>> cd = chosen.Message.ReadAll();
                if (cd.ContainsKey(2))
                    foreach (ProtoField tf in cd[2])
                    {
                        ProtoField? g = ProtoReader.First(tf.Message.ReadAll(), 1);
                        if (g != null && g.Value.Data.Length == 16 && top.Count < 10) top.Add(Base62(g.Value.Data));
                    }
            }
            page.Popular = FetchTracks(top);

            List<string> albumIds = GroupGids(d, 5), singleIds = GroupGids(d, 6);
            if (albumIds.Count > 12) albumIds.RemoveRange(12, albumIds.Count - 12);
            if (singleIds.Count > 12) singleIds.RemoveRange(12, singleIds.Count - 12);
            List<string> uris = new List<string>();
            foreach (string x in albumIds) uris.Add(Uri("album", x));
            foreach (string x in singleIds) uris.Add(Uri("album", x));
            Dictionary<string, byte[]> am = _sp.ExtMeta(9, uris);
            foreach (string x in albumIds) AddAlbum(page.Albums, am, x);
            foreach (string x in singleIds) AddAlbum(page.Singles, am, x);
            Comparison<Album> byYear = delegate (Album p, Album q) { return q.Year.CompareTo(p.Year); };
            page.Albums.Sort(byYear); page.Singles.Sort(byYear);

            if (d.ContainsKey(15))
                foreach (ProtoField rf in d[15])
                {
                    Artist ra = ParseArtistRef(rf);
                    if (ra.Id != null && ra.Name != null && page.Related.Count < 8) page.Related.Add(ra);
                }
            return page;
        }

        void AddAlbum(List<Album> into, Dictionary<string, byte[]> meta, string id)
        {
            byte[] b;
            if (!meta.TryGetValue(Uri("album", id), out b)) return;
            List<string> ignore;
            try { Album a = ParseAlbumHead(b, out ignore); a.Id = id; into.Add(a); } catch (Exception ex) { Log.Write("Bad album metadata: " + ex.Message); }
        }

        // ---------- search ----------

        public SearchResults Search(string query)
        {
            JObject v = new JObject();
            v["searchTerm"] = query; v["offset"] = 0; v["limit"] = 10; v["numberOfTopResults"] = 5;
            v["includeAudiobooks"] = false; v["includeArtistHasConcertsField"] = false; v["includePreReleases"] = false; v["includeAuthors"] = false;
            JObject data = _sp.Pathfinder("searchDesktop", HashSearch, v);
            JToken s = data["searchV2"];
            SearchResults res = new SearchResults();
            res.Query = query;
            if (s == null) return res;
            JArray tr = s.SelectToken("tracksV2.items") as JArray;
            if (tr != null) foreach (JToken it in tr) { JToken td = it.SelectToken("item.data"); if (td != null) res.Tracks.Add(JsonTrack(td, null)); }
            JArray ar = s.SelectToken("artists.items") as JArray;
            if (ar != null) foreach (JToken it in ar) { JToken ad = it["data"]; if (ad != null) res.Artists.Add(JsonArtist(ad)); }
            JArray al = s.SelectToken("albumsV2.items") as JArray;
            if (al != null) foreach (JToken it in al) { JToken ad = it["data"]; if (ad != null) res.Albums.Add(JsonAlbum(ad)); }
            JArray pl = s.SelectToken("playlists.items") as JArray;
            if (pl != null) foreach (JToken it in pl) { JToken pd = it["data"]; if (pd != null) res.Playlists.Add(JsonPlaylist(pd)); }
            return res;
        }

        // ---------- library ----------

        public List<Track> GetSavedTracks()
        {
            List<Track> list = new List<Track>();
            int offset = 0, total = Int32.MaxValue;
            while (offset < total && offset < 500)
            {
                JObject v = new JObject(); v["limit"] = 50; v["offset"] = offset;
                JObject data = _sp.Pathfinder("fetchLibraryTracks", HashLibTracks, v);
                JToken tracks = data.SelectToken("me.library.tracks");
                if (tracks == null) break;
                JToken tc = tracks["totalCount"]; if (tc != null) total = (int)tc;
                JArray items = tracks["items"] as JArray;
                if (items == null || items.Count == 0) break;
                foreach (JToken it in items)
                {
                    JToken td = it.SelectToken("track.data");
                    if (td != null) list.Add(JsonTrack(td, J(it, "track._uri")));
                }
                offset += items.Count;
            }
            return list;
        }

        JArray LibraryItems(string filter)
        {
            JArray all = new JArray();
            for (int offset = 0; offset < 1000; offset += 50)
            {
                JObject v = new JObject();
                v["filters"] = new JArray(filter); v["order"] = null; v["textFilter"] = ""; v["features"] = new JArray();
                v["limit"] = 50; v["offset"] = offset; v["flatten"] = false; v["expandedFolders"] = new JArray();
                v["folderUri"] = null; v["includeFoldersWhenFlattening"] = true;
                JObject data = _sp.Pathfinder("libraryV3", HashLibrary, v);
                JArray page = data.SelectToken("me.libraryV3.items") as JArray;
                if (page == null || page.Count == 0) break;
                foreach (JToken t in page) all.Add(t);
                if (page.Count < 50) break;
            }
            return all;
        }

        public List<Album> GetSavedAlbums()
        {
            List<Album> list = new List<Album>();
            foreach (JToken it in LibraryItems("Albums")) { JToken d = it.SelectToken("item.data"); if (d != null && J(d, "uri") != null) list.Add(JsonAlbum(d)); }
            return list;
        }

        public List<Artist> GetFollowedArtists()
        {
            List<Artist> list = new List<Artist>();
            foreach (JToken it in LibraryItems("Artists")) { JToken d = it.SelectToken("item.data"); if (d != null && J(d, "uri") != null) list.Add(JsonArtist(d)); }
            return list;
        }

        // ---------- browse (home) ----------

        public List<BrowseSection> GetBrowse()
        {
            try
            {
                JObject v = new JObject(); v["timeZone"] = "UTC";
                JObject data = _sp.Pathfinder("home", HashHome, v);
                List<BrowseSection> list = new List<BrowseSection>();
                JArray sections = data.SelectToken("home.sectionContainer.sections.items") as JArray;
                if (sections != null)
                    foreach (JToken sec in sections)
                    {
                        BrowseSection bs = new BrowseSection();
                        bs.Title = J(sec, "data.title.text");
                        JArray items = sec.SelectToken("sectionItems.items") as JArray;
                        if (items == null || bs.Title == null) continue;
                        foreach (JToken it in items)
                        {
                            JToken d = it.SelectToken("content.data");
                            string uri = it["uri"] != null ? (string)it["uri"] : J(d, "uri");
                            if (d == null || uri == null) continue;
                            if (uri.IndexOf(":playlist:", StringComparison.Ordinal) > 0) bs.Playlists.Add(JsonPlaylist(d));
                            else if (uri.IndexOf(":album:", StringComparison.Ordinal) > 0) bs.Albums.Add(JsonAlbum(d));
                        }
                        if (bs.Albums.Count + bs.Playlists.Count > 0) list.Add(bs);
                    }
                if (list.Count > 0) return list;
            }
            catch (Exception ex) { Log.Write("Home feed failed, using demo browse: " + ex.Message); }
            return _mock.GetBrowse();
        }

        // ---------- images ----------

        public Bitmap LoadImage(string url)
        {
            if (url == null || !url.StartsWith("http", StringComparison.Ordinal)) return _mock.LoadImage(url);
            HttpResult r = Native.Get(url, null);
            if (!r.Ok || r.Body.Length == 0) return null;
            using (MemoryStream ms = new MemoryStream(r.Body))
            using (Image img = Image.FromStream(ms))
                return new Bitmap(img);
        }
    }

    /// <summary>Spotify sometimes sends HTML-escaped text (&amp;apos; &amp;amp; &amp;#39; ...). .NET 2.0 has no WebUtility, so this is a small decoder.</summary>
    public static class TextUtil
    {
        static readonly string[] Latin1 = ("nbsp iexcl cent pound curren yen brvbar sect uml copy ordf laquo not shy reg macr deg plusmn sup2 sup3 acute micro para middot cedil sup1 ordm raquo frac14 frac12 frac34 iquest " +
            "Agrave Aacute Acirc Atilde Auml Aring AElig Ccedil Egrave Eacute Ecirc Euml Igrave Iacute Icirc Iuml ETH Ntilde Ograve Oacute Ocirc Otilde Ouml times Oslash Ugrave Uacute Ucirc Uuml Yacute THORN szlig " +
            "agrave aacute acirc atilde auml aring aelig ccedil egrave eacute ecirc euml igrave iacute icirc iuml eth ntilde ograve oacute ocirc otilde ouml divide oslash ugrave uacute ucirc uuml yacute thorn yuml").Split(' ');
        static readonly Dictionary<string, char> Named = BuildNamed();

        static Dictionary<string, char> BuildNamed()
        {
            Dictionary<string, char> d = new Dictionary<string, char>();
            d["amp"] = '&'; d["lt"] = '<'; d["gt"] = '>'; d["quot"] = '"'; d["apos"] = '\'';
            d["hellip"] = '\u2026'; d["ndash"] = '\u2013'; d["mdash"] = '\u2014'; d["lsquo"] = '\u2018'; d["rsquo"] = '\u2019';
            d["ldquo"] = '\u201C'; d["rdquo"] = '\u201D'; d["bull"] = '\u2022'; d["trade"] = '\u2122'; d["euro"] = '\u20AC';
            for (int i = 0; i < Latin1.Length; i++) d[Latin1[i]] = (char)(160 + i);
            return d;
        }

        /// <summary>Decodes &amp;name; and &amp;#123; / &amp;#x1F; entities (repeats, in case the text was escaped twice).</summary>
        public static string Decode(string s)
        {
            for (int pass = 0; pass < 3 && s != null && s.IndexOf('&') >= 0; pass++)
            {
                StringBuilder sb = new StringBuilder(s.Length);
                bool changed = false;
                for (int i = 0; i < s.Length; i++)
                {
                    char ch = s[i];
                    if (ch == '&')
                    {
                        int semi = s.IndexOf(';', i + 1);
                        if (semi > i + 1 && semi - i <= 10)
                        {
                            string name = s.Substring(i + 1, semi - i - 1);
                            string decoded = null;
                            if (name[0] == '#')
                            {
                                int code;
                                bool ok = name.Length > 2 && (name[1] == 'x' || name[1] == 'X')
                                    ? Int32.TryParse(name.Substring(2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out code)
                                    : Int32.TryParse(name.Substring(1), out code);
                                if (ok && code > 0 && code < 0x110000) decoded = Char.ConvertFromUtf32(code);
                            }
                            else { char n; if (Named.TryGetValue(name, out n)) decoded = n.ToString(); }
                            if (decoded != null) { sb.Append(decoded); i = semi; changed = true; continue; }
                        }
                    }
                    sb.Append(ch);
                }
                s = sb.ToString();
                if (!changed) break;
            }
            return s;
        }

        /// <summary>For descriptions: removes tags like &lt;a href=...&gt; and decodes entities.</summary>
        public static string Clean(string s)
        {
            if (s == null) return null;
            if (s.IndexOf('<') >= 0) s = System.Text.RegularExpressions.Regex.Replace(s, "<[^>]*>", "");
            return Decode(s).Trim();
        }
    }
}