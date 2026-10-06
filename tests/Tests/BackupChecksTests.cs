using System;
using System.IO;
using System.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>CHK-010 / REP-020: the checks of the analysis report (as in ITSguard) and the set's own reports.</summary>
    public class BackupChecksTests
    {
        [Fact]
        public void Small_Unchanged_ShortRetention_SharpChange_AddedRemoved_AndTheSetsReports()
        {
            using (var env = new Env())
            {
                env.CreateUser("acme", "Customer-Pass-1");
                var app = env.Agent("acme", "Customer-Pass-1");
                var src = env.Dir("src"); File.WriteAllText(Path.Combine(src, "a.txt"), "a");
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "Docs", Sources = { src }, Retention = new RetentionPolicy { Unit = "DAYS", Period = 7 } });
                for (int i = 0; i < 4; i++) Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);   // the first sends a.txt, then nothing changes
                var still = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "Still", Sources = { env.Dir("still") } });
                for (int i = 0; i < 3; i++) Assert.Equal("BS_STOP_SUCCESS", app.Backup(still.Id).Result);   // three backups, nothing new or changed in 30 days
                var admin = env.Admin();
                var r = admin.Call("GET", "/api/admin/checks");
                Assert.Contains(r.List("small"), x => x["set"] == set.Id);
                Assert.Contains(r.List("noChange"), x => x["set"] == still.Id && x["runs"] == "3");
                Assert.DoesNotContain(r.List("noChange"), x => x["set"] == set.Id);            // a.txt was new this month
                Assert.Contains(r.List("shortRetention"), x => x["set"] == set.Id && x["days"] == "7");

                // a sharp change and a removed set, between yesterday's and today's snapshot of the sizes
                var stats = Path.Combine(env.Cfg.SystemHome, "stats"); Directory.CreateDirectory(stats);
                var day = DateTime.UtcNow.Date;
                File.WriteAllText(Path.Combine(stats, "sets-" + day.AddDays(-1).ToString("yyyyMMdd") + ".tsv"), "acme\t" + set.Id + "\tDocs\t" + (2000L << 20) + "\tFILE\nacme\t42\tOld set\t5\tFILE\n");
                File.WriteAllText(Path.Combine(stats, "sets-" + day.ToString("yyyyMMdd") + ".tsv"), "acme\t" + set.Id + "\tDocs\t" + (1000L << 20) + "\tFILE\nacme\t43\tNew set\t5\tFILE\n");
                r = admin.Call("GET", "/api/admin/checks");
                Assert.Contains(r.List("volume"), x => x["set"] == set.Id && x["percent"] == "-50");
                Assert.Contains(r.List("removed"), x => x["set"] == "42");
                Assert.Contains(r.List("added"), x => x["set"] == "43");

                // the set's own reports: every run, newest first
                var runs = admin.Call("GET", "/api/admin/users/acme/sets/" + set.Id + "/runs").List("runs");
                Assert.Equal(4, runs.Count(x => x["kind"] == "Backup"));
                Assert.Equal("1", runs.Last(x => x["kind"] == "Backup")["new"]);

                // the daily snapshot is written by the maintenance run
                env.Api.Maintenance(DateTime.UtcNow);
                Assert.Contains(set.Id, File.ReadAllText(Path.Combine(stats, "sets-" + day.ToString("yyyyMMdd") + ".tsv")));
            }
        }
    }
}
