using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// Bug 128 (research candidate (ו)): the server's "backup did not run" alert (mail + BackupErrors line) and its service
    /// call used a fixed 48 hours (the call: the customer's "hours without a backup") whatever the set's schedule — a set
    /// that runs once a week was reported as not running every week, two days after each good backup.
    /// Rule kept for sets that run at least daily (48 h, the call's hours unchanged); a set whose longest gap between two
    /// scheduled runs is longer than a day is reported only after that gap plus one day (one missed run + the same day of
    /// grace a daily set has).
    /// ORACLE: the MISSED BACKUP lines of the server's BackupErrors log and the server's service calls, at boundary
    /// instants computed here from the schedule (gap + 24 h − 1 s → nothing; gap + 24 h → reported).
    /// </summary>
    [Collection("NightM-TZ")]
    public class MissedScheduleAwareTests
    {
        static int MissedLines(string login, string setId)
        {
            var d = SysLog.Dir("BackupErrors");
            if (!Directory.Exists(d)) return 0;
            return Directory.GetFiles(d).SelectMany(f => Atomic.ReadShared(f).Split('\n')).Count(l => l.Contains(login + "\t" + setId + "\t") && l.Contains("MISSED BACKUP"));
        }
        static int MissedCalls(Env env, string setName)
        {
            return env.Api.Calls.List("all").Count(c => c["subject"] == "Backup did not run — " + setName && c["status"] != "Closed");
        }

        DateTime NewSet(Env env, string login, string name, string days, ScheduleSlot more, out string setId)
        {
            env.CreateUser(login, "Customer-Pass-1");
            var app = env.Agent(login, "Customer-Pass-1");
            var info = new BackupSetInfo { Name = name, Sources = { env.Dir("src") }, Vss = false, Days = days, Hour = 22, Minute = 0 };
            if (more != null) info.MoreSchedules.Add(more);
            var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", info);
            setId = set.Id;
            return RunId.FromUnixMs(long.Parse(set.Id, CultureInfo.InvariantCulture));   // no backup yet: counted from the creation
        }

        [Fact]
        public void AWeeklySet_IsNotReportedTwoDaysLater_OnlyAfterItsWeekPlusOneDay()
        {
            using (var env = new Env())
            {
                var created = NewSet(env, "msaw1", "Weekly", "-M-----", null, out var id);
                env.Api.Maintenance(created.AddHours(48));
                Assert.True(MissedLines("msaw1", id) == 0, "a set that runs once a week was reported as not running 48 h after its start");
                Assert.True(MissedCalls(env, "Weekly") == 0, "a service call was opened for a weekly set after 48 h");
                env.Api.Maintenance(created.AddHours(7 * 24 + 24) - TimeSpan.FromSeconds(1));
                Assert.Equal(0, MissedLines("msaw1", id)); Assert.Equal(0, MissedCalls(env, "Weekly"));
                env.Api.Maintenance(created.AddHours(7 * 24 + 24));
                Assert.Equal(1, MissedLines("msaw1", id)); Assert.Equal(1, MissedCalls(env, "Weekly"));
            }
        }

        [Fact]
        public void TwoDaysAWeek_TheLongestGapCounts()
        {
            using (var env = new Env())
            {
                // Monday and Thursday 22:00: gaps 3 and 4 days → reported after 4 + 1 = 5 days
                var created = NewSet(env, "msaw2", "MonThu", "-M--T--", null, out var id);
                env.Api.Maintenance(created.AddHours(5 * 24) - TimeSpan.FromSeconds(1));
                Assert.Equal(0, MissedLines("msaw2", id));
                env.Api.Maintenance(created.AddHours(5 * 24));
                Assert.Equal(1, MissedLines("msaw2", id));
            }
        }

        [Fact]
        public void AWeeklyDayPlusASecondDailyTime_IsDaily_48HoursAsBefore()
        {
            using (var env = new Env())
            {
                var created = NewSet(env, "msaw3", "Mixed", "-M-----", new ScheduleSlot { Days = "SMTWTFS", Hour = 6, Minute = 30 }, out var id);
                env.Api.Maintenance(created.AddHours(48) - TimeSpan.FromSeconds(1)); Assert.Equal(0, MissedLines("msaw3", id));
                env.Api.Maintenance(created.AddHours(48)); Assert.Equal(1, MissedLines("msaw3", id));
            }
        }

        [Fact]
        public void ADailySet_Unchanged_48Hours()
        {
            using (var env = new Env())
            {
                var created = NewSet(env, "msaw4", "Daily", "SMTWTFS", null, out var id);
                env.Api.Maintenance(created.AddHours(48) - TimeSpan.FromSeconds(1)); Assert.Equal(0, MissedLines("msaw4", id)); Assert.Equal(0, MissedCalls(env, "Daily"));
                env.Api.Maintenance(created.AddHours(48)); Assert.Equal(1, MissedLines("msaw4", id)); Assert.Equal(1, MissedCalls(env, "Daily"));
            }
        }

        /// <summary>Bug 128, same pattern in the customer's data-protection report: the "Last backup" cell of each set was red
        /// after 48 h whatever the schedule. A weekly set 3 days after its backup is on time; a daily set 49 h after is late.</summary>
        [Fact]
        public void TheDataProtectionReport_MarksLateByTheSetsSchedule()
        {
            var now = new DateTime(2026, 4, 1, 12, 0, 0, DateTimeKind.Utc);
            var p = Profile.Create("acme", "Acme", "x", "en", "UTC");
            Func<string, int, string> cell = (days, hoursAgo) =>
            {
                var facts = new List<Compliance.SetFacts> { new Compliance.SetFacts { Set = new BackupSetInfo { Id = "1700000000777", Name = "Office", Days = days }, LastBackup = now.AddHours(-hoursAgo), LastTest = now.AddDays(-1), TestResult = "OK 5/5" } };
                var html = Compliance.Html("en", "OnlineBackup", "Pilot IT", p, facts, false, false, false, now);
                var at = html.IndexOf("<h2>2. ", StringComparison.Ordinal); var end = html.IndexOf("<h2>3. ", StringComparison.Ordinal);
                return html.Substring(at, end - at);
            };
            Assert.True(cell("-M-----", 72).Contains("<td class='ok'>"), "a weekly set 3 days after its backup is printed late: " + cell("-M-----", 72));
            Assert.Contains("<td class='bad'>", cell("-M-----", 7 * 24 + 25));
            Assert.Contains("<td class='bad'>", cell("SMTWTFS", 49));
            Assert.Contains("<td class='ok'>", cell("SMTWTFS", 47));
        }

        [Theory]
        [InlineData("SMTWTFS", 22, 0, null, 24)]
        [InlineData("-M-----", 22, 0, null, 168)]
        [InlineData("-M--T--", 22, 0, null, 96)]
        [InlineData("-M-----", 22, 0, "SMTWTFS", 24)]
        [InlineData("-------", 22, 0, null, 0)]
        public void LongestScheduleGap(string days, int h, int m, string moreDays, int hours)
        {
            var s = new BackupSetInfo { Days = days, Hour = h, Minute = m };
            if (moreDays != null) s.MoreSchedules.Add(new ScheduleSlot { Days = moreDays, Hour = 6, Minute = 30 });
            Assert.Equal(hours, Api.LongestScheduleGapHours(s));
        }
    }
}
