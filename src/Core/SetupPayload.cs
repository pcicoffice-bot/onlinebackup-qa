using System;
using System.IO;
using System.Text;

// Compiled into Setup.exe too (it reads itself before OnlineBackup.Core.dll is unpacked), in its own namespace there
#if SETUP_EXE
namespace OnlineBackup.Setup
#else
namespace OnlineBackup.Core
#endif
{
    /// <summary>
    /// The one-file Setup.exe (SETUP-C60): the program, then the package's files (gzip), their length and the mark
    /// OBSETUP1 at the very end. WIN-QA W02 (the Windows robot): a download cut off loses the mark, and Setup.exe then took
    /// itself for the bare program of an unzipped package and said "Unzip the whole package" — the customer was sent the
    /// wrong way. Anything after the end of the program itself is the package: without the mark it is a cut-off file.
    /// </summary>
    public static class SetupPayload
    {
        public const string Mark = "OBSETUP1";
        public enum State { None, Complete, Cut }

        public static State Inspect(Stream f)
        {
            try
            {
                long end = ProgramEnd(f);
                if (end < 0 || end > f.Length) return State.Cut;      // not a whole program
                if (f.Length >= 16)
                {
                    f.Seek(-16, SeekOrigin.End);
                    var tail = new byte[16]; Read(f, tail);
                    if (Encoding.ASCII.GetString(tail, 8, 8) == Mark)
                    {
                        var len = BitConverter.ToInt64(tail, 0);
                        return len > 0 && len == f.Length - 16 - end ? State.Complete : State.Cut;
                    }
                }
                return f.Length == end ? State.None : State.Cut;
            }
            catch (Exception) { return State.Cut; }
        }

        /// <summary>Where the program's own bytes end: the furthest section, or the signature after them (Authenticode).</summary>
        static long ProgramEnd(Stream f)
        {
            var b = new byte[64];
            if (f.Length < 64) return -1;
            f.Seek(0, SeekOrigin.Begin); Read(f, b);
            if (b[0] != 'M' || b[1] != 'Z') return -1;
            long pe = BitConverter.ToInt32(b, 0x3C);
            if (pe <= 0 || pe + 24 > f.Length) return -1;
            var h = new byte[24]; f.Seek(pe, SeekOrigin.Begin); Read(f, h);
            if (h[0] != 'P' || h[1] != 'E' || h[2] != 0 || h[3] != 0) return -1;
            int sections = BitConverter.ToUInt16(h, 6), optSize = BitConverter.ToUInt16(h, 20);
            var opt = new byte[optSize]; Read(f, opt);
            long end = 0;
            var s = new byte[40];
            for (int i = 0; i < sections; i++)
            {
                Read(f, s);
                long raw = BitConverter.ToUInt32(s, 16), at = BitConverter.ToUInt32(s, 20);
                if (raw > 0) end = Math.Max(end, at + raw);
            }
            // the security directory (index 4) holds a file offset and size: a signed program ends after its signature
            int dirs = optSize >= 2 && BitConverter.ToUInt16(opt, 0) == 0x20b ? 112 : 96;
            if (optSize >= dirs + 5 * 8)
            {
                long at = BitConverter.ToUInt32(opt, dirs + 4 * 8), size = BitConverter.ToUInt32(opt, dirs + 4 * 8 + 4);
                if (at > 0 && size > 0) end = Math.Max(end, at + size);
            }
            return end;
        }

        static void Read(Stream s, byte[] b) { int o = 0; while (o < b.Length) { var n = s.Read(b, o, b.Length - o); if (n <= 0) throw new EndOfStreamException(); o += n; } }
    }
}
