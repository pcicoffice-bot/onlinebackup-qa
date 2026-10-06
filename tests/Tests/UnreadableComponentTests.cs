using System;
using System.IO;
using System.Linq;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// Unreadable data during a backup run — the agent's BackupRun alone against a recording stand-in server (StubServer, not
    /// the product's server). Component contract (tests/QA/specs.py BK-05):
    ///   purpose  a file or folder that cannot be read is an error, never a silent success and never a deletion
    ///   input    run 1 of a.txt, b.txt, locked.txt; then locked.txt changed and held with an exclusive lock by another
    ///            handle, c.txt new; then the lock released. Separately: every source gone; one of two sources gone
    ///   expected the locked run is BS_STOP_SUCCESS_WITH_ERROR (in the commit sent to the server), an err line names the
    ///            file, no deletion is sent, no well-formed object of it is sent, the newest point restores a, b, c identical
    ///            and locked.txt in its previous version (SHA-256); after the lock is released the next run is BS_STOP_SUCCESS
    ///            and the point restores its new version. Every source gone → BS_STOP_BY_SYSTEM_ERROR, no deletion, the
    ///            local index keeps every file, and once the source is back the run is BS_STOP_SUCCESS with nothing re-sent
    /// Oracle: the requests the stand-in server received and the SHA-256 of files restored from the objects it received.
    /// </summary>
    public class UnreadableComponentTests
    {
        static int StateLines(AgentRig rig) { return File.ReadAllLines(rig.StateFile).Count(l => l.Length > 0 && !l.StartsWith("#")); }

        [Fact]
        public void ALockedFile_IsAnError_NotADeletion_TheOthersRestoreIdentical_AndItIsSentOnceFree()
        {
            using (var rig = new AgentRig())
            {
                rig.File("a.txt", "alpha"); rig.File("b.txt", "beta");
                var locked = rig.File("locked.txt", "locked v1"); var v1 = AgentRig.Sha(locked);
                Assert.Equal("BS_STOP_SUCCESS", rig.Backup().Result);

                File.WriteAllText(locked, "locked v2 — changed while another program holds it"); File.SetLastWriteTimeUtc(locked, DateTime.UtcNow.AddMinutes(1));
                var v2 = AgentRig.Sha(locked);
                rig.File("c.txt", "gamma");
                var want = AgentRig.Tree(rig.Src);
                string run2;
                using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))   // another program's exclusive lock
                {
                    var r = rig.Backup(); run2 = r.Job;
                    Assert.Equal("BS_STOP_SUCCESS_WITH_ERROR", r.Result);
                }
                var commit = rig.Server.Of("commit").Single(c => c.Job == run2).Msg;
                Assert.Equal("BS_STOP_SUCCESS_WITH_ERROR", commit["result"]);
                Assert.Contains(commit.List("log"), l => AhsayLog.Fields(l["l"]).Length > 2 && AhsayLog.Fields(l["l"])[1] == "err" && l["l"].Contains("locked.txt"));
                Assert.DoesNotContain(rig.Server.Requests, q => q.Job == run2 && q.Action == "delete");
                Assert.DoesNotContain(rig.Server.Of("object"), q => q.Job == run2 && StubServer.WellFormed(q.Body, rig.Key) && RelOf(rig, q) == "locked.txt");

                var got = rig.Restored("r2");
                foreach (var f in new[] { "a.txt", "b.txt", "c.txt" }) Assert.Equal(want[f], got[f]);
                Assert.Equal(v1, got["locked.txt"]);                                   // the earlier version is kept, not deleted

                var r3 = rig.Backup();
                Assert.Equal("BS_STOP_SUCCESS", r3.Result);
                Assert.Contains(rig.Server.Of("object"), q => q.Job == r3.Job && StubServer.WellFormed(q.Body, rig.Key) && RelOf(rig, q) == "locked.txt");
                Assert.Equal(v2, rig.Restored("r3")["locked.txt"]);
            }
        }

        /// <summary>The file name of an object the server received (decrypted from its header, as a restore would).</summary>
        static string RelOf(AgentRig rig, StubServer.Req q)
        {
            using (var s = new MemoryStream(q.Body)) return Path.GetFileName(BackupObject.ReadHeader(s, rig.Key)["path"]);
        }

        [Fact]
        public void EverySourceGone_IsAFailure_NothingDeleted_TheIndexKeepsEveryFile_AndTheSourceComesBackWithoutResending()
        {
            using (var rig = new AgentRig())
            {
                rig.File("a.txt", "alpha"); rig.File(Path.Combine("sub", "b.txt"), "beta"); rig.File(Path.Combine("sub", "deep", "c.bin"), new byte[70000]);
                var want = AgentRig.Tree(rig.Src);
                Assert.Equal("BS_STOP_SUCCESS", rig.Backup().Result);
                Assert.Equal(3, StateLines(rig));
                var stateBefore = File.ReadAllText(rig.StateFile);

                var away = rig.Src + ".offline"; Directory.Move(rig.Src, away);           // the drive or share is offline
                var r2 = rig.Backup();
                Assert.Equal("BS_STOP_BY_SYSTEM_ERROR", r2.Result);
                Assert.DoesNotContain(rig.Server.Requests, q => q.Job == r2.Job && q.Action == "delete");
                Assert.Contains(r2.LogLines, l => l.Contains("Nothing was backed up"));
                var sent = rig.Server.Requests.Where(q => q.Job == r2.Job && (q.Action == "commit" || q.Action == "abort")).Select(q => q.Msg["result"]).ToList();
                Assert.Equal(new[] { "BS_STOP_BY_SYSTEM_ERROR" }, sent);               // the server is told it failed, once
                Assert.Equal(3, StateLines(rig));                                       // the index keeps every file
                Assert.Equal(want, rig.Restored("r2"));                                 // and the point still restores them all

                Directory.Move(away, rig.Src);
                var r3 = rig.Backup();
                Assert.Equal("BS_STOP_SUCCESS", r3.Result);
                Assert.DoesNotContain(rig.Server.Of("object"), q => q.Job == r3.Job);  // nothing sent again
                Assert.Equal(want, rig.Restored("r3"));
                Assert.Equal(stateBefore.Split('\n').Skip(1).OrderBy(x => x), File.ReadAllText(rig.StateFile).Split('\n').Skip(1).OrderBy(x => x));
            }
        }

        [Fact]
        public void OneOfTwoSourcesGone_IsSuccessWithError_NamesIt_ItsFilesAreNotDeleted()
        {
            var second = Path.Combine(Path.GetTempPath(), "obrig2-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(second); File.WriteAllText(Path.Combine(second, "x.txt"), "x");
            using (var rig = new AgentRig(second))
            {
                rig.File("a.txt", "alpha");
                Assert.Equal("BS_STOP_SUCCESS", rig.Backup().Result);
                Directory.Delete(second, true);
                var r2 = rig.Backup();
                Assert.Equal("BS_STOP_SUCCESS_WITH_ERROR", r2.Result);
                Assert.Contains(r2.LogLines, l => AhsayLog.Fields(l)[1] == "err" && l.Contains(second));
                Assert.DoesNotContain(rig.Server.Requests, q => q.Job == r2.Job && q.Action == "delete");
                Assert.Equal(2, File.ReadAllLines(rig.StateFile).Count(l => l.Length > 0 && !l.StartsWith("#")));
            }
        }
    }
}
