using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Forms;

namespace OnlineBackup.Setup
{
    /// <summary>
    /// Setup.exe: the client software's installation program. SETUP-C60 (owner: "one file you click — it unpacks to a
    /// temporary folder and opens the installation"): the server appends the package's files to this program; it unpacks
    /// them to a new folder under %TEMP%, runs the installation from there and removes the folder at the end. Without
    /// appended files (the ZIP package) the files are beside it.
    /// </summary>
    static class Program
    {
        static string dir;

        [STAThread]
        static int Main()
        {
            dir = AppDomain.CurrentDomain.BaseDirectory;
            string temp = null;
            try
            {
                temp = Unpack();
                if (temp != null) dir = temp;
            }
            catch (Exception e)
            {
                MessageBox.Show("The installation file is damaged (" + e.Message + "). Download it again.", "Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
            if (!File.Exists(Path.Combine(dir, "connection.xml")) || !File.Exists(Path.Combine(dir, "OnlineBackup.Agent.exe")))
            {
                MessageBox.Show("Unzip the whole package to a folder first, then run Setup.exe from there.", "Setup", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 1;
            }
            // the agent's types come from the unpacked folder
            AppDomain.CurrentDomain.AssemblyResolve += (s, a) =>
            {
                var n = new AssemblyName(a.Name).Name;
                foreach (var ext in new[] { ".exe", ".dll" })
                {
                    var p = Path.Combine(dir, n + ext);
                    if (File.Exists(p)) return Assembly.LoadFrom(p);
                }
                return null;
            };
            try { return Start(dir); }
            finally
            {
                if (temp != null)
                    try { Directory.Delete(temp, true); }
                    catch (Exception)
                    {
                        // a file still in use: removed a moment after this program ends
                        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c ping -n 4 127.0.0.1 >nul & rd /s /q \"" + temp + "\"") { CreateNoWindow = true, UseShellExecute = false }); } catch (Exception) { }
                    }
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static int Start(string packageDir) { return OnlineBackup.Agent.SetupForm.Run(packageDir); }

        /// <summary>The files after the program (the end: their length, then OBSETUP1) → a new folder under %TEMP%; null when there are none; an exception when the file is cut off.</summary>
        static string Unpack()
        {
            var self = Assembly.GetExecutingAssembly().Location;
            using (var f = File.OpenRead(self))
            {
                // WIN-QA W02: a download cut off has no mark at the end — it is damaged, not "the program of an unzipped package"
                var state = SetupPayload.Inspect(f);
                if (state == SetupPayload.State.None) return null;
                if (state == SetupPayload.State.Cut) throw new InvalidDataException("the file is incomplete");
                f.Seek(-16, SeekOrigin.End);
                var tail = new byte[16]; Read(f, tail);
                var len = BitConverter.ToInt64(tail, 0);
                if (len <= 0 || len > f.Length - 16) throw new InvalidDataException("length");
                f.Seek(f.Length - 16 - len, SeekOrigin.Begin);
                var temp = Path.Combine(Path.GetTempPath(), "OnlineBackup-Setup-" + Guid.NewGuid().ToString("N").Substring(0, 12));
                Directory.CreateDirectory(temp);
                using (var gz = new GZipStream(new Bounded(f, len), CompressionMode.Decompress))
                using (var hs = new Hashing(gz))
                using (var br = new BinaryReader(hs, Encoding.UTF8))
                {
                    var count = br.ReadInt32();
                    if (count < 1 || count > 500) throw new InvalidDataException("count");
                    for (int i = 0; i < count; i++)
                    {
                        var nl = br.ReadInt32(); if (nl < 1 || nl > 200) throw new InvalidDataException("name");
                        var name = Encoding.UTF8.GetString(br.ReadBytes(nl));
                        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name == "." || name == "..") throw new InvalidDataException("name");   // plain names only
                        var size = br.ReadInt64(); if (size < 0 || size > 1L << 30) throw new InvalidDataException("size");
                        using (var o = File.Create(Path.Combine(temp, name)))
                        {
                            var buf = new byte[81920];
                            while (size > 0) { var n = br.Read(buf, 0, (int)Math.Min(buf.Length, size)); if (n <= 0) throw new EndOfStreamException(); o.Write(buf, 0, n); size -= n; }
                        }
                    }
                    // bug 109: the SHA-256 of every byte above follows them - a damaged payload is never installed as a shorter file
                    var sum = hs.Finish(); var stored = new byte[32]; Read(gz, stored);
                    for (int i = 0; i < 32; i++)
                        if (stored[i] != sum[i]) { try { Directory.Delete(temp, true); } catch (Exception) { } throw new InvalidDataException("the setup file is damaged (its check sum does not match)"); }
                }
                return temp;
            }
        }

        static void Read(Stream s, byte[] b) { int o = 0; while (o < b.Length) { var n = s.Read(b, o, b.Length - o); if (n <= 0) throw new EndOfStreamException(); o += n; } }

        /// <summary>Bug 109: a read-through stream that hashes what was read (SHA-256), until Finish.</summary>
        sealed class Hashing : Stream
        {
            readonly Stream s; readonly System.Security.Cryptography.SHA256 h = new System.Security.Cryptography.SHA256Managed();
            public Hashing(Stream s) { this.s = s; }
            public byte[] Finish() { h.TransformFinalBlock(new byte[0], 0, 0); return h.Hash; }
            public override int Read(byte[] buffer, int offset, int count) { var n = s.Read(buffer, offset, count); if (n > 0) h.TransformBlock(buffer, offset, n, null, 0); return n; }
            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
            protected override void Dispose(bool disposing) { if (disposing) h.Dispose(); base.Dispose(disposing); }
        }

        /// <summary>A window on the file: only the appended part.</summary>
        sealed class Bounded : Stream
        {
            readonly Stream s; long left;
            public Bounded(Stream s, long len) { this.s = s; left = len; }
            public override int Read(byte[] buffer, int offset, int count) { if (left <= 0) return 0; var n = s.Read(buffer, offset, (int)Math.Min(count, left)); left -= n; return n; }
            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        }
    }
}
