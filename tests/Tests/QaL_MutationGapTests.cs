using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// QA agent L (night): tests that kill mutants the existing suite let survive (tests/QA/night/l/MUTATION-REPORT.md).
    /// Each names the production check it protects; each was shown to FAIL with that one check disabled.
    /// </summary>
    public class QaL_MutationGapTests : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "obqal-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        readonly KeySet key = KeySet.Random();
        public void Dispose() { try { Directory.Delete(root, true); } catch (Exception) { } }
        static string Sha(byte[] b) { return Bytes.Hex(Bytes.Sha256(b)); }

        sealed class Source : IRestoreSource
        {
            public readonly Dictionary<string, byte[]> Objects = new Dictionary<string, byte[]>();
            public Msg Point = new Msg();
            public List<string> Points() { return new List<string> { "2026-03-10-22-00-00" }; }
            public Msg Files(string point) { return Point; }
            public void Fetch(string loc, string toFile) { File.WriteAllBytes(toFile, Objects[loc]); }
            public void Report(Msg log) { }
        }

        /// <summary>A real backup object of one file (as BackupRun writes it), stored under loc; returns its bytes.</summary>
        byte[] Object(string path, byte[] data)
        {
            var ms = new MemoryStream(); var w = new BackupObject.Writer(ms, key);
            var recipe = new List<KeyValuePair<string, int>>(); var seen = new HashSet<string>();
            foreach (var c in Chunker.ForFileSize(data.Length).Split(new MemoryStream(data)))
            {
                var id = BackupObject.ChunkId(key, c); recipe.Add(new KeyValuePair<string, int>(id, c.Length));
                if (seen.Add(id)) w.AddChunk(id, c);
            }
            var header = new Msg().Set("path", path).Set("size", data.Length).Set("mtime", 1700000000000).Set("attrs", "").Set("kind", "F").Set("seq", 0);
            foreach (var r in recipe) header.Add("recipe", new Msg().Set("h", r.Key).Set("n", r.Value));
            w.Finish(header);
            return ms.ToArray();
        }

        /// <summary>
        /// Restore.RestoreFile compares every downloaded object with the checksum the server's index holds. Without that
        /// check a WRONG but valid object (same customer key: another file's object, a misplaced file in the store, a
        /// replica that answered with another version) is decrypted and authenticated chunk by chunk and written under
        /// the asked name as a success — the existing tests only damage bytes, which the chunk authentication catches
        /// on its own, so they pass with the checksum check removed (mutant M03 survived).
        /// Oracle: the bytes on disk (SHA-256) and the restore's own counts.
        /// </summary>
        [Fact]
        public void AValidObjectOfAnotherFile_IsRefusedByItsChecksum_NotRestoredUnderTheAskedName()
        {
            var src = new Source();
            var salary = System.Text.Encoding.UTF8.GetBytes("salaries 2026: confidential");
            var letter = System.Text.Encoding.UTF8.GetBytes("a letter to a customer");
            var oSalary = Object("/data/salary.txt", salary); var oLetter = Object("/data/letter.txt", letter);
            src.Objects["Current/1"] = oSalary; src.Objects["Current/2"] = oLetter;
            // the index says letter.txt is object Current/2 with the checksum of the letter's object; the store hands out
            // the salary object at that place (the bytes under Current/2 are the other file's valid object)
            src.Objects["Current/2"] = oSalary;
            var f = new Msg().Set("rel", "R2").Set("enc", NameCipher.EncryptPath(key, "/data/letter.txt")).Set("orig", letter.Length).Set("mtime", 1700000000000);
            f.Add("objects", new Msg().Set("loc", "Current/2").Set("seq", 0).Set("kind", "F").Set("size", oLetter.Length).Set("sha", Sha(oLetter)));
            src.Point.Add("files", f);
            var target = Path.Combine(root, "t");
            var r = new Restore(src, key, Path.Combine(root, "tmp")); r.Run(null, target, null, false);
            var restored = Path.Combine(target, "data", "letter.txt");
            Assert.False(File.Exists(restored) && Sha(File.ReadAllBytes(restored)) == Sha(salary),
                "letter.txt was restored with the bytes of salary.txt (result " + r.Result + ")");
            Assert.Equal("RESTORE_STOP_WITH_ERROR", r.Result);
            Assert.Equal(1, r.Failed);
        }

        /// <summary>
        /// LocalState.Read (D-2): an index whose "#end" count does not match its lines is damaged. The existing test
        /// (IndexLinesLostComponentTests) also tears the "#end" line itself, which the "damaged line" check catches — so it
        /// passes with the count check removed (mutant M01 survived). Here lines are lost and the "#end" line is intact:
        /// only the count says the index is short. Oracle: the newest point restored, by name and SHA-256, against the source.
        /// </summary>
        [Fact]
        public void LinesLost_EndLineIntact_TheCountAloneFindsIt_DeletedFilesLeaveTheNewestPoint()
        {
            using (var env = new Env())
            {
                env.CreateUser("qal1", "Customer-Pass-1");
                var app = env.Agent("qal1", "Customer-Pass-1");
                var src = env.Dir("src");
                for (int i = 0; i < 12; i++) File.WriteAllText(Path.Combine(src, "f" + i.ToString("00") + ".txt"), "file " + i);
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "L", Sources = { src }, Vss = false });
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);
                for (int i = 0; i < 4; i++) File.Delete(Path.Combine(src, "f" + i.ToString("00") + ".txt"));
                var st = Path.Combine(app.Home.SetDir(set.Id), "state.txt");
                var all = File.ReadAllLines(st);
                Assert.Contains("#end\t12", all);                                                      // precondition: the format under test
                var kept = all.Where(l => !System.Text.RegularExpressions.Regex.IsMatch(l, @"f0[0-3]\.txt\t")).ToList();
                Assert.Equal(all.Length - 4, kept.Count);                                               // the fault really injected
                File.WriteAllText(st, string.Join("\n", kept) + "\n");
                Assert.StartsWith("BS_STOP_SUCCESS", app.Backup(set.Id).Result);
                var target = env.Dir("restore");
                app.RestoreFor(app.Interactive("Customer-Pass-1", null), set.Id).Run(null, target, null, false);
                var got = Directory.GetFiles(Path.Combine(target, Env.Rel(src))).Select(Path.GetFileName).OrderBy(x => x).ToList();
                var want = Directory.GetFiles(src).Select(Path.GetFileName).OrderBy(x => x).ToList();
                Assert.True(want.SequenceEqual(got), "the newest point holds " + string.Join(",", got) + " — the source holds " + string.Join(",", want));
                foreach (var n in want) Assert.Equal(Sha(File.ReadAllBytes(Path.Combine(src, n))), Sha(File.ReadAllBytes(Path.Combine(target, Env.Rel(src), n))));
            }
        }

        /// <summary>
        /// Restore.RestoreFile refuses a file whose chunks do not add up to the size its object header states. No existing
        /// test builds such an object, so the check can be removed with every test still passing (mutant M19 survived).
        /// The case is real: bug B-2 was exactly a header size that differed from the bytes read. Oracle: no file of the
        /// wrong length appears under the asked name; the restore says it failed.
        /// </summary>
        [Fact]
        public void AnObjectWhoseChunksDoNotMakeItsStatedSize_IsAFailedFile_NotAShorterFile()
        {
            var src = new Source();
            var data = System.Text.Encoding.UTF8.GetBytes("the whole invoice, 41 bytes long, no less");
            var ms = new MemoryStream(); var w = new BackupObject.Writer(ms, key);
            var id = BackupObject.ChunkId(key, data); w.AddChunk(id, data);
            var header = new Msg().Set("path", "/data/invoice.txt").Set("size", data.Length + 4096).Set("mtime", 1700000000000).Set("attrs", "").Set("kind", "F").Set("seq", 0);
            header.Add("recipe", new Msg().Set("h", id).Set("n", data.Length));
            w.Finish(header);
            var bytes = ms.ToArray(); src.Objects["Current/1"] = bytes;
            var f = new Msg().Set("rel", "R1").Set("enc", NameCipher.EncryptPath(key, "/data/invoice.txt")).Set("orig", data.Length + 4096).Set("mtime", 1700000000000);
            f.Add("objects", new Msg().Set("loc", "Current/1").Set("seq", 0).Set("kind", "F").Set("size", bytes.Length).Set("sha", Sha(bytes)));
            src.Point.Add("files", f);
            var target = Path.Combine(root, "s");
            var r = new Restore(src, key, Path.Combine(root, "tmp")); r.Run(null, target, null, false);
            Assert.False(File.Exists(Path.Combine(target, "data", "invoice.txt")), "a file of " + data.Length + " bytes was restored for a backed-up size of " + (data.Length + 4096) + " (result " + r.Result + ")");
            Assert.Equal("RESTORE_STOP_WITH_ERROR", r.Result);
        }

        /// <summary>
        /// RS-02 / bug 74: the restore's temporary file must have a name no customer file can already have. The existing
        /// test (RestoreTempNameComponentTests) uses the OLD name "report.txt.restoring", so a fixed, predictable name such
        /// as "report.txt.ob-restoring" passes it (mutant M15 survived). Here the backup holds a customer file named exactly
        /// "&lt;name&gt;.ob-restoring"; restoring both must give both back with their own bytes (SHA-256).
        /// </summary>
        [Fact]
        public void ACustomerFileNamedLikeTheCurrentTemporarySuffix_DoesNotBreakTheRestore()
        {
            var src = new Source();
            var report = System.Text.Encoding.UTF8.GetBytes("the report");
            var own = System.Text.Encoding.UTF8.GetBytes("the customer's own file that happens to have this name");
            int n = 0;
            foreach (var kv in new[] { new KeyValuePair<string, byte[]>("/data/report.txt.ob-restoring", own), new KeyValuePair<string, byte[]>("/data/report.txt", report) })
            {
                var b = Object(kv.Key, kv.Value); var loc = "Current/" + (++n); src.Objects[loc] = b;
                var f = new Msg().Set("rel", "R" + n).Set("enc", NameCipher.EncryptPath(key, kv.Key)).Set("orig", kv.Value.Length).Set("mtime", 1700000000000);
                f.Add("objects", new Msg().Set("loc", loc).Set("seq", 0).Set("kind", "F").Set("size", b.Length).Set("sha", Sha(b)));
                src.Point.Add("files", f);
            }
            var target = Path.Combine(root, "n");
            var r = new Restore(src, key, Path.Combine(root, "tmp")); r.Run(null, target, null, false);
            Assert.Equal("RESTORE_STOP_SUCCESS", r.Result);
            Assert.Equal(Sha(report), Sha(File.ReadAllBytes(Path.Combine(target, "data", "report.txt"))));
            Assert.Equal(Sha(own), Sha(File.ReadAllBytes(Path.Combine(target, "data", "report.txt.ob-restoring"))));
        }

        /// <summary>
        /// AgentApp.Mine (D-4) decides which sets this computer runs on schedule. No xUnit test calls it (only the browser
        /// journeys F12/F14 come near it), so both of its checks can be removed with every xUnit test passing (mutant M18
        /// survived). Oracle: the set's computer as the server stores it.
        /// </summary>
        [Fact]
        public void ASetOfAnotherComputer_OrOfASameNamedComputerWithoutItsKey_IsNotThisComputers()
        {
            using (var env = new Env())
            {
                env.CreateUser("qalmine", "Customer-Pass-1");
                var a = env.Agent("qalmine", "Customer-Pass-1", null, "a");
                var b = env.Agent("qalmine", "Customer-Pass-1", null, "b");
                var a2 = env.Agent("qalmine", "Customer-Pass-1", null, "a");      // another machine that calls itself PC-a
                var src = env.Dir("src");
                var made = a.CreateSet(a.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "A", Sources = { src }, Vss = false });
                Func<AgentApp, BackupSetInfo> seen = app => app.Sets().Single(x => x.Id == made.Id);
                Assert.Equal("PC-a", seen(b).Computer);                                       // precondition: the server says whose it is
                Assert.True(a.Mine(seen(a)), "the computer that made the set does not run it");
                Assert.False(b.Mine(seen(b)), "PC-b runs the set of PC-a");
                Assert.False(a2.Mine(seen(a2)), "a second computer named PC-a, without the set's key, runs the set of the first");
                // a set made on PC-a FOR the computer PC-b (no device of its own yet): PC-b's, never PC-a's
                var forB = a.CreateSet(a.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "B", Computer = "PC-b", Sources = { src }, Vss = false });
                Assert.Equal("PC-b", a.Sets().Single(x => x.Id == forB.Id).Computer);
                Assert.False(a.Mine(a.Sets().Single(x => x.Id == forB.Id)), "PC-a runs a set made for PC-b");
            }
        }
    }
}
