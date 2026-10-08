using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using OnlineBackup.Core;
using Xunit;
using Xunit.Abstractions;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// PROTOTYPE of the .NET Framework 4.8 migration (docs/NET48-MIGRATION-PLAN.md): the agent EXE itself runs natively on
    /// Windows as a separate process (as customers run it), against the real test server behind a TLS 1.2-only front with a
    /// pinned self-signed certificate. The exe is the build of this run (src/Agent/bin/Debug/&lt;tfm&gt;) or, in the certified
    /// pipeline, the file named by OB_AGENT_EXE_NET40 / OB_AGENT_EXE_NET48; its SHA-256 is written to the test output, and
    /// when OB_AGENT_SHA256_NET48 (or _NET40) is set the test refuses any other bytes ("the tested binary is the shipped one").
    /// ORACLE: the agent's exit codes and its counters (new/upd/perm/del), and every restored file's SHA-256 against the source.
    /// </summary>
    public class Net48NativeTests
    {
        readonly ITestOutputHelper log;
        public Net48NativeTests(ITestOutputHelper log) { this.log = log; }

        static string Sha(string f) { return Bytes.Hex(Bytes.Sha256(File.ReadAllBytes(f))); }

        /// <summary>The agent exe of a target framework; NOT TESTED when this machine cannot run it.</summary>
        string Exe(string tfm)
        {
            if (!OperatingSystem.IsWindows()) throw NotTested.Because("the .NET Framework agent runs natively only on Windows");
            var given = Environment.GetEnvironmentVariable("OB_AGENT_EXE_" + tfm.ToUpperInvariant());
            var exe = !string.IsNullOrEmpty(given) ? given : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Agent", "bin", "Debug", tfm, "OnlineBackup.Agent.exe"));
            if (!File.Exists(exe)) throw NotTested.Because("the " + tfm + " agent build is not there: " + exe);
            var sha = Sha(exe);
            log.WriteLine(tfm + " agent: " + exe + " sha256=" + sha);
            var want = Environment.GetEnvironmentVariable("OB_AGENT_SHA256_" + tfm.ToUpperInvariant());
            if (!string.IsNullOrEmpty(want)) Assert.True(string.Equals(want, sha, StringComparison.OrdinalIgnoreCase), "the " + tfm + " agent under test is not the certified one: " + sha + " != " + want);
            return exe;
        }

        sealed class Agent
        {
            public string Exe, Home; public ITestOutputHelper Log;
            public int Code(string args, out string output)
            {
                var p = Process.Start(new ProcessStartInfo(Exe, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
                var err = p.StandardError.ReadToEndAsync();
                var o = p.StandardOutput.ReadToEnd() + err.Result;
                p.WaitForExit();
                output = o.Trim();
                Log.WriteLine("[" + Path.GetFileName(Path.GetDirectoryName(Exe)) + "] " + args.Split(' ')[0] + " -> " + p.ExitCode + ": " + output);
                return p.ExitCode;
            }
            public string Run(string args)
            {
                string o; var c = Code(args, out o);
                Assert.True(c == 0, args.Split(' ')[0] + " failed (" + c + "): " + o);
                return o;
            }
            public string Backup(string setId) { var o = Run("backup --home \"" + Home + "\" --set " + setId); Assert.StartsWith("BS_STOP_SUCCESS", o); return o.Split('\n')[0].Trim(); }
            public string Restore(string setId, string target) { return Run("restore --home \"" + Home + "\" --set " + setId + " --password Customer-Pass-1 --target \"" + target + "\""); }
        }

        /// <summary>A registered agent with one set over src; the server and the front live for the using block.</summary>
        string Register(Env env, TlsFront front, Agent a, string login, string src)
        {
            env.CreateUser(login, "Customer-Pass-1");
            a.Run("register --home \"" + a.Home + "\" --server " + front.Url + " --login " + login + " --password Customer-Pass-1 --computer WIN-" + login.ToUpperInvariant() + " --pin " + front.Fingerprint);
            return a.Run("addset --home \"" + a.Home + "\" --password Customer-Pass-1 --name Docs --source \"" + src + "\"").Split('\n').Last().Trim();
        }

        static Dictionary<string, string> Tree(string dir)
        {
            var b = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
            return Directory.GetFiles(dir, "*", SearchOption.AllDirectories).ToDictionary(f => Path.GetFullPath(f).Substring(b.Length), Sha, StringComparer.OrdinalIgnoreCase);
        }

        static void SameTree(string src, string restored)
        {
            var a = Tree(src); var b = Directory.Exists(restored) ? Tree(restored) : new Dictionary<string, string>();
            foreach (var kv in a)
            {
                string got;
                Assert.True(b.TryGetValue(kv.Key, out got), "not restored: " + kv.Key + " (restored: " + string.Join(", ", b.Keys) + ")");
                Assert.True(kv.Value == got, "restored with other bytes: " + kv.Key);
            }
            Assert.True(a.Count == b.Count, "restored " + b.Count + " files, the source has " + a.Count);
        }

        /// <summary>The same journey as TlsTests.Net40Agent_NativeOnWindows_* on the net48 build: register → set → backup → restore, byte-identical, through a TLS 1.2-only front.</summary>
        [Fact]
        public void Net48Agent_NativeOnWindows_Tls12OnlyServer_BacksUpAndRestoresIdentical()
        {
            var exe = Exe("net48");
            using (var env = new Env())
            using (var front = new TlsFront(new Uri(env.Url).Port))
            {
                var src = env.Dir("src");
                var bytes = new byte[300 * 1024]; new Random(48).NextBytes(bytes);
                File.WriteAllBytes(Path.Combine(src, "ledger.bin"), bytes);
                File.WriteAllText(Path.Combine(src, "note.txt"), "net48 build on Windows");
                var a = new Agent { Exe = exe, Home = Path.Combine(env.Root, "net48home"), Log = log };
                var setId = Register(env, front, a, "net48win", src);
                a.Backup(setId);
                var target = Path.Combine(env.Root, "net48restore");
                Assert.Contains("restored=2", a.Restore(setId, target));
                SameTree(src, AgentRig.Under(target, src));
            }
        }

        /// <summary>
        /// The upgrade risk of the migration: the local state and index written by the SHIPPED net40 agent are read by the
        /// net48 agent (same folder, as an in-place upgrade leaves them). Unchanged files must stay unchanged (nothing new,
        /// updated, permission-only or deleted: a different path normalisation would show here as new + deleted), one changed
        /// file must be exactly one update, the restore must be byte-identical, and the way back (net48 → net40, the rollback)
        /// must also see no change. The source holds names that path normalisation touches: spaces, Hebrew, mixed case, a
        /// dot inside, read-only and hidden files, a deep folder, and the characters whose URI escaping differs for 4.5+ targets (' ( ) ! and others); on the hosted runners the temp folder itself is an 8.3 name.
        /// </summary>
        [Fact]
        public void Net40LocalState_UpgradedToNet48_NothingChangesTwice_AndBackAgain()
        {
            var exe40 = Exe("net40"); var exe48 = Exe("net48");
            using (var env = new Env())
            using (var front = new TlsFront(new Uri(env.Url).Port))
            {
                try { Upgrade(env, front, exe40, exe48); }
                finally
                {
                    // Env.Dispose deletes its folder and stops only at an IOException: a read-only file (source or restored) would
                    // throw UnauthorizedAccessException there and hide this test's own outcome (run 37768479211)
                    foreach (var f in Directory.GetFiles(env.Root, "*", SearchOption.AllDirectories))
                        try { File.SetAttributes(f, FileAttributes.Normal); } catch (Exception) { }
                }
            }
        }

        void Upgrade(Env env, TlsFront front, string exe40, string exe48)
        {
            {
                var src = env.Dir("src");
                var files = new[] { "plain.txt", "With Spaces\\a b.txt", "עברית\\מסמך.txt", "MixedCase\\ReadMe.TXT", "dots\\v1.2.final.doc", "deep\\" + string.Join("\\", Enumerable.Range(0, 8).Select(i => "level" + i)) + "\\leaf.bin", "ro.txt", "hidden.txt", "escape\\it's (1)! [x] 50% a+b=c;d,e~f.txt" };
                int n = 0;
                foreach (var f in files) { var p = Path.Combine(src, f); Directory.CreateDirectory(Path.GetDirectoryName(p)); File.WriteAllText(p, "content " + (n++) + " of " + f, Encoding.UTF8); }
                File.SetAttributes(Path.Combine(src, "ro.txt"), FileAttributes.ReadOnly);
                File.SetAttributes(Path.Combine(src, "hidden.txt"), FileAttributes.Hidden);
                var home = Path.Combine(env.Root, "home");
                var a40 = new Agent { Exe = exe40, Home = home, Log = log };
                var a48 = new Agent { Exe = exe48, Home = home, Log = log };
                var setId = Register(env, front, a40, "upgrade48", src);
                Assert.Contains("new=" + files.Length + " ", a40.Backup(setId));
                Assert.Contains(" new=0 upd=0 perm=0 del=0 ", a48.Backup(setId) + " ");
                File.WriteAllText(Path.Combine(src, "With Spaces\\a b.txt"), "changed after the upgrade", Encoding.UTF8);
                Assert.Contains(" new=0 upd=1 perm=0 del=0 ", a48.Backup(setId) + " ");
                var target = Path.Combine(env.Root, "restore48");
                a48.Restore(setId, target);
                SameTree(src, AgentRig.Under(target, src));
                // rollback: the shipped build reads what net48 wrote
                Assert.Contains(" new=0 upd=0 perm=0 del=0 ", a40.Backup(setId) + " ");
            }
        }

        /// <summary>
        /// Research finding (א): the ACL read exists only in the .NET Framework build and was never tested. A permission-only
        /// change (an explicit read grant for BUILTIN\Users, by SID) must be one permission-only version and nothing else.
        /// </summary>
        [Theory]
        [InlineData("net40")]
        [InlineData("net48")]
        public void PermissionOnlyChange_IsOnePermVersion(string tfm)
        {
            var exe = Exe(tfm);
            using (var env = new Env())
            using (var front = new TlsFront(new Uri(env.Url).Port))
            {
                var src = env.Dir("src");
                var f = Path.Combine(src, "acl.txt"); File.WriteAllText(f, "permissions only");
                File.WriteAllText(Path.Combine(src, "other.txt"), "untouched");
                var a = new Agent { Exe = exe, Home = Path.Combine(env.Root, "home"), Log = log };
                var setId = Register(env, front, a, "acl" + tfm, src);
                Assert.Contains("new=2 ", a.Backup(setId));
                var p = Process.Start(new ProcessStartInfo("icacls.exe", "\"" + f + "\" /grant *S-1-5-32-545:(R)") { UseShellExecute = false, RedirectStandardOutput = true });
                var io = p.StandardOutput.ReadToEnd(); p.WaitForExit();
                if (p.ExitCode != 0) throw NotTested.Because("icacls could not change the file's permissions: " + io);
                Assert.Contains(" new=0 upd=0 perm=1 del=0 ", a.Backup(setId) + " ");
            }
        }

        /// <summary>
        /// Research finding (א): long paths. A file whose full path is over 300 characters (MAX_PATH is 260) is backed up
        /// and restored byte-identical, with nothing reported failed - or the test fails and says what the agent did.
        /// </summary>
        [Theory]
        [InlineData("net40")]
        [InlineData("net48")]
        public void LongPath_Over260_BackedUpAndRestoredIdentical(string tfm)
        {
            var exe = Exe(tfm);
            using (var env = new Env())
            using (var front = new TlsFront(new Uri(env.Url).Port))
            {
                var src = env.Dir("src");
                File.WriteAllText(Path.Combine(src, "short.txt"), "short");
                var dir = src; int i = 0;
                while (dir.Length < 300) dir = Path.Combine(dir, "a-rather-long-folder-name-" + (i++));
                Directory.CreateDirectory(dir);
                var longFile = Path.Combine(dir, "long-file.bin");
                var bytes = new byte[50 * 1024]; new Random(260).NextBytes(bytes);
                File.WriteAllBytes(longFile, bytes);
                log.WriteLine("long file path: " + longFile.Length + " characters");
                var a = new Agent { Exe = exe, Home = Path.Combine(env.Root, "home"), Log = log };
                var setId = Register(env, front, a, "lp" + tfm, src);
                string o; var c = a.Code("backup --home \"" + a.Home + "\" --set " + setId, out o);
                Assert.True(c == 0 && o.StartsWith("BS_STOP_SUCCESS"), "backup (" + c + "): " + o);
                Assert.Contains("new=2 ", o);
                var target = Path.Combine(env.Root, "r");
                var r = a.Restore(setId, target);
                Assert.Contains("restored=2 failed=0", r);
                SameTree(src, AgentRig.Under(target, src));
            }
        }
    }
}
