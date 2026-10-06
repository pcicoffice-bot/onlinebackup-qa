using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using OnlineBackup.Core;

namespace OnlineBackup.Agent
{
    /// <summary>
    /// CLI-010..080: the customer's screen, in the IT company's name. The agent serves it on this computer only
    /// (http://127.0.0.1:port), opened in the browser with a one-time key in the address; every call must carry that key
    /// and a local Host header (no other web site can drive it). Branding comes from branding.xml beside the agent
    /// (written by the server's client builder): product name, company, phone, mail, web site, colour, logo.
    /// Backup now and the set list work with the computer's registration; restore and new sets need the customer's
    /// password (+ code), kept in memory for 15 minutes only.
    /// </summary>
    public sealed class ClientUi : IDisposable
    {
        readonly AgentApp app;
        readonly HttpListener http = new HttpListener();
        public string Key { get; private set; }
        public int Port { get; private set; }
        Client session; string sessionPassword; DateTime sessionUntil;
        readonly object gate = new object();
        readonly Dictionary<string, Job> jobs = new Dictionary<string, Job>();

        sealed class Job { public string Id, Kind, Set, State = "running", Result = "", Detail = ""; public DateTime Started = SystemClock.UtcNow; }

        public ClientUi(AgentApp app, int port)
        {
            this.app = app; Port = port;
            Key = Bytes.Hex(Bytes.Random(16));
            http.Prefixes.Add("http://127.0.0.1:" + port + "/");
            http.Start();
            new Thread(Loop) { IsBackground = true }.Start();
        }

        public string Url { get { return "http://127.0.0.1:" + Port + "/#" + Key; } }

        public static string UiFile { get { return UiFileIn(AppDomain.CurrentDomain.BaseDirectory); } }
        public static string UiFileIn(string installDir) { return Path.Combine(installDir, "ui.txt"); }

        /// <summary>CLI-015: started by the Windows service on the first free port from 18200; the address goes to ui.txt.</summary>
        public static ClientUi StartForService(AgentApp app)
        {
            ClientUi ui = null; Exception last = null;
            for (int i = 0; i < 20 && ui == null; i++) try { ui = new ClientUi(app, 18200 + i); } catch (HttpListenerException e) { last = e; }
            if (ui == null) throw last ?? new InvalidOperationException("no port");
            Atomic.WriteText(UiFile, ui.Url);
#if !NET40
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(UiFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);   // the key: root only
#endif
            return ui;
        }

        void Loop()
        {
            while (http.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = http.GetContext(); } catch (Exception) { return; }
                ThreadPool.QueueUserWorkItem(_ => { try { Handle(ctx); } catch (Exception) { try { ctx.Response.Abort(); } catch { } } });
            }
        }

        public void Dispose() { try { http.Stop(); http.Close(); } catch { } }

        // ---------------------------------------------------------------- branding

        /// <summary>CLI-020: branding.xml beside the agent (the client builder writes it); empty values → neutral defaults.</summary>
        public static XElement Branding()
        {
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            var p = Path.Combine(dir, "branding.xml");
            try { if (File.Exists(p)) return XElement.Load(p); } catch (Exception) { }
            return new XElement("BRANDING");
        }

        /// <summary>BRAND-010: the brand shown by this program — the one built into its package; when the package has none
        /// (installed by hand, or an older package), the brand of the backup server it is connected to (kept for offline use).</summary>
        XElement EffectiveBranding()
        {
            var local = Branding();
            if (!string.IsNullOrEmpty((string)local.Attribute("PRODUCT"))) return local;
            var cache = Path.Combine(app.Home.Dir, "server-brand.xml");
            XElement cached = null;
            try { if (File.Exists(cache)) cached = XElement.Load(cache); } catch (Exception) { }
            if (app.Home.DeviceToken != null && (cached == null || File.GetLastWriteTimeUtc(cache) < SystemClock.UtcNow.AddHours(-6)))
            {
                try
                {
                    var c = app.DeviceClient(); c.Retries = 0;
                    var m = c.Call("GET", "/api/brand");
                    var x = new XElement("BRANDING");
                    foreach (var a in new[] { "PRODUCT", "SLOGAN", "COMPANY", "PHONE", "EMAIL", "WEBSITE", "COLOR", "ACCENT", "LOGO", "LANGUAGE" })
                        if (!string.IsNullOrEmpty(m["brand" + a])) x.SetAttributeValue(a, m["brand" + a]);
                    x.Save(cache); cached = x;
                }
                catch (Exception) { if (cached != null) try { File.SetLastWriteTimeUtc(cache, SystemClock.UtcNow); } catch (Exception) { } }   // offline: keep the last one, try again later
            }
            return cached ?? local;
        }

