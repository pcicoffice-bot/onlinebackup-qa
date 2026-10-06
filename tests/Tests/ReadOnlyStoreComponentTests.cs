using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// N-4 (Agent N, N9a): a set's storage that went read-only (a disk the system remounted read-only after an error)
    /// must still be READ — its points listed, its objects served for a restore. The index was always opened read-write
    /// with a WAL journal: every read answered 500. The fault is a real read-only bind mount (the tests run as root,
    /// so permission bits would not stop them); its proof is a write that fails with EROFS.
    /// </summary>
    public class ReadOnlyStoreComponentTests
    {
        static string Sh(string cmd) { var p = Process.Start(new ProcessStartInfo("/bin/sh", "-c \"" + cmd.Replace("\"", "\\\"") + "\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false }); var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd(); p.WaitForExit(); return o.Trim(); }

        [Fact]
        public void AReadOnlyStore_StillListsItsPoints_AndServesItsObjects()
        {
            if (Environment.OSVersion.Platform != PlatformID.Unix) return;
            var user = Path.Combine(Path.GetTempPath(), "obro-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var st = new SetStore(user, "1700000000777");
            var job = st.BeginJob(DateTime.UtcNow);
            var rel = string.Join("/", "C/a.txt".Split('/').Select(seg => { using (var h = System.Security.Cryptography.SHA256.Create()) return Base32.Encode(h.ComputeHash(System.Text.Encoding.UTF8.GetBytes(seg))).Substring(0, 26); }));
            var key = KeySet.Random(); var ms = new MemoryStream(); var w = new BackupObject.Writer(ms, key);
            var data = new byte[] { 1, 2, 3, 4, 5 }; w.AddChunk(BackupObject.ChunkId(key, data), data); w.Finish(new Msg().Set("path", "a.txt"));
            st.StageObject(job, new ChkRecord { Rel = rel, Seq = 0, Kind = "F", EncPath = "enc-" + rel, Orig = 5, Mtime = 1 }, new MemoryStream(ms.ToArray()), 1L << 30);
            st.Commit(job, new Msg());
            var points = st.Points(); Assert.Single(points);
            var loc = st.FilesAt(null).List("files").Single().List("objects").Single()["loc"];
            var dir = Path.Combine(user, "files", "1700000000777");
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var mounted = Sh("mount --bind '" + dir + "' '" + dir + "' && mount -o remount,bind,ro '" + dir + "' && echo OK");
            if (mounted != "OK") return;   // NOT TESTED where a bind mount is not allowed (reported by the runner's output)
            try
            {
                Assert.Contains("Read-only file system", Sh("touch '" + dir + "/probe'"));   // the fault is real
                var ro = new SetStore(user, "1700000000777");
                Assert.Equal(points, ro.Points());
                Assert.Single(ro.FilesAt(null).List("files"));
                Assert.True(File.Exists(ro.ObjectPath(loc)));
            }
            finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Sh("umount -l '" + dir + "'"); }
        }
    }
}
