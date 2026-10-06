using System;
using System.IO;
using System.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// I-4: the automatic restore test runs only on a backup that ended well. A native run stopped by the technician
    /// commits what it sent; a restore test of that partial point was recorded as "passed" next to "no completed backup".
    /// Oracle: the agent's own decision (RestoreTestDue) after a real stopped run, then after a real successful run.
    /// </summary>
    public class RestoreTestDueComponentTests
    {
        [Fact]
        public void AfterAStoppedRun_NoRestoreTest_AfterASuccessfulRun_ARestoreTest()
        {
            using (var env = new Env())
            {
                env.CreateUser("rtdue", "Customer-Pass-1");
                var app = env.Agent("rtdue", "Customer-Pass-1");
                var src = env.Dir("src");
                for (int i = 0; i < 5; i++) File.WriteAllText(Path.Combine(src, "f" + i + ".txt"), "file " + i);
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "R", Sources = { src }, Vss = false });

                var users = env.Api.UserStore;
                lock (users.ProfileLock) { var p = users.LoadProfile("rtdue"); p.FindSet(set.Id).SetAttributeValue("STOP_REQUEST", RunId.UnixMs(DateTime.UtcNow.AddMinutes(10))); users.SaveProfile("rtdue", p); }
                var stopped = app.Backup(set.Id);
                Assert.Equal("BS_STOP_BY_USER", stopped.Result);
                Assert.False(app.RestoreTestDue(app.Sets().First(s => s.Id == set.Id), DateTime.UtcNow), "a restore test is due after a run that was stopped");

                lock (users.ProfileLock) { var p = users.LoadProfile("rtdue"); p.FindSet(set.Id).SetAttributeValue("STOP_REQUEST", null); users.SaveProfile("rtdue", p); }
                Assert.StartsWith("BS_STOP_SUCCESS", app.Backup(set.Id).Result);
                Assert.True(app.RestoreTestDue(app.Sets().First(s => s.Id == set.Id), DateTime.UtcNow), "control: after a successful run the restore test is due");
            }
        }
    }
}
