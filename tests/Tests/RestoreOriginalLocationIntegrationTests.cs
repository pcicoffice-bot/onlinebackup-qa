using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// Owner decisions UI-Q1 / UI-Q2, integration: a real server, a real backup by the agent (own engine), the restore through
    /// the window's local API (ClientUi) and through the agent's command line.
    ///   UI-Q1  a backup now records each file's whole-file SHA-256 (inside the encrypted header); the restore checks every file
    ///          it wrote against it and the job reports verified / verifiedSha256 / mismatched.
    ///   UI-Q2  restorecheck answers how many / which files exist at the original location; restore to the original location
    ///          without a decision is refused (EXISTS) and changes nothing; cancel runs nothing; skip keeps the customer's
    ///          newer file; overwrite replaces it. The folder restore keeps its behaviour.
    /// Oracle: the bytes on the disk (SHA-256), the API answers, the job record.
    /// </summary>
    public class RestoreOriginalLocationIntegrationTests
    {
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
            for (int port = 19500 + new Random().Next(800); ; port++) try { return new ClientUi(app, port); } catch (HttpListenerException) { }
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

        [Fact]
        public void Window_OriginalLocation_AsksFirst_CancelSkipOverwrite_AndEveryRestoredFileIsVerifiedBySha256()
        {
            using (var env = new Env())
            {
                env.CreateUser("rorig", "Customer-Pass-1");
                var app = env.Agent("rorig", "Customer-Pass-1");
                var src = env.Dir("src");
                var contract = Path.Combine(src, "contract.docx"); File.WriteAllText(contract, "version from the backup");
                var ledger = Path.Combine(src, "ledger.csv"); File.WriteAllText(ledger, string.Join("\n", Enumerable.Range(0, 3000).Select(i => "row," + i)));
                var gone = Path.Combine(src, "deleted-by-mistake.txt"); File.WriteAllText(gone, "bring me back");
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "Files", Sources = { src } });
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);
                var backedUp = new Dictionary<string, string> { { contract, Sha(contract) }, { ledger, Sha(ledger) }, { gone, Sha(gone) } };

                File.WriteAllText(contract, "NEWER local edit");   // the customer worked on it after the backup
                File.Delete(gone);
                var newer = Sha(contract);

                var ui = StartUi(app);
                try
                {
                    Call(ui, "login", new Msg().Set("password", "Customer-Pass-1"));
                    var point = Call(ui, "points", null, "set=" + set.Id).List("points").First()["id"];
                    Func<Msg> req = () => new Msg().Set("set", set.Id).Set("point", point).Set("location", "original");

                    // the question first: 3 files, 2 already there (contract, ledger), named
                    var check = Call(ui, "restorecheck", req());
                    Assert.Equal(3, check.Int("total")); Assert.Equal(2, check.Int("existing")); Assert.Equal("original", check["location"]);
                    Assert.Equal(new[] { contract, ledger }.OrderBy(x => x), check.List("files").Select(f => f["p"]).OrderBy(x => x));

                    // no decision: refused, nothing written
                    var e = Assert.Throws<AgentException>(() => Call(ui, "restore", req()));
                    Assert.Equal("EXISTS", e.Code); Assert.Contains("2", e.Message);
                    Assert.False(File.Exists(gone)); Assert.Equal(newer, Sha(contract));
                    Assert.Empty(Call(ui, "jobs").List("jobs"));

                    // cancel: nothing runs
                    Assert.Equal(1, Call(ui, "restore", req().Set("existing", "cancel")).Int("cancelled"));
                    Assert.False(File.Exists(gone)); Assert.Empty(Call(ui, "jobs").List("jobs"));

                    // skip: the newer file stays, the missing one comes back — verified by the SHA-256 the backup recorded
                    var j = WaitJob(ui, Call(ui, "restore", req().Set("existing", "skip"))["job"]);
                    Assert.True(j["state"] == "ok", j["result"] + " " + j["detail"]);
                    Assert.Equal(newer, Sha(contract)); Assert.Equal(backedUp[gone], Sha(gone));
                    Assert.Equal(1, j.Int("restored")); Assert.Equal(2, j.Int("skipped")); Assert.Equal(1, j.Int("verified")); Assert.Equal(1, j.Int("verifiedSha256")); Assert.Equal(0, j.Int("mismatched"));
                    Assert.Equal("RESTORE_STOP_WITH_WARNING", j["restoreResult"]);

                    // overwrite: everything back as backed up, all verified
                    j = WaitJob(ui, Call(ui, "restore", req().Set("existing", "overwrite"))["job"]);
                    Assert.True(j["state"] == "ok", j["result"] + " " + j["detail"]);
                    foreach (var kv in backedUp) Assert.Equal(kv.Value, Sha(kv.Key));
                    Assert.Equal(3, j.Int("restored")); Assert.Equal(3, j.Int("verified")); Assert.Equal(3, j.Int("verifiedSha256")); Assert.Equal("RESTORE_STOP_SUCCESS", j["restoreResult"]);
                    Assert.Contains("verified 3", j["detail"]);

                    // the folder restore keeps its behaviour (target + overwrite=0) and is verified too
                    var target = env.Dir("alt");
                    j = WaitJob(ui, Call(ui, "restore", new Msg().Set("set", set.Id).Set("point", point).Set("target", target).Set("overwrite", "0"))["job"]);
                    Assert.True(j["state"] == "ok", j["result"] + " " + j["detail"]);
                    Assert.Equal(backedUp[contract], Sha(Path.Combine(target, Env.Rel(contract))));
                    Assert.Equal(3, j.Int("verifiedSha256")); Assert.Equal("alternate", j["location"]);
                    Assert.Equal(3, Call(ui, "restorecheck", new Msg().Set("set", set.Id).Set("point", point).Set("target", target)).Int("existing"));
                }
                finally { ui.Dispose(); }
            }
        }

        [Fact]
        public void CommandLine_OriginalLocation_WithoutADecision_ListsTheFiles_Exit3_ThenSkipExisting()
        {
            using (var env = new Env())
            {
                env.CreateUser("rcli", "Customer-Pass-1");
                var app = env.Agent("rcli", "Customer-Pass-1");
                var src = env.Dir("src");
                var a = Path.Combine(src, "a.txt"); File.WriteAllText(a, "backed up");
                var b = Path.Combine(src, "b.txt"); File.WriteAllText(b, "b");
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "Files", Sources = { src } });
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);
                File.WriteAllText(a, "newer"); File.Delete(b);
                var args = new List<string> { "restore", "--home", app.Home.Dir, "--set", set.Id, "--password", "Customer-Pass-1" };
                Assert.Equal(3, Program.Main(args.ToArray()));
                Assert.Equal("newer", File.ReadAllText(a)); Assert.False(File.Exists(b));
                Assert.Equal(0, Program.Main(args.Concat(new[] { "--skip-existing" }).ToArray()));
                Assert.Equal("newer", File.ReadAllText(a)); Assert.Equal("b", File.ReadAllText(b));
                Assert.Equal(0, Program.Main(args.Concat(new[] { "--overwrite" }).ToArray()));
                Assert.Equal("backed up", File.ReadAllText(a));
            }
        }

        /// <summary>UI-Q1 on the restic engine (outside the pilot): the window's restore runs restic with --verify and the job
        /// reports the number of files restic read back and checked. Before: "Restored to …" with no check at all.</summary>
        [Fact]
        public void Window_ResticRestore_IsVerified_AndTheCountIsReported()
        {
            var exe = Environment.GetEnvironmentVariable("OB_RESTIC");
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) throw NotTested.Because("OB_RESTIC (the restic program) is not set");
            using (var env = new Env())
            {
                env.CreateUser("rrv", "Customer-Pass-1");
                var app = env.Agent("rrv", "Customer-Pass-1");
                var src = env.Dir("src");
                for (int i = 0; i < 3; i++) File.WriteAllText(Path.Combine(src, "f" + i + ".txt"), "content " + i);
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "W", Engine = "RESTIC", Sources = { src } });
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);
                var ui = StartUi(app);
                try
                {
                    Call(ui, "login", new Msg().Set("password", "Customer-Pass-1"));
                    var point = Call(ui, "points", null, "set=" + set.Id).List("points").First()["id"];
                    var target = env.Dir("restore");
                    var j = WaitJob(ui, Call(ui, "restore", new Msg().Set("set", set.Id).Set("point", point).Set("target", target).Set("overwrite", "0"))["job"]);
                    Assert.True(j["state"] == "ok", j["result"] + " " + j["detail"]);
                    Assert.Equal(3, j.Int("verified", -1));
                    Assert.Contains("verified 3", j["detail"]);
                    for (int i = 0; i < 3; i++) Assert.Equal("content " + i, File.ReadAllText(Path.Combine(target, Env.Rel(src), "f" + i + ".txt")));
                }
                finally { ui.Dispose(); }
            }
        }

        /// <summary>RS-06: the automatic restore test goes through RestoreFile, which now checks the disk as well — still passes on a
        /// good backup (the check must not break it).</summary>
        [Fact]
        public void RestoreTest_StillPasses_WithTheCheck()
        {
            using (var env = new Env())
            {
                env.CreateUser("rtest", "Customer-Pass-1");
                var app = env.Agent("rtest", "Customer-Pass-1");
                var src = env.Dir("src");
                for (int i = 0; i < 4; i++) File.WriteAllText(Path.Combine(src, "f" + i + ".txt"), "content " + i);
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "Files", Sources = { src } });
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);
                var t = app.RestoreTest(set.Id);
                Assert.True(t.Int("checked") > 0 && t.Int("ok") == t.Int("checked"), "restore test: " + t["ok"] + "/" + t["checked"]);
            }
        }
    }
}
