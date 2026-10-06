using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using OnlineBackup.Core;

namespace OnlineBackup.Server
{
    /// <summary>
    /// TECH-010: the people who sign in to the admin site — the main administrator (ADMIN), more administrators (STAFF), and a
    /// vendor's administrators (VENDOR_ADMIN). A backup product has administrators only (owner: no technician / view roles):
    /// each one can do everything; two e-mails for the service calls assigned to them. For every one of them: two-step verification is mandatory (the
    /// first sign-in sets it up and nothing else opens until it is done), and wrong passwords lock the account (cannot be
    /// switched off; at least 1 attempt… at most 10 attempts, at least 5 minutes).
    /// &lt;STAFF LOGIN_NAME NAME EMAIL EMAIL2 HASHED_PWD TOTP_SECRET DISABLED NOTIFY_ASSIGN
    ///        FAIL_COUNT LOCKED_UNTIL LAST_LOGIN LAST_IP/&gt;
    /// </summary>
    public static class Staff
    {
        public sealed class Account { public XElement El; public string Login, Vendor = ""; public bool Main; }

        static IEnumerable<XElement> StaffElements(SystemConfig cfg) { return cfg.Doc.Root.Elements("STAFF"); }

        public static Account Find(SystemConfig cfg, string login)
        {
            if (string.IsNullOrEmpty(login)) return null;
            var a = cfg.Admin;
            if (string.Equals((string)a.Attribute("LOGIN_NAME"), login, StringComparison.Ordinal)) return new Account { El = a, Login = login, Main = true };
            var s = StaffElements(cfg).FirstOrDefault(x => string.Equals((string)x.Attribute("LOGIN_NAME"), login, StringComparison.OrdinalIgnoreCase));
            if (s != null) return new Account { El = s, Login = (string)s.Attribute("LOGIN_NAME") };
            XElement va; var v = Vendors.AdminOf(cfg, login, out va);
            if (v != null && (string)v.Attribute("DISABLED") != "Y") return new Account { El = va, Login = login, Vendor = (string)v.Attribute("ID") };
            return null;
        }

        /// <summary>The server itself (localhost) is trusted: no lock, no code (SEC-090/110). LOCAL_TRUST="N" switches it off (the tests,
        /// whose sign-ins also come from this computer).</summary>
        static bool IsLocal(SystemConfig cfg, string ip)
        {
            if ((string)cfg.Doc.Root.Attribute("LOCAL_TRUST") == "N") return false;
            System.Net.IPAddress a; return System.Net.IPAddress.TryParse(ip ?? "", out a) && System.Net.IPAddress.IsLoopback(a);
        }
        public static bool LocalTrusted(SystemConfig cfg, string ip) { return IsLocal(cfg, ip); }

        /// <summary>SEC-090: the console on the server (run as administrator) — unlocks an administrator, and if asked starts its two-step set-up again.</summary>
        public static string Unlock(SystemConfig cfg, string login, bool resetTwoStep)
        {
            lock (cfg)
            {
                var acc = Find(cfg, string.IsNullOrEmpty(login) ? (string)cfg.Admin.Attribute("LOGIN_NAME") : login) ?? throw new ApiException(404, "LOGIN", "Unknown account.");
                acc.El.SetAttributeValue("LOCKED_UNTIL", null); acc.El.SetAttributeValue("FAIL_COUNT", null);
                if (resetTwoStep) { acc.El.SetAttributeValue("TOTP_SECRET", ""); acc.El.SetAttributeValue("TOTP_PENDING", null); }
                cfg.Save();
                SysLog.Write("console", "Admin", "console unlocked " + acc.Login + (resetTwoStep ? " and reset its two-step verification" : ""));
                return acc.Login;
            }
        }

        public sealed class SignIn { public Account Account; public bool Enroll; }

