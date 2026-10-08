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
    /// <summary>
    /// CLI-100 (owner: "a real program, not a web page"): the customer's backup program as a Windows window.
    /// Alternative C ("Hybrid", chosen by the owner 08.10.2026; ux-client/IA.md): a light side menu - Home · Backups ·
    /// Restore · History · Settings, Help from the IT company at its bottom; Home = the protection bar (the state, three
    /// facts, Back up now / Restore) with the backups and the recent activity under it, and the card "What was not backed
    /// up and why" when a run left files out (B2: Partial, amber; "Last complete backup" counts only clean runs); Restore in
    /// 3 steps with a bar fixed at the bottom (UX-1) that says what is not restored (B5); History whose rows open in place;
    /// Settings with the sign-in and two-step verification. The rules (states, words, colours, layout) are ClientView's,
    /// tested without Windows. Hebrew right to left. The work is done by the backup service (the same local API as before),
    /// and an icon by the clock (open, back up now, close).
    /// </summary>
    public sealed class ClientForm : Form
    {
        readonly IClientApi api;
        string lang = "en";
        Msg state = new Msg();
        string page = "home";
        readonly Panel nav = new Panel(), body = new Panel(), scroll = new Panel(), bar = new Panel();
        readonly Panel content = new Panel();
        readonly NotifyIcon tray = new NotifyIcon();
        readonly ToolTip tips = new ToolTip();
        readonly System.Windows.Forms.Timer poll = new System.Windows.Forms.Timer { Interval = 4000 };
        readonly System.Windows.Forms.Timer resized = new System.Windows.Forms.Timer { Interval = 250 };
        List<Msg> jobs = new List<Msg>();
        bool closing;
        public bool StartHidden;
        bool preview;   // the pictures: made-up data, no refresh in the background
        string navKey;
        Msg upd = new Msg(); DateTime updAt = DateTime.MinValue;
        // owner Q10: every size and font follows the display scale (100 / 125 / 150 %); the pictures can set it by hand
        readonly double sys = 1.0; double ui = 1.0;

        // owner Q6: white-label is the logo and the names only - the colours are the product's (contrast and the state colours)
        static readonly Color Brand = Color.FromArgb(31, 95, 191), Ink = Color.FromArgb(27, 36, 48), Muted = Color.FromArgb(91, 100, 114), Line = Color.FromArgb(226, 230, 236),
            Bg = Color.FromArgb(244, 246, 249), Ok = Color.FromArgb(17, 118, 74), Bad = Color.FromArgb(180, 35, 24), Warn = Color.FromArgb(138, 75, 0), Soft = Color.FromArgb(232, 240, 251);

        string T(string s, params object[] a) { return L.T(lang, s, a); }
        bool Rtl { get { return L.Rtl(lang); } }
        RightToLeft Dir { get { return Rtl ? RightToLeft.Yes : RightToLeft.No; } }
        int S(double px) { return (int)Math.Round(px * ui); }
        Font F(float pt, bool bold = false) { return new Font(bold ? "Segoe UI Semibold" : "Segoe UI", (float)(pt * ui / sys)); }
        static Color C(string hex) { return ColorTranslator.FromHtml(hex); }
        static Color ToneColor(Tone t) { return C(ClientView.Hex(t)); }
        static Color ToneFill(Tone t) { return C(ClientView.Fill(t)); }

        public ClientForm(IClientApi api)
        {
            this.api = api;
            AutoScaleMode = AutoScaleMode.None;   // the window scales itself (S / F): Windows' own scaling would come on top
            try { using (var g = CreateGraphics()) sys = g.DpiX / 96.0; } catch (Exception) { sys = 1.0; }
            if (sys < 1) sys = 1; ui = sys;
            Font = F(10f); BackColor = Bg; StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(S(1264), S(761));
            MinimumSize = new Size(S(ClientView.MinWidth), S(ClientView.MinHeight));   // owner Q10: the minimum stays 860x560
            try { Icon = Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetExecutingAssembly().Location); tray.Icon = Icon; } catch (Exception) { tray.Icon = SystemIcons.Shield; }
            nav.BackColor = Color.White; nav.Paint += (s, e) => { using (var p = new Pen(Line)) e.Graphics.DrawLine(p, Rtl ? 0 : nav.Width - 1, 0, Rtl ? 0 : nav.Width - 1, nav.Height); };
            body.Dock = DockStyle.Fill; body.BackColor = Bg;
            bar.Dock = DockStyle.Bottom; bar.BackColor = Color.White; bar.Visible = false;
            bar.Paint += (s, e) => { using (var p = new Pen(Line)) e.Graphics.DrawLine(p, 0, 0, bar.Width, 0); };
            scroll.Dock = DockStyle.Fill; scroll.AutoScroll = true; scroll.BackColor = Bg;
            scroll.Controls.Add(content); body.Controls.Add(scroll); body.Controls.Add(bar);
            Controls.Add(body); Controls.Add(nav);
            // a new size draws the page again for the new width - except pages whose typing is not kept elsewhere (a new backup, a service call)
            resized.Tick += (s, e) => { resized.Stop(); if (Visible && !cfBusy && (state["registered"] == "0" || (page != "new" && page != "help"))) Render(); };
            Resize += (s, e) => { if (WindowState == FormWindowState.Minimized) { Hide(); return; } if (preview) return; resized.Stop(); resized.Start(); };
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
            m.Items.Add(T("Back up now"), null, (s, e) => BackupAll());
            // owner Q3: no manual update in the pilot (the IT company updates)
            if (ClientView.ShowsManualUpdate(state))
                m.Items.Add(upd["available"] == "1" ? "⬆ " + T("Update to {0}", upd["latest"]) : T("Check for updates"), null, (s, e) => { if (upd["available"] == "1") { ShowFromTray(); UpdateNow(); } else { updAt = DateTime.MinValue; Refresh2(true); } });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(T("Close the window"), null, (s, e) => { closing = true; tray.Visible = false; Close(); });
            tray.ContextMenuStrip = m;
        }

        // ---------------------------------------------------------------- data

        List<Msg> Mine() { return state.List("sets").Where(s => s["mine"] != "0").ToList(); }

        void BackupAll()
        {
            var list = Mine().Where(s => string.IsNullOrEmpty(s["blocked"]) && !jobs.Any(j => j["set"] == s["id"] && j["state"] == "running")).ToList();
            if (list.Count == 0) return;
            foreach (var st in list) { var id = st["id"]; Do(() => api.Call("backup", new Msg().Set("set", id), null), list.Count == 1 || st == list[list.Count - 1] ? T("The backup has started") : null); }
        }

        void Refresh2(bool full)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Msg st = null, jb = null; string err = null;
                try { st = api.Call("state", null, null); jb = api.Call("jobs", null, null); } catch (Exception e) { err = e.Message; }
                Msg u = null;
                if (st != null && ClientView.ShowsManualUpdate(st) && (DateTime.UtcNow - updAt).TotalMinutes > 30) { updAt = DateTime.UtcNow; try { u = api.Call("update", null, null); } catch (Exception) { } }
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
                        // CLI-120: the poll draws again only pages without fields (home, backups, a backup, history, the restore's
                        // result); a page with fields, a tree or choices keeps what the person is doing
                        bool quiet = typing || !(page == "home" || page == "sets" || page == "set" || page == "history" || (page == "restore" && rStep == 3));
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
            Text = state["product"] ?? "Backup";
            tray.Text = (Text.Length > 60 ? Text.Substring(0, 60) : Text);
        }
        string saved { get { try { return (string)Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\OnlineBackup", "Language", null); } catch (Exception) { return null; } } set { try { Microsoft.Win32.Registry.SetValue(@"HKEY_CURRENT_USER\Software\OnlineBackup", "Language", value ?? ""); } catch (Exception) { } } }

        /// <summary>The language picked in the installation (written beside the program, for every Windows user).</summary>
        static string InstalledLanguage()
        {
            try { var f = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "language.txt"); if (File.Exists(f)) { var v = OnlineBackup.Core.Atomic.ReadAllText(f).Trim(); if (v.Length > 0) return L.Norm(v); } } catch (Exception) { }
            return null;
        }

        void UpdateTray()
        {
            BuildTrayMenu();
            var h = ClientView.Home(lang, state, jobs);
            var line = Text + " — " + h.Title;
            tray.Text = line.Substring(0, Math.Min(63, line.Length));
        }

        /// <summary>Runs a call of the service in the background; a message when it is done, a sign-in when it needs one.</summary>
        void Do(Func<Msg> work, string done, Action<Msg> then = null, bool refresh = true, Action<Exception> failed = null)
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
                        if (ae != null && ae.Status == 401) { if (SignIn()) Do(work, done, then, refresh, failed); else if (failed != null) failed(err); return; }
                        if (err != null)
                        {
                            if (failed != null) failed(err);
                            if (!(ae != null && ae.Code == "EXISTS" && failed != null)) Message(L.Tr(lang, err.Message), true);   // UI-Q2: EXISTS is answered in the window
                            return;
                        }
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
            Activate();   // UX-1: the message in front of the window
            MessageBox.Show(this, text, Text, MessageBoxButtons.OK, bad ? MessageBoxIcon.Warning : MessageBoxIcon.Information, MessageBoxDefaultButton.Button1, Rtl ? MessageBoxOptions.RightAlign | MessageBoxOptions.RtlReading : 0);
        }

        /// <summary>Restore, changes and new backups need the customer's password (and the code, when two-step is on).</summary>
        bool SignIn()
        {
            using (var d = new Form { Text = T("Sign in"), FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(S(440), S(300)), Font = Font, RightToLeft = Dir, BackColor = Color.White })
            {
                var stack = new Stacker(d, S(24), S(20), S(392), Rtl);
                stack.Text(T("Sign in"), 14f, true);
                stack.Text(T("Restoring and adding backups require your password."), 9.5f, false, Muted);
                stack.Text(T("Password"), 10f, true).Margin = new Padding(0, 10, 0, 2);
                var pw = stack.Box(true);
                stack.Text(T("Verification code (if enabled)"), 10f, true).Margin = new Padding(0, 10, 0, 2);
                var otp = stack.Box(false, true);
                var err = stack.Text("", 9.5f, true, Bad);
                stack.Done();
                var ok = Btn(T("Sign in"), true); var cancel = Btn(T("Cancel"), false);
                ok.Top = cancel.Top = d.ClientSize.Height - S(54); ok.Left = Rtl ? S(24) : d.ClientSize.Width - S(24) - ok.Width; cancel.Left = Rtl ? ok.Right + S(10) : ok.Left - S(10) - cancel.Width;
                d.Controls.Add(ok); d.Controls.Add(cancel); d.AcceptButton = ok; d.CancelButton = cancel;
                cancel.Click += (s, e) => d.DialogResult = DialogResult.Cancel;
                ok.Click += (s, e) =>
                {
                    try { keysReply = api.Call("login", new Msg().Set("password", pw.Text).Set("otp", otp.Text), null); d.DialogResult = DialogResult.OK; }
                    catch (Exception ex) { err.Text = T("Sign-in failed") + ": " + L.Tr(lang, ex.Message); }
                };
                var ok2 = d.ShowDialog(this) == DialogResult.OK;
                if (ok2) KeysMissing(keysReply);
                return ok2;
            }
        }

        /// <summary>Owner decision A7: a sign-in gives the copies of sets on this computer their key; one still without it is said.</summary>
        Msg keysReply;
        void KeysMissing(Msg r)
        {
            if (r != null && r.Int("keysMissing") > 0) Message(T("A backup on this computer still needs its encryption key — contact {0}.", Support()), true);
        }

        // ---------------------------------------------------------------- the frame: the side menu, the page, the fixed bar

        static readonly Dictionary<string, string> Glyph = new Dictionary<string, string> { { "home", "⌂" }, { "sets", "☰" }, { "restore", "↺" }, { "history", "◷" }, { "settings", "⚙" }, { "help", "?" } };

        FrameLayout frame;

        void BuildNav(bool collapsed, int width)
        {
            var h = ClientView.Home(lang, state, jobs);
            var nk = page + "|" + lang + "|" + collapsed + "|" + width + "|" + nav.Height + "|" + state["product"] + state["company"] + state["computer"] + state["offline"] + state["powered"] + "|" + upd["current"] + "|" + h.Problems.Count + "|" + ui;
            if (nk == navKey) return;   // the menu is built again only when it changes
            navKey = nk;
            var oldNav = nav.Controls.Cast<Control>().ToList(); nav.Controls.Clear(); foreach (var c in oldNav) c.Dispose();
            nav.RightToLeft = RightToLeft.No;
            int w = nav.Width, m = S(12), y = S(16);
            Func<int, int, int> mx = (x, cw) => Rtl ? w - x - cw : x;
            // the product's name (white-label: the logo and the names only)
            if (!collapsed)
            {
                var name = new Label { Text = state["product"] ?? "Backup", Font = F(11.5f, true), ForeColor = Ink, AutoSize = false, Width = w - 2 * m - S(8), Height = S(24), RightToLeft = Dir, TextAlign = ContentAlignment.TopLeft };
                name.Location = new Point(mx(m + S(4), name.Width), y); nav.Controls.Add(name);
                var sub = !string.IsNullOrEmpty(state["slogan"]) ? state["slogan"] : (state["company"] ?? "");
                var sl = new Label { Text = sub, Font = F(8.5f), ForeColor = Muted, AutoSize = false, Width = name.Width, Height = S(18), RightToLeft = Dir, TextAlign = ContentAlignment.TopLeft };
                sl.Location = new Point(mx(m + S(4), sl.Width), y + S(24)); nav.Controls.Add(sl);
            }
            y += S(64);
            var current = page == "set" || page == "new" ? "sets" : page;
            foreach (var item in ClientView.Nav)
            {
                var key = item[0]; bool on = current == key;
                var text = (collapsed ? Glyph[key] : Glyph[key] + "   " + T(item[1])) + (key == "sets" && h.Problems.Count > 0 ? (collapsed ? "!" : "   ⚠") : "");
                var b = new Button { Name = "nav_" + key, Text = text, Width = w - 2 * m, Height = S(42), FlatStyle = FlatStyle.Flat, TextAlign = collapsed ? ContentAlignment.MiddleCenter : ContentAlignment.MiddleLeft, RightToLeft = Dir,
                    Font = F(10.5f, on), BackColor = on ? Soft : Color.White, ForeColor = on ? Brand : Ink, Cursor = Cursors.Hand, AccessibleName = T(item[1]) };
                b.FlatAppearance.BorderSize = 0; b.FlatAppearance.MouseOverBackColor = on ? Soft : Bg;
                b.Location = new Point(m, y);
                if (collapsed) tips.SetToolTip(b, T(item[1]));
                b.Click += (s, e) => Go(key);
                nav.Controls.Add(b); y += S(48);
            }
            // the bottom: this computer, help from the IT company, "Powered by"
            int bottom = nav.Height - S(12);
            if (!string.IsNullOrEmpty(state["powered"]) && !collapsed)
            {
                var pb = new Label { Text = T("Powered by {0}", state["powered"]), AutoSize = false, Width = w - 2 * m, Height = S(18), ForeColor = Muted, Font = F(8f), RightToLeft = Dir };
                bottom -= pb.Height; pb.Location = new Point(m, bottom); nav.Controls.Add(pb);
            }
            var help = new Button { Name = "nav_help", Text = collapsed ? Glyph["help"] : Glyph["help"] + "   " + T("Help from {0}", ClientView.Company(lang, state)), Width = w - 2 * m, Height = S(40), FlatStyle = FlatStyle.Flat, TextAlign = collapsed ? ContentAlignment.MiddleCenter : ContentAlignment.MiddleLeft, RightToLeft = Dir,
                Font = F(9.5f, page == "help"), BackColor = page == "help" ? Soft : Color.White, ForeColor = page == "help" ? Brand : Ink, Cursor = Cursors.Hand, AccessibleName = T("Help") };
            help.FlatAppearance.BorderSize = 0; help.FlatAppearance.MouseOverBackColor = Bg;
            bottom -= help.Height + S(4); help.Location = new Point(m, bottom); help.Click += (s, e) => Go("help"); nav.Controls.Add(help);
            if (collapsed) tips.SetToolTip(help, T("Help from {0}", ClientView.Company(lang, state)));
            if (!collapsed && bottom - S(84) > y)
            {
                var card = new Panel { Width = w - 2 * m, Height = S(74), BackColor = Bg }; bottom -= card.Height + S(8); card.Location = new Point(m, bottom);
                var cn = new Label { Text = state["computer"] ?? "", Font = F(10f, true), ForeColor = Ink, AutoSize = false, Width = card.Width - S(20), Height = S(22), RightToLeft = RightToLeft.No, TextAlign = Rtl ? ContentAlignment.TopRight : ContentAlignment.TopLeft, Location = new Point(S(10), S(8)) };
                var off = !string.IsNullOrEmpty(state["offline"]);
                var cs = new Label { Text = "● " + (off ? T("No connection") : T("Connected")) + (string.IsNullOrEmpty(upd["current"]) ? "" : " · " + T("Version {0}", upd["current"])), Font = F(8.5f), ForeColor = off ? Bad : Ok, AutoSize = false, Width = card.Width - S(20), Height = S(36), RightToLeft = Dir, Location = new Point(S(10), S(32)) };
                card.Controls.Add(cn); card.Controls.Add(cs); nav.Controls.Add(card);
            }
        }

        sealed class LangItem
        {
            public readonly string Code;
            public LangItem(string c) { Code = c; }
            static readonly Dictionary<string, string> Names = new Dictionary<string, string> { { "en", "English" }, { "he", "עברית" } };
            public override string ToString() { string n; return Names.TryGetValue(Code, out n) ? n : Code; }
        }

        void Go(string p) { if (p == "restore" && page != "restore") { rStep = 1; } page = p; scroll.AutoScrollPosition = new Point(0, 0); Render(); }

        Button Btn(string text, bool primary)
        {
            var font = F(10f, true);
            var b = new Button { Text = text, Height = S(36), FlatStyle = FlatStyle.Flat, Font = font, Cursor = Cursors.Hand, AutoSize = false, Width = Math.Max(S(110), TextRenderer.MeasureText(text, font).Width + S(30)), MinimumSize = new Size(S(44), S(30)), UseCompatibleTextRendering = false };
            if (primary) { b.BackColor = Brand; b.ForeColor = Color.White; b.FlatAppearance.BorderSize = 0; } else { b.BackColor = Color.White; b.ForeColor = Ink; b.FlatAppearance.BorderColor = Color.FromArgb(203, 210, 220); }
            b.EnabledChanged += (s, e) => { if (primary) b.BackColor = b.Enabled ? Brand : Color.FromArgb(160, 176, 200); };
            return b;
        }

        void UpdateNow()
        {
            if (!ClientView.ShowsManualUpdate(state)) return;   // owner Q3
            if (MessageBox.Show(this, T("Update to version {0}", upd["latest"]) + "\n\n" + T("The backup stops for about a minute and starts again with the new version. Your backups and settings are kept."), Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1, Rtl ? MessageBoxOptions.RightAlign | MessageBoxOptions.RtlReading : 0) != DialogResult.OK) return;
            Do(() => api.Call("update", new Msg().Set("apply", "1"), null), T("Updating… the program opens again when the update is done."), r => { upd = new Msg(); });
        }

        // ---------------------------------------------------------------- small drawing helpers (logical x from the reading side)

        int pageW;
        int MX(Control p, int x, int w) { return Rtl ? p.Width - x - w : x; }

        Label Text2(Control p, string text, float pt, bool bold, Color color, int x, int y, int w, bool ltr = false, string name = null)
        {
            var font = F(pt, bold);
            var h = TextRenderer.MeasureText(string.IsNullOrEmpty(text) ? " " : text, font, new Size(Math.Max(10, w), 0), TextFormatFlags.WordBreak).Height + S(2);
            var l = new Label { Text = text, Font = font, ForeColor = color, AutoSize = false, Width = w, Height = h, BackColor = Color.Transparent, UseMnemonic = false };
            if (ltr) { l.RightToLeft = RightToLeft.No; l.TextAlign = Rtl ? ContentAlignment.TopRight : ContentAlignment.TopLeft; }
            else { l.RightToLeft = Dir; l.TextAlign = ContentAlignment.TopLeft; }
            if (name != null) l.Name = name;
            l.Location = new Point(MX(p, x, w), y); p.Controls.Add(l); return l;
        }

        Button Place(Control p, Button b, int x, int y, string name = null) { b.Location = new Point(MX(p, x, b.Width), y); b.RightToLeft = Dir; if (name != null) b.Name = name; p.Controls.Add(b); return b; }

        LinkLabel Link(Control p, string text, int x, int y, Action click, float pt = 9.5f)
        {
            var l = new LinkLabel { Text = text, AutoSize = true, Font = F(pt, true), LinkColor = Brand, ActiveLinkColor = Brand, LinkBehavior = LinkBehavior.HoverUnderline, RightToLeft = Dir, BackColor = Color.Transparent };
            l.LinkClicked += (s, e) => click();
            p.Controls.Add(l); var w = l.PreferredWidth; l.Location = new Point(MX(p, x, w), y); return l;
        }

        Panel Card(Control p, int x, int y, int w, int h, Color? fill = null, Color? border = null)
        {
            var c = new Panel { Width = w, Height = h, BackColor = fill ?? Color.White };
            var edge = border ?? Line;
            c.Paint += (s, e) => { using (var pen = new Pen(edge)) e.Graphics.DrawRectangle(pen, 0, 0, c.Width - 1, c.Height - 1); };
            c.Location = new Point(MX(p, x, w), y); p.Controls.Add(c); return c;
        }

        /// <summary>A state chip: icon + word, the colour never alone.</summary>
        Label Chip(Control p, string text, Tone t, RunKind k, int x, int y, bool fromEnd = false, string name = null)
        {
            var font = F(8.5f, true);
            var s = ClientView.Icon(t, k) + "  " + text;
            var w = TextRenderer.MeasureText(s, font).Width + S(18);
            var l = new Label { Text = s, Font = font, ForeColor = ToneColor(t), BackColor = ToneFill(t), AutoSize = false, Width = w, Height = S(24), TextAlign = ContentAlignment.MiddleCenter, RightToLeft = Dir, UseMnemonic = false };
            if (name != null) l.Name = name;
            int lx = fromEnd ? p.Width - x - w : x;
            l.Location = new Point(MX(p, lx, w), y); p.Controls.Add(l); return l;
        }

        /// <summary>A round icon with a glyph, in the tone's colours.</summary>
        Panel Badge(Control p, Tone t, string glyph, int x, int y, int size)
        {
            var b = new Panel { Width = size, Height = size, BackColor = Color.Transparent };
            b.Paint += (s, e) =>
            {
                var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var br = new SolidBrush(ToneFill(t))) g.FillEllipse(br, 0, 0, size - 1, size - 1);
                using (var f = F(size / 4.2f, true)) using (var br = new SolidBrush(ToneColor(t)))
                    g.DrawString(glyph, f, br, new RectangleF(0, 0, size, size), new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
            };
            b.Location = new Point(MX(p, x, size), y); p.Controls.Add(b); return b;
        }

        Panel Divider(Control p, int x, int y, int w) { var d = new Panel { Width = w, Height = 1, BackColor = Line }; d.Location = new Point(MX(p, x, w), y); p.Controls.Add(d); return d; }

        // ---------------------------------------------------------------- pages

        void Render()
        {
            SuspendLayout();
            RightToLeft = Dir; RightToLeftLayout = false;
            var old = content.Controls.Cast<Control>().ToList(); content.Controls.Clear(); foreach (var c in old) c.Dispose();
            var oldBar = bar.Controls.Cast<Control>().ToList(); bar.Controls.Clear(); foreach (var c in oldBar) c.Dispose();
            bool registered = state["registered"] != "0";
            bool withBar = registered && page == "restore";
            frame = ClientView.Frame(ClientSize.Width, ClientSize.Height, ui, Rtl, withBar);
            nav.Visible = registered;
            nav.Width = frame.Nav.W; nav.Height = ClientSize.Height; nav.Location = new Point(frame.Nav.X, 0); nav.Dock = Rtl ? DockStyle.Right : DockStyle.Left;
            bar.Visible = withBar; bar.Height = withBar ? frame.Bar.H : 0;
            if (registered) BuildNav(frame.NavCollapsed, frame.Nav.W); else navKey = null;
            body.RightToLeft = RightToLeft.No; scroll.RightToLeft = RightToLeft.No; content.RightToLeft = RightToLeft.No;
            int pad = S(32);
            var avail = (registered ? frame.Page.W : ClientSize.Width) - 2 * pad - SystemInformation.VerticalScrollBarWidth;
            pageW = Math.Max(S(360), avail);
            content.Width = pageW; content.Location = new Point(pad + scroll.AutoScrollPosition.X, S(28) + scroll.AutoScrollPosition.Y);
            content.Height = S(400);
            if (!registered)
            {
                var st = new Stacker(content, 0, 0, pageW, Rtl);
                ConnectPage(st); st.Done(); content.Height = st.Height + S(20); ResumeLayout(true); return;
            }
            int y = 0;
            if (!string.IsNullOrEmpty(state["offline"]) && page != "home")
                y = Text2(content, "☁  " + T("No connection to the backup server right now: {0}", L.Tr(lang, state["offline"])), 10f, true, Bad, 0, 0, pageW, false, "offline").Bottom + S(12);
            switch (page)
            {
                case "home": y = HomePage(y); break;
                case "sets": y = SetsPage(y); break;
                case "set": y = SetPage(y); break;
                case "restore": y = RestorePage(y); break;
                case "history": y = HistoryPage(y); break;
                case "settings": y = SettingsPage(y); break;
                case "new": { y = BackLink(y); var st = new Stacker(content, 0, y, pageW, Rtl); NewPage(st); st.Done(); y += st.Height; break; }
                case "help": { var st = new Stacker(content, 0, y, Math.Min(pageW, S(720)), Rtl); if (Rtl) st = new Stacker(content, pageW - Math.Min(pageW, S(720)), y, Math.Min(pageW, S(720)), Rtl); HelpPage(st); st.Done(); y += st.Height; break; }
                default: page = "home"; y = HomePage(y); break;
            }
            content.Height = y + S(24);
            ResumeLayout(true);
        }

        int BackLink(int y) { Link(content, "‹ " + T("Backups"), 0, y, () => Go("sets")); return y + S(32); }

        int PageTitle(int y, string title, string sub)
        {
            var t = Text2(content, title, 18f, true, Ink, 0, y, pageW, false, "title");
            y = t.Bottom;
            if (!string.IsNullOrEmpty(sub)) y = Text2(content, sub, 9.5f, false, Muted, 0, y + S(2), pageW).Bottom;
            return y + S(16);
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
                Msg conn = null;
                try { conn = api.Call("connect", body, null); } catch (Exception e) { e2 = e; }
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        cfBusy = false;
                        if (e2 != null) { cfError = e2.Message; Render(); return; }
                        foreach (var k in cf.Keys.ToList()) if (k != "server") cf[k] = "";
                        AcceptButton = null; page = "new";
                        Message(T("This computer is connected. Now choose what to back up."), false);
                        KeysMissing(conn);
                        Refresh2(true);
                    }));
                }
                catch (Exception) { }
            });
        }

        /// <summary>The IT company's agreement: read, tick, continue.</summary>
        bool Agreement(string text)
        {
            using (var d = new Form { Text = T("Agreement"), FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(S(600), S(470)), Font = Font, RightToLeft = Dir, BackColor = Color.White })
            {
                var stack = new Stacker(d, S(24), S(16), S(552), Rtl);
                stack.Text(T("The terms of your provider. Please read them before you go on."), 10f, false, Muted);
                var box = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Text = text.Replace("\r\n", "\n").Replace("\n", "\r\n"), Width = S(552), Height = S(300), BackColor = Color.White, Font = new Font("Segoe UI", 9.5f) };
                stack.Add(box);
                var ck = stack.Check(T("I have read and accept the agreement"), false);
                stack.Done();
                var ok = Btn(T("Continue"), true); var cancel = Btn(T("Cancel"), false); ok.Enabled = false;
                ck.CheckedChanged += (s, e) => ok.Enabled = ck.Checked;
                ok.Top = cancel.Top = d.ClientSize.Height - S(54); ok.Left = Rtl ? S(24) : d.ClientSize.Width - S(24) - ok.Width; cancel.Left = Rtl ? ok.Right + S(10) : ok.Left - S(10) - cancel.Width;
                d.Controls.Add(ok); d.Controls.Add(cancel); d.CancelButton = cancel;
                ok.Click += (s, e) => d.DialogResult = DialogResult.OK; cancel.Click += (s, e) => d.DialogResult = DialogResult.Cancel;
                return d.ShowDialog(this) == DialogResult.OK;
            }
        }

        string Support() { var s = state["company"]; if (string.IsNullOrEmpty(s)) s = T("support"); if (!string.IsNullOrEmpty(state["phone"])) s += " " + state["phone"]; return s; }

        // ---------------------------------------------------------------- Home: the protection bar, the problem, the backups and the activity

        void DoAction(string action)
        {
            switch (action)
            {
                case "backup-all": BackupAll(); break;
                case "add": Go("new"); break;
                case "retry": Refresh2(true); break;
                case "contact": Go("help"); break;
                case "restore": Go("restore"); break;
            }
        }

        int HomePage(int y)
        {
            var h = ClientView.Home(lang, state, jobs);
            int m = S(24);
            var hero = Card(content, 0, y, pageW, S(200), Color.White, h.Tone == Tone.Ok ? Line : ToneColor(h.Tone));
            hero.Name = "hero";
            Badge(hero, h.Tone, ClientView.Icon(h.Tone, h.State == HomeState.Stopped ? RunKind.Stopped : RunKind.Never), m, m, S(64));
            var p = Btn(h.Primary, true); var s = h.Secondary.Length > 0 ? Btn(h.Secondary, false) : null;
            bool below;
            var boxes = ClientView.HeroButtons(pageW, ui, Rtl, p.Width + S(16), s == null ? 0 : s.Width + S(16), out below);
            int textX = m + S(80), textW = below ? pageW - textX - m : pageW - textX - m - (boxes[0].W + (s == null ? 0 : boxes[1].W + S(12))) - S(16);
            var cap = Text2(hero, T("Protection status"), 8.5f, true, ToneColor(h.Tone), textX, m - S(2), textW);
            var title = Text2(hero, h.Title, 18f, true, Ink, textX, cap.Bottom, textW, false, "state");
            var sub = Text2(hero, h.Sub, 10f, false, Muted, textX, title.Bottom + S(2), textW, false, "stateSub");
            int ty = sub.Bottom;
            if (h.State == HomeState.Running)
            {
                var pb = new ProgressBar { Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30, Width = Math.Min(textW, S(420)), Height = S(6) };
                pb.Location = new Point(MX(hero, textX, pb.Width), ty + S(8)); hero.Controls.Add(pb); ty = pb.Bottom;
            }
            if (below) { int by = Math.Max(ty, S(96)) + S(16); boxes = new[] { new Box(boxes[0].X, by, boxes[0].W, boxes[0].H), new Box(boxes[1].X, by, boxes[1].W, boxes[1].H) }; }
            p.Bounds = new Rectangle(boxes[0].X, boxes[0].Y, boxes[0].W, boxes[0].H); p.Name = "heroPrimary"; p.RightToLeft = Dir; hero.Controls.Add(p);
            var act = h.PrimaryAction; p.Click += (o, e) => DoAction(act);
            if (h.PrimaryAction == "backup-all" && !Mine().Any(x => string.IsNullOrEmpty(x["blocked"]) && !jobs.Any(j => j["set"] == x["id"] && j["state"] == "running"))) p.Enabled = false;
            if (s != null) { s.Bounds = new Rectangle(boxes[1].X, boxes[1].Y, boxes[1].W, boxes[1].H); s.Name = "heroSecondary"; s.RightToLeft = Dir; hero.Controls.Add(s); var a2 = h.SecondaryAction; s.Click += (o, e) => DoAction(a2); }
            int fy = Math.Max(ty, Math.Max(boxes[0].Bottom, S(88))) + S(20);
            Divider(hero, m, fy, pageW - 2 * m); fy += S(16);
            // the three facts, always in this order (IA §2.1)
            int cols = pageW < S(560) ? 1 : 3, cw = (pageW - 2 * m) / cols, rowH = 0, fx = 0, fyy = fy;
            for (int i = 0; i < h.Facts.Length; i++)
            {
                var f = h.Facts[i];
                int x = m + (i % cols) * cw; if (i % cols == 0 && i > 0) { fyy += rowH + S(12); rowH = 0; }
                var a = Text2(hero, f.Label, 8.5f, true, Muted, x, fyy, cw - S(16));
                var b = Text2(hero, f.Value, 14f, true, f.Tone == Tone.Neutral ? Ink : ToneColor(f.Tone), x, a.Bottom, cw - S(16), false, "fact" + i);
                var c = Text2(hero, f.Sub, 8.5f, false, Muted, x, b.Bottom, cw - S(16));
                rowH = Math.Max(rowH, c.Bottom - fyy); fx++;
            }
            hero.Height = fyy + rowH + m;
            y = hero.Bottom + S(16);
            // the card "What was not backed up and why" (A + B2), with "What to do"
            foreach (var pr in h.Problems.Take(2)) y = ProblemCard(pr, y, true) + S(16);
            // the backups on this computer and the recent activity, side by side when there is room
            bool two = pageW >= S(760);
            int lw = two ? (pageW - S(16)) * 3 / 5 : pageW, rw = two ? pageW - lw - S(16) : pageW;
            var setsCard = Card(content, 0, y, lw, S(100));
            Text2(setsCard, T("Backups on this computer"), 11f, true, Ink, S(20), S(16), lw - S(160));
            Link(setsCard, T("Manage backups"), lw - S(20) - S(130), S(18), () => Go("sets")).Location = new Point(Rtl ? S(20) : lw - S(20) - LinkWidth(setsCard), S(18));
            int ry = S(52);
            if (h.Sets.Count == 0) ry = Text2(setsCard, T("No backups are set up yet."), 9.5f, false, Muted, S(20), ry, lw - S(40)).Bottom + S(12);
            foreach (var sv in h.Sets)
            {
                Divider(setsCard, 0, ry, lw);
                var row = new Panel { Width = lw - 2, Height = S(64), BackColor = sv.Kind == RunKind.Partial ? ToneFill(Tone.Warn) : Color.White, Cursor = Cursors.Hand };
                row.Location = new Point(1, ry + 1); setsCard.Controls.Add(row);
                var id = sv.Id; EventHandler open = (o, e) => { selSet = id; Go("set"); };
                row.Click += open;
                var chip = Chip(row, sv.Chip, sv.Tone, sv.Kind, S(16), S(10), true);
                var chipX = Rtl ? chip.Left : row.Width - chip.Right;   // its logical start from the far end
                int nameW = row.Width - S(36) - chip.Width - S(16);
                var n = Text2(row, sv.Name, 10.5f, true, Ink, S(16), S(10), nameW); n.Click += open;
                var sub2 = Text2(row, sv.Sources + "  ·  " + T("every day at {0}", sv.Hour), 8.5f, false, Muted, S(16), n.Bottom, nameW); sub2.Click += open;
                var lc = Text2(row, sv.Kind == RunKind.Blocked ? T("The set and its backups are kept as they are; it does not run.") : T("Last complete: {0}", ClientView.When(lang, sv.LastComplete)), 8.5f, false, Muted, S(16), chip.Bottom + S(2), row.Width - S(32));
                lc.TextAlign = ContentAlignment.TopRight; if (Rtl) lc.TextAlign = ContentAlignment.TopRight; lc.RightToLeft = Dir; lc.Click += open;
                row.Height = Math.Max(sub2.Bottom, lc.Bottom) + S(10);
                if (lc.Bottom > sub2.Bottom) { lc.Top = Math.Max(lc.Top, sub2.Bottom); row.Height = lc.Bottom + S(10); }
                ry += row.Height + 1;
            }
            setsCard.Height = ry + S(4);
            int ax = two ? lw + S(16) : 0, ay = two ? y : setsCard.Bottom + S(16);
            var actCard = Card(content, ax, ay, rw, S(100));
            Text2(actCard, T("Recent activity"), 11f, true, Ink, S(20), S(16), rw - S(140));
            Link(actCard, T("History"), 0, S(18), () => Go("history")).Location = new Point(Rtl ? S(20) : rw - S(20) - LinkWidth(actCard), S(18));
            int ay2 = S(52);
            var hist = ClientView.History(lang, state, jobs, "all", null).Take(5).ToList();
            if (hist.Count == 0) ay2 = Text2(actCard, T("Nothing yet."), 9.5f, false, Muted, S(20), ay2, rw - S(40)).Bottom + S(12);
            foreach (var r in hist)
            {
                Divider(actCard, 0, ay2, rw);
                var tone = ClientView.ToneOf(r.Run);
                Badge(actCard, tone, ClientView.Icon(tone, r.Run), S(16), ay2 + S(12), S(22));
                var a = Text2(actCard, (r.Kind == "restore" ? T("Restore") : T("Backup")) + " · " + r.SetName, 9.5f, true, Ink, S(48), ay2 + S(8), rw - S(64));
                var b = Text2(actCard, ClientView.When(lang, r.Utc) + " · " + ClientView.Word(lang, r.Run, r.Result) + (r.Errors > 0 && r.Run == RunKind.Partial ? " · " + T("{0} files not backed up", r.Errors) : ""), 8.5f, false, Muted, S(48), a.Bottom, rw - S(64));
                ay2 = b.Bottom + S(8);
            }
            actCard.Height = ay2 + S(4);
            return Math.Max(setsCard.Bottom, actCard.Bottom);
        }

        string When(string v) { var t = ClientView.ParseTime(v); return t == null ? v ?? "" : ClientView.When(lang, t); }

        int LinkWidth(Control p) { var l = p.Controls.OfType<LinkLabel>().LastOrDefault(); return l == null ? 0 : l.PreferredWidth; }

        string selSet;

        /// <summary>What was not backed up and why, and what to do (a partial run) - or why the backup failed.</summary>
        int ProblemCard(SetView sv, int y, bool withLink)
        {
            int m = S(20);
            var tone = sv.Kind == RunKind.Partial ? Tone.Warn : Tone.Bad;
            var card = Card(content, 0, y, pageW, S(100), ToneFill(tone), ToneColor(tone));
            card.Name = "problem";
            var head = sv.Kind == RunKind.Partial
                ? (sv.MissedCount > 0 ? T("{0} — {1} files not backed up ({2})", sv.Name, sv.MissedCount, ClientView.When(lang, sv.LastAttempt)) : T("{0} — some files were not backed up ({1})", sv.Name, ClientView.When(lang, sv.LastAttempt)))
                : T("{0} — the backup failed ({1})", sv.Name, ClientView.When(lang, sv.LastAttempt));
            int linkW = 0;
            if (withLink) { var id = sv.Id; var lk = Link(card, "▤ " + T("Details and log"), 0, m, () => { selSet = id; Go("set"); }); linkW = lk.PreferredWidth; lk.Location = new Point(Rtl ? m : pageW - m - linkW, m); }
            var t = Text2(card, (tone == Tone.Warn ? "⚠  " : "✕  ") + head, 10.5f, true, Ink, m, m, pageW - 2 * m - linkW - S(12), false, "problemTitle");
            int cy = t.Bottom + S(6);
            bool two = pageW >= S(760);
            int lw = two ? (pageW - 2 * m) * 3 / 5 : pageW - 2 * m;
            var withPath = sv.Missed.Where(x => x.Path.Length > 0).ToList();
            var reasons = sv.Missed.Select(x => L.Tr(lang, x.Why)).Where(x => x.Length > 0).Distinct().ToList();
            int ly = cy;
            if (withPath.Count > 0)
            {
                ly = Text2(card, T("These files were not backed up:"), 9f, false, Muted, m, ly, lw).Bottom + S(4);
                foreach (var x in withPath.Take(5))
                {
                    var pl = Text2(card, "▫ " + x.Path, 9f, false, Ink, m + S(8), ly, lw - S(8), true); pl.Font = new Font("Consolas", (float)(9 * ui / sys));
                    ly = pl.Bottom;
                    if (x.Why.Length > 0) ly = Text2(card, L.Tr(lang, x.Why), 8.5f, false, Muted, m + S(24), ly, lw - S(24)).Bottom + S(2);
                }
                if (withPath.Count > 5 || sv.MissedCount > 5) ly = Text2(card, T("and {0} more — see the log", Math.Max(withPath.Count, sv.MissedCount) - 5), 8.5f, false, Muted, m + S(8), ly, lw).Bottom;
            }
            else if (reasons.Count > 0) { foreach (var r in reasons.Take(3)) ly = Text2(card, T("Why: {0}", r), 9.5f, false, Ink, m, ly, lw).Bottom + S(2); }
            int wx = two ? m + lw + S(16) : m, wy = two ? cy : ly + S(10), ww = two ? pageW - wx - m : pageW - 2 * m;
            var what = Card(card, wx, wy, ww, S(60), C("#E8F0FB"), C("#BFD3F2"));
            var wt = Text2(what, T("What to do:") + " " + (sv.Kind == RunKind.Partial ? ClientView.WhatToDo(lang, state) : T("Click \"Back up again now\". If it fails again, contact {0}.", ClientView.Company(lang, state))), 9.5f, false, Ink, S(12), S(10), ww - S(24));
            what.Height = wt.Bottom + S(10);
            card.Height = Math.Max(ly, what.Bottom) + m;
            return card.Bottom;
        }

        /// <summary>The last runs as small coloured boxes (newest at the reading end), with the words in a tooltip.</summary>
        Panel Strip(Control p, SetView sv, int x, int y, int n)
        {
            var runs = sv.Runs.Skip(Math.Max(0, sv.Runs.Count - n)).ToList();
            int bw = S(9), gap = S(3);
            var strip = new Panel { Width = n * (bw + gap), Height = S(18), BackColor = Color.Transparent };
            strip.Paint += (s, e) =>
            {
                for (int i = 0; i < runs.Count; i++)
                {
                    int slot = n - runs.Count + i;                       // the newest at the end of the strip
                    int px = Rtl ? strip.Width - (slot + 1) * (bw + gap) : slot * (bw + gap);
                    using (var b = new SolidBrush(ToneColor(ClientView.ToneOf(runs[i].Run)))) e.Graphics.FillRectangle(b, px, 0, bw, strip.Height);
                }
            };
            tips.SetToolTip(strip, string.Join("\n", runs.Select(r => ClientView.When(lang, r.Utc) + "  " + ClientView.Word(lang, r.Run, r.Result)).Reverse().ToArray()));
            strip.Location = new Point(MX(p, x, strip.Width), y); p.Controls.Add(strip); return strip;
        }

        // ---------------------------------------------------------------- Backups: one card per backup, and "New backup"

        int SetsPage(int y)
        {
            var sets = ClientView.Sets(lang, state, jobs);
            y = PageTitle(y, T("Backups"), T("{0} backups · each runs automatically at its time", sets.Count));
            bool two = pageW >= S(760);
            int cw = two ? (pageW - S(16)) / 2 : pageW, col = 0, rowTop = y, rowH = 0;
            var cards = new List<Panel>();
            Action<Panel> placed = c => { rowH = Math.Max(rowH, c.Height); col++; if (!two || col == 2) { rowTop += rowH + S(16); rowH = 0; col = 0; } };
            foreach (var sv in sets)
            {
                var c = Card(content, two && col == 1 ? cw + S(16) : 0, rowTop, cw, S(100), Color.White, sv.Kind == RunKind.Partial ? ToneColor(Tone.Warn) : sv.Kind == RunKind.Failed ? ToneColor(Tone.Bad) : Line);
                int m = S(20);
                var chip = Chip(c, sv.Chip, sv.Tone, sv.Kind, m, m, true);
                var n = Text2(c, sv.Name, 12f, true, Ink, m, m - S(2), cw - 2 * m - chip.Width - S(12));
                var src = Text2(c, sv.Sources, 8.5f, false, Muted, m, n.Bottom, cw - 2 * m - chip.Width - S(12), true);
                int fy = Math.Max(src.Bottom, chip.Bottom) + S(12), half = (cw - 2 * m) / 2;
                var a1 = Text2(c, T("Last complete backup"), 8.5f, true, Muted, m, fy, half - S(8));
                var a2 = Text2(c, ClientView.When(lang, sv.LastComplete), 12f, true, Ink, m, a1.Bottom, half - S(8));
                var a3 = Text2(c, T("Last attempt: {0}", sv.LastAttempt == null ? T("Not yet") : ClientView.When(lang, sv.LastAttempt) + " · " + ClientView.Word(lang, ClientView.Classify(sv.LastResult), sv.LastResult)), 8.5f, false, Muted, m, a2.Bottom, half - S(8));
                var b1 = Text2(c, T("Schedule"), 8.5f, true, Muted, m + half, fy, half);
                var b2 = Text2(c, T("Every day at {0}", sv.Hour), 12f, true, Ink, m + half, b1.Bottom, half);
                int sy = Math.Max(a3.Bottom, b2.Bottom) + S(12);
                if (sv.Runs.Count > 0) { var stl = Text2(c, T("Last {0} runs", Math.Min(14, sv.Runs.Count)), 8.5f, false, Muted, m, sy, S(160)); Strip(c, sv, m + S(170), sy, 14); sy = stl.Bottom + S(8); }
                var keep = ClientView.Keep(lang, state.List("sets").FirstOrDefault(x => x["id"] == sv.Id) ?? new Msg());
                if (keep.Length > 0) sy = Text2(c, keep, 8.5f, false, Muted, m, sy, cw - 2 * m).Bottom + S(6);
                if (sv.Kind == RunKind.Blocked) sy = Text2(c, L.Tr(lang, sv.Blocked) + " " + T("The set and its backups are kept as they are; it does not run."), 9f, false, Warn, m, sy, cw - 2 * m).Bottom + S(8);   // PILOT-010
                if (sv.Kind == RunKind.Partial)
                {
                    var warn = Card(c, m, sy, cw - 2 * m, S(30), ToneFill(Tone.Warn), C("#F1C27D"));
                    var wl = Text2(warn, "⚠  " + (sv.MissedCount > 0 ? T("{0} files not backed up ({1})", sv.MissedCount, ClientView.When(lang, sv.LastAttempt)) : T("Some files were not backed up")), 9f, false, Warn, S(10), S(6), warn.Width - S(20));
                    warn.Height = wl.Bottom + S(6); sy = warn.Bottom + S(10);
                }
                var id = sv.Id;
                var det = Place(c, Btn(T("Details"), false), m, sy);
                det.Click += (o, e) => { selSet = id; Go("set"); };
                var now = Place(c, Btn(T("Back up now"), true), m + det.Width + S(8), sy, "setBackup_" + id);
                now.Enabled = sv.Kind != RunKind.Running && sv.Kind != RunKind.Blocked;
                now.Click += (o, e) => Do(() => api.Call("backup", new Msg().Set("set", id), null), T("The backup has started"));
                var rl = Link(c, T("Restore"), 0, sy + S(8), () => { rSetId = id; rStep = 1; Go("restore"); });
                rl.Location = new Point(Rtl ? m : cw - m - rl.PreferredWidth, sy + S(8));
                c.Height = now.Bottom + m;
                placed(c);
            }
            // New backup (IA: inside Backups). Owner Q5: when the IT company adds the backups, the button is shown disabled with the reason
            {
                var c = Card(content, two && col == 1 ? cw + S(16) : 0, rowTop, cw, S(100), Bg, C("#C9D0DA"));
                var plus = Badge(c, Tone.Info, "+", (cw - S(48)) / 2, S(24), S(48));
                var t1 = Text2(c, T("New backup"), 11f, true, Ink, S(20), plus.Bottom + S(8), cw - S(40)); t1.TextAlign = ContentAlignment.TopCenter;
                var t2 = Text2(c, T("Files and folders on this computer"), 9f, false, Muted, S(20), t1.Bottom, cw - S(40)); t2.TextAlign = ContentAlignment.TopCenter;
                var add = Btn(T("New backup"), false); add.Name = "newBackup";
                Place(c, add, (cw - add.Width) / 2, t2.Bottom + S(10));
                add.Enabled = state["canAdd"] != "0";
                add.Click += (o, e) => Go("new");
                int bottom = add.Bottom;
                if (state["canAdd"] == "0") { var why = Text2(c, T(ClientView.Locked), 8.5f, false, Muted, S(20), add.Bottom + S(6), cw - S(40), false, "lockedNew"); why.TextAlign = ContentAlignment.TopCenter; bottom = why.Bottom; }
                c.Height = Math.Max(S(180), bottom + S(20));
                placed(c);
            }
            return rowTop + rowH;
        }

        // ---------------------------------------------------------------- one backup's details

        Msg setLog; string setLogFor;

        int SetPage(int y)
        {
            var raw = state.List("sets").FirstOrDefault(x => x["id"] == selSet);
            if (raw == null) { page = "sets"; return SetsPage(y); }
            var sv = ClientView.Set(lang, raw, jobs);
            y = BackLink(y);
            int m = S(20);
            var now = Btn(T("Back up now"), true); var rest = Btn(T("Restore from this backup"), false);
            int bw = now.Width + rest.Width + S(12);
            var chip = Chip(content, sv.Chip, sv.Tone, sv.Kind, 0, y + S(4));
            var title = Text2(content, sv.Name, 18f, true, Ink, chip.Width + S(12), y - S(2), Math.Max(S(120), pageW - chip.Width - S(12) - bw - S(16)), false, "title");
            Place(content, now, pageW - now.Width, y, "setBackup_" + sv.Id); Place(content, rest, pageW - now.Width - S(12) - rest.Width, y);
            if (pageW - chip.Width - bw < S(300)) { now.Top = rest.Top = title.Bottom + S(8); }
            var id = sv.Id;
            now.Enabled = sv.Kind != RunKind.Running && sv.Kind != RunKind.Blocked;
            now.Click += (o, e) => Do(() => api.Call("backup", new Msg().Set("set", id), null), T("The backup has started"));
            rest.Click += (o, e) => { rSetId = id; rStep = 1; Go("restore"); };
            y = Math.Max(title.Bottom, now.Bottom) + S(4);
            y = Text2(content, sv.Sources, 9f, false, Muted, 0, y, pageW, true).Bottom + S(14);
            // the facts of this backup
            var facts = Card(content, 0, y, pageW, S(90));
            var items = new[]
            {
                new[] { T("Last complete backup"), ClientView.When(lang, sv.LastComplete), T("The last run with every file") },
                new[] { T("Last backup attempt"), sv.LastAttempt == null ? T("Not yet") : ClientView.When(lang, sv.LastAttempt), ClientView.Word(lang, ClientView.Classify(sv.LastResult), sv.LastResult) + (sv.MissedCount > 0 && sv.Kind == RunKind.Partial ? " · " + T("{0} files not backed up", sv.MissedCount) : "") },
                new[] { T("Next backup"), T("Every day at {0}", sv.Hour), "" },
                new[] { T("Last restore test"), sv.LastTest == null ? T("Not yet") : ClientView.When(lang, sv.LastTest), T("Automatic test · the result goes to {0}", ClientView.Company(lang, state)) },
            };
            int cols = pageW < S(640) ? 2 : 4, cw = (pageW - 2 * m) / cols, fy = m, rowH = 0;
            for (int i = 0; i < items.Length; i++)
            {
                if (i > 0 && i % cols == 0) { fy += rowH + S(12); rowH = 0; }
                int x = m + (i % cols) * cw;
                var a = Text2(facts, items[i][0], 8.5f, true, Muted, x, fy, cw - S(12));
                var tone = i == 1 ? ClientView.ToneOf(ClientView.Classify(sv.LastResult)) : Tone.Neutral; if (tone == Tone.Ok) tone = Tone.Neutral;
                var b = Text2(facts, items[i][1], 13f, true, tone == Tone.Neutral ? Ink : ToneColor(tone), x, a.Bottom, cw - S(12));
                var c = Text2(facts, items[i][2], 8.5f, false, Muted, x, b.Bottom, cw - S(12));
                rowH = Math.Max(rowH, c.Bottom - fy);
            }
            facts.Height = fy + rowH + m;
            y = facts.Bottom + S(16);
            if (sv.Kind == RunKind.Partial || sv.Kind == RunKind.Failed) y = ProblemCard(sv, y, false) + S(16);
            bool two = pageW >= S(760);
            int lw = two ? (pageW - S(16)) / 2 : pageW;
            // what is backed up, and "Change" (owner Q5: disabled with the reason when the IT company manages both)
            var what = Card(content, 0, y, lw, S(100));
            Text2(what, T("What is backed up"), 11f, true, Ink, m, m, lw - 2 * m - S(120));
            var change = Btn(T("Change"), false); change.Name = "change";
            Place(what, change, lw - m - change.Width, m - S(6));
            bool locked = state["canSources"] == "0" && state["canSchedule"] == "0";
            change.Enabled = !locked && sv.Kind != RunKind.Blocked;
            change.Click += (o, e) => EditSet(raw);
            int wy = m + S(40);
            if (locked) wy = Text2(what, T(ClientView.Locked), 8.5f, false, Muted, m, wy, lw - 2 * m, false, "lockedChange").Bottom + S(6);
            foreach (var p in sv.Src) { Divider(what, m, wy, lw - 2 * m); wy = Text2(what, "✓  " + p, 9.5f, false, Ok, m, wy + S(6), lw - 2 * m, true).Bottom + S(4); }
            foreach (var p in sv.Skip) { Divider(what, m, wy, lw - 2 * m); var l1 = Text2(what, "✕  " + p, 9.5f, false, Muted, m, wy + S(6), lw - 2 * m, true); wy = Text2(what, T("Not backed up"), 8.5f, false, Muted, m, l1.Bottom, lw - 2 * m).Bottom + S(4); }
            if (sv.Src.Length == 0) wy = Text2(what, sv.Sources, 9.5f, false, Ink, m, wy, lw - 2 * m, true).Bottom + S(4);
            Divider(what, m, wy + S(4), lw - 2 * m); wy += S(12);
            var keep = ClientView.Keep(lang, raw); if (keep.Length > 0) wy = Text2(what, keep, 9f, false, Ink, m, wy, lw - 2 * m).Bottom + S(4);
            if (raw["keyRecovery"] != null) wy = Text2(what, T("Recovery copy of the key kept: {0}", raw["keyRecovery"] == "1" ? T("Yes") : T("No")), 9f, false, Ink, m, wy, lw - 2 * m, false, "keyRecovery").Bottom + S(4);   // A7
            wy = Text2(what, T("Files are encrypted on this computer before they leave, with your password. Without the password nothing can be restored — keep it safe."), 8.5f, false, Muted, m, wy, lw - 2 * m).Bottom + S(4);
            if (sv.Kind == RunKind.Blocked) wy = Text2(what, L.Tr(lang, sv.Blocked) + " " + T("The set and its backups are kept as they are; it does not run."), 9f, false, Warn, m, wy, lw - 2 * m).Bottom;
            what.Height = wy + m;
            // the last runs
            int rx = two ? lw + S(16) : 0, ry0 = two ? y : what.Bottom + S(16), rw = two ? pageW - lw - S(16) : pageW;
            var runs = Card(content, rx, ry0, rw, S(100));
            Text2(runs, T("Recent runs"), 11f, true, Ink, m, m, rw - 2 * m - S(180));
            if (sv.Runs.Count > 0) Strip(runs, sv, rw - m - 14 * S(12), m + S(4), 14);
            int ry = m + S(40);
            foreach (var r in sv.Runs.AsEnumerable().Reverse().Take(8))
            {
                Divider(runs, m, ry, rw - 2 * m);
                var t = Text2(runs, ClientView.When(lang, r.Utc), 9.5f, false, Ink, m, ry + S(10), S(170));
                var ch = Chip(runs, ClientView.Word(lang, r.Run, r.Result), ClientView.ToneOf(r.Run), r.Run, m + S(180), ry + S(8));
                if (r.Errors > 0 && r.Run == RunKind.Partial) Text2(runs, T("{0} files not backed up", r.Errors), 8.5f, false, Muted, m + S(190) + ch.Width, ry + S(12), Math.Max(S(40), rw - 2 * m - S(190) - ch.Width));
                ry += S(42);
            }
            if (sv.Runs.Count == 0) ry = Text2(runs, T("No runs yet."), 9.5f, false, Muted, m, ry, rw - 2 * m).Bottom;
            runs.Height = ry + m;
            y = Math.Max(what.Bottom, runs.Bottom) + S(16);
            // the last run's log (B: the log inside the details) and "Export the log for the IT company"
            y = LogCard(sv, y);
            return y;
        }

        int LogCard(SetView sv, int y)
        {
            int m = S(20);
            var card = Card(content, 0, y, pageW, S(80));
            Text2(card, T("Log of the last run"), 11f, true, Ink, m, m, pageW - 2 * m - S(260));
            var exp = Btn(T("Export the log for {0}", ClientView.Company(lang, state)), false); exp.Name = "exportLog";
            Place(card, exp, pageW - m - exp.Width, m - S(6));
            var id = sv.Id;
            exp.Click += (o, e) => Do(() => api.Call("runlog", null, new Dictionary<string, string> { { "set", id } }), null, r => ExportLog(sv.Name, r), false);
            int cy = m + S(40);
            if (setLogFor != sv.Id + "|" + sv.LastResult + sv.LastAttempt)
            {
                setLogFor = sv.Id + "|" + sv.LastResult + sv.LastAttempt; setLog = null;
                if (!preview) Do(() => api.Call("runlog", null, new Dictionary<string, string> { { "set", id } }), null, r => { setLog = r; if (page == "set" && selSet == id) Render(); }, false, ex => { setLog = new Msg(); });
                else setLog = api.Call("runlog", null, new Dictionary<string, string> { { "set", id } });
            }
            if (setLog == null) cy = Text2(card, T("Loading…"), 9f, false, Muted, m, cy, pageW - 2 * m).Bottom;
            else
            {
                var lines = setLog.List("lines").Select(x => x["l"]).ToList();
                if (lines.Count == 0) cy = Text2(card, T("No log on this computer yet — it is written by the next backup."), 9f, false, Muted, m, cy, pageW - 2 * m).Bottom;
                var interesting = lines.Where(l => { var f = AhsayLog.Fields(l); return f.Length > 1 && (f[1] == "err" || f[1] == "warn" || f[1] == "end" || f[1] == "start"); }).ToList();
                var show = (interesting.Count > 0 ? interesting : lines).Take(12).ToList();
                if (show.Count > 0)
                {
                    var box = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = false, BackColor = Color.FromArgb(250, 251, 252), Font = new Font("Consolas", (float)(8.5 * ui / sys)), Text = RunNotes.Readable(show).TrimEnd(), Width = pageW - 2 * m, Height = Math.Min(S(220), S(20) * show.Count + S(12)), RightToLeft = RightToLeft.No };
                    box.Location = new Point(m, cy); card.Controls.Add(box); cy = box.Bottom;
                }
            }
            card.Height = cy + m;
            return card.Bottom;
        }

        void ExportLog(string setName, Msg r)
        {
            using (var d = new SaveFileDialog { FileName = (state["computer"] ?? "computer") + " - " + string.Join("_", setName.Split(Path.GetInvalidFileNameChars())) + " - log.txt", Filter = "Text|*.txt", RestoreDirectory = true })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                var head = (state["product"] ?? "") + " — " + (state["computer"] ?? "") + " — " + setName + "\r\n" + T("Exported {0}", DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)) + "\r\n\r\n";
                try { File.WriteAllText(d.FileName, head + (r["text"] ?? ""), new UTF8Encoding(true)); Message(T("The log was saved: {0}", d.FileName), false); }
                catch (Exception e) { Message(e.Message, true); }
            }
        }

        // ---------------------------------------------------------------- Restore in 3 steps, with the bar fixed at the bottom (UX-1, B5)

        int rStep = 1; string rSetId, rLoadedFor, rPointId, rFilesFor, rFilter = "", rTarget, rJob; bool rOverwrite, rLoading;
        List<Msg> rPoints, rFiles; readonly HashSet<string> rChecked = new HashSet<string>(); List<string> rPaths = new List<string>();

        int RestorePage(int y)
        {
            var sets = state.List("sets");
            if (rSetId == null || !sets.Any(s => s["id"] == rSetId)) rSetId = (sets.OrderByDescending(s => s["mine"] != "0").ThenByDescending(s => s["last"] ?? "").FirstOrDefault() ?? new Msg())["id"];
            // the title and the steps
            var title = Text2(content, T("Restore files"), 18f, true, Ink, 0, y, Math.Min(pageW, S(260)), false, "title");
            int sx = Math.Min(pageW, S(260)) + S(16);
            for (int i = 0; i < 3; i++)
            {
                var done = i + 1 < rStep; var on = i + 1 == rStep;
                var t = (done ? "✓" : (i + 1).ToString(CultureInfo.InvariantCulture)) + "  " + T(ClientView.RestoreSteps[i]);
                var font = F(9.5f, on); var w = TextRenderer.MeasureText(t, font).Width + S(16);
                if (sx + w > pageW) break;
                var l = new Label { Text = t, Font = font, ForeColor = on ? Brand : done ? Ok : Muted, AutoSize = false, Width = w, Height = S(28), TextAlign = ContentAlignment.MiddleCenter, RightToLeft = Dir, BackColor = on ? Soft : Color.Transparent, Name = "step" + (i + 1) };
                l.Location = new Point(MX(content, sx, w), y + S(4)); content.Controls.Add(l); sx += w + S(8);
            }
            y = title.Bottom + S(16);
            if (sets.Count == 0) { Text2(content, T("There are no backups to restore."), 10f, false, Muted, 0, y, pageW); RestoreBar(T("Back to Home"), null, null, () => Go("home"), null, null); return y + S(40); }
            if (rStep == 1) return RestoreStep1(y, sets);
            if (rStep == 2) return RestoreStep2(y, sets);
            return RestoreStep3(y, sets);
        }

        /// <summary>The bar fixed at the bottom: the primary action at the reading end, never outside the window (ClientView.Frame).</summary>
        void RestoreBar(string primary, string secondary, string third, Action p, Action s, Action t, bool primaryOn = true, string info = null)
        {
            var pb = Btn(primary, true); var sb = secondary == null ? null : Btn(secondary, false); var tb = third == null ? null : Btn(third, false);
            var f = ClientView.Frame(ClientSize.Width, ClientSize.Height, ui, Rtl, true, pb.Width + S(20), sb == null ? 0 : sb.Width + S(20), tb == null ? 0 : tb.Width + S(20));
            bar.Height = f.Bar.H;
            Action<Button, Box, string, Action> put = (b, box, name, a) =>
            {
                if (b == null || box == null) return;
                b.Bounds = new Rectangle(box.X - f.Bar.X, box.Y - f.Bar.Y, box.W, box.H); b.Name = name; b.RightToLeft = Dir; bar.Controls.Add(b);
                if (a != null) b.Click += (o, e) => a();
            };
            put(pb, f.Primary, "barPrimary", p); pb.Enabled = primaryOn;
            if (sb != null) put(sb, f.Secondary, "barSecondary", s);
            if (tb != null) put(tb, f.Tertiary, "barTertiary", t);
            if (info != null)
            {
                int left = Rtl ? f.Primary.Right - f.Bar.X + S(16) : (tb != null ? f.Tertiary.Right : f.Secondary.Right) - f.Bar.X + S(16);
                int right = Rtl ? (tb != null ? f.Tertiary.X : f.Secondary.X) - f.Bar.X - S(16) : f.Primary.X - f.Bar.X - S(16);
                if (right - left > S(120))
                {
                    var l = new Label { Text = info, Font = F(9f), ForeColor = Muted, AutoSize = false, AutoEllipsis = true, Bounds = new Rectangle(left, 0, right - left, f.Bar.H), TextAlign = Rtl ? ContentAlignment.MiddleRight : ContentAlignment.MiddleLeft, RightToLeft = Dir, UseMnemonic = false };
                    bar.Controls.Add(l);
                }
            }
        }

        void RestoreReset() { rStep = 1; rChecked.Clear(); rPaths.Clear(); rJob = null; rCheck = null; }

        int RestoreStep1(int y, List<Msg> sets)
        {
            int m = S(20);
            var top = Card(content, 0, y, pageW, S(100));
            var q = Text2(top, T("Which backup and which day?"), 11f, true, Ink, m, m, pageW - 2 * m);
            var setBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = Math.Min(S(320), pageW - 2 * m), Font = F(10f), RightToLeft = Dir, AccessibleName = T("Backup"), Name = "restoreSet" };
            foreach (var s in sets) setBox.Items.Add(s["name"] + (s["mine"] == "0" ? "  (" + s["computer"] + ")" : ""));
            setBox.SelectedIndex = Math.Max(0, sets.FindIndex(s => s["id"] == rSetId));
            setBox.Location = new Point(MX(top, m, setBox.Width), q.Bottom + S(8)); top.Controls.Add(setBox);
            setBox.SelectedIndexChanged += (o, e) => { rSetId = sets[setBox.SelectedIndex]["id"]; rPoints = null; rLoadedFor = null; rPointId = null; rFiles = null; rFilesFor = null; rChecked.Clear(); Render(); };
            int cy = setBox.Bottom + S(14);
            if (rLoadedFor != rSetId && !rLoading)
            {
                rLoading = true; var sid = rSetId;
                Do(() => api.Call("points", null, new Dictionary<string, string> { { "set", sid } }), null, r =>
                {
                    rLoading = false; if (sid != rSetId) return;
                    rPoints = r.List("points"); rLoadedFor = sid; rPointId = rPoints.Count > 0 ? rPoints[0]["id"] : null; rFiles = null; rFilesFor = null;
                    if (page == "restore") Render();
                }, false, ex => { rLoading = false; rPoints = new List<Msg>(); rLoadedFor = sid; if (page == "restore") Render(); });
            }
            if (rLoadedFor != rSetId || rPoints == null) cy = Text2(top, T("Loading restore points…"), 9.5f, false, Muted, m, cy, pageW - 2 * m).Bottom + S(8);
            else if (rPoints.Count == 0) cy = Text2(top, T("This backup has no restore points yet — it runs at the scheduled time, or click \"Back up now\"."), 9.5f, false, Muted, m, cy, pageW - 2 * m).Bottom + S(8);
            else
            {
                // the day strip: one button per day with a point, newest first ("yesterday" is one click)
                var days = ClientView.Days(rPoints);
                var cur = rPoints.FirstOrDefault(p => p["id"] == rPointId) ?? rPoints[0];
                var curDay = (ClientView.ParseTime(cur["time"]) ?? ClientView.ParseTime(cur["id"]) ?? DateTime.UtcNow).ToLocalTime().Date;
                int dw = S(92), dx = m, dh = S(56);
                foreach (var d in days)
                {
                    if (dx + dw > pageW - m) break;
                    bool on = d.Key == curDay;
                    var label = d.Key == DateTime.Today ? T("Today") : d.Key == DateTime.Today.AddDays(-1) ? T("Yesterday") : CultureInfo.GetCultureInfo(L.Norm(lang) == "he" ? "he-IL" : "en-US").DateTimeFormat.GetAbbreviatedDayName(d.Key.DayOfWeek);
                    var b = new Button { Text = label + "\n" + d.Key.ToString("dd.MM", CultureInfo.InvariantCulture), Width = dw, Height = dh, FlatStyle = FlatStyle.Flat, Font = F(9.5f, true), BackColor = on ? Brand : Color.White, ForeColor = on ? Color.White : Ink, Cursor = Cursors.Hand, RightToLeft = Dir, Name = "day" + d.Key.ToString("yyyyMMdd", CultureInfo.InvariantCulture) };
                    b.FlatAppearance.BorderColor = on ? Brand : Line;
                    var first = d.Value[0]["id"];
                    b.Click += (o, e) => { rPointId = first; rFiles = null; rFilesFor = null; rChecked.Clear(); Render(); };
                    b.Location = new Point(MX(top, dx, dw), cy); top.Controls.Add(b); dx += dw + S(8);
                }
                cy += dh + S(10);
                // the points of the chosen day, and every point (another date)
                var todays = days.FirstOrDefault(d => d.Key == curDay).Value ?? new List<Msg>();
                int px = m;
                if (todays.Count > 1)
                {
                    var pl = Text2(top, T("Points on {0}:", curDay.ToString("dd.MM", CultureInfo.InvariantCulture)), 9f, false, Muted, m, cy + S(4), S(120)); px = m + S(124);
                    foreach (var p in todays)
                    {
                        bool on = p["id"] == rPointId;
                        var t = (ClientView.ParseTime(p["time"]) ?? ClientView.ParseTime(p["id"]) ?? DateTime.UtcNow).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
                        var b = new Button { Text = t, Width = S(70), Height = S(28), FlatStyle = FlatStyle.Flat, Font = F(9f, true), BackColor = on ? Ink : Color.White, ForeColor = on ? Color.White : Ink, RightToLeft = RightToLeft.No };
                        b.FlatAppearance.BorderColor = on ? Ink : Line;
                        var pid = p["id"]; b.Click += (o, e) => { rPointId = pid; rFiles = null; rFilesFor = null; rChecked.Clear(); Render(); };
                        if (px + b.Width > pageW - m) break;
                        b.Location = new Point(MX(top, px, b.Width), cy); top.Controls.Add(b); px += b.Width + S(6);
                    }
                    cy += S(36);
                }
                var pcl = Text2(top, T("Restore point (date)"), 9f, true, Muted, m, cy, pageW - 2 * m);
                var pointBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = Math.Min(S(360), pageW - 2 * m), Font = F(10f), RightToLeft = Dir, AccessibleName = T("Restore point (date)"), Name = "restorePoint" };
                for (int i = 0; i < rPoints.Count; i++) pointBox.Items.Add(i == 0 ? T("Latest — {0}", ClientView.When(lang, ClientView.ParseTime(rPoints[i]["time"]) ?? ClientView.ParseTime(rPoints[i]["id"]))) : ClientView.When(lang, ClientView.ParseTime(rPoints[i]["time"]) ?? ClientView.ParseTime(rPoints[i]["id"])));
                pointBox.SelectedIndex = Math.Max(0, rPoints.FindIndex(p => p["id"] == rPointId));
                pointBox.Location = new Point(MX(top, m, pointBox.Width), pcl.Bottom + S(2)); top.Controls.Add(pointBox);
                pointBox.SelectedIndexChanged += (o, e) => { rPointId = rPoints[pointBox.SelectedIndex]["id"]; rFiles = null; rFilesFor = null; rChecked.Clear(); Render(); };
                cy = pointBox.Bottom + S(6);
            }
            top.Height = cy + m;
            y = top.Bottom + S(16);
            // the files of the point: search, a tree with boxes (a folder = everything in it; none = everything)
            var files = Card(content, 0, y, pageW, S(100));
            var fl = Text2(files, T("Files (none selected = everything)"), 11f, true, Ink, m, m, pageW - 2 * m);
            var filter = new TextBox { Width = Math.Min(S(360), pageW - 2 * m), Font = F(10f), Text = rFilter, AccessibleName = T("Search a file or folder in this point"), RightToLeft = RightToLeft.No, Name = "restoreFilter" };
            filter.Location = new Point(MX(files, m, filter.Width), fl.Bottom + S(8)); files.Controls.Add(filter);
            var hint = Text2(files, T("Search a file or folder in this point"), 8.5f, false, Muted, m, filter.Bottom + S(2), pageW - 2 * m);
            // the tree fills the rest of the window above the bar (the bar never moves)
            int treeH = Math.Max(S(160), (frame == null ? S(300) : frame.Page.H) - (y + hint.Bottom + S(28)) - S(28) - S(56));
            var tree = new TreeView { CheckBoxes = true, Width = pageW - 2 * m, Height = treeH, RightToLeftLayout = Rtl, RightToLeft = Dir, Font = F(9.5f), BorderStyle = BorderStyle.FixedSingle, AccessibleName = T("Files (none selected = everything)"), Name = "restoreTree" };
            tree.Location = new Point(m, hint.Bottom + S(6)); files.Controls.Add(tree);
            files.Height = tree.Bottom + m;
            bool filling = false;
            Action fill = () =>
            {
                filling = true;
                FillTree(tree, rFiles ?? new List<Msg>(), rFilter);
                Action<TreeNodeCollection> mark = null; mark = ns => { foreach (TreeNode n in ns) { if (n.Tag is string && rChecked.Contains((string)n.Tag)) { n.Checked = true; for (var up = n.Parent; up != null; up = up.Parent) up.Expand(); } mark(n.Nodes); } };
                mark(tree.Nodes); filling = false;
            };
            tree.AfterCheck += (o, e) =>
            {
                if (filling || !(e.Node.Tag is string)) return;
                if (e.Node.Checked) rChecked.Add((string)e.Node.Tag); else rChecked.Remove((string)e.Node.Tag);
                var info = bar.Controls.OfType<Label>().FirstOrDefault(); if (info != null) info.Text = SelectionInfo(sets);
                var pb = bar.Controls.OfType<Button>().FirstOrDefault(b => b.Name == "barPrimary"); if (pb != null) pb.Enabled = rPointId != null;
            };
            filter.TextChanged += (o, e) => { rFilter = filter.Text; fill(); };
            if (rPointId != null && rFilesFor != rPointId)
            {
                tree.Nodes.Add(T("Loading files…"));
                var sid = rSetId; var pid = rPointId;
                Do(() => api.Call("files", null, new Dictionary<string, string> { { "set", sid }, { "point", pid } }), null, r => { if (pid != rPointId) return; rFiles = r.List("files"); rFilesFor = pid; if (page == "restore" && rStep == 1 && !tree.IsDisposed) fill(); }, false);
            }
            else if (rFiles != null) fill();
            RestoreBar(ClientView.RestoreBar(lang, 1, 0)[0], ClientView.RestoreBar(lang, 1, 0)[1], null,
                () => { if (rPointId == null) return; rPaths = tree.IsDisposed ? rPaths : Checked(tree.Nodes).ToList(); rStep = 2; Render(); },
                () => { RestoreReset(); Go("home"); }, null, rPointId != null, SelectionInfo(sets));
            return files.Bottom;
        }

        string SelectionInfo(List<Msg> sets)
        {
            var s = sets.FirstOrDefault(x => x["id"] == rSetId) ?? new Msg();
            var p = rPoints == null ? null : rPoints.FirstOrDefault(x => x["id"] == rPointId);
            var when = p == null ? "" : ClientView.When(lang, ClientView.ParseTime(p["time"]) ?? ClientView.ParseTime(p["id"]));
            var n = rChecked.Count;
            return (n == 0 ? T("Everything in the point") : T("{0} selected", n)) + "  ·  " + T("from {0}", s["name"] ?? "") + (when.Length > 0 ? "  ·  " + when : "");
        }

        // UI-Q2: where the files go (a new folder, or where they were - own engine), and the answer of "restorecheck" when files exist
        string rLocation = "alternate"; Msg rCheck;

        string EngineOf(List<Msg> sets) { return (sets.FirstOrDefault(x => x["id"] == rSetId) ?? new Msg())["engine"] ?? ""; }

        Msg RestoreBody(List<Msg> sets, string decision)
        {
            var msg = new Msg().Set("set", rSetId).Set("point", rPointId);
            if (rLocation == "original" && ClientView.OriginalLocationAvailable(EngineOf(sets))) msg.Set("location", "original");
            else msg.Set("target", rTarget).Set("overwrite", rOverwrite ? "1" : "0");
            if (decision != null) msg.Set("existing", decision);
            foreach (var p in rPaths) msg.Add("paths", new Msg().Set("p", p));
            return msg;
        }

        /// <summary>UI-Q2: check the destination first (own engine); files that exist are a question in the window, never silent.</summary>
        void RestoreGo(List<Msg> sets)
        {
            var original = rLocation == "original" && ClientView.OriginalLocationAvailable(EngineOf(sets));
            if (!original && string.IsNullOrEmpty(rTarget)) { Message(T("Choose a destination folder."), true); return; }
            if (rPointId == null) return;
            SetBarBusy(true);
            if (EngineOf(sets) == "RESTIC") { RestoreStart(sets, null); return; }   // restic: no check in the local API; "Replace existing files" decides
            var chk = new Msg().Set("set", rSetId).Set("point", rPointId);
            if (original) chk.Set("location", "original"); else chk.Set("target", rTarget);
            foreach (var p in rPaths) chk.Add("paths", new Msg().Set("p", p));
            Do(() => api.Call("restorecheck", chk, null), null, r =>
            {
                if (ClientView.Existing(lang, r) != null) { rCheck = r; ShowFromTray(); Render(); }
                else RestoreStart(sets, null);
            }, false, ex => SetBarBusy(false));
        }

        void RestoreStart(List<Msg> sets, string decision)
        {
            SetBarBusy(true);
            var body = RestoreBody(sets, decision);
            // UX-1: no message box behind the window - the window comes to the front on its own result page
            Do(() => api.Call("restore", body, null), null, r =>
            {
                if (r["cancelled"] == "1") { rCheck = null; Render(); return; }
                rJob = r["job"]; rStep = 3; rCheck = null; ShowFromTray(); Render();
            }, true, ex =>
            {
                SetBarBusy(false);
                // a file appeared at the destination since the check: the API refuses with EXISTS - ask again
                var ae = ex as AgentException; if (ae != null && ae.Code == "EXISTS") RestoreGo(sets);
            });
        }

        void SetBarBusy(bool busy) { foreach (var b in bar.Controls.OfType<Button>()) if (!b.IsDisposed) b.Enabled = !busy; }

        int RestoreStep2(int y, List<Msg> sets)
        {
            int m = S(20);
            if (rCheck != null) return RestoreExisting(y, sets);
            bool two = pageW >= S(760);
            int lw = two ? (pageW - S(16)) / 2 : pageW;
            var engine = EngineOf(sets); bool origOk = ClientView.OriginalLocationAvailable(engine);
            if (!origOk && rLocation == "original") rLocation = "alternate";
            if (string.IsNullOrEmpty(rTarget)) rTarget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "Restore " + DateTime.Now.ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture));
            // where (owner Q2: a new folder, recommended; or the original location - own engine)
            var where = Card(content, 0, y, lw, S(100));
            var q = Text2(where, T("Where to restore?"), 11f, true, Ink, m, m, lw - 2 * m);
            var toNew = new RadioButton { Text = T("To a new folder (recommended)"), Checked = rLocation != "original", AutoSize = false, Width = lw - 2 * m, Height = S(26), Font = F(10f, true), RightToLeft = Dir, Name = "newFolder" };
            toNew.Location = new Point(m, q.Bottom + S(8)); where.Controls.Add(toNew);
            var h1 = Text2(where, T("The files on the computer stay as they are."), 8.5f, false, Muted, m + S(22), toNew.Bottom, lw - 2 * m - S(22));
            var tl = Text2(where, T("Restore to folder"), 9f, true, Muted, m + S(22), h1.Bottom + S(6), lw - 2 * m - S(22));
            var browse = Btn("…", false); browse.Width = S(48);
            var target = new TextBox { Width = lw - 2 * m - S(22) - browse.Width - S(8), Font = F(10f), Text = rTarget, RightToLeft = RightToLeft.No, AccessibleName = T("Restore to folder"), Name = "restoreTarget", Enabled = rLocation != "original" };
            if (Rtl) target.TextAlign = HorizontalAlignment.Right;
            target.Location = new Point(MX(where, m + S(22), target.Width), tl.Bottom + S(2)); where.Controls.Add(target);
            Place(where, browse, m + S(22) + target.Width + S(8), tl.Bottom + S(1)); browse.Height = target.Height + S(2); browse.Enabled = target.Enabled;
            target.TextChanged += (o, e) => rTarget = target.Text;
            browse.Click += (o, e) => { using (var f = new FolderBrowserDialog { SelectedPath = target.Text }) if (f.ShowDialog(this) == DialogResult.OK) target.Text = f.SelectedPath; };
            var orig = new RadioButton { Text = T("To the original location"), Checked = rLocation == "original", Enabled = origOk, AutoSize = false, Width = lw - 2 * m, Height = S(26), Font = F(10f, true), RightToLeft = Dir, Name = "originalLocation" };
            orig.Location = new Point(m, target.Bottom + S(14)); where.Controls.Add(orig);
            var ow = Text2(where, origOk ? T("The files go back where they were. If a file is already there, you are asked first.") : T(ClientView.OriginalLocationWhy), 8.5f, false, Muted, m + S(22), orig.Bottom, lw - 2 * m - S(22));
            toNew.CheckedChanged += (o, e) => { if (toNew.Checked) { rLocation = "alternate"; target.Enabled = browse.Enabled = true; } };
            orig.CheckedChanged += (o, e) => { if (orig.Checked) { rLocation = "original"; target.Enabled = browse.Enabled = false; } };
            int wb = ow.Bottom;
            if (!origOk)
            {
                // restic: the local API cannot check the folder first - "Replace existing files" decides, as before
                var over = new CheckBox { Text = T(" Replace existing files").Trim(), Checked = rOverwrite, AutoSize = false, Width = lw - 2 * m, Height = S(26), Font = F(9.5f), RightToLeft = Dir, Name = "replace" };
                over.Location = new Point(m, ow.Bottom + S(10)); where.Controls.Add(over);
                over.CheckedChanged += (o, e) => rOverwrite = over.Checked;
                wb = Text2(where, T("Unticked: a file that is already in the folder is not replaced."), 8.5f, false, Muted, m + S(22), over.Bottom, lw - 2 * m - S(22)).Bottom;
            }
            else wb = Text2(where, T("If a file is already in the folder, you are asked first: replace, skip or cancel."), 8.5f, false, Muted, m, ow.Bottom + S(10), lw - 2 * m).Bottom;
            where.Height = wb + m;
            // what will be restored, and B5
            int sx = two ? lw + S(16) : 0, sy = two ? y : where.Bottom + S(16), sw = two ? pageW - lw - S(16) : pageW;
            var what = Card(content, sx, sy, sw, S(100));
            var s0 = sets.FirstOrDefault(x => x["id"] == rSetId) ?? new Msg();
            var p0 = rPoints == null ? null : rPoints.FirstOrDefault(x => x["id"] == rPointId);
            var wq = Text2(what, T("What will be restored"), 11f, true, Ink, m, m, sw - 2 * m);
            var b1 = Text2(what, T("Backup") + ":  " + (s0["name"] ?? ""), 9.5f, false, Ink, m, wq.Bottom + S(8), sw - 2 * m);
            var b2 = Text2(what, T("Restore point (date)") + ":  " + (p0 == null ? "" : ClientView.When(lang, ClientView.ParseTime(p0["time"]) ?? ClientView.ParseTime(p0["id"]))), 9.5f, false, Ink, m, b1.Bottom + S(2), sw - 2 * m);
            int wy = b2.Bottom + S(8);
            Divider(what, m, wy, sw - 2 * m); wy += S(8);
            if (rPaths.Count == 0) wy = Text2(what, T("Everything in the point"), 9.5f, true, Ink, m, wy, sw - 2 * m).Bottom;
            foreach (var p in rPaths.Take(6)) wy = Text2(what, "▫ " + p, 9f, false, Ink, m, wy, sw - 2 * m, true).Bottom;
            if (rPaths.Count > 6) wy = Text2(what, T("and {0} more", rPaths.Count - 6), 9f, false, Muted, m, wy, sw - 2 * m).Bottom;
            var keeps = Card(what, m, wy + S(12), sw - 2 * m, S(40), ToneFill(Tone.Warn), C("#F1C27D")); keeps.Name = "b5";
            var kl = Text2(keeps, "ⓘ  " + T(ClientView.RestoreKeeps), 9.5f, false, Ink, S(12), S(10), keeps.Width - S(24), false, "restoreKeeps");
            keeps.Height = kl.Bottom + S(10);
            var code = Text2(what, T("Your password (and the code, when two-step verification is on) is asked when you restore."), 8.5f, false, Muted, m, keeps.Bottom + S(10), sw - 2 * m);
            what.Height = code.Bottom + m;
            var sets2 = sets;
            RestoreBar(ClientView.RestoreBar(lang, 2, rPaths.Count)[0], ClientView.RestoreBar(lang, 2, rPaths.Count)[1], null, () => RestoreGo(sets2), () => { rStep = 1; Render(); }, null, true, SelectionInfo(sets2));
            return Math.Max(where.Bottom, what.Bottom);
        }

        /// <summary>UI-Q2: files already exist at the destination - the count, the list, and Replace / Skip / Cancel in the fixed bar.</summary>
        int RestoreExisting(int y, List<Msg> sets)
        {
            int m = S(20);
            var q = ClientView.Existing(lang, rCheck);
            var card = Card(content, 0, y, pageW, S(100), ToneFill(Tone.Warn), ToneColor(Tone.Warn)); card.Name = "existing";
            var t = Text2(card, "⚠  " + q.Title, 12f, true, Ink, m, m, pageW - 2 * m, false, "existingTitle");
            var tx = Text2(card, q.Text, 9.5f, false, Ink, m, t.Bottom + S(4), pageW - 2 * m);
            // the list fills the room above the bar; it scrolls inside itself
            int room = (frame == null ? S(400) : frame.Page.H) - (y + tx.Bottom + S(16)) - S(28) - S(60);
            var box = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = false, BackColor = Color.White, Font = new Font("Consolas", (float)(9 * ui / sys)), Text = string.Join("\r\n", q.Files.ToArray()), Width = pageW - 2 * m, Height = Math.Max(S(90), Math.Min(S(320), room)), RightToLeft = RightToLeft.No, Name = "existingList", AccessibleName = q.Title };
            box.Location = new Point(m, tx.Bottom + S(10)); card.Controls.Add(box);
            int cy = box.Bottom;
            if (q.More.Length > 0) cy = Text2(card, q.More, 9f, false, Muted, m, cy + S(4), pageW - 2 * m).Bottom;
            card.Height = cy + m;
            var sets2 = sets;
            RestoreBar(q.Buttons[0], q.Buttons[1], q.Buttons[2], () => RestoreStart(sets2, q.Decisions[0]), () => RestoreStart(sets2, q.Decisions[1]), () => RestoreStart(sets2, q.Decisions[2]));
            return card.Bottom;
        }

        int RestoreStep3(int y, List<Msg> sets)
        {
            int m = S(24);
            var job = jobs.FirstOrDefault(j => j["id"] == rJob);
            var v = ClientView.RestoreResult(lang, job, rPaths.Count);
            var card = Card(content, 0, y, pageW, S(100));
            Badge(card, v.Tone, v.Running ? "↻" : v.Success ? "✓" : "✕", m, m, S(72));
            int tx = m + S(92), tw = pageW - tx - m;
            var cap = Text2(card, v.Caption, 9f, true, ToneColor(v.Tone), tx, m, tw);
            var big = Text2(card, v.Headline, 18f, true, Ink, tx, cap.Bottom, tw, false, "state");
            var where = Text2(card, rLocation == "original" && (job == null || job["location"] != "alternate") ? T("To the original location") : T("To folder {0}", rTarget ?? ""), 9.5f, false, Muted, tx, big.Bottom + S(2), tw, true);
            int cy = where.Bottom;
            if (v.Running) { var pb = new ProgressBar { Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30, Width = Math.Min(tw, S(420)), Height = S(6) }; pb.Location = new Point(MX(card, tx, pb.Width), cy + S(10)); card.Controls.Add(pb); cy = pb.Bottom; }
            if (v.Counts.Length > 0) cy = Text2(card, v.Counts, 10f, false, Ink, tx, cy + S(10), tw, false, "restoreDetail").Bottom;
            else if (job != null && !string.IsNullOrEmpty(job["detail"])) cy = Text2(card, L.Tr(lang, job["detail"]), 10f, false, v.Success ? Ink : Bad, tx, cy + S(10), tw, false, "restoreDetail").Bottom;
            // UI-Q1: the verification - only what the job really checked
            foreach (var line in v.Verification) cy = Text2(card, "✓  " + line, 10f, true, Ok, tx, cy + S(4), tw, false, "restoreVerified").Bottom;
            if (v.Mismatch.Count > 0 || v.MismatchNote.Length > 0)
            {
                var err = Card(card, tx, cy + S(10), tw, S(40), ToneFill(Tone.Bad), ToneColor(Tone.Bad)); err.Name = "restoreError";
                int ey = Text2(err, "✕  " + v.MismatchNote, 9.5f, true, Bad, S(12), S(10), tw - S(24)).Bottom;
                foreach (var p in v.Mismatch.Take(10)) ey = Text2(err, p, 9f, false, Ink, S(30), ey + S(2), tw - S(42), true).Bottom;
                if (v.Mismatch.Count > 10) ey = Text2(err, T("and {0} more", v.Mismatch.Count - 10), 9f, false, Muted, S(30), ey, tw - S(42)).Bottom;
                err.Height = ey + S(10); cy = err.Bottom;
            }
            cy = Math.Max(cy, m + S(72)) + S(16);
            Divider(card, m, cy, pageW - 2 * m); cy += S(14);
            var note = Card(card, m, cy, pageW - 2 * m, S(40), C("#EEF0F3"), Line);
            var nl = Text2(note, "ⓘ  " + T(ClientView.RestoreKeepsAfter), 9.5f, false, Ink, S(12), S(10), note.Width - S(24), false, "restoreKeepsAfter");
            note.Height = nl.Bottom + S(10);
            card.Height = note.Bottom + m;
            var bars = ClientView.RestoreBar(lang, 3, rPaths.Count);
            var folder = rLocation == "original" ? null : rTarget;
            RestoreBar(bars[0], bars[1], bars[2],
                () => { try { if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder)) System.Diagnostics.Process.Start("explorer.exe", "\"" + folder + "\""); } catch (Exception e) { Message(e.Message, true); } },
                () => { RestoreReset(); Go("home"); }, () => { RestoreReset(); Render(); }, !v.Running && folder != null);
            return card.Bottom;
        }

        // ---------------------------------------------------------------- History: rows by day, a row opens in place

        string hFilter = "all", hSet = "", hOpen;

        int HistoryPage(int y)
        {
            var all = ClientView.History(lang, state, jobs, "all", null);
            y = PageTitle(y, T("History"), T("Click a row to see what happened and what to do."));
            var problems = all.Count(r => r.Run == RunKind.Partial || r.Run == RunKind.Failed);
            int x = 0;
            foreach (var f in new[] { new[] { "all", T("All") }, new[] { "backups", T("Backups") }, new[] { "restores", T("Restores") }, new[] { "problems", T("Only problems ({0})", problems) } })
            {
                bool on = hFilter == f[0];
                var b = Btn(f[1], false); b.Height = S(32); b.BackColor = on ? Ink : Color.White; b.ForeColor = on ? Color.White : Ink; b.Name = "filter_" + f[0];
                var key = f[0]; b.Click += (o, e) => { hFilter = key; Render(); };
                if (x + b.Width > pageW) break;
                Place(content, b, x, y); x += b.Width + S(8);
            }
            var sets = state.List("sets").Where(s => s["mine"] != "0").ToList();
            var sb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(220), Font = F(9.5f), RightToLeft = Dir, AccessibleName = T("Backup") };
            sb.Items.Add(T("All backups")); foreach (var s in sets) sb.Items.Add(s["name"]);
            sb.SelectedIndex = Math.Max(0, sets.FindIndex(s => s["id"] == hSet) + 1);
            sb.SelectedIndexChanged += (o, e) => { hSet = sb.SelectedIndex == 0 ? "" : sets[sb.SelectedIndex - 1]["id"]; Render(); };
            if (x + sb.Width <= pageW) { sb.Location = new Point(MX(content, pageW - sb.Width, sb.Width), y + S(4)); content.Controls.Add(sb); y += S(48); }
            else { y += S(40); sb.Location = new Point(MX(content, 0, sb.Width), y); content.Controls.Add(sb); y += S(40); }
            var rows = ClientView.History(lang, state, jobs, hFilter, hSet).Take(80).ToList();
            var list = Card(content, 0, y, pageW, S(100));
            int ly = 0, m = S(16);
            if (rows.Count == 0) ly = Text2(list, T("No runs yet."), 10f, false, Muted, m, m, pageW - 2 * m).Bottom + m;
            DateTime? day = null;
            var lastOf = sets.ToDictionary(s => s["id"], s => ClientView.ParseTime(s["last"]));
            foreach (var r in rows)
            {
                var d = r.Utc.ToLocalTime().Date;
                if (day != d)
                {
                    day = d;
                    var dh = new Panel { Width = pageW - 2, Height = S(32), BackColor = Color.FromArgb(248, 249, 251), Location = new Point(1, ly + 1) }; list.Controls.Add(dh);
                    Text2(dh, (d == DateTime.Today ? T("Today") + " — " : d == DateTime.Today.AddDays(-1) ? T("Yesterday") + " — " : "") + d.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture), 9f, true, Muted, m, S(7), pageW - 2 * m);
                    ly = dh.Bottom;
                }
                var key = r.Kind + r.SetId + r.Utc.Ticks;
                bool open = hOpen == key;
                bool bad = r.Run == RunKind.Partial || r.Run == RunKind.Failed;
                var row = new Panel { Width = pageW - 2, Height = S(44), BackColor = open ? (bad ? ToneFill(r.Run == RunKind.Partial ? Tone.Warn : Tone.Bad) : Bg) : Color.White, Cursor = Cursors.Hand, Location = new Point(1, ly) };
                list.Controls.Add(row);
                EventHandler toggle = (o, e) => { hOpen = open ? null : key; Render(); };
                row.Click += toggle;
                var c1 = Text2(row, r.Utc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture), 10f, true, Ink, m, S(12), S(56)); c1.Click += toggle;
                var c2 = Text2(row, r.Kind == "restore" ? T("Restore") : T("Backup"), 9.5f, false, Muted, m + S(60), S(12), S(80)); c2.Click += toggle;
                var c3 = Text2(row, r.SetName, 9.5f, false, Ink, m + S(144), S(12), Math.Max(S(60), Math.Min(S(200), pageW / 5))); c3.Click += toggle;
                var chip = Chip(row, ClientView.Word(lang, r.Run, r.Result), ClientView.ToneOf(r.Run), r.Run, m + S(144) + c3.Width + S(8), S(10));
                var note = r.Kind == "restore" ? L.Tr(lang, r.Detail) : r.Errors > 0 && r.Run == RunKind.Partial ? T("{0} files not backed up", r.Errors) : "";
                int nx = m + S(144) + c3.Width + S(16) + chip.Width;
                if (pageW - nx - m - S(24) > S(60)) { var c4 = Text2(row, note, 9f, false, Muted, nx, S(13), pageW - nx - m - S(24)); c4.AutoEllipsis = true; c4.Height = S(20); c4.Click += toggle; }
                var arrow = Text2(row, open ? "▾" : (Rtl ? "‹" : "›"), 10f, true, Muted, pageW - m - S(16), S(11), S(16)); arrow.Click += toggle;
                ly = row.Bottom;
                if (open)
                {
                    var det = new Panel { Width = pageW - 2, BackColor = row.BackColor, Location = new Point(1, ly) }; list.Controls.Add(det);
                    int dy = 0; int dx = m + S(60), dw = pageW - dx - m;
                    var sv = r.Kind == "backup" ? ClientView.Sets(lang, state, jobs).FirstOrDefault(s => s.Id == r.SetId) : null;
                    bool latest = sv != null && sv.LastAttempt != null && Math.Abs((sv.LastAttempt.Value - r.Utc).TotalSeconds) < 2;
                    if (r.Kind == "restore") dy = Text2(det, string.IsNullOrEmpty(r.Detail) ? ClientView.Word(lang, r.Run) : L.Tr(lang, r.Detail), 9.5f, false, Ink, dx, dy, dw).Bottom;
                    else if (bad && latest)
                    {
                        foreach (var x2 in sv.Missed.Take(8)) { dy = Text2(det, (x2.Path.Length > 0 ? x2.Path : "") + (x2.Why.Length > 0 ? (x2.Path.Length > 0 ? "  —  " : "") + L.Tr(lang, x2.Why) : ""), 9f, false, Ink, dx, dy, dw, x2.Path.Length > 0).Bottom + S(2); }
                        if (sv.MissedCount > 8) dy = Text2(det, T("and {0} more — see the log", sv.MissedCount - 8), 8.5f, false, Muted, dx, dy, dw).Bottom;
                        if (sv.Kind == RunKind.Partial) dy = Text2(det, T("What to do:") + " " + ClientView.WhatToDo(lang, state), 9f, false, Ink, dx, dy + S(6), dw).Bottom;
                        if (sv.LastComplete != null) dy = Text2(det, T("The last complete backup of this set: {0}", ClientView.When(lang, sv.LastComplete)), 9f, true, Ink, dx, dy + S(6), dw).Bottom;
                    }
                    else if (bad) dy = Text2(det, T("The list of files is kept for the last run of each backup only."), 9f, false, Muted, dx, dy, dw).Bottom;
                    else dy = Text2(det, ClientView.Word(lang, r.Run, r.Result) + (r.Errors > 0 ? " · " + T("{0} files not backed up", r.Errors) : ""), 9.5f, false, Ink, dx, dy, dw).Bottom;
                    if (r.Kind == "backup")
                    {
                        var sid = r.SetId; var sname = r.SetName;
                        var lb = Btn(T("Run log"), false); Place(det, lb, dx, dy + S(10));
                        lb.Click += (o, e) => Do(() => api.Call("runlog", null, new Dictionary<string, string> { { "set", sid } }), null, res => ExportLog(sname, res), false);
                        lb.Enabled = latest;
                        var again = Btn(T("Back up again"), false); Place(det, again, dx + lb.Width + S(8), dy + S(10));
                        again.Enabled = sv != null && sv.Kind != RunKind.Running && sv.Kind != RunKind.Blocked;
                        again.Click += (o, e) => Do(() => api.Call("backup", new Msg().Set("set", sid), null), T("The backup has started"));
                        dy = again.Bottom;
                    }
                    det.Height = dy + S(14); ly = det.Bottom;
                }
                Divider(list, 0, ly, pageW); ly += 1;
            }
            list.Height = ly + S(2);
            return list.Bottom;
        }

        // ---------------------------------------------------------------- Settings: only what belongs to this computer's user

        int SettingsPage(int y)
        {
            y = PageTitle(y, T("Settings"), T("Only what belongs to you on this computer. The folders and the time of each backup are in \"Backups\"."));
            bool two = pageW >= S(760);
            int cw = two ? (pageW - S(16)) / 2 : pageW, m = S(20);
            var cards = new List<Func<int, int, int, int>>();
            // 1. this computer and the connection, the version (owner Q3: no manual update in the pilot)
            cards.Add((x, cy, w) =>
            {
                var c = Card(content, x, cy, w, S(100));
                var t = Text2(c, T("This computer and the connection"), 11f, true, Ink, m, m, w - 2 * m);
                int ry = t.Bottom + S(10);
                Func<string, string, bool, Color?, int> row = (k, v, ltr, col) => { var a = Text2(c, k, 9f, false, Muted, m, ry, S(130)); var b = Text2(c, v, 9.5f, true, col ?? Ink, m + S(136), ry, w - 2 * m - S(136), ltr); ry = Math.Max(a.Bottom, b.Bottom) + S(6); return ry; };
                row(T("Computer"), state["computer"] ?? "", true, null);
                row(T("User name"), state["login"] ?? "", true, null);
                var off = !string.IsNullOrEmpty(state["offline"]);
                row(T("Connection"), off ? T("No connection: {0}", L.Tr(lang, state["offline"])) : T("Connected"), false, off ? Bad : Ok);
                row(T("Version"), string.IsNullOrEmpty(upd["current"]) ? "—" : upd["current"], true, null);
                if (ClientView.ShowsManualUpdate(state))
                {
                    var lk = Link(c, T("Check for updates"), m + S(136), ry, () => { updAt = DateTime.MinValue; Do(() => api.Call("update", null, null), null, r => { upd = r; navKey = null; Render(); if (r["available"] == "1") UpdateNow(); else Message(T("The newest version is installed ({0}).", r["current"]), false); }, false); });
                    lk.Name = "checkUpdates"; ry = lk.Bottom + S(6);
                    if (upd["available"] == "1") { var ub = Place(c, Btn("⬆ " + T("Update to {0}", upd["latest"]), true), m + S(136), ry, "update"); ub.Click += (o, e) => UpdateNow(); ry = ub.Bottom + S(6); }
                }
                else ry = Text2(c, T("Updates are installed by {0}.", ClientView.Company(lang, state)), 8.5f, false, Muted, m + S(136), ry, w - 2 * m - S(136)).Bottom + S(6);
                c.Height = ry + m - S(6); return c.Bottom;
            });
            // 2. sign-in and two-step verification (was the Security page)
            cards.Add((x, cy, w) =>
            {
                var c = Card(content, x, cy, w, S(100));
                var st = new Stacker(c, m, m, w - 2 * m, Rtl);
                SecurityPage(st);
                st.Done(); c.Height = m + st.Height + m; return c.Bottom;
            });
            // 3. encryption
            cards.Add((x, cy, w) =>
            {
                var c = Card(content, x, cy, w, S(100));
                var t = Text2(c, T("Encryption"), 11f, true, Ink, m, m, w - 2 * m);
                var a = Text2(c, T("Files are encrypted on this computer before they leave, with your password. Without the password nothing can be restored — keep it safe."), 9.5f, false, Ink, m, t.Bottom + S(8), w - 2 * m);
                c.Height = a.Bottom + m; return c.Bottom;
            });
            // 4. help from the IT company
            cards.Add((x, cy, w) =>
            {
                var c = Card(content, x, cy, w, S(100));
                var t = Text2(c, T("Help from {0}", ClientView.Company(lang, state)), 11f, true, Ink, m, m, w - 2 * m);
                int ry = t.Bottom + S(6);
                var contact = string.Join("  ·  ", new[] { state["phone"], state["email"] }.Where(v => !string.IsNullOrEmpty(v)).ToArray());
                if (contact.Length > 0) ry = Text2(c, contact, 9.5f, false, Ink, m, ry, w - 2 * m, true).Bottom + S(8);
                var b = Place(c, Btn(T("Open a service call"), true), m, ry, "openCall"); b.Click += (o, e) => Go("help");
                c.Height = b.Bottom + m; return c.Bottom;
            });
            // 5. language
            cards.Add((x, cy, w) =>
            {
                var c = Card(content, x, cy, w, S(100));
                var t = Text2(c, T("Language"), 11f, true, Ink, m, m, w - 2 * m);
                var lb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(200), Font = F(10f), Name = "language", AccessibleName = T("Language") };
                foreach (var l in L.Languages) lb.Items.Add(new LangItem(l));
                lb.SelectedItem = lb.Items.Cast<LangItem>().FirstOrDefault(i => i.Code == lang);
                lb.SelectedIndexChanged += (o, e) => { saved = ((LangItem)lb.SelectedItem).Code; lang = saved; navKey = null; Render(); };
                lb.Location = new Point(MX(c, m, lb.Width), t.Bottom + S(8)); c.Controls.Add(lb);
                var h = Text2(c, T("Changes only this window on this computer."), 8.5f, false, Muted, m, lb.Bottom + S(6), w - 2 * m);
                c.Height = h.Bottom + m; return c.Bottom;
            });
            // 6. about
            cards.Add((x, cy, w) =>
            {
                var c = Card(content, x, cy, w, S(100));
                var t = Text2(c, T("About"), 11f, true, Ink, m, m, w - 2 * m);
                var a = Text2(c, (state["product"] ?? "") + (string.IsNullOrEmpty(upd["current"]) ? "" : "  ·  " + T("Version {0}", upd["current"])), 9.5f, false, Ink, m, t.Bottom + S(8), w - 2 * m);
                int ry = a.Bottom;
                if (!string.IsNullOrEmpty(state["powered"])) ry = Text2(c, T("Powered by {0}", state["powered"]), 9f, false, Muted, m, ry + S(4), w - 2 * m).Bottom;
                c.Height = ry + m; return c.Bottom;
            });
            int col = 0, rowTop = y, rowBottom = y;
            foreach (var make in cards)
            {
                var bottom = make(two && col == 1 ? cw + S(16) : 0, rowTop, cw);
                rowBottom = Math.Max(rowBottom, bottom); col++;
                if (!two || col == 2) { col = 0; rowTop = rowBottom + S(16); }
            }
            return Math.Max(rowBottom, rowTop - S(16));
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
            if (state["canAdd"] == "0") { st.Text(T("Your IT provider adds new backups — contact {0}.", Support()), 10f, false, Muted); st.Text(T(ClientView.Locked), 9f, false, Muted); return; }   // owner Q5
            st.Text(T("Files are encrypted on this computer before they leave, with your password. Without the password nothing can be restored — keep it safe."), 9.5f, false, Muted);
            var types = new[] { new[] { "FILE", "Files and folders" }, new[] { "MSSQL", "Microsoft SQL Server" }, new[] { "SYSTEMSTATE", "Windows System State" }, new[] { "BAREMETAL", "Whole computer (bare-metal image)" } };
            if (state["pilot"] == "1") types = types.Take(1).ToArray();   // PILOT-010: files and folders only
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
            // owner decision A7: keep a recovery copy of the key with the IT company? Both options with their texts in full
            Msg kr; try { kr = api.Call("keyrecovery", null, new Dictionary<string, string> { { "lang", lang } }); } catch (Exception) { kr = new Msg().Set("allowed", 0); }
            bool krAllowed = kr["allowed"] != "0";
            st.Text(kr["title"] ?? T("Recovery copy of the encryption key"), 10f, true).Margin = new Padding(0, 14, 0, 2);
            var keep = new RadioButton { Text = kr["keepLabel"] ?? "", Checked = krAllowed && kr["default"] != "0", Enabled = krAllowed, AutoSize = false, Width = st.Width, Height = S(26), Font = F(10f, true), Name = "keyKeep" };
            st.Add(keep); st.Text(kr["keepText"] ?? "", 9f, false, Muted);
            var none = new RadioButton { Text = kr["noneLabel"] ?? "", Checked = !keep.Checked, Enabled = krAllowed, AutoSize = false, Width = st.Width, Height = S(26), Font = F(10f, true), Name = "keyNone" };
            st.Add(none); st.Text(kr["noneText"] ?? "", 9f, false, Muted);
            if (!krAllowed) st.Text(kr["offText"] ?? "", 9.5f, true, Warn);
            var add = Btn(T("New backup"), true); st.Add(add);
            type.SelectedIndexChanged += (s, e) => { var k = types[type.SelectedIndex][0]; picker.Enabled(k == "FILE" || k == "BAREMETAL"); name.Text = T(types[type.SelectedIndex][1]); };
            add.Click += (s, e) =>
            {
                var k = types[type.SelectedIndex][0];
                var m = new Msg().Set("type", k).Set("name", name.Text).Set("sources", string.Join("\n", picker.Included.ToArray())).Set("exclude", string.Join("\n", picker.Excluded.ToArray()))
                    .Set("hour", time.Value.Hour).Set("minute", time.Value.Minute).Set("keyRecovery", krAllowed && keep.Checked ? "1" : "0");
                // what is really kept is the reply's keyRecovery (A7), said with the confirmation
                Do(() => api.Call("addset", m, null), null, r => { page = "sets"; Message(T("The backup was added") + "\n\n" + (r["keyRecovery"] == "1" ? T("A recovery copy of the key is kept by {0}.", ClientView.Company(lang, state)) : T("No recovery copy of the key is kept — keep the key in a safe place.")), false); });
            };
        }

        void EditSet(Msg s)
        {
            using (var d = new Form { Text = T("Change the backup"), FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(S(640), S(560)), Font = Font, RightToLeft = Dir, BackColor = Color.White })
            {
                var st = new Stacker(d, S(24), S(18), S(592), Rtl);
                st.Text(T("Change the backup") + " — " + s["name"], 13f, true);
                st.Text(T("Folders to back up"), 10f, true).Margin = new Padding(0, 10, 0, 2);
                var picker = new FolderPicker(this, api, T, Rtl);
                foreach (var p in (s["src"] ?? "").Split('\n').Where(x => x.Trim().Length > 0)) picker.Included.Add(p.Trim());
                foreach (var p in (s["skip"] ?? "").Split('\n').Where(x => x.Trim().Length > 0)) picker.Excluded.Add(p.Trim());
                var tree = picker.Control(S(592), S(280)); st.Add(tree);
                if (state["canSources"] == "0") { picker.Enabled(false); st.Text(T(ClientView.Locked), 9f, false, Muted); }   // owner Q5: disabled, with the reason
                st.Text(T("Daily backup time"), 10f, true).Margin = new Padding(0, 10, 0, 2);
                int hh, mm; int.TryParse(s["hh"], out hh); int.TryParse(s["mm"], out mm);
                var time = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Width = 110, Value = DateTime.Today.AddHours(hh).AddMinutes(mm), Enabled = state["canSchedule"] != "0" };
                st.Add(time);
                if (state["canSchedule"] == "0") st.Text(T(ClientView.Locked), 9f, false, Muted);
                st.Done();
                var save = Btn(T("Save and exit"), true); var exit = Btn(T("Exit without saving"), false);
                save.Top = exit.Top = d.ClientSize.Height - S(56); save.Left = Rtl ? S(24) : d.ClientSize.Width - S(24) - save.Width; exit.Left = Rtl ? save.Right + S(10) : save.Left - S(10) - exit.Width;
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
        public static int Run(string uiUrl, bool hidden) { return Run(uiUrl, hidden, null); }
        /// <param name="reread">reads ui.txt again (bug 89: the service has a new key after it restarts)</param>
        public static int Run(string uiUrl, bool hidden, Func<string> reread)
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            using (var f = new ClientForm(new LocalApi(uiUrl, reread)) { StartHidden = hidden }) Application.Run(f);
            return 0;
        }

        /// <summary>The robot: every page and state as a picture, with made-up data (a sample API). With matrix, also the
        /// critical screens at 1280x800 and at the minimum 860x560, at 100 / 125 / 150 % (owner Q10), and the place of every
        /// critical control (layout_&lt;lang&gt;.txt: shot, asked client size, client size Windows gave, scale, control,
        /// visible, x,y,w,h in the window's client area, fixed = must be inside the window / scroll = reachable by scrolling).</summary>
        public static void Screens(string outDir, string lang) { Screens(outDir, lang, false); }
        public static void Screens(string outDir, string lang, bool matrix)
        {
            Exception failed = null;
            var t = new Thread(() =>
            {
                try
                {
                    Directory.CreateDirectory(outDir);
                    Application.EnableVisualStyles();
                    var sample = new SampleApi(lang);
                    var layout = new StringBuilder();
                    using (var f = new ClientForm(sample) { StartPosition = FormStartPosition.Manual, Location = new Point(0, 0), preview = true })
                    {
                        f.Show();
                        Action<string> data = mode =>
                        {
                            sample.Mode = mode;
                            f.upd = sample.Call("update", null, null); f.state = sample.Call("state", null, null); f.jobs = sample.Call("jobs", null, null).List("jobs");
                            f.ApplyBrand(); f.lang = L.Norm(lang); f.navKey = null;
                        };
                        Func<string, Size, double, string[], int> shot = (name, asked, scale, critical) =>
                        {
                            f.Render(); f.Activate(); f.Refresh();
                            for (int i = 0; i < 30; i++) { Application.DoEvents(); Thread.Sleep(40); }
                            f.Refresh(); Application.DoEvents();
                            using (var bmp = new Bitmap(f.Width, f.Height))
                            {
                                using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(f.Location, Point.Empty, f.Size);
                                bmp.Save(Path.Combine(outDir, "client_" + name + "_" + f.lang + ".png"), ImageFormat.Png);
                            }
                            if (critical == null) return 0;
                            var act = f.ClientSize;
                            Func<Size, string> wh = s => s.Width + "x" + s.Height;
                            layout.Append(name).Append('\t').Append(wh(asked)).Append('\t').Append(wh(act)).Append('\t').Append(scale.ToString(CultureInfo.InvariantCulture)).Append("\t-\t1\t0,0,0,0\tframe\n");
                            foreach (var spec in critical)
                            {
                                var kind = spec.EndsWith("~") ? "scroll" : "fixed"; var cname = spec.TrimEnd('~');
                                var found = All(f).Where(c => c.Name == cname || (cname.EndsWith("*") && c.Name.StartsWith(cname.TrimEnd('*'), StringComparison.Ordinal))).ToList();
                                if (found.Count == 0) { layout.Append(name).Append('\t').Append(wh(asked)).Append('\t').Append(wh(act)).Append('\t').Append(scale.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(cname).Append("\t0\t0,0,0,0\t").Append(kind).Append('\n'); continue; }
                                var c = found[0];
                                var r = f.RectangleToClient(c.RectangleToScreen(new Rectangle(Point.Empty, c.Size)));
                                layout.Append(name).Append('\t').Append(wh(asked)).Append('\t').Append(wh(act)).Append('\t').Append(scale.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(cname).Append('\t').Append(c.Visible ? "1" : "0").Append('\t')
                                    .Append(r.X + "," + r.Y + "," + r.Width + "," + r.Height).Append('\t').Append(kind).Append('\n');
                            }
                            return 1;
                        };
                        var navNames = ClientView.Nav.Select(n => "nav_" + n[0]).Concat(new[] { "nav_help" }).ToArray();
                        Action<double, Size, string> size = (scale, window, tag) =>
                        {
                            f.ui = f.sys * scale; f.Font = f.F(10f);
                            f.MinimumSize = new Size(f.S(ClientView.MinWidth), f.S(ClientView.MinHeight));
                            f.Size = window; f.navKey = null;
                        };
                        // ---- every page and state, at 1280x800 (100 %)
                        size(1.0, new Size(1280, 800), "");
                        var asked0 = f.ClientSize;
                        foreach (var mode in new[] { "complete", "partial", "failed", "stopped", "running", "never", "offline", "nosets" })
                        {
                            data(mode); f.page = "home";
                            shot("home_" + mode, asked0, 1.0, null);
                        }
                        data("partial");
                        foreach (var p in new[] { "sets", "settings", "help", "new" }) { f.page = p; shot(p, asked0, 1.0, null); }
                        f.selSet = "2"; f.page = "set"; shot("set", asked0, 1.0, null);
                        var open = ClientView.History(f.lang, f.state, f.jobs, "all", null).FirstOrDefault(r => r.Run == RunKind.Partial);
                        f.hOpen = open == null ? null : open.Kind + open.SetId + open.Utc.Ticks; f.page = "history"; shot("history", asked0, 1.0, null); f.hOpen = null;
                        Action restoreSteps = () =>
                        {
                            f.RestoreReset(); f.rSetId = "2"; f.rPoints = null; f.rLoadedFor = null; f.rFiles = null; f.rFilesFor = null; f.rFilter = "";
                            f.rChecked.Add(@"D:\Users\dana\Documents\Contracts\Lease 2026.pdf"); f.rChecked.Add(@"D:\Users\dana\Documents\Contracts\Offer - Sea Towers.pdf");
                            f.page = "restore"; f.rStep = 1;
                        };
                        restoreSteps(); shot("restore1", asked0, 1.0, null);
                        f.rPaths = f.rChecked.ToList(); f.rStep = 2; f.rTarget = @"D:\Restore\2026-10-08 0915"; shot("restore2", asked0, 1.0, null);
                        f.rLocation = "original"; f.rCheck = sample.Call("restorecheck", new Msg().Set("location", "original"), null); shot("restore2_existing", asked0, 1.0, null); f.rCheck = null; f.rLocation = "alternate";
                        f.rJob = "r1"; f.rStep = 3; shot("restore3", asked0, 1.0, null);
                        if (matrix)
                        {
                            // ---- owner Q10: the critical screens at 1280x800 and 860x560, at 100 / 125 / 150 %
                            foreach (var scale in new[] { 1.0, 1.25, 1.5 })
                                foreach (var logical in new[] { new Size(1280, 800), new Size(860, 560) })
                                {
                                    var win = logical.Width == 860 ? new Size((int)Math.Round(860 * scale), (int)Math.Round(560 * scale)) : new Size(Math.Max(1280, (int)Math.Round(860 * scale)), Math.Max(800, (int)Math.Round(560 * scale)));
                                    size(scale, win, "");
                                    var border = f.Size - f.ClientSize; var asked = new Size(win.Width - border.Width, win.Height - border.Height);
                                    var tag = "m" + (int)(scale * 100) + "_" + logical.Width + "x" + logical.Height + "_";
                                    foreach (var mode in new[] { "partial", "failed", "running" })
                                    {
                                        data(mode); f.page = "home";
                                        var crit = new List<string> { "state", "heroPrimary", "heroSecondary", "fact0~", "fact1~" }.Concat(navNames).ToList();
                                        if (mode != "running") crit.Add("problemTitle");
                                        shot(tag + "home_" + mode, asked, scale, crit.ToArray());
                                    }
                                    data("partial"); f.page = "sets";
                                    shot(tag + "sets", asked, scale, new[] { "title", "setBackup_*~", "newBackup~" }.Concat(navNames).ToArray());
                                    restoreSteps(); shot(tag + "restore1", asked, scale, new[] { "title", "barPrimary", "barSecondary", "restorePoint~", "restoreTree~" }.Concat(navNames).ToArray());
                                    f.rPaths = f.rChecked.ToList(); f.rStep = 2; f.rTarget = @"D:\Restore\2026-10-08 0915";
                                    shot(tag + "restore2", asked, scale, new[] { "title", "barPrimary", "barSecondary", "restoreTarget~", "restoreKeeps~", "originalLocation~" }.Concat(navNames).ToArray());
                                    f.rLocation = "original"; f.rCheck = sample.Call("restorecheck", new Msg().Set("location", "original"), null);
                                    shot(tag + "restore2_existing", asked, scale, new[] { "title", "existingTitle", "existingList", "barPrimary", "barSecondary", "barTertiary" }.Concat(navNames).ToArray());
                                    f.rCheck = null; f.rLocation = "alternate";
                                    f.rJob = "r1"; f.rStep = 3;
                                    shot(tag + "restore3", asked, scale, new[] { "title", "state", "restoreVerified~", "barPrimary", "barSecondary", "barTertiary" }.Concat(navNames).ToArray());
                                }
                            File.WriteAllText(Path.Combine(outDir, "layout_" + L.Norm(lang) + ".txt"), layout.ToString(), new UTF8Encoding(false));
                        }
                        // SETUP-C50: the first screen after the installation — sign in, or a new account
                        size(1.0, new Size(1040, 720), "");
                        f.state.Set("registered", 0).Set("defaultServer", "https://backup.acme-it.com:8443");
                        foreach (var mode in new[] { "signin", "signup" }) { f.cfNew = mode == "signup"; shot(mode, f.ClientSize, 1.0, null); }
                        f.closing = true; f.tray.Visible = false; f.Close();
                    }
                }
                catch (Exception e) { failed = e; }
            });
            t.SetApartmentState(ApartmentState.STA); t.Start(); t.Join();
            if (failed != null) throw new InvalidOperationException("client-screens: " + failed, failed);
        }

        static IEnumerable<Control> All(Control c) { foreach (Control x in c.Controls) { yield return x; foreach (var y in All(x)) yield return y; } }

        /// <summary>Made-up data for the pictures (the states of the window's design, ux-client/project/C-*).</summary>
        sealed class SampleApi : IClientApi
        {
            readonly string lang; public string Mode = "partial";
            public SampleApi(string l) { lang = l; }
            static string Ago(double days, int hour, int minute) { return RunId.From(DateTime.Today.AddDays(-days).AddHours(hour).AddMinutes(minute).ToUniversalTime()); }
            static Msg Runs(Msg s, int n, int hour, int minute, Func<int, string> result)
            {
                for (int i = n - 1; i >= 0; i--) s.Add("runs", new Msg().Set("t", Ago(i + 1, hour, minute)).Set("r", result(i)).Set("e", result(i) == "BS_STOP_SUCCESS_WITH_ERROR" ? 2 : 0));
                return s;
            }
            public Msg Call(string op, Msg body, IDictionary<string, string> query)
            {
                var now = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
                switch (op)
                {
                    case "state":
                        {
                            var m = new Msg().Set("registered", 1).Set("computer", "OFFICE-FS01").Set("login", "dana-office").Set("product", "Acme Backup").Set("company", "Acme IT").Set("phone", "03-5550000").Set("email", "support@acme-it.example").Set("language", lang).Set("pilot", 1)
                                .Set("canAdd", 1).Set("canSources", 1).Set("canSchedule", 1);
                            if (Mode == "offline") m.Set("offline", "The backup server cannot be reached.");
                            if (Mode == "nosets") return m;
                            var never = Mode == "never";
                            var office = new Msg().Set("id", "1").Set("name", "Office files").Set("type", "FILE").Set("sources", @"D:\Office; D:\Scans").Set("src", "D:\\Office\nD:\\Scans").Set("mine", 1).Set("hour", "21:00").Set("hh", 21).Set("mm", 0)
                                .Set("last", never ? "" : Ago(1, 21, 38)).Set("result", never ? "" : "BS_STOP_SUCCESS").Set("lastComplete", never ? "" : Ago(1, 21, 38)).Set("lastTest", never ? "" : Ago(17, 3, 10))
                                .Set("retUnit", "DAYS").Set("retPeriod", 30).Set("retMonthly", 12).Set("keyRecovery", 1);
                            if (!never) Runs(office, 14, 21, 38, i => "BS_STOP_SUCCESS");
                            var r2 = Mode == "failed" ? "BS_STOP_BY_SYSTEM_ERROR" : Mode == "stopped" ? "BS_STOP_BY_USER" : Mode == "partial" || Mode == "offline" ? "BS_STOP_SUCCESS_WITH_ERROR" : "BS_STOP_SUCCESS";
                            var users = new Msg().Set("id", "2").Set("name", "User folders").Set("type", "FILE").Set("sources", @"D:\Users").Set("src", @"D:\Users").Set("skip", @"D:\Users\Public\AppData").Set("mine", 1).Set("hour", "23:30").Set("hh", 23).Set("mm", 30)
                                .Set("last", never ? "" : Ago(1, 23, 30)).Set("result", never ? "" : r2).Set("lastComplete", never ? "" : r2 == "BS_STOP_SUCCESS" ? Ago(1, 23, 30) : Ago(2, 23, 52)).Set("missedCount", r2 == "BS_STOP_SUCCESS_WITH_ERROR" ? 2 : 0)
                                .Set("retUnit", "DAYS").Set("retPeriod", 30).Set("keyRecovery", 1);
                            if (r2 == "BS_STOP_SUCCESS_WITH_ERROR")
                            {
                                users.Add("missed", new Msg().Set("p", @"D:\Users\dana\Documents\Outlook Files\dana@example.com.pst").Set("why", "The process cannot access the file because it is being used by another process."));
                                users.Add("missed", new Msg().Set("p", @"D:\Users\avi\Documents\Outlook Files\archive-2025.pst").Set("why", "The process cannot access the file because it is being used by another process."));
                            }
                            if (r2 == "BS_STOP_BY_SYSTEM_ERROR") users.Add("missed", new Msg().Set("p", "").Set("why", "There is no free space in the account (the quota is full)."));
                            if (!never) Runs(users, 14, 23, 30, i => i == 0 ? r2 : i == 5 ? "BS_STOP_BY_SYSTEM_ERROR" : "BS_STOP_SUCCESS");
                            var acc = new Msg().Set("id", "3").Set("name", "Accounting").Set("type", "FILE").Set("sources", @"D:\Accounting; D:\Payroll").Set("src", "D:\\Accounting\nD:\\Payroll").Set("mine", 1).Set("hour", "20:00").Set("hh", 20).Set("mm", 0)
                                .Set("last", never ? "" : Ago(1, 20, 9)).Set("result", never ? "" : "BS_STOP_SUCCESS").Set("lastComplete", never ? "" : Ago(1, 20, 9));
                            if (!never) Runs(acc, 14, 20, 9, i => "BS_STOP_SUCCESS");
                            return m.Add("sets", office).Add("sets", users).Add("sets", acc);
                        }
                    case "jobs":
                        {
                            var j = new Msg();
                            if (Mode == "running") j.Add("jobs", new Msg().Set("id", "b1").Set("kind", "backup").Set("set", "1").Set("state", "running").Set("started", now - 600000));
                            j.Add("jobs", new Msg().Set("id", "r1").Set("kind", "restore").Set("set", "2").Set("state", "ok").Set("detail", "Restored 2, skipped 0, failed 0; verified 2").Set("started", now - 3 * 3600000)
                                .Set("restored", 2).Set("skipped", 0).Set("failed", 0).Set("verified", 2).Set("verifiedSha256", 2).Set("mismatched", 0).Set("location", "alternate").Set("restoreResult", "OK"));
                            return j;
                        }
                    case "points":
                        {
                            var p = new Msg();
                            for (int d = 0; d < 12; d++) { if (d == 4) continue; p.Add("points", new Msg().Set("id", Ago(d + 1, 23, 30)).Set("time", Ago(d + 1, 23, 30))); if (d == 0) p.Add("points", new Msg().Set("id", Ago(1, 12, 0)).Set("time", Ago(1, 12, 0))); }
                            return p;
                        }
                    case "files":
                        {
                            var f = new Msg();
                            foreach (var x in new[] { @"D:\Users\dana\Documents\Contracts\Lease 2026.pdf", @"D:\Users\dana\Documents\Contracts\Offer - Sea Towers.pdf", @"D:\Users\dana\Documents\Contracts\Appendix B - timeline.xlsx", @"D:\Users\dana\Documents\Budget 2026.xlsx", @"D:\Users\avi\Documents\Plans\plan.docx", @"D:\Users\avi\Documents\Plans\draft.docx" })
                                f.Add("files", new Msg().Set("path", x).Set("size", 640000).Set("mtime", now - 86400000));
                            return f;
                        }
                    case "runlog":
                        {
                            var t0 = DateTime.UtcNow.AddHours(-10);
                            var lines = new List<string> { AhsayLog.Line(t0, "start"), AhsayLog.Line(t0.AddSeconds(2), "info", message: "Shadow copy created"), AhsayLog.Line(t0.AddMinutes(11), "err", @"D:\Users\dana\Documents\Outlook Files\dana@example.com.pst", message: "The process cannot access the file because it is being used by another process."), AhsayLog.Line(t0.AddMinutes(12), "err", @"D:\Users\avi\Documents\Outlook Files\archive-2025.pst", message: "The process cannot access the file because it is being used by another process."), AhsayLog.Line(t0.AddMinutes(22), "end", message: "BS_STOP_SUCCESS_WITH_ERROR") };
                            var m = new Msg().Set("at", RunId.From(t0)).Set("text", RunNotes.Readable(lines));
                            foreach (var l in lines) m.Add("lines", new Msg().Set("l", l));
                            return m;
                        }
                    case "keyrecovery": return new Msg().Set("default", 1).Set("allowed", 1).Set("title", L.Tr(lang, KeyTexts.Title)).Set("keepLabel", L.Tr(lang, KeyTexts.KeepLabel)).Set("keepText", L.Tr(lang, KeyTexts.KeepText)).Set("noneLabel", L.Tr(lang, KeyTexts.NoneLabel)).Set("noneText", L.Tr(lang, KeyTexts.NoneText)).Set("offText", L.Tr(lang, KeyTexts.OffText));
                    case "restorecheck": return new Msg().Set("total", 6).Set("existing", 2).Set("location", body != null && body["location"] == "original" ? "original" : "alternate").Add("files", new Msg().Set("p", @"D:\Users\dana\Documents\Contracts\Lease 2026.pdf")).Add("files", new Msg().Set("p", @"D:\Users\dana\Documents\Contracts\Offer - Sea Towers.pdf"));
                    case "security": return new Msg().Set("totp", 1);
                    case "update": return new Msg().Set("current", "0.1.80").Set("latest", "0.1.80").Set("available", 0);
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
