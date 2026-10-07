using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using OnlineBackup.Core;

namespace OnlineBackup.Agent
{
    /// <summary>One item to back up: its name in the backup (Path) and where its bytes are read (ReadPath).</summary>
    public sealed class SourceItem
    {
        public string Path;       // e.g. C:\Data\a.xlsx, or Microsoft SQL Server\SERVER\DB\DATABASE_DB.bak, or System State\…
        public string ReadPath;   // live file, VSS snapshot path or a temporary dump
        public FileInfo Info;
    }

    /// <summary>What a set backs up, by type: files (with VSS), Microsoft SQL Server (native BACKUP), System State (Windows tools).</summary>
    public static class Sources
    {
        /// <summary>
        /// R1: an entry of the "unreachable" list with this prefix only keeps earlier backed-up files (a differential keeps
        /// its full backup); every other entry is something that should have been backed up and was not — an error.
        /// </summary>
        public const string KeepOnly = "\u0001";

        public static IEnumerable<SourceItem> Items(BackupSetInfo set, string mode, Vss snapshot, string temp, string sqlPassword, Action<string> info, Action<string> warn, List<string> unreachable)
        {
            if (set.Type == "MSSQL" || set.Type == "Microsoft SQL Server")
                return SqlBackup.Run(set, mode, temp, sqlPassword, info, warn, unreachable);
            if (set.Type == "SYSTEMSTATE" || set.Type == "System State")
                return SystemState.Run(set, temp, info, warn, unreachable);
            if (set.Type == DbDump.MySql || set.Type == DbDump.Postgres)
                return DbItems(set, sqlPassword, info, warn, unreachable);
            if (set.Type == HyperV.Type)
            {
                var home = CurrentHome; var staging = HyperV.Export(home, set, info, warn);
                return Directory.GetFiles(staging, "*", SearchOption.AllDirectories).Where(f => !f.Contains(".new" + System.IO.Path.DirectorySeparatorChar))
                    .Select(f => new SourceItem { Path = "Hyper-V\\" + f.Substring(staging.Length + 1).Replace(System.IO.Path.DirectorySeparatorChar, '\\'), ReadPath = f, Info = new FileInfo(f) }).ToList();
            }
            if (set.Type == Oracle.Type) return Staged("Oracle", Oracle.Backup(CurrentHome, set, sqlPassword, info, warn));
            if (set.Type == VMware.Type) return Staged("VMware", VMware.Backup(CurrentHome, set, sqlPassword, info, warn));
            if (set.Type == DiskImage.Type)
                return DiskImage.Run(set, info, warn, unreachable);
            return Files(set, snapshot, info, warn, unreachable);
        }

        /// <summary>The dumps under stable names ("MySQL\db.sql"), so each database keeps one delta chain.</summary>
        static IEnumerable<SourceItem> DbItems(BackupSetInfo set, string password, Action<string> info, Action<string> warn, List<string> unreachable)
        {
            var home = CurrentHome;
            var root = DbDump.Folder(home, set);
            return DbDump.Dump(home, set, password, info, warn, unreachable)
                .Select(f => new SourceItem { Path = f.Substring(root.Length + 1).Replace(System.IO.Path.DirectorySeparatorChar, '\\'), ReadPath = f, Info = new FileInfo(f) }).ToList();
        }

        /// <summary>The files of a staging folder (Oracle / VMware) under stable names "&lt;kind&gt;\&lt;instance or VM&gt;\…".</summary>
        static IEnumerable<SourceItem> Staged(string kind, string staging)
        {
            return Directory.GetFiles(staging, "*", SearchOption.AllDirectories).Where(f => !f.Contains(".new" + System.IO.Path.DirectorySeparatorChar))
                .Select(f => new SourceItem { Path = kind + "\\" + f.Substring(staging.Length + 1).Replace(System.IO.Path.DirectorySeparatorChar, '\\'), ReadPath = f, Info = new FileInfo(f) }).ToList();
        }

        /// <summary>The agent folder of the run in progress (set by BackupRun).</summary>
        [ThreadStatic] public static AgentHome CurrentHome;

        static IEnumerable<SourceItem> Files(BackupSetInfo set, Vss snapshot, Action<string> info, Action<string> warn, List<string> unreachable)
        {
            foreach (var f in Scanner.Files(set, warn, unreachable, info))
                yield return new SourceItem { Path = f.FullName, ReadPath = snapshot == null ? f.FullName : snapshot.Map(f.FullName), Info = f };
        }
    }

