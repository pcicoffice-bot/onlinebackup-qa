using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using OnlineBackup.Core;

namespace OnlineBackup.Server
{
    /// <summary>
    /// DEF-010: the defaults for new customers (Ahsay "Policy Group" default values) — set by the administrator in
    /// "Defaults for new customers". A new customer gets: quota, number of sets, two-step verification, keeping the key
    /// for recovery, what it may change in the client. A new set gets: the time, versions kept (30 days by default),
    /// compression, upload limit, shadow copy, missed runs, skipping system and temporary files.
    /// A value the customer chose in the client software when adding the set is kept; one left as it came wins the default.
    /// &lt;system&gt;\policy\default.xml (USER, BACKUP_SET, GLOBAL_FILTER). Existing customers and sets do not change.
    /// </summary>
    public static class NewDefaults
    {
        public const int RetentionDays = 30;

        static string Path_(SystemConfig cfg) { return Path.Combine(cfg.SystemHome, "policy", "default.xml"); }

        public static Msg Get(SystemConfig cfg)
        {
            var p = cfg.Policy(); var u = p.Element("USER") ?? new XElement("USER"); var s = p.Element("BACKUP_SET") ?? new XElement("BACKUP_SET");
            Func<XElement, string, string, string> a = (e, n, d) => (string)e.Attribute(n) ?? d;
            var m = new Msg().Set("quotaGB", a(u, "QUOTA_GB", "50")).Set("quotaType", a(u, "QUOTA_TYPE", "COMPRESSED")).Set("maxSets", a(u, "MAX_BACKUP_SET", "10"))
                .Set("saveKey", a(u, "SAVE_ENCRYPT_KEY", "Y") == "Y" ? 1 : 0).Set("requireTotp", a(u, "REQUIRE_TOTP", "N") == "Y" ? 1 : 0)
                .Set("hour", a(s, "HOUR", "22")).Set("minute", a(s, "MINUTE", "0")).Set("retentionDays", a(s, "RETENTION_DAYS", RetentionDays.ToString(CultureInfo.InvariantCulture)))
                .Set("logDays", a(s, "LOG_RETENTION_DAYS", "60")).Set("compression", a(s, "COMPRESSION", "MAX")).Set("bandwidth", a(s, "BANDWIDTH_KBPS", "0"))
                .Set("vss", a(s, "VSS", "Y") == "Y" ? 1 : 0).Set("runMissed", a(s, "RUN_MISSED", "Y") == "Y" ? 1 : 0).Set("runMissedNet", a(s, "RUN_MISSED_NET", "Y") == "Y" ? 1 : 0)
                .Set("skipSystem", p.Elements("GLOBAL_FILTER").Any() ? 1 : 0).Set("sqlFullDay", a(s, "SQL_FULL_DAY", "-1"));
            foreach (var r in SetControl.Rights) m.Set(r[0].ToLowerInvariant(), a(u, r[0], r[1]) == "Y" ? 1 : 0);
            return m;
        }

