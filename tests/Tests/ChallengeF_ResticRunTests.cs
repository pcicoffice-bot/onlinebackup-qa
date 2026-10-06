using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// QA Agent F (static review challenger): the restic run of the agent (ResticRunner.Backup) against a real server, with
    /// a stand-in restic program (a shell script set per runner through AgentApp.ResticSetup — no global environment change).
    /// </summary>
    public class ChallengeF_ResticRunTests
    {
        static bool Unix { get { return Environment.OSVersion.Platform == PlatformID.Unix; } }

        static string Script(string dir, string name, string body)
        {
            var f = Path.Combine(dir, name); File.WriteAllText(f, "#!/bin/sh\n" + body + "\n");
            Process.Start("chmod", "+x \"" + f + "\"").WaitForExit(10000); return f;
        }

        /// <summary>A restic that works: every command succeeds; "backup" prints a summary; each call is written to calls.txt.</summary>
        static string FakeRestic(string dir)
        {
            var calls = Path.Combine(dir, "calls.txt");
            return Script(dir, "restic",
                "echo \"$1 $2\" >> '" + calls + "'\n" +
                "if [ \"$1\" = backup ]; then echo '{\"message_type\":\"summary\",\"files_new\":1,\"files_changed\":0,\"files_unmodified\":0,\"total_files_processed\":1,\"total_bytes_processed\":10,\"data_added\":10,\"snapshot_id\":\"0123abcd\"}'; fi\n" +
                "exit 0");
        }

        static string RunningFlag(AgentApp app, string setId) { return Path.Combine(app.Home.SetDir(setId), "restic-running.txt"); }

        /// <summary>
        /// Hits 63d4f23a16 / 05ba46d163 (ResticRunner.UnlockAfterDeadRun / RecoverInterrupted), triaged OK: "an unreadable pid
        /// counts as not alive". The run flag holds only a process id. The run is taken as alive when ANY process has that id
        /// — after a reboot the id belongs to another program (ids are reused), and a run that ended with an error leaves its
        /// flag (it is deleted only after a clean prune) while the service process that wrote it lives on. Then every later
        /// run of the set from another process is refused as "still working" for as long as that other process lives.
        /// The open-run note (BackupRun.WriteMarker / AgentApp.ReportInterrupted) already guards against this with the
        /// process start time; the restic run flag does not.
        /// Oracle: the server's history (a refused run is recorded "bad") and the stand-in restic's call list (backup never ran).
        /// </summary>
        [Fact]
        public void RunFlagOfAnotherLiveProgram_DoesNotBlockTheBackup()
        {
            if (!Unix) return;
            using (var env = new Env())
            {
                var src = env.Dir("src"); File.WriteAllText(Path.Combine(src, "a.txt"), "a");
                env.CreateUser("pidre", "Customer-Pass-1");
                var app = env.Agent("pidre", "Customer-Pass-1");
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), null, new BackupSetInfo { Name = "Pid", Engine = "RESTIC", Sources = { src } });
                var bin = env.Dir("bin"); var exe = FakeRestic(bin);
                app.ResticSetup = r => { r.ExePath = exe; };

                // control: without a flag the backup runs and succeeds (the stand-in and the oracle work)
                var first = app.Backup(set.Id);
                Assert.StartsWith("BS_STOP_SUCCESS", first.Result);

                // a program that is not the agent (here: sleep) now has the process id written in the run flag
                var other = Process.Start(new ProcessStartInfo("sleep", "120") { UseShellExecute = false });
                try
                {
                    File.WriteAllText(RunningFlag(app, set.Id), other.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    File.Delete(Path.Combine(bin, "calls.txt"));
                    var r = app.Backup(set.Id);
                    var calls = File.Exists(Path.Combine(bin, "calls.txt")) ? File.ReadAllText(Path.Combine(bin, "calls.txt")) : "";
                    Assert.True(r.Result.StartsWith("BS_STOP_SUCCESS", StringComparison.Ordinal),
                        "a run flag naming another program (pid " + other.Id + ") blocked the backup: " + r.Result + "\n" + string.Join("\n", r.LogLines.Where(l => l.Contains("err"))) + "\nrestic calls: " + calls);
                    Assert.Contains("backup", calls);
                }
                finally { try { other.Kill(); } catch (Exception) { } }
            }
        }

        /// <summary>
        /// Hit 677e900db7 (ResticRunner.cs finally: StopHeartbeat … post-commands) and the sweep 0b217f5a5a, triaged OK.
        /// The restic run stops its heartbeat and THEN runs the post-commands (allowed up to Limits.Command = 1 hour), and
        /// only then reports. The native engine runs them after its commit. A restic post-command longer than the lease
        /// (5 minutes): the server hears nothing, its sweep records the run as interrupted (a failure, a mail, a service
        /// call) — and the real success that follows is filed as a different run. The history shows a failed backup that
        /// never failed.
        /// Real time, real agent and server (its own minute sweep timer); the lease is shortened to 100 s for the test (static,
        /// restored after; the test collections do not run in parallel), the command runs 130 s. Control: the same command
        /// as a PRE-command — the heartbeat (every 60 s) is running then, and the run must not be closed.
        /// </summary>
        [Theory]
        [InlineData("pre")]    // control: the heartbeat runs — must pass
        [InlineData("post")]   // the heartbeat is already stopped
        public void ResticCommandLongerThanTheLease_IsNotRecordedAsAnInterruptedBackup(string kind)
        {
            if (!Unix) return;
            var lease = SetStore.Lease;
            SetStore.Lease = TimeSpan.FromSeconds(100);
            try
            {
                using (var env = new Env())
                {
                    var src = env.Dir("src"); File.WriteAllText(Path.Combine(src, "a.txt"), "a");
                    env.CreateUser("postcmd", "Customer-Pass-1");
                    var app = env.Agent("postcmd", "Customer-Pass-1");
                    var mark = Path.Combine(env.Root, "command-started");
                    var cmd = new List<string> { "touch '" + mark + "'; sleep 130" };
                    var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), null, new BackupSetInfo
                    { Name = "Post", Engine = "RESTIC", Sources = { src }, PreCommands = kind == "pre" ? cmd : new List<string>(), PostCommands = kind == "post" ? cmd : new List<string>() });
                    var exe = FakeRestic(env.Dir("bin"));
                    app.ResticSetup = r => { r.ExePath = exe; };

                    var run = Task.Run(() => app.Backup(set.Id));
                    var until = DateTime.UtcNow.AddSeconds(60);
                    while (!File.Exists(mark) && DateTime.UtcNow < until && !run.IsCompleted) Thread.Sleep(100);
                    Assert.True(File.Exists(mark), "the command did not start");
                    Thread.Sleep(120000);                                    // the command runs on (the server's sweep runs every minute)
                    env.Api.SweepInterrupted(SystemClock.UtcNow);            // and once more now, at 120 s (> the 100 s lease)
                    Assert.True(run.Wait(120000), "the backup did not end");
                    var r = run.Result;
                    Assert.StartsWith("BS_STOP_SUCCESS", r.Result);
                    var rows = env.Api.Runs.Since(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1)).Where(m => m["set"] == set.Id).ToList();
                    Assert.True(!rows.Any(m => m["status"] == "bad"),
                        "a successful backup was recorded as failed: " + string.Join("; ", rows.Select(m => m["job"] + "=" + m["status"])));
                }
            }
            finally { SetStore.Lease = lease; }
        }
    }
}
