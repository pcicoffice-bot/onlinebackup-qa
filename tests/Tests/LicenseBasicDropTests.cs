using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// Owner decision L-1 (docs/OWNER-DECISIONS.md; bug log row L-1, found reading the licence code in SH-07):
    /// "A mail when an unconfirmed licence drops to the basic edition, and a daily reminder; over the basic limit, existing
    /// customers keep backing up for a grace period, only new customers / sets are refused."
    ///  (a) the drop (TEMP_SINCE + 7 days without a confirmation) → ONE mail to the administrators saying so;
    ///  (b) while it stays basic: one reminder a day (counted from the drop), not more and not less, also across midnight;
    ///  (c) over the basic limit (500 GB stored / more than 10 computers): the existing customers' sets keep backing up during
    ///      the grace (LicenseCheckin.OverLimitGraceDays); a new customer (administrator or sign-up) and a new set are refused
    ///      with a clear licence message (402 LICENSE);
    ///  (d) after the grace: today's behaviour (the 507 LICENSE_STORAGE for new backup data) - the decision does not say
    ///      otherwise (open owner question), new customers / sets stay refused;
    ///  (e) a confirmation by the licensing centre ends the reminders and lifts the refusals.
    /// Signed with Env.TestKey (the variable Env sets once): no test here changes the verification key.
    /// </summary>
    public class LicenseBasicDropTests : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "oblic-l1-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        public LicenseBasicDropTests() { Directory.CreateDirectory(root); var _ = Env.TestKey; }
        public void Dispose() { try { Directory.Delete(root, true); } catch (Exception) { } }

        const string Pw = "Customer-Pass-1";
        static byte[] Rnd(int n, int seed) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }
        static string FreeUrl() { var t = new TcpListener(IPAddress.Loopback, 0); t.Start(); int port = ((IPEndPoint)t.LocalEndpoint).Port; t.Stop(); return "http://localhost:" + port; }

        /// <summary>The messages of the fake SMTP server that carry the text (raw, or in a base64 / quoted-printable part).</summary>
        static int Count(FakeSmtp smtp, string text)
        {
            List<string> all; lock (smtp.Messages) all = smtp.Messages.ToList();
            return all.Count(m => Decoded(m).Contains(text));
        }
        static string Decoded(string m)
        {
            var sb = new StringBuilder(m);
            foreach (var part in m.Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.None))
                try { sb.Append('\n').Append(Encoding.UTF8.GetString(Convert.FromBase64String(part.Replace("\r", "").Replace("\n", "")))); } catch (FormatException) { }
            // quoted-printable
            var s = m.Replace("=\r\n", "").Replace("=\n", ""); var bytes = new List<byte>();
            for (int i = 0; i < s.Length; i++)
                if (s[i] == '=' && i + 2 < s.Length && Uri.IsHexDigit(s[i + 1]) && Uri.IsHexDigit(s[i + 2])) { bytes.Add(Convert.ToByte(s.Substring(i + 1, 2), 16)); i += 2; }
                else bytes.AddRange(Encoding.UTF8.GetBytes(s[i].ToString()));
            sb.Append('\n').Append(Encoding.UTF8.GetString(bytes.ToArray()));
            return sb.ToString();
        }
        static int Mails(FakeSmtp smtp) { lock (smtp.Messages) return smtp.Messages.Count; }

        SystemConfig MailCfg(FakeSmtp smtp, string centre, DateTime t0, string lang = null)
        {
            var cfg = SystemConfig.Init(Path.Combine(root, "system"), "admin", "Admin-Pass-1", "localhost", new[] { Path.Combine(root, "home") + "|UNLIMITED|100" });
            cfg.Doc.Root.Elements("SMTP").Remove();
            cfg.Doc.Root.Add(new XElement("SMTP", new XAttribute("HOST", "127.0.0.1"), new XAttribute("PORT", smtp.Port), new XAttribute("SECURITY", "NONE")));
            cfg.Doc.Root.Elements("REPORT_SENDER").Remove(); cfg.Doc.Root.Add(new XElement("REPORT_SENDER", new XAttribute("EMAIL", "backup@example.invalid")));
            cfg.Doc.Root.Add(new XElement("ADMIN_CONTACT", new XAttribute("NAME", "Ops"), new XAttribute("EMAIL", "ops@example.invalid")));
            if (lang != null) cfg.Doc.Root.Element("BRANDING").SetAttributeValue("LANGUAGE", lang);
            var el = cfg.Doc.Root.Element("LICENSE");
            el.SetAttributeValue("KEY", License.Issue(Env.TestKey[0], "L-TEST", "Pilot IT", "PRO", cfg.ServerId, 50, 2000, License.AllModules, t0.AddDays(-30), t0.AddDays(300), 100, centre));
            // what a failed check-in at t0 leaves (LicenseCheckin.Run): the licence is unconfirmed since t0
            el.SetAttributeValue("ONLINE_STATUS", "UNREACHABLE"); el.SetAttributeValue("ONLINE_MESSAGE", "No connection to the licensing centre: timed out");
            el.SetAttributeValue("TEMP_SINCE", t0.ToString("o", CultureInfo.InvariantCulture));
            cfg.Save(); cfg.ResetLicense();
            return cfg;
        }

        /// <summary>
        /// L-1 (a) (b) (e): the licence is unconfirmed since t0 (22:00 UTC). For the 7 days of the temporary licence the
        /// notice sends nothing (that period has its own mail at its start). One second after the 7 days the licence drops
        /// to the basic edition: exactly ONE mail saying so, also when the check runs again at once. Then one reminder a day,
        /// due a whole day after the drop: checks every hour and at 23:59:59 / 00:00:00 / 00:00:01 add nothing across
        /// midnight; after 5 days exactly 5 reminders. The licensing centre confirms the licence (a real centre, a real
        /// check-in): no reminder for the next 3 days. A second unconfirmed period that drops again gets its own drop mail
        /// through the daily check-in itself (LicenseCheckin.Run).
        /// </summary>
        [Fact]
        public void L1_TheDropToBasic_OneMail_ThenOneReminderADay_AcrossMidnight_TheConfirmationEndsThem()
        {
            using (var smtp = new FakeSmtp())
            {
                var centre = FreeUrl();
                var t0 = new DateTime(2026, 10, 1, 22, 0, 0, DateTimeKind.Utc);
                var cfg = MailCfg(smtp, centre, t0);
                var users = new Users(cfg); var mailer = new Mailer(cfg);
                Action<DateTime> tick = at => LicenseCheckin.Notice(cfg, mailer, at);
                const string drop = "now runs the basic edition";
                const string reminder = "Reminder: the licence is still not confirmed";

                for (int h = 0; h <= 7 * 24; h++) tick(t0.AddHours(h));                                // the temporary licence: full, no notice
                Assert.Equal(0, Mails(smtp));

                var dropAt = t0.AddDays(LicenseCheckin.GraceDays);                                     // 2026-10-08 22:00
                tick(dropAt.AddSeconds(1));
                Assert.Equal(1, Mails(smtp));
                Assert.Equal(1, Count(smtp, drop));
                Assert.Equal(1, Count(smtp, "500 GB"));                                                 // the basic edition's limits are named
                tick(dropAt.AddSeconds(2)); tick(dropAt.AddMinutes(1));
                Assert.Equal(1, Mails(smtp));                                                           // ONE mail, not one per check

                var midnight = new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc);
                foreach (var at in new[] { midnight.AddSeconds(-1), midnight, midnight.AddSeconds(1) }) tick(at);
                for (int h = 1; h < 24; h++) tick(dropAt.AddHours(h));
                tick(dropAt.AddDays(1).AddSeconds(-1));
                Assert.Equal(1, Mails(smtp));                                                           // not more across midnight

                for (int h = 24; h <= 5 * 24; h++) tick(dropAt.AddHours(h));
                tick(dropAt.AddDays(5).AddMinutes(30));
                Assert.Equal(6, Mails(smtp));                                                           // the drop + one a day for 5 days
                Assert.Equal(5, Count(smtp, reminder));

                // (e) the licensing centre answers: confirmed, the reminders end
                var now = dropAt.AddDays(5).AddHours(2);
                using (new LicenseCenter(Env.TestKey[0], Path.Combine(root, "centre"), centre + "/"))
                    Assert.Equal("OK", LicenseCheckin.Run(cfg, users, mailer, now));
                cfg.ResetLicense();
                for (int h = 0; h <= 3 * 24; h++) tick(now.AddHours(h));
                Assert.Equal(6, Mails(smtp));

                // a later unconfirmed period: the daily check-in itself starts it (its existing mail) and, 7 days on, says the drop
                var t1 = now.AddDays(10);
                Assert.Equal("TEMPORARY", LicenseCheckin.Run(cfg, users, mailer, t1));
                Assert.Equal(7, Mails(smtp));
                Assert.Equal("TEMPORARY", LicenseCheckin.Run(cfg, users, mailer, t1.AddDays(LicenseCheckin.GraceDays).AddHours(1)));
                Assert.Equal(8, Mails(smtp));
                Assert.Equal(2, Count(smtp, drop));
            }
        }

        /// <summary>L-1 (a) in the server's language: a Hebrew server gets the drop mail in Hebrew (the existing i18n).</summary>
        [Fact]
        public void L1_TheDropMail_IsInTheServersLanguage_Hebrew()
        {
            using (var smtp = new FakeSmtp())
            {
                var t0 = new DateTime(2026, 10, 1, 22, 0, 0, DateTimeKind.Utc);
                var cfg = MailCfg(smtp, FreeUrl(), t0, "he");
                var mailer = new Mailer(cfg);
                LicenseCheckin.Notice(cfg, mailer, t0.AddDays(LicenseCheckin.GraceDays).AddMinutes(5));
                Assert.Equal(1, Mails(smtp));
                Assert.Equal(1, Count(smtp, "במהדורה הבסיסית"));
                Assert.Equal(0, Count(smtp, "now runs the basic edition"));
                LicenseCheckin.Notice(cfg, mailer, t0.AddDays(LicenseCheckin.GraceDays + 1).AddMinutes(5));
                Assert.Equal(2, Mails(smtp));
                Assert.Equal(1, Count(smtp, "תזכורת"));
            }
        }

        /// <summary>
        /// L-1 (c) (d) (e) through the real server: a PRO licence bound to a licensing centre that stops answering, 600 GB
        /// already stored on the server (another customer's kept data). The licence drops to the basic edition (500 GB):
        /// the existing customer's existing set still backs up (today: 507 to every run); a new set for him and a new
        /// customer (administrator's page and the client's sign-up) are refused with the licence named. After the grace:
        /// today's behaviour - the run is refused for the licence's volume (open owner question), new customers / sets
        /// still refused. The centre confirms: backups, a new set and a new customer are taken again.
        /// </summary>
        [Fact]
        public void L1_OverTheBasicLimit_ExistingSetsKeepBackingUpDuringTheGrace_NewCustomersAndSetsRefused_ConfirmationLiftsIt()
        {
            using (var env = new Env())
            {
                var centre = FreeUrl();
                env.SetLicense("PRO", 50, 2000, centre);
                env.CreateUser("keep2026", Pw, quotaGB: 0);
                env.CreateUser("filler2026", Pw, quotaGB: 0);
                var users = new Users(env.Cfg);
                var fp = users.LoadProfile("filler2026"); fp.SetAttr("RETAIN_SIZE", 600L * 1024 * 1024 * 1024); users.SaveProfile("filler2026", fp);

                var app = env.Agent("keep2026", Pw, null, "pc1");
                var src = Path.Combine(root, "src"); Directory.CreateDirectory(src);
                File.WriteAllBytes(Path.Combine(src, "ledger.xlsx"), Rnd(60000, 1)); File.WriteAllText(Path.Combine(src, "a.txt"), "one");
                var set = app.CreateSet(app.Interactive(Pw, null), Pw, new BackupSetInfo { Name = "Files", Sources = { src } });
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);

                Action<double> droppedDaysAgo = days =>
                {
                    var el = env.Cfg.Doc.Root.Element("LICENSE");
                    el.SetAttributeValue("ONLINE_STATUS", "UNREACHABLE");
                    el.SetAttributeValue("TEMP_SINCE", DateTime.UtcNow.AddDays(-LicenseCheckin.GraceDays - days).ToString("o", CultureInfo.InvariantCulture));
                    env.Cfg.Save(); env.Cfg.ResetLicense();
                };
                int n = 0;
                Func<string> changed = () => { n++; File.WriteAllText(Path.Combine(src, "a.txt"), "change " + n); File.SetLastWriteTimeUtc(Path.Combine(src, "a.txt"), DateTime.UtcNow.AddMinutes(n)); System.Threading.Thread.Sleep(1100); return "a.txt"; };
                Func<string, AgentException> newCustomer = login => Assert.Throws<AgentException>(() => env.CreateUser(login, Pw, quotaGB: 0));
                Action<AgentException> isLicence = e => { Assert.Equal(402, e.Status); Assert.Equal("LICENSE", e.Code); Assert.Contains("licence", e.Message); Assert.Contains("basic edition", e.Message); };

                // (c) dropped one day ago: inside the grace
                droppedDaysAgo(1);
                Assert.Equal("FREE", env.Cfg.License.Edition); Assert.Equal(500, env.Cfg.License.MaxStorageGB);
                changed();
                var r = app.Backup(set.Id);
                Assert.True(r.Result == "BS_STOP_SUCCESS", "an existing set must keep backing up during the grace: " + r.Result + "\n" + string.Join("\n", r.LogLines));
                var setRefused = Assert.Throws<AgentException>(() => app.CreateSet(app.Interactive(Pw, null), Pw, new BackupSetInfo { Name = "More", Sources = { src } }));
                isLicence(setRefused);
                isLicence(newCustomer("new2026"));
                var signup = Assert.Throws<ApiException>(() => Contract.Signup(users, env.Cfg, new Msg().Set("company", "New Co").Set("email", "it@newco.example").Set("login", "newco2026").Set("password", Pw), "198.51.100.7", DateTime.UtcNow));
                Assert.Equal(402, signup.Status); Assert.Equal("LICENSE", signup.Code);
                Assert.DoesNotContain("new2026", users.Logins()); Assert.DoesNotContain("newco2026", users.Logins());
                Assert.NotEqual(L.Tr("he", setRefused.Message), setRefused.Message);                     // the refusal has its Hebrew text

                // (d) after the grace: today's behaviour for the volume (open owner question), new ones still refused
                droppedDaysAgo(LicenseCheckin.OverLimitGraceDays + 1.0 / 24);
                changed();
                r = app.Backup(set.Id);
                Assert.NotEqual("BS_STOP_SUCCESS", r.Result);
                Assert.Contains(r.LogLines, l => l.Contains("licence limit"));
                isLicence(newCustomer("new2026"));

                // (e) the licensing centre confirms the licence: everything is taken again
                using (new LicenseCenter(Env.TestKey[0], env.Dir("centre"), centre + "/"))
                {
                    var l = env.Admin().Call("GET", "/api/admin/license?check=1");
                    Assert.Equal("OK", l["online"]); Assert.Equal("PRO", l["edition"]);
                }
                changed();
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);
                Assert.NotNull(app.CreateSet(app.Interactive(Pw, null), Pw, new BackupSetInfo { Name = "More", Sources = { src } }));
                env.CreateUser("new2026", Pw, quotaGB: 0);
                Assert.Contains("new2026", users.Logins());
            }
        }
    }
}
