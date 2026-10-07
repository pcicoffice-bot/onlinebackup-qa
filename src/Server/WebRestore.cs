using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using OnlineBackup.Core;

namespace OnlineBackup.Server
{
    /// <summary>
    /// WEB-010..040: restore from the web, as Ahsay's web restore. The customer signs in on the server's /restore page
    /// (password + 2FA), types the set's encryption password, picks a point and files, and downloads them as a ZIP.
    /// The key lives only for the request: derived in memory (the set's salt, checked against its check value), handed to
    /// restic in the child process's environment, never written or logged. restic reads the set's repository directly on
    /// this server's disk with --no-lock: nothing in the repository is written. restic sets only (the own engine restores
    /// in the client). restic: beside the server in client\restic.exe (the client package's copy), or OB_RESTIC.
    /// </summary>
    public static class WebRestore
    {
        /// <summary>Tests: a stand-in restic program and a short limit.</summary>
        public static string ExeOverride;
        public static TimeSpan Limit = Limits.WebRestore;

        static string Exe
        {
            get
            {
                if (!string.IsNullOrEmpty(ExeOverride)) return ExeOverride;
                var env = Environment.GetEnvironmentVariable("OB_RESTIC");
                if (!string.IsNullOrEmpty(env)) return env;
                return Path.Combine(ClientPackage.ClientDir, OperatingSystem.IsWindows() ? "restic.exe" : Path.Combine("linux", "restic"));
            }
        }

        /// <summary>The set's restic password from what the customer typed, or a WRONG_KEY error.</summary>
        public static string Password(BackupSetInfo set, string typed)
        {
            if (set.Engine != "RESTIC") throw new ApiException(400, "ENGINE", "Web restore is available for restic backup sets. This set is restored from the client software.");
            if (set.KeyType == "DEFAULT" || string.IsNullOrEmpty(set.KeySalt)) throw new ApiException(400, "KEY", "This set's encryption key is kept only on the customer's computer — restore from the client software.");
            if (string.IsNullOrEmpty(typed)) throw new ApiException(400, "KEY", "Enter the encryption password.");
            var k = KeySet.Derive(typed, Convert.FromBase64String(set.KeySalt));
            if (k.CheckValue() != set.KeyCheck) throw new ApiException(403, "WRONG_KEY", "The encryption password is wrong.");
            return Bytes.Hex(k.ToRaw()).Substring(0, 64);
        }

