#if NET40
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using System.Xml.Linq;
using OnlineBackup.Core;

namespace OnlineBackup.Agent
{
    /// <summary>
    /// SETUP-C40 (owner: "a real program, not a web page"): the Windows installation of the client software as a native
    /// window — the product's name, logo and colours on top, the steps, Back / Next / Cancel, right-to-left for Hebrew and
    /// Arabic. The work underneath is the same as the web wizard and the silent installation (SetupUi.CheckServer,
    /// Setup.Run), so what the tests prove for those holds here.
    /// </summary>
    public sealed class SetupForm : Form
    {
        readonly string packageDir;
        readonly XElement conn, brand;
        string lang;
        readonly string product;
        readonly Color brandColor, accent;
        readonly Image logo;
        int step;
        bool busy, accept, finished, launch = true, removeSettings;
        string error = "";
        readonly List<string> lines = new List<string>();
        Setup.Result result; string installError;
        Setup.Result existing;   // SETUP-C70: already installed here → repair or remove
        string action = "install";        // install | repair | remove
        string contractVersion;           // the IT company's contract shown as the license agreement
        /// <summary>Tests and screenshots: answers and switches the program does not ask for.</summary>
        public readonly Dictionary<string, string> Fixed = new Dictionary<string, string>();
        public readonly HashSet<string> Flags = new HashSet<string>();

        readonly Panel header = new Panel(), steps = new Panel(), body = new Panel(), footer = new Panel();
        readonly Button back = new Button(), next = new Button(), cancel = new Button();
        readonly ComboBox langBox = new ComboBox();

        string T(string s, params object[] a) { return L.T(lang, s, a); }
        bool Rtl { get { return lang == "he" || lang == "ar"; } }
        string[] StepNames
        {
            get
            {
                if (action == "remove") return new[] { "Welcome", "Removal", "Finish" };
                if (action == "repair") return new[] { "Welcome", "Installation", "Finish" };
                return new[] { "Welcome", "License agreement", "Installation", "Finish" };
            }
        }
        string Current { get { var n = StepNames; return n[Math.Min(step, n.Length - 1)]; } }

        static Color ParseColor(string hex, Color fallback)
        {
            try { if (!string.IsNullOrEmpty(hex) && hex.Length == 7 && hex[0] == '#') return ColorTranslator.FromHtml(hex); } catch (Exception) { }
            return fallback;
        }

