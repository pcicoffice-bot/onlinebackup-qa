using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>Bug 93b: on Windows an Atomic write (replace by rename) is refused while any open handle does not allow deletion,
    /// and the product read its own files with File.ReadAll* (no FileShare.Delete) - so the client window reading a file the
    /// service was writing could make the write fail. Every product reader now uses Atomic.ReadAll*, which must read exactly
    /// like File.ReadAll* and let the file be replaced. The Windows proof is the CI gate's Windows job; on Linux the first test
    /// proves the reads are unchanged.</summary>
    [Collection("Atomic-attempts")]
    public class AtomicSharedReadComponentTests
    {
        static string NewDir() { var d = Path.Combine(Path.GetTempPath(), "obshared-" + Guid.NewGuid().ToString("N").Substring(0, 8)); Directory.CreateDirectory(d); return d; }

        [Fact]
        public void TheSharedReads_GiveExactlyWhatFileReadAllGives()
        {
            var dir = NewDir();
            var cases = new[]
            {
                Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("bom\r\nline two\n\nלקוח\n")).ToArray(),
                Encoding.UTF8.GetBytes("no bom\nlast line without newline"),
                Encoding.UTF8.GetBytes("\n\n\r\n"),
                new byte[0],
                Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("utf-16 with bom\r\nx")).ToArray(),
                Enumerable.Range(0, 70000).Select(i => (byte)(i * 7)).ToArray(),
            };
            for (int i = 0; i < cases.Length; i++)
            {
                var f = Path.Combine(dir, "case" + i);
                File.WriteAllBytes(f, cases[i]);
                Assert.Equal(File.ReadAllBytes(f), Atomic.ReadAllBytes(f));
                if (i == 5) continue;   // binary: bytes only
                Assert.Equal(File.ReadAllText(f), Atomic.ReadAllText(f));
                Assert.Equal(File.ReadAllLines(f), Atomic.ReadAllLines(f));
                Assert.Equal(File.ReadAllText(f, Encoding.UTF8), Atomic.ReadAllText(f, Encoding.UTF8));
            }
            Assert.Throws<FileNotFoundException>(() => Atomic.ReadAllText(Path.Combine(dir, "missing")));   // the same error as File
            Directory.Delete(dir, true);
        }

        [Fact]
        public void WhileTheProductsOwnReaderReadsWithoutPause_EveryWriteSucceeds_AndNoHalfFileIsSeen()
        {
            var dir = NewDir(); var path = Path.Combine(dir, "state.txt");
            var a = new string('A', 1 << 20); var b = new string('B', 1 << 20);
            Atomic.WriteText(path, a);
            var stop = false; string bad = null; int reads = 0;
            var reader = new Thread(() =>
            {
                while (!Volatile.Read(ref stop))
                {
                    string s;
                    try { s = Atomic.ReadAllText(path); } catch (IOException) { continue; } catch (UnauthorizedAccessException) { continue; }
                    reads++;
                    if (!(s == a || s == b)) { bad = "length " + s.Length; break; }
                }
            });
            reader.Start();
            Exception failure = null;
            var writers = new[] { a, b }.Select(text => new Thread(() => { try { for (int i = 0; i < 30; i++) Atomic.WriteText(path, text); } catch (Exception e) { failure = e; } })).ToList();
            writers.ForEach(t => t.Start()); writers.ForEach(t => t.Join());
            Volatile.Write(ref stop, true); reader.Join();
            Assert.Null(failure);
            Assert.True(bad == null, "a half-written file was seen: " + bad);
            Assert.True(reads > 0);
            // the same check, with every name printed (gate 37635681591 on Windows: a second file was left, its name cut off)
            var left = Directory.GetFiles(dir);
            Assert.True(left.Length == 1 && left[0] == path, "files left in the folder: " + string.Join(", ", left.Select(f => Path.GetFileName(f) + " (" + new FileInfo(f).Length + " bytes)")));
            Directory.Delete(dir, true);
        }

        [Fact]
        public void AnotherProgramThatHoldsTheFileForAMoment_DelaysTheWrite_DoesNotFailIt()
        {
            var dir = NewDir(); var path = Path.Combine(dir, "state.txt");
            Atomic.WriteText(path, "v0");
            var stop = false;
            var other = new Thread(() =>   // an antivirus / indexer: opens without delete sharing for 150 ms, every 300 ms
            {
                while (!Volatile.Read(ref stop))
                {
                    try { using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) Thread.Sleep(150); } catch (IOException) { }
                    Thread.Sleep(150);
                }
            });
            other.Start();
            try { for (int i = 1; i <= 20; i++) Atomic.WriteText(path, "v" + i); }
            finally { Volatile.Write(ref stop, true); other.Join(); }
            Assert.Equal("v20", Atomic.ReadAllText(path));
            Directory.Delete(dir, true);
        }
    }
}
