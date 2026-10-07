using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace OnlineBackup.Core
{
    /// <summary>
    /// db\Profile.xml in the Ahsay 6 schema (ROOT / USER / CONTACT / BACKUP_SET …), so ITSguard's configuration-change
    /// monitor and reports read it unchanged. Unknown attributes and elements are kept as they are.
    /// </summary>
    public sealed class Profile
    {
        public XDocument Doc { get; private set; }
        public XElement Root { get { return Doc.Root; } }
        public XElement User { get { return Root.Element("USER"); } }

        Profile(XDocument d) { Doc = d; }

        public static Profile Load(string path) { return new Profile(Atomic.LoadXml(path)); }
        public static Profile Parse(string xml) { return new Profile(XDocument.Parse(xml)); }

        public static Profile Create(string login, string alias, string passwordHash, string language, string timezone)
        {
            var now = RunId.UnixMs(SystemClock.UtcNow).ToString(CultureInfo.InvariantCulture);
            var user = new XElement("USER",
                new XAttribute("LOGIN_NAME", login), new XAttribute("ALIAS", alias ?? ""), new XAttribute("PASSWORD", ""),
                new XAttribute("HASHED_PWD", passwordHash), new XAttribute("LANGUAGE", language ?? "iw"), new XAttribute("TYPE", "PAID"),
                new XAttribute("STATUS", "ENABLE"), new XAttribute("TIMEZONE", timezone ?? "GMT+02:00 (IDT)"), new XAttribute("DISABLED", "N"),
                new XAttribute("REGISTRATION_DATE", now), new XAttribute("CLIENT_TYPE", "OBM"), new XAttribute("QUOTA", "0"),
                new XAttribute("QUOTA_TYPE", "COMPRESSED"), new XAttribute("QUOTA_REMIND_PERCENTAGE", "90"), new XAttribute("ENABLED_MSSQL", "Y"),
                new XAttribute("ENABLED_SHADOW_COPY", "Y"), new XAttribute("ENABLED_DELTA_BLOCK", "Y"), new XAttribute("SAVE_ENCRYPT_KEY", "Y"),
                new XAttribute("EMAIL_NOTIFY", "ALL"), new XAttribute("SEND_WELCOME_MAIL", "Y"), new XAttribute("UPGRADE", "Auto"),
                new XAttribute("BANDWIDTH", "0"), new XAttribute("LOGIN_FAILURE_COUNT", "0"), new XAttribute("USER_LOCKED_TIME", "-1"),
                new XAttribute("TOTP_SECRET", ""), new XAttribute("OWNER", ""), new XAttribute("MAX_BACKUP_SET", "10"),
                new XAttribute("DATA_SIZE", "0"), new XAttribute("DATA_FILE", "0"), new XAttribute("RETAIN_SIZE", "0"), new XAttribute("RETAIN_FILE", "0"),
                new XAttribute("TOTAL_UNCOMPRESS_FILE_SIZE", "0"), new XAttribute("TOTAL_RETAIN_UNCOMPRESS", "0"),
                new XAttribute("LAST_BACKUP", "0"), new XAttribute("LAST_LOGIN", "0"), new XAttribute("LAST_STORAGE_REBUILD", "0"), new XAttribute("Notes", ""));
            return new Profile(new XDocument(new XElement("ROOT", user)));
        }

        public void Save(string path) { Atomic.WriteText(path, Doc.ToString()); }

        public string Get(string attr) { var a = User.Attribute(attr); return a == null ? null : a.Value; }
        public void SetAttr(string attr, object value) { User.SetAttributeValue(attr, Convert.ToString(value, CultureInfo.InvariantCulture)); }
        public long GetLong(string attr) { long v; return long.TryParse(Get(attr), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0; }

        public IEnumerable<XElement> SetElements { get { return Root.Elements("BACKUP_SET"); } }
        public XElement FindSet(string id) { return SetElements.FirstOrDefault(e => (string)e.Attribute("ID") == id); }
        public List<BackupSetInfo> Sets { get { return SetElements.Select(BackupSetInfo.FromXml).ToList(); } }

        public void AddContact(string name, string email)
        {
            User.Add(new XElement("CONTACT", new XAttribute("ID", RunId.UnixMs(SystemClock.UtcNow)), new XAttribute("NAME", name ?? ""), new XAttribute("EMAIL", email ?? "")));
        }
    }

    /// <summary>One BACKUP_SET of the profile, with the fields phase 1 uses. Written back with the Ahsay names.</summary>
    public sealed class BackupSetInfo
    {
        public string Id, Name, Type = "FILE", Computer = "";
        /// <summary>Agent D (D-4): the registration (device id) of the computer that made the set — two computers that carry
        /// the same name (a cloned image registered again, two offices' "RECEPTION") each took the other's set as its own.</summary>
        public string Device = "";
        public List<string> Sources = new List<string>();
        public List<string> Deselected = new List<string>();
        public List<FilterRule> Filters = new List<FilterRule>();
        public int Hour = 22, Minute = 0, DurationHours = -1;
        public string Days = "SMTWTFS";                  // weekly days (S M T W T F S = Sun..Sat), all = daily
        /// <summary>SCHED-020: more times for the same set (Ahsay: several schedules), each with its days.</summary>
        public List<ScheduleSlot> MoreSchedules = new List<ScheduleSlot>();
        public RetentionPolicy Retention = new RetentionPolicy();
        public bool Vss = true, FollowLink = true, BackupPermissions = true;
        public long MinDeltaFileSize = 25L * 1024 * 1024;
        public int MaxDeltaNo = 100, MaxDeltaRatio = 50;
        public string KeyType = "PASSWORD", KeyCheck = "", KeySalt = "";
        public bool LocalCopy; public string LocalCopyPath = @"C:\LocalBackup"; public int LocalCopyDays = 7;
        public List<string> PreCommands = new List<string>(), PostCommands = new List<string>();
        public bool StopOnPreCommandFailure;
        public int LogRetentionDays = 60;
        public string WorkingDir = "";
        public string SqlUser = "";           // ADMIN_USERNAME (the password stays on the agent, protected)
        public int LogIntervalMinutes;         // MSSQL transaction-log backups every N minutes (0 = off)
        public string DeltaType = "I";          // changes inside large files: I = incremental (against the last version, smallest), D = differential (against the last full copy, a restore needs 2 parts)
        public int SqlFullDay = -1;             // MSSQL: -1 = a full backup every run; 0..6 (Sunday..Saturday) = full on that day, differential on the others
        public string M365Tenant = "", M365ClientId = "", M365Users = "";   // Microsoft 365 sets: tenant, application id, users (empty = all)
        public string DbHost = ""; public int DbPort;                       // MySQL / PostgreSQL sets (the user is ADMIN_USERNAME)
        public string GwsAdmin = "";                                         // Google Workspace sets: an administrator (lists the users when none are given)
        public string VmDatacenter = "", VmThumbprint = "";                  // VMware sets (host = DB_HOST, user = ADMIN_USERNAME): datacenter, certificate SHA-256
        /// <summary>SET-020: a set that runs on several computers is one set per computer — the copies point to the first
        /// (PARENT_SET) and take its settings; each keeps its own backups. RUN_REQUEST / STOP_REQUEST: "back up now" / "stop"
        /// from the admin site (Unix ms), read by the agent within a minute. Read only here: the server writes them.</summary>
        public string Parent = ""; public long RunRequest, StopRequest;
        /// <summary>COMP-010: MAX (default — the smallest backups), FAST, NONE. RES-010: upload limit in KB/s (0 = none), low priority
        /// (the computer stays responsive), pause when the computer is busy (CPU % over which the backup waits; 0 = never).
        /// MISS-010: a run missed (computer off / no internet) runs when the computer is back — after a delay, only if the last
        /// backup is older than N hours; or not at all.</summary>
        public string Compression = "MAX"; public int BandwidthKbps, BusyCpuPercent; public bool LowPriority = true;
        public bool RunMissed = true; public int MissedDelayMinutes = 5, MissedMinHours;
        /// <summary>MISS-020: a backup missed or cut off because the internet (or the server) was down starts as soon as it is back.</summary>
        public bool RunMissedNet = true;
        /// <summary>DEST-010: where the backups go — SERVER (default), BOTH (server + a local copy, LocalCopyPath), LOCAL (only on a local
        /// disk / NAS: restic engine, the repository at LocalCopyPath; the results still reach the server's reports).</summary>
        public string DestMode = "SERVER";
        public string Engine = "";             // "" = this product's own engine (all Windows from 2003); "RESTIC" = restic (Windows 10 / 2016+)

        public static BackupSetInfo FromXml(XElement e)
        {
            Func<string, string, string> a = (n, d) => { var x = e.Attribute(n); return x == null ? d : x.Value; };
            var s = new BackupSetInfo
            {
                Id = a("ID", ""), Name = a("NAME", ""), Type = a("TYPE", "FILE"), Computer = a("SCHEDULE_HOST", ""), Device = a("SCHEDULE_DEVICE", ""), Engine = a("ENGINE", ""), GwsAdmin = a("GWS_ADMIN", ""), VmDatacenter = a("VM_DATACENTER", ""), VmThumbprint = a("VM_THUMBPRINT", ""), DbHost = a("DB_HOST", ""), DbPort = (int)L(a("DB_PORT", "0")), M365Tenant = a("M365_TENANT", ""), M365ClientId = a("M365_CLIENT_ID", ""), M365Users = a("M365_USERS", ""),
                Vss = a("ENABLED_SHADOW_COPY", "Y") == "Y", FollowLink = a("WIN_FOLLOW_LINK", "Y") == "Y",
                BackupPermissions = a("BSET_UPLOAD_PERMISSION", "Y") == "Y",
                MinDeltaFileSize = L(a("MIN_DELTA_FILE_SIZE", "26214400")), MaxDeltaNo = (int)L(a("MAX_DELTA_NO", "100")),
                MaxDeltaRatio = (int)L(a("MAX_DELTA_RATIO", "50")), LogRetentionDays = (int)L(a("LOG_RETENTION_DAYS", "60")),
                WorkingDir = a("WORKING_DIR", ""), SqlUser = a("ADMIN_USERNAME", ""), LogIntervalMinutes = (int)L(a("LOG_INTERVAL_MINUTES", "0")),
                DeltaType = a("DEFAULT_DELTA_TYPE", "I") == "D" ? "D" : "I", SqlFullDay = (int)L(a("SQL_FULL_DAY", "-1")),
                Parent = a("PARENT_SET", ""), RunRequest = L(a("RUN_REQUEST", "0")), StopRequest = L(a("STOP_REQUEST", "0")),
                Compression = a("COMPRESSION", "MAX"), BandwidthKbps = (int)L(a("BANDWIDTH_KBPS", "0")), BusyCpuPercent = (int)L(a("BUSY_CPU_PERCENT", "0")), LowPriority = a("LOW_PRIORITY", "Y") == "Y",
                DestMode = a("DEST_MODE", "SERVER"),
                RunMissed = a("RUN_MISSED", "Y") == "Y", RunMissedNet = a("RUN_MISSED_NET", "Y") == "Y", MissedDelayMinutes = (int)L(a("MISSED_DELAY_MINUTES", "5")), MissedMinHours = (int)L(a("MISSED_MIN_HOURS", "0"))
            };
            s.Sources = e.Elements("SEL-SOURCE").Select(x => x.Value).Where(v => v.Length > 0).ToList();
            s.Deselected = e.Elements("DE-SOURCE").Select(x => x.Value).Where(v => v.Length > 0).ToList();
            s.Filters = e.Elements("FILTER").Select(FilterRule.FromXml).ToList();
            var all = e.Elements().Where(x => x.Name == "DAILY_SCHEDULE" || x.Name == "WEEKLY_SCHEDULE").ToList();
            if (all.Count > 0)
            {
                var first = ScheduleSlot.FromXml(all[0]);
                s.Hour = first.Hour; s.Minute = first.Minute; s.Days = first.Days;
                s.DurationHours = (int)L((string)all[0].Attribute("DURATION") ?? "-1");
                s.MoreSchedules = all.Skip(1).Select(ScheduleSlot.FromXml).ToList();
            }
            var rp = e.Element("RETENTION_POLICY");
            if (rp != null)
            {
                s.Retention.Unit = (string)rp.Attribute("UNIT") ?? "DAYS";
                s.Retention.Period = (int)L((string)rp.Attribute("PERIOD") ?? "30");
                foreach (var rs in rp.Elements("RETENTION_SETTING"))
                {
                    int keep = (int)L((string)rs.Attribute("KEEP") ?? "0");
                    switch ((string)rs.Attribute("TYPE"))
                    {
                        case "DAILY": s.Retention.Daily = keep; break;
                        case "WEEKLY": s.Retention.Weekly = keep; break;
                        case "MONTHLY": s.Retention.Monthly = keep; break;
                        case "QUARTERLY": s.Retention.Quarterly = keep; break;
                        case "YEARLY": s.Retention.Yearly = keep; break;
                    }
                }
            }
            var k = e.Element("ENCRYPTING_KEY");
            if (k != null) { s.KeyType = (string)k.Attribute("KEY_TYPE") ?? "PASSWORD"; s.KeyCheck = (string)k.Attribute("KEY") ?? ""; s.KeySalt = (string)k.Attribute("SALT") ?? ""; }
            var lc = e.Element("EXTRA_LOCAL_BACKUP");
            if (lc != null)
            {
                s.LocalCopy = (string)lc.Attribute("ENABLED") == "Y";
                s.LocalCopyPath = (string)lc.Attribute("BACKUP_TO") ?? s.LocalCopyPath;
                s.LocalCopyDays = (int)L((string)lc.Attribute("PERIOD") ?? "7");
            }
            s.PreCommands = e.Elements("PRE_CMD").Select(x => (string)x.Attribute("PATH")).Where(v => !string.IsNullOrEmpty(v)).ToList();
            s.PostCommands = e.Elements("POST_CMD").Select(x => (string)x.Attribute("PATH")).Where(v => !string.IsNullOrEmpty(v)).ToList();
            s.StopOnPreCommandFailure = e.Elements("PRE_CMD").Any(x => (string)x.Attribute("STOP_ON_FAILURE") == "Y");
            return s;
        }

        static long L(string v) { long r; return long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out r) ? r : 0; }

        public XElement ToXml(XElement existing = null)
        {
            var e = existing ?? new XElement("BACKUP_SET");
            Action<string, object> a = (n, v) => e.SetAttributeValue(n, Convert.ToString(v, CultureInfo.InvariantCulture));
            a("ID", Id); a("NAME", Name); a("TYPE", Type); a("SCHEDULE_HOST", Computer); e.SetAttributeValue("SCHEDULE_DEVICE", string.IsNullOrEmpty(Device) ? null : Device);
            a("ENABLED_SHADOW_COPY", Vss ? "Y" : "N"); a("WIN_FOLLOW_LINK", FollowLink ? "Y" : "N"); a("FOLLOW_LINK", "N");
            a("BSET_UPLOAD_PERMISSION", BackupPermissions ? "Y" : "N");
            a("ENABLE_DELTA_BLOCK", "Y"); a("MIN_DELTA_FILE_SIZE", MinDeltaFileSize); a("MAX_DELTA_NO", MaxDeltaNo); a("MAX_DELTA_RATIO", MaxDeltaRatio);
            a("DEFAULT_DELTA_TYPE", DeltaType == "D" ? "D" : "I"); a("SQL_FULL_DAY", SqlFullDay); a("LOG_RETENTION_DAYS", LogRetentionDays); a("WORKING_DIR", WorkingDir);
            a("COMPRESSION", Compression == "FAST" || Compression == "NONE" ? Compression : "MAX"); a("BANDWIDTH_KBPS", Math.Max(0, BandwidthKbps)); a("BUSY_CPU_PERCENT", Math.Max(0, Math.Min(100, BusyCpuPercent))); a("LOW_PRIORITY", LowPriority ? "Y" : "N");
a("DEST_MODE", DestMode == "LOCAL" || DestMode == "BOTH" ? DestMode : "SERVER");             a("RUN_MISSED", RunMissed ? "Y" : "N"); a("RUN_MISSED_NET", RunMissedNet ? "Y" : "N"); a("MISSED_DELAY_MINUTES", Math.Max(0, MissedDelayMinutes)); a("MISSED_MIN_HOURS", Math.Max(0, MissedMinHours));
            a("ADMIN_USERNAME", SqlUser); a("LOG_INTERVAL_MINUTES", LogIntervalMinutes); a("ENGINE", Engine); a("GWS_ADMIN", GwsAdmin); a("VM_DATACENTER", VmDatacenter); a("VM_THUMBPRINT", VmThumbprint); a("DB_HOST", DbHost); a("DB_PORT", DbPort); a("M365_TENANT", M365Tenant); a("M365_CLIENT_ID", M365ClientId); a("M365_USERS", M365Users);
            foreach (var n in new[] { "SEL-SOURCE", "DE-SOURCE", "FILTER", "DAILY_SCHEDULE", "WEEKLY_SCHEDULE", "RETENTION_POLICY", "ENCRYPTING_KEY", "EXTRA_LOCAL_BACKUP", "PRE_CMD", "POST_CMD" })
                e.Elements(n).Remove();
            foreach (var src in Sources) e.Add(new XElement("SEL-SOURCE", src));
            foreach (var src in Deselected) e.Add(new XElement("DE-SOURCE", src));
            foreach (var f in Filters) e.Add(f.ToXml());
            var stamp = RunId.UnixMs(SystemClock.UtcNow);
            int slotNo = 0;
            foreach (var slot in new[] { new ScheduleSlot { Days = Days, Hour = Hour, Minute = Minute } }.Concat(MoreSchedules))
            { slotNo++; e.Add(slot.ToXml(stamp + slotNo, slotNo == 1 ? "Backup Schedule" : "Backup Schedule " + slotNo, DurationHours, Type == "MSSQL" ? "DATABASE" : "FILE")); }
            var rp = new XElement("RETENTION_POLICY", new XAttribute("UNIT", Retention.Unit), new XAttribute("PERIOD", Retention.Period));
            Action<string, int> rs = (t, n) => { if (n > 0) rp.Add(new XElement("RETENTION_SETTING", new XAttribute("ID", stamp), new XAttribute("NAME", t), new XAttribute("TYPE", t), new XAttribute("KEEP", n), new XAttribute("OVER_LAP_SENSITIVE", "N"))); };
            rs("DAILY", Retention.Daily); rs("WEEKLY", Retention.Weekly); rs("MONTHLY", Retention.Monthly); rs("QUARTERLY", Retention.Quarterly); rs("YEARLY", Retention.Yearly);
            e.Add(rp);
            e.Add(new XElement("ENCRYPTING_KEY", new XAttribute("KEY_TYPE", KeyType), new XAttribute("ALGORITHM", "AES"), new XAttribute("MODE", "CBC-HMAC"), new XAttribute("LENGTH", 256),
                new XAttribute("KEY", KeyCheck ?? ""), new XAttribute("SALT", KeySalt ?? "")));
            e.Add(new XElement("EXTRA_LOCAL_BACKUP", new XAttribute("ENABLED", LocalCopy ? "Y" : "N"), new XAttribute("ZIP", "Y"), new XAttribute("BACKUP_TO", LocalCopyPath),
                new XAttribute("SKIP_OFFSITE_BACKUP", "N"), new XAttribute("SET_LOCAL_COPY_PERMISSION", "Y"), new XAttribute("ENABLE_RETENTION", "Y"), new XAttribute("PERIOD", LocalCopyDays), new XAttribute("UNIT", "DAYS")));
            int ci = 0;
            foreach (var c in PreCommands) e.Add(new XElement("PRE_CMD", new XAttribute("ID", stamp + ci++), new XAttribute("NAME", "pre" + ci), new XAttribute("PATH", c), new XAttribute("WORKING_DIR", ""), new XAttribute("STOP_ON_FAILURE", StopOnPreCommandFailure ? "Y" : "N")));
            foreach (var c in PostCommands) e.Add(new XElement("POST_CMD", new XAttribute("ID", stamp + ci++), new XAttribute("NAME", "post" + ci), new XAttribute("PATH", c), new XAttribute("WORKING_DIR", "")));
            return e;
        }
    }

    /// <summary>One schedule of a set: the days (S M T W T F S, '-' = off; all = daily), the hour and the minute.</summary>
    public sealed class ScheduleSlot
    {
        public string Days = "SMTWTFS"; public int Hour = 22, Minute = 0;
        static readonly string[] DayNames = { "SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT" };

        public static ScheduleSlot FromXml(XElement x)
        {
            int h, m; int.TryParse((string)x.Attribute("HOUR") ?? "22", out h); int.TryParse((string)x.Attribute("MINUTE") ?? "0", out m);
            var s = new ScheduleSlot { Hour = Math.Max(0, Math.Min(23, h)), Minute = Math.Max(0, Math.Min(59, m)) };
            if (x.Name == "WEEKLY_SCHEDULE")
            {
                var sb = new StringBuilder();
                for (int i = 0; i < 7; i++) sb.Append((string)x.Attribute(DayNames[i]) == "Y" ? "SMTWTFS"[i] : '-');
                s.Days = sb.ToString();
            }
            return s;
        }

        public XElement ToXml(long id, string name, int durationHours, string backupType)
        {
            if (Days == "SMTWTFS")
                return new XElement("DAILY_SCHEDULE", new XAttribute("ID", id), new XAttribute("NAME", name), new XAttribute("HOUR", Hour), new XAttribute("MINUTE", Minute),
                    new XAttribute("DURATION", durationHours), new XAttribute("BACKUP_TYPE", backupType), new XAttribute("BACKUP_INTERVAL", -1), new XAttribute("ENABLED_SKIP_BACKUP", "N"));
            var w = new XElement("WEEKLY_SCHEDULE", new XAttribute("ID", id), new XAttribute("NAME", name));
            for (int i = 0; i < 7; i++) w.Add(new XAttribute(DayNames[i], Days.Length > i && Days[i] != '-' ? "Y" : "N"));
            w.Add(new XAttribute("HOUR", Hour), new XAttribute("MINUTE", Minute), new XAttribute("DURATION", durationHours), new XAttribute("BACKUP_TYPE", backupType), new XAttribute("ENABLED_SKIP_BACKUP", "N"));
            return w;
        }
    }

    /// <summary>Ahsay FILTER: TOP_DIR, TYPE (CONTAIN / END_WITH / START_WITH / EXACT / WILDCARD), INCLUDE, ONLY, APPLY_DIR, APPLY_FILE, PATTERN list.</summary>
    public sealed class FilterRule
    {
        public string TopDir = "", Type = "CONTAIN", Name = "Filter";   // NAME: "COMMON" = the admin site's "skip system and temporary files"
        public bool Include, Only = true, ApplyDir, ApplyFile = true;
        public List<string> Patterns = new List<string>();

        public static FilterRule FromXml(XElement e)
        {
            return new FilterRule
            {
                TopDir = (string)e.Attribute("TOP_DIR") ?? "", Type = (string)e.Attribute("TYPE") ?? "CONTAIN", Name = (string)e.Attribute("NAME") ?? "Filter",
                Include = (string)e.Attribute("INCLUDE") == "Y", Only = (string)e.Attribute("ONLY") != "N",
                ApplyDir = (string)e.Attribute("APPLY_DIR") == "Y", ApplyFile = (string)e.Attribute("APPLY_FILE") != "N",
                Patterns = e.Elements("PATTERN").Select(p => p.Value).ToList()
            };
        }

        public XElement ToXml()
        {
            var e = new XElement("FILTER", new XAttribute("ID", RunId.UnixMs(SystemClock.UtcNow)), new XAttribute("NAME", string.IsNullOrEmpty(Name) ? "Filter" : Name), new XAttribute("TYPE", Type), new XAttribute("TOP_DIR", TopDir),
                new XAttribute("INCLUDE", Include ? "Y" : "N"), new XAttribute("ONLY", Only ? "Y" : "N"), new XAttribute("APPLY_DIR", ApplyDir ? "Y" : "N"), new XAttribute("APPLY_FILE", ApplyFile ? "Y" : "N"), new XAttribute("GLOBAL_FILTER_OWNER", ""));
            foreach (var p in Patterns) e.Add(new XElement("PATTERN", p));
            return e;
        }

        /// <summary>Bug 116: the top folder and what is under it - "Docs" does not cover the siblings "Docs2" or "DocsArchive".</summary>
        bool UnderTop(string fullPath)
        {
            var top = TopDir.TrimEnd('\\', '/');
            if (!fullPath.StartsWith(top, StringComparison.OrdinalIgnoreCase)) return false;
            if (fullPath.Length == top.Length) return true;
            var c = fullPath[top.Length]; return c == '\\' || c == '/';
        }

        /// <summary>True if the rule's patterns match this name (file or folder name).</summary>
        public bool Matches(string fullPath, string name, bool isDir)
        {
            if (isDir && !ApplyDir) return false;
            if (!isDir && !ApplyFile) return false;
            if (TopDir.Length > 0 && !UnderTop(fullPath)) return false;
            foreach (var p in Patterns)
            {
                var n = name.ToLowerInvariant(); var q = p.ToLowerInvariant();
                switch (Type)
                {
                    case "END_WITH": if (n.EndsWith(q)) return true; break;
                    case "START_WITH": if (n.StartsWith(q)) return true; break;
                    case "EXACT": if (n == q) return true; break;
                    case "WILDCARD": if (Wildcard(n, q)) return true; break;
                    default: if (n.Contains(q)) return true; break;
                }
            }
            return false;
        }

        static bool Wildcard(string s, string p)
        {
            int si = 0, pi = 0, star = -1, mark = 0;
            while (si < s.Length)
            {
                if (pi < p.Length && (p[pi] == '?' || p[pi] == s[si])) { si++; pi++; }
                else if (pi < p.Length && p[pi] == '*') { star = pi++; mark = si; }
                else if (star >= 0) { pi = star + 1; si = ++mark; }
                else return false;
            }
            while (pi < p.Length && p[pi] == '*') pi++;
            return pi == p.Length;
        }
    }

    /// <summary>Bug 96b / 99: replacing a file on Windows in one step (FileRenameInfoEx with POSIX semantics).</summary>
    static class WindowsRename
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, int infoClass, IntPtr info, uint size);

        const uint Delete = 0x00010000, Synchronize = 0x00100000, ShareAll = 7, OpenExisting = 3;
        const int FileRenameInfoEx = 22, ReplaceIfExists = 0x1, PosixSemantics = 0x2;
        static int unsupported;   // 1: this Windows (or file system) has no POSIX rename - File.Replace from then on

        static string Long(string p) { var f = Path.GetFullPath(p); return f.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + f.Substring(2) : @"\\?\" + f; }

        /// <summary>True when replaced; false when this Windows cannot (the caller uses File.Replace). A refusal ("used by another
        /// process", access denied) is thrown as IOException, so the caller tries again as before.</summary>
        internal static bool ReplaceAtOnce(string source, string target)
        {
            if (System.Threading.Thread.VolatileRead(ref unsupported) == 1) return false;
            using (var h = CreateFileW(Long(source), Delete | Synchronize, ShareAll, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero))
            {
                if (h.IsInvalid) throw Refused(System.Runtime.InteropServices.Marshal.GetLastWin32Error(), source);
                // FILE_RENAME_INFO: Flags (4 bytes), RootDirectory (a handle, aligned), FileNameLength (4), FileName (WCHAR[])
                var name = Long(target);
                int rootAt = IntPtr.Size, lengthAt = 2 * IntPtr.Size, nameAt = lengthAt + 4;
                int size = nameAt + (name.Length + 1) * 2;
                var buf = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
                try
                {
                    for (int i = 0; i < size; i++) System.Runtime.InteropServices.Marshal.WriteByte(buf, i, 0);
                    System.Runtime.InteropServices.Marshal.WriteInt32(buf, 0, ReplaceIfExists | PosixSemantics);
                    System.Runtime.InteropServices.Marshal.WriteIntPtr(buf, rootAt, IntPtr.Zero);
                    System.Runtime.InteropServices.Marshal.WriteInt32(buf, lengthAt, name.Length * 2);
                    System.Runtime.InteropServices.Marshal.Copy(name.ToCharArray(), 0, new IntPtr(buf.ToInt64() + nameAt), name.Length);
                    if (SetFileInformationByHandle(h, FileRenameInfoEx, buf, (uint)size)) return true;
                    int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                    // 1 invalid function, 50 not supported, 87 invalid parameter, 124 invalid level: no POSIX rename here
                    if (err == 1 || err == 50 || err == 87 || err == 124) { System.Threading.Thread.VolatileWrite(ref unsupported, 1); return false; }
                    throw Refused(err, target);
                }
                finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buf); }
            }
        }

        static IOException Refused(int err, string path)
        {
            return new IOException(new System.ComponentModel.Win32Exception(err).Message + " (" + path + ")", unchecked((int)0x80070000) | err);
        }

        /// <summary>Removes the old file of a File.Replace; a program that looks at it for a moment (an antivirus) only delays it.</summary>
        internal static void DeleteSoon(string path)
        {
            for (int attempt = 1; attempt <= 100; attempt++)
            {
                try { File.Delete(path); return; }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { System.Threading.Thread.Sleep(Math.Min(attempt, 20)); }
            }
        }
    }

    /// <summary>Write to a temporary name, flush to disk, then rename: a crash never leaves a half-written file.</summary>
    public static class Atomic
    {
        /// <summary>A file left by a write that never finished: "&lt;name&gt;.tmp" (or ".old") + 8 hex digits — judged by the FILE NAME only.
        /// Bug 35 (Agent B): the index rebuild tested the whole path for ".tmp" and deleted every object of a customer
        /// called "acme.tmp" (or under a folder "D:\Backup.tmp").</summary>
        public static bool IsTemp(string path)
        {
            var n = Path.GetFileName(path ?? "");
            // bug 99: ".old" + 8 hex is the old file of a Windows replace, left only when the process stopped right then
            int i = Math.Max(n.LastIndexOf(".tmp", StringComparison.Ordinal), n.LastIndexOf(".old", StringComparison.Ordinal));
            if (i < 0 || n.Length - i != 12) return false;
            for (int k = i + 4; k < n.Length; k++) if (Uri.IsHexDigit(n[k]) == false || char.IsUpper(n[k])) return false;
            return true;
        }

        public static void WriteText(string path, string text) { WriteBytes(path, new UTF8Encoding(false).GetBytes(text)); }

        public static void WriteBytes(string path, byte[] data)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = path + ".tmp" + Guid.NewGuid().ToString("N").Substring(0, 8);
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(data, 0, data.Length);
                fs.Flush(true);
            }
            // Windows refuses to replace a file another program has open at that moment (a reader, an antivirus, the indexer),
            // and two writers can both see "no file yet": the CI Windows job failed here with "being used by another process".
            // Linux never refuses. So: try again for a while; if it still fails, leave no temporary file behind.
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    if (!File.Exists(path)) { File.Move(tmp, path); return; }
                    if (Environment.OSVersion.Platform != PlatformID.Win32NT) { File.Replace(tmp, path, null); return; }   // rename(): atomic
                    // Bug 96b (QA shards, Windows): File.Replace moves the old file away before the new one is in - while a reader
                    // holds it, the name is missing for a moment ("Unknown device"). The rename with POSIX semantics (Windows 10
                    // 1809 / Server 2019 and later) swaps the name in one step: the file exists at every moment, a reader that has
                    // it open keeps reading the old content, and nothing is left behind (bug 99: "<name>~RFxxxx.TMP" copies).
                    if (WindowsRename.ReplaceAtOnce(tmp, path)) return;
                    // older Windows (or a file system without it): the replace with our own name for the old file, removed after
                    var old = path + ".old" + Guid.NewGuid().ToString("N").Substring(0, 8);
                    File.Replace(tmp, path, old, true);
                    WindowsRename.DeleteSoon(old);
                    return;
                }
                catch (Exception e) when ((e is IOException || e is UnauthorizedAccessException) && attempt < ReplaceAttempts && File.Exists(tmp))
                {
                    // bug 96: a reader that opens the file again at once (a plain reader in a loop, an antivirus) leaves only
                    // short gaps; many quick tries find one, a few slow ones did not (gate 37635681591, two writers + a reader)
                    System.Threading.Thread.Sleep(Math.Min(attempt, 25));
                }
                catch (Exception) { try { File.Delete(tmp); } catch (Exception) { } throw; }
            }
        }

        /// <summary>How often a refused replace is tried (about 10 seconds in all, mostly 25 ms apart).</summary>
        public static int ReplaceAttempts = 420;
        /// <summary>How often a refused open for reading is tried (about 5 seconds in all, mostly 20 ms apart).</summary>
        public static int ReadAttempts = 260;

        /// <summary>File.ReadAllText / ReadAllLines / ReadAllBytes, the same in every way except that the file may be REPLACED while
        /// it is open (FileShare.Delete): on Windows a plain read holds the file so that an Atomic write cannot replace it at that
        /// moment (bug 93b). Every reader of a file the product writes uses these.</summary>
        public static string ReadAllText(string path) { using (var r = new StreamReader(OpenShared(path), Encoding.UTF8, true)) return r.ReadToEnd(); }
        public static string ReadAllText(string path, Encoding encoding) { using (var r = new StreamReader(OpenShared(path), encoding, true)) return r.ReadToEnd(); }
        public static string[] ReadAllLines(string path) { return ReadAllLines(path, Encoding.UTF8); }
        public static string[] ReadAllLines(string path, Encoding encoding)
        {
            var lines = new List<string>();
            using (var r = new StreamReader(OpenShared(path), encoding, true)) { string l; while ((l = r.ReadLine()) != null) lines.Add(l); }
            return lines.ToArray();
        }
        public static byte[] ReadAllBytes(string path)
        {
            using (var fs = OpenShared(path))
            {
                var ms = new MemoryStream(fs.CanSeek ? (int)Math.Min(fs.Length, int.MaxValue) : 0);
                fs.CopyTo(ms); return ms.ToArray();
            }
        }
        /// <summary>The product's XML files (profiles, users, computers, settings) read the same way (bug 96: XDocument.Load(path)
        /// shares only reading, so on Windows it was refused while another request replaced the file - "Server error" 500).</summary>
        public static System.Xml.Linq.XDocument LoadXml(string path) { using (var fs = OpenShared(path)) return System.Xml.Linq.XDocument.Load(fs); }
        public static System.Xml.Linq.XElement LoadXElement(string path) { using (var fs = OpenShared(path)) return System.Xml.Linq.XElement.Load(fs); }

        /// <summary>Opens for reading while letting the file be written or replaced. On Windows the open itself can still be refused
        /// for a moment ("being used by another process") while a replace is under way: that is tried again for a while; a missing
        /// file or any other error is reported at once, as File.ReadAll* reports it.</summary>
        static FileStream OpenShared(string path)
        {
            for (int attempt = 1; ; attempt++)
            {
                try { return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
                catch (IOException e) when (attempt < ReadAttempts && IsSharingRefusal(e)) { System.Threading.Thread.Sleep(Math.Min(attempt, 20)); }
                catch (UnauthorizedAccessException) when (attempt < ReadAttempts && File.Exists(path) && !Directory.Exists(path)) { System.Threading.Thread.Sleep(Math.Min(attempt, 20)); }
            }
        }

        /// <summary>ERROR_SHARING_VIOLATION (32) or ERROR_LOCK_VIOLATION (33) - "used by another process" (Exception.HResult is
        /// not public on .NET 4.0).</summary>
        internal static bool IsSharingRefusal(IOException e)
        {
            if (e is FileNotFoundException || e is DirectoryNotFoundException) return false;
            int code = System.Runtime.InteropServices.Marshal.GetHRForException(e) & 0xFFFF;
            return code == 32 || code == 33;
        }

        /// <summary>Reads a file that may be written at the same moment (a log): on Windows a plain read is refused then.</summary>
        public static string ReadShared(string path)
        {
            using (var r = new StreamReader(OpenShared(path), Encoding.UTF8)) return r.ReadToEnd();
        }
        public static string[] ReadLinesShared(string path) { return ReadShared(path).Replace("\r\n", "\n").Split('\n').Where(l => l.Length > 0).ToArray(); }

        public static void AppendLine(string path, string line)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                var b = new UTF8Encoding(false).GetBytes(line + "\r\n");
                fs.Write(b, 0, b.Length);
                fs.Flush(true);
            }
        }
    }
}
