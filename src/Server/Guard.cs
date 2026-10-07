using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Xml.Linq;
using OnlineBackup.Core;

namespace OnlineBackup.Server
{
    /// <summary>
    /// GUARD-010 (from the ITSguard CRM's web security, WCH): an address that guesses passwords or scans the server is
    /// blocked — every request from it is refused for BLOCK_HOURS (24 by default):
    ///  · FAILS wrong sign-ins (10) from it within 10 minutes — administrators, customers, the restore website;
    ///  · wrong sign-ins for USERS different names (4) from it within 10 minutes (password spraying);
    ///  · PROBES refused requests (30) from it within 10 minutes — unknown addresses, wrong device tokens (scanning).
    /// The office's own network (private addresses), this server and the addresses in TRUSTED are never blocked.
    /// Alerts by mail to the administrators (the "E-mails and alerts" contacts), at most once an hour per address:
    /// an address blocked; one user name attacked from 3 or more addresses; an administrator signed in after 3 or more
    /// failures, from a new address (30 days) or at night (00:00–06:00 server time).
    /// &lt;system&gt;\conf\guard.xml: &lt;GUARD&gt;&lt;BLOCK IP UNTIL REASON BY/&gt;&lt;SEEN LOGIN IP TIME/&gt;&lt;/GUARD&gt;; the settings: &lt;GUARD .../&gt; in system.xml.
    /// Passwords are never written anywhere — only the address, the name tried and the time.
    /// </summary>
    public static class Guard
    {
        public const int Window = 10;   // minutes
        static readonly object gate = new object();
        static readonly Dictionary<string, List<KeyValuePair<DateTime, string>>> fails = new Dictionary<string, List<KeyValuePair<DateTime, string>>>();
        static readonly Dictionary<string, List<DateTime>> probes = new Dictionary<string, List<DateTime>>();
        static readonly Dictionary<string, DateTime> alerted = new Dictionary<string, DateTime>();
        static readonly Dictionary<string, Dictionary<string, XElement>> blockedBy = new Dictionary<string, Dictionary<string, XElement>>();   // per server (System Home), from guard.xml
        static string K(SystemConfig cfg, string ip) { return cfg.SystemHome + "|" + ip; }
        public static Func<DateTime> Clock = () => SystemClock.UtcNow;   // tests
        public static Action<SystemConfig, string, string> Mail;        // (server, subject, html) — set by the server; tests catch it

        /// <summary>The user name tried by this request (set by the sign-in code, read when it fails).</summary>
        [ThreadStatic] public static string Tried;

        public sealed class Settings
        {
            public bool On = true, TrustPrivate = true; public int BlockHours = 24, Fails = 10, Users = 4, Probes = 30; public List<string> Trusted = new List<string>();
            /// <summary>SEC-100 (owner): fixed addresses where an administrator signs in without the code (password still needed).</summary>
            public List<string> NoCode = new List<string>();
        }

