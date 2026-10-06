using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// The shared formats alone — component contract (tests/QA/specs.py CO-01):
    ///   purpose  what is written is read back exactly; a state file is never seen half-written
    ///   input    a message with markup characters, quotes, line breaks, Hebrew, an emoji, empty and missing values, nested
    ///            lists; a 1 MB state file rewritten 40 times while another thread reads it; two writers at once
    ///   expected the message reads back identical; every read sees the whole old or the whole new file, never a mix or a
    ///            short file; two writers at once never fail and leave one whole version; no temporary file is left
    /// </summary>
    public class CoreFormatsTests
    {
        [Fact]
        public void AMessage_ReadsBackExactly()
        {
            var tricky = new[] { "a & b < c > d \"q\" 'a'", "line1\nline2\r\nline3", "שלום עולם", "emoji 😀", "", "  spaces  ", "]]>", "\t" };
            var m = new Msg();
            for (int i = 0; i < tricky.Length; i++) m.Set("k" + i, tricky[i]);
            m.Set("missing", null);
            var child = new Msg().Set("name", "C:\\Data\\חשבוניות [7].pdf"); child.Add("inner", new Msg().Set("x", "1")); child.Add("inner", new Msg().Set("x", "2"));
            m.Add("list", child); m.Add("list", new Msg().Set("name", "second"));
            var back = Msg.Parse(m.ToBytes());
            for (int i = 0; i < tricky.Length; i++) Assert.Equal(tricky[i].Replace("\r\n", "\n"), (back["k" + i] ?? "").Replace("\r\n", "\n"));
            Assert.True(string.IsNullOrEmpty(back["missing"]));
            Assert.Equal(2, back.List("list").Count);
            Assert.Equal("C:\\Data\\חשבוניות [7].pdf", back.List("list")[0]["name"]);
            Assert.Equal(new[] { "1", "2" }, back.List("list")[0].List("inner").Select(x => x["x"]).ToArray());
            Assert.Equal(back.ToBytes(), Msg.Parse(back.ToBytes()).ToBytes());
        }

        [Fact]
        public void AStateFile_IsNeverSeenHalfWritten_AndTwoWritersNeverFail()
        {
            var dir = Path.Combine(Path.GetTempPath(), "obatomic-" + Guid.NewGuid().ToString("N").Substring(0, 8), "deep", "er");
            var path = Path.Combine(dir, "state.txt");
            var a = new string('A', 1 << 20); var b = new string('B', 1 << 20);
            Atomic.WriteText(path, a);
            var stop = false; string bad = null; int reads = 0;
            var reader = new Thread(() =>
            {
                while (!Volatile.Read(ref stop))
                {
                    string s;
                    try { s = File.ReadAllText(path); } catch (IOException) { continue; } catch (UnauthorizedAccessException) { continue; }
                    reads++;
                    if (!(s == a || s == b)) { bad = "length " + s.Length + ", starts " + (s.Length > 0 ? s[0].ToString() : "-") + ", ends " + (s.Length > 0 ? s[s.Length - 1].ToString() : "-"); break; }
                }
            });
            reader.Start();
            for (int i = 0; i < 40; i++) Atomic.WriteText(path, i % 2 == 0 ? b : a);
            Volatile.Write(ref stop, true); reader.Join();
            Assert.True(bad == null, "a half-written state file was seen: " + bad);
            Assert.True(reads > 0);

            Exception failure = null;
            var writers = new[] { a, b }.Select(text => new Thread(() => { try { for (int i = 0; i < 30; i++) Atomic.WriteText(path, text); } catch (Exception e) { failure = e; } })).ToList();
            writers.ForEach(t => t.Start()); writers.ForEach(t => t.Join());
            Assert.Null(failure);
            var end = File.ReadAllText(path);
            Assert.True(end == a || end == b);
            Assert.Empty(Directory.GetFiles(dir).Where(f => f != path));                       // no temporary file left
            Atomic.WriteText(path, ""); Assert.Equal("", File.ReadAllText(path));              // boundary: empty
            Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(dir)), true);
        }

        [Fact]
        public void ATemporaryFile_IsKnownByItsName_NeverByItsPath()   // bug 35
        {
            Assert.True(Atomic.IsTemp(Path.Combine("users", "plain", "x.chk.tmp0123abcd")));
            Assert.False(Atomic.IsTemp(Path.Combine("users", "acme.tmp", "objects", "R1")));      // a customer called acme.tmp
            Assert.False(Atomic.IsTemp(Path.Combine("D:", "Backup.tmp", "users", "u", "x.chk")));  // a storage root with .tmp
            Assert.False(Atomic.IsTemp("report.tmpl")); Assert.False(Atomic.IsTemp("a.tmp")); Assert.False(Atomic.IsTemp("a.tmp0123ABCD"));
            // and what Atomic itself leaves behind is recognised: the name it would use
            var dir = Path.Combine(Path.GetTempPath(), "obtmp-" + Guid.NewGuid().ToString("N").Substring(0, 8)); Directory.CreateDirectory(dir);
            try { Atomic.WriteText(Path.Combine(dir, "a.txt"), "x"); Assert.Empty(Directory.GetFiles(dir).Where(Atomic.IsTemp)); }
            finally { Directory.Delete(dir, true); }
        }
    }
}
