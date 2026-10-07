using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;
using Xunit.Abstractions;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// Reliability of the restic engine under faults and volume. Needs OB_RESTIC; the disk-full test also needs
    /// OB_ALLOW_MOUNT=1 (root on Linux: mounts a small tmpfs as the user home). Skipped otherwise.
    /// </summary>
    public class ReliabilityTests
    {
        readonly ITestOutputHelper output;
        public ReliabilityTests(ITestOutputHelper output) { this.output = output; }

        static bool Have { get { var r = Environment.GetEnvironmentVariable("OB_RESTIC"); return !string.IsNullOrEmpty(r) && File.Exists(r); } }
        static void Rnd(string path, long size, int seed)
        {
            var r = new Random(seed); var b = new byte[1 << 20];
            using (var f = File.Create(path)) for (long n = 0; n < size; n += b.Length) { r.NextBytes(b); f.Write(b, 0, (int)Math.Min(b.Length, size - n)); }
        }
        static bool Same(string a, string b)
        {
            using (var x = File.OpenRead(a)) using (var y = File.OpenRead(b))
            {
                if (x.Length != y.Length) return false;
                var p = new byte[1 << 20]; var q = new byte[1 << 20];
                while (true) { int n = x.Read(p, 0, p.Length); if (n == 0) return true; int m = 0; while (m < n) { int k = y.Read(q, m, n - m); if (k == 0) return false; m += k; } for (int i = 0; i < n; i++) if (p[i] != q[i]) return false; }
            }
        }
        /// <summary>Kills only the restic processes whose command line names <paramref name="mine"/> (this test's folder — never
        /// another test's or agent's restic; outside Linux every restic) and returns how many it killed while they ran.</summary>
        internal static int KillRestic(string mine)
        {
            int killed = 0;
            foreach (var p in Process.GetProcessesByName("restic"))
                try
                {
                    if (OperatingSystem.IsLinux() && !File.ReadAllText("/proc/" + p.Id + "/cmdline").Replace('\0', ' ').Contains(mine)) continue;
                    if (!p.HasExited) { p.Kill(); killed++; }
                }
                catch (Exception) { }
            return killed;
        }
        static long BytesUnder(string d) { try { return Directory.Exists(d) ? Directory.GetFiles(d, "*", SearchOption.AllDirectories).Sum(f => { try { return new FileInfo(f).Length; } catch (IOException) { return 0L; } }) : 0; } catch (IOException) { return 0; } catch (UnauthorizedAccessException) { return 0; } }

        static BackupSetInfo NewSet(Env env, string login, out AgentApp app, string src, double quotaGB = 1)
        {
            env.CreateUser(login, "Customer-Pass-1", quotaGB);
            app = env.Agent(login, "Customer-Pass-1");
            var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "R", Engine = "RESTIC", Sources = { src } });
            return app.Sets().First(x => x.Id == set.Id);
        }

        [Fact]
        public void RestoreInterruptedMidway_RunAgain_GivesIdenticalFiles()
        {
            if (!Have) throw NotTested.Because("OB_RESTIC (the restic program) is not set");
            using (var env = new Env())
            {
                var src = env.Dir("src");
                for (int i = 0; i < 5; i++) Rnd(Path.Combine(src, "f" + i + ".bin"), 50L << 20, 60 + i);
                AgentApp app; var set = NewSet(env, "rri", out app, src);
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);
                var target = env.Dir("restore");
                var rs = app.Restic(set);
                Exception cut = null;
                var t = new Thread(() => { try { rs.Restore(null, target, null, new List<string>()); } catch (Exception e) { cut = e; } });
                t.Start();
                // L-3: restic writes into target/.ob-restoring-* and the files move into place only at the end, so the data
                // to wait for is anywhere under the target, not in its final folder (that wait ended after the restore did)
                var dir = Path.Combine(target, Env.Rel(src)); var until = DateTime.UtcNow.AddSeconds(120);
                while (DateTime.UtcNow < until && t.IsAlive && BytesUnder(target) < 60L << 20) Thread.Sleep(20);
                var killed = t.IsAlive ? KillRestic(target) : 0; t.Join();
                Assert.True(killed > 0, "the fault was not injected: the restore had ended before restic could be killed");
                Assert.NotNull(cut);                                            // the cut restore is not reported as a success
                rs.Restore(null, target, null, new List<string>());          // "the line came back": restore again into the same folder
                foreach (var f in Directory.GetFiles(src)) Assert.True(Same(f, Path.Combine(dir, Path.GetFileName(f))), "differs: " + f);
                Assert.Equal(5, Directory.GetFiles(dir).Length);
            }
        }

        [Fact]
        public void Volume_20000SmallFilesAnd1GB_BackupChangeRestore()
        {
            if (!Have) throw NotTested.Because("OB_RESTIC (the restic program) is not set");
            using (var env = new Env())
            {
                var src = env.Dir("src");
                for (int d = 0; d < 100; d++)
                {
                    var dd = Path.Combine(src, "dir" + d); Directory.CreateDirectory(dd);
                    for (int i = 0; i < 200; i++) File.WriteAllText(Path.Combine(dd, "doc" + i + ".txt"), "file " + d + "/" + i + " " + new string('z', i * 7));
                }
                Rnd(Path.Combine(src, "disk.vhdx"), 1L << 30, 7);
                AgentApp app; var set = NewSet(env, "rvol", out app, src, 5);
                var sw = Stopwatch.StartNew();
                var r1 = app.Backup(set.Id);
                var t1 = sw.Elapsed; output.WriteLine("first backup 20,001 files / 1GB: " + t1.TotalSeconds.ToString("0.0") + " s, sent " + (r1.BytesSent >> 20) + " MB");
                Assert.True(r1.Result == "BS_STOP_SUCCESS", string.Join("\n", r1.LogLines.Take(30)));
                Assert.Equal(20001, r1.New);

                for (int i = 0; i < 50; i++) File.AppendAllText(Path.Combine(src, "dir" + i, "doc1.txt"), " changed");
                using (var f = new FileStream(Path.Combine(src, "disk.vhdx"), FileMode.Open)) { f.Position = 500L << 20; var x = new byte[4 << 20]; new Random(8).NextBytes(x); f.Write(x, 0, x.Length); }
                sw.Restart();
                var r2 = app.Backup(set.Id);
                output.WriteLine("second backup (50 small + 4MB inside 1GB changed): " + sw.Elapsed.TotalSeconds.ToString("0.0") + " s, sent " + (r2.BytesSent >> 10) + " KB");
                Assert.True(r2.Result == "BS_STOP_SUCCESS", string.Join("\n", r2.LogLines.Where(l => l.Contains(",err,"))));
                Assert.Equal(51, r2.Updated);
                Assert.True(r2.BytesSent < 40L << 20, "sent " + r2.BytesSent);

                var target = env.Dir("restore");
                sw.Restart();
                app.Restic(set).Restore(null, target, null, new List<string>());
                output.WriteLine("full restore: " + sw.Elapsed.TotalSeconds.ToString("0.0") + " s");
                var dir = Path.Combine(target, Env.Rel(src));
                Assert.Equal(20001, Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length);
                Assert.True(Same(Path.Combine(src, "disk.vhdx"), Path.Combine(dir, "disk.vhdx")));
                foreach (var f in Directory.GetFiles(Path.Combine(src, "dir7"))) Assert.True(Same(f, Path.Combine(dir, "dir7", Path.GetFileName(f))));
                Assert.Equal(1, app.Restic(set).Check("10%").Int("ok"));
            }
        }

        [Fact]
        public void ServerDiskFullMidBackup_ExistingBackupsUnharmed_NextRunAfterSpaceIsFreedCompletes()
        {
            if (!Have || Environment.GetEnvironmentVariable("OB_ALLOW_MOUNT") != "1") throw NotTested.Because("needs restic and OB_ALLOW_MOUNT=1 (a small disk is mounted to fill it)");
            using (var env = new Env())
            {
                var src = env.Dir("src");
                Rnd(Path.Combine(src, "small.bin"), 5L << 20, 1);
                // the user's home on a 60MB disk
                var mnt = Path.Combine(env.Root, "small-disk"); Directory.CreateDirectory(mnt);
                Assert.Equal(0, Run("mount", "-t tmpfs -o size=60m tmpfs " + mnt));
                try
                {
                    foreach (var d in Directory.GetDirectories(env.HomeA)) { }
                    env.CreateUser("rdf", "Customer-Pass-1");
                    var userDir = Directory.GetDirectories(env.HomeA, "rdf", SearchOption.AllDirectories).First();
                    var onDisk = Path.Combine(mnt, "rdf");
                    CopyDir(userDir, onDisk); Directory.Delete(userDir, true);
                    Assert.Equal(0, Run("ln", "-s " + onDisk + " " + userDir));
                    var app = env.Agent("rdf", "Customer-Pass-1");
                    var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "D", Engine = "RESTIC", Sources = { src } });
                    var s = app.Sets().First(x => x.Id == set.Id);
                    Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);                         // 5MB fits

                    Rnd(Path.Combine(src, "big.bin"), 120L << 20, 2);                                    // 120MB does not
                    var r = app.Backup(set.Id);
                    output.WriteLine(r.Result + ": " + string.Join(" | ", r.LogLines.Where(l => l.Contains(",err,")).Take(3)));
                    Assert.NotEqual("BS_STOP_SUCCESS", r.Result);
                    Assert.Empty(Directory.GetFiles(onDisk, "*.tmp", SearchOption.AllDirectories));    // no half-written files left

                    File.Delete(Path.Combine(src, "big.bin"));                                           // space is freed (here: less data)
                    var r3 = app.Backup(set.Id);
                    Assert.True(r3.Result == "BS_STOP_SUCCESS", string.Join("\n", r3.LogLines));
                    var rs = app.Restic(s);
                    Assert.Equal(1, rs.Check("100%").Int("ok"));
                    Assert.Equal(2, rs.Snapshots().Count);
                    var target = env.Dir("restore");
                    rs.Restore(null, target, null, new List<string>());
                    Assert.True(Same(Path.Combine(src, "small.bin"), Path.Combine(target, Env.Rel(src), "small.bin")));
                }
                finally { Run("umount", "-l " + mnt); }
            }
        }

        static int Run(string exe, string args) { using (var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false })) { p.WaitForExit(); return p.ExitCode; } }
        static void CopyDir(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
            foreach (var d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
        }
    }
}