    /// <summary>
    /// Microsoft SQL Server with SQL Server's own BACKUP commands (not copies of .mdf files), so every backup is a standard
    /// .bak/.trn that restores even without this product. Each run: "Database - complete" to the temporary folder, then
    /// sent as a delta against the previous one (as Ahsay does). Log mode: BACKUP LOG every N minutes, one .trn per run.
    /// Sources: "Microsoft SQL Server\INSTANCE" (all databases) or "Microsoft SQL Server\INSTANCE\DB".
    /// Uses sqlcmd (SQL 2005+) or osql (SQL 2000); Windows authentication by default (the service runs as Local System).
    /// </summary>
    public static class SqlBackup
    {
        public const string Prefix = "Microsoft SQL Server";

        /// <summary>Where the time of each database's last full backup by this set is kept (on this computer).</summary>
        static string MarkerDir(BackupSetInfo set, string temp) { return Sources.CurrentHome != null ? System.IO.Path.Combine(Sources.CurrentHome.SetDir(set.Id), "sql-full") : System.IO.Path.Combine(temp, "sql-full"); }

        public static string Tool
        {
            get
            {
                var env = Environment.GetEnvironmentVariable("OB_SQLCMD");
                if (!string.IsNullOrEmpty(env)) return env;
                return "sqlcmd";
            }
        }

        public static string Run(string instance, string query, string user = null, string password = null)
        {
            // a SQL login (usually sa): the password goes to sqlcmd in its environment (SQLCMDPASSWORD), never on the command
            // line, where any user of the computer could see it in the process list; osql (SQL 2000) has only -P
            bool osql = Tool.EndsWith("osql", StringComparison.OrdinalIgnoreCase) || Tool.EndsWith("osql.exe", StringComparison.OrdinalIgnoreCase);
            var auth = string.IsNullOrEmpty(user) ? "-E" : "-U \"" + user + "\"" + (osql ? " -P \"" + password + "\"" : "");
            var psi = new ProcessStartInfo(Tool, "-S \"" + instance + "\" " + auth + " -b -h -1 -W -Q \"" + query.Replace("\"", "\\\"") + "\"")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            if (!string.IsNullOrEmpty(user) && !osql) psi.EnvironmentVariables["SQLCMDPASSWORD"] = password ?? "";
            var r = ProcessRunner.Run(psi, Limits.SqlCommand);   // R1: a real time limit, both streams read while it runs
            var o = r.Out + r.Err;
            if (r.TimedOut) throw new TimeoutException("sqlcmd did not finish in " + ProcessRunner.Describe(Limits.SqlCommand) + " and was stopped");
            if (r.Code != 0) throw new InvalidOperationException(o.Trim().Length > 0 ? r.Tail() : "sqlcmd exit code " + r.Code);
            return r.Out;   // the answer only: messages on the error stream must not become database names
        }

        static string Safe(string s) { return Regex.Replace(s, "[^A-Za-z0-9_.-]", "_"); }

        /// <summary>SQL-050: free space of the temporary folder's disk (tests replace it).</summary>
        public static Func<string, long> FreeSpace = (path) => { try { return new DriveInfo(System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(path))).AvailableFreeSpace; } catch (Exception) { return long.MaxValue; } };
        /// <summary>A database from this size gets larger buffers for BACKUP (faster on big databases; 32 x 4 MB of memory).</summary>
        public const long BigDatabase = 20L * 1024 * 1024 * 1024;

