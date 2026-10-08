using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Xml.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// Integration (the real server in-process, real HTTP, real files) of what the admin site sends to a computer:
    /// "Back up now" / "Stop" (specs.py AG-02) and edited settings (specs.py AG-03) — through a server restart and a
    /// server that is down, and an edit the server refuses.
    /// Oracle: the agent's run results and counts, and every restored file against the SHA-256 of its source.
    /// </summary>
    public class PilotAgentControlIntegrationTests
    {
        const string Password = "Customer-Pass-1";

        static BackupSetInfo NewSet(AgentApp app, string source) { return app.CreateSet(app.Interactive(Password, null), Password, new BackupSetInfo { Name = "Files", Sources = { source } }); }

        static string Sha(string f) { using (var s = File.OpenRead(f)) using (var h = SHA256.Create()) return Convert.ToHexString(h.ComputeHash(s)); }

        static Dictionary<string, string> Tree(string dir)
        {
            return Directory.Exists(dir) ? Directory.GetFiles(dir, "*", SearchOption.AllDirectories).ToDictionary(f => Path.GetRelativePath(dir, f), Sha) : new Dictionary<string, string>();
        }

        /// <summary>The newest point restored into a new folder: SHA-256 by path relative to the source.</summary>
        static Dictionary<string, string> Restored(Env env, AgentApp app, string setId, string source, string name)
        {
            var target = env.Dir(name);
            var r = app.RestoreFor(app.Interactive(Password, null), setId);
            r.Run(null, target, null, false);
            Assert.Equal(0, r.Failed);
            return Tree(AgentRig.Under(target, source));
        }

        /// <summary>What the edits here change, written out (a filter's ID is a new time stamp at every serialisation).</summary>
        static string Selection(BackupSetInfo s)
        {
            return "sources " + string.Join("|", s.Sources) + "; out " + string.Join("|", s.Deselected) + "; filters " + string.Join("|", s.Filters.Select(f => f.Type + " " + string.Join(",", f.Patterns)))
                + "; retention " + s.Retention.Unit + " " + s.Retention.Period;
        }

        static void Restart(Env env) { env.Api.Dispose(); env.Api = new Api(env.Cfg); env.Api.Start(env.Url); }

        static void Edit(Env env, string login, string setId, Action<BackupSetInfo> edit)
        {
            var admin = env.Admin();
            var s = BackupSetInfo.FromXml(XElement.Parse(admin.Call("GET", "/api/admin/users/" + login + "/sets/" + setId)["set"]));
            edit(s);
            admin.Call("POST", "/api/admin/users/" + login + "/sets/" + setId, new Msg().Set("set", s.ToXml().ToString()));
        }

        // ------------------------------------------------------------------ AG-02

        /// <summary>
        /// AG-02 recovery: "Back up now" pressed while the computer cannot reach the server for its run. The backup cannot
        /// start (the server is down) — the press must not be lost: when the server is back the computer still sees it
        /// pending, runs once, then no more; the point restores identical.
        /// </summary>
        [Fact]
        public void BackUpNow_WhenTheServerGoesDownBeforeTheRun_IsNotLost_ItRunsOnceWhenTheServerIsBack_AndRestoresIdentical()
        {
            using (var env = new Env())
            {
                env.CreateUser("bnow", Password);
                var app = env.Agent("bnow", Password, name: "fs01");
                var src = env.Dir("src");
                for (int i = 0; i < 5; i++) File.WriteAllText(Path.Combine(src, "f" + i + ".txt"), "data " + i);
                var set = NewSet(app, src);
                var want = Tree(src);

                Assert.Equal("1", env.Admin().Call("POST", "/api/admin/users/bnow/sets/" + set.Id + "/run")["computers"]);
                Assert.True(app.RunRequested(app.Sets().Single()));                                     // the service sees the press...
                env.Api.Dispose();                                                                       // ...and the server goes away before the run
                var e = Assert.Throws<AgentException>(() => app.Backup(set.Id));
                Assert.Equal("NETWORK", e.Code);

                env.Api = new Api(env.Cfg); env.Api.Start(env.Url);                                      // the server is back
                Assert.True(app.RunRequested(app.Sets().Single()), "the press of \"Back up now\" was lost while the server was down");
                var r = app.Backup(set.Id);
                Assert.Equal("BS_STOP_SUCCESS", r.Result);
                Assert.Equal(5, r.New);
                Assert.False(app.RunRequested(app.Sets().Single()));                                    // served: not again
                Assert.Equal(want, Restored(env, app, set.Id, src, "restore"));
            }
        }

        /// <summary>
        /// AG-02 failure: a "Stop" older than the run (pressed before it began), and a server restart in between, never stop
        /// the run: it sends every file, ends BS_STOP_SUCCESS and restores identical. Then a Stop newer than a run's start
        /// does stop it (control that the stop path is live).
        /// </summary>
        [Fact]
        public void AStopOlderThanTheRun_NeverStopsIt_EvenAcrossAServerRestart_ANewerStopDoes()
        {
            using (var env = new Env())
            {
                env.CreateUser("bstop", Password);
                var app = env.Agent("bstop", Password, name: "fs01");
                var src = env.Dir("src");
                for (int i = 0; i < 8; i++) File.WriteAllText(Path.Combine(src, "f" + i + ".txt"), "data " + i);
                var set = NewSet(app, src);
                var want = Tree(src);

                env.Admin().Call("POST", "/api/admin/users/bstop/sets/" + set.Id + "/stop");             // pressed before the run
                Restart(env);
                var r = app.Backup(set.Id);                                                              // the run starts after the stop
                Assert.True(r.Result == "BS_STOP_SUCCESS", r.Result + "\n" + string.Join("\n", r.LogLines));
                Assert.Equal(8, r.New);
                Assert.Equal(want, Restored(env, app, set.Id, src, "restore"));

                // control: a stop newer than a run's start stops it
                File.WriteAllText(Path.Combine(src, "new.txt"), "new");
                var started = DateTime.UtcNow.AddSeconds(-30);
                env.Admin().Call("POST", "/api/admin/users/bstop/sets/" + set.Id + "/stop");
                var s = app.Sets().Single();
                var run = new BackupRun(app.DeviceClient(), app.Home, s, app.Key(s)) { StopRequested = app.StopCheck(s, started) };
                run.Run();
                Assert.Equal("BS_STOP_BY_USER", run.Result);
                Assert.Equal(0, run.Deleted);
            }
        }

        // ------------------------------------------------------------------ AG-03

        /// <summary>
        /// AG-03 failure: an edit the server refuses (unlimited versions) changes nothing on the computer — its settings are
        /// still the last saved ones (here: with a filter that skips *.tmp) and the next run uses them: the restore holds
        /// exactly the files those settings select.
        /// </summary>
        [Fact]
        public void AnEditTheServerRefuses_LeavesTheComputersSettingsAsTheyWere_AndTheNextRunUsesThem()
        {
            using (var env = new Env())
            {
                env.CreateUser("sref", Password);
                var app = env.Agent("sref", Password, name: "fs01");
                var src = env.Dir("src");
                File.WriteAllText(Path.Combine(src, "a.txt"), "alpha"); File.WriteAllText(Path.Combine(src, "b.tmp"), "temporary");
                var set = NewSet(app, src);
                Edit(env, "sref", set.Id, s => s.Filters = new List<FilterRule> { new FilterRule { Type = "WILDCARD", Patterns = { "*.tmp" }, ApplyFile = true } });
                var saved = Selection(app.Sets().Single());
                Assert.Contains("WILDCARD *.tmp", saved);                                                // control: the accepted edit arrived

                Assert.Throws<AgentException>(() => Edit(env, "sref", set.Id, s => { s.Filters.Clear(); s.Retention = new RetentionPolicy { Period = 0 }; }));
                Assert.Equal(saved, Selection(app.Sets().Single()));                                    // nothing of the refused edit arrived

                var r = app.Backup(set.Id);
                Assert.Equal("BS_STOP_SUCCESS", r.Result);
                Assert.Equal(1, r.New);
                var want = new Dictionary<string, string> { { "a.txt", Sha(Path.Combine(src, "a.txt")) } };
                Assert.Equal(want, Restored(env, app, set.Id, src, "restore"));
            }
        }

        /// <summary>
        /// AG-03 recovery: an edit saved just before the server restarts reaches the computer after the restart — the next
        /// run backs up the added source, leaves out the folder taken out, and the newest point restores to exactly that.
        /// </summary>
        [Fact]
        public void AnEditSavedBeforeTheServerRestarts_ReachesTheComputerAfterIt_AndTheNextRunRestoresToExactlyTheNewSelection()
        {
            using (var env = new Env())
            {
                env.CreateUser("srst", Password);
                var app = env.Agent("srst", Password, name: "fs01");
                var src = env.Dir("src"); var more = env.Dir("more");
                Directory.CreateDirectory(Path.Combine(src, "Archive"));
                File.WriteAllText(Path.Combine(src, "doc.txt"), "document"); File.WriteAllText(Path.Combine(src, "Archive", "c.bin"), "cache");
                File.WriteAllText(Path.Combine(more, "added.txt"), "added on the server");
                var set = NewSet(app, src);
                var r0 = app.Backup(set.Id);
                Assert.Equal("BS_STOP_SUCCESS", r0.Result); Assert.Equal(2, r0.New);                    // control: the folder taken out later was backed up

                Edit(env, "srst", set.Id, s => { s.Sources.Add(more); s.Deselected.Add(Path.Combine(src, "Archive")); });
                Restart(env);
                var r = app.Backup(set.Id);
                Assert.True(r.Result == "BS_STOP_SUCCESS", r.Result + "\n" + string.Join("\n", r.LogLines));
                Assert.True(r.New == 1 && r.Deleted == 1, "new " + r.New + ", deleted " + r.Deleted + "; the set on the computer: " + app.Sets().Single().ToXml() + "\n" + string.Join("\n", r.LogLines));
                Assert.Equal(new Dictionary<string, string> { { "doc.txt", Sha(Path.Combine(src, "doc.txt")) } }, Restored(env, app, set.Id, src, "restore"));
                Assert.Equal(new Dictionary<string, string> { { "added.txt", Sha(Path.Combine(more, "added.txt")) } }, Restored(env, app, set.Id, more, "restore2"));
            }
        }
    }
}
