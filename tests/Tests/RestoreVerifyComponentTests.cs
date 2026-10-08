using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// Owner decisions UI-Q1 / UI-Q2 on the restore engine (Restore, own engine) — component contract:
    ///   UI-Q1  after a file is written it is read back from the disk and checked against what the backup recorded: the
    ///          whole-file SHA-256 ("fsha", recorded by backups from now on) and every chunk's keyed id + the size (recorded by
    ///          every backup). The result says how many files were verified and how (whole-file SHA-256 or chunks only); a
    ///          mismatch is named, the file is NOT put in place (the file already there stays), and the restore is not a
    ///          success. "Verified" counts only files whose check really ran.
    ///   UI-Q2  restore to the original location with files already there: with no decision (ExistingFiles.Ask) nothing is
    ///          written and the caller gets which / how many files exist; Skip keeps them (said in the result), Overwrite
    ///          replaces them. Conflicts() answers the question before the restore.
    /// Oracle: the bytes on the disk (SHA-256), the engine's counters and the record sent to the server.
    /// </summary>
    public class RestoreVerifyComponentTests : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "obrverify-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        readonly KeySet key = KeySet.Random();
        public void Dispose() { try { Directory.Delete(root, true); } catch (Exception) { } }
        static string Sha(byte[] b) { using (var h = SHA256.Create()) return Bytes.Hex(h.ComputeHash(b)); }
        static string ShaFile(string f) { return Sha(File.ReadAllBytes(f)); }

        sealed class MemorySource : IRestoreSource
        {
            public readonly Dictionary<string, byte[]> Objects = new Dictionary<string, byte[]>();
            public Msg Point = new Msg();
            public Msg Reported; public int Reports;
            public List<string> Points() { return new List<string> { "2026-03-10-22-00-00" }; }
            public Msg Files(string point) { return Point; }
            public void Fetch(string loc, string toFile) { File.WriteAllBytes(toFile, Objects[loc]); }
            public void Report(Msg log) { Reported = log; Reports++; }
        }

        /// <param name="fsha">the whole-file SHA-256 the backup recorded: null = an older backup (none recorded), "" = the real one</param>
        void Add(MemorySource src, string path, byte[] data, string fsha = "")
        {
            var ms = new MemoryStream(); var w = new BackupObject.Writer(ms, key);
            var recipe = new List<KeyValuePair<string, int>>(); var seen = new HashSet<string>();
            foreach (var c in Chunker.ForFileSize(data.Length).Split(new MemoryStream(data)))
            {
                var id = BackupObject.ChunkId(key, c); recipe.Add(new KeyValuePair<string, int>(id, c.Length));
                if (seen.Add(id)) w.AddChunk(id, c);
            }
            var header = new Msg().Set("path", path).Set("size", data.Length).Set("mtime", 1700000000000L).Set("attrs", "").Set("kind", "F").Set("seq", 0);
            if (fsha != null) header.Set("fsha", fsha == "" ? Sha(data) : fsha);
            foreach (var r in recipe) header.Add("recipe", new Msg().Set("h", r.Key).Set("n", r.Value));
            w.Finish(header);
            var bytes = ms.ToArray(); var loc = "Current/" + src.Objects.Count;
            src.Objects[loc] = bytes;
            var f = new Msg().Set("rel", "R" + src.Objects.Count).Set("enc", NameCipher.EncryptPath(key, path)).Set("orig", data.Length).Set("mtime", 1700000000000L);
            f.Add("objects", new Msg().Set("loc", loc).Set("seq", 0).Set("kind", "F").Set("size", bytes.Length).Set("sha", Sha(bytes)));
            src.Point.Add("files", f);
        }

        Dictionary<string, byte[]> Dataset(MemorySource src, string baseDir, bool recorded = true)
        {
            var big = new byte[300000]; new Random(5).NextBytes(big);
            var files = new Dictionary<string, byte[]> { { Path.Combine(baseDir, "empty.txt"), new byte[0] }, { Path.Combine(baseDir, "a.txt"), System.Text.Encoding.UTF8.GetBytes("invoice 7") }, { Path.Combine(baseDir, "big.bin"), big } };
            foreach (var kv in files) Add(src, kv.Key, kv.Value, recorded ? "" : null);
            return files;
        }
        static string Dest(string target, string path) { return Path.Combine(target, path.Replace(":", "").TrimStart('\\', '/')); }

        [Fact]
        public void EveryRestoredFile_IsVerified_ByWholeFileSha256_WhenTheBackupRecordedIt_AndTheCountIsReported()
        {
            var src = new MemorySource(); var files = Dataset(src, "/data");
            var target = Path.Combine(root, "t");
            var r = new Restore(src, key, Path.Combine(root, "tmp")); r.Run(null, target, null, false);
            Assert.Equal("RESTORE_STOP_SUCCESS", r.Result);
            Assert.Equal(3, r.Restored); Assert.Equal(3, r.Verified); Assert.Equal(3, r.VerifiedSha256); Assert.Empty(r.Mismatched);
            foreach (var kv in files) Assert.Equal(Sha(kv.Value), ShaFile(Dest(target, kv.Key)));
            Assert.Equal(3, src.Reported.Int("verified")); Assert.Equal(3, src.Reported.Int("verifiedSha256")); Assert.Equal(0, src.Reported.Int("mismatched"));
            Assert.Contains(r.Log, l => l.Contains("verified") && l.Contains("3"));
        }

        [Fact]
        public void AnOlderBackup_WithoutWholeFileSha_IsVerifiedByItsChunks_AndSaysSo_NeverAsSha256()
        {
            var src = new MemorySource(); var files = Dataset(src, "/old", recorded: false);
            var target = Path.Combine(root, "t");
            var r = new Restore(src, key, Path.Combine(root, "tmp")); r.Run(null, target, null, false);
            Assert.Equal("RESTORE_STOP_SUCCESS", r.Result);
            Assert.Equal(3, r.Verified); Assert.Equal(0, r.VerifiedSha256);
            foreach (var kv in files) Assert.Equal(Sha(kv.Value), ShaFile(Dest(target, kv.Key)));
        }

        /// <summary>Fails before the change with only the existing API: the restore said SUCCESS and replaced the customer's file
        /// although the content does not match the SHA-256 the backup recorded.</summary>
        [Fact]
        public void ContentNotMatchingTheRecordedSha256_IsAFailedFile_NotPutInPlace_TheExistingFileStays()
        {
            var place = Path.Combine(root, "orig"); Directory.CreateDirectory(place);
            var src = new MemorySource();
            var good = Path.Combine(place, "good.txt"); Add(src, good, System.Text.Encoding.UTF8.GetBytes("good"));
            var bad = Path.Combine(place, "contract.docx"); Add(src, bad, System.Text.Encoding.UTF8.GetBytes("restored bytes"), fsha: Sha(System.Text.Encoding.UTF8.GetBytes("what was really backed up")));
            File.WriteAllText(bad, "the customer's file");
            var r = new Restore(src, key, Path.Combine(root, "tmp")); r.Run(null, null, null, true);
            Assert.Equal("RESTORE_STOP_WITH_ERROR", r.Result);
            Assert.Equal(1, r.Failed); Assert.Equal(1, r.Restored);
            Assert.Equal("the customer's file", File.ReadAllText(bad));
            Assert.Equal("good", File.ReadAllText(good));
            Assert.Empty(Directory.GetFiles(place).Where(f => f.EndsWith(".ob-restoring")));
            Assert.Equal("RESTORE_STOP_WITH_ERROR", src.Reported["result"]);
            Assert.Contains(src.Reported.List("log"), l => l["l"].Contains(bad) && l["l"].Contains("integrity"));
            Assert.Equal(new[] { bad }, r.Mismatched.ToArray());
            Assert.Equal(1, r.Verified);   // only the good file
        }

        /// <summary>The file on the disk differs from what was written (a disk / filter driver / antivirus changed it): the check
        /// reads the disk, not the memory — caught for a recorded SHA-256 and, for an older backup, by the chunks.</summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AFileChangedOnTheDisk_AfterWriting_IsCaught_AndNamed(bool recorded)
        {
            var src = new MemorySource(); var files = Dataset(src, "/d" + recorded, recorded);
            var target = Path.Combine(root, "t" + recorded);
            var r = new Restore(src, key, Path.Combine(root, "tmp"));
            r.AfterWrite = tmp => { if (tmp.Contains("big.bin")) using (var fs = new FileStream(tmp, FileMode.Open, FileAccess.ReadWrite)) { fs.Position = 150000; var b = fs.ReadByte(); fs.Position = 150000; fs.WriteByte((byte)(b ^ 1)); } };
            r.Run(null, target, null, false);
            Assert.Equal("RESTORE_STOP_WITH_ERROR", r.Result);
            Assert.Equal(1, r.Failed); Assert.Equal(2, r.Restored); Assert.Equal(2, r.Verified);
            var big = files.Keys.Single(k => k.EndsWith("big.bin"));
            Assert.Equal(new[] { Dest(target, big) }, r.Mismatched.ToArray());
            Assert.False(File.Exists(Dest(target, big)));
            Assert.Empty(Directory.GetFiles(target, "*", SearchOption.AllDirectories).Where(f => f.EndsWith(".ob-restoring")));
            Assert.Equal(1, src.Reported.Int("mismatched"));
        }

        /// <summary>RS-06 (automatic restore test) and every other caller of RestoreFile get the same check.</summary>
        [Fact]
        public void RestoreFile_Alone_ChecksTheDisk_AndThrowsOnAMismatch()
        {
            var src = new MemorySource(); Dataset(src, "/rf");
            var r = new Restore(src, key, Path.Combine(root, "tmp"));
            var f = r.Files(null).Single(kv => kv.Key.EndsWith("a.txt"));
            var dest = Path.Combine(root, "rf", "a.txt");
            Assert.Equal("sha256", r.RestoreFile(f.Value, dest));
            r.AfterWrite = tmp => File.WriteAllText(tmp, "invoice 8");
            var e = Assert.Throws<InvalidDataException>(() => r.RestoreFile(f.Value, dest + "2"));
            Assert.Contains("integrity", e.Message);
            Assert.False(File.Exists(dest + "2"));
        }

        [Fact]
        public void OriginalLocation_WithoutADecision_WritesNothing_AndNamesTheExistingFiles()
        {
            var place = Path.Combine(root, "o"); Directory.CreateDirectory(place);
            var src = new MemorySource(); var files = Dataset(src, place);
            var existing = Path.Combine(place, "a.txt"); File.WriteAllText(existing, "newer work");
            var r = new Restore(src, key, Path.Combine(root, "tmp"));
            var c = r.Conflicts(null, null, null);
            Assert.Equal(3, c.Total); Assert.Equal(new[] { existing }, c.Existing.ToArray());
            var e = Assert.Throws<AgentException>(() => r.Run(null, null, null, ExistingFiles.Ask));
            Assert.Equal("EXISTS", e.Code); Assert.Contains("1", e.Message);
            Assert.Equal(new[] { existing }, r.Existing.ToArray());
            Assert.Equal("newer work", File.ReadAllText(existing));
            Assert.False(File.Exists(Path.Combine(place, "big.bin")));
            Assert.Equal(0, src.Reports);   // nothing ran: no record of a restore
            // no file there: Ask runs straight away
            var place2 = Path.Combine(root, "o2"); var src2 = new MemorySource(); Dataset(src2, place2);
            var r2 = new Restore(src2, key, Path.Combine(root, "tmp")); r2.Run(null, null, null, ExistingFiles.Ask);
            Assert.Equal("RESTORE_STOP_SUCCESS", r2.Result); Assert.Equal(3, r2.Verified);
        }

        [Fact]
        public void OriginalLocation_SkipKeepsThem_OverwriteReplacesThem_AndBothAreVerified()
        {
            var place = Path.Combine(root, "o"); Directory.CreateDirectory(place);
            var src = new MemorySource(); var files = Dataset(src, place);
            var existing = Path.Combine(place, "a.txt"); File.WriteAllText(existing, "newer work");
            var r = new Restore(src, key, Path.Combine(root, "tmp")); r.Run(null, null, null, ExistingFiles.Skip);
            Assert.Equal("RESTORE_STOP_WITH_WARNING", r.Result); Assert.Equal(1, r.Skipped); Assert.Equal(2, r.Restored); Assert.Equal(2, r.Verified);
            Assert.Equal("newer work", File.ReadAllText(existing));
            var r2 = new Restore(src, key, Path.Combine(root, "tmp")); r2.Run(null, null, null, ExistingFiles.Overwrite);
            Assert.Equal("RESTORE_STOP_SUCCESS", r2.Result); Assert.Equal(3, r2.Restored); Assert.Equal(3, r2.Verified);
            Assert.Equal(Sha(files[existing]), ShaFile(existing));
        }
    }
}
