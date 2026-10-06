using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace OnlineBackup.Core
{
    /// <summary>
    /// The .chk file beside every stored object — what the server knows about it, in plain text, without any key:
    /// enough to verify the object (sha256) and to rebuild the whole index from the disk (Rebuild).
    /// </summary>
    public sealed class ChkRecord
    {
        public string Rel;        // server path of the file under Current (encrypted segments, '/')
        public int Seq;           // 0 = full, 1..n = deltas in the chain
        public string Kind;       // F = full, D = delta
        public string Job;        // job (run) that created it
        public string EncPath;    // the real path, encrypted by the agent
        public long Size;         // stored bytes
        public long Orig;         // original file size
        public long Mtime;        // original last write time (unix ms)
        public string Sha256;     // of the stored object
        public bool PermOnly;     // only permissions / attributes changed (Ahsay "Permission Updated Files")

        public string ToText()
        {
            var sb = new StringBuilder();
            sb.Append("v=1\n");
            sb.Append("rel=").Append(Rel).Append('\n');
            sb.Append("seq=").Append(Seq.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("kind=").Append(Kind).Append('\n');
            sb.Append("job=").Append(Job).Append('\n');
            sb.Append("enc=").Append(EncPath).Append('\n');
            sb.Append("size=").Append(Size.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("orig=").Append(Orig.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("mtime=").Append(Mtime.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("sha256=").Append(Sha256).Append('\n');
            sb.Append("perm=").Append(PermOnly ? "Y" : "N").Append('\n');
            return sb.ToString();
        }

        public static ChkRecord Parse(string text)
        {
            var d = new Dictionary<string, string>();
            foreach (var line in text.Split('\n'))
            {
                int i = line.IndexOf('=');
                if (i > 0) d[line.Substring(0, i)] = line.Substring(i + 1).TrimEnd('\r');
            }
            Func<string, string> g = k => { string v; return d.TryGetValue(k, out v) ? v : null; };
            if (g("v") != "1" || g("rel") == null || g("sha256") == null) throw new InvalidDataException("Damaged .chk record.");
            return new ChkRecord
            {
                Rel = g("rel"), Seq = int.Parse(g("seq") ?? "0", CultureInfo.InvariantCulture), Kind = g("kind"), Job = g("job"),
                EncPath = g("enc"), Size = long.Parse(g("size") ?? "0", CultureInfo.InvariantCulture),
                Orig = long.Parse(g("orig") ?? "0", CultureInfo.InvariantCulture), Mtime = long.Parse(g("mtime") ?? "0", CultureInfo.InvariantCulture),
                Sha256 = g("sha256"), PermOnly = g("perm") == "Y"
            };
        }

        public static string ObjectName(string rel, int seq) { return rel + "." + seq.ToString("D3", CultureInfo.InvariantCulture); }
    }

    /// <summary>Run (job) ids: the start time, YYYY-MM-DD-hh-mm-ss (UTC), as Ahsay names its run folders and logs.</summary>
    public static class RunId
    {
        public const string Format = "yyyy-MM-dd-HH-mm-ss";
        public static string From(DateTime utc) { return utc.ToString(Format, CultureInfo.InvariantCulture); }
        public static DateTime Parse(string id) { return DateTime.ParseExact(id, Format, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal); }
        public static bool TryParse(string id, out DateTime utc) { return DateTime.TryParseExact(id, Format, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out utc); }
        public static long UnixMs(DateTime utc) { return (long)(utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds; }
        public static DateTime FromUnixMs(long ms) { return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms); }
    }

    /// <summary>
    /// Job log lines in the exact Ahsay 6 format, so ITSguard's existing readers work unchanged:
    /// time(ms),type,path,stored size,,file time(ms),,original size — types start/info/new/upd/perm/del/warn/err/end.
    /// </summary>
    public static class AhsayLog
    {
        public static string Line(DateTime utc, string type, string path = "", long size = 0, long mtime = 0, long orig = 0, string message = null)
        {
            // Fields: time, type, path, size, message, file time, (unused), original size.
            var c = CultureInfo.InvariantCulture;
            return RunId.UnixMs(utc).ToString(c) + "," + type + "," + Quote(path ?? "") + "," + size.ToString(c) + ","
                + Quote(message ?? "") + "," + (mtime > 0 ? mtime.ToString(c) : "") + ",," + orig.ToString(c);
        }

        public static string Info(DateTime utc, string message) { return Line(utc, "info", message: message); }

        static string Quote(string s)
        {
            if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return s;
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ") + "\"";
        }

        /// <summary>Splits one line into its 8 fields (quotes with backslash escapes, as Ahsay writes them).</summary>
        public static string[] Fields(string line)
        {
            var outp = new List<string>();
            var sb = new StringBuilder();
            bool q = false;
            for (int i = 0; i < line.Length; i++)
            {
                char ch = line[i];
                if (q)
                {
                    if (ch == '\\' && i + 1 < line.Length) { sb.Append(line[++i]); continue; }
                    if (ch == '"') { q = false; continue; }
                    sb.Append(ch);
                }
                else if (ch == '"') q = true;
                else if (ch == ',') { outp.Add(sb.ToString()); sb.Length = 0; }
                else sb.Append(ch);
            }
            outp.Add(sb.ToString());
            return outp.ToArray();
        }
    }

    /// <summary>Retention (Ahsay RETENTION_POLICY + RETENTION_SETTING): simple by days or by job count, or advanced daily/weekly/monthly/quarterly/yearly.</summary>
    public sealed class RetentionPolicy
    {
        public string Unit = "DAYS";   // DAYS | JOBS
        public int Period = 30;
        public int Daily, Weekly, Monthly, Quarterly, Yearly;   // advanced (0 = not used)

        public bool Advanced { get { return Daily + Weekly + Monthly + Quarterly + Yearly > 0; } }

        /// <summary>Which committed jobs stay restorable at "now". Jobs must be sorted oldest → newest.</summary>
        public HashSet<string> Keep(IList<string> jobs, DateTime nowUtc)
        {
            var keep = new HashSet<string>();
            if (jobs.Count == 0) return keep;
            keep.Add(jobs[jobs.Count - 1]);   // the latest point is always kept
            if (Unit == "JOBS") foreach (var j in jobs.Skip(Math.Max(0, jobs.Count - Period))) keep.Add(j);
            else foreach (var j in jobs) if ((nowUtc - RunId.Parse(j)).TotalDays <= Period) keep.Add(j);
            if (Advanced)
            {
                KeepLatestPer(jobs, Daily, d => d.ToString("yyyyMMdd", CultureInfo.InvariantCulture), keep);
                KeepLatestPer(jobs, Weekly, d => d.AddDays(-(int)d.DayOfWeek).ToString("yyyyMMdd", CultureInfo.InvariantCulture), keep);
                KeepLatestPer(jobs, Monthly, d => d.ToString("yyyyMM", CultureInfo.InvariantCulture), keep);
                KeepLatestPer(jobs, Quarterly, d => d.Year + "Q" + ((d.Month - 1) / 3), keep);
                KeepLatestPer(jobs, Yearly, d => d.Year.ToString(CultureInfo.InvariantCulture), keep);
            }
            return keep;
        }

        static void KeepLatestPer(IList<string> jobs, int count, Func<DateTime, string> bucket, HashSet<string> keep)
        {
            if (count <= 0) return;
            var seen = new HashSet<string>();
            for (int i = jobs.Count - 1; i >= 0 && seen.Count < count; i--)
                if (seen.Add(bucket(RunId.Parse(jobs[i])))) keep.Add(jobs[i]);
        }

        public string Describe()
        {
            var s = Unit == "JOBS" ? Period + " last backups" : Period + " days";
            if (Daily > 0) s += ", " + Daily + " daily";
            if (Weekly > 0) s += ", " + Weekly + " weekly";
            if (Monthly > 0) s += ", " + Monthly + " monthly";
            if (Quarterly > 0) s += ", " + Quarterly + " quarterly";
            if (Yearly > 0) s += ", " + Yearly + " yearly";
            return s;
        }
    }
}