        static string B(XElement b, string a, string d) { var v = (string)b.Attribute(a); return string.IsNullOrEmpty(v) ? d : v; }

        // ---------------------------------------------------------------- HTTP

        void Handle(HttpListenerContext ctx)
        {
            var req = ctx.Request; var path = req.Url.AbsolutePath;
            var host = (req.Headers["Host"] ?? "").Split(':')[0];
            if (host != "127.0.0.1" && host != "localhost") { Send(ctx, 403, "text/plain", "forbidden"); return; }    // DNS rebinding
            if (req.HttpMethod == "GET" && (path == "/" || path == "/index.html")) { Send(ctx, 200, "text/html; charset=utf-8", Page()); return; }
            if (req.HttpMethod == "GET" && path.StartsWith("/i18n/"))   // I18N-010: the languages (public texts, no key)
            {
                var name = path.Substring(6);
                string assetType; var asset = WebAssets.Get(name, out assetType);   // DESIGN-040: the design system and its fonts
                if (asset != null) { SendBytes(ctx, 200, assetType, asset); return; }
                if (name == "qrcode.js") { using (var rs = typeof(L).Assembly.GetManifestResourceStream("qrcode.js")) using (var rd = new StreamReader(rs, Encoding.UTF8)) Send(ctx, 200, "application/javascript; charset=utf-8", rd.ReadToEnd()); return; }
                if (name == "i18n.js") { using (var rs = typeof(L).Assembly.GetManifestResourceStream("i18n.js")) using (var rd = new StreamReader(rs, Encoding.UTF8)) Send(ctx, 200, "application/javascript; charset=utf-8", rd.ReadToEnd()); return; }
                if (name.EndsWith(".json") && L.Languages.Contains(name.Substring(0, name.Length - 5))) { Send(ctx, 200, "application/json; charset=utf-8", L.Raw(name.Substring(0, name.Length - 5))); return; }
                Send(ctx, 404, "text/plain", "not found"); return;
            }
            if (req.HttpMethod == "GET" && path == "/logo")
            {
                var logo = (string)EffectiveBranding().Attribute("LOGO") ?? "";   // data:image/png;base64,…
                int c = logo.IndexOf(','); if (!logo.StartsWith("data:image/") || c < 0) { Send(ctx, 404, "text/plain", ""); return; }
                SendBytes(ctx, 200, logo.Substring(5, logo.IndexOf(';') - 5), Convert.FromBase64String(logo.Substring(c + 1)));
                return;
            }
            if (!path.StartsWith("/api/")) { Send(ctx, 404, "text/plain", "not found"); return; }
            if (req.Headers["X-Key"] != Key) { Reply(ctx, 403, new Msg().Set("message", "Wrong key: open the screen from the desktop shortcut.")); return; }
            try
            {
                var b = req.HttpMethod == "POST" ? Msg.Read(req.InputStream) : new Msg();
                Reply(ctx, 200, Api(path.Substring(5), req.QueryString, b));
            }
            catch (AgentException e) { Reply(ctx, e.Status == 401 ? 401 : 400, new Msg().Set("message", e.Message).Set("code", e.Code)); }
            catch (Exception e) { Reply(ctx, 500, new Msg().Set("message", e.Message)); }
        }

