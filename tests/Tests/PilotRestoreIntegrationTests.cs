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
    /// Pilot, restore - integration layer: the real server in this process, real HTTP, the agent's real backup and restore,
    /// real files (tests/QA/specs.py RS-01, RS-02, RS-05, RS-06; native engine).
    ///   RS-01 a set with 3 points; point 2, one file -> exactly that file, byte-identical to point 2 (SHA-256)
    ///   RS-02 files that exist at the original place, overwrite off / on -> off: kept, the result says how many were skipped;
    ///         on: replaced; a failed file = error
    ///   RS-05 a new computer, the account, the key -> the key comes back, the index is rebuilt, every file identical
    ///   RS-06 the automatic restore test: restored in isolation and compared with the SOURCE; a file damaged in storage is
    ///         failed, a changed source is not a candidate, the temporary folder is gone, the result reaches the server
    /// The product's clock moves on by `jump` (distinct run ids without sleeping).
    /// </summary>
    public class PilotRestoreIntegrationTests : IDisposable
    {
        TimeSpan jump = TimeSpan.Zero;
        public PilotRestoreIntegrationTests() { SystemClock.Use(() => DateTime.UtcNow + jump); }
        public void Dispose() { SystemClock.Use(null); }

        const string Pw = "Customer-Pass-1";
        static readonly DateTime T0 = new DateTime(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc);
        static string Sha(byte[] b) { using (var h = SHA256.Create()) return Bytes.Hex(h.ComputeHash(b)); }
        static string ShaFile(string f) { return Sha(File.ReadAllBytes(f)); }
        static long Ms(string f) { return RunId.UnixMs(File.GetLastWriteTimeUtc(f)); }
        static byte[] Rnd(int n, int seed) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }
        static void Write(string f, byte[] data, int version) { File.WriteAllBytes(f, data); File.SetLastWriteTimeUtc(f, T0.AddMinutes(version).AddMilliseconds(137 + version)); }
        static string Restored(string target, string file) { return Path.Combine(target, Env.Rel(file)); }
        static List<string> All(string dir) { return Directory.Exists(dir) ? Directory.GetFiles(dir, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal).ToList() : new List<string>(); }

        BackupSetInfo NewSet(AgentApp app, string src, string keyType = "PASSWORD")
        {
            return app.CreateSet(app.Interactive(Pw, null), Pw, new BackupSetInfo { Name = "Files", Sources = { src }, Vss = false }, keyType);
        }

        BackupRun Backup(AgentApp app, string setId)
        {
            jump += TimeSpan.FromMinutes(1);
            var r = app.Backup(setId);
            Assert.Equal("BS_STOP_SUCCESS", r.Result);
            return r;
        }

        /// <summary>The object of one source file on the server's storage disk (the latest point).</summary>
        static string ObjectOnDisk(Env env, string login, AgentApp app, string setId, string file)
        {
            var r = app.RestoreFor(app.Interactive(Pw, null), setId);
            var loc = r.Files(null).Single(kv => kv.Key == file).Value.List("objects").OrderBy(o => o.Int("seq")).Last()["loc"];
            var p = Path.Combine(new[] { env.HomeA, login, "files", setId }.Concat(loc.Split('/')).ToArray());
            Assert.True(File.Exists(p), "the object of " + file + " is at " + p);
            return p;
        }

        static void Damage(string objectFile) { var b = File.ReadAllBytes(objectFile); b[b.Length / 2] ^= 0x5A; File.WriteAllBytes(objectFile, b); }

        // ------------------------------------------------------------------ RS-01

        [Fact]
        public void RS01_ThreePoints_Point2OneFile_ExactlyThatFile_ByteIdenticalToPoint2_Point1Whole()
        {
            using (var env = new Env())
            {
                env.CreateUser("prs01", Pw);
                var app = env.Agent("prs01", Pw);
                var src = env.Dir("src"); Directory.CreateDirectory(Path.Combine(src, "sub"));
                var a = Path.Combine(src, "sub", "a.bin"); var b = Path.Combine(src, "b.txt");
                var v = new[] { Rnd(70000, 1), Rnd(70500, 2), Rnd(69000, 3) };
                Write(b, System.Text.Encoding.UTF8.GetBytes("unchanged"), 0);
                var set = NewSet(app, src);
                var jobs = new List<string>();
                for (int i = 0; i < 3; i++) { Write(a, v[i], i + 1); jobs.Add(Backup(app, set.Id).Job); }

                var session = app.Interactive(Pw, null);
                var points = app.RestoreFor(session, set.Id).Points();
                foreach (var j in jobs) Assert.Contains(j, points);

                var one = env.Dir("one");
                var r = app.RestoreFor(session, set.Id); r.Run(jobs[1], one, p => p == a, false);
                Assert.Equal("RESTORE_STOP_SUCCESS", r.Result); Assert.Equal(1, r.Restored);
                Assert.Equal(new[] { Restored(one, a) }, All(one));                                     // exactly the requested file
                Assert.Equal(Sha(v[1]), ShaFile(Restored(one, a)));                                      // ... as it was at point 2
                Assert.Equal(RunId.UnixMs(T0.AddMinutes(2).AddMilliseconds(139)), Ms(Restored(one, a)));

                var whole = env.Dir("whole");
                var r1 = app.RestoreFor(session, set.Id); r1.Run(jobs[0], whole, null, false);
                Assert.Equal(new[] { Restored(whole, b), Restored(whole, a) }.OrderBy(x => x, StringComparer.Ordinal), All(whole));
                Assert.Equal(Sha(v[0]), ShaFile(Restored(whole, a))); Assert.Equal(ShaFile(b), ShaFile(Restored(whole, b)));
                // the restore was recorded on the server with its real result
                Assert.Contains(env.Api.Runs.Since(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddDays(1)), m => m["kind"] == "Restore" && m["result"] == "RESTORE_STOP_SUCCESS");
            }
        }

        [Fact]
        public void RS01_AnObjectDamagedOnTheServerDisk_IsAFailedFile_TheOthersRestoreIdentical_NoHalfFile_ServerToldError()
        {
            using (var env = new Env())
            {
                env.CreateUser("prs01f", Pw);
                var app = env.Agent("prs01f", Pw);
                var src = env.Dir("src");
                var files = Enumerable.Range(0, 3).Select(i => Path.Combine(src, "f" + i + ".bin")).ToList();
                for (int i = 0; i < 3; i++) Write(files[i], Rnd(60000 + i, 10 + i), i);
                var set = NewSet(app, src);
                Backup(app, set.Id);
                Damage(ObjectOnDisk(env, "prs01f", app, set.Id, files[1]));

                var target = env.Dir("t");
                var r = app.RestoreFor(app.Interactive(Pw, null), set.Id); r.Run(null, target, null, false);
                Assert.Equal("RESTORE_STOP_WITH_ERROR", r.Result); Assert.Equal(1, r.Failed); Assert.Equal(2, r.Restored);
                Assert.Equal(new[] { Restored(target, files[0]), Restored(target, files[2]) }, All(target));     // no half file of f1
                Assert.Equal(ShaFile(files[0]), ShaFile(Restored(target, files[0]))); Assert.Equal(ShaFile(files[2]), ShaFile(Restored(target, files[2])));
                Assert.Null(r.ReportError);
                Assert.Contains(env.Api.Runs.Since(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddDays(1)), m => m["kind"] == "Restore" && m["result"] == "RESTORE_STOP_WITH_ERROR");
            }
        }

        // ------------------------------------------------------------------ RS-02

        [Fact]
        public void RS02_OriginalPlace_OverwriteOff_KeepsAndCountsSkipped_On_Replaces_ServerGetsTheResult()
        {
            using (var env = new Env())
            {
                env.CreateUser("prs02", Pw);
                var app = env.Agent("prs02", Pw);
                var src = env.Dir("src");
                var keep = Path.Combine(src, "keep.bin"); var gone = Path.Combine(src, "gone.bin"); var same = Path.Combine(src, "same.txt");
                Write(keep, Rnd(50000, 1), 1); Write(gone, Rnd(40000, 2), 2); Write(same, System.Text.Encoding.UTF8.GetBytes("same"), 3);
                var backed = new[] { keep, gone, same }.ToDictionary(f => f, ShaFile);
                var set = NewSet(app, src);
                Backup(app, set.Id);
                File.WriteAllText(keep, "the customer's newer work"); var newer = ShaFile(keep);
                File.Delete(gone);

                var session = app.Interactive(Pw, null);
                var r = app.RestoreFor(session, set.Id); r.Run(null, null, null, false);
                Assert.Equal("RESTORE_STOP_WITH_WARNING", r.Result); Assert.Equal(2, r.Skipped); Assert.Equal(1, r.Restored); Assert.Equal(0, r.Failed);
                Assert.Equal(newer, ShaFile(keep));                                     // kept
                Assert.Equal(backed[gone], ShaFile(gone));                              // the missing one came back
                Assert.Contains(r.Log, l => l.Contains("2 files were not restored"));
                Assert.Contains(env.Api.Runs.Since(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddDays(1)), m => m["kind"] == "Restore" && m["result"] == "RESTORE_STOP_WITH_WARNING");

                var r2 = app.RestoreFor(session, set.Id); r2.Run(null, null, null, true);
                Assert.Equal("RESTORE_STOP_SUCCESS", r2.Result); Assert.Equal(3, r2.Restored);
                foreach (var kv in backed) Assert.Equal(kv.Value, ShaFile(kv.Key));     // replaced: every file as backed up
                Assert.Equal(new[] { gone, keep, same }.OrderBy(x => x, StringComparer.Ordinal), All(src));   // no temporary file beside them
            }
        }

        [Fact]
        public void RS02_OriginalPlace_Overwrite_WithADamagedObject_TheExistingFileIsUntouched_ResultIsError()
        {
            using (var env = new Env())
            {
                env.CreateUser("prs02f", Pw);
                var app = env.Agent("prs02f", Pw);
                var src = env.Dir("src");
                var a = Path.Combine(src, "a.bin"); var b = Path.Combine(src, "b.bin");
                Write(a, Rnd(80000, 1), 1); Write(b, Rnd(80000, 2), 2);
                var shaB = ShaFile(b);
                var set = NewSet(app, src);
                Backup(app, set.Id);
                Damage(ObjectOnDisk(env, "prs02f", app, set.Id, a));
                File.WriteAllText(a, "work done after the backup"); var now = ShaFile(a);
                File.WriteAllText(b, "to be replaced");

                var r = app.RestoreFor(app.Interactive(Pw, null), set.Id); r.Run(null, null, null, true);
                Assert.Equal("RESTORE_STOP_WITH_ERROR", r.Result); Assert.Equal(1, r.Failed); Assert.Equal(1, r.Restored);
                Assert.Equal(now, ShaFile(a));                                          // a failed restore never destroys the file in place
                Assert.Equal(shaB, ShaFile(b));
                Assert.Equal(new[] { a, b }, All(src));                                 // no half / temporary file left
                Assert.Contains(env.Api.Runs.Since(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddDays(1)), m => m["kind"] == "Restore" && m["result"] == "RESTORE_STOP_WITH_ERROR");
            }
        }

        // ------------------------------------------------------------------ RS-05

        [Fact]
        public void RS05_NewComputer_RandomKey_WithoutRecoveryNoKey_RecoveredKeyRestoresAll_IndexRebuilt_NothingResent()
        {
            using (var env = new Env())
            {
                env.CreateUser("prs05", Pw);
                var old = env.Agent("prs05", Pw);
                var src = env.Dir("src"); Directory.CreateDirectory(Path.Combine(src, "d"));
                var files = Enumerable.Range(0, 6).Select(i => Path.Combine(src, i % 2 == 0 ? "d" : "", "f" + i + ".bin")).ToList();
                for (int i = 0; i < files.Count; i++) Write(files[i], Rnd(30000 + 1000 * i, 50 + i), i);
                var want = files.ToDictionary(f => f, f => ShaFile(f) + "|" + Ms(f));
                var set = NewSet(old, src, "DEFAULT");
                Backup(old, set.Id);

                // the old computer is lost; a new one, same account
                var fresh = env.Agent("prs05", Pw, name: "newpc");
                var session = fresh.Interactive(Pw, null);
                Assert.Equal("NO_KEY", Assert.Throws<AgentException>(() => fresh.RestoreFor(session, set.Id, Pw)).Code);   // a random key: no password gives it
                Assert.Equal("NO_KEY", Assert.Throws<AgentException>(() => fresh.Backup(set.Id)).Code);
                Assert.Null(fresh.Home.LoadKey(set.Id));

                var key = Convert.FromBase64String(env.Admin().Call("GET", "/api/admin/keys/prs05/" + set.Id)["key"]);   // key recovery by the provider
                var target = env.Dir("restored");
                var r = fresh.RestoreFor(session, set.Id, null, key); r.Run(null, target, null, false);
                Assert.Equal("RESTORE_STOP_SUCCESS", r.Result); Assert.Equal(files.Count, r.Restored);
                Assert.Equal(files.Select(f => Restored(target, f)).OrderBy(x => x, StringComparer.Ordinal), All(target));
                foreach (var f in files) Assert.Equal(want[f], ShaFile(Restored(target, f)) + "|" + Ms(Restored(target, f)));

                // the backups go on from the new computer: the key was kept, the index is rebuilt from the server, nothing is resent
                Assert.False(File.Exists(Path.Combine(fresh.Home.SetDir(set.Id), "state.txt")));
                var next = Backup(fresh, set.Id);
                Assert.Equal(0, next.New + next.Updated + next.Deleted);
            }
        }

        // ------------------------------------------------------------------ RS-06

        [Fact]
        public void RS06_RestoreTest_ComparesWithTheSource_DamagedInStorageFails_ChangedSourceNotACandidate_TempGone_ServerRecords()
        {
            using (var env = new Env())
            {
                env.CreateUser("prs06", Pw);
                var app = env.Agent("prs06", Pw);
                var src = env.Dir("src");
                var files = Enumerable.Range(0, 5).Select(i => Path.Combine(src, "f" + i + ".bin")).ToList();
                for (int i = 0; i < files.Count; i++) Write(files[i], Rnd(40000 + i, 70 + i), i);
                var set = NewSet(app, src);
                Backup(app, set.Id);
                Func<Dictionary<string, string>> snap = () => All(src).ToDictionary(f => f, f => ShaFile(f) + "|" + Ms(f));
                var temp = Path.Combine(app.Home.Dir, "temp");
                Func<List<string>> left = () => Directory.Exists(temp) ? Directory.GetFileSystemEntries(temp, "*", SearchOption.AllDirectories).ToList() : new List<string>();
                Func<string, string> attr = n => (string)Profile.Load(Path.Combine(env.HomeA, "prs06", "db", "Profile.xml")).FindSet(set.Id).Attribute(n);

                // 1. every source unchanged since the backup: all restore equal
                var before = snap();
                var t1 = app.RestoreTest(set.Id, 10);
                Assert.Equal(5, t1.Int("candidates")); Assert.Equal(5, t1.Int("checked")); Assert.Equal(5, t1.Int("ok")); Assert.Equal(0, t1.Int("failed"));
                Assert.Equal("OK 5/5", attr("RESTORE_TEST_RESULT"));
                Assert.False(string.IsNullOrEmpty(attr("LAST_RESTORE_TEST")));
                Assert.Equal(before, snap()); Assert.Empty(left());

                // 2. f1's object damaged on the storage disk; f3's source changed after the backup (new content and time)
                Damage(ObjectOnDisk(env, "prs06", app, set.Id, files[1]));
                Write(files[3], Rnd(1234, 99), 30);
                jump += TimeSpan.FromMinutes(1);
                var before2 = snap();
                var t2 = app.RestoreTest(set.Id, 10);
                Assert.Equal(4, t2.Int("candidates"));                                 // the changed source is not a candidate
                Assert.Equal(4, t2.Int("checked")); Assert.Equal(3, t2.Int("ok")); Assert.Equal(1, t2.Int("failed"));
                var lines = t2.List("log").Select(l => l["l"]).ToList();
                Assert.Contains(lines, l => l.Contains("f1.bin") && (l.Contains("restore test failed") || l.Contains("DIFFERENT")));
                Assert.DoesNotContain(lines, l => l.Contains("f3.bin"));
                Assert.Equal("FAILED 3/4", attr("RESTORE_TEST_RESULT"));               // never "passed"
                Assert.Equal(before2, snap()); Assert.Empty(left());                   // isolation: the source is never written; no folder left
                var runs = env.Api.Runs.Since(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddDays(1)).Where(m => m["kind"] == "RestoreTest").ToList();
                Assert.Equal(2, runs.Count);
                Assert.Contains(runs, m => m["result"] == "FAILED" && m["status"] == "bad");
            }
        }
    }
}
