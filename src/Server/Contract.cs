using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using OnlineBackup.Core;

namespace OnlineBackup.Server
{
    /// <summary>
    /// CONTRACT-010: the IT company's contract with its customers. The customer sees it when installing the client software
    /// and when signing up, and accepts it before going on; the acceptance is kept (version, time, address). A changed text
    /// is a new version; when "accept again" is on, existing customers accept it at their next sign-in.
    /// &lt;system&gt;\contract\contract.xml: &lt;CONTRACT VERSION DATE INSTALL=Y SIGNUP=Y REACCEPT=Y&gt;&lt;TEXT LANG="he"&gt;…&lt;/TEXT&gt;…&lt;/CONTRACT&gt;
    /// SIGNUP-010: new customers are opened from the client software only (owner) — the server's sign-up, with the
    /// policy's defaults, when it is open (Settings), limited per address.
    /// </summary>
    public static class Contract
    {
        static string PathOf(SystemConfig cfg) { return Path.Combine(cfg.SystemHome, "contract", "contract.xml"); }

        public static XElement Load(SystemConfig cfg)
        {
            var p = PathOf(cfg);
            return File.Exists(p) ? OnlineBackup.Core.Atomic.LoadXElement(p) : new XElement("CONTRACT", new XAttribute("VERSION", 0), new XAttribute("INSTALL", "Y"), new XAttribute("SIGNUP", "Y"), new XAttribute("REACCEPT", "Y"));
        }

        public static int Version(SystemConfig cfg) { return (int?)Load(cfg).Attribute("VERSION") ?? 0; }

        /// <summary>The text for a language: that language, else English, else the first one written. Version 0 = no contract.</summary>
        public static Msg Public(SystemConfig cfg, string lang)
        {
            var c = Load(cfg);
            var texts = c.Elements("TEXT").ToList();
            var t = texts.FirstOrDefault(x => (string)x.Attribute("LANG") == L.Norm(lang ?? "")) ?? texts.FirstOrDefault(x => (string)x.Attribute("LANG") == "en") ?? texts.FirstOrDefault();
            return new Msg().Set("version", t == null ? 0 : (int?)c.Attribute("VERSION") ?? 0).Set("date", (string)c.Attribute("DATE")).Set("lang", t == null ? null : (string)t.Attribute("LANG"))
                .Set("text", t == null ? null : t.Value).Set("install", (string)c.Attribute("INSTALL") == "N" ? 0 : 1).Set("signup", (string)c.Attribute("SIGNUP") == "N" ? 0 : 1).Set("signupOpen", SignupOpen(cfg) ? 1 : 0);
        }

        public static Msg Admin(SystemConfig cfg)
        {
            var c = Load(cfg);
            var m = new Msg().Set("version", (string)c.Attribute("VERSION")).Set("date", (string)c.Attribute("DATE")).Set("install", (string)c.Attribute("INSTALL") == "N" ? 0 : 1)
                .Set("signup", (string)c.Attribute("SIGNUP") == "N" ? 0 : 1).Set("reaccept", (string)c.Attribute("REACCEPT") == "N" ? 0 : 1).Set("signupOpen", SignupOpen(cfg) ? 1 : 0);
            foreach (var t in c.Elements("TEXT")) m.Add("texts", new Msg().Set("lang", (string)t.Attribute("LANG")).Set("text", t.Value));
            return m;
        }

