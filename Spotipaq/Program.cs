// Program.cs - entry point. Logs in (first run: browser login), then opens the main window.
// Without a login it runs on the built-in demo data ("Continue offline").
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Microsoft.Win32;
using System.Windows.Forms;
using Spotipaq.Engine;

namespace Spotipaq
{
    internal static class Program
    {
        static readonly object PendingLock = new object();
        static readonly System.Collections.Generic.Queue<string> Pending = new System.Collections.Generic.Queue<string>();
        static MainForm _main;
        static bool _restartAfterLogout;
        static TcpListener _listener;
        const int LinkPort = 5587;

        static void QueueLink(string link)
        {
            if (String.IsNullOrEmpty(link) || link.Length > 2048) return;
            lock (PendingLock) Pending.Enqueue(link);
            MainForm form = _main;
            if (form != null && form.IsHandleCreated && !form.IsDisposed)
                try { form.BeginInvoke(new MethodInvoker(DispatchLinks)); } catch (InvalidOperationException) { }
        }

        static void DispatchLinks()
        {
            if (_main == null || _main.IsDisposed) return;
            while (true)
            {
                string link;
                lock (PendingLock)
                {
                    if (Pending.Count == 0) break;
                    link = Pending.Dequeue();
                }
                if (link == "__SPOTIPAQ_ACTIVATE__")
                {
                    if (_main.WindowState == FormWindowState.Minimized)
                        _main.WindowState = FormWindowState.Normal;
                    _main.Activate();
                }
                else _main.OpenSpotifyLink(link);
            }
        }

        static void Listen()
        {
            while (true)
            {
                TcpClient client = null;
                try
                {
                    client = _listener.AcceptTcpClient();
                    client.ReceiveTimeout = 3000;
                    using (NetworkStream stream = client.GetStream())
                    {
                        byte[] bytes = new byte[2048];
                        int len = stream.Read(bytes, 0, bytes.Length);
                        if (len > 0) QueueLink(Encoding.UTF8.GetString(bytes, 0, len).Trim());
                    }
                }
                catch (SocketException) { return; }
                catch (ObjectDisposedException) { return; }
                catch (Exception) { }
                finally { if (client != null) client.Close(); }
            }
        }

        static bool Forward(string link)
        {
            for (int i = 0; i < 12; i++)
            {
                try
                {
                    using (TcpClient client = new TcpClient())
                    {
                        client.Connect(IPAddress.Loopback, LinkPort);
                        byte[] data = Encoding.UTF8.GetBytes(String.IsNullOrEmpty(link) ? "__SPOTIPAQ_ACTIVATE__" : link);
                        client.GetStream().Write(data, 0, data.Length);
                        return true;
                    }
                }
                catch { Thread.Sleep(250); }
            }
            return false;
        }

        // Register only the Spotify URI scheme for this Windows user, not the https: scheme.
        static void RegisterUriScheme()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\spotify"))
                {
                    key.SetValue("", "URL:Spotify Link");
                    key.SetValue("URL Protocol", "");
                    using (RegistryKey cmd = key.CreateSubKey(@"shell\open\command"))
                        cmd.SetValue("", "\"" + Application.ExecutablePath + "\" \"%1\"");
                }
            }
            catch (Exception ex) { Log.Write("Spotify link registration failed: " + ex.Message); }
        }

        [STAThread]
        static void Main(string[] args)
        {
            bool created;
            using (Mutex single = new Mutex(true, @"Local\Spotipaq.SingleInstance", out created))
            {
                if (!created)
                {
                    if (!Forward(args.Length > 0 ? args[0] : ""))
                        MessageBox.Show("Spotipaq is already running but could not receive the link.", "Spotipaq");
                    return;
                }
                try { RunPrimary(args); }
                finally
                {
                    if (_listener != null) _listener.Stop();
                    try { single.ReleaseMutex(); } catch (ApplicationException) { }
                }
            }
            if (_restartAfterLogout)
                try { System.Diagnostics.Process.Start(Application.ExecutablePath); } catch { }
        }

        static void RunPrimary(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Log.Debug("---- Spotipaq starting ----");
            RegisterUriScheme();
            try
            {
                _listener = new TcpListener(IPAddress.Loopback, LinkPort);
                _listener.Start();
                Thread thread = new Thread(Listen);
                thread.IsBackground = true;
                thread.Start();
            }
            catch (Exception ex) { Log.Write("Link listener failed: " + ex.Message); }
            if (args.Length > 0) QueueLink(args[0]);

            string native = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SpotipaqNative.dll");
            if (!File.Exists(native))
            {
                MessageBox.Show("SpotipaqNative.dll is missing from the app folder. Running with demo data.", "Spotipaq",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _main = new MainForm(new MockBackend(), new MockPlayback());
                _main.Shown += delegate { DispatchLinks(); };
                Application.Run(_main);
                return;
            }

            SpotifyEngine engine = new SpotifyEngine();
            bool online = CredentialStore.Exists;
            if (!online)
                using (LoginForm login = new LoginForm(engine))
                    online = login.ShowDialog() == DialogResult.OK;

            ISpotifyBackend backend; IPlayback player;
            if (online)
            {
                SpotifyBackend real = new SpotifyBackend(engine);
                backend = real;
                player = new SpotifyPlayback(engine, real.Sp);
                if (Native.Version() < 2)
                    MessageBox.Show("SpotipaqNative.dll is out of date, so there will be no sound. Rebuild the SpotipaqNative project.",
                        "Spotipaq", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else { backend = new MockBackend(); player = new MockPlayback(); }
            MainForm main = new MainForm(backend, player);
            _main = main;
            main.Shown += delegate { DispatchLinks(); };
            Application.Run(main);
            if (main.LogOutRequested)
            {
                Log.Write("User logged out");
                engine.LogOut();
                _restartAfterLogout = true; // restart after the singleton mutex has been released
            }
        }
    }
}