        static void Send(HttpListenerContext ctx, int status, string type, string text) { SendBytes(ctx, status, type, Encoding.UTF8.GetBytes(text)); }
        static void SendBytes(HttpListenerContext ctx, int status, string type, byte[] b)
        {
            var r = ctx.Response; r.StatusCode = status; r.ContentType = type; r.ContentLength64 = b.Length;
            r.Headers["Cache-Control"] = "no-store";
            r.Headers["X-Frame-Options"] = "DENY";
            r.Headers["Content-Security-Policy"] = "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; frame-ancestors 'none'";
            r.OutputStream.Write(b, 0, b.Length); r.Close();
        }
        static void Reply(HttpListenerContext ctx, int status, Msg m) { SendBytes(ctx, status, "application/xml; charset=utf-8", m.ToBytes()); }

        Client Session()
        {
            lock (gate)
            {
                if (session == null || SystemClock.UtcNow > sessionUntil) { session = null; sessionPassword = null; throw new AgentException(401, "LOGIN", "Sign in with your password."); }
                sessionUntil = SystemClock.UtcNow.AddMinutes(15);
                return session;
            }
        }

        Msg Api(string op, System.Collections.Specialized.NameValueCollection q, Msg b)
        {
            switch (op)
            {
                case "state": return State();
                case "login":
                    {
                        // CLI-030: the customer's password (+ code) — a session of the server; the password stays in memory 15 minutes
                        var c = app.Interactive(b["password"], b["otp"]);
                        lock (gate) { session = c; sessionPassword = b["password"]; sessionUntil = SystemClock.UtcNow.AddMinutes(15); }
                        return new Msg().Set("ok", 1);
                    }
                case "logout": lock (gate) { session = null; sessionPassword = null; } return new Msg().Set("ok", 1);
                // SEC-010: two-step verification of this customer — on / off, set up with the authenticator app
                case "security": { var p = app.Profile(); return new Msg().Set("totp", p.Get("TOTP_ON") == "Y" ? 1 : 0).Set("required", p.Get("REQUIRE_TOTP") == "Y" ? 1 : 0).Set("login", p.Get("LOGIN_NAME")); }
                case "totp-enable": return Session().Call("POST", "/api/totp/enable", new Msg());
                case "totp-confirm": return Session().Call("POST", "/api/totp/confirm", new Msg().Set("code", b["code"]));
                case "totp-disable": return Session().Call("POST", "/api/totp/disable", new Msg());
                case "backup": return Start("backup", b["set"], () => { var r = app.Backup(b["set"]); return new[] { r.Result, "New " + r.New + ", updated " + r.Updated + ", sent " + r.BytesSent + " bytes" }; });
                case "jobs":
                    {
                        var m = new Msg();
                        lock (jobs) foreach (var j in jobs.Values.OrderByDescending(x => x.Started).Take(20))
                            m.Add("jobs", new Msg().Set("id", j.Id).Set("kind", j.Kind).Set("set", j.Set).Set("state", j.State).Set("result", j.Result).Set("detail", j.Detail).Set("started", RunId.UnixMs(j.Started)));
                        return m;
                    }
                case "points": return Points(q["set"]);
                case "files": return Files(q["set"], q["point"]);
                case "restore": return Restore(b);
                case "addset": return AddSet(b);
                case "editset": return EditSet(b);
                case "help":
                    {
                        // TICKETS-030: the customer's calls to its IT company — opened here, with this computer's name
                        try
                        {
                            var dc = app.DeviceClient();
                            if (!string.IsNullOrWhiteSpace(b["subject"]))
                                return dc.Call("POST", "/api/tickets", new Msg().Set("subject", b["subject"].Trim()).Set("description", b["description"]).Set("set", b["set"]).Set("computer", app.Home.Computer)).Set("sent", 1);
                            return dc.Call("GET", "/api/tickets");
                        }
                        catch (AgentException e) when (e.Code == "OFF") { return new Msg().Set("off", 1); }
                    }
                case "dirs": return Dirs(q["path"]);
                // SETUP-C50: the program's sign-in screen on a computer not connected yet — the server's address (the
                // package's, or another one), then an existing customer or a new one; only while not connected
                case "server-check":
                    {
                        if (app.Home.DeviceToken != null) throw new AgentException(409, "CONNECTED", "This computer is already connected.");
                        var r = SetupUi.CheckServer(Connection(), b["server"], b["lang"] ?? "en");
                        var m = new Msg(); foreach (var kv in r) m.Set(kv.Key, kv.Value is bool ? ((bool)kv.Value ? "1" : "0") : Convert.ToString(kv.Value, CultureInfo.InvariantCulture));
                        // SETUP-C80: the same contract was accepted in the installation (the package's server) — not asked again
                        if (PreAccepted(m["server"], m["pin"], m["contractVersion"])) m.Set("contract", "").Set("contractNew", "").Set("preaccepted", 1);
                        return m;
                    }
                case "connect":
                    {
                        if (app.Home.DeviceToken != null) throw new AgentException(409, "CONNECTED", "This computer is already connected.");
                        var answers = new Dictionary<string, string>();
                        foreach (var k in new[] { "login", "password", "otp", "company", "email", "phone", "lang" }) if (!string.IsNullOrEmpty(b[k])) answers[k] = b[k];
                        if (b["mode"] != "new") answers.Remove("company");
                        var said = new List<string>();
                        var login = Setup.Connect(app, b["server"], string.IsNullOrEmpty(b["pin"]) ? null : b["pin"], app.Home.Dir, k => answers.ContainsKey(k) ? answers[k] : null, k => k == "accept-contract" && (b["accept"] == "1" || PreAccepted(b["server"], b["pin"], b["contractVersion"])), said.Add);
                        // the password stays for this session (the first backup can be added at once)
                        try { var c = app.Interactive(b["password"], b["otp"]); lock (gate) { session = c; sessionPassword = b["password"]; sessionUntil = SystemClock.UtcNow.AddMinutes(15); } } catch (Exception) { }
                        return new Msg().Set("login", login);
                    }
                case "update":
                    {
                        // UPD-020: "Update" in the program — the newest client from the backup server (the service restarts)
                        if (b != null && b["apply"] == "1") return new Msg().Set("result", ClientUpdate.Apply(app, m => { }));
                        List<Msg> changed; return ClientUpdate.Check(app, out changed);
                    }
            }
            throw new AgentException(404, "OP", "unknown");
        }

