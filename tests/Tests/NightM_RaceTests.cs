using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;
using Xunit.Abstractions;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// Night QA agent M — races. Two operations on the same set are started at the same moment (a Barrier) many times;
    /// after each iteration an oracle OUTSIDE the operation checks the result: the files on disk and their SHA-256 against
    /// the bytes the test sent, the server's index (points, run history), the server's logs. Every failing iteration is
    /// collected with its number and timing, and the test fails with the whole list.
    /// </summary>
    [Collection("NightM-TZ")]
    public class NightM_RaceTests : IDisposable
    {
        readonly ITestOutputHelper output;
        public NightM_RaceTests(ITestOutputHelper output) { this.output = output; }
        readonly List<string> dirs = new List<string>();
        string NewDir(string p) { var d = Path.Combine(Path.GetTempPath(), p + Guid.NewGuid().ToString("N").Substring(0, 8)); dirs.Add(d); return d; }
        public void Dispose() { SystemClock.Use(null); foreach (var d in dirs) try { Directory.Delete(d, true); } catch (Exception) { } }

        // ------------------------------------------------------------------ helpers (also used by NightM_TimeRulesTests)

        internal static byte[] Obj(KeySet key, string content)
        {
            var ms = new MemoryStream();
            var w = new BackupObject.Writer(ms, key);
            var data = System.Text.Encoding.UTF8.GetBytes(content);
            w.AddChunk(BackupObject.ChunkId(key, data), data);
            w.Finish(new Msg().Set("path", content));
            return ms.ToArray();
        }
        internal static string N(string name) { using (var h = SHA256.Create()) return Base32.Encode(h.ComputeHash(System.Text.Encoding.UTF8.GetBytes(name))).Substring(0, 26); }
        internal static ChkRecord Put(SetStore st, string job, string rel, byte[] bytes)
        {
            return st.StageObject(job, new ChkRecord { Rel = rel, Seq = 0, Kind = "F", EncPath = "enc-" + rel, Orig = bytes.Length, Mtime = 1 }, new MemoryStream(bytes), 1L << 30);
        }
        internal static string Sha(byte[] b) { using (var h = SHA256.Create()) return Bytes.Hex(h.ComputeHash(b)); }
        internal static string ShaFile(string f) { using (var s = File.OpenRead(f)) using (var h = SHA256.Create()) return Bytes.Hex(h.ComputeHash(s)); }
        static string OnDisk(SetStore st, string loc) { return Path.Combine(st.Dir, loc.Replace('/', Path.DirectorySeparatorChar)); }

        /// <summary>Runs a and b at the same moment; returns their exceptions and the elapsed time.</summary>
        static void Together(Action a, Action b, out Exception ea, out Exception eb, out long ms)
        {
            var bar = new Barrier(2); Exception xa = null, xb = null;
            var sw = new Stopwatch();
            var ta = new Thread(() => { bar.SignalAndWait(); try { a(); } catch (Exception e) { xa = e; } });
            var tb = new Thread(() => { bar.SignalAndWait(); try { b(); } catch (Exception e) { xb = e; } });
            sw.Start(); ta.Start(); tb.Start(); ta.Join(); tb.Join(); sw.Stop();
            ea = xa; eb = xb; ms = sw.ElapsedMilliseconds;
        }
        static string Ex(Exception e) { return e == null ? "-" : e.GetType().Name + ": " + e.Message; }

        /// <summary>Stages a new full version of the given files; returns rel → SHA-256 of the bytes sent.</summary>
        static Dictionary<string, string> Stage(SetStore st, string job, KeySet key, IEnumerable<string> rels, int gen)
        {
            var m = new Dictionary<string, string>();
            foreach (var rel in rels) { var b = Obj(key, rel + "@" + gen); m[rel] = Sha(b); Put(st, job, rel, b); }
            return m;
        }

        /// <summary>The oracle of the store: every file of the point is on the disk with the bytes sent for that point.</summary>
        static List<string> CheckPoint(SetStore st, string point, Dictionary<string, string> want)
        {
            var bad = new List<string>();
            Msg files;
            try { files = st.FilesAt(point); } catch (Exception e) { bad.Add("point " + point + ": " + Ex(e)); return bad; }
            var got = files.List("files").ToDictionary(f => f["rel"], f => f);
            foreach (var kv in want)
            {
                Msg f;
                if (!got.TryGetValue(kv.Key, out f)) { bad.Add(kv.Key.Substring(0, 6) + " missing from point " + point); continue; }
                if (f["damaged"] == "1") { bad.Add(kv.Key.Substring(0, 6) + " marked damaged in point " + point); continue; }
                var loc = f.List("objects").Last()["loc"]; var p = OnDisk(st, loc);
                if (!File.Exists(p)) { bad.Add(kv.Key.Substring(0, 6) + " object " + loc + " not on disk"); continue; }
                if (ShaFile(p) != kv.Value) bad.Add(kv.Key.Substring(0, 6) + " object " + loc + " has other bytes than the ones sent for point " + point);
            }
            return bad;
        }

        // ------------------------------------------------------------------ commit vs VerifyAll

        /// <summary>
        /// The administrator's "verify" (SetStore.VerifyAll — every object re-read and hashed) on the set while a backup
        /// of that set commits. Nothing is damaged on the disk, so verify must find nothing bad, and the new point must
        /// hold exactly the bytes sent. 50 iterations, 100 files changed per run.
        /// </summary>
        [Fact]
        public void CommitAndVerifyAll_AtTheSameMoment_NoSoundObjectIsQuarantined_TheNewPointIsComplete()
        {
            var st = new SetStore(NewDir("obnmrv-"), "1700000000301"); var key = KeySet.Random();
            var rels = Enumerable.Range(0, 100).Select(i => N("f" + i)).ToList();
            var t = DateTime.UtcNow;
            var j0 = st.BeginJob(t); Stage(st, j0, key, rels, 0); st.Commit(j0, new Msg());
            var fails = new List<string>(); int hits = 0;
            for (int i = 1; i <= 50; i++)
            {
                var j = st.BeginJob(t.AddSeconds(i));
                var want = Stage(st, j, key, rels, i);
                Msg ver = null; Exception ea, eb; long ms;
                Together(() => st.Commit(j, new Msg().Set("upd", rels.Count)), () => ver = st.VerifyAll(), out ea, out eb, out ms);
                var bad = CheckPoint(st, j, want);
                if (ver != null && ver.Int("bad") > 0) { hits++; bad.Insert(0, "verify called " + ver.Int("bad") + " sound object(s) damaged and quarantined"); }
                if (ea != null) bad.Add("commit: " + Ex(ea));
                if (eb != null) bad.Add("verify: " + Ex(eb));
                if (bad.Count > 0)
                {
                    // what is left after the store's own recovery (a new SetStore rolls a journal forward and rebuilds the index)
                    var again = new SetStore(Path.GetDirectoryName(Path.GetDirectoryName(st.Dir)), "1700000000301");
                    var after = CheckPoint(again, j, want);
                    bad.Add("AFTER RECOVERY: point listed " + again.Points().Contains(j) + ", " + after.Count + "/" + want.Count + " files of the point not restorable"
                        + (after.Count > 0 ? " (e.g. " + after[0] + ")" : "") + ", on the resend list " + again.Resend().Count
                        + ", quarantined objects on disk " + (Directory.Exists(Path.Combine(st.Dir, "Quarantine")) ? Directory.GetFiles(Path.Combine(st.Dir, "Quarantine"), "*.000", SearchOption.AllDirectories).Length : 0));
                    fails.Add("iteration " + i + " (" + ms + " ms): " + string.Join("; ", bad.Take(3)) + "; " + bad.Last() + (bad.Count > 4 ? " … (" + bad.Count + " problems)" : ""));
                }
            }
            foreach (var f in fails) output.WriteLine(f);
            Assert.True(fails.Count == 0, fails.Count + "/50 iterations failed:\n" + string.Join("\n", fails.Take(10)));
        }

        // ------------------------------------------------------------------ restore vs commit

        /// <summary>
        /// A restore of the latest point P lists P's files, then fetches each object (as the agent does: GET files, then
        /// GET object?loc=… one by one). A backup of the set commits meanwhile. The restore asked for P: every file must
        /// come back with P's bytes (or the restore must fail loudly — here an exception counts as a failure too, since
        /// the point still exists). Deterministic variant first (the commit lands between the list and the fetch).
        /// </summary>
        [Fact]
        public void RestoreOfTheLatestPoint_WhileABackupCommits_GetsThatPointsBytes()
        {
            var st = new SetStore(NewDir("obnmrc-"), "1700000000302"); var key = KeySet.Random();
            var rels = Enumerable.Range(0, 30).Select(i => N("r" + i)).ToList();
            var t = DateTime.UtcNow;
            var j0 = st.BeginJob(t); var want0 = Stage(st, j0, key, rels, 0); st.Commit(j0, new Msg());
            Func<string, Dictionary<string, string>, List<string>> restore = (point, want) =>
            {
                var bad = new List<string>();
                var files = st.FilesAt(point).List("files");
                foreach (var f in files)
                {
                    var loc = f.List("objects").Last()["loc"]; var job = f.List("objects").Last()["job"];   // as the agent asks: the version and its run
                    try
                    {
                        var p = st.ObjectPath(loc, job);   // the server's GET object
                        var sha = ShaFile(p);
                        if (sha != f.List("objects").Last()["sha"]) bad.Add(f["rel"].Substring(0, 6) + ": the object at " + loc + " no longer matches the checksum the list gave (the agent refuses it)");
                        else if (sha != want[f["rel"]]) bad.Add(f["rel"].Substring(0, 6) + ": bytes of another point");
                    }
                    catch (Exception e) { bad.Add(f["rel"].Substring(0, 6) + ": " + Ex(e)); }
                    Thread.Sleep(0);
                }
                return bad;
            };

            // deterministic: list, commit, fetch
            {
                var files = st.FilesAt(j0).List("files");
                var j = st.BeginJob(t.AddSeconds(1)); Stage(st, j, key, rels, 1); st.Commit(j, new Msg());
                var bad = new List<string>();
                foreach (var f in files)
                {
                    var loc = f.List("objects").Last()["loc"]; var job = f.List("objects").Last()["job"];   // as the agent asks: the version and its run
                    try { if (ShaFile(st.ObjectPath(loc, job)) != want0[f["rel"]]) bad.Add(loc + " other bytes"); } catch (Exception e) { bad.Add(loc + " " + Ex(e)); }
                }
                output.WriteLine("deterministic list→commit→fetch: " + bad.Count + "/" + files.Count + " files fail; first: " + bad.FirstOrDefault());
                Assert.True(bad.Count == 0, "list → commit → fetch: " + bad.Count + "/" + files.Count + " files of point " + j0 + " cannot be restored with that point's bytes; e.g. " + string.Join(" | ", bad.Take(2)));
            }
        }

        [Fact]
        public void RestoreOfTheLatestPoint_ConcurrentWithACommit_50Iterations()
        {
            var st = new SetStore(NewDir("obnmrc2-"), "1700000000303"); var key = KeySet.Random();
            var rels = Enumerable.Range(0, 30).Select(i => N("r" + i)).ToList();
            var t = DateTime.UtcNow;
            var j0 = st.BeginJob(t); var want = Stage(st, j0, key, rels, 0); st.Commit(j0, new Msg());
            var latest = j0;
            var fails = new List<string>();
            for (int i = 1; i <= 50; i++)
            {
                var j = st.BeginJob(t.AddSeconds(i)); var next = Stage(st, j, key, rels, i);
                var bad = new List<string>(); var point = latest; var w = want;
                Exception ea, eb; long ms;
                Together(() =>
                {
                    foreach (var f in st.FilesAt(point).List("files"))
                    {
                        var loc = f.List("objects").Last()["loc"]; var job = f.List("objects").Last()["job"];   // as the agent asks: the version and its run
                        try { if (ShaFile(st.ObjectPath(loc, job)) != w[f["rel"]]) lock (bad) bad.Add(loc + " other bytes"); }
                        catch (Exception e) { lock (bad) bad.Add(loc + " " + e.GetType().Name); }
                    }
                }, () => st.Commit(j, new Msg()), out ea, out eb, out ms);
                if (ea != null) bad.Add("restore: " + Ex(ea));
                if (eb != null) bad.Add("commit: " + Ex(eb));
                if (bad.Count > 0) fails.Add("iteration " + i + " (" + ms + " ms): " + bad.Count + " problem(s), e.g. " + string.Join(" | ", bad.Take(2)));
                latest = j; want = next;
            }
            foreach (var f in fails) output.WriteLine(f);
            Assert.True(fails.Count == 0, fails.Count + "/50 iterations failed:\n" + string.Join("\n", fails.Take(10)));
        }

        // ------------------------------------------------------------------ commit vs retention

        /// <summary>
        /// The nightly retention (keep the last 3 points) runs while a backup commits. After each iteration every kept point
        /// must hold, on the disk, exactly the bytes sent for it (half of the files change in each run, so the retention
        /// area is used and run folders are deleted).
        /// </summary>
        [Fact]
        public void CommitAndRetention_AtTheSameMoment_EveryKeptPointStaysRestorable()
        {
            var st = new SetStore(NewDir("obnmcr-"), "1700000000304"); var key = KeySet.Random();
            var rels = Enumerable.Range(0, 40).Select(i => N("k" + i)).ToList();
            var t = DateTime.UtcNow;
            var byPoint = new Dictionary<string, Dictionary<string, string>>();
            var j0 = st.BeginJob(t); byPoint[j0] = Stage(st, j0, key, rels, 0); st.Commit(j0, new Msg());
            var policy = new RetentionPolicy { Unit = "JOBS", Period = 3 };
            var fails = new List<string>();
            for (int i = 1; i <= 50; i++)
            {
                var prev = byPoint[st.Points().Last()];
                var j = st.BeginJob(t.AddSeconds(i));
                var changed = Stage(st, j, key, rels.Where((r, k) => k % 2 == i % 2), i);
                var now = new Dictionary<string, string>(prev); foreach (var kv in changed) now[kv.Key] = kv.Value;
                byPoint[j] = now;
                Exception ea, eb; long ms;
                Together(() => st.Commit(j, new Msg()), () => st.ApplyRetention(policy, DateTime.UtcNow), out ea, out eb, out ms);
                var bad = new List<string>();
                if (ea != null) bad.Add("commit: " + Ex(ea));
                if (eb != null) bad.Add("retention: " + Ex(eb));
                var points = st.Points();
                if (!points.Contains(j)) bad.Add("the new point " + j + " is not listed");
                if (points.Count > 4) bad.Add(points.Count + " points kept");
                foreach (var p in points) bad.AddRange(CheckPoint(st, p, byPoint[p]));
                if (bad.Count > 0) fails.Add("iteration " + i + " (" + ms + " ms): " + string.Join("; ", bad.Take(4)));
            }
            foreach (var f in fails) output.WriteLine(f);
            Assert.True(fails.Count == 0, fails.Count + "/50 iterations failed:\n" + string.Join("\n", fails.Take(10)));
        }

        // ------------------------------------------------------------------ sign of life vs ExpireStale

        /// <summary>
        /// A sign of life (Touch) arrives at the very moment the sweep (ExpireStale) checks the run at its lease boundary.
        /// Either outcome is valid (alive, or closed); never a half state: a closed run with a folder left (a ghost that
        /// refuses the next backup), a "closed" run still open, or an open run whose folder lost its staging.
        /// Oracle: the folder on disk, jobs.log, and whether the next Begin is accepted.
        /// </summary>
        [Fact]
        public void TouchAndExpireStale_AtTheLeaseBoundary_NeverAHalfClosedRun()
        {
            var userDir = NewDir("obnmte-");
            var st = new SetStore(userDir, "1700000000305");
            var fails = new List<string>(); int alive = 0, closed = 0;
            for (int i = 1; i <= 50; i++)
            {
                var real = DateTime.UtcNow;
                var T = real.AddHours(1).AddMinutes(i * 10);
                var job = st.BeginJob(real);
                SystemClock.Use(() => T); st.Touch(job);
                var dir = Path.Combine(st.Dir, "jobs", job);
                List<string> gone = null; Exception ea, eb; long ms;
                Together(() => { SystemClock.Use(() => T + SetStore.Lease); st.Touch(job); }, () => gone = st.ExpireStale(T + SetStore.Lease + TimeSpan.FromSeconds(1)), out ea, out eb, out ms);
                var bad = new List<string>();
                if (ea != null || eb != null) bad.Add("touch: " + Ex(ea) + " expire: " + Ex(eb));
                bool exists = Directory.Exists(dir), ended = File.ReadAllLines(Path.Combine(st.Dir, "jobs.log")).Contains("E\t" + job);
                if (gone != null && gone.Contains(job))
                {
                    closed++;
                    if (exists) bad.Add("closed, but its folder is there (" + string.Join(",", Directory.GetFileSystemEntries(dir).Select(Path.GetFileName)) + ")");
                    if (!ended) bad.Add("closed, but not in jobs.log");
                    string next = null; try { next = st.BeginJob(T + SetStore.Lease + TimeSpan.FromSeconds(2)); } catch (Exception e) { bad.Add("the next run is refused after the close: " + Ex(e)); }
                    if (next != null) st.Abort(next);
                }
                else
                {
                    alive++;
                    if (!exists || !Directory.Exists(Path.Combine(dir, "new"))) bad.Add("open, but its folder or staging is gone");
                    if (ended) bad.Add("open, but jobs.log says ended");
                    st.Abort(job);
                }
                if (bad.Count > 0) fails.Add("iteration " + i + " (" + ms + " ms): " + string.Join("; ", bad));
            }
            output.WriteLine("alive " + alive + ", closed " + closed);
            Assert.True(fails.Count == 0, fails.Count + "/50 iterations failed (alive " + alive + ", closed " + closed + "):\n" + string.Join("\n", fails.Take(10)));
        }

        // ------------------------------------------------------------------ BeginJob from two agents at once

        /// <summary>
        /// Two agents (two processes; on the server: two request threads, each with its own SetStore object) begin a run of
        /// the same set at the same moment. Exactly one gets a run, the other "BUSY" (409); exactly one open-run folder.
        /// </summary>
        [Fact]
        public void TwoBeginsOfTheSameSet_AtTheSameMoment_ExactlyOneRun()
        {
            var userDir = NewDir("obnmbb-");
            new SetStore(userDir, "1700000000306");
            var fails = new List<string>();
            for (int i = 1; i <= 50; i++)
            {
                string a = null, b = null; Exception ea, eb; long ms;
                var now = DateTime.UtcNow;
                Together(() => a = new SetStore(userDir, "1700000000306").BeginJob(now), () => b = new SetStore(userDir, "1700000000306").BeginJob(now), out ea, out eb, out ms);
                var open = Directory.GetDirectories(Path.Combine(userDir, "files", "1700000000306", "jobs"));
                int got = (a != null ? 1 : 0) + (b != null ? 1 : 0);
                var busy = new[] { ea, eb }.Count(e => e is ApiException && ((ApiException)e).Status == 409);
                if (got != 1 || busy != 1 || open.Length != 1) fails.Add("iteration " + i + " (" + ms + " ms): runs " + got + ", busy " + busy + ", open folders " + open.Length + " (" + Ex(ea) + " / " + Ex(eb) + ")");
                foreach (var d in open) new SetStore(userDir, "1700000000306").Abort(Path.GetFileName(d));
            }
            Assert.True(fails.Count == 0, string.Join("\n", fails));
        }

        // ------------------------------------------------------------------ two admins save the set

        /// <summary>
        /// Two administrators save the same set at the same moment, both on the version they loaded (SetControl.Save with
        /// the version). Exactly one save wins, the other gets 409 CHANGED; the profile holds the winner's change.
        /// </summary>
        [Fact]
        public void TwoAdminsSaveTheSameSet_AtTheSameMoment_ExactlyOneWins_TheOtherIsRefused()
        {
            using (var env = new Env())
            {
                env.CreateUser("nmadm", "Customer-Pass-1");
                var app = env.Agent("nmadm", "Customer-Pass-1");
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "A", Sources = { env.Dir("src") }, Vss = false });
                var users = env.Api.UserStore;
                var fails = new List<string>();
                for (int i = 1; i <= 50; i++)
                {
                    var d = SetControl.Detail(users, "nmadm", set.Id);
                    var version = d["version"];
                    var x = BackupSetInfo.FromXml(System.Xml.Linq.XElement.Parse(d["set"])); x.Name = "by A " + i;
                    var y = BackupSetInfo.FromXml(System.Xml.Linq.XElement.Parse(d["set"])); y.Name = "by B " + i;
                    Exception ea, eb; long ms;
                    Together(() => SetControl.Save(users, "nmadm", set.Id, x, "adminA", "127.0.0.1", version), () => SetControl.Save(users, "nmadm", set.Id, y, "adminB", "127.0.0.1", version), out ea, out eb, out ms);
                    var name = (string)users.LoadProfile("nmadm").FindSet(set.Id).Attribute("NAME");
                    int refused = new[] { ea, eb }.Count(e => e is ApiException && ((ApiException)e).Status == 409);
                    string winner = ea == null && eb != null ? x.Name : eb == null && ea != null ? y.Name : null;
                    if (refused != 1 || winner == null || name != winner) fails.Add("iteration " + i + " (" + ms + " ms): A " + Ex(ea) + ", B " + Ex(eb) + ", stored NAME '" + name + "'");
                }
                Assert.True(fails.Count == 0, string.Join("\n", fails));
            }
        }

        // ------------------------------------------------------------------ the server's missed-backup alert, twice at once

        static int MissedLines(string login, string setId)
        {
            var d = SysLog.Dir("BackupErrors");
            if (!Directory.Exists(d)) return 0;
            return Directory.GetFiles(d).SelectMany(f => Atomic.ReadShared(f).Split('\n')).Count(l => l.Contains(login + "\t" + setId + "\t") && l.Contains("MISSED BACKUP"));
        }

        /// <summary>
        /// The nightly maintenance (timer) and an administrator's "run maintenance" (POST /api/admin/maintenance) run at the
        /// same moment, with a set that has no backup for more than 48 hours. The alert must go once (at most once a day,
        /// LAST_MISSED_ALERT), not twice. Oracle: MISSED BACKUP lines in the server's BackupErrors log (each line = one
        /// mail to the IT company). 30 iterations, each a day later.
        /// </summary>
        [Fact]
        public void TwoMaintenanceRuns_AtTheSameMoment_OneMissedBackupAlert()
        {
            using (var env = new Env())
            {
                env.CreateUser("nmmiss2", "Customer-Pass-1");
                var app = env.Agent("nmmiss2", "Customer-Pass-1");
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "M", Sources = { env.Dir("src") }, Vss = false });
                var created = RunId.FromUnixMs(long.Parse(set.Id, CultureInfo.InvariantCulture));
                var fails = new List<string>();
                for (int i = 0; i < 30; i++)
                {
                    var now = created.AddHours(48 + 25 * i);
                    int before = MissedLines("nmmiss2", set.Id);
                    Exception ea, eb; long ms;
                    Together(() => env.Api.Maintenance(now), () => env.Api.Maintenance(now), out ea, out eb, out ms);
                    int added = MissedLines("nmmiss2", set.Id) - before;
                    if (added != 1 || ea != null || eb != null) fails.Add("iteration " + i + " (" + ms + " ms): " + added + " alerts (" + Ex(ea) + " / " + Ex(eb) + ")");
                }
                foreach (var f in fails) output.WriteLine(f);
                Assert.True(fails.Count == 0, fails.Count + "/30 iterations:\n" + string.Join("\n", fails.Take(10)));
            }
        }

        // ------------------------------------------------------------------ scheduled run vs "back up now" (agent)

        static readonly Dictionary<AgentApp, Client> sessions = new Dictionary<AgentApp, Client>();
        static string RestoredSha(Env env, AgentApp app, string setId, string src, string name)
        {
            var target = env.Dir("restore-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Client session; lock (sessions) if (!sessions.TryGetValue(app, out session)) sessions[app] = session = app.Interactive("Customer-Pass-1", null);
            var rs = app.RestoreFor(session, setId);
            rs.Run(null, target, null, false);
            return ShaFile(Path.Combine(target, Env.Rel(src), name));
        }

        static int PointCount(AgentApp app, string setId) { return app.DeviceClient().Call("GET", "/api/sets/" + setId + "/points").List("points").Count; }

        /// <summary>
        /// The service's scheduled run and the customer's "back up now" start at the same moment in the service process
        /// (AgentApp.Backup twice). One runs, the other is refused BUSY; never two runs, never an error that is not BUSY;
        /// the number of new restore points = the number of runs that said success; the latest point restores to the
        /// file's current bytes (SHA-256). 20 iterations, the file changed each time.
        /// </summary>
        [Fact]
        public void ScheduledRunAndBackUpNow_AtTheSameMoment_OneRun_TheRestoreMatches()
        {
            using (var env = new Env())
            {
                env.CreateUser("nmnow", "Customer-Pass-1", 2);
                var app = env.Agent("nmnow", "Customer-Pass-1");
                var src = env.Dir("src"); var file = Path.Combine(src, "a.bin");
                File.WriteAllBytes(file, new byte[] { 0 });
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "N", Sources = { src }, Vss = false });
                var fails = new List<string>(); var rnd = new Random(7);
                for (int i = 1; i <= 20; i++)
                {
                    var bytes = new byte[200000 + i]; rnd.NextBytes(bytes); File.WriteAllBytes(file, bytes);
                    int before = PointCount(app, set.Id);
                    BackupRun ra = null, rb = null; Exception ea, eb; long ms;
                    Together(() => ra = app.Backup(set.Id), () => rb = app.Backup(set.Id), out ea, out eb, out ms);
                    int ok = new[] { ra, rb }.Count(r => r != null && r.Result.StartsWith("BS_STOP_SUCCESS"));
                    int busy = new[] { ea, eb }.Count(e => e is AgentException && ((AgentException)e).Code == "BUSY");
                    int after = PointCount(app, set.Id);
                    var bad = new List<string>();
                    if (ok + busy != 2 || ok < 1) bad.Add("runs ok " + ok + ", busy " + busy + " (" + (ra == null ? Ex(ea) : ra.Result) + " / " + (rb == null ? Ex(eb) : rb.Result) + ")");
                    if (after - before != ok) bad.Add("points +" + (after - before) + " for " + ok + " successful run(s)");
                    var r = RestoredSha(env, app, set.Id, src, "a.bin");
                    if (r != Sha(bytes)) bad.Add("the latest point does not restore to the file's bytes");
                    if (bad.Count > 0) fails.Add("iteration " + i + " (" + ms + " ms): " + string.Join("; ", bad));
                }
                foreach (var f in fails) output.WriteLine(f);
                Assert.True(fails.Count == 0, fails.Count + "/20 iterations:\n" + string.Join("\n", fails));
            }
        }

        /// <summary>
        /// Two agent PROCESSES (the service and the command line "backup", or two installs on one home) back up the same
        /// set at the same moment: the real program twice (InterruptionTests.StartAgent). Oracle: exit codes, the server's
        /// points (+1 per process that said success), no open run left on the server, and the latest point restores to the
        /// file's bytes; the next run of the service process works (its index of the set is not damaged).
        /// </summary>
        [Fact]
        public void TwoAgentProcesses_BackUpTheSameSetAtTheSameMoment()
        {
            using (var env = new Env())
            {
                env.CreateUser("nmproc", "Customer-Pass-1", 2);
                var app = env.Agent("nmproc", "Customer-Pass-1");
                var src = env.Dir("src"); var file = Path.Combine(src, "a.bin");
                File.WriteAllBytes(file, new byte[] { 0 });
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "P", Sources = { src }, Vss = false });
                var fails = new List<string>(); var rnd = new Random(11); int both = 0;
                for (int i = 1; i <= 8; i++)
                {
                    var bytes = new byte[300000 + i]; rnd.NextBytes(bytes); File.WriteAllBytes(file, bytes);
                    int before = PointCount(app, set.Id);
                    var sw = Stopwatch.StartNew();
                    var pa = InterruptionTests.StartAgent(app.Home.Dir, "backup", "--set", set.Id);
                    var pb = InterruptionTests.StartAgent(app.Home.Dir, "backup", "--set", set.Id);
                    var bad = new List<string>();
                    if (!pa.WaitForExit(120000) || !pb.WaitForExit(120000)) { bad.Add("an agent process did not end in 2 minutes"); try { pa.Kill(true); pb.Kill(true); } catch (Exception) { } }
                    int ok = (pa.HasExited && pa.ExitCode == 0 ? 1 : 0) + (pb.HasExited && pb.ExitCode == 0 ? 1 : 0);
                    if (ok == 2) both++;
                    int after = PointCount(app, set.Id);
                    if (ok < 1) bad.Add("no process succeeded (exit " + (pa.HasExited ? pa.ExitCode : -1) + " / " + (pb.HasExited ? pb.ExitCode : -1) + ")");
                    if (after - before != ok) bad.Add("points +" + (after - before) + " for " + ok + " successful process(es)");
                    var open = SetStore.OpenJobDirs(Path.Combine(env.Api.UserStore.UserDir("nmproc"), "files", set.Id)).ToList();
                    if (open.Count > 0) bad.Add(open.Count + " run(s) left open on the server");
                    if (RestoredSha(env, app, set.Id, src, "a.bin") != Sha(bytes)) bad.Add("the latest point does not restore to the file's bytes");
                    if (bad.Count > 0) fails.Add("iteration " + i + " (" + sw.ElapsedMilliseconds + " ms): " + string.Join("; ", bad));
                }
                // the service's own next run after all this
                var last = new byte[1234]; rnd.NextBytes(last); File.WriteAllBytes(file, last);
                var r = app.Backup(set.Id);
                if (!r.Result.StartsWith("BS_STOP_SUCCESS") || RestoredSha(env, app, set.Id, src, "a.bin") != Sha(last)) fails.Add("the next in-process run: " + r.Result);
                output.WriteLine("iterations where both processes succeeded: " + both);
                foreach (var f in fails) output.WriteLine(f);
                Assert.True(fails.Count == 0, fails.Count + " problem(s):\n" + string.Join("\n", fails));
            }
        }
    }
}
