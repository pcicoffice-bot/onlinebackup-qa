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
    /// The scheduler (AgentApp.Due / LastSlot) — component contract (tests/QA/specs.py AG-01):
    ///   purpose  start backups on time: once per due time, never twice, catch up a missed one once
    ///   input    a set due 09:00, 13:00 and 18:00 every day; a day simulated minute by minute; the agent restarted at
    ///            noon; the computer off 12:50–13:40; a run that fails
    ///   expected exactly one run at 09:00, 13:40 (the missed 13:00, once the delay is over) and 18:00; none twice after
    ///            the restart; a failed run is tried again every 15 minutes, not every minute
    /// The run itself is what the service records after a run (AgentApp.Backup → last-attempt.txt, BackupRun → LastSuccess).
    /// </summary>
    public class SchedulerTests
    {
        static BackupSetInfo Set()
        {
            return new BackupSetInfo
            {
                Id = "1700000000001", Name = "S", Days = "SMTWTFS", Hour = 9, Minute = 0, MissedDelayMinutes = 5,
                MoreSchedules = { new ScheduleSlot { Days = "SMTWTFS", Hour = 13, Minute = 0 }, new ScheduleSlot { Days = "SMTWTFS", Hour = 18, Minute = 0 } }
            };
        }

        static void Record(AgentApp app, BackupSetInfo s, DateTime nowLocal, bool ok)
        {
            var job = RunId.From(nowLocal.ToUniversalTime());
            Directory.CreateDirectory(app.Home.SetDir(s.Id));
            File.WriteAllText(Path.Combine(app.Home.SetDir(s.Id), "last-attempt.txt"), job + "\t" + (ok ? "BS_STOP_SUCCESS" : "BS_STOP_BY_SYSTEM_ERROR"));
            if (ok) { var st = new LocalState(app.Home.SetDir(s.Id)); st.LastSuccess = job; st.Save(); }
        }

        [Fact]
        public void ThreeTimesADay_EachRunsOnce_NotTwiceAfterARestart_TheMissedOneOnce()
        {
            var home = Path.Combine(Path.GetTempPath(), "obsched-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var s = Set();
            var day = new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Local);
            var app = new AgentApp(home);
            Record(app, s, day.AddDays(-1).AddHours(18).AddMinutes(1), true);   // yesterday's last run was done
            var runs = new List<DateTime>();
            for (var now = day.AddHours(7); now <= day.AddHours(21); now = now.AddMinutes(1))
            {
                if (now >= day.AddHours(12).AddMinutes(50) && now < day.AddHours(13).AddMinutes(40)) continue;   // the computer is off
                if (now == day.AddHours(12)) app = new AgentApp(home);                                             // the service restarts
                if (app.Due(s, now)) { runs.Add(now); Record(app, s, now, true); }
            }
            Assert.Equal(new[] { day.AddHours(9), day.AddHours(13).AddMinutes(40 + 5), day.AddHours(18) }, runs.ToArray());
        }

        [Fact]
        public void AFailedRun_IsTriedAgainEvery15Minutes_NotEveryMinute_AndStopsOnceItWorks()
        {
            var home = Path.Combine(Path.GetTempPath(), "obsched-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var s = Set();
            var day = new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Local);
            var app = new AgentApp(home);
            Record(app, s, day.AddDays(-1).AddHours(18).AddMinutes(1), true);
            var runs = new List<DateTime>();
            for (var now = day.AddHours(8).AddMinutes(30); now <= day.AddHours(12); now = now.AddMinutes(1))
                if (app.Due(s, now)) { runs.Add(now); Record(app, s, now, runs.Count >= 3); }   // fails twice, then works
            Assert.Equal(new[] { day.AddHours(9), day.AddHours(9).AddMinutes(15), day.AddHours(9).AddMinutes(30) }, runs.ToArray());
        }

        [Fact]
        public void Boundary_NoDayOfTheWeekChosen_NeverDue_AndASlotExactlyNowIsDue()
        {
            var home = Path.Combine(Path.GetTempPath(), "obsched-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var app = new AgentApp(home);
            var none = new BackupSetInfo { Id = "1700000000002", Days = "-------", Hour = 9, Minute = 0 };
            Assert.Null(AgentApp.LastSlot(none, new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Local)));
            Assert.False(app.Due(none, new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Local)));
            var s = new BackupSetInfo { Id = "1700000000003", Days = "SMTWTFS", Hour = 9, Minute = 0 };
            Record(app, s, new DateTime(2026, 3, 9, 9, 1, 0, DateTimeKind.Local), true);
            Assert.False(app.Due(s, new DateTime(2026, 3, 10, 8, 59, 0, DateTimeKind.Local)));
            Assert.True(app.Due(s, new DateTime(2026, 3, 10, 9, 0, 0, DateTimeKind.Local)));
        }

        /// <summary>
        /// Integration: a real backup through the agent and the server, then the scheduler asked again. A set whose last
        /// scheduled backup succeeded is not due until its next time — for every engine.
        /// </summary>
        [Theory]
        [InlineData("")]
        [InlineData("RESTIC")]
        public void AfterASuccessfulScheduledBackup_TheSetIsNotDueAgain_UntilItsNextTime(string engine)
        {
            if (engine == "RESTIC" && !File.Exists(Environment.GetEnvironmentVariable("OB_RESTIC") ?? "")) throw NotTested.Because("OB_RESTIC (the restic program) is not set - the RESTIC case");
            using (var env = new Env())
            {
                var src = env.Dir("src"); File.WriteAllText(Path.Combine(src, "a.txt"), "a");
                env.CreateUser("sched", "Customer-Pass-1");
                var app = env.Agent("sched", "Customer-Pass-1");
                var now = DateTime.Now; var slot = now.AddMinutes(-2);
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1",
                    new BackupSetInfo { Name = "Daily", Engine = engine, Sources = { src }, Days = "SMTWTFS", Hour = slot.Hour, Minute = slot.Minute });
                set = app.Sets().First(x => x.Id == set.Id);
                Assert.True(app.Due(set, now));
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);
                foreach (var later in new[] { 1, 20, 60, 180, 23 * 60 })
                    Assert.False(app.Due(set, now.AddMinutes(later)), "due again " + later + " minutes after a successful backup (" + (engine == "" ? "native" : engine) + ")");
                Assert.True(app.Due(set, slot.AddDays(1).AddMinutes(1)));
            }
        }
    }
}
