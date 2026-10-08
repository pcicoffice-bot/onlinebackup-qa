using System;

namespace OnlineBackup.Tests
{
    /// <summary>A precondition this machine lacks (restic, Linux, root, mono, a mount...): the test's main part did NOT run.
    /// It must never be counted PASS. Before, 76 places ended such a test with a plain "return", and xUnit reported Passed
    /// (on the hosted-Windows gate every restic test "passed" without restic). Now the test ends as Failed with a message that
    /// starts "NOT TESTED:" and names the reason; the QA aggregator reports it as NOT TESTED (declared), never PASS, and the
    /// quality gate's colour rule for it is unchanged. The test's own conditions are not changed by this.</summary>
    public sealed class NotTestedException : Exception
    {
        public NotTestedException(string reason) : base("NOT TESTED: " + reason) { }
    }

    public static class NotTested
    {
        /// <summary>Used as "throw NotTested.Because(...)" so the compiler, too, sees that the test ends there.</summary>
        public static NotTestedException Because(string reason) { return new NotTestedException(reason); }
    }
}
