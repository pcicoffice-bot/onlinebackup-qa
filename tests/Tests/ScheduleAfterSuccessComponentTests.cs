using System;
using System.IO;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// Night soak (Agent O's data, slots.csv): a set with a slot every 5 minutes ran only every 15 — the 19:40 slot ran at
    /// 19:50, the 20:10 slot at 20:25. The 15-minute wait meant for retrying a FAILED run ("retry every 15 minutes") was also
    /// applied after a successful run: a slot that comes after a successful backup has ended waited up to 15 minutes.
    /// Oracle: AgentApp.Due on the computer's clock, six minutes after a successful run, for a slot five minutes after it.
    /// (A slot that falls DURING a run, and the retry after a failure, are not changed here.)
    /// </summary>
    public class ScheduleAfterSuccessComponentTests
    {
        [Fact]
        public void ASlotAfterASuccessfulRun_IsDueAtItsTime_NotFifteenMinutesLater()
        {
            using (var env = new Env())
            {
                env.CreateUser("slot2", "Customer-Pass-1");
                var app = env.Agent("slot2", "Customer-Pass-1");
                var src = env.Dir("src"); File.WriteAllText(Path.Combine(src, "a.txt"), "a");
                var first = DateTime.Now.AddMinutes(-30); var second = DateTime.Now.AddMinutes(5);
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1",
                    new BackupSetInfo { Name = "S", Sources = { src }, Hour = first.Hour, Minute = first.Minute, MissedDelayMinutes = 0, Vss = false,
                        MoreSchedules = { new ScheduleSlot { Hour = second.Hour, Minute = second.Minute } } });
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);
                var ran = DateTime.Now;
                Assert.False(app.Due(set, ran.AddMinutes(1)), "control: right after the run nothing is due");
                Assert.True(app.Due(set, new DateTime(second.Year, second.Month, second.Day, second.Hour, second.Minute, 0).AddMinutes(1)),
                    "the slot " + second.ToString("HH:mm") + " comes after a successful run that ended " + ran.ToString("HH:mm:ss") + ", but is not due a minute later");
            }
        }
    }
}
