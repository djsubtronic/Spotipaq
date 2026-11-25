// Spclient.cs - bearer token (Login5 + hashcash), client token, and authenticated calls to Spotify's
// web services (spclient, pathfinder). Port of librespot-java's Login5Api / ApiClient.
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Spotipaq.Engine
{
    /// <summary>
    /// Where one page load's time went, for the log: one line per page (MainForm.LoadPage), e.g.
    ///   Loaded playlist in 2410 ms: token 2140, playlist 180, details 5 x 300 = 1500, details parse 40, other 50
    ///   ("Daily Mix 6", 50 songs; token renewed in 2130 ms, session reconnected)
    /// The parts never overlap, so they add up to the total; "other" is everything not measured separately.
    /// Collected on the thread doing the load; calls on other threads (artwork, playback) aren't counted.
    /// </summary>
    public sealed class LoadTimer
    {
        [ThreadStatic] static LoadTimer _current;
        readonly string _what;
        readonly long _start = Now;
        readonly List<string> _order = new List<string>();
        readonly Dictionary<string, double> _ms = new Dictionary<string, double>();
        readonly Dictionary<string, int> _count = new Dictionary<string, int>();
        readonly List<string> _notes = new List<string>();

        LoadTimer(string what) { _what = what; }

        /// <summary>Starts timing a page load on this thread.</summary>
        public static LoadTimer Start(string what) { _current = new LoadTimer(what); return _current; }

        public static long Now { get { return System.Diagnostics.Stopwatch.GetTimestamp(); } }
        public static double Ms(long since) { return (Now - since) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; }

        /// <summary>Adds the time since `since` (a Now value) to a part of the load being timed on this thread, if any.</summary>
        public static void Add(string part, long since)
        {
            LoadTimer t = _current;
            if (t == null) return;
            double ms = Ms(since), had;
            if (t._ms.TryGetValue(part, out had)) { t._ms[part] = had + ms; t._count[part]++; }
            else { t._ms[part] = ms; t._count[part] = 1; t._order.Add(part); }
        }

        /// <summary>Adds a remark to the line (what was loaded, a token renewal...).</summary>
        public static void Note(string text) { LoadTimer t = _current; if (t != null) t._notes.Add(text); }

        /// <summary>Writes the line and stops timing.</summary>
        public void Finish(bool ok)
        {
            if (_current == this) _current = null;
            double total = Ms(_start), accounted = 0;
            StringBuilder sb = new StringBuilder();
            sb.Append(ok ? "Loaded " : "Failed loading ").Append(_what).Append(" in ").Append((int)total).Append(" ms");
            string sep = ": ";
            foreach (string p in _order)
            {
                int n = _count[p];
                sb.Append(sep).Append(p).Append(' ');
                if (n > 1) sb.Append(n).Append(" x ").Append((int)(_ms[p] / n)).Append(" = ");
                sb.Append((int)_ms[p]);
                accounted += _ms[p];
                sep = ", ";
            }
            if (_order.Count > 0) sb.Append(", other ").Append((int)Math.Max(0, total - accounted));
            if (_notes.Count > 0) sb.Append(" (").Append(String.Join("; ", _notes.ToArray())).Append(')');
            Log.Write(sb.ToString());
        }
    }

    /// <summary>Small facts kept between runs so they needn't be fetched before the first page can load: the
    /// country, and which Spotify servers to use. One small file each in the settings folder.</summary>
    static class Remembered
    {
        static readonly object Lock = new object();
        static readonly Dictionary<string, string> Cache = new Dictionary<string, string>();
        static readonly Dictionary<string, bool> WrittenThisRun = new Dictionary<string, bool>();

        static string PathOf(string key) { return Path.Combine(Log.Folder, "remember-" + key + ".txt"); }

        /// <summary>The value, if one was saved no longer than maxAge ago (TimeSpan.MaxValue = any age); else null.</summary>
        public static string Get(string key, TimeSpan maxAge)
        {
            lock (Lock)
            {
                try
                {
                    string p = PathOf(key);
                    if (!File.Exists(p)) return null;
                    if (maxAge != TimeSpan.MaxValue && DateTime.UtcNow - File.GetLastWriteTimeUtc(p) > maxAge) return null;
                    string v;
                    if (!Cache.TryGetValue(key, out v)) { v = File.ReadAllText(p).Trim(); Cache[key] = v; }
                    return v.Length > 0 ? v : null;
                }
                catch (Exception) { return null; }
            }
        }

        /// <summary>Saves a value (once per run when it hasn't changed, which also renews its age).</summary>
        public static void Set(string key, string value)
        {
            lock (Lock)
            {
                string had;
                if (WrittenThisRun.ContainsKey(key) && Cache.TryGetValue(key, out had) && had == value) return;
                Cache[key] = value;
                WrittenThisRun[key] = true;
                try
                {
                    if (!Directory.Exists(Log.Folder)) Directory.CreateDirectory(Log.Folder);
                    File.WriteAllText(PathOf(key), value);
                }
                catch (Exception) { }
            }
        }

        public static void Forget(string key)
        {
            lock (Lock)
            {
                Cache.Remove(key);
                try { File.Delete(PathOf(key)); } catch (Exception) { }
            }
        }
    }

    public sealed class Spclient
    {
        const string ClientId = OAuthLogin.ClientId;
        const string ClientVersion = "1.2.40.599.g606b7f29";

        readonly SpotifyEngine _engine;
        readonly object _lock = new object();
        readonly object _renewLock = new object();      // one renewal at a time; the old token stays usable meanwhile
        string _token, _clientToken, _base;
        bool _baseRemembered;                           // _base came from the last run (see SpBase)
        DateTime _tokenExpires = DateTime.MinValue;
        System.Threading.Timer _renewTimer;

        public Spclient(SpotifyEngine engine) { _engine = engine; }

        public static string Hex(byte[] b)
        {
            StringBuilder sb = new StringBuilder(b.Length * 2);
            foreach (byte x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        /// <summary>The account's country (it decides which songs are available): from the main session, or until that
        /// has connected, as it was last time - pages no longer wait for the session.</summary>
        public string Country
        {
            get
            {
                ApSession s = _engine.Session;
                if (s != null && !String.IsNullOrEmpty(s.CountryCode)) { Remembered.Set("country", s.CountryCode); return s.CountryCode; }
                return Remembered.Get("country", TimeSpan.MaxValue) ?? "US";
            }
        }

        // ---- tokens ----

        public string Token()
        {
            lock (_lock) { if (_token != null && DateTime.UtcNow < _tokenExpires) return _token; }
            lock (_renewLock)
            {
                lock (_lock) { if (_token != null && DateTime.UtcNow < _tokenExpires) return _token; }   // renewed meanwhile
                return Renew();
            }
        }

        /// <summary>Gets a new access token (callers hold _renewLock). The saved login is all Login5 needs, so this
        /// doesn't wait for the main session (playback's connection, slow to set up): that connects in the background.
        /// Only a login that isn't a reusable one yet (just after the first login) needs the session first.</summary>
        string Renew()
        {
            {
                long renew = LoadTimer.Now;
                LoginCredentials c = CredentialStore.Load();
                string user;
                if (c != null && c.Type == AuthType.StoredSpotifyCredentials && !String.IsNullOrEmpty(c.Username))
                {
                    user = c.Username;
                    ConnectInBackground();
                }
                else
                {
                    ApSession before = _engine.Session;
                    bool wasUp = before != null && before.Connected;
                    ApSession ap = _engine.EnsureConnected();
                    if (!wasUp) LoadTimer.Note("session connected in " + (int)LoadTimer.Ms(renew) + " ms");
                    c = ap.ReusableCredentials;
                    if (c == null) c = CredentialStore.Load();
                    if (c == null) throw new InvalidOperationException("No stored credentials");
                    user = String.IsNullOrEmpty(c.Username) ? ap.Username : c.Username;
                }

                ProtoWriter stored = new ProtoWriter().String(1, user).Bytes(2, c.AuthData);
                byte[] resp = Login5(null, null, stored);
                Dictionary<int, List<ProtoField>> r = new ProtoReader(resp).ReadAll();
                if (r.ContainsKey(3))
                {
                    byte[] ctx = ProtoReader.First(r, 5).Value.Data;
                    Dictionary<int, List<ProtoField>> chs = ProtoReader.First(r, 3).Value.Message.ReadAll();
                    Dictionary<int, List<ProtoField>> ch = ProtoReader.First(chs, 1).Value.Message.ReadAll();
                    Dictionary<int, List<ProtoField>> hc = ProtoReader.First(ch, 1).Value.Message.ReadAll();
                    byte[] prefix = ProtoReader.First(hc, 1).Value.Data;
                    DateTime t0 = DateTime.UtcNow;
                    byte[] suffix = SolveHashcash(prefix, ctx);
                    TimeSpan took = DateTime.UtcNow - t0;
                    ProtoWriter dur = new ProtoWriter().Int(1, (long)took.TotalSeconds).Int(2, (long)(took.Ticks % 10000000L) * 100);
                    ProtoWriter sol = new ProtoWriter().Message(1, new ProtoWriter().Bytes(1, suffix).Message(2, dur));
                    ProtoWriter sols = new ProtoWriter().Message(1, sol);
                    resp = Login5(ctx, sols, stored);
                    r = new ProtoReader(resp).ReadAll();
                }
                ProtoField? ok = ProtoReader.First(r, 1);
                if (ok == null)
                {
                    ProtoField? err = ProtoReader.First(r, 2);
                    throw new InvalidOperationException("Login5 refused the stored login (error " + (err == null ? -1 : (int)err.Value.Int) + ")");
                }
                Dictionary<int, List<ProtoField>> okf = ok.Value.Message.ReadAll();
                string token = ProtoReader.First(okf, 2).Value.Text;
                ProtoField? ex = ProtoReader.First(okf, 4);
                int secs = ex == null ? 3600 : (int)ex.Value.Int;
                lock (_lock) { _token = token; _tokenExpires = DateTime.UtcNow.AddSeconds(Math.Max(60, secs - 120)); }
                Log.Write("Got access token (valid " + secs + "s)");
                LoadTimer.Note("token renewed in " + (int)LoadTimer.Ms(renew) + " ms");
                ScheduleRenewal(secs);
                return token;
            }
        }

        // The next token is fetched in the background 5 minutes before this one runs out, so no page ever waits for a
        // renewal (pages keep using the current token meanwhile).
        void ScheduleRenewal(int secs)
        {
            long due = Math.Max(30, secs - 300) * 1000L;
            System.Threading.Timer old = _renewTimer;
            _renewTimer = new System.Threading.Timer(delegate
            {
                try { lock (_renewLock) Renew(); }
                catch (Exception e) { Log.Write("Background token renewal failed: " + e.Message); }
            }, null, due, System.Threading.Timeout.Infinite);
            if (old != null) old.Dispose();
        }

        // the main session isn't needed for pages, but playback will want it: started now, off the page's path
        void ConnectInBackground()
        {
            ApSession s = _engine.Session;
            if (s != null && s.Connected) return;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try { _engine.EnsureConnected(); }
                catch (Exception e) { Log.Write("Connecting the session in the background failed: " + e.Message); }
            });
        }

        byte[] Login5(byte[] loginContext, ProtoWriter solutions, ProtoWriter stored)
        {
            ProtoWriter w = new ProtoWriter();
            w.Message(1, new ProtoWriter().String(1, ClientId).String(2, _engine.DeviceId));
            if (loginContext != null) w.Bytes(2, loginContext);
            if (solutions != null) w.Message(3, solutions);
            w.Message(100, stored);
            HttpResult r = Native.Http("POST", "https://login5.spotify.com/v3/login",
                "Content-Type: application/x-protobuf\r\nAccept: application/x-protobuf\r\n", w.ToArray());
            if (!r.Ok) throw new InvalidOperationException("Login5 HTTP " + r.Status);
            return r.Body;
        }

        static void Inc(byte[] ctr, int index)
        {
            ctr[index]++;
            if (ctr[index] == 0 && index != 0) Inc(ctr, index - 1);
        }

        static byte[] SolveHashcash(byte[] prefix, byte[] loginContext)
        {
            SHA1 sha = SHA1.Create();
            byte[] ctxHash = sha.ComputeHash(loginContext);
            byte[] suffix = new byte[16];
            Array.Copy(ctxHash, 12, suffix, 0, 8);
            byte[] buf = new byte[prefix.Length + 16];
            Array.Copy(prefix, buf, prefix.Length);
            while (true)
            {
                Array.Copy(suffix, 0, buf, prefix.Length, 16);
                byte[] d = sha.ComputeHash(buf);
                if (d[19] == 0 && (d[18] & 3) == 0) return suffix;
                Inc(suffix, 15);
                Inc(suffix, 7);
            }
        }

        string ClientToken()
        {
            lock (_lock)
            {
                if (_clientToken != null) return _clientToken;
                ProtoWriter win = new ProtoWriter().Int(1, 10).Int(3, 21370).Int(4, 2).Int(6, 9).Int(7, 332).Int(8, 34404).Bool(10, true);
                ProtoWriter sdk = new ProtoWriter()
                    .Message(1, new ProtoWriter().Message(4, win))
                    .String(2, _engine.DeviceId);
                ProtoWriter data = new ProtoWriter().String(1, ClientVersion).String(2, ClientId).Message(3, sdk);
                ProtoWriter req = new ProtoWriter().Int(1, 1).Message(2, data);
                HttpResult r = Native.Http("POST", "https://clienttoken.spotify.com/v1/clienttoken",
                    "Accept: application/x-protobuf\r\nContent-Type: application/x-protobuf\r\n", req.ToArray());
                if (!r.Ok) throw new InvalidOperationException("Client token HTTP " + r.Status);
                Dictionary<int, List<ProtoField>> top = new ProtoReader(r.Body).ReadAll();
                ProtoField? granted = ProtoReader.First(top, 2);
                if (granted == null) throw new InvalidOperationException("Spotify didn't grant a client token");
                _clientToken = ProtoReader.First(granted.Value.Message.ReadAll(), 1).Value.Text;
                return _clientToken;
            }
        }

        string SpBase()
        {
            lock (_lock)
            {
                if (_base != null) return _base;
                // the server last time, without asking (a page would wait for the answer); asked again if it fails
                string known = Remembered.Get("spclient", TimeSpan.FromDays(3));
                if (known != null) { _base = "https://" + known; _baseRemembered = true; return _base; }
                long t = LoadTimer.Now;
                string host = "spclient.wg.spotify.com:443";
                try
                {
                    HttpResult r = Native.Get("https://apresolve.spotify.com/?type=spclient", null);
                    if (r.Ok)
                    {
                        JArray a = (JArray)JObject.Parse(r.Text)["spclient"];
                        if (a != null && a.Count > 0) { host = (string)a[0]; Remembered.Set("spclient", host); }
                    }
                }
                catch (Exception ex) { Log.Write("spclient resolve failed: " + ex.Message); }
                _base = "https://" + host;
                LoadTimer.Add("server lookup", t);
                return _base;
            }
        }

        string AuthHeaders(bool json)
        {
            long t = LoadTimer.Now;                     // includes waiting for another thread's renewal
            string h = "Authorization: Bearer " + Token() + "\r\nclient-token: " + ClientToken() + "\r\n" +
                (json ? "Accept: application/json\r\n" : "");
            LoadTimer.Add("token", t);
            return h;
        }

        // the log's name for a spclient request (LoadTimer)
        static string Part(string path)
        {
            if (path.StartsWith("/playlist/", StringComparison.Ordinal)) return "playlist";
            if (path.StartsWith("/extended-metadata/", StringComparison.Ordinal)) return "details";
            return "spclient";
        }

        // ---- calls ----

        /// <summary>Request to spclient (path starts with /). Retries once with fresh tokens after a 401.</summary>
        public HttpResult Sp(string method, string path, byte[] body, string contentType)
        {
            for (int attempt = 0; ; attempt++)
            {
                string h = AuthHeaders(false) + (contentType != null ? "Content-Type: " + contentType + "\r\n" : "");
                string url = SpBase() + path;
                long t = LoadTimer.Now;
                HttpResult r;
                try { r = Native.Http(method, url, h, body); }
                catch (IOException)
                {
                    // can't reach the server remembered from last time: ask which one to use, and try that
                    if (!ForgetRememberedServer()) throw;
                    continue;
                }
                LoadTimer.Add(Part(path), t);
                if (r.Status == 401 && attempt == 0) { lock (_lock) { _token = null; _clientToken = null; } continue; }
                return r;
            }
        }

        bool ForgetRememberedServer()
        {
            lock (_lock)
            {
                if (!_baseRemembered) return false;
                Log.Write("Remembered spclient server unreachable; asking again");
                _base = null; _baseRemembered = false;
                Remembered.Forget("spclient");
                return true;
            }
        }

        /// <summary>Request to the public Web API (https://api.spotify.com/v1 + pathAndQuery) with the bearer token.</summary>
        public HttpResult Api(string method, string pathAndQuery)
        {
            for (int attempt = 0; ; attempt++)
            {
                long tt = LoadTimer.Now;
                string h = "Authorization: Bearer " + Token() + "\r\nAccept: application/json\r\n";
                LoadTimer.Add("token", tt);
                long t = LoadTimer.Now;
                HttpResult r = Native.Http(method, "https://api.spotify.com/v1" + pathAndQuery, h, method == "GET" ? null : new byte[0]);
                LoadTimer.Add("web api", t);
                if (r.Status == 401 && attempt == 0) { lock (_lock) { _token = null; } continue; }
                return r;
            }
        }

        /// <summary>Pathfinder persisted query; returns the "data" object.</summary>
        public JObject Pathfinder(string operation, string hash, JObject variables)
        {
            JObject body = new JObject();
            body["operationName"] = operation;
            body["variables"] = variables;
            body["extensions"] = new JObject(new JProperty("persistedQuery",
                new JObject(new JProperty("version", 1), new JProperty("sha256Hash", hash))));
            byte[] bytes = Encoding.UTF8.GetBytes(body.ToString(Newtonsoft.Json.Formatting.None));
            for (int attempt = 0; ; attempt++)
            {
                string h = AuthHeaders(true) + "Content-Type: application/json;charset=UTF-8\r\napp-platform: Win32\r\nAccept-Language: en\r\n";
                long t = LoadTimer.Now;
                HttpResult r = Native.Http("POST", "https://api-partner.spotify.com/pathfinder/v2/query", h, bytes);
                LoadTimer.Add("pathfinder", t);
                if (r.Status == 401 && attempt == 0) { lock (_lock) { _token = null; _clientToken = null; } continue; }
                if (!r.Ok) throw new InvalidOperationException(operation + " failed (HTTP " + r.Status + ")");
                JObject root = JObject.Parse(r.Text);
                JToken errors = root["errors"];
                if (errors != null && errors.Type != JTokenType.Null &&
                    (errors.Type != JTokenType.Array || ((JArray)errors).Count > 0))
                    throw new InvalidOperationException(operation + " rejected by Spotify: " +
                        errors.ToString(Newtonsoft.Json.Formatting.None));

                JToken result = root["data"];
                if (result == null || result.Type == JTokenType.Null)
                    throw new InvalidOperationException(operation +
                        " returned no data. Response: " +
                        (r.Text.Length > 1000 ? r.Text.Substring(0, 1000) : r.Text));
                JObject resultObject = result as JObject;
                if (resultObject == null)
                    throw new InvalidOperationException(operation +
                        " returned unexpected data: " + result.ToString(Newtonsoft.Json.Formatting.None));
                return resultObject;
            }
        }

        /// <summary>Batched extended metadata. kind: ARTIST_V4=8, ALBUM_V4=9, TRACK_V4=10. Key = spotify uri.</summary>
        public Dictionary<string, byte[]> ExtMeta(int kind, IList<string> uris) { return ExtMeta(kind, uris, true); }

        /// <param name="header">false = send no country/catalogue header (what librespot does for tracks).</param>
        public Dictionary<string, byte[]> ExtMeta(int kind, IList<string> uris, bool header)
        {
            Dictionary<string, byte[]> result = new Dictionary<string, byte[]>();
            for (int start = 0; start < uris.Count; start += 100)
            {
                ProtoWriter req = new ProtoWriter();
                if (header) req.Message(1, new ProtoWriter().String(1, Country).String(2, "premium"));
                for (int i = start; i < uris.Count && i < start + 100; i++)
                    req.Message(2, new ProtoWriter().String(1, uris[i]).Message(2, new ProtoWriter().Int(1, kind)));
                HttpResult r = Sp("POST", "/extended-metadata/v0/extended-metadata", req.ToArray(), "application/x-protobuf");
                if (!r.Ok) throw new InvalidOperationException("Metadata request failed (HTTP " + r.Status + ")");
                long parse = LoadTimer.Now;
                foreach (ProtoField arr in new ProtoReader(r.Body).ReadAll().ContainsKey(2) ? new ProtoReader(r.Body).ReadAll()[2] : new List<ProtoField>())
                {
                    Dictionary<int, List<ProtoField>> a = arr.Message.ReadAll();
                    if (!a.ContainsKey(3)) continue;
                    foreach (ProtoField ed in a[3])
                    {
                        Dictionary<int, List<ProtoField>> e = ed.Message.ReadAll();
                        ProtoField? hdr = ProtoReader.First(e, 1);
                        if (hdr != null)
                        {
                            ProtoField? sc = ProtoReader.First(hdr.Value.Message.ReadAll(), 1);
                            if (sc != null && sc.Value.Int != 200) continue;
                        }
                        ProtoField? uri = ProtoReader.First(e, 2), any = ProtoReader.First(e, 3);
                        if (uri == null || any == null) continue;
                        ProtoField? val = ProtoReader.First(any.Value.Message.ReadAll(), 2);
                        if (val != null) result[uri.Value.Text] = val.Value.Data;
                    }
                }
                LoadTimer.Add("details parse", parse);
            }
            return result;
        }
    }
}