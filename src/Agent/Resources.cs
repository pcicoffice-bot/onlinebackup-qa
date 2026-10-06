using System;
using OnlineBackup.Core;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace OnlineBackup.Agent
{
    /// <summary>
    /// RES-010: a backup must not stall the customer's computer — an upload limit (KB/s), a low priority for the work, and a
    /// pause while the computer is busy (CPU over a level), for the agent's own engine and for restic.
    /// </summary>
    public static class Resources
    {
        /// <summary>The share of all CPUs in use now (0..100), or -1 when it cannot be read.</summary>
        public static int CpuPercent()
        {
            try
            {
#if NET40
                using (var pc = new PerformanceCounter("Processor", "% Processor Time", "_Total")) { pc.NextValue(); Thread.Sleep(500); return (int)pc.NextValue(); }
#else
                if (!File.Exists("/proc/stat")) return -1;
                Func<long[]> read = () => File.ReadAllLines("/proc/stat")[0].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(long.Parse).ToArray();
                var a = read(); Thread.Sleep(500); var b = read();
                long idle = (b[3] + (b.Length > 4 ? b[4] : 0)) - (a[3] + (a.Length > 4 ? a[4] : 0)), total = b.Sum() - a.Sum();
                return total <= 0 ? 0 : (int)(100 - idle * 100 / total);
#endif
            }
            catch (Exception) { return -1; }
        }

        /// <summary>Waits while the computer is busier than the level (checked every 30 s, at most 30 minutes at a time); says why.</summary>
        public static void WaitWhileBusy(int level, Action<string> info, Func<bool> stop = null)
        {
            if (level <= 0 || level >= 100) return;
            DateTime start = SystemClock.UtcNow; bool said = false;
            while ((SystemClock.UtcNow - start).TotalMinutes < 30 && (stop == null || !stop()))
            {
                int cpu = CpuPercent();
                if (cpu < 0 || cpu < level) return;
                if (!said) { info("The computer is busy (CPU " + cpu + "%): the backup waits"); said = true; }
                Thread.Sleep(30000);
            }
        }

        /// <summary>Lowers this process while a backup runs (restored after); restic runs below normal too.</summary>
        public static IDisposable LowPriority(bool on)
        {
            if (!on) return new Restore(null);
            try { var p = Process.GetCurrentProcess(); var old = p.PriorityClass; p.PriorityClass = ProcessPriorityClass.BelowNormal; return new Restore(() => { try { p.PriorityClass = old; } catch (Exception) { } }); }
            catch (Exception) { return new Restore(null); }
        }

        sealed class Restore : IDisposable { readonly Action a; public Restore(Action a) { this.a = a; } public void Dispose() { if (a != null) a(); } }
    }

    /// <summary>RES-010: an upload limit shared by the whole run (KB/s, 0 = none).</summary>
    public sealed class Throttle
    {
        readonly long bytesPerSecond; readonly Stopwatch clock = Stopwatch.StartNew(); long sent;
        public Throttle(int kbps) { bytesPerSecond = Math.Max(0, kbps) * 1024L; }
        public bool On { get { return bytesPerSecond > 0; } }
        public void Account(int bytes)
        {
            if (!On) return;
            lock (clock)
            {
                sent += bytes;
                var due = TimeSpan.FromSeconds((double)sent / bytesPerSecond) - clock.Elapsed;
                if (due > TimeSpan.Zero) Thread.Sleep(due > TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : due);
            }
        }
    }

    /// <summary>A write stream that keeps to a Throttle.</summary>
    public sealed class ThrottledStream : Stream
    {
        readonly Stream inner; readonly Throttle t;
        public ThrottledStream(Stream inner, Throttle t) { this.inner = inner; this.t = t; }
        public override void Write(byte[] buffer, int offset, int count)
        {
            const int Slice = 16 * 1024;
            for (int i = 0; i < count; i += Slice) { int n = Math.Min(Slice, count - i); inner.Write(buffer, offset + i, n); t.Account(n); }
        }
        public override void Flush() { inner.Flush(); }
        public override bool CanRead { get { return false; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return true; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
        public override int Read(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
    }
}
