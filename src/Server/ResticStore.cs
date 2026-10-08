using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using OnlineBackup.Core;

namespace OnlineBackup.Server
{
    /// <summary>
    /// One restic repository per backup set (restic: BSD-2 licence, the engine of the "RESTIC" sets), served with restic's
    /// own REST protocol (the same as restic's rest-server, API v1 + v2) at /restic/&lt;login&gt;/&lt;set&gt;/.
    /// Encryption stays on the customer's computer (restic: AES-256-CTR + Poly1305, scrypt key); the server never has the
    /// key, so it protects the data by its own rules:
    ///   • every stored file is named by the SHA-256 of its content — checked on upload (a damaged upload is refused) and
    ///     again by the server's own scrub, without any key;
    ///   • append-only: an existing file is never overwritten; a delete (restic prune / forget) moves the file to a trash
    ///     kept TRASH_DAYS (14) — ransomware on the customer's computer cannot destroy backups; frozen retention keeps it longer;
    ///   • the user's quota is enforced on every upload.
    /// Layout (as rest-server): &lt;user&gt;\restic\&lt;set&gt;\{config, keys\, locks\, snapshots\, index\, data\xx\} + .trash\.
    /// </summary>
    public sealed class ResticStore
    {
        public static readonly string[] Types = { "data", "keys", "locks", "snapshots", "index" };
        public const int TrashDays = 14;
        public string Dir { get; private set; }

        public ResticStore(string userDir, string setId) { Dir = Path.Combine(userDir, "restic", setId); }

        public bool Exists { get { return File.Exists(Path.Combine(Dir, "config")); } }

        public void Create()
        {
            foreach (var t in Types) Directory.CreateDirectory(Path.Combine(Dir, t));
            for (int i = 0; i < 256; i++) Directory.CreateDirectory(Path.Combine(Dir, "data", i.ToString("x2", CultureInfo.InvariantCulture)));
        }

        static bool ValidName(string type, string name)
        {
            if (type == "config") return name == null;
            if (Array.IndexOf(Types, type) < 0 || name == null) return false;
            return name.Length == 64 && name.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
        }

        public string FilePath(string type, string name)
        {
            if (!ValidName(type, name)) throw new ApiException(400, "NAME", "invalid name");
            if (type == "config") return Path.Combine(Dir, "config");
            return type == "data" ? Path.Combine(Dir, "data", name.Substring(0, 2), name) : Path.Combine(Dir, type, name);
        }

