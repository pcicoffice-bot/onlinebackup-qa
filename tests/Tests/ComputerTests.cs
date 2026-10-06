using System;
using System.IO;
using System.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>COMP-010: a customer's computers — last connection and version, disconnect, move to another customer with the backups.</summary>
    public class ComputerTests
    {
        [Fact]
        public void List_Disconnect_MoveToAnotherCustomerWithTheBackups()
        {
            using (var env = new Env())
            {
                env.CreateUser("acme", "Customer-Pass-1");
                env.CreateUser("beta", "Customer-Pass-2");
                var a = env.Agent("acme", "Customer-Pass-1", name: "a");
                var b = env.Agent("acme", "Customer-Pass-1", name: "b");
                var src = env.Dir("srcB"); File.WriteAllText(Path.Combine(src, "x.txt"), "x");
                var setB = b.CreateSet(b.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "Files B", Sources = { src } });
                Assert.Equal("BS_STOP_SUCCESS", b.Backup(setB.Id).Result);
                a.Sets();
                var admin = env.Admin();

                var pcs = admin.Call("GET", "/api/admin/users/acme/computers").List("computers");
                Assert.Equal(2, pcs.Count);
                var pb = pcs.Single(c => c["name"] == b.Home.Computer);
                Assert.Equal("1", pb["connected"]); Assert.Equal("1", pb["sets"]); Assert.False(string.IsNullOrEmpty(pb["lastSeen"])); Assert.False(string.IsNullOrEmpty(pb["version"])); Assert.False(string.IsNullOrEmpty(pb["os"]));

                // disconnect: the computer stops, its backups stay
                Assert.Equal("1", admin.Call("POST", "/api/admin/users/acme/computers/disconnect", new Msg().Set("computer", a.Home.Computer))["revoked"]);
                Assert.Throws<AgentException>(() => a.Sets());
                Assert.Equal("0", admin.Call("GET", "/api/admin/users/acme/computers").List("computers").Single(c => c["name"] == a.Home.Computer)["connected"]);

                // move B to the other customer: the set and its backups go along; B signs in as beta and goes on
                Assert.Equal("1", admin.Call("POST", "/api/admin/users/acme/computers/move", new Msg().Set("computer", b.Home.Computer).Set("target", "beta"))["sets"]);
                Assert.Throws<AgentException>(() => b.Sets());
                Assert.Throws<AgentException>(() => admin.Call("POST", "/api/admin/users/acme/computers/move", new Msg().Set("computer", b.Home.Computer).Set("target", "nobody")));
                b.Register(env.Url, "beta", "Customer-Pass-2", null, b.Home.Computer);
                var moved = b.Sets().Single();
                Assert.Equal(setB.Id, moved.Id);
                File.WriteAllText(Path.Combine(src, "y.txt"), "y");
                var r = b.Backup(setB.Id);
                Assert.Equal("BS_STOP_SUCCESS", r.Result); Assert.Equal(1, r.New);                       // only the new file: the old backups came along
                Assert.DoesNotContain(admin.Call("GET", "/api/admin/users/acme/computers").List("computers"), c => c["name"] == b.Home.Computer && c["sets"] != "0");
            }
        }
    }
}
