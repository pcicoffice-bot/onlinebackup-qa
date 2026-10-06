using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using OnlineBackup.Core;

namespace OnlineBackup.Server
{
    /// <summary>
    /// Backup users: one folder each under a User Home (&lt;home&gt;\&lt;login&gt;\db\Profile.xml), allocated by the
    /// quota-to-space ratio (Ahsay "Auto User Home Allocation"), devices (agents) and sign-in with lockout and 2FA.
    /// </summary>
    public sealed partial class Users
    {
        static readonly Regex LoginRe = new Regex("^[A-Za-z0-9][A-Za-z0-9_.-]{2,63}$", RegexOptions.Compiled);
        readonly SystemConfig cfg;
        readonly object gate = new object();
        readonly ConcurrentDictionary<string, Session> sessions = new ConcurrentDictionary<string, Session>();

        public sealed class Session { public string Login; public bool Admin; public DateTime Expires; public string Device; public string Vendor = ""; public bool Enroll; public bool Sliding; }

        public Users(SystemConfig cfg) { this.cfg = cfg; }

        /// <summary>Every read-modify-write of a Profile.xml takes this lock (sign-in counters, statistics, settings).</summary>
        public object ProfileLock { get { return gate; } }

        string UsersXml { get { return Path.Combine(cfg.SystemHome, "conf", "users.xml"); } }

        // LOAD-010: users.xml is read once and kept while it does not change (every request asked for it, some 500 times
        // per page); a write here drops the copy at once, a change from outside is seen by its time and size
        readonly object indexGate = new object();
        XDocument indexDoc; DateTime indexTime; long indexLength = -1;
        Dictionary<string, string> dirs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); List<string> logins = new List<string>();

