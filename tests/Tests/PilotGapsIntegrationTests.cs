using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// Pilot release blockers — the INTEGRATION layers that were missing (real server in this process, real HTTP, real
    /// files, the real agent). Contracts in tests/QA/specs.py; the oracle is the SHA-256 of the source files (computed
    /// here), the server's own files on disk and its history of runs (/api/admin/users/{login}/sets/{id}/runs).
    ///   ST-06 the server's disk answers "no space left" (ENOSPC from the kernel: the object's file is /dev/full — no
    ///         root needed) in the middle of a backup, twice: each run fails with the server's reason (disk full) on the
    ///         computer, the server keeps no open run (no lock) and no part of it, one alert reaches the administrators,
    ///         the stored point is byte-identical and restores identical; the run after the space is back completes and
    ///         every point restores identical
    ///   IN-06 a reboot in the middle of a backup: the agent's process state is dropped at the moment the server has
    ///         stored the first object (the line goes dead, the agent's folder is put back exactly as it was at that
    ///         moment) and a new agent starts from its files with the service loop: while the network is not up nothing
    ///         is reported and the note is kept; then the dead run is recorded once on the server, the next backup
    ///         completes, both points restore identical, and a later round reports nothing more
    ///   CO-01 the shared formats between the real server and agent: unknown attributes of the profile survive a change
    ///         made by the customer; a reader of the profile file and of /api/profile never sees a half file while the
    ///         server writes it again and again; a write that died between its temporary file and the rename (on the
    ///         server and on the computer) leaves the old file in force and the next write and backup work; the run id
    ///         and every log line the agent wrote are exactly what the server keeps
    /// </summary>
    public class PilotGapsIntegrationTests
    {
        readonly Xunit.Abstractions.ITestOutputHelper output;
        public PilotGapsIntegrationTests(Xunit.Abstractions.ITestOutputHelper output) { this.output = output; }
        readonly System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        void Step(string what) { output.WriteLine(watch.Elapsed.TotalSeconds.ToString("0.0") + " s  " + what); }
        const string Pw = "Customer-Pass-1";
        static string Sha(byte[] b) { using (var h = SHA256.Create()) return Bytes.Hex(h.ComputeHash(b)); }
        static string ShaFile(string f) { return Sha(File.ReadAllBytes(f)); }
        static byte[] Rnd(int n, int seed) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }
        static Dictionary<string, string> Tree(string dir)
        {
            return Directory.Exists(dir) ? Directory.GetFiles(dir, "*", SearchOption.AllDirectories).ToDictionary(f => Path.GetRelativePath(dir, f).Replace('\\', '/'), ShaFile) : new Dictionary<string, string>();
        }
        static void AssertSame(Dictionary<string, string> want, Dictionary<string, string> got, string what)
        {
            Assert.True(want.OrderBy(k => k.Key, StringComparer.Ordinal).SequenceEqual(got.OrderBy(k => k.Key, StringComparer.Ordinal)),
                what + ": got [" + string.Join(", ", got.OrderBy(k => k.Key).Select(k => k.Key + "=" + k.Value.Substring(0, 8))) + "] want [" + string.Join(", ", want.OrderBy(k => k.Key).Select(k => k.Key + "=" + k.Value.Substring(0, 8))) + "]");
        }
        static Dictionary<string, string> RestoreTree(Env env, AgentApp app, string setId, string point, string source, string name)
        {
            var target = env.Dir(name);
            var r = app.RestoreFor(app.Interactive(Pw, null), setId);
            r.Run(point, target, null, false);
            Assert.Equal(0, r.Failed);
            return Tree(Path.Combine(target, Env.Rel(source)));
        }
        static string UserDir(Env env, string login) { return Directory.GetDirectories(env.HomeA, login, SearchOption.AllDirectories).First(); }
        static List<Msg> Runs(Client admin, string login, string setId) { return admin.Call("GET", "/api/admin/users/" + login + "/sets/" + setId + "/runs").List("runs"); }
        static void CopyDir(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
            foreach (var d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
        }

        // ================================================================== ST-06

        /// <summary>
        /// ST-06 failure + recovery + integrity (INTEGRATION): the disk of the server is full under the new object of a
        /// backup (ENOSPC from the kernel), in two runs one after the other. Each run ends BS_STOP_BY_SYSTEM_ERROR with the
        /// server's reason ("disk is full") in its log, the computer's index does not move, the server keeps no open run
        /// and no closed-run leftovers (no lock, the space given back), the server's history shows the run as failed, the
        /// stored point is byte-identical on the server's disk and restores identical, and the administrators get one
        /// alert (not one per run). Once the space is back the next run completes; both points restore identical.
        /// </summary>
        [Fact]
        public void ST06_TheServerDiskIsFullMidBackup_TheRunFailsWithTheReason_NoLockNoLeftovers_OneAlert_EarlierPointIntact_TheNextRunCompletes()
        {
            if (!File.Exists("/dev/full")) throw NotTested.Because("a full server disk without root needs /dev/full (Linux) to make the kernel answer ENOSPC");
            using (var smtp = new FakeSmtp())
            using (var env = new Env())
            {
                var admin = env.Admin();
                admin.Call("POST", "/api/admin/settings", new Msg().Set("smtpSet", "1").Set("senderEmail", "backup@example.invalid").Set("contactsSet", "1")
                    .Add("smtp", new Msg().Set("host", "127.0.0.1").Set("port", smtp.Port).Set("security", "NONE")).Add("contacts", new Msg().Set("name", "Ops").Set("email", "ops@example.invalid")));
                env.CreateUser("full2026", Pw);
                var app = env.Agent("full2026", Pw);
                var src = env.Dir("src");
                File.WriteAllBytes(Path.Combine(src, "ledger.xlsx"), Rnd(300000, 1));
                File.WriteAllText(Path.Combine(src, "notes.txt"), "version 1");
                var set = app.CreateSet(app.Interactive(Pw, null), Pw, new BackupSetInfo { Name = "Files", Sources = { src } });
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);
                var v1 = Tree(src);
                var store = Path.Combine(UserDir(env, "full2026"), "files", set.Id);
                Assert.True(Directory.Exists(Path.Combine(store, SetStore.Current)), "the set's store is at " + store);
                var storedBefore = Tree(Path.Combine(store, SetStore.Current));
                var stateFile = Path.Combine(app.Home.SetDir(set.Id), "state.txt");
                var indexBefore = ShaFile(stateFile);

                // the new big file lands where the disk is full: its object's file on the server is /dev/full
                var big = Path.Combine(src, "scan.pdf"); File.WriteAllBytes(big, Rnd(400000, 2));
                File.WriteAllText(Path.Combine(src, "notes.txt"), "version 2, longer"); File.SetLastWriteTimeUtc(Path.Combine(src, "notes.txt"), DateTime.UtcNow.AddMinutes(1));
                var v2 = Tree(src);
                var rel = NameCipher.RelPath(app.Key(app.Sets().Single(s => s.Id == set.Id)), big);
                bool full = true; var planted = new HashSet<string>();
                app.RunClock = () =>
                {
                    var jobs = Path.Combine(store, "jobs");
                    if (full && Directory.Exists(jobs))
                        foreach (var jd in Directory.GetDirectories(jobs))
                            if (planted.Add(jd))
                                for (int seq = 0; seq < 3; seq++)
                                {
                                    var part = Path.Combine(jd, "new", ChkRecord.ObjectName(rel, seq).Replace('/', Path.DirectorySeparatorChar)) + ".part";
                                    Directory.CreateDirectory(Path.GetDirectoryName(part));
                                    File.CreateSymbolicLink(part, "/dev/full");
                                }
                    return SystemClock.UtcNow;
                };

                var failed = new List<BackupRun>();
                for (int i = 0; i < 2; i++)
                {
                    var r = app.Backup(set.Id);
                    failed.Add(r);
                    Assert.True(r.Result == "BS_STOP_BY_SYSTEM_ERROR", r.Result + "\n" + string.Join("\n", r.LogLines));
                    Assert.Contains(r.LogLines, l => AhsayLog.Fields(l)[1] == "err" && l.Contains("disk is full"));
                    Assert.Equal(indexBefore, ShaFile(stateFile));                                                  // the local index did not move
                    Assert.False(File.Exists(Path.Combine(app.Home.SetDir(set.Id), "open-run.txt")), "the end of the run reached the server");
                    var open = Directory.Exists(Path.Combine(store, "jobs")) ? Directory.GetDirectories(Path.Combine(store, "jobs")) : new string[0];
                    Assert.True(open.Length == 0, "the server keeps an open run (a lock) after the failure: " + string.Join(", ", open));
                    var closed = Path.Combine(store, "closed-runs");
                    Assert.True(!Directory.Exists(closed) || Directory.GetFileSystemEntries(closed).Length == 0, "the failed run's data stayed on the server");
                    AssertSame(storedBefore, Tree(Path.Combine(store, SetStore.Current)), "the stored point on the server's disk");
                    Thread.Sleep(1100);   // a distinct run id (one per second)
                }
                Assert.Equal(2, planted.Count);
                var hist = Runs(admin, "full2026", set.Id);
                foreach (var r in failed) Assert.Equal("BS_STOP_BY_SYSTEM_ERROR", hist.Single(h => h["job"] == r.Job)["result"]);
                var points = app.RestoreFor(app.Interactive(Pw, null), set.Id).Points();
                Assert.Single(points);
                AssertSame(v1, RestoreTree(env, app, set.Id, null, src, "r-during"), "the point before the full disk, restored while the disk is full");
                var alerts = Directory.GetFiles(Path.Combine(env.SystemHome, "logs", "Email")).SelectMany(File.ReadAllLines).Where(l => l.Contains("disk is full")).ToList();
                Assert.True(alerts.Count == 1 && alerts[0].Contains("sent via"), "one alert mail to the administrators, got: " + string.Join(" | ", alerts));

                // the provider frees space
                full = false;
                var ok = app.Backup(set.Id);
                Assert.True(ok.Result == "BS_STOP_SUCCESS", ok.Result + "\n" + string.Join("\n", ok.LogLines));
                points = app.RestoreFor(app.Interactive(Pw, null), set.Id).Points();
                Assert.Equal(2, points.Count);
                AssertSame(v2, RestoreTree(env, app, set.Id, null, src, "r-after"), "the newest point");
                AssertSame(v1, RestoreTree(env, app, set.Id, points[0], src, "r-first"), "the point from before the full disk");
            }
        }

        // ================================================================== IN-06

        /// <summary>A line between the computer and the server that can go dead (the computer's power goes) and come back.
        /// The server answers only its own host name and port: "Host: localhost:proxy" is rewritten to the server's.</summary>
        sealed class Line : IDisposable
        {
            readonly TcpListener l;
            readonly int target; volatile bool down;
            readonly List<TcpClient> open = new List<TcpClient>();
            public int Port { get { return ((IPEndPoint)l.LocalEndpoint).Port; } }
            public Line(int targetPort)
            {
                target = targetPort;
                for (int i = 0; ; i++)
                {
                    l = new TcpListener(IPAddress.Loopback, 0); l.Start();
                    if (Port.ToString().Length == target.ToString().Length) break;   // the rewrite keeps the length
                    l.Stop(); if (i > 50) throw new InvalidOperationException("no port of the same length");
                }
                new Thread(() =>
                {
                    while (true)
                    {
                        TcpClient a; try { a = l.AcceptTcpClient(); } catch (Exception) { return; }
                        if (down) { try { a.Client.LingerState = new LingerOption(true, 0); a.Close(); } catch (Exception) { } continue; }
                        var b = new TcpClient();
                        try { b.Connect("localhost", target); } catch (Exception) { try { a.Close(); b.Close(); } catch (Exception) { } continue; }
                        lock (open) { open.Add(a); open.Add(b); }
                        Pump(a, b, true); Pump(b, a, false);
                    }
                }) { IsBackground = true }.Start();
            }
            public void Down()
            {
                down = true;
                lock (open) { foreach (var c in open) try { c.Client.LingerState = new LingerOption(true, 0); c.Close(); } catch (Exception) { } open.Clear(); }
            }
            public void Up() { down = false; }
            void Pump(TcpClient from, TcpClient to, bool upward)
            {
                new Thread(() =>
                {
                    var buf = new byte[16384]; int n;
                    try
                    {
                        var fs = from.GetStream(); var ts = to.GetStream();
                        while (!down && (n = fs.Read(buf, 0, buf.Length)) > 0)
                        {
                            if (down) break;
                            if (upward) Rewrite(buf, n);
                            ts.Write(buf, 0, n);
                        }
                    }
                    catch (Exception) { }
                    try { to.Client.Shutdown(SocketShutdown.Send); } catch (Exception) { }
                }) { IsBackground = true }.Start();
            }
            void Rewrite(byte[] buf, int n)
            {
                var from = Encoding.ASCII.GetBytes("localhost:" + Port); var to = Encoding.ASCII.GetBytes("localhost:" + target);
                for (int i = 0; i + from.Length <= n; i++)
                {
                    int k = 0; while (k < from.Length && buf[i + k] == from[k]) k++;
                    if (k == from.Length) Buffer.BlockCopy(to, 0, buf, i, to.Length);
                }
            }
            public void Dispose() { Down(); l.Stop(); }
        }

        /// <summary>One round of the service loop (what the service does after the computer starts); its end is seen in what it says.</summary>
        static List<string> OneRound(AgentApp app, Func<string, bool> roundOver)
        {
            var said = new List<string>();
            using (var stop = new CancellationTokenSource())
            {
                stop.CancelAfter(TimeSpan.FromSeconds(100));   // a guard only: a round that never ends fails below
                app.ServiceLoop(stop.Token, m => { lock (said) said.Add(m); if (roundOver(m)) stop.Cancel(); });
            }
            return said;
        }
        static bool RunEnded(string m) { return m.Contains(": BS_") || m.Contains("cannot run") || m.StartsWith("error:", StringComparison.Ordinal); }

        /// <summary>
        /// IN-06 failure + recovery + integrity (INTEGRATION): a computer reboots in the middle of a backup. The server has
        /// stored the first object of the run, then hears nothing more; the agent's folder is exactly as it was at that
        /// moment (its note of the open run, its index of the last point). A new agent process starts from these files
        /// with the service loop. While the network is not up, it reports nothing and keeps the note. Then the server
        /// records the dead run exactly once as failed (and releases it: the next backup is not "busy"), the pending
        /// backup completes, the newest point restores identical to the source and the point before the reboot restores
        /// identical to what it was (SHA-256); the dead run is no restore point; a later round reports nothing more.
        /// </summary>
        [Fact]
        public void IN06_ARebootMidBackup_WithTheRealServer_TheDeadRunIsRecordedOnce_TheNextBackupCompletes_EveryPointRestoresIdentical()
        {
            var delay = BackupRun.UploadRetryDelay;
            try
            {
                BackupRun.UploadRetryDelay = a => 50;
                using (var env = new Env())
                using (var line = new Line(new Uri(env.Url).Port))
                {
                    var admin = env.Admin();   // one session: each new sign-in waits for a fresh two-step code
                    env.CreateUser("boot2026", Pw);
                    var app = env.Agent("boot2026", Pw);
                    app.Home.SaveRegistration("http://localhost:" + line.Port + "/", "boot2026", app.Home.Computer, app.Home.DeviceToken);   // through the line
                    var src = env.Dir("src");
                    File.WriteAllText(Path.Combine(src, "a.txt"), "alpha"); File.WriteAllText(Path.Combine(src, "b.txt"), "beta");
                    File.WriteAllBytes(Path.Combine(src, "c.bin"), Rnd(120000, 3));
                    var set = app.CreateSet(app.Interactive(Pw, null), Pw, new BackupSetInfo { Name = "Office", Sources = { src }, Days = "-------" });
                    Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);
                    var v1 = Tree(src);
                    var setDir = app.Home.SetDir(set.Id);
                    File.WriteAllText(Path.Combine(app.Home.Dir, "folders-sent.txt"), RunId.UnixMs(DateTime.UtcNow).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    File.WriteAllText(Path.Combine(setDir, "last-restore-test.txt"), RunId.From(DateTime.UtcNow.AddDays(1)));
                    var stateBefore = ShaFile(Path.Combine(setDir, "state.txt"));
                    Thread.Sleep(1100);

                    File.WriteAllText(Path.Combine(src, "a.txt"), "alpha, changed before the reboot"); File.SetLastWriteTimeUtc(Path.Combine(src, "a.txt"), DateTime.UtcNow.AddMinutes(1));
                    File.WriteAllBytes(Path.Combine(src, "d.bin"), Rnd(150000, 4));
                    File.WriteAllText(Path.Combine(src, "e.txt"), "epsilon, new before the reboot");
                    File.Delete(Path.Combine(src, "b.txt"));
                    var v2 = Tree(src);

                    // the power goes when the server has stored the first object of this run: the line is dead and the
                    // agent's folder is what it was at that moment
                    var store = Path.Combine(UserDir(env, "boot2026"), "files", set.Id);
                    var atPowerLoss = Path.Combine(env.Root, "agent-disk-at-power-loss");
                    string dying = null;
                    app.RunClock = () =>
                    {
                        var jobs = Path.Combine(store, "jobs");
                        if (dying == null && Directory.Exists(jobs))
                            foreach (var jd in Directory.GetDirectories(jobs))
                            {
                                var up = Path.Combine(jd, "uploads.txt");
                                if (File.Exists(up) && File.ReadAllLines(up).Any(x => x.Trim().Length > 0))
                                {
                                    dying = Path.GetFileName(jd);
                                    line.Down();
                                    CopyDir(app.Home.Dir, atPowerLoss);
                                }
                            }
                        return SystemClock.UtcNow;
                    };
                    Step("the run that dies begins");
                    try { app.Backup(set.Id); } catch (AgentException) { }   // whatever the dying process still did is lost with it
                    Assert.NotNull(dying);
                    Directory.Delete(app.Home.Dir, true); CopyDir(atPowerLoss, app.Home.Dir);    // the disk as it was when the power went
                    var marker = Path.Combine(setDir, "open-run.txt");
                    Assert.True(File.Exists(marker), "the dead run left its note");
                    Assert.Equal(dying, File.ReadAllText(marker).Split('\t')[0]);
                    Assert.Equal(stateBefore, ShaFile(Path.Combine(setDir, "state.txt")));
                    Assert.Single(Directory.GetDirectories(Path.Combine(store, "jobs")));          // the server still holds the run open
                    Assert.DoesNotContain(Runs(admin, "boot2026", set.Id), h => h["job"] == dying);

                    // the computer starts again: the network is not up yet at the first round
                    Step("the dying run returned");
                    var after = new AgentApp(app.Home.Dir);
                    var said1 = OneRound(after, m => m.StartsWith("waiting:", StringComparison.Ordinal) || RunEnded(m));
                    Assert.Contains(said1, m => m.StartsWith("waiting:", StringComparison.Ordinal));
                    Assert.True(File.Exists(marker), "the note of the dead run is kept until the server hears it");
                    Assert.DoesNotContain(Runs(admin, "boot2026", set.Id), h => h["job"] == dying);

                    // the network is up; a backup is due ("back up now" pressed while the computer was off)
                    Step("round 1 over");
                    line.Up();
                    admin.Call("POST", "/api/admin/users/boot2026/sets/" + set.Id + "/run");
                    var said2 = OneRound(after, RunEnded);
                    string all2 = string.Join(" | ", said2);
                    Assert.True(said2.Any(m => m.Contains("the previous backup was interrupted")), all2);
                    Assert.True(said2.Contains("Office: BS_STOP_SUCCESS"), all2);
                    Assert.False(File.Exists(marker));
                    Step("round 2 over");
                    var hist = Runs(admin, "boot2026", set.Id);
                    var dead = Assert.Single(hist, h => h["job"] == dying);
                    Assert.Equal("BS_STOP_BY_SYSTEM_ERROR", dead["result"]);
                    Assert.Equal("bad", dead["status"]);
                    var newest = hist.Where(h => h["job"] != dying).OrderByDescending(h => h.Long("time")).First();
                    Assert.Equal("BS_STOP_SUCCESS", newest["result"]);
                    var points = after.RestoreFor(after.Interactive(Pw, null), set.Id).Points();
                    Assert.Equal(2, points.Count);
                    Assert.DoesNotContain(dying, points);
                    AssertSame(v2, RestoreTree(env, after, set.Id, null, src, "r-newest"), "the newest point after the reboot");
                    AssertSame(v1, RestoreTree(env, after, set.Id, points[0], src, "r-before"), "the point from before the reboot");
                    Assert.False(Directory.Exists(Path.Combine(store, "jobs")) && Directory.GetDirectories(Path.Combine(store, "jobs")).Length > 0, "no run left open on the server");

                    // once only: a later round with another backup reports nothing more
                    Thread.Sleep(1100);
                    admin.Call("POST", "/api/admin/users/boot2026/sets/" + set.Id + "/run");
                    var said3 = OneRound(after, RunEnded);
                    Assert.True(said3.Contains("Office: BS_STOP_SUCCESS"), string.Join(" | ", said3));
                    Assert.DoesNotContain(said3, m => m.Contains("interrupted"));
                    Assert.Single(Runs(admin, "boot2026", set.Id), h => h["job"] == dying);
                    Assert.Equal(1, Runs(admin, "boot2026", set.Id).Count(h => h["result"] == "BS_STOP_BY_SYSTEM_ERROR"));
                }
            }
            finally { BackupRun.UploadRetryDelay = delay; }
        }

        /// <summary>
        /// IN-06 happy (INTEGRATION): the computer is restarted while idle. A new agent process starts from its files with
        /// the service loop; a backup asked while the computer was off ("back up now") runs and completes through the real
        /// server, nothing is reported as interrupted, the server's history holds no failed run, and the point restores
        /// identical (SHA-256).
        /// </summary>
        [Fact]
        public void IN06_AnIdleReboot_TheServiceLoopStartsFromItsFiles_ThePendingBackupCompletes_NothingInterrupted_RestoresIdentical()
        {
            using (var env = new Env())
            {
                var admin = env.Admin();
                env.CreateUser("idle2026", Pw);
                var app = env.Agent("idle2026", Pw);
                var src = env.Dir("src");
                File.WriteAllText(Path.Combine(src, "a.txt"), "alpha"); File.WriteAllBytes(Path.Combine(src, "b.bin"), Rnd(90000, 6));
                var set = app.CreateSet(app.Interactive(Pw, null), Pw, new BackupSetInfo { Name = "Office", Sources = { src }, Days = "-------" });
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);
                File.WriteAllText(Path.Combine(app.Home.Dir, "folders-sent.txt"), RunId.UnixMs(DateTime.UtcNow).ToString(System.Globalization.CultureInfo.InvariantCulture));
                File.WriteAllText(Path.Combine(app.Home.SetDir(set.Id), "last-restore-test.txt"), RunId.From(DateTime.UtcNow.AddDays(1)));
                File.WriteAllText(Path.Combine(src, "a.txt"), "alpha, changed while the computer was off"); File.SetLastWriteTimeUtc(Path.Combine(src, "a.txt"), DateTime.UtcNow.AddMinutes(1));
                File.WriteAllText(Path.Combine(src, "c.txt"), "gamma, new");
                var want = Tree(src);
                Thread.Sleep(1100);
                admin.Call("POST", "/api/admin/users/idle2026/sets/" + set.Id + "/run");

                var after = new AgentApp(app.Home.Dir);   // the computer started again: a new process, the same files
                var said = OneRound(after, RunEnded);
                Assert.True(said.Contains("Office: BS_STOP_SUCCESS"), string.Join(" | ", said));
                Assert.DoesNotContain(said, m => m.Contains("interrupted"));
                var hist = Runs(admin, "idle2026", set.Id);
                Assert.Equal(2, hist.Count);
                Assert.All(hist, h => Assert.Equal("BS_STOP_SUCCESS", h["result"]));
                Assert.Equal(2, after.RestoreFor(after.Interactive(Pw, null), set.Id).Points().Count);
                AssertSame(want, RestoreTree(env, after, set.Id, null, src, "r-idle"), "the point made after the restart");
            }
        }

        // ================================================================== CO-01

        /// <summary>
        /// CO-01 happy + recovery + integrity (INTEGRATION): the formats the server and the agent share, through the real
        /// server. (1) Unknown attributes in the customer's Profile.xml (a newer version wrote them: on the customer, on the
        /// set, an unknown element in the set) survive 25 changes the customer makes through the server. (2) Meanwhile a
        /// reader of Profile.xml (opened the way every reader of the product opens it) and the agent asking /api/profile
        /// never see a half or missing file: every read parses and holds the unknown attribute. (3) A write that died
        /// between its temporary file and the rename — on the server (Profile.xml) and on the computer (its local index)
        /// — leaves the last whole file in force; the next change and the next backup work, and the point restores
        /// identical. (4) The run id the server gave is a valid run id and names the restore point; the server's log of the
        /// run holds exactly the agent's log lines, in order; the server's history names the run with the agent's result.
        /// </summary>
        [Fact]
        public void CO01_SharedFormats_UnknownAttributesKept_NoHalfFileSeenWhileWritten_ACrashBeforeTheRenameLeavesTheOldFile_LogAndRunIdExact()
        {
            using (var env = new Env())
            {
                var admin = env.Admin();
                env.CreateUser("fmt2026", Pw);
                var app = env.Agent("fmt2026", Pw);
                var src = env.Dir("src");
                File.WriteAllBytes(Path.Combine(src, "report.docx"), Rnd(90000, 5)); File.WriteAllText(Path.Combine(src, "list.txt"), "one\r\ntwo\r\n");
                var session = app.Interactive(Pw, null);
                var set = app.CreateSet(session, Pw, new BackupSetInfo { Name = "Docs", Sources = { src } });

                // (1) a newer version's attributes in the stored profile
                var profPath = Path.Combine(UserDir(env, "fmt2026"), "db", "Profile.xml");
                var doc = XDocument.Parse(Atomic.ReadAllText(profPath));
                var userEl = doc.Root.Element("USER") ?? doc.Root;
                var setEl = doc.Descendants("BACKUP_SET").Single(e => (string)e.Attribute("ID") == set.Id);
                userEl.SetAttributeValue("FUTURE_USER_OPTION", "u-2027");
                setEl.SetAttributeValue("FUTURE_SET_OPTION", "s-2027");
                setEl.Add(new XElement("FUTURE_ELEMENT", new XAttribute("X", "42")));
                Atomic.WriteText(profPath, doc.ToString());

                // (2) readers while the server writes the profile again and again
                var problems = new List<string>(); int fileReads = 0, apiReads = 0; var stop = false;
                var fileReader = new Thread(() =>
                {
                    while (!Volatile.Read(ref stop))
                        try
                        {
                            string text; using (var fs = new FileStream(profPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) using (var r = new StreamReader(fs)) text = r.ReadToEnd();
                            var d = XDocument.Parse(text);
                            if (!d.Descendants("BACKUP_SET").Any(e => (string)e.Attribute("FUTURE_SET_OPTION") == "s-2027")) lock (problems) problems.Add("file: unknown attribute lost");
                            Interlocked.Increment(ref fileReads);
                        }
                        catch (Exception e) when (e is IOException || e is System.Xml.XmlException) { lock (problems) problems.Add("file: " + e.GetType().Name + " " + e.Message); }
                }) { IsBackground = true };
                var apiReader = new Thread(() =>
                {
                    while (!Volatile.Read(ref stop))
                        try
                        {
                            var p = app.Profile();
                            if (p.Sets.All(s => s.Id != set.Id)) lock (problems) problems.Add("api: the set is missing");
                            Interlocked.Increment(ref apiReads);
                        }
                        catch (Exception e) { lock (problems) problems.Add("api: " + e.GetType().Name + " " + e.Message); }
                }) { IsBackground = true };
                fileReader.Start(); apiReader.Start();
                try
                {
                    for (int i = 1; i <= 25; i++)
                    {
                        var cur = app.Sets().Single(s => s.Id == set.Id); cur.Name = "Docs " + i;
                        session.Call("POST", "/api/sets/" + set.Id + "/settings", new Msg().Set("set", cur.ToXml().ToString(SaveOptions.DisableFormatting)));
                    }
                }
                finally { Volatile.Write(ref stop, true); fileReader.Join(TimeSpan.FromSeconds(30)); apiReader.Join(TimeSpan.FromSeconds(30)); }
                Assert.True(problems.Count == 0, string.Join("\n", problems.Take(10)));
                Assert.True(fileReads > 0 && apiReads > 0, "the readers ran (file " + fileReads + ", api " + apiReads + ")");
                var stored = XDocument.Parse(Atomic.ReadAllText(profPath));
                var storedSet = stored.Descendants("BACKUP_SET").Single(e => (string)e.Attribute("ID") == set.Id);
                Assert.Equal("Docs 25", app.Sets().Single(s => s.Id == set.Id).Name);
                Assert.Equal("s-2027", (string)storedSet.Attribute("FUTURE_SET_OPTION"));
                Assert.Equal("42", (string)storedSet.Element("FUTURE_ELEMENT")?.Attribute("X"));
                Assert.Equal("u-2027", (string)stored.Descendants().Concat(new[] { stored.Root }).First(e => e.Attribute("FUTURE_USER_OPTION") != null).Attribute("FUTURE_USER_OPTION"));

                // (3) a write that died between the temporary file and the rename — on the server ...
                var whole = Atomic.ReadAllText(profPath);
                var newer = whole.Replace("Docs 25", "Docs NEVER-RENAMED");
                File.WriteAllText(profPath + ".tmp" + "1a2b3c4d", newer);                                  // written whole, never renamed
                File.WriteAllText(profPath + ".tmp" + "5e6f7a8b", newer.Substring(0, newer.Length / 2));  // cut in the middle
                Assert.True(Atomic.IsTemp(profPath + ".tmp1a2b3c4d"));
                Assert.Equal("Docs 25", app.Sets().Single(s => s.Id == set.Id).Name);                    // the last whole file is in force
                var c2 = app.Sets().Single(s => s.Id == set.Id); c2.Name = "Docs after the crash";
                session.Call("POST", "/api/sets/" + set.Id + "/settings", new Msg().Set("set", c2.ToXml().ToString(SaveOptions.DisableFormatting)));
                Assert.Equal("Docs after the crash", app.Sets().Single(s => s.Id == set.Id).Name);
                Assert.Equal("s-2027", (string)XDocument.Parse(Atomic.ReadAllText(profPath)).Descendants("BACKUP_SET").Single(e => (string)e.Attribute("ID") == set.Id).Attribute("FUTURE_SET_OPTION"));

                // (4) a backup: the run id and the log lines as the server keeps them
                var r1 = app.Backup(set.Id);
                Assert.Equal("BS_STOP_SUCCESS", r1.Result);
                DateTime started;
                Assert.True(RunId.TryParse(r1.Job, out started), "a run id: " + r1.Job);
                Assert.Equal(r1.Job, RunId.From(RunId.Parse(r1.Job)));
                Assert.True(Math.Abs((DateTime.UtcNow - RunId.Parse(r1.Job)).TotalMinutes) < 10, "the run id is the start time (UTC): " + r1.Job);
                var serverLog = Path.Combine(UserDir(env, "fmt2026"), "logs", set.Id, "Backup", r1.Job + ".log");
                Assert.True(File.Exists(serverLog), serverLog);
                Assert.Equal(r1.LogLines.ToArray(), Atomic.ReadAllLines(serverLog).Where(l => l.Length > 0).ToArray());
                Assert.All(r1.LogLines, l => Assert.True(AhsayLog.Fields(l).Length > 1 && new[] { "start", "info", "warn", "err", "new", "upd", "del", "perm", "end" }.Contains(AhsayLog.Fields(l)[1]), "log line: " + l));
                Assert.Equal("BS_STOP_SUCCESS", AhsayLog.Fields(r1.LogLines.Last(l => AhsayLog.Fields(l)[1] == "end"))[4]);
                var h1 = Assert.Single(Runs(admin, "fmt2026", set.Id), h => h["job"] == r1.Job);
                Assert.Equal(r1.Result, h1["result"]);
                Assert.Contains(r1.Job, app.RestoreFor(app.Interactive(Pw, null), set.Id).Points());

                // ... and on the computer: its local index's write died before the rename
                var state = Path.Combine(app.Home.SetDir(set.Id), "state.txt");
                var stateSha = ShaFile(state);
                var half = File.ReadAllBytes(state); File.WriteAllBytes(state + ".tmp" + "9c8d7e6f", half.Take(half.Length / 2).ToArray());
                Assert.Equal(stateSha, ShaFile(state));
                File.WriteAllText(Path.Combine(src, "list.txt"), "one\r\ntwo\r\nthree\r\n"); File.SetLastWriteTimeUtc(Path.Combine(src, "list.txt"), DateTime.UtcNow.AddMinutes(1));
                Thread.Sleep(1100);
                var r2 = app.Backup(set.Id);
                Assert.True(r2.Result == "BS_STOP_SUCCESS", r2.Result + "\n" + string.Join("\n", r2.LogLines));
                Assert.Equal(1, r2.Updated); Assert.Equal(0, r2.New);                                      // the index of the last point was read, not the half one
                AssertSame(Tree(src), RestoreTree(env, app, set.Id, null, src, "r-co01"), "the newest point");
            }
        }
    }
}
