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
    /// DB-010..040: MySQL / MariaDB and PostgreSQL with their own dump tools, so every backup is a standard .sql that
    /// restores even without this product (as SQL Server's .bak). Each run dumps every chosen database (all user
    /// databases when none is chosen) into a stable folder (&lt;agent&gt;\sets\&lt;set&gt;\dump), which the set backs up
    /// (restic: only the changed parts of the dumps travel). MySQL: --single-transaction (InnoDB without locking),
    /// routines, triggers, events. PostgreSQL: plain SQL per database + the roles (pg_dumpall --globals-only).
    /// Connection: DB_HOST / DB_PORT / ADMIN_USERNAME of the set; the password stays on this computer (sql-password),
    /// given to the tools by MYSQL_PWD / PGPASSWORD — never on a command line or in a log.
    /// Tools: on the PATH, in the usual install folders, or OB_MYSQL / OB_MYSQLDUMP / OB_PSQL / OB_PGDUMP / OB_PGDUMPALL.
    /// </summary>
    public static class DbDump
    {
        public const string MySql = "MYSQL", Postgres = "POSTGRESQL";
        static readonly string[] MySqlSystem = { "information_schema", "performance_schema", "sys" };

        public static string Folder(AgentHome home, BackupSetInfo set) { return Path.Combine(home.SetDir(set.Id), "dump"); }

        static string Tool(string env, string exe, string[] dirs)
        {
            var e = Environment.GetEnvironmentVariable(env);
            if (!string.IsNullOrEmpty(e)) return e;
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? "" })
                    foreach (var d in dirs)
                    {
                        var base_ = Path.Combine(root, d);
                        if (!Directory.Exists(base_)) continue;
                        var hit = Directory.GetDirectories(base_).OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase).Select(x => Path.Combine(x, "bin", exe + ".exe")).FirstOrDefault(File.Exists);
                        if (hit != null) return hit;
                    }
            return exe;   // on the PATH
        }

        static Dictionary<string, string> Env(BackupSetInfo set, string password)
        {
            var env = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(password)) { env["MYSQL_PWD"] = password; env["PGPASSWORD"] = password; }
            return env;
        }

        static List<string> Conn(BackupSetInfo set, bool mysql)
        {
            var a = new List<string>();
            if (!string.IsNullOrEmpty(set.DbHost)) { a.Add(mysql ? "-h" : "--host"); a.Add(set.DbHost); }
            if (set.DbPort > 0) { a.Add(mysql ? "-P" : "--port"); a.Add(set.DbPort.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
            if (!string.IsNullOrEmpty(set.SqlUser)) { a.Add(mysql ? "-u" : "--username"); a.Add(set.SqlUser); }
            if (!mysql) a.Add("--no-password");
            return a;
        }

        /// <summary>Runs a tool; its standard output goes to a file (binary-safe) or comes back as text.</summary>
        public static string Run(string exe, IEnumerable<string> args, Dictionary<string, string> env, string toFile = null)
        {
            var psi = new ProcessStartInfo(exe, string.Join(" ", args.Select(Quote).ToArray()));
            foreach (var kv in env) psi.EnvironmentVariables[kv.Key] = kv.Value;
            // R1 (GPT audit 6): a dump that hangs (a locked table, a lost database connection) stops at the limit
            ProcessRunner.Result r;
            if (toFile != null) using (var f = File.Create(toFile)) r = ProcessRunner.Run(psi, Limits.Dump, stdoutTo: f);
            else r = ProcessRunner.Run(psi, Limits.Dump);
            if (r.TimedOut) throw new TimeoutException(Path.GetFileName(exe) + " did not finish in " + ProcessRunner.Describe(Limits.Dump) + " and was stopped");
            if (r.Code != 0) { var e2 = r.Err.Trim(); throw new InvalidOperationException(Path.GetFileName(exe) + " exit " + r.Code + ": " + (e2.Length > 500 ? e2.Substring(e2.Length - 500) : e2)); }
            return toFile != null ? null : r.Out;
        }

        static string Quote(string a) { return a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0 ? a : "\"" + a.Replace("\"", "\\\"") + "\""; }

        static string FileName(string db) { return M365Sync.Safe(db, 120) + ".sql"; }

        /// <summary>
        /// Dumps the set's databases into its dump folder: &lt;dump&gt;\&lt;MySQL|PostgreSQL&gt;\&lt;db&gt;.sql. A database that fails
        /// is reported and its previous dump is left out of this run (never an empty or partial file).
        /// </summary>
        public static List<string> Dump(AgentHome home, BackupSetInfo set, string password, Action<string> info, Action<string> warn, List<string> unreachable)
        {
            bool mysql = set.Type == MySql;
            var kind = mysql ? "MySQL" : "PostgreSQL";
            var dir = Path.Combine(Folder(home, set), kind);
            Directory.CreateDirectory(dir);
            var env = Env(set, password);
            var conn = Conn(set, mysql);
            List<string> dbs = set.Sources.Where(s => !string.IsNullOrWhiteSpace(s) && s != "*").Select(s => s.Trim()).ToList();
            if (dbs.Count == 0)
            {
                var list = mysql
                    ? Run(Tool("OB_MYSQL", "mysql", new[] { "MySQL", "MariaDB 10.11", "MariaDB 11.4", "MariaDB" }), conn.Concat(new[] { "-N", "-B", "-e", "SHOW DATABASES" }), env)
                    : Run(Tool("OB_PSQL", "psql", new[] { "PostgreSQL" }), conn.Concat(new[] { "-At", "-d", "postgres", "-c", "SELECT datname FROM pg_database WHERE NOT datistemplate ORDER BY 1" }), env);
                dbs = list.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0 && !(mysql && MySqlSystem.Contains(x, StringComparer.OrdinalIgnoreCase))).ToList();
            }
            info("[" + kind + "] databases: " + string.Join(", ", dbs.ToArray()));
            var written = new List<string>();
            foreach (var db in dbs)
            {
                var file = Path.Combine(dir, FileName(db)); var tmp = file + ".part";
                try
                {
                    if (mysql)
                        Run(Tool("OB_MYSQLDUMP", "mysqldump", new[] { "MySQL", "MariaDB 10.11", "MariaDB 11.4", "MariaDB" }),
                            conn.Concat(new[] { "--single-transaction", "--quick", "--routines", "--triggers", "--events", "--hex-blob", "--default-character-set=utf8mb4", "--databases", db }), env, tmp);
                    else
                        Run(Tool("OB_PGDUMP", "pg_dump", new[] { "PostgreSQL" }), conn.Concat(new[] { "--format=plain", "--create", "--dbname", db }), env, tmp);
                    if (new FileInfo(tmp).Length == 0) throw new InvalidOperationException("empty dump");
                    if (File.Exists(file)) File.Delete(file);
                    File.Move(tmp, file);
                    written.Add(file);
                    info("[" + kind + "] " + db + ": " + new FileInfo(file).Length + " bytes");
                }
                catch (Exception e)
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { }
                    warn("[" + kind + "] " + db + " not dumped (its last dump is kept): " + e.Message);
                    unreachable.Add(kind + "\\" + FileName(db));   // as the backed-up name: the last dump is not treated as deleted
                }
            }
            if (!mysql)
                try
                {
                    var g = Path.Combine(dir, "_globals_roles.sql");
                    Run(Tool("OB_PGDUMPALL", "pg_dumpall", new[] { "PostgreSQL" }), conn.Concat(new[] { "--globals-only" }), env, g + ".part");
                    if (File.Exists(g)) File.Delete(g); File.Move(g + ".part", g); written.Add(g);
                }
                catch (Exception e)
                {
                    // bug 36 (Agent B): a failed roles dump was only a warning and the cleaning below deleted the last one —
                    // the latest point lost the roles. Like a database: an error, and the last dump is kept
                    try { var p = Path.Combine(dir, "_globals_roles.sql.part"); if (File.Exists(p)) File.Delete(p); } catch (IOException) { }
                    warn("[PostgreSQL] roles not dumped (the last dump is kept): " + e.Message);
                    unreachable.Add(kind + "\\_globals_roles.sql");
                }
            // dumps of databases no longer chosen leave the folder (they stay in earlier restore points)
            foreach (var f in Directory.GetFiles(dir, "*.sql")) if (!written.Contains(f) && !unreachable.Contains(kind + "\\" + Path.GetFileName(f))) File.Delete(f);
            return written;
        }

        /// <summary>DB-040: loads a restored dump into a database (a new name keeps the original untouched).</summary>
        public static void Load(BackupSetInfo set, string password, string sqlFile, string intoDb)
        {
            bool mysql = set.Type == MySql;
            var env = Env(set, password);
            var conn = Conn(set, mysql);
            if (mysql)
            {
                Run(Tool("OB_MYSQL", "mysql", new[] { "MySQL", "MariaDB" }), conn.Concat(new[] { "-e", "CREATE DATABASE IF NOT EXISTS `" + intoDb.Replace("`", "") + "`" }), env);
                // the dump names its database (--databases): load it into the chosen one instead
                var text = File.ReadAllText(sqlFile, Encoding.UTF8);
                text = System.Text.RegularExpressions.Regex.Replace(text, @"^(CREATE DATABASE|USE) [^\n]*\n", "", System.Text.RegularExpressions.RegexOptions.Multiline);
                var tmp = sqlFile + ".load.sql"; File.WriteAllText(tmp, text, new UTF8Encoding(false));
                try { Run(Tool("OB_MYSQL", "mysql", new[] { "MySQL", "MariaDB" }), conn.Concat(new[] { intoDb, "-e", "source " + tmp.Replace('\\', '/') }), env); }
                finally { File.Delete(tmp); }
            }
            else
            {
                Run(Tool("OB_PSQL", "psql", new[] { "PostgreSQL" }), conn.Concat(new[] { "-d", "postgres", "-c", "CREATE DATABASE \"" + intoDb.Replace("\"", "") + "\"" }), env);
                var text = File.ReadAllText(sqlFile, Encoding.UTF8);
                text = System.Text.RegularExpressions.Regex.Replace(text, @"^(CREATE DATABASE|\\connect|ALTER DATABASE) [^\n]*\n", "", System.Text.RegularExpressions.RegexOptions.Multiline);
                var tmp = sqlFile + ".load.sql"; File.WriteAllText(tmp, text, new UTF8Encoding(false));
                try { Run(Tool("OB_PSQL", "psql", new[] { "PostgreSQL" }), conn.Concat(new[] { "-d", intoDb, "-v", "ON_ERROR_STOP=1", "-f", tmp }), env); }
                finally { File.Delete(tmp); }
            }
        }
    }
}
