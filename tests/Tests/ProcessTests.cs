using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>R1 (GPT audit 5–7): external programs that hang or misbehave never hang a backup.</summary>
    [Collection("Limits")]
    public class ProcessTests
    {
        static bool Unix { get { return Environment.OSVersion.Platform == PlatformID.Unix; } }

        [Fact]
        public void StuckProgram_IsKilledWithItsChildren_AtTheLimit()
        {
            if (!Unix) throw NotTested.Because("needs Linux (the stand-in programs are shell scripts)");
            var dir = Path.Combine(Path.GetTempPath(), "obproc-" + Guid.NewGuid().ToString("N").Substring(0, 6)); Directory.CreateDirectory(dir);
            var pidFile = Path.Combine(dir, "child.pid");
            var psi = new ProcessStartInfo("/bin/sh", "-c \"sleep 600 & echo $! > '" + pidFile + "'; wait\"");
            var sw = Stopwatch.StartNew();
            var r = ProcessRunner.Run(psi, TimeSpan.FromSeconds(2));
            Assert.True(r.TimedOut);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), "took " + sw.Elapsed);
            var child = int.Parse(File.ReadAllText(pidFile).Trim());
            Thread.Sleep(500);
            Assert.False(File.Exists("/proc/" + child + "/stat") && !File.ReadAllText("/proc/" + child + "/stat").Contains(") Z "), "the child process (sleep) is still running");
            Assert.Throws<TimeoutException>(() => ProcessRunner.Check(new ProcessStartInfo("/bin/sh", "-c \"sleep 600\""), TimeSpan.FromSeconds(1)));
        }

        [Fact]
        public void ProgramWritingMuchToBothStreams_IsReadToTheEnd()
        {
            if (!Unix) throw NotTested.Because("needs Linux (the stand-in programs are shell scripts)");
            var r = ProcessRunner.Run(new ProcessStartInfo("/bin/sh", "-c \"head -c 500000 /dev/zero | tr '\\\\0' e >&2; head -c 500000 /dev/zero | tr '\\\\0' o; exit 3\""), TimeSpan.FromSeconds(30));
            Assert.False(r.TimedOut);
            Assert.Equal(3, r.Code);
            Assert.True(r.Out.Length >= 500000 && r.Err.Length >= 500000, "out " + r.Out.Length + " err " + r.Err.Length + " " + r.Err.Substring(0, Math.Min(200, r.Err.Length)));
        }

        /// <summary>A pre-command that never ends (a script waiting for input, a stuck service stop) ends at its limit.</summary>
        [Fact]
        public void StuckPreCommand_EndsAtTheLimit_AndTheBackupGoesOn()
        {
            if (!Unix) throw NotTested.Because("needs Linux (the stand-in programs are shell scripts)");
            var before = Limits.Command;
            try
            {
                Limits.Command = TimeSpan.FromSeconds(2);
                var lines = new System.Collections.Generic.List<string>();
                var sw = Stopwatch.StartNew();
                var ok = Commands.Run(new[] { "sleep 600" }, "pre", lines.Add, lines.Add);
                Assert.False(ok);
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), "took " + sw.Elapsed);
                Assert.Contains(lines, l => l.Contains("timed out") || l.Contains("did not finish"));
            }
            finally { Limits.Command = before; }
        }
    
        static string Script(string dir, string name, string body)
        {
            var f = Path.Combine(dir, name); File.WriteAllText(f, "#!/bin/sh\n" + body + "\n");
            Process.Start("chmod", "+x \"" + f + "\"").WaitForExit(10000); return f;
        }

        static string Sha(string f) { using (var s = File.OpenRead(f)) using (var h = System.Security.Cryptography.SHA256.Create()) return Convert.ToHexString(h.ComputeHash(s)); }

        /// <summary>
        /// Static review, class "raw-process": restic ran without any time limit. A restic whose connection stalls (it neither
        /// fails nor goes on) was a backup that never ended — and the heartbeat kept it "Running" for ever.
        /// Component: the runner stops a silent restic at its idle limit. Integration: the agent's backup ends as a failure,
        /// the server's history says so, nothing stays Running. Recovery + integrity: the next backup with a working restic
        /// succeeds and every file restores with the same SHA-256.
        /// </summary>
        [Fact]
        public void HungRestic_BackupEndsAsAFailure_AtTheIdleLimit_AndTheNextBackupRestoresIdentical()
        {
            var real = Environment.GetEnvironmentVariable("OB_RESTIC");
            if (!Unix || string.IsNullOrEmpty(real) || !File.Exists(real)) throw NotTested.Because("needs Linux (the stand-in programs are shell scripts) and restic");
            using (var env = new Env())
            {
                var src = env.Dir("src");
                for (int i = 0; i < 3; i++) { var b = new byte[200000 + i]; new Random(i).NextBytes(b); File.WriteAllBytes(Path.Combine(src, "f" + i + ".bin"), b); }
                env.CreateUser("hang", "Customer-Pass-1");
                var app = env.Agent("hang", "Customer-Pass-1");
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), null, new BackupSetInfo { Name = "Hang", Engine = "RESTIC", Sources = { src } });
                // the stand-in: every command goes to the real restic, except "backup", which says nothing and never ends
                var hung = Script(env.Dir("bin"), "restic", "if [ \"$1\" = backup ]; then sleep 600; fi\nexec '" + real + "' \"$@\"");
                app.ResticSetup = r => { r.ExePath = hung; r.IdleLimit = TimeSpan.FromSeconds(3); };

                var sw = Stopwatch.StartNew();
                var bad = app.Backup(set.Id);
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60), "the backup took " + sw.Elapsed);
                Assert.NotEqual("BS_STOP_SUCCESS", bad.Result);
                Assert.StartsWith("BS_STOP_BY_", bad.Result);
                Assert.DoesNotContain(env.Api.Live(), m => m["set"] == set.Id);
                var runs = env.Api.Runs.Since(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1)).Where(m => m["set"] == set.Id).ToList();
                Assert.Contains(runs, m => m["job"] == bad.Job && m["status"] == "bad");
                var log = File.ReadAllText(Path.Combine(env.HomeA, "hang", "logs", set.Id, "Backup", bad.Job + ".log"));
                Assert.Contains("stopped answering", log);

                app.ResticSetup = null;
                var ok = app.Backup(set.Id);
                Assert.Equal("BS_STOP_SUCCESS", ok.Result);
                var target = env.Dir("restore");
                app.Restic(app.Sets().First(x => x.Id == set.Id)).Restore(null, target, null, new System.Collections.Generic.List<string>());
                foreach (var f in Directory.GetFiles(src)) Assert.Equal(Sha(f), Sha(Path.Combine(target, Env.Rel(src), Path.GetFileName(f))));
            }
        }

        /// <summary>Static review, "raw-process": the web restore waited for restic without a limit — a hung restic held the
        /// request (and a server thread) for ever. Now it is stopped at the limit and the customer gets a clear 504.</summary>
        [Fact]
        public void HungWebRestore_IsStopped_WithAClearTimeout()
        {
            if (!Unix) throw NotTested.Because("needs Linux (the stand-in programs are shell scripts)");
            var dir = Path.Combine(Path.GetTempPath(), "obwr-" + Guid.NewGuid().ToString("N").Substring(0, 6)); Directory.CreateDirectory(dir);
            var before = WebRestore.Limit;
            try
            {
                WebRestore.ExeOverride = Script(dir, "restic", "sleep 600");
                WebRestore.Limit = TimeSpan.FromSeconds(2);
                var sw = Stopwatch.StartNew();
                var e = Assert.Throws<ApiException>(() => WebRestore.Run(dir, "pw", "snapshots", "--json"));
                Assert.Equal(504, e.Status);
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), "took " + sw.Elapsed);
                // and a restic that fails says why
                WebRestore.ExeOverride = Script(dir, "restic2", "echo 'Fatal: repository does not exist' >&2; exit 1");
                var f = Assert.Throws<ApiException>(() => WebRestore.Run(dir, "pw", "snapshots"));
                Assert.Equal(500, f.Status); Assert.Contains("repository does not exist", f.Message);
            }
            finally { WebRestore.ExeOverride = null; WebRestore.Limit = before; }
        }

        /// <summary>Static review, "raw-process": the client installation ran chmod / systemctl / launchctl with stderr redirected
        /// and never read, and without a limit. A program that writes much to stderr (systemctl's warnings) filled the pipe
        /// and the installation hung for ever; a step that never answers hung it too.</summary>
        [Fact]
        public void InstallStep_ChattyOnStderr_DoesNotHang_AndAStuckStepEndsAtTheLimit()
        {
            if (!Unix) throw NotTested.Because("needs Linux (the stand-in programs are shell scripts)");
            var dir = Path.Combine(Path.GetTempPath(), "obsetup-" + Guid.NewGuid().ToString("N").Substring(0, 6)); Directory.CreateDirectory(dir);
            var chatty = Script(dir, "chatty", "head -c 400000 /dev/zero | tr '\\0' w >&2; exit 4");
            int code = 0;
            var t = new Thread(() => code = OnlineBackup.Agent.Setup.Exec(chatty, "")) { IsBackground = true }; t.Start();
            Assert.True(t.Join(TimeSpan.FromSeconds(60)), "the installation step hung on a full stderr pipe");
            Assert.Equal(4, code);
            var before = Limits.Install;
            try
            {
                Limits.Install = TimeSpan.FromSeconds(2);
                var sw = Stopwatch.StartNew();
                Assert.Equal(-1, OnlineBackup.Agent.Setup.Exec(Script(dir, "stuck", "sleep 600"), ""));
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), "took " + sw.Elapsed);
            }
            finally { Limits.Install = before; }
        }
}
}
