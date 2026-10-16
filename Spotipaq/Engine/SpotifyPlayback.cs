// SpotifyPlayback.cs - real playback: audio key over the access point, CDN download, native decode + waveOut.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Spotipaq.Engine
{
    /// <summary>Requests AES audio keys over the AP connection (packet 0x0c -> 0x0d / 0x0e).</summary>
    public sealed class AudioKeys
    {
        sealed class Waiter { public readonly ManualResetEvent Done = new ManualResetEvent(false); public byte[] Key; }
        readonly Dictionary<int, Waiter> _waiting = new Dictionary<int, Waiter>();
        int _seq;
        ApSession _hooked;

        void Hook(ApSession ap)
        {
            lock (_waiting)
            {
                if (_hooked == ap) return;
                _hooked = ap;
            }
            ap.PacketReceived += OnPacket;
        }

        void OnPacket(object sender, ApPacketEventArgs e)
        {
            byte cmd = e.Packet.Cmd; byte[] p = e.Packet.Payload;
            if ((cmd != ApSession.CmdAesKey && cmd != ApSession.CmdAesKeyError) || p.Length < 4) return;
            int seq = (p[0] << 24) | (p[1] << 16) | (p[2] << 8) | p[3];
            Waiter w;
            lock (_waiting) { if (!_waiting.TryGetValue(seq, out w)) return; _waiting.Remove(seq); }
            if (cmd == ApSession.CmdAesKey && p.Length >= 20) { w.Key = new byte[16]; Buffer.BlockCopy(p, 4, w.Key, 0, 16); }
            else Log.Write("Audio key refused (code " + (p.Length >= 6 ? (p[4] << 8 | p[5]) : -1) + ")");
            w.Done.Set();
        }

        public byte[] Get(ApSession ap, byte[] fileId, byte[] gid) { return Get(ap, fileId, gid, 2); }

        public byte[] Get(ApSession ap, byte[] fileId, byte[] gid, int attempts)
        {
            Hook(ap);
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                int seq = Interlocked.Increment(ref _seq) - 1;
                Waiter w = new Waiter();
                lock (_waiting) _waiting[seq] = w;
                byte[] pl = new byte[20 + 16 + 4 + 2];
                Buffer.BlockCopy(fileId, 0, pl, 0, 20);
                Buffer.BlockCopy(gid, 0, pl, 20, 16);
                pl[36] = (byte)(seq >> 24); pl[37] = (byte)(seq >> 16); pl[38] = (byte)(seq >> 8); pl[39] = (byte)seq;
                ap.SendPacket(ApSession.CmdRequestKey, pl);
                bool got = w.Done.WaitOne(6000, false);
                lock (_waiting) _waiting.Remove(seq);
                if (got && w.Key != null) return w.Key;
            }
            throw new InvalidOperationException("Spotify didn't give an audio key for this track");
        }
    }

    /// <summary>The track can't be played at all (not licensed here, no key, no download) - as opposed to a network hiccup.</summary>
    public sealed class UnplayableException : InvalidOperationException
    {
        public UnplayableException(string message) : base(message) { }
    }

    public sealed class SpotifyPlayback : IPlayback
    {
        public event EventHandler StateChanged;
        public event EventHandler TrackEnded;
        public event EventHandler TrackUnplayable;

        readonly SpotifyEngine _engine;
        readonly Spclient _sp;
        readonly AudioKeys _keys = new AudioKeys();
        readonly object _audioLock = new object();
        readonly System.Threading.Timer _poll;
        volatile PlayState _state = PlayState.Stopped;
        Track _current;
        int _gen, _volume = 80, _startMs, _seekMs, _seekTick;
        bool _endFired;
        int _loadStartedTick, _loadRetry;
        const int LoadLimitMs = 45000;
        public string LastError;

        public SpotifyPlayback(SpotifyEngine engine, Spclient sp)
        {
            _engine = engine; _sp = sp;
            _poll = new System.Threading.Timer(Tick, null, 250, 250);
        }

        public PlayState State { get { return _state; } }
        public Track Current { get { return _current; } }

        public int Volume
        {
            get { return _volume; }
            set { _volume = Math.Max(0, Math.Min(100, value)); try { Native.AudioVolume(_volume); } catch { } }
        }

        public int PositionMs
        {
            get
            {
                if (_state == PlayState.Loading) return _startMs;
                if (_state == PlayState.Stopped || _current == null) return 0;
                if (_seekTick != 0 && unchecked(Environment.TickCount - _seekTick) < 400) return _seekMs;
                int p;
                try { p = Native.AudioPosition(); } catch { p = 0; }
                return Math.Max(0, Math.Min(_current.DurationMs, p));
            }
        }

        public void Play(Track track, int startMs)
        {
            int gen;
            lock (_audioLock)
            {
                gen = ++_gen;
                try { Native.AudioStop(); } catch { }
            }
            _current = track; _startMs = startMs; _seekTick = 0; _endFired = false; LastError = null;
            _loadRetry = 0; _loadStartedTick = Environment.TickCount;
            Set(PlayState.Loading);
            ThreadPool.QueueUserWorkItem(delegate { Load(track, startMs, gen); });
        }

        public void Pause()
        {
            if (_state != PlayState.Playing) return;
            try { Native.AudioPause(true); } catch { }
            Set(PlayState.Paused);
        }

        public void Resume()
        {
            if (_state != PlayState.Paused) return;
            try { Native.AudioPause(false); } catch { }
            Set(PlayState.Playing);
        }

        public void Seek(int ms)
        {
            if (_current == null || (_state != PlayState.Playing && _state != PlayState.Paused)) return;
            ms = Math.Max(0, Math.Min(_current.DurationMs, ms));
            _seekMs = ms; _seekTick = Environment.TickCount; if (_seekTick == 0) _seekTick = 1;
            _endFired = false;
            try { Native.AudioSeek(ms); } catch { }
            Fire(StateChanged);
        }

        public void Stop()
        {
            lock (_audioLock) { _gen++; try { Native.AudioStop(); } catch { } }
            _current = null; _seekTick = 0;
            Set(PlayState.Stopped);
        }

        void Set(PlayState s) { _state = s; Fire(StateChanged); }
        void Fire(EventHandler h) { if (h != null) h(this, EventArgs.Empty); }

        void Tick(object o)
        {
            try
            {
                if (_state == PlayState.Loading && !_endFired)
                {
                    if (unchecked((uint)(Environment.TickCount - _loadStartedTick)) > LoadLimitMs)
                    {
                        if (_loadRetry == 0)
                        {
                            _loadRetry = 1;
                            int gen;
                            lock (_audioLock) { gen = ++_gen; try { Native.AudioStop(); } catch { } }
                            _loadStartedTick = Environment.TickCount;
                            Log.Write("Playback recovery: track load exceeded 45s; retrying " + (_current == null ? "" : _current.Name));
                            Track track = _current; int at = _startMs;
                            ThreadPool.QueueUserWorkItem(delegate { Load(track, at, gen); });
                        }
                        else
                        {
                            lock (_audioLock) { ++_gen; try { Native.AudioStop(); } catch { } }
                            _endFired = true;
                            Fail("Track load exceeded 45s twice");
                            Fire(TrackUnplayable);
                        }
                    }
                    return;
                }
                if (_state != PlayState.Playing || _endFired) return;
                int st = Native.AudioState();
                if (st == 4)
                {
                    _endFired = true;
                    _state = PlayState.Paused;
                    Fire(StateChanged);
                    Fire(TrackEnded);
                }
                else if (st == -1)
                {
                    _endFired = true;
                    Fail("The audio decoder reported an error");
                    Fire(TrackUnplayable);
                }
            }
            catch { }
        }

        void Fail(string why)
        {
            LastError = why;
            Log.Write("Playback failed: " + why);
            _state = PlayState.Stopped;
            Fire(StateChanged);
        }

        // ------------------------------------------------------------------ loading a track
        static byte[] DecodeBase62(string id)
        {
            const string alpha = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
            byte[] n = new byte[16];
            foreach (char ch in id)
            {
                int d = alpha.IndexOf(ch);
                if (d < 0) throw new ArgumentException("Bad track id");
                int carry = d;
                for (int i = 15; i >= 0; i--)
                {
                    int v = n[i] * 62 + carry;
                    n[i] = (byte)v; carry = v >> 8;
                }
            }
            return n;
        }

        static int PreferredFormat()
        {
            try
            {
                string f = Path.Combine(Log.Folder, "quality.txt");
                if (File.Exists(f)) { string t = File.ReadAllText(f).Trim(); if (t == "96") return 0; if (t == "320") return 2; }
            }
            catch { }
            return 1;   // OGG_VORBIS_160
        }

        sealed class Candidate
        {
            public byte[] FileId, Gid; public int Format; public string Source;
            public override string ToString() { return Source + " fmt " + Format + " file " + Spclient.Hex(FileId).Substring(0, 8); }
        }

        static void AddFiles(List<Candidate> list, Dictionary<int, List<ProtoField>> c, string source, int want)
        {
            ProtoField? g = ProtoReader.First(c, 1);
            if (g == null || g.Value.Data.Length != 16 || !c.ContainsKey(12)) return;
            List<Candidate> mine = new List<Candidate>();
            foreach (ProtoField file in c[12])
            {
                Dictionary<int, List<ProtoField>> fd = file.Message.ReadAll();
                ProtoField? id = ProtoReader.First(fd, 1), fmt = ProtoReader.First(fd, 2);
                if (id == null || id.Value.Data.Length != 20) continue;
                int f = fmt == null ? 0 : (int)fmt.Value.Int;
                if (f > 2) continue;                              // not Ogg Vorbis
                Candidate cd = new Candidate(); cd.FileId = id.Value.Data; cd.Gid = g.Value.Data; cd.Format = f; cd.Source = source;
                mine.Add(cd);
            }
            mine.Sort(delegate (Candidate x, Candidate y) { return Score(y.Format, want).CompareTo(Score(x.Format, want)); });
            list.AddRange(mine);
        }

        static int Score(int f, int want) { return f == want ? 100 : 50 - Math.Abs(f - want); }

        /// <summary>Walks an unknown protobuf looking for AudioFile-shaped messages (20-byte id + small format number).</summary>
        static void ScanForFiles(byte[] data, byte[] gid, List<Candidate> list, int depth)
        {
            if (depth > 4 || data == null) return;
            Dictionary<int, List<ProtoField>> d;
            try { d = new ProtoReader(data).ReadAll(); } catch { return; }
            ProtoField? id = ProtoReader.First(d, 1), fmt = ProtoReader.First(d, 2);
            if (id != null && id.Value.Wire == 2 && id.Value.Data.Length == 20 && fmt != null && fmt.Value.Wire == 0 && fmt.Value.Int <= 2)
            {
                Candidate cd = new Candidate(); cd.FileId = id.Value.Data; cd.Gid = gid; cd.Format = (int)fmt.Value.Int; cd.Source = "audio-files";
                list.Add(cd); return;
            }
            foreach (List<ProtoField> fl in d.Values) foreach (ProtoField f in fl) if (f.Wire == 2) ScanForFiles(f.Data, gid, list, depth + 1);
        }

        static List<Candidate> Candidates(byte[] trackMeta, byte[] audioFilesExt)
        {
            List<Candidate> list = new List<Candidate>();
            Dictionary<int, List<ProtoField>> t = new ProtoReader(trackMeta).ReadAll();
            int want = PreferredFormat();
            AddFiles(list, t, "main", want);
            if (t.ContainsKey(13)) foreach (ProtoField alt in t[13]) AddFiles(list, alt.Message.ReadAll(), "alt", want);
            ProtoField? g = ProtoReader.First(t, 1);
            if (audioFilesExt != null && g != null && g.Value.Data.Length == 16) ScanForFiles(audioFilesExt, g.Value.Data, list, 0);
            return list;
        }

        // Retry transient CDN failures without duplicating or losing an audio byte.
        // Routine successful downloads are not logged here.
        HttpResult GetRange(string url, int from, int to, int gen)
        {
            Exception last = null;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                if (gen != _gen) return null;
                try
                {
                    HttpResult r = Native.Http("GET", url, "Range: bytes=" + from + "-" + to + "\r\n", null);
                    int wanted = to - from + 1;
                    if (r.Status == 206 && r.Body != null && r.Body.Length == wanted) return r;
                    // A full-file 200 response is valid only for the initial request.
                    if (r.Status == 200 && from == 0 && r.Body != null && r.Body.Length > 0) return r;
                    last = new IOException("CDN response HTTP " + r.Status + ", bytes " +
                        (r.Body == null ? 0 : r.Body.Length) + "/" + wanted);
                }
                catch (Exception ex) { last = ex; }
                if (gen != _gen) return null;
                if (attempt < 3) Thread.Sleep(300 * attempt);
            }
            throw new IOException("CDN range " + from + "-" + to + " failed after 3 attempts", last);
        }

        void Load(Track track, int startMs, int gen)
        {
            try
            {
                if (Native.Version() < 2) throw new InvalidOperationException("SpotipaqNative.dll is out of date - rebuild SpotipaqNative");
                ApSession ap = _engine.EnsureConnected();

                string uri = "spotify:track:" + track.Id;
                Dictionary<string, byte[]> meta = _sp.ExtMeta(10, new string[] { uri }, false);   // no header, like librespot
                byte[] tm;
                if (!meta.TryGetValue(uri, out tm)) throw new InvalidOperationException("Track metadata not available");
                byte[] af = null;
                try { Dictionary<string, byte[]> am = _sp.ExtMeta(5, new string[] { uri }, false); am.TryGetValue(uri, out af); }
                catch (Exception ex) { Log.Write("Audio files lookup: " + ex.Message); }
                List<Candidate> cands = Candidates(tm, af);
                if (cands.Count == 0) throw new UnplayableException("This track isn't available to play in your country");
                if (gen != _gen) return;

                byte[] key = null, fileId = null;
                for (int i = 0; i < cands.Count && i < 8 && key == null; i++)
                {
                    if (gen != _gen) return;
                    try { key = _keys.Get(ap, cands[i].FileId, cands[i].Gid, 1); fileId = cands[i].FileId; }
                    catch (InvalidOperationException) { }
                }
                if (key == null)
                {
                    if (ap.AccountType != null && ap.AccountType != "premium")
                        throw new InvalidOperationException("Spotify only allows playback on Premium accounts - this account is '" + ap.AccountType + "'");
                    throw new UnplayableException("Spotify refused the audio key for every file of this track");
                }
                if (gen != _gen) return;

                HttpResult sr = _sp.Sp("GET", "/storage-resolve/files/audio/interactive/" + Spclient.Hex(fileId), null, null);
                if (!sr.Ok) throw new InvalidOperationException("Couldn't locate the audio file (HTTP " + sr.Status + ")");
                Dictionary<int, List<ProtoField>> sd = new ProtoReader(sr.Body).ReadAll();
                ProtoField? result = ProtoReader.First(sd, 1);
                if ((result != null && result.Value.Int != 0) || !sd.ContainsKey(2))
                    throw new UnplayableException("Spotify didn't offer a download for this track");
                string url = sd[2][0].Text;

                HttpResult first = GetRange(url, 0, 131071, gen);
                if (first == null) return;
                int total, offset;
                if (first.Status == 200) total = first.Body.Length;
                else
                {
                    Match m = Regex.Match(first.Headers ?? "", @"Content-Range:\s*bytes\s+\d+-\d+/(\d+)", RegexOptions.IgnoreCase);
                    if (!m.Success) throw new InvalidOperationException("CDN didn't report the file size");
                    total = Int32.Parse(m.Groups[1].Value);
                }
                lock (_audioLock)
                {
                    if (gen != _gen) return;
                    if (!Native.AudioOpen(key, total)) throw new InvalidOperationException("Audio engine refused the file");
                    Native.AudioVolume(_volume);
                    Native.AudioFeed(first.Body);
                    if (!Native.AudioPlay(startMs)) throw new InvalidOperationException("Couldn't start the audio engine");
                }
                offset = first.Body.Length;
                if (gen == _gen) Set(PlayState.Playing);

                while (offset < total)
                {
                    if (gen != _gen) return;
                    int end = Math.Min(total - 1, offset + 524288 - 1);
                    // A failed future range is not a decoder failure. Keep the already-decoded
                    // audio playing and retry this same range, without resetting the native
                    // session or advancing the byte offset until a complete range arrives.
                    HttpResult r = null;
                    int recoveryStart = 0;
                    int recoveryCycles = 0;
                    Exception recoveryError = null;
                    while (r == null)
                    {
                        if (gen != _gen) return;
                        try
                        {
                            r = GetRange(url, offset, end, gen);
                            if (r == null) return;
                        }
                        catch (IOException ex)
                        {
                            if (gen != _gen) return;
                            recoveryError = ex;
                            if (recoveryCycles++ == 0)
                            {
                                recoveryStart = Environment.TickCount;
                                Log.Write("CDN stream recovery started: " + track.Name +
                                    ", range " + offset + "-" + end + ", " + ex.Message);
                            }
                            // Retain the existing decoded audio and allow the CDN to recover.
                            // Cap the window so a permanently unavailable final range cannot
                            // strand a playlist indefinitely. Tick/Next can supersede this load.
                            int elapsed = unchecked(Environment.TickCount - recoveryStart);
                            if (elapsed < 0) elapsed = Int32.MaxValue;
                            if (elapsed >= 60000)
                            {
                                Log.Write("CDN stream recovery exhausted: " + track.Name +
                                    ", range " + offset + "-" + end + ", " +
                                    elapsed + "ms, " + recoveryCycles + " cycles; " + recoveryError.Message);
                                throw new IOException("Audio stream unavailable after recovery", recoveryError);
                            }
                            Thread.Sleep(500);
                        }
                    }
                    if (gen != _gen) return;
                    if (recoveryCycles > 0)
                        Log.Write("CDN stream recovered: " + track.Name + ", range " + offset +
                            "-" + end + " after " + recoveryCycles + " failed cycles");
                    lock (_audioLock)
                    {
                        if (gen != _gen) return;
                        if (Native.AudioFeed(r.Body) != r.Body.Length)
                            throw new IOException("Native decoder rejected downloaded audio bytes");
                    }
                    offset += r.Body.Length;
                }
            }
            catch (Exception ex)
            {
                if (gen != _gen) return;
                Log.Write("Load failed: " + ex);
                bool loading = _state == PlayState.Loading;
                if (loading && !(ex is UnplayableException) && _loadRetry == 0)
                {
                    _loadRetry = 1;
                    int retryGen;
                    lock (_audioLock) { retryGen = ++_gen; try { Native.AudioStop(); } catch { } }
                    _loadStartedTick = Environment.TickCount;
                    Log.Write("Playback recovery: retrying track once after load failure: " + ex.Message);
                    ThreadPool.QueueUserWorkItem(delegate { Load(track, startMs, retryGen); });
                    return;
                }
                _endFired = true;
                Fail(ex.Message);
                Fire(TrackUnplayable);
            }
        }
    }
}
