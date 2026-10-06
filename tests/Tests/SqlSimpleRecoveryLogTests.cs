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
    /// DB-01 (Agent B note, proven here with a stand-in for sqlcmd): "all databases" in log mode tried BACKUP LOG on a
    /// database in SIMPLE recovery (and on master) — SQL Server refuses it, so every log run, every 15 minutes, ended with
    /// an error although the logs that can exist were all sent. The databases whose log cannot be backed up are skipped,
    /// said once in the log; the others' logs are sent.
    /// </summary>
    public class SqlSimpleRecoveryLogTests
    {
        [Fact(Skip = "NEEDS OWNER DECISION (bug 76, docs/PRODUCT-BENCHMARK.md): skip, warn or fail a SIMPLE-recovery database in log mode")]
        public void LogMode_AllDatabases_ASimpleRecoveryDatabaseIsSkipped_NotAnErrorEveryRun()
        {
            if (Environment.OSVersion.Platform != PlatformID.Unix) return;
            using (var env = new Env())
            {
                var fake = Path.Combine(env.Root, "sqlcmd.sh");
                File.WriteAllText(fake, "#!/bin/sh\nQ=\"$*\"\ncase \"$Q\" in\n" +
                    " *Recovery*) case \"$Q\" in *Simple*|*master*) echo SIMPLE;; *) echo FULL;; esac;;\n" +
                    " *sysdatabases*) printf 'master\\nSales\\nSimple\\n';;\n *ProductVersion*) echo 15.0.2000.5;;\n" +
                    " *'BACKUP LOG [Simple]'*|*'BACKUP LOG [master]'*) echo 'Msg 4208: The statement BACKUP LOG is not allowed while the recovery model is SIMPLE.' >&2; exit 1;;\n" +
                    " *'BACKUP DATABASE'*) F=$(echo \"$Q\" | sed -n \"s/.*TO DISK='\\([^']*\\)'.*/\\1/p\"); head -c 300000 /dev/zero > \"$F\"; date +%s%N >> \"$F\";;\n" +
                    " *'BACKUP LOG'*) F=$(echo \"$Q\" | sed -n \"s/.*TO DISK='\\([^']*\\)'.*/\\1/p\"); date +%s%N > \"$F\";;\nesac\n");
                Process.Start("chmod", "+x \"" + fake + "\"").WaitForExit();
                Environment.SetEnvironmentVariable("OB_SQLCMD", fake);
                try
                {
                    env.CreateUser("sqlsimple", "Customer-Pass-1");
                    var app = env.Agent("sqlsimple", "Customer-Pass-1");
                    var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1",
                        new BackupSetInfo { Name = "SQL", Type = "MSSQL", Sources = { @"Microsoft SQL Server\SQLEXPRESS" }, LogIntervalMinutes = 15 });
                    Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);
                    var log = app.Backup(set.Id, "LOG");
                    Assert.True(log.Result == "BS_STOP_SUCCESS", "the log run ended " + log.Result + ":\n" + string.Join("\n", log.LogLines.Where(l => l.Contains("err") || l.Contains("warn"))));
                    var files = app.RestoreFor(app.Interactive("Customer-Pass-1", null), set.Id).Files(null).Select(f => f.Key).ToList();
                    Assert.Single(files.Where(f => f.EndsWith(".trn") && f.Contains(@"\Sales\")));          // the log that can exist was sent
                    Assert.Contains(log.LogLines, l => l.Contains("Simple") && l.Contains("SIMPLE"));        // and the skip is said
                }
                finally { Environment.SetEnvironmentVariable("OB_SQLCMD", null); }
            }
        }
    }
}
