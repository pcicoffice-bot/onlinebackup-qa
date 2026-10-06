using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using OnlineBackup.Core;

namespace OnlineBackup.Agent
{
    /// <summary>
    /// ORA-010..040: Oracle Database with Oracle's own RMAN, so every backup is a standard RMAN backup that restores even
    /// without this product. Online (the database stays open; ARCHIVELOG mode required): the database, the archived logs,
    /// the control file and the spfile, one datafile per backup piece and no RMAN compression — so the pieces of unchanged
    /// datafiles stay alike and only changed parts travel. Each instance is backed up into &lt;staging&gt;\&lt;SID&gt;.new and
    /// replaces &lt;SID&gt; only after RMAN finished without error (a failed run keeps the previous backup).
    /// Sources: ORACLE_SID values (one per instance). Connection: operating-system authentication ("connect target /",
    /// the agent's account in ORA_DBA), or a user (ADMIN_USERNAME, e.g. "sys") whose password stays on this computer
    /// (sql-password) and reaches RMAN through its standard input — never a command line, file or log.
    /// RMAN leaves the archived logs in place: their removal stays with the DBA's own policy.
    /// OB_RMAN replaces rman in tests.
    /// </summary>
    public static class Oracle
    {
        public const string Type = "ORACLE";

        static string Rman
        {
            get
            {
                var e = Environment.GetEnvironmentVariable("OB_RMAN");
                if (!string.IsNullOrEmpty(e)) return e;
                var home = Environment.GetEnvironmentVariable("ORACLE_HOME");
                var exe = Environment.OSVersion.Platform == PlatformID.Win32NT ? "rman.exe" : "rman";
                if (!string.IsNullOrEmpty(home) && File.Exists(Path.Combine(home, "bin", exe))) return Path.Combine(home, "bin", exe);
                return exe;   // on the PATH
            }
        }

        public static string Staging(AgentHome home, BackupSetInfo set)
        {
            return string.IsNullOrEmpty(set.WorkingDir) ? Path.Combine(home.SetDir(set.Id), "oracle") : Path.Combine(set.WorkingDir, "oracle-" + set.Id);
        }

        static string Lit(string s) { return "'" + (s ?? "").Replace("'", "''") + "'"; }

        /// <summary>The RMAN script for one instance (the connect line is added in front, never written anywhere).</summary>
        public static string Script(string dir)
        {
            var f = Path.Combine(dir, "%d_%T_%U.bkp");
            return "RUN {\n"
                + "  BACKUP AS BACKUPSET FILESPERSET 1 DATABASE FORMAT " + Lit(f) + " TAG 'ONLINEBACKUP'\n"
                + "    PLUS ARCHIVELOG FORMAT " + Lit(Path.Combine(dir, "arch_%d_%T_%U.bkp")) + " TAG 'ONLINEBACKUP';\n"
                + "  BACKUP CURRENT CONTROLFILE FORMAT " + Lit(Path.Combine(dir, "ctl_%d_%T_%U.bkp")) + " TAG 'ONLINEBACKUP';\n"
                + "  BACKUP SPFILE FORMAT " + Lit(Path.Combine(dir, "spfile_%d_%T_%U.bkp")) + " TAG 'ONLINEBACKUP';\n"
                + "}\nEXIT;\n";
        }

        static string Connect(BackupSetInfo set, string password)
        {
            if (string.IsNullOrEmpty(set.SqlUser)) return "CONNECT TARGET /;\n";
            var who = set.SqlUser.Replace("\"", "");
            var target = string.IsNullOrEmpty(set.DbHost) ? "" : "@" + set.DbHost + (set.DbPort > 0 ? ":" + set.DbPort : "");
            return "CONNECT TARGET \"" + who + "/\\\"" + (password ?? "").Replace("\"", "") + "\\\"" + target + (who.Equals("sys", StringComparison.OrdinalIgnoreCase) ? " AS SYSDBA" : "") + "\";\n";
        }

