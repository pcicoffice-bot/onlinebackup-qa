using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using OnlineBackup.Core;

namespace OnlineBackup.Server
{
    /// <summary>
    /// TASKS-010: every run the server hears about — backups (own engine, restic, aborted), restores and restore tests —
    /// one line in &lt;system&gt;\runs\yyyyMMdd.log (UTC day): the "tasks of the last 24 hours" screen and the dashboard read it
    /// without walking every customer's logs. Kept 400 days.
    /// </summary>
    public sealed class RunLog
    {
        readonly SystemConfig cfg; readonly object gate = new object();
        public RunLog(SystemConfig cfg) { this.cfg = cfg; }
        string Dir { get { var d = Path.Combine(cfg.SystemHome, "runs"); Directory.CreateDirectory(d); return d; } }
        static readonly string[] Fields = { "time", "login", "set", "setName", "computer", "kind", "job", "result", "new", "upd", "del", "bytes", "started", "log" };

        static string Clean(string v) { return (v ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' '); }

        public void Add(DateTime utc, string login, BackupSetInfo set, string kind, string job, Msg body, string logFile)
        {
            try
            {
                var vals = new[] { RunId.UnixMs(utc).ToString(CultureInfo.InvariantCulture), login, set.Id, set.Name, set.Computer, kind, job, body["result"] ?? (kind == "Backup" ? "BS_STOP_BY_SYSTEM_ERROR" : body.Int("failed") > 0 ? "FAILED" : body.Int("checked") == 0 && kind == "RestoreTest" ? "NOT_CHECKED" : "OK")   /* bug 110: nothing to compare is not a failed restore test (R1 fixed only the set) */,
                    body["new"] ?? body["ok"], body["upd"] ?? body["checked"], body["del"], body["bytes"], body["started"], logFile == null ? "" : Path.GetFileName(logFile) };
                var file = Path.Combine(Dir, utc.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");
                // bug 107: a line cut by a power failure has no line end - the next run was glued onto it and lost from the history
                lock (gate) File.AppendAllText(file, (EndsCut(file) ? "\n" : "") + string.Join("\t", vals.Select(Clean)) + "\n", new UTF8Encoding(false));
            }
            catch (Exception e) { SysLog.Write(null, "System", "error: run log " + e.Message); }
        }

        /// <summary>The runs since a moment (newest first), optionally of one customer.</summary>
        public List<Msg> Since(DateTime fromUtc, DateTime nowUtc, Func<string, bool> visible = null)
        {
            var list = new List<Msg>();
            for (var d = fromUtc.Date; d <= nowUtc.Date; d = d.AddDays(1))
            {
                var f = Path.Combine(Dir, d.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");
                if (!File.Exists(f)) continue;
                string[] lines; lock (gate) lines = Atomic.ReadLinesShared(f);
                foreach (var l in lines)
                {
                    var v = l.Split('\t'); if (v.Length < Fields.Length) continue;
                    long t; if (!long.TryParse(v[0], out t) || RunId.FromUnixMs(t) < fromUtc) continue;
                    if (visible != null && !visible(v[1])) continue;
                    var m = new Msg(); for (int i = 0; i < Fields.Length; i++) m.Set(Fields[i], v[i].Length == 0 ? null : v[i]);
                    m.Set("status", Status(v[5], v[7]));
                    list.Add(m);
                }
            }
            return list.OrderByDescending(m => m.Long("time")).ToList();
        }

        /// <summary>ok / warn / bad / stopped — one word for the colour of the row.</summary>
        public static string Status(string kind, string result)
        {
            if (result == "BS_STOP_SUCCESS" || result == "OK" || result == "RESTORE_STOP_SUCCESS") return "ok";
            if (result == "BS_STOP_SUCCESS_WITH_WARNING" || result == "RESTORE_STOP_WITH_WARNING") return "warn";
            if (result == "BS_STOP_BY_USER") return "stopped";
            if (result == "NOT_CHECKED") return "warn";   // bug 110: a restore test with nothing to compare proves nothing - not ok, not a failure
            return "bad";
        }

        static bool EndsCut(string file)
        {
            try
            {
                using (var f = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (f.Length == 0) return false;
                    f.Seek(-1, SeekOrigin.End); return f.ReadByte() != '\n';
                }
            }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }

        /// <summary>Removes runs older than 400 days (bug 108: each run is kept 400 days - a day file used to go 400 days after its
        /// FIRST second, taking runs of that evening with it). A day file is deleted when all its runs are older; the day file on
        /// the boundary keeps its younger lines. Returns the number of day files deleted.</summary>
        public int Purge(DateTime nowUtc)
        {
            int n = 0; var cut = nowUtc.AddDays(-400);
            foreach (var f in Directory.GetFiles(Dir, "*.log"))
            {
                DateTime d;
                if (!DateTime.TryParseExact(Path.GetFileNameWithoutExtension(f), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out d)) continue;
                if (d.AddDays(1) <= cut) { File.Delete(f); n++; continue; }          // every run of that day is older
                if (d >= cut) continue;                                              // every run of that day is younger
                lock (gate)
                {
                    var lines = OnlineBackup.Core.Atomic.ReadAllText(f, new UTF8Encoding(false)).Split('\n');
                    var keep = lines.Where(l => { long t; var v = l.Split('\t'); return !(long.TryParse(v[0], out t) && RunId.FromUnixMs(t) < cut); }).ToArray();
                    if (keep.Length == lines.Length) continue;
                    if (keep.All(l => l.Length == 0)) { File.Delete(f); n++; }
                    else OnlineBackup.Core.Atomic.WriteText(f, string.Join("\n", keep.Where(l => l.Length > 0)) + "\n");
                }
            }
            return n;
        }
    }
}
