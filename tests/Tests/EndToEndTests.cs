using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>A real server on a free port with its own folders; agents talk to it over HTTP like in production.</summary>
    public sealed class Env : IDisposable
    {
        /// <summary>Where a source folder lands under a restore folder: the drive letter becomes a folder (C:\x → target\C\x), as the agent and restic do.</summary>
        public static string Rel(string source) { return source.Replace(":", "").TrimStart('\\', '/'); }
        public string Root, SystemHome, HomeA, HomeB, Url;
        public Api Api;
        public SystemConfig Cfg;

        /// <summary>A test signing key (the real one stays with the product owner); every test server gets a full licence unless asked.</summary>
        public static readonly string[] TestKey = License.KeyGen();
        static Env() { Environment.SetEnvironmentVariable("OB_LICENSE_PUBKEY", TestKey[1]); }

        public void SetLicense(string edition, int users, double storageGb, params string[] modules) { SetLicense(edition, users, storageGb, "", modules); }
        public void SetLicense(string edition, int users, double storageGb, string center, params string[] modules)
        {
            var key = License.Issue(TestKey[0], "L-TEST", "Test IT", edition, Cfg.ServerId, users, storageGb, modules.Length == 0 ? License.AllModules : modules, DateTime.UtcNow, DateTime.UtcNow.AddDays(30), 0, center);
            var el = Cfg.Doc.Root.Element("LICENSE"); el.SetAttributeValue("KEY", key); Cfg.Save(); Cfg.ResetLicense();
        }
        public void RemoveLicense() { Cfg.Doc.Root.Element("LICENSE").SetAttributeValue("KEY", ""); Cfg.Save(); Cfg.ResetLicense(); }

        public Env(string homeA = "|UNLIMITED|100", string homeB = null, bool licensed = true)
        {
            Root = Path.Combine(Path.GetTempPath(), "obtest-" + Guid.NewGuid().ToString("N").Substring(0, 10));
            SystemHome = Path.Combine(Root, "system");
            HomeA = Path.Combine(Root, "homeA"); HomeB = Path.Combine(Root, "homeB");
            var homes = new List<string> { HomeA + homeA };
            if (homeB != null) homes.Add(HomeB + homeB);
            Cfg = SystemConfig.Init(SystemHome, "admin", "Admin-Pass-1", "localhost", homes);
            Cfg.Doc.Root.SetAttributeValue("LOCAL_TRUST", "N"); Cfg.Save();   // the test run signs in from this computer too: no shortcut for it
            var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); int port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
            Url = "http://localhost:" + port + "/";
            if (licensed) SetLicense("PRO", 0, 0);
            Api = new Api(Cfg);
            Api.Start(Url);
        }

        public Client Admin()
        {
            return TestAuth.Admin(Url, "admin", "Admin-Pass-1");
        }

        public void CreateUser(string login, string password, double quotaGB = 1, string quotaType = "COMPRESSED")
        {
            Admin().Call("POST", "/api/admin/users", new Msg().Set("login", login).Set("password", password).Set("alias", login).Set("quotaGB", quotaGB).Set("quotaType", quotaType).Set("email", "it@example.invalid"));
        }

        public AgentApp Agent(string login, string password, string otp = null, string name = "agent")
        {
            var app = new AgentApp(Path.Combine(Root, name + "-" + Guid.NewGuid().ToString("N").Substring(0, 6)));
            app.Register(Url, login, password, otp, "PC-" + name);
            return app;
        }

        public string Dir(string name) { var d = Path.Combine(Root, name); Directory.CreateDirectory(d); return d; }

        public void Dispose()
        {
            Api.Dispose();
            try { Directory.Delete(Root, true); } catch (IOException) { }
        }
    }

    public class EndToEndTests
    {
        static MemoryStream FakeObject()
        {
            var ms = new MemoryStream();
            var w = new BackupObject.Writer(ms, KeySet.Random());
            w.AddChunk("x", new byte[] { 1, 2, 3 });
            w.Finish(new Msg());
            ms.Position = 0;
            return ms;
        }

        static byte[] Rnd(int n, int seed) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }

        static void AssertSameTree(string expected, string actual)
        {
            var a = Directory.GetFiles(expected, "*", SearchOption.AllDirectories).Select(f => f.Substring(expected.Length)).OrderBy(x => x).ToList();
            var b = Directory.GetFiles(actual, "*", SearchOption.AllDirectories).Select(f => f.Substring(actual.Length)).OrderBy(x => x).ToList();
            Assert.Equal(a, b);
            foreach (var f in a) Assert.True(File.ReadAllBytes(expected + f).SequenceEqual(File.ReadAllBytes(actual + f)), "content differs: " + f);
        }

        static string Restored(string target, string source) { return Path.Combine(target, Env.Rel(source)); }

        static BackupSetInfo NewSet(AgentApp app, string password, string source, long minDelta = 25L * 1024 * 1024)
        {
            var s = new BackupSetInfo { Name = "Files", Sources = { source } };
            var created = app.CreateSet(app.Interactive(password, null), password, s);
            return created;
        }

        [Fact]
        public void FullCycle_NewUpdatedPermissionDeleted_DeltaForLargeFiles_RestoreAnyPoint()
        {
            using (var env = new Env())
            {
                env.CreateUser("acme2026", "Customer-Pass-1");
                var app = env.Agent("acme2026", "Customer-Pass-1");
                var src = env.Dir("src");
                Directory.CreateDirectory(Path.Combine(src, "Accounting", "2026"));
                File.WriteAllText(Path.Combine(src, "readme.txt"), "hello");
                File.WriteAllText(Path.Combine(src, "Accounting", "2026", "ledger.csv"), string.Join("\n", Enumerable.Range(0, 5000).Select(i => "row," + i)));
                File.WriteAllText(Path.Combine(src, "Accounting", "to-delete.txt"), "old");
                File.WriteAllText(Path.Combine(src, "temp.tmp"), "excluded");
                var big = Rnd(30 * 1024 * 1024, 7);                         // above the 25MB delta threshold (like a PST)
                File.WriteAllBytes(Path.Combine(src, "mail.pst"), big);
                var set = NewSet(app, "Customer-Pass-1", src);

                var r1 = app.Backup(set.Id);
                Assert.Equal("BS_STOP_SUCCESS", r1.Result);
                Assert.Equal(4, r1.New);                                     // temp.tmp excluded by the default policy filter
                var snapshot1 = env.Dir("snapshot1");
                foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".tmp")))
                {
                    var d = Path.Combine(snapshot1, f.Substring(src.Length + 1));
                    Directory.CreateDirectory(Path.GetDirectoryName(d)); File.Copy(f, d);
                }

                // Server layout: Ahsay tree with encrypted names, .chk beside every object, Profile.xml, log in Ahsay CSV.
                var userDir = Path.Combine(env.HomeA, "acme2026");
                var current = Path.Combine(userDir, "files", set.Id, "Current");
                var objects = Directory.GetFiles(current, "*.000", SearchOption.AllDirectories);
                Assert.Equal(4, objects.Length);
                Assert.All(objects, o => Assert.True(File.Exists(o + ".chk")));
                Assert.DoesNotContain(Directory.GetFileSystemEntries(current, "*", SearchOption.AllDirectories), p => p.Contains("Accounting") || p.Contains("ledger") || p.Contains("mail"));
                var profile = Profile.Load(Path.Combine(userDir, "db", "Profile.xml"));
                var setXml = profile.FindSet(set.Id);
                Assert.Equal("4", (string)setXml.Attribute("NO_OF_FILES"));
                Assert.True(long.Parse((string)setXml.Attribute("TOTAL_BSET_SIZE")) > 0);
                Assert.Equal(src, setXml.Element("SEL-SOURCE").Value);
                var logFile = Directory.GetFiles(Path.Combine(userDir, "logs", set.Id, "Backup")).Single();
                var lines = File.ReadAllLines(logFile);
                Assert.Equal("start", AhsayLog.Fields(lines[0])[1]);
                Assert.Equal("BS_STOP_SUCCESS", AhsayLog.Fields(lines.Last())[4]);
                Assert.Equal(4, lines.Count(l => AhsayLog.Fields(l)[1] == "new"));

                // Changes: one appended, 1KB inserted in the middle of the PST, one deleted, one added, one only read-only.
                File.AppendAllText(Path.Combine(src, "Accounting", "2026", "ledger.csv"), "\nrow,new");
                var big2 = big.Take(15 * 1024 * 1024).Concat(Rnd(1024, 8)).Concat(big.Skip(15 * 1024 * 1024)).ToArray();
                File.WriteAllBytes(Path.Combine(src, "mail.pst"), big2);
                File.Delete(Path.Combine(src, "Accounting", "to-delete.txt"));
                File.WriteAllText(Path.Combine(src, "new.txt"), "new file");
                File.SetAttributes(Path.Combine(src, "readme.txt"), FileAttributes.ReadOnly);
                System.Threading.Thread.Sleep(1100);                        // distinct run id

                var r2 = app.Backup(set.Id);
                Assert.Equal("BS_STOP_SUCCESS", r2.Result);
                Assert.Equal(1, r2.New); Assert.Equal(2, r2.Updated); Assert.Equal(1, r2.Deleted); Assert.Equal(1, r2.PermOnly);
                Assert.True(r2.BytesSent < 5 * 1024 * 1024, "sent " + r2.BytesSent + " bytes for a 1KB change in a 30MB file");
                Assert.Single(Directory.GetFiles(current, "*.001", SearchOption.AllDirectories));   // the PST delta beside its full copy

                System.Threading.Thread.Sleep(1100);
                var r3 = app.Backup(set.Id);                                // nothing changed: nothing sent
                Assert.Equal(0, r3.New + r3.Updated + r3.Deleted + r3.PermOnly);

                // Restore: latest point to a new folder, and the first point (before the changes).
                var session = app.Interactive("Customer-Pass-1", null);
                var restore = app.RestoreFor(session, set.Id);
                var points = restore.Points();
                Assert.Equal(3, points.Count);
                var latest = env.Dir("restore-latest");
                restore.Run(null, latest, null, false);
                Assert.Equal(0, restore.Failed);
                File.SetAttributes(Path.Combine(src, "readme.txt"), FileAttributes.Normal);
                var expected = env.Dir("expected");
                foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".tmp")))
                {
                    var d = Path.Combine(expected, f.Substring(src.Length + 1));
                    Directory.CreateDirectory(Path.GetDirectoryName(d)); File.Copy(f, d);
                }
                AssertSameTree(expected, Restored(latest, src));

                var first = env.Dir("restore-first");
                var restore1 = app.RestoreFor(session, set.Id);
                restore1.Run(points[0], first, null, false);
                Assert.Equal(0, restore1.Failed);
                AssertSameTree(snapshot1, Restored(first, src));
                Assert.True(Directory.GetFiles(Path.Combine(userDir, "logs", set.Id, "Restore")).Length >= 2);

                // Restore needs an interactive sign-in: the device token alone cannot download data.
                var loc = session.Call("GET", "/api/sets/" + set.Id + "/files").List("files")[0].List("objects")[0]["loc"];
                var ex = Assert.Throws<AgentException>(() => app.DeviceClient().Download("/api/sets/" + set.Id + "/object?loc=" + Client.Url(loc), Path.Combine(env.Root, "x")));
                Assert.Equal("SESSION", ex.Code);
            }
        }

        [Fact]
        public void CrashDuringCommitIsRolledForward_AndAnUncommittedRunLeavesNothing()
        {
            using (var env = new Env())
            {
                env.CreateUser("crash2026", "Customer-Pass-1");
                var app = env.Agent("crash2026", "Customer-Pass-1");
                var src = env.Dir("src");
                File.WriteAllText(Path.Combine(src, "a.txt"), "v1");
                var set = NewSet(app, "Customer-Pass-1", src);
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);
                var userDir = Path.Combine(env.HomeA, "crash2026");

                // A run that uploads but never commits: no new point, Current untouched.
                var store = new SetStore(userDir, set.Id);
                var before = store.FilesAt(null).ToString();
                var job = store.BeginJob(DateTime.UtcNow.AddMinutes(1));
                var rel = store.FilesAt(null).List("files")[0]["rel"];
                store.StageObject(job, new ChkRecord { Rel = rel, Seq = 0, Kind = "F", EncPath = "x", Orig = 2, Mtime = 1 }, FakeObject(), 1 << 20);
                store.Abort(job);
                Assert.Equal(1, store.Points().Count);
                Assert.Equal(before, store.FilesAt(null).ToString());

                // A run whose journal was written, then the server "crashed" before moving anything.
                job = store.BeginJob(DateTime.UtcNow.AddMinutes(2));
                store.StageObject(job, new ChkRecord { Rel = rel, Seq = 0, Kind = "F", EncPath = "x", Orig = 3, Mtime = 2 }, FakeObject(), 1 << 20);
                store.PrepareCommit(job, new Msg().Set("upd", 1));
                var restarted = new SetStore(userDir, set.Id);             // the server starts again
                Assert.Equal(2, restarted.Points().Count);
                var f = restarted.FilesAt(null).List("files").Single();
                Assert.Equal("3", f["orig"]);
                Assert.Single(restarted.FilesAt(restarted.Points()[0]).List("files"));   // the old version is in the retention area
                Assert.False(Directory.Exists(Path.Combine(userDir, "files", set.Id, "jobs", job)));
            }
        }

        [Fact]
        public void DamagedObjectIsFoundQuarantinedAndResentFromTheSource()
        {
            using (var env = new Env())
            {
                env.CreateUser("heal2026", "Customer-Pass-1");
                var app = env.Agent("heal2026", "Customer-Pass-1");
                var src = env.Dir("src");
                File.WriteAllBytes(Path.Combine(src, "data.bin"), Rnd(200000, 1));
                File.WriteAllText(Path.Combine(src, "ok.txt"), "fine");
                var set = NewSet(app, "Customer-Pass-1", src);
                app.Backup(set.Id);
                var current = Path.Combine(env.HomeA, "heal2026", "files", set.Id, "Current");
                var victim = Directory.GetFiles(current, "*.000", SearchOption.AllDirectories).OrderByDescending(f => new FileInfo(f).Length).First();
                var bytes = File.ReadAllBytes(victim); bytes[bytes.Length / 2] ^= 0xFF; File.WriteAllBytes(victim, bytes);   // bit rot

                var v = env.Admin().Call("POST", "/api/admin/verify", new Msg().Set("login", "heal2026").Set("set", set.Id));
                Assert.Equal(1, v.Int("bad"));
                Assert.True(Directory.Exists(Path.Combine(env.HomeA, "heal2026", "files", set.Id, "Quarantine")));

                System.Threading.Thread.Sleep(1100);
                var r = app.Backup(set.Id);                                  // nothing changed locally, yet the file is sent again
                Assert.Equal(1, r.Updated);
                var target = env.Dir("restore");
                var restore = app.RestoreFor(app.Interactive("Customer-Pass-1", null), set.Id);
                restore.Run(null, target, null, false);
                Assert.Equal(0, restore.Failed);
                AssertSameTree(src, Restored(target, src));
            }
        }

        [Fact]
        public void RebuildRecreatesTheIndexFromTheDisk()
        {
            using (var env = new Env())
            {
                env.CreateUser("rebuild2026", "Customer-Pass-1");
                var app = env.Agent("rebuild2026", "Customer-Pass-1");
                var src = env.Dir("src");
                for (int i = 0; i < 20; i++) File.WriteAllText(Path.Combine(src, "f" + i + ".txt"), "content " + i);
                var set = NewSet(app, "Customer-Pass-1", src);
                app.Backup(set.Id);
                File.WriteAllText(Path.Combine(src, "f1.txt"), "changed");
                System.Threading.Thread.Sleep(1100);
                app.Backup(set.Id);
                var userDir = Path.Combine(env.HomeA, "rebuild2026");
                var store = new SetStore(userDir, set.Id);
                var points = store.Points();
                var listing = points.Select(p => store.FilesAt(p).ToString()).ToList();

                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                foreach (var f in Directory.GetFiles(Path.Combine(userDir, "files", set.Id), "index.db*")) File.Delete(f);
                var r = env.Admin().Call("POST", "/api/admin/rebuild", new Msg().Set("login", "rebuild2026").Set("set", set.Id).Set("verify", "1"));
                Assert.Equal(21, r.Int("files")); Assert.Equal(0, r.Int("bad")); Assert.Equal(0, r.Int("orphans"));
                var again = new SetStore(userDir, set.Id);
                Assert.Equal(points, again.Points());
                Assert.Equal(listing, points.Select(p => again.FilesAt(p).ToString()).ToList());
                Assert.NotEqual("0", Profile.Load(Path.Combine(userDir, "db", "Profile.xml")).Get("LAST_STORAGE_REBUILD"));
            }
        }

        [Fact]
        public void RetentionDeletesOnlyWhatNoKeptPointNeeds_CurrentIsNeverTouched()
        {
            using (var env = new Env())
            {
                env.CreateUser("ret2026", "Customer-Pass-1");
                var app = env.Agent("ret2026", "Customer-Pass-1");
                var src = env.Dir("src");
                File.WriteAllText(Path.Combine(src, "a.txt"), "v1");
                File.WriteAllText(Path.Combine(src, "b.txt"), "stable");
                var set = NewSet(app, "Customer-Pass-1", src);
                app.Backup(set.Id);
                for (int v = 2; v <= 4; v++) { System.Threading.Thread.Sleep(1100); File.WriteAllText(Path.Combine(src, "a.txt"), "v" + v); app.Backup(set.Id); }
                var setDir = Path.Combine(env.HomeA, "ret2026", "files", set.Id);
                Assert.Equal(3, Directory.GetDirectories(setDir).Count(d => RunId.TryParse(Path.GetFileName(d), out _)));

                // 40 days later, 30-day policy: only the latest point stays, the retention area is emptied.
                var m = env.Admin().Call("POST", "/api/admin/maintenance", new Msg().Set("now", RunId.From(DateTime.UtcNow.AddDays(40))));
                Assert.Equal(3, m.List("sets")[0].Int("expiredPoints"));
                Assert.Equal(0, Directory.GetDirectories(setDir).Count(d => RunId.TryParse(Path.GetFileName(d), out _)));
                var target = env.Dir("restore");
                var restore = app.RestoreFor(app.Interactive("Customer-Pass-1", null), set.Id);
                Assert.Single(restore.Points());
                restore.Run(null, target, null, false);
                Assert.Equal("v4", File.ReadAllText(Path.Combine(Restored(target, src), "a.txt")));
                Assert.Equal("stable", File.ReadAllText(Path.Combine(Restored(target, src), "b.txt")));
                Assert.Single(Directory.GetFiles(Path.Combine(env.HomeA, "ret2026", "logs", set.Id, "Retention")));
            }
        }

        [Fact]
        public void QuotaStopsNewBackupsAndKeepsExistingOnes()
        {
            using (var env = new Env())
            {
                env.CreateUser("quota2026", "Customer-Pass-1", quotaGB: 0.001);   // ~1MB
                var app = env.Agent("quota2026", "Customer-Pass-1");
                var src = env.Dir("src");
                File.WriteAllBytes(Path.Combine(src, "small.bin"), Rnd(200000, 3));
                var set = NewSet(app, "Customer-Pass-1", src);
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);
                File.WriteAllBytes(Path.Combine(src, "big.bin"), Rnd(3 * 1024 * 1024, 4));
                System.Threading.Thread.Sleep(1100);
                Assert.Equal("BS_STOP_QUOTA_EXCEEDED", app.Backup(set.Id).Result);
                var store = new SetStore(Path.Combine(env.HomeA, "quota2026"), set.Id);
                Assert.Single(store.FilesAt(null).List("files"));            // the earlier backup is intact
                Assert.True(Directory.GetFiles(Path.Combine(env.SystemHome, "logs", "BackupErrors")).Length > 0);
            }
        }

        [Fact]
        public void TenSetsPerUser()
        {
            using (var env = new Env())
            {
                env.CreateUser("sets2026", "Customer-Pass-1");
                var app = env.Agent("sets2026", "Customer-Pass-1");
                var src = env.Dir("src");
                var session = app.Interactive("Customer-Pass-1", null);
                for (int i = 0; i < 10; i++) app.CreateSet(session, "Customer-Pass-1", new BackupSetInfo { Name = "S" + i, Sources = { src } }, "DEFAULT");
                var ex = Assert.Throws<AgentException>(() => app.CreateSet(session, "Customer-Pass-1", new BackupSetInfo { Name = "S11", Sources = { src } }, "DEFAULT"));
                Assert.Equal("SET_LIMIT", ex.Code);
                Assert.Equal(10, app.Sets().Count);
            }
        }

        [Fact]
        public void LockoutAfterThreeFailures_TwoFactorWithBackupCodes_DeviceRunsWithoutCode()
        {
            using (var env = new Env())
            {
                env.CreateUser("sec2026", "Customer-Pass-1");
                var c = new Client(env.Url);
                for (int i = 0; i < 3; i++) Assert.Throws<AgentException>(() => c.Call("POST", "/api/login", new Msg().Set("login", "sec2026").Set("password", "wrong")));
                var locked = Assert.Throws<AgentException>(() => c.Call("POST", "/api/login", new Msg().Set("login", "sec2026").Set("password", "Customer-Pass-1")));
                Assert.Equal("LOCKED", locked.Code);
                env.Admin().Call("POST", "/api/admin/users/sec2026/unlock");

                var app = env.Agent("sec2026", "Customer-Pass-1");
                var session = app.Interactive("Customer-Pass-1", null);
                var setup = session.Call("POST", "/api/totp/enable");
                session.Call("POST", "/api/totp/confirm", new Msg().Set("code", Totp.Code(setup["secret"], DateTime.UtcNow.AddSeconds(-30)))); // owner decision 122 (A): the confirming code is used up - confirmed with the code shown a moment earlier (the step before is accepted), the sign-in below takes the current one
                var noCode = Assert.Throws<AgentException>(() => app.Interactive("Customer-Pass-1", null));
                Assert.Equal("OTP_REQUIRED", noCode.Code);
                Assert.NotNull(app.Interactive("Customer-Pass-1", TestAuth.Fresh("e2e|sec2026", setup["secret"]))   /* H-02: each sign-in takes the next code, as a person does */.Session);
                var backupCode = setup.List("codes")[0]["code"];
                Assert.NotNull(app.Interactive("Customer-Pass-1", backupCode).Session);
                Assert.Throws<AgentException>(() => app.Interactive("Customer-Pass-1", backupCode));   // one-time

                var src = env.Dir("src");
                File.WriteAllText(Path.Combine(src, "a.txt"), "x");
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", TestAuth.Fresh("e2e|sec2026", setup["secret"]))   /* H-02: each sign-in takes the next code, as a person does */, "Customer-Pass-1", new BackupSetInfo { Name = "F", Sources = { src } });
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);   // scheduled run: device token, no code
                var profile = File.ReadAllText(Path.Combine(env.HomeA, "sec2026", "db", "Profile.xml"));
                Assert.DoesNotContain("Customer-Pass-1", profile);
                var logs = string.Join("\n", Directory.GetFiles(Path.Combine(env.SystemHome, "logs"), "*.log", SearchOption.AllDirectories).Select(Atomic.ReadShared));
                Assert.DoesNotContain("Customer-Pass-1", logs);
                Assert.DoesNotContain(setup["secret"], logs);
                Assert.Contains("locked", logs);
            }
        }

        [Fact]
        public void NewUsersGoToTheHomeWithTheLowestQuotaRatio()
        {
            using (var env = new Env("|100|10", "|UNLIMITED|10"))
            {
                env.CreateUser("u1x", "Customer-Pass-1", 6);
                env.CreateUser("u2x", "Customer-Pass-1", 6);
                env.CreateUser("u3x", "Customer-Pass-1", 6);                // A would be 120% > 100% → B
                Assert.True(Directory.Exists(Path.Combine(env.HomeA, "u1x")));
                Assert.True(Directory.Exists(Path.Combine(env.HomeB, "u2x")));
                Assert.True(Directory.Exists(Path.Combine(env.HomeB, "u3x")));
                var homes = env.Admin().Call("GET", "/api/admin/homes").List("homes");
                Assert.Equal("60.0", homes.First(h => h["path"] == Path.GetFullPath(env.HomeA))["ratio"]);
                Assert.Equal("120.0", homes.First(h => h["path"] == Path.GetFullPath(env.HomeB))["ratio"]);
            }
        }

        [Fact]
        public void KeyRecoveryAndRestoreOnANewComputer()
        {
            using (var env = new Env())
            {
                env.CreateUser("key2026", "Customer-Pass-1");
                var app = env.Agent("key2026", "Customer-Pass-1");
                var src = env.Dir("src");
                File.WriteAllText(Path.Combine(src, "contract.docx"), "signed");
                var set = NewSet(app, "Customer-Pass-1", src);
                app.Backup(set.Id);

                // The computer burned: a new agent home, same user. Key from the password (PASSWORD key type).
                var fresh = env.Agent("key2026", "Customer-Pass-1", name: "newpc");
                var session = fresh.Interactive("Customer-Pass-1", null);
                var target = env.Dir("restore");
                var wrong = Assert.Throws<AgentException>(() => fresh.RestoreFor(session, set.Id, "not-the-password"));
                Assert.Equal("WRONG_KEY", wrong.Code);
                var r = fresh.RestoreFor(session, set.Id, "Customer-Pass-1");
                r.Run(null, target, null, false);
                Assert.Equal("signed", File.ReadAllText(Path.Combine(Restored(target, src), "contract.docx")));
                // the new computer keeps the proven key: the set's backups go on from it
                File.WriteAllText(Path.Combine(src, "contract.docx"), "signed v2");
                Assert.Equal("BS_STOP_SUCCESS", fresh.Backup(set.Id).Result);

                // Key recovery by the provider (saved encrypted on the server, read only by the administrator, logged).
                var key = Convert.FromBase64String(env.Admin().Call("GET", "/api/admin/keys/key2026/" + set.Id)["key"]);
                var r2 = env.Agent("key2026", "Customer-Pass-1", name: "thirdpc").RestoreFor(session, set.Id, null, key);
                Assert.Equal(0, r2.Failed);
                Assert.Contains("READ ENCRYPTION KEY", string.Join("\n", Directory.GetFiles(Path.Combine(env.SystemHome, "logs", "Admin")).Select(Atomic.ReadShared)));
            }
        }

        [Fact]
        public void LostLocalIndexIsRebuiltFromTheServerWithoutResendingEverything()
        {
            using (var env = new Env())
            {
                env.CreateUser("idx2026", "Customer-Pass-1");
                var app = env.Agent("idx2026", "Customer-Pass-1");
                var src = env.Dir("src");
                for (int i = 0; i < 10; i++) File.WriteAllText(Path.Combine(src, "f" + i + ".txt"), "c" + i);
                var set = NewSet(app, "Customer-Pass-1", src);
                app.Backup(set.Id);
                File.Delete(Path.Combine(app.Home.SetDir(set.Id), "state.txt"));
                System.Threading.Thread.Sleep(1100);
                var r = app.Backup(set.Id);
                Assert.Equal(0, r.New + r.Updated + r.PermOnly + r.Deleted);
            }
        }

        [Fact]
        public void PreAndPostCommandsRunAndAreLogged_SchedulerCatchesUpMissedRuns()
        {
            using (var env = new Env())
            {
                env.CreateUser("cmd2026", "Customer-Pass-1");
                var app = env.Agent("cmd2026", "Customer-Pass-1");
                var src = env.Dir("src");
                var marker = Path.Combine(env.Root, "post-ran.txt");
                File.WriteAllText(Path.Combine(src, "a.txt"), "x");
                var s = new BackupSetInfo { Name = "F", Sources = { src }, Hour = 22, Minute = 0, MissedDelayMinutes = 0 };
                s.PreCommands.Add("echo pre-output");
                s.PostCommands.Add("echo done > \"" + marker + "\"");
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", s);
                var nowLocal = DateTime.Now.Date.AddHours(23);
                Assert.True(app.Due(set, nowLocal));                         // 22:00 slot not done yet
                var r = app.Backup(set.Id);
                Assert.True(File.Exists(marker));
                Assert.Contains(r.LogLines, l => l.Contains("pre-output"));
                Assert.Contains(r.LogLines, l => l.Contains("Finished running post-commands"));
                Assert.False(app.Due(set, DateTime.Now.AddMinutes(1)));     // done for the current slot
                Assert.Equal(DateTime.Now.Date.AddDays(-1).AddHours(22), AgentApp.LastSlot(set, DateTime.Now.Date.AddHours(8)));
            }
        }

        [Fact]
        public void EveryPointOfADeltaChainRestoresExactly_AndALongChainStartsANewFullCopy()
        {
            using (var env = new Env())
            {
                env.CreateUser("chain2026", "Customer-Pass-1");
                var app = env.Agent("chain2026", "Customer-Pass-1");
                var src = env.Dir("src");
                var path = Path.Combine(src, "db.mdf");
                var data = Rnd(24 * 1024 * 1024, 11);
                File.WriteAllBytes(path, data);
                var s = new BackupSetInfo { Name = "Chain", Sources = { src }, MinDeltaFileSize = 1024 * 1024, MaxDeltaNo = 4 };
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", s);
                var versions = new List<byte[]> { data };
                app.Backup(set.Id);
                for (int v = 1; v <= 5; v++)
                {
                    System.Threading.Thread.Sleep(1100);
                    var cur = versions.Last();
                    int at = 4 * 1024 * 1024 * v;
                    var next = cur.Take(at).Concat(Rnd(2048, 100 + v)).Concat(cur.Skip(at + 512)).ToArray();   // insert + overwrite
                    File.WriteAllBytes(path, next);
                    versions.Add(next);
                    Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);
                }
                var current = Path.Combine(env.HomeA, "chain2026", "files", set.Id, "Current");
                // Deltas .001 .. .003, then the 4th change starts a new full copy (MAX_DELTA_NO = 4); the old chain went to the retention area.
                Assert.Single(Directory.GetFiles(current, "*.000", SearchOption.AllDirectories));
                Assert.True(Directory.GetFiles(Path.Combine(env.HomeA, "chain2026", "files", set.Id), "*.003", SearchOption.AllDirectories).Length >= 1);
                var session = app.Interactive("Customer-Pass-1", null);
                var r = app.RestoreFor(session, set.Id);
                var points = r.Points();
                Assert.Equal(6, points.Count);
                for (int i = 0; i < points.Count; i++)
                {
                    var target = env.Dir("restore" + i);
                    var ri = app.RestoreFor(session, set.Id);
                    ri.Run(points[i], target, null, false);
                    Assert.Equal(0, ri.Failed);
                    Assert.True(versions[i].SequenceEqual(File.ReadAllBytes(Path.Combine(Restored(target, src), "db.mdf"))), "point " + i);
                }
            }
        }

        [Fact]
        public void DifferentialChain_EveryPointRestoresExactly_AndEachDeltaCarriesAllChangesSinceTheFull()
        {
            using (var env = new Env())
            {
                env.CreateUser("diff2026", "Customer-Pass-1");
                var app = env.Agent("diff2026", "Customer-Pass-1");
                var src = env.Dir("src");
                var path = Path.Combine(src, "db.mdf");
                var data = Rnd(24 * 1024 * 1024, 21);
                File.WriteAllBytes(path, data);
                var s = new BackupSetInfo { Name = "Diff", Sources = { src }, MinDeltaFileSize = 1024 * 1024, MaxDeltaNo = 10, DeltaType = "D" };
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", s);
                Assert.Equal("D", app.Sets().Single(x => x.Id == set.Id).DeltaType);    // kept in the profile
                var versions = new List<byte[]> { data };
                var sent = new List<long>();
                app.Backup(set.Id);
                for (int v = 1; v <= 4; v++)
                {
                    System.Threading.Thread.Sleep(1100);
                    var cur = versions.Last();
                    int at = 4 * 1024 * 1024 * v;
                    var next = cur.Take(at).Concat(Rnd(300000, 200 + v)).Concat(cur.Skip(at + 300000)).ToArray();   // a different region each time
                    File.WriteAllBytes(path, next);
                    versions.Add(next);
                    var r = app.Backup(set.Id);
                    Assert.Equal("BS_STOP_SUCCESS", r.Result);
                    sent.Add(r.BytesSent);
                }
                // differential: each delta holds every region changed since the full copy — it grows; incremental would stay flat
                Assert.True(sent[3] > sent[0] * 2, string.Join(",", sent));
                var session = app.Interactive("Customer-Pass-1", null);
                var points = app.RestoreFor(session, set.Id).Points();
                Assert.Equal(5, points.Count);
                for (int i = 0; i < points.Count; i++)
                {
                    var target = env.Dir("restore" + i);
                    var ri = app.RestoreFor(session, set.Id);
                    ri.Run(points[i], target, null, false);
                    Assert.Equal(0, ri.Failed);
                    Assert.True(versions[i].SequenceEqual(File.ReadAllBytes(Path.Combine(Restored(target, src), "db.mdf"))), "point " + i);
                }
            }
        }

        [Fact]
        public void TruncatedUploadIsRejectedAndNeverCommitted()
        {
            using (var env = new Env())
            {
                env.CreateUser("trunc2026", "Customer-Pass-1");
                var app = env.Agent("trunc2026", "Customer-Pass-1");
                var src = env.Dir("src");
                File.WriteAllText(Path.Combine(src, "a.txt"), "x");
                var set = NewSet(app, "Customer-Pass-1", src);
                var store = new SetStore(Path.Combine(env.HomeA, "trunc2026"), set.Id);
                var job = store.BeginJob(DateTime.UtcNow);
                var rel = NameCipher.RelPath(KeySet.Random(), @"C:.txt");
                var ex = Assert.Throws<ApiException>(() => store.StageObject(job, new ChkRecord { Rel = rel, Seq = 0, Kind = "F", EncPath = "x" },
                    new MemoryStream(Encoding.ASCII.GetBytes("OBK1 half an object")), 1 << 20));
                Assert.Equal("BAD_OBJECT", ex.Code);
                store.Commit(job, new Msg());
                Assert.Empty(store.FilesAt(null).List("files"));
            }
        }

        [Fact]
        public void AnOfflineSourceIsNotTreatedAsDeleted()
        {
            using (var env = new Env())
            {
                env.CreateUser("offline2026", "Customer-Pass-1");
                var app = env.Agent("offline2026", "Customer-Pass-1");
                var share = env.Dir("share");
                File.WriteAllText(Path.Combine(share, "important.docx"), "data");
                var set = NewSet(app, "Customer-Pass-1", share);
                app.Backup(set.Id);
                Directory.Move(share, share + "-offline");               // the drive / share is gone for now
                System.Threading.Thread.Sleep(1100);
                var r = app.Backup(set.Id);
                Assert.Equal(0, r.Deleted);
                // R1 (owner's decision on GPT finding 4): a source that is entirely unreadable is a FAILED run — it used to be
                // "completed with warnings". What this test protects stays: its files are not treated as deleted, and
                // nothing is sent again when it is back.
                Assert.Equal("BS_STOP_BY_SYSTEM_ERROR", r.Result);
                Directory.Move(share + "-offline", share);
                System.Threading.Thread.Sleep(1100);
                Assert.Equal(0, app.Backup(set.Id).Updated);               // back online: nothing to send again
            }
        }

        [Fact]
        public void Net40AgentUnderMonoBacksUpAndRestores()
        {
            var mono = new[] { "/usr/bin/mono", "/usr/local/bin/mono" }.FirstOrDefault(File.Exists);
            var exe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Agent", "bin", "Debug", "net40", "OnlineBackup.Agent.exe"));
            if (mono == null || !File.Exists(exe)) throw NotTested.Because("needs mono and the .NET 4.0 agent build");   // the .NET 4.0 build is checked where mono exists
            using (var env = new Env())
            {
                env.CreateUser("mono2026", "Customer-Pass-1");
                var home = Path.Combine(env.Root, "monoagent");
                var src = env.Dir("src");
                File.WriteAllText(Path.Combine(src, "doc.txt"), "from the .NET 4.0 agent");
                File.WriteAllBytes(Path.Combine(src, "big.bin"), Rnd(2 * 1024 * 1024, 9));
                Func<string, string> run = args =>
                {
                    var p = Process.Start(new ProcessStartInfo(mono, "\"" + exe + "\" " + args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
                    var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    Assert.True(p.ExitCode == 0, args.Split(' ')[0] + " failed: " + o);
                    return o.Trim();
                };
                run("register --home \"" + home + "\" --server " + env.Url + " --login mono2026 --password Customer-Pass-1 --computer MONOPC");
                var setId = run("addset --home \"" + home + "\" --password Customer-Pass-1 --name Docs --source \"" + src + "\"").Split('\n').Last().Trim();
                Assert.StartsWith("BS_STOP_SUCCESS", run("backup --home \"" + home + "\" --set " + setId));
                var target = Path.Combine(env.Root, "monorestore");
                Assert.Contains("restored=2", run("restore --home \"" + home + "\" --set " + setId + " --password Customer-Pass-1 --target \"" + target + "\""));
                AssertSameTree(src, Restored(target, src));
            }
        }
    }
}
