using System;
using System.IO;
using System.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// "Back up now" and "Stop" from the admin site, on the computer — AgentApp.RunRequested / StopCheck and BackupRun alone
    /// against a recording stand-in server that serves the profile (StubServer). Component contract (tests/QA/specs.py AG-02):
    ///   purpose  the admin's "Back up now" starts one run, and "Stop" ends the running one as stopped, safely
    ///   input    request times newer / equal / older than the last handled, and a damaged note of it; a stop newer than the
    ///            run's start arriving after 2 of 5 changed files; a stop older than the start; a server that answers with an
    ///            error or not at all while a run asks; a request whose backup cannot start at that moment
    ///   expected each new request starts exactly one run, an old or repeated one none (a damaged note costs at most one
    ///            extra run); the stopped run is BS_STOP_BY_USER (also in the commit), sent exactly the 2 files, no deletion,
    ///            the local index keeps the old entries of the others; the next run completes and the point restores
    ///            identical (SHA-256); an older stop or an unreachable server never stops a run; a request whose backup could
    ///            not start is still pending afterwards (it runs at the next chance)
    /// Oracle: the requests the stand-in server received and the files restored from the objects it received.
    /// </summary>
    public class RunRequestComponentTests
    {
        static AgentApp Registered(AgentRig rig) { rig.Home.SaveRegistration(rig.Server.Url, "rig", "pc-1", "device-of-the-test"); return new AgentApp(rig.Home.Dir); }

        static void ServeProfile(AgentRig rig, long runRequest, long stopRequest)
        {
            var p = Profile.Create("rig", "", "", "en", null);
            var e = rig.Set.ToXml(); e.SetAttributeValue("RUN_REQUEST", runRequest); e.SetAttributeValue("STOP_REQUEST", stopRequest);
            p.Root.Add(e);
            rig.Server.ProfileXml = p.Doc.ToString();
        }

        static BackupSetInfo WithRequest(AgentRig rig, long runRequest) { var s = BackupSetInfo.FromXml(rig.Set.ToXml()); s.RunRequest = runRequest; return s; }

        [Fact]
        public void EachNewRequest_StartsExactlyOneRun_AnOldOrRepeatedOneNone()
        {
            using (var rig = new AgentRig())
            {
                var app = Registered(rig);
                Assert.False(app.RunRequested(WithRequest(rig, 0)));                  // never pressed
                Assert.True(app.RunRequested(WithRequest(rig, 1759740000000)));
                Assert.False(app.RunRequested(WithRequest(rig, 1759740000000)));      // the same press again
                Assert.False(app.RunRequested(WithRequest(rig, 1759739999999)));      // older
                Assert.True(app.RunRequested(WithRequest(rig, 1759740060000)));       // pressed again
                File.WriteAllText(Path.Combine(rig.Home.SetDir(rig.Set.Id), "run-request.txt"), "\0\0garbage");
                Assert.True(app.RunRequested(WithRequest(rig, 1759740060000)));       // a damaged note: one extra run at most
                Assert.False(app.RunRequested(WithRequest(rig, 1759740060000)));
            }
        }

        [Fact]
        public void AStopNewerThanTheStart_EndsTheRunAsStopped_NothingDeleted_TheNextRunCompletes_AndRestoresIdentical()
        {
            using (var rig = new AgentRig())
            {
                var app = Registered(rig);
                for (int i = 1; i <= 5; i++) rig.File("f" + i + ".txt", "version 1 of " + i);
                rig.File("old.txt", "will be deleted");
                ServeProfile(rig, 0, 0);
                Assert.Equal("BS_STOP_SUCCESS", rig.Backup().Result);

                var now = DateTime.UtcNow; SystemClock.Use(() => now);
                try
                {
                    for (int i = 1; i <= 5; i++) { var f = rig.File("f" + i + ".txt", "version 2 of " + i + " — longer"); File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddMinutes(1)); }
                    File.Delete(Path.Combine(rig.Src, "old.txt"));
                    var want = AgentRig.Tree(rig.Src);
                    var started = now;
                    int objects = 0;
                    rig.Server.OnRequest = q =>
                    {
                        if (q.Action == "object" && ++objects == 2) { ServeProfile(rig, 0, RunId.UnixMs(started.AddSeconds(5))); now = now.AddSeconds(21); }   // Stop pressed; the check is due
                    };
                    var stopped = rig.Backup(app.StopCheck(rig.Set, started));
                    Assert.Equal("BS_STOP_BY_USER", stopped.Result);
                    Assert.Equal("BS_STOP_BY_USER", rig.Server.Of("commit").Single(c => c.Job == stopped.Job).Msg["result"]);
                    Assert.Equal(2, rig.Server.Of("object").Count(q => q.Job == stopped.Job));
                    Assert.DoesNotContain(rig.Server.Requests, q => q.Job == stopped.Job && q.Action == "delete");
                    var index = File.ReadAllLines(rig.StateFile).Where(l => l.Length > 0 && !l.StartsWith("#")).ToList();
                    Assert.Equal(6, index.Count);                                      // the 3 not reached and old.txt kept as they were

                    rig.Server.OnRequest = q => { };
                    now = now.AddMinutes(5);
                    var next = rig.Backup(app.StopCheck(rig.Set, now));                // the stop is older than this run
                    Assert.Equal("BS_STOP_SUCCESS", next.Result);
                    Assert.Equal(3, rig.Server.Of("object").Count(q => q.Job == next.Job));
                    Assert.Single(rig.Server.Requests, q => q.Job == next.Job && q.Action == "delete");
                    Assert.Equal(want, rig.Restored());
                }
                finally { SystemClock.Use(null); }
            }
        }

        [Fact]
        public void AnOlderStop_OrAServerThatFails_NeverStopsARun()
        {
            using (var rig = new AgentRig())
            {
                var app = Registered(rig);
                var now = DateTime.UtcNow; SystemClock.Use(() => now);
                try
                {
                    ServeProfile(rig, 0, RunId.UnixMs(now.AddMinutes(-1)));
                    var check = app.StopCheck(rig.Set, now);
                    Assert.False(check());
                    rig.Server.Handler = q => q.Action == "profile" ? StubServer.Answer.Error(503, "HTTP", "Service Unavailable") : null;
                    now = now.AddSeconds(21); Assert.False(check());
                    rig.Server.Handler = q => q.Action == "profile" ? new StubServer.Answer { Drop = true } : null;
                    now = now.AddSeconds(21); Assert.False(check());
                    Assert.True(rig.Server.Of("profile").Count >= 3);                  // it did ask each time
                }
                finally { SystemClock.Use(null); }
            }
        }

        [Fact]
        public void ARequestWhoseBackupCouldNotStart_IsNotLost()
        {
            using (var rig = new AgentRig())
            {
                var app = Registered(rig);
                rig.File("a.txt", "alpha");
                ServeProfile(rig, 1759740000000, 0);
                var s = app.Profile().Sets.Single();
                // AgentApp.ServiceLoop: "if (RunRequested(s) || Due(s, now)) Backup(s.Id)" — here the backup cannot start: the
                // server fails the profile call that Backup makes first (a moment of trouble on the line or the server)
                Assert.True(app.RunRequested(s));
                rig.Server.Handler = q => q.Action == "profile" ? StubServer.Answer.Error(503, "HTTP", "Service Unavailable") : null;
                Assert.Throws<AgentException>(() => app.Backup(s.Id));
                Assert.Empty(rig.Server.Of("begin"));                                 // no run began
                rig.Server.Handler = q => null;
                // the next round of the loop: the press of "Back up now" must still be pending
                Assert.True(app.RunRequested(app.Profile().Sets.Single()), "the admin's \"Back up now\" was marked done although no backup ran");
            }
        }
    }
}