        /// <summary>Password, lock and two-step verification of one sign-in. Enroll = the account has no two-step yet.</summary>
        public static SignIn Check(SystemConfig cfg, string login, string password, string otp, string ip, DateTime nowUtc)
        {
            Guard.Tried = login;
            lock (cfg)
            {
                var acc = Find(cfg, login);
                bool disabled = acc != null && (string)acc.El.Attribute("DISABLED") == "Y";
                long until; long.TryParse(acc == null ? null : (string)acc.El.Attribute("LOCKED_UNTIL"), out until);
                // SEC-090 (owner): on the server itself (the console, the browser there) the account never locks
                bool local = IsLocal(cfg, ip);
                if (acc != null && until > RunId.UnixMs(nowUtc) && !local)
                {
                    SysLog.Write(ip, "Access", "admin login refused (locked) " + login);
                    throw new ApiException(423, "LOCKED", "The account is locked after wrong passwords. Try again later or ask the main administrator.");
                }
                // H-11: an unknown or disabled account costs the same key derivation as a wrong password
                bool ok = PasswordHash.Verify(password ?? "", acc != null && !disabled ? (string)acc.El.Attribute("HASHED_PWD") : Users.DummyHash) && acc != null && !disabled;
                var secret = acc == null ? null : (string)acc.El.Attribute("TOTP_SECRET");
                bool enroll = string.IsNullOrEmpty(secret);
                bool noCode = Guard.NoCode(cfg, ip) || IsLocal(cfg, ip);   // SEC-100: a fixed address the owner listed, or the server itself (SEC-110) — the password is enough
                if (noCode) enroll = false;
                else if (ok && !enroll)
                {
                    // H-02: each code opens one sign-in (its step is remembered with the account)
                    long last; if (!long.TryParse((string)acc.El.Attribute("TOTP_LAST_STEP"), out last)) last = -1;
                    var step = Totp.Step(secret, otp, DateTime.UtcNow /* real time: the phone's code */);
                    ok = step > last;
                    if (ok) acc.El.SetAttributeValue("TOTP_LAST_STEP", step);
                }
                if (acc != null)
                {
                    if (ok) { acc.El.SetAttributeValue("FAIL_COUNT", null); acc.El.SetAttributeValue("LAST_LOGIN", RunId.UnixMs(nowUtc)); acc.El.SetAttributeValue("LAST_IP", ip); }
                    else if (!local)
                    {
                        int fails = ((int?)acc.El.Attribute("FAIL_COUNT") ?? 0) + 1;
                        if (fails >= Users.ClampAttempts(cfg.AutoLockAttempts))
                        {
                            int mins = Users.ClampMinutes(cfg.LockMinutes); if (mins == 0) mins = 60 * 24 * 3650;   // 0 = until someone unlocks it
                            acc.El.SetAttributeValue("LOCKED_UNTIL", RunId.UnixMs(nowUtc.AddMinutes(mins))); fails = 0;
                            SysLog.Write(ip, "Access", "warn: admin account locked after wrong passwords " + login);
                        }
                        acc.El.SetAttributeValue("FAIL_COUNT", fails);
                    }
                    cfg.Save();
                }
                SysLog.Write(ip, "Access", "admin login " + (ok ? "ok " : "failed ") + login + (acc != null && acc.Vendor.Length > 0 ? " (vendor " + acc.Vendor + ")" : "") + (ok && enroll ? " — two-step set-up required" : ""));
                if (!ok) throw new ApiException(401, "LOGIN", "Wrong administrator details.");
                return new SignIn { Account = acc, Enroll = enroll };
            }
        }

        /// <summary>Two-step set-up of the signed-in account: a new secret (pending until a code confirms it).</summary>
        public static Msg TotpEnable(SystemConfig cfg, string login)
        {
            lock (cfg)
            {
                var acc = Find(cfg, login) ?? throw new ApiException(404, "LOGIN", "Unknown account.");
                var secret = Totp.NewSecret();
                acc.El.SetAttributeValue("TOTP_PENDING", secret); cfg.Save();
                // the authenticator app shows the product's name beside the login (not a bare "admin" nobody can place)
                var product = ((string)cfg.Doc.Root.Element("BRANDING")?.Attribute("PRODUCT") ?? "").Trim();
                var issuer = (product.Length > 0 ? product : "Backup server") + " — management";
                return new Msg().Set("secret", secret).Set("issuer", issuer)
                    .Set("uri", "otpauth://totp/" + Uri.EscapeDataString(issuer) + ":" + Uri.EscapeDataString(login) + "?secret=" + secret + "&issuer=" + Uri.EscapeDataString(issuer));
            }
        }

        public static void TotpConfirm(SystemConfig cfg, string login, string code, DateTime nowUtc)
        {
            lock (cfg)
            {
                var acc = Find(cfg, login) ?? throw new ApiException(404, "LOGIN", "Unknown account.");
                var pending = (string)acc.El.Attribute("TOTP_PENDING");
                var step = Totp.Step(pending, code, DateTime.UtcNow /* real time: the phone's code */);
                if (string.IsNullOrEmpty(pending) || step < 0) throw new ApiException(400, "OTP", "The code is wrong.");
                acc.El.SetAttributeValue("TOTP_SECRET", pending); acc.El.SetAttributeValue("TOTP_PENDING", null); cfg.Save();
            }
        }

        // ------------------------------------------------------------------ the people list (main administrator only)

        public static Msg List(SystemConfig cfg)
        {
            var m = new Msg();
            var a = cfg.Admin;
            m.Add("staff", Row(a).Set("main", 1));
            foreach (var s in StaffElements(cfg)) m.Add("staff", Row(s));
            return m;
        }

        static Msg Row(XElement e)
        {
            long until; long.TryParse((string)e.Attribute("LOCKED_UNTIL"), out until);
            return new Msg().Set("login", (string)e.Attribute("LOGIN_NAME")).Set("name", (string)e.Attribute("NAME")).Set("email", (string)e.Attribute("EMAIL")).Set("email2", (string)e.Attribute("EMAIL2"))
                .Set("totp", string.IsNullOrEmpty((string)e.Attribute("TOTP_SECRET")) ? 0 : 1).Set("disabled", (string)e.Attribute("DISABLED") == "Y" ? 1 : 0)
                .Set("notifyAssign", (string)e.Attribute("NOTIFY_ASSIGN") == "N" ? 0 : 1).Set("lastLogin", (string)e.Attribute("LAST_LOGIN")).Set("lastIp", (string)e.Attribute("LAST_IP"))
                .Set("locked", until > RunId.UnixMs(SystemClock.UtcNow) ? 1 : 0);
        }

