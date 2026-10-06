using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using OnlineBackup.Core;

namespace OnlineBackup.Server
{
    /// <summary>
    /// PORTAL-010..060: the partner portal, served by the product owner's licensing centre under /portal. An IT company
    /// signs up (company, contact, e-mail, password), describes its product (name, slogan, logo, colour, contact details,
    /// language) and downloads: the server package with its branding preset (the installation wizard is filled in, and
    /// the server fetches its trial licence by itself), and the client software for Windows, Mac and Linux for its server's
    /// address. A trial licence is issued once per account (the owner's limits); everything else is the owner's decision
    /// on the owner's screen (/portal, "owner" sign-in): accounts, licences, enabling / disabling.
    /// Data: &lt;centre data&gt;\portal\settings.json and accounts\&lt;id&gt;.json. Passwords: PBKDF2-SHA256, never stored or logged.
    /// </summary>
    public sealed class Portal
    {
        readonly string dir, privateKey;
        readonly Func<DateTime> clock;
        readonly object gate = new object();
        readonly Dictionary<string, KeyValuePair<string, DateTime>> sessions = new Dictionary<string, KeyValuePair<string, DateTime>>();   // token → (account id or "owner", until)
        readonly Dictionary<string, KeyValuePair<int, DateTime>> failures = new Dictionary<string, KeyValuePair<int, DateTime>>();      // ip → (count, until)

        public Portal(string centreDataDir, string privateKeyB64, Func<DateTime> clock)
        {
            dir = Path.Combine(centreDataDir, "portal"); privateKey = privateKeyB64; this.clock = clock;
            Directory.CreateDirectory(Path.Combine(dir, "accounts"));
        }

        // ------------------------------------------------------------------ settings (the owner's, set by license-center-portal)

        public sealed class Settings
        {
            public string Package = "", CenterUrl = "", OwnerHash = "";
            public bool SignupOpen = true;
            // LIC-075: no trial by default — a partner's server starts in the free edition (10 computers, 500 GB); 0 days = no trial
            public int TrialDays = 0, TrialUsers = 10, TrialComputers = 25; public double TrialStorageGB = 500;
            public string TrialModules = string.Join(",", License.AllModules);
        }

        string SettingsPath { get { return Path.Combine(dir, "settings.json"); } }

        public Settings Load()
        {
            var s = new Settings();
            if (!File.Exists(SettingsPath)) return s;
            var j = Json.Obj(Json.Parse(File.ReadAllText(SettingsPath)));
            s.Package = Json.Str(j, "package") ?? ""; s.CenterUrl = Json.Str(j, "centerUrl") ?? ""; s.OwnerHash = Json.Str(j, "ownerHash") ?? "";
            s.SignupOpen = Json.Str(j, "signupOpen") != "False";
            if (j.ContainsKey("trialDays")) s.TrialDays = (int)Json.Num(j, "trialDays");
            if (j.ContainsKey("trialUsers")) s.TrialUsers = (int)Json.Num(j, "trialUsers");
            if (j.ContainsKey("trialComputers")) s.TrialComputers = (int)Json.Num(j, "trialComputers");
            if (j.ContainsKey("trialStorageGB")) s.TrialStorageGB = Json.Num(j, "trialStorageGB");
            s.TrialModules = Json.Str(j, "trialModules") ?? s.TrialModules;
            return s;
        }

        public void Save(Settings s)
        {
            Atomic.WriteText(SettingsPath, Json.Write(new Dictionary<string, object>
            {
                { "package", s.Package }, { "centerUrl", s.CenterUrl }, { "ownerHash", s.OwnerHash }, { "signupOpen", s.SignupOpen ? "True" : "False" },
                { "trialDays", (long)s.TrialDays }, { "trialUsers", (long)s.TrialUsers }, { "trialComputers", (long)s.TrialComputers }, { "trialStorageGB", s.TrialStorageGB }, { "trialModules", s.TrialModules },
            }));
        }

        // ------------------------------------------------------------------ passwords

        public static string Hash(string password)
        {
            var salt = Bytes.Random(16);
            using (var k = new Rfc2898DeriveBytes(Encoding.UTF8.GetBytes(password), salt, 200000, HashAlgorithmName.SHA256))
                return "pbkdf2$200000$" + Convert.ToBase64String(salt) + "$" + Convert.ToBase64String(k.GetBytes(32));
        }

