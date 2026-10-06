using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// The agent's side of a run's life (open-run note, report of a dead run, heartbeat) — BackupRun and
    /// AgentApp.ReportInterrupted alone against a recording stand-in server (StubServer). Component contract
    /// (tests/QA/specs.py AG-04):
    ///   purpose  keep the server informed while a run is alive, and tell it about a run that died
    ///   input    a normal run; a run whose end (commit and abort) never reaches the server; the next start; a note of a
    ///            run still alive in another process; a damaged note; a server that cannot be reached at the report; one
    ///            upload the server answers only after 65 s
    ///   expected while a run is open the note open-run.txt holds its run id, start time and process; a confirmed end
    ///            removes it; a lost end leaves it and the local index does not move (the changed files are sent again
    ///            next time and the point restores identical, SHA-256); the next start reports that run exactly once
    ///            (POST interrupted with its id and start time) and removes the note; a note of a live process is not
    ///            reported; a damaged note is reported and removed (never blocks); an unreachable server keeps the note for
    ///            later; a sign of life (progress of this run) reaches the server within 60 s even while one upload is silent
    /// Oracle: the note on disk, the requests the stand-in server received, files restored from the objects it received.
    /// </summary>
    public class OpenRunComponentTests
    {
        static AgentApp Registered(AgentRig rig)
        {
            rig.Home.SaveRegistration(rig.Server.Url, "rig", "pc-1", "device-of-the-test");
            return new AgentApp(rig.Home.Dir);
        }

        [Fact]
        public void WhileARunIsOpen_TheNoteHoldsIt_AConfirmedEndRemovesIt()
        {
            using (var rig = new AgentRig())
            {
                rig.File("a.txt", "alpha"); rig.File("b.txt", "beta");
                string seen = null;
                rig.Server.OnRequest = q => { if (q.Action == "object" && seen == null && File.Exists(rig.MarkerFile)) seen = File.ReadAllText(rig.MarkerFile); };
                var before = DateTime.UtcNow.AddSeconds(-1);
                var r = rig.Backup();
                Assert.Equal("BS_STOP_SUCCESS", r.Result);
                Assert.NotNull(seen);
                var f = seen.Split('\t');
                Assert.Equal(4, f.Length);
                Assert.Equal(r.Job, f[0]);
                Assert.InRange(RunId.FromUnixMs(long.Parse(f[1])), before, DateTime.UtcNow);
                Assert.Equal(Process.GetCurrentProcess().Id.ToString(), f[2]);
                Assert.False(File.Exists(rig.MarkerFile));
            }
        }

        [Fact]
        public void ALostEnd_LeavesTheNote_TheIndexDoesNotMove_TheNextStartReportsItOnce_AndTheFilesAreSentAgain()
        {
            using (var rig = new AgentRig())
            {
                rig.File("a.txt", "alpha"); rig.File("b.txt", "beta");
                Assert.Equal("BS_STOP_SUCCESS", rig.Backup().Result);
                var stateBefore = AgentRig.Sha(rig.StateFile);
                rig.File("a.txt", "alpha, changed"); File.SetLastWriteTimeUtc(Path.Combine(rig.Src, "a.txt"), DateTime.UtcNow.AddMinutes(1));
                rig.File("c.txt", "gamma, new");
                var want = AgentRig.Tree(rig.Src);

                rig.Server.Handler = q => q.Action == "commit" || q.Action == "abort" ? new StubServer.Answer { Drop = true } : null;   // the line breaks at the end
                var lost = rig.Backup();
                Assert.Equal("BS_STOP_BY_SYSTEM_ERROR", lost.Result);
                Assert.True(File.Exists(rig.MarkerFile));
                var note = File.ReadAllText(rig.MarkerFile).Split('\t');
                Assert.Equal(lost.Job, note[0]);
                Assert.Equal(stateBefore, AgentRig.Sha(rig.StateFile));            // the local index did not move

                rig.Server.Handler = q => null;
                var app = Registered(rig);
                Assert.True(app.ReportInterrupted(rig.Set.Id));
                Assert.False(app.ReportInterrupted(rig.Set.Id));                     // once
                var reported = Assert.Single(rig.Server.Of("interrupted"));
                Assert.Equal("/api/sets/" + rig.Set.Id + "/interrupted", reported.Path);
                Assert.Equal(lost.Job, reported.Msg["job"]);
                Assert.Equal(note[1], reported.Msg["started"]);
                Assert.False(File.Exists(rig.MarkerFile));

                var next = rig.Backup();
                Assert.Equal("BS_STOP_SUCCESS", next.Result);
                var sentAgain = rig.Server.Of("object").Where(q => q.Job == next.Job).Select(q => { using (var s = new MemoryStream(q.Body)) return Path.GetFileName(BackupObject.ReadHeader(s, rig.Key)["path"]); }).OrderBy(x => x).ToArray();
                Assert.Equal(new[] { "a.txt", "c.txt" }, sentAgain);
                Assert.Equal(want, rig.Restored());
            }
        }

        [Fact]
        public void ANoteOfALiveProcess_IsNotReported_ADamagedNoteIsReportedAndRemoved_AnUnreachableServerKeepsTheNote()
        {
            using (var rig = new AgentRig())
            {
                var app = Registered(rig);
                Directory.CreateDirectory(Path.GetDirectoryName(rig.MarkerFile));
                var live = Process.Start(new ProcessStartInfo("sleep", "60") { UseShellExecute = false });
                try
                {
                    File.WriteAllText(rig.MarkerFile, "2026-10-06-09-00-00\t" + RunId.UnixMs(DateTime.UtcNow) + "\t" + live.Id + "\t" + RunId.UnixMs(live.StartTime.ToUniversalTime()));
                    Assert.False(app.ReportInterrupted(rig.Set.Id));
                    Assert.Empty(rig.Server.Of("interrupted"));
                    Assert.True(File.Exists(rig.MarkerFile));
                }
                finally { live.Kill(); live.WaitForExit(); }
                Assert.True(app.ReportInterrupted(rig.Set.Id));                       // the same note once that process is gone
                Assert.Equal("2026-10-06-09-00-00", Assert.Single(rig.Server.Of("interrupted")).Msg["job"]);

                File.WriteAllText(rig.MarkerFile, "#damaged");
                Assert.True(app.ReportInterrupted(rig.Set.Id));
                Assert.False(File.Exists(rig.MarkerFile));

                File.WriteAllText(rig.MarkerFile, "2026-10-06-09-30-00\t1\t999999\t1");
                rig.Server.Handler = q => q.Action == "interrupted" ? new StubServer.Answer { Drop = true } : null;
                Assert.Throws<AgentException>(() => app.ReportInterrupted(rig.Set.Id));
                Assert.True(File.Exists(rig.MarkerFile));                             // kept for later
                rig.Server.Handler = q => null;
                Assert.True(app.ReportInterrupted(rig.Set.Id));
                Assert.Equal("2026-10-06-09-30-00", rig.Server.Of("interrupted").Last().Msg["job"]);
                Assert.False(File.Exists(rig.MarkerFile));
            }
        }

        [Fact]
        public void ASilentUpload_StillSendsASignOfLifeWithinAMinute()
        {
            using (var rig = new AgentRig())
            {
                rig.File("one.bin", new byte[50000]);
                DateTime held = DateTime.MaxValue;
                rig.Server.Handler = q =>
                {
                    if (q.Action != "object") return null;
                    held = q.At;
                    using (var h = System.Security.Cryptography.SHA256.Create())
                        return new StubServer.Answer { DelayMs = 65000, Body = new Msg().Set("sha256", Bytes.Hex(h.ComputeHash(q.Body))).Set("size", q.Body.Length) };
                };
                var r = rig.Backup();
                Assert.Equal("BS_STOP_SUCCESS", r.Result);
                var during = rig.Server.Of("progress").Where(p => p.Msg["job"] == r.Job && p.At > held.AddSeconds(5)).ToList();
                Assert.NotEmpty(during);
                Assert.True(during.Min(p => p.At) <= held.AddSeconds(62), "first sign of life " + (during.Min(p => p.At) - held).TotalSeconds + " s after the upload began");
            }
        }
    }
}
