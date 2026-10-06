using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;
using Xunit.Abstractions;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// QA agent L (night): ReliabilityTests.RestoreInterruptedMidway_RunAgain_GivesIdenticalFiles waits until 60 MB are in
    /// &lt;target&gt;/&lt;source path&gt; before it kills restic. Since the restic restore writes into a staging folder
    /// (target/.ob-restoring-*, moved into place only when restic has finished), that folder never fills while restic runs:
    /// the wait ends after the restore is complete, KillRestic() kills nothing, and the test passes without its fault.
    /// This test injects the same fault where the data really is (the staging folder), PROVES the restore was running when
    /// it was killed, and only then checks the recovery (restore again into the same folder, byte for byte).
    /// It also records whether the old wait condition was ever met while restic ran (evidence for finding L-3).
    /// Needs OB_RESTIC; without it the test FAILS (it never passes without running).
    /// </summary>
    public class QaL_FaultInjectionProofTests
    {
        readonly ITestOutputHelper output;
        public QaL_FaultInjectionProofTests(ITestOutputHelper output) { this.output = output; }

        static void Rnd(string path, long size, int seed)
        {
            var r = new Random(seed); var b = new byte[1 << 20];
            using (var f = File.Create(path)) for (long n = 0; n < size; n += b.Length) { r.NextBytes(b); f.Write(b, 0, (int)Math.Min(b.Length, size - n)); }
        }
        static string Sha(string f) { using (var s = File.OpenRead(f)) return Bytes.Hex(System.Security.Cryptography.SHA256.Create().ComputeHash(s)); }
        static long Bytes0(string d) { try { return Directory.Exists(d) ? Directory.GetFiles(d, "*", SearchOption.AllDirectories).Sum(f => { try { return new FileInfo(f).Length; } catch (IOException) { return 0L; } }) : 0; } catch (IOException) { return 0; } catch (UnauthorizedAccessException) { return 0; } }

        /// <summary>Only the restic processes that restore into this test's folder (never another test's or agent's restic).</summary>
        static List<Process> OurRestic(string target)
        {
            var mine = new List<Process>();
            foreach (var p in Process.GetProcessesByName("restic"))
                try { if (File.ReadAllText("/proc/" + p.Id + "/cmdline").Replace('\0', ' ').Contains(target)) mine.Add(p); } catch (Exception) { }
            return mine;
        }

        [Fact]
        public void ResticRestoreKilledWhileItReallyRuns_RestoreAgain_GivesIdenticalFiles()
        {
            var restic = Environment.GetEnvironmentVariable("OB_RESTIC");
            Assert.True(!string.IsNullOrEmpty(restic) && File.Exists(restic), "NOT TESTED: OB_RESTIC (the restic program) is needed — this test never passes without running");
            Assert.True(OperatingSystem.IsLinux(), "NOT TESTED: reads /proc to kill only this test's restic");
            using (var env = new Env())
            {
                var src = env.Dir("src");
                for (int i = 0; i < 5; i++) Rnd(Path.Combine(src, "f" + i + ".bin"), 50L << 20, 60 + i);
                env.CreateUser("qalri", "Customer-Pass-1", 1);
                var app = env.Agent("qalri", "Customer-Pass-1");
                var created = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "R", Engine = "RESTIC", Sources = { src } });
                var set = app.Sets().First(x => x.Id == created.Id);
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);
                var target = env.Dir("restore");
                var dir = Path.Combine(target, Env.Rel(src));
                var rs = app.Restic(set);
                Exception first = null;
                var t = new Thread(() => { try { rs.Restore(null, target, null, new List<string>()); } catch (Exception e) { first = e; } });
                t.Start();
                bool oldConditionMetWhileRunning = false; var until = DateTime.UtcNow.AddSeconds(120);
                while (DateTime.UtcNow < until && t.IsAlive && Bytes0(target) < (60L << 20))
                {
                    if (Directory.Exists(dir) && Bytes0(dir) >= (60L << 20)) oldConditionMetWhileRunning = true;
                    Thread.Sleep(20);
                }
                var victims = OurRestic(target);
                bool aliveAtKill = t.IsAlive && victims.Count > 0;
                foreach (var p in victims) try { p.Kill(); } catch (Exception) { }
                t.Join();
                output.WriteLine("restore alive when killed: " + aliveAtKill + "; bytes in target then: " + Bytes0(target) + "; the old test's condition (60 MB under " + dir + ") met while restic ran: " + oldConditionMetWhileRunning + "; first restore: " + (first == null ? "no exception" : first.Message));
                Assert.True(aliveAtKill, "the fault was not injected: the restore had already ended (or no restic of ours ran) when it was to be killed");
                Assert.NotNull(first);                                                    // the cut restore is not a success

                rs.Restore(null, target, null, new List<string>());                         // the line came back: restore again, same folder
                foreach (var f in Directory.GetFiles(src)) Assert.Equal(Sha(f), Sha(Path.Combine(dir, Path.GetFileName(f))));
                Assert.Equal(5, Directory.GetFiles(dir).Length);
                Assert.Empty(Directory.GetDirectories(target, ".ob-restoring-*"));          // nothing half left in the customer's folder
            }
        }
    }
}
