using System;
using System.IO;
using System.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// Network trouble in a real backup run — BackupRun and Client alone against the recording stand-in server (StubServer),
    /// which can lose an answer on the line (the status line arrives, the body is cut). Component contract (tests/QA/specs.py
    /// AG-05):
    ///   purpose  survive network trouble
    ///   input    a request whose answer is lost (one object upload); the server gone in the middle of a run
    ///   expected the run resumes (the object is sent again) or ends as failed with a clear reason; a repeated request is not
    ///            counted twice; nothing half is committed and the local index does not move; the next run completes and
    ///            the newest point restores identical (SHA-256)
    /// Oracle: the requests the stand-in server received, and the files restored from the objects it stored, against the
    /// SHA-256 of the source files.
    /// </summary>
    public class PilotAgentNetworkComponentTests
    {
        static void Fill(AgentRig rig)
        {
            rig.File("a.txt", "alpha");
            rig.File(Path.Combine("sub", "b.txt"), "beta");
            var big = new byte[1536 * 1024]; new Random(11).NextBytes(big); rig.File("big.bin", big);
            rig.File("empty.txt", "");
            rig.File("e.txt", "epsilon");
        }

        [Fact]
        public void AnObjectWhoseAnswerIsLost_IsSentAgain_CountedOnce_TheRunCompletes_AndRestoresIdentical()
        {
            using (var rig = new AgentRig())
            {
                Fill(rig);
                var want = AgentRig.Tree(rig.Src);
                int puts = 0;
                rig.Server.Handler = q => q.Action == "object" && System.Threading.Interlocked.Increment(ref puts) == 2 ? new StubServer.Answer { Drop = true } : null;

                var r = rig.Backup();
                Assert.True(r.Result == "BS_STOP_SUCCESS" || r.Result == "BS_STOP_SUCCESS_WITH_WARNING", r.Result + "\n" + string.Join("\n", r.LogLines));
                var objs = rig.Server.Of("object").Where(q => q.Job == r.Job).ToList();
                Assert.Equal(6, objs.Count);                                                             // 5 files, one of them twice
                var lost = objs.Single(q => q.Answered == -1);
                Assert.Equal(2, objs.Count(q => q.Query["rel"] == lost.Query["rel"] && q.Query["seq"] == lost.Query["seq"]));
                Assert.Equal(5, r.New);                                                                  // the repeat is not counted twice
                var commit = rig.Server.Of("commit").Single(q => q.Job == r.Job);
                Assert.Equal("5", commit.Msg["new"]);
                Assert.Equal(want, rig.Restored());
            }
        }

        [Fact]
        public void TheServerGoneMidRun_EndsAsAFailureWithTheReason_NothingCommitted_TheIndexDoesNotMove_TheNextRunCompletesAndRestoresIdentical()
        {
            using (var rig = new AgentRig())
            {
                rig.File("a.txt", "alpha 1"); rig.File("b.txt", "beta 1");
                Assert.Equal("BS_STOP_SUCCESS", rig.Backup().Result);
                var index = File.ReadAllBytes(rig.StateFile);

                Fill(rig);   // a.txt changed (another size), sub/b.txt, big.bin, empty.txt, e.txt new; b.txt unchanged
                var want = AgentRig.Tree(rig.Src);

                bool down = false; int objects = 0;
                rig.Server.Handler = q =>
                {
                    if (q.Action == "object" && System.Threading.Interlocked.Increment(ref objects) == 2) down = true;
                    return down ? new StubServer.Answer { Drop = true } : null;   // from the second object on, nothing comes back
                };
                var run = new BackupRun(rig.NewClient(), rig.Home, rig.Set, rig.Key);
                string reason;
                try { run.Run(); reason = run.Result + "\n" + string.Join("\n", run.LogLines); }
                catch (AgentException e) { reason = e.Code + " " + e.Message; Assert.Equal("NETWORK", e.Code); }
                Assert.False((run.Result ?? "").StartsWith("BS_STOP_SUCCESS", StringComparison.Ordinal), "a run cut off by the network was reported as a success: " + reason);
                Assert.Contains("connection", reason, StringComparison.OrdinalIgnoreCase);              // a clear reason
                Assert.DoesNotContain(rig.Server.Of("commit"), q => q.Job == run.Job && q.Answered == 200);
                Assert.Equal(index, File.ReadAllBytes(rig.StateFile));                                  // the index did not move

                down = false; rig.Server.Handler = q => null;
                var next = rig.Backup();
                Assert.True(next.Result == "BS_STOP_SUCCESS", next.Result + "\n" + string.Join("\n", next.LogLines));
                Assert.Equal(want, rig.Restored());
            }
        }
    }
}
