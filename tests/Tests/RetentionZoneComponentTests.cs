using System;
using System.Collections.Generic;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// ST-03 (Agent B note, proven here): the long-term versions ("keep the last of each month") are the customer's months.
    /// In a zone 3 hours ahead of UTC, a backup at 01:30 on 1 July is July's — not June's last, as UTC buckets made it.
    /// Oracle: which run ids the policy keeps (pure function; the runs and the zone are given).
    /// </summary>
    public class RetentionZoneComponentTests
    {
        static readonly TimeZoneInfo Plus3 = TimeZoneInfo.CreateCustomTimeZone("QA+3", TimeSpan.FromHours(3), "QA+3", "QA+3");

        // the zone-aware Keep exists only with the fix that waits for the owner's decision: called by reflection, so this file
        // compiles either way (the test is skipped until the decision; NOT TESTED, never PASS)
        static HashSet<string> Keep(RetentionPolicy p, IList<string> jobs, DateTime now, TimeZoneInfo zone)
        {
            var m = typeof(RetentionPolicy).GetMethod("Keep", new[] { typeof(IList<string>), typeof(DateTime), typeof(TimeZoneInfo) });
            if (m == null) throw new InvalidOperationException("RetentionPolicy.Keep has no time zone (the fix of bug 77 is not in this build)");
            return (HashSet<string>)m.Invoke(p, new object[] { jobs, now, zone });
        }

        [Fact(Skip = "NEEDS OWNER DECISION (bug 77, docs/PRODUCT-BENCHMARK.md): GFS day/month boundaries in the server time zone or UTC")]
        public void TheLastBackupOfAMonth_IsTheCustomersMonth_NotUtcs()
        {
            var june30Evening = RunId.From(new DateTime(2026, 6, 30, 20, 0, 0, DateTimeKind.Utc));   // 23:00 on 30 June there
            var july1Night = RunId.From(new DateTime(2026, 6, 30, 22, 30, 0, DateTimeKind.Utc));     // 01:30 on 1 July there
            var july15 = RunId.From(new DateTime(2026, 7, 15, 10, 0, 0, DateTimeKind.Utc));
            var jobs = new List<string> { june30Evening, july1Night, july15 };
            var p = new RetentionPolicy { Unit = "JOBS", Period = 1, Monthly = 2 };
            var keep = Keep(p, jobs, new DateTime(2026, 7, 16, 0, 0, 0, DateTimeKind.Utc), Plus3);
            Assert.True(keep.Contains(june30Evening), "June's last backup (23:00 on 30 June, local) was not kept as June's");
            Assert.False(keep.Contains(july1Night), "a July backup (01:30 on 1 July, local) was kept as June's last");
            Assert.Contains(july15, keep);
            // control: in UTC itself the 22:30 run is June's
            var utc = Keep(p, jobs, new DateTime(2026, 7, 16, 0, 0, 0, DateTimeKind.Utc), TimeZoneInfo.Utc);
            Assert.Contains(july1Night, utc);
        }
    }
}