        // the size of a database's data files (an upper bound of its backup file); 0 = unknown
        static long DataSize(Func<string, string, string> run, string instance, string db, bool modern)
        {
            try
            {
                var q = modern ? "SET NOCOUNT ON; SELECT CAST(SUM(CAST(size AS bigint)) * 8192 AS varchar(30)) FROM sys.master_files WHERE type = 0 AND database_id = DB_ID('" + db.Replace("'", "''") + "')"
                               : "SET NOCOUNT ON; SELECT CAST(SUM(CAST(size AS bigint)) * 8192 AS varchar(30)) FROM master..sysaltfiles WHERE dbid = DB_ID('" + db.Replace("'", "''") + "') AND (status & 0x40) = 0";
                long v; return long.TryParse(run(instance, q).Trim().Split('\n')[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0;
            }
            catch (Exception) { return 0; }
        }

        static string Gb(long b) { return (b / 1073741824.0).ToString("0.0", CultureInfo.InvariantCulture) + " GB"; }

        public static IEnumerable<SourceItem> Run(BackupSetInfo set, string mode, string temp, string password, Action<string> info, Action<string> warn, List<string> unreachable)
        {
            var items = new List<SourceItem>();
            string user = string.IsNullOrEmpty(set.SqlUser) ? null : set.SqlUser;
            Func<string, string, string> run = (inst, q) => Run(inst, q, user, password);
            var dir = System.IO.Path.Combine(temp, "mssql");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            // SQL-050: every backup file waits in the temporary folder until it is sent — check the room before, not half-way
            long free = FreeSpace(dir);
            foreach (var src in set.Sources)
            {
                var parts = src.Split('\\');
                if (parts.Length < 2 || !parts[0].Equals(Prefix, StringComparison.OrdinalIgnoreCase)) { warn("Not a SQL Server source: " + src); continue; }
                var instance = parts[1];
                List<string> dbs;
                if (parts.Length >= 3) dbs = new List<string> { parts[2] };
                else
                {
                    try
                    {
                        dbs = run(instance, "SET NOCOUNT ON; SELECT name FROM master..sysdatabases WHERE name NOT IN ('tempdb') AND DATABASEPROPERTYEX(name,'Status')='ONLINE'")
                            .Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
                    }
                    catch (Exception e) { warn("Cannot list databases of " + instance + ": " + e.Message); unreachable.Add(src); continue; }
                }
                bool checksum = true;
                try
                {
                    var ver = run(instance, "SET NOCOUNT ON; SELECT CAST(SERVERPROPERTY('ProductVersion') AS varchar(20))").Trim();
                    int major; checksum = int.TryParse(ver.Split('.')[0], out major) && major >= 9;   // CHECKSUM since SQL 2005
                }
                catch (Exception) { }
                foreach (var db in dbs)
                {
                    var logical = Prefix + "\\" + instance + "\\" + db;
                    try
                    {
                        string file, name;
                        if (mode == "LOG")
                        {
                            var stamp = SystemClock.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
                            name = "LOG_" + Safe(db) + "_" + stamp + ".trn";
                            file = System.IO.Path.Combine(dir, name);
                            info("[Start] Backing up \"" + instance + "\\" + db + "\" using \"Transaction log\" to " + file);
                            run(instance, "BACKUP LOG [" + db.Replace("]", "]]") + "] TO DISK='" + file.Replace("'", "''") + "' WITH INIT" + (checksum ? ", CHECKSUM" : ""));
                        }
                        else
                        {
                            // SQL-030: a differential backup on the days that are not the full day — only when the last full backup of
                            // the database is ours (another program's full would make our differential unrestorable without it)
                            var full = "DATABASE_" + Safe(db) + ".bak";
                            var marker = System.IO.Path.Combine(MarkerDir(set, temp), Safe(instance) + "_" + Safe(db) + ".txt");
                            Func<string> lastFull = () => run(instance, "SET NOCOUNT ON; SELECT TOP 1 CONVERT(varchar(33), backup_finish_date, 126) FROM msdb..backupset WHERE database_name='" + db.Replace("'", "''") + "' AND type='D' AND is_copy_only=0 ORDER BY backup_finish_date DESC").Trim();
                            long size = DataSize(run, instance, db, checksum);
                            if (size > 0 && size + 512L * 1024 * 1024 > free)
                            {
                                warn("Not enough free space for the backup of " + instance + "\\" + db + ": it needs about " + Gb(size) + ", the disk of " + temp + " has " + Gb(Math.Max(0, free)) + " free. Choose a temporary folder on a larger disk in the set (\"What to back up\"). The previous backup of this database is kept.");
                                unreachable.Add(logical); continue;
                            }
                            free -= size;
                            var big = checksum && size >= BigDatabase ? ", BUFFERCOUNT = 32, MAXTRANSFERSIZE = 4194304" : "";
                            if (big.Length > 0) info("Large database (" + Gb(size) + "): larger buffers for a faster backup");
                            bool diff = set.SqlFullDay >= 0 && (int)SystemClock.Now.DayOfWeek != set.SqlFullDay && File.Exists(marker) && db != "master";
                            if (diff)
                                try { if (lastFull() != OnlineBackup.Core.Atomic.ReadAllText(marker).Trim()) { warn("Another program made a full backup of " + instance + "\\" + db + " — this run makes a full backup"); diff = false; } }
                                catch (Exception) { diff = false; }
                            if (diff)
                            {
                                name = "DIFF_" + Safe(db) + ".bak";
                                file = System.IO.Path.Combine(dir, name);
                                info("[Start] Backing up \"" + instance + "\\" + db + "\" using \"Database - differential\" to " + file);
                                run(instance, "BACKUP DATABASE [" + db.Replace("]", "]]") + "] TO DISK='" + file.Replace("'", "''") + "' WITH DIFFERENTIAL, INIT" + (checksum ? ", CHECKSUM" : "") + big);
                                unreachable.Add(Sources.KeepOnly + logical + "\\" + full);   // the full backup stays in this restore point (restore = full + this differential) — not a failure
                            }
                            else
                            {
                                name = full;
                                file = System.IO.Path.Combine(dir, name);
                                info("[Start] Backing up \"" + instance + "\\" + db + "\" using \"Database - complete\" to " + file);
                                run(instance, "BACKUP DATABASE [" + db.Replace("]", "]]") + "] TO DISK='" + file.Replace("'", "''") + "' WITH INIT" + (checksum ? ", CHECKSUM" : "") + big);
                                if (set.SqlFullDay >= 0) try { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(marker)); File.WriteAllText(marker, lastFull()); } catch (Exception) { }
                            }
                        }
                        if (!File.Exists(file)) throw new IOException("SQL Server did not create " + file);
                        info("[End] " + instance + "\\" + db);
                        items.Add(new SourceItem { Path = logical + "\\" + name, ReadPath = file, Info = new FileInfo(file) });
                    }
                    catch (Exception e)
                    {
                        warn("Backup of database " + instance + "\\" + db + " failed: " + e.Message);
                        unreachable.Add(logical);   // keep its previous backup, never "deleted"
                    }
                }
            }
            return items;
        }
    }

