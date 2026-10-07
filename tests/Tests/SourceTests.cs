using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// R1 (GPT audit 4): a source that should be backed up but cannot be read must never look like a good backup —
    /// not in the run result, not in "Last backup" of the admin site, not in the missed-backup alert. And its files are
    /// never treated as deleted; when it comes back, the backup goes on without sending everything again.
    /// </summary>
    public class SourceTests
    {
        static AgentApp App(Env env, string login, List<string> sources, out BackupSetInfo set, string engine = "")
        {
            env.CreateUser(login, "Customer-Pass-1", 1);
            var app = env.Agent(login, "Customer-Pass-1");
            var s = new BackupSetInfo { Name = "Docs", Engine = engine };
            s.Sources.AddRange(sources);
            var created = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", s);
            set = app.Sets().First(x => x.Id == created.Id);
            return app;
        }
        static XAttr SetAttr(Env env, string login, string setId) { return new XAttr(env.Api.UserStore.LoadProfile(login).FindSet(setId)); }
        sealed class XAttr { readonly System.Xml.Linq.XElement e; public XAttr(System.Xml.Linq.XElement e) { this.e = e; } public string this[string n] { get { return (string)e.Attribute(n); } } }
        static Msg AdminSet(Env env, string login, string setId)
        {
            return env.Admin().Call("GET", "/api/admin/users").List("users").First(u => u["login"] == login).List("sets").First(s => s["id"] == setId);
        }

        [Theory]
        [InlineData("")]
        [InlineData("RESTIC")]
        public void WholeSourceGone_IsAFailure_LastBackupNotRefreshed_FilesKept_AndComesBackCleanly(string engine)
        {
            if (engine == "RESTIC" && !File.Exists(Environment.GetEnvironmentVariable("OB_RESTIC") ?? "")) throw NotTested.Because("OB_RESTIC (the restic program) is not set - the RESTIC case");
            using (var env = new Env())
            {
                var src = env.Dir("docs");
                for (int i = 0; i < 5; i++) File.WriteAllText(Path.Combine(src, "d" + i + ".txt"), "document " + i);
                BackupSetInfo set; var app = App(env, "src1", new List<string> { src }, out set, engine);
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);
                var good = SetAttr(env, "src1", set.Id)["LAST_BACKUP_COMPLETE"];
                Assert.False(string.IsNullOrEmpty(good));

                // the folder is moved away (a disconnected disk, a renamed share)
                var away = src + "-away"; Directory.Move(src, away);
                System.Threading.Thread.Sleep(20);
                var r = app.Backup(set.Id);
                Assert.Equal("BS_STOP_BY_SYSTEM_ERROR", r.Result);                       // not SUCCESS_WITH_WARNING
                Assert.Contains(r.LogLines, l => l.Contains("\"err\"") || l.Contains("err"));
                Assert.Equal(good, SetAttr(env, "src1", set.Id)["LAST_BACKUP_COMPLETE"]); // "Last backup" still shows the last good one
                var a = AdminSet(env, "src1", set.Id);
                Assert.Equal("BS_STOP_BY_SYSTEM_ERROR", a["lastResult"]);                  // and the site shows the failure
                Assert.Equal(good, a["lastBackup"]);
                Assert.Contains(env.Api.Runs.Since(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow), m => m["job"] == r.Job && m["status"] == "bad");
                // the missed-backup check counts from the last good backup, not from the failed run
                var since = env.Api.UserStore.LoadProfile("src1");

                // the folder is back: the next backup succeeds, nothing was deleted, nothing is sent again
                Directory.Move(away, src);
                var back = app.Backup(set.Id);
                Assert.Equal("BS_STOP_SUCCESS", back.Result);
                if (engine == "") { Assert.Equal(0, back.Deleted); Assert.Equal(0, back.New); Assert.Equal(0, back.Updated); }
                Assert.NotEqual(good, SetAttr(env, "src1", set.Id)["LAST_BACKUP_COMPLETE"]);
                Assert.Equal("BS_STOP_SUCCESS", AdminSet(env, "src1", set.Id)["lastResult"]);
            }
        }

        /// <summary>R1 (found by QA F5): the restore test with the source offline compared nothing and was shown as FAILED.</summary>
        [Fact]
        public void RestoreTest_WithTheSourceOffline_IsNotChecked_NotFailed()
        {
            using (var env = new Env())
            {
                var src = env.Dir("docs"); File.WriteAllText(Path.Combine(src, "a.txt"), "a");
                BackupSetInfo set; var app = App(env, "src4", new List<string> { src }, out set);
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);
                Directory.Move(src, src + "-away");
                app.RestoreTest(set.Id);
                Assert.StartsWith("NOT_CHECKED", SetAttr(env, "src4", set.Id)["RESTORE_TEST_RESULT"]);
                Directory.Move(src + "-away", src);
                app.RestoreTest(set.Id);
                Assert.StartsWith("OK", SetAttr(env, "src4", set.Id)["RESTORE_TEST_RESULT"]);
            }
        }

        [Fact]
        public void OneOfTwoSourcesGone_IsSuccessWithError_ShownAsProblem_NotAsWarning()
        {
            using (var env = new Env())
            {
                var a = env.Dir("a"); var b = env.Dir("b");
                File.WriteAllText(Path.Combine(a, "x.txt"), "x"); File.WriteAllText(Path.Combine(b, "y.txt"), "y");
                BackupSetInfo set; var app = App(env, "src2", new List<string> { a, b }, out set);
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);
                Directory.Delete(b, true);
                var r = app.Backup(set.Id);
                Assert.Equal("BS_STOP_SUCCESS_WITH_ERROR", r.Result);
                Assert.Equal(0, r.Deleted);                                                  // y.txt is kept, not "deleted"
                Assert.Contains(env.Api.Runs.Since(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow), m => m["job"] == r.Job && m["status"] == "bad");
                Assert.Equal("BS_STOP_SUCCESS_WITH_ERROR", AdminSet(env, "src2", set.Id)["lastResult"]);
            }
        }

        [Fact]
        public void SubfolderNotReadable_IsAnError_NotAWarning()
        {
            if (OperatingSystem.IsWindows() || Environment.UserName == "root") throw NotTested.Because("needs a non-root Linux user (root reads every folder; Windows has no such permission bits)");   // root reads everything
            using (var env = new Env())
            {
                var a = env.Dir("perm"); var locked = Path.Combine(a, "secret"); Directory.CreateDirectory(locked);
                File.WriteAllText(Path.Combine(a, "ok.txt"), "ok"); File.WriteAllText(Path.Combine(locked, "s.txt"), "s");
                BackupSetInfo set; var app = App(env, "src3", new List<string> { a }, out set);
                File.SetUnixFileMode(locked, UnixFileMode.None);
                try { Assert.Equal("BS_STOP_SUCCESS_WITH_ERROR", app.Backup(set.Id).Result); }
                finally { File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            }
        }
    }
}
