using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Xml.Linq;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// Pilot release blockers, state and history — COMPONENT layer (no backup server is started):
    ///   SH-05 suspect ransomware and freeze retention (Insights: the verdict and the learning; ResticStore: what the
    ///         freeze keeps — the trash of a repository)
    ///   SH-07 the licence when the check-in fails (License.Effective, LicenseCheckin.Run against an unreachable and then a
    ///         real licensing centre, Users: the computers that back up) — backups must not be lost or silently stopped
    /// Contracts: tests/QA/specs.py. Oracle: known numbers in, the expected verdict / edition / dates out; SHA-256 of files.
    /// </summary>
    public class PilotStateComponentTests : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "obpilot-state-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        public PilotStateComponentTests() { Directory.CreateDirectory(root); }
        public void Dispose() { try { Directory.Delete(root, true); } catch (Exception) { } }

        static string Sha(string f) { using (var s = File.OpenRead(f)) using (var h = SHA256.Create()) return Bytes.Hex(h.ComputeHash(s)); }

        // ================================================================== SH-01

        /// <summary>
        /// SH-01 happy + boundary + recovery (RunLog, the history of every run): a success, then a failure whose report has
        /// no result, then a run stopped by the technician, across midnight (two day files) — read back after a restart (a
        /// new RunLog over the same folder): all three, newest first, each with its colour (failure red, never green); a
        /// set name with a tab and a line break does not split or shift its row; a line cut by a power failure is skipped
        /// and every other run still shows; the history keeps exactly 400 days.
        /// </summary>
        [Fact]
        public void SH01_EveryRunIsInTheHistory_NewestFirst_WithItsColour_AfterARestart_ACutLineSkipped_400DaysKept()
        {
            var cfg = SystemConfig.Init(Path.Combine(root, "system"), "admin", "Admin-Pass-1", "localhost", new[] { Path.Combine(root, "home") + "|UNLIMITED|100" });
            var set = new BackupSetInfo { Id = "1700000000077", Name = "Office\tfiles\nmain", Computer = "PC-1" };
            var t = new DateTime(2026, 10, 5, 23, 50, 0, DateTimeKind.Utc);
            var log = new RunLog(cfg);
            log.Add(t, "acme2026", set, "Backup", "2026-10-05-23-50-00", new Msg().Set("result", "BS_STOP_SUCCESS").Set("new", 5), null);
            log.Add(t.AddMinutes(20), "acme2026", set, "Backup", "2026-10-06-00-10-00", new Msg().Set("new", 0), null);           // no result reported
            log.Add(t.AddMinutes(40), "acme2026", set, "Backup", "2026-10-06-00-30-00", new Msg().Set("result", "BS_STOP_BY_USER"), null);
            File.AppendAllText(Path.Combine(cfg.SystemHome, "runs", "20261006.log"), RunId.UnixMs(t.AddMinutes(41)) + "\tacme2026\t1700000000077");   // cut by a power failure

            var rows = new RunLog(SystemConfig.Load(cfg.SystemHome)).Since(t.AddHours(-1), t.AddHours(2));   // after a restart
            Assert.Equal(new[] { "2026-10-06-00-30-00", "2026-10-06-00-10-00", "2026-10-05-23-50-00" }, rows.Select(r => r["job"]).ToArray());
            Assert.Equal(new[] { "stopped", "bad", "ok" }, rows.Select(r => r["status"]).ToArray());
            Assert.Equal("BS_STOP_BY_SYSTEM_ERROR", rows[1]["result"]);
            Assert.All(rows, r => Assert.Equal("1700000000077", r["set"]));
            Assert.All(rows, r => Assert.Equal("acme2026", r["login"]));

            var keep = new RunLog(cfg);
            Assert.Equal(0, keep.Purge(new DateTime(2026, 10, 5, 23, 50, 0, DateTimeKind.Utc).AddDays(400)));   // the oldest run (23:50) exactly 400 days old: kept (bug 108: counted from the run, not from the start of its day)
            Assert.Equal(3, keep.Since(t.AddHours(-1), t.AddHours(2)).Count);
            Assert.Equal(1, keep.Purge(new DateTime(2026, 10, 5, 23, 50, 0, DateTimeKind.Utc).AddDays(400).AddSeconds(1)));   // one second older: that run goes, and with it its (now empty) day file
            Assert.Equal(new[] { "2026-10-06-00-30-00", "2026-10-06-00-10-00" }, keep.Since(t.AddHours(-1), t.AddHours(2)).Select(r => r["job"]).ToArray());
        }

        // ================================================================== SH-05

        static RunStat Run(DateTime t, long prev, long nw, long upd, long del, string ext = "docx", long extCount = 0)
        {
            return new RunStat { Time = t, Prev = prev, New = nw, Upd = upd, Del = del, Bytes = 1000, TopExt = ext, TopExtCount = extCount, Usage = 1000 };
        }

        /// <summary>
        /// SH-05 happy: a run that rewrote most files under one new extension (80 of 100 deleted, 80 ".locked" added) is
        /// suspect, with a reason that says how many files of how many; an ordinary run of the same set (5 of 100 changed)
        /// is not — before the set's normal is learned (the fixed rule: 50 files and 30%).
        /// </summary>
        [Fact]
        public void SH05_MostFilesRewrittenUnderANewExtension_IsSuspect_WithItsReason_AnOrdinaryRunIsNot()
        {
            var t = new DateTime(2026, 10, 1, 22, 0, 0, DateTimeKind.Utc);
            var attack = Insights.Check(new List<RunStat>(), Run(t, 100, 80, 0, 80, "locked", 80), 50, 30);
            Assert.True(attack.Suspect);
            Assert.False(attack.Learned);
            Assert.Contains("80", attack.Why); Assert.Contains("100", attack.Why);
            var ordinary = Insights.Check(new List<RunStat>(), Run(t, 100, 2, 5, 0, "docx", 3), 50, 30);
            Assert.False(ordinary.Suspect);
            Assert.Null(ordinary.Why);
        }

        /// <summary>
        /// SH-05 recovery: the set's history on disk (Insights.Record / History) — 12 ordinary runs (2% changed), then an
        /// attack (90% changed) recorded as suspect. Read back, the attack keeps its mark; the learned normal stays 2%
        /// (the attack does not become "normal"); the next ordinary run is not suspect (no lasting false alarm after the
        /// administrator releases the set), and a second attack is caught again.
        /// </summary>
        [Fact]
        public void SH05_AfterASuspectRun_TheLearningIgnoresIt_TheNextOrdinaryRunIsNotSuspect_ASecondAttackIsCaught()
        {
            var userDir = Path.Combine(root, "user"); const string set = "1700000000001";
            var t = new DateTime(2026, 9, 1, 22, 0, 0, DateTimeKind.Utc);
            for (int i = 0; i < 12; i++) Insights.Record(userDir, set, Run(t.AddDays(i), 1000, 5, 20, 0, "docx", 10));
            var attackRun = Run(t.AddDays(12), 1000, 0, 900, 0, "docx", 900);
            var v = Insights.Check(Insights.History(userDir, set), attackRun, 50, 30);
            Assert.True(v.Suspect); Assert.True(v.Learned);
            attackRun.Suspect = true; Insights.Record(userDir, set, attackRun);

            var h = Insights.History(userDir, set);
            Assert.Equal(13, h.Count);
            Assert.True(h.Last().Suspect);
            Assert.Equal(12, h.Count(x => !x.Suspect));
            var next = Insights.Check(h, Run(t.AddDays(13), 1000, 5, 20, 0, "docx", 10), 50, 30);
            Assert.False(next.Suspect);
            Assert.Equal(0.02, next.Normal, 6);                                                 // the normal is still the ordinary runs'
            var again = Insights.Check(h, Run(t.AddDays(13), 1000, 0, 850, 0, "docx", 850), 50, 30);
            Assert.True(again.Suspect);
        }

        /// <summary>
        /// SH-05 integrity: what the freeze keeps. A pack a compromised computer deletes from its repository (restic forget
        /// --prune) goes to the trash byte-identical; the trash is not emptied before its delay (the server skips emptying
        /// it altogether while retention is frozen); putting the trash back restores every deleted file byte-identical
        /// under its name.
        /// </summary>
        [Fact]
        public void SH05_PacksDeletedDuringAnAttack_StayByteIdenticalInTheTrash_AndComeBackByteIdentical()
        {
            var rs = new ResticStore(Path.Combine(root, "user"), "1700000000002"); rs.Create();
            var packs = new Dictionary<string, byte[]>();
            var rnd = new Random(5);
            for (int i = 0; i < 3; i++)
            {
                var b = new byte[20000 + i]; rnd.NextBytes(b);
                var name = Bytes.Hex(Bytes.Sha256(b));
                Assert.Equal(b.Length, rs.Save("data", name, new MemoryStream(b), -1));
                packs[name] = b;
            }
            var t0 = DateTime.UtcNow;
            foreach (var n in packs.Keys) rs.Delete("data", n);                                      // the attacker's prune
            foreach (var n in packs.Keys) Assert.False(File.Exists(rs.FilePath("data", n)));
            Assert.Equal(0, rs.PurgeTrash(t0.AddDays(ResticStore.TrashDays - 1), ResticStore.TrashDays));   // within the delay: nothing removed
            Assert.Equal(3, rs.RestoreTrash());
            foreach (var kv in packs) Assert.Equal(Bytes.Hex(Bytes.Sha256(kv.Value)), Sha(rs.FilePath("data", kv.Key)));
        }

        // ================================================================== SH-07

        sealed class VendorKey : IDisposable
        {
            public readonly string[] Pair = License.KeyGen();
            readonly string before = Environment.GetEnvironmentVariable("OB_LICENSE_PUBKEY"), ips = Environment.GetEnvironmentVariable("OB_LOCAL_IPS");
            public VendorKey() { Environment.SetEnvironmentVariable("OB_LICENSE_PUBKEY", Pair[1]); Environment.SetEnvironmentVariable("OB_LOCAL_IPS", "10.0.0.5"); }
            public void Dispose() { Environment.SetEnvironmentVariable("OB_LICENSE_PUBKEY", before); Environment.SetEnvironmentVariable("OB_LOCAL_IPS", ips); }
        }

        static XElement State(string status, DateTime? tempSince)
        {
            var e = new XElement("LICENSE", new XAttribute("ONLINE_STATUS", status), new XAttribute("ONLINE_MESSAGE", "No connection to the licensing centre: timed out"));
            if (tempSince.HasValue) e.SetAttributeValue("TEMP_SINCE", tempSince.Value.ToString("o", CultureInfo.InvariantCulture));
            return e;
        }

        /// <summary>
        /// SH-07 happy + boundary + recovery (License.Effective): a PRO licence bound to a licensing centre. Confirmed: PRO.
        /// Not confirmed since t0: still PRO with every module, marked temporary until exactly t0 + 7 days — at that very
        /// moment still PRO, one second later the basic edition with a reason that says why (never silent). Revoked: the
        /// basic edition at once. Confirmed again: PRO, not temporary.
        /// </summary>
        [Fact]
        public void SH07_AnUnconfirmedLicence_StaysFullExactly7Days_ThenBasicWithItsReason_AndAConfirmationBringsItBack()
        {
            using (var key = new VendorKey())
            {
                var t0 = new DateTime(2026, 10, 1, 3, 0, 0, DateTimeKind.Utc);
                var text = License.Issue(key.Pair[0], "L-PILOT", "Pilot IT", "PRO", "OB-PILOT-1", 50, 2000, License.AllModules, t0.AddDays(-30), t0.AddDays(300), 100, "http://centre.example");
                var ok = License.Effective(text, "OB-PILOT-1", t0, State("OK", null));
                Assert.Equal("PRO", ok.Edition); Assert.False(ok.Temporary);

                var during = License.Effective(text, "OB-PILOT-1", t0.AddDays(3), State("UNREACHABLE", t0));
                Assert.Equal("PRO", during.Edition); Assert.True(during.Temporary);
                Assert.Equal(t0.AddDays(LicenseCheckin.GraceDays), during.TemporaryUntil);
                Assert.Equal(50, during.MaxUsers); Assert.Equal(100, during.MaxDevices); Assert.True(License.AllModules.All(during.Has));

                Assert.Equal("PRO", License.Effective(text, "OB-PILOT-1", t0.AddDays(7), State("UNREACHABLE", t0)).Edition);   // the last moment of the 7 days
                var after = License.Effective(text, "OB-PILOT-1", t0.AddDays(7).AddSeconds(1), State("UNREACHABLE", t0));
                Assert.Equal("FREE", after.Edition);
                Assert.Contains("7 days", after.Reason);
                Assert.Contains("licensing centre", after.Reason);

                Assert.Equal("FREE", License.Effective(text, "OB-PILOT-1", t0, State("REVOKED", null)).Edition);
                var back = License.Effective(text, "OB-PILOT-1", t0.AddDays(9), State("OK", null));
                Assert.Equal("PRO", back.Edition); Assert.False(back.Temporary);
            }
        }

        /// <summary>
        /// SH-07 failure + recovery (LicenseCheckin.Run): the licensing centre cannot be reached. The first failed check-in
        /// makes the licence temporary and alerts the administrators once; later failures (the next days) neither move
        /// the start of the 7 days (the grace is never stretched or cut short) nor send another mail; the server's licence
        /// stays full meanwhile. When the centre answers (a real centre with the vendor's key), the check-in is OK, the
        /// temporary mark is gone and the licence is full again — also after the grace had run out.
        /// </summary>
        [Fact]
        public void SH07_FailedCheckIns_StartTheGraceOnce_OneMail_TheLicenceStaysFull_ThenTheCentreAnswers_AndItIsFullAgain()
        {
            using (var key = new VendorKey())
            using (var smtp = new FakeSmtp())
            {
                var t = new TcpListener(IPAddress.Loopback, 0); t.Start(); int port = ((IPEndPoint)t.LocalEndpoint).Port; t.Stop();
                var centre = "http://localhost:" + port;
                var cfg = SystemConfig.Init(Path.Combine(root, "system"), "admin", "Admin-Pass-1", "localhost", new[] { Path.Combine(root, "home") + "|UNLIMITED|100" });
                cfg.Doc.Root.Elements("SMTP").Remove();
                cfg.Doc.Root.Add(new XElement("SMTP", new XAttribute("HOST", "127.0.0.1"), new XAttribute("PORT", smtp.Port), new XAttribute("SECURITY", "NONE")));
                cfg.Doc.Root.Elements("REPORT_SENDER").Remove(); cfg.Doc.Root.Add(new XElement("REPORT_SENDER", new XAttribute("EMAIL", "backup@example.invalid")));
                cfg.Doc.Root.Add(new XElement("ADMIN_CONTACT", new XAttribute("NAME", "Ops"), new XAttribute("EMAIL", "ops@example.invalid")));
                var now = DateTime.UtcNow;
                cfg.Doc.Root.Element("LICENSE").SetAttributeValue("KEY", License.Issue(key.Pair[0], "L-PILOT-2", "Pilot IT", "PRO", cfg.ServerId, 50, 2000, License.AllModules, now.AddDays(-30), now.AddDays(300), 100, centre));
                cfg.Save(); cfg.ResetLicense();
                var users = new Users(cfg); var mailer = new Mailer(cfg);
                var clock = now; cfg.Clock = () => clock;
                Func<string> tempSince = () => (string)cfg.Doc.Root.Element("LICENSE").Attribute("TEMP_SINCE");

                Assert.Equal("TEMPORARY", LicenseCheckin.Run(cfg, users, mailer, clock));
                var since = tempSince();
                Assert.Equal(now.ToString("o", CultureInfo.InvariantCulture), since);
                Assert.Equal("UNREACHABLE", (string)cfg.Doc.Root.Element("LICENSE").Attribute("ONLINE_STATUS"));
                int mails; lock (smtp.Messages) mails = smtp.Messages.Count;
                Assert.Equal(1, mails);
                for (int day = 1; day <= 6; day++)
                {
                    clock = now.AddDays(day);
                    Assert.Equal("TEMPORARY", LicenseCheckin.Run(cfg, users, mailer, clock));
                    Assert.Equal(since, tempSince());                                                   // still counted from the first failure
                    cfg.ResetLicense();
                    Assert.Equal("PRO", cfg.License.Edition); Assert.True(cfg.License.Temporary);
                }
                lock (smtp.Messages) Assert.Equal(1, smtp.Messages.Count);                             // one mail, not one a day

                clock = now.AddDays(8); cfg.ResetLicense();
                Assert.Equal("FREE", cfg.License.Edition);                                              // the grace ran out

                using (new LicenseCenter(key.Pair[0], Path.Combine(root, "centre"), centre + "/"))
                {
                    Assert.Equal("OK", LicenseCheckin.Run(cfg, users, mailer, clock));
                    Assert.Null(tempSince());
                    cfg.ResetLicense();
                    Assert.Equal("PRO", cfg.License.Edition); Assert.False(cfg.License.Temporary);
                }
            }
        }

        /// <summary>
        /// SH-07 boundary (backups are not stopped): 12 computers back up under a PRO licence bound to a centre that stops
        /// answering; after the 7 days the server falls to the basic edition (10 computers). Every one of the 12 computers
        /// is still accepted for its backups, and each may register again (a reinstall); only a 13th, new computer is
        /// refused — with the licence named as the reason.
        /// </summary>
        [Fact]
        public void SH07_AfterTheGrace_TheBasicEditionKeepsEveryExistingComputerBackingUp_OnlyANewOneIsRefusedWithTheReason()
        {
            using (var key = new VendorKey())
            {
                var cfg = SystemConfig.Init(Path.Combine(root, "system"), "admin", "Admin-Pass-1", "localhost", new[] { Path.Combine(root, "home") + "|UNLIMITED|100" });
                var now = DateTime.UtcNow;
                var el = cfg.Doc.Root.Element("LICENSE");
                el.SetAttributeValue("KEY", License.Issue(key.Pair[0], "L-PILOT-3", "Pilot IT", "PRO", cfg.ServerId, 50, 2000, License.AllModules, now.AddDays(-30), now.AddDays(300), 100, "http://localhost:9/"));
                cfg.Save(); cfg.ResetLicense();
                var users = new Users(cfg);
                users.Create("acme2026", "Customer-Pass-1", "Acme", null, "COMPRESSED", "it@acme.example", "203.0.113.5");
                var tokens = Enumerable.Range(1, 12).Select(i => users.RegisterDevice("acme2026", "PC-" + i, "203.0.113.5")).ToList();

                el.SetAttributeValue("ONLINE_STATUS", "UNREACHABLE"); el.SetAttributeValue("TEMP_SINCE", now.ToString("o", CultureInfo.InvariantCulture));
                cfg.Save(); cfg.Clock = () => now.AddDays(8); cfg.ResetLicense();
                Assert.Equal("FREE", cfg.License.Edition);
                Assert.Equal(10, cfg.License.MaxDevices);

                foreach (var tk in tokens) Assert.Equal("acme2026", users.CheckDevice(tk, "203.0.113.5"));   // every computer still backs up
                Assert.NotNull(users.RegisterDevice("acme2026", "PC-7", "203.0.113.5"));                    // a reinstall of one of them
                var e = Assert.Throws<ApiException>(() => users.RegisterDevice("acme2026", "PC-13", "203.0.113.5"));
                Assert.Equal(402, e.Status); Assert.Equal("LICENSE", e.Code);
                Assert.Contains("licence", e.Message);
            }
        }
    }
}