        public SetupForm(string packageDir)
        {
            this.packageDir = packageDir;
            conn = OnlineBackup.Core.Atomic.LoadXElement(Path.Combine(packageDir, "connection.xml"));
            var bp = Path.Combine(packageDir, "branding.xml");
            brand = File.Exists(bp) ? OnlineBackup.Core.Atomic.LoadXElement(bp) : new XElement("BRANDING");
            product = (string)brand.Attribute("PRODUCT"); if (string.IsNullOrEmpty(product)) product = "Backup";
            brandColor = ParseColor((string)brand.Attribute("COLOR"), Color.FromArgb(79, 70, 229));
            accent = ParseColor((string)brand.Attribute("ACCENT"), Color.FromArgb(249, 115, 22));
            var lg = (string)brand.Attribute("LOGO") ?? "";
            int comma = lg.IndexOf(',');
            if (lg.StartsWith("data:image/") && comma > 0) try { logo = Image.FromStream(new MemoryStream(Convert.FromBase64String(lg.Substring(comma + 1)))); } catch (Exception) { }
            // I18N-040 (owner): English until the person picks a language by hand (remembered for the program too)
            lang = "en";
            try { var sv = L.Norm((string)Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\OnlineBackup", "Language", null)); if (!string.IsNullOrEmpty(sv)) lang = sv; } catch (Exception) { }
            var folder = (string)conn.Attribute("FOLDER"); if (string.IsNullOrEmpty(folder)) folder = "OnlineBackup";
            existing = Setup.Installed(folder);
            if (existing != null) action = "repair";

            Text = product; Font = new Font("Segoe UI", 10f); ClientSize = new Size(780, 680); FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; StartPosition = FormStartPosition.CenterScreen; BackColor = Color.FromArgb(244, 246, 250); AutoScaleMode = AutoScaleMode.Dpi;
            try { Icon = Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetExecutingAssembly().Location); } catch (Exception) { }

            header.Dock = DockStyle.Top; header.Height = 78; header.Paint += PaintHeader;
            steps.Dock = DockStyle.Top; steps.Height = 46; steps.Paint += PaintSteps;
            footer.Dock = DockStyle.Bottom; footer.Height = 64; footer.BackColor = Color.White; footer.Paint += (s, e) => e.Graphics.DrawLine(new Pen(Color.FromArgb(226, 232, 240)), 0, 0, footer.Width, 0);
            body.Dock = DockStyle.Fill; body.AutoScroll = true;
            Controls.Add(body); Controls.Add(steps); Controls.Add(header); Controls.Add(footer);

            langBox.DropDownStyle = ComboBoxStyle.DropDownList; langBox.Width = 130; langBox.Font = new Font("Segoe UI", 9f);
            foreach (var l in L.Languages) langBox.Items.Add(new LangItem(l));
            langBox.SelectedItem = langBox.Items.Cast<LangItem>().FirstOrDefault(i => i.Code == lang);
            langBox.SelectedIndexChanged += (s, e) => { lang = ((LangItem)langBox.SelectedItem).Code; try { Microsoft.Win32.Registry.SetValue(@"HKEY_CURRENT_USER\Software\OnlineBackup", "Language", lang); } catch (Exception) { } Render(); };
            header.Controls.Add(langBox);

            foreach (var b in new[] { back, next, cancel }) { b.Height = 38; b.Width = 120; b.FlatStyle = FlatStyle.Flat; b.Font = new Font("Segoe UI Semibold", 10f); b.Cursor = Cursors.Hand; footer.Controls.Add(b); }
            next.BackColor = accent; next.ForeColor = Color.White; next.FlatAppearance.BorderSize = 0;
            back.BackColor = Color.White; back.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225);
            cancel.BackColor = Color.White; cancel.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225);
            back.Click += (s, e) => { error = ""; if (step > 0) step--; Render(); };
            next.Click += (s, e) => Next();
            cancel.Click += (s, e) => { if (!busy) Close(); };
            FormClosing += (s, e) => { if (busy) e.Cancel = true; };   // never half installed
            AcceptButton = next;
            Resize += (s, e) => Layout2();
            Render();
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
            int y = (footer.Height - 38) / 2;
            if (Rtl) { next.Left = 24; back.Left = next.Right + 10; cancel.Left = footer.Width - 24 - cancel.Width; }
            else { next.Left = footer.Width - 24 - next.Width; back.Left = next.Left - 10 - back.Width; cancel.Left = 24; }
            foreach (var b in new[] { back, next, cancel }) b.Top = y;
            langBox.Top = (header.Height - langBox.Height) / 2; langBox.Left = Rtl ? 20 : header.Width - 20 - langBox.Width;
        }

        void PaintHeader(object s, PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (var bg = new LinearGradientBrush(header.ClientRectangle, Color.FromArgb(11, 16, 32), Color.FromArgb(32, 30, 80), 0f)) g.FillRectangle(bg, header.ClientRectangle);
            using (var line = new SolidBrush(brandColor)) g.FillRectangle(line, 0, header.Height - 3, header.Width, 3);
            int x = Rtl ? header.Width - 24 : 24; int lw = 0;
            if (logo != null)
            {
                lw = 46; var r = new Rectangle(Rtl ? x - lw : x, (header.Height - lw) / 2, lw, lw);
                using (var w = new SolidBrush(Color.White)) g.FillRectangle(w, r);
                g.DrawImage(logo, new Rectangle(r.X + 3, r.Y + 3, r.Width - 6, r.Height - 6));
                lw += 12;
            }
            var fmt = new StringFormat { Alignment = StringAlignment.Near, FormatFlags = Rtl ? StringFormatFlags.DirectionRightToLeft : 0 };   // right to left: "near" is the right edge
            var tx = Rtl ? new RectangleF(160, 14, x - lw - 160, 30) : new RectangleF(x + lw, 14, header.Width - x - lw - 170, 30);
            using (var f = new Font("Segoe UI Semibold", 15f)) g.DrawString(T("{0} — setup", product), f, Brushes.White, tx, fmt);
            var sub = (string)brand.Attribute("SLOGAN"); if (string.IsNullOrEmpty(sub)) sub = (string)brand.Attribute("COMPANY") ?? "";
            using (var f = new Font("Segoe UI", 9.5f)) using (var b = new SolidBrush(Color.FromArgb(190, 200, 220))) g.DrawString(sub, f, b, new RectangleF(tx.X, 46, tx.Width, 20), fmt);
        }

