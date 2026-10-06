using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// What the API does with junk, below the web server: the message reader (Msg.Parse, used by Api.Body for every request)
    /// and the set store's checks of names, objects and run ids (SetStore), alone. Component contract (tests/QA/specs.py AU-07):
    ///   purpose  junk and attacks are refused as the sender's mistake (400), never a server error, never a file elsewhere
    ///   input    bodies of random bytes, cut XML, 5 000 nested elements, an external entity naming a local file, an entity
    ///            expansion bomb; names that climb out (.., absolute, backslash, NUL, too long); an object of random bytes;
    ///            a run id that is a path; a version number and type out of range
    ///   expected every junk body fails as XmlException / FormatException / ArgumentException / InvalidOperationException
    ///            (the kinds Api turns into 400) within 2 s, and the local file's content never appears; every bad name,
    ///            object, run id, version is an ApiException 400 and leaves no file inside or outside the store
    /// </summary>
    public class ApiInputComponentTests : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "objunk-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        public void Dispose() { try { Directory.Delete(root, true); } catch (Exception) { } }

        static bool RefusedAs400(Exception e) { return e is System.Xml.XmlException || e is FormatException || e is ArgumentException || e is InvalidOperationException; }

        [Fact]
        public void JunkBodies_FailAsTheSendersMistake_Quickly_AndNeverPullInALocalFile()
        {
            Directory.CreateDirectory(root);
            var secret = Path.Combine(root, "secret.txt"); File.WriteAllText(secret, "SECRET-7f3a-do-not-leak");
            var rnd = new byte[4096]; new Random(11).NextBytes(rnd);
            var deep = new StringBuilder("<m>"); for (int i = 0; i < 5000; i++) deep.Append("<l n=\"a\"><i>");          // nested and never closed
            var junk = new[]
            {
                rnd,
                Encoding.UTF8.GetBytes("<m><f n=\"login\">adm"),
                Encoding.UTF8.GetBytes(deep.ToString()),
                Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><!DOCTYPE m [<!ENTITY x SYSTEM \"file://" + secret + "\">]><m><f n=\"login\">&x;</f></m>"),
                Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><!DOCTYPE m [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;\"><!ENTITY c \"&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;\"><!ENTITY d \"&c;&c;&c;&c;&c;&c;&c;&c;&c;&c;\"><!ENTITY e \"&d;&d;&d;&d;&d;&d;&d;&d;&d;&d;\"><!ENTITY f \"&e;&e;&e;&e;&e;&e;&e;&e;&e;&e;\"><!ENTITY g \"&f;&f;&f;&f;&f;&f;&f;&f;&f;&f;\">]><m><f n=\"login\">&g;</f></m>"),
                new byte[] { 0xEF, 0xBB, 0xBF, 0x3C, 0x00, 0x6D },
            };
            foreach (var body in junk)
            {
                var sw = Stopwatch.StartNew();
                Exception got = null; Msg m = null;
                try { m = Msg.Parse(body); } catch (Exception e) { got = e; }
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), "took " + sw.Elapsed);
                Assert.True(got != null && RefusedAs400(got), "accepted or a server error: " + (got == null ? "parsed: " + m : got.GetType().Name + " " + got.Message));
                Assert.DoesNotContain("SECRET-7f3a", got.Message);
            }
        }

        [Fact]
        public void BadNames_Objects_RunIds_AreRefused400_AndNothingIsWrittenAnywhere()
        {
            var userDir = Path.Combine(root, "user");
            var st = new SetStore(userDir, "1700000000077");
            var job = st.BeginJob(DateTime.UtcNow);
            var before = Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToHashSet();
            var key = KeySet.Random();
            var ms = new MemoryStream(); var w = new BackupObject.Writer(ms, key); var d = Encoding.UTF8.GetBytes("ok"); w.AddChunk(BackupObject.ChunkId(key, d), d); w.Finish(new Msg().Set("path", "p"));
            var good = ms.ToArray();
            var rnd = new byte[3000]; new Random(4).NextBytes(rnd);
            var good26 = "AAAAAAAAAAAAAAAAAAAAAAAAAA";
            foreach (var rel in new[] { "../../../../escape", "/etc/passwd", "..", good26 + "/../" + good26, good26 + "\\..\\x", good26 + "\0x", "", new string('A', 5000) })
            {
                Assert.Equal(400, Assert.Throws<ApiException>(() => SetStore.CheckRel(rel)).Status);
                Assert.Equal(400, Assert.Throws<ApiException>(() => st.StageObject(job, new ChkRecord { Rel = rel, Seq = 0, Kind = "F" }, new MemoryStream(good), 1L << 30)).Status);
            }
            Assert.Equal(400, Assert.Throws<ApiException>(() => st.StageObject(job, new ChkRecord { Rel = good26, Seq = 0, Kind = "F" }, new MemoryStream(rnd), 1L << 30)).Status);   // not an object
            Assert.Equal(400, Assert.Throws<ApiException>(() => st.StageObject(job, new ChkRecord { Rel = good26, Seq = 1000, Kind = "D" }, new MemoryStream(good), 1L << 30)).Status);
            Assert.Equal(400, Assert.Throws<ApiException>(() => st.StageObject(job, new ChkRecord { Rel = good26, Seq = 0, Kind = "X" }, new MemoryStream(good), 1L << 30)).Status);
            Assert.Equal(400, Assert.Throws<ApiException>(() => st.StageObject("../../x", new ChkRecord { Rel = good26, Seq = 0, Kind = "F" }, new MemoryStream(good), 1L << 30)).Status);
            Assert.Equal(400, Assert.Throws<ApiException>(() => st.StageDelete(job, "../../../etc")).Status);
            var after = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Where(f => !before.Contains(f) && !f.EndsWith("lease") && !f.Contains("index.db")).ToList();
            Assert.Empty(after);
            Assert.False(File.Exists(Path.Combine(Path.GetTempPath(), "escape")));
            Assert.Equal(new SetStore(userDir, "1700000000077").StagedBytes(job), 0);
            Assert.Equal(GoodStillWorks(st, job, good26, good), Sha(good));
        }

        static string Sha(byte[] b) { using (var h = System.Security.Cryptography.SHA256.Create()) return Bytes.Hex(h.ComputeHash(b)); }
        static string GoodStillWorks(SetStore st, string job, string rel, byte[] good) { return st.StageObject(job, new ChkRecord { Rel = rel, Seq = 0, Kind = "F" }, new MemoryStream(good), 1L << 30).Sha256; }
    }
}
