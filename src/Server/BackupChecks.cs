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
    /// CHK-010: the backup checks of the ITSguard analysis report, inside the backup server — a set that is too small (original
    /// size under 1,000 MB), a set where nothing changed in 30 days (3 or more successful backups, no new or changed file —
    /// SQL too), a sharp change in size (30% and at least 100 MB since the day before), versions kept less than 30 days,
    /// and sets or customers added or removed in the last 14 days. Local-only sets are left out (nothing is kept here).
    /// A snapshot of every set's size is kept each day: &lt;system&gt;\stats\sets-yyyyMMdd.tsv (login, set, name, original size, type).
    /// </summary>
    public static class BackupChecks
    {
        public const long SmallMB = 1000, VolumeMinMB = 100; public const int NoChangeDays = 30, NoChangeRuns = 3, VolumePercent = 30, MinRetentionDays = 30, ChangesDays = 14;

        static string Dir(SystemConfig cfg) { var d = Path.Combine(cfg.SystemHome, "stats"); Directory.CreateDirectory(d); return d; }

        sealed class Row { public string Login, Alias, Set, Name, Type, Computer; public long Orig; }

        static List<Row> Current(Users users)
        {
            var l = new List<Row>();
            foreach (var login in users.Logins())
            {
                Profile p; try { p = users.LoadProfile(login); } catch (Exception) { continue; }
                foreach (var e in p.SetElements)
                {
                    if ((string)e.Attribute("DEST_MODE") == "LOCAL") continue;
                    long o; long.TryParse((string)e.Attribute("TOTAL_UNCOMPRESS_FILE_SIZE"), out o);
                    l.Add(new Row { Login = login, Alias = p.Get("ALIAS"), Set = (string)e.Attribute("ID"), Name = (string)e.Attribute("NAME"), Type = (string)e.Attribute("TYPE") ?? "FILE", Computer = (string)e.Attribute("SCHEDULE_HOST"), Orig = o });
                }
            }
            return l;
        }

        /// <summary>Today's snapshot of every set's size (from the maintenance run; written again during the day).</summary>
        public static void Snapshot(SystemConfig cfg, Users users, DateTime nowUtc)
        {
            var sb = new StringBuilder();
            foreach (var r in Current(users)) sb.Append(string.Join("\t", new[] { r.Login, r.Set, (r.Name ?? "").Replace('\t', ' '), r.Orig.ToString(CultureInfo.InvariantCulture), r.Type })).Append('\n');
            Atomic.WriteText(Path.Combine(Dir(cfg), "sets-" + nowUtc.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".tsv"), sb.ToString());
            foreach (var old in Directory.GetFiles(Dir(cfg), "sets-*.tsv").OrderByDescending(x => x, StringComparer.Ordinal).Skip(400)) try { File.Delete(old); } catch (IOException) { }
        }

        static List<KeyValuePair<DateTime, Dictionary<string, string[]>>> Snapshots(SystemConfig cfg, DateTime fromUtc)
        {
            var l = new List<KeyValuePair<DateTime, Dictionary<string, string[]>>>();
            foreach (var f in Directory.GetFiles(Dir(cfg), "sets-*.tsv").OrderBy(x => x, StringComparer.Ordinal))
            {
                DateTime d;
                if (!DateTime.TryParseExact(Path.GetFileNameWithoutExtension(f).Substring(5), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out d) || d < fromUtc.Date) continue;
                var m = new Dictionary<string, string[]>();
                foreach (var line in OnlineBackup.Core.Atomic.ReadAllLines(f)) { var v = line.Split('\t'); if (v.Length >= 4) m[v[0] + "/" + v[1]] = v; }
                l.Add(new KeyValuePair<DateTime, Dictionary<string, string[]>>(d, m));
            }
            return l;
        }

        /// <summary>The findings, each with the customer, the set and what was found.</summary>
        public static Msg Report(SystemConfig cfg, Users users, RunLog runs, DateTime nowUtc, Func<string, bool> visible)
        {
            var m = new Msg().Set("smallMB", SmallMB).Set("noChangeDays", NoChangeDays).Set("volumePercent", VolumePercent).Set("minRetentionDays", MinRetentionDays).Set("changesDays", ChangesDays);
            var rows = Current(users).Where(r => visible(r.Login)).ToList();
            Func<Row, Msg> item = (r) => new Msg().Set("login", r.Login).Set("alias", r.Alias).Set("set", r.Set).Set("name", r.Name).Set("type", r.Type).Set("computer", r.Computer).Set("orig", r.Orig);

            // a backup that holds almost nothing: often a wrong path or a disconnected drive
            foreach (var r in rows.Where(r => r.Orig > 0 && r.Orig < SmallMB * 1024 * 1024)) m.Add("small", item(r));

            // nothing changed for 30 days although it backed up: a database or a folder no longer in use, or the wrong source
            var recent = runs.Since(nowUtc.AddDays(-NoChangeDays), nowUtc, visible).Where(x => x["kind"] == "Backup" && (x["result"] ?? "").StartsWith("BS_STOP_SUCCESS", StringComparison.Ordinal)).ToList();
            foreach (var r in rows)
            {
                var mine = recent.Where(x => x["login"] == r.Login && x["set"] == r.Set).ToList();
                if (mine.Count >= NoChangeRuns && mine.All(x => x.Long("new") + x.Long("upd") == 0))
                    m.Add("noChange", item(r).Set("runs", mine.Count).Set("last", mine.Max(x => x.Long("time"))));
            }

            // versions kept for too short a time
            foreach (var login in users.Logins().Where(visible))
            {
                Profile p; try { p = users.LoadProfile(login); } catch (Exception) { continue; }
                foreach (var s in p.Sets.Where(s => s.DestMode != "LOCAL" && s.Retention != null && s.Retention.Unit == "DAYS" && s.Retention.Period > 0 && s.Retention.Period < MinRetentionDays))
                    m.Add("shortRetention", new Msg().Set("login", login).Set("alias", p.Get("ALIAS")).Set("set", s.Id).Set("name", s.Name).Set("computer", s.Computer).Set("days", s.Retention.Period));
            }

            // a sharp change in size since the day before, and sets / customers added or removed (14 days)
            var snaps = Snapshots(cfg, nowUtc.AddDays(-ChangesDays));
            for (int i = 1; i < snaps.Count; i++)
            {
                var before = snaps[i - 1].Value; var now = snaps[i].Value; var day = RunId.UnixMs(snaps[i].Key);
                foreach (var kv in now.Where(kv => visible(kv.Value[0])))
                {
                    string[] old;
                    if (!before.TryGetValue(kv.Key, out old)) { m.Add("added", new Msg().Set("login", kv.Value[0]).Set("set", kv.Value[1]).Set("name", kv.Value[2]).Set("day", day)); continue; }
                    if (i != snaps.Count - 1) continue;
                    long a, b; long.TryParse(old[3], out a); long.TryParse(kv.Value[3], out b);
                    if (a > 0 && Math.Abs(b - a) >= VolumeMinMB * 1024 * 1024 && Math.Abs(b - a) * 100.0 / a >= VolumePercent)
                        m.Add("volume", new Msg().Set("login", kv.Value[0]).Set("set", kv.Value[1]).Set("name", kv.Value[2]).Set("before", a).Set("after", b).Set("percent", Math.Round((b - a) * 100.0 / a, 1)));
                }
                foreach (var kv in before.Where(kv => visible(kv.Value[0]) && !now.ContainsKey(kv.Key)))
                    m.Add("removed", new Msg().Set("login", kv.Value[0]).Set("set", kv.Value[1]).Set("name", kv.Value[2]).Set("day", day));
            }
            return m;
        }
    }
}
