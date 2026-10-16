// ApSession.cs - the Spotify access-point connection: resolve, Diffie-Hellman handshake, Shannon-encrypted
// packets, login, keep-alive. Ported from librespot-java's Session.connect()/authenticatePartial()/Receiver.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace Spotipaq.Engine
{
    public enum AuthType { UserPass = 0, StoredSpotifyCredentials = 1, SpotifyToken = 3 }

    public sealed class LoginCredentials
    {
        public string Username;          // required for stored credentials, null for an OAuth token
        public AuthType Type;
        public byte[] AuthData;
    }

    public sealed class ApLoginException : Exception
    {
        public readonly int ErrorCode;
        public ApLoginException(int code, string description)
            : base("Spotify refused the login: " + Describe(code) + (String.IsNullOrEmpty(description) ? "" : " (" + description + ")")) { ErrorCode = code; }

        static string Describe(int code)
        {
            switch (code)
            {
                case 0x2: return "try another server";
                case 0x9: return "travel restriction";
                case 0xb: return "Premium account required";
                case 0xc: return "bad credentials";
                case 0xd: return "could not validate credentials";
                case 0xf: return "extra verification required";
                case 0x11: return "application banned";
                default: return "error " + code;
            }
        }
    }

    public sealed class ApPacket
    {
        public readonly byte Cmd; public readonly byte[] Payload;
        public ApPacket(byte cmd, byte[] payload) { Cmd = cmd; Payload = payload; }
    }

    public sealed class ApSession : IDisposable
    {
        // packet types (librespot Packet.Type)
        public const byte CmdPing = 0x04, CmdRequestKey = 0x0c, CmdAesKey = 0x0d, CmdAesKeyError = 0x0e, CmdUnknown0f = 0x0f,
                          CmdCountryCode = 0x1b, CmdPong = 0x49, CmdPongAck = 0x4a, CmdPreferredLocale = 0x74,
                          CmdLogin = 0xab, CmdApWelcome = 0xac, CmdAuthFailure = 0xad;

        static readonly byte[] DhPrime = {
            0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xc9,0x0f,0xda,0xa2,0x21,0x68,0xc2,0x34,0xc4,0xc6,0x62,0x8b,0x80,0xdc,0x1c,0xd1,
            0x29,0x02,0x4e,0x08,0x8a,0x67,0xcc,0x74,0x02,0x0b,0xbe,0xa6,0x3b,0x13,0x9b,0x22,0x51,0x4a,0x08,0x79,0x8e,0x34,0x04,0xdd,
            0xef,0x95,0x19,0xb3,0xcd,0x3a,0x43,0x1b,0x30,0x2b,0x0a,0x6d,0xf2,0x5f,0x14,0x37,0x4f,0xe1,0x35,0x6d,0x6d,0x51,0xc2,0x45,
            0xe4,0x85,0xb5,0x76,0x62,0x5e,0x7e,0xc6,0xf4,0x4c,0x42,0xe9,0xa6,0x3a,0x36,0x20,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff };
        const long BuildVersion = 117300517;       // librespot-java's "standard build info" version

        public event EventHandler<ApPacketEventArgs> PacketReceived;   // non-session packets (audio keys, ...) - worker thread
        public event EventHandler Disconnected;

        public string Username { get; private set; }
        public string CountryCode { get; private set; }
        /// <summary>What Spotify says the account is ("premium", "free", "open", ...), or null until the AP tells us.</summary>
        public string AccountType { get; private set; }
        public LoginCredentials ReusableCredentials { get; private set; }
        public bool Connected { get { return _running; } }

        readonly string _deviceId;
        TcpClient _tcp;
        NetworkStream _net;
        Shannon _send, _recv;
        int _sendNonce, _recvNonce;
        readonly object _sendLock = new object();
        Thread _reader;
        volatile bool _running;
        static readonly RandomNumberGenerator Rng = RandomNumberGenerator.Create();

        public ApSession(string deviceId) { _deviceId = deviceId; }

        // ------------------------------------------------------------------ access point list
        public static List<string> ResolveAccessPoints()
        {
            List<string> aps = new List<string>();
            try
            {
                HttpResult r = Native.Get("https://apresolve.spotify.com/?type=accesspoint", null);
                if (r.Ok)
                    foreach (JToken t in (JArray)JObject.Parse(r.Text)["accesspoint"]) aps.Add((string)t);
            }
            catch (Exception ex) { Log.Write("apresolve failed: " + ex.Message); }
            if (aps.Count == 0) aps.Add("ap.spotify.com:4070");
            return aps;
        }

        // ------------------------------------------------------------------ connect + login
        /// <summary>Connects to an access point and logs in. Blocking; throws on failure.</summary>
        public void ConnectAndLogin(LoginCredentials creds)
        {
            List<string> aps = ResolveAccessPoints();
            Exception last = null;
            for (int i = 0; i < aps.Count && i < 4; i++)
            {
                try
                {
                    Connect(aps[i]);
                    Authenticate(creds);
                    return;
                }
                catch (ApLoginException ex)
                {
                    Close();
                    if (ex.ErrorCode != 0x2) throw;              // only "try another AP" is worth retrying
                    last = ex;
                }
                catch (Exception ex)
                {
                    Log.Write("AP " + aps[i] + " failed: " + ex.Message);
                    Close();
                    last = ex;
                }
            }
            throw last ?? new IOException("No access point reachable");
        }

        void Connect(string hostPort)
        {
            int colon = hostPort.LastIndexOf(':');
            string host = colon > 0 ? hostPort.Substring(0, colon) : hostPort;
            int port = colon > 0 ? Int32.Parse(hostPort.Substring(colon + 1)) : 4070;
            Log.Write("Connecting to " + host + ":" + port);
            _tcp = new TcpClient();
            _tcp.NoDelay = true;
            _tcp.Connect(host, port);
            _net = _tcp.GetStream();
            _net.ReadTimeout = 20000;

            // ---- Diffie-Hellman keys ----
            byte[] priv = new byte[95];
            Rng.GetBytes(priv);
            byte[] pub = Native.ModExp(new byte[] { 2 }, priv, DhPrime);
            byte[] nonce = new byte[16];
            Rng.GetBytes(nonce);

            // ---- ClientHello ----
            ProtoWriter buildInfo = new ProtoWriter().Int(10, 0).Int(20, 0).Int(30, 0).Int(40, BuildVersion); // CLIENT, FLAG_NONE, WIN32_X86
            ProtoWriter hello = new ProtoWriter()
                .Message(10, buildInfo)
                .Int(30, 0)                                                          // CRYPTO_SUITE_SHANNON
                .Message(50, new ProtoWriter().Message(10, new ProtoWriter().Bytes(10, pub).Int(20, 1)))
                .Bytes(60, nonce)
                .Bytes(70, new byte[] { 0x1e });
            byte[] helloBytes = hello.ToArray();

            MemoryStream acc = new MemoryStream();                // everything sent+received so far (for the HMAC)
            byte[] head = new byte[6];
            head[0] = 0; head[1] = 4;
            WriteInt(head, 2, 2 + 4 + helloBytes.Length);
            _net.Write(head, 0, 6); _net.Write(helloBytes, 0, helloBytes.Length); _net.Flush();
            acc.Write(head, 0, 6); acc.Write(helloBytes, 0, helloBytes.Length);

            // ---- APResponseMessage ----
            byte[] lenBytes = ReadExactly(4);
            int len = ReadInt(lenBytes, 0);
            byte[] resp = ReadExactly(len - 4);
            acc.Write(lenBytes, 0, 4); acc.Write(resp, 0, resp.Length);

            Dictionary<int, List<ProtoField>> top = new ProtoReader(resp).ReadAll();
            ProtoField? failed = ProtoReader.First(top, 30);
            if (failed.HasValue) ThrowLoginFailed(failed.Value.Data);
            ProtoField? challenge = ProtoReader.First(top, 10);
            if (!challenge.HasValue) throw new IOException("AP sent no challenge");
            byte[] gs = Dig(challenge.Value.Data, 10, 10, 10);        // challenge.login_crypto_challenge.diffie_hellman.gs
            // (the server's RSA signature over gs is not verified yet - TODO with the native DLL)

            byte[] shared = Native.ModExp(gs, priv, DhPrime);
            byte[] accBytes = acc.ToArray();

            // ---- key derivation: 5 x HMAC-SHA1(shared, acc || i) ----
            MemoryStream data = new MemoryStream();
            using (HMACSHA1 mac = new HMACSHA1(shared))
                for (int i = 1; i < 6; i++)
                {
                    byte[] input = new byte[accBytes.Length + 1];
                    Buffer.BlockCopy(accBytes, 0, input, 0, accBytes.Length);
                    input[accBytes.Length] = (byte)i;
                    byte[] h = mac.ComputeHash(input);
                    data.Write(h, 0, h.Length);
                }
            byte[] d = data.ToArray();
            byte[] answer;
            using (HMACSHA1 mac = new HMACSHA1(Slice(d, 0, 0x14))) answer = mac.ComputeHash(accBytes);

            ProtoWriter plain = new ProtoWriter()
                .Message(10, new ProtoWriter().Message(10, new ProtoWriter().Bytes(10, answer)))
                .Message(20, new ProtoWriter())
                .Message(30, new ProtoWriter());
            byte[] plainBytes = plain.ToArray();
            byte[] plen = new byte[4];
            WriteInt(plen, 0, 4 + plainBytes.Length);
            _net.Write(plen, 0, 4); _net.Write(plainBytes, 0, plainBytes.Length); _net.Flush();

            // A login failure comes back immediately in the clear; silence means success.
            if (_tcp.Client.Poll(300 * 1000, SelectMode.SelectRead) && _tcp.Available >= 4)
            {
                int flen = ReadInt(ReadExactly(4), 0);
                Dictionary<int, List<ProtoField>> f = new ProtoReader(ReadExactly(flen - 4)).ReadAll();
                ProtoField? lf = ProtoReader.First(f, 30);
                if (lf.HasValue) ThrowLoginFailed(lf.Value.Data);
                throw new IOException("Unexpected data after handshake");
            }

            _send = new Shannon(); _send.Key(Slice(d, 0x14, 0x20));
            _recv = new Shannon(); _recv.Key(Slice(d, 0x34, 0x20));
            _sendNonce = 0; _recvNonce = 0;
            Log.Write("Handshake complete");
        }

        void Authenticate(LoginCredentials creds)
        {
            ProtoWriter lc = new ProtoWriter();
            if (!String.IsNullOrEmpty(creds.Username)) lc.String(10, creds.Username);
            lc.Int(20, (int)creds.Type).Bytes(30, creds.AuthData);
            string ver = "Spotipaq 0.1 (librespot-java compatible)";
            ProtoWriter sys = new ProtoWriter()
                .Int(10, 0)                                   // CPU_UNKNOWN
                .Int(60, 0)                                   // OS_UNKNOWN
                .String(90, ver + "; Windows XP")
                .String(100, _deviceId);
            ProtoWriter msg = new ProtoWriter().Message(10, lc).Message(50, sys).String(70, ver);
            SendPacket(CmdLogin, msg.ToArray());

            ApPacket p = ReceivePacket();
            if (p.Cmd == CmdAuthFailure) ThrowLoginFailed(p.Payload);
            if (p.Cmd != CmdApWelcome) throw new IOException("Unexpected login reply 0x" + p.Cmd.ToString("x2"));

            Dictionary<int, List<ProtoField>> w = new ProtoReader(p.Payload).ReadAll();
            Username = ProtoReader.First(w, 10).Value.Text;
            LoginCredentials reusable = new LoginCredentials();
            reusable.Username = Username;
            reusable.Type = (AuthType)ProtoReader.First(w, 30).Value.Int;
            reusable.AuthData = ProtoReader.First(w, 40).Value.Data;
            ReusableCredentials = reusable;
            Log.Write("Logged in as " + Username);

            byte[] r20 = new byte[20];
            Rng.GetBytes(r20);
            SendPacket(CmdUnknown0f, r20);
            MemoryStream loc = new MemoryStream();
            loc.Write(new byte[] { 0, 0, 0x10, 0, 0x02 }, 0, 5);
            byte[] key = Encoding.ASCII.GetBytes("preferred-locale"); loc.Write(key, 0, key.Length);
            byte[] lang = Encoding.ASCII.GetBytes("en"); loc.Write(lang, 0, lang.Length);
            SendPacket(CmdPreferredLocale, loc.ToArray());

            _net.ReadTimeout = Timeout.Infinite;
            _running = true;
            _reader = new Thread(ReadLoop);
            _reader.IsBackground = true;
            _reader.Name = "Spotify AP reader";
            _reader.Start();
        }

        static void ThrowLoginFailed(byte[] apLoginFailed)
        {
            Dictionary<int, List<ProtoField>> f = new ProtoReader(apLoginFailed).ReadAll();
            ProtoField? code = ProtoReader.First(f, 10), desc = ProtoReader.First(f, 40);
            throw new ApLoginException(code.HasValue ? (int)code.Value.Int : -1, desc.HasValue ? desc.Value.Text : null);
        }

        // ------------------------------------------------------------------ encrypted packets
        public void SendPacket(byte cmd, byte[] payload)
        {
            lock (_sendLock)
            {
                if (_send == null) throw new InvalidOperationException("Not connected");
                byte[] buf = new byte[3 + payload.Length + 4];
                buf[0] = cmd; buf[1] = (byte)(payload.Length >> 8); buf[2] = (byte)payload.Length;
                Buffer.BlockCopy(payload, 0, buf, 3, payload.Length);
                byte[] n = new byte[4]; WriteInt(n, 0, _sendNonce++);
                _send.Nonce(n);
                _send.Encrypt(buf, 0, 3 + payload.Length);
                byte[] mac = new byte[4];
                _send.Finish(mac);
                Buffer.BlockCopy(mac, 0, buf, 3 + payload.Length, 4);
                _net.Write(buf, 0, buf.Length);
                _net.Flush();
            }
        }

        ApPacket ReceivePacket()
        {
            byte[] n = new byte[4]; WriteInt(n, 0, _recvNonce++);
            _recv.Nonce(n);
            byte[] head = ReadExactly(3);
            _recv.Decrypt(head, 0, 3);
            int len = (head[1] << 8) | head[2];
            byte[] payload = ReadExactly(len);
            _recv.Decrypt(payload, 0, len);
            byte[] mac = ReadExactly(4), expected = new byte[4];
            _recv.Finish(expected);
            for (int i = 0; i < 4; i++) if (mac[i] != expected[i]) throw new IOException("Packet MAC mismatch");
            return new ApPacket(head[0], payload);
        }

        void ReadLoop()
        {
            try
            {
                while (_running)
                {
                    ApPacket p = ReceivePacket();
                    switch (p.Cmd)
                    {
                        case CmdPing: SendPacket(CmdPong, p.Payload); break;
                        case CmdPongAck: break;
                        case CmdCountryCode: CountryCode = Encoding.ASCII.GetString(p.Payload); Log.Debug("Country: " + CountryCode); break;
                        case 0x50:   // ProductInfo: XML saying what Spotify thinks the account is (premium/free/...)
                            try
                            {
                                string xml = Encoding.UTF8.GetString(p.Payload);
                                xml = System.Text.RegularExpressions.Regex.Replace(xml, @"\s+", " ");
                                Log.Debug("ProductInfo: " + (xml.Length > 900 ? xml.Substring(0, 900) : xml));
                                System.Text.RegularExpressions.Match tm = System.Text.RegularExpressions.Regex.Match(xml, @"<type>\s*([^<\s]+)\s*</type>");
                                if (tm.Success) { AccountType = tm.Groups[1].Value.ToLowerInvariant(); Log.Write("Account type: " + AccountType); }
                            }
                            catch { }
                            break;
                        default:
                            if (p.Cmd != CmdAesKey && p.Cmd != CmdAesKeyError) Log.Debug("AP packet 0x" + p.Cmd.ToString("x2") + " (" + p.Payload.Length + " bytes)");
                            EventHandler<ApPacketEventArgs> h = PacketReceived;
                            if (h != null) h(this, new ApPacketEventArgs(p));
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                if (_running) Log.Write("AP connection lost: " + ex.Message);
            }
            bool was = _running;
            _running = false;
            if (was && Disconnected != null) Disconnected(this, EventArgs.Empty);
        }

        // ------------------------------------------------------------------ helpers
        byte[] ReadExactly(int n)
        {
            byte[] b = new byte[n];
            int got = 0;
            while (got < n)
            {
                int r = _net.Read(b, got, n - got);
                if (r <= 0) throw new EndOfStreamException("Connection closed by Spotify");
                got += r;
            }
            return b;
        }

        static byte[] Dig(byte[] msg, params int[] path)
        {
            byte[] cur = msg;
            foreach (int field in path)
            {
                ProtoField? f = ProtoReader.First(new ProtoReader(cur).ReadAll(), field);
                if (!f.HasValue) throw new IOException("Missing protobuf field " + field);
                cur = f.Value.Data;
            }
            return cur;
        }

        static byte[] Slice(byte[] b, int offset, int count) { byte[] r = new byte[count]; Buffer.BlockCopy(b, offset, r, 0, count); return r; }
        static void WriteInt(byte[] b, int o, int v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }
        static int ReadInt(byte[] b, int o) { return (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]; }

        public void Close()
        {
            _running = false;
            try { if (_tcp != null) _tcp.Close(); } catch { }
            _tcp = null; _net = null; _send = null; _recv = null;
        }

        public void Dispose() { Close(); }
    }

    public sealed class ApPacketEventArgs : EventArgs
    {
        public readonly ApPacket Packet;
        public ApPacketEventArgs(ApPacket p) { Packet = p; }
    }
}
