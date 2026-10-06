using System;
using System.IO;
using System.Linq;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// "Running now" without ghosts, on the server alone: Api.SweepInterrupted over a set's store, with the Api object made
    /// but never started (no web server, no HTTP; the clock is the test's). Component contract (tests/QA/specs.py SH-02):
    ///   purpose  a run whose computer died is closed once and shown as failed — never left "running", never doubled
    ///   input    a customer with one set whose last run succeeded; a run begun at T and left open on disk (the computer
    ///            died); the minute sweep at T+4 min, T+5 min 30 s and T+12 min
    ///   expected at T+4 nothing changes (no job log, no history row, the set refuses a second run); at T+5m30 the run is
    ///            closed: one job log with BS_STOP_BY_SYSTEM_ERROR and "interrupted", one history row with that result, the
    ///            set's last result failed and its last good backup time unchanged, and the set can begin again; at T+12
    ///            nothing is added (once); a run that sends a sign of life every 4 minutes for 40 minutes is never closed
    /// Oracle: the job log file, the server's history file (RunLog), the profile on disk, SetStore's open runs.
    /// </summary>
    public class SweepComponentTests : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "obsweep-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        const string SetId = "1700000000045";
        DateTime now = DateTime.UtcNow;
        public SweepComponentTests() { SystemClock.Use(() => now); }
        public void Dispose() { SystemClock.Use(null); try { Directory.Delete(root, true); } catch (Exception) { } }

        [Fact]
        public void ADeadRun_IsClosedOnceAfterItsLease_AsAFailure_InTheHistory_AndTheSetCanBeginAgain()
        {
            var cfg = SystemConfig.Init(Path.Combine(root, "system"), "admin", "Admin-Pass-1", "localhost", new[] { Path.Combine(root, "home") + "|UNLIMITED|100" });
            using (var api = new Api(cfg))
            {
                api.UserStore.Create("sweep", "Customer-Pass-1", "", null, "COMPRESSED", "sweep@example.com", "-");
                var p = api.UserStore.LoadProfile("sweep");
                var set = new BackupSetInfo { Id = SetId, Name = "Office", Computer = "PC-7" }.ToXml();
                set.SetAttributeValue("LAST_RESULT", "BS_STOP_SUCCESS"); set.SetAttributeValue("LAST_BACKUP_COMPLETE", "1759700000000");
                p.Root.Add(set); api.UserStore.SaveProfile("sweep", p);
                var userDir = api.UserStore.UserDir("sweep");
                var t0 = now;
                var store = new SetStore(userDir, SetId);
                var job = store.BeginJob(t0);                                            // the computer dies right after this
                var log = Path.Combine(userDir, "logs", SetId, "Backup", job + ".log");
                Func<int> rows = () => api.Runs.Since(t0.AddHours(-1), t0.AddHours(1)).Count(r => r["job"] == job);

                now = t0.AddMinutes(4); api.SweepInterrupted(now);
                Assert.False(File.Exists(log));
                Assert.Equal(0, rows());
                Assert.Equal(409, Assert.Throws<ApiException>(() => new SetStore(userDir, SetId).BeginJob(now)).Status);   // still running

                now = t0.AddMinutes(5).AddSeconds(30); api.SweepInterrupted(now);
                Assert.True(File.Exists(log));
                var text = File.ReadAllText(log);
                Assert.Contains("BS_STOP_BY_SYSTEM_ERROR", text); Assert.Contains("interrupted", text);
                Assert.Equal(1, rows());
                Assert.Equal("BS_STOP_BY_SYSTEM_ERROR", api.Runs.Since(t0.AddHours(-1), t0.AddHours(1)).Single(r => r["job"] == job)["result"]);
                var e = api.UserStore.LoadProfile("sweep").FindSet(SetId);
                Assert.Equal("BS_STOP_BY_SYSTEM_ERROR", (string)e.Attribute("LAST_RESULT"));
                Assert.Equal("1759700000000", (string)e.Attribute("LAST_BACKUP_COMPLETE"));
                Assert.Empty(SetStore.OpenJobDirs(store.Dir));

                now = t0.AddMinutes(12); api.SweepInterrupted(now);
                Assert.Equal(1, rows());                                                  // once
                Assert.Equal(text, File.ReadAllText(log));
                Assert.NotNull(new SetStore(userDir, SetId).BeginJob(now));               // the set backs up again
            }
        }

        [Fact]
        public void ARunThatKeepsSendingSignsOfLife_IsNeverClosedBySweeps()
        {
            var cfg = SystemConfig.Init(Path.Combine(root, "system"), "admin", "Admin-Pass-1", "localhost", new[] { Path.Combine(root, "home") + "|UNLIMITED|100" });
            using (var api = new Api(cfg))
            {
                api.UserStore.Create("alive", "Customer-Pass-1", "", null, "COMPRESSED", "alive@example.com", "-");
                var p = api.UserStore.LoadProfile("alive"); p.Root.Add(new BackupSetInfo { Id = SetId, Name = "Office", Computer = "PC-8" }.ToXml()); api.UserStore.SaveProfile("alive", p);
                var userDir = api.UserStore.UserDir("alive");
                var t0 = now; var store = new SetStore(userDir, SetId); var job = store.BeginJob(t0);
                for (int m = 4; m <= 40; m += 4) { now = t0.AddMinutes(m); store.Touch(job); api.SweepInterrupted(now.AddSeconds(50)); }   // a 40-minute run, a sign of life every 4 minutes
                Assert.False(File.Exists(Path.Combine(userDir, "logs", SetId, "Backup", job + ".log")));
                Assert.Empty(api.Runs.Since(t0.AddHours(-1), t0.AddHours(2)));
                Assert.Single(SetStore.OpenJobDirs(store.Dir));
            }
        }
    }
}
