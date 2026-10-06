using System;

namespace OnlineBackup.Core
{
    /// <summary>
    /// TIME-020: the time of the product. Real time, except inside a test's time machine (TimeMachineTests), where a whole
    /// month of a server and its computers runs in minutes — every part (schedules, versions kept, alerts, service calls,
    /// reports) sees the moved clock. The time machine is per test (it follows the test's own work), so other tests running
    /// at the same moment keep real time. Two-step codes always use real time (the phone's clock).
    /// </summary>
    public static class SystemClock
    {
#if NET40
        public static DateTime UtcNow { get { return DateTime.UtcNow; } }
#else
        static readonly System.Threading.AsyncLocal<Func<DateTime>> over = new System.Threading.AsyncLocal<Func<DateTime>>();
        public static DateTime UtcNow { get { var f = over.Value; return f == null ? DateTime.UtcNow : f(); } }

        /// <summary>Tests: from here on (and in all the work started from here) the clock is this function.</summary>
        public static void Use(Func<DateTime> clock) { over.Value = clock; }
#endif
        public static DateTime Now { get { return UtcNow.ToLocalTime(); } }
    }
}