        /// <summary>Saves the texts (one per language) and where they are shown. A changed text makes a new version.</summary>
        public static Msg Save(SystemConfig cfg, Msg b, string admin, string ip)
        {
            lock (cfg)
            {
                var c = Load(cfg);
                bool changed = false;
                foreach (var t in b.List("texts"))
                {
                    var lang = L.Norm(t["lang"] ?? ""); var text = (t["text"] ?? "").Replace("\r\n", "\n").Trim();
                    if (text.Length > 200000) throw new ApiException(400, "TOO_LONG", "The contract is too long.");
                    var e = c.Elements("TEXT").FirstOrDefault(x => (string)x.Attribute("LANG") == lang);
                    if (text.Length == 0) { if (e != null) { e.Remove(); changed = true; } continue; }
                    if (e == null) { c.Add(new XElement("TEXT", new XAttribute("LANG", lang), text)); changed = true; }
                    else if (e.Value != text) { e.Value = text; changed = true; }
                }
                foreach (var k in new[] { "install", "signup", "reaccept" }) if (b[k] != null) c.SetAttributeValue(k.ToUpperInvariant(), b.Bool(k) ? "Y" : "N");
                if (b["signupOpen"] != null)
                {
                    var se = cfg.Doc.Root.Element("SIGNUP"); if (se == null) { se = new XElement("SIGNUP"); cfg.Doc.Root.Add(se); }
                    se.SetAttributeValue("OPEN", b.Bool("signupOpen") ? "Y" : "N"); cfg.Save();
                }
                if (changed)
                {
                    c.SetAttributeValue("VERSION", ((int?)c.Attribute("VERSION") ?? 0) + 1);
                    c.SetAttributeValue("DATE", SystemClock.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    // every version is kept as it was (what each customer accepted can always be shown)
                    Atomic.WriteText(Path.Combine(cfg.SystemHome, "contract", "v" + (string)c.Attribute("VERSION") + ".xml"), c.ToString());
                }
                Atomic.WriteText(PathOf(cfg), c.ToString());
                SysLog.Write(ip, "Admin", admin + " saved the customer contract" + (changed ? " — version " + (string)c.Attribute("VERSION") : ""));
                return Admin(cfg);
            }
        }

        /// <summary>True when this customer must accept the current version before going on.</summary>
        public static bool Due(SystemConfig cfg, Profile p)
        {
            var c = Load(cfg); int v = (int?)c.Attribute("VERSION") ?? 0;
            if (v == 0 || !c.Elements("TEXT").Any()) return false;
            int accepted; int.TryParse(p.Get("CONTRACT_VERSION"), out accepted);
            if (accepted >= v) return false;
            return accepted == 0 || (string)c.Attribute("REACCEPT") != "N";
        }

        /// <summary>Records the customer's acceptance of a version (only the current one counts).</summary>
        public static void Accept(Users users, SystemConfig cfg, string login, int version, string ip)
        {
            if (version != Version(cfg)) throw new ApiException(412, "CONTRACT", "Read and accept the current contract.");
            lock (users.ProfileLock)
            {
                var p = users.LoadProfile(login);
                p.SetAttr("CONTRACT_VERSION", version); p.SetAttr("CONTRACT_TIME", RunId.UnixMs(SystemClock.UtcNow)); p.SetAttr("CONTRACT_IP", ip);
                users.SaveProfile(login, p);
            }
            SysLog.Write(ip, "Access", "contract version " + version + " accepted by " + login);
        }

        // ------------------------------------------------------------------ sign-up from the client software

        static readonly Dictionary<string, List<DateTime>> attempts = new Dictionary<string, List<DateTime>>();

        public static bool SignupOpen(SystemConfig cfg) { var s = cfg.Doc.Root.Element("SIGNUP"); return s == null || (string)s.Attribute("OPEN") != "N"; }

        /// <summary>
        /// SIGNUP-010: a new customer from the client software — the company, an e-mail, a sign-in name and a password (8+ with
        /// a letter); the policy's quota and settings; the contract accepted. At most 5 sign-ups an hour from one address.
        /// </summary>
        public static Profile Signup(Users users, SystemConfig cfg, Msg b, string ip, DateTime nowUtc)
        {
            if (!SignupOpen(cfg)) throw new ApiException(403, "SIGNUP_CLOSED", "New customers are opened by your IT provider. Contact them for a user name.");
            var key = cfg.SystemHome + "|" + ip;   // new customers made from this address in the last hour (a typo does not count)
            lock (attempts)
            {
                List<DateTime> l; if (!attempts.TryGetValue(key, out l)) attempts[key] = l = new List<DateTime>();
                l.RemoveAll(x => (nowUtc - x).TotalHours >= 1);
                if (l.Count >= 5) throw new ApiException(429, "LIMIT", "Too many sign-ups from this address. Try again later.");
            }
            var company = (b["company"] ?? "").Trim(); var email = (b["email"] ?? "").Trim();
            if (company.Length < 2 || company.Length > 120) throw new ApiException(400, "COMPANY", "Write the company or customer name.");
            if (email.Length < 5 || !email.Contains("@") || email.Contains(" ") || email.Length > 200) throw new ApiException(400, "EMAIL", "Write a valid e-mail.");
            int v = Version(cfg);
            if (v > 0 && (string)Load(cfg).Attribute("SIGNUP") != "N" && b.Int("contractVersion") != v) throw new ApiException(412, "CONTRACT", "Read and accept the contract.");
            var lic = cfg.License;
            if (lic.MaxUsers > 0 && users.Logins().Count() >= lic.MaxUsers) throw new ApiException(402, "LICENSE", "The server cannot take new customers right now. Contact your IT provider.");
            var p = users.Create(b["login"], b["password"], company, null, null, email, ip);
            var login = p.Get("LOGIN_NAME");
            lock (users.ProfileLock)
            {
                p = users.LoadProfile(login);
                p.SetAttr("SIGNUP", "CLIENT"); p.SetAttr("SIGNUP_IP", ip);
                if (!string.IsNullOrEmpty(b["phone"])) p.SetAttr("PHONE", b["phone"].Trim());
                if (v > 0) { p.SetAttr("CONTRACT_VERSION", v); p.SetAttr("CONTRACT_TIME", RunId.UnixMs(nowUtc)); p.SetAttr("CONTRACT_IP", ip); }
                users.SaveProfile(login, p);
            }
            lock (attempts) attempts[key].Add(nowUtc);
            SysLog.Write(ip, "Access", "new customer signed up from the client software: " + login + " (" + company + ")");
            return p;
        }
    }
}
