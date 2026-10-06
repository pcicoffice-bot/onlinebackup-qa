using System;
using System.IO;
using System.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>TASKS-010 every run of the last 24 hours; TIME-010 the server's time zone.</summary>
    public class TasksTests
    {
        [Fact]
        public void Tasks_Of24Hours_WithStatusAndCounts()
        {
            using (var env = new Env())
            {
                env.CreateUser("acme", "Customer-Pass-1");
                var app = env.Agent("acme", "Customer-Pass-1", name: "fs01");
                var src = env.Dir("src"); File.WriteAllText(Path.Combine(src, "a.txt"), "a");
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "Files", Sources = { src } });
                app.Backup(set.Id);
                var stopped = new BackupRun(app.DeviceClient(), app.Home, app.Sets().Single(), app.Key(app.Sets().Single())) { StopRequested = () => true }; stopped.Run();
                app.RestoreTest(set.Id);
                var t = env.Admin().Call("GET", "/api/admin/tasks");
                Assert.Equal("3", t["total"]);
                var rows = t.List("tasks");
                Assert.Contains(rows, r => r["kind"] == "Backup" && r["status"] == "ok" && r["new"] == "1" && r["setName"] == "Files" && r["computer"] == app.Home.Computer && r["log"] != null);
                Assert.Contains(rows, r => r["kind"] == "Backup" && r["status"] == "stopped");
                Assert.Contains(rows, r => r["kind"] == "RestoreTest");
                Assert.True(rows[0].Long("time") >= rows[2].Long("time"));                    // newest first
                Assert.Empty(env.Api.Runs.Since(DateTime.UtcNow.AddHours(1), DateTime.UtcNow.AddHours(2)));
            }
        }

        [Fact]
        public void TimeZone_ByCountry()
        {
            using (var env = new Env())
            {
                var admin = env.Admin();
                var r = admin.Call("POST", "/api/admin/time", new Msg().Set("country", "IL").Set("ntp", "time.windows.com").Set("ntpOn", 1));
                Assert.Equal("Asia/Jerusalem", r["zone"]); Assert.Equal("time.windows.com", r["ntp"]);
                var summer = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
                Assert.Equal(15, TimeSettings.Local(env.Cfg, summer).Hour);                    // UTC+3 in summer
                Assert.Equal(14, TimeSettings.Local(env.Cfg, new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc)).Hour);
                Assert.Throws<AgentException>(() => admin.Call("POST", "/api/admin/time", new Msg().Set("zone", "Mars/Olympus")));
                Assert.Throws<AgentException>(() => admin.Call("POST", "/api/admin/time", new Msg().Set("ntp", "x; rm -rf /")));
            }
        }

        [Fact]
        public void ActiveBackups_ReportedWhileRunning_GoneWhenDone()
        {
            using (var env = new Env())
            {
                env.CreateUser("acme", "Customer-Pass-1");
                var app = env.Agent("acme", "Customer-Pass-1", name: "fs01");
                var src = env.Dir("src"); File.WriteAllText(Path.Combine(src, "a.txt"), "a");
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "Files", Sources = { src } });
                app.DeviceClient().Call("POST", "/api/sets/" + set.Id + "/progress", new Msg().Set("job", "2026-10-04-10-00-00").Set("files", 12).Set("bytes", 4096).Set("current", "C:\\a.txt").Set("percent", 40));
                var live = env.Admin().Call("GET", "/api/admin/live").List("live");
                Assert.Single(live);
                Assert.Equal("40", live[0]["percent"]); Assert.Equal("Files", live[0]["setName"]); Assert.Equal(app.Home.Computer, live[0]["computer"]);
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);                    // the run reports itself, then finishes
                Assert.Empty(env.Admin().Call("GET", "/api/admin/live").List("live"));
            }
        }
    }
}
