using System;
using System.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>Bug 94 (Windows run 15, W17): the client window creates a System State set with no folders (there are none to
    /// choose: wbadmin backs up the system state), and the server refused every set without folders unless its type was a
    /// "none chosen = all" type - System State was not one. So a System State set (a free-edition module) could not be made.
    /// Real server, real agent API.</summary>
    public class SystemStateSetIntegrationTests
    {
        [Fact]
        public void ASystemStateSet_WithoutFolders_IsCreated_AndKeepsItsType()
        {
            using (var env = new Env())
            {
                env.CreateUser("sstate", "Customer-Pass-1");
                var app = env.Agent("sstate", "Customer-Pass-1");
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "System State", Type = "SYSTEMSTATE" });
                Assert.False(string.IsNullOrEmpty(set.Id));
                var back = app.Sets().Single(s => s.Id == set.Id);
                Assert.Equal("SYSTEMSTATE", back.Type);
                Assert.Empty(back.Sources);
            }
        }

        [Fact]
        public void AFilesSet_WithoutFolders_IsStillRefused()
        {
            using (var env = new Env())
            {
                env.CreateUser("sstate2", "Customer-Pass-1");
                var app = env.Agent("sstate2", "Customer-Pass-1");
                var e = Assert.ThrowsAny<Exception>(() => app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "Files", Type = "FILE" }));
                Assert.Contains("No folders were chosen", e.Message);
            }
        }
    }
}
