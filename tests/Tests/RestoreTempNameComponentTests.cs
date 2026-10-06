using System;
using System.IO;
using System.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// RS-02 (Agent B note, proven here): the restore wrote each file first as "&lt;name&gt;.restoring" with FileMode.Create —
    /// a customer's own file of exactly that name was overwritten and then moved away. Here the backup itself holds both
    /// "report.txt" and the customer's "report.txt.restoring"; both must come back with their own bytes (SHA-256).
    /// </summary>
    public class RestoreTempNameComponentTests
    {
        [Fact]
        public void ACustomerFileNamedLikeTheRestoresTemporaryFile_IsNotOverwritten()
        {
            using (var env = new Env())
            {
                env.CreateUser("tmpname", "Customer-Pass-1");
                var app = env.Agent("tmpname", "Customer-Pass-1");
                var src = env.Dir("src");
                File.WriteAllText(Path.Combine(src, "report.txt"), "the report");
                File.WriteAllText(Path.Combine(src, "report.txt.restoring"), "the customer's own file that happens to have this name");
                File.WriteAllText(Path.Combine(src, "report.txt.ob-restoring"), "another own file, named like today's temporary file without its random part");   // Agent L, M15
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "T", Sources = { src }, Vss = false });
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);

                // the customer lost report.txt and restores it to its original place; their other file stays where it is
                var own = File.ReadAllBytes(Path.Combine(src, "report.txt.restoring"));
                var own2 = File.ReadAllBytes(Path.Combine(src, "report.txt.ob-restoring"));
                var report = File.ReadAllBytes(Path.Combine(src, "report.txt"));
                File.Delete(Path.Combine(src, "report.txt"));
                var rs = app.RestoreFor(app.Interactive("Customer-Pass-1", null), set.Id);
                rs.Run(null, null, p => p.EndsWith("report.txt", StringComparison.Ordinal), false);
                Assert.Equal(1, rs.Restored);
                Assert.True(File.Exists(Path.Combine(src, "report.txt.restoring")), "the customer's file report.txt.restoring is gone after restoring report.txt");
                Assert.Equal(Bytes.Hex(Bytes.Sha256(own)), Bytes.Hex(Bytes.Sha256(File.ReadAllBytes(Path.Combine(src, "report.txt.restoring")))));
                Assert.Equal(Bytes.Hex(Bytes.Sha256(report)), Bytes.Hex(Bytes.Sha256(File.ReadAllBytes(Path.Combine(src, "report.txt")))));
                Assert.True(File.Exists(Path.Combine(src, "report.txt.ob-restoring")), "the customer's file report.txt.ob-restoring is gone after restoring report.txt");
                Assert.Equal(Bytes.Hex(Bytes.Sha256(own2)), Bytes.Hex(Bytes.Sha256(File.ReadAllBytes(Path.Combine(src, "report.txt.ob-restoring")))));
            }
        }

        /// <summary>
        /// Bug 78 (Agent K's code reading): a restore killed while writing (power, kill) leaves its own temporary file
        /// "&lt;name&gt;.&lt;8 hex&gt;.ob-restoring" beside the customer's file; nothing removed it, and each cut restore left one more.
        /// The kill itself is not repeated here: the leftover is planted exactly as the product names it (its existence is
        /// checked first). The next restore of that file removes the product's own leftovers — and never a customer's file
        /// that merely ends the same way.
        /// </summary>
        [Fact]
        public void TheLeftoverOfACutRestore_IsRemovedByTheNextRestore_ACustomersLookalikeIsKept()
        {
            using (var env = new Env())
            {
                env.CreateUser("leftover", "Customer-Pass-1");
                var app = env.Agent("leftover", "Customer-Pass-1");
                var src = env.Dir("src");
                File.WriteAllText(Path.Combine(src, "report.txt"), "the report");
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "T", Sources = { src }, Vss = false });
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);

                var target = env.Dir("restore"); var dir = Path.Combine(target, Env.Rel(src)); Directory.CreateDirectory(dir);
                var leftover = Path.Combine(dir, "report.txt.0a1b2c3d.ob-restoring");
                var lookalike = Path.Combine(dir, "report.txt.notours.ob-restoring");
                File.WriteAllText(leftover, "half a report"); File.WriteAllText(lookalike, "the customer's");
                Assert.True(File.Exists(leftover));

                var rs = app.RestoreFor(app.Interactive("Customer-Pass-1", null), set.Id);
                rs.Run(null, target, null, false);
                Assert.Equal(1, rs.Restored);
                Assert.False(File.Exists(leftover), "the leftover of a cut restore is still there");
                Assert.Equal("the customer's", File.ReadAllText(lookalike));
                Assert.Equal("the report", File.ReadAllText(Path.Combine(dir, "report.txt")));
            }
        }
    }
}
