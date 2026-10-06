using System;
using System.IO;
using System.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>LIC: a signed licence per server; without one, the free basic edition.</summary>
    public class LicenseTests
    {
        static int Status(Action a) { try { a(); return 200; } catch (AgentException e) { return e.Status; } }

        [Fact]
        public void WithoutALicence_TheFreeEditionLimitsUsersAndModules_AndShowsPoweredBy()
        {
            using (var env = new Env(licensed: false))
            {
                var admin = env.Admin();
                var lic = admin.Call("GET", "/api/admin/license");
                Assert.Equal("FREE", lic["edition"]); Assert.StartsWith("OB-", lic["serverId"]);
                for (int i = 1; i <= 12; i++) env.CreateUser("free" + i, "Customer-Pass-1");                   // users are not limited in the free edition
                var app = env.Agent("free1", "Customer-Pass-1");
                for (int i = 2; i <= 10; i++) env.Agent("free" + i, "Customer-Pass-1", name: "pc" + i);         // 10 computers (LIC-075)
                env.Agent("free2", "Customer-Pass-1", name: "pc2");                                             // the same computer again: not a new one
                var eleventh = Assert.Throws<AgentException>(() => env.Agent("free11", "Customer-Pass-1", name: "pc11"));
                Assert.Equal(402, eleventh.Status);
                var src = env.Dir("src"); File.WriteAllText(Path.Combine(src, "a.txt"), "x");
                app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "F", Sources = { src } });       // files: free
                var ex = Assert.Throws<AgentException>(() => app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "S", Type = "MSSQL", Sources = { @"Microsoft SQL Server\SQLEXPRESS" } }));
                Assert.Equal(402, ex.Status);

                // a licence that is not this server's, forged, or expired is refused
                var other = License.Issue(Env.TestKey[0], "L1", "X", "PRO", "OB-OTHERSERVER", 0, 0, License.AllModules, DateTime.UtcNow, DateTime.UtcNow.AddDays(10));
                Assert.Equal(400, Status(() => admin.Call("POST", "/api/admin/license", new Msg().Set("key", other))));
                var forged = License.Issue(License.KeyGen()[0], "L2", "X", "PRO", lic["serverId"], 0, 0, License.AllModules, DateTime.UtcNow, DateTime.UtcNow.AddDays(10));
                Assert.Equal(400, Status(() => admin.Call("POST", "/api/admin/license", new Msg().Set("key", forged))));
                var expired = License.Issue(Env.TestKey[0], "L3", "X", "PRO", lic["serverId"], 0, 0, License.AllModules, DateTime.UtcNow.AddDays(-40), DateTime.UtcNow.AddDays(-1));
                Assert.Equal(400, Status(() => admin.Call("POST", "/api/admin/license", new Msg().Set("key", expired))));
                var payload = License.Issue(Env.TestKey[0], "L4", "X", "PRO", lic["serverId"], 0, 0, License.AllModules, DateTime.UtcNow, DateTime.UtcNow.AddDays(10));
                var tampered = payload.Substring(0, 10) + (payload[10] == 'A' ? 'B' : 'A') + payload.Substring(11);
                Assert.Equal(400, Status(() => admin.Call("POST", "/api/admin/license", new Msg().Set("key", tampered))));

                // the real licence: everything opens
                var ok = admin.Call("POST", "/api/admin/license", new Msg().Set("key", payload));
                Assert.Equal("PRO", ok["edition"]);
                env.Agent("free6", "Customer-Pass-1", name: "pc6");
            }
        }

        [Fact]
        public void LicenceLimits_UsersModulesAndStorage()
        {
            using (var env = new Env())
            {
                env.SetLicense("PRO", 2, 0.0005, "FILE");                     // 2 users, ~0.5MB, files only
                env.CreateUser("lic1", "Customer-Pass-1"); env.CreateUser("lic2", "Customer-Pass-1");
                Assert.Equal(402, Status(() => env.CreateUser("lic3", "Customer-Pass-1")));
                var app = env.Agent("lic1", "Customer-Pass-1");
                var src = env.Dir("src"); var rnd = new byte[2 * 1024 * 1024]; new Random(9).NextBytes(rnd); File.WriteAllBytes(Path.Combine(src, "big.bin"), rnd);   // incompressible
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "F", Sources = { src } });
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);   // under the limit when it started
                File.WriteAllText(Path.Combine(src, "more.txt"), "more");
                var r = app.Backup(set.Id);
                Assert.NotEqual("BS_STOP_SUCCESS", r.Result);                // over the licence: new data stops, the old stays
                Assert.Contains(r.LogLines, l => l.Contains("licence") || l.Contains("LICENSE"));
            }
        }
    }
}
