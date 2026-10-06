using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Text;
using System.Xml.Linq;
using OnlineBackup.Core;

namespace OnlineBackup.Server
{
    public static class Fmt
    {
        public static string Size(long b)
        {
            string[] u = { "B", "K", "M", "G", "T" };
            double v = b; int i = 0;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return (i == 0 ? v.ToString("0", CultureInfo.InvariantCulture) : v.ToString("0.00", CultureInfo.InvariantCulture)) + (i == 0 ? " B" : " " + u[i]);
        }

        /// <summary>Ahsay statistics format: compressed / uncompressed [ratio%] [files].</summary>
        public static string Area(long compressed, long uncompressed, long files)
        {
            var ratio = uncompressed > 0 ? (int)Math.Round(100.0 * (uncompressed - compressed) / uncompressed) : 0;
            return Size(compressed) + " / " + Size(uncompressed) + " [" + Math.Max(0, ratio) + "%] [" + files + "]";
        }

        public static string H(string s) { return WebUtility.HtmlEncode(s ?? ""); }
    }

    /// <summary>
    /// E-mail through the SMTP servers of the system settings, in order (the next one if the first fails).
    /// Every message is written to the Email log; passwords are stored protected and never logged.
    /// Security: NONE / STARTTLS. (Implicit SSL on 465 is not supported by .NET's SMTP client — use 587 + STARTTLS.)
    /// </summary>
    public sealed class Mailer
    {
        readonly SystemConfig cfg;
        public Mailer(SystemConfig cfg) { this.cfg = cfg; }

        public bool Configured { get { return cfg.Doc.Root.Elements("SMTP").Any(e => !string.IsNullOrEmpty((string)e.Attribute("HOST"))); } }

        public string Brand(string attr, string fallback) { return Vendors.Brand(cfg, null, attr, fallback); }