    /// <summary>
    /// Restore helpers for SQL Server: verify a .bak, and restore full + logs to a NEW database name (never over the
    /// existing one by default), optionally to a point in time (STOPAT).
    /// </summary>
    public static class SqlRestore
    {
        public static string VerifyOnly(string instance, string bak) { return SqlBackup.Run(instance, "RESTORE VERIFYONLY FROM DISK='" + bak.Replace("'", "''") + "'"); }

        /// <param name="diff">SQL-030: the differential backup taken after this full one (DIFF_&lt;db&gt;.bak in the same point), or null.</param>
        public static string Script(string newDb, string bak, IList<string> logs, string dataDir, IList<string[]> files, DateTime? stopAt, string diff = null)
        {
            var sb = new StringBuilder();
            sb.Append("RESTORE DATABASE [").Append(newDb.Replace("]", "]]")).Append("] FROM DISK='").Append(bak.Replace("'", "''")).Append("' WITH ");
            foreach (var f in files)   // logical name, type (D/L)
                sb.Append("MOVE '").Append(f[0].Replace("'", "''")).Append("' TO '").Append(System.IO.Path.Combine(dataDir, newDb + "_" + f[0] + (f[1] == "L" ? ".ldf" : ".mdf")).Replace("'", "''")).Append("', ");
            sb.Append(logs.Count > 0 || diff != null ? "NORECOVERY;" : "RECOVERY;");
            if (diff != null) sb.Append(" RESTORE DATABASE [").Append(newDb.Replace("]", "]]")).Append("] FROM DISK='").Append(diff.Replace("'", "''")).Append("' WITH ").Append(logs.Count > 0 ? "NORECOVERY;" : "RECOVERY;");
            for (int i = 0; i < logs.Count; i++)
            {
                sb.Append(" RESTORE LOG [").Append(newDb.Replace("]", "]]")).Append("] FROM DISK='").Append(logs[i].Replace("'", "''")).Append("' WITH ");
                if (stopAt != null) sb.Append("STOPAT='").Append(stopAt.Value.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)).Append("', ");
                sb.Append(i == logs.Count - 1 ? "RECOVERY;" : "NORECOVERY;");
            }
            return sb.ToString();
        }

