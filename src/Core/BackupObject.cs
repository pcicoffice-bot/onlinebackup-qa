using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace OnlineBackup.Core
{
    /// <summary>
    /// One stored version of one file on the server ("full" = .000, "delta" = .001 … .099 beside it).
    /// Layout: "OBK1" · chunk records [int32 length][encrypted(flag + deflated-or-raw chunk)] … ·
    ///         encrypted header (path, size, times, attributes, recipe, stored chunk list) · int64 header offset · "OBKE".
    /// The header is written last so a 2TB file streams straight to the server without a temporary copy.
    /// Everything except the framing is encrypted; the server only sees sizes and a SHA-256 of the whole object.
    /// </summary>
    public static class BackupObject
    {
        static readonly byte[] Head = Encoding.ASCII.GetBytes("OBK1");
        static readonly byte[] Tail = Encoding.ASCII.GetBytes("OBKE");

        /// <summary>Chunk identity: keyed, so equal chunks are found without revealing content.</summary>
        public static string ChunkId(KeySet k, byte[] chunk) { return Bytes.Hex(Bytes.Hmac(k.MacKey, chunk), 16); }

        public sealed class Writer
        {
            readonly Stream output;
            readonly KeySet key;
            readonly HashAlgorithm sha = SHA256.Create();
            readonly List<string> stored = new List<string>();
            public long Length { get; private set; }
            public string Sha256 { get; private set; }
            public int StoredChunks { get { return stored.Count; } }

            /// <summary>COMP-010: MAX (default, the smallest), FAST, or NONE (stored as is).</summary>
            public string Compression = "MAX";

            public Writer(Stream output, KeySet key)
            {
                this.output = output; this.key = key;
                Write(Head, 0, Head.Length);
            }

            void Write(byte[] b, int off, int n)
            {
                output.Write(b, off, n);
                sha.TransformBlock(b, off, n, null, 0);
                Length += n;
            }

            public void AddChunk(string id, byte[] chunk)
            {
                byte[] payload = Compress(chunk, Compression);
                var enc = Cipher.Encrypt(key, payload);
                Write(BitConverter.GetBytes(enc.Length), 0, 4);
                Write(enc, 0, enc.Length);
                stored.Add(id);
            }

            /// <summary>Header fields: path, size, mtime, attrs, acl, kind; list "recipe" (h, n) = every chunk of the file in order.</summary>
            public void Finish(Msg header)
            {
                var l = header.List("stored");
                l.Clear();
                foreach (var id in stored) l.Add(new Msg().Set("h", id));
                long headerOffset = Length;
                var enc = Cipher.Encrypt(key, header.ToBytes());
                Write(BitConverter.GetBytes(enc.Length), 0, 4);
                Write(enc, 0, enc.Length);
                Write(BitConverter.GetBytes(headerOffset), 0, 8);
                Write(Tail, 0, Tail.Length);
                sha.TransformFinalBlock(new byte[0], 0, 0);
                Sha256 = Bytes.Hex(sha.Hash);
                output.Flush();
            }
        }

        static byte[] Compress(byte[] chunk, string level = "MAX")
        {
            var ms = new MemoryStream();
            if (level != "NONE")
            {
                ms.WriteByte(1);
#if NET40
                using (var z = new DeflateStream(ms, CompressionMode.Compress, true)) z.Write(chunk, 0, chunk.Length);
#else
                using (var z = new DeflateStream(ms, level == "FAST" ? CompressionLevel.Fastest : CompressionLevel.SmallestSize, true)) z.Write(chunk, 0, chunk.Length);
#endif
                if (ms.Length < chunk.Length + 1) return ms.ToArray();
            }
            var raw = new byte[chunk.Length + 1];          // incompressible (jpg, zip): stored as is
            Buffer.BlockCopy(chunk, 0, raw, 1, chunk.Length);
            return raw;
        }

        static byte[] Decompress(byte[] payload)
        {
            if (payload[0] == 0)
            {
                var raw = new byte[payload.Length - 1];
                Buffer.BlockCopy(payload, 1, raw, 0, raw.Length);
                return raw;
            }
            using (var z = new DeflateStream(new MemoryStream(payload, 1, payload.Length - 1), CompressionMode.Decompress))
            {
                var outp = new MemoryStream();
                z.CopyTo(outp);
                return outp.ToArray();
            }
        }

        /// <summary>Reads the header of a stored object (needs a seekable stream: a downloaded file).</summary>
        public static Msg ReadHeader(Stream s, KeySet k)
        {
            if (s.Length < 20) throw new InvalidDataException("Object too short.");
            var tail = new byte[12];
            s.Seek(-12, SeekOrigin.End);
            ReadExact(s, tail, 12);
            if (Encoding.ASCII.GetString(tail, 8, 4) != "OBKE") throw new InvalidDataException("Object end marker missing.");
            long off = BitConverter.ToInt64(tail, 0);
            s.Seek(off, SeekOrigin.Begin);
            var lenb = new byte[4];
            ReadExact(s, lenb, 4);
            int len = BitConverter.ToInt32(lenb, 0);
            if (len <= 0 || off + 4 + len + 12 != s.Length) throw new InvalidDataException("Object header damaged.");
            var enc = new byte[len];
            ReadExact(s, enc, len);
            return Msg.Parse(Cipher.Decrypt(k, enc));
        }

        /// <summary>All chunks stored in this object, by id (decrypted and verified).</summary>
        public static IEnumerable<KeyValuePair<string, byte[]>> ReadChunks(Stream s, KeySet k, Msg header)
        {
            s.Seek(0, SeekOrigin.Begin);
            var head = new byte[4];
            ReadExact(s, head, 4);
            if (Encoding.ASCII.GetString(head) != "OBK1") throw new InvalidDataException("Not a backup object.");
            var lenb = new byte[4];
            foreach (var item in header.List("stored"))
            {
                ReadExact(s, lenb, 4);
                int len = BitConverter.ToInt32(lenb, 0);
                if (len <= 0 || len > 64 * 1024 * 1024) throw new InvalidDataException("Chunk record damaged.");
                var enc = new byte[len];
                ReadExact(s, enc, len);
                var chunk = Decompress(Cipher.Decrypt(k, enc));
                var id = item["h"];
                if (ChunkId(k, chunk) != id) throw new InvalidDataException("Chunk content does not match its id.");
                yield return new KeyValuePair<string, byte[]>(id, chunk);
            }
        }

        /// <summary>Where each stored chunk starts in the object (for random access while rebuilding a file from a chain).</summary>
        public static Dictionary<string, long> ChunkOffsets(Stream s, Msg header)
        {
            var d = new Dictionary<string, long>();
            long pos = 4;
            var lenb = new byte[4];
            foreach (var item in header.List("stored"))
            {
                s.Seek(pos, SeekOrigin.Begin);
                ReadExact(s, lenb, 4);
                int len = BitConverter.ToInt32(lenb, 0);
                if (len <= 0 || len > 64 * 1024 * 1024) throw new InvalidDataException("Chunk record damaged.");
                if (!d.ContainsKey(item["h"])) d[item["h"]] = pos;
                pos += 4 + len;
            }
            return d;
        }

        public static byte[] ReadChunkAt(Stream s, long offset, KeySet k, string expectedId)
        {
            s.Seek(offset, SeekOrigin.Begin);
            var lenb = new byte[4];
            ReadExact(s, lenb, 4);
            int len = BitConverter.ToInt32(lenb, 0);
            if (len <= 0 || len > 64 * 1024 * 1024) throw new InvalidDataException("Chunk record damaged.");
            var enc = new byte[len];
            ReadExact(s, enc, len);
            var chunk = Decompress(Cipher.Decrypt(k, enc));
            if (ChunkId(k, chunk) != expectedId) throw new InvalidDataException("Chunk content does not match its id.");
            return chunk;
        }

        static void ReadExact(Stream s, byte[] b, int n)
        {
            int got = 0;
            while (got < n)
            {
                int r = s.Read(b, got, n - got);
                if (r <= 0) throw new EndOfStreamException("Unexpected end of object.");
                got += r;
            }
        }
    }
}
