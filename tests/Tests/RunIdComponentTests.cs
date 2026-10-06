using System;
using System.IO;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// A run's id is never used twice for one set (found while fixing bug 42: a run closed by the server — aborted, its
    /// lease expired, reported dead — had its folder deleted, and a new run begun in the same second got the SAME id:
    /// its history, its log and its commit were mixed with the closed one, and its commit was refused as "closed").
    ///   input    begin at T; close it (abort / lease expiry / the computer's report); begin again at T
    ///   expected a different id each time
    /// </summary>
    public class RunIdComponentTests : IDisposable
    {
        readonly string userDir = Path.Combine(Path.GetTempPath(), "obrunid-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        public void Dispose() { try { Directory.Delete(userDir, true); } catch (Exception) { } }

        [Theory]
        [InlineData("abort")] [InlineData("expired")] [InlineData("reported")]
        public void AClosedRunsId_IsNeverGivenAgain(string how)
        {
            var st = new SetStore(userDir, "1700000000012");
            var t = DateTime.UtcNow;   // the lease is read from the files' real times
            var a = st.BeginJob(t);
            if (how == "abort") st.Abort(a);
            else if (how == "expired") Assert.Single(st.ExpireStale(t.AddHours(1)));
            else Assert.True(st.AbortIfOpen(a));
            var b = st.BeginJob(t);
            Assert.NotEqual(a, b);
            st.Abort(b);
            var c = new SetStore(userDir, "1700000000012").BeginJob(t);   // also after the store is opened again
            Assert.NotEqual(a, c); Assert.NotEqual(b, c);
        }
    }
}