        public static bool Verify(string password, string stored)
        {
            var p = (stored ?? "").Split('$');
            if (p.Length != 4 || p[0] != "pbkdf2") return false;
            using (var k = new Rfc2898DeriveBytes(Encoding.UTF8.GetBytes(password ?? ""), Convert.FromBase64String(p[2]), int.Parse(p[1], CultureInfo.InvariantCulture), HashAlgorithmName.SHA256))
                return CryptographicOperations.FixedTimeEquals(k.GetBytes(32), Convert.FromBase64String(p[3]));
        }

        static void CheckPassword(string pw)
        {
            if (!Passwords.Ok(pw)) throw new PortalError(400, Passwords.Rule);
        }

        // ------------------------------------------------------------------ accounts

        string AccountPath(string id) { return Path.Combine(dir, "accounts", new string((id ?? "").Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray()) + ".json"); }
        public Dictionary<string, object> Account(string id) { var p = AccountPath(id); return File.Exists(p) ? Json.Obj(Json.Parse(File.ReadAllText(p))) : null; }
        void SaveAccount(Dictionary<string, object> a) { Atomic.WriteText(AccountPath(Json.Str(a, "id")), Json.Write(a)); }
        public IEnumerable<Dictionary<string, object>> Accounts() { return Directory.GetFiles(Path.Combine(dir, "accounts"), "*.json").Select(f => Json.Obj(Json.Parse(File.ReadAllText(f)))); }
        Dictionary<string, object> ByEmail(string email) { return Accounts().FirstOrDefault(a => string.Equals(Json.Str(a, "email"), email, StringComparison.OrdinalIgnoreCase)); }

        static readonly Regex EmailRx = new Regex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$");

        public Dictionary<string, object> Register(Dictionary<string, object> q)
        {
            if (!Load().SignupOpen) throw new PortalError(403, "New sign-ups are closed. Contact the software vendor.");
            var company = (Json.Str(q, "company") ?? "").Trim(); var contact = (Json.Str(q, "contact") ?? "").Trim();
            var email = (Json.Str(q, "email") ?? "").Trim().ToLowerInvariant(); var phone = (Json.Str(q, "phone") ?? "").Trim(); var country = (Json.Str(q, "country") ?? "").Trim();
            if (company.Length < 2 || company.Length > 100) throw new PortalError(400, "Please enter the company name.");
            if (contact.Length < 2 || contact.Length > 100) throw new PortalError(400, "Please enter your name.");
            if (!EmailRx.IsMatch(email) || email.Length > 200) throw new PortalError(400, "The e-mail address is not valid.");
            CheckPassword(Json.Str(q, "password"));
            if (Json.Str(q, "terms") != "1") throw new PortalError(400, "Please accept the terms of use.");
            lock (gate)
            {
                if (ByEmail(email) != null) throw new PortalError(409, "An account with this e-mail already exists. Sign in instead.");
                var a = new Dictionary<string, object>
                {
                    { "id", "P-" + Bytes.Hex(Bytes.Random(6)) }, { "company", company }, { "contact", contact }, { "email", email }, { "phone", phone }, { "country", country },
                    { "password", Hash(Json.Str(q, "password")) }, { "created", clock().ToString("o", CultureInfo.InvariantCulture) }, { "status", "active" },
                    { "token", Bytes.Hex(Bytes.Random(24)) }, { "brand", new Dictionary<string, object> { { "PRODUCT", company + " Backup" }, { "COMPANY", company }, { "EMAIL", email }, { "PHONE", phone }, { "COLOR", "#4F46E5" }, { "ACCENT", "#F97316" }, { "LANGUAGE", L.Norm(Json.Str(q, "lang")) } } },
                    { "servers", new List<object>() },
                };
                SaveAccount(a);
                return a;
            }
        }

        // ------------------------------------------------------------------ sessions

        string NewSession(string who) { var t = Bytes.Hex(Bytes.Random(24)); lock (sessions) sessions[t] = new KeyValuePair<string, DateTime>(who, clock().AddHours(8)); return t; }
        string Who(HttpListenerContext c)
        {
            var t = c.Request.Headers["X-Session"];
            if (string.IsNullOrEmpty(t)) return null;
            lock (sessions) { KeyValuePair<string, DateTime> s; return sessions.TryGetValue(t, out s) && clock() < s.Value ? s.Key : null; }
        }

