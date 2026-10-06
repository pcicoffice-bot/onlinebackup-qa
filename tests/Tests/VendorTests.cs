using System;
using System.IO;
using System.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>VND: IT companies on one server — own administrators, own customers only, limits, own branding.</summary>
    public class VendorTests
    {
        static Client Login(Env env, string login, string password)
        {
            return TestAuth.Admin(env.Url, login, password, 0);
        }
        static int Status(Action a) { try { a(); return 200; } catch (AgentException e) { return e.Status; } }

        [Fact]
        public void EachVendorSeesOnlyItsCustomers_WithinItsLimits_AndMailsCarryItsBrand()
        {
            using (var smtp = new FakeSmtp())
            using (var env = new Env())
            {
                var sys = env.Admin();
                sys.Call("POST", "/api/admin/settings", new Msg().Set("smtpSet", "1").Set("senderEmail", "backup@example.invalid").Set("contactsSet", "1")
                    .Add("smtp", new Msg().Set("host", "127.0.0.1").Set("port", smtp.Port).Set("security", "NONE"))
                    .Add("contacts", new Msg().Set("name", "Ops").Set("email", "ops@example.invalid")));
                sys.Call("POST", "/api/admin/vendors", new Msg().Set("id", "acme-it").Set("name", "Acme IT").Set("maxUsers", 2).Set("maxQuotaGB", "3")
                    .Set("brandPRODUCT", "AcmeBackup Pro").Set("brandEMAIL", "noc@acme.invalid").Set("brandCOLOR", "#336699"));
                sys.Call("POST", "/api/admin/vendors/acme-it/admins", new Msg().Set("login", "acme-admin").Set("password", "Acme-Admin-Pass-1"));
                sys.Call("POST", "/api/admin/vendors", new Msg().Set("id", "beta").Set("name", "Beta"));
                sys.Call("POST", "/api/admin/vendors/beta/admins", new Msg().Set("login", "beta-admin").Set("password", "Beta-Admin-Pass-1"));
                Assert.Equal(409, Status(() => sys.Call("POST", "/api/admin/vendors/beta/admins", new Msg().Set("login", "acme-admin").Set("password", "Another-Pass-11"))));
                env.CreateUser("direct", "Customer-Pass-1");                                    // a customer of the server owner itself

                var acme = Login(env, "acme-admin", "Acme-Admin-Pass-1");
                Func<string, double, int> create = (l, gb) => Status(() => acme.Call("POST", "/api/admin/users", new Msg().Set("login", l).Set("password", "Customer-Pass-1")
                    .Set("alias", l).Set("quotaGB", gb).Set("email", l + "@cust.invalid")));
                Assert.Equal(200, create("acme-c1", 1));
                Assert.Equal(409, create("acme-c2", 2.5));                                        // 1 + 2.5 > 3GB of the vendor
                Assert.Equal(200, create("acme-c2", 2));
                Assert.Equal(409, create("acme-c3", 0.1));                                        // 2 users in the licence

                var seen = acme.Call("GET", "/api/admin/users").List("users").Select(u => u["login"]).OrderBy(x => x).ToList();
                Assert.Equal(new[] { "acme-c1", "acme-c2" }, seen);
                Assert.Equal(3, sys.Call("GET", "/api/admin/users").List("users").Count);
                Assert.Empty(Login(env, "beta-admin", "Beta-Admin-Pass-1").Call("GET", "/api/admin/users").List("users"));

                // nothing outside its own customers
                Assert.Equal(403, Status(() => acme.Call("POST", "/api/admin/users/direct/unlock")));
                Assert.Equal(403, Status(() => acme.Call("GET", "/api/admin/settings")));
                Assert.Equal(403, Status(() => acme.Call("GET", "/api/admin/vendors")));
                Assert.Equal(403, Status(() => acme.Call("POST", "/api/admin/maintenance")));
                Assert.Equal(200, Status(() => acme.Call("POST", "/api/admin/users/acme-c1/unlock")));
                Assert.Equal(401, Status(() => Login(env, "acme-admin", "wrong-password-1")));

                // its own branding, and its customers' mails carry it (not the server's)
                acme.Call("POST", "/api/admin/brand", new Msg().Set("brandCOMPANY", "Acme IT Ltd").Set("brandPHONE", "03-5550000"));
                var v = sys.Call("GET", "/api/admin/vendors").List("vendors").First(x => x["id"] == "acme-it");
                Assert.Equal("Acme IT Ltd", v["brandCOMPANY"]); Assert.Equal("2", v["users"]);
                var app = env.Agent("acme-c1", "Customer-Pass-1");
                var src = env.Dir("src"); File.WriteAllText(Path.Combine(src, "a.txt"), "x");
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "F", Sources = { src } });
                app.Backup(set.Id);
                Assert.Contains("acme-c1@cust.invalid", smtp.All);
                Assert.True(smtp.Has("AcmeBackup Pro"), "vendor product name in the report");
                Assert.True(smtp.Has("Acme IT Ltd"), "vendor company in the report");

                // alerts about its customers reach the vendor too
                int before = smtp.Messages.Count;
                sys.Call("POST", "/api/admin/maintenance", new Msg().Set("now", RunId.From(DateTime.UtcNow.AddDays(3))));
                Assert.True(smtp.Messages.Count > before);
                Assert.Contains("noc@acme.invalid", smtp.All);

                // a disabled vendor: its administrators cannot sign in
                sys.Call("POST", "/api/admin/vendors", new Msg().Set("id", "beta").Set("disabled", "1"));
                Assert.Equal(401, Status(() => Login(env, "beta-admin", "Beta-Admin-Pass-1")));
            }
        }
    }
}
