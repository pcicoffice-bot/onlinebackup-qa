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
    /// <summary>The BK-06 tests that change the machine (the Volume Shadow Copy service, drive letters, a killed agent): alone,
    /// never beside another test that may take a snapshot.</summary>
    [CollectionDefinition("VssExclusive", DisableParallelization = true)]
    public class VssExclusiveCollection { }

    /// <summary>The shipped agent (.NET 4.0 build) run as a program, as the customer's computer runs it.</summary>
    static class AgentCli
    {
        public static string Exe()
        {
            var exe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Agent", "bin", "Debug", "net40", "OnlineBackup.Agent.exe"));
            if (!File.Exists(exe)) throw NotTested.Because("the .NET 4.0 agent build is not there: " + exe);
            return exe;
        }

        public static Process Start(string args)
        {
            return Process.Start(new ProcessStartInfo(Exe(), args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
        }

        /// <summary>Runs one command to its end; returns exit code and output.</summary>
        public static KeyValuePair<int, string> Run(string args)
        {
            using (var p = Start(args))
            {
                var err = p.StandardError.ReadToEndAsync();
                var o = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(600000)) { p.Kill(true); throw new TimeoutException("agent " + args.Split(' ')[0] + " did not end in 10 minutes"); }
                return new KeyValuePair<int, string>(p.ExitCode, (o + err.Result).Trim());
            }
        }

        public static string Ok(string args)
        {
            var r = Run(args);
            Assert.True(r.Key == 0, args.Split(' ')[0] + " failed (" + r.Key + "): " + r.Value);
            return r.Value;
        }
    }

    /// <summary>
    /// BK-06 adversarial (component: the agent's BackupRun against the stand-in server, real Windows as administrator).
    /// The Volume Shadow Copy service is unavailable; the source is on a drive letter without a volume of its own (subst);
    /// two sets on the same volume start at the same moment. Contract (specs.py BK-06 + Vss.cs): a run without its snapshot
    /// never claims a plain success; nothing is left behind; whatever was read restores identical (SHA-256).
    /// </summary>
    [Collection("VssExclusive")]
    public class PilotVssChallengeComponentTests
    {
        readonly ITestOutputHelper output;
        public PilotVssChallengeComponentTests(ITestOutputHelper output) { this.output = output; }

        [Fact]
        public void TheShadowCopyServiceIsDisabled_TheRunSaysSo_IsNotAPlainSuccess_TheOtherFilesRestoreIdentical_AndOnceBackTheNextRunCompletes()
        {
            PilotVss.Need();
            using (var rig = new AgentRig())
            {
                rig.Set.Vss = true;
                var a = rig.File("a.txt", "alpha");
                var locked = rig.File("ledger.mdb", PilotRig.Rnd(200000, 46));
                var vol = PilotVss.Volume(rig.Src);
                var linksBefore = PilotVss.Links();
                var shadowsBefore = PilotVss.Shadows(vol);
                BackupRun r;
                using (PilotVss.VssServiceDisabled())
                using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    r = rig.Backup();
                output.WriteLine("result with the service disabled: " + r.Result + "\n" + string.Join("\n", r.LogLines));
                Assert.NotEqual("BS_STOP_SUCCESS", r.Result);                                    // never a silent success
                Assert.StartsWith("BS_STOP_SUCCESS_WITH_", r.Result);                            // the run went on (Vss.cs: "the run continues")
                Assert.DoesNotContain(r.LogLines, l => l.Contains("Shadow Copy Set successfully created"));
                Assert.Contains(r.LogLines, l => l.Contains("Shadow Copy of " + vol));           // the reason, with the volume
                var got = rig.Restored("p1");
                Assert.Equal(AgentRig.Sha(a), got["a.txt"]);                                     // what could be read restores identical
                Assert.Equal(linksBefore, PilotVss.Links());
                Assert.Equal(shadowsBefore, PilotVss.Shadows(vol));

                var r2 = rig.Backup();                                                            // the service is back (manual start)
                Assert.True(r2.Result == "BS_STOP_SUCCESS", r2.Result + "\n" + string.Join("\n", r2.LogLines));
                Assert.Contains(r2.LogLines, l => l.Contains("Shadow Copy Set successfully created"));
                Assert.Equal(AgentRig.Tree(rig.Src), rig.Restored("p2"));
                Assert.Equal(shadowsBefore, PilotVss.Shadows(vol));
            }
        }

        /// <summary>A source on a subst drive letter (a folder shown as a drive, no volume of its own). What happens is
        /// written to the test output; either way: no silent success without the held file, nothing left behind.</summary>
        [Fact]
        public void ASourceOnASubstDrive_EitherIsReadThroughASnapshot_OrTheRunSaysItHadNone_NothingIsLeftBehind()
        {
            PilotVss.Need();
            using (var rig = new AgentRig())
            {
                var a = rig.File("a.txt", "alpha");
                var locked = rig.File("ledger.mdb", PilotRig.Rnd(200000, 47));
                var letter = PilotVss.FreeLetter();
                var under = PilotVss.Volume(rig.Src);
                var shadowsBefore = PilotVss.Shadows(under); var linksBefore = PilotVss.Links();
                BackupRun r;
                using (PilotVss.Subst(letter, rig.Src))
                {
                    rig.Set.Vss = true; rig.Set.Sources.Clear(); rig.Set.Sources.Add(letter + "\\");
                    using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) r = rig.Backup();
                }
                var snap = r.LogLines.Any(l => l.Contains("Shadow Copy Set successfully created"));
                output.WriteLine("subst " + letter + ": snapshot=" + snap + " result=" + r.Result + "\n" + string.Join("\n", r.LogLines));
                var target = Path.Combine(rig.Root, "restore");
                new Restore(rig.Server.Point(rig.Key), rig.Key, Path.Combine(rig.Root, "rtmp")).Run(null, target, null, false);
                var got = AgentRig.Tree(AgentRig.Under(target, letter + "\\"));
                if (snap)
                {
                    Assert.True(r.Result == "BS_STOP_SUCCESS", r.Result + "\n" + string.Join("\n", r.LogLines));
                    Assert.Equal(AgentRig.Tree(rig.Src), got);
                }
                else
                {
                    Assert.NotEqual("BS_STOP_SUCCESS", r.Result);
                    Assert.Contains(r.LogLines, l => l.Contains("Shadow Copy of " + letter));
                    Assert.Equal(AgentRig.Sha(a), got["a.txt"]);
                }
                Assert.Equal(linksBefore, PilotVss.Links());
                Assert.Equal(shadowsBefore, PilotVss.Shadows(under));
            }
        }

        /// <summary>Two sets on the same volume start together (the same schedule): each takes its snapshot, sends its held
        /// file, restores identical, and both snapshots are gone afterwards.</summary>
        [Fact]
        public void TwoSetsOnTheSameVolumeAtTheSameMoment_EachBacksUpItsHeldFile_RestoresIdentical_NoShadowCopyLeft()
        {
            PilotVss.Need();
            using (var one = new AgentRig())
            using (var two = new AgentRig())
            {
                one.Set.Vss = true; two.Set.Vss = true;
                one.File("a.txt", "alpha"); var l1 = one.File("one.mdb", PilotRig.Rnd(200000, 48));
                two.File("b.txt", "beta"); var l2 = two.File("two.mdb", PilotRig.Rnd(200000, 49));
                var want1 = AgentRig.Tree(one.Src); var want2 = AgentRig.Tree(two.Src);
                var vol = PilotVss.Volume(one.Src);
                Assert.Equal(vol, PilotVss.Volume(two.Src));
                var shadowsBefore = PilotVss.Shadows(vol); var linksBefore = PilotVss.Links();
                BackupRun r1 = null, r2 = null;
                using (new FileStream(l1, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                using (new FileStream(l2, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    var go = new Barrier(2);
                    var t1 = new Thread(() => { go.SignalAndWait(); r1 = one.Backup(); });
                    var t2 = new Thread(() => { go.SignalAndWait(); r2 = two.Backup(); });
                    t1.Start(); t2.Start(); t1.Join(); t2.Join();
                }
                output.WriteLine("set 1: " + r1.Result + "\n" + string.Join("\n", r1.LogLines) + "\nset 2: " + r2.Result + "\n" + string.Join("\n", r2.LogLines));
                Func<BackupRun, string> why = r => r.Result + " " + string.Join(" | ", r.LogLines.Where(l => l.Contains(",warn,") || l.Contains(",err,")));
                Assert.True(r1.Result == "BS_STOP_SUCCESS", "set 1: " + why(r1));
                Assert.True(r2.Result == "BS_STOP_SUCCESS", "set 2: " + why(r2));
                Assert.Equal(want1, one.Restored());
                Assert.Equal(want2, two.Restored());
                Assert.Equal(linksBefore, PilotVss.Links());
                Assert.Equal(shadowsBefore, PilotVss.Shadows(vol));
            }
        }
    }

    /// <summary>
    /// BK-06 integration, failure: the Volume Shadow Copy service is disabled; the run goes through the real server. The
    /// server's history and its log of the run must show it was not a plain success and why (the volume named).
    /// </summary>
    [Collection("VssExclusive")]
    public class PilotVssFailureIntegrationTests
    {
        static string UserDir(Env env, string login) { return Directory.GetDirectories(env.HomeA, login, SearchOption.AllDirectories).First(); }

        [Fact]
        public void TheShadowCopyServiceIsDisabled_TheServerRecordsTheRunAsNotAPlainSuccess_WithTheReason_TheReadableFilesRestoreIdentical()
        {
            PilotVss.Need();
            using (var env = new Env())
            {
                env.CreateUser("pvssoff", PilotEnv.Pass);
                var app = env.Agent("pvssoff", PilotEnv.Pass);
                var src = env.Dir("src");
                var a = Path.Combine(src, "a.txt"); File.WriteAllText(a, "alpha");
                var locked = Path.Combine(src, "ledger.mdb"); File.WriteAllBytes(locked, PilotRig.Rnd(300000, 86));
                var set = app.CreateSet(app.Interactive(PilotEnv.Pass, null), PilotEnv.Pass, new BackupSetInfo { Name = "Files", Sources = { src }, Vss = true });
                var vol = PilotVss.Volume(src);
                var shadowsBefore = PilotVss.Shadows(vol); var linksBefore = PilotVss.Links();
                BackupRun r;
                using (PilotVss.VssServiceDisabled())
                using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    r = app.Backup(set.Id);
                Assert.NotEqual("BS_STOP_SUCCESS", r.Result);
                Assert.StartsWith("BS_STOP_SUCCESS_WITH_", r.Result);
                var runs = env.Admin().Call("GET", "/api/admin/users/pvssoff/sets/" + set.Id + "/runs").List("runs");
                var h = Assert.Single(runs, x => x["job"] == r.Job);
                Assert.Equal(r.Result, h["result"]);                                              // the server shows what the agent saw
                var serverLog = Path.Combine(UserDir(env, "pvssoff"), "logs", set.Id, "Backup", r.Job + ".log");
                Assert.True(File.Exists(serverLog), serverLog);
                Assert.Contains(Atomic.ReadAllLines(serverLog), l => l.Contains("Shadow Copy of " + vol));
                var got = PilotEnv.Restored(env, app, set.Id, src, null, "restore");
                Assert.Equal(AgentRig.Sha(a), got["a.txt"]);
                Assert.Equal(linksBefore, PilotVss.Links());
                Assert.Equal(shadowsBefore, PilotVss.Shadows(vol));
            }
        }
    }

    /// <summary>
    /// BK-06 end-to-end: the shipped agent program (.NET 4.0 build, its command line) against the real server, as on the
    /// customer's computer - register, add a set, back up while another program holds a file open with an exclusive lock,
    /// restore, compare SHA-256; no shadow copy and no snapshot link left. And the agent killed while its snapshot exists:
    /// the next run must leave no shadow copy behind (the contract: "the shadow copy is removed afterwards, also on failure").
    /// </summary>
    [Collection("VssExclusive")]
    public class PilotVssEndToEndTests
    {
        static Dictionary<string, string> RestoreCli(string home, string setId, string src, string target)
        {
            var o = AgentCli.Ok("restore --home \"" + home + "\" --set " + setId + " --password " + PilotEnv.Pass + " --target \"" + target + "\"");
            Assert.Contains("failed=0", o);
            return AgentRig.Tree(AgentRig.Under(target, src));
        }

        static string Register(Env env, string login, string home, string src)
        {
            env.CreateUser(login, PilotEnv.Pass);
            AgentCli.Ok("register --home \"" + home + "\" --server " + env.Url + " --login " + login + " --password " + PilotEnv.Pass + " --computer WIN-VSS");
            return AgentCli.Ok("addset --home \"" + home + "\" --password " + PilotEnv.Pass + " --name Docs --source \"" + src + "\"").Split('\n').Last().Trim();
        }

        [Fact]
        public void TheShippedAgent_BacksUpAFileHeldOpenByAnotherProgram_RestoresIdentical_SendsItsChange_ThenNothing_NoShadowCopyLeft()
        {
            PilotVss.Need();
            AgentCli.Exe();
            using (var env = new Env())
            {
                var home = Path.Combine(env.Root, "agenthome");
                var src = env.Dir("src");
                File.WriteAllText(Path.Combine(src, "note.txt"), "open files");
                var locked = Path.Combine(src, "mail.pst"); File.WriteAllBytes(locked, PilotRig.Rnd(400000, 87));
                var vol = PilotVss.Volume(src);
                var shadowsBefore = PilotVss.Shadows(vol); var linksBefore = PilotVss.Links();
                var setId = Register(env, "pvsse2e", home, src);
                using (var hold = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))   // the mail program
                {
                    Assert.Throws<IOException>(() => File.ReadAllBytes(locked));
                    var o1 = AgentCli.Ok("backup --home \"" + home + "\" --set " + setId);
                    Assert.StartsWith("BS_STOP_SUCCESS new=2 ", o1);
                    Assert.Equal(shadowsBefore, PilotVss.Shadows(vol));
                    Assert.Equal(linksBefore, PilotVss.Links());

                    var v2 = PilotRig.Rnd(410000, 88);                                            // the program writes, still holding it
                    hold.SetLength(0); hold.Write(v2, 0, v2.Length); hold.Flush(true);
                }
                var want = AgentRig.Tree(src);
                using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    Thread.Sleep(1100);                                                       // a run id of its own
                    var o2 = AgentCli.Ok("backup --home \"" + home + "\" --set " + setId);
                    Assert.StartsWith("BS_STOP_SUCCESS new=0 upd=1 ", o2);
                    Thread.Sleep(1100);
                    var o3 = AgentCli.Ok("backup --home \"" + home + "\" --set " + setId);
                    Assert.StartsWith("BS_STOP_SUCCESS new=0 upd=0 perm=0 del=0 ", o3);
                }
                Assert.Equal(want, RestoreCli(home, setId, src, Path.Combine(env.Root, "restored")));
                Assert.Equal(shadowsBefore, PilotVss.Shadows(vol));
                Assert.Equal(linksBefore, PilotVss.Links());
            }
        }

        [Fact]
        public void TheAgentKilledWhileItsSnapshotExists_TheNextRunLeavesNoShadowCopy_AndRestoresIdentical()
        {
            PilotVss.Need();
            AgentCli.Exe();
            using (var env = new Env())
            {
                var home = Path.Combine(env.Root, "agenthome");
                var src = env.Dir("src");
                File.WriteAllBytes(Path.Combine(src, "big.bin"), PilotRig.Rnd(4 * 1024 * 1024, 89));
                var locked = Path.Combine(src, "ledger.mdb"); File.WriteAllBytes(locked, PilotRig.Rnd(200000, 90));
                var vol = PilotVss.Volume(src);
                var shadowsBefore = PilotVss.Shadows(vol); var linksBefore = PilotVss.Links();
                var setId = Register(env, "pvsskill", home, src);
                PilotEnv.Change(env, "pvsskill", setId, s => { s.BandwidthKbps = 64; s.Compression = "NONE"; });   // a slow run (about a minute)
                var leftover = new HashSet<string>();
                try
                {
                    using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    {
                        using (var p = AgentCli.Start("backup --home \"" + home + "\" --set " + setId))
                        {
                            p.StandardOutput.ReadToEndAsync(); p.StandardError.ReadToEndAsync();
                            var until = DateTime.UtcNow.AddMinutes(3);
                            while (!PilotVss.Shadows(vol).Except(shadowsBefore).Any() && !p.HasExited && DateTime.UtcNow < until) Thread.Sleep(300);
                            Assert.False(p.HasExited, "the run ended before it was killed (exit " + (p.HasExited ? p.ExitCode : 0) + ")");
                            Thread.Sleep(2000);                                                   // well inside the upload
                            Assert.False(p.HasExited, "the run ended before it was killed");
                            p.Kill(true); p.WaitForExit();                                         // power cut / task manager
                        }
                        foreach (var s in PilotVss.Shadows(vol).Except(shadowsBefore)) leftover.Add(s);
                        Assert.True(leftover.Count > 0, "precondition: the killed run's shadow copy exists");
                    }
                    PilotEnv.Change(env, "pvsskill", setId, s => s.BandwidthKbps = 0);
                    Thread.Sleep(1100);
                    var o = AgentCli.Run("backup --home \"" + home + "\" --set " + setId);
                    Assert.True(o.Value.StartsWith("BS_STOP_SUCCESS", StringComparison.Ordinal), o.Value);
                    var still = PilotVss.Shadows(vol).Except(shadowsBefore).ToList();
                    Assert.True(still.Count == 0, "the shadow copy of the killed run is still on the volume after the next run: " + string.Join(", ", still));
                    Assert.Equal(linksBefore, PilotVss.Links());
                    Assert.Equal(AgentRig.Tree(src), RestoreCli(home, setId, src, Path.Combine(env.Root, "restored")));
                }
                finally
                {
                    var now = PilotVss.Shadows(vol);
                    PilotVss.DeleteShadows(now.Except(shadowsBefore));                            // the machine stays clean either way
                    foreach (var l in PilotVss.Links().Except(linksBefore)) try { Directory.Delete(l); } catch (Exception) { }
                }
            }
        }
    }
}