        /// <summary>v1: ["name", …]; v2: [{"name":…, "size":…}, …].</summary>
        public string List(string type, bool v2)
        {
            if (Array.IndexOf(Types, type) < 0) throw new ApiException(400, "TYPE", "invalid type");
            var root = Path.Combine(Dir, type);
            var files = Directory.Exists(root) ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".tmp", StringComparison.Ordinal)) : Enumerable.Empty<string>();
            var sb = new StringBuilder("[");
            bool first = true;
            foreach (var f in files)
            {
                if (!first) sb.Append(',');
                first = false;
                var n = Path.GetFileName(f);
                if (v2) sb.Append("{\"name\":\"").Append(n).Append("\",\"size\":").Append(new FileInfo(f).Length.ToString(CultureInfo.InvariantCulture)).Append('}');
                else sb.Append('"').Append(n).Append('"');
            }
            return sb.Append(']').ToString();
        }

        /// <summary>
        /// Stores one file: streamed to a temporary name with its SHA-256 computed on the way, refused if the hash is not
        /// its name (except config), flushed to disk, then renamed. An existing file is never replaced.
        /// Returns the bytes written (0 = already there, identical by name).
        /// </summary>
        public long Save(string type, string name, Stream body, long maxBytes)
        {
            var path = FilePath(type, name);
            if (File.Exists(path))
            {
                if (type == "config") throw new ApiException(403, "APPEND_ONLY", "config exists");
                Drain(body);
                return 0;   // same name = same content (SHA-256); restic retries uploads
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            long n = 0;
            try
            {
                using (var sha = SHA256.Create())
                using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                {
                    var buf = new byte[1 << 16];
                    int r;
                    while ((r = body.Read(buf, 0, buf.Length)) > 0)
                    {
                        n += r;
                        if (maxBytes >= 0 && n > maxBytes) throw new ApiException(507, "QUOTA", "The quota is full: new backups are stopped, existing backups are kept.");
                        sha.TransformBlock(buf, 0, r, null, 0);
                        fs.Write(buf, 0, r);
                    }
                    sha.TransformFinalBlock(buf, 0, 0);
                    if (type != "config" && Bytes.Hex(sha.Hash) != name) throw new ApiException(400, "HASH", "content does not match its name (damaged upload)");
                    fs.Flush(true);
                }
                File.Move(tmp, path);
                return n;
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }

        static void Drain(Stream s) { var b = new byte[1 << 16]; while (s.Read(b, 0, b.Length) > 0) { } }

        /// <summary>Locks are deleted at once (restic's own bookkeeping); everything else goes to the trash.</summary>
        public void Delete(string type, string name)
        {
            if (type == "config") throw new ApiException(403, "APPEND_ONLY", "the repository configuration is never deleted");
            var path = FilePath(type, name);
            if (!File.Exists(path)) throw new ApiException(404, "NOT_FOUND", "not found");
            if (type == "locks") { File.Delete(path); return; }
            var t = Path.Combine(Dir, ".trash", type);
            Directory.CreateDirectory(t);
            var dst = Path.Combine(t, name + "." + RunId.UnixMs(SystemClock.UtcNow).ToString(CultureInfo.InvariantCulture));
            File.Move(path, dst);
            File.SetLastWriteTimeUtc(dst, SystemClock.UtcNow);   // the trash clock starts at the delete
        }

        /// <summary>Removes trash older than the delay (none while retention is frozen). Returns the bytes freed.</summary>
        public long PurgeTrash(DateTime nowUtc, int days)
        {
            var t = Path.Combine(Dir, ".trash");
            if (!Directory.Exists(t)) return 0;
            long freed = 0;
            foreach (var f in Directory.EnumerateFiles(t, "*", SearchOption.AllDirectories).ToList())
            {
                var fi = new FileInfo(f);
                if ((nowUtc - fi.LastWriteTimeUtc).TotalDays < days) continue;
                freed += fi.Length; fi.Delete();
            }
            return freed;
        }

        /// <summary>Puts every file of the trash back (undo of a malicious prune). Returns the files restored.</summary>
        public int RestoreTrash()
        {
            var t = Path.Combine(Dir, ".trash");
            if (!Directory.Exists(t)) return 0;
            int n = 0;
            foreach (var type in Types)
            {
                var d = Path.Combine(t, type);
                if (!Directory.Exists(d)) continue;
                foreach (var f in Directory.GetFiles(d))
                {
                    var name = Path.GetFileName(f); int dot = name.IndexOf('.'); if (dot > 0) name = name.Substring(0, dot);
                    var dst = FilePath(type, name);
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    if (File.Exists(dst)) File.Delete(f); else { File.Move(f, dst); n++; }
                }
            }
            return n;
        }

        /// <summary>
        /// Integrity without the key: recompute the SHA-256 of up to <paramref name="maxBytes"/> of files, oldest check first
        /// (the position is kept in .scrub), so every file is checked in turn. A damaged file is moved to .bad and listed —
        /// the next "restic check" on the customer's computer then reports which snapshots need a new backup.
        /// </summary>
        public Msg Scrub(long maxBytes)
        {
            var m = new Msg();
            if (!Exists) return m.Set("checked", 0).Set("bad", 0);
            var all = new List<KeyValuePair<string, string>>();   // type, path
            foreach (var t in Types.Where(t => t != "locks"))
            {
                var d = Path.Combine(Dir, t);
                if (Directory.Exists(d)) all.AddRange(Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".tmp", StringComparison.Ordinal)).Select(f => new KeyValuePair<string, string>(t, f)));
            }
            all.Sort((a, b) => string.CompareOrdinal(a.Value, b.Value));
            var posFile = Path.Combine(Dir, ".scrub");
            var pos = File.Exists(posFile) ? OnlineBackup.Core.Atomic.ReadAllText(posFile).Trim() : "";
            var todo = all.Where(x => string.CompareOrdinal(x.Value, pos) > 0).Concat(all.Where(x => string.CompareOrdinal(x.Value, pos) <= 0)).ToList();
            long done = 0; int checkedN = 0, bad = 0; string last = pos;
            foreach (var x in todo)
            {
                if (done >= maxBytes) break;
                var name = Path.GetFileName(x.Value);
                string hash;
                using (var sha = SHA256.Create()) using (var fs = File.OpenRead(x.Value)) { hash = Bytes.Hex(sha.ComputeHash(fs)); done += fs.Length; }
                checkedN++; last = x.Value;
                if (hash != name)
                {
                    bad++;
                    var q = Path.Combine(Dir, ".bad"); Directory.CreateDirectory(q);
                    File.Move(x.Value, Path.Combine(q, x.Key + "-" + name));
                    m.Add("bad", new Msg().Set("type", x.Key).Set("name", name));
                }
            }
            File.WriteAllText(posFile, last);
            return m.Set("checked", checkedN).Set("bad", bad).Set("bytes", done);
        }

        /// <summary>Bytes stored (repository + trash): counted against the quota.</summary>
        public long Size()
        {
            return Directory.Exists(Dir) ? Directory.EnumerateFiles(Dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) : 0;
        }

        // ---------------------------------------------------------------- access tokens (a password of the repository, not of the user)

        static string TokenPath(string userDir, string setId) { return Path.Combine(userDir, "db", "restic", setId + ".token"); }

        /// <summary>A new random access token for the set; only its SHA-256 is kept. The previous one stops working.</summary>
        public static string NewToken(string userDir, string setId)
        {
            var t = Bytes.Hex(Bytes.Random(24));
            Atomic.WriteText(TokenPath(userDir, setId), Bytes.Hex(Bytes.Sha256(Encoding.UTF8.GetBytes(t))));
            return t;
        }

        public static bool CheckToken(string userDir, string setId, string token)
        {
            var p = TokenPath(userDir, setId);
            if (string.IsNullOrEmpty(token) || !File.Exists(p)) return false;
            var want = OnlineBackup.Core.Atomic.ReadAllText(p).Trim();
            var got = Bytes.Hex(Bytes.Sha256(Encoding.UTF8.GetBytes(token)));
            return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(want), Encoding.ASCII.GetBytes(got));
        }
    }
}
