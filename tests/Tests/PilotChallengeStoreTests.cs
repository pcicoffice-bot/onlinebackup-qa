using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// An adversarial challenge of the pilot tests of restore (RS-06) and of the server's storage (ST-03 retention, ST-04
    /// verify / quarantine / rebuild, ST-09 recycle bin) that PASSED. Each test here is stronger where the challenged one is
    /// weak: the real path instead of a stand-in, the boundary on both sides, more than one kind of damage, more than one
    /// retention policy, the order of events the challenged test did not try. Oracles are outside the product: SHA-256 of
    /// the bytes the test itself wrote or sent, and counts worked out by hand from the contract (tests/QA/specs.py).
    /// </summary>
    public partial class PilotChallengeStoreTests : IDisposable
    {
        TimeSpan jump = TimeSpan.Zero;
        readonly string root = Path.Combine(Path.GetTempPath(), "obchst-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        public PilotChallengeStoreTests() { SystemClock.Use(() => DateTime.UtcNow + jump); Directory.CreateDirectory(root); }
        public void Dispose() { SystemClock.Use(null); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(root, true); } catch (Exception) { } }

        const string Pw = "Customer-Pass-1";
        static readonly DateTime T0 = new DateTime(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc);
        static string Sha(byte[] b) { using (var h = SHA256.Create()) return Bytes.Hex(h.ComputeHash(b)); }
        static string ShaFile(string f) { return Sha(File.ReadAllBytes(f)); }
        static byte[] Rnd(int n, int seed) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }
        static void Write(string f, byte[] data, int version) { File.WriteAllBytes(f, data); File.SetLastWriteTimeUtc(f, T0.AddMinutes(version).AddMilliseconds(137 + version)); }
        static string N(string path) { return string.Join("/", path.Split('/').Select(seg => { using (var h = SHA256.Create()) return Base32.Encode(h.ComputeHash(Encoding.UTF8.GetBytes(seg))).Substring(0, 26); })); }
        static Dictionary<string, string> Tree(string dir)
        {
            return Directory.GetFiles(dir, "*", SearchOption.AllDirectories).ToDictionary(f => f.Substring(dir.Length).Replace('\\', '/').TrimStart('/'), ShaFile);
        }

        BackupRun Backup(AgentApp app, string setId)
        {
            jump += TimeSpan.FromMinutes(1);
            var r = app.Backup(setId);
            Assert.Equal("BS_STOP_SUCCESS", r.Result);
            return r;
        }

        static string ObjectFile(Env env, string login, string setId, string loc)
        {
            return Path.Combine(new[] { env.HomeA, login, "files", setId }.Concat(loc.Split('/')).ToArray());
        }

        // ====================================================================================================== RS-06

        /// <summary>
        /// Challenges the RS-06 integration and component tests: every file there is one full object of 40 KB (one chunk), so
        /// the restore test was never run over a delta chain, a file with repeated chunks, or an empty file - and the damage
        /// was always in the only (last) object of the file. Here: a file stored as a full copy + a delta (the real agent
        /// makes it), a file whose chunks repeat, an empty file, a plain one. All are candidates and identical; then only the
        /// FULL copy at the start of the chain is damaged on the server disk (the newest object is sound): the test must fail
        /// that file, never pass it.
        /// </summary>
        [Fact]
        public void RS06_ADeltaChain_RepeatedChunks_AnEmptyFile_AllCompared_ADamagedFullCopyUnderASoundDeltaFails()
        {
            using (var env = new Env())
            {
                env.CreateUser("chrs06d", Pw);
                var app = env.Agent("chrs06d", Pw);
                var src = env.Dir("src");
                string big = Path.Combine(src, "big.mdf"), rep = Path.Combine(src, "rep.bin"), empty = Path.Combine(src, "empty.txt"), plain = Path.Combine(src, "plain.txt");
                var bigData = Rnd(3 * 1024 * 1024, 41);
                var block = Rnd(64 * 1024, 42);
                var repData = Enumerable.Range(0, 24).SelectMany(_ => block).ToArray();
                Write(big, bigData, 1); Write(rep, repData, 2); Write(empty, new byte[0], 3); Write(plain, Encoding.UTF8.GetBytes("plain"), 4);
                var set = app.CreateSet(app.Interactive(Pw, null), Pw, new BackupSetInfo { Name = "Chain", Sources = { src }, Vss = false, MinDeltaFileSize = 1024 * 1024 });
                Backup(app, set.Id);
                // an in-place change of 4 KB in the middle: the second run sends a delta
                var bigV2 = (byte[])bigData.Clone(); Array.Copy(Rnd(4096, 43), 0, bigV2, 1024 * 1024, 4096);
                Write(big, bigV2, 5);
                Backup(app, set.Id);
                var listed = app.RestoreFor(app.Interactive(Pw, null), set.Id).Files(null).ToDictionary(kv => kv.Key, kv => kv.Value);
                var chain = listed[big].List("objects").OrderBy(o => o.Int("seq")).ToList();
                Assert.True(chain.Count == 2 && chain[1]["kind"] == "D", "the second run stored big.mdf as a full copy + a delta (" + chain.Count + " objects)");

                var t1 = app.RestoreTest(set.Id, 10);
                Assert.Equal(4, t1.Int("candidates")); Assert.Equal(4, t1.Int("checked")); Assert.Equal(4, t1.Int("ok")); Assert.Equal(0, t1.Int("failed"));
                foreach (var f in new[] { big, rep, empty, plain })
                    Assert.Contains(t1.List("log").Select(l => l["l"]), l => l.Contains(Path.GetFileName(f)) && l.Contains("identical"));

                // damage only the full copy (seq 0) - the delta, the newest object, is sound
                var fullFile = ObjectFile(env, "chrs06d", set.Id, chain[0]["loc"]);
                var b = File.ReadAllBytes(fullFile); b[b.Length / 2] ^= 0x5A; File.WriteAllBytes(fullFile, b);
                jump += TimeSpan.FromMinutes(1);
                var t2 = app.RestoreTest(set.Id, 10);
                Assert.Equal(4, t2.Int("checked")); Assert.Equal(3, t2.Int("ok")); Assert.Equal(1, t2.Int("failed"));
                Assert.Contains(t2.List("log").Select(l => l["l"]), l => l.Contains("big.mdf") && (l.Contains("restore test failed") || l.Contains("DIFFERENT")));
                Assert.Equal(Sha(bigV2), ShaFile(big));                                                 // the source untouched
                var temp = Path.Combine(app.Home.Dir, "temp");
                Assert.Empty(Directory.Exists(temp) ? Directory.GetFileSystemEntries(temp, "*", SearchOption.AllDirectories) : new string[0]);
            }
        }

        // ====================================================================================================== ST-03 / ST-04 rig

        sealed class Rig
        {
            public SetStore St;
            public readonly KeySet Key = KeySet.Random();
            public readonly List<string> Jobs = new List<string>();
            public readonly Dictionary<string, Dictionary<string, List<string>>> Model = new Dictionary<string, Dictionary<string, List<string>>>();
            public readonly string UserDir, SetId;
            public Rig(string userDir, string setId) { UserDir = userDir; SetId = setId; St = new SetStore(userDir, setId); }

            byte[] Obj(string content)
            {
                var ms = new MemoryStream(); var w = new BackupObject.Writer(ms, Key);
                var data = Encoding.UTF8.GetBytes(content + new string('.', 300));
                w.AddChunk(BackupObject.ChunkId(Key, data), data); w.Finish(new Msg().Set("path", content));
                return ms.ToArray();
            }

            public string Run(DateTime utc, string[] full, string[] delta, string[] deleted)
            {
                var prev = Jobs.Count == 0 ? new Dictionary<string, List<string>>() : Model[Jobs.Last()];
                var now = prev.ToDictionary(kv => kv.Key, kv => new List<string>(kv.Value));
                var j = St.BeginJob(utc);
                foreach (var f in full)
                {
                    var b = Obj(f + "@" + j);
                    St.StageObject(j, new ChkRecord { Rel = N("C/" + f), Seq = 0, Kind = "F", EncPath = "enc-" + f, Orig = b.Length, Mtime = 1 }, new MemoryStream(b), 1L << 30);
                    now[f] = new List<string> { Sha(b) };
                }
                foreach (var f in delta)
                {
                    var b = Obj(f + " delta @" + j);
                    St.StageObject(j, new ChkRecord { Rel = N("C/" + f), Seq = now[f].Count, Kind = "D", EncPath = "enc-" + f, Orig = b.Length, Mtime = 1 }, new MemoryStream(b), 1L << 30);
                    now[f].Add(Sha(b));
                }
                foreach (var f in deleted) { St.StageDelete(j, N("C/" + f)); now.Remove(f); }
                St.Commit(j, new Msg().Set("new", full.Length));
                Jobs.Add(j); Model[j] = now;
                return j;
            }

            public Dictionary<string, List<string>> Read(string point)
            {
                return St.FilesAt(point).List("files").Where(f => !f.Bool("damaged")).ToDictionary(f => f["rel"],
                    f => f.List("objects").OrderBy(o => o.Int("seq")).Select(o => ShaFile(St.ObjectPath(o["loc"]))).ToList());
            }

            public void AssertExactly(string point, IEnumerable<string> without = null)
            {
                var skip = new HashSet<string>(without ?? new string[0]);
                var want = Model[point].Where(kv => !skip.Contains(kv.Key)).ToDictionary(kv => N("C/" + kv.Key), kv => kv.Value);
                var got = Read(point);
                Assert.Equal(want.Keys.OrderBy(x => x, StringComparer.Ordinal), got.Keys.OrderBy(x => x, StringComparer.Ordinal));
                foreach (var kv in want) Assert.True(kv.Value.SequenceEqual(got[kv.Key]), "point " + point + ": the objects of a file are not the bytes sent");
            }

            public void Reopen() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); St = new SetStore(UserDir, SetId); }
            public void LoseIndex() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); foreach (var f in Directory.GetFiles(St.Dir, "index.db*")) File.Delete(f); Reopen(); }
        }

        static readonly string[] None = new string[0];

        // ====================================================================================================== ST-03

        /// <summary>
        /// Challenges Retention_OnTheStore_*: its "days" policy was tried only 400 days later, when every point is far too
        /// old - never at the boundary, never with a kept point that needs versions made by expired points. Here policy
        /// "30 days" at a fixed now, runs on both sides of the boundary (30 d + 1 s old, 29 d 23:59:59 old) and a delta chain
        /// that starts 45 days ago and is needed by a kept point. Expected (by hand): the 4 points older than 30 days expire,
        /// the 4 younger are kept, each exactly the bytes sent; then "2 last backups" on the same store, then "1 last backup +
        /// 3 weekly" (mid-week noon runs), each as worked out by hand.
        /// </summary>
        [Fact]
        public void ST03_Days_BothSidesOfTheBoundary_AChainFromAnExpiredPointStaysForAKeptOne_ThenJobsAndWeekly()
        {
            var now = new DateTime(2026, 6, 17, 12, 0, 0, DateTimeKind.Utc);   // a Wednesday
            var r = new Rig(Path.Combine(root, "user"), "1700000000611");
            var at = new[] { now.AddDays(-45), now.AddDays(-40), now.AddDays(-31), now.AddDays(-30).AddSeconds(-1),
                             now.AddDays(-30).AddSeconds(1), now.AddDays(-20), now.AddDays(-10), now.AddDays(-1) };
            r.Run(at[0], new[] { "a.txt", "b.txt", "c.txt", "d.pst" }, None, None);          // d's full copy
            r.Run(at[1], new[] { "a.txt" }, new[] { "d.pst" }, None);                         // d delta 1
            r.Run(at[2], new[] { "a.txt" }, None, new[] { "b.txt" });
            r.Run(at[3], new[] { "a.txt" }, new[] { "d.pst" }, None);                         // d delta 2 (made by a point that expires)
            r.Run(at[4], new[] { "a.txt", "e.txt" }, None, None);                             // kept: needs d's full + 2 deltas
            r.Run(at[5], new[] { "a.txt", "d.pst" }, None, None);                             // d's new full copy: the old chain leaves Current
            r.Run(at[6], new[] { "a.txt" }, None, new[] { "e.txt" });
            r.Run(at[7], new[] { "a.txt" }, None, None);
            var j = r.Jobs;
            foreach (var p in j) r.AssertExactly(p);

            var res = r.St.ApplyRetention(new RetentionPolicy { Unit = "DAYS", Period = 30 }, now);
            Assert.Equal(new[] { j[4], j[5], j[6], j[7] }, r.St.Points());
            Assert.Equal(new[] { j[0], j[1], j[2], j[3] }, res.List("expired").Select(m => m["id"]).ToArray());
            foreach (var p in new[] { j[4], j[5], j[6], j[7] }) r.AssertExactly(p);           // point 5 still has d's whole old chain
            foreach (var k in new[] { 0, 1, 2, 3 }) Assert.Equal(404, Assert.Throws<ApiException>(() => r.St.FilesAt(j[k])).Status);
            Assert.Equal(0, r.St.VerifyAll().Int("bad"));
            // the same policy again a second later changes nothing (idempotent), a kept point does not move
            var again = r.St.ApplyRetention(new RetentionPolicy { Unit = "DAYS", Period = 30 }, now.AddSeconds(1));
            Assert.Equal(0, again.Int("expiredPoints"));
            // 2 seconds later the 30 d - 1 s point crosses the boundary: it alone goes
            var cross = r.St.ApplyRetention(new RetentionPolicy { Unit = "DAYS", Period = 30 }, now.AddSeconds(3));
            Assert.Equal(new[] { j[4] }, cross.List("expired").Select(m => m["id"]).ToArray());
            foreach (var p in new[] { j[5], j[6], j[7] }) r.AssertExactly(p);
            // the lost index does not bring an expired point back
            r.LoseIndex();
            Assert.Equal(new[] { j[5], j[6], j[7] }, r.St.Points());
            foreach (var p in new[] { j[5], j[6], j[7] }) r.AssertExactly(p);

            // more points, then "2 last backups"
            var w = new List<string>();
            foreach (var d in new[] { -9, -8, -7, -6, -5 }) w.Add(r.Run(now.AddDays(7 * 4 + d), new[] { "a.txt" }, None, None)); // Mon 6 .. Fri 10 Jul (noon)
            w.Add(r.Run(now.AddDays(7 * 5), new[] { "a.txt" }, None, None));                                                    // Wed 22 Jul
            var res2 = r.St.ApplyRetention(new RetentionPolicy { Unit = "JOBS", Period = 2 }, now.AddDays(36));
            Assert.Equal(new[] { w[4], w[5] }, r.St.Points());
            r.AssertExactly(w[4]); r.AssertExactly(w[5]);
            Assert.Equal(0, r.St.VerifyAll().Int("bad"));
            // "1 last backup + 3 weekly" at noon on Thu 23 Jul: the weeks (Sun-Sat) of 19 Jul and 5 Jul have their last run
            // kept (22 Jul = the last backup, 10 Jul); nothing older is left to keep
            r.St.ApplyRetention(new RetentionPolicy { Unit = "JOBS", Period = 1, Weekly = 3 }, now.AddDays(36));
            Assert.Equal(new[] { w[4], w[5] }, r.St.Points());
            r.AssertExactly(w[4]); r.AssertExactly(w[5]);
        }

        /// <summary>
        /// The weekly part of GFS on its own (the challenged GFS test tried only monthly and quarterly): a run every weekday
        /// at noon for three weeks; "1 last backup + 2 weekly" keeps the last run of the newest week and of the week before
        /// (Friday), everything else goes, every kept point exact.
        /// </summary>
        [Fact]
        public void ST03_GfsWeekly_KeepsTheLastRunOfEachWeek_NotTheFirst_AndNothingElse()
        {
            var r = new Rig(Path.Combine(root, "user"), "1700000000612");
            var mon = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);    // Monday 1 Jun 2026
            Assert.Equal(DayOfWeek.Monday, mon.DayOfWeek);
            r.Run(mon, new[] { "x.txt", "fixed.txt" }, None, None);
            for (int d = 1; d < 19; d++) if (mon.AddDays(d).DayOfWeek != DayOfWeek.Saturday && mon.AddDays(d).DayOfWeek != DayOfWeek.Sunday) r.Run(mon.AddDays(d), new[] { "x.txt" }, None, None);
            var j = r.Jobs;
            var lastFri2 = j.Single(x => RunId.Parse(x) == mon.AddDays(11));                // Fri 12 Jun
            var newest = j.Last();                                                            // Fri 19 Jun
            r.St.ApplyRetention(new RetentionPolicy { Unit = "JOBS", Period = 1, Weekly = 2 }, mon.AddDays(19));
            Assert.Equal(new[] { lastFri2, newest }, r.St.Points());
            r.AssertExactly(lastFri2); r.AssertExactly(newest);
            Assert.Equal(0, r.St.VerifyAll().Int("bad"));
        }

        // ====================================================================================================== ST-04

        /// <summary>
        /// Challenges Upgrade_ADamagedObjectInAVersion1Index_IsQuarantined (and SetStoreComponentTests): there the damage is
        /// always ONE flipped byte in the middle of a FULL copy. Here four kinds at once: an object cut short, an object
        /// gone from the disk, the last byte flipped, and only the DELTA of a chain damaged. Expected: all 4 found and
        /// quarantined, all 4 asked again, the newest point lists the 4 as damaged (not silently left out), the older point
        /// keeps d's full copy exactly (it never needed the delta); after full copies are sent again, no resend is left.
        /// </summary>
        [Fact]
        public void ST04_FourKindsOfDamage_EachFoundQuarantinedAskedAgain_TheOlderPointStaysExact()
        {
            var r = new Rig(Path.Combine(root, "user"), "1700000000621");
            var t = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
            var j1 = r.Run(t, new[] { "a.txt", "b.txt", "c.txt", "d.pst", "ok.txt" }, None, None);
            var j2 = r.Run(t.AddDays(1), None, new[] { "d.pst" }, None);
            var files = r.St.FilesAt(j2).List("files").ToDictionary(f => f["rel"]);
            Func<string, int, string> obj = (f, seq) => r.St.ObjectPath(files[N("C/" + f)].List("objects").Single(o => o.Int("seq") == seq)["loc"]);
            string pa = obj("a.txt", 0), pb = obj("b.txt", 0), pc = obj("c.txt", 0), pd1 = obj("d.pst", 1);
            var ba = File.ReadAllBytes(pa); File.WriteAllBytes(pa, ba.Take(ba.Length - 7).ToArray());
            File.Delete(pb);
            var bc = File.ReadAllBytes(pc); bc[bc.Length - 1] ^= 0x01; File.WriteAllBytes(pc, bc);
            var bd = File.ReadAllBytes(pd1); bd[bd.Length / 2] ^= 0x80; File.WriteAllBytes(pd1, bd);

            var v = r.St.VerifyAll();
            Assert.Equal(4, v.Int("bad"));
            foreach (var p in new[] { pa, pc, pd1 }) Assert.False(File.Exists(p), p + " is still in the store");
            Assert.Equal(new[] { "a.txt", "b.txt", "c.txt", "d.pst" }.Select(f => N("C/" + f)).OrderBy(x => x, StringComparer.Ordinal), r.St.Resend().OrderBy(x => x, StringComparer.Ordinal));
            var damaged = r.St.FilesAt(j2).List("files").Where(f => f.Bool("damaged")).Select(f => f["rel"]).OrderBy(x => x, StringComparer.Ordinal);
            Assert.Equal(new[] { "a.txt", "b.txt", "c.txt", "d.pst" }.Select(f => N("C/" + f)).OrderBy(x => x, StringComparer.Ordinal), damaged);
            r.AssertExactly(j2, new[] { "a.txt", "b.txt", "c.txt", "d.pst" });               // ok.txt as sent
            r.AssertExactly(j1, new[] { "a.txt", "b.txt", "c.txt" });                          // d's full copy at point 1: exact
            Assert.Equal(0, r.St.VerifyAll().Int("bad"));

            // the next run sends full copies of the four: nothing left to ask, the new point exact
            var j3 = r.Run(t.AddDays(2), new[] { "a.txt", "b.txt", "c.txt", "d.pst" }, None, None);
            Assert.Empty(r.St.Resend());
            r.AssertExactly(j3);
        }

        // ====================================================================================================== ST-09

        static Dictionary<string, string> RestoreTree(Env env, AgentApp app, string setId, string source, string name)
        {
            var target = env.Dir(name);
            var r = app.RestoreFor(app.Interactive(Pw, null), setId, Pw);
            r.Run(null, target, null, false);
            Assert.Equal(0, r.Failed);
            return Tree(Path.Combine(target, Env.Rel(source)));
        }

        /// <summary>
        /// A set deleted, then its customer deleted the next day (the bin of the set moves inside the customer's folder),
        /// then the customer restored: the set must still be in the bin with its first erase time, restorable byte-identical;
        /// and erased exactly when ITS 14 days are over (not those of the customer). Challenges the recycle tests, which
        /// deleted a set and a customer only one after the other with a restore in between.
        /// </summary>
        [Fact]
        public void ST09_ASetInTheBin_ThenItsCustomerDeletedAndRestored_TheSetIsStillRestorable_AndErasedOnItsOwnDay()
        {
            var cfg = SystemConfig.Init(Path.Combine(root, "system"), "admin", "Admin-Pass-1", "localhost", new[] { Path.Combine(root, "home") + "|UNLIMITED|100" });
            var users = new Users(cfg);
            users.Create("acme", Pw, "acme", 1L << 30, null, null, null);
            const string setA = "1700000000931", setB = "1700000000932";
            var p = users.LoadProfile("acme");
            foreach (var s in new[] { setA, setB }) p.Root.Add(new XElement("BACKUP_SET", new XAttribute("ID", s), new XAttribute("NAME", "Set " + s), new XAttribute("TYPE", "FILE")));
            users.SaveProfile("acme", p);
            var r = new Rig(users.UserDir("acme"), setA);
            var j1 = r.Run(new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc), new[] { "a.txt", "b.txt" }, None, None);
            var j2 = r.Run(new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc), new[] { "a.txt" }, None, None);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var data = Tree(Path.Combine(users.UserDir("acme"), "files", setA));
            var d0 = new DateTime(2026, 9, 10, 9, 0, 0, DateTimeKind.Utc);

            Assert.Null(Recycle.Request(cfg, users, "acme", setA, "admin", null, d0));
            Assert.Null(Recycle.Request(cfg, users, "acme", null, "admin", null, d0.AddDays(1)));
            Assert.DoesNotContain("acme", users.Logins());
            Assert.Equal(0, users.PurgeRecycled(d0.AddDays(13)));
            users.RestoreRecycled(users.Recycled().Single(x => x["kind"] == "user")["id"], "admin", null);
            var item = users.Recycled().Single();
            Assert.Equal("set", item["kind"]); Assert.Equal(setA, item["set"]);
            Assert.Equal(RunId.UnixMs(d0.AddDays(14)), item.Long("erase"));
            // 1 minute before its 14 days: kept; restorable byte-identical
            Assert.Equal(0, users.PurgeRecycled(d0.AddDays(14).AddMinutes(-1)));
            users.RestoreRecycled(item["id"], "admin", null);
            Assert.Equal(data, Tree(Path.Combine(users.UserDir("acme"), "files", setA)));
            r.Reopen();
            Assert.Equal(new[] { j1, j2 }, r.St.Points());
            r.AssertExactly(j1); r.AssertExactly(j2);
            Assert.NotNull(users.LoadProfile("acme").FindSet(setB));                             // the other set never left
        }
    }
}
