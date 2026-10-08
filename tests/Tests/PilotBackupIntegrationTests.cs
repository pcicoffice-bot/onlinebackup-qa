using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>Shared helpers of the pilot integration tests: the real server in-process (Env), real HTTP, real files.</summary>
    static class PilotEnv
    {
        public const string Pass = "Customer-Pass-1";

        /// <summary>The administrator changes a set on the server (as the admin site does); the agent reads it at its next run.</summary>
        public static void Change(Env env, string login, string setId, Action<BackupSetInfo> edit)
        {
            var admin = env.Admin();
            var s = BackupSetInfo.FromXml(XElement.Parse(admin.Call("GET", "/api/admin/users/" + login + "/sets/" + setId)["set"]));
            edit(s);
            admin.Call("POST", "/api/admin/users/" + login + "/sets/" + setId, new Msg().Set("set", s.ToXml().ToString()));
        }

        /// <summary>Restores a point (null = the newest) through the server into a new folder; SHA-256 by path relative to the source.</summary>
        public static Dictionary<string, string> Restored(Env env, AgentApp app, string setId, string src, string point, string name)
        {
            var target = env.Dir(name);
            var r = app.RestoreFor(app.Interactive(Pass, null), setId);
            r.Run(point, target, null, false);
            Assert.Equal(0, r.Failed);
            return AgentRig.Tree(AgentRig.Under(target, src));
        }

        public static List<string> Points(AgentApp app, string setId) { return app.RestoreFor(app.Interactive(Pass, null), setId).Points(); }
    }

    /// <summary>
    /// BK-02 integration: a delta chain through the real server. Contract (specs.py BK-02): each delta is small; every point
    /// restores identical; a change too large for a delta (over MAX_DELTA_RATIO) is sent as a new full copy. Oracle: the
    /// bytes sent, the object files on the server's disk, SHA-256 of every point restored through the server.
    /// </summary>
    public class PilotDeltaChainIntegrationTests
    {
        [Fact]
        public void AChangeOverTheDeltaRatio_IsSentAsANewFullCopy_ASmallOneAsADelta_EveryPointRestoresIdentical()
        {
            using (var env = new Env())
            {
                env.CreateUser("pchain", PilotEnv.Pass);
                var app = env.Agent("pchain", PilotEnv.Pass);
                var src = env.Dir("src"); var path = Path.Combine(src, "db.mdf");
                const int size = 16 * 1024 * 1024;
                var versions = new List<byte[]> { PilotRig.Rnd(size, 51) };
                File.WriteAllBytes(path, versions[0]);
                var set = app.CreateSet(app.Interactive(PilotEnv.Pass, null), PilotEnv.Pass,
                    new BackupSetInfo { Name = "Chain", Sources = { src }, MinDeltaFileSize = 1024 * 1024, MaxDeltaNo = 10, MaxDeltaRatio = 50, Compression = "NONE", Vss = false });
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);

                System.Threading.Thread.Sleep(1100);                                                   // a run id of its own
                var v1 = versions[0].Take(8 * 1024 * 1024).Concat(PilotRig.Rnd(2048, 52)).Concat(versions[0].Skip(8 * 1024 * 1024 + 512)).ToArray();
                File.WriteAllBytes(path, v1); versions.Add(v1);
                var r2 = app.Backup(set.Id);
                Assert.Equal("BS_STOP_SUCCESS", r2.Result);
                Assert.True(r2.BytesSent < size / 4, "a 2 KB change sent " + r2.BytesSent + " bytes");
                var current = Path.Combine(env.HomeA, "pchain", "files", set.Id, "Current");
                Assert.Single(Directory.GetFiles(current, "*.001", SearchOption.AllDirectories));    // a delta beside its full copy

                System.Threading.Thread.Sleep(1100);
                var v2 = PilotRig.Rnd(10 * 1024 * 1024, 53).Concat(v1.Skip(10 * 1024 * 1024)).ToArray();   // ~60 % new: over the 50 % ratio
                File.WriteAllBytes(path, v2); versions.Add(v2);
                var r3 = app.Backup(set.Id);
                Assert.Equal("BS_STOP_SUCCESS", r3.Result);
                Assert.True(r3.BytesSent >= v2.Length, "a change over the ratio sent only " + r3.BytesSent + " bytes — not a full copy");
                Assert.Single(Directory.GetFiles(current, "*.000", SearchOption.AllDirectories));
                Assert.Empty(Directory.GetFiles(current, "*.001", SearchOption.AllDirectories));     // the old chain left the newest point

                var points = PilotEnv.Points(app, set.Id);
                Assert.Equal(3, points.Count);
                for (int i = 0; i < points.Count; i++)
                    Assert.Equal(PilotRig.Sha(versions[i]), PilotEnv.Restored(env, app, set.Id, src, points[i], "p" + i)["db.mdf"]);
            }
        }
    }

    /// <summary>
    /// BK-08 integration: the upload limit and compression set by the administrator on the real server, measured on a real
    /// backup. Contract (specs.py BK-08): the transfer keeps to the limit; with compression the stored size is smaller;
    /// restore identical. Oracle: a LOWER bound on the time (a slow machine cannot break it), the object files on the
    /// server's disk, SHA-256 of the restore.
    /// </summary>
    public class PilotResourcesIntegrationTests
    {
        [Fact]
        public void UploadLimitAndCompressionFromTheServer_NeverFasterThanTheLimit_TextStoredSmaller_PhotoNotBigger_RestoresIdentical()
        {
            using (var env = new Env())
            {
                env.CreateUser("plimit", PilotEnv.Pass);
                var app = env.Agent("plimit", PilotEnv.Pass);
                var src = env.Dir("src");
                File.WriteAllBytes(Path.Combine(src, "photo.jpg"), PilotRig.Rnd(1024 * 1024, 61));          // does not compress
                var text = string.Concat(Enumerable.Range(0, 40000).Select(i => "row," + i + ",the same text again\n"));
                File.WriteAllText(Path.Combine(src, "table.csv"), text);
                var set = app.CreateSet(app.Interactive(PilotEnv.Pass, null), PilotEnv.Pass, new BackupSetInfo { Name = "Files", Sources = { src }, Vss = false });
                PilotEnv.Change(env, "plimit", set.Id, s => { s.BandwidthKbps = 256; s.Compression = "MAX"; });
                var want = AgentRig.Tree(src);

                var sw = Stopwatch.StartNew();
                var r = app.Backup(set.Id);
                var took = sw.Elapsed.TotalSeconds;
                Assert.True(r.Result == "BS_STOP_SUCCESS", string.Join("\n", r.LogLines));
                Assert.Contains(r.LogLines, l => l.Contains("Upload limit 256 KB/s"));
                var floor = (double)r.BytesSent / (256 * 1024);
                Assert.True(took >= floor * 0.95, r.BytesSent + " bytes at 256 KB/s took only " + took.ToString("0.00") + " s (at least " + floor.ToString("0.00") + " s)");

                var objects = Directory.GetFiles(Path.Combine(env.HomeA, "plimit", "files", set.Id, "Current"), "*.000", SearchOption.AllDirectories).Select(f => new FileInfo(f).Length).OrderBy(x => x).ToList();
                Assert.Equal(2, objects.Count);
                Assert.True(objects[0] * 4 < text.Length, "the table is stored in " + objects[0] + " bytes of " + text.Length);   // compressed
                Assert.True(objects[1] >= 1024 * 1024 && objects[1] < 1024 * 1024 + 64 * 1024, "the photo is stored in " + objects[1] + " bytes");   // not bigger than itself
                Assert.Equal(want, PilotEnv.Restored(env, app, set.Id, src, null, "restore"));
            }
        }
    }

    /// <summary>
    /// AP-07 integration: external programs the backup runs (pre- and post-commands set on the server) that write much to
    /// both streams. Contract (specs.py AP-07): both streams read in full; never a hung backup. Oracle: the result (a hung
    /// or unread command would end as a timeout warning), the run's log, SHA-256 of the restore through the server.
    /// </summary>
    [Collection("Limits")]
    public class PilotProcessIntegrationTests
    {
        static bool Win { get { return Environment.OSVersion.Platform == PlatformID.Win32NT; } }

        [Fact]
        public void PreAndPostCommandsWritingMuchToBothStreams_AreReadToTheEnd_TheBackupSucceedsAndRestoresIdentical()
        {
            var chatty = Win
                ? "powershell -NoProfile -NonInteractive -Command \"[Console]::Error.Write('e' * 400000); [Console]::Out.Write('o' * 400000); exit 0\""
                : "yes e | head -c 400000 >&2; yes o | head -c 400000; exit 0";
            var before = Limits.Command;
            try
            {
                Limits.Command = TimeSpan.FromSeconds(90);                          // a deadlock would end as a timeout, not hang the test
                using (var env = new Env())
                {
                    env.CreateUser("pcmd", PilotEnv.Pass);
                    var app = env.Agent("pcmd", PilotEnv.Pass);
                    var src = env.Dir("src");
                    File.WriteAllText(Path.Combine(src, "a.txt"), "alpha"); File.WriteAllBytes(Path.Combine(src, "b.bin"), PilotRig.Rnd(70000, 71));
                    var set = app.CreateSet(app.Interactive(PilotEnv.Pass, null), PilotEnv.Pass, new BackupSetInfo { Name = "Files", Sources = { src }, Vss = false });
                    PilotEnv.Change(env, "pcmd", set.Id, s => { s.PreCommands = new List<string> { chatty }; s.PostCommands = new List<string> { chatty }; s.StopOnPreCommandFailure = true; });
                    var want = AgentRig.Tree(src);
                    var r = app.Backup(set.Id);
                    Assert.True(r.Result == "BS_STOP_SUCCESS", r.Result + "\n" + string.Join("\n", r.LogLines));
                    Assert.Contains(r.LogLines, l => l.Contains("[pre-command output]"));
                    Assert.Contains(r.LogLines, l => l.Contains("[post-command output]"));
                    Assert.DoesNotContain(r.LogLines, l => l.Contains("timed out") || l.Contains("exit code"));
                    Assert.Equal(2, r.New);
                    Assert.Equal(want, PilotEnv.Restored(env, app, set.Id, src, null, "restore"));
                }
            }
            finally { Limits.Command = before; }
        }
    }

    /// <summary>
    /// BK-06 integration: Volume Shadow Copy in a real backup through the real server — real Windows as administrator only
    /// (the hosted-Windows QA shards); NOT TESTED elsewhere. Contract (specs.py BK-06): a file held open with an exclusive
    /// lock → in the backup; the shadow copy is removed afterwards, also on failure. Oracle: SHA-256 of the restore through
    /// the server, vssadmin's list of shadow copies and the snapshot links before and after.
    /// </summary>
    [Collection("Vss")]
    public class PilotVssIntegrationTests
    {
        [Fact]
        public void ALockedFile_IsBackedUpThroughTheShadowCopy_RestoresIdentical_NoShadowCopyLeft()
        {
            PilotVss.Need();
            using (var env = new Env())
            {
                env.CreateUser("pvss", PilotEnv.Pass);
                var app = env.Agent("pvss", PilotEnv.Pass);
                var src = env.Dir("src");
                File.WriteAllText(Path.Combine(src, "a.txt"), "alpha");
                var locked = Path.Combine(src, "ledger.mdb"); File.WriteAllBytes(locked, PilotRig.Rnd(300000, 81));
                var set = app.CreateSet(app.Interactive(PilotEnv.Pass, null), PilotEnv.Pass, new BackupSetInfo { Name = "Files", Sources = { src }, Vss = true });
                var want = AgentRig.Tree(src);
                var vol = PilotVss.Volume(src);
                var shadowsBefore = PilotVss.Shadows(vol); var linksBefore = PilotVss.Links();
                BackupRun r;
                using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) r = app.Backup(set.Id);
                Assert.True(r.Result == "BS_STOP_SUCCESS", r.Result + "\n" + string.Join("\n", r.LogLines));
                Assert.Contains(r.LogLines, l => l.Contains("Shadow Copy Set successfully created"));
                Assert.Equal(2, r.New);
                Assert.Equal(linksBefore, PilotVss.Links());
                Assert.Equal(shadowsBefore, PilotVss.Shadows(vol));
                Assert.Equal(want, PilotEnv.Restored(env, app, set.Id, src, null, "restore"));
            }
        }

        /// <summary>
        /// Integrity + recovery through the real server: a large file first (sent slowly by the set's upload limit), then a
        /// held file in a sub-folder. While the run sends the large file - after the snapshot, before the sub-folder is
        /// listed - the program holding the file writes new content into it. The point holds the content at snapshot time;
        /// the NEXT run must send the new content (else the change is lost until the file changes again).
        /// </summary>
        [Fact]
        public void AFileWrittenDuringTheRun_ThePointHoldsTheSnapshotContent_TheNextRunSendsTheNewContent()
        {
            PilotVss.Need();
            using (var env = new Env())
            {
                env.CreateUser("pvssw", PilotEnv.Pass);
                var app = env.Agent("pvssw", PilotEnv.Pass);
                var src = env.Dir("src");
                File.WriteAllBytes(Path.Combine(src, "big.bin"), PilotRig.Rnd(2 * 1024 * 1024, 83));
                Directory.CreateDirectory(Path.Combine(src, "zz"));
                var held = Path.Combine(src, "zz", "ledger.mdb"); var v2 = PilotRig.Rnd(310000, 85);
                File.WriteAllBytes(held, PilotRig.Rnd(300000, 84));
                var set = app.CreateSet(app.Interactive(PilotEnv.Pass, null), PilotEnv.Pass, new BackupSetInfo { Name = "Files", Sources = { src }, Vss = true, BandwidthKbps = 256, Compression = "NONE" });
                var atSnapshot = AgentRig.Tree(src);
                var vol = PilotVss.Volume(src);
                var shadowsBefore = PilotVss.Shadows(vol); var linksBefore = PilotVss.Links();
                BackupRun r = null; Exception failed = null; bool runningAtTheWrite;
                using (var hold = new FileStream(held, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    var t = new System.Threading.Thread(() => { try { r = app.Backup(set.Id); } catch (Exception e) { failed = e; } });
                    t.Start();
                    var until = DateTime.UtcNow.AddMinutes(3);
                    while (!PilotVss.Shadows(vol).Except(shadowsBefore).Any() && t.IsAlive && DateTime.UtcNow < until) System.Threading.Thread.Sleep(200);
                    hold.SetLength(0); hold.Write(v2, 0, v2.Length); hold.Flush(true);
                    runningAtTheWrite = t.IsAlive;
                    t.Join();
                }
                Assert.Null(failed);
                Assert.True(runningAtTheWrite, "the run ended before the file was written: the test did not write during the run");
                Assert.True(r.Result == "BS_STOP_SUCCESS", r.Result + "\n" + string.Join("\n", r.LogLines));
                Assert.Contains(r.LogLines, l => l.Contains("Shadow Copy Set successfully created"));
                Assert.Equal(atSnapshot, PilotEnv.Restored(env, app, set.Id, src, null, "restore1"));
                Assert.Equal(linksBefore, PilotVss.Links());
                Assert.Equal(shadowsBefore, PilotVss.Shadows(vol));

                PilotEnv.Change(env, "pvssw", set.Id, s => s.BandwidthKbps = 0);
                System.Threading.Thread.Sleep(1100);                                              // a run id of its own
                var r2 = app.Backup(set.Id);
                Assert.True(r2.Result == "BS_STOP_SUCCESS", r2.Result + "\n" + string.Join("\n", r2.LogLines));
                Assert.True(r2.Updated == 1, "the next run did not send the content written during the run (updated=" + r2.Updated + ", new=" + r2.New + ", perm=" + r2.PermOnly + ")");
                Assert.Equal(AgentRig.Tree(src), PilotEnv.Restored(env, app, set.Id, src, null, "restore2"));
                Assert.Equal(shadowsBefore, PilotVss.Shadows(vol));
            }
        }

        [Fact]
        public void ARunTheServerStopsForTheQuota_RemovesItsShadowCopy_AndOnceThereIsRoom_TheNextRunCompletes()
        {
            PilotVss.Need();
            using (var env = new Env())
            {
                env.CreateUser("pvssq", PilotEnv.Pass, quotaGB: 0.001);                          // ~1 MB
                var app = env.Agent("pvssq", PilotEnv.Pass);
                var src = env.Dir("src");
                var locked = Path.Combine(src, "big.mdb"); File.WriteAllBytes(locked, PilotRig.Rnd(3 * 1024 * 1024, 82));
                var set = app.CreateSet(app.Interactive(PilotEnv.Pass, null), PilotEnv.Pass, new BackupSetInfo { Name = "Files", Sources = { src }, Vss = true });
                var want = AgentRig.Tree(src);
                var vol = PilotVss.Volume(src);
                var shadowsBefore = PilotVss.Shadows(vol); var linksBefore = PilotVss.Links();
                using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    var failed = app.Backup(set.Id);
                    Assert.True(failed.Result == "BS_STOP_QUOTA_EXCEEDED", failed.Result + "\n" + string.Join("\n", failed.LogLines));
                    Assert.Contains(failed.LogLines, l => l.Contains("Shadow Copy Set successfully created"));
                    Assert.Contains(failed.LogLines, l => l.Contains("Deleting Shadow Copy snapshot"));
                }
                Assert.Equal(linksBefore, PilotVss.Links());
                Assert.Equal(shadowsBefore, PilotVss.Shadows(vol));

                env.Admin().Call("POST", "/api/admin/users/pvssq/quota", new Msg().Set("quotaGB", 1));
                System.Threading.Thread.Sleep(1100);                                              // a run id of its own
                var r = app.Backup(set.Id);
                Assert.True(r.Result == "BS_STOP_SUCCESS", r.Result + "\n" + string.Join("\n", r.LogLines));
                Assert.Equal(shadowsBefore, PilotVss.Shadows(vol));
                Assert.Equal(want, PilotEnv.Restored(env, app, set.Id, src, null, "restore"));
            }
        }
    }
}
