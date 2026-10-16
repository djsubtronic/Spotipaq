// Spclient.cs - bearer token (Login5 + hashcash), client token, and authenticated calls to Spotify's
// web services (spclient, pathfinder). Port of librespot-java's Login5Api / ApiClient.
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Spotipaq.Engine
{
    public sealed class Spclient
    {
        const string ClientId = OAuthLogin.ClientId;
        const string ClientVersion = "1.2.40.599.g606b7f29";

        readonly SpotifyEngine _engine;
        readonly object _lock = new object();
        string _token, _clientToken, _base;
        DateTime _tokenExpires = DateTime.MinValue;

        public Spclient(SpotifyEngine engine) { _engine = engine; }

        public static string Hex(byte[] b)
        {
            StringBuilder sb = new StringBuilder(b.Length * 2);
            foreach (byte x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        public string Country
        {
            get { ApSession s = _engine.Session; return s != null && !String.IsNullOrEmpty(s.CountryCode) ? s.CountryCode : "US"; }
        }

        // ---- tokens ----

        public string Token()
        {
            lock (_lock)
            {
                if (_token != null && DateTime.UtcNow < _tokenExpires) return _token;
                _token = null;
                ApSession ap = _engine.EnsureConnected();
                LoginCredentials c = ap.ReusableCredentials;
                if (c == null) c = CredentialStore.Load();
                if (c == null) throw new InvalidOperationException("No stored credentials");
                string user = String.IsNullOrEmpty(c.Username) ? ap.Username : c.Username;

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
                _token = ProtoReader.First(okf, 2).Value.Text;
                ProtoField? ex = ProtoReader.First(okf, 4);
                int secs = ex == null ? 3600 : (int)ex.Value.Int;
                _tokenExpires = DateTime.UtcNow.AddSeconds(Math.Max(60, secs - 120));
                Log.Write("Got access token (valid " + secs + "s)");
                return _token;
            }
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
                string host = "spclient.wg.spotify.com:443";
                try
                {
                    HttpResult r = Native.Get("https://apresolve.spotify.com/?type=spclient", null);
                    if (r.Ok)
                    {
                        JArray a = (JArray)JObject.Parse(r.Text)["spclient"];
                        if (a != null && a.Count > 0) host = (string)a[0];
                    }
                }
                catch (Exception ex) { Log.Write("spclient resolve failed: " + ex.Message); }
                _base = "https://" + host;
                return _base;
            }
        }

        string AuthHeaders(bool json)
        {
            return "Authorization: Bearer " + Token() + "\r\nclient-token: " + ClientToken() + "\r\n" +
                (json ? "Accept: application/json\r\n" : "");
        }

        // ---- calls ----

        /// <summary>Request to spclient (path starts with /). Retries once with fresh tokens after a 401.</summary>
        public HttpResult Sp(string method, string path, byte[] body, string contentType)
        {
            for (int attempt = 0; ; attempt++)
            {
                string h = AuthHeaders(false) + (contentType != null ? "Content-Type: " + contentType + "\r\n" : "");
                HttpResult r = Native.Http(method, SpBase() + path, h, body);
                if (r.Status == 401 && attempt == 0) { lock (_lock) { _token = null; _clientToken = null; } continue; }
                return r;
            }
        }

        /// <summary>Request to the public Web API (https://api.spotify.com/v1 + pathAndQuery) with the bearer token.</summary>
        public HttpResult Api(string method, string pathAndQuery)
        {
            for (int attempt = 0; ; attempt++)
            {
                string h = "Authorization: Bearer " + Token() + "\r\nAccept: application/json\r\n";
                HttpResult r = Native.Http(method, "https://api.spotify.com/v1" + pathAndQuery, h, method == "GET" ? null : new byte[0]);
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
                HttpResult r = Native.Http("POST", "https://api-partner.spotify.com/pathfinder/v2/query", h, bytes);
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
            }
            return result;
        }
    }
}
