using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using OnlineBackup.Core;

namespace OnlineBackup.Server
{
    /// <summary>UPD-020: the Windows client files this server carries (server\client), with their SHA-256 — what the computers update to.</summary>
    public static class ClientFiles
    {
        static readonly object gate = new object();
        static readonly Dictionary<string, Tuple<DateTime, long, string>> cache = new Dictionary<string, Tuple<DateTime, long, string>>(StringComparer.OrdinalIgnoreCase);
        static readonly string[] Kinds = { ".exe", ".dll", ".config" };

        static IEnumerable<string> Files()
        {
            var dir = ClientPackage.ClientDir;
            if (!Directory.Exists(dir)) return new string[0];
            // the program files and version.txt — never connection.xml / branding.xml (each computer keeps its own)
            return Directory.GetFiles(dir).Where(f => !System.IO.Path.GetFileName(f).StartsWith("Setup.exe", StringComparison.OrdinalIgnoreCase)).Where(f => Kinds.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant()) || System.IO.Path.GetFileName(f).Equals("version.txt", StringComparison.OrdinalIgnoreCase));
        }

        static string Sha(string f)
        {
            var fi = new FileInfo(f);
            lock (gate)
            {
                Tuple<DateTime, long, string> c;
                if (cache.TryGetValue(f, out c) && c.Item1 == fi.LastWriteTimeUtc && c.Item2 == fi.Length) return c.Item3;
                string h; using (var s = File.OpenRead(f)) using (var x = SHA256.Create()) h = Bytes.Hex(x.ComputeHash(s));
                cache[f] = Tuple.Create(fi.LastWriteTimeUtc, fi.Length, h);
                return h;
            }
        }

        public static Msg List()
        {
            var m = new Msg().Set("version", Updater.CurrentVersion);
            var v = System.IO.Path.Combine(ClientPackage.ClientDir, "version.txt");
            if (File.Exists(v)) m.Set("version", OnlineBackup.Core.Atomic.ReadAllText(v).Trim());
            foreach (var f in Files()) m.Add("files", new Msg().Set("name", System.IO.Path.GetFileName(f)).Set("size", new FileInfo(f).Length).Set("sha256", Sha(f)));
            return m;
        }

        /// <summary>A file of the list only (a plain name, no folders).</summary>
        public static string Path(string name)
        {
            if (string.IsNullOrEmpty(name) || name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || name.Contains("..")) return null;
            return Files().FirstOrDefault(f => System.IO.Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase));
        }
    }
}
