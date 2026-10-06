using System;
using System.IO;
using System.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// QA agent B (code audit) — a file that changes size between the scan and the upload (BK-01 / BK-06).
    /// BackupRun takes the size from the FileInfo the scanner made when it LISTED the folder (cached; with VSS it is even
    /// the live file while the bytes come from the snapshot), writes it into the object header as "size", but uploads
    /// whatever bytes it reads later. Restore.RestoreFile checks the rebuilt length against the header size and refuses
    /// the file ("Restored size differs from the backed-up size"). The backup reports success; the restore of that file in
    /// that point always fails. Typical victims: logs, mail stores, databases — files being written during the backup.
    /// </summary>
    public class AuditB_GrowingFileTests
    {
        [Theory]
        [InlineData(true)]
        [InlineData(false)]   // control: the same run without the write restores (the oracle can pass)
        public void AFileThatGrowsAfterTheFolderWasListed_IsRestorable_OrTheRunSaysItIsNot(bool grows)
        {
            using (var env = new Env())
            {
                env.CreateUser("auditb5", "Customer-Pass-1");
                var app = env.Agent("auditb5", "Customer-Pass-1");
                var src = env.Dir("src");
                File.WriteAllText(Path.Combine(src, "a.txt"), "first file");
                var log = Path.Combine(src, "b.log");
                File.WriteAllText(log, "line 1\n");
                var created = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "F", Sources = { src }, Vss = false });
                var set = app.Sets().First(s => s.Id == created.Id);

                int calls = 0;
                // StopRequested is asked once per item, after the folder was listed and before the item is read:
                // the application appends a line to b.log while a.txt is being sent
                var run = new BackupRun(app.DeviceClient(), app.Home, set, app.Key(set))
                {
                    StopRequested = () => { if (++calls == 2 && grows) File.AppendAllText(log, "line 2 written during the backup\n"); return false; }
                };
                var result = run.Run();
                Assert.Equal(2, calls);

                var target = env.Dir("restore");
                var rs = app.RestoreFor(app.Interactive("Customer-Pass-1", null), set.Id);
                rs.Run(null, target, null, false);
                var restored = Path.Combine(target, Env.Rel(src), "b.log");
                bool ok = rs.Failed == 0 && File.Exists(restored) && (File.ReadAllText(restored) == "line 1\n" || File.ReadAllText(restored) == File.ReadAllText(log));
                Assert.True(ok || result != "BS_STOP_SUCCESS",
                    "backup ended " + result + " but the restore of that point: " + rs.Result + ", failed=" + rs.Failed + " — " + string.Join(" | ", rs.Log.Where(l => l.Contains("err"))));
            }
        }
    }
}
