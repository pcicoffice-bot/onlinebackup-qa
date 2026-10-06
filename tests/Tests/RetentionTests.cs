using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// Retention — component contract (tests/QA/specs.py ST-03):
    ///   purpose  delete old versions by the policy, never the current one, never a version a kept point needs
    ///   input    90 daily backups (1 Jan – 31 Mar 2026, 22:00 UTC), "now" = 1 Apr 00:00; policy 7 days + 4 weekly + 3 monthly
    ///   expected exactly 25–31 Mar (7 days), 14 and 21 Mar (weekly: weeks start on Sunday), 31 Jan and 28 Feb (monthly)
    ///            = 11 points, worked out by hand before the run
    /// Integration: the real server and agent, files with delta chains (incremental and differential), policy "2 last
    /// backups": only the two newest points are offered, and each restores byte-identical to what it was (SHA-256).
    /// </summary>
    public class RetentionTests
    {
        static string Day(int month, int day) { return RunId.From(new DateTime(2026, month, day, 22, 0, 0, DateTimeKind.Utc)); }

        [Fact]
        public void Policy_KeepsExactlyTheExpectedPoints()
        {
            var jobs = Enumerable.Range(0, 90).Select(i => RunId.From(new DateTime(2026, 1, 1, 22, 0, 0, DateTimeKind.Utc).AddDays(i))).ToList();
            var now = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
            var keep = new RetentionPolicy { Unit = "DAYS", Period = 7, Weekly = 4, Monthly = 3 }.Keep(jobs, now);
            var expected = new[] { Day(1, 31), Day(2, 28), Day(3, 14), Day(3, 21), Day(3, 25), Day(3, 26), Day(3, 27), Day(3, 28), Day(3, 29), Day(3, 30), Day(3, 31) };
            Assert.Equal(expected, keep.OrderBy(x => x, StringComparer.Ordinal).ToArray());
        }

        [Fact]
        public void Boundaries_NoPoints_OnePoint_AllTooOld_ThePointExactlyAtTheLimit()
        {
            var now = new DateTime(2026, 4, 1, 22, 0, 0, DateTimeKind.Utc);
            var p = new RetentionPolicy { Unit = "DAYS", Period = 30 };
            Assert.Empty(p.Keep(new List<string>(), now));
            var old = new List<string> { RunId.From(now.AddDays(-400)) };
            Assert.Equal(old, p.Keep(old, now).ToList());                                         // the only (current) point is never deleted
            var jobs = new List<string> { RunId.From(now.AddDays(-31)), RunId.From(now.AddDays(-30)), RunId.From(now.AddDays(-29)) };
            Assert.Equal(new[] { jobs[1], jobs[2] }, p.Keep(jobs, now).OrderBy(x => x).ToArray());   // exactly 30 days old: kept
            Assert.Equal(new[] { jobs[2] }, new RetentionPolicy { Unit = "JOBS", Period = 1 }.Keep(jobs, now).ToArray());
            Assert.Equal(new[] { jobs[2] }, new RetentionPolicy { Unit = "JOBS", Period = 0 }.Keep(jobs, now).ToArray());   // 0 = only the latest
        }

        static string Sha(string f) { using (var s = File.OpenRead(f)) using (var h = SHA256.Create()) return Convert.ToHexString(h.ComputeHash(s)); }
        static Dictionary<string, string> Manifest(string dir) { return Directory.GetFiles(dir, "*", SearchOption.AllDirectories).ToDictionary(f => Path.GetRelativePath(dir, f), Sha); }

        [Theory]
        [InlineData("I")]
        [InlineData("D")]
        public void RealServer_KeepsOnlyThePolicysPoints_AndEachKeptPointRestoresIdentical_DeltaChainsIncluded(string deltaType)
        {
            using (var env = new Env())
            {
                env.CreateUser("ret", "Customer-Pass-1");
                var app = env.Agent("ret", "Customer-Pass-1");
                var src = env.Dir("src");
                var big = new byte[16 * 1024 * 1024]; new Random(7).NextBytes(big);
                File.WriteAllBytes(Path.Combine(src, "big.bin"), big);
                File.WriteAllText(Path.Combine(src, "small.txt"), "v0");
                File.WriteAllText(Path.Combine(src, "gone-later.txt"), "deleted in run 3");
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo
                {
                    Name = "Ret", Sources = { src }, MinDeltaFileSize = 1024 * 1024, MaxDeltaNo = 3, DeltaType = deltaType,
                    Retention = new RetentionPolicy { Unit = "JOBS", Period = 2 }
                });
                var manifests = new List<Dictionary<string, string>>();
                for (int run = 0; run < 5; run++)
                {
                    if (run > 0)
                    {
                        System.Threading.Thread.Sleep(1100);
                        for (int k = 0; k < 2000; k++) big[3 * 1024 * 1024 * run + k] ^= 0x5A;                  // a change in the middle → a delta (the 3rd change starts a new full copy: MaxDeltaNo = 3)
                        File.WriteAllBytes(Path.Combine(src, "big.bin"), big);
                        File.WriteAllText(Path.Combine(src, "small.txt"), "v" + run);
                        if (run == 3) File.Delete(Path.Combine(src, "gone-later.txt"));
                    }
                    var r = app.Backup(set.Id);
                    Assert.Equal("BS_STOP_SUCCESS", r.Result);
                    if (run > 0 && run < 3) Assert.True(r.BytesSent < 6 * 1024 * 1024, "run " + run + " sent " + r.BytesSent + " bytes: no delta");
                    manifests.Add(Manifest(src));
                }
                var m = env.Admin().Call("POST", "/api/admin/maintenance", new Msg().Set("now", RunId.From(DateTime.UtcNow.AddMinutes(1))));
                Assert.Equal(3, m.List("sets").Single(x => x["set"] == set.Id).Int("expiredPoints"));

                var restore = app.RestoreFor(app.Interactive("Customer-Pass-1", null), set.Id);
                var points = restore.Points().ToList();
                Assert.Equal(2, points.Count);
                for (int i = 0; i < 2; i++)
                {
                    var target = env.Dir("restore-" + deltaType + i);
                    restore.Run(points[i], target, null, false);
                    var got = Manifest(Path.Combine(target, Env.Rel(src)));
                    var want = manifests[3 + i];
                    Assert.Equal(want.OrderBy(x => x.Key), got.OrderBy(x => x.Key));
                }
                // and the current version once more after a second maintenance pass (nothing left that a kept point needs is removed)
                env.Admin().Call("POST", "/api/admin/maintenance", new Msg().Set("now", RunId.From(DateTime.UtcNow.AddDays(400))));
                var t2 = env.Dir("restore-now" + deltaType);
                app.RestoreFor(app.Interactive("Customer-Pass-1", null), set.Id).Run(null, t2, null, false);
                Assert.Equal(manifests[4].OrderBy(x => x.Key), Manifest(Path.Combine(t2, Env.Rel(src))).OrderBy(x => x.Key));
            }
        }
    
        /// <summary>
        /// Static review, class "catch-all": a nightly maintenance task that failed (here: retention of a set whose index is
        /// damaged) was only in the system log; nobody was told, and old versions were never deleted again. The failure is
        /// in the maintenance answer and in one alert to the administrators; the other sets are still maintained.
        /// </summary>
        [Fact]
        public void AFailingMaintenanceTask_IsAlerted_AndTheOtherSetsAreStillMaintained()
        {
            using (var smtp = new FakeSmtp())
            using (var env = new Env())
            {
                env.Admin().Call("POST", "/api/admin/settings", new Msg().Set("smtpSet", "1").Set("senderName", "Backup").Set("senderEmail", "backup@example.invalid").Set("contactsSet", "1")
                    .Add("smtp", new Msg().Set("host", "127.0.0.1").Set("port", smtp.Port).Set("security", "NONE")).Add("contacts", new Msg().Set("name", "Ops").Set("email", "ops@example.invalid")));
                env.CreateUser("maint", "Customer-Pass-1");
                var app = env.Agent("maint", "Customer-Pass-1");
                var src = env.Dir("src"); File.WriteAllText(Path.Combine(src, "a.txt"), "v1");
                var good = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "Good", Sources = { src }, Retention = new RetentionPolicy { Unit = "JOBS", Period = 1 } });
                var bad = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "Damaged", Sources = { src } });
                app.Backup(good.Id); System.Threading.Thread.Sleep(1100); File.WriteAllText(Path.Combine(src, "a.txt"), "v2"); app.Backup(good.Id);
                app.Backup(bad.Id);
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                var index = Path.Combine(env.HomeA, "maint", "files", bad.Id, "index.db");
                foreach (var f in new[] { index, index + "-wal", index + "-shm" }) if (File.Exists(f)) File.Delete(f);
                File.WriteAllText(index, "this is not a database");
                Msg m;
                try { m = env.Admin().Call("POST", "/api/admin/maintenance", new Msg().Set("now", RunId.From(DateTime.UtcNow.AddMinutes(1)))); }
                catch (OnlineBackup.Agent.AgentException e) { throw new Exception(e.Message + "\n" + string.Join("\n", Directory.GetFiles(Path.Combine(env.SystemHome, "logs"), "*.log", SearchOption.AllDirectories).Select(OnlineBackup.Core.Atomic.ReadShared).SelectMany(t => t.Split('\n')).Where(l => l.Contains("error")).TakeLast(5))); }
                Assert.Contains(m.List("errors"), e => e["error"].Contains("Damaged"));
                Assert.Equal(1, m.List("sets").Single(x => x["set"] == good.Id).Int("expiredPoints"));   // the good set was still maintained
                Assert.True(smtp.Has("Nightly maintenance") || smtp.All.Contains("=?utf-8?"), smtp.All);
            }
        }

        /// <summary>
        /// Found by the maintenance test: after every backup the server recounts the sizes of ALL the customer's sets; one set
        /// whose index is damaged made that recount throw, so the commit of every OTHER set of the customer answered 500 —
        /// stored on the server, a failure on the computer, no history row, no mail. A damaged set must not break the others.
        /// </summary>
        [Fact]
        public void ADamagedSet_DoesNotBreakTheBackupsOfTheCustomersOtherSets()
        {
            using (var env = new Env())
            {
                env.CreateUser("two", "Customer-Pass-1");
                var app = env.Agent("two", "Customer-Pass-1");
                var src = env.Dir("src"); File.WriteAllText(Path.Combine(src, "a.txt"), "v1");
                var good = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "Good", Sources = { src } });
                var bad = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "Damaged", Sources = { src } });
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(good.Id).Result);
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(bad.Id).Result);
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                var index = Path.Combine(env.HomeA, "two", "files", bad.Id, "index.db");
                foreach (var f in new[] { index, index + "-wal", index + "-shm" }) if (File.Exists(f)) File.Delete(f);
                File.WriteAllText(index, "this is not a database");

                System.Threading.Thread.Sleep(1100); File.WriteAllText(Path.Combine(src, "a.txt"), "v2");
                var r = app.Backup(good.Id);
                Assert.True(r.Result == "BS_STOP_SUCCESS", r.Result + "\n" + string.Join("\n", r.LogLines));
                Assert.Contains(env.Api.Runs.Since(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1)), m => m["job"] == r.Job && m["status"] == "ok");
                var target = env.Dir("restore");
                app.RestoreFor(app.Interactive("Customer-Pass-1", null), good.Id).Run(null, target, null, false);
                Assert.Equal("v2", File.ReadAllText(Path.Combine(target, Env.Rel(src), "a.txt")));
            }
        }
}
}
