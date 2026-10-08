using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// Pilot release blocker IN-06 — a reboot of the computer: the service comes back and the runs go on. COMPONENT layer:
    /// the agent's service definitions and its service loop (AgentApp.ServiceLoop, the code the Windows service / systemd /
    /// launchd runs) against a recording stand-in server (StubServer), no backup server. Contract (tests/QA/specs.py IN-06):
    ///   input    a reboot while idle; a reboot in the middle of a backup (the process dies: its open-run note stays,
    ///            and after the restart its process id may belong to another program); the server not yet reachable at
    ///            the first round after the restart
    ///   expected the service is defined to start with the computer and to be restarted after a crash (Windows, Linux,
    ///            Mac); after the restart the dead run is reported to the server exactly once (never while the server
    ///            cannot hear it — the note is kept), the pending backup runs and completes, and the point restores
    ///            identical (SHA-256) with the files changed before the reboot sent again
    /// Oracle: the requests the stand-in server recorded, the files restored from the objects it received (SHA-256).
    /// </summary>
    public class PilotRebootComponentTests : IDisposable
    {
        public void Dispose() { ServiceSetup.ScHook = null; }

        static AgentApp Restarted(AgentRig rig) { rig.Home.SaveRegistration(rig.Server.Url, "rig", "pc-1", "device-of-the-test"); return new AgentApp(rig.Home.Dir); }

        /// <summary>The server's profile with this set and a pending "Back up now" (a slot missed while the computer was off is the same: a run is due).</summary>
        static void ServeProfile(AgentRig rig, long runRequest)
        {
            var p = Profile.Create("rig", "", "", "en", null);
            var e = rig.Set.ToXml(); e.SetAttributeValue("RUN_REQUEST", runRequest);
            p.Root.Add(e);
            rig.Server.ProfileXml = p.Doc.ToString();
        }

        /// <summary>A computer whose set has its key here and whose other periodic jobs are not due (folders sent, restore test done).</summary>
        static void Prepare(AgentRig rig)
        {
            rig.Set.KeyCheck = rig.Key.CheckValue();
            rig.Home.SaveKey(rig.Set.Id, rig.Key);
            File.WriteAllText(Path.Combine(rig.Home.Dir, "folders-sent.txt"), RunId.UnixMs(DateTime.UtcNow).ToString(System.Globalization.CultureInfo.InvariantCulture));
            File.WriteAllText(Path.Combine(rig.Home.SetDir(rig.Set.Id), "last-restore-test.txt"), RunId.From(DateTime.UtcNow.AddDays(1)));
        }

        /// <summary>One round of the service loop (what the service does after it starts); the round's end is seen in what it says.</summary>
        static List<string> OneRound(AgentApp app, Func<string, bool> roundOver)
        {
            var said = new List<string>();
            using (var stop = new CancellationTokenSource())
            {
                stop.CancelAfter(TimeSpan.FromSeconds(100));   // a guard only: a round that never ends fails below
                app.ServiceLoop(stop.Token, m => { lock (said) said.Add(m); if (roundOver(m)) stop.Cancel(); });
            }
            return said;
        }

        static bool RunEnded(string m) { return m.Contains(": BS_") || m.Contains("cannot run") || m.StartsWith("error:", StringComparison.Ordinal); }

        /// <summary>
        /// IN-06 happy: the service is created to start with Windows (start= auto, Local System) and to be restarted after a
        /// failure; the Linux unit is enabled for the boot target and restarted on failure; the Mac LaunchDaemon runs at
        /// load and is kept alive. After an idle restart the first round reports no interrupted run and the pending backup
        /// runs and completes.
        /// </summary>
        [Fact]
        public void IN06_TheServiceStartsWithTheComputer_AndAfterAnIdleRestart_ThePendingBackupCompletes_NothingReportedAsInterrupted()
        {
            var calls = new List<string>();
            ServiceSetup.ScHook = a => { calls.Add(a); return a.StartsWith("query") ? "STATE              : 4  RUNNING" : "[SC] OK"; };
            ServiceSetup.Install(Path.Combine("C:", "P", "OnlineBackup.Agent.exe"), Path.Combine("C:", "D"), "Pilot Backup", 5);
            var create = calls.Single(c => c.StartsWith("create OnlineBackupAgent", StringComparison.Ordinal));
            Assert.Contains("start= auto", create);
            Assert.Contains("obj= LocalSystem", create);
            var failure = calls.Single(c => c.StartsWith("failure OnlineBackupAgent", StringComparison.Ordinal));
            Assert.Matches("actions= restart/\\d+/restart/\\d+/restart/\\d+", failure);
            var unit = Setup.SystemdUnit("/opt/Pilot/OnlineBackup.Agent", "/var/lib/Pilot", "Pilot Backup");
            Assert.Contains("WantedBy=multi-user.target", unit);
            Assert.Contains("Restart=on-failure", unit);
            Assert.Contains("After=network-online.target", unit);
            var plist = Setup.LaunchdPlist("com.onlinebackup.pilot", "/usr/local/Pilot/OnlineBackup.Agent", "/Library/Application Support/Pilot");
            Assert.Matches("<key>RunAtLoad</key>\\s*<true/>", plist);
            Assert.Contains("<key>KeepAlive</key>", plist);

            using (var rig = new AgentRig())
            {
                rig.File("a.txt", "alpha"); rig.File(Path.Combine("docs", "b.bin"), Enumerable.Range(0, 70000).Select(i => (byte)(i * 7)).ToArray());
                Prepare(rig);
                ServeProfile(rig, RunId.UnixMs(DateTime.UtcNow));
                var said = OneRound(Restarted(rig), RunEnded);
                Assert.Contains(said, m => m == "Rig: BS_STOP_SUCCESS");
                Assert.DoesNotContain(said, m => m.Contains("interrupted"));
                Assert.Empty(rig.Server.Of("interrupted"));
                Assert.Equal(AgentRig.Tree(rig.Src), rig.Restored());
            }
        }

        /// <summary>
        /// IN-06 failure + recovery + integrity: the computer goes down in the middle of a backup (the server hears nothing
        /// more after the first file: no end of the run). After the restart the old process id belongs to another program
        /// (same number, another start time). The first round finds the server not yet reachable: nothing is reported,
        /// the note of the dead run is kept, nothing is lost. The next round reports the dead run exactly once (its run id
        /// and start), the pending backup completes, the changed and new files are sent again, and the newest point
        /// restores identical to the source (SHA-256).
        /// </summary>
        [Fact]
        public void IN06_ARebootInTheMiddleOfABackup_TheDeadRunIsReportedOnceWhenTheServerAnswers_TheNextRunCompletes_AndRestoresIdentical()
        {
            using (var rig = new AgentRig())
            {
                rig.File("a.txt", "alpha"); rig.File("b.txt", "beta"); rig.File("c.bin", Enumerable.Range(0, 50000).Select(i => (byte)(i % 251)).ToArray());
                Prepare(rig);
                Assert.Equal("BS_STOP_SUCCESS", rig.Backup().Result);
                var stateBefore = AgentRig.Sha(rig.StateFile);
                rig.File("a.txt", "alpha, changed before the reboot"); File.SetLastWriteTimeUtc(Path.Combine(rig.Src, "a.txt"), DateTime.UtcNow.AddMinutes(1));
                rig.File("d.txt", "delta, new before the reboot");
                File.Delete(Path.Combine(rig.Src, "b.txt"));
                var want = AgentRig.Tree(rig.Src);

                // the power goes: after the first file of this run the server hears nothing more
                int objects = 0; string dying = null;
                rig.Server.Handler = q =>
                {
                    if (q.Action == "begin" || dying == null) { if (q.Action == "object") { dying = q.Job; objects++; } return null; }
                    return new StubServer.Answer { Drop = true };
                };
                try { rig.Backup(); } catch (AgentException) { }   // the process would be gone: whatever it reported to itself does not matter
                Assert.NotNull(dying);
                Assert.True(File.Exists(rig.MarkerFile), "the dead run left its note");
                Assert.Equal(stateBefore, AgentRig.Sha(rig.StateFile));                              // the local index did not move
                var note = File.ReadAllText(rig.MarkerFile).Split('\t');
                Assert.Equal(dying, note[0]);
                // after the reboot that process id is another program's (here: this test process, started at another time)
                var me = Process.GetCurrentProcess();
                File.WriteAllText(rig.MarkerFile, note[0] + "\t" + note[1] + "\t" + me.Id + "\t" + RunId.UnixMs(me.StartTime.ToUniversalTime().AddHours(-3)));

                // the first round after the restart: the network is not up yet
                rig.Server.Handler = q => new StubServer.Answer { Drop = true };
                ServeProfile(rig, RunId.UnixMs(DateTime.UtcNow));
                var app = Restarted(rig);
                var said1 = OneRound(app, m => m.StartsWith("waiting:", StringComparison.Ordinal) || RunEnded(m));
                Assert.Contains(said1, m => m.StartsWith("waiting:", StringComparison.Ordinal));
                Assert.True(File.Exists(rig.MarkerFile), "the note of the dead run is kept until the server hears it");
                Assert.DoesNotContain(rig.Server.Requests, q => q.Action == "interrupted" && q.Answered == 200);

                // the next round: the server answers
                rig.Server.Handler = q => null;
                var said2 = OneRound(app, RunEnded);
                Assert.Contains(said2, m => m.Contains("the previous backup was interrupted"));
                var reported = Assert.Single(rig.Server.Requests, q => q.Action == "interrupted" && q.Answered == 200);
                Assert.Equal(dying, reported.Msg["job"]);
                Assert.Equal(note[1], reported.Msg["started"]);
                Assert.False(File.Exists(rig.MarkerFile));
                Assert.Contains(said2, m => m == "Rig: BS_STOP_SUCCESS");
                var next = rig.Server.CommittedJobs.Last();
                Assert.NotEqual(dying, next);
                Assert.DoesNotContain(dying, rig.Server.CommittedJobs);
                var sentAgain = rig.Server.Of("object").Where(q => q.Job == next && q.Answered == 200).Select(q => { using (var s = new MemoryStream(q.Body)) return Path.GetFileName(BackupObject.ReadHeader(s, rig.Key)["path"]); }).OrderBy(x => x).ToArray();
                Assert.Equal(new[] { "a.txt", "d.txt" }, sentAgain);
                Assert.Equal(want, rig.Restored());

                // and once only: a later round (another backup) reports nothing more
                ServeProfile(rig, RunId.UnixMs(DateTime.UtcNow) + 60000);
                var said3 = OneRound(app, RunEnded);
                Assert.Contains(said3, m => m == "Rig: BS_STOP_SUCCESS");
                Assert.DoesNotContain(said3, m => m.Contains("interrupted"));
                Assert.Single(rig.Server.Requests, q => q.Action == "interrupted");
            }
        }
    }
}
