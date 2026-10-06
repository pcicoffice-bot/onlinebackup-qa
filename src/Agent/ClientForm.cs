#if NET40
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using OnlineBackup.Core;

namespace OnlineBackup.Agent
{
    /// <summary>The customer's screen talks to the backup service on this computer (the same local API the service always served).</summary>
    public interface IClientApi { Msg Call(string op, Msg body, IDictionary<string, string> query); }

    /// <summary>The service's local API: http://127.0.0.1:port with the key from ui.txt (only this computer, only with the key).</summary>
    public sealed class LocalApi : IClientApi
    {
        readonly string baseUrl, key;
        public LocalApi(string uiUrl)
        {
            var u = new Uri(uiUrl.Trim());
            baseUrl = "http://127.0.0.1:" + u.Port + "/api/"; key = u.Fragment.TrimStart('#');
        }
        public Msg Call(string op, Msg body, IDictionary<string, string> query)
        {
            var q = query == null || query.Count == 0 ? "" : "?" + string.Join("&", query.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value ?? "")).ToArray());
            var r = (HttpWebRequest)WebRequest.Create(baseUrl + op + q);
            r.Method = body == null ? "GET" : "POST"; r.Headers["X-Key"] = key; r.Timeout = 600000; r.ReadWriteTimeout = 600000; r.Proxy = null;
            if (body != null) { var b = body.ToBytes(); r.ContentType = "application/xml"; r.ContentLength = b.Length; using (var s = r.GetRequestStream()) s.Write(b, 0, b.Length); }
            try { using (var w = (HttpWebResponse)r.GetResponse()) using (var s = w.GetResponseStream()) return Msg.Read(s); }
            catch (WebException e)
            {
                var w = e.Response as HttpWebResponse;
                if (w == null) throw new AgentException(0, "SERVICE", "The backup service on this computer does not answer.");
                // CLI-130 (owner's screen: "Cannot access a disposed object 'HttpWebResponse'"): the status is read before the
                // response is closed — after it, .NET Framework throws and the real message was lost
                var status = (int)w.StatusCode; Msg m;
                using (w) using (var s = w.GetResponseStream()) { try { m = Msg.Read(s); } catch (Exception) { m = new Msg(); } }
                throw new AgentException(status, m["code"] ?? m["error"] ?? "", m["message"] ?? ("Error " + status));
            }
        }
    }

    /// <summary>
    /// CLI-100 (owner: "a real program, not a web page"): the customer's backup program as a Windows window — the
    /// product's name, logo and colours, a menu (backup status, restore, new backup, security, help), the backups with
    /// "Back up now", restore of chosen files, two-step verification, calls to the IT company, and an icon by the clock.
    /// Hebrew and Arabic right to left. The work is done by the backup service (the same local API as before).
    /// </summary>
    public sealed class ClientForm : Form
    {
        readonly IClientApi api;
        string lang = "en";
        Msg state = new Msg();
        string page = "status";
        Color brandColor = Color.FromArgb(79, 70, 229), accent = Color.FromArgb(249, 115, 22);
        Image logo;
        readonly Panel header = new Panel(), nav = new Panel(), body = new Panel();
        readonly Panel content = new Panel();
        readonly NotifyIcon tray = new NotifyIcon();
        readonly System.Windows.Forms.Timer poll = new System.Windows.Forms.Timer { Interval = 4000 };
        List<Msg> jobs = new List<Msg>();
        bool closing;
        public bool StartHidden;
        bool preview;   // the pictures: made-up data, no refresh in the background
        string navKey;
        Msg upd = new Msg(); DateTime updAt = DateTime.MinValue;

        static readonly Color Ink = Color.FromArgb(15, 23, 42), Muted = Color.FromArgb(100, 116, 139), Line = Color.FromArgb(226, 232, 240), Ok = Color.FromArgb(4, 120, 87), Bad = Color.FromArgb(185, 28, 28), Warn = Color.FromArgb(180, 83, 9), Bg = Color.FromArgb(244, 246, 250);

        string T(string s, params object[] a) { return L.T(lang, s, a); }
        bool Rtl { get { return L.Rtl(lang); } }
        RightToLeft Dir { get { return Rtl ? RightToLeft.Yes : RightToLeft.No; } }

        public ClientForm(IClientApi api)
        {
            this.api = api;
            Font = new Font("Segoe UI", 10f); ClientSize = new Size(1040, 680); MinimumSize = new Size(860, 560); StartPosition = FormStartPosition.CenterScreen; BackColor = Bg;
            AutoScaleMode = AutoScaleMode.Dpi;
            try { Icon = Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetExecutingAssembly().Location); tray.Icon = Icon; } catch (Exception) { tray.Icon = SystemIcons.Shield; }
            header.Dock = DockStyle.Top; header.Height = 72; header.Paint += PaintHeader;
            nav.Dock = DockStyle.Left; nav.Width = 220; nav.BackColor = Color.White; nav.Paint += (s, e) => e.Graphics.DrawLine(new Pen(Line), Rtl ? 0 : nav.Width - 1, 0, Rtl ? 0 : nav.Width - 1, nav.Height);
            body.Dock = DockStyle.Fill; body.AutoScroll = true; body.Padding = new Padding(0);
            content.Location = new Point(28, 20); body.Controls.Add(content);
            Controls.Add(body); Controls.Add(nav); Controls.Add(header);
            Resize += (s, e) => { if (WindowState == FormWindowState.Minimized) { Hide(); return; } Layout2(); };
            poll.Tick += (s, e) => Refresh2(false);
            // CLI-110: the icon by the clock — open, back up everything now, the state at a glance
            tray.Visible = true; tray.Text = "Backup";
            tray.DoubleClick += (s, e) => ShowFromTray();
            FormClosing += (s, e) => { if (!closing && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); tray.ShowBalloonTip(3000, Text, T("The backup keeps running in the background."), ToolTipIcon.Info); } };
            Load += (s, e) => { if (preview) return; Refresh2(true); poll.Start(); if (StartHidden) BeginInvoke(new Action(Hide)); };
        }

        void ShowFromTray() { Show(); WindowState = FormWindowState.Normal; Activate(); }

        void BuildTrayMenu()
        {
            var m = new ContextMenuStrip { RightToLeft = Dir };
            m.Items.Add(T("Open"), null, (s, e) => ShowFromTray());
            m.Items.Add(T("Back up now"), null, (s, e) => { foreach (var st in Mine()) Do(() => api.Call("backup", new Msg().Set("set", st["id"]), null), T("The backup has started")); });
            m.Items.Add(upd["available"] == "1" ? "⬆ " + T("Update to {0}", upd["latest"]) : T("Check for updates"), null, (s, e) => { if (upd["available"] == "1") { ShowFromTray(); UpdateNow(); } else { updAt = DateTime.MinValue; Refresh2(true); } });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(T("Close the window"), null, (s, e) => { closing = true; tray.Visible = false; Close(); });
            tray.ContextMenuStrip = m;
        }

        // ---------------------------------------------------------------- data

        List<Msg> Mine() { return state.List("sets").Where(s => s["mine"] != "0").ToList(); }

        void Refresh2(bool full)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Msg st = null, jb = null; string err = null;
                try { st = api.Call("state", null, null); jb = api.Call("jobs", null, null); } catch (Exception e) { err = e.Message; }
                Msg u = null;
                if (st != null && (DateTime.UtcNow - updAt).TotalMinutes > 30) { updAt = DateTime.UtcNow; try { u = api.Call("update", null, null); } catch (Exception) { } }
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        var before = Signature();
                        if (u != null) upd = u;
                        if (st != null) { state = st; jobs = jb.List("jobs"); ApplyBrand(); }
                        else state.Set("offline", err);
                        // the sign-in screen is never drawn again by the poll (the person is typing); only when the computer got connected
                        bool typing = before.StartsWith("0|") && state["registered"] == "0";
                        // CLI-120 (owner: "a bug found is fixed in the whole program"): the poll draws again only the status
                        // page; a page with fields, a tree or choices (sign-in, restore, new backup, security, help) keeps what
                        // the person is doing — it is drawn again when they act or move to another page
                        bool quiet = typing || page != "status";
                        if (full ? !typing || !connectShown : before != Signature() && !quiet) Render();
                        UpdateTray();
                    }));
                }
                catch (Exception) { }
            });
        }

        bool connectShown;
        string Signature() { return (state["registered"] == "0" ? "0|" : "1|") + state.ToString() + "|" + upd.ToString() + "|" + string.Join(",", jobs.Select(j => j["id"] + j["state"]).ToArray()); }

        void ApplyBrand()
        {
            // I18N-040 (owner): English until the person picks a language by hand (then that one, remembered)
            var l = !string.IsNullOrEmpty(saved) ? L.Norm(saved) : InstalledLanguage() ?? "en";
            lang = L.Languages.Contains(l) ? l : "en";
            brandColor = Hex(state["color"], brandColor); accent = Hex(state["accent"], accent);
            Text = state["product"] ?? "Backup";
            tray.Text = (Text.Length > 60 ? Text.Substring(0, 60) : Text);
        }
        string saved { get { try { return (string)Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\OnlineBackup", "Language", null); } catch (Exception) { return null; } } set { try { Microsoft.Win32.Registry.SetValue(@"HKEY_CURRENT_USER\Software\OnlineBackup", "Language", value ?? ""); } catch (Exception) { } } }

        /// <summary>The language picked in the installation (written beside the program, for every Windows user).</summary>
        static string InstalledLanguage()
        {
            try { var f = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "language.txt"); if (File.Exists(f)) { var v = File.ReadAllText(f).Trim(); if (v.Length > 0) return L.Norm(v); } } catch (Exception) { }
            return null;
        }

        static Color Hex(string h, Color d) { try { if (!string.IsNullOrEmpty(h) && h.Length == 7 && h[0] == '#') return ColorTranslator.FromHtml(h); } catch (Exception) { } return d; }

        void UpdateTray()
        {
            BuildTrayMenu();
            var failed = Mine().Any(s => s["result"] != "" && !s["result"].StartsWith("BS_STOP_SUCCESS", StringComparison.Ordinal));
            var running = jobs.Any(j => j["state"] == "running");
            tray.Text = (Text + " — " + (running ? T("Running…") : failed ? T("Attention needed") : T("This computer is protected"))).Substring(0, Math.Min(63, (Text + " — " + (running ? T("Running…") : failed ? T("Attention needed") : T("This computer is protected"))).Length));
        }

        /// <summary>Runs a call of the service in the background; a message when it is done, a sign-in when it needs one.</summary>
        void Do(Func<Msg> work, string done, Action<Msg> then = null, bool refresh = true)
        {
            Cursor = Cursors.WaitCursor;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Msg r = null; Exception err = null;
                try { r = work(); } catch (Exception e) { err = e; }
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        Cursor = Cursors.Default;
                        var ae = err as AgentException;
                        if (ae != null && ae.Status == 401) { if (SignIn()) Do(work, done, then); return; }
                        if (err != null) { Message(L.Tr(lang, err.Message), true); return; }
                        if (done != null) Message(done, false);
                        if (then != null) then(r);
                        if (refresh) Refresh2(true);   // only after a change — a read (points, files) must not draw the page again (it would read again, forever)
                    }));
                }
                catch (Exception) { }
            });
        }

        void Message(string text, bool bad)
        {
            MessageBox.Show(this, text, Text, MessageBoxButtons.OK, bad ? MessageBoxIcon.Warning : MessageBoxIcon.Information, MessageBoxDefaultButton.Button1, Rtl ? MessageBoxOptions.RightAlign | MessageBoxOptions.RtlReading : 0);
        }

        /// <summary>Restore, changes and new backups need the customer's password (and the code, when two-step is on).</summary>
        bool SignIn()
        {
            using (var d = new Form { Text = T("Sign in"), FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(440, 300), Font = Font, RightToLeft = Dir, BackColor = Color.White })
            {
                var stack = new Stacker(d, 24, 20, 392, Rtl);
                stack.Text(T("Sign in"), 14f, true);
                stack.Text(T("Restoring and adding backups require your password."), 9.5f, false, Muted);
                stack.Text(T("Password"), 10f, true).Margin = new Padding(0, 10, 0, 2);
                var pw = stack.Box(true);
                stack.Text(T("Verification code (if enabled)"), 10f, true).Margin = new Padding(0, 10, 0, 2);
                var otp = stack.Box(false, true);
                var err = stack.Text("", 9.5f, true, Bad);
                stack.Done();
                var ok = Btn(T("Sign in"), true); var cancel = Btn(T("Cancel"), false);
                ok.Top = cancel.Top = d.ClientSize.Height - 54; ok.Left = Rtl ? 24 : d.ClientSize.Width - 24 - ok.Width; cancel.Left = Rtl ? ok.Right + 10 : ok.Left - 10 - cancel.Width;
                d.Controls.Add(ok); d.Controls.Add(cancel); d.AcceptButton = ok; d.CancelButton = cancel;
                cancel.Click += (s, e) => d.DialogResult = DialogResult.Cancel;
                ok.Click += (s, e) =>
                {
                    try { api.Call("login", new Msg().Set("password", pw.Text).Set("otp", otp.Text), null); d.DialogResult = DialogResult.OK; }
                    catch (Exception ex) { err.Text = T("Sign-in failed") + ": " + L.Tr(lang, ex.Message); }
                };
                return d.ShowDialog(this) == DialogResult.OK;
            }
        }

        // ---------------------------------------------------------------- frame

        void PaintHeader(object s, PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (var bg = new LinearGradientBrush(header.ClientRectangle, Color.FromArgb(11, 16, 32), Color.FromArgb(32, 30, 80), 0f)) g.FillRectangle(bg, header.ClientRectangle);
            using (var b = new SolidBrush(brandColor)) g.FillRectangle(b, 0, header.Height - 3, header.Width, 3);
            int lw = 0, x = Rtl ? header.Width - 24 : 24;
            if (logo != null)
            {
                lw = 42; var r = new Rectangle(Rtl ? x - lw : x, (header.Height - lw) / 2, lw, lw);
                g.FillRectangle(Brushes.White, r); g.DrawImage(logo, new Rectangle(r.X + 3, r.Y + 3, r.Width - 6, r.Height - 6)); lw += 12;
            }
            var fmt = new StringFormat { Alignment = StringAlignment.Near, FormatFlags = Rtl ? StringFormatFlags.DirectionRightToLeft : 0 };
            var area = Rtl ? new RectangleF(200, 12, x - lw - 200, 30) : new RectangleF(x + lw, 12, header.Width - x - lw - 200, 30);
            using (var f = new Font("Segoe UI Semibold", 15f)) g.DrawString(state["product"] ?? "Backup", f, Brushes.White, area, fmt);
            var sub = !string.IsNullOrEmpty(state["slogan"]) ? state["slogan"] : (state["company"] ?? "");
            using (var f = new Font("Segoe UI", 9.5f)) using (var b = new SolidBrush(Color.FromArgb(190, 200, 220))) g.DrawString(sub, f, b, new RectangleF(area.X, 42, area.Width, 20), fmt);
            var who = string.Join("  ·  ", new[] { state["login"], state["computer"] }.Where(x => !string.IsNullOrEmpty(x)).ToArray());   // no lone dot before the computer
            using (var f = new Font("Segoe UI", 9f)) using (var b = new SolidBrush(Color.FromArgb(190, 200, 220)))
                g.DrawString(who, f, b, Rtl ? new RectangleF(20, 28, 180, 20) : new RectangleF(header.Width - 200, 28, 180, 20), new StringFormat { Alignment = StringAlignment.Far, FormatFlags = Rtl ? StringFormatFlags.DirectionRightToLeft : 0 });
        }

        static readonly string[][] Menu = { new[] { "status", "Backup status" }, new[] { "restore", "Restore" }, new[] { "new", "New backup" }, new[] { "security", "Security" }, new[] { "help", "Help" } };

        void BuildNav()
        {
            var nk = page + "|" + lang + "|" + state["powered"] + "|" + upd.ToString();
            if (nk == navKey) return;   // the menu is built again only when it changes
            navKey = nk;
            var oldNav = nav.Controls.Cast<Control>().ToList(); nav.Controls.Clear(); foreach (var c in oldNav) c.Dispose(); nav.Dock = Rtl ? DockStyle.Right : DockStyle.Left;
            int y = 16;
            foreach (var m in Menu)
            {
                var key = m[0];
                var b = new Button { Text = "  " + T(m[1]), Width = nav.Width - 24, Height = 42, Left = 12, Top = y, FlatStyle = FlatStyle.Flat, TextAlign = ContentAlignment.MiddleLeft, RightToLeft = Dir,
                    Font = new Font(page == key ? "Segoe UI Semibold" : "Segoe UI", 10.5f), BackColor = page == key ? Color.FromArgb(17, 24, 39) : Color.White, ForeColor = page == key ? Color.White : Ink, Cursor = Cursors.Hand };
                b.FlatAppearance.BorderSize = 0; b.FlatAppearance.MouseOverBackColor = page == key ? Color.FromArgb(17, 24, 39) : Color.FromArgb(241, 245, 249);
                b.Click += (s, e) => { page = key; Render(); };
                nav.Controls.Add(b); y += 48;
            }
            // UPD-020: the version, and "Update" when the backup server has a newer client
            if (upd["available"] == "1")
            {
                var ub = Btn("⬆ " + T("Update"), true); ub.Width = nav.Width - 24; ub.Left = 12; ub.Top = nav.Height - 130; ub.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
                ub.Click += (s, e) => UpdateNow(); nav.Controls.Add(ub);
            }
            var ver = new LinkLabel { Text = T("Version {0}", upd["current"] ?? "—") + "  ·  " + T("Check for updates"), AutoSize = false, Width = nav.Width - 24, Height = 20, Left = 12, Top = nav.Height - 88, Anchor = AnchorStyles.Bottom | AnchorStyles.Left, Font = new Font("Segoe UI", 8.5f), RightToLeft = Dir, LinkColor = Muted };
            ver.LinkClicked += (s, e) => { updAt = DateTime.MinValue; Do(() => api.Call("update", null, null), null, r => { upd = r; navKey = null; BuildNav(); if (r["available"] == "1") UpdateNow(); else Message(T("The newest version is installed ({0}).", r["current"]), false); }, false); };
            nav.Controls.Add(ver);
            var lb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = nav.Width - 24, Left = 12, Top = nav.Height - 48, Anchor = AnchorStyles.Bottom | AnchorStyles.Left, Font = new Font("Segoe UI", 9f) };
            foreach (var l in L.Languages) lb.Items.Add(new LangItem(l));
            lb.SelectedItem = lb.Items.Cast<LangItem>().FirstOrDefault(i => i.Code == lang);
            lb.SelectedIndexChanged += (s, e) => { saved = ((LangItem)lb.SelectedItem).Code; lang = saved; Render(); };
            nav.Controls.Add(lb);
            if (!string.IsNullOrEmpty(state["powered"]))
                nav.Controls.Add(new Label { Text = T("Powered by {0}", state["powered"]), AutoSize = false, Width = nav.Width - 24, Height = 20, Left = 12, Top = nav.Height - 64, Anchor = AnchorStyles.Bottom | AnchorStyles.Left, ForeColor = Muted, Font = new Font("Segoe UI", 8.5f), RightToLeft = Dir });
        }

        sealed class LangItem
        {
            public readonly string Code;
            public LangItem(string c) { Code = c; }
            static readonly Dictionary<string, string> Names = new Dictionary<string, string> { { "en", "English" }, { "he", "עברית" } };
            public override string ToString() { string n; return Names.TryGetValue(Code, out n) ? n : Code; }
        }

        void Layout2()
        {
            if (state["registered"] == "0") { if (!cfBusy && Visible) Render(); return; }   // the sign-in screen is laid out again
            int w = body.ClientSize.Width - 56; if (w < 400) w = 400;
            content.Width = w; content.Left = 28;
            foreach (Control c in content.Controls) if (c.Tag as string == "full") c.Width = w;
        }

        Button Btn(string text, bool primary)
        {
            var font = new Font("Segoe UI Semibold", 10f);
            var b = new Button { Text = text, Height = 36, FlatStyle = FlatStyle.Flat, Font = font, Cursor = Cursors.Hand, AutoSize = false, Width = Math.Max(110, TextRenderer.MeasureText(text, font).Width + 30), MinimumSize = new Size(44, 30) };
            if (primary) { b.BackColor = accent; b.ForeColor = Color.White; b.FlatAppearance.BorderSize = 0; } else { b.BackColor = Color.White; b.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225); }
            return b;
        }

        void UpdateNow()
        {
            if (MessageBox.Show(this, T("Update to version {0}", upd["latest"]) + "\n\n" + T("The backup stops for about a minute and starts again with the new version. Your backups and settings are kept."), Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1, Rtl ? MessageBoxOptions.RightAlign | MessageBoxOptions.RtlReading : 0) != DialogResult.OK) return;
            Do(() => api.Call("update", new Msg().Set("apply", "1"), null), T("Updating… the program opens again when the update is done."), r => { upd = new Msg(); });
        }

        // ---------------------------------------------------------------- pages

        void Render()
        {
            SuspendLayout();
            RightToLeft = Dir; RightToLeftLayout = false;
            header.Invalidate(); BuildNav();
            var old = content.Controls.Cast<Control>().ToList(); content.Controls.Clear(); foreach (var c in old) c.Dispose();
            var w = Math.Max(400, body.ClientSize.Width - 56);
            content.Width = w; content.Left = 28; content.Top = 20; body.RightToLeft = RightToLeft.No; content.RightToLeft = RightToLeft.No;
            var st = new Stacker(content, 0, 0, w, Rtl);
            nav.Visible = state["registered"] != "0";
            if (state["registered"] == "0") { ConnectPage(st); st.Done(); content.Height = st.Height + 20; ResumeLayout(true); return; }
            if (!string.IsNullOrEmpty(state["offline"])) st.Banner(T("No connection to the backup server right now: {0}", L.Tr(lang, state["offline"])), Warn);
            switch (page)
            {
                case "status": Status(st); break;
                case "restore": RestorePage(st); break;
                case "new": NewPage(st); break;
                case "security": SecurityPage(st); break;
                case "help": HelpPage(st); break;
            }
            st.Done(); content.Height = st.Height + 20;
            ResumeLayout(true);
        }

        // ---------------------------------------------------------------- SETUP-C50: sign in on a computer not connected yet

        readonly Dictionary<string, string> cf = new Dictionary<string, string> { { "server", "" }, { "company", "" }, { "email", "" }, { "phone", "" }, { "login", "" }, { "password", "" }, { "password2", "" }, { "otp", "" } };
        bool cfOther, cfNew, cfBusy; string cfError = "";

        TextBox CField(Stacker st, string key, string label, bool password = false, bool ltr = false, string hint = null)
        {
            st.Text(label, 10f, true).Margin = new Padding(0, 10, 0, 2);
            var b = st.Box(password, ltr); b.Text = cf[key]; b.Enabled = !cfBusy;
            b.TextChanged += (s, e) => cf[key] = b.Text;
            if (hint != null) st.Text(hint, 9f, false, Muted);
            return b;
        }

        /// <summary>The program's first screen after the installation: the backup server (the package's, or another
        /// address), then the user name and password — or a new account.</summary>
        void ConnectPage(Stacker st)
        {
            var w = Math.Min(st.Width, 520);
            var cs = new Stacker(content, Rtl ? st.Width - w : 0, 0, w, Rtl);
            connectShown = true;
            var def = state["defaultServer"] ?? "";
            // the language, right here (the menu with its choice is hidden until the computer is connected)
            var lb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160, Font = new Font("Segoe UI", 9.5f), Margin = new Padding(0, 0, 0, 6) };
            foreach (var l in new[] { "en", "he" }.Concat(L.Languages.Where(x => x != "en" && x != "he"))) lb.Items.Add(new LangItem(l));
            lb.SelectedItem = lb.Items.Cast<LangItem>().FirstOrDefault(i => i.Code == lang);
            lb.SelectedIndexChanged += (s2, e2) => { saved = ((LangItem)lb.SelectedItem).Code; lang = saved; Render(); };
            cs.Add(lb);
            cs.Text(T("Sign in to {0}", state["product"] ?? "Backup"), 16f, true);
            cs.Text(T("Sign in with the user name and password you received, or open a new account."), 10f, false, Muted);
            cs.Text(T("Backup server"), 10f, true).Margin = new Padding(0, 14, 0, 2);
            if (def.Length > 0 && !cfOther) { var sl = cs.Text(def, 10.5f, false, Ink); sl.RightToLeft = RightToLeft.No; if (Rtl) sl.TextAlign = ContentAlignment.TopRight; }   // an address reads left to right, on the reading side
            var other = cs.Check(T("Use a different server address"), cfOther || def.Length == 0); other.Enabled = !cfBusy && def.Length > 0;
            other.CheckedChanged += (s, e) => { cfOther = other.Checked; cfError = ""; Render(); };
            if (cfOther || def.Length == 0) { var sb = CField(cs, "server", T("Server address"), false, true, T("For example backup.company.com:8443 — your IT company gives it.")); sb.Margin = new Padding(0, 2, 0, 2); }
            var have = new RadioButton { Text = T("I already have an account"), Checked = !cfNew, AutoSize = false, Width = w, Height = 28, Margin = new Padding(0, 14, 0, 0), TextAlign = ContentAlignment.MiddleLeft, CheckAlign = ContentAlignment.MiddleLeft, Enabled = !cfBusy };
            var fresh = new RadioButton { Text = T("I am a new customer — open an account"), Checked = cfNew, AutoSize = false, Width = w, Height = 28, Margin = new Padding(0, 2, 0, 0), TextAlign = ContentAlignment.MiddleLeft, CheckAlign = ContentAlignment.MiddleLeft, Enabled = !cfBusy };
            cs.Add(have); cs.Add(fresh);
            have.CheckedChanged += (s, e) => { if (have.Checked && cfNew) { cfNew = false; cfError = ""; Render(); } };
            fresh.CheckedChanged += (s, e) => { if (fresh.Checked && !cfNew) { cfNew = true; cfError = ""; Render(); } };
            if (cfNew)
            {
                CField(cs, "company", T("Company name"));
                CField(cs, "email", T("E-mail"), false, true, T("Reports and alerts are sent here."));
                CField(cs, "phone", T("Phone (optional)"), false, true);
                CField(cs, "login", T("Choose a user name"), false, true);
                CField(cs, "password", T("Choose a password"), true);
                CField(cs, "password2", T("The password again"), true, false, T("At least 8 characters, with at least one letter. Keep it: it is needed to restore files."));
            }
            else
            {
                CField(cs, "login", T("User name"), false, true);
                CField(cs, "password", T("Password"), true);
                CField(cs, "otp", T("Code from the authenticator app (only if it is on)"), false, true);
            }
            var go = Btn(cfBusy ? T("Connecting…") : cfNew ? T("Open the account") : T("Sign in"), true); go.Enabled = !cfBusy; go.Margin = new Padding(0, 16, 0, 2);
            cs.Add(go); AcceptButton = go;
            go.Click += (s, e) => Connect();
            if (cfError.Length > 0) cs.Text(L.Tr(lang, cfError), 10f, true, Bad);
            cs.Text(T("Need help? Contact {0}.", Support()), 9f, false, Muted).Margin = new Padding(0, 14, 0, 2);
            cs.Done();
            // the outer stacker only measures
            var spacer = new Panel { Width = 1, Height = cs.Height, Margin = new Padding(0) }; st.Add(spacer);
        }

        void Connect()
        {
            cfError = "";
            var server = cfOther || string.IsNullOrEmpty(state["defaultServer"]) ? cf["server"].Trim() : state["defaultServer"];
            if (server.Length == 0) cfError = "Write the server address, e.g. https://backup.company.com:8443";
            else if (cfNew)
            {
                if (cf["company"].Trim() == "" || cf["email"].Trim() == "" || cf["login"].Trim() == "") cfError = "Fill in the company, the e-mail and a user name.";
                else if (cf["password"].Length < 8 || !cf["password"].Any(char.IsLetter)) cfError = "Password: at least 8 characters, with at least one letter.";
                else if (cf["password"] != cf["password2"]) cfError = "The two passwords are not the same.";
            }
            else if (cf["login"].Trim() == "" || cf["password"] == "") cfError = "Write the user name and the password you received.";
            if (cfError.Length > 0) { Render(); return; }
            cfBusy = true; Render();
            Msg chk = null; Exception err = null;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { chk = api.Call("server-check", new Msg().Set("server", server).Set("lang", lang), null); } catch (Exception e) { err = e; }
                try { BeginInvoke(new Action(() => AfterCheck(chk, err))); } catch (Exception) { }
            });
        }

        void AfterCheck(Msg chk, Exception err)
        {
            if (err != null) { cfBusy = false; cfError = err.Message; Render(); return; }
            if (cfNew && chk["signupOpen"] == "0") { cfBusy = false; cfError = T("This server does not open new accounts. Contact {0}.", Support()); Render(); return; }
            if (chk["confirm"] == "1" && MessageBox.Show(this, T("The server uses its own certificate. Make sure this fingerprint is the one your provider gave you:") + "\n\n" + chk["fingerprint"] + "\n\n" + T("I trust this server"), Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2, Rtl ? MessageBoxOptions.RightAlign | MessageBoxOptions.RtlReading : 0) != DialogResult.Yes)
            { cfBusy = false; Render(); return; }
            var accept = "0";
            var contractText = cfNew ? chk["contractNew"] : chk["contract"];
            if (!string.IsNullOrEmpty(contractText)) { if (!Agreement(contractText)) { cfBusy = false; Render(); return; } accept = "1"; }
            var body = new Msg().Set("server", chk["server"]).Set("pin", chk["pin"] ?? "").Set("mode", cfNew ? "new" : "existing").Set("login", cf["login"].Trim()).Set("password", cf["password"]).Set("lang", lang).Set("accept", accept).Set("contractVersion", chk["contractVersion"] ?? "");
            if (cfNew) body.Set("company", cf["company"].Trim()).Set("email", cf["email"].Trim()).Set("phone", cf["phone"].Trim());
            else if (cf["otp"].Trim() != "") body.Set("otp", cf["otp"].Trim());
            Exception e2 = null;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { api.Call("connect", body, null); } catch (Exception e) { e2 = e; }
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        cfBusy = false;
                        if (e2 != null) { cfError = e2.Message; Render(); return; }
                        foreach (var k in cf.Keys.ToList()) if (k != "server") cf[k] = "";
                        AcceptButton = null; page = "new";
                        Message(T("This computer is connected. Now choose what to back up."), false);
                        Refresh2(true);
                    }));
                }
                catch (Exception) { }
            });
        }

        /// <summary>The IT company's agreement: read, tick, continue.</summary>
        bool Agreement(string text)
        {
            using (var d = new Form { Text = T("Agreement"), FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(600, 470), Font = Font, RightToLeft = Dir, BackColor = Color.White })
            {
                var stack = new Stacker(d, 24, 16, 552, Rtl);
                stack.Text(T("The terms of your provider. Please read them before you go on."), 10f, false, Muted);
                var box = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Text = text.Replace("\r\n", "\n").Replace("\n", "\r\n"), Width = 552, Height = 300, BackColor = Color.White, Font = new Font("Segoe UI", 9.5f) };
                stack.Add(box);
                var ck = stack.Check(T("I have read and accept the agreement"), false);
                stack.Done();
                var ok = Btn(T("Continue"), true); var cancel = Btn(T("Cancel"), false); ok.Enabled = false;
                ck.CheckedChanged += (s, e) => ok.Enabled = ck.Checked;
                ok.Top = cancel.Top = d.ClientSize.Height - 54; ok.Left = Rtl ? 24 : d.ClientSize.Width - 24 - ok.Width; cancel.Left = Rtl ? ok.Right + 10 : ok.Left - 10 - cancel.Width;
                d.Controls.Add(ok); d.Controls.Add(cancel); d.CancelButton = cancel;
                ok.Click += (s, e) => d.DialogResult = DialogResult.OK; cancel.Click += (s, e) => d.DialogResult = DialogResult.Cancel;
                return d.ShowDialog(this) == DialogResult.OK;
            }
        }

        string Support() { var s = state["company"]; if (string.IsNullOrEmpty(s)) s = T("support"); if (!string.IsNullOrEmpty(state["phone"])) s += " " + state["phone"]; return s; }

        string When(string unixMsOrText)
        {
            long ms; if (!long.TryParse(unixMsOrText, out ms)) return unixMsOrText ?? "";
            CultureInfo ci; try { ci = new CultureInfo(lang == "zh" ? "zh-CN" : lang); } catch (Exception) { ci = CultureInfo.InvariantCulture; }
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms).ToLocalTime().ToString("g", ci);
        }

        string ResultText(string r)
        {
            if (string.IsNullOrEmpty(r)) return T("Not yet");
            if (r == "BS_STOP_SUCCESS") return T("Completed successfully");
            if (r.StartsWith("BS_STOP_SUCCESS", StringComparison.Ordinal)) return T("Completed with warnings");
            if (r.Contains("QUOTA")) return T("The quota is full");
            return T("Failed");
        }

        void Status(Stacker st)
        {
            var mine = Mine();
            var failed = mine.Where(s => s["result"] != "" && !s["result"].StartsWith("BS_STOP_SUCCESS", StringComparison.Ordinal)).ToList();
            var partial = mine.Where(s => s["result"] == "" || (s["result"] != "BS_STOP_SUCCESS" && s["result"].StartsWith("BS_STOP_SUCCESS", StringComparison.Ordinal))).ToList();
            if (mine.Count == 0) st.Hero(T("No backups are set up yet. Add one in the \"New backup\" tab."), "", Muted);
            else if (failed.Count > 0) st.Hero(T("Attention needed"), T("A backup did not finish. Click \"Back up now\" or contact {0}.", Support()), Bad);
            else if (partial.Count > 0) st.Hero(T("Partly protected"), T("Some backups finished with warnings or have not run yet."), Warn);
            else st.Hero(T("This computer is protected"), T("AI is watching every backup for ransomware"), Ok);
            foreach (var s in mine)
            {
                var running = jobs.FirstOrDefault(j => j["set"] == s["id"] && j["state"] == "running");
                var res = s["result"];
                var color = running != null ? brandColor : res == "" ? Muted : res == "BS_STOP_SUCCESS" ? Ok : res.StartsWith("BS_STOP_SUCCESS", StringComparison.Ordinal) ? Warn : Bad;
                var card = st.Card(color);
                var cs = new Stacker(card, Rtl ? 18 + 260 : 18, 12, card.Width - 36 - 260, Rtl);   // the text on the reading side, the buttons on the other
                cs.Text(s["name"], 12f, true);
                cs.Text(s["sources"], 9f, false, Muted);
                cs.Text(running != null ? (running["kind"] == "restore" ? T("Restoring…") : T("Backing up now…")) : T("Automatic backup every day at {0}", s["hour"]) + "  ·  " + T("last backup: {0}", s["last"] == "" ? T("Not yet") : When(s["last"])) + "  ·  " + ResultText(res), 9.5f, false, color);
                cs.Done();
                card.Height = Math.Max(92, cs.Height + 24);
                var now = Btn(T("Back up now"), true); now.Enabled = running == null;
                var id = s["id"];
                now.Click += (o, e) => Do(() => api.Call("backup", new Msg().Set("set", id), null), T("The backup has started"));
                var ch = Btn(T("Change"), false);
                var set = s;
                ch.Click += (o, e) => EditSet(set);
                now.Top = ch.Top = (card.Height - 36) / 2;
                now.Left = Rtl ? 18 : card.Width - 18 - now.Width; ch.Left = Rtl ? now.Right + 8 : now.Left - 8 - ch.Width;
                now.Anchor = ch.Anchor = AnchorStyles.Top | (Rtl ? AnchorStyles.Left : AnchorStyles.Right);
                card.Controls.Add(now); card.Controls.Add(ch); now.BringToFront(); ch.BringToFront();
            }
            if (jobs.Count > 0)
            {
                st.Text(T("Recent activity"), 12f, true).Margin = new Padding(0, 18, 0, 6);
                var lv = st.List(new[] { T("Action"), T("Time"), T("Status"), T("Details") }, new[] { 22, 22, 18, 38 }, Math.Min(8, jobs.Count) * 24 + 30);
                foreach (var j in jobs.Take(20))
                {
                    var name = (state.List("sets").FirstOrDefault(x => x["id"] == j["set"]) ?? new Msg())["name"] ?? "";
                    var it = new ListViewItem(new[] { (j["kind"] == "restore" ? T("Restore") : T("Backup")) + " — " + name, When(j["started"]), j["state"] == "running" ? T("Running…") : j["state"] == "ok" ? T("Succeeded") : T("Failed"), L.Tr(lang, j["detail"] ?? "") });
                    it.ForeColor = j["state"] == "failed" ? Bad : j["state"] == "ok" ? Ok : Ink;
                    lv.Items.Add(it);
                }
            }
        }

        // -- restore: a set, a point, the files (none chosen = everything), the folder
        void RestorePage(Stacker st)
        {
            var sets = state.List("sets");
            st.Text(T("Restore"), 15f, true);
            if (sets.Count == 0) { st.Text(T("There are no backups to restore."), 10f, false, Muted); return; }
            st.Text(T("Backup"), 10f, true).Margin = new Padding(0, 10, 0, 2);
            var setBox = st.Combo(sets.Select(s => s["name"] + (s["mine"] == "0" ? "  (" + s["computer"] + ")" : "")).ToArray(), 0);
            st.Text(T("Restore point (date)"), 10f, true).Margin = new Padding(0, 10, 0, 2);
            var pointBox = st.Combo(new string[0], -1);
            st.Text(T("Files (none selected = everything)"), 10f, true).Margin = new Padding(0, 10, 0, 2);
            var filter = st.Box(false); filter.Text = ""; var filterHint = st.Text(T("Filter by file or folder name"), 8.5f, false, Muted);
            var tree = st.Tree(260);
            st.Text(T("Restore to folder"), 10f, true).Margin = new Padding(0, 10, 0, 2);
            var target = st.Box(false, true); target.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "Restore");
            var browse = Btn("…", false); browse.MinimumSize = new Size(44, 30); st.Beside(target, browse);
            browse.Click += (s, e) => { using (var f = new FolderBrowserDialog { SelectedPath = target.Text }) if (f.ShowDialog(this) == DialogResult.OK) target.Text = f.SelectedPath; };
            var overwrite = st.Check(T(" Replace existing files").Trim(), false);
            var go = Btn(T("Restore"), true); st.Add(go);
            List<Msg> points = new List<Msg>(); List<Msg> files = new List<Msg>();
            Action loadFiles = () =>
            {
                tree.Nodes.Clear(); if (pointBox.SelectedIndex < 0) return;
                var setId = sets[setBox.SelectedIndex]["id"]; var pt = points[pointBox.SelectedIndex]["id"];
                tree.Nodes.Add(T("Loading files…"));
                Do(() => api.Call("files", null, new Dictionary<string, string> { { "set", setId }, { "point", pt } }), null, r => { files = r.List("files"); FillTree(tree, files, filter.Text); }, false);
            };
            Action loadPoints = () =>
            {
                pointBox.Items.Clear(); tree.Nodes.Clear();
                var setId = sets[setBox.SelectedIndex]["id"];
                Do(() => api.Call("points", null, new Dictionary<string, string> { { "set", setId } }), null, r =>
                {
                    points = r.List("points");
                    if (points.Count == 0) { tree.Nodes.Add(T("This backup has no restore points yet — it runs at the scheduled time, or click \"Back up now\".")); return; }
                    for (int i = 0; i < points.Count; i++) pointBox.Items.Add(i == 0 ? T("Latest — {0}", When(points[i]["time"])) : When(points[i]["time"]));
                    pointBox.SelectedIndex = 0;
                }, false);
            };
            setBox.SelectedIndexChanged += (s, e) => loadPoints();
            pointBox.SelectedIndexChanged += (s, e) => loadFiles();
            filter.TextChanged += (s, e) => FillTree(tree, files, filter.Text);
            go.Click += (s, e) =>
            {
                if (pointBox.SelectedIndex < 0) return;
                var m = new Msg().Set("set", sets[setBox.SelectedIndex]["id"]).Set("point", points[pointBox.SelectedIndex]["id"]).Set("target", target.Text).Set("overwrite", overwrite.Checked ? "1" : "0");
                foreach (var p in Checked(tree.Nodes)) m.Add("paths", new Msg().Set("p", p));
                Do(() => api.Call("restore", m, null), T("The restore has started — follow it in the \"Backup status\" tab"));
            };
            if (sets.Count > 0) BeginInvoke(loadPoints);
        }

        static void FillTree(TreeView tree, List<Msg> files, string filter)
        {
            tree.BeginUpdate(); tree.Nodes.Clear();
            var f = (filter ?? "").Trim();
            int n = 0;
            foreach (var x in files)
            {
                var path = x["path"] ?? ""; if (f.Length > 0 && path.IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (++n > 5000) break;
                var parts = path.Replace('/', '\\').Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
                var nodes = tree.Nodes; string acc = path.StartsWith("/") ? "/" : "";
                for (int i = 0; i < parts.Length; i++)
                {
                    acc = acc.Length == 0 || acc == "/" ? acc + parts[i] + (i == 0 && path.Length > 1 && path[1] == ':' ? "\\" : "") : acc.TrimEnd('\\') + "\\" + parts[i];
                    var node = nodes.Cast<TreeNode>().FirstOrDefault(t => t.Text == parts[i]);
                    if (node == null) { node = nodes.Add(parts[i]); node.Tag = i == parts.Length - 1 ? path : acc; }
                    nodes = node.Nodes;
                }
            }
            if (tree.Nodes.Count == 1) tree.Nodes[0].Expand();
            tree.EndUpdate();
        }

        static IEnumerable<string> Checked(TreeNodeCollection nodes)
        {
            foreach (TreeNode n in nodes) { if (n.Checked) yield return (string)n.Tag; else foreach (var c in Checked(n.Nodes)) yield return c; }
        }

        // -- a new backup and a change of one: the folders (a tree of this computer), the time
        void NewPage(Stacker st)
        {
            st.Text(T("New backup"), 15f, true);
            if (state["canAdd"] == "0") { st.Text(T("Your IT provider adds new backups — contact {0}.", Support()), 10f, false, Muted); return; }
            st.Text(T("Files are encrypted on this computer before they leave, with your password. Without the password nothing can be restored — keep it safe."), 9.5f, false, Muted);
            var types = new[] { new[] { "FILE", "Files and folders" }, new[] { "MSSQL", "Microsoft SQL Server" }, new[] { "SYSTEMSTATE", "Windows System State" }, new[] { "BAREMETAL", "Whole computer (bare-metal image)" } };
            st.Text(T("Backup type"), 10f, true).Margin = new Padding(0, 10, 0, 2);
            var type = st.Combo(types.Select(x => T(x[1])).ToArray(), 0);
            st.Text(T("Backup name"), 10f, true).Margin = new Padding(0, 10, 0, 2);
            var name = st.Box(false); name.Text = T("Documents");
            var picker = new FolderPicker(this, api, T, Rtl);
            st.Text(T("Folders to back up"), 10f, true).Margin = new Padding(0, 10, 0, 2);
            st.Add(picker.Control(st.Width, 240));
            st.Text(T("Daily backup time"), 10f, true).Margin = new Padding(0, 10, 0, 2);
            var time = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Width = 110, Value = DateTime.Today.AddHours(22) };
            st.Add(time);
            var dbUser = new TextBox { Width = 260 };
            var add = Btn(T("New backup"), true); st.Add(add);
            type.SelectedIndexChanged += (s, e) => { var k = types[type.SelectedIndex][0]; picker.Enabled(k == "FILE" || k == "BAREMETAL"); name.Text = T(types[type.SelectedIndex][1]); };
            add.Click += (s, e) =>
            {
                var k = types[type.SelectedIndex][0];
                var m = new Msg().Set("type", k).Set("name", name.Text).Set("sources", string.Join("\n", picker.Included.ToArray())).Set("exclude", string.Join("\n", picker.Excluded.ToArray()))
                    .Set("hour", time.Value.Hour).Set("minute", time.Value.Minute);
                Do(() => api.Call("addset", m, null), T("The backup was added"), r => { page = "status"; });
            };
        }

        void EditSet(Msg s)
        {
            using (var d = new Form { Text = T("Change the backup"), FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(640, 560), Font = Font, RightToLeft = Dir, BackColor = Color.White })
            {
                var st = new Stacker(d, 24, 18, 592, Rtl);
                st.Text(T("Change the backup") + " — " + s["name"], 13f, true);
                st.Text(T("Folders to back up"), 10f, true).Margin = new Padding(0, 10, 0, 2);
                var picker = new FolderPicker(this, api, T, Rtl);
                foreach (var p in (s["src"] ?? "").Split('\n').Where(x => x.Trim().Length > 0)) picker.Included.Add(p.Trim());
                foreach (var p in (s["skip"] ?? "").Split('\n').Where(x => x.Trim().Length > 0)) picker.Excluded.Add(p.Trim());
                var tree = picker.Control(592, 280); st.Add(tree);
                if (state["canSources"] == "0") { picker.Enabled(false); st.Text(T("Your IT provider manages the folders of this backup."), 9f, false, Muted); }
                st.Text(T("Daily backup time"), 10f, true).Margin = new Padding(0, 10, 0, 2);
                int hh, mm; int.TryParse(s["hh"], out hh); int.TryParse(s["mm"], out mm);
                var time = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Width = 110, Value = DateTime.Today.AddHours(hh).AddMinutes(mm), Enabled = state["canSchedule"] != "0" };
                st.Add(time);
                if (state["canSchedule"] == "0") st.Text(T("Your IT provider manages the time of this backup."), 9f, false, Muted);
                st.Done();
                var save = Btn(T("Save and exit"), true); var exit = Btn(T("Exit without saving"), false);
                save.Top = exit.Top = d.ClientSize.Height - 56; save.Left = Rtl ? 24 : d.ClientSize.Width - 24 - 160; exit.Left = Rtl ? 200 : d.ClientSize.Width - 24 - 160 - 10 - 190;
                d.Controls.Add(save); d.Controls.Add(exit); d.CancelButton = exit;
                exit.Click += (o, e) => d.DialogResult = DialogResult.Cancel;
                save.Click += (o, e) =>
                {
                    var m = new Msg().Set("set", s["id"]).Set("hour", time.Value.Hour).Set("minute", time.Value.Minute);
                    if (state["canSources"] != "0") m.Set("sources", string.Join("\n", picker.Included.ToArray())).Set("exclude", string.Join("\n", picker.Excluded.ToArray()));
                    d.DialogResult = DialogResult.OK;
                    Do(() => api.Call("editset", m, null), T("The backup was changed"));
                };
                d.ShowDialog(this);
            }
        }

        void SecurityPage(Stacker st)
        {
            st.Text(T("Two-step verification"), 15f, true);
            Msg sec = null; try { sec = api.Call("security", null, null); } catch (Exception) { sec = new Msg(); }
            if (sec["totp"] == "1")
            {
                st.Text(T("On — signing in needs your password and a code from the authenticator app."), 10.5f, false, Ok);
                if (sec["required"] == "1") st.Text(T("Your provider requires two-step verification."), 9.5f, false, Muted);
                else { var off = Btn(T("Switch off"), false); st.Add(off); off.Click += (s, e) => { if (MessageBox.Show(this, T("Switch two-step verification off?"), Text, MessageBoxButtons.YesNo) == DialogResult.Yes) Do(() => api.Call("totp-disable", new Msg(), null), T("Two-step verification is off")); }; }
                return;
            }
            st.Text(T("Protect restores and changes with a code from an authenticator app on your phone (Google Authenticator, Microsoft Authenticator, Authy…)."), 10f, false, Muted);
            var on = Btn(T("Turn on"), true); st.Add(on);
            on.Click += (s, e) => Do(() => api.Call("totp-enable", new Msg(), null), null, r =>
            {
                var codes = string.Join("   ", r.List("codes").Select(c => c["code"]).ToArray());
                var code = Microsoft.VisualBasic.Interaction.InputBox(T("Or type the key:") + "\n" + r["secret"] + "\n\n" + T("Keep these one-time backup codes in a safe place (each works once if the phone is lost):") + "\n" + codes + "\n\n" + T("Type the code the app shows now:"), T("Set up two-step verification"), "");
                if (!string.IsNullOrEmpty(code)) Do(() => api.Call("totp-confirm", new Msg().Set("code", code.Trim()), null), T("Two-step verification is on"));
            });
        }

        void HelpPage(Stacker st)
        {
            st.Text(T("Help"), 15f, true);
            st.Text(T("Contact {0}.", Support()), 10f, false, Muted);
            st.Text(T("Subject"), 10f, true).Margin = new Padding(0, 10, 0, 2);
            var subject = st.Box(false);
            st.Text(T("Description"), 10f, true).Margin = new Padding(0, 10, 0, 2);
            var desc = st.Box(false); desc.Multiline = true; desc.Height = 110; desc.ScrollBars = ScrollBars.Vertical;
            var send = Btn(T("Send"), true); st.Add(send);
            send.Click += (s, e) =>
            {
                if (subject.Text.Trim().Length == 0) { Message(T("Write a subject."), true); return; }
                Do(() => api.Call("help", new Msg().Set("subject", subject.Text).Set("description", desc.Text), null), T("The call was sent to {0}", Support()));
            };
            st.Text(T("My calls"), 12f, true).Margin = new Padding(0, 18, 0, 6);
            var lv = st.List(new[] { T("Call"), T("Opened"), T("Status") }, new[] { 55, 25, 20 }, 180);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var r = api.Call("help", null, null);
                    BeginInvoke(new Action(() => { foreach (var c in r.List("tickets")) lv.Items.Add(new ListViewItem(new[] { c["subject"] ?? "", When(c["opened"] ?? c["created"]), L.Tr(lang, c["status"] ?? "") })); if (lv.Items.Count == 0) lv.Items.Add(T("No calls yet.")); }));
                }
                catch (Exception) { }
            });
        }

        // ---------------------------------------------------------------- start

        /// <summary>The desktop shortcut (and Windows start-up with --tray): the program, with the service's local API.</summary>
        public static int Run(string uiUrl, bool hidden)
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            using (var f = new ClientForm(new LocalApi(uiUrl)) { StartHidden = hidden }) Application.Run(f);
            return 0;
        }

        /// <summary>The robot: every page as a picture, with made-up data (a sample API).</summary>
        public static void Screens(string outDir, string lang)
        {
            var t = new Thread(() =>
            {
                Directory.CreateDirectory(outDir);
                Application.EnableVisualStyles();
                using (var f = new ClientForm(new SampleApi(lang)) { StartPosition = FormStartPosition.Manual, Location = new Point(0, 0), preview = true })
                {
                    f.Show(); f.upd = new SampleApi(lang).Call("update", null, null); f.state = new SampleApi(lang).Call("state", null, null); f.jobs = new SampleApi(lang).Call("jobs", null, null).List("jobs"); f.ApplyBrand(); f.lang = L.Norm(lang); f.Render();
                    foreach (var p in new[] { "status", "restore", "new", "security", "help" })
                    {
                        f.page = p; f.Render(); f.Activate(); f.Refresh();
                        for (int i = 0; i < 25; i++) { Application.DoEvents(); Thread.Sleep(40); }
                        using (var bmp = new Bitmap(f.Width, f.Height))
                        {
                            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(f.Location, Point.Empty, f.Size);
                            bmp.Save(Path.Combine(outDir, "client_" + p + "_" + f.lang + ".png"), ImageFormat.Png);
                        }
                    }
                    // SETUP-C50: the first screen after the installation — sign in, or a new account
                    f.state.Set("registered", 0).Set("defaultServer", "https://backup.acme-it.com:8443");
                    foreach (var mode in new[] { "signin", "signup" })
                    {
                        f.cfNew = mode == "signup"; f.Render(); f.Activate(); f.Refresh();
                        for (int i = 0; i < 15; i++) { Application.DoEvents(); Thread.Sleep(40); }
                        using (var bmp = new Bitmap(f.Width, f.Height))
                        {
                            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(f.Location, Point.Empty, f.Size);
                            bmp.Save(Path.Combine(outDir, "client_" + mode + "_" + f.lang + ".png"), ImageFormat.Png);
                        }
                    }
                    f.closing = true; f.tray.Visible = false; f.Close();
                }
            });
            t.SetApartmentState(ApartmentState.STA); t.Start(); t.Join();
        }

        /// <summary>Made-up data for the pictures.</summary>
        sealed class SampleApi : IClientApi
        {
            readonly string lang; public SampleApi(string l) { lang = l; }
            public Msg Call(string op, Msg body, IDictionary<string, string> query)
            {
                var now = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
                switch (op)
                {
                    case "state":
                        return new Msg().Set("registered", 1).Set("computer", "OFFICE-PC").Set("login", "dana-office").Set("product", "Acme Backup").Set("company", "Acme IT").Set("phone", "03-5550000").Set("color", "#4F46E5").Set("accent", "#F97316").Set("language", lang)
                            .Add("sets", new Msg().Set("id", "1").Set("name", "Office files").Set("sources", @"C:\Users\Dana\Documents; D:\Shared").Set("src", "C:\\Users\\Dana\\Documents\nD:\\Shared").Set("mine", 1).Set("hour", "22:00").Set("hh", 22).Set("mm", 0).Set("last", now - 3600000).Set("result", "BS_STOP_SUCCESS"))
                            .Add("sets", new Msg().Set("id", "2").Set("name", "SQL Server — ERP").Set("sources", @"Microsoft SQL Server\SQLEXPRESS\ERP").Set("mine", 1).Set("hour", "01:00").Set("hh", 1).Set("mm", 0).Set("last", now - 7200000).Set("result", "BS_STOP_SUCCESS_WITH_WARNING"));
                    case "jobs":
                        return new Msg().Add("jobs", new Msg().Set("id", "a").Set("kind", "backup").Set("set", "1").Set("state", "ok").Set("detail", "New 12, updated 3, sent 18400000 bytes").Set("started", now - 3600000));
                    case "points": return new Msg().Add("points", new Msg().Set("id", "p2").Set("time", now - 3600000)).Add("points", new Msg().Set("id", "p1").Set("time", now - 90000000));
                    case "files": return new Msg().Add("files", new Msg().Set("path", @"C:\Users\Dana\Documents\Budget 2026.xlsx")).Add("files", new Msg().Set("path", @"C:\Users\Dana\Documents\Contracts\Lease.pdf")).Add("files", new Msg().Set("path", @"D:\Shared\plan.docx"));
                    case "security": return new Msg().Set("totp", 0);
                    case "update": return new Msg().Set("current", "0.1.73").Set("latest", "0.1.80").Set("available", 1);
                    case "help": return new Msg();
                    case "dirs": return query != null && query.ContainsKey("path") ? new Msg().Add("dirs", new Msg().Set("path", query["path"].TrimEnd('\\') + "\\Documents")).Add("dirs", new Msg().Set("path", query["path"].TrimEnd('\\') + "\\Pictures")) : new Msg().Add("dirs", new Msg().Set("path", "C:\\")).Add("dirs", new Msg().Set("path", "D:\\"));
                }
                return new Msg();
            }
        }
    }

    /// <summary>The folders of this computer as a tree with check boxes: ✓ backs up a folder and everything in it; a folder unticked inside a ticked one is skipped.</summary>
    sealed class FolderPicker
    {
        readonly Form owner; readonly IClientApi api; readonly Func<string, object[], string> t; readonly bool rtl;
        readonly FolderSelection sel = new FolderSelection();
        public List<string> Included { get { return sel.Included; } }
        public List<string> Excluded { get { return sel.Excluded; } }
        TreeView tree; bool filling;
        public FolderPicker(Form owner, IClientApi api, Func<string, object[], string> t, bool rtl) { this.owner = owner; this.api = api; this.t = t; this.rtl = rtl; }

        public Control Control(int width, int height)
        {
            tree = new TreeView { CheckBoxes = true, Width = width, Height = height, RightToLeft = rtl ? RightToLeft.Yes : RightToLeft.No, RightToLeftLayout = rtl, Font = new Font("Segoe UI", 9.5f), BorderStyle = BorderStyle.FixedSingle };
            Load(null, tree.Nodes);
            tree.BeforeExpand += (s, e) => { if (e.Node.Nodes.Count == 1 && e.Node.Nodes[0].Tag == null) { e.Node.Nodes.Clear(); Load((string)e.Node.Tag, e.Node.Nodes); } };
            tree.AfterCheck += (s, e) =>
            {
                if (filling) return;
                // UX-27: a line that is not a folder ("No access to this folder.", "…") cannot be ticked
                if (!sel.Toggle((string)e.Node.Tag, e.Node.Checked)) { filling = true; e.Node.Checked = false; filling = false; return; }
                filling = true; Mark(e.Node.Nodes, e.Node.Checked); filling = false;
            };
            return tree;
        }

        public void Enabled(bool on) { if (tree != null) tree.Enabled = on; }

        void Mark(TreeNodeCollection nodes, bool on) { foreach (TreeNode n in nodes) { if (n.Tag != null) n.Checked = on; Mark(n.Nodes, on); } }

        void Load(string path, TreeNodeCollection into)
        {
            Msg r;
            try { r = api.Call("dirs", null, path == null ? new Dictionary<string, string>() : new Dictionary<string, string> { { "path", path } }); }
            catch (Exception) { return; }
            if (r["denied"] == "1") { into.Add(t("No access to this folder.", new object[0])); return; }
            filling = true;
            foreach (var d in r.List("dirs"))
            {
                var p = d["path"]; var name = path == null ? p : Path.GetFileName(p.TrimEnd('\\'));
                var n = into.Add(name); n.Tag = p;
                n.Checked = Included.Any(x => p.Equals(x, StringComparison.OrdinalIgnoreCase) || p.StartsWith(x.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) && !Excluded.Any(x => p.Equals(x, StringComparison.OrdinalIgnoreCase) || p.StartsWith(x.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
                n.Nodes.Add(new TreeNode("…"));   // loaded when opened
            }
            filling = false;
        }
    }

    /// <summary>Rows one under the other, the full width, aligned to the reading direction (Windows mirrors "left" in Hebrew / Arabic).</summary>
    sealed class Stacker
    {
        readonly Control parent; readonly int x0, y0; public readonly int Width; readonly bool rtl;
        readonly List<Control> rows = new List<Control>();
        public int Height { get; private set; }
        public Stacker(Control parent, int x, int y, int width, bool rtl) { this.parent = parent; x0 = x; y0 = y; Width = width; this.rtl = rtl; }
        RightToLeft Dir { get { return rtl ? RightToLeft.Yes : RightToLeft.No; } }

        // WIN-QA: a field is named by the caption above it (screen readers and Windows UI Automation read the name;
        // without it every field was only "edit")
        string caption;
        public Control Add(Control c)
        {
            if (c.Margin == new Padding(3)) c.Margin = new Padding(0, 6, 0, 2); c.RightToLeft = Dir;
            if (!(c is Label || c is ButtonBase) && caption != null) { if (string.IsNullOrEmpty(c.AccessibleName)) c.AccessibleName = caption; caption = null; }
            rows.Add(c); parent.Controls.Add(c); return c;
        }

        public Label Text(string s, float size = 10f, bool bold = false, Color? color = null)
        {
            var font = new Font(bold ? "Segoe UI Semibold" : "Segoe UI", size);
            var h = TextRenderer.MeasureText(string.IsNullOrEmpty(s) ? " " : s, font, new Size(Width, 0), TextFormatFlags.WordBreak).Height + 2;
            var l = new Label { Text = s, AutoSize = false, Width = Width, Height = h, Font = font, TextAlign = ContentAlignment.TopLeft, Margin = new Padding(0, 6, 0, 2), Tag = "full" };
            if (color.HasValue) l.ForeColor = color.Value;
            Add(l); if (bold) caption = s; return l;
        }
        public TextBox Box(bool password, bool ltr = false)
        {
            var b = new TextBox { Width = Width, Font = new Font("Segoe UI", 11f), UseSystemPasswordChar = password, Margin = new Padding(0, 2, 0, 2), Tag = "full" };
            Add(b);
            if (ltr) { b.RightToLeft = RightToLeft.No; if (rtl) b.TextAlign = HorizontalAlignment.Right; }
            return b;
        }
        public ComboBox Combo(string[] items, int selected)
        {
            var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = Math.Min(Width, 520), Font = new Font("Segoe UI", 10.5f), Margin = new Padding(0, 2, 0, 2) };
            c.Items.AddRange(items); if (selected >= 0 && selected < items.Length) c.SelectedIndex = selected;
            Add(c); return c;
        }
        public CheckBox Check(string label, bool on)
        {
            var c = new CheckBox { Text = label, Checked = on, AutoSize = false, Width = Width, Height = 28, TextAlign = ContentAlignment.MiddleLeft, CheckAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 8, 0, 2), Tag = "full" };
            Add(c); return c;
        }
        public TreeView Tree(int height)
        {
            var t = new TreeView { CheckBoxes = true, Width = Width, Height = height, RightToLeftLayout = rtl, Font = new Font("Segoe UI", 9.5f), BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 4, 0, 2), Tag = "full" };
            Add(t); return t;
        }
        public ListView List(string[] cols, int[] percent, int height)
        {
            var lv = new ListView { View = View.Details, FullRowSelect = true, Width = Width, Height = height, RightToLeftLayout = rtl, Font = new Font("Segoe UI", 9.5f), HeaderStyle = ColumnHeaderStyle.Nonclickable, Margin = new Padding(0, 4, 0, 2), Tag = "full" };
            for (int i = 0; i < cols.Length; i++) lv.Columns.Add(cols[i], (Width - 24) * percent[i] / 100);
            Add(lv); return lv;
        }
        /// <summary>A coloured line with a message.</summary>
        public void Banner(string text, Color color)
        {
            var l = Text(text, 10f, true, Color.White); l.BackColor = color; l.Padding = new Padding(12, 8, 12, 8); l.Height += 16;
        }
        /// <summary>The big state at the top (protected / attention).</summary>
        public void Hero(string title, string sub, Color color)
        {
            var p = new Panel { Width = Width, Height = 84, BackColor = color, Margin = new Padding(0, 0, 0, 12), Tag = "full" };
            var a = new Label { Text = title, AutoSize = false, Left = 20, Top = 14, Width = Width - 40, Height = 30, Font = new Font("Segoe UI Semibold", 15f), ForeColor = Color.White, BackColor = Color.Transparent, RightToLeft = Dir, TextAlign = ContentAlignment.TopLeft };
            var b = new Label { Text = sub, AutoSize = false, Left = 20, Top = 46, Width = Width - 40, Height = 24, Font = new Font("Segoe UI", 10f), ForeColor = Color.FromArgb(235, 255, 255, 255), BackColor = Color.Transparent, RightToLeft = Dir, TextAlign = ContentAlignment.TopLeft };
            p.Controls.Add(a); p.Controls.Add(b); Add(p);
        }
        /// <summary>A white card with a coloured edge on the reading side.</summary>
        public Panel Card(Color edge)
        {
            var p = new Panel { Width = Width, Height = 96, BackColor = Color.White, Margin = new Padding(0, 0, 0, 10), Tag = "full" };
            p.Paint += (s, e) => { using (var b = new SolidBrush(edge)) e.Graphics.FillRectangle(b, rtl ? p.Width - 5 : 0, 0, 5, p.Height); using (var pen = new Pen(Color.FromArgb(226, 232, 240))) e.Graphics.DrawRectangle(pen, 0, 0, p.Width - 1, p.Height - 1); };
            Add(p); return p;
        }
        /// <summary>A small control at the end of the row above (e.g. "…" beside a folder).</summary>
        public void Beside(Control row, Control small)
        {
            row.Width = Width - small.MinimumSize.Width - 8; small.Tag = "beside"; parent.Controls.Add(small); small.RightToLeft = Dir;
            rows.Add(new BesideMarker(row, small));
        }
        sealed class BesideMarker : Control { public readonly Control Row, Small; public BesideMarker(Control r, Control s) { Row = r; Small = s; Visible = false; } }

        /// <summary>Puts the rows in place.</summary>
        public void Done()
        {
            int y = y0;
            foreach (var c in rows)
            {
                var bm = c as BesideMarker;
                if (bm != null) { bm.Small.Top = bm.Row.Top; bm.Small.Height = bm.Row.Height; bm.Small.Left = rtl ? x0 : x0 + Width - bm.Small.Width; if (rtl) bm.Row.Left = x0 + bm.Small.Width + 8; continue; }
                y += c.Margin.Top; c.Top = y;
                c.Left = rtl ? x0 + Width - c.Width : x0;   // a narrower row (a list, a button) sits on the reading side
                y += c.Height + c.Margin.Bottom;
            }
            Height = y - y0;
        }
    }
}
#endif
