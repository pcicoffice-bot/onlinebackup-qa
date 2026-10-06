using System;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// How a run's result is read — component contract (tests/QA/specs.py SH-01): one table, written before the test, of
    /// every result code → the colour of its row, and whether it moves "Last backup"; and the result of a report that
    /// has none. A failure must never look like a backup, and a backup with some errors must still count as a backup.
    /// </summary>
    public class ResultComponentTests
    {
        [Theory]
        [InlineData("BS_STOP_SUCCESS", "ok", true)]
        [InlineData("BS_STOP_SUCCESS_WITH_WARNING", "warn", true)]
        [InlineData("BS_STOP_SUCCESS_WITH_ERROR", "bad", true)]       // stored, but some files were not: red, and it is a backup
        [InlineData("BS_STOP_BY_SYSTEM_ERROR", "bad", false)]
        [InlineData("BS_STOP_QUOTA_EXCEEDED", "bad", false)]
        [InlineData("BS_STOP_BY_PRE_COMMAND", "bad", false)]
        [InlineData("BS_STOP_BY_USER", "stopped", false)]
        [InlineData("SOMETHING_NEW", "bad", false)]
        [InlineData(null, "bad", false)]
        [InlineData("", "bad", false)]
        public void EveryResult_HasItsColour_AndOnlyARunThatBackedUpMovesLastBackup(string result, string status, bool moves)
        {
            Assert.Equal(status, RunLog.Status("Backup", result));
            Assert.Equal(moves, Api.Completed(result));
        }

        [Theory]
        [InlineData("RESTORE_STOP_SUCCESS", "ok")]
        [InlineData("RESTORE_STOP_WITH_WARNING", "warn")]
        [InlineData("RESTORE_STOP_WITH_ERROR", "bad")]
        public void EveryRestoreResult_HasItsColour(string result, string status) { Assert.Equal(status, RunLog.Status("Restore", result)); }

        static Msg Report(string result, string endLine)
        {
            var m = new Msg(); if (result != null) m.Set("result", result);
            m.Add("log", new Msg().Set("l", AhsayLog.Line(DateTime.UtcNow, "start")));
            if (endLine != null) m.Add("log", new Msg().Set("l", AhsayLog.Line(DateTime.UtcNow, "end", message: endLine)));
            return m;
        }

        [Fact]
        public void AReportWithoutAResult_TakesItsEndLine_OrIsAFailure_NeverASuccess()
        {
            var a = Report(null, "BS_STOP_SUCCESS_WITH_WARNING"); Api.NormalizeResult(a); Assert.Equal("BS_STOP_SUCCESS_WITH_WARNING", a["result"]);
            var b = Report(null, null); Api.NormalizeResult(b); Assert.Equal("BS_STOP_BY_SYSTEM_ERROR", b["result"]);
            Assert.Contains(b.List("log"), l => l["l"].Contains("did not report"));
            var c = Report(null, "garbage"); Api.NormalizeResult(c); Assert.Equal("BS_STOP_BY_SYSTEM_ERROR", c["result"]);
            var d = Report(null, null); Api.NormalizeRestoreResult(d); Assert.Equal("RESTORE_STOP_WITH_ERROR", d["result"]);
            var e = Report(null, "RESTORE_STOP_SUCCESS"); Api.NormalizeRestoreResult(e); Assert.Equal("RESTORE_STOP_SUCCESS", e["result"]);
            var f = Report("BS_STOP_BY_USER", "BS_STOP_SUCCESS"); Api.NormalizeResult(f); Assert.Equal("BS_STOP_BY_USER", f["result"]);   // a given result stands
        }
    }
}
