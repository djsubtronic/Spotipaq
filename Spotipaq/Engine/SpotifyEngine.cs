// SpotifyEngine.cs - owns the logged-in session: stored credentials (DPAPI-protected), device id,
// connecting / reconnecting. Pages get their data through SpotifyBackend, which uses this.
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Spotipaq.Engine
{
    public static class CredentialStore
    {
        static string FilePath { get { return Path.Combine(Log.Folder, "credentials.dat"); } }
        static string DevicePath { get { return Path.Combine(Log.Folder, "device.txt"); } }

        public static bool Exists { get { return File.Exists(FilePath); } }

        /// <summary>Saves the reusable login, encrypted for this Windows user only (DPAPI).</summary>
        public static void Save(LoginCredentials c)
        {
            JObject o = new JObject();
            o["username"] = c.Username;
            o["type"] = (int)c.Type;
            o["credentials"] = Convert.ToBase64String(c.AuthData);
            byte[] plain = Encoding.UTF8.GetBytes(o.ToString());
            byte[] sealedBytes = ProtectedData.Protect(plain, Encoding.ASCII.GetBytes("Spotipaq"), DataProtectionScope.CurrentUser);
            if (!Directory.Exists(Log.Folder)) Directory.CreateDirectory(Log.Folder);
            File.WriteAllBytes(FilePath, sealedBytes);
        }

        public static LoginCredentials Load()
        {
            if (!Exists) return null;
            try
            {
                byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Encoding.ASCII.GetBytes("Spotipaq"), DataProtectionScope.CurrentUser);
                JObject o = JObject.Parse(Encoding.UTF8.GetString(plain));
                LoginCredentials c = new LoginCredentials();
                c.Username = (string)o["username"];
                c.Type = (AuthType)(int)o["type"];
                c.AuthData = Convert.FromBase64String((string)o["credentials"]);
                return c;
            }
            catch (Exception ex)
            {
                Log.Write("Stored credentials unreadable: " + ex.Message);
                return null;
            }
        }

        public static void Delete() { try { File.Delete(FilePath); } catch { } }

        /// <summary>A stable random device id (40 hex chars), like librespot.</summary>
        public static string DeviceId()
        {
            try { if (File.Exists(DevicePath)) { string s = File.ReadAllText(DevicePath).Trim(); if (s.Length == 40) return s; } } catch { }
            byte[] r = new byte[20];
            RandomNumberGenerator.Create().GetBytes(r);
            StringBuilder sb = new StringBuilder();
            foreach (byte b in r) sb.Append(b.ToString("x2"));
            try { if (!Directory.Exists(Log.Folder)) Directory.CreateDirectory(Log.Folder); File.WriteAllText(DevicePath, sb.ToString()); } catch { }
            return sb.ToString();
        }
    }

    public sealed class SpotifyEngine
    {
        readonly object _lock = new object();
        ApSession _ap;
        public readonly string DeviceId = CredentialStore.DeviceId();

        public ApSession Session { get { return _ap; } }
        public bool LoggedIn { get { return _ap != null && _ap.Connected; } }

        /// <summary>First login with an OAuth access token; stores the reusable credentials it returns.</summary>
        public void LoginWithToken(string accessToken)
        {
            LoginCredentials c = new LoginCredentials();
            c.Type = AuthType.SpotifyToken;
            c.AuthData = Encoding.UTF8.GetBytes(accessToken);
            ApSession s = new ApSession(DeviceId);
            s.ConnectAndLogin(c);
            CredentialStore.Save(s.ReusableCredentials);
            // librespot reconnects after a token login; the stored credentials give a full session.
            s.Close();
            EnsureConnected();
        }

        /// <summary>Uses a login received from another computer: saves it and connects. Removes it again if Spotify says no.</summary>
        public void LoginWithCredentials(LoginCredentials c)
        {
            CredentialStore.Save(c);
            try { EnsureConnected(); }
            catch { CredentialStore.Delete(); throw; }
        }

        /// <summary>Connects with the stored credentials if not already connected. Blocking.</summary>
        public ApSession EnsureConnected()
        {
            lock (_lock)
            {
                if (_ap != null && _ap.Connected) return _ap;
                LoginCredentials c = CredentialStore.Load();
                if (c == null) throw new InvalidOperationException("Not logged in");
                ApSession s = new ApSession(DeviceId);
                s.ConnectAndLogin(c);
                if (s.ReusableCredentials != null) CredentialStore.Save(s.ReusableCredentials);   // refreshed blob
                _ap = s;
                return s;
            }
        }

        public void LogOut()
        {
            lock (_lock)
            {
                if (_ap != null) _ap.Close();
                _ap = null;
                CredentialStore.Delete();
            }
        }
    }

    public sealed class LanCredentialsEventArgs : EventArgs
    {
        public readonly LoginCredentials Credentials;
        public LanCredentialsEventArgs(LoginCredentials c) { Credentials = c; }
    }

    public static class LanLogin
    {
        public const int Port = 5589;
        static readonly byte[] Magic = { (byte)'S', (byte)'P', (byte)'Q', (byte)'1' };
        const int KeyIterations = 3000;

        static void Derive(string pin, byte[] salt, out byte[] encKey, out byte[] macKey)
        {
            Rfc2898DeriveBytes k = new Rfc2898DeriveBytes(pin, salt, KeyIterations);
            encKey = k.GetBytes(32); macKey = k.GetBytes(32);
        }

        public static byte[] Seal(string pin, LoginCredentials c)
        {
            JObject o = new JObject();
            o["u"] = c.Username; o["t"] = (int)c.Type; o["c"] = Convert.ToBase64String(c.AuthData);
            byte[] plain = Encoding.UTF8.GetBytes(o.ToString(Newtonsoft.Json.Formatting.None));
            byte[] salt = new byte[16], iv = new byte[16];
            RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider();
            rng.GetBytes(salt); rng.GetBytes(iv);
            byte[] ek, mk;
            Derive(pin, salt, out ek, out mk);
            byte[] cipher;
            using (RijndaelManaged aes = new RijndaelManaged())
            {
                aes.Key = ek; aes.IV = iv; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                using (ICryptoTransform t = aes.CreateEncryptor()) cipher = t.TransformFinalBlock(plain, 0, plain.Length);
            }
            byte[] msg = new byte[4 + 16 + 16 + cipher.Length + 32];
            Buffer.BlockCopy(Magic, 0, msg, 0, 4);
            Buffer.BlockCopy(salt, 0, msg, 4, 16);
            Buffer.BlockCopy(iv, 0, msg, 20, 16);
            Buffer.BlockCopy(cipher, 0, msg, 36, cipher.Length);
            byte[] mac = new HMACSHA256(mk).ComputeHash(msg, 0, 36 + cipher.Length);
            Buffer.BlockCopy(mac, 0, msg, 36 + cipher.Length, 32);
            return msg;
        }

        /// <summary>Returns the credentials, or null when the PIN is wrong / the message is damaged.</summary>
        public static LoginCredentials Open(string pin, byte[] msg)
        {
            if (msg == null || msg.Length < 4 + 16 + 16 + 16 + 32) return null;
            for (int i = 0; i < 4; i++) if (msg[i] != Magic[i]) return null;
            byte[] salt = new byte[16], iv = new byte[16];
            Buffer.BlockCopy(msg, 4, salt, 0, 16); Buffer.BlockCopy(msg, 20, iv, 0, 16);
            byte[] ek, mk;
            Derive(pin, salt, out ek, out mk);
            int cl = msg.Length - 36 - 32;
            byte[] mac = new HMACSHA256(mk).ComputeHash(msg, 0, 36 + cl);
            int diff = 0;
            for (int i = 0; i < 32; i++) diff |= mac[i] ^ msg[36 + cl + i];
            if (diff != 0) return null;
            try
            {
                byte[] plain;
                using (RijndaelManaged aes = new RijndaelManaged())
                {
                    aes.Key = ek; aes.IV = iv; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                    using (ICryptoTransform t = aes.CreateDecryptor()) plain = t.TransformFinalBlock(msg, 36, cl);
                }
                JObject o = JObject.Parse(Encoding.UTF8.GetString(plain));
                LoginCredentials c = new LoginCredentials();
                c.Username = (string)o["u"]; c.Type = (AuthType)(int)o["t"]; c.AuthData = Convert.FromBase64String((string)o["c"]);
                return c;
            }
            catch { return null; }
        }

        public static List<string> LocalAddresses()
        {
            List<string> list = new List<string>();
            try
            {
                foreach (IPAddress a in Dns.GetHostAddresses(Dns.GetHostName()))
                    if (a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) && !a.ToString().StartsWith("169.254.")) list.Add(a.ToString());
            }
            catch { }
            return list;
        }

        /// <summary>Finds computers that are waiting for a login. Returns (name, address) pairs.</summary>
        public static List<KeyValuePair<string, string>> Discover(int ms)
        {
            List<KeyValuePair<string, string>> found = new List<KeyValuePair<string, string>>();
            Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
                s.Bind(new IPEndPoint(IPAddress.Any, 0));
                byte[] ask = Encoding.ASCII.GetBytes("SPQ?");
                List<IPAddress> targets = new List<IPAddress>();
                targets.Add(IPAddress.Broadcast);
                try
                {
                    foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                        foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                        {
                            if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                            byte[] ip = ua.Address.GetAddressBytes(), mk = ua.IPv4Mask.GetAddressBytes();
                            for (int i = 0; i < 4; i++) ip[i] |= (byte)~mk[i];
                            targets.Add(new IPAddress(ip));
                        }
                }
                catch { }
                foreach (IPAddress t in targets) { try { s.SendTo(ask, new IPEndPoint(t, Port)); } catch { } }
                int end = Environment.TickCount + ms;
                byte[] buf = new byte[256];
                while (unchecked(end - Environment.TickCount) > 0)
                {
                    if (!s.Poll(100000, SelectMode.SelectRead)) continue;
                    EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    int n = s.ReceiveFrom(buf, ref from);
                    string txt = Encoding.UTF8.GetString(buf, 0, n);
                    if (!txt.StartsWith("SPQ!")) continue;
                    string addr = ((IPEndPoint)from).Address.ToString();
                    bool dup = false;
                    foreach (KeyValuePair<string, string> kv in found) if (kv.Value == addr) dup = true;
                    if (!dup) found.Add(new KeyValuePair<string, string>(txt.Substring(4), addr));
                }
            }
            catch (Exception ex) { Log.Write("LAN discovery: " + ex.Message); }
            finally { s.Close(); }
            return found;
        }

        /// <summary>Sends the login to a waiting computer. Throws with a readable message on failure.</summary>
        public static void Send(string host, string pin, LoginCredentials c)
        {
            byte[] msg = Seal(pin, c);
            TcpClient cl = new TcpClient();
            try
            {
                IAsyncResult ar = cl.BeginConnect(host, Port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(6000, false)) throw new Exception("Couldn't reach " + host + " (is it waiting for a login, and is the firewall allowing it?)");
                cl.EndConnect(ar);
                NetworkStream ns = cl.GetStream();
                ns.ReadTimeout = 15000;
                byte[] head = new byte[4] { (byte)(msg.Length >> 24), (byte)(msg.Length >> 16), (byte)(msg.Length >> 8), (byte)msg.Length };
                ns.Write(head, 0, 4); ns.Write(msg, 0, msg.Length); ns.Flush();
                byte[] rep = new byte[4]; int got = 0;
                while (got < 4) { int r = ns.Read(rep, got, 4 - got); if (r <= 0) break; got += r; }
                string reply = Encoding.ASCII.GetString(rep, 0, got);
                if (reply == "OKAY") return;
                if (reply == "NOPE") throw new Exception("Wrong PIN");
                throw new Exception("The other computer didn't accept the login");
            }
            finally { cl.Close(); }
        }
    }

    /// <summary>Waits for another computer to send its login. Shows a PIN the sender must type.</summary>
    public sealed class LanReceiver : IDisposable
    {
        public readonly string Pin;
        public event EventHandler<LanCredentialsEventArgs> Received;
        TcpListener _tcp;
        Socket _udp;
        volatile bool _stop;
        int _fails;

        public LanReceiver()
        {
            byte[] b = new byte[4];
            new RNGCryptoServiceProvider().GetBytes(b);
            Pin = (BitConverter.ToUInt32(b, 0) % 1000000).ToString("D6");
        }

        public void Start()
        {
            _tcp = new TcpListener(IPAddress.Any, LanLogin.Port);
            _tcp.Start();
            Thread t = new Thread(AcceptLoop); t.IsBackground = true; t.Name = "LAN login"; t.Start();
            try
            {
                _udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                _udp.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _udp.Bind(new IPEndPoint(IPAddress.Any, LanLogin.Port));
                Thread u = new Thread(UdpLoop); u.IsBackground = true; u.Name = "LAN discovery"; u.Start();
            }
            catch (Exception ex) { Log.Write("LAN discovery responder: " + ex.Message); }
        }

        void UdpLoop()
        {
            byte[] reply = Encoding.UTF8.GetBytes("SPQ!" + Environment.MachineName);
            byte[] buf = new byte[64];
            try
            {
                while (!_stop)
                {
                    EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    int n = _udp.ReceiveFrom(buf, ref from);
                    if (n >= 4 && buf[0] == 'S' && buf[1] == 'P' && buf[2] == 'Q' && buf[3] == '?')
                        _udp.SendTo(reply, from);
                }
            }
            catch { }
        }

        void AcceptLoop()
        {
            try
            {
                while (!_stop)
                {
                    TcpClient c = _tcp.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(delegate { Handle(c); });
                }
            }
            catch { }
        }

        void Handle(TcpClient c)
        {
            try
            {
                using (c)
                {
                    NetworkStream ns = c.GetStream();
                    ns.ReadTimeout = 8000;
                    byte[] head = ReadN(ns, 4);
                    int len = (head[0] << 24) | (head[1] << 16) | (head[2] << 8) | head[3];
                    if (len < 0 || len > 16384) return;
                    byte[] msg = ReadN(ns, len);
                    LoginCredentials creds = LanLogin.Open(Pin, msg);
                    if (creds == null)
                    {
                        byte[] no = Encoding.ASCII.GetBytes("NOPE"); ns.Write(no, 0, 4);
                        if (Interlocked.Increment(ref _fails) >= 5) { Log.Write("LAN login: too many wrong PINs, closing"); Dispose(); }
                        return;
                    }
                    byte[] ok = Encoding.ASCII.GetBytes("OKAY"); ns.Write(ok, 0, 4); ns.Flush();
                    EventHandler<LanCredentialsEventArgs> h = Received;
                    if (h != null) h(this, new LanCredentialsEventArgs(creds));
                }
            }
            catch (Exception ex) { Log.Write("LAN login connection: " + ex.Message); }
        }

        static byte[] ReadN(NetworkStream ns, int n)
        {
            byte[] b = new byte[n]; int got = 0;
            while (got < n) { int r = ns.Read(b, got, n - got); if (r <= 0) throw new System.IO.EndOfStreamException(); got += r; }
            return b;
        }

        public void Dispose()
        {
            _stop = true;
            try { if (_tcp != null) _tcp.Stop(); } catch { }
            try { if (_udp != null) _udp.Close(); } catch { }
        }
    }
}
