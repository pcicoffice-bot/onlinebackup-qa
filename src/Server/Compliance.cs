using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using OnlineBackup.Core;

namespace OnlineBackup.Server
{
    /// <summary>
    /// AI-080: the customer's data-protection report — a printable page (Print → Save as PDF) built from what the server
    /// knows: every backup set, how and when it runs, where the copies are, the encryption, the retention, the ransomware
    /// protection, the last restore test of every set (the restore certificate) and the backup procedure. It supports an
    /// audit (GDPR, Israel's Privacy Protection Regulations (Data Security) and Amendment 13, ISO 27001 A.8.13); it does not
    /// itself declare that the customer complies. Language and direction follow the company language.
    /// </summary>
    public static class Compliance
    {
        public sealed class SetFacts
        {
            public BackupSetInfo Set; public DateTime? LastBackup, LastTest; public string TestResult, Suspect; public int RestoresYear; public int Runs30;
        }

        public static string Html(string lang, string product, string company, Profile p, IList<SetFacts> sets, bool replication, bool userTwoFactor, bool adminTwoFactor, DateTime nowUtc)
        {
            lang = L.Norm(lang);
            Func<string, object[], string> T = (t, a) => Fmt.H(L.T(lang, t, a));
            Func<string, string> t1 = t => Fmt.H(L.T(lang, t));
            Func<DateTime?, string> D = d => d.HasValue ? d.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "—";
            var sb = new StringBuilder();
            sb.Append("<!doctype html><html lang='").Append(lang).Append("' dir='").Append(L.Rtl(lang) ? "rtl" : "ltr").Append("'><head><meta charset='utf-8'><title>")
              .Append(t1("Data protection report")).Append(" — ").Append(Fmt.H(p.Get("ALIAS") ?? p.Get("LOGIN_NAME")))
              .Append("</title><link rel='stylesheet' href='/admin/report.css'><script src='/admin/report.js' defer></script></head><body>");   // the admin site's CSP allows no inline style or script
            sb.Append("<p class='noprint'><button id='print'>").Append(t1("Print / save as PDF")).Append("</button></p>");
            sb.Append("<h1>").Append(t1("Data protection report")).Append("</h1>");
            sb.Append("<p>").Append(Fmt.H(product)).Append(company.Length > 0 ? " — " + Fmt.H(company) : "").Append("<br>")
              .Append(t1("Customer")).Append(": <b>").Append(Fmt.H(p.Get("ALIAS") ?? "")).Append("</b> (<span class='num'>").Append(Fmt.H(p.Get("LOGIN_NAME"))).Append("</span>)<br>")
              .Append(t1("Date")).Append(": <span class='num'>").Append(D(nowUtc)).Append("</span></p>");

            // 1. summary
            int tested = sets.Count(s => s.LastTest.HasValue && (nowUtc - s.LastTest.Value).TotalDays <= 31 && (s.TestResult ?? "").StartsWith("OK"));
            int recent = sets.Count(s => s.LastBackup.HasValue && (nowUtc - s.LastBackup.Value).TotalHours <= 48);
            sb.Append("<h2>1. ").Append(t1("Summary")).Append("</h2><table>");
            Row(sb, t1("Backup sets"), sets.Count.ToString(CultureInfo.InvariantCulture));
            Row(sb, t1("Backed up in the last 48 hours"), Status(recent == sets.Count && sets.Count > 0, recent + " / " + sets.Count));
            Row(sb, t1("Restore tested successfully in the last 31 days"), Status(tested == sets.Count && sets.Count > 0, tested + " / " + sets.Count));
            Row(sb, t1("Storage used"), "<span class='num'>" + Fmt.Size(p.GetLong("DATA_SIZE") + p.GetLong("RETAIN_SIZE")) + "</span>");
            Row(sb, t1("Suspected ransomware"), sets.Any(s => !string.IsNullOrEmpty(s.Suspect)) ? "<span class='bad'>" + t1("Yes — old versions are kept until it is checked") + "</span>" : "<span class='ok'>" + t1("None") + "</span>");
            sb.Append("</table>");

            // 2. sets
            sb.Append("<h2>2. ").Append(t1("Backup sets")).Append("</h2><table><tr><th>").Append(t1("Backup set")).Append("</th><th>").Append(t1("Type")).Append("</th><th>")
              .Append(t1("Schedule")).Append("</th><th>").Append(t1("Retention")).Append("</th><th>").Append(t1("Encryption")).Append("</th><th>").Append(t1("Last backup")).Append("</th><th>")
              .Append(t1("Runs in the last 30 days")).Append("</th></tr>");
            foreach (var s in sets)
            {
                var r = s.Set.Retention;
                string ret = r.Unit == "JOBS" ? T("{0} last backups", new object[] { r.Period }) : T("{0} days", new object[] { r.Period });
                if (r.Advanced) ret += "<br><span class='muted'>" + T("daily {0}, weekly {1}, monthly {2}, quarterly {3}, yearly {4}", new object[] { r.Daily, r.Weekly, r.Monthly, r.Quarterly, r.Yearly }) + "</span>";
                string enc = s.Set.KeyType == "DEFAULT" ? t1("AES-256, key generated on the customer's computer") : t1("AES-256, key from the customer's password");
                bool late = !s.LastBackup.HasValue || (nowUtc - s.LastBackup.Value).TotalHours > 48;
                sb.Append("<tr><td>").Append(Fmt.H(s.Set.Name)).Append("</td><td>").Append(Fmt.H(s.Set.Type)).Append("</td><td>").Append(Schedule(lang, s.Set)).Append("</td><td>").Append(ret)
                  .Append("</td><td>").Append(enc).Append("</td><td class='").Append(late ? "bad" : "ok").Append("'><span class='num'>").Append(D(s.LastBackup)).Append("</span></td><td><span class='num'>")
                  .Append(s.Runs30).Append("</span></td></tr>");
            }
            sb.Append("</table>");

            // 3. restore certificate
            sb.Append("<h2>3. ").Append(t1("Restore certificate")).Append("</h2><p>").Append(t1("A restore test restores a sample of files from the backup and compares them with the original fingerprints."))
              .Append("</p><table><tr><th>").Append(t1("Backup set")).Append("</th><th>").Append(t1("Last restore test")).Append("</th><th>").Append(t1("Result")).Append("</th><th>")
              .Append(t1("Restores in the last 12 months")).Append("</th></tr>");
            foreach (var s in sets)
            {
                bool ok = (s.TestResult ?? "").StartsWith("OK"), none = (s.TestResult ?? "").StartsWith("NOT_CHECKED");   // bug 110: nothing to compare is not "Failed"
                sb.Append("<tr><td>").Append(Fmt.H(s.Set.Name)).Append("</td><td><span class='num'>").Append(D(s.LastTest)).Append("</span></td><td class='").Append(s.LastTest.HasValue ? (ok ? "ok" : none ? "" : "bad") : "").Append("'>")
                  .Append(s.LastTest.HasValue ? (none ? t1("Nothing to compare") : (ok ? t1("Passed") : t1("Failed")) + " <span class='num'>" + Fmt.H((s.TestResult ?? "").Replace("OK ", "").Replace("FAILED ", "")) + "</span>") : t1("Not tested yet"))
                  .Append("</td><td><span class='num'>").Append(s.RestoresYear).Append("</span></td></tr>");
            }
            sb.Append("</table>");

            // 4. controls
            sb.Append("<h2>4. ").Append(t1("Security controls")).Append("</h2><table>");
            Row(sb, t1("Encryption"), t1("Files are encrypted on the customer's computer before they are sent (AES-256); the backup server stores only encrypted data and cannot read it."));
            Row(sb, t1("In transit"), t1("HTTPS (TLS) with the server's certificate pinned in the client."));
            Row(sb, t1("Second copy"), replication ? "<span class='ok'>" + t1("Yes — every backup is copied to a second server.") + "</span>" : t1("No second server is set up."));
            Row(sb, t1("Ransomware protection"), T("Unusual changes are detected automatically against what is normal for each backup set; deleted versions stay recoverable for {0} days and nothing is deleted while a suspicion is open.", new object[] { ResticStore.TrashDays }));
            Row(sb, t1("Sign-in of the customer"), userTwoFactor ? "<span class='ok'>" + t1("Password and two-factor code") + "</span>" : t1("Password only"));
            Row(sb, t1("Sign-in of the administrators"), adminTwoFactor ? "<span class='ok'>" + t1("Password and two-factor code") + "</span>" : t1("Password only"));
            Row(sb, t1("Audit trail"), t1("Every sign-in, restore and administrator action is written to the server's logs."));
            sb.Append("</table>");

            // 5. procedure
            sb.Append("<h2>5. ").Append(t1("Backup and restore procedure")).Append("</h2><ol>");
            foreach (var step in new[]
            {
                "The backup runs automatically on the schedule above. A failed run is reported to the IT provider at once, and so is a backup that has not run for two days.",
                "Every run is checked on the server: damaged data is set aside and sent again by the next run.",
                "Old versions are kept according to the retention above; deleted versions remain recoverable for a further period.",
                "A restore test runs automatically every month for every backup set, and its result appears in this report.",
                "A restore is done by the IT provider or by the customer from the client software or from the website, after signing in and entering the encryption password.",
                "The encryption password is kept by the customer; without it the backup cannot be read by anyone, including the IT provider.",
                "This report is produced at least every quarter and kept with the organisation's information security documents.",
            }) sb.Append("<li>").Append(t1(step)).Append("</li>");
            sb.Append("</ol><p class='muted'>").Append(t1("This report supports audits under data protection rules (for example GDPR, Israel's Privacy Protection Regulations (Data Security) and Amendment 13, ISO 27001). It is not a legal opinion."))
              .Append("</p><div class='sig'><div>").Append(t1("IT provider")).Append("</div><div>").Append(t1("Customer")).Append("</div></div></body></html>");
            return sb.ToString();
        }

