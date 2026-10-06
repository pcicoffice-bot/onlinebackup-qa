using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>CFGBK-010: the server's settings are backed up every day — settings and profiles, never the backups.</summary>
    public class ConfigBackupTests
    {
        [Fact]
        public void SettingsBackup_NowAndDaily_WithACopy_WithoutTheData()
        {
            using (var env = new Env())
            {
                env.CreateUser("acme", "Customer-Pass-1");
                var app = env.Agent("acme", "Customer-Pass-1");
                var src = env.Dir("src"); File.WriteAllText(Path.Combine(src, "secret-data.txt"), "customer data");
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "Docs", Sources = { src } });
                app.Backup(set.Id);
                var admin = env.Admin();
                var copy = env.Dir("second-disk");
                Assert.Throws<AgentException>(() => admin.Call("POST", "/api/admin/configbackup", new Msg().Set("copyTo", "relative\\path")));
                admin.Call("POST", "/api/admin/configbackup", new Msg().Set("copyTo", copy));
                var r = admin.Call("POST", "/api/admin/configbackup/now");
                var name = r["made"];
                Assert.Single(r.List("backups"));
                Assert.True(File.Exists(Path.Combine(copy, name)));
                using (var z = ZipFile.OpenRead(Path.Combine(copy, name)))
                {
                    var names = z.Entries.Select(e => e.FullName).ToList();
                    Assert.Contains("system/conf/system.xml", names);
                    Assert.Contains("users/acme/db/Profile.xml", names);
                    Assert.Contains(names, n => n.StartsWith("users/acme/db/devices", StringComparison.Ordinal));
                    Assert.DoesNotContain(names, n => n.Contains("/files/") || n.Contains("/restic/") || n.Contains("secret-data"));
                }
                // the daily run makes one only when the last is a day old
                Assert.Null(env.Api.Maintenance(DateTime.UtcNow)["settingsBackup"]);
                Assert.NotNull(env.Api.Maintenance(DateTime.UtcNow.AddDays(1))["settingsBackup"]);
                Assert.Throws<AgentException>(() => admin.Call("GET", "/api/admin/configbackup/..%2Fconf%2Fsystem.xml"));
            }
        }
    }
}
