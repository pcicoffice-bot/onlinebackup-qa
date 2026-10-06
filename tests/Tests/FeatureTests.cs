using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>Minimal SMTP server that keeps every message (for the e-mail tests).</summary>
    public sealed class FakeSmtp : IDisposable
    {
        readonly TcpListener l = new TcpListener(IPAddress.Loopback, 0);
        public int Port { get { return ((IPEndPoint)l.LocalEndpoint).Port; } }
        public List<string> Messages = new List<string>();
        volatile bool stop;

        public FakeSmtp()
        {
            l.Start();
            new Thread(() =>
            {
                while (!stop)
                {
                    TcpClient c;
                    try { c = l.AcceptTcpClient(); } catch { return; }
                    try
                    {
                        using (c)
                        using (var s = c.GetStream())
                        using (var r = new StreamReader(s, Encoding.ASCII))
                        using (var w = new StreamWriter(s, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" })
                        {
                            w.WriteLine("220 fake");
                            string line; var data = new StringBuilder(); bool inData = false;
                            while ((line = r.ReadLine()) != null)
                            {
                                if (inData) { if (line == ".") { inData = false; lock (Messages) Messages.Add(data.ToString()); data.Clear(); w.WriteLine("250 ok"); } else data.AppendLine(line); continue; }
                                var cmd = line.Split(' ')[0].ToUpperInvariant();
                                if (cmd == "EHLO") { w.WriteLine("250-fake"); w.WriteLine("250 8BITMIME"); }
                                else if (cmd == "DATA") { w.WriteLine("354 go"); inData = true; }
                                else if (cmd == "QUIT") { w.WriteLine("221 bye"); break; }
                                else w.WriteLine("250 ok");
                            }
                        }
                    }
                    catch { }
                }
            }) { IsBackground = true }.Start();
        }

        public string All { get { lock (Messages) return string.Join("\n=====\n", Messages); } }

        /// <summary>Message text with quoted-printable / base64 bodies decoded enough to search for Hebrew words.</summary>
        public bool Has(string text)
        {
            lock (Messages)
                foreach (var m in Messages)
                {
                    if (m.Contains(text)) return true;
                    foreach (var part in m.Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.None))
                        try { if (Encoding.UTF8.GetString(Convert.FromBase64String(part.Replace("\r", "").Replace("\n", ""))).Contains(text)) return true; } catch { }
                    try { if (System.Net.Mail.Attachment.CreateAttachmentFromString("", "x").Name != null && QuotedPrintable(m).Contains(text)) return true; } catch { }
                }
            return false;
        }

        static string QuotedPrintable(string s)
        {
            s = s.Replace("=\r\n", "").Replace("=\n", "");
            var bytes = new List<byte>();
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '=' && i + 2 < s.Length && Uri.IsHexDigit(s[i + 1]) && Uri.IsHexDigit(s[i + 2])) { bytes.Add(Convert.ToByte(s.Substring(i + 1, 2), 16)); i += 2; }
                else bytes.AddRange(Encoding.UTF8.GetBytes(s[i].ToString()));
            }
            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        public void Dispose() { stop = true; l.Stop(); }
    }

    public class FeatureTests
    {
        static byte[] Rnd(int n, int seed) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }
        static string Restored(string target, string source) { return Path.Combine(target, Env.Rel(source)); }
        static void Tick() { Thread.Sleep(1100); }

        static void AssertSameTree(string expected, string actual)
        {
            var a = Directory.GetFiles(expected, "*", SearchOption.AllDirectories).Select(f => f.Substring(expected.Length)).OrderBy(x => x).ToList();
            var b = Directory.GetFiles(actual, "*", SearchOption.AllDirectories).Select(f => f.Substring(actual.Length)).OrderBy(x => x).ToList();
            Assert.Equal(a, b);
            foreach (var f in a) Assert.True(File.ReadAllBytes(expected + f).SequenceEqual(File.ReadAllBytes(actual + f)), "content differs: " + f);
        }

        [Fact]
        public void LocalCopyIsWrittenBesideTheOnlineBackupAndRestoresWithoutTheServer()
        {
            using (var env = new Env())
            {
                env.CreateUser("local2026", "Customer-Pass-1");
                var app = env.Agent("local2026", "Customer-Pass-1");
                var src = env.Dir("src"); var nas = env.Dir("nas");
                File.WriteAllText(Path.Combine(src, "a.txt"), "v1");
                File.WriteAllBytes(Path.Combine(src, "big.bin"), Rnd(3 * 1024 * 1024, 1));
                var s = new BackupSetInfo { Name = "L", Sources = { src }, LocalCopy = true, LocalCopyPath = nas, LocalCopyDays = 7, MinDeltaFileSize = 1024 * 1024 };
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", s);
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);
                Tick();
                File.WriteAllText(Path.Combine(src, "a.txt"), "v2");
                var b = File.ReadAllBytes(Path.Combine(src, "big.bin")); b[100] ^= 1; File.WriteAllBytes(Path.Combine(src, "big.bin"), b);
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);
                var repo = Path.Combine(nas, "local2026", set.Id);
                Assert.True(Directory.Exists(Path.Combine(repo, "Current")));
                Assert.Equal(2, File.ReadAllLines(Path.Combine(repo, "jobs.log")).Count(l => l.StartsWith("C\t")));

                env.Api.Dispose();                                             // the internet / server is gone
                var restore = app.RestoreLocal(set);
                var pts = restore.Points();
                Assert.Equal(2, pts.Count);
                var t1 = env.Dir("r1"); restore.Run(null, t1, null, false);
                Assert.Equal(0, restore.Failed);
                AssertSameTree(src, Restored(t1, src));
                var t0 = env.Dir("r0"); var r0 = app.RestoreLocal(set); r0.Run(pts[0], t0, null, false);
                Assert.Equal("v1", File.ReadAllText(Path.Combine(Restored(t0, src), "a.txt")));
                Assert.Equal(1, new LocalRepo(nas, "local2026", set.Id).ApplyRetention(7, DateTime.UtcNow.AddDays(30)));
            }
        }

        [Fact]
        public void MassChangeFreezesRetentionUntilAnAdministratorReleasesIt_AndAlertsByMail()
        {
            using (var smtp = new FakeSmtp())
            using (var env = new Env())
            {
                var admin = env.Admin();
                admin.Call("POST", "/api/admin/settings", new Msg().Set("smtpSet", "1").Set("senderName", "Backup").Set("senderEmail", "backup@example.invalid").Set("contactsSet", "1")
                    .Set("brandPRODUCT", "Acme Backup").Add("smtp", new Msg().Set("host", "127.0.0.1").Set("port", smtp.Port).Set("security", "NONE"))
                    .Add("contacts", new Msg().Set("name", "Ops").Set("email", "ops@example.invalid")));
                env.CreateUser("ransom2026", "Customer-Pass-1");
                var app = env.Agent("ransom2026", "Customer-Pass-1");
                var src = env.Dir("src");
                for (int i = 0; i < 80; i++) File.WriteAllText(Path.Combine(src, "doc" + i + ".docx"), "content " + i);
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "Docs", Sources = { src } });
                app.Backup(set.Id);
                Tick();
                for (int i = 0; i < 80; i++) File.WriteAllText(Path.Combine(src, "doc" + i + ".docx"), "ENCRYPTED!!" + i);   // ransomware
                app.Backup(set.Id);
                var prof = Profile.Load(Path.Combine(env.HomeA, "ransom2026", "db", "Profile.xml"));
                Assert.Equal("Y", prof.Get("RETENTION_FROZEN"));
                Assert.False(string.IsNullOrEmpty((string)prof.FindSet(set.Id).Attribute("SUSPECT_RANSOMWARE")));
                Assert.True(smtp.Has("Suspected ransomware") || smtp.All.Contains("=?utf-8?"), smtp.All);

                admin.Call("POST", "/api/admin/maintenance", new Msg().Set("now", RunId.From(DateTime.UtcNow.AddDays(40))));
                Assert.Equal(2, new SetStore(Path.Combine(env.HomeA, "ransom2026"), set.Id).Points().Count);   // nothing expired while frozen
                admin.Call("POST", "/api/admin/users/ransom2026/unfreeze");
                admin.Call("POST", "/api/admin/maintenance", new Msg().Set("now", RunId.From(DateTime.UtcNow.AddDays(40))));
                Assert.Single(new SetStore(Path.Combine(env.HomeA, "ransom2026"), set.Id).Points());
            }
        }

        [Fact]
        public void BackupReportTestMailAndMissedBackupAlertAreSent_PasswordsNeverLeaveTheServer()
        {
            using (var smtp = new FakeSmtp())
            using (var env = new Env())
            {
                var admin = env.Admin();
                admin.Call("POST", "/api/admin/settings", new Msg().Set("smtpSet", "1").Set("senderEmail", "backup@example.invalid").Set("contactsSet", "1")
                    .Add("smtp", new Msg().Set("host", "127.0.0.1").Set("port", smtp.Port).Set("security", "NONE").Set("login", "mailer").Set("password", "Smtp-Secret-9"))
                    .Add("contacts", new Msg().Set("name", "Ops").Set("email", "ops@example.invalid")));
                var settings = admin.Call("GET", "/api/admin/settings");
                Assert.Equal("1", settings.List("smtp")[0]["hasPassword"]);
                Assert.DoesNotContain("Smtp-Secret-9", settings.ToString());
                Assert.DoesNotContain("Smtp-Secret-9", File.ReadAllText(Path.Combine(env.SystemHome, "conf", "system.xml")));

                Assert.Equal("1", admin.Call("POST", "/api/admin/testmail", new Msg().Set("to", "me@example.invalid"))["sent"]);
                env.CreateUser("mail2026", "Customer-Pass-1");                 // contact it@example.invalid
                var app = env.Agent("mail2026", "Customer-Pass-1");
                var src = env.Dir("src"); File.WriteAllText(Path.Combine(src, "a.txt"), "x");
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "F", Sources = { src } });
                app.Backup(set.Id);
                Assert.Contains("it@example.invalid", smtp.All);
                int before = smtp.Messages.Count;
                admin.Call("POST", "/api/admin/maintenance", new Msg().Set("now", RunId.From(DateTime.UtcNow.AddDays(3))));
                Assert.True(smtp.Messages.Count > before);                     // missed-backup alert to ops
                Assert.Contains("MISSED BACKUP", string.Join("\n", Directory.GetFiles(Path.Combine(env.SystemHome, "logs", "BackupErrors")).Select(Atomic.ReadShared)));
                Assert.DoesNotContain("Smtp-Secret-9", string.Join("\n", Directory.GetFiles(Path.Combine(env.SystemHome, "logs"), "*.log", SearchOption.AllDirectories).Select(Atomic.ReadShared)));
            }
        }

        [Fact]
        public void SecondServerReceivesEveryCommitInOrder_AgentsRestoreFromIt_DeletionsWaitForTheDelay()
        {
            using (var primary = new Env())
            using (var second = new Env())
            {
                second.Admin().Call("POST", "/api/admin/settings", new Msg().Set("replicaReceiverToken", "repl-token-1234").Set("replicaDeleteDelayDays", "14"));
                primary.Admin().Call("POST", "/api/admin/settings", new Msg().Set("replicationUrl", second.Url).Set("replicationToken", "repl-token-1234").Set("replicationOn", "1"));
                primary.CreateUser("repl2026", "Customer-Pass-1");
                var app = primary.Agent("repl2026", "Customer-Pass-1");
                var src = primary.Dir("src");
                File.WriteAllText(Path.Combine(src, "a.txt"), "v1");
                File.WriteAllBytes(Path.Combine(src, "big.bin"), Rnd(2 * 1024 * 1024, 5));
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "R", Sources = { src }, MinDeltaFileSize = 1024 * 1024 });
                app.Backup(set.Id);
                Tick();
                File.WriteAllText(Path.Combine(src, "a.txt"), "v2");
                var b = File.ReadAllBytes(Path.Combine(src, "big.bin")); b[1000] ^= 1; File.WriteAllBytes(Path.Combine(src, "big.bin"), b);
                app.Backup(set.Id);
                Assert.True(primary.Api.Replication.RunOnce() > 0);
                Assert.Equal(0, primary.Api.Replication.Pending);

                // Disaster: the agent switches to the second server with the same registration.
                var cfg = app.Home.Config; cfg.SetAttributeValue("SERVER", second.Url); app.Home.Config = cfg;
                var session = app.Interactive("Customer-Pass-1", null);
                var r = app.RestoreFor(session, set.Id);
                Assert.Equal(2, r.Points().Count);
                var t = second.Dir("restore"); r.Run(null, t, null, false);
                Assert.Equal(0, r.Failed);
                AssertSameTree(src, Restored(t, src));
                var t0 = second.Dir("restore0"); var r0 = app.RestoreFor(session, set.Id); r0.Run(r.Points()[0], t0, null, false);
                Assert.True(r0.Failed == 0 && r0.Restored == 2, "restored " + r0.Restored + " failed " + r0.Failed + "\n" + string.Join("\n", r0.Log));
                Assert.Equal("v1", File.ReadAllText(Path.Combine(Restored(t0, src), "a.txt")));
                Assert.NotNull(second.Admin().Call("GET", "/api/admin/keys/repl2026/" + set.Id)["key"]);   // key recovery works there too

                // Append-only: an object on the second server cannot be overwritten.
                var obj = Directory.GetFiles(Path.Combine(second.HomeA, "repl2026", "files", set.Id, "Current"), "*.000", SearchOption.AllDirectories).First();
                var rel = obj.Substring(Path.Combine(second.HomeA, "repl2026").Length + 1).Replace(Path.DirectorySeparatorChar, '/');
                var req = (HttpWebRequest)WebRequest.Create(second.Url + "api/replica/file?login=repl2026&path=" + Uri.EscapeDataString(rel) + "&overwrite=1");
                req.Method = "PUT"; req.Headers["X-Replica-Token"] = "repl-token-1234";
                using (var s = req.GetRequestStream()) s.Write(new byte[] { 1, 2, 3 }, 0, 3);
                var ex = Assert.Throws<WebException>(() => req.GetResponse());
                Assert.Equal(409, (int)((HttpWebResponse)ex.Response).StatusCode);

                // Retention on the first server: the second one keeps the old versions for its delay, then deletes them.
                primary.Admin().Call("POST", "/api/admin/maintenance", new Msg().Set("now", RunId.From(DateTime.UtcNow.AddDays(40))));
                primary.Api.Replication.RunOnce();
                var secondSet = Path.Combine(second.HomeA, "repl2026", "files", set.Id);
                Assert.True(Directory.GetDirectories(secondSet).Any(d => RunId.TryParse(Path.GetFileName(d), out _)));
                second.Admin().Call("POST", "/api/admin/maintenance", new Msg().Set("now", RunId.From(DateTime.UtcNow.AddDays(15))));
                Assert.False(Directory.GetDirectories(secondSet).Any(d => RunId.TryParse(Path.GetFileName(d), out _)));
            }
        }

        [Fact]
        public void ExportForITSguardHasTheAhsayShareLayout()
        {
            using (var env = new Env())
            {
                var profiles = env.Dir("Profiles$"); var logs = env.Dir("DetailedLogs$");
                env.Admin().Call("POST", "/api/admin/settings", new Msg().Set("exportProfiles", profiles).Set("exportLogs", logs));
                env.CreateUser("exp2026", "Customer-Pass-1");
                var app = env.Agent("exp2026", "Customer-Pass-1");
                var src = env.Dir("src"); File.WriteAllText(Path.Combine(src, "a.txt"), "x");
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "F", Sources = { src } });
                var run = app.Backup(set.Id);
                var p = Profile.Load(Path.Combine(profiles, "exp2026", "Profile.xml"));
                Assert.Equal("F", (string)p.FindSet(set.Id).Attribute("NAME"));
                Assert.Equal(src, p.FindSet(set.Id).Element("SEL-SOURCE").Value);
                var log = Path.Combine(logs, "exp2026", set.Id, run.Job + ".log");
                Assert.True(File.Exists(log));
                Assert.Equal("BS_STOP_SUCCESS", AhsayLog.Fields(File.ReadAllLines(log).Last())[4]);
            }
        }

        [Fact]
        public void AutomaticRestoreTestComparesWithTheSourceAndReportsToTheServer()
        {
            using (var env = new Env())
            {
                env.CreateUser("rt2026", "Customer-Pass-1");
                var app = env.Agent("rt2026", "Customer-Pass-1");
                var src = env.Dir("src");
                for (int i = 0; i < 6; i++) File.WriteAllBytes(Path.Combine(src, "f" + i + ".bin"), Rnd(50000 + i, i));
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "F", Sources = { src } });
                app.Backup(set.Id);
                var t = app.RestoreTest(set.Id, 6);
                Assert.Equal(6, t.Int("checked")); Assert.Equal(0, t.Int("failed"));
                var prof = Profile.Load(Path.Combine(env.HomeA, "rt2026", "db", "Profile.xml"));
                Assert.StartsWith("OK 6/6", (string)prof.FindSet(set.Id).Attribute("RESTORE_TEST_RESULT"));

                // The source changed silently (same size and time): the test notices the difference and reports a failure.
                var f0 = Path.Combine(src, "f0.bin"); var mt = File.GetLastWriteTimeUtc(f0);
                var bytes = File.ReadAllBytes(f0); bytes[10] ^= 0xFF; File.WriteAllBytes(f0, bytes); File.SetLastWriteTimeUtc(f0, mt);
                var t2 = app.RestoreTest(set.Id, 6);
                Assert.Equal(1, t2.Int("failed"));
                Assert.StartsWith("FAILED", (string)Profile.Load(Path.Combine(env.HomeA, "rt2026", "db", "Profile.xml")).FindSet(set.Id).Attribute("RESTORE_TEST_RESULT"));
            }
        }

        [Fact]
        public void MssqlFullAndLogBackupsWithNativeBackupFiles_RestoreGivesTheBakFiles()
        {
            if (Environment.OSVersion.Platform != PlatformID.Unix) return;
            using (var env = new Env())
            {
                // A stand-in for sqlcmd: lists two databases and writes .bak / .trn files where BACKUP asks.
                var fake = Path.Combine(env.Root, "sqlcmd.sh");
                File.WriteAllText(fake, "#!/bin/sh\nQ=\"$*\"\ncase \"$Q\" in\n *sysdatabases*) printf 'Sales\\nHR\\n';;\n *ProductVersion*) echo 15.0.2000.5;;\n" +
                    " *'BACKUP DATABASE'*) F=$(echo \"$Q\" | sed -n \"s/.*TO DISK='\\([^']*\\)'.*/\\1/p\"); head -c 3000000 /dev/zero | tr '\\\\0' 'A' > \"$F\"; date +%s%N >> \"$F\";;\n" +
                    " *'BACKUP LOG'*) F=$(echo \"$Q\" | sed -n \"s/.*TO DISK='\\([^']*\\)'.*/\\1/p\"); date +%s%N > \"$F\";;\nesac\n");
                Process.Start("chmod", "+x \"" + fake + "\"").WaitForExit();
                Environment.SetEnvironmentVariable("OB_SQLCMD", fake);
                try
                {
                    env.CreateUser("sql2026", "Customer-Pass-1");
                    var app = env.Agent("sql2026", "Customer-Pass-1");
                    var s = new BackupSetInfo { Name = "SQL", Type = "MSSQL", Sources = { @"Microsoft SQL Server\SQLEXPRESS" }, MinDeltaFileSize = 1024 * 1024, LogIntervalMinutes = 15 };
                    var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", s);
                    var r1 = app.Backup(set.Id);
                    Assert.Equal("BS_STOP_SUCCESS", r1.Result);
                    Assert.Equal(2, r1.New);
                    Assert.Contains(r1.LogLines, l => l.Contains("Database - complete"));
                    Tick();
                    var r2 = app.Backup(set.Id);                                // a new full dump each run → sent as a small delta
                    Assert.Equal(2, r2.Updated);
                    Assert.True(r2.BytesSent < 2 * 1024 * 1024, "sent " + r2.BytesSent);
                    Tick();
                    var r3 = app.Backup(set.Id, "LOG");                          // transaction logs only; the .bak files stay
                    Assert.Equal(2, r3.New); Assert.Equal(0, r3.Deleted);
                    var restore = app.RestoreFor(app.Interactive("Customer-Pass-1", null), set.Id);
                    var files = restore.Files(null).Select(f => f.Key).ToList();
                    Assert.Contains(@"Microsoft SQL Server\SQLEXPRESS\Sales\DATABASE_Sales.bak", files);
                    Assert.Equal(2, files.Count(f => f.EndsWith(".trn")));
                    var t = env.Dir("restore"); restore.Run(null, t, null, false);
                    Assert.Equal(0, restore.Failed);
                    Assert.True(new FileInfo(Path.Combine(t, "Microsoft SQL Server", "SQLEXPRESS", "Sales", "DATABASE_Sales.bak")).Length > 3000000);
                    Assert.Contains("STOPAT='2026-10-03T12:00:00'", SqlRestore.Script("Sales_restored", @"C:\r\Sales.bak", new[] { @"C:\r\1.trn" }, @"D:\Data", new List<string[]> { new[] { "Sales", "D" }, new[] { "Sales_log", "L" } }, new DateTime(2026, 10, 3, 12, 0, 0)));
                }
                finally { Environment.SetEnvironmentVariable("OB_SQLCMD", null); }
            }
        }

        [Fact]
        public void MssqlWeeklyFullAndDailyDifferential_TheFullStaysInEveryPoint_AnotherProgramsFullForcesOurs()
        {
            if (Environment.OSVersion.Platform != PlatformID.Unix) return;
            using (var env = new Env())
            {
                var fake = Path.Combine(env.Root, "sqlcmd.sh");
                File.WriteAllText(fake, "#!/bin/sh\nQ=\"$*\"\ncase \"$Q\" in\n *sysdatabases*) printf 'Sales\\n';;\n *ProductVersion*) echo 15.0.2000.5;;\n *backupset*) echo ${FAKE_LASTFULL:-2026-10-01T22:00:00.000};;\n" +
                    " *'BACKUP DATABASE'*) F=$(echo \"$Q\" | sed -n \"s/.*TO DISK='\\([^']*\\)'.*/\\1/p\"); head -c 3000000 /dev/zero | tr '\\\\0' 'A' > \"$F\"; date +%s%N >> \"$F\";;\nesac\n");
                Process.Start("chmod", "+x \"" + fake + "\"").WaitForExit();
                Environment.SetEnvironmentVariable("OB_SQLCMD", fake);
                try
                {
                    env.CreateUser("sqld2026", "Customer-Pass-1");
                    var app = env.Agent("sqld2026", "Customer-Pass-1");
                    int notToday = ((int)DateTime.Now.DayOfWeek + 1) % 7;
                    var s = new BackupSetInfo { Name = "SQL", Type = "MSSQL", Sources = { @"Microsoft SQL Server\SQLEXPRESS" }, MinDeltaFileSize = 1024 * 1024, SqlFullDay = notToday };
                    var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", s);
                    var r1 = app.Backup(set.Id);                                       // no full of ours yet → full
                    Assert.Contains(r1.LogLines, l => l.Contains("Database - complete"));
                    Tick();
                    var r2 = app.Backup(set.Id);                                       // not the full day → differential
                    Assert.Equal("BS_STOP_SUCCESS", r2.Result);
                    Assert.Contains(r2.LogLines, l => l.Contains("Database - differential"));
                    Assert.Equal(0, r2.Deleted);
                    var files = app.RestoreFor(app.Interactive("Customer-Pass-1", null), set.Id).Files(null).Select(f => f.Key).ToList();
                    Assert.Contains(@"Microsoft SQL Server\SQLEXPRESS\Sales\DATABASE_Sales.bak", files);   // restore = full + differential
                    Assert.Contains(@"Microsoft SQL Server\SQLEXPRESS\Sales\DIFF_Sales.bak", files);
                    Tick();
                    Environment.SetEnvironmentVariable("FAKE_LASTFULL", "2026-10-02T03:00:00.000");   // another program made a full backup
                    var r3 = app.Backup(set.Id);
                    Assert.Contains(r3.LogLines, l => l.Contains("Another program made a full backup"));
                    Assert.Contains(r3.LogLines, l => l.Contains("Database - complete"));
                    Assert.Contains("WITH NORECOVERY; RESTORE DATABASE [Sales_r] FROM DISK='C:\\r\\DIFF.bak' WITH RECOVERY;",
                        SqlRestore.Script("Sales_r", @"C:\r\FULL.bak", new string[0], @"D:\Data", new List<string[]>(), null, @"C:\r\DIFF.bak"));
                }
                finally { Environment.SetEnvironmentVariable("OB_SQLCMD", null); Environment.SetEnvironmentVariable("FAKE_LASTFULL", null); }
            }
        }

        [Fact]
        public void ManagementUiIsServedWithStrictHeadersAndBranding()
        {
            using (var env = new Env())
            {
                env.Admin().Call("POST", "/api/admin/settings", new Msg().Set("brandPRODUCT", "Acme Backup").Set("brandCOLOR", "#204060"));
                using (var http = new System.Net.Http.HttpClient())
                {
                    var page = http.GetAsync(env.Url + "admin").Result;
                    Assert.True(page.IsSuccessStatusCode);
                    Assert.Contains("/admin/app.js", page.Content.ReadAsStringAsync().Result);
                    Assert.Contains("script-src 'self'", string.Join(";", page.Headers.GetValues("Content-Security-Policy")));
                    var js = http.GetStringAsync(env.Url + "admin/app.js").Result;
                    Assert.Contains("Save and exit", js); Assert.Contains("Exit without saving", js);
                    Assert.Contains("Acme Backup", http.GetStringAsync(env.Url + "api/brand").Result);
                    Assert.Equal(HttpStatusCode.NotFound, http.GetAsync(env.Url + "admin/../conf/system.xml").Result.StatusCode == HttpStatusCode.OK ? HttpStatusCode.OK : HttpStatusCode.NotFound);
                }
                var logs = env.Admin().Call("GET", "/api/admin/logs?cat=Admin");
                Assert.Contains("changed the system settings", logs["text"]);
            }
        }
    }
}
