using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>SET-010/020/030: a customer's set from the admin site — settings, several computers, back up now / stop.</summary>
    public class SetControlTests
    {
        static BackupSetInfo NewSet(AgentApp app, string password, string source)
        {
            return app.CreateSet(app.Interactive(password, null), password, new BackupSetInfo { Name = "Files", Sources = { source } });
        }

        [Fact]
        public void Admin_ChangesSettings_KeyTypeEngineKept_AgentSeesThem()
        {
            using (var env = new Env())
            {
                env.CreateUser("acme", "Customer-Pass-1");
                var app = env.Agent("acme", "Customer-Pass-1", name: "fs01");
                var set = NewSet(app, "Customer-Pass-1", env.Dir("src"));
                var admin = env.Admin();
                var d = admin.Call("GET", "/api/admin/users/acme/sets/" + set.Id);
                var s = BackupSetInfo.FromXml(XElement.Parse(d["set"]));
                Assert.Equal(app.Home.Computer, d.List("computers").Single()["computer"]);
                s.Name = "File server"; s.Hour = 23; s.Minute = 30; s.DurationHours = 6; s.Days = "-MTWTF-";
                s.Retention = new RetentionPolicy { Unit = "JOBS", Period = 14, Monthly = 12 };
                s.Filters.Add(new FilterRule { Type = "START_WITH", Patterns = { "~$" } });
                s.Type = "MSSQL"; s.KeyCheck = "forged";                                  // never changed from the admin site
                admin.Call("POST", "/api/admin/users/acme/sets/" + set.Id, new Msg().Set("set", s.ToXml().ToString()));
                var now = app.Sets().Single();
                Assert.Equal("File server", now.Name); Assert.Equal(23, now.Hour); Assert.Equal(30, now.Minute); Assert.Equal(6, now.DurationHours); Assert.Equal("-MTWTF-", now.Days);
                Assert.Equal("JOBS", now.Retention.Unit); Assert.Equal(14, now.Retention.Period); Assert.Equal(12, now.Retention.Monthly);
                Assert.Contains(now.Filters, f => f.Type == "START_WITH" && f.Patterns.Contains("~$"));
                Assert.Equal("FILE", now.Type); Assert.Equal(set.KeyCheck, now.KeyCheck);
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);                // the key still works

                // no unlimited versions, no zero duration
                s.Retention = new RetentionPolicy { Period = 0 };
                Assert.Throws<AgentException>(() => admin.Call("POST", "/api/admin/users/acme/sets/" + set.Id, new Msg().Set("set", s.ToXml().ToString())));
            }
        }

        [Fact]
        public void BackUpNow_And_Stop_FromTheServer()
        {
            using (var env = new Env())
            {
                env.CreateUser("acme", "Customer-Pass-1");
                var app = env.Agent("acme", "Customer-Pass-1", name: "fs01");
                var src = env.Dir("src");
                for (int i = 0; i < 30; i++) File.WriteAllText(Path.Combine(src, "f" + i + ".txt"), "data " + i);
                var set = NewSet(app, "Customer-Pass-1", src);
                var admin = env.Admin();
                Assert.False(app.RunRequested(app.Sets().Single()));
                Assert.Equal("1", admin.Call("POST", "/api/admin/users/acme/sets/" + set.Id + "/run")["computers"]);
                Assert.True(app.RunRequested(app.Sets().Single()));                      // once
                Assert.False(app.RunRequested(app.Sets().Single()));

                // a stop asked before the run reached the files: what was sent is kept, nothing counts as deleted, no failure call
                var started = DateTime.UtcNow;
                admin.Call("POST", "/api/admin/users/acme/sets/" + set.Id + "/stop");
                Assert.True(app.StopCheck(app.Sets().Single(), started.AddSeconds(-5))());
                Assert.False(app.StopCheck(app.Sets().Single(), DateTime.UtcNow.AddSeconds(5))());   // an older stop does not stop a new run
                var run = new BackupRun(app.DeviceClient(), app.Home, app.Sets().Single(), app.Key(app.Sets().Single())) { StopRequested = () => true };
                run.Run();
                Assert.Equal("BS_STOP_BY_USER", run.Result);
                Assert.Equal(0, run.Deleted);
                Assert.Empty(env.Api.Calls.List("all"));
                var r2 = app.Backup(set.Id);
                Assert.Equal("BS_STOP_SUCCESS", r2.Result); Assert.Equal(30, r2.New);
            }
        }

        [Fact]
        public void SetCopiedToAnotherComputer_OwnPathsAndSettings_SharedKey()
        {
            using (var env = new Env())
            {
                env.CreateUser("acme", "Customer-Pass-1");
                var a = env.Agent("acme", "Customer-Pass-1", name: "a");
                var b = env.Agent("acme", "Customer-Pass-1", name: "b");
                var src = env.Dir("src"); File.WriteAllText(Path.Combine(src, "x.txt"), "x");
                var srcB = env.Dir("srcB"); File.WriteAllText(Path.Combine(srcB, "y.txt"), "y");
                var set = NewSet(a, "Customer-Pass-1", src);
                var admin = env.Admin();
                var path = "/api/admin/users/acme/sets/" + set.Id;
                var copyId = admin.Call("POST", path + "/addcomputer", new Msg().Set("computer", b.Home.Computer))["id"];
                Assert.Throws<AgentException>(() => admin.Call("POST", path + "/addcomputer", new Msg().Set("computer", b.Home.Computer)));
                var copy = b.Sets().Single(x => x.Id == copyId);
                Assert.Equal(set.Id, copy.Parent); Assert.Equal(b.Home.Computer, copy.Computer);
                Assert.Equal("Files — " + b.Home.Computer, copy.Name);                       // a set of its own, like Ahsay OBM
                Assert.Equal(b.Home.Computer, admin.Call("GET", "/api/admin/users/acme/sets/" + copyId)["computer"]);
                Assert.Single(admin.Call("GET", path).List("related"));

                // the copy gets its own paths; a change of one set does not touch the other
                var cs = BackupSetInfo.FromXml(XElement.Parse(admin.Call("GET", "/api/admin/users/acme/sets/" + copyId)["set"]));
                cs.Sources.Clear(); cs.Sources.Add(srcB); cs.Deselected.Add(Path.Combine(srcB, "tmp"));
                admin.Call("POST", "/api/admin/users/acme/sets/" + copyId, new Msg().Set("set", cs.ToXml().ToString()));
                var s = BackupSetInfo.FromXml(XElement.Parse(admin.Call("GET", path)["set"])); s.Hour = 3;
                admin.Call("POST", path, new Msg().Set("set", s.ToXml().ToString()));
                var nowCopy = b.Sets().Single(x => x.Id == copyId);
                Assert.Equal(new[] { srcB }, nowCopy.Sources); Assert.NotEqual(3, nowCopy.Hour); Assert.Equal(set.Id, nowCopy.Parent);
                Assert.Equal(new[] { src }, a.Sets().Single(x => x.Id == set.Id).Sources);
                var r = b.Backup(copyId);                                                   // B gets the key (key recovery is on) and backs up its own folder
                Assert.Equal("BS_STOP_SUCCESS", r.Result); Assert.Equal(1, r.New);
                Assert.Equal("1", admin.Call("POST", path + "/run")["computers"]);          // back up now: this set's computer only
            }
        }

        [Fact]
        public void Folders_OfTheComputer_ForTheTree()
        {
            using (var env = new Env())
            {
                env.CreateUser("acme", "Customer-Pass-1");
                var app = env.Agent("acme", "Customer-Pass-1", name: "fs01");
                var src = env.Dir("src"); Directory.CreateDirectory(Path.Combine(src, "A", "B", "C", "D"));
                NewSet(app, "Customer-Pass-1", src);
                var admin = env.Admin();
                var q = "/api/admin/users/acme/folders?computer=" + Uri.EscapeDataString(app.Home.Computer);
                Assert.Equal("", admin.Call("GET", q)["dirs"]);
                var now = DateTime.UtcNow;
                Assert.True(app.SendFolders(app.Profile(), now));
                Assert.False(app.SendFolders(app.Profile(), now.AddMinutes(5)));             // every 6 hours…
                var dirs = admin.Call("GET", q)["dirs"].Split('\n');
                Assert.Contains(src, dirs); Assert.Contains(Path.Combine(src, "A"), dirs);    // the set's sources one level down
                Assert.DoesNotContain(Path.Combine(src, "A", "B", "C"), dirs);

                // …or when a folder is opened in the admin site
                admin.Call("POST", "/api/admin/users/acme/browse", new Msg().Set("computer", app.Home.Computer).Set("path", Path.Combine(src, "A", "B")));
                Assert.Single(admin.Call("GET", q).List("waiting"));
                Assert.True(app.SendFolders(app.Profile(), DateTime.UtcNow.AddMinutes(1)));
                dirs = admin.Call("GET", q)["dirs"].Split('\n');
                Assert.Contains(Path.Combine(src, "A", "B", "C"), dirs); Assert.Contains(Path.Combine(src, "A", "B", "C", "D"), dirs);
                Assert.Empty(admin.Call("GET", q).List("waiting"));
                Assert.Throws<AgentException>(() => admin.Call("GET", "/api/admin/users/acme/folders?computer="));
                admin.Call("GET", "/api/admin/users/acme/folders?computer=" + Uri.EscapeDataString("../../x"));            // a name with slashes stays inside the customer's folder
                Assert.False(File.Exists(Path.Combine(env.Api.UserStore.UserDir("acme"), "..", "..", "x.txt")));
            }
        }

        [Fact]
        public void SeveralTimesADay_EachWithItsDays()
        {
            var s = new BackupSetInfo { Name = "x", Hour = 22, Minute = 0, Days = "SMTWTFS" };
            s.MoreSchedules.Add(new ScheduleSlot { Hour = 12, Minute = 30, Days = "-MTWTF-" });
            var back = BackupSetInfo.FromXml(s.ToXml());
            Assert.Single(back.MoreSchedules); Assert.Equal(12, back.MoreSchedules[0].Hour); Assert.Equal(30, back.MoreSchedules[0].Minute); Assert.Equal("-MTWTF-", back.MoreSchedules[0].Days);
            Assert.Equal(22, back.Hour); Assert.Equal("SMTWTFS", back.Days);
            // Monday 13:00: the 12:30 slot of today; Saturday 13:00: last night's 22:00 (no 12:30 on Saturday)
            Assert.Equal(new DateTime(2026, 10, 5, 12, 30, 0), AgentApp.LastSlot(back, new DateTime(2026, 10, 5, 13, 0, 0)));
            Assert.Equal(new DateTime(2026, 10, 9, 22, 0, 0), AgentApp.LastSlot(back, new DateTime(2026, 10, 10, 13, 0, 0)));
            Assert.Equal(new DateTime(2026, 10, 5, 22, 0, 0), AgentApp.LastSlot(back, new DateTime(2026, 10, 5, 23, 0, 0)));
        }
    }
}
