// LoginForm.cs - first-run "Log in with Spotify". Opens Spotify's login page in a browser (default,
// or a chosen one such as Serpent) and waits for it to come back to 127.0.0.1:5588. If you log in on a
// different computer, paste the address it ends up on (the one starting http://127.0.0.1:5588/login?code=).
using System;
using System.Diagnostics;
using System.Drawing;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Spotipaq.Engine;

namespace Spotipaq
{
    public sealed class LoginForm : Form
    {
        readonly SpotifyEngine _engine;
        readonly OAuthLogin _oauth = new OAuthLogin();
        readonly Label _status = new Label();
        readonly TextBox _url = new TextBox(), _paste = new TextBox();
        readonly System.Windows.Forms.Timer _poll = new System.Windows.Forms.Timer();
        bool _finishing;
        static string BrowserFile { get { return Path.Combine(Log.Folder, "browser.txt"); } }

        public LoginForm(SpotifyEngine engine)
        {
            _engine = engine;
            Text = "Log in to Spotipaq";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(500, 352);
            BackColor = Theme.Main; ForeColor = Theme.Text; Font = Theme.Normal;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            Label title = new Label();
            title.Text = "Log in with your Spotify Premium account";
            title.Font = Theme.Heading; title.AutoSize = true; title.Location = new Point(24, 20);
            Controls.Add(title);

            Label info = new Label();
            info.Text = "Spotipaq opens Spotify's own login page. After you log in, the browser comes back to Spotipaq " +
                        "automatically. You only need to do this once.";
            info.ForeColor = Theme.SubText; info.SetBounds(24, 52, 452, 36);
            Controls.Add(info);

            Button open = MakeButton("Open login page", 24, 98, 150, true);
            open.Click += delegate { OpenIn(null); };
            Button other = MakeButton("Use another browser...", 184, 98, 170, false);
            other.Click += delegate { ChooseBrowser(); };
            Button copy = MakeButton("Copy link", 364, 98, 112, false);
            copy.Click += delegate { try { Clipboard.SetText(_oauth.AuthUrl); _status.Text = "Login link copied."; } catch { } };

            _url.ReadOnly = true; _url.Text = _oauth.AuthUrl; Style(_url); _url.SetBounds(24, 138, 452, 20);
            Controls.Add(_url);

            Label pasteInfo = new Label();
            pasteInfo.Text = "Logged in on a different computer? Paste the address it ended on (http://127.0.0.1:5588/login?code=...):";
            pasteInfo.ForeColor = Theme.SubText; pasteInfo.SetBounds(24, 176, 452, 32);
            Controls.Add(pasteInfo);
            Style(_paste); _paste.SetBounds(24, 212, 360, 20);
            Controls.Add(_paste);
            Button use = MakeButton("Use", 394, 209, 82, false);
            use.Click += delegate
            {
                try { _oauth.SetCodeManually(_paste.Text.Trim()); }
                catch (Exception ex) { _status.Text = ex.Message; }
            };

            _status.ForeColor = Theme.SubText; _status.SetBounds(24, 246, 452, 52);
            _status.Text = "Waiting for you to log in...";
            Controls.Add(_status);

            Button recv = MakeButton("Get login from my other computer...", 24, 312, 250, false);
            recv.Click += delegate { StartReceiver(); };

            Button cancel = MakeButton("Continue offline", 336, 312, 140, false);
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            CancelButton = cancel;

            _poll.Interval = 100;
            _poll.Tick += delegate { if (!_finishing && _oauth.WaitForCode(0)) Finish(); };
        }

        Button MakeButton(string text, int x, int y, int w, bool primary)
        {
            Button b = new Button();
            b.Text = text; b.SetBounds(x, y, w, 26);
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = primary ? Theme.Accent : Color.FromArgb(80, 80, 80);
            b.BackColor = primary ? Theme.Accent : Theme.Bar;
            b.ForeColor = Color.White;
            b.UseVisualStyleBackColor = false;
            Controls.Add(b);
            return b;
        }

        static void Style(TextBox t)
        {
            t.BorderStyle = BorderStyle.FixedSingle;
            t.BackColor = Color.FromArgb(18, 18, 18);
            t.ForeColor = Color.White;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            try { _oauth.StartListening(); }
            catch (Exception ex)
            {
                _status.Text = "Can't listen on 127.0.0.1:5588 (" + ex.Message + "). Use the paste box instead.";
                Log.Write("OAuth listen failed: " + ex.Message);
            }
            _poll.Start();
        }

