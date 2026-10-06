using System;
using System.IO;
using System.Linq;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// The run lease alone (SetStore, no web server) — component contract (tests/QA/specs.py ST-02):
    ///   purpose  an open run lives while its computer shows signs of life, and is closed when they stop — never earlier,
    ///            never kept for ever, never a committing run
    ///   input    a run begun at T; signs of life at T+4 min; checks at T+4m59, T+8m59, T+9m01; a run whose commit journal
    ///            is written; the computer's own report that its run died
    ///   expected open until 5 minutes after the last sign of life (T+8m59 still open), closed at T+9m01 and named once; a
    ///            committing run is never expired (it is rolled forward); the computer's report closes an open run at once
    ///            and is harmless for a run that is not open; after a close the set can begin again at once
    /// </summary>
    public class LeaseComponentTests : IDisposable
    {
        readonly string userDir = Path.Combine(Path.GetTempPath(), "oblease-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        const string SetId = "1700000000011";
        DateTime now = DateTime.UtcNow;
        public LeaseComponentTests() { SystemClock.Use(() => now); }
        public void Dispose() { SystemClock.Use(null); try { Directory.Delete(userDir, true); } catch (Exception) { } }

        [Fact]
        public void OpenWhileAlive_ClosedFiveMinutesAfterTheLastSignOfLife_NamedOnce_ThenTheSetBeginsAgain()
        {
            var st = new SetStore(userDir, SetId);
            var t0 = now;
            var job = st.BeginJob(t0);
            now = t0.AddMinutes(4); st.Touch(job);                                   // a sign of life
            Assert.Empty(st.ExpireStale(t0.AddMinutes(4).AddSeconds(59)));
            Assert.Empty(st.ExpireStale(t0.AddMinutes(8).AddSeconds(59)));          // 4:59 after the last sign: still open
            Assert.Equal(409, Assert.Throws<ApiException>(() => st.BeginJob(t0.AddMinutes(8).AddSeconds(59))).Status);
            Assert.Equal(new[] { job }, st.ExpireStale(t0.AddMinutes(9).AddSeconds(1)).ToArray());   // 5:01 after it: closed
            Assert.Empty(st.ExpireStale(t0.AddMinutes(10)));                         // named once
            Assert.NotNull(st.BeginJob(t0.AddMinutes(10)));                          // the set can back up again
        }

        [Fact]
        public void ACommittingRun_IsNeverExpired()
        {
            var st = new SetStore(userDir, SetId);
            var t0 = now;
            var job = st.BeginJob(t0);
            st.PrepareCommit(job, new Msg());                                         // the journal is written (crash right here)
            Assert.Empty(st.ExpireStale(t0.AddHours(5)));
            Assert.False(SetStore.LeaseExpired(Path.Combine(userDir, "files", SetId, "jobs", job), t0.AddHours(5)));
        }

        [Fact]
        public void TheComputersReport_ClosesAnOpenRunAtOnce_AndIsHarmlessOtherwise()
        {
            var st = new SetStore(userDir, SetId);
            var job = st.BeginJob(now);
            Assert.True(st.AbortIfOpen(job));
            Assert.False(st.AbortIfOpen(job));                                        // again: nothing open
            Assert.False(st.AbortIfOpen("not-a-run"));
            Assert.False(st.AbortIfOpen("2020-01-01-00-00-00"));
            Assert.NotNull(st.BeginJob(now.AddSeconds(2)));
        }
    }
}