        static void Row(StringBuilder sb, string k, string v) { sb.Append("<tr><th class='k'>").Append(k).Append("</th><td>").Append(v).Append("</td></tr>"); }
        static string Status(bool ok, string text) { return "<span class='" + (ok ? "ok" : "bad") + "'><span class='num'>" + Fmt.H(text) + "</span></span>"; }

        static string Schedule(string lang, BackupSetInfo s)
        {
            var time = s.Hour.ToString("00", CultureInfo.InvariantCulture) + ":" + s.Minute.ToString("00", CultureInfo.InvariantCulture);
            int days = s.Days.Length == 7 ? s.Days.Count(c => c != '-') : 7;
            var text = days == 7 ? L.T(lang, "Every day at {0}", time) : L.T(lang, "{0} days a week at {1}", days, time);
            if (s.LogIntervalMinutes > 0) text += "; " + L.T(lang, "transaction log every {0} minutes", s.LogIntervalMinutes);
            return Fmt.H(text);
        }

        /// <summary>The facts of one set from its profile entry, its run statistics and its restore logs.</summary>
        public static SetFacts Facts(string userDir, Profile p, BackupSetInfo s, DateTime nowUtc)
        {
            var e = p.FindSet(s.Id);
            Func<string, DateTime?> ms = a => { long v; return long.TryParse((string)e.Attribute(a), out v) && v > 0 ? RunId.FromUnixMs(v) : (DateTime?)null; };
            var restoreDir = Path.Combine(userDir, "logs", s.Id, "Restore");
            int restores = Directory.Exists(restoreDir) ? Directory.GetFiles(restoreDir, "*.log").Count(f => (nowUtc - File.GetLastWriteTimeUtc(f)).TotalDays <= 365) : 0;
            return new SetFacts
            {
                Set = s, LastBackup = ms("LAST_BACKUP_COMPLETE"), LastTest = ms("LAST_RESTORE_TEST"), TestResult = (string)e.Attribute("RESTORE_TEST_RESULT"),
                Suspect = (string)e.Attribute("SUSPECT_RANSOMWARE"), RestoresYear = restores,
                Runs30 = Insights.History(userDir, s.Id).Count(h => (nowUtc - h.Time).TotalDays <= 30),
            };
        }
    }
}
