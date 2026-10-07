using System;
using System.Linq;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// BK-07 component, the result of a run stopped at its maximum duration: the agent's BackupRun alone against the stand-in
    /// server, its clock moved past the limit while the server receives the 3rd of 10 files. Contract (specs.py BK-07): "the
    /// run ends as stopped (not success)". The server's own texts call such a run stopped (Notify.cs: "stopped by the
    /// administrator / at its maximum duration"); a stop from the admin site is BS_STOP_BY_USER. Oracle: the result the agent
    /// reports and sends in its commit.
    /// </summary>
    public class PilotMaxDurationComponentTests
    {
        [Fact]
        public void AtTheMaximumDuration_TheRunIsReportedAsStopped_NotAsASuccess()
        {
            using (var rig = new AgentRig())
            {
                PilotStopComponentTests.Files(rig, 10);
                rig.Set.DurationHours = 1;
                var r = PilotStopComponentTests.RunWithJump(rig, 3, TimeSpan.FromHours(2));
                Assert.Equal(3, PilotRig.Objects(rig, r.Job).Count);                          // it did stop
                var commit = rig.Server.Of("commit").Single(c => c.Job == r.Job).Msg["result"];
                Assert.Equal(r.Result, commit);
                Assert.False(r.Result.StartsWith("BS_STOP_SUCCESS", StringComparison.Ordinal),
                    "a run stopped at its maximum duration with 7 of 10 files not backed up is reported as " + r.Result + " (a success) — the contract: \"the run ends as stopped (not success)\"");
            }
        }
    }
}
