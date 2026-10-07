using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// The quota and a full server disk — on the server, the set's store alone (SetStore, no web server); on the computer,
    /// BackupRun alone against a recording stand-in server (StubServer). Component contracts (tests/QA/specs.py ST-05, ST-06):
    ///   ST-05 input    a committed point; an object exactly the room left, and the same with one byte less room; on the
    ///                  computer a run of 4 changed files and 1 deleted whose 2nd upload is refused 507 QUOTA
    ///         expected exactly the room is accepted, one byte over is refused 507 QUOTA and leaves no file in the run; the
    ///                  committed objects are byte-identical afterwards; the run ends BS_STOP_QUOTA_EXCEEDED, no deletion is
    ///                  sent, the newest point holds the first file new and the others (also the deleted one) as before;
    ///                  the next run with room completes and the point restores identical to the source (SHA-256)
    ///   ST-06 input    the disk answering "no space left" (ENOSPC, from the kernel: the object's file is /dev/full) in the
    ///                  middle of an object; on the computer an upload refused 507 DISK_FULL
    ///         expected no half object is left and the run's list of uploads does not name it; earlier points byte-identical;
    ///                  the error is the server's own "disk full" kind; the same object is accepted once space is back; the
    ///                  run is BS_STOP_BY_SYSTEM_ERROR with "disk is full" in its log, ended by an abort (no commit), the
    ///                  local index unchanged; the next run completes and restores identical
    /// </summary>
    public class StorageLimitsComponentTests : IDisposable
    {
        readonly string userDir = Path.Combine(Path.GetTempPath(), "oblimits-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        const string SetId = "1700000000031";
        readonly KeySet key = KeySet.Random();
        public void Dispose() { try { Directory.Delete(userDir, true); } catch (Exception) { } }

        byte[] Obj(string content, int pad = 0)
        {
            var ms = new MemoryStream(); var w = new BackupObject.Writer(ms, key) { Compression = "NONE" };
            var data = System.Text.Encoding.UTF8.GetBytes(content + new string('x', pad));
            w.AddChunk(BackupObject.ChunkId(key, data), data); w.Finish(new Msg().Set("path", content));
            return ms.ToArray();
        }
        static string Sha(byte[] b) { using (var h = SHA256.Create()) return Bytes.Hex(h.ComputeHash(b)); }
        static ChkRecord Meta(string rel) { return new ChkRecord { Rel = rel, Seq = 0, Kind = "F", EncPath = "enc-" + rel, Orig = 10, Mtime = 1 }; }
        static string Rel(string name) { return "AAAAAAAAAAAAAAAAAAAAAAAAAA/" + name.ToUpperInvariant().PadRight(26, 'A').Substring(0, 26); }
        string JobDir(SetStore st, string job) { return Directory.GetDirectories(st.Dir, job, SearchOption.AllDirectories).Single(); }
        static Dictionary<string, string> Stored(SetStore st) { return AgentRig.Tree(Path.Combine(st.Dir, SetStore.Current)); }

        SetStore WithOnePoint(out string files1)
        {
            var st = new SetStore(userDir, SetId);
            var j1 = st.BeginJob(DateTime.UtcNow.AddMinutes(-10));
            foreach (var n in new[] { "a", "b" }) st.StageObject(j1, Meta(Rel(n)), new MemoryStream(Obj(n + " v1", 5000)), 1L << 30);
            st.Commit(j1, new Msg().Set("new", 2));
            files1 = st.FilesAt(null).ToString();
            return st;
        }

        [Fact]
        public void Quota_ExactlyTheRoomIsAccepted_OneByteOverIsRefused_NothingLeft_StoredPointsUnchanged()
        {
            string files1; var st = WithOnePoint(out files1);
            var before = Stored(st);
            var j2 = st.BeginJob(DateTime.UtcNow);
            var obj = Obj("c v1", 20000);
            var e = Assert.Throws<ApiException>(() => st.StageObject(j2, Meta(Rel("c")), new MemoryStream(obj), obj.Length - 1));
            Assert.Equal(507, e.Status); Assert.Equal("QUOTA", e.Code);
            Assert.Empty(Directory.GetFiles(JobDir(st, j2), "*", SearchOption.AllDirectories).Where(f => !f.EndsWith("lease") && Path.GetFileName(f) != "key"));
            Assert.Equal(0, st.StagedBytes(j2));
            Assert.Equal(before, Stored(st));
            Assert.Equal(files1, st.FilesAt(null).ToString());

            Assert.Equal(Sha(obj), st.StageObject(j2, Meta(Rel("c")), new MemoryStream(obj), obj.Length).Sha256);   // exactly the room
            st.Commit(j2, new Msg().Set("new", 1));
            foreach (var kv in before) Assert.Equal(kv.Value, Stored(st)[kv.Key]);                                      // the old objects untouched
            Assert.Equal(3, st.FilesAt(null).List("files").Count);
        }

        [Fact]
        public void ServerDiskFull_MidObject_NoHalfObjectLeft_EarlierPointsUnchanged_TheSameObjectIsAcceptedOnceSpaceIsBack()
        {
            if (!File.Exists("/dev/full")) throw NotTested.Because("this test needs /dev/full (Linux) to make the kernel answer ENOSPC");
            string files1; var st = WithOnePoint(out files1);
            var before = Stored(st);
            var j2 = st.BeginJob(DateTime.UtcNow);
            var obj = Obj("c v1", 300000);
            var rel = Rel("c");
            var part = Path.Combine(JobDir(st, j2), "new", ChkRecord.ObjectName(rel, 0).Replace('/', Path.DirectorySeparatorChar)) + ".part";
            Directory.CreateDirectory(Path.GetDirectoryName(part));
            File.CreateSymbolicLink(part, "/dev/full");                                   // the disk is full under this object
            var e = Assert.ThrowsAny<IOException>(() => st.StageObject(j2, Meta(rel), new MemoryStream(obj), 1L << 30));
            var diskFull = typeof(Api).GetMethod("DiskFull", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.True((bool)diskFull.Invoke(null, new object[] { e }), "the server must recognise this as a full disk: " + e.Message + " 0x" + e.HResult.ToString("X"));
            Assert.False(File.Exists(part) || File.Exists(part.Substring(0, part.Length - 5)));
            var uploads = Path.Combine(JobDir(st, j2), "uploads.txt");
            Assert.False(File.Exists(uploads) && File.ReadAllText(uploads).Contains(ChkRecord.ObjectName(rel, 0)));
            Assert.Equal(before, Stored(st));
            Assert.Equal(files1, st.FilesAt(null).ToString());

            Assert.Equal(Sha(obj), st.StageObject(j2, Meta(rel), new MemoryStream(obj), 1L << 30).Sha256);   // space is back
            st.Commit(j2, new Msg().Set("new", 1));
            var c = st.FilesAt(null).List("files").Single(f => f["rel"] == rel).List("objects").Single();
            Assert.Equal(Sha(obj), Sha(File.ReadAllBytes(st.ObjectPath(c["loc"]))));
            foreach (var kv in before) Assert.Equal(kv.Value, Stored(st)[kv.Key]);
        }

        static string Name(AgentRig rig, StubServer.Req q) { using (var s = new MemoryStream(q.Body)) return Path.GetFileName(BackupObject.ReadHeader(s, rig.Key)["path"]); }

        [Fact]
        public void AQuotaRefusalMidRun_EndsQuotaExceeded_NothingDeleted_TheRestWaits_TheNextRunCompletes()
        {
            using (var rig = new AgentRig())
            {
                foreach (var n in new[] { "a", "b", "c", "d", "e" }) rig.File(n + ".txt", n + " version 1");
                Assert.Equal("BS_STOP_SUCCESS", rig.Backup().Result);
                var v1 = AgentRig.Tree(rig.Src);
                foreach (var n in new[] { "a", "b", "c", "d" }) { var f = rig.File(n + ".txt", n + " version 2, longer"); File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddMinutes(1)); }
                File.Delete(Path.Combine(rig.Src, "e.txt"));
                var v2 = AgentRig.Tree(rig.Src);

                int uploads = 0;
                rig.Server.Handler = q => q.Action == "object" && ++uploads == 2 ? StubServer.Answer.Error(507, "QUOTA", "The user's quota was exceeded.") : null;
                var r = rig.Backup();
                Assert.Equal("BS_STOP_QUOTA_EXCEEDED", r.Result);
                Assert.Equal("BS_STOP_QUOTA_EXCEEDED", rig.Server.Of("commit").Single(c => c.Job == r.Job).Msg["result"]);
                Assert.DoesNotContain(rig.Server.Requests, q => q.Job == r.Job && q.Action == "delete");
                var point = rig.Restored("r2");
                Assert.Equal(v2["a.txt"], point["a.txt"]);                                       // sent before the quota was reached
                foreach (var n in new[] { "b.txt", "c.txt", "d.txt", "e.txt" }) Assert.Equal(v1[n], point[n]);   // the rest as before, e not deleted

                rig.Server.Handler = q => null;                                                  // room is given
                var r3 = rig.Backup();
                Assert.Equal("BS_STOP_SUCCESS", r3.Result);
                Assert.Equal(new[] { "b.txt", "c.txt", "d.txt" }, rig.Server.Of("object").Where(q => q.Job == r3.Job).Select(q => Name(rig, q)).OrderBy(x => x).ToArray());
                Assert.Equal(v2, rig.Restored("r3"));
            }
        }

        [Fact]
        public void ServerDiskFull_TheRunFailsWithTheServersReason_TheIndexDoesNotMove_TheNextRunCompletes()
        {
            using (var rig = new AgentRig())
            {
                rig.File("a.txt", "alpha"); rig.File("b.txt", "beta");
                Assert.Equal("BS_STOP_SUCCESS", rig.Backup().Result);
                var index = AgentRig.Sha(rig.StateFile);
                var f = rig.File("b.txt", "beta, changed"); File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddMinutes(1));
                rig.File("c.txt", "gamma");
                var want = AgentRig.Tree(rig.Src);

                rig.Server.Quota = new Msg().Set("diskFull", 1);
                rig.Server.Handler = q => q.Action == "object" ? StubServer.Answer.Error(507, "DISK_FULL", "The backup server's disk is full: new backups cannot be stored.") : null;
                var r = rig.Backup();
                Assert.Equal("BS_STOP_BY_SYSTEM_ERROR", r.Result);
                Assert.Contains(r.LogLines, l => AhsayLog.Fields(l)[1] == "err" && l.Contains("disk is full"));
                Assert.Empty(rig.Server.Of("commit").Where(c => c.Job == r.Job));
                Assert.Equal("BS_STOP_BY_SYSTEM_ERROR", rig.Server.Of("abort").Single(a => a.Job == r.Job).Msg["result"]);
                Assert.Equal(index, AgentRig.Sha(rig.StateFile));
                Assert.False(File.Exists(rig.MarkerFile));                                       // the end reached the server

                rig.Server.Handler = q => null; rig.Server.Quota = new Msg();                    // space is freed
                var r2 = rig.Backup();
                Assert.Equal("BS_STOP_SUCCESS", r2.Result);
                Assert.Equal(want, rig.Restored());
            }
        }
    }
}
