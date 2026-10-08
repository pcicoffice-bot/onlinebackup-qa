using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// QA night round S: xUnit tests that kill mutants the existing xUnit suite let survive, for bugs whose only
    /// regression cover is Playwright (bug 85 restore onto a full/unwritable local disk; bug 86 the empty temp object a
    /// failed download leaves). Each was shown to FAIL with the fix's one line undone and PASS with it in place
    /// (see tests/QA/night/s/README.md). Oracle is outside the product's own success text: the AgentException Code the
    /// agent assigns (local-disk vs network), and the files left on disk.
    /// </summary>
    public class NightS_MutationKillTests : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "obnights-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        readonly KeySet key = KeySet.Random();
        public void Dispose() { try { Directory.Delete(root, true); } catch (Exception) { } }
        static string Sha(byte[] b) { return Bytes.Hex(Bytes.Sha256(b)); }

        /// <summary>A real backup object of one file (as BackupRun writes it); returns its bytes.</summary>
        byte[] Object(string path, byte[] data)
        {
            var ms = new MemoryStream(); var w = new BackupObject.Writer(ms, key);
            var recipe = new List<KeyValuePair<string, int>>(); var seen = new HashSet<string>();
            foreach (var c in Chunker.ForFileSize(data.Length).Split(new MemoryStream(data)))
            {
                var id = BackupObject.ChunkId(key, c); recipe.Add(new KeyValuePair<string, int>(id, c.Length));
                if (seen.Add(id)) w.AddChunk(id, c);
            }
            var header = new Msg().Set("path", path).Set("size", data.Length).Set("mtime", 1700000000000).Set("attrs", "").Set("kind", "F").Set("seq", 0);
            foreach (var r in recipe) header.Add("recipe", new Msg().Set("h", r.Key).Set("n", r.Value));
            w.Finish(header);
            return ms.ToArray();
        }

        // ---------------------------------------------------------------- bug 86

        /// <summary>A restore source whose download leaves an EMPTY file and returns (a cut download: the bytes never
        /// arrived but the object file was already created). The checksum then refuses it.</summary>
        sealed class EmptyDownloadSource : IRestoreSource
        {
            public List<string> Points() { return new List<string> { "p" }; }
            public Msg Files(string point) { return new Msg(); }
            public void Fetch(string loc, string toFile) { File.WriteAllBytes(toFile, new byte[0]); }   // created, then nothing
            public void Report(Msg log) { }
        }

        /// <summary>
        /// Bug 86 (Agent N, N-3): a failed download used to leave its empty "*.obj" in the agent's temp folder, because
        /// the path was noted for clean-up only AFTER the download+checksum, which a failure never reached. The fix notes
        /// the path BEFORE the download. Oracle: the files left in the temp folder after the restore of this object fails.
        /// Killed mutant: paths.Add(p) moved back after the checksum (its pre-fix place).
        /// </summary>
        [Fact]
        public void AFailedDownload_LeavesNoEmptyObjectInTheTempFolder()
        {
            var temp = Path.Combine(root, "tmp86");
            var r = new Restore(new EmptyDownloadSource(), key, temp);
            var real = Object("/data/a.txt", System.Text.Encoding.UTF8.GetBytes("the real contents"));
            var f = new Msg().Set("rel", "R1").Set("enc", NameCipher.EncryptPath(key, "/data/a.txt")).Set("orig", 17).Set("mtime", 1700000000000);
            f.Add("objects", new Msg().Set("loc", "Current/1").Set("seq", 0).Set("kind", "F").Set("size", real.Length).Set("sha", Sha(real)));
            // the download writes an empty file, so the checksum refuses it: the restore of this file fails
            Assert.ThrowsAny<Exception>(() => r.RestoreFile(f, Path.Combine(root, "out86", "a.txt")));
            var left = Directory.Exists(temp) ? Directory.GetFiles(temp, "*.obj") : new string[0];
            Assert.True(left.Length == 0, "a failed download left " + left.Length + " object file(s) in the temp folder: " + string.Join(", ", left.Select(Path.GetFileName)));
        }

        // ---------------------------------------------------------------- bug 85

        /// <summary>
        /// Bug 85 (Agent N, N-2): a failure of THIS computer's disk while downloading (full, no permission, a missing
        /// folder) was caught as an IOException by the retry loop and retried four times with pauses, for every file —
        /// a restore onto a full disk took days to say so, and said "network". The fix opens the local file through
        /// Client.LocalFile, which stops at once with AgentException code LOCAL_DISK. Here the download target sits under a
        /// folder that does not exist (a DirectoryNotFoundException, an IOException), the GET itself succeeds.
        /// Oracle: the AgentException Code the agent assigns. Killed mutant: LocalFile reverted to a bare FileStream, so the
        /// IOException falls into the retry loop and the call ends as NETWORK.
        /// </summary>
        [Fact]
        public void ALocalWriteFailureWhileDownloading_IsLocalDisk_NotRetriedAsNetwork()
        {
            using (var env = new Env())
            {
                env.CreateUser("nights85", "Customer-Pass-1");
                var app = env.Agent("nights85", "Customer-Pass-1");
                var src = env.Dir("src85");
                File.WriteAllText(Path.Combine(src, "readme.txt"), "hello from bug 85");
                var session = app.Interactive("Customer-Pass-1", null);
                var set = app.CreateSet(session, "Customer-Pass-1", new BackupSetInfo { Name = "S85", Sources = { src }, Vss = false });
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);

                var loc = session.Call("GET", "/api/sets/" + set.Id + "/files").List("files")[0].List("objects")[0]["loc"];
                // a destination under a folder that does not exist: the local write fails, the server GET does not
                var badPath = Path.Combine(env.Root, "no-such-folder-85", "object.obj");
                Assert.False(Directory.Exists(Path.GetDirectoryName(badPath)));

                var ex = Assert.Throws<AgentException>(() =>
                    session.Download("/api/sets/" + set.Id + "/object?loc=" + Client.Url(loc), badPath));
                Assert.Equal("LOCAL_DISK", ex.Code);
            }
        }
    }
}
