using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace OnlineBackup.Core
{
    /// <summary>
    /// Content-defined chunking (FastCDC with normalized chunking, as in restic / Borg / Kopia). Cut points depend on
    /// the content, so inserting a few bytes in the middle of a 20GB PST moves one chunk, not every chunk after it.
    /// </summary>
    public sealed class Chunker
    {
        static readonly ulong[] Gear = BuildGear();
        readonly int min, avg, max;
        readonly ulong maskHard, maskEasy;

        public int Average { get { return avg; } }

        public Chunker(int averageSize)
        {
            if (averageSize < 64) throw new ArgumentException("Average chunk size too small.");
            avg = averageSize; min = averageSize / 4; max = averageSize * 8;
            int bits = (int)Math.Round(Math.Log(averageSize, 2));
            maskHard = HighMask(bits + 2);   // before the average: harder to cut
            maskEasy = HighMask(bits - 2);   // after the average: easier to cut
        }

        /// <summary>Spec: average 1MB up to 10GB, 4MB above — keeps the chunk list of a 2TB file small.</summary>
        public static Chunker ForFileSize(long size) { return new Chunker(size > 10L * 1024 * 1024 * 1024 ? 4 * 1024 * 1024 : 1024 * 1024); }

        static ulong HighMask(int bits) { return bits <= 0 ? 0UL : bits >= 64 ? ulong.MaxValue : ((1UL << bits) - 1) << (64 - bits); }

        static ulong[] BuildGear()
        {
            // Fixed table so every agent version cuts identically.
            var g = new ulong[256];
            using (var sha = SHA256.Create())
                for (int i = 0; i < 256; i++)
                {
                    var h = sha.ComputeHash(new[] { (byte)i, (byte)0x5A, (byte)0xC3 });
                    g[i] = BitConverter.ToUInt64(h, 0);
                }
            return g;
        }

        /// <summary>Splits a stream into chunks. Memory use is bounded by the maximum chunk size.</summary>
        public IEnumerable<byte[]> Split(Stream s)
        {
            var buf = new byte[max];
            int len = 0;
            bool eof = false;
            while (true)
            {
                while (!eof && len < max)
                {
                    int r = s.Read(buf, len, max - len);
                    if (r <= 0) eof = true; else len += r;
                }
                if (len == 0) yield break;
                int cut = Cut(buf, len, eof);
                var chunk = new byte[cut];
                Buffer.BlockCopy(buf, 0, chunk, 0, cut);
                yield return chunk;
                Buffer.BlockCopy(buf, cut, buf, 0, len - cut);
                len -= cut;
            }
        }

        int Cut(byte[] b, int n, bool eof)
        {
            if (n <= min) return n;
            int normal = Math.Min(avg, n), limit = Math.Min(max, n);
            ulong fp = 0;
            int i = min;
            for (; i < normal; i++)
            {
                fp = (fp << 1) + Gear[b[i]];
                if ((fp & maskHard) == 0) return i + 1;
            }
            for (; i < limit; i++)
            {
                fp = (fp << 1) + Gear[b[i]];
                if ((fp & maskEasy) == 0) return i + 1;
            }
            return limit;
        }
    }
}