        public static void Save(SystemConfig cfg, Msg b, string by, string ip)
        {
            Func<string, int, int, int, int> n = (k, lo, hi, d) => { int v; if (!int.TryParse(b[k], NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return d; if (v < lo || v > hi) throw new ApiException(400, "VALUE", "A value is out of range: " + k + " (" + lo + "–" + hi + ")."); return v; };
            double gb; if (!double.TryParse(b["quotaGB"] ?? "50", NumberStyles.Float, CultureInfo.InvariantCulture, out gb) || gb <= 0 || gb > 1024 * 1024) throw new ApiException(400, "QUOTA", "The quota must be more than 0 GB.");
            int days = n("retentionDays", 1, 3650, RetentionDays);
            var comp = b["compression"] == "FAST" || b["compression"] == "NONE" ? b["compression"] : "MAX";
            var qt = b["quotaType"] == "UNCOMPRESSED" ? "UNCOMPRESSED" : "COMPRESSED";
            Func<string, string> yn = (k) => b[k] == "1" ? "Y" : "N";
            lock (cfg)
            {
                var p = cfg.Policy();
                var u = p.Element("USER"); if (u == null) { u = new XElement("USER"); p.Add(u); }
                var s = p.Element("BACKUP_SET"); if (s == null) { s = new XElement("BACKUP_SET"); p.Add(s); }
                u.SetAttributeValue("QUOTA_GB", gb.ToString(CultureInfo.InvariantCulture)); u.SetAttributeValue("QUOTA_TYPE", qt); u.SetAttributeValue("MAX_BACKUP_SET", n("maxSets", 1, 100, 10));
                u.SetAttributeValue("SAVE_ENCRYPT_KEY", yn("saveKey")); u.SetAttributeValue("REQUIRE_TOTP", yn("requireTotp"));
                foreach (var r in SetControl.Rights) if (b[r[0].ToLowerInvariant()] != null) u.SetAttributeValue(r[0], yn(r[0].ToLowerInvariant()));
                s.SetAttributeValue("HOUR", n("hour", 0, 23, 22)); s.SetAttributeValue("MINUTE", n("minute", 0, 59, 0)); s.SetAttributeValue("RETENTION_DAYS", days);
                s.SetAttributeValue("LOG_RETENTION_DAYS", n("logDays", 7, 3650, 60)); s.SetAttributeValue("COMPRESSION", comp); s.SetAttributeValue("BANDWIDTH_KBPS", n("bandwidth", 0, 10000000, 0));
                s.SetAttributeValue("SQL_FULL_DAY", n("sqlFullDay", -1, 6, -1)); s.SetAttributeValue("VSS", yn("vss")); s.SetAttributeValue("RUN_MISSED", yn("runMissed")); s.SetAttributeValue("RUN_MISSED_NET", yn("runMissedNet"));
                p.Elements("GLOBAL_FILTER").Remove();
                if (b["skipSystem"] == "1") foreach (var g in SystemConfig.DefaultPolicy().Elements("GLOBAL_FILTER")) p.Add(new XElement(g));
                Directory.CreateDirectory(Path.GetDirectoryName(Path_(cfg)));
                Atomic.WriteText(Path_(cfg), p.ToString());
            }
            SysLog.Write(ip, "Admin", by + " changed the defaults for new customers (versions kept " + days + " days, quota " + gb + " GB)");
        }

        /// <summary>A new set: what came as the product's own default takes the administrator's default.</summary>
        public static void ApplyToNewSet(XElement pol, BackupSetInfo s)
        {
            var ps = pol.Element("BACKUP_SET") ?? new XElement("BACKUP_SET");
            Func<string, int, int> n = (k, d) => { int v; return int.TryParse((string)ps.Attribute(k), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : d; };
            var fresh = new BackupSetInfo();
            var r = s.Retention;
            if (r == null || r.Period <= 0 || (r.Unit == "DAYS" && r.Period == fresh.Retention.Period && r.Daily + r.Weekly + r.Monthly + r.Quarterly + r.Yearly == 0))
                s.Retention = new RetentionPolicy { Unit = "DAYS", Period = n("RETENTION_DAYS", RetentionDays) };
            if (s.Hour == fresh.Hour && s.Minute == fresh.Minute && s.MoreSchedules.Count == 0) { s.Hour = Math.Max(0, Math.Min(23, n("HOUR", 22))); s.Minute = Math.Max(0, Math.Min(59, n("MINUTE", 0))); }
            if (s.Compression == fresh.Compression) { var c = (string)ps.Attribute("COMPRESSION"); if (c == "FAST" || c == "NONE" || c == "MAX") s.Compression = c; }
            if (s.BandwidthKbps == 0) s.BandwidthKbps = Math.Max(0, n("BANDWIDTH_KBPS", 0));
            if (s.Vss == fresh.Vss) s.Vss = (string)ps.Attribute("VSS") != "N";
            if (s.RunMissed == fresh.RunMissed) s.RunMissed = (string)ps.Attribute("RUN_MISSED") != "N";
            if (s.RunMissedNet == fresh.RunMissedNet) s.RunMissedNet = (string)ps.Attribute("RUN_MISSED_NET") != "N";
            if (s.Type == "MSSQL" && s.SqlFullDay == fresh.SqlFullDay) s.SqlFullDay = Math.Max(-1, Math.Min(6, n("SQL_FULL_DAY", -1)));   // SQL-050: e.g. full on Friday, differential on the other days
            s.LogRetentionDays = n("LOG_RETENTION_DAYS", 60);
        }
    }
}