        void FreshIndex()
        {
            var fi = new FileInfo(UsersXml);
            if (indexDoc != null && fi.Exists && fi.LastWriteTimeUtc == indexTime && fi.Length == indexLength) return;
            indexDoc = fi.Exists ? XDocument.Load(UsersXml) : new XDocument(new XElement("USERS"));
            indexTime = fi.Exists ? fi.LastWriteTimeUtc : DateTime.MinValue; indexLength = fi.Exists ? fi.Length : -1;
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); var l = new List<string>();
            foreach (var x in indexDoc.Root.Elements("USER")) { var n = (string)x.Attribute("LOGIN"); if (n == null || d.ContainsKey(n)) continue; d[n] = Path.Combine((string)x.Attribute("HOME"), n); l.Add(n); }
            dirs = d; logins = l;
        }

        /// <summary>A copy of users.xml to read or to change and write back (with WriteIndex).</summary>
        XDocument Index() { lock (indexGate) { FreshIndex(); return new XDocument(indexDoc); } }

        void WriteIndex(XDocument idx) { lock (indexGate) { Atomic.WriteText(UsersXml, idx.ToString()); indexDoc = null; } }

        public string UserDir(string login)
        {
            if (login == null || !LoginRe.IsMatch(login)) throw new ApiException(400, "BAD_LOGIN", "Invalid user name.");
            string dir;
            lock (indexGate) { FreshIndex(); if (!dirs.TryGetValue(login, out dir)) throw new ApiException(404, "NO_USER", "The user does not exist."); }
            return dir;
        }

        public List<string> Logins() { lock (indexGate) { FreshIndex(); return new List<string>(logins); } }

        public Profile LoadProfile(string login) { return Profile.Load(Path.Combine(UserDir(login), "db", "Profile.xml")); }
        public void SaveProfile(string login, Profile p) { p.Save(Path.Combine(UserDir(login), "db", "Profile.xml")); }

        /// <summary>QPS of every user home: allocated quota / disk size, against its maximum.</summary>
        public List<Msg> HomesReport()
        {
            var idx = Index();
            return cfg.Homes.Select(h =>
            {
                long allocated = idx.Root.Elements("USER").Where(u => string.Equals((string)u.Attribute("HOME"), h.Path, StringComparison.OrdinalIgnoreCase))
                    .Sum(u => long.Parse((string)u.Attribute("QUOTA") ?? "0", CultureInfo.InvariantCulture));
                double ratio = h.CapacityBytes > 0 ? allocated * 100.0 / h.CapacityBytes : 0;
                long free = 0;
                try { free = new DriveInfo(Path.GetPathRoot(h.Path)).AvailableFreeSpace; } catch { }
                return new Msg().Set("path", h.Path).Set("maxQps", h.MaxQps).Set("allocated", allocated).Set("capacity", (long)h.CapacityBytes)
                    .Set("ratio", ratio.ToString("0.0", CultureInfo.InvariantCulture)).Set("free", free);
            }).ToList();
        }

        string AllocateHome(long quota)
        {
            var best = HomesReport()
                .Where(h => h["maxQps"] != "NOT_USED")
                .Where(h =>
                {
                    if (h["maxQps"] == "UNLIMITED") return true;
                    double max, cap = h.Long("capacity");
                    if (!double.TryParse(h["maxQps"], NumberStyles.Float, CultureInfo.InvariantCulture, out max) || cap <= 0) return false;
                    return (h.Long("allocated") + quota) * 100.0 / cap <= max;
                })
                .OrderBy(h => double.Parse(h["ratio"], CultureInfo.InvariantCulture)).FirstOrDefault();
            if (best == null) throw new ApiException(507, "NO_HOME", "No user folder has room under the maximum quota ratio.");
            return best["path"];
        }

        /// <summary>New backup user from the default-values template (policy), in the home with the lowest QPS ratio.</summary>
        public Profile Create(string login, string password, string alias, long? quotaBytes, string quotaType, string email, string ip)
        {
            if (login == null || !LoginRe.IsMatch(login)) throw new ApiException(400, "BAD_LOGIN", "Invalid user name (3–64 characters: Latin letters, digits, dot, hyphen).");
            if (!Passwords.Ok(password)) throw new ApiException(400, "WEAK_PASSWORD", Passwords.Rule);
            lock (gate)
            {
                var idx = Index();
                if (idx.Root.Elements("USER").Any(x => string.Equals((string)x.Attribute("LOGIN"), login, StringComparison.OrdinalIgnoreCase)))
                    throw new ApiException(409, "EXISTS", "The user already exists.");
                var pol = cfg.Policy().Element("USER");
                long quota = quotaBytes ?? (long)(double.Parse((string)pol.Attribute("QUOTA_GB") ?? "50", CultureInfo.InvariantCulture) * 1024 * 1024 * 1024);
                var home = AllocateHome(quota);
                var p = Profile.Create(login, alias, PasswordHash.Create(password), (string)pol.Attribute("LANGUAGE"), (string)pol.Attribute("TIMEZONE"));
                p.SetAttr("QUOTA", quota);
                p.SetAttr("QUOTA_TYPE", quotaType ?? (string)pol.Attribute("QUOTA_TYPE") ?? "COMPRESSED");
                p.SetAttr("MAX_BACKUP_SET", (string)pol.Attribute("MAX_BACKUP_SET") ?? "10");
                p.SetAttr("SAVE_ENCRYPT_KEY", (string)pol.Attribute("SAVE_ENCRYPT_KEY") ?? "Y");
                p.SetAttr("REQUIRE_TOTP", (string)pol.Attribute("REQUIRE_TOTP") ?? "N");
                foreach (var r in SetControl.Rights) if (pol.Attribute(r[0]) != null) p.SetAttr(r[0], (string)pol.Attribute(r[0]));   // DEF-010: what a new customer may change
                if (!string.IsNullOrEmpty(email)) p.AddContact(alias ?? login, email);
                var dir = Path.Combine(home, login);
                Directory.CreateDirectory(Path.Combine(dir, "db"));
                Directory.CreateDirectory(Path.Combine(dir, "files"));
                Directory.CreateDirectory(Path.Combine(dir, "logs"));
                p.Save(Path.Combine(dir, "db", "Profile.xml"));
                idx.Root.Add(new XElement("USER", new XAttribute("LOGIN", login), new XAttribute("HOME", home), new XAttribute("QUOTA", quota)));
                WriteIndex(idx);
                SysLog.Write(ip, "Admin", "user created " + login + " home=" + home + " quota=" + quota);
                return p;
            }
        }

        /// <summary>Second server: the user's folder is created in one of this server's own homes; the profile follows from the first server.</summary>
        public void EnsureReplicaUser(string login, long quota, string ip)
        {
            if (login == null || !LoginRe.IsMatch(login)) throw new ApiException(400, "BAD_LOGIN", "Invalid user name.");
            lock (gate)
            {
                var idx = Index();
                if (idx.Root.Elements("USER").Any(x => string.Equals((string)x.Attribute("LOGIN"), login, StringComparison.OrdinalIgnoreCase))) return;
                var home = AllocateHome(quota);
                foreach (var d in new[] { "db", "files", "logs" }) Directory.CreateDirectory(Path.Combine(home, login, d));
                idx.Root.Add(new XElement("USER", new XAttribute("LOGIN", login), new XAttribute("HOME", home), new XAttribute("QUOTA", quota)));
                WriteIndex(idx);
                SysLog.Write(ip, "Admin", "replica user created " + login + " home=" + home);
            }
        }

        public void UpdateQuotaIndex(string login, long quota)
        {
            lock (gate)
            {
                var idx = Index();
                var e = idx.Root.Elements("USER").First(x => string.Equals((string)x.Attribute("LOGIN"), login, StringComparison.OrdinalIgnoreCase));
                e.SetAttributeValue("QUOTA", quota);
                WriteIndex(idx);
            }
        }

        // ---------------------------------------------------------------- sign-in

        /// <summary>
        /// Password + (if enabled) a one-time code. Wrong passwords and wrong codes count together toward the automatic
        /// lock (default 3 attempts, Ahsay "Auto Lock User").
        /// </summary>
        public Profile CheckUser(string login, string password, string otp, string ip)
        {
            Guard.Tried = login;
            lock (gate)
            {
                Profile p;
                try { p = LoadProfile(login); }
                catch (ApiException) { SysLog.Write(ip, "Access", "login failed (unknown user) " + login); throw new ApiException(401, "LOGIN", "Wrong user name or password."); }
                CheckIp(p, ip);
                int lockAttempts = LockAttempts(p), lockMinutes = LockMinutes(p);
                long locked = p.GetLong("USER_LOCKED_TIME");
                if (locked > 0 && (lockMinutes == 0 || RunId.FromUnixMs(locked).AddMinutes(lockMinutes) > SystemClock.UtcNow))
                {
                    SysLog.Write(ip, "Access", "login refused (locked) " + login);
                    throw new ApiException(423, "LOCKED", "The user is locked after failed sign-ins. Contact your provider or try again later.");
                }
                if (p.Get("STATUS") != "ENABLE" || p.Get("DISABLED") == "Y") throw new ApiException(403, "SUSPENDED", "The user is suspended.");
                bool ok = PasswordHash.Verify(password, p.Get("HASHED_PWD"));
                var secret = p.Get("TOTP_SECRET");
                if (ok && !string.IsNullOrEmpty(secret))
                {
                    if (string.IsNullOrEmpty(otp)) throw new ApiException(401, "OTP_REQUIRED", "A verification code is required.");
                    ok = Totp.Verify(secret, otp, DateTime.UtcNow /* real time: the phone's code */) || UseBackupCode(p, otp);
                }
                if (!ok)
                {
                    var fails = p.GetLong("LOGIN_FAILURE_COUNT") + 1;
                    p.SetAttr("LOGIN_FAILURE_COUNT", fails);
                    if (fails >= lockAttempts) { p.SetAttr("USER_LOCKED_TIME", RunId.UnixMs(SystemClock.UtcNow)); p.SetAttr("LOGIN_FAILURE_COUNT", 0); }
                    SaveProfile(login, p);
                    SysLog.Write(ip, "Access", "login failed " + login + (fails >= lockAttempts ? " → locked" + (lockMinutes == 0 ? " until an administrator unlocks" : " for " + lockMinutes + " minutes") : ""));
                    throw new ApiException(401, "LOGIN", "Wrong user name, password or code.");
                }
                p.SetAttr("LOGIN_FAILURE_COUNT", 0); p.SetAttr("USER_LOCKED_TIME", -1); p.SetAttr("LAST_LOGIN", RunId.UnixMs(SystemClock.UtcNow));
                SaveProfile(login, p);
                SysLog.Write(ip, "Access", "login ok " + login);
                return p;
            }
        }

        // SEC-020: the customer's own lock policy (empty = the server's): attempts, and minutes (0 = until an administrator unlocks)
        // SEC-040: the lock after wrong passwords cannot be switched off or weakened (owner's decision): at most
        // MaxLockAttempts attempts, locked for at least MinLockMinutes (0 = until an administrator unlocks) — enforced here,
        // where it is used, so no setting (old or edited by hand) gets around it
        public const int MaxLockAttempts = 10, MinLockMinutes = 5;
        public static int ClampAttempts(long v) { return (int)Math.Max(1, Math.Min(MaxLockAttempts, v)); }
        public static int ClampMinutes(long v) { return v == 0 ? 0 : (int)Math.Max(MinLockMinutes, Math.Min(100000, v)); }
        int LockAttempts(Profile p) { var v = p.GetLong("LOCK_ATTEMPTS"); return ClampAttempts(v > 0 ? v : cfg.AutoLockAttempts); }
        int LockMinutes(Profile p) { var s = p.Get("LOCK_MINUTES"); int v; return ClampMinutes(!string.IsNullOrEmpty(s) && int.TryParse(s, out v) && v >= 0 ? v : cfg.LockMinutes); }

        /// <summary>
        /// SEC-030: fixed addresses — when the customer has ALLOWED_IPS (addresses or ranges such as 192.0.2.10, 198.51.100.0/24),
        /// sign-in, backup, restore and the web restore are accepted only from them. Empty = from anywhere.
        /// </summary>
        public void CheckIp(Profile p, string ip)
        {
            var list = (p.Get("ALLOWED_IPS") ?? "").Split(new[] { ',', ' ', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            if (list.Length == 0) return;
            if (IpAllowed(list, ip)) return;
            SysLog.Write(ip, "Access", "refused (address not allowed) " + p.Get("LOGIN_NAME"));
            throw new ApiException(403, "IP_NOT_ALLOWED", "Access from this address is not allowed for this user.");
        }

        public static bool IpAllowed(IEnumerable<string> list, string ip)
        {
            System.Net.IPAddress a;
            if (!System.Net.IPAddress.TryParse(ip ?? "", out a)) return false;
            if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
            foreach (var e in list)
            {
                var parts = e.Trim().Split('/');
                System.Net.IPAddress n;
                if (!System.Net.IPAddress.TryParse(parts[0], out n)) continue;
                if (n.IsIPv4MappedToIPv6) n = n.MapToIPv4();
                if (n.AddressFamily != a.AddressFamily) continue;
                var ab = a.GetAddressBytes(); var nb = n.GetAddressBytes();
                int bits = parts.Length > 1 && int.TryParse(parts[1], out bits) ? Math.Max(0, Math.Min(bits, ab.Length * 8)) : ab.Length * 8;
                bool match = true;
                for (int i = 0; i < ab.Length && match; i++)
                {
                    int take = Math.Max(0, Math.Min(8, bits - i * 8));
                    if (take == 0) break;
                    int mask = (0xFF << (8 - take)) & 0xFF;
                    match = (ab[i] & mask) == (nb[i] & mask);
                }
                if (match) return true;
            }
            return false;
        }

        /// <summary>A valid list of addresses / ranges (one per line or comma separated), normalised; else an error naming the wrong one.</summary>
        public static string NormalizeIps(string text)
        {
            var items = (text ?? "").Split(new[] { ',', ' ', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Distinct().ToList();
            foreach (var x in items)
            {
                var parts = x.Split('/'); System.Net.IPAddress n; int bits;
                if (parts.Length > 2 || !System.Net.IPAddress.TryParse(parts[0], out n) || (parts.Length == 2 && (!int.TryParse(parts[1], out bits) || bits < 0 || bits > (n.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128))))
                    throw new ApiException(400, "IP", "Not a valid address or range: " + x);
            }
            return string.Join(",", items);
        }

        bool UseBackupCode(Profile p, string code)
        {
            var codes = (p.Get("TOTP_BACKUP_CODES") ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            foreach (var h in codes)
                if (PasswordHash.Verify(code.Trim(), h)) { codes.Remove(h); p.SetAttr("TOTP_BACKUP_CODES", string.Join(",", codes)); return true; }
            return false;
        }

        /// <summary>Two-factor setup: returns the secret (for the authenticator app) and 10 one-time backup codes, shown once.</summary>
        public Msg EnableTotp(string login, string ip)
        {
            lock (gate)
            {
                var p = LoadProfile(login);
                var secret = Totp.NewSecret();
                var codes = Enumerable.Range(0, 10).Select(i => Bytes.Hex(Bytes.Random(5))).ToList();
                p.SetAttr("TOTP_PENDING", secret);
                p.SetAttr("TOTP_PENDING_CODES", string.Join(",", codes.Select(c => PasswordHash.Create(c, 20000))));
                SaveProfile(login, p);
                SysLog.Write(ip, "Access", "2fa setup started " + login);
                // the app shows the IT company's product name beside the user name
                var issuer = Vendors.Brand(cfg, p.Get("OWNER"), "PRODUCT", "ITSguard Server Online");
                var m = new Msg().Set("secret", secret).Set("uri", "otpauth://totp/" + Uri.EscapeDataString(issuer) + ":" + Uri.EscapeDataString(login) + "?secret=" + secret + "&issuer=" + Uri.EscapeDataString(issuer));
                foreach (var c in codes) m.Add("codes", new Msg().Set("code", c));
                return m;
            }
        }

        public void ConfirmTotp(string login, string code, string ip)
        {
            lock (gate)
            {
                var p = LoadProfile(login);
                var pending = p.Get("TOTP_PENDING");
                if (string.IsNullOrEmpty(pending) || !Totp.Verify(pending, code, DateTime.UtcNow /* real time: the phone's code */)) throw new ApiException(400, "OTP", "The code is wrong.");
                p.SetAttr("TOTP_SECRET", pending); p.SetAttr("TOTP_BACKUP_CODES", p.Get("TOTP_PENDING_CODES"));
                p.SetAttr("TOTP_PENDING", ""); p.SetAttr("TOTP_PENDING_CODES", "");
                SaveProfile(login, p);
                SysLog.Write(ip, "Access", "2fa enabled " + login);
            }
        }

        /// <summary>Lost phone: the administrator resets the user's two-factor (logged).</summary>
        public void ResetTotp(string login, string admin, string ip)
        {
            lock (gate)
            {
                var p = LoadProfile(login);
                p.SetAttr("TOTP_SECRET", ""); p.SetAttr("TOTP_BACKUP_CODES", "");
                SaveProfile(login, p);
                SysLog.Write(ip, "Admin", admin + " reset 2fa of " + login);
            }
        }

        public void Unlock(string login, string admin, string ip)
        {
            lock (gate)
            {
                var p = LoadProfile(login);
                p.SetAttr("USER_LOCKED_TIME", -1); p.SetAttr("LOGIN_FAILURE_COUNT", 0);
                SaveProfile(login, p);
                SysLog.Write(ip, "Admin", admin + " unlocked " + login);
            }
        }

        /// <summary>System administrator → "" ; a vendor's administrator → the vendor id (VND-040). Throws when wrong.</summary>
        // ---------------------------------------------------------------- sessions and devices

        public string NewSession(string login, bool admin, string device = null, string vendor = "")
        {
            var token = Bytes.Hex(Bytes.Random(24));
            sessions[token] = new Session { Login = login, Admin = admin, Device = device, Vendor = vendor ?? "", Expires = SystemClock.UtcNow.AddHours(admin ? 2 : 12) };
            return token;
        }

        /// <summary>TECH-010: an administrator's session, and whether two-step must be set up first.</summary>
        public string NewStaffSession(Staff.SignIn si) { return NewStaffSession(si, false); }

        /// <summary>SEC-110 (owner): on the server itself the sign-in stays 30 days (the browser keeps it); elsewhere 2 hours.</summary>
        public string NewStaffSession(Staff.SignIn si, bool local)
        {
            var token = Bytes.Hex(Bytes.Random(24));
            // SEC-130 (owner: "after the update it went back to the sign-in again" — from outside): every administrator sign-in
            // outlives a restart; away from the server it ends after 2 hours without use (sliding), on the server after 30 days
            var s = new Session { Login = si.Account.Login, Admin = true, Vendor = si.Account.Vendor ?? "", Enroll = si.Enroll, Expires = SystemClock.UtcNow.AddHours(local ? 24 * 30 : 2), Sliding = !local };
            sessions[token] = s;
            if (!si.Enroll) Keep(token, s);
            return token;
        }

        // SEC-120 (owner: "not back to the sign-in after every update"): the server's own long sign-ins outlive a restart —
        // kept as a hash of the token (never the token) in conf/kept-sessions.xml, only the server's administrators can read it
        static readonly object keptGate = new object();
        string KeptPath { get { return Path.Combine(cfg.SystemHome, "conf", "kept-sessions.xml"); } }
        static string Hash(string token) { using (var h = System.Security.Cryptography.SHA256.Create()) return Bytes.Hex(h.ComputeHash(System.Text.Encoding.UTF8.GetBytes(token))); }

        void Keep(string token, Session s)
        {
            lock (keptGate)
                try
                {
                    var doc = File.Exists(KeptPath) ? XDocument.Load(KeptPath) : new XDocument(new XElement("KEPT"));
                    var h = Hash(token);
                    doc.Root.Elements("S").Where(e => (long)e.Attribute("EXPIRES") < RunId.UnixMs(SystemClock.UtcNow) || (string)e.Attribute("HASH") == h).Remove();
                    doc.Root.Add(new XElement("S", new XAttribute("HASH", h), new XAttribute("LOGIN", s.Login), new XAttribute("VENDOR", s.Vendor ?? ""), new XAttribute("EXPIRES", RunId.UnixMs(s.Expires)), new XAttribute("SLIDING", s.Sliding ? "Y" : "N")));
                    Atomic.WriteText(KeptPath, doc.ToString());
                }
                catch (Exception) { }
        }

        Session Kept(string token)
        {
            lock (keptGate)
                try
                {
                    if (!File.Exists(KeptPath)) return null;
                    var h = Hash(token);
                    var e = XDocument.Load(KeptPath).Root.Elements("S").FirstOrDefault(x => (string)x.Attribute("HASH") == h);
                    if (e == null || (long)e.Attribute("EXPIRES") < RunId.UnixMs(SystemClock.UtcNow)) return null;
                    if (Staff.Find(cfg, (string)e.Attribute("LOGIN")) == null) return null;   // the administrator was removed meanwhile
                    return new Session { Login = (string)e.Attribute("LOGIN"), Admin = true, Vendor = (string)e.Attribute("VENDOR") ?? "", Sliding = (string)e.Attribute("SLIDING") == "Y", Expires = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds((long)e.Attribute("EXPIRES")) };
                }
                catch (Exception) { return null; }
        }

        /// <summary>SEC-130: "Sign out" ends the sign-in on the server too (also the kept copy).</summary>
        public void EndSession(string token)
        {
            if (string.IsNullOrEmpty(token)) return;
            Session gone; sessions.TryRemove(token, out gone);
            lock (keptGate)
                try
                {
                    if (!File.Exists(KeptPath)) return;
                    var doc = XDocument.Load(KeptPath); var h = Hash(token);
                    doc.Root.Elements("S").Where(e => (string)e.Attribute("HASH") == h).Remove();
                    Atomic.WriteText(KeptPath, doc.ToString());
                }
                catch (Exception) { }
        }

        public Session GetSession(string token)
        {
            Session s;
            if (token == null) return null;
            if (!sessions.TryGetValue(token, out s)) { s = Kept(token); if (s != null) sessions[token] = s; }
            if (s == null || s.Expires < SystemClock.UtcNow) return null;
            // SEC-130: in use → 2 more hours (written down at most every 10 minutes, so a restart keeps it)
            if (s.Admin && s.Sliding && s.Expires < SystemClock.UtcNow.AddMinutes(110)) { s.Expires = SystemClock.UtcNow.AddHours(2); Keep(token, s); }
            return s;
        }

        /// <summary>
        /// Registers a computer to a user (password + 2FA). The agent keeps the returned device token (DPAPI on Windows)
        /// and uses it for scheduled runs, so a scheduled backup never needs a code. Revoking one device leaves the others.
        /// </summary>
        /// <summary>Computers with an active registration, over all users (one per user + computer name).</summary>
        public int ActiveComputers()
        {
            int n = 0;
            foreach (var l in Logins())
            {
                var p = Path.Combine(UserDir(l), "db", "devices.xml");
                if (!File.Exists(p)) continue;
                n += XDocument.Load(p).Root.Elements("DEVICE").Where(d => (string)d.Attribute("REVOKED") != "Y")
                    .Select(d => ((string)d.Attribute("NAME") ?? "").ToUpperInvariant()).Distinct().Count();
            }
            return n;
        }

        public string RegisterDevice(string login, string computer, string ip)
        {
            lock (gate)
            {
                var path = Path.Combine(UserDir(login), "db", "devices.xml");
                var doc = File.Exists(path) ? XDocument.Load(path) : new XDocument(new XElement("DEVICES"));
                // LIC-016: the licence counts the computers backed up (the same computer registering again is not a new one)
                var lic = cfg.License;
                bool again = doc.Root.Elements("DEVICE").Any(d => (string)d.Attribute("REVOKED") != "Y" && string.Equals((string)d.Attribute("NAME"), computer ?? "", StringComparison.OrdinalIgnoreCase));
                if (lic.MaxDevices > 0 && !again && ActiveComputers() >= lic.MaxDevices)
                    throw new ApiException(402, "LICENSE", "You have reached the number of computers in the licence (" + lic.MaxDevices + "). To upgrade, contact the software vendor.");
                var secret = Bytes.Hex(Bytes.Random(32));
                var id = RunId.UnixMs(SystemClock.UtcNow).ToString(CultureInfo.InvariantCulture);
                doc.Root.Add(new XElement("DEVICE", new XAttribute("ID", id), new XAttribute("NAME", computer ?? ""), new XAttribute("TOKEN_HASH", Bytes.Hex(Bytes.Sha256(Encoding.UTF8.GetBytes(secret)))),
                    new XAttribute("CREATED", id), new XAttribute("REVOKED", "N")));
                Atomic.WriteText(path, doc.ToString());
                SysLog.Write(ip, "Access", "device registered " + login + " computer=" + computer);
                return Convert.ToBase64String(Encoding.UTF8.GetBytes(login)) + "." + id + "." + secret;
            }
        }

        public string CheckDevice(string token, string ip, string agent = null)
        {
            var p = (token ?? "").Split('.');
            if (p.Length != 3) throw new ApiException(401, "DEVICE", "Unknown device.");
            string login;
            try { login = Encoding.UTF8.GetString(Convert.FromBase64String(p[0])); } catch (FormatException) { throw new ApiException(401, "DEVICE", "Unknown device."); }
            var path = Path.Combine(UserDir(login), "db", "devices.xml");
            if (!File.Exists(path)) throw new ApiException(401, "DEVICE", "Unknown device.");
            var hash = Bytes.Hex(Bytes.Sha256(Encoding.UTF8.GetBytes(p[2])));
            var dev = XDocument.Load(path).Root.Elements("DEVICE").FirstOrDefault(d => (string)d.Attribute("ID") == p[1] && (string)d.Attribute("TOKEN_HASH") == hash && (string)d.Attribute("REVOKED") != "Y");
            if (dev == null) { SysLog.Write(ip, "Access", "device refused " + login); throw new ApiException(401, "DEVICE", "The device was revoked or is unknown."); }
            ComputerSeen(path, p[1], ip, agent, SystemClock.UtcNow);
            var prof = LoadProfile(login);
            if (prof.Get("STATUS") != "ENABLE" || prof.Get("DISABLED") == "Y") throw new ApiException(403, "SUSPENDED", "The user is suspended.");
            CheckIp(prof, ip);
            return login;
        }

        // ---------------------------------------------------------------- sets

        /// <summary>Adds a backup set (up to MAX_BACKUP_SET, 10 by default). Defaults come from the policy template.</summary>
        public BackupSetInfo CreateSet(string login, BackupSetInfo s, string ip)
        {
            lock (gate)
            {
                var p = LoadProfile(login);
                int max = (int)Math.Max(1, p.GetLong("MAX_BACKUP_SET"));
                if (p.SetElements.Count() >= max) throw new ApiException(409, "SET_LIMIT", "Maximum number of backup sets reached (" + max + ").");
                if (s.Type != "FILE" && s.Type != "MSSQL" && s.Type != "SYSTEMSTATE" && s.Type != "BAREMETAL" && s.Type != "M365" && s.Type != "MYSQL" && s.Type != "POSTGRESQL" && s.Type != "HYPERV" && s.Type != "GWS" && s.Type != "VMWARE" && s.Type != "ORACLE" && s.Type != "DOMINO")
                    throw new ApiException(400, "SET_TYPE", "Unsupported backup set type (files, MSSQL, MySQL, PostgreSQL, Oracle, Domino, System State, whole computer, Hyper-V, VMware, Microsoft 365, Google Workspace).");
                if (s.Type == "GWS" && s.Engine != "RESTIC") throw new ApiException(400, "GWS", "Google Workspace backup requires the restic engine.");
                // LIC-030: modules of the licence (the free edition: files and System State)
                var lic = cfg.License;
                if (!lic.Has(s.Type)) throw new ApiException(402, "LICENSE", "The server's licence does not include " + s.Type + " backup (" + (lic.Valid ? lic.Edition : "free edition") + ").");
                if (s.Type == "M365" && (s.Engine != "RESTIC" || string.IsNullOrEmpty(s.M365Tenant) || string.IsNullOrEmpty(s.M365ClientId))) throw new ApiException(400, "M365", "Microsoft 365 backup requires the restic engine, a tenant id and an application id.");
                if (s.DestMode == "LOCAL" && s.Engine != "RESTIC") throw new ApiException(400, "DEST", "A local-only backup needs the restic engine (Windows 10 / Server 2016 or later, Linux, Mac).");
                if ((s.DestMode == "BOTH" || s.DestMode == "LOCAL") && string.IsNullOrWhiteSpace(s.LocalCopyPath)) throw new ApiException(400, "DEST", "Choose the local disk or network folder.");
                if (s.DestMode == "BOTH") s.LocalCopy = true;
                bool allByDefault = s.Type == "MYSQL" || s.Type == "POSTGRESQL" || s.Type == "HYPERV" || s.Type == "VMWARE" || s.Type == "DOMINO";   // none chosen = every database / VM
                if (s.Sources.Count == 0 && !allByDefault) throw new ApiException(400, "NO_SOURCE", "No folders were chosen for backup.");
                var pol = cfg.Policy();
                var ps = pol.Element("BACKUP_SET");
                long id = RunId.UnixMs(SystemClock.UtcNow);
                while (p.FindSet(id.ToString(CultureInfo.InvariantCulture)) != null) id++;
                s.Id = id.ToString(CultureInfo.InvariantCulture);
                NewDefaults.ApplyToNewSet(pol, s);   // DEF-010
                foreach (var gf in pol.Elements("GLOBAL_FILTER"))
                    s.Filters.Add(new FilterRule { Type = (string)gf.Attribute("TYPE"), ApplyDir = (string)gf.Attribute("APPLY_DIR") == "Y", ApplyFile = (string)gf.Attribute("APPLY_FILE") == "Y", Patterns = gf.Elements("PATTERN").Select(x => x.Value).ToList() });
                p.Root.Add(s.ToXml());
                SaveProfile(login, p);
                SysLog.Write(ip, "Admin", "set created " + login + "/" + s.Id + " " + s.Name);
                return s;
            }
        }
    }
}
