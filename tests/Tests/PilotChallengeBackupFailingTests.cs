using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    public partial class PilotChallengeBackupTests
    {
        static readonly DateTime T0 = new DateTime(2026, 10, 7, 22, 0, 0, DateTimeKind.Utc);

        /// <summary>
        /// BK-05 / AG-07 (challenge of the BK-05 locked-file test, which ran only over plain HTTP, and of the TLS tests, which
        /// backed up only readable files): a source file the listing shows but that cannot be opened, among readable files,
        /// over each of the agent's two ways to send (the system's HTTP stack, the built-in TLS 1.2). Contract (specs.py BK-05 component): BS_STOP_SUCCESS_WITH_ERROR, an err line
        /// naming that file, every other file sent and restoring identical (SHA-256). The TLS path must not change that: a
        /// file of the computer that cannot be read is not a broken line to the server.
        /// </summary>
        [Theory]
        [InlineData(false)]   // the system's HTTP stack (HttpWebRequest: the same code with or without the system's TLS) — the control
        [InlineData(true)]    // the agent's own TLS 1.2 (Windows 2003 / XP, or TLS=BUILTIN at registration), pinned
        public void BK05_AFileThatCannotBeOpened_IsAnErrorForThatFileOnly_OverEitherTls(bool builtin)
        {
            using (var rig = new AgentRig())
            using (var front = builtin ? TlsFront.For(rig) : null)
            {
                rig.File("a.txt", "alpha"); rig.File("c.bin", PilotRig.Rnd(70000, 8)); rig.File("d.txt", "delta");
                var want = AgentRig.Tree(rig.Src);
                var bad = Path.Combine(rig.Src, "b.lock");
                using (Unopenable(bad))
                {
                    var client = builtin ? new Client(front.Url, front.Fingerprint) { Builtin = true, Device = "device-of-the-test", Retries = 0, TimeoutMs = 30000 } : rig.NewClient();
                    var run = new BackupRun(client, rig.Home, rig.Set, rig.Key); run.Run();
                    var log = run.Result + "\n" + string.Join("\n", run.LogLines);
                    Assert.True(run.Result == "BS_STOP_SUCCESS_WITH_ERROR", (builtin ? "built-in TLS: " : "system HTTP stack: ") + log);
                    Assert.Contains(run.LogLines, l => l.Contains("b.lock") && l.Contains("err"));
                    Assert.Equal(want.Keys.OrderBy(x => x, StringComparer.Ordinal), Sent(rig, run));
                }
                Assert.Equal(want, rig.Restored());
            }
        }

        /// <summary>
        /// BK-07 / AG-02 (challenge of AtTheMaximumDuration_... and ExactlyAtTheLimit_..., whose clock jumped only while the
        /// server received an object, i.e. BETWEEN objects; and of the stop tests, whose stop was already pending when a file
        /// began): the limit passes, or Stop is pressed, while ONE large object is being sent — a set of one big file at a
        /// 64 KB/s upload limit (at least 16 s), its clock 15 minutes per second from the moment the file began (the 1-hour
        /// limit passes after 4 s of the 16). Contract (BK-07): "a slow backup past its limit → the run ends as stopped (not
        /// success)"; AG-02: "Stop ends it as stopped". The run must not go on to the end of the object and report a success.
        /// The clock and the stop move only after the run checked them for the file (progress seen) — deterministic; a slow
        /// machine only makes the upload longer.
        /// </summary>
        [Theory]
        [InlineData("duration")]
        [InlineData("stop")]
        public void BK07_TheLimitPassesOrStopIsPressedInsideOneBigObject_TheRunIsNotASuccess(string how)
        {
            using (var rig = new AgentRig())
            {
                rig.Set.Compression = "NONE"; rig.Set.BandwidthKbps = 64;
                rig.File("big.bin", PilotRig.Rnd(1024 * 1024, 76));
                var began = new Stopwatch(); bool seen = false;
                rig.Server.OnRequest = q => { if (q.Action == "progress" && !seen) { seen = true; began.Start(); } };
                Func<DateTime> clock = () => seen ? T0 + TimeSpan.FromSeconds(began.Elapsed.TotalSeconds * 900) : T0;
                if (how == "duration") rig.Set.DurationHours = 1;
                var r = new BackupRun(rig.NewClient(), rig.Home, rig.Set, rig.Key) { Clock = clock, StopRequested = how == "stop" ? () => seen : (Func<bool>)null };
                r.Run();
                var end = clock();
                Assert.True(seen, "the run never reported its progress: the test did not reach its moment");
                if (how == "duration") Assert.True(end > T0.AddHours(1), "the upload was faster than its 64 KB/s limit: " + (end - T0));
                else
                {
                    // the whole upload takes 16 s at 64 KB/s: a stop that works inside the file ends the run well before that
                    Assert.Equal("BS_STOP_BY_USER", r.Result);
                    Assert.True(began.Elapsed.TotalSeconds < 8, "Stop pressed when the file began took " + began.Elapsed.TotalSeconds.ToString("0.0") + " s of a 16 s upload");
                }
                Assert.False(r.Result.StartsWith("BS_STOP_SUCCESS", StringComparison.Ordinal),
                    (how == "duration" ? "the 1-hour limit passed during the upload, the run went on to " + (end - T0).TotalHours.ToString("0.0") + " h"
                                       : "Stop was pressed when the file began, the run went on for " + began.Elapsed.TotalSeconds.ToString("0") + " s")
                    + " and ended " + r.Result + " — the limit / stop is checked only between files\n" + string.Join("\n", r.LogLines));
            }
        }
        // ------------------------------------------------------------------ BK-04: a filter's top folder, a link to a file

        /// <summary>
        /// BK-04 (challenge of SkippedFolderAndExcludeFilter_..., which tried the same-prefix boundary only for a SKIPPED
        /// folder, never for a filter's top folder): an exclude filter "*.tmp" limited to the top folder Docs. Its files and
        /// those of its subfolders are left out; a sibling folder whose name starts with "Docs" (Docs2, DocsArchive) and a file
        /// outside are not under Docs — they must be backed up and restore identical.
        /// </summary>
        [Fact]
        public void BK04_AFilterWithATopFolder_LeavesASiblingFolderWithTheSamePrefixAlone()
        {
            using (var rig = new AgentRig())
            {
                rig.File(Path.Combine("Docs", "a.tmp"), "in the top folder: left out");
                rig.File(Path.Combine("Docs", "sub", "b.tmp"), "under the top folder: left out");
                rig.File(Path.Combine("Docs", "keep.txt"), "kept");
                rig.File(Path.Combine("Docs2", "c.tmp"), "a sibling folder, not under Docs: kept");
                rig.File(Path.Combine("DocsArchive", "d.tmp"), "another sibling with the same prefix: kept");
                rig.File("e.tmp", "outside the top folder: kept");
                rig.Set.Filters.Add(new FilterRule { Type = "WILDCARD", TopDir = Path.Combine(rig.Src, "Docs"), Patterns = { "*.tmp" }, ApplyFile = true });
                var want = AgentRig.Tree(rig.Src);
                Assert.True(want.Remove(Path.Combine("Docs", "a.tmp")) && want.Remove(Path.Combine("Docs", "sub", "b.tmp")));

                var r = rig.Backup();
                Assert.True(r.Result == "BS_STOP_SUCCESS", r.Result + "\n" + string.Join("\n", r.LogLines));
                Assert.Equal(want.Keys.OrderBy(x => x, StringComparer.Ordinal), Sent(rig, r));
                Assert.Equal(want, rig.Restored());
            }
        }

        /// <summary>
        /// BK-04 (challenge of ALinkedFolder_IsFollowedOnlyWithTheOption, which linked only a FOLDER): a symbolic link to a
        /// FILE outside the selection. Contract: "the link followed only with the option on" — with FollowLink off the
        /// file behind the link is not in the backup; with it on, it is, with the target's bytes.
        /// </summary>
        [Fact]
        public void BK04_ALinkToAFileOutside_IsFollowedOnlyWithTheOption()
        {
            foreach (var follow in new[] { false, true })
                using (var rig = new AgentRig())
                {
                    var outside = Path.Combine(rig.Root, "outside"); Directory.CreateDirectory(outside);
                    var target = Path.Combine(outside, "secret.bin"); File.WriteAllBytes(target, PilotRig.Rnd(3000, 5));
                    rig.File("own.txt", "own");
                    try { File.CreateSymbolicLink(Path.Combine(rig.Src, "link.bin"), target); }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                    {
                        throw NotTested.Because("this machine does not let the test create a file link: " + e.Message);
                    }
                    rig.Set.FollowLink = follow;
                    var r = rig.Backup();
                    Assert.True(r.Result == "BS_STOP_SUCCESS", "follow " + follow + ": " + r.Result + "\n" + string.Join("\n", r.LogLines));
                    var got = rig.Restored();
                    if (follow)
                    {
                        Assert.Equal(new[] { "link.bin", "own.txt" }, got.Keys.OrderBy(x => x, StringComparer.Ordinal));
                        Assert.Equal(AgentRig.Sha(target), got["link.bin"]);
                    }
                    else
                        Assert.True(got.Keys.SequenceEqual(new[] { "own.txt" }), "with FollowLink off the file behind a link was backed up: " + string.Join(", ", got.Keys));
                }
        }

        /// <summary>
        /// BK-04 (the same gap as BK04_ALinkToAFileOutside_...: only folder links were ever tried): a link to a file that no
        /// longer exists (a common leftover), with FollowLink OFF. A link that is not followed has nothing to read: the run is
        /// a plain success and the other files restore identical — not an error in every run of the set for ever.
        /// </summary>
        [Fact]
        public void BK04_ABrokenLinkToAFile_WithTheOptionOff_IsNotAnError()
        {
            using (var rig = new AgentRig())
            {
                rig.File("own.txt", "own");
                var want = AgentRig.Tree(rig.Src);
                try { File.CreateSymbolicLink(Path.Combine(rig.Src, "gone.lnk"), Path.Combine(rig.Root, "no-such-file.bin")); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    throw NotTested.Because("this machine does not let the test create a file link: " + e.Message);
                }
                rig.Set.FollowLink = false;
                for (int run = 1; run <= 2; run++)
                {
                    var r = rig.Backup();
                    Assert.True(r.Result == "BS_STOP_SUCCESS", "run " + run + ": " + r.Result + "\n" + string.Join("\n", r.LogLines));
                }
                Assert.Equal(want, rig.Restored());
            }
        }
    }
}
