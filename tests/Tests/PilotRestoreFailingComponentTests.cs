using System;
using System.IO;
using OnlineBackup.Agent;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// RS-06 contract: "the result reaches the server". The agent marks the restore test as done (last-restore-test.txt)
    /// when it DECIDES to run it (RestoreTestDue), before the test has run and been reported. A test whose report the server
    /// could not take (busy for a moment, the line cut) is then not tried again for 30 days, and the server has no result.
    /// Oracle: the stand-in server's own list of reports received; the agent's own RestoreTestDue an hour later.
    /// </summary>
    public partial class PilotRestoreComponentTests
    {
        [Fact]
        public void RS06_AReportTheServerDidNotTake_IsTriedAgain_NotMarkedDoneFor30Days()
        {
            using (var server = new StubServer())
            {
                var fx = Make(server, Sources(2));
                BackedUpWell(fx.App, fx.Set);
                var now = new DateTime(2026, 4, 1, 3, 0, 0, DateTimeKind.Utc);
                Assert.True(fx.App.RestoreTestDue(fx.Set, now));                            // the scheduler decides to run it ...
                server.RefuseReport = true;
                Assert.Throws<AgentException>(() => fx.App.RestoreTest(fx.Set.Id));        // ... and its report is not taken
                Assert.Empty(server.RestoreTests);                                          // the server has no result
                Assert.Empty(TempLeft(fx.App));
                server.RefuseReport = false;
                Assert.True(fx.App.RestoreTestDue(fx.Set, now.AddHours(1)),
                    "the restore test whose result never reached the server is not due again for 30 days (last-restore-test.txt was written before it ran)");
            }
        }
    }
}
