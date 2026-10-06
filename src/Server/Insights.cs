using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using OnlineBackup.Core;

namespace OnlineBackup.Server
{
    /// <summary>One completed run of a set, as kept in &lt;user&gt;\stats\&lt;set&gt;.tsv (one line per run).</summary>
    public sealed class RunStat
    {
        public DateTime Time; public long Prev, New, Upd, Del, Bytes, TopExtCount, Usage; public string TopExt = ""; public bool Suspect;
        public double ChangeRate { get { return Prev <= 0 ? 0 : (Upd + Del) / (double)Prev; } }

        public string ToLine()
        {
            return string.Join("\t", new object[] { RunId.UnixMs(Time), Prev, New, Upd, Del, Bytes, (TopExt ?? "").Replace("\t", ""), TopExtCount, Usage, Suspect ? "S" : "" }
                .Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)));
        }

        public static RunStat Parse(string line)
        {
            var f = line.Split('\t');
            if (f.Length < 9) return null;
            Func<int, long> n = i => { long v; return long.TryParse(f[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0; };
            return new RunStat { Time = RunId.FromUnixMs(n(0)), Prev = n(1), New = n(2), Upd = n(3), Del = n(4), Bytes = n(5), TopExt = f[6], TopExtCount = n(7), Usage = n(8), Suspect = f.Length > 9 && f[9] == "S" };
        }
    }

    /// <summary>
    /// AI-050..070: what the server learns from the runs it has seen — no AI service is needed for these, they work offline:
    /// the normal amount of change of every set (ransomware), the growth of every customer and of the server's disks
    /// (forecasts), and how regularly every computer backs up (which one will most likely miss its next backup).
    /// </summary>
    public static class Insights
    {
        public const int MinRunsToLearn = 7;
        const int Keep = 400;

        static string StatsFile(string userDir, string setId) { return Path.Combine(userDir, "stats", setId + ".tsv"); }

        public static List<RunStat> History(string userDir, string setId)
        {
            var p = StatsFile(userDir, setId);
            if (!File.Exists(p)) return new List<RunStat>();
            return Atomic.ReadLinesShared(p).Select(RunStat.Parse).Where(x => x != null).OrderBy(x => x.Time).ToList();
        }

        public static void Record(string userDir, string setId, RunStat r)
        {
            var p = StatsFile(userDir, setId);
            Atomic.AppendLine(p, r.ToLine());
            var lines = File.ReadAllLines(p);
            if (lines.Length > Keep + 50) Atomic.WriteText(p, string.Join("\n", lines.Skip(lines.Length - Keep)) + "\n");
        }

        // ------------------------------------------------------------------ AI-050: ransomware against what is normal for this set

        public sealed class Verdict { public bool Suspect; public bool Learned; public string Why; public double Threshold, Normal; }

        static double Median(IList<double> v) { var s = v.OrderBy(x => x).ToList(); if (s.Count == 0) return 0; return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2; }

        /// <summary>
        /// Before 7 runs: the fixed rule (RANSOM_MIN_FILES changed files and RANSOM_PERCENT of the set). From then on the
        /// set's own normal change rate is learned (median and spread of the last 60 good runs) and a run is suspect when it
        /// is far above it — a set that always changes 40% (a database dump) no longer raises false alarms, and a quiet set
        /// is caught early. A file extension that suddenly dominates the run and was never common before is also suspect.
        /// More than 90% of the set changed or deleted is always suspect.
        /// </summary>
        public static Verdict Check(List<RunStat> history, RunStat run, int minFiles, int percent)
        {
            long changed = run.Upd + run.Del;
            var good = history.Where(h => !h.Suspect && h.Prev > 0).Skip(Math.Max(0, history.Count - 60)).ToList();
            var v = new Verdict();
            if (run.Prev <= 0) return v;
            double rate = run.ChangeRate;
            bool newExt = false;
            if (good.Count < MinRunsToLearn)
            {
                bool mass = changed >= minFiles && rate * 100 >= percent;
                bool ext = run.TopExtCount >= minFiles && run.TopExtCount * 100 >= run.Prev * percent && run.New >= minFiles / 2;
                v.Suspect = mass || ext;
                v.Threshold = percent / 100.0;
                v.Why = mass ? changed + " files were changed or deleted out of " + run.Prev : ext ? run.TopExtCount + " files with the extension " + run.TopExt : null;
                return v;
            }
            v.Learned = true;
            var rates = good.Select(h => h.ChangeRate).ToList();
            double med = Median(rates), mad = Median(rates.Select(r => Math.Abs(r - med)).ToList()) * 1.4826;
            v.Normal = med;
            v.Threshold = Math.Max(Math.Max(med + 6 * mad, med * 1.5 + 0.05), percent / 300.0);
            bool learnedMass = changed >= minFiles && (rate > v.Threshold || rate >= 0.9);
            var commonExts = new HashSet<string>(good.Where(h => !string.IsNullOrEmpty(h.TopExt)).Select(h => h.TopExt.ToLowerInvariant()));
            newExt = !string.IsNullOrEmpty(run.TopExt) && !commonExts.Contains(run.TopExt.ToLowerInvariant())
                && run.TopExtCount >= minFiles && run.New >= minFiles / 2 && run.TopExtCount > 3 * Math.Max(1, good.Max(h => h.TopExtCount));
            v.Suspect = learnedMass || newExt;
            if (learnedMass) v.Why = changed + " files were changed or deleted out of " + run.Prev + " (" + Pct(rate) + "; normal for this set " + Pct(med) + ")";
            else if (newExt) v.Why = run.TopExtCount + " files with the extension " + run.TopExt + ", which this set never had in such numbers";
            return v;
        }

        static string Pct(double r) { return (r * 100).ToString(r < 0.1 ? "0.0" : "0", CultureInfo.InvariantCulture) + "%"; }

        // ------------------------------------------------------------------ AI-060: forecasts

        /// <summary>Least squares over (days, value): the slope per day; 0 with fewer than 3 points or 2 days.</summary>
        public static double SlopePerDay(IList<KeyValuePair<DateTime, double>> pts)
        {
            if (pts.Count < 3) return 0;
            var t0 = pts[0].Key;
            var xs = pts.Select(p => (p.Key - t0).TotalDays).ToList(); var ys = pts.Select(p => p.Value).ToList();
            if (xs.Max() - xs.Min() < 2) return 0;
            double mx = xs.Average(), my = ys.Average(), num = 0, den = 0;
            for (int i = 0; i < xs.Count; i++) { num += (xs[i] - mx) * (ys[i] - my); den += (xs[i] - mx) * (xs[i] - mx); }
            return den == 0 ? 0 : num / den;
        }

        /// <summary>Days until a value growing by the trend of the last 30 days reaches the limit; null when not growing.</summary>
        public static double? DaysUntil(IList<KeyValuePair<DateTime, double>> pts, double limit, DateTime nowUtc)
        {
            var recent = pts.Where(p => (nowUtc - p.Key).TotalDays <= 30).ToList();
            double slope = SlopePerDay(recent);
            if (slope <= 0 || recent.Count == 0) return null;
            double left = limit - recent[recent.Count - 1].Value;
            return left <= 0 ? 0 : left / slope;
        }

        static string DiskFile(string systemHome) { return Path.Combine(systemHome, "stats", "disk.tsv"); }

        /// <summary>Daily: the free space of every storage folder (for the disk forecast).</summary>
        public static void RecordDisks(string systemHome, IEnumerable<Msg> homes, DateTime nowUtc)
        {
            foreach (var h in homes)
                if (h.Long("free") > 0) Atomic.AppendLine(DiskFile(systemHome), RunId.UnixMs(nowUtc) + "\t" + h["path"] + "\t" + h.Long("free"));
        }

        /// <summary>Per storage folder: free space now and the days until it is full at the current pace.</summary>
        public static List<Msg> DiskForecast(string systemHome, IEnumerable<Msg> homes, DateTime nowUtc)
        {
            var rows = File.Exists(DiskFile(systemHome)) ? File.ReadAllLines(DiskFile(systemHome)).Select(l => l.Split('\t')).Where(f => f.Length == 3).ToList() : new List<string[]>();
            var r = new List<Msg>();
            foreach (var h in homes)
            {
                var pts = rows.Where(f => f[1] == h["path"]).Select(f => new KeyValuePair<DateTime, double>(RunId.FromUnixMs(long.Parse(f[0], CultureInfo.InvariantCulture)), -double.Parse(f[2], CultureInfo.InvariantCulture))).OrderBy(p => p.Key).ToList();
                // used = -free: growing towards 0 free
                var days = DaysUntil(pts, 0, nowUtc);
                r.Add(new Msg().Set("path", h["path"]).Set("free", h.Long("free")).Set("days", days.HasValue ? ((long)Math.Floor(days.Value)).ToString(CultureInfo.InvariantCulture) : null));
            }
            return r;
        }

        /// <summary>The days until a customer's quota is full, from the usage after each run (all sets together); null when not growing.</summary>
        public static double? QuotaDays(string userDir, IEnumerable<BackupSetInfo> sets, long quota, DateTime nowUtc)
        {
            if (quota <= 0) return null;
            var pts = sets.SelectMany(s => History(userDir, s.Id)).Where(h => h.Usage > 0).OrderBy(h => h.Time)
                .Select(h => new KeyValuePair<DateTime, double>(h.Time, h.Usage)).ToList();
            return DaysUntil(pts, quota, nowUtc);
        }

        // ------------------------------------------------------------------ AI-070: which computer will most likely miss its backup

        /// <summary>
        /// From the set's own rhythm (the usual time between runs) and how often it missed lately: 0..100, and why.
        /// Overdue now weighs most; a set that skipped several of its expected runs in the last two weeks comes next.
        /// </summary>
        public static Msg MissRisk(List<RunStat> history, DateTime nowUtc)
        {
            var m = new Msg();
            if (history.Count < 4) return m.Set("risk", 0).Set("learning", 1);
            var times = history.Select(h => h.Time).OrderBy(t => t).ToList();
            var gaps = new List<double>();
            for (int i = 1; i < times.Count; i++) gaps.Add((times[i] - times[i - 1]).TotalHours);
            var recentGaps = gaps.Skip(Math.Max(0, gaps.Count - 30)).ToList();
            double usual = Math.Max(1, Median(recentGaps));
            double since = (nowUtc - times[times.Count - 1]).TotalHours;
            double overdue = since / usual;   // 1 = exactly on time
            int expected = (int)Math.Max(1, Math.Round(Math.Min(14 * 24, (nowUtc - times[0]).TotalHours) / usual));
            int ran = times.Count(t => (nowUtc - t).TotalDays <= 14);
            double missRate = Math.Max(0, Math.Min(1, 1 - ran / (double)expected));
            double longGaps = recentGaps.Count(g => g > usual * 1.8) / (double)Math.Max(1, recentGaps.Count);
            double risk = 0;
            if (overdue > 1.25) risk += Math.Min(60, (overdue - 1) * 40);
            risk += missRate * 30 + longGaps * 30;
            risk = Math.Max(0, Math.Min(100, risk));
            string why = overdue > 1.25 ? "no backup for " + Math.Round(since) + " hours (usually every " + Math.Round(usual) + ")"
                : missRate > 0.2 ? "missed " + Math.Round(missRate * 100) + "% of its runs in the last 14 days"
                : longGaps > 0.2 ? "often runs late" : "";
            return m.Set("risk", (long)Math.Round(risk)).Set("usualHours", (long)Math.Round(usual)).Set("hoursSince", (long)Math.Round(since)).Set("why", why);
        }
    }
}
