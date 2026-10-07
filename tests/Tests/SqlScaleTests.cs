using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>SQL-050: big SQL Server databases — the room for the backup file is checked before, larger buffers for big ones.</summary>
    [Collection("Limits")]
    public class SqlScaleTests
    {
        [Fact]
        public void NotEnoughRoom_TheDatabaseIsSkippedWithAClearMessage_BigOnesGetLargerBuffers()
        {
            if (Environment.OSVersion.Platform != PlatformID.Unix) throw NotTested.Because("needs Linux (the stand-in tools are shell scripts)");
            using (var env = new Env())
            {
                var log = Path.Combine(env.Root, "sqlcmd.log");
                var fake = Path.Combine(env.Root, "sqlcmd.sh");
                File.WriteAllText(fake, "#!/bin/sh\nQ=\"$*\"\necho \"$Q\" >> '" + log + "'\ncase \"$Q\" in\n *sysdatabases*) printf 'Small\\nHuge\\n';;\n *ProductVersion*) echo 16.0.1000.6;;\n" +
                    " *master_files*Huge*) echo 32212254720;;\n *master_files*) echo 1048576;;\n" +
                    " *'BACKUP DATABASE'*) F=$(echo \"$Q\" | sed -n \"s/.*TO DISK='\\([^']*\\)'.*/\\1/p\"); head -c 100000 /dev/zero > \"$F\"; date +%s%N >> \"$F\";;\nesac\n");
                Process.Start("chmod", "+x \"" + fake + "\"").WaitForExit();
                Environment.SetEnvironmentVariable("OB_SQLCMD", fake);
                var before = SqlBackup.FreeSpace;
                try
                {
                    env.CreateUser("sqlbig", "Customer-Pass-1");
                    var app = env.Agent("sqlbig", "Customer-Pass-1");
                    var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "SQL", Type = "MSSQL", Sources = { @"Microsoft SQL Server\SQLEXPRESS" } });
                    // 25 GB free, the big database has 30 GB of data: skipped before anything is written, the small one is backed up
                    SqlBackup.FreeSpace = _ => 25L * 1024 * 1024 * 1024;
                    var r1 = app.Backup(set.Id);
                    Assert.Equal(1, r1.New);
                    Assert.Contains(r1.LogLines, l => l.Contains("Not enough free space for the backup of SQLEXPRESS") && l.Contains("Huge") && l.Contains("30.0 GB"));
                    Assert.DoesNotContain(File.ReadAllLines(log), l => l.Contains("BACKUP DATABASE [Huge]"));
                    // with room: backed up, with larger buffers
                    SqlBackup.FreeSpace = _ => 100L * 1024 * 1024 * 1024;
                    var r2 = app.Backup(set.Id);
                    Assert.Equal(1, r2.New);
                    Assert.Contains(File.ReadAllLines(log), l => l.Contains("BACKUP DATABASE [Huge]") && l.Contains("BUFFERCOUNT = 32") && l.Contains("MAXTRANSFERSIZE = 4194304"));
                    Assert.DoesNotContain(File.ReadAllLines(log), l => l.Contains("BACKUP DATABASE [Small]") && l.Contains("BUFFERCOUNT"));
                }
                finally { Environment.SetEnvironmentVariable("OB_SQLCMD", null); SqlBackup.FreeSpace = before; }
            }
        }

        /// <summary>A fake sqlcmd: lists Good and Bad; BACKUP of a database in <paramref name="failing"/> fails as SQL Server would.</summary>
        static string FakeSql(Env env, string failing, string hangOn = null)
        {
            var fake = Path.Combine(env.Root, "sqlcmd-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".sh");
            File.WriteAllText(fake, "#!/bin/sh\nQ=\"$*\"\ncase \"$Q\" in\n *sysdatabases*) printf 'Good\\nBad\\n';;\n *ProductVersion*) echo 16.0.1000.6;;\n *master_files*) echo 1048576;;\n" +
                (hangOn != null ? " *'BACKUP DATABASE ['" + hangOn + "*) sleep 600;;\n" : "") +
                " *'BACKUP DATABASE ['" + failing + "*) echo 'Msg 3201, Level 16: Cannot open backup device. Operating system error 5 (Access is denied.)' >&2; exit 1;;\n" +
                " *'BACKUP DATABASE'*) F=$(echo \"$Q\" | sed -n \"s/.*TO DISK='\\([^']*\\)'.*/\\1/p\"); head -c 1000 /dev/zero > \"$F\"; date +%s%N >> \"$F\";;\nesac\n");
            Process.Start("chmod", "+x \"" + fake + "\"").WaitForExit();
            return fake;
        }

        /// <summary>
        /// R1 (found while fixing GPT audit 4): a database whose backup fails was only a warning — the run ended "completed
        /// with warnings" although the database was not backed up. Now: one failing → completed with errors (red, a service
        /// call); all failing → the run failed.
        /// </summary>
        [Fact]
        public void FailingDatabase_IsAnError_AllFailing_IsAFailure()
        {
            if (Environment.OSVersion.Platform != PlatformID.Unix) throw NotTested.Because("needs Linux (the stand-in tools are shell scripts)");
            using (var env = new Env())
            {
                try
                {
                    env.CreateUser("sqlfail", "Customer-Pass-1");
                    var app = env.Agent("sqlfail", "Customer-Pass-1");
                    var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "SQL", Type = "MSSQL", Sources = { @"Microsoft SQL Server\SQLEXPRESS" } });
                    Environment.SetEnvironmentVariable("OB_SQLCMD", FakeSql(env, "Bad"));
                    var r1 = app.Backup(set.Id);
                    Assert.Equal("BS_STOP_SUCCESS_WITH_ERROR", r1.Result);
                    Assert.Contains(r1.LogLines, l => l.Contains("Bad") && l.Contains("Access is denied"));
                    Environment.SetEnvironmentVariable("OB_SQLCMD", FakeSql(env, ""));   // every BACKUP fails
                    var r2 = app.Backup(set.Id);
                    Assert.Equal("BS_STOP_BY_SYSTEM_ERROR", r2.Result);
                }
                finally { Environment.SetEnvironmentVariable("OB_SQLCMD", null); }
            }
        }

        /// <summary>
        /// R1 (GPT audit 5): sqlcmd that writes a lot to its error output. The old code read the whole output first and the
        /// error output after — the program blocked on a full pipe, and the backup hung forever.
        /// </summary>
        [Fact]
        public void SqlcmdWritingMuchToStderr_DoesNotHangTheBackup()
        {
            if (Environment.OSVersion.Platform != PlatformID.Unix) throw NotTested.Because("needs Linux (the stand-in tools are shell scripts)");
            using (var env = new Env())
            {
                var fake = Path.Combine(env.Root, "sqlcmd-noisy.sh");
                File.WriteAllText(fake, "#!/bin/sh\nQ=\"$*\"\nhead -c 300000 /dev/zero | tr '\\0' 'w' >&2\ncase \"$Q\" in\n *sysdatabases*) printf 'Good\\n';;\n *ProductVersion*) echo 16.0.1000.6;;\n *master_files*) echo 1048576;;\n" +
                    " *'BACKUP DATABASE'*) F=$(echo \"$Q\" | sed -n \"s/.*TO DISK='\\([^']*\\)'.*/\\1/p\"); head -c 1000 /dev/zero > \"$F\";;\nesac\n");
                Process.Start("chmod", "+x \"" + fake + "\"").WaitForExit();
                try
                {
                    env.CreateUser("sqlnoisy", "Customer-Pass-1");
                    var app = env.Agent("sqlnoisy", "Customer-Pass-1");
                    var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "SQL", Type = "MSSQL", Sources = { @"Microsoft SQL Server\SQLEXPRESS" } });
                    Environment.SetEnvironmentVariable("OB_SQLCMD", fake);
                    var t = System.Threading.Tasks.Task.Run(() => app.Backup(set.Id));
                    Assert.True(t.Wait(TimeSpan.FromSeconds(90)), "the backup hung on sqlcmd's output");
                    Assert.True(t.Result.New == 1, string.Join("\n", t.Result.LogLines.Select(l => l.Length > 300 ? l.Substring(0, 300) : l)));
                }
                finally { Environment.SetEnvironmentVariable("OB_SQLCMD", null); }
            }
        }

        /// <summary>
        /// R1 (GPT audit 5): a BACKUP DATABASE that never ends (a hung SQL Server, a stuck tape driver) stops at the limit:
        /// that database is an error, the others are backed up, the run ends.
        /// </summary>
        [Fact]
        public void HungDatabaseBackup_IsStoppedAtTheLimit_TheRunEnds()
        {
            if (Environment.OSVersion.Platform != PlatformID.Unix) throw NotTested.Because("needs Linux (the stand-in tools are shell scripts)");
            using (var env = new Env())
            {
                var before = Limits.SqlCommand;
                try
                {
                    env.CreateUser("sqlhang", "Customer-Pass-1");
                    var app = env.Agent("sqlhang", "Customer-Pass-1");
                    var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "SQL", Type = "MSSQL", Sources = { @"Microsoft SQL Server\SQLEXPRESS" } });
                    Environment.SetEnvironmentVariable("OB_SQLCMD", FakeSql(env, "Nothing", hangOn: "Bad"));
                    Limits.SqlCommand = TimeSpan.FromSeconds(3);
                    var sw = Stopwatch.StartNew();
                    var t = System.Threading.Tasks.Task.Run(() => app.Backup(set.Id));
                    Assert.True(t.Wait(TimeSpan.FromSeconds(90)), "the backup hung on a stuck sqlcmd");
                    Assert.Equal("BS_STOP_SUCCESS_WITH_ERROR", t.Result.Result);
                    Assert.Contains(t.Result.LogLines, l => l.Contains("Bad") && l.Contains("did not finish"));
                    Assert.Equal(1, t.Result.New);                                     // Good was backed up
                    Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60));
                }
                finally { Environment.SetEnvironmentVariable("OB_SQLCMD", null); Limits.SqlCommand = before; }
            }
        }

        /// <summary>R1 (found by the QA runs): a database set that finds no database at all backed up nothing and said "success".</summary>
        [Fact]
        public void NoDatabaseFound_IsAFailure_NotAnEmptySuccess()
        {
            if (Environment.OSVersion.Platform != PlatformID.Unix) throw NotTested.Because("needs Linux (the stand-in tools are shell scripts)");
            using (var env = new Env())
            {
                var fake = Path.Combine(env.Root, "sqlcmd-empty.sh");
                File.WriteAllText(fake, "#!/bin/sh\nQ=\"$*\"\ncase \"$Q\" in\n *ProductVersion*) echo 16.0.1000.6;;\nesac\n");
                Process.Start("chmod", "+x \"" + fake + "\"").WaitForExit();
                try
                {
                    env.CreateUser("sqlnone", "Customer-Pass-1");
                    var app = env.Agent("sqlnone", "Customer-Pass-1");
                    var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "SQL", Type = "MSSQL", Sources = { @"Microsoft SQL Server\SQLEXPRESS" } });
                    Environment.SetEnvironmentVariable("OB_SQLCMD", fake);
                    var r = app.Backup(set.Id);
                    Assert.Equal("BS_STOP_BY_SYSTEM_ERROR", r.Result);
                    Assert.Contains(r.LogLines, l => l.Contains("Nothing was backed up"));
                }
                finally { Environment.SetEnvironmentVariable("OB_SQLCMD", null); }
            }
        }

        [Fact]
        public void WeeklyFull_FromTheDefaultsForNewCustomers()
        {
            using (var env = new Env())
            {
                var admin = env.Admin();
                var d = admin.Call("GET", "/api/admin/defaults");
                d.Set("sqlFullDay", 5);
                admin.Call("POST", "/api/admin/defaults", d);
                env.CreateUser("sqlweek", "Customer-Pass-1");
                var app = env.Agent("sqlweek", "Customer-Pass-1");
                var s = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "SQL", Type = "MSSQL", Sources = { @"Microsoft SQL Server\SQLEXPRESS" } });
                Assert.Equal(5, app.Sets().Single(x => x.Id == s.Id).SqlFullDay);
                var f = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "Files", Sources = { env.Dir("x") } });
                Assert.Equal(-1, app.Sets().Single(x => x.Id == f.Id).SqlFullDay);   // only SQL Server sets
            }
        }
    }
}