        void Throttle(string ip)
        {
            lock (failures) { KeyValuePair<int, DateTime> f; if (failures.TryGetValue(ip, out f) && f.Key >= 5 && clock() < f.Value) throw new PortalError(429, "Too many attempts. Try again in 15 minutes."); }
        }
        void Failed(string ip)
        {
            lock (failures) { KeyValuePair<int, DateTime> f; int n = failures.TryGetValue(ip, out f) && clock() < f.Value ? f.Key + 1 : 1; failures[ip] = new KeyValuePair<int, DateTime>(n, clock().AddMinutes(15)); }
        }

        // ------------------------------------------------------------------ licences

        static readonly Regex ServerIdRx = new Regex(@"^[A-Za-z0-9-]{6,80}$");

        /// <summary>PORTAL-030: the trial licence of an account (once; a second server, or a full licence, is the owner's decision).</summary>
        public string Trial(Dictionary<string, object> a, string serverId)
        {
            serverId = (serverId ?? "").Trim();
            if (!ServerIdRx.IsMatch(serverId)) throw new PortalError(400, "The server ID is not valid. It is shown in the management website → Company and product → Licence.");
            var servers = Json.Arr(a.ContainsKey("servers") ? a["servers"] : null).Select(Json.Obj).ToList();
            var same = servers.FirstOrDefault(s => Json.Str(s, "serverId") == serverId);
            if (same != null) return Json.Str(same, "license");
            if (servers.Count > 0) throw new PortalError(402, "The trial licence was already issued for another server. Contact the software vendor for a full licence.");
            var st = Load();
            if (st.TrialDays <= 0) throw new PortalError(409, "There is no trial licence: the server works in the free edition (up to 10 computers and 500 GB) without a licence. For more, contact the software vendor.");
            var id = "L-" + clock().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "-" + Bytes.Hex(Bytes.Random(2));
            var lic = License.Issue(privateKey, id, Json.Str(a, "company"), "TRIAL", serverId, st.TrialUsers, st.TrialStorageGB, st.TrialModules.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0),
                clock(), clock().AddDays(st.TrialDays), st.TrialComputers, st.CenterUrl);
            servers.Add(new Dictionary<string, object> { { "serverId", serverId }, { "licenseId", id }, { "edition", "TRIAL" }, { "expires", clock().AddDays(st.TrialDays).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }, { "license", lic } });
            a["servers"] = servers.Cast<object>().ToList();
            SaveAccount(a);
            return lic;
        }

