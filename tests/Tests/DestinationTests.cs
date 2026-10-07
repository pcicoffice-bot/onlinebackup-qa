using System;
using System.IO;
using System.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>DEST-010: server only, server + local copy, local only.</summary>
    public class DestinationTests
    {
        [Fact]
        public void LocalOnly_And_ServerPlusLocalCopy_WithRestic()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OB_RESTIC"))) throw NotTested.Because("OB_RESTIC (the restic program) is not set");   // needs the restic program
            using (var env = new Env())
            {
                env.CreateUser("acme", "Customer-Pass-1", 5);
                var app = env.Agent("acme", "Customer-Pass-1");
                var src = env.Dir("src"); File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
                var nas = env.Dir("nas");
                var session = app.Interactive("Customer-Pass-1", null);
                var local = app.CreateSet(session, "Customer-Pass-1", new BackupSetInfo { Name = "Local", Sources = { src }, Engine = "RESTIC", DestMode = "LOCAL", LocalCopyPath = nas });
                var r = app.Backup(local.Id);
                Assert.True(r.Result == "BS_STOP_SUCCESS", string.Join("\n", r.LogLines));
                Assert.True(Directory.Exists(Path.Combine(nas, "restic-" + local.Id, "snapshots")));
                Assert.False(Directory.Exists(Path.Combine(env.Api.UserStore.UserDir("acme"), "restic", local.Id, "snapshots")) && Directory.GetFiles(Path.Combine(env.Api.UserStore.UserDir("acme"), "restic", local.Id, "snapshots")).Any());
                Assert.Contains(env.Admin().Call("GET", "/api/admin/tasks").List("tasks"), t => t["set"] == local.Id && t["status"] == "ok");   // the server still knows

                var both = app.CreateSet(session, "Customer-Pass-1", new BackupSetInfo { Name = "Both", Sources = { src }, Engine = "RESTIC", DestMode = "BOTH", LocalCopyPath = nas });
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(both.Id).Result);
                Assert.NotEmpty(Directory.GetFiles(Path.Combine(nas, "restic-" + both.Id, "snapshots")));
                Assert.NotEmpty(Directory.GetFiles(Path.Combine(env.Api.UserStore.UserDir("acme"), "restic", both.Id, "snapshots")));

                Assert.Throws<AgentException>(() => app.CreateSet(session, "Customer-Pass-1", new BackupSetInfo { Name = "Bad", Sources = { src }, DestMode = "LOCAL", LocalCopyPath = nas }));   // own engine: not local-only
            }
        }
    }
}
