using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// The restic engine alone (ResticRunner, no backup server: a local repository) — component contract (specs.py BK-03 / RS-03):
    ///   purpose  back up a tree with restic and restore it exactly; refuse a wrong key; never call damaged data good
    ///   input    a known tree: nested folders, a Hebrew name, a name with [ ] *, spaces, an empty file, 3 MB random, an empty folder
    ///   expected the backup succeeds; `restic check --read-data` is clean; the restore is byte-identical (SHA-256 of every
    ///            file); a second run without changes adds no data; another key reads nothing; a damaged pack is found by
    ///            `restic check` and its restore fails loudly; a backup stopped in the middle leaves a repository that checks
    ///            clean and the next backup completes and restores identical
    /// </summary>
    public class ResticComponentTests
    {
        static string Restic { get { return Environment.GetEnvironmentVariable("OB_RESTIC"); } }
        static bool Have { get { return !string.IsNullOrEmpty(Restic) && File.Exists(Restic); } }
        static string Sha(string f) { using (var s = File.OpenRead(f)) using (var h = SHA256.Create()) return Convert.ToHexString(h.ComputeHash(s)); }
        static Dictionary<string, string> Manifest(string dir) { return Directory.GetFiles(dir, "*", SearchOption.AllDirectories).ToDictionary(f => Path.GetRelativePath(dir, f), Sha); }

        sealed class Rig : IDisposable
        {
            public string Root, Src, Repo; public AgentApp App; public BackupSetInfo Set; public KeySet Key;
            public Rig()
            {
                Root = Path.Combine(Path.GetTempPath(), "obrc-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Src = Path.Combine(Root, "src"); Repo = Path.Combine(Root, "nas");
                Directory.CreateDirectory(Path.Combine(Src, "a", "b", "c")); Directory.CreateDirectory(Path.Combine(Src, "empty-folder")); Directory.CreateDirectory(Repo);
                File.WriteAllText(Path.Combine(Src, "a", "חשבונית מס 7.txt"), "שלום");
                // "*" cannot be in a Windows file name (CreateFile refuses it): there the name keeps the brackets - the characters that
                // are special in restic's include patterns and CAN be in a Windows name - and the same number of files
                File.WriteAllText(Path.Combine(Src, "a", "b", OperatingSystem.IsWindows() ? "Report [final] (v2).docx" : "Report [final] *v2*.docx"), "brackets and stars");
                File.WriteAllText(Path.Combine(Src, "a", "b", "c", "with space.txt"), "x y");
                File.WriteAllBytes(Path.Combine(Src, "empty.bin"), new byte[0]);
                var big = new byte[3 * 1024 * 1024]; new Random(42).NextBytes(big); File.WriteAllBytes(Path.Combine(Src, "big.bin"), big);
                App = new AgentApp(Path.Combine(Root, "home"));
                App.Home.SaveRegistration("http://127.0.0.1:9/", "nobody", "pc", "dev");   // no server: the engine works alone
                Set = new BackupSetInfo { Id = "1700000000099", Name = "Local", Engine = "RESTIC", DestMode = "LOCAL", LocalCopyPath = Repo, Sources = { Src } };
                Key = KeySet.Random();
            }
            public ResticRunner Runner(KeySet k = null) { return new ResticRunner(App, Set, k ?? Key); }
            public void Dispose() { try { Directory.Delete(Root, true); } catch (Exception) { } }
        }

        [Fact]
        public void KnownTree_BacksUp_ChecksClean_RestoresIdentical_SecondRunAddsNothing()
        {
            if (!Have) throw NotTested.Because("OB_RESTIC (the restic program) is not set");
            using (var rig = new Rig())
            {
                var r = rig.Runner().Backup();
                Assert.True(r.Result == "BS_STOP_SUCCESS" || r.Result == "BS_STOP_SUCCESS_WITH_WARNING", r.Result + "\n" + string.Join("\n", r.LogLines));
                Assert.Equal(5, r.New);                                                    // the counts are real, not zero by default
                Assert.True(r.BytesSent > 3 * 1024 * 1024, "sent " + r.BytesSent);
                Assert.Equal(1, rig.Runner().Check("100%").Int("ok"));
                var target = Path.Combine(rig.Root, "restore");
                rig.Runner().RestoreMany(null, target, null, new List<string>());
                var restored = AgentRig.Under(target, rig.Src);
                Assert.Equal(Manifest(rig.Src).OrderBy(x => x.Key), Manifest(restored).OrderBy(x => x.Key));
                Assert.True(Directory.Exists(Path.Combine(restored, "empty-folder")));
                // nothing changed: no file is new or changed and no file data is sent again (restic writes only the new
                // snapshot's small metadata — it keeps access times, which the first run's reading changed)
                var again = rig.Runner().Backup();
                Assert.StartsWith("BS_STOP_SUCCESS", again.Result);
                Assert.Equal(0, again.New); Assert.Equal(0, again.Updated);
                Assert.True(again.BytesSent < 64 * 1024, "sent again: " + again.BytesSent);
                Assert.Equal(2, rig.Runner().SnapshotList().Count);
            }
        }

        [Fact]
        public void AnotherKey_ReadsNothing()
        {
            if (!Have) throw NotTested.Because("OB_RESTIC (the restic program) is not set");
            using (var rig = new Rig())
            {
                Assert.StartsWith("BS_STOP_SUCCESS", rig.Runner().Backup().Result);
                var other = rig.Runner(KeySet.Random());
                Assert.Throws<AgentException>(() => other.SnapshotList());
                Assert.Throws<AgentException>(() => other.RestoreMany(null, Path.Combine(rig.Root, "r2"), null, new List<string>()));
                Assert.False(Directory.Exists(AgentRig.Under(Path.Combine(rig.Root, "r2"), rig.Src)));
            }
        }

        [Fact]
        public void DamagedPack_IsFoundByCheck_AndItsRestoreFailsLoudly()
        {
            if (!Have) throw NotTested.Because("OB_RESTIC (the restic program) is not set");
            using (var rig = new Rig())
            {
                Assert.StartsWith("BS_STOP_SUCCESS", rig.Runner().Backup().Result);
                var biggest = Directory.GetFiles(Path.Combine(rig.Repo, "restic-" + rig.Set.Id, "data"), "*", SearchOption.AllDirectories).OrderByDescending(f => new FileInfo(f).Length).First();
                File.SetAttributes(biggest, FileAttributes.Normal);
                using (var fs = new FileStream(biggest, FileMode.Open, FileAccess.ReadWrite)) { fs.Position = fs.Length / 2; var b = new byte[64]; fs.Read(b, 0, 64); for (int i = 0; i < 64; i++) b[i] ^= 0xFF; fs.Position = fs.Length / 2; fs.Write(b, 0, 64); }
                var c = rig.Runner().Check("100%");
                Assert.Equal(0, c.Int("ok"));
                Assert.Equal(1, c.Int("failed"));
                Assert.Throws<AgentException>(() => rig.Runner().RestoreMany(null, Path.Combine(rig.Root, "r3"), null, new List<string>()));
            }
        }

        /// <summary>NOTE: this test did NOT reproduce bug 98 locally (it passes on the old code too: forget --prune holds its exclusive
        /// lock only for part of its run, and the product prepares ~10 s before restic starts) - it is kept as a behaviour check,
        /// not as the proof; the proof is the CI gate's J6 over several runs and the component test below.
        /// Bug 98 (CI gate, Playwright J6): a backup that started while another restic command of the product held the
        /// repository's exclusive lock (forget --prune after a run, the restore test's check) failed at once - "unable to create
        /// lock in backend: repository is already locked exclusively" - and the run was BS_STOP_BY_SYSTEM_ERROR. A real,
        /// running exclusive lock is held here by forget --prune deleting 160 MB.</summary>
        [Fact]
        public void ABackupStartedWhileTheRepositoryIsLockedForAMoment_WaitsForTheLock_AndCompletes()
        {
            if (!Have) throw NotTested.Because("OB_RESTIC (the restic program) is not set");
            using (var rig = new Rig())
            {
                Assert.StartsWith("BS_STOP_SUCCESS", rig.Runner().Backup().Result);
                for (int i = 0; i < 20; i++) { var b = new byte[8 * 1024 * 1024]; new Random(300 + i).NextBytes(b); File.WriteAllBytes(Path.Combine(rig.Src, "big" + i + ".bin"), b); }
                Assert.StartsWith("BS_STOP_SUCCESS", rig.Runner().Backup().Result);
                foreach (var f in Directory.GetFiles(rig.Src, "big*.bin")) File.Delete(f);
                Assert.StartsWith("BS_STOP_SUCCESS", rig.Runner().Backup().Result);   // the 160 MB are now only in older snapshots
                File.WriteAllText(Path.Combine(rig.Src, "a", "changed.txt"), "after the lock");
                var locks = Path.Combine(rig.Repo, "restic-" + rig.Set.Id, "locks");
                Directory.CreateDirectory(locks);
                ResticRunner.Result held = null;
                // an EXCLUSIVE lock, as in the gate: forget --prune deleting 160 MB (restic check takes a shared lock a backup can live with -
                // the first version of this test held that one and passed on the old code: it tested nothing)
                var holder = new System.Threading.Thread(() => held = rig.Runner().Run("forget", "--keep-last", "1", "--prune", "--tag", "set:" + rig.Set.Id));
                holder.Start();
                var until = DateTime.UtcNow.AddSeconds(30);
                while (Directory.GetFiles(locks).Length == 0 && DateTime.UtcNow < until) System.Threading.Thread.Sleep(20);
                Assert.True(Directory.GetFiles(locks).Length > 0, "the check never took its lock: nothing was tested");
                var r = rig.Runner().Backup();
                holder.Join();
                Assert.True(held != null && held.Code == 0, "the lock holder failed: " + (held == null ? "-" : held.Err));
                Assert.True(r.Result.StartsWith("BS_STOP_SUCCESS"), "a backup that found the repository locked for a moment must wait, not fail: " + r.Result + "\n" + string.Join("\n", r.LogLines));
            }
        }

        /// <summary>Bug 98: every command of the product that writes to the repository waits for a lock held for a moment by
        /// another of its own commands (restic's --retry-lock) instead of failing at once. Fails on the old code.</summary>
        [Fact]
        public void EveryWritingResticCommand_WaitsForALockedRepository()
        {
            if (!Have) throw NotTested.Because("OB_RESTIC (the restic program) is not set");
            using (var rig = new Rig())
            {
                var seen = new System.Collections.Concurrent.ConcurrentQueue<string[]>();
                var was = ResticRunner.Trace;
                ResticRunner.Trace = a => seen.Enqueue(a);
                try
                {
                    Assert.StartsWith("BS_STOP_SUCCESS", rig.Runner().Backup().Result);
                    rig.Runner().RestoreMany(null, Path.Combine(rig.Root, "r98"), null, new List<string>());
                }
                finally { ResticRunner.Trace = was; }
                var writing = seen.Where(a => a.Length > 0 && (a[0] == "backup" || a[0] == "forget" || a[0] == "prune" || a[0] == "restore")).ToList();
                Assert.Contains(writing, a => a[0] == "backup");
                Assert.Contains(writing, a => a[0] == "restore");
                foreach (var a in writing) Assert.True(a.Contains("--retry-lock"), "restic " + string.Join(" ", a.Take(4)) + " ... does not wait for a lock");
            }
        }

        [Fact]
        public void StoppedInTheMiddle_RepositoryChecksClean_NextBackupCompletes_RestoresIdentical()
        {
            if (!Have) throw NotTested.Because("OB_RESTIC (the restic program) is not set");
            using (var rig = new Rig())
            {
                for (int i = 0; i < 16; i++) { var b = new byte[8 * 1024 * 1024]; new Random(100 + i).NextBytes(b); File.WriteAllBytes(Path.Combine(rig.Src, "more" + i + ".bin"), b); }
                var first = rig.Runner();
                var sw = Stopwatch.StartNew();
                // Q32: the stop came after 1.5 s; on the fast CI runner the backup had ENDED by then - its "success" was true and
                // the test called it a product failure. The stop now comes early, and the test proves it was asked for in time.
                bool asked = false;
                first.StopRequested = () => { if (sw.ElapsedMilliseconds > 300) asked = true; return asked; };
                var stopped = first.Backup();
                if (!asked) throw new InvalidOperationException("NOT TESTED: the backup ended in " + sw.ElapsedMilliseconds + " ms, before the stop could be asked for");
                Assert.False(stopped.Result.StartsWith("BS_STOP_SUCCESS"), "a stopped backup must not say success: " + stopped.Result);
                var chk = rig.Runner().Check("100%");
                Assert.True(chk.Int("ok") == 1, "restic check after a stopped backup: " + chk["message"] + "\n" + string.Join("\n", stopped.LogLines));
                var r = rig.Runner().Backup();
                Assert.StartsWith("BS_STOP_SUCCESS", r.Result);
                var target = Path.Combine(rig.Root, "restore");
                rig.Runner().RestoreMany(null, target, null, new List<string>());
                Assert.Equal(Manifest(rig.Src).OrderBy(x => x.Key), Manifest(AgentRig.Under(target, rig.Src)).OrderBy(x => x.Key));
            }
        }
    }
}
