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
    /// <summary>TPL-010 set templates, BULK-010 actions on many customers, DASH-010 the dashboard's numbers.</summary>
    public class BulkTests
    {
        [Fact]
        public void Template_AppliedToManyCustomers_AndBulkActions_AndDashboard()
        {
            using (var env = new Env())
            {
                env.CreateUser("acme", "Customer-Pass-1"); env.CreateUser("beta", "Customer-Pass-1");
                var a = env.Agent("acme", "Customer-Pass-1", name: "a"); var b = env.Agent("beta", "Customer-Pass-1", name: "b");
                var sa = a.CreateSet(a.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "Files", Sources = { env.Dir("srcA") } });
                var sb = b.CreateSet(b.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "Files", Sources = { env.Dir("srcB") } });
                var admin = env.Admin();

                var tp = new BackupSetInfo { Name = "Office standard", Hour = 21, Minute = 30, Retention = new RetentionPolicy { Unit = "DAYS", Period = 90, Monthly = 12 }, Compression = "MAX", BandwidthKbps = 2048 };
                tp.Filters.Add(new FilterRule { Type = "START_WITH", Patterns = { "~$" } });
                admin.Call("POST", "/api/admin/templates", new Msg().Set("name", "Office standard").Set("type", "FILE").Set("set", tp.ToXml().ToString()));
                Assert.Single(admin.Call("GET", "/api/admin/templates").List("templates"));

                var r = admin.Call("POST", "/api/admin/bulk", new Msg().Set("action", "template").Set("template", "Office standard").Add("logins", new Msg().Set("login", "acme")).Add("logins", new Msg().Set("login", "beta")).Add("logins", new Msg().Set("login", "nobody")));
                Assert.Equal("2", r["done"]);
                Assert.Contains(r.List("results"), x => x["login"] == "nobody" && x["ok"] == "0");
                foreach (var app in new[] { a, b })
                {
                    var s = app.Sets().Single();
                    Assert.Equal(21, s.Hour); Assert.Equal(30, s.Minute); Assert.Equal(90, s.Retention.Period); Assert.Equal(2048, s.BandwidthKbps);
                    Assert.Contains(s.Filters, f => f.Type == "START_WITH");
                    Assert.NotEmpty(s.Sources);                                                  // the sources are the customer's own
                }
                admin.Call("POST", "/api/admin/bulk", new Msg().Set("action", "quota").Set("quotaGB", "50").Add("logins", new Msg().Set("login", "acme")).Add("logins", new Msg().Set("login", "beta")));
                Assert.Equal(50L * 1024 * 1024 * 1024, env.Api.UserStore.LoadProfile("beta").GetLong("QUOTA"));
                admin.Call("POST", "/api/admin/bulk", new Msg().Set("action", "run").Add("logins", new Msg().Set("login", "acme")));
                Assert.True(a.RunRequested(a.Sets().Single()));

                a.Backup(sa.Id);
                var d = admin.Call("GET", "/api/admin/dashboard");
                Assert.Equal("2", d["customers"]); Assert.Equal("2", d["sets"]); Assert.Equal("1", d["tasks"]); Assert.Equal("1", d["ok"]);
                Assert.Equal(14, d.List("days").Count);
                Assert.Equal("1", d.List("days").Last()["ok"]);
            }
        }
    }
}
