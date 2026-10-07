using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// QA audit J — BK-03 / RS-03, the restic engine alone (component layer: ResticRunner + a real restic, local repository).
    /// Oracle: restic's own output (snapshots / ls, run directly, not through the product) and SHA-256 of the restored files.
    /// Skipped without OB_RESTIC.
    /// </summary>
    public class AuditJ_ResticEngineTests
    {
        static string Restic { get { return Environment.GetEnvironmentVariable("OB_RESTIC"); } }
        static bool Have { get { return !string.IsNullOrEmpty(Restic) && File.Exists(Restic); } }
        static string Sha(string f) { using (var s = File.OpenRead(f)) using (var h = SHA256.Create()) return Convert.ToHexString(h.ComputeHash(s)); }
        static Dictionary<string, string> Manifest(string dir) { return Directory.GetFiles(dir, "*", SearchOption.AllDirectories).ToDictionary(f => Path.GetRelativePath(dir, f), Sha); }

        sealed class Rig : IDisposable
        {
            public string Root, Src, Repo; public AgentApp App; public BackupSetInfo Set; public KeySet Key;
            public Rig(string id = "1700000000201")
            {
                Root = Path.Combine(Path.GetTempPath(), "obaj-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Src = Path.Combine(Root, "src"); Repo = Path.Combine(Root, "nas");
                Directory.CreateDirectory(Src); Directory.CreateDirectory(Repo);
                App = NewApp("home");
                Set = new BackupSetInfo { Id = id, Name = "Local", Engine = "RESTIC", DestMode = "LOCAL", LocalCopyPath = Repo, Sources = { Src } };
                Key = KeySet.Random();
            }
            public AgentApp NewApp(string home)
            {
                var a = new AgentApp(Path.Combine(Root, home));
                a.Home.SaveRegistration("http://127.0.0.1:9/", "nobody", "pc", "dev");   // no server: the engine works alone
                return a;
            }
            public string RepoDir { get { return Path.Combine(Repo, "restic-" + Set.Id); } }
            public ResticRunner Runner(KeySet k = null, AgentApp app = null) { return new ResticRunner(app ?? App, Set, k ?? Key); }

            /// <summary>The oracle: restic itself, run directly on the repository.</summary>
            public string Oracle(KeySet k, params string[] args)
            {
                var psi = new ProcessStartInfo(Restic) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var a in args) psi.ArgumentList.Add(a);
                psi.Environment["RESTIC_REPOSITORY"] = RepoDir;
                psi.Environment["RESTIC_PASSWORD"] = Bytes.Hex((k ?? Key).ToRaw()).Substring(0, 64);
                psi.Environment["RESTIC_CACHE_DIR"] = Path.Combine(Root, "oracle-cache");
                using (var p = Process.Start(psi))
                {
                    var err = p.StandardError.ReadToEndAsync();
                    var o = p.StandardOutput.ReadToEnd(); p.WaitForExit();
                    if (p.ExitCode != 0) throw new Exception("oracle restic " + string.Join(" ", args) + " exit " + p.ExitCode + ": " + err.Result);
                    return o;
                }
            }
            public List<string> SnapshotIds(KeySet k = null)
            {
                return Regex.Matches(Oracle(k, "snapshots", "--json"), "\"id\"\\s*:\\s*\"([0-9a-f]{64})\"").Cast<Match>().Select(m => m.Groups[1].Value).ToList();
            }
            public void Dispose()
            {
                try { foreach (var f in Directory.GetFiles(Root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal); } catch (Exception) { }
                try { Directory.Delete(Root, true); } catch (Exception) { }
            }
        }

        static string Log(BackupRun r) { return r.Result + "\n" + string.Join("\n", r.LogLines); }

        /// <summary>
        /// A wrapper that runs the real restic without the root capabilities that read any file (the test machine runs as
        /// root, where chmod 000 is not enough to make a file unreadable). Its output is restic's own.
        /// </summary>
        static string NoReadAllWrapper(string dir)
        {
            var w = Path.Combine(dir, "restic-noroot.sh");
            File.WriteAllText(w, "#!/bin/sh\nexec setpriv --inh-caps=-dac_override,-dac_read_search --bounding-set=-dac_override,-dac_read_search \"" + Restic + "\" \"$@\"\n");
            File.SetUnixFileMode(w, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return w;
        }

        static bool CanDropCaps { get { return OperatingSystem.IsLinux() && File.Exists("/usr/bin/setpriv"); } }

        // ------------------------------------------------------------------------------------------------ BK-03 partial backup

        /// <summary>
        /// BK-03 / partial snapshot (restic exit code 3 = "at least one source file could not be read").
        /// Expected: the run is not a plain success — like the native engine (BackupRun.Err → Errors++ → SUCCESS_WITH_ERROR)
        /// the unreadable file is an error and is NAMED in the job log, so the customer knows what is not protected.
        /// restic writes its JSON "error" messages to STDERR; ResticRunner.Backup only parses STDOUT for them.
        /// </summary>
        [Fact]
        public void UnreadableFile_ExitCode3_IsAnErrorNamedInTheLog()
        {
            if (!Have || !CanDropCaps) return;
            using (var rig = new Rig())
            {
                File.WriteAllText(Path.Combine(rig.Src, "readable.txt"), "ok");
                var locked = Path.Combine(rig.Src, "payroll-locked.xlsx");
                File.WriteAllText(locked, "cannot be read");
                File.SetUnixFileMode(locked, UnixFileMode.None);
                var runner = rig.Runner();
                runner.ExePath = NoReadAllWrapper(rig.Root);
                var r = runner.Backup();

                // the oracle: restic made a snapshot, without the unreadable file
                Assert.Single(rig.SnapshotIds());
                var ls = rig.Oracle(null, "ls", "latest");
                Assert.Contains("readable.txt", ls);
                Assert.DoesNotContain("payroll-locked.xlsx", ls);

                Assert.True(r.Errors >= 1, "an unreadable source file must count as an error (native engine does): errors=" + r.Errors + "\n" + Log(r));
                Assert.Equal("BS_STOP_SUCCESS_WITH_ERROR", r.Result);
                Assert.Contains(r.LogLines, l => l.Contains("payroll-locked.xlsx"));
            }
        }

        // ------------------------------------------------------------------------------------------------ key handling

        /// <summary>
        /// Wrong key on a computer that has not used the repository yet (a reinstall, a typo in the set password): no new
        /// repository may be created over / beside the old one, the old snapshots stay readable with the right key.
        /// Passing coverage test (BK-03, dimension "wrong key", component). Can fail: if EnsureRepository's `init` succeeded
        /// (e.g. a repository path that changed with the key) the snapshot count with the old key or the config hash differ.
        /// </summary>
        [Fact]
        public void WrongKey_FreshComputer_NeverCreatesANewRepository_OldSnapshotsIntact()
        {
            if (!Have) return;
            using (var rig = new Rig())
            {
                File.WriteAllText(Path.Combine(rig.Src, "a.txt"), "a");
                Assert.StartsWith("BS_STOP_SUCCESS", rig.Runner().Backup().Result);
                var configSha = Sha(Path.Combine(rig.RepoDir, "config"));
                var keysBefore = Directory.GetFiles(Path.Combine(rig.RepoDir, "keys")).Length;

                var other = rig.NewApp("home2");   // a fresh agent home: no "restic-init.txt" flag yet
                var r = rig.Runner(KeySet.Random(), other).Backup();
                Assert.False(r.Result.StartsWith("BS_STOP_SUCCESS"), "a wrong key must not be a successful backup: " + Log(r));
                Assert.Equal(configSha, Sha(Path.Combine(rig.RepoDir, "config")));
                Assert.Equal(keysBefore, Directory.GetFiles(Path.Combine(rig.RepoDir, "keys")).Length);
                Assert.Single(rig.SnapshotIds());
            }
        }

        /// <summary>
        /// The same run must tell the customer WHY: restic answered "wrong password or no key found" (exit 12) to
        /// `cat config`; EnsureRepository ignores that and tries `restic init`, so the run's error is
        /// "restic init: ... config file already exists" — which reads like a server fault, not a wrong key.
        /// </summary>
        [Fact]
        public void WrongKey_FreshComputer_ErrorSaysWrongKey_NotRepositoryCreation()
        {
            if (!Have) return;
            using (var rig = new Rig())
            {
                File.WriteAllText(Path.Combine(rig.Src, "a.txt"), "a");
                Assert.StartsWith("BS_STOP_SUCCESS", rig.Runner().Backup().Result);
                var r = rig.Runner(KeySet.Random(), rig.NewApp("home2")).Backup();
                var errs = r.LogLines.Where(l => l.Contains("err")).ToList();
                Assert.True(errs.Any(l => Regex.IsMatch(l, "wrong password|wrong key|no key found|key", RegexOptions.IgnoreCase)) && !errs.Any(l => l.Contains("restic init")),
                    "the error must name the wrong key, not a failed repository creation:\n" + string.Join("\n", errs));
            }
        }

        // ------------------------------------------------------------------------------------------------ retention

        /// <summary>
        /// Retention "keep the last 1 job": after three runs with changes, restic keeps exactly one snapshot and it is the
        /// NEWEST (its content is the third version). Passing coverage test (BK-03, retention, component). Can fail: if
        /// forget kept the oldest (or removed everything) the restored SHA would be version 1's, or the count would differ.
        /// </summary>
        [Fact]
        public void Retention_KeepLastOne_KeepsTheNewestSnapshot()
        {
            if (!Have) return;
            using (var rig = new Rig())
            {
                rig.Set.Retention = new RetentionPolicy { Unit = "JOBS", Period = 1 };
                var f = Path.Combine(rig.Src, "doc.txt");
                string lastSnap = null;
                for (int v = 1; v <= 3; v++)
                {
                    File.WriteAllText(f, "version " + v + new string('x', v * 1000));
                    var r = rig.Runner().Backup();
                    Assert.True(r.Result == "BS_STOP_SUCCESS", Log(r));
                    lastSnap = r.LogLines.Select(l => Regex.Match(l, "Snapshot ([0-9a-f]{64})")).Last(m => m.Success).Groups[1].Value;
                }
                var ids = rig.SnapshotIds();
                Assert.Equal(new[] { lastSnap }, ids);
                var target = Path.Combine(rig.Root, "restore");
                rig.Runner().RestoreMany(null, target, null, new List<string>());
                Assert.Equal(Sha(f), Sha(Path.Combine(AgentRig.Under(target, rig.Src), "doc.txt")));
            }
        }

        /// <summary>
        /// Retention after the set's folders change: restic forget groups snapshots by host AND PATHS by default, so the
        /// snapshots taken with the old folder list form their own group whose newest snapshot is kept for ever — the
        /// set's retention ("keep the last 1 job") is never applied to them (storage/quota grows; deleted data is kept
        /// beyond the retention the customer chose). Expected: one snapshot of the set remains.
        /// </summary>
        [Fact]
        public void Retention_AfterTheSourcesChange_OldSnapshotsAreStillRemoved()
        {
            if (!Have) return;
            using (var rig = new Rig())
            {
                rig.Set.Retention = new RetentionPolicy { Unit = "JOBS", Period = 1 };
                File.WriteAllText(Path.Combine(rig.Src, "a.txt"), "a");
                Assert.Equal("BS_STOP_SUCCESS", rig.Runner().Backup().Result);
                var more = Path.Combine(rig.Root, "more"); Directory.CreateDirectory(more); File.WriteAllText(Path.Combine(more, "b.txt"), "b");
                rig.Set.Sources.Add(more);                                   // the customer adds a folder to the set
                Assert.Equal("BS_STOP_SUCCESS", rig.Runner().Backup().Result);
                Assert.Equal("BS_STOP_SUCCESS", rig.Runner().Backup().Result);
                var ids = rig.SnapshotIds();
                Assert.True(ids.Count == 1, "retention keep-last 1: restic still holds " + ids.Count + " snapshots of the set:\n" + rig.Oracle(null, "snapshots"));
            }
        }

        // ------------------------------------------------------------------------------------------------ RS-03 restore

        /// <summary>
        /// Restore of names with Unicode (Hebrew, emoji, combining marks), spaces at the ends and inside, and a 255-byte
        /// name in a path over 400 bytes — whole point and single file (restic --include). Passing coverage test (RS-03,
        /// names, component). Can fail: a manifest difference (any byte, any missing file) fails the Equal.
        /// </summary>
        [Fact]
        public void Restore_UnicodeSpacesAndLongNames_ByteIdentical_WholeAndSingleFile()
        {
            if (!Have) return;
            using (var rig = new Rig())
            {
                var deep = rig.Src;
                for (int i = 0; i < 3; i++) deep = Path.Combine(deep, "תיקייה עמוקה " + i + new string('d', 90));
                Directory.CreateDirectory(deep);
                var longName = new string('ש', 120) + "1234567890.txt";          // 120 × 2 bytes + 14 = 254 bytes
                Assert.True(Encoding.UTF8.GetByteCount(longName) <= 255);
                File.WriteAllText(Path.Combine(deep, longName), "long");
                File.WriteAllText(Path.Combine(rig.Src, " leading and trailing .txt "), "spaces");
                File.WriteAllText(Path.Combine(rig.Src, "été 🎉 ünï.txt"), "nfd+nfc+emoji");
                File.WriteAllText(Path.Combine(rig.Src, "semi;colon,comma'quote\".txt"), "punct");
                var big = new byte[700 * 1024]; new Random(7).NextBytes(big); File.WriteAllBytes(Path.Combine(deep, "big.bin"), big);
                Assert.True(Encoding.UTF8.GetByteCount(Path.Combine(deep, longName)) > 400);

                Assert.Equal("BS_STOP_SUCCESS", rig.Runner().Backup().Result);
                var all = Path.Combine(rig.Root, "r-all");
                rig.Runner().RestoreMany(null, all, null, new List<string>());
                Assert.Equal(Manifest(rig.Src).OrderBy(x => x.Key, StringComparer.Ordinal), Manifest(AgentRig.Under(all, rig.Src)).OrderBy(x => x.Key, StringComparer.Ordinal));

                var one = Path.Combine(rig.Root, "r-one");
                var path = rig.Runner().Ls(null).Select(x => x["path"]).Single(p => p.EndsWith(longName, StringComparison.Ordinal));
                rig.Runner().RestoreMany(null, one, new[] { path }, new List<string>());
                var files = Directory.GetFiles(one, "*", SearchOption.AllDirectories);
                Assert.Single(files);
                Assert.Equal(Sha(Path.Combine(deep, longName)), Sha(files[0]));
            }
        }

        /// <summary>
        /// RS-03: a restore whose chosen path is not in the point restores NOTHING — restic exits 0 ("Restored 0 files"),
        /// and RestoreMany returns as a success (the CLI prints "restored (restic) to …", the window shows "OK").
        /// Expected: a restore that wrote no file is a clear failure.
        /// </summary>
        [Fact]
        public void Restore_PathNotInThePoint_IsNotReportedAsSuccess()
        {
            if (!Have) return;
            using (var rig = new Rig())
            {
                File.WriteAllText(Path.Combine(rig.Src, "a.txt"), "a");
                Assert.Equal("BS_STOP_SUCCESS", rig.Runner().Backup().Result);
                var target = Path.Combine(rig.Root, "r");
                Exception thrown = null;
                try { rig.Runner().RestoreMany(null, target, new[] { Path.Combine(rig.Src, "no-such-file.txt") }, new List<string>()); }
                catch (AgentException e) { thrown = e; }
                var written = Directory.Exists(target) ? Directory.GetFiles(target, "*", SearchOption.AllDirectories).Length : 0;
                Assert.Equal(0, written);
                Assert.True(thrown != null, "restic restored 0 files and the product reported success");
            }
        }
    }
}
