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
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "T", Sources = { src }, Vss = false });
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);

                // the customer lost report.txt and restores it to its original place; their other file stays where it is
                var own = File.ReadAllBytes(Path.Combine(src, "report.txt.restoring"));
                var report = File.ReadAllBytes(Path.Combine(src, "report.txt"));
                File.Delete(Path.Combine(src, "report.txt"));
                var rs = app.RestoreFor(app.Interactive("Customer-Pass-1", null), set.Id);
                rs.Run(null, null, p => p.EndsWith("report.txt", StringComparison.Ordinal), false);
                Assert.Equal(1, rs.Restored);
                Assert.True(File.Exists(Path.Combine(src, "report.txt.restoring")), "the customer's file report.txt.restoring is gone after restoring report.txt");
                Assert.Equal(Bytes.Hex(Bytes.Sha256(own)), Bytes.Hex(Bytes.Sha256(File.ReadAllBytes(Path.Combine(src, "report.txt.restoring")))));
                Assert.Equal(Bytes.Hex(Bytes.Sha256(report)), Bytes.Hex(Bytes.Sha256(File.ReadAllBytes(Path.Combine(src, "report.txt")))));
            }
        }
    }
}