        public static string Run(string repo, string password, params string[] args)
        {
            var psi = new ProcessStartInfo(Exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
            foreach (var a in new[] { "--no-lock", "--no-cache", "-r", repo }.Concat(args)) psi.ArgumentList.Add(a);
            psi.Environment["RESTIC_PASSWORD"] = password;
            psi.Environment.Remove("RESTIC_REPOSITORY");
            // Verification (class "raw-process"): the server waited for restic without a limit — a hung restore held the request for ever
            var r = ProcessRunner.Run(psi, Limit);
            if (r.TimedOut) throw new ApiException(504, "TIMEOUT", "The restore did not finish in " + ProcessRunner.Describe(Limit) + " and was stopped. Restore from the client software, or try a smaller selection.");
            if (r.Code != 0) throw new ApiException(500, "RESTIC", "The restore failed: " + Last(r.Err));
            return r.Out;
        }

        static string Last(string s) { var l = (s ?? "").Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToList(); return l.Count == 0 ? "" : l[l.Count - 1]; }

        public static List<Msg> Points(string repo, string password)
        {
            var list = Json.Arr(Json.Parse(Run(repo, password, "snapshots", "--json")));
            return list.Select(Json.Obj).OrderByDescending(x => Json.Str(x, "time"), StringComparer.Ordinal)
                .Select(x => new Msg().Set("id", Json.Str(x, "short_id") ?? (Json.Str(x, "id") ?? "").Substring(0, 8)).Set("time", Json.Str(x, "time"))).ToList();
        }

        /// <summary>The direct children of a folder of a point (path empty = the point's top folders).</summary>
        public static List<Msg> Ls(string repo, string password, string point, string path)
        {
            CheckPoint(point);
            var args = new List<string> { "ls", "--json", point };
            path = OnlineBackup.Core.ResticPaths.Of(path);   // bug 100: a folder asked for as Windows writes it
            if (!string.IsNullOrEmpty(path)) args.Add(path);
            var items = new List<Msg>();
            var norm = string.IsNullOrEmpty(path) ? null : path.TrimEnd('/');
            foreach (var line in Run(repo, password, args.ToArray()).Split('\n'))
            {
                if (line.Trim().Length == 0) continue;
                var j = Json.Obj(Json.Parse(line));
                if (Json.Str(j, "struct_type") != "node") continue;
                var p = Json.Str(j, "path") ?? "";
                if (norm != null && p == norm) continue;   // the folder itself
                var parent = p.Substring(0, Math.Max(0, p.LastIndexOf('/')));
                if (norm == null ? !TopLevel(p) : parent != norm) continue;
                items.Add(new Msg().Set("path", p).Set("name", Json.Str(j, "name")).Set("type", Json.Str(j, "type")).Set("size", Json.Num(j, "size")).Set("mtime", Json.Str(j, "mtime")));
            }
            return items;
        }

        // without a path restic lists the whole tree: keep the shortest paths (the roots of what was backed up)
        static bool TopLevel(string p) { return p.Count(c => c == '/') <= 1; }

        static void CheckPoint(string point)
        {
            if (string.IsNullOrEmpty(point) || point.Length > 64 || !point.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) throw new ApiException(400, "POINT", "Invalid restore point.");
        }

        /// <summary>The chosen files / folders of a point as a ZIP written to the response (restored to a temporary folder first, then removed).</summary>
        public static long Zip(string repo, string password, string point, IList<string> paths, string tempRoot, Stream output)
        {
            CheckPoint(point);
            if (paths.Count == 0 || paths.Any(p => string.IsNullOrEmpty(p) || p.Contains("\0"))) throw new ApiException(400, "PATHS", "Choose files to restore.");
            var tmp = Path.Combine(tempRoot, "webrestore-" + Guid.NewGuid().ToString("N").Substring(0, 12));
            Directory.CreateDirectory(tmp);
            try
            {
                var args = new List<string> { "restore", point, "--target", tmp };
                foreach (var p in paths) { args.Add("--include"); args.Add(GlobLiteral(OnlineBackup.Core.ResticPaths.Of(p))); }   // bug 100: C:\... -> /C/...
                Run(repo, password, args.ToArray());
                long n = 0;
                using (var z = new ZipArchive(output, ZipArchiveMode.Create, true))
                    foreach (var f in Directory.EnumerateFiles(tmp, "*", SearchOption.AllDirectories))
                    {
                        var rel = Path.GetRelativePath(tmp, f).Replace('\\', '/');
                        z.CreateEntryFromFile(f, rel, CompressionLevel.Fastest);
                        n++;
                    }
                return n;
            }
            finally { var why = OnlineBackup.Core.TempDirs.Remove(tmp); if (why != null) SysLog.Write(null, "System", "error: web restore - decrypted files left on the server: " + why); }   // bug 101: read-only folders too; a folder that stays is reported, not swallowed
        }

        /// <summary>
        /// AI-040: files of all the set's points that match a search (from the AI or plain keywords). restic finds the names
        /// here on the server with the customer's key — the names never leave the server. One row per file version
        /// (path + modification time), in its newest point; at most 200.
        /// </summary>
        public static List<Msg> Find(string repo, string password, SearchFilter f, bool keywordMode)
        {
            Func<string, string> clean = w => new string((w ?? "").Where(c => c != '/' && c != '\\' && c != '\0' && c != '[' && c != ']').ToArray()).Trim();
            var args = new List<string> { "find", "--json", "-i" };
            if (f.From.HasValue) { args.Add("--oldest"); args.Add(f.From.Value.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)); }
            if (f.To.HasValue) { args.Add("--newest"); args.Add(f.To.Value.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)); }
            var patterns = new List<string>();
            if (f.Extensions.Count > 0) patterns.AddRange(f.Extensions.Select(clean).Where(e => e.Length > 0).Select(e => "*." + e));
            else
            {
                var names = f.Names.Select(clean).Where(w => w.Length > 0).ToList();
                if (keywordMode && names.Count > 0) patterns.Add("*" + names.OrderByDescending(w => w.Length).First() + "*");
                else if (!keywordMode && names.Count > 0) patterns.AddRange(names.Select(w => "*" + w + "*"));
            }
            if (patterns.Count == 0) patterns.Add("*");
            args.AddRange(patterns.Distinct().Take(20));
            var times = Points(repo, password).ToDictionary(p => p["id"], p => p["time"] ?? "");
            var best = new Dictionary<string, Msg>();
            var output = Run(repo, password, args.ToArray()).Trim();
            foreach (var hit in Json.Arr(Json.Parse(output.Length > 0 ? output : "[]")).Select(Json.Obj))
            {
                var snap = Json.Str(hit, "snapshot") ?? "";
                var shortId = snap.Length > 8 ? snap.Substring(0, 8) : snap;
                string when; times.TryGetValue(shortId, out when);
                foreach (var mt in Json.Arr(hit.ContainsKey("matches") ? hit["matches"] : null).Select(Json.Obj))
                {
                    if (Json.Str(mt, "type") != "file") continue;
                    var path = Json.Str(mt, "path") ?? "";
                    var mtimeText = Json.Str(mt, "mtime");
                    DateTimeOffset mto; DateTime? mtime = DateTimeOffset.TryParse(mtimeText, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out mto) ? mto.LocalDateTime : (DateTime?)null;
                    if (!f.Match(path, mtime, keywordMode)) continue;
                    var key = path + "|" + mtimeText;
                    Msg prev;
                    if (best.TryGetValue(key, out prev) && string.CompareOrdinal(prev["pointTime"] ?? "", when ?? "") >= 0) continue;
                    best[key] = new Msg().Set("path", path).Set("name", path.Substring(path.LastIndexOf('/') + 1)).Set("size", Json.Num(mt, "size")).Set("mtime", mtimeText)
                        .Set("point", shortId).Set("pointTime", when);
                }
            }
            return best.Values.OrderByDescending(m => m["mtime"] ?? "", StringComparer.Ordinal).Take(200).ToList();
        }

        /// <summary>restic's --include is a pattern: [ * ? \ in names are matched literally.</summary>
        static string GlobLiteral(string s)
        {
            var sb = new StringBuilder();
            foreach (var c in s) { if (c == '[' || c == '*' || c == '?') sb.Append('[').Append(c).Append(']'); else sb.Append(c); }
            return sb.ToString();
        }
    }
}
