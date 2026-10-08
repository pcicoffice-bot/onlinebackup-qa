using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// The challenge tests of PilotChallengeStoreTests that FAIL because of the product (bug candidates, kept failing on
    /// purpose; see the report of the pilot challenge of RS-06 / ST-04 / ST-09).
    /// </summary>
    public partial class PilotChallengeStoreTests
    {
        /// <summary>
        /// Challenges RS06_AChangedOrMissingSource_IsNotACandidate_NothingToCompare_IsZeroChecked_NeverFailed: that test sees
        /// "0 checked" only at the agent and at a stand-in server that stores the report and says nothing. The contract ("no
        /// source to compare = NOT CHECKED, never FAILED") is about what the provider SEES. The real server records the report
        /// in three places: the set (RESTORE_TEST_RESULT), the run history (Runs: result + colour), and tickets/mail. Every
        /// place must not say FAILED.
        /// </summary>
        [Fact]
        public void RS06_NothingToCompare_IsNotRecordedAsFailed_InTheProfile_NorInTheRunHistory()
        {
            using (var env = new Env())
            {
                env.CreateUser("chrs06n", Pw);
                var app = env.Agent("chrs06n", Pw);
                var src = env.Dir("src");
                var files = Enumerable.Range(0, 3).Select(i => Path.Combine(src, "f" + i + ".bin")).ToList();
                for (int i = 0; i < files.Count; i++) Write(files[i], Rnd(20000 + i, 300 + i), i);
                var set = app.CreateSet(app.Interactive(Pw, null), Pw, new BackupSetInfo { Name = "Files", Sources = { src }, Vss = false });
                Backup(app, set.Id);
                // every source changed after the backup (a newer time): nothing can be compared
                foreach (var f in files) File.SetLastWriteTimeUtc(f, File.GetLastWriteTimeUtc(f).AddSeconds(30));
                jump += TimeSpan.FromMinutes(1);

                var t = app.RestoreTest(set.Id, 10);
                Assert.Equal(0, t.Int("candidates")); Assert.Equal(0, t.Int("checked")); Assert.Equal(0, t.Int("failed"));
                var attr = (string)Profile.Load(Path.Combine(env.HomeA, "chrs06n", "db", "Profile.xml")).FindSet(set.Id).Attribute("RESTORE_TEST_RESULT");
                Assert.StartsWith("NOT_CHECKED", attr);                                              // the set: said right
                var run = Assert.Single(env.Api.Runs.Since(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddDays(1)), m => m["kind"] == "RestoreTest");
                Assert.True(run["result"] != "FAILED" && run["status"] != "bad",
                    "nothing to compare is recorded in the run history as result=" + run["result"] + " status=" + run["status"] + " (the set itself says " + attr + ")");
            }
        }

        /// <summary>
        /// The provider's data-protection report (Compliance) prints the restore certificate from RESTORE_TEST_RESULT. Its
        /// three values: OK (passed), FAILED (failed), NOT_CHECKED (nothing to compare). Controls: OK -> Passed, FAILED ->
        /// Failed (so the oracle can fail). NOT_CHECKED must never be printed as Failed.
        /// </summary>
        [Fact]
        public void RS06_TheDataProtectionReport_NeverPrintsNothingToCompareAsFailed()
        {
            var now = new DateTime(2026, 4, 1, 12, 0, 0, DateTimeKind.Utc);
            var p = Profile.Create("acme", "Acme", "x", "en", "UTC");
            Func<string, string> cert = result =>
            {
                var facts = new List<Compliance.SetFacts> { new Compliance.SetFacts { Set = new BackupSetInfo { Id = "1700000000777", Name = "Office" }, LastBackup = now.AddHours(-2), LastTest = now.AddDays(-1), TestResult = result } };
                var html = Compliance.Html("en", "OnlineBackup", "Pilot IT", p, facts, false, false, false, now);
                var at = html.IndexOf("<h2>3. ", StringComparison.Ordinal); var end = html.IndexOf("<h2>4. ", StringComparison.Ordinal);
                Assert.True(at > 0 && end > at);
                return html.Substring(at, end - at);
            };
            Assert.Contains("Passed", cert("OK 5/5")); Assert.DoesNotContain("Failed", cert("OK 5/5"));
            Assert.Contains("Failed", cert("FAILED 3/4"));
            var nc = cert("NOT_CHECKED 0/0");
            Assert.False(nc.Contains("Failed"), "the restore certificate prints a test with nothing to compare as: " + System.Text.RegularExpressions.Regex.Replace(nc, "<[^>]+>", " "));
        }

        /// <summary>
        /// Challenges SetStoreComponentTests.ADamagedObject_IsQuarantinedAndAskedAgain_ALostIndexIsRebuilt, which rebuilds the
        /// lost index FIRST and damages after - so it never sees damage found before the index was lost. Order here: the
        /// monthly verify finds a damaged object and quarantines it; before the next backup the index is lost (disk error)
        /// and rebuilt from the disk. Contract ST-04: "resent from the source". The damage is still on the disk (the
        /// quarantined object and its .chk), so after the rebuild the file must still be asked again and still be said
        /// damaged in the newest point - not silently gone (the agent believes it is backed up and never sends it again).
        /// </summary>
        [Fact]
        public void ST04_DamageFoundThenTheIndexLost_TheRebuiltIndexStillAsksForTheFile_AndStillSaysItIsDamaged()
        {
            var r = new Rig(Path.Combine(root, "user"), "1700000000622");
            var t = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
            r.Run(t, new[] { "a.txt", "b.txt" }, None, None);
            var j2 = r.Run(t.AddDays(1), new[] { "a.txt" }, None, None);
            var pb = r.St.ObjectPath(r.St.FilesAt(null).List("files").Single(f => f["rel"] == N("C/b.txt")).List("objects")[0]["loc"]);
            var b = File.ReadAllBytes(pb); b[b.Length / 2] ^= 0x10; File.WriteAllBytes(pb, b);
            Assert.Equal(1, r.St.VerifyAll().Int("bad"));
            Assert.Equal(new[] { N("C/b.txt") }, r.St.Resend());                                 // control: before the loss
            Assert.True(r.St.FilesAt(j2).List("files").Single(f => f["rel"] == N("C/b.txt")).Bool("damaged"));

            r.LoseIndex();
            Assert.Equal(r.Jobs, r.St.Points());
            r.AssertExactly(j2, new[] { "b.txt" });
            var resend = r.St.Resend();
            var bInPoint = r.St.FilesAt(j2).List("files").FirstOrDefault(f => f["rel"] == N("C/b.txt"));
            Assert.True(resend.Contains(N("C/b.txt")) && bInPoint != null && bInPoint.Bool("damaged"),
                "after the index was rebuilt: resend=[" + string.Join(",", resend) + "], b.txt in the newest point: " + (bInPoint == null ? "absent (silently left out)" : "damaged=" + bInPoint.Bool("damaged")));
        }

        /// <summary>
        /// The other way damage is found: the administrator's "rebuild with verify" (Rebuild(true)) on a store whose index
        /// is there. Expected, as for VerifyAll (B-4): the file is asked again AND the points that held it say it is damaged
        /// - never silently left out of the restore point.
        /// </summary>
        [Fact]
        public void ST04_RebuildWithVerify_FindsTheDamage_AsksAgain_AndThePointSaysDamaged_NotSilentlyLeftOut()
        {
            var r = new Rig(Path.Combine(root, "user"), "1700000000623");
            var t = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
            var j1 = r.Run(t, new[] { "a.txt", "b.txt" }, None, None);
            var pb = r.St.ObjectPath(r.St.FilesAt(null).List("files").Single(f => f["rel"] == N("C/b.txt")).List("objects")[0]["loc"]);
            var b = File.ReadAllBytes(pb); b[b.Length / 2] ^= 0x10; File.WriteAllBytes(pb, b);

            var rb = r.St.Rebuild(true);
            Assert.Equal(1, rb.Int("bad"));
            Assert.False(File.Exists(pb));
            Assert.Contains(N("C/b.txt"), r.St.Resend());
            r.AssertExactly(j1, new[] { "b.txt" });
            var bInPoint = r.St.FilesAt(j1).List("files").FirstOrDefault(f => f["rel"] == N("C/b.txt"));
            Assert.True(bInPoint != null && bInPoint.Bool("damaged"),
                "after Rebuild(verify) found b.txt damaged, the point lists it as: " + (bInPoint == null ? "absent (silently left out)" : "damaged=" + bInPoint.Bool("damaged")));
        }

        /// <summary>
        /// Challenges the recycle-bin tests: there the set is deleted while nothing is happening. A set is often deleted
        /// because its computer misbehaves - while a backup of it is running (it reported progress). The server's sweep
        /// then closes that run as interrupted 5 minutes later. Contract ST-09: "restorable from the recycle bin until it
        /// expires": after the sweep the set must still come back from the bin, byte-identical (SHA-256 of the sources).
        /// </summary>
        [Fact]
        public void ST09_ASetDeletedWhileItsBackupRuns_StillComesBackFromTheBin_AfterTheServerClosesTheRun()
        {
            using (var env = new Env())
            {
                env.CreateUser("acme", Pw);
                var app = env.Agent("acme", Pw);
                var src = env.Dir("src");
                File.WriteAllBytes(Path.Combine(src, "ledger.bin"), Rnd(200000, 1));
                File.WriteAllText(Path.Combine(src, "notes.txt"), "kept in the recycle bin");
                var set = app.CreateSet(app.Interactive(Pw, null), Pw, new BackupSetInfo { Name = "Office", Sources = { src }, Vss = false });
                Backup(app, set.Id);
                var want = Tree(src);
                // the next backup is running (a minute later): it reports its progress
                jump += TimeSpan.FromMinutes(1);
                app.DeviceClient().Call("POST", "/api/sets/" + set.Id + "/progress", new Msg().Set("job", RunId.From(SystemClock.UtcNow)).Set("files", 1).Set("bytes", 1000).Set("percent", 40));
                Assert.Contains(env.Api.Live(), m => m["set"] == set.Id);
                var admin = env.Admin();
                Assert.Equal("1", admin.Call("POST", "/api/admin/users/acme/delete", new Msg().Set("set", set.Id))["deleted"]);
                var setFolder = Path.Combine(env.HomeA, "acme", "files", set.Id);
                Assert.False(Directory.Exists(setFolder));
                // the computer goes quiet; the server's sweep runs after the lease
                env.Api.SweepInterrupted(SystemClock.UtcNow.AddMinutes(6));
                var item = admin.Call("GET", "/api/admin/recycle").List("items").Single();
                string error = null;
                try { admin.Call("POST", "/api/admin/recycle/" + Uri.EscapeDataString(item["id"]) + "/restore"); }
                catch (AgentException e) { error = e.Status + " " + e.Message; }
                Assert.True(error == null, "the set cannot be restored from the recycle bin after the sweep: " + error + (Directory.Exists(setFolder) ? " (the sweep made a new empty " + Path.Combine("files", set.Id) + " folder: " + string.Join(",", Directory.GetFileSystemEntries(setFolder).Select(Path.GetFileName)) + ")" : ""));
                Assert.Equal(want.OrderBy(k => k.Key, StringComparer.Ordinal), RestoreTree(env, app, set.Id, src, "back").OrderBy(k => k.Key, StringComparer.Ordinal));
            }
        }
    }
}