        void PaintSteps(object s, PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            var names = StepNames; float x = Rtl ? steps.Width - 36 : 36;
            using (var f = new Font("Segoe UI Semibold", 9f))
                for (int i = 0; i < names.Length; i++)
                {
                    var text = T(names[i]); var sz = g.MeasureString(text, f); float w = sz.Width + 24, h = 26;
                    var r = new RectangleF(Rtl ? x - w : x, 12, w, h);
                    var fill = i == step ? Color.FromArgb(17, 24, 39) : i < step ? Color.FromArgb(209, 250, 229) : Color.FromArgb(226, 232, 240);
                    var fg = i == step ? Color.White : i < step ? Color.FromArgb(4, 120, 87) : Color.FromArgb(100, 116, 139);
                    using (var path = Round(r, 13)) using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                    using (var b = new SolidBrush(fg)) g.DrawString(text, f, b, r, new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
                    x = Rtl ? x - w - 8 : x + w + 8;
                }
        }

        static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath(); float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90); p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90); p.CloseFigure();
            return p;
        }

        // ---------------------------------------------------------------- the pages

        readonly Panel flow = new Panel();
        int W { get { return body.ClientSize.Width - 72 - SystemInformation.VerticalScrollBarWidth; } }

        Label Text1(string s, float size = 10f, bool bold = false, Color? color = null)
        {
            var font = new Font(bold ? "Segoe UI Semibold" : "Segoe UI", size);
            var h = TextRenderer.MeasureText(s, font, new Size(W, 0), TextFormatFlags.WordBreak).Height + 2;
            var l = new Label { Text = s, AutoSize = false, Width = W, Height = h, Font = font, Margin = new Padding(0, 6, 0, 2),
                TextAlign = ContentAlignment.TopLeft, RightToLeft = Rtl ? RightToLeft.Yes : RightToLeft.No };   // Windows mirrors "left" to the right in Hebrew / Arabic
            if (color.HasValue) l.ForeColor = color.Value;
            flow.Controls.Add(l); return l;
        }
        CheckBox Check(string label, bool value, Action<bool> set)
        {
            var c = new CheckBox { Text = label, Checked = value, AutoSize = false, Width = W, Height = 28, Margin = new Padding(0, 12, 0, 2), Font = new Font("Segoe UI", 10f),
                RightToLeft = Rtl ? RightToLeft.Yes : RightToLeft.No, TextAlign = ContentAlignment.MiddleLeft, CheckAlign = ContentAlignment.MiddleLeft };
            c.CheckedChanged += (s, e) => { set(c.Checked); UpdateButtons(); };
            flow.Controls.Add(c); return c;
        }
        RadioButton Radio(string label, string sub, bool value, Action set)
        {
            var r = new RadioButton { Text = label + (sub.Length > 0 ? "   —   " + sub : ""), Checked = value, AutoSize = false, Width = W, Height = 28, Margin = new Padding(0, 8, 0, 2), Font = new Font("Segoe UI", 10f),
                RightToLeft = Rtl ? RightToLeft.Yes : RightToLeft.No, TextAlign = ContentAlignment.MiddleLeft, CheckAlign = ContentAlignment.MiddleLeft };
            r.CheckedChanged += (s, e) => { if (r.Checked) { set(); error = ""; Render(); } };
            r.Enabled = !busy;
            flow.Controls.Add(r); return r;
        }

        /// <summary>Rows one under the other, each the full width of the page.</summary>
        void Stack()
        {
            int y = 0;
            foreach (Control c in flow.Controls) { y += c.Margin.Top; c.Left = 0; c.Top = y; c.Width = W; y += c.Height + c.Margin.Bottom; }
            flow.Height = y + 8; flow.Width = W;
        }

        void Render()
        {
            SuspendLayout();
            RightToLeft = Rtl ? RightToLeft.Yes : RightToLeft.No; RightToLeftLayout = false;   // positions are set by hand for both directions
            body.RightToLeft = RightToLeft.No; flow.RightToLeft = RightToLeft.No;   // the scroll area keeps plain coordinates; each row carries its own direction
            body.Controls.Clear(); var old = flow.Controls.Cast<Control>().ToList(); flow.Controls.Clear(); foreach (var c in old) c.Dispose();
            flow.Location = new Point(36, 18); flow.BackColor = Color.Transparent;
            body.Controls.Add(flow);
            back.Text = T("Back"); cancel.Text = T("Cancel"); next.Text = T("Next");
            var muted = Color.FromArgb(71, 85, 105);
            switch (Current)
            {
                case "Welcome":
                    if (existing == null)
                    {
                        Text1(T("Welcome to the {0} setup", product), 15f, true);
                        Text1(T("This installs {0} on this computer. It backs up your files to your provider's backup server, encrypted on this computer before they leave.", product), 10.5f, false, muted);
                        Text1(T("Computer: {0}", Environment.MachineName));
                        Text1(T("Click Next to continue, or Cancel to exit."), 10f, false, muted).Margin = new Padding(0, 18, 0, 2);
                    }
                    else
                    {
                        Text1(T("{0} is already installed on this computer", product), 15f, true);
                        if (!string.IsNullOrEmpty(existing.Server)) Text1(T("Installed version: {0}", existing.Server), 10f, false, muted);
                        Text1(T("What would you like to do?"), 10.5f, true).Margin = new Padding(0, 14, 0, 2);
                        // SETUP-C90 (owner: "the first step is to update with one click"): a newer version → "Update", chosen already
                        if (IsNewer) Radio(T("Update to version {0}", PackageVersion), T("One click — the backups and settings are kept"), action == "repair", () => action = "repair");
                        else Radio(T("Repair"), T("Install it again over the present one — the backups and settings are kept"), action == "repair", () => action = "repair");
                        Radio(T("Remove"), T("Remove {0} from this computer", product), action == "remove", () => action = "remove");
                        if (action == "remove")
                        {
                            var c = Check(T("Also remove this computer's settings (the backups on the server are kept)"), removeSettings, v => removeSettings = v); c.Margin = new Padding(24, 4, 0, 2);
                            Text1(T("Your backups stay on the server. To restore them later, install again and sign in with your user name and password."), 9.5f, false, muted);
                        }
                    }
                    break;
                case "License agreement":
                    Text1(T("License agreement"), 15f, true);
                    Text1(T("Please read the license agreement. To continue, accept it."), 10f, false, muted);
                    var box = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Text = LicenseText(), Width = W, Height = 300, BackColor = Color.White, Font = new Font("Segoe UI", 9.5f), Margin = new Padding(0, 8, 0, 2), RightToLeft = Rtl ? RightToLeft.Yes : RightToLeft.No };
                    flow.Controls.Add(box);
                    Check(T("I accept the terms of the license agreement"), accept, v => accept = v);
                    next.Text = T("Install");
                    break;
                case "Installation":
                case "Removal":
                    Text1(Current == "Removal" ? T("Removing {0}…", product) : T("Installing {0}…", product), 15f, true);
                    Text1(T("Please wait. This takes about a minute."), 10f, false, muted);
                    var bar = new ProgressBar { Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30, Width = W, Height = 18, Margin = new Padding(0, 14, 0, 8) };
                    flow.Controls.Add(bar);
                    foreach (var l in Snapshot()) Text1("•  " + L.Tr(lang, l), 9.5f, false, Color.FromArgb(51, 65, 85));
                    break;
                case "Finish":
                    if (installError != null)
                    {
                        Text1(action == "remove" ? T("The removal did not finish") : T("The installation did not finish"), 15f, true, Color.FromArgb(185, 28, 28));
                        Text1(L.Tr(lang, installError), 10.5f, false, Color.FromArgb(185, 28, 28));
                        Text1(T("Run the setup again. If it happens again, contact {0}.", Support()), 10f, false, muted);
                    }
                    else if (action == "remove")
                    {
                        Text1(T("{0} was removed from this computer", product), 15f, true, Color.FromArgb(4, 120, 87));
                        Text1(T("Your backups stay on the server."), 10.5f, false, muted);
                    }
                    else
                    {
                        Text1(T("{0} is installed", product), 15f, true, Color.FromArgb(4, 120, 87));
                        Text1(T("It runs in the background and starts with Windows. When it opens, sign in with your user name and password, or open a new account."), 10.5f, false, muted);
                        Check(T("Open {0} now", product), launch, v => launch = v);
                    }
                    next.Text = T("Finish");
                    break;
            }
            if (error.Length > 0) Text1(L.Tr(lang, error), 10f, true, Color.FromArgb(185, 28, 28));
            Stack();
            UpdateButtons();
            Layout2(); header.Invalidate(); steps.Invalidate();
            ResumeLayout(true);
        }

        string PackageVersion { get { try { return OnlineBackup.Core.Atomic.ReadAllText(Path.Combine(packageDir, "version.txt")).Trim(); } catch (Exception) { return ""; } } }
        bool IsNewer { get { return existing != null && PackageVersion.Length > 0 && PackageVersion != (existing.Server ?? ""); } }

        void UpdateButtons()
        {
            if (Current == "Welcome" && existing != null) next.Text = action == "remove" ? T("Remove") : IsNewer ? T("Update") : T("Repair");
            var working = Current == "Installation" || Current == "Removal";
            back.Visible = step > 0 && !working && Current != "Finish";
            next.Visible = !working;
            next.Enabled = !busy && (Current != "License agreement" || accept);
            cancel.Visible = Current != "Finish"; cancel.Enabled = !working;
        }

        string Support() { var c = (string)brand.Attribute("COMPANY"); var ph = (string)brand.Attribute("PHONE"); return ((string.IsNullOrEmpty(c) ? T("support") : c) + " " + (ph ?? "")).Trim(); }

        List<string> Snapshot() { lock (lines) return lines.Where(x => !x.Contains("assword")).ToList(); }

        /// <summary>The license agreement: license.txt of the package (the IT company's own) or the standard one.</summary>
        string LicenseText()
        {
            var own = Path.Combine(packageDir, "license.txt");
            string text = null;
            // SETUP-C80: the IT company's contract (from its server, in the person's language, else English, else the first)
            try
            {
                var cp = Path.Combine(packageDir, "contract.xml");
                if (File.Exists(cp))
                {
                    var c = OnlineBackup.Core.Atomic.LoadXElement(cp); var texts = c.Elements("TEXT").ToList();
                    var t = texts.FirstOrDefault(x => (string)x.Attribute("LANG") == lang) ?? texts.FirstOrDefault(x => (string)x.Attribute("LANG") == "en") ?? texts.FirstOrDefault();
                    if (t != null && t.Value.Trim().Length > 0) { text = t.Value; contractVersion = (string)c.Attribute("VERSION"); }
                }
            }
            catch (Exception) { }
            try { if (text == null && File.Exists(own)) text = OnlineBackup.Core.Atomic.ReadAllText(own); } catch (Exception) { }
            if (string.IsNullOrEmpty(text))
                text = string.Join("\n\n", new[]
                {
                    T("{0} — license agreement", product),
                    T("1. This software is licensed, not sold, by {0} (the provider) to back up your computers to the provider's backup service.", Support()),
                    T("2. You may install it on the computers covered by your service agreement. You may not copy, change, decompile or resell it."),
                    T("3. Your files are encrypted on this computer before they are sent. Keep your password: without it, backups cannot be restored."),
                    T("4. The software sends the backup service what it needs to work: the computer's name, the backup status and logs. It checks for updates by itself."),
                    T("5. The software is provided as is, as far as the law allows. The provider is not liable for indirect damage or for lost data beyond what your service agreement says."),
                    T("6. Removing the software from this computer does not delete your backups on the server.")
                });
            return text.Replace("\r\n", "\n").Replace("\n", "\r\n");
        }

        void Next()
        {
            error = "";
            switch (Current)
            {
                case "Welcome":
                    if (action == "remove")
                    {
                        if (MessageBox.Show(this, T("Remove {0} from this computer?", product), product, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2, Rtl ? MessageBoxOptions.RightAlign | MessageBoxOptions.RtlReading : 0) != DialogResult.Yes) return;
                        Work(); return;
                    }
                    if (action == "repair") { Work(); return; }
                    step++; Render(); return;
                case "License agreement": if (accept) Work(); return;
                case "Finish":
                    if (installError == null && action != "remove" && launch && result != null) Launch(result.InstallDir);
                    Close(); return;
            }
        }

        /// <summary>The program window, as the person signed in to Windows (not as administrator, like the setup).</summary>
        static void Launch(string installDir)
        {
            var gui = Path.Combine(installDir, "OnlineBackup.Client.exe");
            if (!File.Exists(gui)) return;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "\"" + gui + "\"") { UseShellExecute = false }); }
            catch (Exception) { try { System.Diagnostics.Process.Start(gui); } catch (Exception) { } }
        }

        /// <summary>Installs (the files, the service, the shortcuts — the sign-in is in the program), repairs or removes.</summary>
        void Work()
        {
            step = Array.IndexOf(StepNames, action == "remove" ? "Removal" : "Installation");
            lock (lines) lines.Clear(); finished = false; installError = null; busy = true; Render();
            var answers = new Dictionary<string, string> { { "lang", lang } };
            if (existing != null) { answers["install-dir"] = existing.InstallDir; if (!string.IsNullOrEmpty(existing.DataDir)) answers["data-dir"] = existing.DataDir; }
            foreach (var kv in Fixed) answers[kv.Key] = kv.Value;
            var what = action; var removeAll = removeSettings; var accepted = what == "install" && accept ? contractVersion : null;
            Action<string> say = m => { lock (lines) lines.Add(m); try { BeginInvoke(new Action(Render)); } catch (Exception) { } };
            new Thread(() =>
            {
                try
                {
                    if (what == "remove")
                    {
                        var folder = (string)conn.Attribute("FOLDER"); if (string.IsNullOrEmpty(folder)) folder = "OnlineBackup";
                        Setup.Uninstall(existing.InstallDir, existing.DataDir, product, folder, removeAll, say);
                    }
                    else
                    {
                        result = Setup.Run(packageDir, k => answers.ContainsKey(k) && answers[k] != "" ? answers[k] : null, k => k == "no-register" || Flags.Contains(k), say);
                        try { File.WriteAllText(Path.Combine(result.InstallDir, "language.txt"), lang); } catch (Exception) { }   // the program opens in the language of the installation
                        // accepted here: the program does not ask again when it connects to this server
                        if (accepted != null) try { File.WriteAllText(Path.Combine(result.InstallDir, "contract-accepted.txt"), accepted); } catch (Exception) { }
                    }
                }
                catch (Exception e) { installError = e.Message; }
                try { BeginInvoke(new Action(() => { busy = false; finished = true; step = StepNames.Length - 1; Render(); })); } catch (Exception) { }
            }) { IsBackground = true }.Start();
        }

        // ---------------------------------------------------------------- start, and pictures of every page

        /// <summary>Setup.cmd / Setup.exe on Windows: the installation program; returns when it is closed.</summary>
        public static int Run(string packageDir) { return Run(packageDir, false); }

        /// <summary>remove: from "Uninstall" in Programs and Features — "Remove" is chosen already.</summary>
        public static int Run(string packageDir, bool remove)
        {
            int code = 1;
            var t = new Thread(() =>
            {
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                using (var f = new SetupForm(packageDir)) { if (remove && f.existing != null) f.action = "remove"; Application.Run(f); code = f.finished && f.installError == null ? 0 : 1; }
            });
            t.SetApartmentState(ApartmentState.STA); t.Start(); t.Join();
            return code;
        }

        /// <summary>The robot: every page as a picture (welcome, server, server checked, agreement, account new / existing, done).</summary>
        public static void Screens(string packageDir, string outDir, string lang)
        {
            var t = new Thread(() =>
            {
                Directory.CreateDirectory(outDir);
                Application.EnableVisualStyles();
                using (var f = new SetupForm(packageDir))
                {
                    if (!string.IsNullOrEmpty(lang)) f.lang = lang;
                    f.StartPosition = FormStartPosition.Manual; f.Location = new Point(0, 0); f.CreateControl(); f.Show(); f.Render();
                    Action<string> shot = name =>
                    {
                        // what is on the screen (the controls as Windows draws them)
                        f.Activate(); f.Refresh(); for (int i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(40); }
                        using (var bmp = new Bitmap(f.Width, f.Height))
                        {
                            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(f.Location, Point.Empty, f.Size);
                            bmp.Save(Path.Combine(outDir, "client-setup_" + name + "_" + f.lang + ".png"), ImageFormat.Png);
                        }
                    };
                    f.action = "install"; f.step = 0; f.Render(); shot("welcome");
                    f.step = 1; f.Render(); shot("license");
                    f.accept = true; f.Render(); shot("license-accepted");
                    f.step = 2; f.busy = true; f.lines.AddRange(new[] { f.product + " — https://backup.acme-it.com:8443", "Installed to C:\\Program Files\\" + f.product }); f.Render(); shot("installing");
                    f.busy = false; f.finished = true; f.step = 3; f.result = new Setup.Result(); f.Render(); shot("finish");
                    // SETUP-C70: already installed — repair or remove
                    f.existing = new Setup.Result { Server = "0.1.73" }; f.action = "repair"; f.step = 0; f.Render(); shot("maintenance");
                    f.action = "remove"; f.Render(); shot("remove");
                    f.step = 2; f.Render(); shot("removed");
                    f.Close();
                }
            });
            t.SetApartmentState(ApartmentState.STA); t.Start(); t.Join();
        }
    }
}
#endif
