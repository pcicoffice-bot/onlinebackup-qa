using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// QA audit J — BK-03 / RS-03, integration layer: real restic against this product's server (REST store), the agent's
    /// window API (ClientUi) and the server's own records (job log files, repository files). Skipped without OB_RESTIC.
    /// </summary>
    public class AuditJ_ResticServerTests
    {
        static string ResticExe { get { return Environment.GetEnvironmentVariable("OB_RESTIC"); } }
        static bool Have { get { return !string.IsNullOrEmpty(ResticExe) && File.Exists(ResticExe); } }
        static string Sha(string f) { using (var s = File.OpenRead(f)) using (var h = SHA256.Create()) return Convert.ToHexString(h.ComputeHash(s)); }

        static Msg Call(ClientUi ui, string op, Msg body = null, string query = null)
        {
            var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + ui.Port + "/api/" + op + (query == null ? "" : "?" + query));
            req.Method = body == null ? "GET" : "POST"; req.Headers["X-Key"] = ui.Key; req.Proxy = null;
            if (body != null) { var b = body.ToBytes(); req.ContentType = "application/xml"; using (var s = req.GetRequestStream()) s.Write(b, 0, b.Length); }
            try { using (var r = (HttpWebResponse)req.GetResponse()) return Msg.Read(r.GetResponseStream()); }
            catch (WebException e) when (e.Response != null) { using (var r = (HttpWebResponse)e.Response) { var m = Msg.Read(r.GetResponseStream()); throw new AgentException((int)r.StatusCode, m["code"], m["message"]); } }
        }

        static ClientUi StartUi(AgentApp app)
        {
            for (int port = 18700 + new Random().Next(800); ; port++) try { return new ClientUi(app, port); } catch (HttpListenerException) { }
        }

        static Msg WaitJob(ClientUi ui, string id)
        {
            var until = DateTime.UtcNow.AddMinutes(3);
            while (DateTime.UtcNow < until)
            {
                var j = Call(ui, "jobs").List("jobs").FirstOrDefault(x => x["id"] == id);
                if (j != null && j["state"] != "running") return j;
                Thread.Sleep(200);
            }
            throw new TimeoutException("job " + id);
        }

        static string UserDir(Env env, string login) { return Directory.GetDirectories(env.HomeA, login, SearchOption.AllDirectories).First(); }

        /// <summary>
        /// RS-03 "Replace existing files" (window and WinForms send overwrite=0 by default). For a restic set ClientUi.Restore
        /// ignores the flag (only the native engine gets it) and restic's own default is --overwrite always: a file the
        /// customer changed after the backup is silently replaced by the old version. Oracle: the bytes on disk.
        /// </summary>
        [Fact]
        public void WindowRestore_ReplaceExistingUnchecked_KeepsTheNewerLocalFile()
        {
            if (!Have) throw NotTested.Because("OB_RESTIC (the restic program) is not set");
            using (var env = new Env())
            {
                env.CreateUser("rjow", "Customer-Pass-1");
                var app = env.Agent("rjow", "Customer-Pass-1");
                var src = env.Dir("src");
                File.WriteAllText(Path.Combine(src, "contract.docx"), "version from the backup");
                File.WriteAllText(Path.Combine(src, "other.txt"), "other");
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "W", Engine = "RESTIC", Sources = { src } });
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);

                // the restore folder already holds a newer version of contract.docx (e.g. restoring to where the files are)
                var target = env.Dir("restore");
                var existingDir = Path.Combine(target, Env.Rel(src)); Directory.CreateDirectory(existingDir);
                var existing = Path.Combine(existingDir, "contract.docx");
                File.WriteAllText(existing, "NEWER local edit — must survive");
                var before = Sha(existing);

                var ui = StartUi(app);
                try
                {
                    Call(ui, "login", new Msg().Set("password", "Customer-Pass-1"));
                    var point = Call(ui, "points", null, "set=" + set.Id).List("points").First()["id"];
                    var job = Call(ui, "restore", new Msg().Set("set", set.Id).Set("point", point).Set("target", target).Set("overwrite", "0"))["job"];
                    var j = WaitJob(ui, job);
                    Assert.True(j["state"] == "ok", j["result"] + " " + j["detail"]);
                    Assert.Equal("other", File.ReadAllText(Path.Combine(existingDir, "other.txt")));      // the restore did run
                    Assert.True(before == Sha(existing), "'Replace existing files' was off, yet the newer local file now reads: " + File.ReadAllText(existing));
                }
                finally { ui.Dispose(); }
            }
        }

        /// <summary>
        /// BK-03 server view of a partial snapshot (restic exit 3): the server's job log — what the admin site, the mails and
        /// the history show — must say an error happened and which file was not backed up. Oracle: the server's job-log
        /// file and the repository's snapshot files.
        /// </summary>
        [Fact]
        public void PartialSnapshot_ServerJobLog_ShowsErrorAndTheMissingFile()
        {
            if (!Have || !OperatingSystem.IsLinux() || !File.Exists("/usr/bin/setpriv")) throw NotTested.Because("needs restic, Linux and /usr/bin/setpriv");
            using (var env = new Env())
            {
                env.CreateUser("rjpart", "Customer-Pass-1");
                var app = env.Agent("rjpart", "Customer-Pass-1");
                var src = env.Dir("src");
                File.WriteAllText(Path.Combine(src, "ok.txt"), "ok");
                var locked = Path.Combine(src, "ledger-locked.db");
                File.WriteAllText(locked, "locked"); File.SetUnixFileMode(locked, UnixFileMode.None);
                var w = Path.Combine(env.Root, "restic-noroot.sh");
                File.WriteAllText(w, "#!/bin/sh\n# Q31: only root has the read-anything capability to drop; elsewhere (the CI runner) setpriv refuses and restic never ran\n[ \"$(id -u)\" != 0 ] && exec \"" + ResticExe + "\" \"$@\"\nexec setpriv --inh-caps=-dac_override,-dac_read_search --bounding-set=-dac_override,-dac_read_search \"" + ResticExe + "\" \"$@\"\n");
                File.SetUnixFileMode(w, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                app.ResticSetup = r => r.ExePath = w;
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "P", Engine = "RESTIC", Sources = { src } });
                var run = app.Backup(set.Id);

                var userDir = UserDir(env, "rjpart");
                Assert.Single(Directory.GetFiles(Path.Combine(new ResticStore(userDir, set.Id).Dir, "snapshots")));   // restic made a snapshot
                var logs = Directory.GetFiles(Path.Combine(userDir, "logs", set.Id, "Backup"));
                Assert.Single(logs);
                var text = File.ReadAllText(logs[0]);
                Assert.True(text.Contains("ledger-locked.db") && text.Contains("BS_STOP_SUCCESS_WITH_ERROR"),
                    "agent result " + run.Result + "; the server's job log:\n" + text);
            }
        }

        /// <summary>
        /// Wrong key against the server's repository on a computer that lost its "repository created" mark: restic's
        /// `init` must be refused (config is append-only), nothing in the repository changes, the run is not a success
        /// and the server's job log says so. Passing coverage test (BK-03, key handling, integration). Can fail: an accepted
        /// init would change the config file's SHA-256 / add a key; a success result would fail the StartsWith check.
        /// </summary>
        [Fact]
        public void WrongKey_AgainstServerRepository_NothingChanges_RunIsAFailureOnTheServer()
        {
            if (!Have) throw NotTested.Because("OB_RESTIC (the restic program) is not set");
            using (var env = new Env())
            {
                env.CreateUser("rjkey", "Customer-Pass-1");
                var app = env.Agent("rjkey", "Customer-Pass-1");
                var src = env.Dir("src");
                File.WriteAllText(Path.Combine(src, "a.txt"), "a");
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "K", Engine = "RESTIC", Sources = { src } });
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);
                var repo = new ResticStore(UserDir(env, "rjkey"), set.Id);
                var cfg = Sha(Path.Combine(repo.Dir, "config"));
                var keys = Directory.GetFiles(Path.Combine(repo.Dir, "keys")).Length;
                var snaps = Directory.GetFiles(Path.Combine(repo.Dir, "snapshots")).Length;

                File.Delete(Path.Combine(app.Home.SetDir(set.Id), "restic-init.txt"));
                var bad = new ResticRunner(app, app.Sets().First(x => x.Id == set.Id), KeySet.Random()).Backup();
                Assert.False(bad.Result.StartsWith("BS_STOP_SUCCESS"), string.Join("\n", bad.LogLines));
                Assert.Equal(cfg, Sha(Path.Combine(repo.Dir, "config")));
                Assert.Equal(keys, Directory.GetFiles(Path.Combine(repo.Dir, "keys")).Length);
                Assert.Equal(snaps, Directory.GetFiles(Path.Combine(repo.Dir, "snapshots")).Length);
                Assert.Empty(Directory.GetFiles(Path.Combine(repo.Dir, "locks")));
                var logs = Directory.GetFiles(Path.Combine(UserDir(env, "rjkey"), "logs", set.Id, "Backup")).OrderBy(x => x, StringComparer.Ordinal).ToList();
                Assert.Equal(2, logs.Count);
                Assert.Contains("BS_STOP_BY_SYSTEM_ERROR", File.ReadAllText(logs[1]));
                // the right key still reads everything
                Assert.Single(app.Restic(app.Sets().First(x => x.Id == set.Id)).Snapshots());
            }
        }
    }
}
