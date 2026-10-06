using System;
using System.IO;
using System.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// D-2: lines of the computer's local index (state.txt) lost between two backups — a disk error, a torn write. The
    /// files on the lost lines were deleted by the customer since: the next backup must still delete them from the newest
    /// point (oracle: the files restored from the newest point, by name and SHA-256, against the source as it is now).
    /// </summary>
    public class IndexLinesLostComponentTests
    {
        [Theory]
        [InlineData(false)]   // control: the index intact — the deleted files leave the newest point
        [InlineData(true)]    // the lines of the deleted files lost, the last line torn
        public void LinesOfTheIndexLost_TheDeletedFilesStillLeaveTheNewestPoint(bool damage)
        {
            using (var env = new Env())
            {
                env.CreateUser("lines1", "Customer-Pass-1");
                var app = env.Agent("lines1", "Customer-Pass-1");
                var src = env.Dir("src");
                for (int i = 0; i < 12; i++) File.WriteAllText(Path.Combine(src, "f" + i.ToString("00") + ".txt"), "file " + i);
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "L", Sources = { src }, Vss = false });
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);

                for (int i = 0; i < 4; i++) File.Delete(Path.Combine(src, "f" + i.ToString("00") + ".txt"));
                if (damage)
                {
                    var st = Path.Combine(app.Home.SetDir(set.Id), "state.txt");
                    var lines = File.ReadAllLines(st).Where(l => !System.Text.RegularExpressions.Regex.IsMatch(l, @"f0[0-3]\.txt\t")).ToList();
                    Assert.Equal(File.ReadAllLines(st).Length - 4, lines.Count);
                    var text = string.Join("\n", lines); File.WriteAllText(st, text.Substring(0, text.Length - 3));
                }
                Assert.StartsWith("BS_STOP_SUCCESS", app.Backup(set.Id).Result);

                var target = env.Dir("restore");
                var rs = app.RestoreFor(app.Interactive("Customer-Pass-1", null), set.Id);
                rs.Run(null, target, null, false);
                var got = Directory.GetFiles(Path.Combine(target, Env.Rel(src))).Select(Path.GetFileName).OrderBy(x => x).ToList();
                var want = Directory.GetFiles(src).Select(Path.GetFileName).OrderBy(x => x).ToList();
                Assert.True(want.SequenceEqual(got), "the newest point holds " + string.Join(",", got) + " — the source holds " + string.Join(",", want));
                foreach (var f in want) Assert.Equal(Bytes.Hex(Bytes.Sha256(File.ReadAllBytes(Path.Combine(src, f)))), Bytes.Hex(Bytes.Sha256(File.ReadAllBytes(Path.Combine(target, Env.Rel(src), f)))));
            }
        }
    }
}
