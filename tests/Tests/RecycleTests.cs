using System;
using System.IO;
using System.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>DEL-010/020: deleting goes to a 14-day recycle bin; with two administrators a second one approves.</summary>
    public class RecycleTests
    {
        static int Status(Action a) { try { a(); return 200; } catch (AgentException e) { return e.Status; } }

        [Fact]
        public void OneAdministrator_DeletesAtOnce_ToTheRecycleBin_AndRestores()
        {
            using (var env = new Env())
            {
                env.CreateUser("acme", "Customer-Pass-1");
                var app = env.Agent("acme", "Customer-Pass-1");
                var src = env.Dir("src"); File.WriteAllText(Path.Combine(src, "a.txt"), "a");
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "Files", Sources = { src } });
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);
                var admin = env.Admin();

                Assert.Equal("1", admin.Call("POST", "/api/admin/users/acme/delete", new Msg().Set("set", set.Id))["deleted"]);   // one administrator: at once
                Assert.Empty(env.Api.UserStore.LoadProfile("acme").SetElements);
                var bin = admin.Call("GET", "/api/admin/recycle").List("items").Single();
                Assert.Equal("set", bin["kind"]); Assert.Equal("Files", bin["name"]);
                admin.Call("POST", "/api/admin/recycle/" + Uri.EscapeDataString(bin["id"]) + "/restore");
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);                                // the set and its backups are back

                admin.Call("POST", "/api/admin/users/acme/delete");
                Assert.DoesNotContain("acme", env.Api.UserStore.Logins());
                var ub = admin.Call("GET", "/api/admin/recycle").List("items").Single(x => x["kind"] == "user");
                Assert.Equal(0, env.Api.UserStore.PurgeRecycled(DateTime.UtcNow.AddDays(13)));            // kept 14 days
                admin.Call("POST", "/api/admin/recycle/" + Uri.EscapeDataString(ub["id"]) + "/restore");
                Assert.Contains("acme", env.Api.UserStore.Logins());
                app.Interactive("Customer-Pass-1", null);
                admin.Call("POST", "/api/admin/users/acme/delete");
                Assert.Equal(1, env.Api.UserStore.PurgeRecycled(DateTime.UtcNow.AddDays(15)));            // then erased
                Assert.Empty(admin.Call("GET", "/api/admin/recycle").List("items"));
            }
        }

        [Fact]
        public void TwoAdministrators_ASecondOneApproves()
        {
            using (var env = new Env())
            {
                env.CreateUser("acme", "Customer-Pass-1");
                var admin = env.Admin();
                admin.Call("POST", "/api/admin/staff", new Msg().Set("login", "dana").Set("password", "Dana-Pass-1"));
                var r = admin.Call("POST", "/api/admin/users/acme/delete");
                Assert.Equal("1", r["pending"]);
                Assert.Contains("acme", env.Api.UserStore.Logins());
                Assert.Equal(403, Status(() => admin.Call("POST", "/api/admin/deletes/" + r["id"] + "/approve")));   // not the one who asked
                var dana = TestAuth.Admin(env.Url, "dana", "Dana-Pass-1");
                dana.Call("POST", "/api/admin/deletes/" + r["id"] + "/approve");
                Assert.DoesNotContain("acme", env.Api.UserStore.Logins());
                Assert.Empty(admin.Call("GET", "/api/admin/deletes").List("requests"));

                // the rule can be switched off
                env.CreateUser("beta", "Customer-Pass-1");
                admin.Call("POST", "/api/admin/deletes/settings", new Msg().Set("dual", 0));
                Assert.Equal("1", admin.Call("POST", "/api/admin/users/beta/delete")["deleted"]);
            }
        }
    }
}
