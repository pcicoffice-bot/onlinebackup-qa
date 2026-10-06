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
    /// The restore alone (Restore with an in-memory source of real backup objects, no server) — component contract
    /// (tests/QA/specs.py RS-01 / RS-02):
    ///   purpose  give back exactly the files of a point, safely
    ///   input    a point with an empty file, a Hebrew name, a 300 KB file; restore all; one file only; to the original place
    ///            over an existing file without / with overwrite; an object damaged on the server; a source that fails
    ///            in the middle
    ///   expected all files byte-identical (SHA-256) with their times; the filter gives only its file; without overwrite the
    ///            existing file is untouched and the result is WITH_WARNING; with overwrite it is replaced; a damaged or
    ///            missing object is a failed file (WITH_ERROR), the other files are restored, and no half file is left
    /// </summary>
    public class RestoreComponentTests : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "obrestore-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        readonly KeySet key = KeySet.Random();
        public void Dispose() { try { Directory.Delete(root, true); } catch (Exception) { } }
        static string Sha(byte[] b) { using (var h = SHA256.Create()) return Bytes.Hex(h.ComputeHash(b)); }
        static string ShaFile(string f) { return Sha(File.ReadAllBytes(f)); }

        sealed class MemorySource : IRestoreSource
        {
            public readonly Dictionary<string, byte[]> Objects = new Dictionary<string, byte[]>();
            public Msg Point = new Msg();
            public Func<string, bool> FailFetch = loc => false;
            public Msg Reported;
            public List<string> Points() { return new List<string> { "2026-03-10-22-00-00" }; }
            public Msg Files(string point) { return Point; }
            public void Fetch(string loc, string toFile) { if (FailFetch(loc)) throw new IOException("the line broke"); File.WriteAllBytes(toFile, Objects[loc]); }
            public void Report(Msg log) { Reported = log; }
        }

        /// <summary>One file as the agent uploads it: chunks → object (with the recipe in the header).</summary>
        void Add(MemorySource src, string path, byte[] data, long mtime)
        {
            var ms = new MemoryStream(); var w = new BackupObject.Writer(ms, key);
            var recipe = new List<KeyValuePair<string, int>>(); var seen = new HashSet<string>();
            foreach (var c in Chunker.ForFileSize(data.Length).Split(new MemoryStream(data)))
            {
                var id = BackupObject.ChunkId(key, c); recipe.Add(new KeyValuePair<string, int>(id, c.Length));
                if (seen.Add(id)) w.AddChunk(id, c);
            }
            var header = new Msg().Set("path", path).Set("size", data.Length).Set("mtime", mtime).Set("attrs", "").Set("kind", "F").Set("seq", 0);
            foreach (var r in recipe) header.Add("recipe", new Msg().Set("h", r.Key).Set("n", r.Value));
            w.Finish(header);
            var bytes = ms.ToArray(); var loc = "Current/" + src.Objects.Count;
            src.Objects[loc] = bytes;
            var f = new Msg().Set("rel", "R" + src.Objects.Count).Set("enc", NameCipher.EncryptPath(key, path)).Set("orig", data.Length).Set("mtime", mtime);
            f.Add("objects", new Msg().Set("loc", loc).Set("seq", 0).Set("kind", "F").Set("size", bytes.Length).Set("sha", Sha(bytes)));
            src.Point.Add("files", f);
        }

        Dictionary<string, byte[]> Dataset(MemorySource src, string baseDir)
        {
            var big = new byte[300000]; new Random(3).NextBytes(big);
            var files = new Dictionary<string, byte[]> { { Path.Combine(baseDir, "empty.txt"), new byte[0] }, { Path.Combine(baseDir, "חשבוניות", "מס 7.txt"), System.Text.Encoding.UTF8.GetBytes("חשבונית") }, { Path.Combine(baseDir, "big.bin"), big } };
            foreach (var kv in files) Add(src, kv.Key, kv.Value, 1700000000000);
            return files;
        }
        string Dest(string target, string path) { return Path.Combine(target, path.Replace(":", "").TrimStart('\\', '/')); }

        [Fact]
        public void EveryFile_Identical_WithItsTime_AndTheFilterGivesOnlyItsFile()
        {
            var src = new MemorySource(); var files = Dataset(src, "/data");
            var target = Path.Combine(root, "all");
            var r = new Restore(src, key, Path.Combine(root, "tmp")); r.Run(null, target, null, false);
            Assert.Equal("RESTORE_STOP_SUCCESS", r.Result); Assert.Equal(3, r.Restored);
            foreach (var kv in files) { Assert.Equal(Sha(kv.Value), ShaFile(Dest(target, kv.Key))); Assert.Equal(RunId.FromUnixMs(1700000000000), File.GetLastWriteTimeUtc(Dest(target, kv.Key))); }
            Assert.Equal("RESTORE_STOP_SUCCESS", src.Reported["result"]);
            var one = Path.Combine(root, "one");
            new Restore(src, key, Path.Combine(root, "tmp")).Run(null, one, p => p.EndsWith("big.bin"), false);
            Assert.Single(Directory.GetFiles(one, "*", SearchOption.AllDirectories));
        }

        [Fact]
        public void OriginalPlace_WithoutOverwrite_KeepsTheFileAndSaysSo_WithOverwrite_ReplacesIt()
        {
            var place = Path.Combine(root, "orig"); Directory.CreateDirectory(place);
            var src = new MemorySource(); var files = Dataset(src, place);
            var existing = Path.Combine(place, "big.bin"); File.WriteAllText(existing, "the customer's newer work");
            var r = new Restore(src, key, Path.Combine(root, "tmp")); r.Run(null, null, null, false);
            Assert.Equal("RESTORE_STOP_WITH_WARNING", r.Result); Assert.Equal(1, r.Skipped);
            Assert.Equal("the customer's newer work", File.ReadAllText(existing));
            var r2 = new Restore(src, key, Path.Combine(root, "tmp")); r2.Run(null, null, null, true);
            Assert.Equal("RESTORE_STOP_SUCCESS", r2.Result);
            Assert.Equal(Sha(files[existing]), ShaFile(existing));
        }

        [Fact]
        public void ADamagedOrMissingObject_IsAFailedFile_TheOthersAreRestored_NoHalfFileLeft()
        {
            var src = new MemorySource(); var files = Dataset(src, "/data");
            // the big file's object is damaged on the server AFTER its checksum was taken again (a rebuilt index): the
            // checksum matches, a chunk inside does not authenticate
            var bigLoc = src.Point.List("files").Select(f => f.List("objects")[0]).Single(o => o.Long("size") > 100000)["loc"];
            var bad = (byte[])src.Objects[bigLoc].Clone(); bad[bad.Length / 3] ^= 0xFF; src.Objects[bigLoc] = bad;
            src.Point.List("files").Select(f => f.List("objects")[0]).Single(o => o["loc"] == bigLoc).Set("sha", Sha(bad));
            var target = Path.Combine(root, "dmg");
            var r = new Restore(src, key, Path.Combine(root, "tmp")); r.Run(null, target, null, false);
            Assert.Equal("RESTORE_STOP_WITH_ERROR", r.Result); Assert.Equal(1, r.Failed); Assert.Equal(2, r.Restored);
            Assert.False(File.Exists(Dest(target, "/data/big.bin")));
            Assert.Empty(Directory.GetFiles(target, "*", SearchOption.AllDirectories).Where(f => !files.Keys.Any(k => Dest(target, k) == f)));   // no half file
            // a source that fails in the middle: that file failed, the others restored
            var src2 = new MemorySource(); Dataset(src2, "/data2"); src2.FailFetch = loc => loc == "Current/1";
            var r2 = new Restore(src2, key, Path.Combine(root, "tmp")); r2.Run(null, Path.Combine(root, "cut"), null, false);
            Assert.Equal("RESTORE_STOP_WITH_ERROR", r2.Result); Assert.Equal(1, r2.Failed); Assert.Equal(2, r2.Restored);
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "tmp")));                                                     // no temporary object left
        }
    }
}
