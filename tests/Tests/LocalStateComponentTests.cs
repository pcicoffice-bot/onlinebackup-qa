using System;
using System.IO;
using System.Linq;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// The agent's local index (LocalState) lost or damaged — BackupRun alone against a recording stand-in server that also
    /// answers GET /files like the real one (StubServer). Component contract (tests/QA/specs.py AG-06):
    ///   purpose  a lost or damaged local index costs at most some re-sending, never a wrong backup or a stuck one
    ///   input    a set of a.txt, b.txt and a 200 KB big.bin (delta threshold 1 000 bytes) backed up once; then state.txt
    ///            removed and b.txt changed; or one line of state.txt damaged (a size that is not a number) and a.txt
    ///            changed; or every chunk list damaged and big.bin changed in the middle; two runs after each
    ///   expected the index gone: rebuilt from the server's file list, only b.txt sent, BS_STOP_SUCCESS; a damaged line or
    ///            chunk list: both later runs BS_STOP_SUCCESS (rebuilt or re-sent, never failed for ever); every time the
    ///            newest point restores identical to the source (SHA-256)
    /// Oracle: the requests the stand-in server received and the files restored from the objects it received.
    /// </summary>
    public class LocalStateComponentTests
    {
        static AgentRig Rig()
        {
            var rig = new AgentRig();
            rig.Set.MinDeltaFileSize = 1000;
            rig.File("a.txt", "alpha"); rig.File("b.txt", "beta");
            var big = new byte[200000]; new Random(5).NextBytes(big); rig.File("big.bin", big);
            return rig;
        }
        static string[] SentIn(AgentRig rig, string job)
        {
            return rig.Server.Of("object").Where(q => q.Job == job).Select(q => { using (var s = new MemoryStream(q.Body)) return Path.GetFileName(BackupObject.ReadHeader(s, rig.Key)["path"]); }).OrderBy(x => x).ToArray();
        }
        /// <summary>The error lines the computer sent to the server (what a technician would read).</summary>
        static string Errors(AgentRig rig)
        {
            return string.Join(" | ", rig.Server.Requests.Where(q => q.Action == "abort" || q.Action == "commit").SelectMany(q => q.Msg.List("log")).Select(l => l["l"]).Where(l => AhsayLog.Fields(l).Length > 4 && AhsayLog.Fields(l)[1] == "err").Select(l => AhsayLog.Fields(l)[4]).Distinct());
        }
        static void Change(AgentRig rig, string name, string text) { var f = rig.File(name, text); File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddMinutes(1)); }

        [Fact]
        public void ALostIndex_IsRebuiltFromTheServer_OnlyTheChangedFileIsSent_AndEverythingRestoresIdentical()
        {
            using (var rig = Rig())
            {
                Assert.Equal("BS_STOP_SUCCESS", rig.Backup().Result);
                rig.Server.Files = rig.Server.PointFiles(rig.Key);
                File.Delete(rig.StateFile); Directory.Delete(Path.Combine(Path.GetDirectoryName(rig.StateFile), "chunks"), true);   // the computer lost its index
                Change(rig, "b.txt", "beta, changed after the index was lost");
                var want = AgentRig.Tree(rig.Src);
                int listsBefore = rig.Server.Of("files").Count;

                var r = rig.Backup();
                Assert.Equal("BS_STOP_SUCCESS", r.Result);
                Assert.Equal(listsBefore + 1, rig.Server.Of("files").Count);          // it asked the server
                Assert.Equal(new[] { "b.txt" }, SentIn(rig, r.Job));
                Assert.DoesNotContain(rig.Server.Requests, q => q.Job == r.Job && q.Action == "delete");
                Assert.Equal(want, rig.Restored());
                Assert.Equal(3, File.ReadAllLines(rig.StateFile).Count(l => l.Length > 0 && !l.StartsWith("#")));

                var big = File.ReadAllBytes(Path.Combine(rig.Src, "big.bin")); big[100000] ^= 0x55; rig.File("big.bin", big);
                File.SetLastWriteTimeUtc(Path.Combine(rig.Src, "big.bin"), DateTime.UtcNow.AddMinutes(2));
                var r3 = rig.Backup();
                Assert.Equal("BS_STOP_SUCCESS", r3.Result);
                Assert.Equal(new[] { "big.bin" }, SentIn(rig, r3.Job));
                Assert.Equal(AgentRig.Tree(rig.Src), rig.Restored("r3"));
            }
        }

        [Fact]
        public void ADamagedIndexLine_DoesNotFailEveryLaterBackup()
        {
            using (var rig = Rig())
            {
                Assert.Equal("BS_STOP_SUCCESS", rig.Backup().Result);
                rig.Server.Files = rig.Server.PointFiles(rig.Key);
                var lines = File.ReadAllLines(rig.StateFile);
                int i = Array.FindIndex(lines, l => !l.StartsWith("#") && l.Contains("\t"));
                var f = lines[i].Split('\t'); f[2] = "1x2"; lines[i] = string.Join("\t", f);   // one damaged field (a bad sector, a half write by another tool)
                File.WriteAllLines(rig.StateFile, lines);
                Change(rig, "a.txt", "alpha, changed");
                var want = AgentRig.Tree(rig.Src);

                var results = new[] { rig.Backup().Result, rig.Backup().Result };
                Assert.True(results.All(x => x == "BS_STOP_SUCCESS"), string.Join(", ", results) + " — " + Errors(rig));
                Assert.Equal(want, rig.Restored());
            }
        }

        [Fact]
        public void ADamagedChunkList_DoesNotFailEveryLaterBackup()
        {
            using (var rig = Rig())
            {
                Assert.Equal("BS_STOP_SUCCESS", rig.Backup().Result);
                var chunks = Directory.GetFiles(Path.Combine(Path.GetDirectoryName(rig.StateFile), "chunks"));
                Assert.NotEmpty(chunks);
                foreach (var c in chunks) File.WriteAllText(c, "damaged");
                var big = File.ReadAllBytes(Path.Combine(rig.Src, "big.bin")); big[100000] ^= 0x55; rig.File("big.bin", big);
                File.SetLastWriteTimeUtc(Path.Combine(rig.Src, "big.bin"), DateTime.UtcNow.AddMinutes(1));
                var want = AgentRig.Tree(rig.Src);

                var results = new[] { rig.Backup().Result, rig.Backup().Result };
                Assert.True(results.All(x => x == "BS_STOP_SUCCESS"), string.Join(", ", results) + " — " + Errors(rig));
                Assert.Equal(want, rig.Restored());
            }
        }
    }
}