        /// <summary>Adds or changes an administrator. A new one needs a password (8+ characters with a letter).</summary>
        public static void Save(SystemConfig cfg, Msg b, string by, string ip)
        {
            var login = (b["login"] ?? "").Trim();
            if (login.Length < 2 || login.Length > 64 || login.IndexOfAny(new[] { '<', '>', '"', '/', '\\' }) >= 0) throw new ApiException(400, "LOGIN", "The sign-in name is not valid.");
            foreach (var k in new[] { "email", "email2" }) { var v = (b[k] ?? "").Trim(); if (v.Length > 0 && (!v.Contains("@") || v.Length > 200 || v.Contains(" "))) throw new ApiException(400, "EMAIL", "The e-mail is not valid: " + v); }
            lock (cfg)
            {
                var main = (string)cfg.Admin.Attribute("LOGIN_NAME");
                if (string.Equals(main, login, StringComparison.OrdinalIgnoreCase)) throw new ApiException(400, "LOGIN", "The main administrator is changed in the security settings.");
                XElement va; if (Vendors.AdminOf(cfg, login, out va) != null) throw new ApiException(409, "LOGIN", "This name belongs to a vendor's administrator.");
                var e = StaffElements(cfg).FirstOrDefault(x => string.Equals((string)x.Attribute("LOGIN_NAME"), login, StringComparison.OrdinalIgnoreCase));
                bool fresh = e == null;
                if (fresh) { e = new XElement("STAFF", new XAttribute("LOGIN_NAME", login)); cfg.Doc.Root.Add(e); }
                var pw = b["password"];
                if (fresh && string.IsNullOrEmpty(pw)) throw new ApiException(400, "PASSWORD", "Give the administrator a password.");
                if (!string.IsNullOrEmpty(pw)) { if (!Passwords.Ok(pw)) throw new ApiException(400, "PASSWORD", "The password needs at least " + Passwords.MinLength + " characters, with at least one letter."); e.SetAttributeValue("HASHED_PWD", PasswordHash.Create(pw)); }
                e.SetAttributeValue("NAME", (b["name"] ?? "").Trim()); e.SetAttributeValue("EMAIL", (b["email"] ?? "").Trim()); e.SetAttributeValue("EMAIL2", (b["email2"] ?? "").Trim());
                if (b["notifyAssign"] != null) e.SetAttributeValue("NOTIFY_ASSIGN", b.Bool("notifyAssign") ? "Y" : "N");
                if (b["disabled"] != null) e.SetAttributeValue("DISABLED", b.Bool("disabled") ? "Y" : "N");
                cfg.Save();
                SysLog.Write(ip, "Admin", by + (fresh ? " added the administrator " : " changed the administrator ") + login);
            }
        }

        /// <summary>Unlock, reset two-step (set up again at the next sign-in), or delete one administrator.</summary>
        public static void Act(SystemConfig cfg, string login, string action, string by, string ip)
        {
            lock (cfg)
            {
                var acc = Find(cfg, login);
                if (acc == null || acc.Vendor.Length > 0) throw new ApiException(404, "LOGIN", "The administrator was not found.");
                switch (action)
                {
                    case "unlock": acc.El.SetAttributeValue("LOCKED_UNTIL", null); acc.El.SetAttributeValue("FAIL_COUNT", null); break;
                    case "resettotp":
                        if (acc.Main && login == by) throw new ApiException(400, "TOTP", "Reset your own two-step from another administrator.");
                        acc.El.SetAttributeValue("TOTP_SECRET", null); acc.El.SetAttributeValue("TOTP_PENDING", null); break;
                    case "delete":
                        if (acc.Main) throw new ApiException(400, "MAIN", "The main administrator cannot be deleted.");
                        acc.El.Remove(); break;
                    default: throw new ApiException(404, "NOT_FOUND", "Not found.");
                }
                cfg.Save();
                SysLog.Write(ip, "Admin", by + " " + action + " administrator " + login);
            }
        }

        /// <summary>The e-mails of one handler (calls assigned to them), when they want them.</summary>
        public static List<string> Mails(SystemConfig cfg, string login)
        {
            var acc = Find(cfg, login);
            if (acc == null || (string)acc.El.Attribute("NOTIFY_ASSIGN") == "N" || (string)acc.El.Attribute("DISABLED") == "Y") return new List<string>();
            return new[] { (string)acc.El.Attribute("EMAIL"), (string)acc.El.Attribute("EMAIL2") }.Where(x => !string.IsNullOrEmpty(x)).ToList();
        }
    }
}
