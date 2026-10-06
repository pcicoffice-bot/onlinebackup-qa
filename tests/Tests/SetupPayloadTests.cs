using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// The one-file Setup.exe read by itself — component contract (found by the Windows QA robot, journey W02: a Setup.exe
    /// whose download was cut off said "Unzip the whole package to a folder first", sending the customer the wrong way):
    ///   input    a real program file (PE) alone; the same + the package files + their length + OBSETUP1; that file cut at
    ///            60 %, at 99 %, and just after the program
    ///   expected alone = no package (the files are beside it); whole = the package; cut anywhere after the program =
    ///            damaged (the customer is told to download it again)
    /// </summary>
    public class SetupPayloadTests
    {
        static byte[] Program() { return File.ReadAllBytes(typeof(SetupPayload).Assembly.Location); }
        static byte[] WithPackage(byte[] exe)
        {
            var ms = new MemoryStream(); ms.Write(exe, 0, exe.Length); long start = ms.Position;
            using (var gz = new GZipStream(ms, CompressionLevel.Optimal, true))
            using (var w = new BinaryWriter(gz, Encoding.UTF8, true))
            {
                var data = new byte[300000]; new Random(4).NextBytes(data);
                w.Write(1); var nb = Encoding.UTF8.GetBytes("OnlineBackup.Agent.exe"); w.Write(nb.Length); w.Write(nb); w.Write((long)data.Length); w.Write(data);
            }
            long len = ms.Position - start;
            var t = new BinaryWriter(ms, Encoding.ASCII, true); t.Write(len); t.Write(Encoding.ASCII.GetBytes("OBSETUP1")); t.Flush();
            return ms.ToArray();
        }
        static SetupPayload.State Of(byte[] b) { using (var s = new MemoryStream(b)) return SetupPayload.Inspect(s); }
        static byte[] Cut(byte[] b, int n) { var r = new byte[n]; Array.Copy(b, r, n); return r; }

        [Fact]
        public void AloneWholeAndCut()
        {
            var exe = Program(); var full = WithPackage(exe);
            Assert.Equal(SetupPayload.State.None, Of(exe));
            Assert.Equal(SetupPayload.State.Complete, Of(full));
            Assert.Equal(SetupPayload.State.Cut, Of(Cut(full, (int)(full.Length * 0.6))));
            Assert.Equal(SetupPayload.State.Cut, Of(Cut(full, full.Length - 1)));
            Assert.Equal(SetupPayload.State.Cut, Of(Cut(full, exe.Length + 10)));
        }

        [Fact]
        public void NotAProgram_IsDamaged_NeverAnException()
        {
            Assert.Equal(SetupPayload.State.Cut, Of(new byte[] { 1, 2, 3 }));
            Assert.Equal(SetupPayload.State.Cut, Of(Cut(Program(), 300)));   // the program itself cut
        }
    }
}
