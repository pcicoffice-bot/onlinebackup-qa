using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using OnlineBackup.Core;

namespace OnlineBackup.Agent
{
    /// <summary>
    /// Owner decision B2, in the CLIENT window only: what the customer's window needs to tell a partial run from a complete
    /// one, kept on this computer beside last-attempt.txt (which stays exactly as it was - the scheduler, the restore test and
    /// the other consumers read it unchanged):
    ///   last-complete.txt  the time of the last run that ended BS_STOP_SUCCESS or BS_STOP_SUCCESS_WITH_WARNING (clean runs only)
    ///   last-missed.txt    the files the last run did not back up, and why (its "err" log lines; at most 200, with the count)
    ///   last-run-log.txt   the last run's log without the per-file lines (start, info, warn, err, end - for the details and
    ///                      "Export the log for your IT provider")
    ///   runs.txt           the last 60 runs: time, result, number of errors (the window's history and its strip of runs)
    /// Writing them never changes the run or its result: every failure here is swallowed.
    /// </summary>
    public static class RunNotes
    {
        public const int MaxRuns = 60, MaxMissed = 200, MaxLog = 400;

        /// <summary>B2: only a clean run counts as complete (SUCCESS, SUCCESS_WITH_WARNING - nothing missing).</summary>
        public static bool Complete(string result) { return result == "BS_STOP_SUCCESS" || result == "BS_STOP_SUCCESS_WITH_WARNING"; }

        public static void Record(string setDir, string result, IList<string> log, int errors, DateTime utc)
        {
            try
            {
                Directory.CreateDirectory(setDir);
                var at = RunId.From(utc);
                result = (result ?? "").Trim();
                if (Complete(result)) Atomic.WriteText(Path.Combine(setDir, "last-complete.txt"), at);
                var lines = log ?? new List<string>();
                var missed = new List<string>();
                foreach (var l in lines)
                {
                    var f = AhsayLog.Fields(l);
                    if (f.Length < 5 || f[1] != "err") continue;
                    if (missed.Count < MaxMissed) missed.Add(Clean(f[2]) + "\t" + Clean(f[4]));
                }
                var count = Math.Max(errors, missed.Count);
                Atomic.WriteText(Path.Combine(setDir, "last-missed.txt"), at + "\t" + count.ToString(CultureInfo.InvariantCulture) + "\n" + string.Join("\n", missed.ToArray()));
                var kept = lines.Where(l => { var f = AhsayLog.Fields(l); return f.Length < 2 || (f[1] != "new" && f[1] != "upd" && f[1] != "perm" && f[1] != "del"); }).ToList();
                if (kept.Count > MaxLog) kept = kept.Take(MaxLog / 2).Concat(kept.Skip(kept.Count - MaxLog / 2)).ToList();
                Atomic.WriteText(Path.Combine(setDir, "last-run-log.txt"), at + "\n" + string.Join("\n", kept.ToArray()));
                var runsFile = Path.Combine(setDir, "runs.txt");
                var runs = File.Exists(runsFile) ? Atomic.ReadAllLines(runsFile).Where(x => x.Trim().Length > 0).ToList() : new List<string>();
                runs.Add(at + "\t" + result + "\t" + count.ToString(CultureInfo.InvariantCulture));
                if (runs.Count > MaxRuns) runs = runs.Skip(runs.Count - MaxRuns).ToList();
                Atomic.WriteText(runsFile, string.Join("\n", runs.ToArray()) + "\n");
            }
            catch (Exception) { }
        }

        static string Clean(string s) { return (s ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' '); }

        static string Read(string f) { try { return File.Exists(f) ? Atomic.ReadAllText(f) : null; } catch (Exception) { return null; } }

        /// <summary>The time of the last complete run (RunId text), or "" when none is known. Before these notes existed, a
        /// last attempt that was itself complete is the last complete run.</summary>
        public static string LastComplete(string setDir, string lastAttempt, string lastResult)
        {
            var v = (Read(Path.Combine(setDir, "last-complete.txt")) ?? "").Trim();
            if (v.Length > 0) return v;
            return Complete((lastResult ?? "").Trim()) ? lastAttempt ?? "" : "";
        }

        /// <summary>The files the last run did not back up (path, why) and their number - only when the notes belong to the
        /// last attempt (an older run's list is never shown as the last one's).</summary>
        public static List<KeyValuePair<string, string>> Missed(string setDir, string lastAttempt, out int count)
        {
            count = 0; var l = new List<KeyValuePair<string, string>>();
            var t = Read(Path.Combine(setDir, "last-missed.txt"));
            if (t == null) return l;
            var lines = t.Replace("\r", "").Split('\n');
            var head = lines[0].Split('\t');
            if (head[0] != (lastAttempt ?? "")) return l;
            if (head.Length > 1) int.TryParse(head[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out count);
            foreach (var x in lines.Skip(1)) { if (x.Length == 0) continue; var p = x.Split(new[] { '\t' }, 2); l.Add(new KeyValuePair<string, string>(p[0], p.Length > 1 ? p[1] : "")); }
            return l;
        }

        /// <summary>The last run's log lines (Ahsay format) and the run's time.</summary>
        public static List<string> Log(string setDir, out string at)
        {
            at = "";
            var t = Read(Path.Combine(setDir, "last-run-log.txt"));
            if (t == null) return new List<string>();
            var lines = t.Replace("\r", "").Split('\n').ToList();
            at = lines[0];
            return lines.Skip(1).Where(x => x.Length > 0).ToList();
        }

        /// <summary>The recorded runs, oldest first: (time, result, errors).</summary>
        public static List<string[]> Runs(string setDir)
        {
            var t = Read(Path.Combine(setDir, "runs.txt"));
            if (t == null) return new List<string[]>();
            return t.Replace("\r", "").Split('\n').Where(x => x.Trim().Length > 0).Select(x => x.Split('\t')).Where(x => x.Length >= 2).ToList();
        }

        /// <summary>The log as a person reads it (for "Export the log"): local time, kind, file, message.</summary>
        public static string Readable(IEnumerable<string> lines)
        {
            var sb = new StringBuilder();
            foreach (var l in lines)
            {
                var f = AhsayLog.Fields(l);
                long ms; var when = f.Length > 0 && long.TryParse(f[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out ms) ? RunId.FromUnixMs(ms).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "";
                sb.Append(when).Append("  ").Append(f.Length > 1 ? f[1] : "").Append("  ");
                if (f.Length > 2 && f[2].Length > 0) sb.Append(f[2]).Append("  ");
                if (f.Length > 4) sb.Append(f[4]);
                sb.Append("\r\n");
            }
            return sb.ToString();
        }
    }
}
