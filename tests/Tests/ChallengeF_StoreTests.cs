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
    /// QA Agent F (static review challenger): SetStore's integrity check (VerifyRows), hit dad16b8a54, triaged OK: "an
    /// object that cannot be read counts as damaged (actual = null ≠ sha)". Counting it as damaged is not the problem;
    /// what follows is: the object is moved to Quarantine and DELETED FROM THE INDEX. For the current version the agent
    /// sends it again (resend), but an OLD version (in a run folder of the retention area) cannot be sent again — the
    /// computer has only the current file. So a read that fails for a moment (the file held open exclusively by another
    /// program — an antivirus scan, a backup of the backup server, a copy tool; too many open files) during the
    /// administrator's "Verify" of the set (Api.cs:1317, SetStore.VerifyAll) removes a sound old version from every restore point, permanently.
    /// The SHA-256 of the file is the oracle: the object is byte-identical before and after; only the index lost it.
    /// </summary>
    public class ChallengeF_StoreTests : IDisposable
    {
        readonly string userDir = Path.Combine(Path.GetTempPath(), "obstoref-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        const string SetId = "1700000000077";
        readonly KeySet key = KeySet.Random();
        public void Dispose() { SystemClock.Use(null); try { Directory.Delete(userDir, true); } catch (Exception) { } }

        byte[] Obj(string content)
        {
            var ms = new MemoryStream();
            var w = new BackupObject.Writer(ms, key);
            var data = System.Text.Encoding.UTF8.GetBytes(content);
            w.AddChunk(BackupObject.ChunkId(key, data), data);
            w.Finish(new Msg().Set("path", content));
            return ms.ToArray();
        }
        static string N(string path) { return string.Join("/", path.Split('/').Select(seg => { using (var h = SHA256.Create()) return Base32.Encode(h.ComputeHash(System.Text.Encoding.UTF8.GetBytes(seg))).Substring(0, 26); })); }
        static string ShaFile(string f) { using (var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) using (var h = SHA256.Create()) return Bytes.Hex(h.ComputeHash(s)); }
        ChkRecord Put(SetStore st, string job, string rel, byte[] bytes, long orig)
        {
            return st.StageObject(job, new ChkRecord { Rel = rel, Seq = 0, Kind = "F", EncPath = "enc-" + rel, Orig = orig, Mtime = 1 }, new MemoryStream(bytes), 1L << 30);
        }
        static string[] Rels(Msg files) { return files.List("files").Select(f => f["rel"] + "=" + f["orig"]).OrderBy(x => x, StringComparer.Ordinal).ToArray(); }

        [Theory]
        [InlineData(false)]   // control: nothing holds the file — the check finds nothing and the old point is whole
        [InlineData(true)]    // another program holds the old version open (exclusively) while the check runs
        public void AnOldVersionThatCannotBeReadForAMoment_IsNotRemovedFromItsRestorePoint(bool heldOpen)
        {
            var st = new SetStore(userDir, SetId);
            var t0 = DateTime.UtcNow;
            var a = N("C/data/a.txt"); var c = N("C/data/c.txt");
            var j1 = st.BeginJob(t0);
            Put(st, j1, a, Obj("a1"), 10); Put(st, j1, c, Obj("c1"), 10);
            st.Commit(j1, new Msg().Set("new", 2));
            var j2 = st.BeginJob(t0.AddMinutes(1));
            Put(st, j2, a, Obj("a2"), 20);                               // a changes: version 1 goes to the retention area
            st.Commit(j2, new Msg().Set("upd", 1));
            var first = st.Points()[0];
            Assert.Equal(new[] { a + "=10", c + "=10" }.OrderBy(x => x, StringComparer.Ordinal).ToArray(), Rels(st.FilesAt(first)));
            var oldLoc = st.FilesAt(first).List("files").Single(f => f["rel"] == a).List("objects").Single()["loc"];
            var oldPath = st.ObjectPath(oldLoc);
            Assert.NotEqual(SetStore.Current, oldLoc.Split('/')[0]);     // it is in a run folder (an old version)
            var shaBefore = ShaFile(oldPath);

            Msg check;
            using (heldOpen ? new FileStream(oldPath, FileMode.Open, FileAccess.Read, FileShare.None) : null)
                check = st.VerifyAll();

            // the bytes of the old version were never damaged (the oracle: the file's own SHA-256, wherever it now is)
            var quarantine = Path.Combine(userDir, "files", SetId, "Quarantine");
            var nowAt = File.Exists(oldPath) ? oldPath : !Directory.Exists(quarantine) ? null
                : Directory.EnumerateFiles(quarantine, "*", SearchOption.AllDirectories).FirstOrDefault(f => f.Replace('\\', '/').EndsWith(oldLoc));
            Assert.NotNull(nowAt);
            Assert.Equal(shaBefore, ShaFile(nowAt));
            // the old restore point must still restore version 1 of a.txt
            Assert.True(Rels(st.FilesAt(first)).Contains(a + "=10"),
                "the first restore point lost a.txt (version 1) although its bytes are intact: check said " + check.ToString() + "; the object is now at " + nowAt);
        }
        /// <summary>
        /// Hits 784709256f / 57165906eb (SetStore.Touch / TouchFile), triaged OK: "a lease touch on a run folder that is gone:
        /// nothing to keep alive". Touch runs OUTSIDE the set's gate (every progress report and heartbeat calls it). When it
        /// runs while ExpireStale (the minute sweep, or a Begin) is deleting the same run folder — a computer that wakes up
        /// and sends its heartbeat just as the server closes its run — TouchFile finds no "lease" and writes a new one into the
        /// folder being deleted. The recursive delete then fails ("directory not empty"): ExpireStale throws (the sweep / the
        /// Begin fails), and a half-deleted run folder with a FRESH lease stays: the set is "busy" for another lease period
        /// and the next backup is refused (409). A big run has thousands of staged objects, which makes the delete — and
        /// the window — long. Oracle: the folder on disk and the next BeginJob.
        /// </summary>
        [Theory]
        [InlineData(false)]   // control: no heartbeat during the close — must pass
        [InlineData(true)]
        public void AHeartbeatWhileTheRunIsBeingClosed_LeavesNoGhostRun(bool heartbeatDuringClose)
        {
            var st = new SetStore(userDir, SetId);
            var t0 = DateTime.UtcNow;
            var job = st.BeginJob(t0);
            var jd = Path.Combine(userDir, "files", SetId, "jobs", job);
            // a big run: many staged objects (the store's own staging layout, written directly to save time)
            for (int d = 0; d < 40; d++)
            {
                var dir = Path.Combine(jd, "new", "D" + d); Directory.CreateDirectory(dir);
                for (int i = 0; i < 100; i++) File.WriteAllText(Path.Combine(dir, "o" + i + ".000"), "x");
            }
            var closeAt = t0.AddMinutes(10);                             // the computer was silent for 10 minutes; now it wakes up
            SystemClock.Use(() => closeAt);                              // (the heartbeat's touch is stamped with the server's clock)
            var stop = false; int touches = 0;
            // the heartbeat arrives once the server has decided to close the run and is deleting its folder
            var probes = Enumerable.Range(0, 40).Select(d => Path.Combine(jd, "new", "D" + d, "o0.000")).Concat(new[] { Path.Combine(jd, "lease") }).ToArray();
            var heartbeat = new System.Threading.Thread(() =>
            {
                while (!System.Threading.Volatile.Read(ref stop) && probes.All(File.Exists)) { }
                while (!System.Threading.Volatile.Read(ref stop)) { st.Touch(job); touches++; }
            });
            if (heartbeatDuringClose) heartbeat.Start();
            Exception closing = null;
            try { st.ExpireStale(closeAt); } catch (Exception e) { closing = e; }
            System.Threading.Volatile.Write(ref stop, true); if (heartbeatDuringClose) heartbeat.Join();
            var ghost = Directory.Exists(jd) ? string.Join(", ", Directory.GetFileSystemEntries(jd).Select(Path.GetFileName)) : null;
            Exception next = null;
            try { st.BeginJob(closeAt.AddMinutes(1)); } catch (Exception e) { next = e; }   // the next backup, a minute later
            finally { SystemClock.Use(null); }
            Assert.True(closing == null && ghost == null && next == null,
                "closing the run while it was touched (" + touches + " touches): close " + (closing == null ? "ok" : "threw " + closing.GetType().Name + ": " + closing.Message)
                + "; folder left: " + (ghost ?? "none") + "; next begin: " + (next == null ? "ok" : next.Message));
        }
    }
}
