using System.IO;
using System.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>DEF-010: the defaults for new customers and new sets, set by the administrator.</summary>
    public class DefaultsTests
    {
        [Fact]
        public void NewCustomerAndNewSet_StartFromTheAdministratorsDefaults_AChosenTimeIsKept()
        {
            using (var env = new Env())
            {
                var admin = env.Admin();
                var d = admin.Call("GET", "/api/admin/defaults");
                Assert.Equal("30", d["retentionDays"]);                                   // the product's default: 30 days
                admin.Call("POST", "/api/admin/defaults", new Msg().Set("quotaGB", 20).Set("quotaType", "COMPRESSED").Set("maxSets", 3).Set("saveKey", 1).Set("requireTotp", 0)
                    .Set("hour", 1).Set("minute", 30).Set("retentionDays", 45).Set("logDays", 90).Set("compression", "FAST").Set("bandwidth", 2048).Set("vss", 1).Set("runMissed", 0).Set("runMissedNet", 1).Set("skipSystem", 1)
                    .Set("can_add_sets", 1).Set("can_edit_sources", 1).Set("can_edit_schedule", 0).Set("can_edit_retention", 0).Set("can_edit_destination", 0).Set("can_edit_options", 0));
                Assert.Equal("45", admin.Call("GET", "/api/admin/defaults")["retentionDays"]);
                Assert.Throws<AgentException>(() => admin.Call("POST", "/api/admin/defaults", new Msg().Set("quotaGB", 20).Set("retentionDays", 0)));

                env.CreateUser("newco", "Customer-Pass-1");
                var u = admin.Call("GET", "/api/admin/users").List("users").Single(x => x["login"] == "newco");
                var app = env.Agent("newco", "Customer-Pass-1");
                var s1 = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "Docs", Sources = { env.Dir("a") } });
                var got = app.Sets().Single(x => x.Id == s1.Id);
                Assert.Equal(45, got.Retention.Period); Assert.Equal(1, got.Hour); Assert.Equal(30, got.Minute);
                Assert.Equal("FAST", got.Compression); Assert.Equal(2048, got.BandwidthKbps); Assert.False(got.RunMissed); Assert.Equal(90, got.LogRetentionDays);
                Assert.Contains(got.Filters, f => f.Patterns.Contains("pagefile.sys"));
                // a time the customer chose is kept
                var s2 = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "Mail", Hour = 3, Minute = 15, Sources = { env.Dir("b") } });
                var got2 = app.Sets().Single(x => x.Id == s2.Id);
                Assert.Equal(3, got2.Hour); Assert.Equal(15, got2.Minute); Assert.Equal(45, got2.Retention.Period);
                // the customer: at most 3 sets, may not change the schedule
                var prof = Directory.GetFiles(env.Root, "Profile.xml", SearchOption.AllDirectories).Select(f => File.ReadAllText(f)).Single(x => x.Contains("newco"));
                Assert.Contains("MAX_BACKUP_SET=\"3\"", prof); Assert.Contains("CAN_EDIT_SCHEDULE=\"N\"", prof);
                Assert.NotNull(u);
            }
        }
    }
}
