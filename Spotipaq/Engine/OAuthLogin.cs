// OAuthLogin.cs - "Log in with Spotify" through a browser (PKCE), like librespot-java's OAuth flow.
// Spotify redirects the browser to http://127.0.0.1:5588/login?code=...; we listen there for it.
// If the browser runs on ANOTHER machine, the user can paste that final address in instead.
using System;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace Spotipaq.Engine
{
    public sealed class OAuthLogin : IDisposable
    {
        // Spotify's own desktop client id + the loopback redirect it allows (what librespot-java uses).
        public const string ClientId = "65b708073fc0480ea92a077233ca87bd";
        public const string RedirectUri = "http://127.0.0.1:5588/login";
        const string Scopes = "app-remote-control+playlist-modify+playlist-modify-private+playlist-modify-public+playlist-read+" +
            "playlist-read-collaborative+playlist-read-private+streaming+ugc-image-upload+user-follow-modify+user-follow-read+" +
            "user-library-modify+user-library-read+user-modify+user-modify-playback-state+user-modify-private+user-personalized+" +
            "user-read-birthdate+user-read-currently-playing+user-read-email+user-read-play-history+user-read-playback-position+" +
            "user-read-playback-state+user-read-private+user-read-recently-played+user-top-read";

        readonly string _verifier;
        public readonly string AuthUrl;
        TcpListener _listener;
        string _code;
        readonly ManualResetEvent _gotCode = new ManualResetEvent(false);

        public OAuthLogin()
        {
            const string possible = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
            byte[] rnd = new byte[128];
            RandomNumberGenerator.Create().GetBytes(rnd);
            StringBuilder sb = new StringBuilder();
            foreach (byte b in rnd) sb.Append(possible[b % possible.Length]);
            _verifier = sb.ToString();
            byte[] hash;
            using (SHA256Managed sha = new SHA256Managed()) hash = sha.ComputeHash(Encoding.ASCII.GetBytes(_verifier));
            string challenge = Convert.ToBase64String(hash).Replace("=", "").Replace('+', '-').Replace('/', '_');
            AuthUrl = "https://accounts.spotify.com/authorize?response_type=code&client_id=" + ClientId +
                "&redirect_uri=" + Uri.EscapeDataString(RedirectUri) + "&code_challenge=" + challenge +
                "&code_challenge_method=S256&scope=" + Scopes;
        }

        /// <summary>Starts listening on 127.0.0.1:5588 for the browser's redirect.</summary>
        public void StartListening()
        {
            _listener = new TcpListener(IPAddress.Loopback, 5588);
            _listener.Start();
            Thread t = new Thread(Listen);
            t.IsBackground = true;
            t.Name = "OAuth callback";
            t.Start();
        }

        void Listen()
        {
            try
            {
                while (_code == null)
                {
                    using (TcpClient c = _listener.AcceptTcpClient())
                    using (NetworkStream s = c.GetStream())
                    {
                        s.ReadTimeout = 5000;
                        byte[] buf = new byte[8192];
                        int n = s.Read(buf, 0, buf.Length);
                        string req = Encoding.ASCII.GetString(buf, 0, Math.Max(0, n));
                        string firstLine = req.Split('\n')[0];
                        string code = ExtractCode(firstLine);
                        string html = code != null
                            ? "<html><body style='font-family:sans-serif;background:#181818;color:#fff'><h2>Spotipaq is logged in.</h2>You can close this window.</body></html>"
                            : "<html><body>Waiting for Spotify...</body></html>";
                        byte[] body = Encoding.UTF8.GetBytes(html);
                        byte[] head = Encoding.ASCII.GetBytes("HTTP/1.0 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                        s.Write(head, 0, head.Length);
                        s.Write(body, 0, body.Length);
                        if (code != null) { _code = code; _gotCode.Set(); }
                    }
                }
            }
            catch (Exception ex) { if (_code == null) Log.Write("OAuth listener stopped: " + ex.Message); }
        }

        /// <summary>Pulls code=... out of a request line or a pasted redirect address.</summary>
        public static string ExtractCode(string text)
        {
            if (String.IsNullOrEmpty(text)) return null;
            int i = text.IndexOf("code=", StringComparison.Ordinal);
            if (i < 0) return null;
            int start = i + 5, end = start;
            while (end < text.Length && text[end] != '&' && text[end] != ' ' && text[end] != '#' && text[end] != '\r' && text[end] != '\n') end++;
            return end > start ? Uri.UnescapeDataString(text.Substring(start, end - start)) : null;
        }

        public void SetCodeManually(string pastedAddress)
        {
            string code = ExtractCode(pastedAddress);
            if (code == null) throw new ArgumentException("That address doesn't contain a login code (\"code=...\").");
            _code = code;
            _gotCode.Set();
        }

        public bool WaitForCode(int ms) { return _gotCode.WaitOne(ms, false); }

        /// <summary>Exchanges the code for an access token (https via the native DLL).</summary>
        public string RequestToken()
        {
            string form = "grant_type=authorization_code&client_id=" + ClientId + "&redirect_uri=" + Uri.EscapeDataString(RedirectUri) +
                "&code=" + Uri.EscapeDataString(_code) + "&code_verifier=" + _verifier;
            HttpResult r = Native.PostForm("https://accounts.spotify.com/api/token", form);
            if (!r.Ok) throw new InvalidOperationException("Spotify rejected the login code (HTTP " + r.Status + "): " + Trim(r.Text));
            string token = (string)JObject.Parse(r.Text)["access_token"];
            if (String.IsNullOrEmpty(token)) throw new InvalidOperationException("No access token in Spotify's reply");
            return token;
        }

        static string Trim(string s) { return s.Length > 200 ? s.Substring(0, 200) : s; }

        public void Dispose()
        {
            try { if (_listener != null) _listener.Stop(); } catch { }
            _listener = null;
        }
    }
}