        /// <summary>Runs RMAN with the script on its standard input; its output (without the connect line) for the log.</summary>
        static string Run(string sid, string connect, string script)
        {
            var psi = new ProcessStartInfo(Rman, "") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            if (!string.IsNullOrEmpty(sid)) psi.EnvironmentVariables["ORACLE_SID"] = sid;
            psi.EnvironmentVariables["NLS_DATE_FORMAT"] = "YYYY-MM-DD HH24:MI:SS";
            {
                var rr = ProcessRunner.Run(psi, Limits.Rman, stdin: connect + script);   // R1: a stuck RMAN stops at the limit
                if (rr.TimedOut) throw new TimeoutException("RMAN did not finish in " + ProcessRunner.Describe(Limits.Rman) + " and was stopped");
                var p = new { ExitCode = rr.Code };
                string e2 = rr.Err, o = rr.Out;
                var all = o + e2;
                var errors = all.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("RMAN-", StringComparison.Ordinal) || l.StartsWith("ORA-", StringComparison.Ordinal)).ToList();
                if (p.ExitCode != 0 || errors.Count > 0)
                {
                    var why = string.Join(" | ", errors.Take(6).ToArray());
                    if (all.Contains("ORA-19602")) why = "the database is in NOARCHIVELOG mode: an online backup needs ARCHIVELOG mode (" + why + ")";
                    throw new InvalidOperationException("RMAN exit " + p.ExitCode + (why.Length > 0 ? ": " + why : ""));
                }
                return o;
            }
        }

        /// <summary>ORA-020: backs up every chosen instance; returns the staging folder. An instance that fails keeps its last backup.</summary>
        public static string Backup(AgentHome home, BackupSetInfo set, string password, Action<string> info, Action<string> warn)
        {
            var staging = Staging(home, set);
            Directory.CreateDirectory(staging);
            var sids = set.Sources.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
            if (sids.Count == 0) { var env = Environment.GetEnvironmentVariable("ORACLE_SID"); if (!string.IsNullOrEmpty(env)) sids.Add(env); }
            if (sids.Count == 0) throw new AgentException(0, "ORACLE", "No Oracle instance (ORACLE_SID) chosen");
            var connect = Connect(set, password);
            int ok = 0;
            foreach (var sid in sids)
            {
                var name = M365Sync.Safe(sid, 60);
                var final = Path.Combine(staging, name); var tmp = Path.Combine(staging, name + ".new");
                try
                {
                    if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
                    Directory.CreateDirectory(tmp);
                    var t0 = SystemClock.UtcNow;
                    Run(sid, connect, Script(tmp));
                    var pieces = Directory.GetFiles(tmp, "*.bkp");
                    if (pieces.Length == 0 || pieces.All(f => new FileInfo(f).Length == 0)) throw new InvalidOperationException("RMAN wrote no backup piece");
                    long bytes = pieces.Sum(f => new FileInfo(f).Length);
                    File.WriteAllText(Path.Combine(tmp, "RESTORE-README.txt"), Readme(sid), Encoding.UTF8);
                    if (Directory.Exists(final)) Directory.Delete(final, true);
                    Directory.Move(tmp, final);
                    ok++;
                    info("[Oracle] " + sid + ": " + pieces.Length + " pieces, " + bytes + " bytes in " + (int)(SystemClock.UtcNow - t0).TotalSeconds + "s");
                }
                catch (Exception e)
                {
                    try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch (IOException) { }
                    warn("[Oracle] " + sid + " not backed up (its last backup is kept): " + e.Message);
                }
            }
            if (ok == 0) throw new AgentException(0, "ORACLE", "No Oracle instance was backed up");
            return staging;
        }

        /// <summary>The way back, kept with every backup (the restored folder is where RMAN finds the pieces).</summary>
        public static string Readme(string sid)
        {
            return "Oracle " + sid + " - RMAN backup (restore with Oracle's RMAN)\r\n\r\n"
                + "1. Restore this folder from the backup to a disk of the database server, e.g. D:\\OraRestore\\" + sid + "\r\n"
                + "2. set ORACLE_SID=" + sid + "   then   rman target /\r\n"
                + "   STARTUP NOMOUNT;   (a lost instance: STARTUP FORCE NOMOUNT and first RESTORE SPFILE FROM 'D:\\OraRestore\\" + sid + "\\spfile_....bkp'; then STARTUP FORCE NOMOUNT)\r\n"
                + "   RESTORE CONTROLFILE FROM 'D:\\OraRestore\\" + sid + "\\ctl_....bkp';\r\n"
                + "   ALTER DATABASE MOUNT;\r\n"
                + "   CATALOG START WITH 'D:\\OraRestore\\" + sid + "\\' NOPROMPT;\r\n"
                + "   RESTORE DATABASE;\r\n"
                + "   RECOVER DATABASE;            (or to a moment: RUN { SET UNTIL TIME \"TO_DATE('2026-01-31 10:00','YYYY-MM-DD HH24:MI')\"; RESTORE DATABASE; RECOVER DATABASE; })\r\n"
                + "   ALTER DATABASE OPEN RESETLOGS;\r\n\r\n"
                + "A single table: restore to a test server as above, then export it with Data Pump (expdp) and import it (impdp).\r\n";
        }
    }
}
