using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>Shared helpers of the pilot component tests (the agent's BackupRun against the recording StubServer).</summary>
    static class PilotRig
    {
        public static byte[] Rnd(int n, int seed) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }
        public static string Sha(byte[] b) { return Bytes.Hex(Bytes.Sha256(b)); }

        /// <summary>The well-formed objects the stand-in server received in one run.</summary>
        public static List<StubServer.Req> Objects(AgentRig rig, string job)
        {
            return rig.Server.Of("object").Where(q => q.Job == job && q.Method == "PUT" && StubServer.WellFormed(q.Body, rig.Key)).ToList();
        }

        /// <summary>The decrypted header of an object (path, kind, seq, stored chunks), as a restore reads it.</summary>
        public static Msg Header(AgentRig rig, StubServer.Req q) { using (var s = new MemoryStream(q.Body)) return BackupObject.ReadHeader(s, rig.Key); }

        /// <summary>The source path an object belongs to, relative to the rig's source folder.</summary>
        public static string RelOf(AgentRig rig, StubServer.Req q) { return Path.GetRelativePath(rig.Src, Header(rig, q)["path"]); }

        public static long Stored(AgentRig rig, string job) { return Objects(rig, job).Sum(q => (long)q.Body.Length); }

        /// <summary>A run with its own clock (the product's BackupRun.Clock injection).</summary>
        public static BackupRun Backup(AgentRig rig, Func<DateTime> clock, Func<bool> stop = null)
        {
            var r = new BackupRun(rig.NewClient(), rig.Home, rig.Set, rig.Key) { Clock = clock, StopRequested = stop };
            r.Run(); return r;
        }

        public static void Touch(string path, int minutes) { File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(minutes)); }
    }

    /// <summary>
    /// BK-01 component: the agent's BackupRun alone against the recording stand-in server (no product server). Contract
    /// (specs.py BK-01): a tree of new, changed, deleted and permission-changed files, two runs → run 2 sends only what
    /// changed; every point restores byte-identical (SHA-256); deleted files gone from the newest point only.
    /// Oracle: the objects the stand-in received (decrypted header paths) and SHA-256 of the files restored from them.
    /// </summary>
    public class PilotFileBackupComponentTests
    {
        [Fact]
        public void KnownTree_Run2SendsOnlyTheNewChangedAndPermissionChangedFiles_DeletesOne_EveryPointRestoresIdentical()
        {
            using (var rig = new AgentRig())
            {
                rig.File("readme.txt", "hello");
                var ledger = rig.File(Path.Combine("Accounting", "2026", "ledger.csv"), string.Join("\n", Enumerable.Range(0, 5000).Select(i => "row," + i)));
                rig.File(Path.Combine("Accounting", "to-delete.txt"), "old");
                var perm = rig.File("perm.txt", "only its permission changes");
                rig.File("empty.txt", new byte[0]);                                                     // boundary: an empty file
                rig.File(Path.Combine("deep", "a", "b", "c", "d", "e", "f.bin"), PilotRig.Rnd(200000, 1));
                rig.File("שלום ü.txt", "a name outside ASCII");
                var point1 = AgentRig.Tree(rig.Src);
                Assert.Equal(7, point1.Count);

                var r1 = rig.Backup();
                Assert.True(r1.Result == "BS_STOP_SUCCESS", string.Join("\n", r1.LogLines));
                Assert.Equal(7, r1.New);
                Assert.Equal(point1.Keys.OrderBy(x => x), PilotRig.Objects(rig, r1.Job).Select(q => PilotRig.RelOf(rig, q)).OrderBy(x => x));
                Assert.Equal(point1, rig.Restored("p1"));

                File.AppendAllText(ledger, "\nrow,new"); PilotRig.Touch(ledger, 1);
                File.Delete(Path.Combine(rig.Src, "Accounting", "to-delete.txt"));
                rig.File("new.txt", "a new file");
                File.SetAttributes(perm, FileAttributes.ReadOnly);
                try
                {
                    var point2 = AgentRig.Tree(rig.Src);
                    var r2 = rig.Backup();
                    Assert.True(r2.Result == "BS_STOP_SUCCESS", string.Join("\n", r2.LogLines));
                    Assert.Equal(1, r2.New); Assert.Equal(1, r2.Updated); Assert.Equal(1, r2.Deleted); Assert.Equal(1, r2.PermOnly);
                    var sent = PilotRig.Objects(rig, r2.Job).Select(q => PilotRig.RelOf(rig, q)).OrderBy(x => x, StringComparer.Ordinal).ToList();
                    Assert.Equal(new[] { Path.Combine("Accounting", "2026", "ledger.csv"), "new.txt", "perm.txt" }.OrderBy(x => x, StringComparer.Ordinal), sent);   // only what changed
                    var del = rig.Server.Of("delete").Where(q => q.Job == r2.Job).ToList();
                    Assert.Single(del);
                    Assert.Single(del[0].Msg.List("rels"));
                    var got2 = rig.Restored("p2");
                    Assert.Equal(point2, got2);
                    Assert.False(got2.ContainsKey(Path.Combine("Accounting", "to-delete.txt")));     // gone from the newest point
                    Assert.True(point1.ContainsKey(Path.Combine("Accounting", "to-delete.txt")));    // and it was in the first

                    var r3 = rig.Backup();                                                            // nothing changed: nothing sent
                    Assert.Equal("BS_STOP_SUCCESS", r3.Result);
                    Assert.Empty(rig.Server.Of("object").Where(q => q.Job == r3.Job));
                    Assert.Empty(rig.Server.Of("delete").Where(q => q.Job == r3.Job));
                    Assert.Equal(point2, rig.Restored("p3"));
                }
                finally { File.SetAttributes(perm, FileAttributes.Normal); }
            }
        }

        [Fact]
        public void TheServerFailsMidRun_TheRunFails_NothingDeleted_TheOldPointStillRestores_TheNextRunSendsTheChangesAgain()
        {
            using (var rig = new AgentRig())
            {
                rig.File("a.txt", "alpha v1"); rig.File("b.txt", "beta"); rig.File("c.txt", "gamma");
                var point1 = AgentRig.Tree(rig.Src);
                Assert.Equal("BS_STOP_SUCCESS", rig.Backup().Result);
                var indexBefore = File.ReadAllText(rig.StateFile);

                var a = rig.File("a.txt", "alpha v2 — changed"); PilotRig.Touch(a, 1);
                rig.File("d.txt", "delta, new");
                File.Delete(Path.Combine(rig.Src, "c.txt"));
                int objects = 0;
                rig.Server.Handler = q => q.Action == "object" && ++objects == 2 ? StubServer.Answer.Error(500, "INTERNAL", "The server failed") : null;
                var r2 = rig.Backup();
                Assert.Equal("BS_STOP_BY_SYSTEM_ERROR", r2.Result);
                Assert.DoesNotContain(rig.Server.Requests, q => q.Job == r2.Job && (q.Action == "delete" || q.Action == "commit"));
                Assert.Single(rig.Server.Requests, q => q.Job == r2.Job && q.Action == "abort");     // the server is told the run ended
                Assert.Equal(indexBefore, File.ReadAllText(rig.StateFile));                            // the index did not move
                Assert.Equal(point1, rig.Restored("p2"));                                              // the last point is intact

                rig.Server.Handler = q => null;
                var point3 = AgentRig.Tree(rig.Src);
                var r3 = rig.Backup();
                Assert.True(r3.Result == "BS_STOP_SUCCESS", string.Join("\n", r3.LogLines));
                Assert.Equal(new[] { "a.txt", "d.txt" }, PilotRig.Objects(rig, r3.Job).Select(q => PilotRig.RelOf(rig, q)).OrderBy(x => x, StringComparer.Ordinal));
                Assert.Single(rig.Server.Of("delete"), q => q.Job == r3.Job);
                Assert.Equal(point3, rig.Restored("p3"));
            }
        }
    }

    /// <summary>
    /// BK-02 component: delta chains in the agent alone (stand-in server). Contract (specs.py BK-02): a large file changed
    /// in the middle N times, incremental and differential → each delta is small; every point of the chain restores
    /// identical; past the chain limit a new full copy is sent. Oracle: the objects received (kind, seq, size, the chunks
    /// each holds per its decrypted header) and SHA-256 of the file restored from the point after every run.
    /// </summary>
    public class PilotDeltaChainComponentTests
    {
        const int Size = 16 * 1024 * 1024;   // ~16 chunks of 1 MB on average

        static byte[] Insert(byte[] cur, int at, int seed) { return cur.Take(at).Concat(PilotRig.Rnd(2048, seed)).Concat(cur.Skip(at + 512)).ToArray(); }
        static HashSet<string> StoredChunks(AgentRig rig, StubServer.Req q) { return new HashSet<string>(PilotRig.Header(rig, q).List("stored").Select(m => m["h"])); }

        [Fact]
        public void Incremental_EachDeltaIsSmallAndHoldsOnlyNewChunks_EveryPointRestoresIdentical_PastTheLimitANewFullCopy()
        {
            using (var rig = new AgentRig())
            {
                rig.Set.MinDeltaFileSize = 1024 * 1024; rig.Set.MaxDeltaNo = 3; rig.Set.Compression = "NONE"; rig.Set.DeltaType = "I";
                var path = Path.Combine(rig.Src, "db.mdf");
                var v = PilotRig.Rnd(Size, 11); File.WriteAllBytes(path, v);
                var r = rig.Backup();
                Assert.Equal("BS_STOP_SUCCESS", r.Result);
                var full = PilotRig.Objects(rig, r.Job).Single();
                Assert.Equal("F", full.Query["kind"]); Assert.Equal("0", full.Query["seq"]);
                Assert.Equal(PilotRig.Sha(v), rig.Restored("p0")["db.mdf"]);

                var seen = new HashSet<string>(StoredChunks(rig, full));
                for (int n = 1; n <= 3; n++)
                {
                    v = Insert(v, 4 * 1024 * 1024 * n, 100 + n); File.WriteAllBytes(path, v); PilotRig.Touch(path, n);
                    r = rig.Backup();
                    Assert.True(r.Result == "BS_STOP_SUCCESS", string.Join("\n", r.LogLines));
                    var o = PilotRig.Objects(rig, r.Job).Single();
                    if (n < 3)
                    {
                        Assert.Equal("D", o.Query["kind"]); Assert.Equal(n.ToString(), o.Query["seq"]);
                        Assert.True(o.Body.Length < Size / 4, "delta " + n + " is " + o.Body.Length + " bytes for a 2 KB change in a 16 MB file");
                        var mine = StoredChunks(rig, o);
                        Assert.NotEmpty(mine);
                        Assert.Empty(mine.Intersect(seen));                                    // incremental: only chunks not sent before
                        seen.UnionWith(mine);
                    }
                    else
                    {
                        // chain limit MAX_DELTA_NO = 3: full copy + 2 deltas, the 3rd change starts a new full copy
                        Assert.Equal("F", o.Query["kind"]); Assert.Equal("0", o.Query["seq"]);
                        Assert.True(o.Body.Length >= Size, "the new full copy is " + o.Body.Length + " bytes");
                    }
                    Assert.Equal(PilotRig.Sha(v), rig.Restored("p" + n)["db.mdf"]);
                }
            }
        }

        [Fact]
        public void Differential_EachDeltaCarriesEveryChangeSinceTheFull_EveryPointRestoresIdentical()
        {
            using (var rig = new AgentRig())
            {
                rig.Set.MinDeltaFileSize = 1024 * 1024; rig.Set.MaxDeltaNo = 10; rig.Set.Compression = "NONE"; rig.Set.DeltaType = "D";
                var path = Path.Combine(rig.Src, "db.mdf");
                var v = PilotRig.Rnd(Size, 21); File.WriteAllBytes(path, v);
                Assert.Equal("BS_STOP_SUCCESS", rig.Backup().Result);
                HashSet<string> before = null;
                for (int n = 1; n <= 3; n++)
                {
                    v = Insert(v, 4 * 1024 * 1024 * n, 200 + n); File.WriteAllBytes(path, v); PilotRig.Touch(path, n);
                    var r = rig.Backup();
                    Assert.True(r.Result == "BS_STOP_SUCCESS", string.Join("\n", r.LogLines));
                    var o = PilotRig.Objects(rig, r.Job).Single();
                    Assert.Equal("D", o.Query["kind"]);
                    Assert.True(o.Body.Length < Size / 2, "differential " + n + " is " + o.Body.Length + " bytes");
                    var mine = StoredChunks(rig, o);
                    if (before != null) Assert.True(before.IsSubsetOf(mine), "delta " + n + " does not carry the changes of the delta before it");
                    before = mine;
                    Assert.Equal(PilotRig.Sha(v), rig.Restored("p" + n)["db.mdf"]);
                }
            }
        }

        [Fact]
        public void ADeltaTheServerFailed_LeavesTheChainAsItWas_ThePointRestoresTheOldVersion_TheNextRunSendsTheDelta()
        {
            using (var rig = new AgentRig())
            {
                rig.Set.MinDeltaFileSize = 1024 * 1024; rig.Set.Compression = "NONE";
                var path = Path.Combine(rig.Src, "db.mdf");
                var v1 = PilotRig.Rnd(Size, 31); File.WriteAllBytes(path, v1);
                Assert.Equal("BS_STOP_SUCCESS", rig.Backup().Result);

                var v2 = Insert(v1, 8 * 1024 * 1024, 301); File.WriteAllBytes(path, v2); PilotRig.Touch(path, 1);
                rig.Server.Handler = q => q.Action == "object" ? StubServer.Answer.Error(500, "INTERNAL", "The server failed") : null;
                var failed = rig.Backup();
                Assert.Equal("BS_STOP_BY_SYSTEM_ERROR", failed.Result);
                Assert.Equal(PilotRig.Sha(v1), rig.Restored("p1")["db.mdf"]);              // the chain is as it was

                rig.Server.Handler = q => null;
                var r = rig.Backup();
                Assert.True(r.Result == "BS_STOP_SUCCESS", string.Join("\n", r.LogLines));
                var o = PilotRig.Objects(rig, r.Job).Single();
                Assert.Equal("D", o.Query["kind"]); Assert.Equal("1", o.Query["seq"]);      // a delta on the same full copy, not a gap
                Assert.True(o.Body.Length < Size / 4, "delta is " + o.Body.Length + " bytes");
                Assert.Equal(PilotRig.Sha(v2), rig.Restored("p2")["db.mdf"]);
            }
        }
    }

    /// <summary>
    /// BK-04 component: filters, skipped folders and links in the agent alone (stand-in server). Contract (specs.py BK-04):
    /// a tree with excluded folders, filter patterns and a symbolic link → excluded items absent from the restore, included
    /// ones identical; the link followed only with the option on. Oracle: the objects received and SHA-256 of the restore.
    /// </summary>
    public class PilotFilterComponentTests
    {
        [Fact]
        public void SkippedFolderAndExcludeFilter_AreAbsentFromTheRestore_EverythingElseIdentical_ASiblingWithTheSamePrefixIsKept()
        {
            using (var rig = new AgentRig())
            {
                rig.File(Path.Combine("Docs", "contract.docx"), "contract");
                rig.File(Path.Combine("Docs", "draft.tmp"), "temporary");
                rig.File(Path.Combine("Docs", "draft.tmp.txt"), "not a .tmp file");                 // boundary: the pattern is the end of the name
                rig.File(Path.Combine("Cache", "x.bin"), PilotRig.Rnd(5000, 2));
                rig.File(Path.Combine("Cache2", "keep.bin"), PilotRig.Rnd(5000, 3));                 // boundary: same prefix as the skipped folder
                rig.File(Path.Combine("Other", "notes.TMP"), "upper case extension");                // boundary: patterns ignore case
                rig.File(Path.Combine("Other", "keep.txt"), "keep");
                rig.Set.Deselected.Add(Path.Combine(rig.Src, "Cache"));
                rig.Set.Filters.Add(new FilterRule { Type = "WILDCARD", Patterns = { "*.tmp" }, ApplyFile = true });
                var want = AgentRig.Tree(rig.Src);
                foreach (var x in new[] { Path.Combine("Docs", "draft.tmp"), Path.Combine("Cache", "x.bin"), Path.Combine("Other", "notes.TMP") }) Assert.True(want.Remove(x), x);

                var r = rig.Backup();
                Assert.True(r.Result == "BS_STOP_SUCCESS", string.Join("\n", r.LogLines));
                Assert.Equal(want.Keys.OrderBy(x => x, StringComparer.Ordinal), PilotRig.Objects(rig, r.Job).Select(q => PilotRig.RelOf(rig, q)).OrderBy(x => x, StringComparer.Ordinal));
                Assert.Equal(want, rig.Restored());
            }
        }

        [Fact]
        public void AnIncludeOnlyFilter_KeepsOnlyItsFiles_InEveryFolder_AndTheyRestoreIdentical()
        {
            using (var rig = new AgentRig())
            {
                rig.File(Path.Combine("A", "one.docx"), "one"); rig.File(Path.Combine("A", "one.pdf"), "pdf");
                rig.File(Path.Combine("A", "B", "two.DOCX"), "two"); rig.File("three.docx.bak", "a backup copy");
                rig.Set.Filters.Add(new FilterRule { Type = "WILDCARD", Include = true, Only = true, Patterns = { "*.docx" }, ApplyFile = true });
                var want = AgentRig.Tree(rig.Src).Where(kv => kv.Key.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)).ToDictionary(kv => kv.Key, kv => kv.Value);
                Assert.Equal(2, want.Count);
                var r = rig.Backup();
                Assert.True(r.Result == "BS_STOP_SUCCESS", string.Join("\n", r.LogLines));
                Assert.Equal(want, rig.Restored());
            }
        }

        [Fact]
        public void ALinkedFolder_IsFollowedOnlyWithTheOption_AndALinkBackToItsParentIsNotWalkedAgain()
        {
            using (var rig = new AgentRig())
            {
                var outside = Path.Combine(rig.Root, "outside"); Directory.CreateDirectory(outside);
                File.WriteAllBytes(Path.Combine(outside, "linked.bin"), PilotRig.Rnd(3000, 4));
                rig.File("own.txt", "own");
                try
                {
                    Directory.CreateSymbolicLink(Path.Combine(rig.Src, "link"), outside);
                    Directory.CreateSymbolicLink(Path.Combine(rig.Src, "loop"), rig.Src);           // a link back to its own parent
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    throw NotTested.Because("this machine does not let the test create a folder link: " + e.Message);
                }

                rig.Set.FollowLink = false;
                var r1 = rig.Backup();
                Assert.True(r1.Result == "BS_STOP_SUCCESS", string.Join("\n", r1.LogLines));
                Assert.Equal(new[] { "own.txt" }, PilotRig.Objects(rig, r1.Job).Select(q => PilotRig.RelOf(rig, q)));
                Assert.Equal(new[] { "own.txt" }, rig.Restored("off").Keys);

                rig.Set.FollowLink = true;
                var r2 = rig.Backup();
                Assert.True(r2.Result == "BS_STOP_SUCCESS", string.Join("\n", r2.LogLines));
                Assert.Equal(new[] { Path.Combine("link", "linked.bin") }, PilotRig.Objects(rig, r2.Job).Select(q => PilotRig.RelOf(rig, q)));   // the loop is not walked
                var got = rig.Restored("on");
                Assert.Equal(new[] { Path.Combine("link", "linked.bin"), "own.txt" }, got.Keys.OrderBy(x => x, StringComparer.Ordinal));
                Assert.Equal(AgentRig.Sha(Path.Combine(outside, "linked.bin")), got[Path.Combine("link", "linked.bin")]);
                Assert.Contains(r2.LogLines, l => l.Contains("Link not followed"));
            }
        }
    }

    /// <summary>
    /// BK-07 component: the maximum duration in the agent alone (stand-in server), with the run's own clock (BackupRun.Clock)
    /// moved by the test when the server receives a given object — no waiting, no wall clock. Contract (specs.py BK-07): a
    /// slow backup past its limit → the run ends as stopped (not success), nothing half-stored is visible, the next run
    /// completes. Oracle: the result and commit sent, the objects received, SHA-256 of the restore after each run.
    /// </summary>
    public class PilotStopComponentTests
    {
        static readonly DateTime T0 = new DateTime(2026, 10, 7, 22, 0, 0, DateTimeKind.Utc);

        internal static void Files(AgentRig rig, int n) { for (int i = 0; i < n; i++) rig.File("f" + i.ToString("00") + ".txt", "data " + i + new string('x', 1000 + i)); }

        /// <summary>The run's clock jumps to <paramref name="jump"/> while the server receives the n-th object of the run.</summary>
        internal static BackupRun RunWithJump(AgentRig rig, int n, TimeSpan jump)
        {
            var now = T0; int objects = 0;
            rig.Server.OnRequest = q => { if (q.Action == "object" && ++objects == n) now = T0 + jump; };
            try { return PilotRig.Backup(rig, () => now); }
            finally { rig.Server.OnRequest = q => { }; }
        }

        [Fact]
        public void AtTheMaximumDuration_WhatWasSentRestoresIdentical_NothingIsDeleted_TheNextRunSendsOnlyTheRest()
        {
            using (var rig = new AgentRig())
            {
                Files(rig, 10);
                rig.File("old.txt", "backed up before");
                Assert.Equal("BS_STOP_SUCCESS", PilotRig.Backup(rig, () => T0.AddDays(-1)).Result);
                File.Delete(Path.Combine(rig.Src, "old.txt"));
                foreach (var f in Directory.GetFiles(rig.Src)) { File.AppendAllText(f, " v2"); PilotRig.Touch(f, 1); }
                for (int i = 10; i < 14; i++) rig.File("n" + i + ".txt", "new " + i);
                var now = AgentRig.Tree(rig.Src);

                rig.Set.DurationHours = 1;
                var r = RunWithJump(rig, 3, TimeSpan.FromHours(2));
                var sent = PilotRig.Objects(rig, r.Job).Select(q => PilotRig.RelOf(rig, q)).ToList();
                Assert.Equal(3, sent.Count);
                Assert.Contains(r.LogLines, l => l.Contains("maximum duration"));
                Assert.DoesNotContain(rig.Server.Requests, q => q.Job == r.Job && q.Action == "delete");   // not reached is not deleted
                var got = rig.Restored("stopped");
                foreach (var f in sent) Assert.Equal(now[f], got[f]);                                       // what was sent is whole
                Assert.True(got.ContainsKey("old.txt"), "a stopped run deleted a file it never reached");
                foreach (var f in now.Keys.Except(sent).Where(k => k.StartsWith("n"))) Assert.False(got.ContainsKey(f), f);   // never half there

                var next = RunWithJump(rig, 0, TimeSpan.Zero);
                Assert.True(next.Result == "BS_STOP_SUCCESS", string.Join("\n", next.LogLines));
                var rest = PilotRig.Objects(rig, next.Job).Select(q => PilotRig.RelOf(rig, q)).ToList();
                Assert.Equal(now.Count - 3, rest.Count);
                Assert.Empty(rest.Intersect(sent));                                                         // the 3 are not sent again
                Assert.Equal(now, rig.Restored("next"));
            }
        }

        [Fact]
        public void ExactlyAtTheLimit_TheRunGoesOn_OneTickPast_ItStops_WithoutALimit_ItNeverStops()
        {
            using (var rig = new AgentRig())
            {
                Files(rig, 6);
                rig.Set.DurationHours = 1;
                var at = RunWithJump(rig, 1, TimeSpan.FromHours(1));
                Assert.Equal("BS_STOP_SUCCESS", at.Result);
                Assert.Equal(6, at.New);

                foreach (var f in Directory.GetFiles(rig.Src)) { File.AppendAllText(f, " v2"); PilotRig.Touch(f, 1); }
                var past = RunWithJump(rig, 1, TimeSpan.FromHours(1) + TimeSpan.FromTicks(1));
                Assert.Equal(1, past.Updated);
                Assert.Contains(past.LogLines, l => l.Contains("maximum duration"));

                rig.Set.DurationHours = -1;                                                                  // no limit
                foreach (var f in Directory.GetFiles(rig.Src)) { File.AppendAllText(f, " v3"); PilotRig.Touch(f, 2); }
                var want = AgentRig.Tree(rig.Src);
                var free = RunWithJump(rig, 1, TimeSpan.FromHours(1000));
                Assert.Equal("BS_STOP_SUCCESS", free.Result);
                Assert.Equal(6, free.Updated);
                Assert.Equal(want, rig.Restored());
            }
        }
    }

    /// <summary>
    /// BK-08 component: compression and the upload limit in the agent alone (stand-in server). Contract (specs.py BK-08):
    /// with compression the stored size is smaller; restore identical either way; the transfer keeps to the limit.
    /// Oracle: the bytes the stand-in received, SHA-256 of the restore, and a LOWER bound on the time (sleeping makes a run
    /// slower, never faster, so a slow machine cannot break it).
    /// </summary>
    public class PilotResourcesComponentTests
    {
        static (long stored, Dictionary<string, string> want, Dictionary<string, string> got) Run(string level, Action<AgentRig> files)
        {
            using (var rig = new AgentRig())
            {
                rig.Set.Compression = level;
                files(rig);
                var want = AgentRig.Tree(rig.Src);
                var r = rig.Backup();
                Assert.True(r.Result == "BS_STOP_SUCCESS", string.Join("\n", r.LogLines));
                return (PilotRig.Stored(rig, r.Job), want, rig.Restored());
            }
        }

        [Fact]
        public void Compression_MaxStoresFarLessThanNone_BothRestoreIdentical()
        {
            Action<AgentRig> text = rig => rig.File("table.csv", string.Concat(Enumerable.Range(0, 40000).Select(i => "row," + i + ",the same text again\n")));
            var none = Run("NONE", text); var fast = Run("FAST", text); var max = Run("MAX", text);
            Assert.Equal(none.want, none.got); Assert.Equal(fast.want, fast.got); Assert.Equal(max.want, max.got);
            Assert.Equal(none.want, max.want);
            Assert.True(none.stored > 1000000, "NONE stored only " + none.stored + " bytes of a 1 MB table");
            Assert.True(max.stored * 4 < none.stored, "MAX " + max.stored + " vs NONE " + none.stored);
            Assert.True(fast.stored < none.stored && max.stored <= fast.stored, "FAST " + fast.stored + " MAX " + max.stored + " NONE " + none.stored);
        }

        [Fact]
        public void Compression_OfDataThatDoesNotCompress_StoresNoMoreThanWithout_AndRestoresIdentical()
        {
            Action<AgentRig> random = rig => { rig.File("photo.jpg", PilotRig.Rnd(3 * 1024 * 1024, 5)); rig.File("tiny.txt", "a"); rig.File("empty.dat", new byte[0]); };
            var none = Run("NONE", random); var max = Run("MAX", random);
            Assert.Equal(none.want, none.got); Assert.Equal(max.want, max.got);
            Assert.True(max.stored <= none.stored + 64, "MAX stored " + max.stored + " bytes, NONE " + none.stored + " — compression made incompressible data bigger");
        }

        [Fact]
        public void UploadLimit_ARunNeverSendsFasterThanTheLimit_AndRestoresIdentical()
        {
            using (var rig = new AgentRig())
            {
                rig.Set.Compression = "NONE"; rig.Set.BandwidthKbps = 128;
                rig.File("a.bin", PilotRig.Rnd(512 * 1024, 6));
                var want = AgentRig.Tree(rig.Src);
                var sw = Stopwatch.StartNew();
                var r = rig.Backup();
                var took = sw.Elapsed.TotalSeconds;
                Assert.True(r.Result == "BS_STOP_SUCCESS", string.Join("\n", r.LogLines));
                Assert.Contains(r.LogLines, l => l.Contains("Upload limit 128 KB/s"));
                var floor = (double)PilotRig.Stored(rig, r.Job) / (128 * 1024);
                Assert.True(floor >= 4.0, "sent " + PilotRig.Stored(rig, r.Job) + " bytes");
                Assert.True(took >= floor * 0.95, PilotRig.Stored(rig, r.Job) + " bytes at 128 KB/s took only " + took.ToString("0.00") + " s (at least " + floor.ToString("0.00") + " s)");
                Assert.Equal(want, rig.Restored());

                rig.Set.BandwidthKbps = 0;                                                     // no limit: not announced
                rig.File("b.bin", PilotRig.Rnd(1000, 7));
                var r2 = rig.Backup();
                Assert.Equal("BS_STOP_SUCCESS", r2.Result);
                Assert.DoesNotContain(r2.LogLines, l => l.Contains("Upload limit"));
            }
        }
    }

    /// <summary>
    /// AP-07 component: an external program the backup runs (a pre-command) that hangs and starts a child. Contract
    /// (specs.py AP-07): stopped at its limit with its children; both streams read in full; never a hung backup. Here the
    /// agent's BackupRun with the stand-in server: the stuck pre-command ends at the limit, its child is gone, the backup
    /// goes on and completes, a post-command that writes much to both streams finishes, the files restore identical.
    /// </summary>
    [Collection("Limits")]
    public class PilotProcessComponentTests
    {
        static bool Win { get { return Environment.OSVersion.Platform == PlatformID.Win32NT; } }

        static bool Alive(int pid)
        {
            if (!Win)
            {
                var stat = "/proc/" + pid + "/stat";
                try { return File.Exists(stat) && !File.ReadAllText(stat).Contains(") Z "); } catch (IOException) { return false; }
            }
            try { using (var p = Process.GetProcessById(pid)) return !p.HasExited; } catch (ArgumentException) { return false; } catch (InvalidOperationException) { return false; }
        }

        [Fact]
        public void AStuckPreCommandWithAChild_IsStoppedAtTheLimitWithItsChild_TheBackupGoesOnAndRestoresIdentical()
        {
            using (var rig = new AgentRig())
            {
                var pidFile = Path.Combine(rig.Root, "child.pid");
                rig.Set.PreCommands.Add(Win
                    ? "powershell -NoProfile -NonInteractive -Command \"$p = Start-Process -FilePath ping.exe -ArgumentList '-n','600','127.0.0.1' -PassThru -WindowStyle Hidden; Set-Content -LiteralPath '" + pidFile + "' -Value $p.Id; Wait-Process -Id $p.Id\""
                    : "sleep 600 & echo $! > '" + pidFile + "'; wait");
                rig.Set.PostCommands.Add(Win
                    ? "powershell -NoProfile -NonInteractive -Command \"[Console]::Error.Write('e' * 300000); [Console]::Out.Write('o' * 300000); exit 0\""
                    : "yes e | head -c 300000 >&2; yes o | head -c 300000; exit 0");
                rig.Set.StopOnPreCommandFailure = false;
                rig.File("a.txt", "alpha"); rig.File(Path.Combine("sub", "b.bin"), PilotRig.Rnd(70000, 8));
                var want = AgentRig.Tree(rig.Src);
                var before = Limits.Command;
                try
                {
                    Limits.Command = TimeSpan.FromSeconds(3);
                    var sw = Stopwatch.StartNew();
                    var r = rig.Backup();
                    Assert.True(sw.Elapsed < TimeSpan.FromSeconds(100), "the backup took " + sw.Elapsed + " with a 3 s limit on its command");
                    Assert.True(r.Result == "BS_STOP_SUCCESS_WITH_WARNING", r.Result + "\n" + string.Join("\n", r.LogLines));
                    Assert.Contains(r.LogLines, l => l.Contains("pre-command timed out"));
                    Assert.DoesNotContain(r.LogLines, l => l.Contains("post-command timed out") || l.Contains("post-command exit code"));   // both streams read to the end
                    Assert.Contains(r.LogLines, l => l.Contains("[post-command output]"));
                    Assert.True(File.Exists(pidFile), "the stuck command did not start its child");
                    var child = int.Parse(File.ReadAllText(pidFile).Trim());
                    Assert.False(Alive(child), "the child process " + child + " of the stopped command is still running");
                    Assert.Equal(2, r.New);
                    Assert.Equal(want, rig.Restored());

                    rig.Set.PreCommands.Clear();                                                 // the next run is unaffected
                    rig.File("c.txt", "gamma");
                    var r2 = rig.Backup();
                    Assert.Equal("BS_STOP_SUCCESS", r2.Result);
                    Assert.Equal(AgentRig.Tree(rig.Src), rig.Restored("r2"));
                }
                finally { Limits.Command = before; }
            }
        }
    }
    /// <summary>Shadow-copy helpers shared by the BK-06 tests (real Windows only).</summary>
    static class PilotVss
    {
        /// <summary>BK-06 needs a real Volume Shadow Copy: Windows, as administrator (the hosted-Windows QA shards).</summary>
        public static void Need()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) throw NotTested.Because("Volume Shadow Copy exists only on Windows (this test runs on the hosted-Windows QA shards)");
            if (!Environment.IsPrivilegedProcess) throw NotTested.Because("creating a Volume Shadow Copy needs administrator rights");
        }

        /// <summary>The ids of the shadow copies of a volume now (vssadmin; the GUIDs, so the language of Windows does not matter).</summary>
        public static HashSet<string> Shadows(string vol)
        {
            var r = ProcessRunner.Run(new ProcessStartInfo("vssadmin", "list shadows /for=" + vol + "\\"), TimeSpan.FromMinutes(2));
            Assert.False(r.TimedOut, "vssadmin did not answer");
            return new HashSet<string>(System.Text.RegularExpressions.Regex.Matches(r.Out, @"\{[0-9A-Fa-f-]{36}\}").Cast<System.Text.RegularExpressions.Match>().Select(m => m.Value.ToUpperInvariant()));
        }

        /// <summary>The snapshot links the product made in the temporary folder (obvss_*).</summary>
        public static HashSet<string> Links() { return new HashSet<string>(Directory.GetDirectories(Path.GetTempPath(), "obvss_*")); }

        public static string Volume(string path) { return Path.GetPathRoot(Path.GetFullPath(path)).Substring(0, 2).ToUpperInvariant(); }

        /// <summary>A drive letter nothing uses now ("Q:"), from the end of the alphabet.</summary>
        public static string FreeLetter()
        {
            var used = new HashSet<char>(DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])));
            for (var c = 'Z'; c >= 'G'; c--) if (!used.Contains(c)) return c + ":";
            throw NotTested.Because("no free drive letter on this computer");
        }

        static string Tool(string exe, string args)
        {
            var r = ProcessRunner.Run(new ProcessStartInfo(exe, args), TimeSpan.FromMinutes(2));
            Assert.False(r.TimedOut, exe + " " + args + " did not answer");
            return r.Out + r.Err;
        }

        sealed class Undo : IDisposable { readonly Action a; public Undo(Action a) { this.a = a; } public void Dispose() { a(); } }

        /// <summary>The Volume Shadow Copy service stopped and disabled until Dispose (then back to "manual", as Windows ships it).</summary>
        public static IDisposable VssServiceDisabled()
        {
            Tool("sc.exe", "config VSS start= disabled");
            Tool("sc.exe", "stop VSS");
            var until = DateTime.UtcNow.AddSeconds(90);
            while (!Tool("sc.exe", "query VSS").Contains("STOPPED"))
            {
                if (DateTime.UtcNow > until) { Tool("sc.exe", "config VSS start= demand"); throw NotTested.Because("the Volume Shadow Copy service did not stop on this machine"); }
                System.Threading.Thread.Sleep(500);
            }
            Assert.Contains("DISABLED", Tool("sc.exe", "qc VSS"));
            return new Undo(() => Tool("sc.exe", "config VSS start= demand"));
        }

        /// <summary>A drive letter that is a folder (subst): no volume of its own, so no Volume Shadow Copy for it.</summary>
        public static IDisposable Subst(string letter, string folder)
        {
            Tool("subst.exe", letter + " \"" + folder + "\"");
            if (!Directory.Exists(letter + "\\")) throw NotTested.Because("subst " + letter + " did not work on this machine");
            return new Undo(() => Tool("subst.exe", letter + " /D"));
        }

        /// <summary>Removes the shadow copies a test left (a run it killed): the machine stays clean for the next test.</summary>
        public static void DeleteShadows(IEnumerable<string> ids)
        {
            foreach (var id in ids) Tool("vssadmin", "delete shadows /shadow=" + id + " /quiet");
        }
    }

    /// <summary>
    /// BK-06 component: the product's Volume Shadow Copy wrapper (Agent/Vss.cs) and the agent's BackupRun with it, against
    /// the stand-in server. Vss.cs has no stand-in of its own (it calls the Windows tools), so these run on real Windows as
    /// administrator and are NOT TESTED elsewhere. Contract (specs.py BK-06): a file held open with an exclusive lock and
    /// written during the backup → the file is in the backup with its content at snapshot time; the shadow copy is removed
    /// afterwards, also on failure. Oracle: SHA-256 of the content at snapshot time, of the restore; vssadmin's list of
    /// shadow copies and the snapshot links before and after.
    /// </summary>
    [Collection("Vss")]
    public class PilotVssComponentTests
    {
        [Fact]
        public void ALockedFile_IsReadFromTheSnapshot_WithItsContentAtSnapshotTime_AndTheSnapshotIsRemovedAfterwards()
        {
            PilotVss.Need();
            var dir = Path.Combine(Path.GetTempPath(), "obvsst-" + Guid.NewGuid().ToString("N").Substring(0, 8)); Directory.CreateDirectory(dir);
            try
            {
                var file = Path.Combine(dir, "mail.pst");
                var v1 = PilotRig.Rnd(300000, 41); File.WriteAllBytes(file, v1);
                var vol = PilotVss.Volume(dir);
                var shadowsBefore = PilotVss.Shadows(vol); var linksBefore = PilotVss.Links();
                using (var held = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))   // another program's exclusive lock
                {
                    var warnings = new List<string>();
                    var vss = Vss.Create(new[] { dir }, warnings.Add);
                    Assert.True(vss != null, "no shadow copy: " + string.Join(" | ", warnings));
                    string mapped;
                    try
                    {
                        mapped = vss.Map(file);
                        Assert.NotEqual(file, mapped);
                        Assert.NotEqual(shadowsBefore.Count, PilotVss.Shadows(vol).Count);
                        var v2 = PilotRig.Rnd(310000, 42);                                     // written after the snapshot, through the lock
                        held.SetLength(0); held.Write(v2, 0, v2.Length); held.Flush(true);
                        Assert.Throws<IOException>(() => File.ReadAllBytes(file));             // the live file cannot be read
                        Assert.Equal(PilotRig.Sha(v1), PilotRig.Sha(File.ReadAllBytes(mapped))); // the snapshot holds the content at its moment
                    }
                    finally { vss.Dispose(); }
                    Assert.False(File.Exists(mapped), "the snapshot is still reachable after it was removed");
                }
                Assert.Equal(linksBefore, PilotVss.Links());
                Assert.Equal(shadowsBefore, PilotVss.Shadows(vol));
            }
            finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
        }

        [Fact]
        public void ABackupWithShadowCopy_SendsALockedFile_IsASuccessWithoutError_RestoresIdentical_AndLeavesNoShadowCopy()
        {
            PilotVss.Need();
            using (var rig = new AgentRig())
            {
                rig.Set.Vss = true;
                rig.File("a.txt", "alpha");
                var locked = rig.File("ledger.mdb", PilotRig.Rnd(200000, 43));
                var want = AgentRig.Tree(rig.Src);
                var vol = PilotVss.Volume(rig.Src);
                var shadowsBefore = PilotVss.Shadows(vol); var linksBefore = PilotVss.Links();
                BackupRun r;
                using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) r = rig.Backup();
                Assert.True(r.Result == "BS_STOP_SUCCESS", r.Result + "\n" + string.Join("\n", r.LogLines));
                Assert.Contains(r.LogLines, l => l.Contains("Shadow Copy Set successfully created"));
                Assert.Contains(r.LogLines, l => l.Contains("Deleting Shadow Copy snapshot"));
                Assert.Equal(new[] { "a.txt", "ledger.mdb" }, PilotRig.Objects(rig, r.Job).Select(q => PilotRig.RelOf(rig, q)).OrderBy(x => x, StringComparer.Ordinal));
                Assert.Equal(want, rig.Restored());
                Assert.Equal(linksBefore, PilotVss.Links());
                Assert.Equal(shadowsBefore, PilotVss.Shadows(vol));
            }
        }

        [Fact]
        public void WhenTheRunFails_TheShadowCopyIsRemovedToo_AndTheNextRunCompletes()
        {
            PilotVss.Need();
            using (var rig = new AgentRig())
            {
                rig.Set.Vss = true;
                rig.File("a.txt", "alpha"); rig.File("b.txt", "beta");
                var want = AgentRig.Tree(rig.Src);
                var vol = PilotVss.Volume(rig.Src);
                var shadowsBefore = PilotVss.Shadows(vol); var linksBefore = PilotVss.Links();
                rig.Server.Handler = q => q.Action == "object" ? StubServer.Answer.Error(500, "INTERNAL", "The server failed") : null;
                var failed = rig.Backup();
                Assert.Equal("BS_STOP_BY_SYSTEM_ERROR", failed.Result);
                Assert.Contains(failed.LogLines, l => l.Contains("Shadow Copy Set successfully created"));
                Assert.Contains(failed.LogLines, l => l.Contains("Deleting Shadow Copy snapshot"));
                Assert.Equal(linksBefore, PilotVss.Links());
                Assert.Equal(shadowsBefore, PilotVss.Shadows(vol));

                rig.Server.Handler = q => null;
                var r = rig.Backup();
                Assert.True(r.Result == "BS_STOP_SUCCESS", r.Result + "\n" + string.Join("\n", r.LogLines));
                Assert.Equal(want, rig.Restored());
                Assert.Equal(shadowsBefore, PilotVss.Shadows(vol));
            }
        }

        /// <summary>
        /// Integrity + recovery: the file is rewritten AFTER the snapshot was taken and before anything is read (in the
        /// server's answer to "begin", which the agent asks once the snapshot exists). The run must hold the content at
        /// snapshot time, and the NEXT run must send the newer content (the change is not lost). A third run with nothing
        /// changed sends nothing. Oracle: SHA-256 of each point's restore, the counters of each run.
        /// </summary>
        [Fact]
        public void AFileRewrittenAfterTheSnapshot_ThePointHoldsTheSnapshotContent_AndTheNextRunSendsTheNewContent()
        {
            PilotVss.Need();
            using (var rig = new AgentRig())
            {
                rig.Set.Vss = true;
                rig.File("a.txt", "alpha");
                var v1 = PilotRig.Rnd(250000, 44); var v2 = PilotRig.Rnd(260000, 45);
                var mail = rig.File("mail.pst", v1);
                var atSnapshot = AgentRig.Tree(rig.Src);
                var vol = PilotVss.Volume(rig.Src);
                var shadowsBefore = PilotVss.Shadows(vol); var linksBefore = PilotVss.Links();
                BackupRun r; bool rewritten = false;
                using (var held = new FileStream(mail, FileMode.Open, FileAccess.ReadWrite, FileShare.None))   // the mail program holds it
                {
                    rig.Server.Handler = q =>
                    {
                        if (q.Action == "begin" && !rewritten) { held.SetLength(0); held.Write(v2, 0, v2.Length); held.Flush(true); rewritten = true; }
                        return null;
                    };
                    r = rig.Backup();
                    rig.Server.Handler = q => null;
                }
                Assert.True(rewritten, "the run never asked the server to begin");
                Assert.True(r.Result == "BS_STOP_SUCCESS", r.Result + "\n" + string.Join("\n", r.LogLines));
                Assert.Contains(r.LogLines, l => l.Contains("Shadow Copy Set successfully created"));
                Assert.Equal(PilotRig.Sha(v2), PilotRig.Sha(File.ReadAllBytes(mail)));         // the live file did change during the run
                Assert.Equal(atSnapshot, rig.Restored("p1"));                                   // the point: the moment of the snapshot
                Assert.Equal(linksBefore, PilotVss.Links());
                Assert.Equal(shadowsBefore, PilotVss.Shadows(vol));

                var r2 = rig.Backup();                                                          // the change made during run 1
                Assert.True(r2.Result == "BS_STOP_SUCCESS", r2.Result + "\n" + string.Join("\n", r2.LogLines));
                Assert.True(r2.Updated == 1, "the next run did not send the content written after the snapshot (updated=" + r2.Updated + ", new=" + r2.New + ", perm=" + r2.PermOnly + ")");
                Assert.Equal(AgentRig.Tree(rig.Src), rig.Restored("p2"));

                var r3 = rig.Backup();                                                          // nothing changed: nothing sent
                Assert.Equal("BS_STOP_SUCCESS", r3.Result);
                Assert.Equal(0, r3.New + r3.Updated + r3.PermOnly + r3.Deleted);
                Assert.Equal(shadowsBefore, PilotVss.Shadows(vol));
            }
        }

        /// <summary>Boundary / failure: a drive letter that does not exist gets no snapshot, a warning that names it, no tool
        /// left running and nothing left behind (Vss.Create returns null).</summary>
        [Fact]
        public void ADriveLetterThatDoesNotExist_GetsNoShadowCopy_AWarningNamesIt_NothingIsLeftBehind()
        {
            PilotVss.Need();
            var free = PilotVss.FreeLetter();
            var linksBefore = PilotVss.Links();
            var warnings = new List<string>();
            var v = Vss.Create(new[] { free + @"\Data" }, warnings.Add);
            Assert.Null(v);
            Assert.Contains(warnings, w => w.Contains("Shadow Copy of " + free));
            Assert.Equal(linksBefore, PilotVss.Links());
        }

        /// <summary>Runs everywhere: a source that is not on a drive letter (a share, a relative or a Unix path) gets no
        /// snapshot, no tool is started, nothing is warned — the run reads the live files as before.</summary>
        [Fact]
        public void SourcesWithoutADriveLetter_GetNoShadowCopy_AndNoWarning()
        {
            var warnings = new List<string>();
            var v = Vss.Create(new[] { @"\\server\share\data", "relative" + Path.DirectorySeparatorChar + "data", "/srv/data" }, warnings.Add);
            Assert.Null(v);
            Assert.Empty(warnings);
        }
    }
}