        public static void RestoreAs(string instance, string newDb, string bak, IList<string> logs, string dataDir, DateTime? stopAt, string diff = null)
        {
            var list = SqlBackup.Run(instance, "SET NOCOUNT ON; RESTORE FILELISTONLY FROM DISK='" + bak.Replace("'", "''") + "'");
            var files = new List<string[]>();
            foreach (var line in list.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0))
            {
                var cols = Regex.Split(line, "\\s{1,}");
                if (cols.Length >= 3) files.Add(new[] { cols[0], line.IndexOf(" L ", StringComparison.Ordinal) > 0 || line.Contains("\tL\t") ? "L" : "D" });
            }
            SqlBackup.Run(instance, Script(newDb, bak, logs, dataDir, files, stopAt, diff));
        }
    }

    /// <summary>
    /// System State with Windows' own tool: wbadmin (2008 and later) to a volume, ntbackup (2003 / XP) to a .bkf file.
    /// The result is sent like files (with delta). Restore: download, then the Windows tool (wbadmin start systemstaterecovery).
    /// </summary>
    public static class SystemState
    {
        public static IEnumerable<SourceItem> Run(BackupSetInfo set, string temp, Action<string> info, Action<string> warn, List<string> unreachable)
        {
            var items = new List<SourceItem>();
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) { warn("System State backup runs on Windows only"); unreachable.Add("System State"); return items; }
            try
            {
                if (Environment.OSVersion.Version.Major >= 6)
                {
                    var target = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(temp)).TrimEnd('\\');
                    info("[System State Backup] wbadmin to " + target);
                    AllowTargetOnSystemVolume(RegGet, RegSet, info);
                    Exec("wbadmin", "start systemstatebackup -backupTarget:" + target + " -quiet");
                    var root = System.IO.Path.Combine(target + "\\", "WindowsImageBackup");
                    foreach (var f in new DirectoryInfo(root).GetFiles("*", SearchOption.AllDirectories))
                        items.Add(new SourceItem { Path = "System State\\" + f.FullName.Substring(root.Length + 1), ReadPath = f.FullName, Info = f });
                }
                else
                {
                    var bkf = System.IO.Path.Combine(temp, "systemstate.bkf");
                    info("[System State Backup] ntbackup to " + bkf);
                    Exec("ntbackup", "backup systemstate /F \"" + bkf + "\"");
                    items.Add(new SourceItem { Path = "System State\\systemstate.bkf", ReadPath = bkf, Info = new FileInfo(bkf) });
                }
                info("[System State Backup] Found (" + items.Count + ") files.");
            }
            catch (Exception e) { warn("System State backup failed: " + e.Message + SpaceNote(temp)); unreachable.Add("System State"); }
            return items;
        }

        /// <summary>Bug 97 (Windows run 20, W17): wbadmin filled C: (10.7 GB free before, 1.0 GB after) and the customer saw only
        /// "wbadmin exit code -4". When the target volume is nearly full after a failure, the log says so in plain words.</summary>
        public static string SpaceNote(string temp)
        {
            try { return SpaceNote(System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(temp)), new DriveInfo(System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(temp))).AvailableFreeSpace); }
            catch (Exception) { return ""; }
        }
        public static string SpaceNote(string volume, long freeBytes)
        {
            const long low = 2L * 1024 * 1024 * 1024;
            if (freeBytes >= low) return "";
            return " - the disk " + volume.TrimEnd('\\') + " where Windows writes the System State has only " + (freeBytes / (1024.0 * 1024 * 1024)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) +
                " GB free: it probably ran out of space (a System State backup needs several GB; free space on that disk and run the backup again)";
        }

        static void Exec(string exe, string args)
        {
            ProcessRunner.Check(new ProcessStartInfo(exe, args), Limits.SystemState);   // R1: stopped at the limit, an error if it fails
        }

        /// <summary>Bug 95 (Windows run 17, W17): wbadmin refuses a System State backup to a volume that is part of the system
        /// state ("You cannot use a volume that is included in the backup as a storage location") unless Windows' documented
        /// setting AllowSSBToAnyVolume=1 is present - so on a server with one disk (C:) every System State backup failed. The
        /// setting is made once, and said in the log.</summary>
        public const string WbengineKey = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\wbengine\SystemStateBackup";
        public static void AllowTargetOnSystemVolume(Func<string, object> get, Action<string, object> set, Action<string> info)
        {
            object v = null; try { v = get("AllowSSBToAnyVolume"); } catch (Exception) { }
            if (v is int && (int)v == 1) return;
            set("AllowSSBToAnyVolume", 1);
            info("[System State Backup] Windows setting AllowSSBToAnyVolume=1 made (wbadmin may then keep the System State on the system disk)");
        }
        static object RegGet(string name) { return Microsoft.Win32.Registry.GetValue(WbengineKey, name, null); }
        static void RegSet(string name, object value) { Microsoft.Win32.Registry.SetValue(WbengineKey, name, value, Microsoft.Win32.RegistryValueKind.DWord); }
    }
}
