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
    /// <summary>
    /// Bug 103 (BK-07): a backup cut at its maximum duration was reported as BS_STOP_SUCCESS_WITH_WARNING - a success - in
    /// both engines. It is now BS_STOP_BY_USER ("stopped before the end", as the server's mail, log and screens name it) with
    /// "stop": "duration" in the report; the service calls still count it as before (a warning), so a set that never finishes
    /// within its window does not go quiet, while a stop from the admin site still changes nothing.
    /// </summary>
    public class Bug103DurationStopTests
    {
        static string ResticExe { get { return Environment.GetEnvironmentVariable("OB_RESTIC"); } }
        static bool HaveRestic { get { return !string.IsNullOrEmpty(ResticExe) && File.Exists(ResticExe); } }

        static void Change(Env env, string login, string setId, Action<BackupSetInfo> edit)
        {
            var admin = env.Admin();
            var s = BackupSetInfo.FromXml(XElement.Parse(admin.Call("GET", "/api/admin/users/" + login + "/sets/" + setId)["set"]));
            edit(s);
            admin.Call("POST", "/api/admin/users/" + login + "/sets/" + setId, new Msg().Set("set", s.ToXml().ToString()));
        }

        static int Warns(Env env, string login, string setId) { return (int?)env.Api.UserStore.LoadProfile(login).FindSet(setId).Attribute("TICKET_WARNS") ?? 0; }

        [Fact]
        public void ServiceCalls_ADurationStop_CountsAsAWarning_AnAdminStop_ChangesNothing()
        {
            using (var env = new Env())
            {
                env.CreateUser("acme", "Customer-Pass-1");
                var t = env.Api.Calls;
                t.SaveSettings(new Msg().Set("fail", 3).Set("warn", 2));
                var p = env.Api.UserStore.LoadProfile("acme");
                int f = 0, w = 0;
                Assert.Null(t.BackupResult("acme", "S1", "Files", "PC", "BS_STOP_BY_USER", ref f, ref w, null, p));
                Assert.Null(t.BackupResult("acme", "S1", "Files", "PC", "BS_STOP_BY_USER", ref f, ref w, null, p));
                Assert.Equal(0, w);                                                                      // stopped on purpose: nothing
                Assert.Null(t.BackupResult("acme", "S1", "Files", "PC", "BS_STOP_BY_USER", ref f, ref w, null, p, byDuration: true));
                Assert.Equal(1, w);
                Assert.NotNull(t.BackupResult("acme", "S1", "Files", "PC", "BS_STOP_BY_USER", ref f, ref w, null, p, byDuration: true));   // 2 in a row: a call
                Assert.Single(t.List("open"));
            }
        }

        /// <summary>Native engine through the real server: the result, the commit's marker and the service-call counter.</summary>
        [Fact]
        public void Native_ADurationStop_IsStopped_TheServerCountsIt_TheNextRunSucceeds()
        {
            using (var env = new Env())
            {
                env.CreateUser("d103", "Customer-Pass-1");
                var app = env.Agent("d103", "Customer-Pass-1", name: "fs01");
                var src = env.Dir("src");
                for (int i = 0; i < 40; i++) File.WriteAllText(Path.Combine(src, "f" + i.ToString("00") + ".txt"), "data " + i);
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "Files", Sources = { src } });
                Change(env, "d103", set.Id, s => s.DurationHours = 1);
                int calls = 0; var t0 = DateTime.UtcNow;
                app.RunClock = () => ++calls > 60 ? t0.AddHours(2) : t0;
                var r1 = app.Backup(set.Id);
                app.RunClock = null;
                Assert.True(r1.New < 40, "did not stop: " + r1.New + " files");
                Assert.Equal("BS_STOP_BY_USER", r1.Result);
                Assert.Equal(1, Warns(env, "d103", set.Id));
                var r2 = app.Backup(set.Id);
                Assert.Equal("BS_STOP_SUCCESS", r2.Result);
                Assert.Equal(40, r1.New + r2.New);
                Assert.Equal(0, Warns(env, "d103", set.Id));
            }
        }

        /// <summary>The restic engine: the same result and marker. The stop is checked once a second while restic runs, so the
        /// source is big enough to take longer; if restic still finished first, the stop was not exercised: NOT TESTED.</summary>
        [Fact]
        public void Restic_ADurationStop_IsStopped_TheServerCountsIt()
        {
            if (!HaveRestic) throw NotTested.Because("OB_RESTIC (the restic program) is not set");
            using (var env = new Env())
            {
                env.CreateUser("r103", "Customer-Pass-1");
                var app = env.Agent("r103", "Customer-Pass-1", name: "fs01");
                var src = env.Dir("src");
                var rnd = new Random(103);
                for (int i = 0; i < 8; i++) { var b = new byte[16 << 20]; rnd.NextBytes(b); File.WriteAllBytes(Path.Combine(src, "big" + i + ".bin"), b); }
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "R", Engine = "RESTIC", Sources = { src } });
                Change(env, "r103", set.Id, s => s.DurationHours = 1);
                var t0 = DateTime.UtcNow; bool first = true;
                app.RunClock = () => { if (first) { first = false; return t0; } return t0.AddHours(2); };   // the start, then past the limit
                var r1 = app.Backup(set.Id);
                app.RunClock = null;
                if (!r1.LogLines.Any(l => l.Contains("maximum duration")))
                    throw NotTested.Because("restic finished before the first stop check (" + r1.Result + ")");
                Assert.Equal("BS_STOP_BY_USER", r1.Result);
                Assert.Equal(1, Warns(env, "r103", set.Id));
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);
            }
        }
    }
}
