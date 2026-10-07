using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// QA agent B (code audit) — PostgreSQL dumps (DB-02), native engine.
    /// DbDump.Dump: when pg_dumpall --globals-only (the roles every database dump needs to load) fails, it only warns,
    /// does not add the roles dump to the "unreachable" list, and its clean-up loop then DELETES the previous
    /// _globals_roles.sql from the dump folder. BackupRun sees the file gone and moves it out of the latest restore point
    /// ("del"). The run ends "completed with warnings" — the same class as bug 2/3 (something that should have been
    /// backed up and was not is only a warning), plus the last good copy is removed from the current point.
    /// </summary>
    public class AuditB_DbDumpTests
    {
        static string Script(string dir, string name, string body)
        {
            var p = Path.Combine(dir, name); File.WriteAllText(p, "#!/bin/sh\n" + body);
            Process.Start("chmod", "+x \"" + p + "\"").WaitForExit(); return p;
        }

        [Fact]
        public void PostgresRolesDumpFails_ItIsAnError_AndTheLastRolesDumpStaysInTheLatestPoint()
        {
            if (Environment.OSVersion.Platform != PlatformID.Unix) throw NotTested.Because("needs Linux (the stand-in tools are shell scripts)");
            using (var env = new Env())
            {
                var bin = env.Dir("bin");
                var flag = Path.Combine(env.Root, "roles-fail");
                Environment.SetEnvironmentVariable("OB_PSQL", Script(bin, "psql", "case \"$*\" in *pg_database*) printf 'app\\n';; esac\n"));
                Environment.SetEnvironmentVariable("OB_PGDUMP", Script(bin, "pg_dump", "for a; do db=$a; done\necho \"-- PostgreSQL database dump $db\"; echo \"CREATE TABLE t(x int);\"\n"));
                Environment.SetEnvironmentVariable("OB_PGDUMPALL", Script(bin, "pg_dumpall", "[ -f '" + flag + "' ] && { echo 'connection refused' >&2; exit 2; }\necho '-- roles'; echo 'CREATE ROLE app;'\n"));
                try
                {
                    env.CreateUser("auditb4", "Customer-Pass-1", 5);
                    var app = env.Agent("auditb4", "Customer-Pass-1");
                    var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "Pg", Type = "POSTGRESQL", Vss = false, SqlUser = "postgres" });
                    var r1 = app.Backup(set.Id);
                    Assert.True(r1.Result == "BS_STOP_SUCCESS", string.Join("\n", r1.LogLines));
                    var rs1 = app.RestoreFor(app.Interactive("Customer-Pass-1", null), set.Id);
                    Assert.Contains(@"PostgreSQL\_globals_roles.sql", rs1.Files(null).Select(f => f.Key));   // control: run 1 has the roles

                    File.WriteAllText(flag, "1");
                    System.Threading.Thread.Sleep(1100);
                    var r2 = app.Backup(set.Id);
                    var latest = app.RestoreFor(app.Interactive("Customer-Pass-1", null), set.Id).Files(null).Select(f => f.Key).ToList();
                    bool rolesKept = latest.Contains(@"PostgreSQL\_globals_roles.sql");
                    bool isError = r2.Result != "BS_STOP_SUCCESS" && r2.Result != "BS_STOP_SUCCESS_WITH_WARNING";
                    Assert.True(rolesKept && isError,
                        "run 2 (pg_dumpall failed) ended " + r2.Result + ", deleted=" + r2.Deleted + "; the latest point " + (rolesKept ? "still has" : "LOST") + " the roles dump");
                }
                finally { foreach (var v in new[] { "OB_PSQL", "OB_PGDUMP", "OB_PGDUMPALL" }) Environment.SetEnvironmentVariable(v, null); }
            }
        }
    }
}
