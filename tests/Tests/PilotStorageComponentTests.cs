using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// Pilot release blockers of the server's storage, one component at a time and without a web server (no Env): the set's
    /// store (SetStore), the users and their recycle bin (Users, Recycle), the settings backup (ConfigBackup). Each test is
    /// written against its contract in tests/QA/specs.py; the oracle is outside the code under test (the SHA-256 of the bytes
    /// the test itself sent, or of the files of the fixture's manifest, and counts worked out by hand).
    ///
    /// ST-03 retention   input    6 runs of a set on 2-7 Mar 2026 12:00 UTC: a.txt changed every run; b.txt deleted in run 3;
    ///                            c.txt never changed; d.txt a full copy and 2 deltas, then a new full copy in run 5; e.txt
    ///                            new in run 4. Policy "3 last backups", then "1 day" 400 days later.
    ///                   expected points 4, 5, 6 kept, 1-3 gone; a run folder is deleted only when no kept point needs a
    ///                            version in it (folders of runs 2, 3, 4 deleted, 5 and 6 kept: point 4 needs d's chain and
    ///                            a4); every kept point gives exactly its files, each object with the SHA-256 of the bytes sent;
    ///                            the current version is never deleted. An interrupted retention (points marked, part of a
    ///                            folder deleted) is finished by the next one; after a lost index the expired points stay gone.
    ///                   GFS: only what is decided is tested — the last backup of a month at mid-month noon UTC, a time
    ///                   that is in the same month in every time zone. The case of a backup near midnight at a month's
    ///                   boundary (UTC or the server's zone) is OWNER DECISION 77, open: it is not tested here (see
    ///                   RetentionZoneComponentTests, skipped "NEEDS OWNER DECISION").
    /// ST-08 upgrade     input    tests/Fixtures/upgrade/v1-2026-10.zip — a server's folders as version 1 left them (5 Oct 2026,
    ///                            before the B-4 change of 6 Oct 2026 added the "lost" table to the index: the old format)
    ///                   expected the store opens as it is (no object, .chk or jobs.log changed); 3 points; every file of
    ///                            every point, decrypted with the customer's password, has the SHA-256 the manifest recorded;
    ///                            a damaged object is quarantined in the old index too; a lost old index is rebuilt; new runs
    ///                            go on beside the old points; the old settings and users open and the password signs in
    /// ST-09 recycle bin input    a customer with a set holding 2 points; delete the set, then the customer
    ///                   expected gone from the profile / list at once, in the bin with erase time = deletion + 14 days;
    ///                            restored byte-identical (SHA-256 of every file); kept at 13 days 23 h, erased at 14 days
    ///                            1 min; a restore over an existing set / customer is refused 409 and changes nothing; the
    ///                            bin survives a restart; with two administrators the one who asked cannot approve, a request
    ///                            older than 7 days is refused and deletes nothing
    /// ST-10 settings    input    the server's settings, a customer's profile, devices and kept key, his backups
    ///                   expected a zip with exactly the settings files (byte-identical) and no backup data; restoring it
    ///                            as documented gives back byte-identical settings that load; 30 kept; a copy folder that
    ///                            fails does not stop the local backup; a crashed earlier backup does not count
    /// </summary>
    public class PilotStorageComponentTests : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "obpilotst-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(root, true); } catch (Exception) { } }

        static string Sha(byte[] b) { using (var h = SHA256.Create()) return Bytes.Hex(h.ComputeHash(b)); }
        static string ShaFile(string f) { using (var s = File.OpenRead(f)) using (var h = SHA256.Create()) return Bytes.Hex(h.ComputeHash(s)); }
        static string N(string path) { return string.Join("/", path.Split('/').Select(seg => { using (var h = SHA256.Create()) return Base32.Encode(h.ComputeHash(Encoding.UTF8.GetBytes(seg))).Substring(0, 26); })); }

        /// <summary>Every file under a folder → its SHA-256 (paths with '/').</summary>
        static Dictionary<string, string> Tree(string dir, Func<string, bool> skip = null)
        {
            if (!Directory.Exists(dir)) return new Dictionary<string, string>();
            return Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                .Select(f => new { f, rel = f.Substring(dir.Length).Replace('\\', '/').TrimStart('/') })
                .Where(x => skip == null || !skip(x.rel)).ToDictionary(x => x.rel, x => ShaFile(x.f));
        }

        // ================================================================== ST-03 retention

        sealed class RetentionRig
        {
            public SetStore St;
            public readonly KeySet Key = KeySet.Random();
            public readonly List<string> Jobs = new List<string>();
            /// <summary>The model, worked out from what was sent: point → file → the SHA-256 of each object of its chain.</summary>
            public readonly Dictionary<string, Dictionary<string, List<string>>> Model = new Dictionary<string, Dictionary<string, List<string>>>();
            public readonly Dictionary<string, long> SizeOf = new Dictionary<string, long>();   // sha → bytes

            byte[] Obj(string content)
            {
                var ms = new MemoryStream(); var w = new BackupObject.Writer(ms, Key);
                var data = Encoding.UTF8.GetBytes(content + new string('.', 300));
                w.AddChunk(BackupObject.ChunkId(Key, data), data); w.Finish(new Msg().Set("path", content));
                return ms.ToArray();
            }

            /// <summary>One run: full copies, deltas, deletions; the model of the new point follows from the previous one.</summary>
            public string Run(DateTime utc, string[] full, string[] delta, string[] deleted)
            {
                var prev = Jobs.Count == 0 ? new Dictionary<string, List<string>>() : Model[Jobs.Last()];
                var now = prev.ToDictionary(kv => kv.Key, kv => new List<string>(kv.Value));
                var j = St.BeginJob(utc);
                foreach (var f in full)
                {
                    var b = Obj(f + "@" + j); SizeOf[Sha(b)] = b.Length;
                    var rec = St.StageObject(j, new ChkRecord { Rel = N("C/" + f), Seq = 0, Kind = "F", EncPath = "enc-" + f, Orig = b.Length, Mtime = 1 }, new MemoryStream(b), 1L << 30);
                    Assert.Equal(Sha(b), rec.Sha256);
                    now[f] = new List<string> { Sha(b) };
                }
                foreach (var f in delta)
                {
                    var b = Obj(f + " delta @" + j); SizeOf[Sha(b)] = b.Length;
                    St.StageObject(j, new ChkRecord { Rel = N("C/" + f), Seq = now[f].Count, Kind = "D", EncPath = "enc-" + f, Orig = b.Length, Mtime = 1 }, new MemoryStream(b), 1L << 30);
                    now[f].Add(Sha(b));
                }
                foreach (var f in deleted) { St.StageDelete(j, N("C/" + f)); now.Remove(f); }
                St.Commit(j, new Msg().Set("new", full.Length));
                Jobs.Add(j); Model[j] = now;
                return j;
            }

            /// <summary>The point as the store gives it, read back from the disk: file → SHA-256 of each object file in order.</summary>
            public Dictionary<string, List<string>> Read(string point)
            {
                return St.FilesAt(point).List("files").ToDictionary(f => f["rel"],
                    f => f.List("objects").OrderBy(o => o.Int("seq")).Select(o => ShaFile(St.ObjectPath(o["loc"]))).ToList());
            }

            public void AssertPointIsExactlyItsModel(string point)
            {
                var want = Model[point].ToDictionary(kv => N("C/" + kv.Key), kv => kv.Value);
                var got = Read(point);
                Assert.Equal(want.Keys.OrderBy(x => x, StringComparer.Ordinal), got.Keys.OrderBy(x => x, StringComparer.Ordinal));
                foreach (var kv in want) Assert.True(kv.Value.SequenceEqual(got[kv.Key]), "point " + point + ": the objects of a file are not the bytes sent");
            }
        }

        static readonly DateTime Mar2 = new DateTime(2026, 3, 2, 12, 0, 0, DateTimeKind.Utc);
        static readonly string[] None = new string[0];

        RetentionRig SixRuns()
        {
            var r = new RetentionRig { St = new SetStore(Path.Combine(root, "user"), "1700000000301") };
            r.Run(Mar2, new[] { "a.txt", "b.txt", "c.txt", "d.pst" }, None, None);
            r.Run(Mar2.AddDays(1), new[] { "a.txt" }, new[] { "d.pst" }, None);
            r.Run(Mar2.AddDays(2), new[] { "a.txt" }, new[] { "d.pst" }, new[] { "b.txt" });
            r.Run(Mar2.AddDays(3), new[] { "a.txt", "e.txt" }, None, None);
            r.Run(Mar2.AddDays(4), new[] { "a.txt", "d.pst" }, None, None);
            r.Run(Mar2.AddDays(5), new[] { "a.txt" }, None, None);
            Assert.Equal(r.Jobs, r.St.Points());
            foreach (var j in r.Jobs) r.AssertPointIsExactlyItsModel(j);   // the rig itself: every point as sent
            return r;
        }

        [Fact]
        public void Retention_OnTheStore_ExpiresOnlyPointsOutsideThePolicy_NeverAVersionAKeptPointNeeds_EveryKeptPointIsTheBytesSent()
        {
            var r = SixRuns(); var j = r.Jobs; var dir = r.St.Dir;
            var res = r.St.ApplyRetention(new RetentionPolicy { Unit = "JOBS", Period = 3 }, Mar2.AddDays(6));

            Assert.Equal(new[] { j[3], j[4], j[5] }, r.St.Points());
            Assert.Equal(new[] { j[0], j[1], j[2] }, res.List("expired").Select(m => m["id"]).ToArray());
            foreach (var gone in new[] { j[0], j[1], j[2] }) Assert.Equal(404, Assert.Throws<ApiException>(() => r.St.FilesAt(gone)).Status);
            // folders: run 2 held a1, run 3 held a2 and b1, run 4 held a3 — no kept point needs them; run 5 holds a4 and d's
            // first chain (point 4 needs them), run 6 holds a5 (point 5 needs it)
            foreach (var k in new[] { 1, 2, 3 }) Assert.False(Directory.Exists(Path.Combine(dir, j[k])), "run folder " + j[k] + " should be deleted");
            foreach (var k in new[] { 4, 5 }) Assert.True(Directory.Exists(Path.Combine(dir, j[k])), "run folder " + j[k] + " is needed by a kept point");
            Assert.Equal(4, res.Int("deletedObjects"));
            long freed = new[] { r.Model[j[0]]["a.txt"][0], r.Model[j[1]]["a.txt"][0], r.Model[j[0]]["b.txt"][0], r.Model[j[2]]["a.txt"][0] }.Sum(s => r.SizeOf[s]);
            Assert.Equal(freed, res.Long("freed"));
            foreach (var k in new[] { 3, 4, 5 }) r.AssertPointIsExactlyItsModel(j[k]);       // point 4 still has d's full copy + 2 deltas
            Assert.Equal(0, r.St.VerifyAll().Int("bad"));

            // 400 days later, "1 day": every point is too old — the current one is still never deleted
            var res2 = r.St.ApplyRetention(new RetentionPolicy { Unit = "DAYS", Period = 1 }, Mar2.AddDays(400));
            Assert.Equal(new[] { j[5] }, r.St.Points());
            Assert.Equal(2, res2.Int("expiredPoints"));
            foreach (var k in new[] { 3, 4, 5 }) Assert.False(Directory.Exists(Path.Combine(dir, j[k])));
            r.AssertPointIsExactlyItsModel(j[5]);
            Assert.Equal(4, r.St.FilesAt(null).List("files").Count);                          // a6, c1, d5, e4
            // and again: nothing more to do, nothing lost
            var res3 = r.St.ApplyRetention(new RetentionPolicy { Unit = "DAYS", Period = 1 }, Mar2.AddDays(800));
            Assert.Equal(0, res3.Int("expiredPoints")); Assert.Equal(0, res3.Int("deletedObjects"));
            r.AssertPointIsExactlyItsModel(j[5]);
        }

        [Fact]
        public void Retention_AnInterruptedRun_IsFinishedByTheNext_AndALostIndexDoesNotBringExpiredPointsBack()
        {
            var r = SixRuns(); var j = r.Jobs; var dir = r.St.Dir;
            // a retention that stopped in the middle: the points were marked expired, run 3's folder was half deleted
            r.St.ExpireJobs(new[] { j[0], j[1], j[2] });
            var half = Directory.GetFiles(Path.Combine(dir, j[2]), "*.000", SearchOption.AllDirectories).First();
            File.Delete(half);
            Assert.Equal(new[] { j[3], j[4], j[5] }, r.St.Points());

            var res = r.St.ApplyRetention(new RetentionPolicy { Unit = "JOBS", Period = 3 }, Mar2.AddDays(6));
            Assert.Equal(0, res.Int("expiredPoints"));
            foreach (var k in new[] { 1, 2, 3 }) Assert.False(Directory.Exists(Path.Combine(dir, j[k])), "run folder " + j[k] + " was left by the interrupted retention");
            foreach (var k in new[] { 3, 4, 5 }) r.AssertPointIsExactlyItsModel(j[k]);

            // the index is lost after the retention: rebuilt from the disk, the expired points do not come back
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var f in Directory.GetFiles(dir, "index.db*")) File.Delete(f);
            r.St = new SetStore(Path.Combine(root, "user"), "1700000000301");
            Assert.Equal(new[] { j[3], j[4], j[5] }, r.St.Points());
            foreach (var k in new[] { 3, 4, 5 }) r.AssertPointIsExactlyItsModel(j[k]);
            Assert.Equal(0, r.St.VerifyAll().Int("bad"));

            // and the set goes on: a new run, the next retention keeps the 3 newest, each as sent
            var j7 = r.Run(Mar2.AddDays(6), new[] { "a.txt" }, None, new[] { "e.txt" });
            r.St.ApplyRetention(new RetentionPolicy { Unit = "JOBS", Period = 3 }, Mar2.AddDays(7));
            Assert.Equal(new[] { j[4], j[5], j7 }, r.St.Points());
            foreach (var p in new[] { j[4], j[5], j7 }) r.AssertPointIsExactlyItsModel(p);
        }

        [Fact]
        public void Retention_Gfs_TheLastBackupOfEachMonth_AtMidMonthNoon_IsKept_TheOthersGo()
        {
            // DECIDED part only: every run is at 12:00 UTC on the 10th or 20th — the same day and month in every time zone.
            // A run near midnight at a month's boundary is owner decision 77 (open) and is deliberately not used here.
            var r = new RetentionRig { St = new SetStore(Path.Combine(root, "user"), "1700000000302") };
            var at = new[] { new DateTime(2026, 1, 10, 12, 0, 0, DateTimeKind.Utc), new DateTime(2026, 1, 20, 12, 0, 0, DateTimeKind.Utc),
                             new DateTime(2026, 2, 10, 12, 0, 0, DateTimeKind.Utc), new DateTime(2026, 2, 20, 12, 0, 0, DateTimeKind.Utc),
                             new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc), new DateTime(2026, 3, 20, 12, 0, 0, DateTimeKind.Utc) };
            r.Run(at[0], new[] { "ledger.csv", "fixed.txt" }, None, None);
            for (int i = 1; i < at.Length; i++) r.Run(at[i], new[] { "ledger.csv" }, None, None);
            var j = r.Jobs;
            // "the last backup" + 2 monthly: March's last (20 Mar, also the latest) and February's last (20 Feb); January goes
            r.St.ApplyRetention(new RetentionPolicy { Unit = "JOBS", Period = 1, Monthly = 2 }, new DateTime(2026, 3, 21, 12, 0, 0, DateTimeKind.Utc));
            Assert.Equal(new[] { j[3], j[5] }, r.St.Points());
            r.AssertPointIsExactlyItsModel(j[3]); r.AssertPointIsExactlyItsModel(j[5]);
            // the yearly one keeps the year's last only (the same run here); quarterly: Q1's last
            r.St.ApplyRetention(new RetentionPolicy { Unit = "JOBS", Period = 1, Quarterly = 1 }, new DateTime(2026, 3, 21, 12, 0, 0, DateTimeKind.Utc));
            Assert.Equal(new[] { j[5] }, r.St.Points());
            r.AssertPointIsExactlyItsModel(j[5]);
        }

        // ================================================================== ST-08 earlier versions of the storage

        const string UpgradeRoot = "@@UPGRADE_ROOT@@", UpgradeLogin = "upgrade2026", UpgradePassword = "Customer-Pass-1";

        static string UpgradeFixture()
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
                if (Directory.Exists(Path.Combine(d.FullName, "tests", "Tests")))
                {
                    var z = Path.Combine(d.FullName, "tests", "Fixtures", "upgrade", "v1-2026-10.zip");
                    if (File.Exists(z)) return z;
                    break;
                }
            throw NotTested.Because("tests/Fixtures/upgrade/v1-2026-10.zip is not in this checkout");
        }

        static bool IsText(string f)
        {
            var b = File.ReadAllBytes(f);
            if (b.Length > 4 * 1024 * 1024 || Array.IndexOf(b, (byte)0) >= 0) return false;
            try { new UTF8Encoding(false, true).GetString(b); return true; } catch (DecoderFallbackException) { return false; }
        }

        sealed class OldServer
        {
            public string Root, SetId, Original; public KeySet Key;
            public List<Dictionary<string, string>> Points;
            public string UserDir { get { return Path.Combine(Root, "homeA", UpgradeLogin); } }
            public string SetDir { get { return Path.Combine(UserDir, "files", SetId); } }
        }

        /// <summary>The version-1 folders, unpacked as UpgradeTests does (its root placeholder replaced by this folder).</summary>
        OldServer Unpack()
        {
            var zip = UpgradeFixture();
            var stage = Path.Combine(root, "stage"); var dst = Path.Combine(root, "old");
            ZipFile.ExtractToDirectory(zip, stage);
            foreach (var f in Directory.GetFiles(stage, "*", SearchOption.AllDirectories))
            {
                var to = Path.Combine(dst, f.Substring(stage.Length + 1));
                Directory.CreateDirectory(Path.GetDirectoryName(to));
                if (IsText(f)) File.WriteAllText(to, File.ReadAllText(f).Replace(UpgradeRoot, dst)); else File.Copy(f, to);
            }
            var m = XElement.Parse(File.ReadAllText(Path.Combine(dst, "manifest.xml")));
            var o = new OldServer
            {
                Root = dst, SetId = (string)m.Attribute("SET"), Original = (string)m.Attribute("ORIGINAL"),
                Points = m.Elements("POINT").Select(p => p.Elements("FILE").ToDictionary(f => (string)f.Attribute("PATH"), f => (string)f.Attribute("SHA256"))).ToList()
            };
            var keyEl = XElement.Parse(File.ReadAllText(Path.Combine(o.UserDir, "db", "Profile.xml"))).Elements("BACKUP_SET").Single(e => (string)e.Attribute("ID") == o.SetId).Element("ENCRYPTING_KEY");
            o.Key = KeySet.Derive(UpgradePassword, Convert.FromBase64String((string)keyEl.Attribute("SALT")));
            Assert.Equal((string)keyEl.Attribute("KEY"), o.Key.CheckValue());   // the customer's password is the set's key
            return o;
        }

        /// <summary>A point as the store gives it, decrypted outside the product's restore: original path → SHA-256 of the file.</summary>
        static Dictionary<string, string> Decrypt(SetStore st, OldServer o, string point, ICollection<string> damaged = null)
        {
            var got = new Dictionary<string, string>();
            foreach (var f in st.FilesAt(point).List("files"))
            {
                var path = NameCipher.DecryptPath(o.Key, f["enc"]).Replace('\\', '/');
                Assert.StartsWith(o.Original, path);
                var rel = path.Substring(o.Original.Length).TrimStart('/');
                if (f.Bool("damaged")) { if (damaged != null) damaged.Add(rel); continue; }
                var streams = f.List("objects").OrderBy(x => x.Int("seq")).Select(x => (Stream)File.OpenRead(st.ObjectPath(x["loc"]))).ToList();
                try
                {
                    var where = new Dictionary<string, KeyValuePair<int, long>>(); Msg header = null;
                    for (int i = 0; i < streams.Count; i++)
                    {
                        header = BackupObject.ReadHeader(streams[i], o.Key);
                        foreach (var kv in BackupObject.ChunkOffsets(streams[i], header)) where[kv.Key] = new KeyValuePair<int, long>(i, kv.Value);
                    }
                    var bytes = new MemoryStream();
                    foreach (var c in header.List("recipe")) { var at = where[c["h"]]; var b = BackupObject.ReadChunkAt(streams[at.Key], at.Value, o.Key, c["h"]); bytes.Write(b, 0, b.Length); }
                    got[rel] = Sha(bytes.ToArray());
                }
                finally { foreach (var s in streams) s.Dispose(); }
            }
            return got;
        }

        static void AssertSame(Dictionary<string, string> want, Dictionary<string, string> got, string what)
        {
            Assert.True(want.OrderBy(k => k.Key, StringComparer.Ordinal).SequenceEqual(got.OrderBy(k => k.Key, StringComparer.Ordinal)),
                what + ": " + string.Join(", ", got.OrderBy(k => k.Key).Select(k => k.Key + "=" + k.Value.Substring(0, 8))) + " ≠ " + string.Join(", ", want.OrderBy(k => k.Key).Select(k => k.Key + "=" + k.Value.Substring(0, 8))));
        }

        static bool IsIndex(string rel) { return rel.StartsWith("index.db", StringComparison.Ordinal); }

        [Fact]
        public void Upgrade_AVersion1Store_OpensAsItIs_EveryFileOfEveryPointIsTheManifestsSha256_AndItsSettingsAndUsersOpen()
        {
            var o = Unpack();
            var before = Tree(o.SetDir, IsIndex);
            var st = new SetStore(o.UserDir, o.SetId);
            var points = st.Points();
            Assert.Equal(o.Points.Count, points.Count);
            for (int i = 0; i < points.Count; i++) AssertSame(o.Points[i], Decrypt(st, o, points[i]), "point " + i);
            var v = st.VerifyAll();
            Assert.Equal(0, v.Int("bad")); Assert.Equal(v.Int("checked"), v.Int("ok")); Assert.True(v.Int("checked") > 0);
            Assert.Equal(before, Tree(o.SetDir, IsIndex));                                   // opened as it is: not one stored byte changed

            // the version-1 settings and users
            var cfg = SystemConfig.Load(Path.Combine(o.Root, "system"));
            var users = new Users(cfg);
            Assert.Equal(new[] { UpgradeLogin }, users.Logins());
            var p = users.LoadProfile(UpgradeLogin);
            Assert.True(PasswordHash.Verify(UpgradePassword, p.Get("HASHED_PWD")));
            Assert.False(PasswordHash.Verify("Customer-Pass-2", p.Get("HASHED_PWD")));
            Assert.Equal("Office files", (string)p.FindSet(o.SetId).Attribute("NAME"));
        }

        [Fact]
        public void Upgrade_ADamagedObjectInAVersion1Index_IsQuarantined_TheOtherFilesOfEveryPointStayIdentical()
        {
            var o = Unpack();
            var st = new SetStore(o.UserDir, o.SetId);
            var points = st.Points();
            // damage the one object of "new in point 2.txt" (only in the last point)
            var f = st.FilesAt(null).List("files").Single(x => NameCipher.DecryptPath(o.Key, x["enc"]).Replace('\\', '/').EndsWith("/new in point 2.txt", StringComparison.Ordinal));
            var path = st.ObjectPath(f.List("objects").Single()["loc"]);
            var b = File.ReadAllBytes(path); b[b.Length / 2] ^= 0x10; File.WriteAllBytes(path, b);

            var v = st.VerifyAll();                                                           // the old index has no "lost" table: B-4 needs it
            Assert.Equal(1, v.Int("bad"));
            Assert.False(File.Exists(path));
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(o.SetDir, "Quarantine"), "*", SearchOption.AllDirectories));
            Assert.Contains(f["rel"], st.Resend());
            for (int i = 0; i < 2; i++) AssertSame(o.Points[i], Decrypt(st, o, points[i]), "point " + i);   // untouched points: exact
            var damaged = new List<string>();
            var last = Decrypt(st, o, points[2], damaged);
            Assert.Equal(new[] { "new in point 2.txt" }, damaged);                              // said, not silently left out
            AssertSame(o.Points[2].Where(k => k.Key != "new in point 2.txt").ToDictionary(k => k.Key, k => k.Value), last, "point 2 without the damaged file");
            Assert.Equal(0, st.VerifyAll().Int("bad"));
        }

        [Fact]
        public void Upgrade_AVersion1StoreWithItsIndexLost_IsRebuiltFromTheOldObjects_AndNewRunsGoOnBesideTheOldPoints()
        {
            var o = Unpack();
            foreach (var f in Directory.GetFiles(o.SetDir, "index.db*")) File.Delete(f);
            var st = new SetStore(o.UserDir, o.SetId);                                        // a fresh index from the version-1 .chk files
            var points = st.Points();
            Assert.Equal(o.Points.Count, points.Count);
            for (int i = 0; i < points.Count; i++) AssertSame(o.Points[i], Decrypt(st, o, points[i]), "rebuilt point " + i);

            // a new run of this version: a new file, written with the customer's key
            var data = Encoding.UTF8.GetBytes("written after the upgrade");
            var ms = new MemoryStream(); var w = new BackupObject.Writer(ms, o.Key);
            w.AddChunk(BackupObject.ChunkId(o.Key, data), data);
            var header = new Msg().Set("size", data.Length); header.Add("recipe", new Msg().Set("h", BackupObject.ChunkId(o.Key, data))); w.Finish(header);
            var newPath = o.Original + "/after the upgrade.txt";
            var rel = NameCipher.RelPath(o.Key, newPath);
            var j = st.BeginJob(RunId.Parse(points.Last()).AddDays(1));
            st.StageObject(j, new ChkRecord { Rel = rel, Seq = 0, Kind = "F", EncPath = NameCipher.EncryptPath(o.Key, newPath), Orig = data.Length, Mtime = 1 }, new MemoryStream(ms.ToArray()), 1L << 30);
            st.Commit(j, new Msg().Set("new", 1));

            var after = st.Points();
            Assert.Equal(o.Points.Count + 1, after.Count);
            for (int i = 0; i < o.Points.Count; i++) AssertSame(o.Points[i], Decrypt(st, o, after[i]), "old point " + i + " after a new run");
            var want = new Dictionary<string, string>(o.Points.Last()) { ["after the upgrade.txt"] = Sha(data) };
            AssertSame(want, Decrypt(st, o, after.Last()), "the new point");
        }

        // ================================================================== ST-09 recycle bin

        SystemConfig cfg; Users users;
        const string SetA = "1700000000901", Login = "acme";

        void Server()
        {
            cfg = SystemConfig.Init(Path.Combine(root, "system"), "admin", "Admin-Pass-1", "localhost", new[] { Path.Combine(root, "home") + "|UNLIMITED|100" });
            users = new Users(cfg);
        }

        /// <summary>A customer with a set holding 2 points (objects, .chk, index, jobs log), its log and a kept key.</summary>
        void CustomerWithASet(string login = Login, string set = SetA)
        {
            users.Create(login, "Customer-Pass-1", login, 1L << 30, null, null, null);
            var p = users.LoadProfile(login);
            p.Root.Add(new XElement("BACKUP_SET", new XAttribute("ID", set), new XAttribute("NAME", "Files of " + login), new XAttribute("TYPE", "FILE"), new XElement("SEL-SOURCE", "C:\\Data")));
            users.SaveProfile(login, p);
            var dir = users.UserDir(login);
            var st = new SetStore(dir, set); var key = KeySet.Random();
            for (int run = 0; run < 2; run++)
            {
                var j = st.BeginJob(new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc).AddDays(run));
                foreach (var n in new[] { "a", "b" })
                {
                    var ms = new MemoryStream(); var w = new BackupObject.Writer(ms, key); var d = Encoding.UTF8.GetBytes(n + run);
                    w.AddChunk(BackupObject.ChunkId(key, d), d); w.Finish(new Msg());
                    st.StageObject(j, new ChkRecord { Rel = N("C/" + n), Seq = 0, Kind = "F", EncPath = "e" + n, Orig = 2, Mtime = 1 }, new MemoryStream(ms.ToArray()), 1L << 30);
                }
                st.Commit(j, new Msg().Set("new", 2));
            }
            Directory.CreateDirectory(Path.Combine(dir, "logs", set, "Backup"));
            File.WriteAllText(Path.Combine(dir, "logs", set, "Backup", "2026-09-02.log"), "the run's log of " + login);
            Directory.CreateDirectory(Path.Combine(dir, "db", "keys"));
            File.WriteAllBytes(Path.Combine(dir, "db", "keys", set + ".bin"), Bytes.Random(64));
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }

        Dictionary<string, string> SetData(string login = Login, string set = SetA)
        {
            var dir = users.UserDir(login); var all = new Dictionary<string, string>();
            foreach (var part in new[] { "files", "logs" }) foreach (var kv in Tree(Path.Combine(dir, part, set))) all[part + "/" + kv.Key] = kv.Value;
            return all;
        }

        static readonly DateTime Deleted = new DateTime(2026, 9, 10, 9, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void RecycleBin_ASetAndACustomer_GoAtOnce_ComeBackByteIdentical_AndAreErasedOnlyAfter14Days()
        {
            Server(); CustomerWithASet();
            var data = SetData(); var setXml = users.LoadProfile(Login).FindSet(SetA).ToString();
            var points = new SetStore(users.UserDir(Login), SetA).Points();
            Assert.Equal(2, points.Count);

            Assert.Null(Recycle.Request(cfg, users, Login, SetA, "admin", null, Deleted));    // one administrator: at once
            Assert.Null(users.LoadProfile(Login).FindSet(SetA));
            Assert.False(Directory.Exists(Path.Combine(users.UserDir(Login), "files", SetA)));
            var item = users.Recycled().Single();
            Assert.Equal("set", item["kind"]); Assert.Equal(SetA, item["set"]); Assert.Equal("Files of " + Login, item["name"]); Assert.Equal("admin", item["by"]);
            Assert.Equal(RunId.UnixMs(Deleted.AddDays(14)), item.Long("erase"));

            users.RestoreRecycled(item["id"], "admin", null);
            Assert.Equal(setXml, users.LoadProfile(Login).FindSet(SetA).ToString());
            Assert.Equal(data, SetData());                                                    // every file of the set, byte for byte
            var st = new SetStore(users.UserDir(Login), SetA);
            Assert.Equal(points, st.Points()); Assert.Equal(0, st.VerifyAll().Int("bad"));
            Assert.Empty(users.Recycled());
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            // the customer: gone from the list, back with everything
            var home = Path.GetDirectoryName(users.UserDir(Login));
            var whole = Tree(users.UserDir(Login));
            Assert.Null(Recycle.Request(cfg, users, Login, null, "admin", null, Deleted));
            Assert.DoesNotContain(Login, users.Logins());
            Assert.Throws<ApiException>(() => users.UserDir(Login));
            var u = users.Recycled().Single(x => x["kind"] == "user");
            Assert.Equal(0, users.PurgeRecycled(Deleted.AddDays(14).AddMinutes(-1)));         // 13 days 23 h 59 min: kept
            users.RestoreRecycled(u["id"], "admin", null);
            Assert.Contains(Login, users.Logins());
            Assert.Equal(Path.Combine(home, Login), users.UserDir(Login));
            Assert.Equal(whole, Tree(users.UserDir(Login)));
            Assert.True(PasswordHash.Verify("Customer-Pass-1", users.LoadProfile(Login).Get("HASHED_PWD")));

            // erased only after 14 days
            Assert.Null(Recycle.Request(cfg, users, Login, null, "admin", null, Deleted));
            Assert.Equal(0, users.PurgeRecycled(Deleted.AddDays(14).AddMinutes(-1)));
            Assert.Single(users.Recycled());
            Assert.Equal(1, users.PurgeRecycled(Deleted.AddDays(14).AddMinutes(1)));
            Assert.Empty(users.Recycled());
            Assert.Empty(Directory.GetDirectories(Path.Combine(home, "_recycle")));
        }

        [Fact]
        public void RecycleBin_ARestoreOverAnExistingSetOrCustomer_IsRefused_AndChangesNothing()
        {
            Server(); CustomerWithASet();
            Recycle.Request(cfg, users, Login, SetA, "admin", null, Deleted);
            var item = users.Recycled().Single();
            // meanwhile a set with the same id is back in the profile (e.g. the computer re-created it)
            var p = users.LoadProfile(Login); p.Root.Add(new XElement("BACKUP_SET", new XAttribute("ID", SetA), new XAttribute("NAME", "Newer")));
            users.SaveProfile(Login, p);
            var e = Assert.Throws<ApiException>(() => users.RestoreRecycled(item["id"], "admin", null));
            Assert.Equal(409, e.Status); Assert.Equal("EXISTS", e.Code);
            Assert.Equal("Newer", (string)users.LoadProfile(Login).FindSet(SetA).Attribute("NAME"));
            Assert.Single(users.Recycled());                                                   // still in the bin, still restorable
            Assert.Equal(404, Assert.Throws<ApiException>(() => users.RestoreRecycled("set-1-1@" + Login, "admin", null)).Status);
            Assert.Equal(404, Assert.Throws<ApiException>(() => users.RestoreRecycled("nothing", "admin", null)).Status);

            // a customer deleted, and a new customer with the same name created meanwhile
            var p2 = users.LoadProfile(Login); p2.FindSet(SetA).Remove(); users.SaveProfile(Login, p2);
            Recycle.Request(cfg, users, Login, null, "admin", null, Deleted);
            var u = users.Recycled().Single(x => x["kind"] == "user");
            users.Create(Login, "Another-Pass-9", Login, 1L << 30, null, null, null);
            var newer = Tree(users.UserDir(Login));
            Assert.Equal(409, Assert.Throws<ApiException>(() => users.RestoreRecycled(u["id"], "admin", null)).Status);
            Assert.Equal(newer, Tree(users.UserDir(Login)));
            Assert.True(PasswordHash.Verify("Another-Pass-9", users.LoadProfile(Login).Get("HASHED_PWD")));
            Assert.Contains(users.Recycled(), x => x["id"] == u["id"]);
        }

        [Fact]
        public void RecycleBin_SurvivesARestart_AndTwoAdministrators_TheAskerCannotApprove_AnOldRequestDeletesNothing()
        {
            Server(); CustomerWithASet(); CustomerWithASet("beta", "1700000000902");
            var data = SetData();
            Recycle.Request(cfg, users, Login, SetA, "admin", null, Deleted);
            // the server restarts: its settings and users are read again from the disk
            cfg = SystemConfig.Load(Path.Combine(root, "system")); users = new Users(cfg);
            var item = users.Recycled().Single();
            users.RestoreRecycled(item["id"], "admin", null);
            Assert.Equal(data, SetData());

            // a second administrator: a deletion waits for the other one
            lock (cfg) { cfg.Doc.Root.Add(new XElement("STAFF", new XAttribute("LOGIN", "dana"))); cfg.Save(); }
            var req = Recycle.Request(cfg, users, "beta", null, "admin", null, Deleted);
            Assert.NotNull(req);
            Assert.Contains("beta", users.Logins());
            Assert.Equal(403, Assert.Throws<ApiException>(() => Recycle.Approve(cfg, users, req["id"], "ADMIN", null, Deleted.AddHours(1))).Status);
            Assert.Contains("beta", users.Logins());
            Assert.Equal(req["id"], Recycle.Requests(cfg).Single()["id"]);                     // still waiting, once
            // asked again (the same request is answered), approved after 7 days: refused, nothing deleted
            Assert.Equal(req["id"], Recycle.Request(cfg, users, "beta", null, "admin", null, Deleted)["id"]);
            Assert.Equal(410, Assert.Throws<ApiException>(() => Recycle.Approve(cfg, users, req["id"], "dana", null, Deleted.AddDays(7).AddMinutes(1))).Status);
            Assert.Contains("beta", users.Logins());
            Assert.Empty(Recycle.Requests(cfg));
            // asked again and approved in time by the other one
            var again = Recycle.Request(cfg, users, "beta", null, "admin", null, Deleted.AddDays(8));
            Recycle.Approve(cfg, users, again["id"], "dana", null, Deleted.AddDays(8).AddHours(1));
            Assert.DoesNotContain("beta", users.Logins());
            Assert.Equal("admin + dana", users.Recycled().Single(x => x["kind"] == "user")["by"]);
        }

        // ================================================================== ST-10 settings backup

        static Dictionary<string, string> ZipTree(string zip)
        {
            using (var z = ZipFile.OpenRead(zip))
                return z.Entries.Where(e => e.FullName != "BACKUP.xml" && !e.FullName.EndsWith("/")).ToDictionary(e => e.FullName, e => { using (var s = e.Open()) { var m = new MemoryStream(); s.CopyTo(m); return Sha(m.ToArray()); } });
        }

        /// <summary>What the settings backup must hold, from the disk: the system's settings folders and each customer's db.</summary>
        Dictionary<string, string> SettingsOnDisk()
        {
            var want = new Dictionary<string, string>();
            foreach (var part in new[] { "conf", "contract", "policy", "tickets", "stats" })
                foreach (var kv in Tree(Path.Combine(cfg.SystemHome, part))) want["system/" + part + "/" + kv.Key] = kv.Value;
            foreach (var login in users.Logins())
                foreach (var kv in Tree(Path.Combine(users.UserDir(login), "db"))) want["users/" + login + "/db/" + kv.Key] = kv.Value;
            return want;
        }

        [Fact]
        public void SettingsBackup_HoldsExactlyTheSettings_NoBackupData_AndRestoresThemByteIdentical_EvenWhenTheSystemFolderIsLost()
        {
            Server(); CustomerWithASet(); CustomerWithASet("beta", "1700000000902");
            lock (cfg) { cfg.Doc.Root.Element("BRANDING").SetAttributeValue("COMPANY", "Pilot IT"); cfg.Save(); }
            var copy = Path.Combine(root, "second-disk");
            ConfigBackup.Save(cfg, copy, "admin", null);
            var want = SettingsOnDisk();
            var t = new DateTime(2026, 9, 1, 3, 0, 0, DateTimeKind.Utc);
            var name = ConfigBackup.Make(cfg, users, t, "admin", null);
            Assert.Equal("config-20260901-030000.zip", name);
            var zip = Path.Combine(cfg.SystemHome, "config-backups", name);
            Assert.Equal(ShaFile(zip), ShaFile(Path.Combine(copy, name)));                     // the copy is the same file
            var got = ZipTree(zip);
            Assert.Equal(want.OrderBy(k => k.Key, StringComparer.Ordinal), got.OrderBy(k => k.Key, StringComparer.Ordinal));
            Assert.Contains("system/conf/system.xml", got.Keys); Assert.Contains("users/acme/db/Profile.xml", got.Keys); Assert.Contains("users/acme/db/keys/" + SetA + ".bin", got.Keys);
            Assert.DoesNotContain(got.Keys, k => k.Contains("/files/") || k.Contains("/logs/") || k.Contains("index.db"));
            using (var z = ZipFile.OpenRead(zip))
            using (var s = z.GetEntry("BACKUP.xml").Open())
            {
                var info = XElement.Load(s);
                Assert.Equal(RunId.UnixMs(t).ToString(CultureInfo.InvariantCulture), (string)info.Attribute("TIME"));
                Assert.Equal(cfg.ServerId, (string)info.Attribute("SERVER")); Assert.Equal("2", (string)info.Attribute("USERS"));
            }

            // the system folder's settings are lost and a profile is damaged: restored as documented, from the copy
            var acmeDb = Path.Combine(users.UserDir(Login), "db");
            foreach (var part in new[] { "conf", "policy" }) Directory.Delete(Path.Combine(cfg.SystemHome, part), true);
            File.WriteAllText(Path.Combine(acmeDb, "Profile.xml"), "<damaged");
            using (var z = ZipFile.OpenRead(Path.Combine(copy, name)))
                foreach (var e in z.Entries.Where(x => !x.FullName.EndsWith("/") && x.FullName != "BACKUP.xml"))
                {
                    var parts = e.FullName.Split('/');
                    var to = parts[0] == "system" ? Path.Combine(new[] { cfg.SystemHome }.Concat(parts.Skip(1)).ToArray())
                                                  : Path.Combine(new[] { Path.Combine(root, "home", parts[1], "db") }.Concat(parts.Skip(3)).ToArray());
                    Directory.CreateDirectory(Path.GetDirectoryName(to));
                    e.ExtractToFile(to, true);
                }
            var cfg2 = SystemConfig.Load(cfg.SystemHome); var users2 = new Users(cfg2);
            Assert.Equal("Pilot IT", (string)cfg2.Doc.Root.Element("BRANDING").Attribute("COMPANY"));
            Assert.Equal(cfg.ServerId, cfg2.ServerId);
            Assert.Equal(new[] { "acme", "beta" }, users2.Logins().OrderBy(x => x).ToArray());
            Assert.True(PasswordHash.Verify("Customer-Pass-1", users2.LoadProfile(Login).Get("HASHED_PWD")));
            Assert.NotNull(users2.LoadProfile(Login).FindSet(SetA));
            cfg = cfg2; users = users2;
            var back = SettingsOnDisk();
            Assert.Equal(want.OrderBy(k => k.Key, StringComparer.Ordinal), back.OrderBy(k => k.Key, StringComparer.Ordinal));
        }

        [Fact]
        public void SettingsBackup_KeepsTheLast30_DailyAfter23Hours_ACopyFolderThatFails_DoesNotStopTheLocalBackup()
        {
            Server(); CustomerWithASet();
            var t = new DateTime(2026, 9, 1, 3, 0, 0, DateTimeKind.Utc);
            Assert.True(ConfigBackup.Due(cfg, DateTime.UtcNow));                                // never made: due
            // a copy folder that cannot be made (its parent is a file): the local backup is still made, nothing thrown
            var blocker = Path.Combine(root, "a-file"); File.WriteAllText(blocker, "x");
            ConfigBackup.Save(cfg, Path.Combine(blocker, "copies"), "admin", null);
            var first = ConfigBackup.Make(cfg, users, t, "admin", null);
            Assert.True(File.Exists(Path.Combine(cfg.SystemHome, "config-backups", first)));
            Assert.Equal(SettingsOnDisk().Count, ZipTree(Path.Combine(cfg.SystemHome, "config-backups", first)).Count);
            Assert.False(Directory.Exists(Path.Combine(blocker, "copies")));
            Assert.Equal(400, Assert.Throws<ApiException>(() => ConfigBackup.Save(cfg, Path.Combine("relative", "copies"), "admin", null)).Status);
            foreach (var bad in new[] { "../conf/system.xml", "config-x.zip/../../conf", "system.xml", "config-20990101-000000.zip" })
                Assert.Equal(404, Assert.Throws<ApiException>(() => ConfigBackup.PathOf(cfg, bad)).Status);

            // daily: not again within 23 hours of the last one (its file's time), again after
            var made = File.GetLastWriteTimeUtc(Path.Combine(cfg.SystemHome, "config-backups", first));
            Assert.False(ConfigBackup.Due(cfg, made.AddHours(22)));
            Assert.True(ConfigBackup.Due(cfg, made.AddHours(23).AddMinutes(1)));

            // 31 more backups (one a day): the last 30 are kept, here and in the copy folder
            var copy = Path.Combine(root, "second-disk"); ConfigBackup.Save(cfg, copy, "admin", null);
            var names = new List<string> { first };
            for (int d = 1; d <= 31; d++) names.Add(ConfigBackup.Make(cfg, users, t.AddDays(d), "admin", null));
            var keptHere = Directory.GetFiles(Path.Combine(cfg.SystemHome, "config-backups"), "config-*.zip").Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            Assert.Equal(names.Skip(2).ToArray(), keptHere);
            Assert.Equal(names.Skip(2).ToArray(), Directory.GetFiles(copy, "config-*.zip").Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal).ToArray());
            Assert.Equal(30, ConfigBackup.List(cfg).List("backups").Count);
        }

        [Fact]
        public void SettingsBackup_ACrashedEarlierBackup_DoesNotCount_AndTheNextOneIsComplete()
        {
            Server(); CustomerWithASet();
            var dir = Path.Combine(cfg.SystemHome, "config-backups"); Directory.CreateDirectory(dir);
            // the server died while writing a backup: its half file is left
            File.WriteAllBytes(Path.Combine(dir, "config-20260831-030000.zip.tmp"), new byte[] { 0x50, 0x4B, 3, 4, 1, 2 });
            Assert.True(ConfigBackup.Due(cfg, DateTime.UtcNow));                                // a half file is not a backup
            Assert.Empty(ConfigBackup.List(cfg).List("backups"));
            var name = ConfigBackup.Make(cfg, users, new DateTime(2026, 9, 1, 3, 0, 0, DateTimeKind.Utc), "admin", null);
            var list = ConfigBackup.List(cfg).List("backups");
            Assert.Equal(new[] { name }, list.Select(m => m["name"]).ToArray());
            Assert.Equal(SettingsOnDisk().OrderBy(k => k.Key, StringComparer.Ordinal), ZipTree(Path.Combine(dir, name)).OrderBy(k => k.Key, StringComparer.Ordinal));
            Assert.False(ConfigBackup.Due(cfg, DateTime.UtcNow));
        }
    }
}