        /// <param name="vendor">VND-070: the mail carries this vendor's branding (its customers never see the server's own name).</param>
        public bool Send(IEnumerable<string> to, string subject, string htmlBody, string category, string vendor = null)
        {
            Func<string, string, string> Brand = (a, f) => Vendors.Brand(cfg, vendor, a, f);
            var rcpt = to.Where(t => !string.IsNullOrEmpty(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (rcpt.Count == 0 || !Configured) { SysLog.Write(null, "Email", "not sent (" + (rcpt.Count == 0 ? "no recipients" : "no SMTP server") + "): " + subject); return false; }
            var sender = cfg.Doc.Root.Element("REPORT_SENDER");
            var fromMail = sender == null ? null : (string)sender.Attribute("EMAIL");
            var fromName = sender == null ? null : (string)sender.Attribute("NAME");
            if (string.IsNullOrEmpty(fromMail)) { SysLog.Write(null, "Email", "not sent (no report sender): " + subject); return false; }
            var product = Brand("PRODUCT", "ITSguard Server Online");
            var color = Brand("COLOR", "#4F46E5"); var accent = Brand("ACCENT", "#F97316");
            // I18N-020: in the language of the vendor (or the server); written in English, translated here
            var lang = Lang(vendor); var dir = L.Rtl(lang) ? "rtl" : "ltr";
            subject = L.Tr(lang, subject); htmlBody = L.Tr(lang, htmlBody);
            var html = "<!doctype html><html dir='" + dir + "' lang='" + lang + "'><body style='font-family:Arial,sans-serif;direction:" + dir + ";text-align:start'>"
                + "<div style='background:" + Fmt.H(color) + ";border-bottom:4px solid " + Fmt.H(accent) + ";color:#fff;padding:12px 16px;font-size:18px;font-weight:bold'>" + Fmt.H(product) + (Brand("SLOGAN", "").Length > 0 ? "<div style='font-size:13px;opacity:.85'>" + Fmt.H(Brand("SLOGAN", "")) + "</div>" : "") + "</div>"
                + "<div style='padding:14px'>" + htmlBody + "</div>"
                + "<div style='color:#666;font-size:12px;padding:0 14px 14px'>" + Fmt.H(Brand("COMPANY", "")) + " " + Fmt.H(Brand("PHONE", "")) + " " + Fmt.H(Brand("EMAIL", ""))
                + (cfg.License.Has("WHITELABEL") ? "" : "<br>" + L.T(lang, "Powered by {0}", Fmt.H(License.OwnerProduct))) + "</div></body></html>";
            foreach (var smtp in cfg.Doc.Root.Elements("SMTP"))
            {
                var host = (string)smtp.Attribute("HOST");
                if (string.IsNullOrEmpty(host)) continue;
                try
                {
                    using (var client = new SmtpClient(host, int.Parse((string)smtp.Attribute("PORT") ?? "25", CultureInfo.InvariantCulture)))
                    {
                        client.EnableSsl = string.Equals((string)smtp.Attribute("SECURITY"), "STARTTLS", StringComparison.OrdinalIgnoreCase);
                        client.Timeout = 30000;
                        var login = (string)smtp.Attribute("LOGIN");
                        var enc = (string)smtp.Attribute("PASSWORD_ENC");
                        if (!string.IsNullOrEmpty(login))
                            client.Credentials = new NetworkCredential(login, string.IsNullOrEmpty(enc) ? "" : Encoding.UTF8.GetString(KeyVault.Unprotect(cfg.SystemHome, Convert.FromBase64String(enc))));
                        using (var m = new MailMessage { From = new MailAddress(fromMail, string.IsNullOrEmpty(fromName) ? product : fromName), Subject = subject, Body = html, IsBodyHtml = true, BodyEncoding = Encoding.UTF8, SubjectEncoding = Encoding.UTF8 })
                        {
                            foreach (var r in rcpt) m.To.Add(r);
                            client.Send(m);
                        }
                    }
                    SysLog.Write(null, "Email", category + " sent via " + host + " to " + string.Join(",", rcpt) + ": " + subject);
                    return true;
                }
                catch (Exception e) { SysLog.Write(null, "Email", "error via " + host + ": " + e.Message + " (" + subject + ")"); }
            }
            return false;
        }

        /// <summary>The language of the e-mails: the vendor's, else the server's (Company and product), else English.</summary>
        public string Lang(string vendor) { return L.Norm(Vendors.Brand(cfg, vendor, "LANGUAGE", "en")); }

        public List<string> AdminContacts() { return cfg.Doc.Root.Elements("ADMIN_CONTACT").Select(e => (string)e.Attribute("EMAIL")).Where(e => !string.IsNullOrEmpty(e)).ToList(); }

        /// <summary>Alerts to the administrator contacts: quota, damaged data, suspected ransomware, missed backups, failed restore tests.</summary>
        public void Alert(string subject, string html, string vendor = null)
        {
            // VND-070: an alert about a vendor's customer also goes to that vendor (its EMAIL), with its branding
            var to = AdminContacts();
            var v = Vendors.Find(cfg, vendor);
            if (v != null && !string.IsNullOrEmpty((string)v.Attribute("EMAIL"))) to.Add((string)v.Attribute("EMAIL"));
            Send(to, subject, html, "alert", vendor);
        }

        /// <summary>The run report to the customer's contacts (EMAIL_NOTIFY: ALL every run, FAILURE only failures, NONE).</summary>
        public void BackupReport(Profile p, BackupSetInfo set, string job, Msg body, Msg setStats)
        {
            var notify = p.Get("EMAIL_NOTIFY") ?? "ALL";
            var result = body["result"] ?? "BS_STOP_BY_SYSTEM_ERROR";
            bool ok = result == "BS_STOP_SUCCESS";
            if (notify == "NONE" || (notify == "FAILURE" && ok)) return;
            var to = p.User.Elements("CONTACT").Select(c => (string)c.Attribute("EMAIL")).ToList();
            var lang = Lang(p.Get("OWNER"));
            // Bug 25: the mark and the title say what happened — a warning is not a failure (it had the failure mark ✗), and a
            // run stopped by the administrator / at its maximum duration, or by the quota, is named so, not "Backup failed"
            bool warn = result == "BS_STOP_SUCCESS_WITH_WARNING";
            var title = ok ? "Backup completed successfully" : warn ? "Backup completed with warnings"
                : result == "BS_STOP_SUCCESS_WITH_ERROR" ? "Backup completed with errors — some data was not backed up"
                : result == "BS_STOP_BY_USER" ? "Backup stopped before the end — not all data was backed up"
                : result == "BS_STOP_QUOTA_EXCEEDED" ? "Backup stopped: the quota is full — new data was not backed up"
                : result == "BS_STOP_BY_PRE_COMMAND" ? "Backup not run: the command before the backup failed"
                : "Backup failed";
            Func<string, string, string> Row = (k, v) => "<tr><td style='background:#f3f3f3'>" + Fmt.H(L.T(lang, k)) + "</td><td>" + Fmt.H(v) + "</td></tr>";
            var html = "<h2 style='margin:0 0 8px'>" + Fmt.H(L.T(lang, title)) + "</h2>"
                + "<table style='border-collapse:collapse' cellpadding='6' border='1'>"
                + Row("User", p.Get("LOGIN_NAME")) + Row("Backup set", set.Name) + Row("Run", job) + Row("Result", result)
                + Row("New files", body["new"]) + Row("Updated files", body["upd"]) + Row("Updated permissions", body["perm"]) + Row("Deleted files", body["del"])
                + Row("Sent", Fmt.Size(body.Long("bytes")))
                + Row("Data area", Fmt.Area(setStats.Long("dataSize"), setStats.Long("dataOrig"), setStats.Long("dataFiles")))
                + Row("Retention area", Fmt.Area(setStats.Long("retainSize"), setStats.Long("retainOrig"), setStats.Long("retainFiles")))
                + "</table>";
            Send(to, (ok ? "✓ " : warn ? "⚠ " : "✗ ") + "Backup report — " + set.Name + " — " + p.Get("LOGIN_NAME"), html, "report", p.Get("OWNER"));
        }
    }

    /// <summary>
    /// ITSguard reads Ahsay through two shares: Profiles$ (&lt;account&gt;\Profile.xml) and DetailedLogs$
    /// (&lt;account&gt;\&lt;set&gt;\&lt;run&gt;.log). The server keeps a copy of exactly that shape, so every existing ITSguard
    /// screen (failures, configuration changes, ransomware suspicion, revenue) works for this server without a change.
    /// </summary>
    public sealed class Exporter
    {
        readonly SystemConfig cfg;
        public Exporter(SystemConfig cfg) { this.cfg = cfg; }

        string Attr(string a) { var e = cfg.Doc.Root.Element("EXPORT"); return e == null ? null : (string)e.Attribute(a); }

        public void Profile(string login, string userDir)
        {
            var root = Attr("PROFILES");
            if (string.IsNullOrEmpty(root)) return;
            try { Atomic.WriteBytes(Path.Combine(root, login, "Profile.xml"), File.ReadAllBytes(Path.Combine(userDir, "db", "Profile.xml"))); }
            catch (Exception e) { SysLog.Write(null, "System", "error: export profile " + login + ": " + e.Message); }
        }

        public void Log(string login, string setId, string job, string logFile)
        {
            var root = Attr("LOGS");
            if (string.IsNullOrEmpty(root) || !File.Exists(logFile)) return;
            try { Atomic.WriteBytes(Path.Combine(root, login, setId, job + ".log"), File.ReadAllBytes(logFile)); }
            catch (Exception e) { SysLog.Write(null, "System", "error: export log " + login + ": " + e.Message); }
        }
    }
}