        void OpenIn(string browser)
        {
            if (browser == null && File.Exists(BrowserFile))
            {
                string saved = File.ReadAllText(BrowserFile).Trim();
                if (File.Exists(saved)) browser = saved;
            }
            try
            {
                if (browser != null) Process.Start(browser, "\"" + _oauth.AuthUrl + "\"");
                else Process.Start(_oauth.AuthUrl);
                _status.Text = "Log in in the browser window. This page will continue by itself.";
            }
            catch (Exception ex) { _status.Text = "Couldn't open the browser: " + ex.Message + " - use Copy link."; }
        }

        void ChooseBrowser()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = "Choose a browser that can show modern websites (e.g. Serpent)";
                d.Filter = "Programs (*.exe)|*.exe";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try { if (!Directory.Exists(Log.Folder)) Directory.CreateDirectory(Log.Folder); File.WriteAllText(BrowserFile, d.FileName); } catch { }
                OpenIn(d.FileName);
            }
        }

        LanReceiver _recv;

        /// <summary>No browser needed here: another computer with Spotipaq sends its login across the home network.</summary>
        void StartReceiver()
        {
            if (_recv == null)
            {
                try
                {
                    _recv = new LanReceiver();
                    _recv.Received += OnLanLogin;
                    _recv.Start();
                }
                catch (Exception ex)
                {
                    _recv = null;
                    _status.Text = "Couldn't start listening (" + ex.Message + "). Is another Spotipaq open?";
                    return;
                }
            }
            List<string> ips = LanLogin.LocalAddresses();
            _status.Text = "Waiting for your other computer...\r\nPIN: " + _recv.Pin + (ips.Count > 0 ? "     (this computer: " + String.Join(", ", ips.ToArray()) + ")" : "") +
                "\r\nThere: click your name (top right) > Send login to another device.";
        }

        void OnLanLogin(object sender, LanCredentialsEventArgs e)
        {
            try
            {
                BeginInvoke(new MethodInvoker(delegate ()
                {
                    if (_finishing) return;
                    _finishing = true;
                    _status.Text = "Login received - connecting to Spotify...";
                    Cursor = Cursors.WaitCursor;
                    Thread t = new Thread(delegate ()
                    {
                        Exception error = null;
                        try { _engine.LoginWithCredentials(e.Credentials); }
                        catch (Exception ex) { error = ex; Log.Write("LAN login failed: " + ex); }
                        BeginInvoke(new MethodInvoker(delegate ()
                        {
                            Cursor = Cursors.Default;
                            if (error == null) { DialogResult = DialogResult.OK; Close(); return; }
                            _finishing = false;
                            _status.Text = "That login didn't work: " + error.Message + "  Log in again on the other computer and resend.";
                        }));
                    });
                    t.IsBackground = true;
                    t.Start();
                }));
            }
            catch (InvalidOperationException) { }      // window already closed
        }

        void Finish()
        {
            _finishing = true;
            _status.Text = "Logging in to Spotify...";
            Cursor = Cursors.WaitCursor;
            Thread t = new Thread(delegate ()
            {
                Exception error = null;
                try { _engine.LoginWithToken(_oauth.RequestToken()); }
                catch (Exception ex) { error = ex; Log.Write("Login failed: " + ex); }
                BeginInvoke(new MethodInvoker(delegate ()
                {
                    Cursor = Cursors.Default;
                    if (error == null) { DialogResult = DialogResult.OK; Close(); return; }
                    _status.Text = error.Message + "  (Close this window and start Spotipaq again to retry.)";
                }));
            });
            t.IsBackground = true;
            t.Start();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _poll.Stop();
            _oauth.Dispose();
            if (_recv != null) _recv.Dispose();
            base.OnFormClosed(e);
        }
    }

    /// <summary>"Send login to another device": picks a computer that is waiting on the login screen and sends it.</summary>
    public sealed class SendLoginForm : Form
    {
        sealed class Device
        {
            public string Name, Address;
            public override string ToString() { return Name + "  (" + Address + ")"; }
        }

        readonly LoginCredentials _creds;
        readonly ListBox _list = new ListBox();
        readonly TextBox _host = new TextBox(), _pin = new TextBox();
        readonly Label _status = new Label();
        Button _send, _scan;

        public SendLoginForm(LoginCredentials creds)
        {
            _creds = creds;
            Text = "Send login to another device";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(460, 340);
            BackColor = Theme.Main; ForeColor = Theme.Text; Font = Theme.Normal;

            Label info = new Label();
            info.Text = "On the other computer, start Spotipaq and choose \"Get login from my other computer...\". It will show a PIN and appear below.";
            info.ForeColor = Theme.SubText; info.SetBounds(20, 16, 420, 40);
            Controls.Add(info);

            _list.SetBounds(20, 62, 330, 80);
            _list.BorderStyle = BorderStyle.FixedSingle; _list.BackColor = Color.FromArgb(18, 18, 18); _list.ForeColor = Color.White;
            _list.SelectedIndexChanged += delegate { Device d = _list.SelectedItem as Device; if (d != null) _host.Text = d.Address; };
            Controls.Add(_list);
            _scan = MakeButton("Look again", 360, 62, 80, false);
            _scan.Click += delegate { Scan(); };

            Label h = new Label(); h.Text = "Address:"; h.ForeColor = Theme.SubText; h.SetBounds(20, 156, 80, 20); Controls.Add(h);
            Style(_host); _host.SetBounds(104, 154, 336, 20); Controls.Add(_host);
            Label p = new Label(); p.Text = "PIN:"; p.ForeColor = Theme.SubText; p.SetBounds(20, 188, 80, 20); Controls.Add(p);
            Style(_pin); _pin.SetBounds(104, 186, 120, 20); _pin.MaxLength = 6; Controls.Add(_pin);

            _status.ForeColor = Theme.SubText; _status.SetBounds(20, 224, 420, 60); Controls.Add(_status);

            _send = MakeButton("Send login", 20, 296, 120, true);
            _send.Click += delegate { Send(); };
            Button close = MakeButton("Close", 340, 296, 100, false);
            close.Click += delegate { Close(); };
            CancelButton = close; AcceptButton = _send;
        }

        Button MakeButton(string text, int x, int y, int w, bool primary)
        {
            Button b = new Button();
            b.Text = text; b.SetBounds(x, y, w, 26);
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = primary ? Theme.Accent : Color.FromArgb(80, 80, 80);
            b.BackColor = primary ? Theme.Accent : Theme.Bar;
            b.ForeColor = Color.White;
            b.UseVisualStyleBackColor = false;
            Controls.Add(b);
            return b;
        }

        static void Style(TextBox t)
        {
            t.BorderStyle = BorderStyle.FixedSingle;
            t.BackColor = Color.FromArgb(18, 18, 18);
            t.ForeColor = Color.White;
        }

        protected override void OnShown(EventArgs e) { base.OnShown(e); Scan(); }

        void Scan()
        {
            _status.Text = "Looking for computers that are waiting for a login...";
            _scan.Enabled = false;
            Thread t = new Thread(delegate ()
            {
                List<KeyValuePair<string, string>> found = LanLogin.Discover(1800);
                try
                {
                    BeginInvoke(new MethodInvoker(delegate ()
                    {
                        _scan.Enabled = true;
                        _list.Items.Clear();
                        foreach (KeyValuePair<string, string> kv in found)
                        {
                            Device d = new Device(); d.Name = kv.Key; d.Address = kv.Value;
                            _list.Items.Add(d);
                        }
                        if (_list.Items.Count > 0) { _list.SelectedIndex = 0; _status.Text = "Found it. Type the PIN shown on the other computer and press Send login."; _pin.Focus(); }
                        else _status.Text = "Nothing found. Make sure the other computer is on the login screen (and allow it through the firewall if Windows asks), or type its address above.";
                    }));
                }
                catch (InvalidOperationException) { }
            });
            t.IsBackground = true;
            t.Start();
        }

        void Send()
        {
            string host = _host.Text.Trim(), pin = _pin.Text.Trim();
            if (host.Length == 0 || pin.Length != 6) { _status.Text = "Pick the computer (or type its address) and enter the 6-digit PIN."; return; }
            _send.Enabled = false;
            _status.Text = "Sending...";
            Thread t = new Thread(delegate ()
            {
                string error = null;
                try { LanLogin.Send(host, pin, _creds); }
                catch (Exception ex) { error = ex.Message; Log.Write("Send login failed: " + ex.Message); }
                try
                {
                    BeginInvoke(new MethodInvoker(delegate ()
                    {
                        _send.Enabled = true;
                        if (error == null) { MessageBox.Show(this, "Sent. The other computer is logging in now.", "Spotipaq", MessageBoxButtons.OK, MessageBoxIcon.Information); Close(); }
                        else _status.Text = error;
                    }));
                }
                catch (InvalidOperationException) { }
            });
            t.IsBackground = true;
            t.Start();
        }
    }
}
