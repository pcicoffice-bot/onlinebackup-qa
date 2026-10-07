using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>DB / HV: MySQL, PostgreSQL and Hyper-V through stand-ins for their own tools.</summary>
    public class DbHyperVTests
    {
        static bool Restic { get { var r = Environment.GetEnvironmentVariable("OB_RESTIC"); return !string.IsNullOrEmpty(r) && File.Exists(r); } }
        static string Script(string dir, string name, string body)
        {
            var p = Path.Combine(dir, name); File.WriteAllText(p, "#!/bin/sh\n" + body);
            Process.Start("chmod", "+x \"" + p + "\"").WaitForExit(); return p;
        }

        static void WithTools(Env env, Action body)
        {
            var bin = env.Dir("bin");
            Environment.SetEnvironmentVariable("OB_MYSQL", Script(bin, "mysql",
                "[ \"$MYSQL_PWD\" = 'Db-Pass-1' ] || { echo 'Access denied' >&2; exit 3; }\n" +
                "case \"$*\" in *'SHOW DATABASES'*) printf 'information_schema\\nshop\\ncrm\\nsys\\n';; *) echo \"$*\" >> '" + env.Root + "/mysql-load.log';; esac\n"));
            Environment.SetEnvironmentVariable("OB_MYSQLDUMP", Script(bin, "mysqldump",
                "[ \"$MYSQL_PWD\" = 'Db-Pass-1' ] || exit 3\n" +
                "for a; do db=$a; done\n[ \"$db\" = 'broken' ] && { echo 'Got error: 1049' >&2; exit 2; }\n" +
                "echo \"-- MySQL dump of $db\"; echo \"CREATE DATABASE \\`$db\\`;\"; echo \"USE \\`$db\\`;\"; head -c 400000 /dev/zero | tr '\\\\0' 'r'; echo; cat '" + env.Root + "/rows' 2>/dev/null; echo \"-- end $db\"\n"));
            Environment.SetEnvironmentVariable("OB_PSQL", Script(bin, "psql", "[ \"$PGPASSWORD\" = 'Db-Pass-1' ] || exit 3\ncase \"$*\" in *pg_database*) printf 'app\\nreports\\n';; esac\n"));
            Environment.SetEnvironmentVariable("OB_PGDUMP", Script(bin, "pg_dump", "for a; do db=$a; done\necho \"-- PostgreSQL database dump $db\"; echo \"CREATE TABLE t(x int);\"\n"));
            Environment.SetEnvironmentVariable("OB_PGDUMPALL", Script(bin, "pg_dumpall", "echo '-- roles'; echo 'CREATE ROLE app;'\n"));
            try { body(); }
            finally { foreach (var v in new[] { "OB_MYSQL", "OB_MYSQLDUMP", "OB_PSQL", "OB_PGDUMP", "OB_PGDUMPALL" }) Environment.SetEnvironmentVariable(v, null); }
        }

        [Fact]
        public void MySql_EveryDatabaseDumped_PasswordNeverInTheLog_DumpsTravelAsDeltas_LoadIntoANewDatabase()
        {
            if (Environment.OSVersion.Platform != PlatformID.Unix) throw NotTested.Because("needs Linux (the stand-in tools are shell scripts)");
            using (var env = new Env())
                WithTools(env, () =>
                {
                    env.CreateUser("mysql1", "Customer-Pass-1", 5);
                    var app = env.Agent("mysql1", "Customer-Pass-1");
                    var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "DB", Type = "MYSQL", Vss = false, SqlUser = "backup", DbHost = "127.0.0.1" });
                    app.Home.SaveSecret(set.Id + "-sql", "Db-Pass-1");
                    var r1 = app.Backup(set.Id);
                    Assert.True(r1.Result == "BS_STOP_SUCCESS", string.Join("\n", r1.LogLines));
                    Assert.Equal(2, r1.New);                                                    // shop + crm (system databases skipped)
                    Assert.DoesNotContain(r1.LogLines, l => l.Contains("Db-Pass-1"));

                    // own engine: the next dumps travel as deltas
                    var sets = app.Sets(); var s = sets.First(x => x.Id == set.Id);
                    Thread.Sleep(1100);
                    File.WriteAllText(Path.Combine(env.Root, "rows"), "INSERT INTO t VALUES (2);");
                    var r2 = app.Backup(set.Id);
                    Assert.Equal(2, r2.Updated); Assert.True(r2.BytesSent < 200000, "sent " + r2.BytesSent);   // dumps travel as deltas
                    var restore = app.RestoreFor(app.Interactive("Customer-Pass-1", null), set.Id);
                    Assert.Contains(@"MySQL\shop.sql", restore.Files(null).Select(f => f.Key));
                    var t = env.Dir("r"); restore.Run(null, t, null, false);
                    Assert.Contains("INSERT INTO t VALUES (2);", File.ReadAllText(Directory.GetFiles(t, "shop.sql", SearchOption.AllDirectories).Single()));
                    DbDump.Load(s, "Db-Pass-1", Directory.GetFiles(t, "shop.sql", SearchOption.AllDirectories).Single(), "shop_restored");
                    Assert.Contains("shop_restored", File.ReadAllText(Path.Combine(env.Root, "mysql-load.log")));
                });
        }

        [Fact]
        public void MySqlAndPostgres_OnRestic_AndAFailingDatabaseIsReported()
        {
            if (!Restic) throw NotTested.Because("OB_RESTIC (the restic program) is not set");
            if (Environment.OSVersion.Platform != PlatformID.Unix) throw NotTested.Because("needs Linux (the stand-in tools are shell scripts)");   // as the MySQL test above
            using (var env = new Env())
                WithTools(env, () =>
                {
                    env.CreateUser("pgsql1", "Customer-Pass-1", 5);
                    var app = env.Agent("pgsql1", "Customer-Pass-1");
                    var my = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "My", Type = "MYSQL", Engine = "RESTIC", Vss = false, SqlUser = "backup", Sources = { "shop", "broken" } });
                    var pg = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "Pg", Type = "POSTGRESQL", Engine = "RESTIC", Vss = false, SqlUser = "postgres" });
                    app.Home.SaveSecret(my.Id + "-sql", "Db-Pass-1"); app.Home.SaveSecret(pg.Id + "-sql", "Db-Pass-1");
                    var r = app.Backup(my.Id);
                    Assert.Equal("BS_STOP_SUCCESS_WITH_WARNING", r.Result);
                    Assert.Contains(r.LogLines, l => l.Contains("broken not dumped"));
                    var p = app.Backup(pg.Id);
                    Assert.True(p.Result == "BS_STOP_SUCCESS", string.Join("\n", p.LogLines));
                    var files = app.Restic(app.Sets().First(x => x.Id == pg.Id)).Ls(null).Where(f => f["type"] == "file").Select(f => Path.GetFileName(f["path"])).OrderBy(x => x).ToList();
                    Assert.Equal(new[] { "_globals_roles.sql", "app.sql", "reports.sql" }, files);
                });
        }

        [Fact]
        public void HyperV_VmsExportedOnline_OnlyChangedDiskBlocksSent_RestoredAndImported()
        {
            if (!Restic) throw NotTested.Because("OB_RESTIC (the restic program) is not set");
            if (Environment.OSVersion.Platform != PlatformID.Unix) throw NotTested.Because("needs Linux (the stand-in tools are shell scripts)");   // as the MySQL test above
            using (var env = new Env())
            {
                var bin = env.Dir("bin");
                var disk = Path.Combine(env.Root, "dc01.vhdx"); var b = new byte[24 * 1024 * 1024]; new Random(4).NextBytes(b); File.WriteAllBytes(disk, b);
                var ps = Script(bin, "powershell",
                    "C=\"$*\"\n" +
                    "case \"$C\" in *Get-VM*) printf 'DC01\\nSQL01\\n'; exit 0;; esac\n" +
                    "case \"$C\" in *Import-VM*) echo 'DC01'; exit 0;; esac\n" +
                    "N=$(echo \"$C\" | sed -n \"s/.*-Name '\\([^']*\\)'.*/\\1/p\"); P=$(echo \"$C\" | sed -n \"s/.*-Path '\\([^']*\\)'.*/\\1/p\")\n" +
                    "[ \"$N\" = 'SQL01' ] && [ -f '" + env.Root + "/sql-fails' ] && { echo 'The operation failed' >&2; exit 1; }\n" +
                    "mkdir -p \"$P/$N/Virtual Hard Disks\" \"$P/$N/Virtual Machines\"\n" +
                    "cp '" + disk + "' \"$P/$N/Virtual Hard Disks/$N.vhdx\"; echo cfg > \"$P/$N/Virtual Machines/ABC.vmcx\"\n");
                Environment.SetEnvironmentVariable("OB_POWERSHELL", ps);
                try
                {
                    env.CreateUser("hyperv1", "Customer-Pass-1", 5);
                    var app = env.Agent("hyperv1", "Customer-Pass-1");
                    var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "VMs", Type = "HYPERV", Engine = "RESTIC", Vss = false });
                    var r1 = app.Backup(set.Id);
                    Assert.True(r1.Result == "BS_STOP_SUCCESS", string.Join("\n", r1.LogLines));
                    Assert.Equal(4, r1.New);                                                     // two VMs: disk + configuration each

                    using (var f = new FileStream(disk, FileMode.Open)) { f.Position = 10 * 1024 * 1024; var x = new byte[256 * 1024]; new Random(5).NextBytes(x); f.Write(x, 0, x.Length); }
                    File.WriteAllText(Path.Combine(env.Root, "sql-fails"), "1");
                    Thread.Sleep(1100);
                    var r2 = app.Backup(set.Id);
                    Assert.Equal("BS_STOP_SUCCESS_WITH_WARNING", r2.Result);                      // SQL01 failed: its last export stays
                    Assert.True(r2.BytesSent < 20L * 1024 * 1024, "sent " + r2.BytesSent);           // DC01 changed: only blocks around the change (two VMs share them too)
                    var rs = app.Restic(app.Sets().First(x => x.Id == set.Id));
                    var paths = rs.Ls(null).Select(f => f["path"]).ToList();
                    Assert.Contains(paths, p => p.EndsWith("SQL01/Virtual Hard Disks/SQL01.vhdx"));
                    var target = env.Dir("restore");
                    rs.RestoreMany(null, target, paths.Where(p => p.Contains("/DC01/")).ToList(), new List<string>());
                    var folder = Directory.GetDirectories(target, "DC01", SearchOption.AllDirectories).Single();
                    Assert.True(File.ReadAllBytes(disk).SequenceEqual(File.ReadAllBytes(Path.Combine(folder, "Virtual Hard Disks", "DC01.vhdx"))));
                    Assert.Equal("DC01", HyperV.Import(folder, env.Dir("vms")));
                }
                finally { Environment.SetEnvironmentVariable("OB_POWERSHELL", null); }
            }
        }
    }
}
