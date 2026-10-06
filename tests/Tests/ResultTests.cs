using System;
using System.IO;
using System.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// Verification (static review, class "default-success"): a run reported to the server without a result was counted as
    /// a SUCCESS in six places (history, last result, mails, service calls, statistics). A run whose outcome is unknown is
    /// never a success: the result is taken from the log's end line, and without one the run failed.
    /// </summary>
    public class ResultTests
    {
        static (Env env, AgentApp app, BackupSetInfo set) Setup(string login)
        {
            var env = new Env();
            env.CreateUser(login, "Customer-Pass-1");
            var app = env.Agent(login, "Customer-Pass-1");
            var src = env.Dir("src"); File.WriteAllText(Path.Combine(src, "a.txt"), "a");
            var s = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "R", Sources = { src } });
            return (env, app, app.Sets().First(x => x.Id == s.Id));
        }

        [Fact]
        public void CommitWithoutAResult_AndWithoutAnEndLine_IsAFailure()
        {
            var (env, app, set) = Setup("nores");
            using (env)
            {
                var c = app.DeviceClient();
                var job = c.Call("POST", "/api/sets/" + set.Id + "/begin")["job"];
                c.Call("POST", "/api/sets/" + set.Id + "/jobs/" + job + "/commit", new Msg().Set("new", 0));
                var e = env.Api.UserStore.LoadProfile("nores").FindSet(set.Id);
                Assert.Equal("BS_STOP_BY_SYSTEM_ERROR", (string)e.Attribute("LAST_RESULT"));
                Assert.True(string.IsNullOrEmpty((string)e.Attribute("LAST_BACKUP_COMPLETE")));
                Assert.Contains(env.Api.Runs.Since(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow), m => m["job"] == job && m["status"] == "bad");
            }
        }

        [Fact]
        public void CommitWithoutAResult_TakesItFromTheLogsEndLine()
        {
            var (env, app, set) = Setup("endline");
            using (env)
            {
                var c = app.DeviceClient();
                var job = c.Call("POST", "/api/sets/" + set.Id + "/begin")["job"];
                var body = new Msg().Set("new", 0);
                body.Add("log", new Msg().Set("l", AhsayLog.Line(DateTime.UtcNow, "end", message: "BS_STOP_SUCCESS_WITH_WARNING")));
                c.Call("POST", "/api/sets/" + set.Id + "/jobs/" + job + "/commit", body);
                Assert.Equal("BS_STOP_SUCCESS_WITH_WARNING", (string)env.Api.UserStore.LoadProfile("endline").FindSet(set.Id).Attribute("LAST_RESULT"));
            }
        }
    
        /// <summary>
        /// Verification (same class, found by reading the restore path): a restore with failed files was recorded on the
        /// server as "OK" — the computer sent no result and the server took "OK" when there was none.
        /// </summary>
        [Fact]
        public void RestoreWithADamagedFile_IsRecordedAsFailed_AndTheProgramSaysSo()
        {
            var (env, app, set) = Setup("rsbad");
            using (env)
            {
                var src = app.Sets().First().Sources[0];
                File.WriteAllText(Path.Combine(src, "b.txt"), "second file");
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);
                // one stored object is damaged on the server's disk (a bad sector, a bad copy)
                var obj = Directory.GetFiles(Path.Combine(env.HomeA, "rsbad", "files", set.Id), "*", SearchOption.AllDirectories)
                    .Where(f => !f.EndsWith(".chk") && !f.EndsWith(".db") && !f.Contains("jobs") && new FileInfo(f).Length > 40).OrderBy(f => f).First();
                var b = File.ReadAllBytes(obj); b[b.Length / 2] ^= 0xFF; File.WriteAllBytes(obj, b);
                var r = app.RestoreFor(app.Interactive("Customer-Pass-1", null), set.Id);
                r.Run(null, env.Dir("restore"), null, false);
                Assert.Equal(1, r.Failed);
                var run = env.Api.Runs.Since(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow).First(m => m["kind"] == "Restore");
                Assert.Equal("bad", run["status"]);
            }
        }

        /// <summary>A restore that skipped files because they already exist does not report a plain success.</summary>
        [Fact]
        public void RestoreThatSkipsExistingFiles_SaysSo_NotAPlainSuccess()
        {
            var (env, app, set) = Setup("rsskip");
            using (env)
            {
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);
                var src = app.Sets().First().Sources[0];
                File.WriteAllText(Path.Combine(src, "a.txt"), "changed since the backup");
                var r = app.RestoreFor(app.Interactive("Customer-Pass-1", null), set.Id);
                r.Run(null, null, null, false);                                  // to the original place, no overwrite
                Assert.Equal(1, r.Skipped);
                Assert.Equal("changed since the backup", File.ReadAllText(Path.Combine(src, "a.txt")));   // nothing overwritten
                var run = env.Api.Runs.Since(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow).First(m => m["kind"] == "Restore");
                Assert.Equal("warn", run["status"]);
            }
        }
    }
}
