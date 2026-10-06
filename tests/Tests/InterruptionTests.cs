using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// R1 (GPT audit 1–3, found by the Linux E2E run): a backup whose agent process dies in the middle (power cut, reboot,
    /// killed) must not block the set, must not stay "Running", and must appear in the history as a failed run.
    /// The agent is a real separate process (the same program the Windows service runs) and is really killed.
    /// </summary>
    public class InterruptionTests
    {
        static string AgentDll { get { return Path.Combine(AppContext.BaseDirectory, "OnlineBackup.Agent.dll"); } }

        /// <summary>Starts "OnlineBackup.Agent backup --home H --set S" as its own process.</summary>
        public static Process StartAgent(string home, string verb, params string[] more)
        {
            var args = new List<string> { "exec", "--runtimeconfig", Path.Combine(AppContext.BaseDirectory, "OnlineBackup.Agent.runtimeconfig.json"), AgentDll, verb, "--home", home };
            args.AddRange(more);
            var psi = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            var p = Process.Start(psi);
            p.OutputDataReceived += (s, e) => { }; p.ErrorDataReceived += (s, e) => { };
            p.BeginOutputReadLine(); p.BeginErrorReadLine();
            return p;
        }

        static void Rnd(string path, long size, int seed)
        {
            var r = new Random(seed); var b = new byte[1 << 16];
            using (var f = File.Create(path)) for (long n = 0; n < size; n += b.Length) { r.NextBytes(b); f.Write(b, 0, (int)Math.Min(b.Length, size - n)); }
        }
        static string Sha(string f) { using (var s = File.OpenRead(f)) using (var h = SHA256.Create()) return Convert.ToHexString(h.ComputeHash(s)); }

        /// <summary>A native set whose upload is slowed to 300 KB/s, so a run can be caught and killed in the middle.</summary>
        static BackupSetInfo SlowSet(Env env, string login, string src, out AgentApp app, string engine = "NATIVE")
        {
            env.CreateUser(login, "Customer-Pass-1", 5);
            app = env.Agent(login, "Customer-Pass-1");
            var s = new BackupSetInfo { Name = "Slow", Engine = engine, Sources = { src }, BandwidthKbps = 300 };
            var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", s);
            return app.Sets().First(x => x.Id == set.Id);
        }

        /// <summary>The administrator lifts the upload limit (the set editor of the admin site).</summary>
        static void Unthrottle(Env env, string login, string setId)
        {
            var admin = env.Admin();
            var s = BackupSetInfo.FromXml(System.Xml.Linq.XElement.Parse(admin.Call("GET", "/api/admin/users/" + login + "/sets/" + setId)["set"]));
            s.BandwidthKbps = 0;
            admin.Call("POST", "/api/admin/users/" + login + "/sets/" + setId, new Msg().Set("set", s.ToXml().ToString()));
        }

        /// <summary>Runs the agent and kills it once the server shows the run as running.</summary>
        static string KillMidway(Env env, AgentApp app, BackupSetInfo set)
        {
            var p = StartAgent(app.Home.Dir, "backup", "--set", set.Id);
            var until = DateTime.UtcNow.AddSeconds(90); string job = null;
            while (DateTime.UtcNow < until && job == null)
            {
                var live = env.Api.Live().FirstOrDefault(m => m["set"] == set.Id);
                if (live != null && !string.IsNullOrEmpty(live["job"])) job = live["job"];
                Thread.Sleep(100);
                if (p.HasExited) break;
            }
            Assert.False(p.HasExited, "the agent finished before it could be killed");
            Assert.NotNull(job);
            Thread.Sleep(1500);   // in the middle of sending
            p.Kill(true); p.WaitForExit();
            return job;
        }

        [Theory]
        [InlineData("")]
        [InlineData("RESTIC")]
        public void AgentKilledMidBackup_NextBackupRunsAtOnce_HistoryShowsTheFailure_NotRunning(string engine)
        {
            if (engine == "RESTIC" && !File.Exists(Environment.GetEnvironmentVariable("OB_RESTIC") ?? "")) return;
            using (var env = new Env())
            {
                var src = env.Dir("src");
                for (int i = 0; i < 4; i++) Rnd(Path.Combine(src, "f" + i + ".bin"), (engine == "RESTIC" ? 30L : 3L) << 20, i);
                AgentApp app; var set = SlowSet(env, "intr", src, out app, engine);
                var killed = KillMidway(env, app, set);

                // the computer starts again: the very next backup of the set must run (no 12-hour lock)
                Unthrottle(env, "intr", set.Id);
                var r = app.Backup(set.Id);
                Assert.Equal("BS_STOP_SUCCESS", r.Result);

                // nothing stays "Running"
                Assert.DoesNotContain(env.Api.Live(), m => m["set"] == set.Id);
                // the killed run is in the history as a failure, with a reason a person can read
                var runs = env.Api.Runs.Since(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow).Where(m => m["set"] == set.Id).ToList();
                var dead = runs.FirstOrDefault(m => m["job"] == killed);
                Assert.NotNull(dead);
                Assert.Equal("bad", dead["status"]);
                var log = File.ReadAllText(Path.Combine(env.HomeA, "intr", "logs", set.Id, "Backup", killed + ".log"));
                Assert.Contains("interrupted", log);
                Assert.Contains(runs, m => m["job"] == r.Job && m["status"] == "ok");

                // and the backup after it is real: every file restores identical
                var target = env.Dir("restore");
                if (engine == "RESTIC") app.Restic(app.Sets().First(x => x.Id == set.Id)).Restore(null, target, null, new List<string>());
                else app.RestoreFor(app.Interactive("Customer-Pass-1", null), set.Id).Run(null, target, null, false);
                foreach (var f in Directory.GetFiles(src)) Assert.Equal(Sha(f), Sha(Path.Combine(target, Env.Rel(src), Path.GetFileName(f))));
            }
        }

        [Fact]
        public void ComputerNeverComesBack_RunIsClosedAsInterrupted_AndAnotherProcessCanBackUp()
        {
            using (var env = new Env())
            {
                var src = env.Dir("src");
                for (int i = 0; i < 4; i++) Rnd(Path.Combine(src, "f" + i + ".bin"), 3L << 20, 10 + i);
                AgentApp app; var set = SlowSet(env, "gone", src, out app);
                var killed = KillMidway(env, app, set);
                File.Delete(Path.Combine(app.Home.SetDir(set.Id), "open-run.txt"));   // the computer is gone with its notes

                // a few minutes without any sign of life: the server closes the run by itself
                env.Api.SweepInterrupted(DateTime.UtcNow.AddMinutes(6));
                Assert.DoesNotContain(env.Api.Live(), m => m["set"] == set.Id);
                var dead = env.Api.Runs.Since(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1)).FirstOrDefault(m => m["job"] == killed);
                Assert.NotNull(dead); Assert.Equal("bad", dead["status"]);
                // the open run no longer blocks: a backup from the same set starts and completes
                Unthrottle(env, "gone", set.Id);
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);
            }
        }

        /// <summary>R1 bug 11 (found by QA F1): the server went down in the middle of a backup and came back — the next
        /// backup was refused for 5 minutes because the agent forgot its run although the server never heard its end.</summary>
        [Fact]
        public void ServerDownMidBackupAndBack_NextBackupRunsAtOnce()
        {
            using (var env = new Env())
            {
                var src = env.Dir("src");
                for (int i = 0; i < 4; i++) Rnd(Path.Combine(src, "f" + i + ".bin"), 3L << 20, 30 + i);
                AgentApp app; var set = SlowSet(env, "srvdown", src, out app);
                var p = StartAgent(app.Home.Dir, "backup", "--set", set.Id);
                var until = DateTime.UtcNow.AddSeconds(90);
                while (DateTime.UtcNow < until && !env.Api.Live().Any(m => m["set"] == set.Id)) Thread.Sleep(100);
                env.Api.Dispose();                                                   // the server is gone
                Assert.True(p.WaitForExit(120000), "the agent did not end after the server went away");
                Assert.NotEqual(0, p.ExitCode);                                      // not reported as a success
                env.Api = new Api(env.Cfg); env.Api.Start(env.Url);                  // the server is back
                Unthrottle(env, "srvdown", set.Id);
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);         // not "still running"
            }
        }

        [Fact]
        public void LiveBackupThatKeepsReporting_IsNotClosedBySweeper()
        {
            using (var env = new Env())
            {
                var src = env.Dir("src");
                for (int i = 0; i < 2; i++) Rnd(Path.Combine(src, "f" + i + ".bin"), 2L << 20, 20 + i);
                AgentApp app; var set = SlowSet(env, "alive", src, out app);
                var p = StartAgent(app.Home.Dir, "backup", "--set", set.Id);
                try
                {
                    var until = DateTime.UtcNow.AddSeconds(60);
                    while (DateTime.UtcNow < until && !env.Api.Live().Any(m => m["set"] == set.Id)) Thread.Sleep(100);
                    env.Api.SweepInterrupted(DateTime.UtcNow);   // a run that reported just now is alive
                    Assert.Contains(env.Api.Live(), m => m["set"] == set.Id);
                    Assert.True(p.WaitForExit(120000));
                    Assert.Equal(0, p.ExitCode);
                    Assert.Contains(env.Api.Runs.Since(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow), m => m["set"] == set.Id && m["status"] == "ok");
                    Assert.DoesNotContain(env.Api.Runs.Since(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow), m => m["set"] == set.Id && m["status"] == "bad");
                }
                finally { if (!p.HasExited) p.Kill(true); }
            }
        }
    
        /// <summary>
        /// Static review, class "timer" (the sweep runs on a timer, beside requests): an interrupted run can be reported by
        /// three paths at once — the agent ("interrupted"), the minute sweep, and the next Begin. RecordInterrupted checked
        /// "is there a log?" and then wrote one, so two paths at the same moment both recorded it: the history showed the
        /// failure twice, and the customer got two mails and two service calls for one event.
        /// </summary>
        [Fact]
        public void InterruptedRunReportedByManyPathsAtOnce_IsRecordedExactlyOnce()
        {
            using (var env = new Env())
            {
                var src = env.Dir("src"); File.WriteAllText(Path.Combine(src, "a.txt"), "a");
                env.CreateUser("race", "Customer-Pass-1");
                var app = env.Agent("race", "Customer-Pass-1");
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "Race", Sources = { src } });
                var job = RunId.From(DateTime.UtcNow.AddMinutes(-3));
                var go = new ManualResetEventSlim(false);
                var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();
                var threads = Enumerable.Range(0, 16).Select(i => new Thread(() =>
                {
                    try { var c = app.DeviceClient(); go.Wait(); c.Call("POST", "/api/sets/" + set.Id + "/interrupted", new Msg().Set("job", job).Set("why", "test " + i)); }
                    catch (Exception e) { errors.Add(e); }
                })).ToList();
                threads.ForEach(t => t.Start()); Thread.Sleep(300); go.Set(); threads.ForEach(t => t.Join(60000));
                Assert.Empty(errors);
                var rows = env.Api.Runs.Since(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1)).Where(m => m["set"] == set.Id && m["job"] == job).ToList();
                Assert.Single(rows);
                Assert.Equal("bad", rows[0]["status"]);
            }
        }

        /// <summary>
        /// The agent's client sends a request again when the answer does not come (a cut connection, or the 5-minute
        /// timeout while a big commit is still running on the server). The server must treat the end of a run said twice as
        /// one end: one row in the history (one mail, one service call), and the second call answers like the first —
        /// never an error that makes the computer think a stored backup failed.
        /// </summary>
        [Theory]
        [InlineData("commit")]
        [InlineData("abort")]
        [InlineData("resticreport")]
        public void EndOfRunSentTwice_IsRecordedOnce_AndTheRepeatIsNotAnError(string how)
        {
            using (var env = new Env())
            {
                var src = env.Dir("src"); File.WriteAllText(Path.Combine(src, "a.txt"), "a");
                env.CreateUser("twice", "Customer-Pass-1");
                var app = env.Agent("twice", "Customer-Pass-1");
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "Twice", Sources = { src } });
                var c = app.DeviceClient();
                var t0 = DateTime.UtcNow;
                Func<string, Msg> body = job => { var m = new Msg().Set("job", job).Set("result", how == "abort" ? "BS_STOP_BY_SYSTEM_ERROR" : "BS_STOP_SUCCESS").Set("started", RunId.UnixMs(t0)).Set("new", 0).Set("upd", 0).Set("del", 0).Set("bytes", 0);
                    foreach (var l in new[] { AhsayLog.Line(t0, "start"), AhsayLog.Line(t0, "end", message: how == "abort" ? "BS_STOP_BY_SYSTEM_ERROR" : "BS_STOP_SUCCESS") }) m.Add("log", new Msg().Set("l", l)); return m; };
                string jobId;
                if (how == "resticreport")
                {
                    jobId = RunId.From(t0);
                    c.Call("POST", "/api/sets/" + set.Id + "/resticreport", body(jobId));
                    c.Call("POST", "/api/sets/" + set.Id + "/resticreport", body(jobId));
                }
                else
                {
                    jobId = c.Call("POST", "/api/sets/" + set.Id + "/begin")["job"];
                    Assert.False(string.IsNullOrEmpty(jobId));
                    var first = c.Call("POST", "/api/sets/" + set.Id + "/jobs/" + jobId + "/" + how, body(jobId));
                    var again = c.Call("POST", "/api/sets/" + set.Id + "/jobs/" + jobId + "/" + how, body(jobId));
                    if (how == "commit") Assert.Equal(first["bad"], again["bad"]);
                }
                var rows = env.Api.Runs.Since(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1)).Where(m => m["set"] == set.Id && m["job"] == jobId).ToList();
                Assert.Single(rows);
                Assert.Equal(how == "abort" ? "bad" : "ok", rows[0]["status"]);
            }
        }

        /// <summary>
        /// The start of a run said twice (the answer to "begin" lost, the client sends it again): the second must get the
        /// same run — not "another backup is running" (a failed backup for nothing), and no orphan run that the sweep
        /// later reports as interrupted (a false alert). A different backup at the same time is still refused.
        /// Then a real backup runs and restores identical.
        /// </summary>
        [Fact]
        public void BeginSentTwice_GivesTheSameRun_NoFalseFailure_NoOrphan()
        {
            using (var env = new Env())
            {
                var src = env.Dir("src"); Rnd(Path.Combine(src, "a.bin"), 300000, 3);
                env.CreateUser("begin2", "Customer-Pass-1");
                var app = env.Agent("begin2", "Customer-Pass-1");
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "Begin", Sources = { src } });
                var c = app.DeviceClient();
                var key = Guid.NewGuid().ToString("N");
                var a = c.Call("POST", "/api/sets/" + set.Id + "/begin?key=" + key)["job"];
                var b = c.Call("POST", "/api/sets/" + set.Id + "/begin?key=" + key)["job"];
                Assert.Equal(a, b);
                var other = Assert.Throws<AgentException>(() => c.Call("POST", "/api/sets/" + set.Id + "/begin?key=" + Guid.NewGuid().ToString("N")));
                Assert.Equal(409, other.Status);
                c.Call("POST", "/api/sets/" + set.Id + "/jobs/" + a + "/abort", new Msg().Set("result", "BS_STOP_BY_SYSTEM_ERROR"));
                env.Api.SweepInterrupted(DateTime.UtcNow.AddMinutes(10));
                Assert.Single(env.Api.Runs.Since(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1)).Where(m => m["set"] == set.Id));

                // the agent sends its key: a real backup through it, and every file restores identical
                var r = app.Backup(set.Id);
                Assert.Equal("BS_STOP_SUCCESS", r.Result);
                var target = env.Dir("restore");
                app.RestoreFor(app.Interactive("Customer-Pass-1", null), set.Id).Run(null, target, null, false);
                foreach (var f in Directory.GetFiles(src)) Assert.Equal(Sha(f), Sha(Path.Combine(target, Env.Rel(src), Path.GetFileName(f))));
            }
        }

        /// <summary>
        /// Found by SourceTests after the fix of bug 20: restic runs are named by the computer from their start, to the
        /// second. A run that failed at once and the next one started in the same second had one name: the server replaced
        /// the first run's log with the second's (a run lost from the history) — and, with "a repeat is not recorded again",
        /// the success was lost instead. Two different runs with one name must both be recorded; the same report twice once.
        /// </summary>
        [Fact]
        public void TwoDifferentResticRunsWithTheSameName_AreBothRecorded()
        {
            using (var env = new Env())
            {
                var src = env.Dir("src"); File.WriteAllText(Path.Combine(src, "a.txt"), "a");
                env.CreateUser("same", "Customer-Pass-1");
                var app = env.Agent("same", "Customer-Pass-1");
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "Same", Engine = "RESTIC", Sources = { src } });
                var c = app.DeviceClient(); var t0 = DateTime.UtcNow; var job = RunId.From(t0);
                Func<string, Msg> report = result => { var m = new Msg().Set("job", job).Set("result", result).Set("started", RunId.UnixMs(t0));
                    foreach (var l in new[] { AhsayLog.Line(t0, "start"), AhsayLog.Line(t0, "end", message: result) }) m.Add("log", new Msg().Set("l", l)); return m; };
                c.Call("POST", "/api/sets/" + set.Id + "/resticreport", report("BS_STOP_BY_SYSTEM_ERROR"));
                c.Call("POST", "/api/sets/" + set.Id + "/resticreport", report("BS_STOP_SUCCESS"));
                c.Call("POST", "/api/sets/" + set.Id + "/resticreport", report("BS_STOP_SUCCESS"));   // a repeat of the second
                var rows = env.Api.Runs.Since(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1)).Where(m => m["set"] == set.Id).ToList();
                Assert.Equal(2, rows.Count);
                Assert.Contains(rows, m => m["status"] == "bad"); Assert.Contains(rows, m => m["status"] == "ok");
                Assert.Equal(2, Directory.GetFiles(Path.Combine(env.Api.UserStore.UserDir("same"), "logs", set.Id, "Backup"), "*.log").Length);
            }
        }

        /// <summary>
        /// Static review, class "timer" (the agent's heartbeat): a progress report that arrives after the run ended — a
        /// slow request, or the heartbeat firing at the moment the run ends — put the set back in "Running now", where it
        /// stayed as a ghost for 5 minutes. A run that has ended stays ended; a progress report of a live run still shows.
        /// </summary>
        [Theory]
        [InlineData("")]
        [InlineData("RESTIC")]
        public void ProgressThatArrivesAfterTheEnd_DoesNotBringTheRunBack(string engine)
        {
            using (var env = new Env())
            {
                var src = env.Dir("src"); File.WriteAllText(Path.Combine(src, "a.txt"), "a");
                env.CreateUser("ghost", "Customer-Pass-1");
                var app = env.Agent("ghost", "Customer-Pass-1");
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "Ghost", Engine = engine, Sources = { src } });
                var c = app.DeviceClient(); var t0 = DateTime.UtcNow; string job;
                Func<string, Msg> end = j => { var m = new Msg().Set("job", j).Set("result", "BS_STOP_SUCCESS").Set("started", RunId.UnixMs(t0));
                    foreach (var l in new[] { AhsayLog.Line(t0, "start"), AhsayLog.Line(t0, "end", message: "BS_STOP_SUCCESS") }) m.Add("log", new Msg().Set("l", l)); return m; };
                if (engine == "RESTIC")
                {
                    job = RunId.From(t0);
                    c.Call("POST", "/api/sets/" + set.Id + "/progress", new Msg().Set("job", job).Set("files", 1));
                    Assert.Contains(env.Api.Live(), m => m["set"] == set.Id);                 // a live run shows
                    c.Call("POST", "/api/sets/" + set.Id + "/resticreport", end(job));
                }
                else
                {
                    job = c.Call("POST", "/api/sets/" + set.Id + "/begin")["job"];
                    c.Call("POST", "/api/sets/" + set.Id + "/progress", new Msg().Set("job", job).Set("files", 1));
                    Assert.Contains(env.Api.Live(), m => m["set"] == set.Id);
                    c.Call("POST", "/api/sets/" + set.Id + "/jobs/" + job + "/commit", end(job));
                }
                Assert.DoesNotContain(env.Api.Live(), m => m["set"] == set.Id);
                c.Call("POST", "/api/sets/" + set.Id + "/progress", new Msg().Set("job", job).Set("files", 1));   // late
                Assert.DoesNotContain(env.Api.Live(), m => m["set"] == set.Id);
            }
        }
}
}
