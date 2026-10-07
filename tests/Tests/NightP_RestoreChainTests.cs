using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;
using Xunit.Abstractions;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// Night round P — try to disprove "every point restores byte-identical" (BK-02 delta chains, RS-01 point selection,
    /// CDC edges, retention interplay, restic engine). Real server (Env) + real agent, every point restored to an empty
    /// folder. Oracle: SHA-256 (and the file time in ms, which RS-01 claims: "Identical_WithItsTime") of every file on disk,
    /// taken from the SOURCE right after each backup — never the product's own success message.
    /// </summary>
    public class NightP_RestoreChainTests : IDisposable
    {
        readonly ITestOutputHelper output;
        public NightP_RestoreChainTests(ITestOutputHelper output) { this.output = output; }
        public void Dispose() { SystemClock.Use(null); }

        // the product's clock moves on by `jump` (TimeMachineTests pattern): distinct run ids without sleeping, days for retention
        TimeSpan jump = TimeSpan.Zero;
        void TimeMachine() { SystemClock.Use(() => DateTime.UtcNow + jump); }

        static string Sha(string f) { using (var s = File.OpenRead(f)) using (var h = SHA256.Create()) return Bytes.Hex(h.ComputeHash(s)); }
        static long Ms(string f) { return RunId.UnixMs(File.GetLastWriteTimeUtc(f)); }

        /// <summary>relative path (case kept, '/' separators) → "sha|mtime-ms" of every file under dir.</summary>
        internal static SortedDictionary<string, string> Manifest(string dir, bool withTime = true)
        {
            var m = new SortedDictionary<string, string>(StringComparer.Ordinal);
            if (!Directory.Exists(dir)) return m;
            foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                m[Path.GetRelativePath(dir, f).Replace('\\', '/')] = Sha(f) + (withTime ? "|" + Ms(f) : "");
            return m;
        }

        internal static List<string> Diff(IDictionary<string, string> want, IDictionary<string, string> got)
        {
            var d = new List<string>();
            foreach (var kv in want)
            {
                string g;
                if (!got.TryGetValue(kv.Key, out g)) d.Add("missing " + kv.Key);
                else if (g != kv.Value) d.Add("differs " + kv.Key + " want " + Short(kv.Value) + " got " + Short(g));
            }
            foreach (var k in got.Keys) if (!want.ContainsKey(k)) d.Add("extra " + k);
            return d;
        }
        static string Short(string v) { var p = v.Split('|'); return p[0].Substring(0, 12) + (p.Length > 1 ? "|" + p[1] : ""); }

        static readonly DateTime T0 = new DateTime(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc);
        static void Write(string path, byte[] data, int version)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, data);
            File.SetLastWriteTimeUtc(path, T0.AddMinutes(version).AddMilliseconds(137 + version));   // a distinct, ms-exact time per version
        }

        static byte[] Rnd(Random r, int n) { var b = new byte[n]; r.NextBytes(b); return b; }

        /// <summary>One edit of the long-chain file; the kind cycles so every kind meets every chain position.</summary>
        static byte[] Mutate(byte[] cur, int i, Random r, out string what)
        {
            int at() { return cur.Length == 0 ? 0 : r.Next(cur.Length); }
            switch (i % 10)
            {
                case 0: { var p = Math.Max(0, Math.Min(at(), cur.Length - 1000)); var b = (byte[])cur.Clone(); if (b.Length >= 1000) Buffer.BlockCopy(Rnd(r, 1000), 0, b, p, 1000); what = "overwrite 1000 @" + p; return b; }
                case 1: { var p = at(); what = "insert 777 @" + p; return cur.Take(p).Concat(Rnd(r, 777)).Concat(cur.Skip(p)).ToArray(); }
                case 2: { what = "append 50000"; return cur.Concat(Rnd(r, 50000)).ToArray(); }
                case 3: { var n = Math.Min(cur.Length, 100000); what = "truncate -" + n; return cur.Take(cur.Length - n).ToArray(); }
                case 4: { var p = at(); var n = Math.Min(3000, cur.Length - p); what = "cut 3000 @" + p; return cur.Take(p).Concat(cur.Skip(p + n)).ToArray(); }
                case 5: { var b = (byte[])cur.Clone(); var p = Math.Max(0, Math.Min(at(), b.Length - 1)); if (b.Length > 0) b[p] ^= 0xFF; what = "same size, one byte @" + p; return b; }
                case 6: { what = "same size, all new bytes"; return Rnd(r, cur.Length); }
                case 7: { what = "shrink to 0"; return new byte[0]; }
                case 8: { what = "grow from 0 to 6MB"; return Rnd(r, 6 * 1024 * 1024 + 333); }
                default: { what = "prepend 10"; return Rnd(r, 10).Concat(cur).ToArray(); }
            }
        }

        static string Restored(string target, string src) { return Path.Combine(target, Env.Rel(src)); }

        /// <summary>Restores every listed point of a native set into its own empty folder and compares with the manifest of the run.</summary>
        List<string> RestoreEveryPoint(Env env, AgentApp app, string password, string setId, string src, Dictionary<string, SortedDictionary<string, string>> manifests, string tag)
        {
            var bad = new List<string>();
            var session = app.Interactive(password, null);
            var points = app.RestoreFor(session, setId).Points();
            foreach (var p in points)
            {
                SortedDictionary<string, string> want;
                if (!manifests.TryGetValue(p, out want)) { bad.Add(tag + " point " + p + ": not a run this test made"); continue; }
                var target = env.Dir(tag + "-r-" + p);
                var rs = app.RestoreFor(session, setId);
                rs.Run(p, target, null, false);
                var d = Diff(want, Manifest(Restored(target, src)));
                if (rs.Failed > 0 || d.Count > 0)
                    bad.Add(tag + " point " + p + " (run " + manifests.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList().IndexOf(p) + "): failed=" + rs.Failed + " " + string.Join("; ", d.Take(6))
                        + (rs.Failed > 0 ? " log: " + string.Join(" / ", rs.Log.Where(l => l.Contains("err")).Take(3)) : ""));
                try { Directory.Delete(target, true); } catch (Exception) { }
            }
            output.WriteLine(tag + ": restored " + points.Count + " points, " + bad.Count + " bad");
            return bad;
        }

        /// <summary>Longest chain (objects per file) in each point, from the server's own index — proof that deltas were really made.</summary>
        static int LongestChain(Env env, string user, string setId)
        {
            var st = new SetStore(Path.Combine(env.HomeA, user), setId);
            return st.Points().Max(p => st.FilesAt(p).List("files").Select(f => f.List("objects").Count).DefaultIfEmpty(0).Max());
        }

        // ------------------------------------------------------------------------------------------------ 1. long delta chains

        /// <summary>
        /// BK-02, end-to-end: one large file edited in 45 runs (overwrite, insert, append, truncate, cut, one byte, all new
        /// bytes at the same size, shrink to 0, grow back, prepend) next to an all-zero file that grows and a small file;
        /// MAX_DELTA_RATIO high so the chain stays a chain (and MAX_DELTA_NO 7 for the variant that rolls over to new full
        /// copies). EVERY point is restored and compared with the source manifest taken right after its run.
        /// Can fail: a wrong chunk (a stale `known` list, a recipe from the wrong object, a delta on the wrong base) gives a
        /// different SHA at that point; a wrong time gives a different ms; a point listing the wrong chain gives another version.
        /// </summary>
        [Theory]
        [InlineData("I", 100, true)]
        [InlineData("I", 7, true)]
        [InlineData("D", 100, true)]
        [InlineData("D", 7, true)]
        [InlineData("I", 100, false)]
        [InlineData("D", 100, false)]
        public void LongChain_EveryPointRestoresIdentical(string deltaType, int maxDeltaNo, bool shrinkToZero)
        {
            TimeMachine();
            using (var env = new Env())
            {
                const string user = "chainp", pw = "Customer-Pass-1";
                env.CreateUser(user, pw, 20);
                var app = env.Agent(user, pw);
                var src = env.Dir("src");
                var r = new Random(4242 + maxDeltaNo + deltaType[0]);
                var big = Rnd(r, 8 * 1024 * 1024 + 17);
                var zeros = new byte[3 * 1024 * 1024];
                Write(Path.Combine(src, "data", "big.mdb"), big, 0);
                Write(Path.Combine(src, "data", "zeros.img"), zeros, 0);
                Write(Path.Combine(src, "note.txt"), new byte[] { 1, 2, 3 }, 0);
                var set = app.CreateSet(app.Interactive(pw, null), pw, new BackupSetInfo
                {
                    Name = "ChainP", Sources = { src }, MinDeltaFileSize = 1024 * 1024, MaxDeltaNo = maxDeltaNo, MaxDeltaRatio = 100000, DeltaType = deltaType
                });
                var manifests = new Dictionary<string, SortedDictionary<string, string>>();
                var edits = new List<string>();
                long fullBytes = 0, deltaRunBytes = 0;
                for (int i = 0; i <= 45; i++)
                {
                    if (i > 0)
                    {
                        string what;
                        // without shrink-to-0 (a small file is always a new full copy) the chain runs unbroken for all 45 edits
                        big = Mutate(big, shrinkToZero || i % 10 != 7 ? i : 1, r, out what);
                        Write(Path.Combine(src, "data", "big.mdb"), big, i);
                        edits.Add(i + ":" + what + "→" + big.Length);
                        if (i % 4 == 0) { zeros = zeros.Concat(new byte[64 * 1024 + i]).ToArray(); Write(Path.Combine(src, "data", "zeros.img"), zeros, i); }
                        if (i % 3 == 0) Write(Path.Combine(src, "note.txt"), BitConverter.GetBytes(i), i);
                    }
                    jump += TimeSpan.FromSeconds(3);
                    var run = app.Backup(set.Id);
                    Assert.True(run.Result == "BS_STOP_SUCCESS", "run " + i + ": " + run.Result + "\n" + string.Join("\n", run.LogLines.Where(l => l.Contains("err") || l.Contains("warn"))));
                    if (i == 0) fullBytes = run.BytesSent; else if (i % 10 == 1) deltaRunBytes = Math.Max(deltaRunBytes, run.BytesSent);
                    manifests[run.Job] = Manifest(src);
                }
                var chain = LongestChain(env, user, set.Id);
                output.WriteLine("edits: " + string.Join(", ", edits));
                output.WriteLine("longest chain " + chain + " objects; first run sent " + fullBytes + ", largest insert-run sent " + deltaRunBytes);
                // the chain really is a chain (else this test proves nothing about deltas)
                Assert.True(chain >= (shrinkToZero ? Math.Min(maxDeltaNo, 8) : 40), "longest chain only " + chain + " objects");
                jump += TimeSpan.FromMinutes(1);
                var bad = RestoreEveryPoint(env, app, pw, set.Id, src, manifests, "chain-" + deltaType + maxDeltaNo + (shrinkToZero ? "s" : ""));
                Assert.True(bad.Count == 0, bad.Count + " of " + manifests.Count + " points differ from the source at their run:\n" + string.Join("\n", bad.Take(15)));
                Assert.Equal(manifests.Count, app.RestoreFor(app.Interactive(pw, null), set.Id).Points().Count);
            }
        }

        // ------------------------------------------------------------------------------------------------ 2. point selection

        /// <summary>
        /// RS-01: names reused across runs. Deleted then re-created with the OLD content and OLD time; renamed away and a new
        /// file under the old name; a folder replaced by a file of the same name and back; a file replaced by a folder; a file
        /// deleted and the folder holding it deleted; a large (delta) file deleted mid-chain and re-created. Every point
        /// restored and compared. Can fail: a point that lists a version created later, or misses one it held.
        /// </summary>
        [Fact]
        public void NamesReusedAcrossRuns_EveryPointGivesItsOwnVersion()
        {
            TimeMachine();
            using (var env = new Env())
            {
                const string user = "namesp", pw = "Customer-Pass-1";
                env.CreateUser(user, pw, 5);
                var app = env.Agent(user, pw);
                var src = env.Dir("src");
                var r = new Random(77);
                Func<string, string> P = x => Path.Combine(src, x.Replace('/', Path.DirectorySeparatorChar));
                var docV1 = Rnd(r, 5000); var bigV1 = Rnd(r, 3 * 1024 * 1024);
                Write(P("doc.txt"), docV1, 0);
                Write(P("a/x.txt"), Rnd(r, 100), 0);
                Write(P("swap/inner.txt"), Rnd(r, 200), 0);
                Write(P("swap2"), Rnd(r, 300), 0);
                Write(P("deep/er/f.bin"), Rnd(r, 400), 0);
                Write(P("big.vhd"), bigV1, 0);
                var set = app.CreateSet(app.Interactive(pw, null), pw, new BackupSetInfo { Name = "NamesP", Sources = { src }, MinDeltaFileSize = 1024 * 1024, MaxDeltaRatio = 100000 });
                var manifests = new Dictionary<string, SortedDictionary<string, string>>();
                Action<int> backup = i =>
                {
                    jump += TimeSpan.FromSeconds(3);
                    var run = app.Backup(set.Id);
                    Assert.True(run.Result == "BS_STOP_SUCCESS", "run " + i + ": " + run.Result + "\n" + string.Join("\n", run.LogLines));
                    manifests[run.Job] = Manifest(src);
                };
                backup(0);
                // 1: doc deleted; x renamed to x2; swap (folder) → file "swap"; swap2 (file) → folder swap2/; deep/ removed; big edited (delta)
                File.Delete(P("doc.txt"));
                File.Move(P("a/x.txt"), P("a/x2.txt"));
                Directory.Delete(P("swap"), true); Write(P("swap"), Rnd(r, 222), 1);
                File.Delete(P("swap2")); Write(P("swap2/inner.txt"), Rnd(r, 333), 1);
                Directory.Delete(P("deep"), true);
                var big2 = bigV1.Take(1000000).Concat(Rnd(r, 999)).Concat(bigV1.Skip(1000000)).ToArray(); Write(P("big.vhd"), big2, 1);
                backup(1);
                // 2: doc re-created with the OLD content and the OLD time; a new x.txt under the old name; swap back to a folder; big deleted
                File.WriteAllBytes(P("doc.txt"), docV1); File.SetLastWriteTimeUtc(P("doc.txt"), T0.AddMilliseconds(137));
                Write(P("a/x.txt"), Rnd(r, 101), 2);
                File.Delete(P("swap")); Write(P("swap/inner.txt"), Rnd(r, 201), 2);
                File.Delete(P("big.vhd"));
                backup(2);
                // 3: big re-created with its version-1 bytes and time (its old chain is in the retention area); swap2 back to a file
                File.WriteAllBytes(P("big.vhd"), bigV1); File.SetLastWriteTimeUtc(P("big.vhd"), T0.AddMilliseconds(137));
                Directory.Delete(P("swap2"), true); Write(P("swap2"), Rnd(r, 300), 3);
                Write(P("deep/er/f.bin"), Rnd(r, 401), 3);
                backup(3);
                // 4: big edited again (a delta on the re-created full copy), x2 renamed back to x.txt over the new x.txt
                var big4 = bigV1.Take(2000000).Concat(Rnd(r, 50)).Concat(bigV1.Skip(2000000)).ToArray(); Write(P("big.vhd"), big4, 4);
                File.Delete(P("a/x.txt")); File.Move(P("a/x2.txt"), P("a/x.txt"));
                backup(4);
                // 5: nothing changed
                backup(5);
                jump += TimeSpan.FromMinutes(1);
                var bad = RestoreEveryPoint(env, app, pw, set.Id, src, manifests, "names");
                Assert.True(bad.Count == 0, string.Join("\n", bad));
            }
        }

        /// <summary>
        /// RS-01, names: a case-only rename (Windows: "Report.txt" → "report.txt", the same file to Windows). Server-side
        /// names are case-insensitive (NameCipher.Segment lowercases) and the change test is size + time, so the rename is
        /// "unchanged": no upload, and every later point keeps the OLD spelling. On a case-sensitive disk the restored file
        /// has another name than the source had at that run. Expected to FAIL on this snapshot (finding P-1).
        /// </summary>
        [Fact]
        public void CaseOnlyRename_LaterPointsRestoreTheNewSpelling()
        {
            TimeMachine();
            using (var env = new Env())
            {
                const string user = "casep", pw = "Customer-Pass-1";
                env.CreateUser(user, pw, 1);
                var app = env.Agent(user, pw);
                var src = env.Dir("src");
                Write(Path.Combine(src, "Reports", "Report.txt"), new byte[] { 1, 2, 3, 4 }, 0);
                Write(Path.Combine(src, "Other.txt"), new byte[] { 5 }, 0);
                var set = app.CreateSet(app.Interactive(pw, null), pw, new BackupSetInfo { Name = "CaseP", Sources = { src } });
                var manifests = new Dictionary<string, SortedDictionary<string, string>>();
                jump += TimeSpan.FromSeconds(3); var r0 = app.Backup(set.Id); manifests[r0.Job] = Manifest(src);
                File.Move(Path.Combine(src, "Reports", "Report.txt"), Path.Combine(src, "Reports", "report.txt"));   // content and time unchanged
                Directory.Move(Path.Combine(src, "Reports"), Path.Combine(src, "reports-tmp")); Directory.Move(Path.Combine(src, "reports-tmp"), Path.Combine(src, "REPORTS"));
                jump += TimeSpan.FromSeconds(3); var r1 = app.Backup(set.Id); manifests[r1.Job] = Manifest(src);
                output.WriteLine("run 2: " + r1.Result + " new=" + r1.New + " upd=" + r1.Updated + " del=" + r1.Deleted);
                jump += TimeSpan.FromMinutes(1);
                var bad = RestoreEveryPoint(env, app, pw, set.Id, src, manifests, "case");
                Assert.True(bad.Count == 0, string.Join("\n", bad));
            }
        }

        static void Change(Env env, string login, string setId, Action<BackupSetInfo> edit)
        {
            var admin = env.Admin();
            var s = BackupSetInfo.FromXml(System.Xml.Linq.XElement.Parse(admin.Call("GET", "/api/admin/users/" + login + "/sets/" + setId)["set"]));
            edit(s);
            admin.Call("POST", "/api/admin/users/" + login + "/sets/" + setId, new Msg().Set("set", s.ToXml().ToString()));
        }

        /// <summary>
        /// BK-02, differential: the delta is made against the chunk list of the last FULL copy kept on this computer
        /// (LocalState "*.full.txt"), which is written only when a full copy is sent while the set is differential. The set
        /// is switched differential → incremental → differential by the administrator (a normal set edit); in the
        /// incremental period a new full copy replaces the chain on the server (MAX_DELTA_NO), but the local "full" list is
        /// still the first one's. Back in differential, a region restored to its first-version bytes is "known" and not sent —
        /// yet no object of the server's current chain holds it. The run says success; the point cannot be restored.
        /// Expected to FAIL on this snapshot (finding P-2). Can pass only if the newest point restores identical.
        /// </summary>
        [Fact]
        public void DifferentialAfterAnIncrementalFullCopy_EveryPointRestoresIdentical()
        {
            TimeMachine();
            using (var env = new Env())
            {
                const string user = "basep", pw = "Customer-Pass-1";
                env.CreateUser(user, pw, 5);
                var app = env.Agent(user, pw);
                var src = env.Dir("src");
                var path = Path.Combine(src, "ledger.mdb");
                var r = new Random(808);
                var v0 = Rnd(r, 8 * 1024 * 1024);
                Write(path, v0, 0);
                var set = app.CreateSet(app.Interactive(pw, null), pw, new BackupSetInfo { Name = "BaseP", Sources = { src }, MinDeltaFileSize = 1024 * 1024, MaxDeltaNo = 2, MaxDeltaRatio = 100000, DeltaType = "D" });
                var manifests = new Dictionary<string, SortedDictionary<string, string>>();
                var results = new List<string>();
                Action<int> backup = i =>
                {
                    jump += TimeSpan.FromSeconds(3);
                    var run = app.Backup(set.Id);
                    results.Add(i + ":" + run.Result + " upd=" + run.Updated + " sent=" + run.BytesSent);
                    manifests[run.Job] = Manifest(src);
                };
                backup(0);                                                                 // differential: full copy, local "full" list = v0
                Change(env, user, set.Id, s => s.DeltaType = "I");
                var v1 = (byte[])v0.Clone(); Buffer.BlockCopy(Rnd(r, 4000), 0, v1, 2 * 1024 * 1024 + 500, 4000);
                Write(path, v1, 1); backup(1);                                             // incremental delta .001
                var v2 = (byte[])v1.Clone(); Buffer.BlockCopy(Rnd(r, 4000), 0, v2, 6 * 1024 * 1024 + 500, 4000);
                Write(path, v2, 2); backup(2);                                             // MAX_DELTA_NO 2: a new full copy of v2; the v0 chain leaves Current
                Change(env, user, set.Id, s => { s.DeltaType = "D"; s.MaxDeltaNo = 100; });
                var v3 = (byte[])v2.Clone(); Buffer.BlockCopy(v0, 2 * 1024 * 1024 + 500, v3, 2 * 1024 * 1024 + 500, 4000);   // the first region back to its v0 bytes
                Buffer.BlockCopy(Rnd(r, 100), 0, v3, 7 * 1024 * 1024, 100);
                Write(path, v3, 3); backup(3);
                output.WriteLine(string.Join("\n", results));
                var current = Path.Combine(env.HomeA, user, "files", set.Id, SetStore.Current);
                output.WriteLine("objects in Current: " + string.Join(", ", Directory.GetFiles(current, "*.0*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".chk")).Select(f => Path.GetExtension(f))));
                jump += TimeSpan.FromMinutes(1);
                var bad = RestoreEveryPoint(env, app, pw, set.Id, src, manifests, "base");
                Assert.True(bad.Count == 0, "runs: " + string.Join(" | ", results) + "\n" + string.Join("\n", bad));
            }
        }

        // ------------------------------------------------------------------------------------------------ 3. CDC edges

        /// <summary>
        /// BK-02 / Chunker: sizes at the chunker's limits (min 256 KiB, average 1 MiB, max 8 MiB, ±1), empty and 1-byte files,
        /// all-zero files (every cut at the maximum), one 300 KB block repeated 40 times (the same chunk many times in one
        /// recipe), the same 2 MB under 12 names; then bytes inserted in the middle of the repeated and the zero files (every
        /// later cut moves), a byte appended at exactly the 8 MiB boundary, and a file that becomes the content of another.
        /// Can fail: a recipe that repeats a chunk the object stored once, a zero-length last chunk, a boundary off by one.
        /// </summary>
        [Fact]
        public void ChunkerEdges_EveryPointRestoresIdentical()
        {
            TimeMachine();
            using (var env = new Env())
            {
                const string user = "cdcp", pw = "Customer-Pass-1";
                env.CreateUser(user, pw, 20);
                var app = env.Agent(user, pw);
                var src = env.Dir("src");
                var r = new Random(99);
                const int K = 1024, M = 1024 * 1024;
                var files = new Dictionary<string, byte[]>();
                foreach (var n in new[] { 0, 1, 256 * K - 1, 256 * K, 256 * K + 1, M - 1, M, M + 1, 8 * M - 1, 8 * M, 8 * M + 1 })
                    files["size/" + n + ".bin"] = Rnd(r, n);
                files["zero/z8m.bin"] = new byte[8 * M];
                files["zero/z20m.bin"] = new byte[20 * M + 5];
                var block = Rnd(r, 300 * K);
                files["rep/repeat.bin"] = Enumerable.Repeat(block, 40).SelectMany(b => b).ToArray();
                var same = Rnd(r, 2 * M);
                for (int i = 0; i < 12; i++) files["same/copy" + i + ".dat"] = same;
                foreach (var kv in files) Write(Path.Combine(src, kv.Key), kv.Value, 0);
                var set = app.CreateSet(app.Interactive(pw, null), pw, new BackupSetInfo { Name = "CdcP", Sources = { src }, MinDeltaFileSize = 1024 * 1024, MaxDeltaRatio = 100000 });
                var manifests = new Dictionary<string, SortedDictionary<string, string>>();
                Action<int> backup = i =>
                {
                    jump += TimeSpan.FromSeconds(3);
                    var run = app.Backup(set.Id);
                    Assert.True(run.Result == "BS_STOP_SUCCESS", "run " + i + ": " + run.Result + "\n" + string.Join("\n", run.LogLines.Where(l => l.Contains("err") || l.Contains("warn"))));
                    manifests[run.Job] = Manifest(src);
                    output.WriteLine("run " + i + ": new " + run.New + " upd " + run.Updated + " sent " + run.BytesSent);
                };
                backup(0);
                Func<byte[], int, byte[], byte[]> ins = (b, at, x) => b.Take(at).Concat(x).Concat(b.Skip(at)).ToArray();
                Action<string, byte[], int> put = (k, v, i) => { files[k] = v; Write(Path.Combine(src, k), v, i); };
                put("rep/repeat.bin", ins(files["rep/repeat.bin"], 6 * M + 7, Rnd(r, 13)), 1);
                put("zero/z20m.bin", ins(files["zero/z20m.bin"], 10 * M, new byte[] { 1 }), 1);
                put("size/" + (8 * M - 1) + ".bin", files["size/" + (8 * M - 1) + ".bin"].Concat(new byte[] { 0x42 }).ToArray(), 1);   // now exactly 8 MiB
                put("size/" + (8 * M) + ".bin", files["size/" + (8 * M) + ".bin"].Take(8 * M - 1).ToArray(), 1);                           // now 8 MiB - 1
                put("same/copy3.dat", ins(same, M, Rnd(r, 1)), 1);
                backup(1);
                put("rep/repeat.bin", ins(files["rep/repeat.bin"], 100, Rnd(r, 300 * K)), 2);                                           // one more block-sized run at the front
                put("zero/z8m.bin", files["zero/z20m.bin"], 2);                                                                          // becomes another file's content
                put("size/" + M + ".bin", new byte[0], 2);
                put("same/copy5.dat", files["rep/repeat.bin"], 2);
                backup(2);
                put("size/" + M + ".bin", Rnd(r, M), 3);
                put("rep/repeat.bin", files["rep/repeat.bin"].Skip(300 * K).ToArray(), 3);                                               // the front block cut away again
                backup(3);
                jump += TimeSpan.FromMinutes(1);
                var bad = RestoreEveryPoint(env, app, pw, set.Id, src, manifests, "cdc");
                Assert.True(bad.Count == 0, string.Join("\n", bad));
            }
        }

        // ------------------------------------------------------------------------------------------------ 5. retention interplay

        /// <summary>
        /// RT / BK-02: 18 daily runs (the clock moves a day per run) with delta chains rolling over (MAX_DELTA_NO 4), files
        /// deleted and re-created, then retention "last 2 + 3 weekly" — the kept points are NOT contiguous (the intermediate
        /// runs that made parts of their chains expire). Every remaining point must restore identical, after the first
        /// maintenance and after a second one a day later. Can fail: a run folder deleted while a kept older point still needs
        /// a chain object in it, or an expired point still listed.
        /// </summary>
        [Theory]
        [InlineData("I")]
        [InlineData("D")]
        public void RetentionWithGaps_EveryKeptPointRestoresIdentical(string deltaType)
        {
            TimeMachine();
            using (var env = new Env())
            {
                const string user = "retp", pw = "Customer-Pass-1";
                env.CreateUser(user, pw, 20);
                var app = env.Agent(user, pw);
                var src = env.Dir("src");
                var r = new Random(5150 + deltaType[0]);
                var big = Rnd(r, 6 * 1024 * 1024);
                var big2 = Rnd(r, 2 * 1024 * 1024);
                Write(Path.Combine(src, "big.pst"), big, 0);
                Write(Path.Combine(src, "b", "big2.bak"), big2, 0);
                Write(Path.Combine(src, "small.txt"), new byte[] { 0 }, 0);
                var policy = new RetentionPolicy { Unit = "JOBS", Period = 2, Weekly = 3 };
                var set = app.CreateSet(app.Interactive(pw, null), pw, new BackupSetInfo
                {
                    Name = "RetP", Sources = { src }, MinDeltaFileSize = 1024 * 1024, MaxDeltaNo = 4, MaxDeltaRatio = 100000, DeltaType = deltaType, Retention = policy
                });
                var manifests = new Dictionary<string, SortedDictionary<string, string>>();
                for (int i = 0; i <= 17; i++)
                {
                    if (i > 0)
                    {
                        string what;
                        big = Mutate(big, i % 10 == 7 ? 1 : i, r, out what);   // no shrink-to-0 here: keep chains running
                        Write(Path.Combine(src, "big.pst"), big, i);
                        if (i % 2 == 0) { var p = r.Next(big2.Length - 100); big2[p] ^= 0x11; Write(Path.Combine(src, "b", "big2.bak"), big2, i); }
                        if (i == 5) Directory.Delete(Path.Combine(src, "b"), true);
                        if (i == 9) Write(Path.Combine(src, "b", "big2.bak"), big2, i);
                        Write(Path.Combine(src, "small.txt"), new[] { (byte)i }, i);
                    }
                    jump += TimeSpan.FromDays(1);
                    var run = app.Backup(set.Id);
                    Assert.True(run.Result == "BS_STOP_SUCCESS", "run " + i + ": " + run.Result + "\n" + string.Join("\n", run.LogLines.Where(l => l.Contains("err") || l.Contains("warn"))));
                    manifests[run.Job] = Manifest(src);
                }
                var all = manifests.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList();
                var expectKept = policy.Keep(all, SystemClock.UtcNow);
                output.WriteLine("runs " + string.Join(",", all.Select(j => j.Substring(5, 5))) + "; policy keeps " + string.Join(",", all.Where(expectKept.Contains).Select(j => j.Substring(5, 5))));
                var m = env.Api.Maintenance(SystemClock.UtcNow);
                output.WriteLine("maintenance: " + m.ToString().Substring(0, Math.Min(600, m.ToString().Length)));
                // retention really deleted stored versions (else the kept points were never at risk)
                Assert.True(m.List("sets").Single(x => x["set"] == set.Id).Long("deletedObjects") > 0, "retention deleted nothing");
                var st = new SetStore(Path.Combine(env.HomeA, user), set.Id);
                var listed = st.Points();
                Assert.Equal(all.Where(expectKept.Contains).ToList(), listed);
                // gaps: at least one kept point has an expired run right after it (else this proves nothing new)
                Assert.Contains(listed, p => all.IndexOf(p) + 1 < all.Count && !listed.Contains(all[all.IndexOf(p) + 1]));
                jump += TimeSpan.FromMinutes(5);
                var bad = RestoreEveryPoint(env, app, pw, set.Id, src, manifests, "ret1-" + deltaType);
                jump += TimeSpan.FromDays(1);
                env.Api.Maintenance(SystemClock.UtcNow);
                bad.AddRange(RestoreEveryPoint(env, app, pw, set.Id, src, manifests, "ret2-" + deltaType));
                Assert.True(bad.Count == 0, string.Join("\n", bad));
            }
        }

        // ------------------------------------------------------------------------------------------------ restic engine

        static string Restic { get { return Environment.GetEnvironmentVariable("OB_RESTIC"); } }

        /// <summary>
        /// BK-03 / RS-03, restic engine (local repository, the product's ResticRunner, the real restic): the same 30-edit
        /// chain plus names reused (deleted / re-created, folder ↔ file), every snapshot restored through the product into
        /// an empty folder and compared with the source at its run. Can fail: a snapshot id mapped to the wrong run, a
        /// Place() that drops or mixes entries. Skipped without OB_RESTIC (then NOT TESTED).
        /// </summary>
        [Fact]
        public void Restic_LongChain_EverySnapshotRestoresIdentical()
        {
            if (string.IsNullOrEmpty(Restic) || !File.Exists(Restic)) throw NotTested.Because("OB_RESTIC (the restic program) is not set");
            var root = Path.Combine(Path.GetTempPath(), "obnp-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                var src = Path.Combine(root, "src"); var repo = Path.Combine(root, "nas");
                Directory.CreateDirectory(src); Directory.CreateDirectory(repo);
                var app = new AgentApp(Path.Combine(root, "home"));
                app.Home.SaveRegistration("http://127.0.0.1:9/", "nobody", "pc", "dev");
                var set = new BackupSetInfo { Id = "1700000000777", Name = "LocalP", Engine = "RESTIC", DestMode = "LOCAL", LocalCopyPath = repo, Sources = { src } };
                var key = KeySet.Random();
                var r = new Random(31337);
                var big = Rnd(r, 8 * 1024 * 1024 + 17);
                Func<string, string> P = x => Path.Combine(src, x.Replace('/', Path.DirectorySeparatorChar));
                Write(P("data/big.mdb"), big, 0);
                Write(P("swap/inner.txt"), Rnd(r, 50), 0);
                Write(P("doc.txt"), Rnd(r, 60), 0);
                var manifests = new List<KeyValuePair<string, SortedDictionary<string, string>>>();
                for (int i = 0; i <= 30; i++)
                {
                    if (i > 0)
                    {
                        string what;
                        big = Mutate(big, i, r, out what);
                        Write(P("data/big.mdb"), big, i);
                        if (i % 5 == 0) { if (Directory.Exists(P("swap"))) { Directory.Delete(P("swap"), true); Write(P("swap"), Rnd(r, 70 + i), i); } else { File.Delete(P("swap")); Write(P("swap/inner.txt"), Rnd(r, 80 + i), i); } }
                        if (i % 7 == 0) { if (File.Exists(P("doc.txt"))) File.Delete(P("doc.txt")); else Write(P("doc.txt"), Rnd(r, 60), i); }
                    }
                    var run = new ResticRunner(app, set, key).Backup();
                    Assert.True(run.Result == "BS_STOP_SUCCESS", "run " + i + ": " + run.Result + "\n" + string.Join("\n", run.LogLines));
                    var snap = run.LogLines.Select(l => Regex.Match(l, "Snapshot ([0-9a-f]{8,64})")).Last(x => x.Success).Groups[1].Value;
                    manifests.Add(new KeyValuePair<string, SortedDictionary<string, string>>(snap, Manifest(src)));
                }
                var list = new ResticRunner(app, set, key).SnapshotList();   // newest first, as the restore screen shows it
                output.WriteLine("snapshots listed " + list.Count + ", runs " + manifests.Count);
                Assert.Equal(manifests.Count, list.Count);
                var bad = new List<string>();
                for (int i = 0; i < list.Count; i++)
                {
                    var id = list[i]["id"];
                    var mine = manifests.FindIndex(kv => kv.Key.StartsWith(id, StringComparison.Ordinal) || id.StartsWith(kv.Key, StringComparison.Ordinal));
                    if (mine != manifests.Count - 1 - i) bad.Add("list position " + i + " is snapshot " + id + " = run " + mine + " (expected run " + (manifests.Count - 1 - i) + ")");
                    if (mine < 0) continue;
                    var target = Path.Combine(root, "restore-" + i);
                    new ResticRunner(app, set, key).RestoreMany(id, target, null, new List<string>());
                    var d = Diff(manifests[mine].Value, Manifest(AgentRig.Under(target, src)));
                    if (d.Count > 0) bad.Add("snapshot " + id + " (run " + mine + "): " + string.Join("; ", d.Take(6)));
                    RemoveRestored(target);
                }
                Assert.True(bad.Count == 0, string.Join("\n", bad));
            }
            finally { try { Directory.Delete(root, true); } catch (Exception) { } }
        }

        /// <summary>T-4 (QA shards, hosted Windows with restic): removing a restore failed at restore-0 - "Access to the path
        /// ...\restore-0\C\Users is denied" - so the other 30 snapshots were never compared there. The restore and its
        /// comparison had already run; what restic put on the folders above the backed-up one (attributes, permissions) is
        /// printed here as evidence for T-4 (open), then the attributes are cleared so the chain goes on.</summary>
        void RemoveRestored(string target)
        {
            try { Directory.Delete(target, true); return; }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { output.WriteLine("T-4 the restore could not be removed: " + e.Message); }
            foreach (var dir in new[] { target }.Concat(Directory.GetDirectories(target, "*", SearchOption.AllDirectories)).Take(8))
                output.WriteLine("T-4 " + (dir == target ? "." : Path.GetRelativePath(target, dir)) + ": " + File.GetAttributes(dir));
            if (OperatingSystem.IsWindows())
                foreach (var dir in Directory.GetDirectories(target).Take(2))
                {
                    var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("icacls", "\"" + dir + "\" /T /C") { RedirectStandardOutput = true, UseShellExecute = false });
                    output.WriteLine("T-4 icacls: " + string.Join(" | ", p.StandardOutput.ReadToEnd().Split('\n').Take(12).Select(x => x.Trim())));
                    p.WaitForExit(30000);
                }
            foreach (var f in Directory.GetFileSystemEntries(target, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(target, true);
        }
    }
}