        /// <summary>The contract version accepted in the installation, for the package's own server (its address or its certificate).</summary>
        static bool PreAccepted(string server, string pin, string version)
        {
            try
            {
                var f = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "contract-accepted.txt");
                if (string.IsNullOrEmpty(version) || version == "0" || !File.Exists(f) || File.ReadAllText(f).Trim() != version) return false;
                var c = Connection(); var pkgPin = ((string)c.Attribute("PIN") ?? "").ToLowerInvariant();
                return Setup.SameServer(server, (string)c.Attribute("SERVER")) || (pkgPin.Length > 0 && pkgPin == (pin ?? "").ToLowerInvariant());
            }
            catch (Exception) { return false; }
        }

        static XElement Connection()
        {
            var p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "connection.xml");
            return File.Exists(p) ? XElement.Load(p) : new XElement("CONNECTION");
        }

        Msg State()
        {
            var br = EffectiveBranding();
            var m = new Msg().Set("registered", app.Home.DeviceToken != null ? 1 : 0).Set("computer", app.Home.Computer).Set("login", app.Home.Login ?? "")
                .Set("restic", ResticSupported ? 1 : 0).Set("product", B(br, "PRODUCT", "ITSguard Server Online")).Set("slogan", B(br, "SLOGAN", "")).Set("company", B(br, "COMPANY", "")).Set("phone", B(br, "PHONE", "")).Set("email", B(br, "EMAIL", ""))
                .Set("website", B(br, "WEBSITE", "")).Set("color", B(br, "COLOR", "#4F46E5")).Set("accent", B(br, "ACCENT", "#F97316")).Set("powered", B(br, "POWERED", "")).Set("language", B(br, "LANGUAGE", "")).Set("defaultServer", (string)Connection().Attribute("SERVER") ?? "").Set("logo", string.IsNullOrEmpty((string)br.Attribute("LOGO")) ? 0 : 1);
            lock (gate) m.Set("session", session != null && SystemClock.UtcNow < sessionUntil ? 1 : 0);
            if (app.Home.DeviceToken == null) return m;
            try
            {
                // like Ahsay OBM: this computer's own sets; the others (other computers of the customer) only for restore
                var pr = app.DeviceClient().Call("GET", "/api/profile");
                var rights = pr.List("rights").FirstOrDefault() ?? new Msg();
                m.Set("canAdd", rights["can_add_sets"] ?? "1").Set("canSources", rights["can_edit_sources"] ?? "1").Set("canSchedule", rights["can_edit_schedule"] ?? "1");
                foreach (var s in Core.Profile.Parse(pr["profile"]).Sets)
                {
                    var la = Path.Combine(app.Home.SetDir(s.Id), "last-attempt.txt");
                    string last = "", result = "";
                    if (File.Exists(la)) { var f = File.ReadAllText(la).Split('\t'); last = f[0]; result = f.Length > 1 ? f[1].Trim() : ""; }
                    bool mine = string.IsNullOrEmpty(s.Computer) || s.Computer.Equals(app.Home.Computer, StringComparison.OrdinalIgnoreCase);
                    m.Add("sets", new Msg().Set("id", s.Id).Set("name", s.Name).Set("type", s.Type).Set("engine", s.Engine).Set("sources", string.Join("; ", s.Sources.ToArray()))
                        .Set("computer", s.Computer).Set("mine", mine ? 1 : 0).Set("src", string.Join("\n", s.Sources.ToArray())).Set("skip", string.Join("\n", s.Deselected.ToArray()))
                        .Set("hh", s.Hour).Set("mm", s.Minute)
                        .Set("hour", s.Hour.ToString("00", CultureInfo.InvariantCulture) + ":" + s.Minute.ToString("00", CultureInfo.InvariantCulture)).Set("last", last).Set("result", result));
                }
            }
            catch (AgentException e) { m.Set("offline", e.Message); }
            return m;
        }

        Msg Start(string kind, string set, Func<string[]> work)
        {
            var j = new Job { Id = Bytes.Hex(Bytes.Random(6)), Kind = kind, Set = set };
            lock (jobs)
            {
                if (jobs.Values.Any(x => x.State == "running" && x.Set == set)) throw new AgentException(409, "BUSY", "Another action on this set is still running.");
                jobs[j.Id] = j;
            }
            new Thread(() =>
            {
                try { var r = work(); j.Result = r[0]; j.Detail = r.Length > 1 ? r[1] : ""; j.State = r[0].StartsWith("BS_STOP_SUCCESS") || r[0] == "OK" ? "ok" : "failed"; }
                catch (Exception e) { j.State = "failed"; j.Result = "ERROR"; j.Detail = e.Message; }
            }) { IsBackground = true }.Start();
            return new Msg().Set("job", j.Id);
        }

        BackupSetInfo SetOf(string id)
        {
            var s = app.Sets().FirstOrDefault(x => x.Id == id);
            if (s == null) throw new AgentException(404, "NO_SET", "The backup set does not exist.");
            return s;
        }

        /// <summary>CLI-040: restore points, newest first.</summary>
        Msg Points(string setId)
        {
            var s = SetOf(setId); var m = new Msg();
            if (s.Engine == "RESTIC")
            {
                if (!File.Exists(Path.Combine(app.Home.SetDir(s.Id), "restic-init.txt"))) return m;   // never backed up yet
                foreach (var p in app.Restic(s).SnapshotList()) m.Add("points", p);
            }
            else
            {
                var r = app.RestoreFor(Session(), setId, PasswordNow());
                foreach (var p in r.Points().OrderByDescending(x => x, StringComparer.Ordinal)) m.Add("points", new Msg().Set("id", p).Set("time", p));
            }
            return m;
        }

        string PasswordNow() { Session(); lock (gate) return sessionPassword; }

        /// <summary>CLI-050: the files of a point (paths decrypted here — the server never has names in clear).</summary>
        Msg Files(string setId, string point)
        {
            var s = SetOf(setId); var m = new Msg();
            if (s.Engine == "RESTIC")
            {
                Session();
                foreach (var f in app.Restic(s, PasswordNow()).Ls(point).Where(x => x["type"] == "file")) m.Add("files", new Msg().Set("path", f["path"]).Set("size", f["size"]).Set("mtime", f["mtime"]));
            }
            else
            {
                var r = app.RestoreFor(Session(), setId, PasswordNow());
                foreach (var kv in r.Files(point)) m.Add("files", new Msg().Set("path", kv.Key).Set("size", kv.Value["orig"]).Set("mtime", kv.Value["mtime"]));
            }
            return m;
        }

        /// <summary>CLI-060: restore chosen files / folders (all when none) to a folder; a background job.</summary>
        Msg Restore(Msg b)
        {
            var s = SetOf(b["set"]); var client = Session(); var pw = PasswordNow();
            var target = b["target"];
            if (string.IsNullOrEmpty(target) && b["toM365"] != "1") throw new AgentException(400, "TARGET", "Choose a destination folder.");
            var paths = b.List("paths").Select(x => x["p"]).Where(x => !string.IsNullOrEmpty(x)).ToList();
            var point = b["point"]; bool overwrite = b["overwrite"] == "1";
            return Start("restore", s.Id, () =>
            {
                if (!string.IsNullOrEmpty(target)) Directory.CreateDirectory(target);
                if (s.Type == "GWS" && b["toM365"] == "1")
                {
                    var gb = app.Restic(s, pw).RestoreToGoogle(point, paths.Count == 0 ? null : paths, new List<string>());
                    return new[] { gb.Failed == 0 ? "OK" : "FAILED", "Returned to Google: e-mails " + gb.Mail + ", files " + gb.Files + (gb.Failed > 0 ? ", failed " + gb.Failed : "") };
                }
                if (s.Type == "M365" && b["toM365"] == "1")
                {
                    var back = app.Restic(s, pw).RestoreToM365(point, paths.Count == 0 ? null : paths, new List<string>());
                    return new[] { back.Failed == 0 ? "OK" : "FAILED", "Returned to Microsoft 365: e-mails " + back.Mail + ", files " + back.Files + (back.Failed > 0 ? ", failed " + back.Failed : "") };
                }
                if (s.Engine == "RESTIC")
                {
                    app.Restic(s, pw).RestoreMany(point, target, paths.Count == 0 ? null : paths, new List<string>(), overwrite);
                    return new[] { "OK", "Restored to " + target };
                }
                var r = app.RestoreFor(client, s.Id, pw);
                Func<string, bool> filter = paths.Count == 0 ? null : (Func<string, bool>)(p => paths.Any(x => p.Equals(x, StringComparison.OrdinalIgnoreCase) || p.StartsWith(x.TrimEnd('\\', '/') + "\\", StringComparison.OrdinalIgnoreCase) || p.StartsWith(x.TrimEnd('\\', '/') + "/", StringComparison.OrdinalIgnoreCase)));
                r.Run(point, target, filter, overwrite);
                return new[] { r.Failed == 0 ? "OK" : "FAILED", "Restored " + r.Restored + ", skipped " + r.Skipped + ", failed " + r.Failed };
            });
        }

        /// <summary>CLI-070: a new set of folders (restic on Windows 10 / 2016 and later, this product's engine before).</summary>
        static List<string> Lines(string v) { return (v ?? "").Split(new[] { '\n', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList(); }

        /// <summary>SRC-030: the sub-folders of a folder on this computer (the drives when no folder is given), for the tree.</summary>
        static Msg Dirs(string path)
        {
            var m = new Msg().Set("sep", Path.DirectorySeparatorChar.ToString());
            List<string> l;
            if (string.IsNullOrEmpty(path)) l = FolderScan.Roots();
            else
            {
                l = new List<string>();
                string[] subs;
                try { subs = Directory.GetDirectories(path); } catch (Exception) { return m.Set("denied", 1); }
                foreach (var d in subs)
                {
                    try { var a = File.GetAttributes(d); if ((a & FileAttributes.ReparsePoint) != 0 || ((a & FileAttributes.System) != 0 && (a & FileAttributes.Hidden) != 0)) continue; } catch (Exception) { continue; }
                    l.Add(d);
                }
                l.Sort(StringComparer.OrdinalIgnoreCase);
            }
            foreach (var d in l.Take(5000)) m.Add("dirs", new Msg().Set("path", d));
            return m;
        }

        /// <summary>SET-040: the customer changes a set of this computer — its folders and its time, when the IT company allows it.</summary>
        Msg EditSet(Msg b)
        {
            var client = Session();
            var e = app.Profile().FindSet(b["set"]);
            if (e == null) throw new AgentException(404, "NO_SET", "The backup set was not found.");
            var s = BackupSetInfo.FromXml(e);
            if (b["sources"] != null) { var src = Lines(b["sources"]); if (src.Count == 0 && s.Type == "FILE") throw new AgentException(400, "NO_SOURCE", "Choose at least one folder."); s.Sources = src; s.Deselected = Lines(b["exclude"]); }
            int hour, minute;
            if (int.TryParse(b["hour"], out hour) && hour >= 0 && hour <= 23) s.Hour = hour;
            if (int.TryParse(b["minute"], out minute) && minute >= 0 && minute <= 59) s.Minute = minute;
            if (!string.IsNullOrWhiteSpace(b["name"])) s.Name = b["name"].Trim();
            client.Call("POST", "/api/sets/" + s.Id + "/settings", new Msg().Set("set", s.ToXml(new XElement(e)).ToString(SaveOptions.DisableFormatting)));
            return new Msg().Set("ok", 1);
        }

        Msg AddSet(Msg b)
        {
            var client = Session(); var pw = PasswordNow();
            var sources = (b["sources"] ?? "").Split(new[] { '\n', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            bool all = b["type"] == "MYSQL" || b["type"] == "POSTGRESQL" || b["type"] == "HYPERV" || b["type"] == "VMWARE" || b["type"] == "ORACLE" || b["type"] == "DOMINO" || b["type"] == "MSSQL" || b["type"] == "SYSTEMSTATE";
            if (b["type"] == "BAREMETAL" && sources.Count == 0) throw new AgentException(400, "NO_SOURCE", "Choose where to keep the image (a second disk or a network folder).");
            if (b["type"] == "GWS" && sources.Count == 0) throw new AgentException(400, "NO_SOURCE", "Choose a local folder for the copy.");
            if (sources.Count == 0 && !all) throw new AgentException(400, "NO_SOURCE", "Choose at least one folder.");
            int hour; if (!int.TryParse(b["hour"] ?? "22", out hour) || hour < 0 || hour > 23) hour = 22;
            int minute; if (!int.TryParse(b["minute"] ?? "0", out minute) || minute < 0 || minute > 59) minute = 0;
            var s = new BackupSetInfo { Name = string.IsNullOrEmpty(b["name"]) ? "Files" : b["name"], Sources = sources, Deselected = Lines(b["exclude"]), Hour = hour, Minute = minute, Engine = ResticSupported ? "RESTIC" : "" };
            if (b["type"] == "M365")
            {
                // CLI-075: Microsoft 365 — the folder is the local mirror; the application secret stays on this computer
                if (!ResticSupported) throw new AgentException(400, "M365", "Microsoft 365 backup needs Windows 10 / Server 2016 or later.");
                if (string.IsNullOrEmpty(b["tenant"]) || string.IsNullOrEmpty(b["clientId"]) || string.IsNullOrEmpty(b["secret"])) throw new AgentException(400, "M365", "Fill in the tenant, the application id and the secret.");
                s.Type = "M365"; s.Engine = "RESTIC"; s.Vss = false; s.Sources = sources.Take(1).ToList();
                s.M365Tenant = b["tenant"].Trim(); s.M365ClientId = b["clientId"].Trim(); s.M365Users = (b["users"] ?? "").Trim();
            }
            if (b["type"] == "GWS")
            {
                // CLI-077: Google Workspace — the folder is the local mirror; the service account key stays on this computer
                if (!ResticSupported) throw new AgentException(400, "GWS", "Google Workspace backup needs Windows 10 / Server 2016 or later.");
                if (string.IsNullOrEmpty(b["gwsKey"])) throw new AgentException(400, "GWS", "Paste the Service Account key (JSON).");
                try { new GoogleClient(b["gwsKey"], GoogleClient.ReadScopes); } catch (Exception) { throw new AgentException(400, "GWS", "The Service Account key is not valid."); }
                s.Type = "GWS"; s.Engine = "RESTIC"; s.Vss = false; s.Sources = sources.Take(1).ToList();
                s.GwsAdmin = (b["gwsAdmin"] ?? "").Trim(); s.M365Users = (b["users"] ?? "").Trim();
            }
            if (b["type"] == "MSSQL")
            {
                // CLI-078: SQL Server — native BACKUP of each database ("Microsoft SQL Server\instance[\database]"); empty user = Windows authentication
                var inst = string.IsNullOrEmpty((b["dbHost"] ?? "").Trim()) ? Environment.MachineName : b["dbHost"].Trim();
                s.Type = "MSSQL"; s.SqlUser = (b["dbUser"] ?? "").Trim();
                s.Sources = sources.Count == 0 ? new List<string> { SqlBackup.Prefix + "\\" + inst } : sources.Select(d => SqlBackup.Prefix + "\\" + inst + "\\" + d).ToList();
            }
            else if (b["type"] == "SYSTEMSTATE") { s.Type = "SYSTEMSTATE"; s.Sources = new List<string>(); }
            else if (b["type"] == "BAREMETAL") { s.Type = DiskImage.Type; s.Vss = false; }
            else if (all)
            {
                // CLI-076: databases (own dump tools) and Hyper-V VMs (Export-VM); none chosen = all of them
                s.Type = b["type"]; s.Vss = false;
                if (s.Type == "DOMINO") { s.Vss = true; if (s.Sources.Count == 0) s.Sources = Domino.DefaultSources(); }
                if (s.Type == "VMWARE") { s.VmDatacenter = (b["vmDatacenter"] ?? "").Trim(); s.VmThumbprint = (b["vmThumbprint"] ?? "").Trim(); if (string.IsNullOrEmpty(b["dbHost"]) || string.IsNullOrEmpty(b["dbUser"])) throw new AgentException(400, "VMWARE", "Fill in the ESXi / vCenter address and the user."); }
                if (s.Type == "ORACLE" && s.Sources.Count == 0) throw new AgentException(400, "ORACLE", "Enter at least one instance (ORACLE_SID).");
                if (s.Type != "HYPERV" && s.Type != "DOMINO") { s.DbHost = (b["dbHost"] ?? "").Trim(); int port; s.DbPort = int.TryParse(b["dbPort"], out port) ? port : 0; s.SqlUser = (b["dbUser"] ?? "").Trim(); }
            }
            // BKP-040: how changes inside large files are sent (own engine), and SQL Server's weekly full + daily differential
            s.DeltaType = b["deltaType"] == "D" ? "D" : "I";
            int fullDay; if (s.Type == "MSSQL" && int.TryParse(b["sqlFullDay"], out fullDay) && fullDay >= -1 && fullDay <= 6) s.SqlFullDay = fullDay;
            var created = app.CreateSet(client, pw, s);
            if (s.Type == "M365") app.Home.SaveSecret(created.Id + "-m365", b["secret"]);
            if (s.Type == "GWS") app.Home.SaveSecret(created.Id + "-gws", b["gwsKey"]);
            if ((s.Type == "MSSQL" || s.Type == "MYSQL" || s.Type == "POSTGRESQL" || s.Type == "ORACLE" || s.Type == "VMWARE") && !string.IsNullOrEmpty(b["dbPassword"])) app.Home.SaveSecret(created.Id + "-sql", b["dbPassword"]);
            return new Msg().Set("id", created.Id).Set("engine", s.Engine);
        }

        /// <summary>restic runs on Windows 10 / Server 2016 (build 14393) and later, and wherever restic is present (tests).</summary>
        public static bool ResticSupported
        {
            get
            {
                if (!File.Exists(ResticRunner.Exe)) return false;
                var v = Environment.OSVersion;
                return v.Platform != PlatformID.Win32NT || v.Version.Major > 10 || (v.Version.Major == 10 && v.Version.Build >= 14393);
            }
        }

        string Page()
        {
            using (var s = typeof(ClientUi).Assembly.GetManifestResourceStream("OnlineBackup.Agent.client.html"))
            using (var r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd().Replace("data-lang=\"en\"", "data-lang=\"" + L.Norm((string)Branding().Attribute("LANGUAGE")) + "\"");
        }
    }
}
