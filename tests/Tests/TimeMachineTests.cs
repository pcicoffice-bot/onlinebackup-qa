using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// TIME-020: the time machine — 25 days of a real server and a real computer in a few minutes. Every evening the
    /// schedule must start the backup at 22:00 (and not before); the computer is off for three days, then its backups fail
    /// for two; the server's nightly maintenance runs every morning. Checked: what a person would otherwise have to watch
    /// for a month — versions kept, "the backup did not run" mails and service calls, a failure call that closes itself
    /// when the backup works again, the mails a customer gets, and that no mail carries a password.
    /// </summary>
    public class TimeMachineTests
    {
        [Fact]
        public void TwentyFiveDays_SchedulesVersionsAlertsServiceCallsAndMails()
        {
            var real0 = DateTime.UtcNow;
            var start = new DateTime(real0.Year, real0.Month, real0.Day, 8, 0, 0, DateTimeKind.Utc);
            var jump = start - real0;
            SystemClock.Use(() => DateTime.UtcNow + jump);                 // the clock moves on by itself, and jumps when told
            Action<DateTime> go = (t) => { jump = t - DateTime.UtcNow; };
            Func<int, int, int, DateTime> at = (day, h, m) => start.Date.AddDays(day).AddHours(h).AddMinutes(m);

            using (var smtp = new FakeSmtp())
            using (var env = new Env())
            {
                var admin = env.Admin();
                admin.Call("POST", "/api/admin/settings", new Msg().Set("smtpSet", "1").Set("senderName", "Backup").Set("senderEmail", "backup@example.invalid").Set("contactsSet", "1")
                    .Set("brandPRODUCT", "Acme Backup").Add("smtp", new Msg().Set("host", "127.0.0.1").Set("port", smtp.Port).Set("security", "NONE"))
                    .Add("contacts", new Msg().Set("name", "Ops").Set("email", "ops@example.invalid")));
                env.CreateUser("month2026", "Customer-Pass-1");
                var app = env.Agent("month2026", "Customer-Pass-1", name: "office-pc");
                var src = env.Dir("src");
                File.WriteAllText(Path.Combine(src, "ledger.xlsx"), "day 0");
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1",
                    new BackupSetInfo { Name = "Office", Hour = 22, Minute = 0, Retention = new RetentionPolicy { Unit = "JOBS", Period = 7 }, Sources = { src } });
                var fail = Environment.OSVersion.Platform == PlatformID.Win32NT ? "cmd /c exit 3" : "sh -c \"exit 3\"";

                var off = new[] { 8, 9, 10 }; var broken = new[] { 16, 17 };
                var log = new List<string>(); int done = 0;
                for (int day = 0; day < 25; day++)
                {
                    go(at(day, 21, 59));
                    var early = app.Sets().Single(s => s.Id == set.Id);
                    if (!off.Contains(day - 1) && day > 0) Assert.False(app.Due(early, SystemClock.Now), "day " + day + ": due before 22:00");
                    if (!off.Contains(day))
                    {
                        go(at(day, 22, 1));
                        var s0 = app.Sets().Single(s => s.Id == set.Id);
                        Assert.True(app.Due(s0, SystemClock.Now), "day " + day + ": not due at 22:01");
                        if (day == broken[0]) Change(env, set.Id, s => { s.PreCommands = new List<string> { fail }; s.StopOnPreCommandFailure = true; });
                        if (day == broken[1] + 1) Change(env, set.Id, s => { s.PreCommands = new List<string>(); s.StopOnPreCommandFailure = false; });
                        File.WriteAllText(Path.Combine(src, "ledger.xlsx"), "day " + day);
                        var r = app.Backup(set.Id);
                        log.Add(day + ":" + r.Result);
                        if (broken.Contains(day)) Assert.False(r.Result.StartsWith("BS_STOP_SUCCESS", StringComparison.Ordinal), "day " + day + " should fail");
                        else { Assert.True(r.Result.StartsWith("BS_STOP_SUCCESS", StringComparison.Ordinal), "day " + day + ": " + r.Result); done++; }
                    }
                    go(at(day + 1, 6, 0));
                    env.Api.Maintenance(SystemClock.UtcNow);                    // the server's night: versions kept, missed backups, checks
                    if (day == 10) Assert.Contains(Calls(env), c => c["subject"].StartsWith("Backup did not run", StringComparison.Ordinal) && c["status"] != "Closed");
                    if (day == 17) Assert.Contains(Calls(env), c => c["kind"] == "fail" && c["status"] != "Closed");
                }

                // versions kept: the last 7 backups only
                var points = app.RestoreFor(app.Interactive("Customer-Pass-1", null), set.Id).Points();
                Assert.True(points.Count == 7, "kept " + points.Count + " restore points: " + string.Join(", ", log));
                // the service calls opened by themselves (checked on days 10 and 17) went away by themselves when the backup
                // worked again — nobody had worked on them, so they are removed, not left as noise
                Assert.DoesNotContain(env.Api.Calls.List("open"), c => c["source"] == "auto");
                // the mails: the missed backup reached the administrators; no mail carries a password
                Assert.True(smtp.Has("has not completed a backup since"), "no missed-backup mail");
                Assert.DoesNotContain("Customer-Pass-1", smtp.All);
                Assert.True(done >= 20, "only " + done + " backups");
            }
        }

        static List<Msg> Calls(Env env) { return env.Api.Calls.List("all"); }

        static void Change(Env env, string setId, Action<BackupSetInfo> edit)
        {
            var a = env.Admin();
            var s = BackupSetInfo.FromXml(System.Xml.Linq.XElement.Parse(a.Call("GET", "/api/admin/users/month2026/sets/" + setId)["set"]));
            edit(s);
            a.Call("POST", "/api/admin/users/month2026/sets/" + setId, new Msg().Set("set", s.ToXml().ToString()));
        }
    }
}