        /// <summary>PORTAL-050: the owner issues (or replaces) a licence for one of an account's servers.</summary>
        public string Issue(Dictionary<string, object> a, Dictionary<string, object> q)
        {
            var serverId = (Json.Str(q, "serverId") ?? "").Trim();
            if (!ServerIdRx.IsMatch(serverId)) throw new PortalError(400, "The server ID is not valid.");
            var days = Math.Max(1, (int)Json.Num(q, "days"));
            var edition = (Json.Str(q, "edition") ?? "PRO").ToUpperInvariant();
            var modules = (Json.Str(q, "modules") ?? string.Join(",", License.AllModules)).Split(',').Select(x => x.Trim().ToUpperInvariant()).Where(x => License.AllModules.Contains(x)).ToList();
            var id = "L-" + clock().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "-" + Bytes.Hex(Bytes.Random(2));
            var lic = License.Issue(privateKey, id, Json.Str(a, "company"), edition, serverId, (int)Json.Num(q, "users"), Json.Num(q, "storageGB"), modules, clock(), clock().AddDays(days), (int)Json.Num(q, "computers"), Load().CenterUrl);
            var servers = Json.Arr(a.ContainsKey("servers") ? a["servers"] : null).Select(Json.Obj).Where(s => Json.Str(s, "serverId") != serverId).ToList();
            servers.Add(new Dictionary<string, object> { { "serverId", serverId }, { "licenseId", id }, { "edition", edition }, { "expires", clock().AddDays(days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }, { "license", lic } });
            a["servers"] = servers.Cast<object>().ToList();
            SaveAccount(a);
            return lic;
        }

        public sealed class Update { public string Version, Sha256, Signature, Path; public long Size; }

        /// <summary>UPD-010: the current package as one ZIP (without any account's branding), its SHA-256 and the owner's signature of both.</summary>
        public Update UpdatePackage()
        {
            var st = Load();
            var root = ServerPackageDir(st);
            var vf = System.IO.Path.Combine(st.Package, "version.txt");
            var version = File.Exists(vf) ? File.ReadAllText(vf).Trim() : "0";
            var dir = System.IO.Path.Combine(this.dir, "updates"); Directory.CreateDirectory(dir);
            var zip = System.IO.Path.Combine(dir, "OnlineBackup-Server-" + new string(version.Where(ch => char.IsLetterOrDigit(ch) || ch == '.' || ch == '-').ToArray()) + ".zip");
            lock (gate)
                if (!File.Exists(zip))
                {
                    var tmp = zip + ".tmp";
                    using (var fs = File.Create(tmp)) using (var z = new ZipArchive(fs, ZipArchiveMode.Create))
                        foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                        {
                            var rel = System.IO.Path.GetRelativePath(root, f).Replace('\\', '/');
                            if (rel == "server/branding-preset.xml" || rel == "server/portal.xml") continue;
                            z.CreateEntryFromFile(f, rel, CompressionLevel.Fastest);
                        }
                    File.Move(tmp, zip);
                }
            string sha; using (var fs = File.OpenRead(zip)) using (var h = SHA256.Create()) sha = Bytes.Hex(h.ComputeHash(fs));
            byte[] sig;
            using (var ec = ECDsa.Create()) { ec.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKey), out _); sig = ec.SignData(Encoding.UTF8.GetBytes("OBUPDATE|" + version + "|" + sha), HashAlgorithmName.SHA256); }
            return new Update { Version = version, Sha256 = sha, Signature = Convert.ToBase64String(sig), Path = zip, Size = new FileInfo(zip).Length };
        }

        // ------------------------------------------------------------------ packages

        static Dictionary<string, string> Brand(Dictionary<string, object> a)
        {
            var b = Json.Obj(a.ContainsKey("brand") ? a["brand"] : null);
            return Vendors.BrandAttrs.ToDictionary(k => k, k => Json.Str(b, k) ?? "");
        }

        static string ServerPackageDir(Settings st)
        {
            if (string.IsNullOrEmpty(st.Package) || !Directory.Exists(Path.Combine(st.Package, "server"))) throw new PortalError(503, "The download is not ready yet. Please try again later.");
            return st.Package;
        }

        /// <summary>PORTAL-020: the server package (as built by build-package.sh) + server\branding-preset.xml + server\portal.xml, as a ZIP file.</summary>
        public void ServerZip(Dictionary<string, object> a, string portalUrl, Stream output)
        {
            var root = ServerPackageDir(Load());
            var preset = new XElement("BRANDING");
            foreach (var kv in Brand(a)) preset.SetAttributeValue(kv.Key, kv.Value);
            var portal = new XElement("PORTAL", new XAttribute("URL", portalUrl), new XAttribute("ACCOUNT", Json.Str(a, "id")), new XAttribute("TOKEN", Json.Str(a, "token")), new XAttribute("TRIAL", Load().TrialDays > 0 ? "1" : "0"));
            using (var z = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(root, f).Replace('\\', '/');
                    if (rel == "server/branding-preset.xml" || rel == "server/portal.xml") continue;
                    z.CreateEntryFromFile(f, rel, CompressionLevel.Fastest);
                }
                using (var w = new StreamWriter(z.CreateEntry("server/branding-preset.xml").Open(), new UTF8Encoding(false))) w.Write(preset.ToString());
                using (var w = new StreamWriter(z.CreateEntry("server/portal.xml").Open(), new UTF8Encoding(false))) w.Write(portal.ToString());
            }
        }

        static readonly Regex UrlRx = new Regex(@"^https?://[A-Za-z0-9.\-\[\]:]+(:\d{1,5})?/?$");

        /// <summary>PORTAL-040: the client software of the account's product for its server's address and certificate fingerprint.</summary>
        public byte[] Client(Dictionary<string, object> a, string os, string serverUrl, string pin, out string fileName)
        {
            serverUrl = (serverUrl ?? "").Trim().TrimEnd('/');
            if (!UrlRx.IsMatch(serverUrl)) throw new PortalError(400, "Server address for customers: for example https://backup.company.com:8443");
            pin = (pin ?? "").Replace(":", "").Replace(" ", "").Trim().ToLowerInvariant();
            if (pin.Length > 0 && !Regex.IsMatch(pin, "^[0-9a-f]{64}$")) throw new PortalError(400, "The certificate fingerprint is 64 characters (0-9, a-f), as shown by the installation.");
            var clientDir = Path.Combine(ServerPackageDir(Load()), "server", "client");
            var lic = Json.Arr(a.ContainsKey("servers") ? a["servers"] : null).Select(Json.Obj).Any(s => Json.Str(s, "edition") != "TRIAL");
            var p = ClientPackage.FromBrand(Brand(a), lic && Json.Str(a, "whiteLabel") == "1", serverUrl, pin, clientDir);
            fileName = ClientPackage.FileName(p, os);
            try { return os == "linux" ? ClientPackage.BuildLinux(p) : os == "mac" ? ClientPackage.BuildMac(p) : os == "zip" ? ClientPackage.Build(p) : ClientPackage.BuildExe(p); }
            catch (ApiException) { throw new PortalError(503, "The download is not ready yet. Please try again later."); }
        }

        // ------------------------------------------------------------------ HTTP

        public sealed class PortalError : Exception { public readonly int Status; public PortalError(int status, string message) : base(message) { Status = status; } }

        static Dictionary<string, object> Body(HttpListenerContext c)
        {
            if (c.Request.ContentLength64 > 1000000) throw new PortalError(413, "Too large.");
            using (var r = new StreamReader(c.Request.InputStream, Encoding.UTF8)) { var t = r.ReadToEnd(); return t.Length == 0 ? new Dictionary<string, object>() : Json.Obj(Json.Parse(t)); }
        }

        static void Reply(HttpListenerContext c, int status, object o)
        {
            var b = Encoding.UTF8.GetBytes(Json.Write(o));
            c.Response.StatusCode = status; c.Response.ContentType = "application/json; charset=utf-8"; c.Response.Headers["Cache-Control"] = "no-store";
            c.Response.ContentLength64 = b.Length; c.Response.OutputStream.Write(b, 0, b.Length); c.Response.Close();
        }

        static void Static(HttpListenerContext c, string name, string type)
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("web." + name))
            {
                if (s == null) { c.Response.StatusCode = 404; c.Response.Close(); return; }
                c.Response.ContentType = type;
                c.Response.Headers["Content-Security-Policy"] = "default-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; frame-ancestors 'none'";
                c.Response.Headers["X-Content-Type-Options"] = "nosniff"; c.Response.Headers["Cache-Control"] = "no-cache";
                c.Response.ContentLength64 = s.Length; s.CopyTo(c.Response.OutputStream); c.Response.Close();
            }
        }

        static Dictionary<string, object> Public(Dictionary<string, object> a, bool owner)
        {
            var o = new Dictionary<string, object>();
            foreach (var k in new[] { "id", "company", "contact", "email", "phone", "country", "created", "status", "whiteLabel" }) o[k] = Json.Str(a, k);
            o["brand"] = a.ContainsKey("brand") ? a["brand"] : new Dictionary<string, object>();
            o["servers"] = Json.Arr(a.ContainsKey("servers") ? a["servers"] : null).Select(Json.Obj)
                .Select(s => (object)new Dictionary<string, object> { { "serverId", Json.Str(s, "serverId") }, { "licenseId", Json.Str(s, "licenseId") }, { "edition", Json.Str(s, "edition") }, { "expires", Json.Str(s, "expires") }, { "license", Json.Str(s, "license") } }).ToList();
            return o;
        }

        static string BaseUrl(HttpListenerContext c)
        {
            var host = c.Request.Headers["X-Forwarded-Host"] ?? c.Request.Url.Authority;
            var proto = c.Request.Headers["X-Forwarded-Proto"] ?? c.Request.Url.Scheme;
            return proto + "://" + host;
        }

        /// <summary>/portal, /portal/portal.js, /portal/portal.css, /i18n/… and /portal/api/….</summary>
        public void Handle(HttpListenerContext c, string ip)
        {
            var path = c.Request.Url.AbsolutePath.TrimEnd('/');
            var m = c.Request.HttpMethod;
            try
            {
                if (path == "/portal" && m == "GET") { Static(c, "portal.html", "text/html; charset=utf-8"); return; }
                if (path == "/portal/portal.js") { Static(c, "portal.js", "application/javascript; charset=utf-8"); return; }
                if (path == "/portal/portal.css") { Static(c, "portal.css", "text/css; charset=utf-8"); return; }
                if (path.StartsWith("/i18n/")) { AdminUi.ServeI18n(c, path.Substring(6)); return; }
                if (!path.StartsWith("/portal/api/")) throw new PortalError(404, "Not found.");
                var op = path.Substring(12);
                if (op == "info" && m == "GET") { var st = Load(); Reply(c, 200, new Dictionary<string, object> { { "signupOpen", st.SignupOpen }, { "https", c.Request.IsSecureConnection || c.Request.Headers["X-Forwarded-Proto"] == "https" }, { "trialDays", (long)st.TrialDays } }); return; }
                if (op == "register" && m == "POST")
                {
                    Throttle(ip);
                    var a = Register(Body(c));
                    SysLog.Write(ip, "Admin", "portal: new account " + Json.Str(a, "id") + " " + Json.Str(a, "email"));
                    Reply(c, 200, new Dictionary<string, object> { { "session", NewSession(Json.Str(a, "id")) }, { "owner", false } }); return;
                }
                if (op == "login" && m == "POST")
                {
                    Throttle(ip);
                    var q = Body(c);
                    var email = (Json.Str(q, "email") ?? "").Trim();
                    if (email.Equals("owner", StringComparison.OrdinalIgnoreCase))
                    {
                        var st = Load();
                        if (string.IsNullOrEmpty(st.OwnerHash) || !Verify(Json.Str(q, "password"), st.OwnerHash)) { Failed(ip); throw new PortalError(401, "Wrong e-mail or password."); }
                        Reply(c, 200, new Dictionary<string, object> { { "session", NewSession("owner") }, { "owner", true } }); return;
                    }
                    Dictionary<string, object> acc;
                    lock (gate) acc = ByEmail(email);
                    if (acc == null || !Verify(Json.Str(q, "password"), Json.Str(acc, "password"))) { Failed(ip); throw new PortalError(401, "Wrong e-mail or password."); }
                    if (Json.Str(acc, "status") != "active") throw new PortalError(403, "This account is disabled. Contact the software vendor.");
                    Reply(c, 200, new Dictionary<string, object> { { "session", NewSession(Json.Str(acc, "id")) }, { "owner", false } }); return;
                }
                if (op == "autolicense" && m == "POST")
                {
                    // the server's installation asks for its trial licence with the account's package token
                    Throttle(ip);
                    var q = Body(c);
                    lock (gate)
                    {
                        var acc = Account(Json.Str(q, "account"));
                        if (acc == null || Json.Str(acc, "status") != "active" || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Json.Str(acc, "token") ?? "-"), Encoding.UTF8.GetBytes(Json.Str(q, "token") ?? ""))) { Failed(ip); throw new PortalError(403, "Not allowed."); }
                        var lic = Trial(acc, Json.Str(q, "serverId"));
                        SysLog.Write(ip, "Admin", "portal: licence for " + Json.Str(acc, "id") + " server " + Json.Str(q, "serverId"));
                        Reply(c, 200, new Dictionary<string, object> { { "license", lic } }); return;
                    }
                }
                if ((op == "latestlicense" || op == "update/latest" || op == "update/package") && m == "POST")
                {
                    // LIC-150 / UPD-010: a backup server asks for its newest licence or the newest version. Proof: the
                    // account's package token, or a genuine licence of this server (both are bound to the server ID)
                    Throttle(ip);
                    var q = Body(c);
                    var serverId = (Json.Str(q, "serverId") ?? "").Trim();
                    Dictionary<string, object> acc = null;
                    lock (gate)
                    {
                        var byToken = Account(Json.Str(q, "account"));
                        if (byToken != null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Json.Str(byToken, "token") ?? "-"), Encoding.UTF8.GetBytes(Json.Str(q, "token") ?? ""))) acc = byToken;
                        if (acc == null && License.Check(Json.Str(q, "license"), serverId, clock()).Valid)
                            acc = Accounts().FirstOrDefault(a => Json.Arr(a.ContainsKey("servers") ? a["servers"] : null).Select(Json.Obj).Any(x => Json.Str(x, "serverId") == serverId));
                    }
                    if (acc == null) { Failed(ip); throw new PortalError(403, "Not allowed."); }
                    if (Json.Str(acc, "status") != "active") throw new PortalError(403, "This account is disabled. Contact the software vendor.");
                    if (op == "latestlicense")
                    {
                        var sv = Json.Arr(acc.ContainsKey("servers") ? acc["servers"] : null).Select(Json.Obj).FirstOrDefault(x => Json.Str(x, "serverId") == serverId);
                        Reply(c, 200, new Dictionary<string, object> { { "license", sv == null ? null : Json.Str(sv, "license") } }); return;
                    }
                    var u = UpdatePackage();
                    if (op == "update/latest") { Reply(c, 200, new Dictionary<string, object> { { "version", u.Version }, { "sha256", u.Sha256 }, { "size", u.Size }, { "signature", u.Signature } }); return; }
                    SysLog.Write(ip, "Admin", "portal: update " + u.Version + " for " + Json.Str(acc, "id") + " server " + serverId);
                    c.Response.StatusCode = 200; c.Response.ContentType = "application/zip"; c.Response.ContentLength64 = u.Size;
                    using (var f = File.OpenRead(u.Path)) f.CopyTo(c.Response.OutputStream, 1 << 20);
                    c.Response.Close(); return;
                }
                var who = Who(c);
                if (who == null) throw new PortalError(401, "Please sign in again.");
                if (who == "owner") { Owner(c, op, m, ip); return; }
                Dictionary<string, object> me;
                lock (gate) me = Account(who);
                if (me == null || Json.Str(me, "status") != "active") throw new PortalError(401, "Please sign in again.");
                switch (op)
                {
                    case "me": Reply(c, 200, Public(me, false)); return;
                    case "brand":
                        {
                            if (m != "POST") break;
                            var q = Body(c);
                            var b = new Dictionary<string, object>();
                            foreach (var k in Vendors.BrandAttrs) b[k] = ((Json.Str(q, k) ?? "").Trim());
                            if (((string)b["PRODUCT"]).Length < 2) throw new PortalError(400, "Please enter the product name.");
                            foreach (var ck in new[] { "COLOR", "ACCENT" }) if (((string)b[ck]).Length > 0 && !Regex.IsMatch((string)b[ck], "^#[0-9a-fA-F]{6}$")) throw new PortalError(400, "The color is not valid.");
                            try { Vendors.CheckLogo((string)b["LOGO"]); } catch (ApiException e) { throw new PortalError(400, e.Message); }
                            b["LANGUAGE"] = L.Norm((string)b["LANGUAGE"]);
                            foreach (var k in b.Keys.ToList()) if (k != "LOGO" && ((string)b[k]).Length > 200) b[k] = ((string)b[k]).Substring(0, 200);
                            lock (gate) { me = Account(who); me["brand"] = b; SaveAccount(me); }
                            Reply(c, 200, Public(me, false)); return;
                        }
                    case "license":
                        {
                            if (m != "POST") break;
                            string lic;
                            lock (gate) { me = Account(who); lic = Trial(me, Json.Str(Body(c), "serverId")); }
                            SysLog.Write(ip, "Admin", "portal: licence for " + who);
                            Reply(c, 200, new Dictionary<string, object> { { "license", lic } }); return;
                        }
                    case "password":
                        {
                            if (m != "POST") break;
                            var q = Body(c);
                            if (!Verify(Json.Str(q, "old"), Json.Str(me, "password"))) throw new PortalError(400, "The current password is wrong.");
                            CheckPassword(Json.Str(q, "password"));
                            lock (gate) { me = Account(who); me["password"] = Hash(Json.Str(q, "password")); SaveAccount(me); }
                            Reply(c, 200, new Dictionary<string, object> { { "ok", true } }); return;
                        }
                    case "download/server":
                        {
                            var tmp = Path.Combine(dir, "tmp-" + Guid.NewGuid().ToString("N") + ".zip");
                            try
                            {
                                using (var f = System.IO.File.Create(tmp)) ServerZip(me, BaseUrl(c), f);
                                var name = ClientPackage.SafeName(Brand(me)["PRODUCT"]).Replace(' ', '-') + "-Server.zip";
                                c.Response.StatusCode = 200; c.Response.ContentType = "application/zip"; c.Response.ContentLength64 = new FileInfo(tmp).Length;
                                c.Response.AddHeader("Content-Disposition", "attachment; filename=\"" + name + "\"");
                                using (var f = System.IO.File.OpenRead(tmp)) f.CopyTo(c.Response.OutputStream);
                                c.Response.Close();
                                SysLog.Write(ip, "Admin", "portal: server package for " + who);
                            }
                            finally { try { System.IO.File.Delete(tmp); } catch (Exception) { } }
                            return;
                        }
                    case "download/client":
                        {
                            if (m != "POST") break;
                            var q = Body(c);
                            string fn;
                            var os = Json.Str(q, "os") == "linux" ? "linux" : Json.Str(q, "os") == "mac" ? "mac" : Json.Str(q, "os") == "zip" ? "zip" : "windows";
                            var bytes = Client(me, os, Json.Str(q, "serverUrl"), Json.Str(q, "pin"), out fn);
                            c.Response.StatusCode = 200; c.Response.ContentType = os == "windows" ? "application/vnd.microsoft.portable-executable" : os == "zip" ? "application/zip" : "application/gzip"; c.Response.ContentLength64 = bytes.Length;
                            c.Response.AddHeader("Content-Disposition", "attachment; filename=\"" + fn + "\"");
                            c.Response.OutputStream.Write(bytes, 0, bytes.Length); c.Response.Close();
                            SysLog.Write(ip, "Admin", "portal: " + os + " client for " + who);
                            return;
                        }
                }
                throw new PortalError(404, "Not found.");
            }
            catch (PortalError e) { Reply(c, e.Status, new Dictionary<string, object> { { "message", e.Message } }); }
            catch (Exception e)
            {
                SysLog.Write(ip, "System", "error: portal " + path + ": " + e.GetType().Name + " " + e.Message);
                try { Reply(c, 500, new Dictionary<string, object> { { "message", "Server error. Please try again later." } }); } catch (Exception) { }
            }
        }

        void Owner(HttpListenerContext c, string op, string m, string ip)
        {
            if (op == "owner/accounts" && m == "GET")
            {
                List<object> list; lock (gate) list = Accounts().OrderByDescending(a => Json.Str(a, "created")).Select(a => (object)Public(a, true)).ToList();
                var st = Load();
                Reply(c, 200, new Dictionary<string, object> { { "owner", true }, { "accounts", list }, { "settings", new Dictionary<string, object> {
                    { "signupOpen", st.SignupOpen }, { "trialDays", (long)st.TrialDays }, { "trialUsers", (long)st.TrialUsers }, { "trialComputers", (long)st.TrialComputers },
                    { "trialStorageGB", st.TrialStorageGB }, { "package", st.Package }, { "centerUrl", st.CenterUrl } } } });
                return;
            }
            if (op == "owner/account" && m == "POST")
            {
                var q = Body(c);
                lock (gate)
                {
                    var a = Account(Json.Str(q, "id"));
                    if (a == null) throw new PortalError(404, "Not found.");
                    if (Json.Str(q, "status") == "active" || Json.Str(q, "status") == "disabled") a["status"] = Json.Str(q, "status");
                    if (Json.Str(q, "whiteLabel") == "1" || Json.Str(q, "whiteLabel") == "0") a["whiteLabel"] = Json.Str(q, "whiteLabel");
                    string lic = null;
                    if (!string.IsNullOrEmpty(Json.Str(q, "serverId"))) lic = Issue(a, q); else SaveAccount(a);
                    SysLog.Write(ip, "Admin", "portal owner: account " + Json.Str(a, "id") + (lic != null ? " licence " + Json.Str(q, "serverId") : " " + Json.Str(a, "status")));
                    Reply(c, 200, new Dictionary<string, object> { { "license", lic } }); return;
                }
            }
            if (op == "owner/settings" && m == "POST")
            {
                var q = Body(c);
                lock (gate)
                {
                    var st = Load();
                    if (q.ContainsKey("signupOpen")) st.SignupOpen = Json.Str(q, "signupOpen") == "1" || Json.Str(q, "signupOpen") == "True";
                    if (q.ContainsKey("trialDays")) st.TrialDays = Math.Max(0, (int)Json.Num(q, "trialDays"));
                    if (q.ContainsKey("trialUsers")) st.TrialUsers = Math.Max(0, (int)Json.Num(q, "trialUsers"));
                    if (q.ContainsKey("trialComputers")) st.TrialComputers = Math.Max(0, (int)Json.Num(q, "trialComputers"));
                    if (q.ContainsKey("trialStorageGB")) st.TrialStorageGB = Math.Max(0, Json.Num(q, "trialStorageGB"));
                    Save(st);
                }
                Reply(c, 200, new Dictionary<string, object> { { "ok", true } }); return;
            }
            throw new PortalError(404, "Not found.");
        }
    }
}
