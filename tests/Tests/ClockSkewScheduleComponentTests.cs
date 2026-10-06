using System;
using System.IO;
using System.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// D-5: the computer's clock is ahead of the server's. The day's slot ran and succeeded; the run's id is the server's
    /// time (here: 3 hours behind the computer). The slot must count as done — not run again after the missed-run delay.
    /// Oracle: AgentApp.Due on the computer's own clock, right after the run, with the run id as the server gave it.
    /// </summary>
    public class ClockSkewScheduleComponentTests
    {
        [Theory]
        [InlineData(0)]     // control: same clocks
        [InlineData(3)]     // the server's clock 3 hours behind the computer
        public void ADailySlotDoneOnce_IsNotDueAgain_WhenTheServerClockIsBehind(int hoursBehind)
        {
            using (var env = new Env())
            {
                env.CreateUser("skew1", "Customer-Pass-1");
                var app = env.Agent("skew1", "Customer-Pass-1");
                var src = env.Dir("src"); File.WriteAllText(Path.Combine(src, "a.txt"), "a");
                var slot = DateTime.Now.AddMinutes(-30);
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1",
                    new BackupSetInfo { Name = "S", Sources = { src }, Hour = slot.Hour, Minute = slot.Minute, MissedDelayMinutes = 0, Vss = false });
                Assert.True(app.Due(set, DateTime.Now));                                 // control: the slot is not done yet
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);

                // the run id as a server whose clock is behind would have stamped it
                var st = new LocalState(app.Home.SetDir(set.Id));
                DateTime job; Assert.True(RunId.TryParse(st.LastSuccess, out job));
                st.LastSuccess = RunId.From(job.AddHours(-hoursBehind)); st.Save();

                Assert.False(app.Due(set, DateTime.Now), "the slot ran and succeeded, but is due again (the server's clock is " + hoursBehind + " h behind)");
                Assert.False(app.Due(set, DateTime.Now.AddMinutes(20)));
            }
        }
    }
}
