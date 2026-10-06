using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace OnlineBackup.Core
{
    /// <summary>
    /// R1 (GPT audit 5–7): the one way the product runs an external program (sqlcmd, wbadmin, ntbackup, mysqldump,
    /// pg_dump, RMAN, PowerShell for VSS and Hyper-V, pre/post commands, sc.exe).
    ///
    /// The old pattern — <c>ReadToEnd()</c> of the output, then <c>WaitForExit(timeout)</c> — never timed out:
    /// ReadToEnd returns only when the program ends, so a stuck program hung the backup forever; reading standard
    /// output to the end before standard error could also deadlock a program that writes much to standard error;
    /// and the result of WaitForExit(timeout) was ignored, so ExitCode then threw. Here both streams are read while
    /// the program runs, the time limit is real, and a program over its limit is killed with all its child processes
    /// (a "cmd /c" or "sh -c" is only the parent of the real work).
    /// </summary>
    public static class ProcessRunner
    {
        public sealed class Result
        {
            public int Code; public bool TimedOut, Cancelled, Idle; public string Out = "", Err = "";
            public string Tail(int max = 2000) { var t = (Out + "\n" + Err).Trim(); return t.Length > max ? t.Substring(t.Length - max) : t; }
        }

        /// <summary>Runs the program to its end, within <paramref name="limit"/>.</summary>
        /// <param name="stdin">written to the program's input, then closed (null: no input)</param>
        /// <param name="stdoutTo">the program's output is copied to this stream (binary-safe) instead of collected</param>
        /// <param name="cancel">asked every second; true stops the program</param>
        /// <param name="onLine">each line of output as it comes (not with stdoutTo)</param>
        public static Result Run(ProcessStartInfo psi, TimeSpan limit, string stdin = null, Stream stdoutTo = null, Func<bool> cancel = null, Action<string> onLine = null)
        {
            return Run(psi, limit, null, null, stdin, stdoutTo, cancel, onLine);
        }

        /// <param name="idle">also stopped when it writes nothing for this long (a program that hangs without ending)</param>
        /// <param name="started">called with the process once it runs (e.g. to lower its priority)</param>
        public static Result Run(ProcessStartInfo psi, TimeSpan limit, TimeSpan? idle, Action<Process> started, string stdin = null, Stream stdoutTo = null, Func<bool> cancel = null, Action<string> onLine = null)
        {
            psi.UseShellExecute = false; psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true; psi.RedirectStandardError = true; psi.RedirectStandardInput = stdin != null;
            var r = new Result();
            var outText = new StringBuilder(); var errText = new StringBuilder();
            long lastOutput = DateTime.UtcNow.Ticks;
            using (var p = Process.Start(psi))
            {
                if (started != null) try { started(p); } catch (Exception) { }
                Thread copy = null; Exception copyError = null;
                var outEnd = new ManualResetEvent(false); var errEnd = new ManualResetEvent(false);
                p.ErrorDataReceived += (s, e) => { if (e.Data == null) { errEnd.Set(); return; } Interlocked.Exchange(ref lastOutput, DateTime.UtcNow.Ticks); lock (errText) if (errText.Length < 1 << 20) errText.AppendLine(e.Data); };
                p.BeginErrorReadLine();
                if (stdoutTo != null)
                {
                    copy = new Thread(() => { try { p.StandardOutput.BaseStream.CopyTo(stdoutTo, 1 << 16); } catch (Exception e) { copyError = e; } finally { outEnd.Set(); } }) { IsBackground = true };
                    copy.Start();
                }
                else
                {
                    p.OutputDataReceived += (s, e) =>
                    {
                        if (e.Data == null) { outEnd.Set(); return; }
                        Interlocked.Exchange(ref lastOutput, DateTime.UtcNow.Ticks);
                        if (onLine != null) try { onLine(e.Data); } catch (Exception) { }
                        lock (outText) if (outText.Length < 4 << 20) outText.AppendLine(e.Data);
                    };
                    p.BeginOutputReadLine();
                }
                if (stdin != null) try { p.StandardInput.Write(stdin); p.StandardInput.Close(); } catch (IOException) { }

                var deadline = DateTime.UtcNow + limit;
                while (!p.WaitForExit(1000))
                {
                    if (cancel != null && cancel()) { r.Cancelled = true; break; }
                    if (DateTime.UtcNow > deadline) { r.TimedOut = true; break; }
                    if (idle != null && DateTime.UtcNow - new DateTime(Interlocked.Read(ref lastOutput), DateTimeKind.Utc) > idle.Value) { r.TimedOut = r.Idle = true; break; }
                }
                if (r.TimedOut || r.Cancelled) KillTree(p);
                // the end of both streams — WaitForExit(timeout) does not wait for them (bounded: a grandchild that kept
                // them open must not hang us)
                p.WaitForExit(15000);
                outEnd.WaitOne(15000); errEnd.WaitOne(15000);
                r.Code = p.HasExited ? p.ExitCode : -1;
                lock (outText) r.Out = outText.ToString();
                lock (errText) r.Err = errText.ToString();
                if (copyError != null && !r.TimedOut && !r.Cancelled) throw new IOException("Writing the output of " + Path.GetFileName(psi.FileName) + " failed: " + copyError.Message, copyError);
            }
            return r;
        }

        /// <summary>Runs and throws when the program did not finish in time or ended with an error code.</summary>
        public static Result Check(ProcessStartInfo psi, TimeSpan limit, string stdin = null, Stream stdoutTo = null)
        {
            var r = Run(psi, limit, stdin, stdoutTo);
            var name = Path.GetFileName(psi.FileName);
            if (r.TimedOut) throw new TimeoutException(name + " did not finish in " + Describe(limit) + " and was stopped");
            if (r.Code != 0) { var t = r.Tail(); throw new InvalidOperationException(name + " exit code " + r.Code + (t.Length > 0 ? ": " + t : "")); }
            return r;
        }

        public static string Describe(TimeSpan t)
        {
            return t.TotalHours >= 1 ? ((int)t.TotalHours + " h") : t.TotalMinutes >= 1 ? ((int)t.TotalMinutes + " min") : ((int)Math.Max(1, t.TotalSeconds) + " s");
        }

        /// <summary>Kills the program and every process it started.</summary>
        public static void KillTree(Process p)
        {
#if NET40
            try
            {
                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                    using (var k = Process.Start(new ProcessStartInfo("taskkill.exe", "/T /F /PID " + p.Id) { UseShellExecute = false, CreateNoWindow = true }))
                        k.WaitForExit(30000);
            }
            catch (Exception) { }
            try { if (!p.HasExited) p.Kill(); } catch (Exception) { }
#else
            try { p.Kill(true); } catch (Exception) { try { p.Kill(); } catch (Exception) { } }
#endif
        }
    }

    /// <summary>R1: the time limits of external programs, in one place (the tests make them short).</summary>
    public static class Limits
    {
        public static TimeSpan Install = TimeSpan.FromMinutes(10);        // one installation step (sc, netsh, icacls, systemctl, launchctl, w32tm)
        public static TimeSpan SqlCommand = TimeSpan.FromHours(6);        // one BACKUP DATABASE (big databases take hours)
        public static TimeSpan SystemState = TimeSpan.FromHours(6);       // wbadmin / ntbackup system state
        public static TimeSpan Image = TimeSpan.FromHours(24);            // wbadmin bare-metal image
        public static TimeSpan Dump = TimeSpan.FromHours(6);              // mysqldump / pg_dump of one database
        public static TimeSpan Rman = TimeSpan.FromHours(12);
        public static TimeSpan HyperV = TimeSpan.FromHours(12);           // export of virtual machines
        public static TimeSpan ShadowCopy = TimeSpan.FromMinutes(10);     // creating / deleting a VSS snapshot
        public static TimeSpan Command = TimeSpan.FromHours(1);           // a pre- or post-command of a set
        public static TimeSpan Short = TimeSpan.FromMinutes(2);           // sc.exe, vssadmin list, Domino dbcache flush, netsh, w32tm
        public static TimeSpan ResticCommand = TimeSpan.FromHours(48);    // any one restic command (a first backup of terabytes takes long)
        public static TimeSpan ResticIdle = TimeSpan.FromMinutes(30);     // a restic backup that wrote nothing (it reports every minute) hangs
        public static TimeSpan WebRestore = TimeSpan.FromMinutes(30);     // a restore prepared by the server for a download
    }
}