        public static Settings Read(SystemConfig cfg)
        {
            var e = cfg.Doc.Root.Element("GUARD"); var s = new Settings();
            if (e == null) return s;
            Func<string, int, int, int, int> n = (a, d, lo, hi) => { int v; return int.TryParse((string)e.Attribute(a), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? Math.Max(lo, Math.Min(hi, v)) : d; };
            s.On = (string)e.Attribute("ON") != "N"; s.TrustPrivate = (string)e.Attribute("TRUST_PRIVATE") != "N";
            s.BlockHours = n("BLOCK_HOURS", 24, 1, 24 * 365); s.Fails = n("FAILS", 10, 3, 100); s.Users = n("USERS", 4, 2, 50); s.Probes = n("PROBES", 30, 10, 1000);
            s.Trusted = ((string)e.Attribute("TRUSTED") ?? "").Split(new[] { ',', ';', ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            s.NoCode = ((string)e.Attribute("NO_CODE") ?? "").Split(new[] { ',', ';', ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            return s;
        }

        public static void Save(SystemConfig cfg, Settings s, string by, string ip)
        {
            s.Trusted = Users.NormalizeIps(string.Join(",", s.Trusted)).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            s.NoCode = Users.NormalizeIps(string.Join(",", s.NoCode)).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            lock (cfg)
            {
                var e = cfg.Doc.Root.Element("GUARD"); if (e == null) { e = new XElement("GUARD"); cfg.Doc.Root.Add(e); }
                e.SetAttributeValue("ON", s.On ? "Y" : "N"); e.SetAttributeValue("BLOCK_HOURS", Math.Max(1, s.BlockHours)); e.SetAttributeValue("FAILS", Math.Max(3, s.Fails));
                e.SetAttributeValue("USERS", Math.Max(2, s.Users)); e.SetAttributeValue("PROBES", Math.Max(10, s.Probes)); e.SetAttributeValue("TRUSTED", string.Join(", ", s.Trusted)); e.SetAttributeValue("NO_CODE", s.NoCode.Count == 0 ? null : string.Join(", ", s.NoCode));
                if (!s.TrustPrivate) e.SetAttributeValue("TRUST_PRIVATE", "N"); else e.SetAttributeValue("TRUST_PRIVATE", null);
                cfg.Save();
            }
            SysLog.Write(ip, "Admin", by + " changed the blocking of attacking addresses: " + (s.On ? "on, " + s.Fails + " failures / " + s.Users + " names / " + s.Probes + " refused requests in " + Window + " minutes, blocked " + s.BlockHours + " hours" : "off"));
        }

        /// <summary>SEC-100: an administrator signing in from one of these addresses is not asked for the code.</summary>
        public static bool NoCode(SystemConfig cfg, string ip) { var l = Read(cfg).NoCode; return l.Count > 0 && Users.IpAllowed(l, ip); }

        /// <summary>Never blocked: this computer, the office's network, the trusted list.</summary>
        public static bool Trusted(SystemConfig cfg, string ip)
        {
            IPAddress a; if (!IPAddress.TryParse(ip ?? "", out a)) return true;
            var s = Read(cfg);
            if (s.Trusted.Count > 0 && Users.IpAllowed(s.Trusted, ip)) return true;
            if (!s.TrustPrivate) return false;
            if (IPAddress.IsLoopback(a)) return true;
            if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
            var b = a.GetAddressBytes();
            if (a.AddressFamily == AddressFamily.InterNetwork)
                return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
            return a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || (b[0] & 0xFE) == 0xFC;
        }

        static string FilePath(SystemConfig cfg) { return Path.Combine(cfg.SystemHome, "conf", "guard.xml"); }

        static Dictionary<string, XElement> Blocked(SystemConfig cfg)
        {
            Dictionary<string, XElement> blocked;
            if (blockedBy.TryGetValue(cfg.SystemHome, out blocked)) return blocked;
            blockedBy[cfg.SystemHome] = blocked = new Dictionary<string, XElement>();
            try { if (File.Exists(FilePath(cfg))) foreach (var e in OnlineBackup.Core.Atomic.LoadXml(FilePath(cfg)).Root.Elements("BLOCK")) blocked[(string)e.Attribute("IP")] = e; } catch (Exception) { }
            return blocked;
        }

        static void Write(SystemConfig cfg)
        {
            try { Atomic.WriteText(FilePath(cfg), new XElement("GUARD", Blocked(cfg).Values.Select(e => new XElement(e))).ToString()); } catch (IOException) { }
        }

        /// <summary>Reset (tests: a fresh server in the same process).</summary>
        public static void Reset() { lock (gate) { blockedBy.Clear(); fails.Clear(); probes.Clear(); alerted.Clear(); } }

        /// <summary>Is the address blocked now? (Checked first, on every request.)</summary>
        public static bool IsBlocked(SystemConfig cfg, string ip)
        {
            lock (gate)
            {
                XElement e; if (!Blocked(cfg).TryGetValue(ip, out e)) return false;
                if ((long)e.Attribute("UNTIL") > RunId.UnixMs(Clock())) return true;
                Blocked(cfg).Remove(ip); Write(cfg); return false;
            }
        }

        /// <summary>A wrong sign-in from the address (the name tried, never the password).</summary>
        public static void Failed(SystemConfig cfg, string ip, string user)
        {
            var s = Read(cfg); if (!s.On || Trusted(cfg, ip)) return;
            var now = Clock(); string why = null; List<string> users;
            lock (gate)
            {
                List<KeyValuePair<DateTime, string>> l; if (!fails.TryGetValue(K(cfg, ip), out l)) fails[K(cfg, ip)] = l = new List<KeyValuePair<DateTime, string>>();
                l.Add(new KeyValuePair<DateTime, string>(now, (user ?? "").ToLowerInvariant())); l.RemoveAll(x => (now - x.Key).TotalMinutes > Window);
                users = l.Select(x => x.Value).Where(x => x.Length > 0).Distinct().ToList();
                if (l.Count >= s.Fails) why = l.Count + " wrong sign-ins in " + Window + " minutes";
                else if (users.Count >= s.Users) why = "sign-ins tried for " + users.Count + " different names in " + Window + " minutes (" + string.Join(", ", users.Take(6)) + ")";
                // one name attacked from several addresses
                if (!string.IsNullOrEmpty(user))
                {
                    var from = fails.Where(kv => kv.Key.StartsWith(cfg.SystemHome + "|", StringComparison.Ordinal) && kv.Value.Any(x => x.Value == user.ToLowerInvariant() && (now - x.Key).TotalMinutes <= Window)).Select(kv => kv.Key.Substring(cfg.SystemHome.Length + 1)).ToList();
                    if (from.Count >= 3) AlertOnce(cfg, "user:" + user.ToLowerInvariant(), "The user name " + user + " is attacked", "Wrong passwords for <b>" + Fmt.H(user) + "</b> from " + from.Count + " addresses in " + Window + " minutes: " + Fmt.H(string.Join(", ", from.Take(10))) + ". Each address is blocked when it reaches the limit.");
                }
            }
            if (why != null) Block(cfg, ip, why, "automatic", s.BlockHours);
        }

        /// <summary>A refused request from someone not signed in: an unknown address, a wrong device token.</summary>
        public static void Refused(SystemConfig cfg, string ip)
        {
            var s = Read(cfg); if (!s.On || Trusted(cfg, ip)) return;
            var now = Clock(); bool block;
            lock (gate)
            {
                List<DateTime> l; if (!probes.TryGetValue(K(cfg, ip), out l)) probes[K(cfg, ip)] = l = new List<DateTime>();
                l.Add(now); l.RemoveAll(x => (now - x).TotalMinutes > Window); block = l.Count >= s.Probes;
            }
            if (block) Block(cfg, ip, s.Probes + " refused requests in " + Window + " minutes (scanning the server)", "automatic", s.BlockHours);
        }

        public static void Block(SystemConfig cfg, string ip, string reason, string by, int hours)
        {
            IPAddress a; if (!IPAddress.TryParse(ip ?? "", out a)) throw new ApiException(400, "IP", "Not an address.");
            lock (gate)
            {
                Blocked(cfg)[ip] = new XElement("BLOCK", new XAttribute("IP", ip), new XAttribute("SINCE", RunId.UnixMs(Clock())), new XAttribute("UNTIL", RunId.UnixMs(Clock().AddHours(hours))), new XAttribute("REASON", reason ?? ""), new XAttribute("BY", by ?? ""));
                Write(cfg); fails.Remove(K(cfg, ip)); probes.Remove(K(cfg, ip));
            }
            SysLog.Write(ip, "Access", "warn: address blocked for " + hours + " hours — " + reason + (by != "automatic" ? " (by " + by + ")" : ""));
            if (by == "automatic") AlertOnce(cfg, "block:" + ip, "An address was blocked: " + ip, "The address <b>" + Fmt.H(ip) + "</b> was blocked for " + hours + " hours: " + Fmt.H(reason) + ". To let it in again: Security and sign-in → Blocked addresses.");
        }

        public static bool Unblock(SystemConfig cfg, string ip, string by, string adminIp)
        {
            bool had;
            lock (gate) { had = Blocked(cfg).Remove(ip ?? ""); if (had) Write(cfg); fails.Remove(K(cfg, ip ?? "")); probes.Remove(K(cfg, ip ?? "")); }
            if (had) SysLog.Write(adminIp, "Admin", by + " unblocked the address " + ip);
            return had;
        }

        public static Msg List(SystemConfig cfg)
        {
            var s = Read(cfg);
            var m = new Msg().Set("on", s.On ? 1 : 0).Set("blockHours", s.BlockHours).Set("fails", s.Fails).Set("users", s.Users).Set("probes", s.Probes).Set("window", Window)
                .Set("trusted", string.Join(", ", s.Trusted)).Set("trustPrivate", s.TrustPrivate ? 1 : 0).Set("noCode", string.Join(", ", s.NoCode));
            lock (gate)
                foreach (var e in Blocked(cfg).Values.Where(e => (long)e.Attribute("UNTIL") > RunId.UnixMs(Clock())).OrderByDescending(e => (long)e.Attribute("SINCE")))
                    m.Add("blocked", new Msg().Set("ip", (string)e.Attribute("IP")).Set("since", (string)e.Attribute("SINCE")).Set("until", (string)e.Attribute("UNTIL")).Set("reason", (string)e.Attribute("REASON")).Set("by", (string)e.Attribute("BY")));
            return m;
        }

        /// <summary>An administrator signed in: alert when it looks wrong — after failures, from a new address, at night.</summary>
        public static void SignedIn(SystemConfig cfg, string login, string ip, DateTime localNow)
        {
            var why = new List<string>(); var now = Clock();
            lock (gate)
            {
                List<KeyValuePair<DateTime, string>> l;
                if (fails.TryGetValue(K(cfg, ip), out l) && l.Count(x => x.Value == (login ?? "").ToLowerInvariant()) >= 3) why.Add(l.Count(x => x.Value == login.ToLowerInvariant()) + " wrong passwords before it");
                // bug 120: a good sign-in cleared EVERY failure of the address - any account (a reseller's) could reset the count
                // between guesses for other names and spray for ever. Only this name's own failures are cleared.
                var mine = (login ?? "").ToLowerInvariant();
                if (l != null) { l.RemoveAll(x => x.Value == mine); if (l.Count == 0) fails.Remove(K(cfg, ip)); }
                var doc = SeenDoc(cfg); var key = (login ?? "").ToLowerInvariant();
                var seen = doc.Root.Elements("SEEN").Where(e => (string)e.Attribute("LOGIN") == key).ToList();
                if (seen.Count > 0 && !seen.Any(e => (string)e.Attribute("IP") == ip)) why.Add("from a new address");
                foreach (var e in seen.Where(e => (string)e.Attribute("IP") == ip || (long)e.Attribute("TIME") < RunId.UnixMs(now.AddDays(-30))).ToList()) e.Remove();
                doc.Root.Add(new XElement("SEEN", new XAttribute("LOGIN", key), new XAttribute("IP", ip), new XAttribute("TIME", RunId.UnixMs(now))));
                try { Atomic.WriteText(SeenPath(cfg), doc.ToString()); } catch (IOException) { }
            }
            if (localNow.Hour < 6) why.Add("at night (" + localNow.ToString("HH:mm", CultureInfo.InvariantCulture) + ")");
            if (why.Count > 0 && !Trusted(cfg, ip))
                AlertOnce(cfg, "signin:" + login + ":" + ip + ":" + string.Join(",", why.Select(w => w.Split(' ')[0])), "Administrator sign-in to check: " + login, "<b>" + Fmt.H(login) + "</b> signed in from " + Fmt.H(ip) + " — " + Fmt.H(string.Join("; ", why)) + ". If it was not you: change the password, and block the address in Security and sign-in.");
        }

        static string SeenPath(SystemConfig cfg) { return Path.Combine(cfg.SystemHome, "conf", "guard-seen.xml"); }
        static XDocument SeenDoc(SystemConfig cfg) { try { if (File.Exists(SeenPath(cfg))) return OnlineBackup.Core.Atomic.LoadXml(SeenPath(cfg)); } catch (Exception) { } return new XDocument(new XElement("SEEN_LIST")); }

        static void AlertOnce(SystemConfig cfg, string key, string subject, string html)
        {
            var now = Clock(); key = cfg.SystemHome + "|" + key;
            lock (alerted) { DateTime last; if (alerted.TryGetValue(key, out last) && (now - last).TotalMinutes < 60) return; alerted[key] = now; }
            var m = Mail; if (m == null) return;
            System.Threading.ThreadPool.QueueUserWorkItem(_ => { try { m(cfg, subject, html); } catch (Exception) { } });
        }
    }
}
