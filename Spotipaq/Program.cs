// Program.cs - entry point. Logs in (first run: browser login), then opens the main window.
// Without a login it runs on the built-in demo data ("Continue offline").
using System;
using System.IO;
using System.Windows.Forms;
using Spotipaq.Engine;

namespace Spotipaq
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Log.Write("---- Spotipaq starting ----");

            string native = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SpotipaqNative.dll");
            if (!File.Exists(native))
            {
                MessageBox.Show("SpotipaqNative.dll is missing from the app folder. Running with demo data.", "Spotipaq",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                Application.Run(new MainForm(new MockBackend(), new MockPlayback()));
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
            Application.Run(main);
            if (main.LogOutRequested)
            {
                Log.Write("User logged out");
                engine.LogOut();
                try { System.Diagnostics.Process.Start(Application.ExecutablePath); } catch { }   // back to the login window
            }
        }
    }
}
