// Native.cs - P/Invoke wrapper for SpotipaqNative.dll (https over TLS 1.2 + big-number maths), plus logging.
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Spotipaq.Engine
{
    public sealed class HttpResult
    {
        public int Status;
        public byte[] Body;
        public string Headers;
        public string Text { get { return Body == null ? "" : Encoding.UTF8.GetString(Body); } }
        public bool Ok { get { return Status >= 200 && Status < 300; } }
    }

    public static class Native
    {
        const string Dll = "SpotipaqNative.dll";

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        static extern int spq_http(string method, string url, string headers, byte[] body, int bodyLen,
            out int status, out IntPtr outBody, out int outLen, out IntPtr outHeaders, StringBuilder err, int errCap);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern void spq_free(IntPtr p);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern int spq_modexp(byte[] b, int bl, byte[] e, int el, byte[] m, int ml, byte[] output, int outCap);


        // ---- audio player (spq_audio.c) ----
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int spq_version();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int spq_audio_open(byte[] key, int total);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int spq_audio_feed(byte[] data, int len);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int spq_audio_play(int startMs);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern void spq_audio_pause(int paused);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int spq_audio_seek(int ms);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern void spq_audio_volume(int v);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int spq_audio_position();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int spq_audio_state();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern void spq_audio_stop();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern void spq_last_timing(StringBuilder buf, int cap);

        static string Timing() { try { StringBuilder b = new StringBuilder(200); spq_last_timing(b, b.Capacity); return " [" + b + "]"; } catch { return ""; } }

        public static int Version() { try { return spq_version(); } catch { return 0; } }
        public static bool AudioOpen(byte[] key, int total) { return spq_audio_open(key, total) != 0; }
        public static int AudioFeed(byte[] data) { return spq_audio_feed(data, data.Length); }
        public static bool AudioPlay(int startMs) { return spq_audio_play(startMs) != 0; }
        public static void AudioPause(bool paused) { spq_audio_pause(paused ? 1 : 0); }
        public static void AudioSeek(int ms) { spq_audio_seek(ms); }
        public static void AudioVolume(int v) { spq_audio_volume(v); }
        public static int AudioPosition() { return spq_audio_position(); }
        /// <summary>0 none, 1 buffering, 2 playing, 3 paused, 4 ended, -1 error.</summary>
        public static int AudioState() { return spq_audio_state(); }
        public static void AudioStop() { spq_audio_stop(); }

        /// <summary>Blocking HTTPS request. headers: "Name: value\r\n" lines. Throws on network/TLS failure.</summary>
        public static HttpResult Http(string method, string url, string headers, byte[] body)
        {
            StringBuilder err = new StringBuilder(512);
            int status, len;
            IntPtr pBody, pHeaders;
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            int ok = spq_http(method, url, headers, body, body == null ? 0 : body.Length, out status, out pBody, out len, out pHeaders, err, err.Capacity);
            HttpResult r = new HttpResult();
            try
            {
                if (ok == 0) throw new IOException(method + " " + Short(url) + ": " + err);
                r.Status = status;
                r.Body = new byte[len];
                if (len > 0) Marshal.Copy(pBody, r.Body, 0, len);
                r.Headers = pHeaders == IntPtr.Zero ? "" : Marshal.PtrToStringAnsi(pHeaders);
            }
            finally
            {
                if (pBody != IntPtr.Zero) spq_free(pBody);
                if (pHeaders != IntPtr.Zero) spq_free(pHeaders);
            }
            if (Log.IsVerbose || r.Status >= 400 || sw.ElapsedMilliseconds > 3000)     // routine requests aren't logged
                Log.Write("HTTP " + method + " " + Short(url) + " -> " + r.Status + " (" + r.Body.Length + " bytes, " + sw.ElapsedMilliseconds + " ms)" + Timing());
            return r;
        }

        public static HttpResult Get(string url, string headers) { return Http("GET", url, headers, null); }

        public static HttpResult PostForm(string url, string form)
        {
            return Http("POST", url, "Content-Type: application/x-www-form-urlencoded\r\n", Encoding.ASCII.GetBytes(form));
        }

        /// <summary>base^exp mod m (unsigned big-endian).</summary>
        public static byte[] ModExp(byte[] b, byte[] e, byte[] m)
        {
            byte[] out1 = new byte[m.Length + 1];
            int n = spq_modexp(b, b.Length, e, e.Length, m, m.Length, out1, out1.Length);
            if (n < 0) throw new InvalidOperationException("modexp failed");
            byte[] r = new byte[n];
            Array.Copy(out1, r, n);
            return r;
        }

        static string Short(string url) { int q = url.IndexOf('?'); return q > 0 ? url.Substring(0, q) : url; }
    }

    /// <summary>
    /// Small log in %APPDATA%\Spotipaq\spotipaq.log (never contains secrets). Lines are buffered in memory and written
    /// two seconds after the first one (no per-line file open/close, no timer while idle). Routine chatter (every HTTP
    /// request, AP packets) goes through Debug(), which only logs if a file called verbose.txt exists in that folder.
    /// </summary>
    public static class Log
    {
        static readonly object Lock = new object();
        public static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Spotipaq");
        public static string FilePath { get { return Path.Combine(Folder, "spotipaq.log"); } }
        static readonly bool Verbose = File.Exists(Path.Combine(Folder, "verbose.txt"));
        static StreamWriter _w;
        static System.Threading.Timer _timer;
        static bool _dirty;
        static int _writes;

        public static bool IsVerbose { get { return Verbose; } }

        public static void Debug(string line) { if (Verbose) Write(line); }

        public static void Write(string line)
        {
            try
            {
                lock (Lock)
                {
                    if (_w == null && !Open()) return;
                    _w.Write(DateTime.Now.ToString("HH:mm:ss.fff"));
                    _w.Write("  ");
                    _w.WriteLine(line);
                    if (!_dirty) { _dirty = true; _timer.Change(2000, System.Threading.Timeout.Infinite); }
                    if (++_writes % 100 == 0 && _w.BaseStream.Length > 512 * 1024) { Close(); Open(); }
                }
            }
            catch { }
        }

        /// <summary>Writes anything still buffered (called on exit and before the log is opened in Notepad).</summary>
        public static void Flush()
        {
            try { lock (Lock) { if (_w != null) _w.Flush(); _dirty = false; } }
            catch { }
        }

        static bool Open()
        {
            try
            {
                if (!Directory.Exists(Folder)) Directory.CreateDirectory(Folder);
                FileInfo fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 256 * 1024)
                {
                    string old = FilePath + ".old";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(FilePath, old);
                }
                _w = new StreamWriter(new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8);
                if (_timer == null)
                {
                    _timer = new System.Threading.Timer(delegate { Flush(); }, null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
                    AppDomain.CurrentDomain.ProcessExit += delegate { Flush(); };
                }
                return true;
            }
            catch { _w = null; return false; }
        }

        static void Close()
        {
            try { if (_w != null) { _w.Flush(); _w.Close(); } } catch { }
            _w = null; _dirty = false;
        }
    }
}
