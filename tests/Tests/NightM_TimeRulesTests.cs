using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// Night QA agent M — every time-based rule other than the schedule, at its exact boundary (one tick before, at, one
    /// tick after) and across midnight / month / year ends:
    ///   retention DAYS (exactly N days) and JOBS · log-mode interval (LogDue) · restore-test 30 days (RestoreTestDue) ·
    ///   run lease (SetStore.Lease) · the once-a-day "could not start" report (ReportCannotRun, 20 h) · the server's
    ///   "no backup for 48 hours" alert (MissedBackupCheck / LAST_MISSED_ALERT, every 24 h) · run ids around midnight.
    /// ORACLE: the boundary instants are computed in the test from the rule as documented (e.g. T + 30 days), and the
    /// outcome is read outside the function under test where it can be: the files on disk, the server's run history,
    /// the server's BackupErrors log.
    /// </summary>
    [Collection("NightM-TZ")]
    public class NightM_TimeRulesTests : IDisposable
    {
        readonly List<string> dirs = new List<string>();
        string NewDir(string p) { var d = Path.Combine(Path.GetTempPath(), p + Guid.NewGuid().ToString("N").Substring(0, 8)); dirs.Add(d); return d; }
        public void Dispose() { SystemClock.Use(null); foreach (var d in dirs) try { Directory.Delete(d, true); } catch (Exception) { } }

        static readonly TimeSpan Tick = TimeSpan.FromTicks(1);
        static DateTime U(int y, int mo, int d, int h = 0, int mi = 0, int s = 0) { return new DateTime(y, mo, d, h, mi, s, DateTimeKind.Utc); }

        // ------------------------------------------------------------------ RunId at midnight

        [Fact]
        public void RunIds_AreUtc_AndSortInTimeOrder_AcrossMidnightMonthAndYearEnds()
        {
            var t = new[] { U(2026, 12, 31, 23, 59, 59), U(2027, 1, 1, 0, 0, 0), U(2028, 2, 28, 23, 59, 59), U(2028, 2, 29, 0, 0, 0), U(2028, 3, 1, 0, 0, 0) };
            var ids = t.Select(RunId.From).ToArray();
            Assert.Equal(new[] { "2026-12-31-23-59-59", "2027-01-01-00-00-00", "2028-02-28-23-59-59", "2028-02-29-00-00-00", "2028-03-01-00-00-00" }, ids);
            Assert.Equal(ids.OrderBy(x => x, StringComparer.Ordinal), ids);   // ordinal order = time order (the store relies on it)
            for (int i = 0; i < t.Length; i++) { Assert.Equal(t[i], RunId.Parse(ids[i])); Assert.Equal(DateTimeKind.Utc, RunId.Parse(ids[i]).Kind); }
            // a run at 01:30 on 1 Jan in Israel (UTC+2) is named after 31 Dec (UTC): the id's date is NOT the local date
            var israel = TimeZoneInfo.FindSystemTimeZoneById("Asia/Jerusalem");
            Assert.Equal("2026-12-31-23-30-00", RunId.From(TimeZoneInfo.ConvertTimeToUtc(new DateTime(2027, 1, 1, 1, 30, 0), israel)));
        }

        // ------------------------------------------------------------------ retention DAYS / JOBS

        [Theory]
        [InlineData(30, 2026, 3, 1)]    // the period spans the end of February (not a leap year)
        [InlineData(30, 2028, 3, 1)]    // leap year
        [InlineData(1, 2027, 1, 1)]     // one day, at the year end
        [InlineData(7, 2026, 3, 30)]    // across the Israeli / European DST change (UTC instants: no effect)
        public void RetentionDays_KeepsAPointExactlyNDaysOld_AndDropsItOneSecondLater(int days, int y, int m, int d)
        {
            var now = U(y, m, d, 0, 30, 0);
            var edge = now.AddDays(-days);
            var jobs = new[] { edge.AddSeconds(-1), edge, edge.AddSeconds(1), now.AddHours(-1) }.Select(RunId.From).ToList();
            var keep = new RetentionPolicy { Unit = "DAYS", Period = days }.Keep(jobs, now);
            Assert.False(keep.Contains(jobs[0]), days + " days: a point " + days + " days + 1 s old is kept");
            Assert.True(keep.Contains(jobs[1]), days + " days: a point exactly " + days + " days old is dropped");
            Assert.True(keep.Contains(jobs[2]));
            Assert.True(keep.Contains(jobs[3]));
        }

        [Fact]
        public void RetentionJobs_KeepsExactlyTheLastN_AndTheLatestAlways()
        {
            var jobs = Enumerable.Range(0, 5).Select(i => RunId.From(U(2026, 12, 31, 23, 58).AddMinutes(i))).ToList();   // across the year end
            Assert.Equal(jobs.Skip(2).ToArray(), new RetentionPolicy { Unit = "JOBS", Period = 3 }.Keep(jobs, U(2027, 6, 1)).OrderBy(x => x).ToArray());
            Assert.Equal(new[] { jobs[4] }, new RetentionPolicy { Unit = "DAYS", Period = 1 }.Keep(jobs, U(2030, 1, 1)).ToArray());   // all too old: the latest stays
        }

        /// <summary>
        /// Retention end-to-end on the store (files on disk as the oracle): a run folder created exactly N days before the
        /// maintenance survives, the one a second older is deleted (its objects gone from the disk).
        /// </summary>
        [Fact]
        public void RetentionDays_OnTheStore_TheFolderOfThePointExactlyNDaysOldStays()
        {
            var userDir = NewDir("obnmret-"); var key = KeySet.Random();
            var st = new SetStore(userDir, "1700000000201");
            var now = U(2027, 1, 1, 0, 0, 30);
            var t = new[] { now.AddDays(-2).AddSeconds(-1), now.AddDays(-2), now.AddDays(-1) };
            var ids = new List<string>();
            foreach (var x in t)
            {
                var j = st.BeginJob(x); ids.Add(j);
                NightM_RaceTests.Put(st, j, NightM_RaceTests.N("a"), NightM_RaceTests.Obj(key, "a@" + j));
                st.Commit(j, new Msg().Set("new", 1));
            }
            Assert.Equal(t.Select(RunId.From), ids);
            // the version replaced by run 2 sits in run 2's folder; run 1's point needs it — kept only while point 1 is kept
            var r = st.ApplyRetention(new RetentionPolicy { Unit = "DAYS", Period = 2 }, now);
            Assert.Equal(new[] { ids[1], ids[2] }, st.Points().ToArray());
            Assert.Equal(1, r.Int("expiredPoints"));
            Assert.False(Directory.Exists(Path.Combine(userDir, "files", "1700000000201", ids[1])), "the folder with point 1's version stayed after point 1 expired");
            Assert.True(Directory.Exists(Path.Combine(userDir, "files", "1700000000201", ids[2])), "the folder with point 2's version was deleted although point 2 is exactly 2 days old");
        }

        // ------------------------------------------------------------------ LogDue / RestoreTestDue

        [Theory]
        [InlineData(15, 2026, 12, 31, 23, 50)]
        [InlineData(60, 2028, 2, 28, 23, 30)]
        [InlineData(15, 2026, 10, 24, 22, 55)]   // the Israeli fall-back hour (UTC rule: no effect)
        public void LogDue_FalseOneTickBeforeTheInterval_TrueAtIt(int interval, int y, int mo, int d, int h, int mi)
        {
            var app = new AgentApp(NewDir("obnmlog-"));
            var s = new BackupSetInfo { Id = "1700000000202", Type = "MSSQL", LogIntervalMinutes = interval };
            var last = U(y, mo, d, h, mi);
            Directory.CreateDirectory(app.Home.SetDir(s.Id));
            File.WriteAllText(Path.Combine(app.Home.SetDir(s.Id), "last-log.txt"), RunId.From(last) + "\tBS_STOP_SUCCESS");
            Assert.False(app.LogDue(s, last.AddMinutes(interval) - Tick));
            Assert.True(app.LogDue(s, last.AddMinutes(interval)));
        }

        [Theory]
        [InlineData(2026, 1, 31)] [InlineData(2028, 2, 1)] [InlineData(2026, 12, 15)] [InlineData(2026, 10, 1)]
        public void RestoreTestDue_ThirtyDaysExactly_AcrossMonthLeapYearAndDst(int y, int mo, int d)
        {
            var app = new AgentApp(NewDir("obnmrt-"));
            var s = new BackupSetInfo { Id = "1700000000203" };
            var sd = app.Home.SetDir(s.Id); Directory.CreateDirectory(sd);
            var last = U(y, mo, d, 21, 45);
            File.WriteAllText(Path.Combine(sd, "last-attempt.txt"), RunId.From(last) + "\tBS_STOP_SUCCESS");
            var st = new LocalState(sd); st.LastSuccess = RunId.From(last); st.Save();
            File.WriteAllText(Path.Combine(sd, "last-restore-test.txt"), RunId.From(last));
            Assert.False(app.RestoreTestDue(s, last.AddDays(30) - TimeSpan.FromSeconds(1)));
            Assert.Equal(RunId.From(last), File.ReadAllText(Path.Combine(sd, "last-restore-test.txt")).Trim());   // not rewritten
            Assert.True(app.RestoreTestDue(s, last.AddDays(30)));
            Assert.Equal(RunId.From(last.AddDays(30)), File.ReadAllText(Path.Combine(sd, "last-restore-test.txt")).Trim());
        }

        // ------------------------------------------------------------------ the run lease

        [Theory]
        [InlineData(23, 58)]   // the lease ends after midnight
        [InlineData(12, 0)]
        public void Lease_OpenAtExactlyFiveMinutes_ClosedOneTickLater_AcrossMidnight(int h, int m)
        {
            var userDir = NewDir("obnmlease-");
            var st = new SetStore(userDir, "1700000000204");
            // the lease is read from the files' times: the sign of life is placed in the future so it is the newest time
            var real = DateTime.UtcNow;
            var signUtc = real.Date.AddDays(1).AddHours(h).AddMinutes(m);
            var job = st.BeginJob(real);
            SystemClock.Use(() => signUtc);
            st.Touch(job);
            var dir = Path.Combine(userDir, "files", "1700000000204", "jobs", job);
            Assert.Equal(signUtc, File.GetLastWriteTimeUtc(Path.Combine(dir, "lease")));   // the sign of life really is at signUtc
            Assert.Empty(st.ExpireStale(signUtc + SetStore.Lease));
            Assert.True(Directory.Exists(dir));
            Assert.Equal(new[] { job }, st.ExpireStale(signUtc + SetStore.Lease + Tick).ToArray());
            Assert.False(Directory.Exists(dir));
        }

        // ------------------------------------------------------------------ ReportCannotRun (20 hours)

        static void CannotRun(AgentApp app, BackupSetInfo s, string why)
        {
            typeof(AgentApp).GetMethod("ReportCannotRun", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(app, new object[] { s, why });
        }

        static int CannotRunRows(Env env, string setId)
        {
            return env.Api.Runs.Since(DateTime.UtcNow.AddDays(-2), DateTime.UtcNow.AddDays(2)).Count(x => x["set"] == setId && (x["result"] ?? "") == "BS_STOP_BY_SYSTEM_ERROR");
        }

        /// <summary>
        /// "A scheduled run that could not even start reaches the server as a failed run … once a day per set."
        /// (a) control: the last attempt is 20 h old → reported; a second one 19 h 59 m later → not again; at 20 h → again.
        /// (b) a set backed up twice a day: the 09:00 run succeeded, the 18:00 run cannot start (9 hours later). That is
        ///     the first failure of the day — the server must hear of it.
        /// Oracle: the server's own run history (rows with BS_STOP_BY_SYSTEM_ERROR for the set).
        /// </summary>
        [Fact]
        public void ReportCannotRun_ReachesTheServer_OncePer20Hours()
        {
            using (var env = new Env())
            {
                env.CreateUser("nmcr1", "Customer-Pass-1");
                var app = env.Agent("nmcr1", "Customer-Pass-1");
                var src = env.Dir("src"); File.WriteAllText(Path.Combine(src, "a.txt"), "a");
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "C", Sources = { src }, Vss = false });
                var mark = Path.Combine(app.Home.SetDir(set.Id), "last-attempt.txt");
                var t0 = DateTime.UtcNow;
                File.WriteAllText(mark, RunId.From(t0.AddHours(-20)) + "\tBS_STOP_SUCCESS");
                SystemClock.Use(() => t0); CannotRun(app, set, "no key");
                Assert.Equal(1, CannotRunRows(env, set.Id));
                SystemClock.Use(() => t0.AddHours(20) - TimeSpan.FromSeconds(1)); CannotRun(app, set, "no key");
                Assert.Equal(1, CannotRunRows(env, set.Id));
                SystemClock.Use(() => t0.AddHours(20)); CannotRun(app, set, "no key");
                Assert.Equal(2, CannotRunRows(env, set.Id));
            }
        }

        [Fact]
        public void ReportCannotRun_TheFirstFailureAfterASuccessfulRunEarlierThatDay_ReachesTheServer()
        {
            using (var env = new Env())
            {
                env.CreateUser("nmcr2", "Customer-Pass-1");
                var app = env.Agent("nmcr2", "Customer-Pass-1");
                var src = env.Dir("src"); File.WriteAllText(Path.Combine(src, "a.txt"), "a");
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "C", Sources = { src }, Vss = false });
                Assert.StartsWith("BS_STOP_SUCCESS", app.Backup(set.Id).Result);   // the 09:00 run (writes last-attempt.txt)
                Assert.Equal(0, CannotRunRows(env, set.Id));
                var now = DateTime.UtcNow.AddHours(9);                                 // 18:00: the run cannot start
                SystemClock.Use(() => now); CannotRun(app, set, "the key is missing on this computer");
                Assert.True(CannotRunRows(env, set.Id) == 1, "a run that could not start 9 hours after a successful one never reached the server (rows: " + CannotRunRows(env, set.Id) + "); last-attempt.txt = " + File.ReadAllText(Path.Combine(app.Home.SetDir(set.Id), "last-attempt.txt")).Trim());
            }
        }

        // ------------------------------------------------------------------ server: "no backup for 48 hours" (MissedBackupCheck)

        static int MissedLines(string login, string setId)
        {
            var d = SysLog.Dir("BackupErrors");
            if (!Directory.Exists(d)) return 0;
            return Directory.GetFiles(d).SelectMany(f => Atomic.ReadShared(f).Split('\n')).Count(l => l.Contains(login + "\t" + setId + "\t") && l.Contains("MISSED BACKUP"));
        }

        /// <summary>
        /// The server alerts when a set has no completed backup for 48 hours, then at most once every 24 hours.
        /// Boundaries: 48 h − 1 s → no alert; 48 h → alert; + 24 h − 1 s → none; + 24 h → the second alert.
        /// Oracle: the MISSED BACKUP lines of the server's BackupErrors log for the set.
        /// </summary>
        [Fact]
        public void MissedBackupAlert_At48HoursExactly_ThenEvery24Hours()
        {
            using (var env = new Env())
            {
                env.CreateUser("nmmiss1", "Customer-Pass-1");
                var app = env.Agent("nmmiss1", "Customer-Pass-1");
                var src = env.Dir("src");
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "M", Sources = { src }, Vss = false });
                var created = RunId.FromUnixMs(long.Parse(set.Id, CultureInfo.InvariantCulture));   // no backup yet: counted from the creation
                env.Api.Maintenance(created.AddHours(48) - TimeSpan.FromSeconds(1)); Assert.Equal(0, MissedLines("nmmiss1", set.Id));
                env.Api.Maintenance(created.AddHours(48)); Assert.Equal(1, MissedLines("nmmiss1", set.Id));
                env.Api.Maintenance(created.AddHours(72) - TimeSpan.FromSeconds(1)); Assert.Equal(1, MissedLines("nmmiss1", set.Id));
                env.Api.Maintenance(created.AddHours(72)); Assert.Equal(2, MissedLines("nmmiss1", set.Id));
            }
        }
    }
}
