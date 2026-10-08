using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// Night QA agent M — the scheduler (AgentApp.Due / LastSlot) at time boundaries: midnight, end of week, end of month
    /// (31st, 30th, 28/29 Feb), end of year, and the DST changes of Asia/Jerusalem and Europe/London.
    ///
    /// How time is faked: the service calls Due(s, SystemClock.Now) every minute, SystemClock.Now = UtcNow.ToLocalTime().
    /// The test walks UTC minute by minute, converts with the process time zone (set for the test through TZ, see
    /// <see cref="Zone"/>), and records each run the way the product does (BackupRun: LastSuccess = the run id,
    /// LastSuccessLocalMs = the start; AgentApp.BackupLocked: last-attempt.txt at the end).
    ///
    /// ORACLE: the list of expected run instants is computed in the test, independently of LastSlot: the local wall
    /// times of every slot of every day, converted to UTC with the zone's known offsets (zdump), and the delay rules
    /// written out by hand. On DST days where the slot does not exist (spring) or exists twice (fall) only the count of
    /// runs per local date is asserted (exactly one): the exact minute is an owner decision (see the report).
    /// </summary>
    [Collection("NightM-TZ")]
    public class NightM_ScheduleBoundaryTests
    {
        readonly Xunit.Abstractions.ITestOutputHelper output;
        public NightM_ScheduleBoundaryTests(Xunit.Abstractions.ITestOutputHelper output) { this.output = output; }
        /// <summary>Switches the process time zone for one test (TZ + ClearCachedData) and proves the switch took effect.</summary>
        public sealed class Zone : IDisposable
        {
            readonly string old;
            public Zone(string id)
            {
                // Q26: TZ moves TimeZoneInfo.Local on Linux/macOS only; on Windows the zone is the machine's (changing it needs
                // administrator rights and changes it for every program). Not a pass and not a product failure there:
                if (OperatingSystem.IsWindows()) throw new InvalidOperationException("NOT TESTED on Windows: the time zone is injected with TZ, which Windows ignores (" + id + ")");
                old = Environment.GetEnvironmentVariable("TZ");
                Environment.SetEnvironmentVariable("TZ", id);
                TimeZoneInfo.ClearCachedData();
                Assert.Equal(id, TimeZoneInfo.Local.Id);   // the fault (another zone) is really injected
            }
            public void Dispose() { Environment.SetEnvironmentVariable("TZ", old); TimeZoneInfo.ClearCachedData(); }
        }

        static string NewHome() { return Path.Combine(Path.GetTempPath(), "obnightm-" + Guid.NewGuid().ToString("N").Substring(0, 8)); }

        /// <summary>A run as the product records it: started at startUtc, ended at endUtc.</summary>
        static void Record(AgentApp app, BackupSetInfo s, DateTime startUtc, DateTime endUtc, bool ok)
        {
            Directory.CreateDirectory(app.Home.SetDir(s.Id));
            File.WriteAllText(Path.Combine(app.Home.SetDir(s.Id), "last-attempt.txt"), RunId.From(endUtc) + "\t" + (ok ? "BS_STOP_SUCCESS" : "BS_STOP_BY_SYSTEM_ERROR"));
            if (ok) { var st = new LocalState(app.Home.SetDir(s.Id)); st.LastSuccess = RunId.From(startUtc); st.LastSuccessLocalMs = RunId.UnixMs(startUtc); st.Save(); }
        }

        /// <summary>The service: every minute (UTC), every set in order; a run blocks the loop for its duration.</summary>
        static Dictionary<string, List<DateTime>> Simulate(AgentApp app, IList<BackupSetInfo> sets, DateTime fromUtc, DateTime toUtc,
            Func<DateTime, bool> off = null, Func<BackupSetInfo, int> runMinutes = null)
        {
            var runs = sets.ToDictionary(s => s.Id, s => new List<DateTime>());
            var utc = fromUtc;
            while (utc < toUtc)
            {
                if (off != null && off(utc)) { utc = utc.AddMinutes(1); continue; }
                foreach (var s in sets)
                {
                    if (utc >= toUtc) break;
                    if (!app.Due(s, utc.ToLocalTime())) continue;
                    int d = runMinutes == null ? 1 : runMinutes(s);
                    runs[s.Id].Add(utc);
                    Record(app, s, utc, utc.AddMinutes(d), true);
                    utc = utc.AddMinutes(d);   // the loop is busy with this run
                }
                utc = utc.AddMinutes(1);
            }
            return runs;
        }

        static BackupSetInfo Set(string id, string days, int h, int m) { return new BackupSetInfo { Id = id, Name = "S" + id, Days = days, Hour = h, Minute = m, MissedDelayMinutes = 5 }; }

        /// <summary>Expected UTC instants of a slot: each local date in [from, to), on the chosen week days, converted with the
        /// zone's offset for that instant (taken from the zone database, not from the product).</summary>
        static List<DateTime> Expected(TimeZoneInfo zone, DateTime fromLocalDate, DateTime toLocalDate, string days, int h, int m)
        {
            var l = new List<DateTime>();
            for (var d = fromLocalDate.Date; d < toLocalDate.Date; d = d.AddDays(1))
            {
                if (days[(int)d.DayOfWeek] == '-') continue;
                var wall = new DateTime(d.Year, d.Month, d.Day, h, m, 0, DateTimeKind.Unspecified);
                Assert.False(zone.IsInvalidTime(wall) || zone.IsAmbiguousTime(wall), "test setup: " + wall + " is a DST edge; use the DST tests");
                l.Add(TimeZoneInfo.ConvertTimeToUtc(wall, zone));
            }
            return l;
        }

        static string Show(IEnumerable<DateTime> utc) { return string.Join(", ", utc.Select(x => x.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "Z")); }

        // ------------------------------------------------------------------ midnight, month ends, leap years, year end

        public static IEnumerable<object[]> Boundaries()
        {
            foreach (var zone in new[] { "UTC", "Asia/Jerusalem" })
            {
                yield return new object[] { zone, "2026-01-30", "2026-02-02" };   // 31 Jan → 1 Feb
                yield return new object[] { zone, "2026-04-29", "2026-05-02" };   // 30 Apr → 1 May
                yield return new object[] { zone, "2027-02-27", "2027-03-02" };   // 28 Feb → 1 Mar (not a leap year)
                yield return new object[] { zone, "2028-02-27", "2028-03-02" };   // 28 → 29 Feb → 1 Mar (leap year)
                yield return new object[] { zone, "2026-12-30", "2027-01-02" };   // 31 Dec → 1 Jan
            }
        }

        [Theory]
        [MemberData(nameof(Boundaries))]
        public void DailyAt2359_And0000_RunExactlyOncePerDay_AcrossMidnightMonthLeapAndYearEnd(string zoneId, string from, string to)
        {
            using (new Zone(zoneId))
            {
                var zone = TimeZoneInfo.Local;
                var d0 = DateTime.ParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                var d1 = DateTime.ParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                var late = Set("1700000000101", "SMTWTFS", 23, 59);
                var early = Set("1700000000102", "SMTWTFS", 0, 0);
                var app = new AgentApp(NewHome());
                // both sets ran at their last slot before the window (so the first day starts clean)
                Record(app, late, TimeZoneInfo.ConvertTimeToUtc(d0.AddDays(-1).AddHours(23).AddMinutes(59), zone), TimeZoneInfo.ConvertTimeToUtc(d0.AddDays(-1).AddHours(23).AddMinutes(59), zone).AddMinutes(1), true);
                Record(app, early, TimeZoneInfo.ConvertTimeToUtc(d0, zone), TimeZoneInfo.ConvertTimeToUtc(d0, zone).AddMinutes(1), true);
                var runs = Simulate(app, new[] { late, early }, TimeZoneInfo.ConvertTimeToUtc(d0.AddMinutes(1), zone), TimeZoneInfo.ConvertTimeToUtc(d1, zone));
                var wantLate = Expected(zone, d0, d1, late.Days, 23, 59);
                var wantEarly = Expected(zone, d0.AddDays(1), d1, early.Days, 0, 0);
                Assert.True(wantLate.SequenceEqual(runs[late.Id]), "23:59 in " + zoneId + ": expected " + Show(wantLate) + " got " + Show(runs[late.Id]));
                Assert.True(wantEarly.SequenceEqual(runs[early.Id]), "00:00 in " + zoneId + ": expected " + Show(wantEarly) + " got " + Show(runs[early.Id]));
            }
        }

        // ------------------------------------------------------------------ end of week

        [Theory]
        [InlineData("UTC")]
        [InlineData("Asia/Jerusalem")]
        public void Weekly_Saturday2330_AndSunday0015_RunOnTheirDaysOnly_ForThreeWeeksAcrossAMonthEnd(string zoneId)
        {
            using (new Zone(zoneId))
            {
                var zone = TimeZoneInfo.Local;
                var d0 = new DateTime(2026, 5, 18); var d1 = new DateTime(2026, 6, 8);   // Mon 18 May → Mon 8 Jun (31 May is a Sunday)
                var sat = Set("1700000000103", "------S", 23, 30);
                var sun = Set("1700000000104", "S------", 0, 15);
                var app = new AgentApp(NewHome());
                Record(app, sat, TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 5, 16, 23, 30, 0), zone), TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 5, 16, 23, 31, 0), zone), true);
                Record(app, sun, TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 5, 17, 0, 15, 0), zone), TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 5, 17, 0, 16, 0), zone), true);
                var runs = Simulate(app, new[] { sat, sun }, TimeZoneInfo.ConvertTimeToUtc(d0, zone), TimeZoneInfo.ConvertTimeToUtc(d1, zone));
                var wSat = Expected(zone, d0, d1, sat.Days, 23, 30); var wSun = Expected(zone, d0, d1, sun.Days, 0, 15);
                Assert.Equal(3, wSat.Count); Assert.Equal(3, wSun.Count);   // the oracle itself: 23, 30 May, 6 Jun / 24, 31 May, 7 Jun
                Assert.True(wSat.SequenceEqual(runs[sat.Id]), "Saturday: expected " + Show(wSat) + " got " + Show(runs[sat.Id]));
                Assert.True(wSun.SequenceEqual(runs[sun.Id]), "Sunday: expected " + Show(wSun) + " got " + Show(runs[sun.Id]));
            }
        }

        // ------------------------------------------------------------------ DST

        /// <summary>The runs of each LOCAL date (the computer's calendar).</summary>
        static Dictionary<DateTime, int> PerLocalDay(IEnumerable<DateTime> utcRuns, DateTime fromLocal, DateTime toLocal)
        {
            var m = new Dictionary<DateTime, int>();
            for (var d = fromLocal.Date; d < toLocal.Date; d = d.AddDays(1)) m[d] = 0;
            foreach (var u in utcRuns) { var d = u.ToLocalTime().Date; if (m.ContainsKey(d)) m[d]++; }
            return m;
        }

        static string ShowDays(Dictionary<DateTime, int> m) { return string.Join(", ", m.Select(kv => kv.Key.ToString("MM-dd", CultureInfo.InvariantCulture) + "=" + kv.Value)); }

        /// <summary>
        /// Spring forward: the slot's wall time does not exist that night (Jerusalem: 27 Mar 2026 02:00→03:00; London:
        /// 29 Mar 2026 01:00→02:00). The computer is ON all night. A daily backup must still run exactly once that local
        /// date — with "run missed backups" on and off (the backup was not missed: the computer was on).
        /// </summary>
        [Theory]
        [InlineData("Asia/Jerusalem", 3, 27, 2, 30, true)]
        [InlineData("Asia/Jerusalem", 3, 27, 2, 30, false)]
        [InlineData("Asia/Jerusalem", 3, 27, 2, 50, false)]   // control: 20 minutes after the gap's end the slot is "on time"
        [InlineData("Europe/London", 3, 29, 1, 30, true)]
        [InlineData("Europe/London", 3, 29, 1, 30, false)]
        public void SpringForward_ASlotThatDoesNotExistThatNight_RunsExactlyOnceThatDay(string zoneId, int month, int day, int h, int m, bool runMissed)
        {
            using (new Zone(zoneId))
            {
                var zone = TimeZoneInfo.Local;
                var dst = new DateTime(2026, month, day);
                Assert.True(zone.IsInvalidTime(dst.AddHours(2).AddMinutes(30)) || zone.IsInvalidTime(dst.AddHours(1).AddMinutes(30)), "test setup: no DST gap that night");
                var s = Set("1700000000105", "SMTWTFS", h, m); s.RunMissed = runMissed;
                var app = new AgentApp(NewHome());
                var prev = TimeZoneInfo.ConvertTimeToUtc(dst.AddDays(-2).AddHours(h).AddMinutes(m), zone);
                Record(app, s, prev, prev.AddMinutes(1), true);
                var from = TimeZoneInfo.ConvertTimeToUtc(dst.AddDays(-1), zone); var to = TimeZoneInfo.ConvertTimeToUtc(dst.AddDays(2), zone);
                var runs = Simulate(app, new[] { s }, from, to)[s.Id];
                var per = PerLocalDay(runs, dst.AddDays(-1), dst.AddDays(2));
                output.WriteLine("runs (local): " + string.Join(", ", runs.Select(u => u.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture))));
                Assert.True(per.Values.All(v => v == 1), zoneId + " slot " + h + ":" + m.ToString("D2") + " runMissed=" + runMissed + ": runs per local day " + ShowDays(per) + " — runs at " + Show(runs));
            }
        }

        /// <summary>
        /// Fall back: the slot's wall time happens twice (Jerusalem: 25 Oct 2026 02:00→01:00; London: 25 Oct 2026 02:00→01:00).
        /// The backup runs exactly once that local date (never twice), and once on the days around it.
        /// </summary>
        [Theory]
        [InlineData("Asia/Jerusalem", 1, 30)]
        [InlineData("Asia/Jerusalem", 1, 0)]
        [InlineData("Europe/London", 1, 30)]
        [InlineData("Europe/London", 1, 59)]
        public void FallBack_ASlotThatHappensTwice_RunsExactlyOnceThatDay(string zoneId, int h, int m)
        {
            using (new Zone(zoneId))
            {
                var zone = TimeZoneInfo.Local;
                var dst = new DateTime(2026, 10, 25);
                Assert.True(zone.IsAmbiguousTime(dst.AddHours(1).AddMinutes(30)), "test setup: no repeated hour that night");
                var s = Set("1700000000106", "SMTWTFS", h, m);
                var app = new AgentApp(NewHome());
                var prev = TimeZoneInfo.ConvertTimeToUtc(dst.AddDays(-2).AddHours(h).AddMinutes(m), zone);
                Record(app, s, prev, prev.AddMinutes(1), true);
                var from = TimeZoneInfo.ConvertTimeToUtc(dst.AddDays(-1), zone); var to = TimeZoneInfo.ConvertTimeToUtc(dst.AddDays(2).AddHours(-1), zone);
                var runs = Simulate(app, new[] { s }, from, to)[s.Id];
                var per = PerLocalDay(runs, dst.AddDays(-1), dst.AddDays(2));
                output.WriteLine("runs (UTC): " + Show(runs));
                Assert.True(per.Values.All(v => v == 1), zoneId + " slot " + h + ":" + m.ToString("D2") + ": runs per local day " + ShowDays(per) + " — runs at " + Show(runs));
            }
        }

        // ------------------------------------------------------------------ missed runs at a boundary

        /// <summary>
        /// Slot 23:50; the computer is off 23:40–00:10 (the slot is missed, the date changes while it is off); missed-run
        /// delay 30 minutes. Expected: one run at 00:40 (back at 00:10 + 30), none for the new day's slot until 23:50.
        /// </summary>
        [Theory]
        [InlineData("UTC")]
        [InlineData("Asia/Jerusalem")]
        public void AMissedSlotJustBeforeMidnight_RunsOnceAfterTheDelay_OnTheNextDate(string zoneId)
        {
            using (new Zone(zoneId))
            {
                var zone = TimeZoneInfo.Local;
                var day = new DateTime(2026, 12, 31);   // and the year changes too
                var s = Set("1700000000107", "SMTWTFS", 23, 50); s.MissedDelayMinutes = 30;
                var app = new AgentApp(NewHome());
                var prev = TimeZoneInfo.ConvertTimeToUtc(day.AddDays(-1).AddHours(23).AddMinutes(50), zone);
                Record(app, s, prev, prev.AddMinutes(1), true);
                var offFrom = TimeZoneInfo.ConvertTimeToUtc(day.AddHours(23).AddMinutes(40), zone); var offTo = TimeZoneInfo.ConvertTimeToUtc(day.AddDays(1).AddMinutes(10), zone);
                var runs = Simulate(app, new[] { s }, TimeZoneInfo.ConvertTimeToUtc(day.AddHours(12), zone), TimeZoneInfo.ConvertTimeToUtc(day.AddDays(1).AddHours(23).AddMinutes(55), zone),
                    off: u => u >= offFrom && u < offTo)[s.Id];
                var want = new[] { TimeZoneInfo.ConvertTimeToUtc(day.AddDays(1).AddMinutes(40), zone), TimeZoneInfo.ConvertTimeToUtc(day.AddDays(1).AddHours(23).AddMinutes(50), zone) };
                Assert.True(want.SequenceEqual(runs), "expected " + Show(want) + " got " + Show(runs));
            }
        }

        /// <summary>
        /// MissedMinHours = 24 (a missed slot runs only when the last backup is at least 24 hours old). The last backup ran
        /// late yesterday at 09:20; today the 09:00 slot is missed (off 08:00–09:17), delay 0. Expected: one run at 09:20
        /// exactly (the first minute the last backup is 24 hours old), across a month end.
        /// </summary>
        [Fact]
        public void MissedMinHours_RunsTheFirstMinuteTheLastBackupIsOldEnough()
        {
            using (new Zone("Asia/Jerusalem"))
            {
                var zone = TimeZoneInfo.Local;
                var day = new DateTime(2026, 7, 1);
                var s = Set("1700000000108", "SMTWTFS", 9, 0); s.MissedDelayMinutes = 0; s.MissedMinHours = 24;
                var app = new AgentApp(NewHome());
                var last = TimeZoneInfo.ConvertTimeToUtc(day.AddDays(-1).AddHours(9).AddMinutes(20), zone);
                Record(app, s, last, last.AddMinutes(1), true);
                var offFrom = TimeZoneInfo.ConvertTimeToUtc(day.AddHours(8), zone); var offTo = TimeZoneInfo.ConvertTimeToUtc(day.AddHours(9).AddMinutes(17), zone);   // back 17 minutes after the slot: missed (more than 15)
                var runs = Simulate(app, new[] { s }, TimeZoneInfo.ConvertTimeToUtc(day.AddHours(7), zone), TimeZoneInfo.ConvertTimeToUtc(day.AddHours(12), zone), off: u => u >= offFrom && u < offTo)[s.Id];
                var want = new[] { last.AddHours(24) };
                Assert.True(want.SequenceEqual(runs), "expected " + Show(want) + " got " + Show(runs));
            }
        }

        /// <summary>
        /// The service runs the sets one after the other in one loop. Set A (22:00) takes 2 hours; set B is due 22:30 with
        /// "run missed backups" OFF. The computer is ON the whole time — B's slot was not missed because the computer was
        /// off; the service was busy. Expected: B runs that night (after A), every night.
        /// Oracle: B's runs counted per night over 3 nights.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]   // control: with "run missed backups" on, B runs after A
        public void ASetDueWhileTheServiceIsBusyWithAnotherSet_RunsThatNight_EvenWithRunMissedOff(bool runMissed)
        {
            using (new Zone("Asia/Jerusalem"))
            {
                var zone = TimeZoneInfo.Local;
                var day = new DateTime(2026, 6, 1);
                var a = Set("1700000000109", "SMTWTFS", 22, 0);
                var b = Set("1700000000110", "SMTWTFS", 22, 30); b.RunMissed = runMissed;
                var app = new AgentApp(NewHome());
                foreach (var x in new[] { a, b }) { var p = TimeZoneInfo.ConvertTimeToUtc(day.AddDays(-1).AddHours(x.Hour).AddMinutes(x.Minute), zone); Record(app, x, p, p.AddMinutes(1), true); }
                var runs = Simulate(app, new[] { a, b }, TimeZoneInfo.ConvertTimeToUtc(day.AddHours(12), zone), TimeZoneInfo.ConvertTimeToUtc(day.AddDays(3).AddHours(12), zone),
                    runMinutes: x => x.Id == a.Id ? 120 : 10);
                var nights = Enumerable.Range(0, 3).Select(i => runs[b.Id].Count(u => u >= TimeZoneInfo.ConvertTimeToUtc(day.AddDays(i).AddHours(22).AddMinutes(30), zone)
                    && u < TimeZoneInfo.ConvertTimeToUtc(day.AddDays(i + 1).AddHours(22).AddMinutes(30), zone))).ToArray();
                Assert.Equal(3, runs[a.Id].Count);   // control: A ran every night
                Assert.True(nights.All(n => n == 1), "runMissed=" + runMissed + ": B's runs per night " + string.Join(",", nights) + " (B ran at " + Show(runs[b.Id]) + "; A at " + Show(runs[a.Id]) + ")");
            }
        }
    }

    [CollectionDefinition("NightM-TZ", DisableParallelization = true)]
    public class NightMTzCollection { }
}
