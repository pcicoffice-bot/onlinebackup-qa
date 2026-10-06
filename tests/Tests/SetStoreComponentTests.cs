using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// The server's store of one backup set alone (SetStore, no web server) — component contract (tests/QA/specs.py ST-01):
    ///   purpose  store a run all-or-nothing: what the computer sent is either fully a restore point or not there at all
    ///   input    run 1: a.txt, b.txt, c.txt; run 2: a.txt changed, b.txt deleted; runs that stop: before the commit, and
    ///            after the commit's journal is written (a crash); a truncated object; a wrong type; a path that climbs out
    ///   expected after run 2 the newest point has a (new), c; the first point still has a (old), b, c; every stored object
    ///            has the SHA-256 of the bytes sent; a run stopped before its commit leaves no trace; a run whose journal
    ///            was written is completed by the next start (rolled forward); a truncated object, a wrong type and a path
    ///            out of the set are refused and leave no file; a second run of the set at the same time is refused
    /// </summary>
    public class SetStoreComponentTests : IDisposable
    {
        readonly string userDir = Path.Combine(Path.GetTempPath(), "obstore-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        const string SetId = "1700000000007";
        readonly KeySet key = KeySet.Random();
        public void Dispose() { try { Directory.Delete(userDir, true); } catch (Exception) { } }

        byte[] Obj(string content)
        {
            var ms = new MemoryStream();
            var w = new BackupObject.Writer(ms, key);
            var data = System.Text.Encoding.UTF8.GetBytes(content);
            w.AddChunk(BackupObject.ChunkId(key, data), data);
            w.Finish(new Msg().Set("path", content));
            return ms.ToArray();
        }
        /// <summary>The store keeps encrypted names only (base32 segments of 26 characters): "C/data/a.txt" → three such segments.</summary>
        static string N(string path) { return string.Join("/", path.Split('/').Select(seg => { using (var h = SHA256.Create()) return Base32.Encode(h.ComputeHash(System.Text.Encoding.UTF8.GetBytes(seg))).Substring(0, 26); })); }
        static string Sha(byte[] b) { using (var h = SHA256.Create()) return Bytes.Hex(h.ComputeHash(b)); }
        static string ShaFile(string f) { using (var s = File.OpenRead(f)) using (var h = SHA256.Create()) return Bytes.Hex(h.ComputeHash(s)); }

        ChkRecord Put(SetStore st, string job, string rel, int seq, byte[] bytes, long orig)
        {
            return st.StageObject(job, new ChkRecord { Rel = rel, Seq = seq, Kind = seq == 0 ? "F" : "D", EncPath = "enc-" + rel, Orig = orig, Mtime = 1 }, new MemoryStream(bytes), 1L << 30);
        }

        static string[] Rels(Msg files) { return files.List("files").Select(f => f["rel"] + "=" + f["orig"]).OrderBy(x => x, StringComparer.Ordinal).ToArray(); }
        static string[] Want(params string[] relEqOrig) { return relEqOrig.Select(x => N(x.Split('=')[0]) + "=" + x.Split('=')[1]).OrderBy(x => x, StringComparer.Ordinal).ToArray(); }

        [Fact]
        public void TwoRuns_EveryPointHoldsExactlyItsFiles_AndTheStoredBytesAreTheSentOnes()
        {
            var st = new SetStore(userDir, SetId);
            var t0 = DateTime.UtcNow;
            var j1 = st.BeginJob(t0);
            var sent = new System.Collections.Generic.Dictionary<string, string>();
            foreach (var n in new[] { "a", "b", "c" }) { var b = Obj(n + "1"); sent[n] = Sha(b); Assert.Equal(Sha(b), Put(st, j1, N("C/data/" + n + ".txt"), 0, b, 10).Sha256); }
            st.Commit(j1, new Msg().Set("new", 3));
            var j2 = st.BeginJob(t0.AddMinutes(1));
            var a2 = Obj("a2"); Put(st, j2, N("C/data/a.txt"), 0, a2, 20);
            st.StageDelete(j2, N("C/data/b.txt"));
            st.Commit(j2, new Msg().Set("upd", 1).Set("del", 1));

            var points = st.Points();
            Assert.Equal(2, points.Count);
            Assert.Equal(Want("C/data/a.txt=20", "C/data/c.txt=10"), Rels(st.FilesAt(null)));
            Assert.Equal(Want("C/data/a.txt=10", "C/data/b.txt=10", "C/data/c.txt=10"), Rels(st.FilesAt(points[0])));
            // integrity: the bytes on the disk are the bytes that were sent
            foreach (var f in st.FilesAt(null).List("files"))
            {
                var o = f.List("objects").Last(); var path = st.ObjectPath(o["loc"]);
                Assert.Equal(o["sha"], ShaFile(path));
                Assert.Equal(f["rel"] == N("C/data/a.txt") ? Sha(a2) : sent["c"], ShaFile(path));
            }
            Assert.Equal(0, st.VerifyAll().Int("bad"));
        }

        [Fact]
        public void AStoppedRun_LeavesNoTrace_AndAJournaledRun_IsCompletedByTheNextStart()
        {
            var st = new SetStore(userDir, SetId);
            var t0 = DateTime.UtcNow;
            var j1 = st.BeginJob(t0); Put(st, j1, N("C/a.txt"), 0, Obj("a1"), 10); st.Commit(j1, new Msg());
            var before = st.FilesAt(null).ToString();

            var j2 = st.BeginJob(t0.AddMinutes(1)); Put(st, j2, N("C/a.txt"), 0, Obj("a2"), 20); Put(st, j2, N("C/new.txt"), 0, Obj("n"), 5);
            st.Abort(j2);
            Assert.Equal(before, st.FilesAt(null).ToString());
            Assert.Single(st.Points());
            Assert.False(Directory.Exists(Path.Combine(userDir, "files", SetId, "jobs", j2)));

            var j3 = st.BeginJob(t0.AddMinutes(2)); var a3 = Obj("a3"); Put(st, j3, N("C/a.txt"), 0, a3, 30);
            st.PrepareCommit(j3, new Msg().Set("upd", 1));           // the journal is written … and the server dies here
            var again = new SetStore(userDir, SetId);                // the next start
            Assert.Equal(Want("C/a.txt=30"), Rels(again.FilesAt(null)));
            Assert.Equal(2, again.Points().Count);
            Assert.Equal(Sha(a3), ShaFile(again.ObjectPath(again.FilesAt(null).List("files")[0].List("objects").Last()["loc"])));
            Assert.Equal(0, again.VerifyAll().Int("bad"));
        }

        [Fact]
        public void TruncatedWrongOrEscapingObjects_AreRefused_AndLeaveNoFile()
        {
            var st = new SetStore(userDir, SetId);
            var j = st.BeginJob(DateTime.UtcNow);
            var whole = Obj("whole");
            var cut = whole.Take(whole.Length - 7).ToArray();
            Assert.Equal(400, Assert.Throws<ApiException>(() => Put(st, j, N("C/x.txt"), 0, cut, 5)).Status);
            Assert.Equal(400, Assert.Throws<ApiException>(() => st.StageObject(j, new ChkRecord { Rel = N("C/x.txt"), Seq = 0, Kind = "D", EncPath = "e" }, new MemoryStream(whole), 1 << 20)).Status);
            Assert.ThrowsAny<Exception>(() => Put(st, j, "../../" + N("outside.txt"), 0, whole, 5));
            Assert.ThrowsAny<Exception>(() => Put(st, j, N("C") + "/../../" + N("outside.txt"), 0, whole, 5));
            Assert.Empty(Directory.GetFiles(Path.Combine(userDir, "files", SetId), "*.part", SearchOption.AllDirectories));
            Assert.False(File.Exists(Path.Combine(userDir, "outside.txt")));
            Assert.False(File.Exists(Path.Combine(userDir, "files", "outside.txt")));
            // over the quota: refused, nothing left
            Assert.Equal(507, Assert.Throws<ApiException>(() => st.StageObject(j, new ChkRecord { Rel = N("C/big.txt"), Seq = 0, Kind = "F", EncPath = "e" }, new MemoryStream(new byte[300000]), 1000)).Status);
            Assert.Empty(Directory.GetFiles(Path.Combine(userDir, "files", SetId), "*.part", SearchOption.AllDirectories));
            st.Commit(j, new Msg());
            Assert.Empty(st.FilesAt(null).List("files"));
        }

        [Fact]
        public void ASecondRunOfTheSameSet_IsRefusedWhileTheFirstIsOpen()
        {
            var st = new SetStore(userDir, SetId);
            var j = st.BeginJob(DateTime.UtcNow);
            Assert.Equal(409, Assert.Throws<ApiException>(() => new SetStore(userDir, SetId).BeginJob(DateTime.UtcNow.AddSeconds(5))).Status);
            st.Commit(j, new Msg());
            Assert.NotNull(new SetStore(userDir, SetId).BeginJob(DateTime.UtcNow.AddSeconds(10)));
        }
    }
}
