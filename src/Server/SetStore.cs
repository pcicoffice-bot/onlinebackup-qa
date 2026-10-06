using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using OnlineBackup.Core;

namespace OnlineBackup.Server
{
    /// <summary>
    /// One backup set on disk, in the Ahsay 6 layout:
    ///   files\&lt;set&gt;\Current\&lt;enc&gt;\&lt;enc&gt;\&lt;enc&gt;.000 (+ .chk)   the latest version of every file (full + deltas)
    ///   files\&lt;set&gt;\YYYY-MM-DD-hh-mm-ss\…                  retention area: what each run replaced or deleted
    ///   files\&lt;set&gt;\index.db                               index (rebuilt from the .chk files at any time)
    ///   files\&lt;set&gt;\jobs.log                               every committed run, append-only (second copy of the restore points)
    ///   files\&lt;set&gt;\jobs\&lt;run&gt;\                           staging of a run that is not committed yet
    /// A run becomes a restore point only at commit; until then Current is untouched. Commit writes a journal first,
    /// so a crash at any moment is rolled forward (journal present) or rolled back (no journal).
    /// </summary>
    public sealed class SetStore
    {
        public const string Current = "Current";
        static readonly Regex SafeRel = new Regex("^[A-Z2-7]{26}(/[A-Z2-7]{26})*$", RegexOptions.Compiled);
        static readonly object[] Locks = Enumerable.Range(0, 64).Select(i => new object()).ToArray();

        public string Dir { get; private set; }
        public string SetId { get; private set; }
        /// <summary>The moves of the last commit (for replication to the second server).</summary>
        public List<string[]> LastMoves { get; private set; }
        string IndexPath { get { return Path.Combine(Dir, "index.db"); } }
        string JobsLog { get { return Path.Combine(Dir, "jobs.log"); } }
        string JobsDir { get { return Path.Combine(Dir, "jobs"); } }
        public object Gate { get { return Locks[(uint)Dir.ToLowerInvariant().GetHashCode() % Locks.Length]; } }

        public SetStore(string userDir, string setId)
        {
            if (!Regex.IsMatch(setId ?? "", "^[0-9]{6,20}$")) throw new ArgumentException("Invalid set id.");
            SetId = setId;
            Dir = Path.Combine(userDir, "files", setId);
            Directory.CreateDirectory(Path.Combine(Dir, Current));
            lock (Gate)
            {
                bool fresh = !File.Exists(IndexPath);
                using (var c = Open()) Schema(c);
                if (fresh && Directory.EnumerateFiles(Dir, "*.chk", SearchOption.AllDirectories).Any()) Rebuild(false);
                RecoverPending();
            }
        }

        SqliteConnection Open()
        {
            var c = new SqliteConnection("Data Source=" + IndexPath + ";Pooling=False");
            c.Open();
            using (var cmd = c.CreateCommand()) { cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;"; cmd.ExecuteNonQuery(); }
            return c;
        }

        static void Schema(SqliteConnection c)
        {
            Exec(c, null, @"CREATE TABLE IF NOT EXISTS objects(loc TEXT PRIMARY KEY, rel TEXT NOT NULL, seq INTEGER NOT NULL, kind TEXT NOT NULL,
                job TEXT NOT NULL, removed TEXT NULL, size INTEGER NOT NULL, orig INTEGER NOT NULL, mtime INTEGER NOT NULL, sha TEXT NOT NULL,
                enc TEXT NOT NULL, perm INTEGER NOT NULL DEFAULT 0);
              CREATE INDEX IF NOT EXISTS objects_rel ON objects(rel);
              CREATE INDEX IF NOT EXISTS objects_removed ON objects(removed);
              CREATE TABLE IF NOT EXISTS jobs(id TEXT PRIMARY KEY, status TEXT NOT NULL, finished INTEGER NOT NULL DEFAULT 0, newf INTEGER NOT NULL DEFAULT 0,
                updf INTEGER NOT NULL DEFAULT 0, permf INTEGER NOT NULL DEFAULT 0, delf INTEGER NOT NULL DEFAULT 0, bytes INTEGER NOT NULL DEFAULT 0, expired INTEGER NOT NULL DEFAULT 0);
              CREATE TABLE IF NOT EXISTS resend(rel TEXT PRIMARY KEY, reason TEXT);");
        }

        static int Exec(SqliteConnection c, SqliteTransaction t, string sql, params object[] args)
        {
            using (var cmd = c.CreateCommand())
            {
                cmd.Transaction = t;
                cmd.CommandText = sql;
                for (int i = 0; i < args.Length; i++) cmd.Parameters.AddWithValue("$" + i, args[i] ?? DBNull.Value);
                return cmd.ExecuteNonQuery();
            }
        }

        static List<object[]> Query(SqliteConnection c, string sql, params object[] args)
        {
            var rows = new List<object[]>();
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = sql;
                for (int i = 0; i < args.Length; i++) cmd.Parameters.AddWithValue("$" + i, args[i] ?? DBNull.Value);
                using (var r = cmd.ExecuteReader())
                    while (r.Read()) { var row = new object[r.FieldCount]; r.GetValues(row); rows.Add(row); }
            }
            return rows;
        }

        static string OsPath(string rel) { return rel.Replace('/', Path.DirectorySeparatorChar); }
        string Abs(string loc) { return Path.Combine(Dir, OsPath(loc)); }

        // ---------------------------------------------------------------- runs (jobs)

        /// <summary>
        /// R1 (GPT audit 1–2): a run is open only while its agent shows signs of life. Every call of the run (begin, each
        /// object — also during a long upload —, deletes, progress, the agent's heartbeat every minute) renews its lease;
        /// a run without a sign of life for this long is dead (power cut, reboot, killed) and no longer blocks the set.
        /// </summary>
        public static TimeSpan Lease = TimeSpan.FromMinutes(5);

        /// <summary>The runs closed by <see cref="BeginJob"/> because their agent died (the caller records them as interrupted).</summary>
        public List<string> Expired = new List<string>();

        /// <summary>Opens a run. One open run per set; an open run whose lease ran out is closed first (listed in <see cref="Expired"/>).</summary>
        /// <param name="key">the agent's own name for this attempt (bug 20): "begin" said again with the same key — its answer
        /// was lost and the client sent it again — gets the same run, instead of "another backup is running"</param>
        public string BeginJob(DateTime utc, string key = null)
        {
            if (key != null && !System.Text.RegularExpressions.Regex.IsMatch(key, "^[0-9a-f]{32}$")) key = null;
            lock (Gate)
            {
                Directory.CreateDirectory(JobsDir);
                Expired = ExpireStale(utc);
                foreach (var d in Directory.GetDirectories(JobsDir))
                {
                    if (File.Exists(Path.Combine(d, "journal"))) { RecoverPending(); continue; }
                    var kf = Path.Combine(d, "key");
                    if (key != null && File.Exists(kf) && File.ReadAllText(kf).Trim() == key) { File.WriteAllText(Path.Combine(d, "lease"), ""); return Path.GetFileName(d); }
                    throw new ApiException(409, "BUSY", "Another backup of this set is still running (it last answered " + Math.Max(0, (int)(utc - LeaseTime(d)).TotalMinutes) + " minutes ago).");
                }
                string id = RunId.From(utc);
                var ended = new HashSet<string>(ReadLines(JobsLog).Where(l => l.StartsWith("E\t", StringComparison.Ordinal)).Select(l => l.Substring(2).Trim()));
                using (var c = Open())
                    while (Query(c, "SELECT 1 FROM jobs WHERE id=$0", id).Count > 0 || ended.Contains(id) || Directory.Exists(Path.Combine(Dir, id)) || Directory.Exists(Path.Combine(JobsDir, id)))
                    { utc = utc.AddSeconds(1); id = RunId.From(utc); }
                Directory.CreateDirectory(Path.Combine(JobsDir, id, "new"));
                File.WriteAllText(Path.Combine(JobsDir, id, "lease"), "");
                if (key != null) File.WriteAllText(Path.Combine(JobsDir, id, "key"), key);
                return id;
            }
        }

        /// <summary>A run closed without a commit (aborted, its lease expired, reported dead): its id is kept as used
        /// ("E" in jobs.log), so a run begun in the same second never gets it again (found while fixing bug 42).</summary>
        void Ended(string job) { try { Atomic.AppendLine(JobsLog, "E\t" + job); } catch (IOException) { } }

        static DateTime LeaseTime(string jobDir)
        {
            var l = Path.Combine(jobDir, "lease");
            var t = Directory.GetCreationTimeUtc(jobDir);
            if (File.Exists(l)) { var m = File.GetLastWriteTimeUtc(l); if (m > t) t = m; }
            return t;
        }

        /// <summary>A sign of life of an open run (no effect on a run that is not open).</summary>
        public void Touch(string job)
        {
            if (!RunId.TryParse(job ?? "", out _)) return;
            var d = Path.Combine(JobsDir, job);
            // bug 47 (Agent F, F-5): a sign of life that came while the run was being closed wrote a NEW lease into the
            // half-deleted folder — a ghost run that refused the next backup. Only an existing lease is touched.
            var l = Path.Combine(d, "lease");
            try { if (File.Exists(l)) File.SetLastWriteTimeUtc(l, SystemClock.UtcNow); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        /// <summary>A run's folder leaves the open runs in one step (moved aside, then deleted): nothing arriving late — a
        /// sign of life, an object — can bring it back half (bug 47).</summary>
        void DropJobDir(string d)
        {
            var dead = Path.Combine(Dir, "closed-runs", Path.GetFileName(d) + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            try { Directory.CreateDirectory(Path.GetDirectoryName(dead)); Directory.Move(d, dead); d = dead; } catch (IOException) { } catch (UnauthorizedAccessException) { }
            try { Directory.Delete(d, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            // what an earlier close could not delete (a file still open then) goes now
            try { foreach (var old in Directory.GetDirectories(Path.Combine(Dir, "closed-runs"))) try { Directory.Delete(old, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } } catch (IOException) { }
        }

        /// <summary>Closes the open runs whose lease ran out (not those already committing: they are rolled forward). Returns their ids.</summary>
        public List<string> ExpireStale(DateTime utc)
        {
            var gone = new List<string>();
            lock (Gate)
            {
                if (!Directory.Exists(JobsDir)) return gone;
                foreach (var d in Directory.GetDirectories(JobsDir))
                {
                    if (File.Exists(Path.Combine(d, "journal"))) continue;
                    if (utc - LeaseTime(d) <= Lease) continue;
                    Ended(Path.GetFileName(d));
                    DropJobDir(d);
                    gone.Add(Path.GetFileName(d));
                }
            }
            return gone;
        }

        /// <summary>The agent says this run of its own died: closed at once if still open. True when it was open.</summary>
        public bool AbortIfOpen(string job)
        {
            if (!RunId.TryParse(job ?? "", out _)) return false;
            lock (Gate)
            {
                var d = Path.Combine(JobsDir, job);
                if (!Directory.Exists(d) || File.Exists(Path.Combine(d, "journal"))) return false;
                Ended(job);
                DropJobDir(d);
                return true;
            }
        }

        /// <summary>The open runs of a set folder (without opening the store): for the server's sweep.</summary>
        public static IEnumerable<string> OpenJobDirs(string setDir)
        {
            var j = Path.Combine(setDir, "jobs");
            return Directory.Exists(j) ? Directory.GetDirectories(j) : new string[0];
        }
        public static bool LeaseExpired(string jobDir, DateTime utc) { return !File.Exists(Path.Combine(jobDir, "journal")) && utc - LeaseTime(jobDir) > Lease; }

        string JobDir(string job)
        {
            if (!RunId.TryParse(job ?? "", out _)) throw new ApiException(400, "BAD_JOB", "Invalid run id.");
            var d = Path.Combine(JobsDir, job);
            if (!Directory.Exists(d)) throw new ApiException(404, "NO_JOB", "The run is not open (it may have been closed or cancelled).");
            return d;
        }

        public static void CheckRel(string rel)
        {
            if (rel == null || rel.Length > 4000 || !SafeRel.IsMatch(rel)) throw new ApiException(400, "BAD_PATH", "Invalid path.");
        }

        /// <summary>
        /// Receives one object into the run's staging folder. The server computes the SHA-256 itself and stores it in
        /// the .chk; the agent compares it with its own (end-to-end check).
        /// </summary>
        public ChkRecord StageObject(string job, ChkRecord meta, Stream body, long maxBytes)
        {
            CheckRel(meta.Rel);
            if (meta.Seq < 0 || meta.Seq > 999 || (meta.Kind != "F" && meta.Kind != "D") || (meta.Kind == "F") != (meta.Seq == 0))
                throw new ApiException(400, "BAD_OBJECT", "Invalid type or version number.");
            var jd = JobDir(job);
            var target = Path.Combine(jd, "new", OsPath(ChkRecord.ObjectName(meta.Rel, meta.Seq)));
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            var tmp = target + ".part";
            long total = 0;
            string sha;
            try
            {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            using (var h = System.Security.Cryptography.SHA256.Create())
            {
                var buf = new byte[1 << 16];
                int r;
                var touched = DateTime.UtcNow;
                while ((r = body.Read(buf, 0, buf.Length)) > 0)
                {
                    total += r;
                    if ((DateTime.UtcNow - touched).TotalSeconds > 30) { touched = DateTime.UtcNow; Touch(job); }   // a long upload is a sign of life
                    if (total > maxBytes) { fs.Close(); File.Delete(tmp); throw new ApiException(507, "QUOTA", "The user's quota was exceeded."); }
                    fs.Write(buf, 0, r);
                    h.TransformBlock(buf, 0, r, null, 0);
                }
                h.TransformFinalBlock(buf, 0, 0);
                sha = Bytes.Hex(h.Hash);
                fs.Flush(true);
            }
            }
            catch (IOException) { try { File.Delete(tmp); } catch (IOException) { } throw; }   // a half object never stays on the disk
            if (!WellFormed(tmp, total)) { File.Delete(tmp); throw new ApiException(400, "BAD_OBJECT", "The received object is partial or damaged and was refused."); }
            if (File.Exists(target)) File.Delete(target);
            File.Move(tmp, target);
            meta.Job = job; meta.Size = total; meta.Sha256 = sha;
            Atomic.WriteText(target + ".chk", meta.ToText());
            Atomic.AppendLine(Path.Combine(jd, "uploads.txt"), ChkRecord.ObjectName(meta.Rel, meta.Seq));
            Touch(job);
            return meta;
        }

        /// <summary>Framing check without the key: start and end markers, and the header offset points to a header that ends exactly at the footer.</summary>
        static bool WellFormed(string path, long length)
        {
            if (length < 4 + 4 + 12) return false;
            using (var fs = File.OpenRead(path))
            {
                var head = new byte[4]; fs.Read(head, 0, 4);
                if (Encoding.ASCII.GetString(head) != "OBK1") return false;
                var tail = new byte[12]; fs.Seek(-12, SeekOrigin.End); if (fs.Read(tail, 0, 12) != 12) return false;
                if (Encoding.ASCII.GetString(tail, 8, 4) != "OBKE") return false;
                long off = BitConverter.ToInt64(tail, 0);
                if (off < 4 || off > length - 16) return false;
                var lenb = new byte[4]; fs.Seek(off, SeekOrigin.Begin); fs.Read(lenb, 0, 4);
                return off + 4 + BitConverter.ToInt32(lenb, 0) + 12 == length;
            }
        }

        public long StagedBytes(string job)
        {
            var d = Path.Combine(JobDir(job), "new");
            return Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".chk") && !f.EndsWith(".part")).Sum(f => new FileInfo(f).Length);
        }

        public void StageDelete(string job, string rel)
        {
            CheckRel(rel);
            Atomic.AppendLine(Path.Combine(JobDir(job), "deletes.txt"), rel);
            Touch(job);
        }

        public void Abort(string job)
        {
            lock (Gate)
            {
                var d = JobDir(job);
                if (File.Exists(Path.Combine(d, "journal"))) return;   // already committing: will be rolled forward
                Ended(job);
                DropJobDir(d);
            }
        }

        /// <summary>Commits a run: the restore point exists only after every object is in place and the index agrees.</summary>
        public Msg Commit(string job, Msg stats)
        {
            lock (Gate)
            {
                var moves = PrepareCommit(job, stats);
                Apply(job, moves, stats);
                LastMoves = moves;
                return VerifyJob(job);
            }
        }

        /// <summary>Plans the moves and writes the journal (the point of no return). Public for the crash tests.</summary>
        public List<string[]> PrepareCommit(string job, Msg stats)
        {
            lock (Gate)
            {
                var jd = JobDir(job);
                var uploads = ReadLines(Path.Combine(jd, "uploads.txt")).Distinct().ToList();
                var deletes = ReadLines(Path.Combine(jd, "deletes.txt")).Distinct().ToList();
                var moves = new List<string[]>();
                using (var c = Open())
                {
                    Func<string, List<string>> chain = rel => Query(c, "SELECT loc FROM objects WHERE rel=$0 AND removed IS NULL", rel).Select(r => (string)r[0]).ToList();
                    var replaced = new HashSet<string>();
                    foreach (var rel in deletes) replaced.Add(rel);
                    foreach (var name in uploads)
                    {
                        var rec = ChkRecord.Parse(File.ReadAllText(Path.Combine(jd, "new", OsPath(name)) + ".chk"));
                        if (rec.Kind == "F") replaced.Add(rec.Rel);
                        else
                        {
                            var cur = Query(c, "SELECT MAX(seq) FROM objects WHERE rel=$0 AND removed IS NULL", rec.Rel);
                            long have = cur.Count == 0 || cur[0][0] is DBNull ? -1 : (long)cur[0][0];
                            bool stagedPrev = uploads.Contains(ChkRecord.ObjectName(rec.Rel, rec.Seq - 1));
                            if (have != rec.Seq - 1 && !stagedPrev)
                            {
                                // A delta whose base is missing can never be restored: refuse it and ask the agent for a full copy.
                                Exec(c, null, "INSERT OR REPLACE INTO resend(rel, reason) VALUES($0,$1)", rec.Rel, "delta without base");
                                continue;
                            }
                        }
                    }
                    foreach (var rel in replaced)
                        foreach (var loc in chain(rel))
                        {
                            var dst = job + loc.Substring(Current.Length);
                            moves.Add(new[] { loc, dst });
                            moves.Add(new[] { loc + ".chk", dst + ".chk" });
                        }
                    foreach (var name in uploads)
                    {
                        var rec = ChkRecord.Parse(File.ReadAllText(Path.Combine(jd, "new", OsPath(name)) + ".chk"));
                        if (rec.Kind == "D" && Query(c, "SELECT 1 FROM resend WHERE rel=$0 AND reason='delta without base'", rec.Rel).Count > 0) continue;
                        moves.Add(new[] { "jobs/" + job + "/new/" + name, Current + "/" + name });
                        moves.Add(new[] { "jobs/" + job + "/new/" + name + ".chk", Current + "/" + name + ".chk" });
                    }
                }
                // 1. Journal (all moves), written atomically: from here the run is rolled forward even after a crash.
                var sb = new StringBuilder();
                foreach (var m in moves) sb.Append("M\t").Append(m[0]).Append('\t').Append(m[1]).Append('\n');
                Atomic.WriteText(Path.Combine(jd, "journal"), sb.ToString());
                // 2. Second copy of the restore point list.
                Atomic.AppendLine(JobsLog, "C\t" + job + "\t" + StatsLine(stats));
                return moves;
            }
        }

        static string StatsLine(Msg s)
        {
            return string.Join("\t", new[] { "new", "upd", "perm", "del", "bytes" }.Select(k => k + "=" + (s == null ? 0 : s.Long(k))));
        }

        static IEnumerable<string> ReadLines(string p) { return File.Exists(p) ? File.ReadAllLines(p).Where(l => l.Length > 0) : Enumerable.Empty<string>(); }

        void Apply(string job, List<string[]> moves, Msg stats)
        {
            foreach (var m in moves)
            {
                string src = Abs(m[0]), dst = Abs(m[1]);
                if (File.Exists(src))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    if (File.Exists(dst)) File.Delete(dst);
                    File.Move(src, dst);
                }
                else if (!File.Exists(dst)) SysLog.Write(null, "System", "warn: commit move source missing " + m[0]);
            }
            using (var c = Open())
            using (var t = c.BeginTransaction())
            {
                foreach (var m in moves.Where(x => !x[0].EndsWith(".chk")))
                {
                    if (m[0].StartsWith(Current + "/"))
                        Exec(c, t, "UPDATE objects SET loc=$0, removed=$1 WHERE loc=$2", m[1], job, m[0]);
                    else
                    {
                        var rec = ChkRecord.Parse(File.ReadAllText(Abs(m[1]) + ".chk"));
                        Exec(c, t, "INSERT OR REPLACE INTO objects(loc,rel,seq,kind,job,removed,size,orig,mtime,sha,enc,perm) VALUES($0,$1,$2,$3,$4,NULL,$5,$6,$7,$8,$9,$10)",
                            m[1], rec.Rel, rec.Seq, rec.Kind, rec.Job, rec.Size, rec.Orig, rec.Mtime, rec.Sha256, rec.EncPath, rec.PermOnly ? 1 : 0);
                        if (rec.Kind == "F") Exec(c, t, "DELETE FROM resend WHERE rel=$0", rec.Rel);
                    }
                }
                Exec(c, t, "INSERT OR REPLACE INTO jobs(id,status,finished,newf,updf,permf,delf,bytes,expired) VALUES($0,'OK',$1,$2,$3,$4,$5,$6,0)",
                    job, RunId.UnixMs(SystemClock.UtcNow), stats == null ? 0 : stats.Long("new"), stats == null ? 0 : stats.Long("upd"),
                    stats == null ? 0 : stats.Long("perm"), stats == null ? 0 : stats.Long("del"), stats == null ? 0 : stats.Long("bytes"));
                t.Commit();
            }
            var jd = Path.Combine(JobsDir, job);
            if (Directory.Exists(jd)) Directory.Delete(jd, true);
        }

        /// <summary>After a crash: journals are rolled forward (then the index is rebuilt from disk); staging without a journal is dropped.</summary>
        public void RecoverPending()
        {
            lock (Gate)
            {
                if (!Directory.Exists(JobsDir)) return;
                bool rebuilt = false;
                foreach (var d in Directory.GetDirectories(JobsDir))
                {
                    var journal = Path.Combine(d, "journal");
                    if (!File.Exists(journal)) continue;
                    var job = Path.GetFileName(d);
                    var moves = File.ReadAllLines(journal).Where(l => l.StartsWith("M\t")).Select(l => l.Split('\t')).Select(p => new[] { p[1], p[2] }).ToList();
                    foreach (var m in moves)
                    {
                        string src = Abs(m[0]), dst = Abs(m[1]);
                        if (File.Exists(src)) { Directory.CreateDirectory(Path.GetDirectoryName(dst)); if (File.Exists(dst)) File.Delete(dst); File.Move(src, dst); }
                    }
                    if (!ReadLines(JobsLog).Any(l => l.StartsWith("C\t" + job + "\t"))) Atomic.AppendLine(JobsLog, "C\t" + job + "\t" + StatsLine(null));
                    Directory.Delete(d, true);
                    SysLog.Write(null, "System", "recovered interrupted commit " + SetId + "/" + job);
                    rebuilt = true;
                }
                if (rebuilt) Rebuild(false);
            }
        }

        /// <summary>
        /// Second server: applies a commit exactly as the first server did — the objects were already received into the
        /// same staging paths; the same journal is written, then the same moves. Idempotent.
        /// </summary>
        public void ApplyReplicated(string job, List<string[]> moves, string statsLine)
        {
            lock (Gate)
            {
                using (var c = Open())
                    if (Query(c, "SELECT 1 FROM jobs WHERE id=$0", job).Count > 0) return;
                var jd = Path.Combine(JobsDir, job);
                Directory.CreateDirectory(jd);
                var sb = new StringBuilder();
                foreach (var m in moves) sb.Append("M\t").Append(m[0]).Append('\t').Append(m[1]).Append('\n');
                Atomic.WriteText(Path.Combine(jd, "journal"), sb.ToString());
                Atomic.AppendLine(JobsLog, "C\t" + job + "\t" + statsLine);
                var stats = new Msg();
                foreach (var kv in statsLine.Split('\t').Select(x => x.Split('=')).Where(x => x.Length == 2)) stats.Set(kv[0], kv[1]);
                Apply(job, moves, stats);
            }
        }

        public void ReceiveFile(string relPath, Stream body, bool overwrite)
        {
            if (relPath.Contains("..") || relPath.StartsWith("/")) throw new ApiException(400, "BAD_PATH", "Invalid path.");
            var p = Abs(relPath);
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            var tmp = p + ".recv";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write)) { body.CopyTo(fs); fs.Flush(true); }
            if (File.Exists(p))
            {
                if (!overwrite)
                {
                    bool same;
                    using (var a = File.OpenRead(p)) using (var b = File.OpenRead(tmp)) same = Bytes.Sha256Hex(a) == Bytes.Sha256Hex(b);
                    File.Delete(tmp);
                    if (!same) throw new ApiException(409, "IMMUTABLE", "An existing file on the secondary server is never overwritten.");
                    return;
                }
                File.Delete(p);
            }
            File.Move(tmp, p);
        }

        /// <summary>Where an object created by a commit is now (it may have moved to a run folder since).</summary>
        public string Locate(string originalLoc, string job)
        {
            bool chk = originalLoc.EndsWith(".chk");
            var objLoc = chk ? originalLoc.Substring(0, originalLoc.Length - 4) : originalLoc;
            var name = objLoc.Substring(objLoc.IndexOf('/') + 1);
            using (var c = Open())
            {
                // The exact version created by that run (a newer version may sit at the same name in Current now).
                var r = Query(c, "SELECT loc FROM objects WHERE job=$0 AND (loc=$1 OR loc LIKE $2)", job, objLoc, "%/" + name);
                if (r.Count == 0) return null;
                var loc = (string)r[0][0];
                return chk ? loc + ".chk" : loc;
            }
        }

        /// <summary>Deletes one run folder of the retention area (the second server, after its own delay).</summary>
        public void DeleteRunFolder(string folder)
        {
            lock (Gate)
            {
                if (!RunId.TryParse(folder, out _)) return;
                var d = Path.Combine(Dir, folder);
                if (Directory.Exists(d)) Directory.Delete(d, true);
                using (var c = Open()) Exec(c, null, "DELETE FROM objects WHERE removed=$0", folder);
            }
        }

        public void ExpireJobs(IEnumerable<string> jobs)
        {
            lock (Gate)
                using (var c = Open())
                    foreach (var j in jobs)
                        if (Exec(c, null, "UPDATE jobs SET expired=1 WHERE id=$0 AND expired=0", j) > 0) Atomic.AppendLine(JobsLog, "X\t" + j);
        }

        public long PointCount()
        {
            using (var c = Open()) return (long)Query(c, "SELECT COUNT(*) FROM jobs WHERE status='OK' AND expired=0")[0][0];
        }

        public long PreviousFileCount(string beforeJob)
        {
            using (var c = Open()) return (long)Query(c, "SELECT COUNT(DISTINCT rel) FROM objects WHERE job<$0 AND (removed IS NULL OR removed>=$0)", beforeJob)[0][0];
        }

        // ---------------------------------------------------------------- reading

        public List<string> Points()
        {
            using (var c = Open()) return Query(c, "SELECT id FROM jobs WHERE status='OK' AND expired=0 ORDER BY id").Select(r => (string)r[0]).ToList();
        }

        public Msg PointsMsg()
        {
            var m = new Msg();
            using (var c = Open())
                foreach (var r in Query(c, "SELECT id,newf,updf,permf,delf,bytes FROM jobs WHERE status='OK' AND expired=0 ORDER BY id"))
                    m.Add("points", new Msg().Set("id", r[0]).Set("new", r[1]).Set("upd", r[2]).Set("perm", r[3]).Set("del", r[4]).Set("bytes", r[5]));
            return m;
        }

        /// <summary>Every file as it was at the end of run "point": its chain of objects (full + deltas) and the encrypted path.</summary>
        public Msg FilesAt(string point)
        {
            if (point == null) point = Points().LastOrDefault();
            var m = new Msg().Set("point", point);
            if (point == null) return m;
            using (var c = Open())
            {
                if (Query(c, "SELECT 1 FROM jobs WHERE id=$0 AND status='OK' AND expired=0", point).Count == 0) throw new ApiException(404, "NO_POINT", "The restore point does not exist or has expired.");
                var rows = Query(c, "SELECT rel,seq,kind,job,loc,size,orig,mtime,sha,enc,perm FROM objects WHERE job<=$0 AND (removed IS NULL OR removed>$0) ORDER BY rel, seq", point);
                foreach (var g in rows.GroupBy(r => (string)r[0]))
                {
                    var items = g.OrderBy(r => (long)r[1]).ToList();
                    if ((string)items[0][2] != "F") continue;   // a chain without its full copy is not restorable (reported by verify)
                    var last = items[items.Count - 1];
                    var f = new Msg().Set("rel", g.Key).Set("enc", last[9]).Set("orig", last[6]).Set("mtime", last[7]).Set("perm", last[10]);
                    foreach (var r in items) f.Add("objects", new Msg().Set("loc", r[4]).Set("seq", r[1]).Set("kind", r[2]).Set("size", r[5]).Set("sha", r[8]));
                    m.Add("files", f);
                }
            }
            return m;
        }

        public string ObjectPath(string loc)
        {
            if (loc == null || loc.Contains("..") || loc.Contains("\\")) throw new ApiException(400, "BAD_PATH", "Invalid path.");
            using (var c = Open())
                if (Query(c, "SELECT 1 FROM objects WHERE loc=$0", loc).Count == 0) throw new ApiException(404, "NO_OBJECT", "The object was not found.");
            return Abs(loc);
        }

        public List<string> Resend()
        {
            using (var c = Open()) return Query(c, "SELECT rel FROM resend").Select(r => (string)r[0]).ToList();
        }

        /// <summary>Ahsay statistics: data area (Current) and retention area — stored (compressed) bytes, original bytes, files.</summary>
        public Msg Stats()
        {
            using (var c = Open())
            {
                var cur = Query(c, "SELECT COALESCE(SUM(size),0), COUNT(DISTINCT rel) FROM objects WHERE removed IS NULL")[0];
                var curOrig = Query(c, @"SELECT COALESCE(SUM(o.orig),0) FROM objects o JOIN (SELECT rel, MAX(seq) s FROM objects WHERE removed IS NULL GROUP BY rel) m
                                          ON o.rel=m.rel AND o.seq=m.s WHERE o.removed IS NULL")[0];
                var ret = Query(c, "SELECT COALESCE(SUM(size),0), COUNT(*), COALESCE(SUM(orig),0) FROM objects WHERE removed IS NOT NULL")[0];
                var last = Query(c, "SELECT MAX(id) FROM jobs WHERE status='OK'")[0][0];
                return new Msg().Set("dataSize", cur[0]).Set("dataFiles", cur[1]).Set("dataOrig", curOrig[0])
                    .Set("retainSize", ret[0]).Set("retainFiles", ret[1]).Set("retainOrig", ret[2]).Set("lastJob", last is DBNull ? "" : last);
            }
        }

        // ---------------------------------------------------------------- integrity

        /// <summary>Health check of one run (right after commit, like Veeam): every new object is re-read and hashed.</summary>
        public Msg VerifyJob(string job)
        {
            using (var c = Open())
                return VerifyRows(c, Query(c, "SELECT loc,rel,sha FROM objects WHERE job=$0", job));
        }

        /// <summary>Full verification: every object in the set is read and hashed (the monthly cycle).</summary>
        public Msg VerifyAll()
        {
            using (var c = Open()) return VerifyRows(c, Query(c, "SELECT loc,rel,sha FROM objects"));
        }

        Msg VerifyRows(SqliteConnection c, List<object[]> rows)
        {
            int ok = 0, bad = 0, unreadable = 0;
            foreach (var r in rows)
            {
                string loc = (string)r[0], rel = (string)r[1], sha = (string)r[2];
                var p = Abs(loc);
                string actual = null;
                try { using (var fs = File.OpenRead(p)) actual = Bytes.Sha256Hex(fs); }
                catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }   // gone: damaged
                catch (IOException e)
                {
                    // bug 46 (Agent F, F-4): a read that failed for a moment (an antivirus, a copy of the store, too many
                    // open files) made a sound object "damaged": quarantined and taken out of every restore point — an old
                    // version cannot be sent again. Not read is not checked: counted, said, tried at the next check.
                    unreadable++;
                    SysLog.Write(null, "System", "warning: object not checked (could not be read now: " + e.Message + ") " + SetId + "/" + loc);
                    continue;
                }
                catch (UnauthorizedAccessException e) { unreadable++; SysLog.Write(null, "System", "warning: object not checked (" + e.Message + ") " + SetId + "/" + loc); continue; }
                if (actual == sha) { ok++; continue; }
                bad++;
                Quarantine(loc);
                Exec(c, null, "DELETE FROM objects WHERE loc=$0", loc);
                // Self-healing: the agent sends a full copy of this file from the source in its next run.
                Exec(c, null, "INSERT OR REPLACE INTO resend(rel, reason) VALUES($0,$1)", rel, "damaged object " + loc);
                SysLog.Write(null, "System", "error: damaged object quarantined " + SetId + "/" + loc);
            }
            return new Msg().Set("checked", ok + bad).Set("ok", ok).Set("bad", bad).Set("unreadable", unreadable);
        }

        void Quarantine(string loc)
        {
            var q = Path.Combine(Dir, "Quarantine", RunId.From(SystemClock.UtcNow));
            foreach (var p in new[] { Abs(loc), Abs(loc) + ".chk" })
                if (File.Exists(p))
                {
                    var dst = Path.Combine(q, OsPath(loc) + (p.EndsWith(".chk") ? ".chk" : ""));
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    if (File.Exists(dst)) File.Delete(dst);
                    File.Move(p, dst);
                }
        }

        /// <summary>
        /// Rebuild (as in Ahsay): the index is rebuilt from scratch from the .chk files on disk — no key needed.
        /// verify=true also re-hashes every object. Orphans (object without .chk or the opposite) go to quarantine.
        /// </summary>
        public Msg Rebuild(bool verify)
        {
            lock (Gate)
            {
                int files = 0, bad = 0, orphans = 0;
                var records = new List<KeyValuePair<string, ChkRecord>>();
                foreach (var area in Directory.GetDirectories(Dir))
                {
                    var name = Path.GetFileName(area);
                    bool isCurrent = name == Current;
                    if (!isCurrent && !RunId.TryParse(name, out _)) continue;
                    foreach (var f in Directory.EnumerateFiles(area, "*", SearchOption.AllDirectories).ToList())
                    {
                        var loc = name + "/" + f.Substring(area.Length + 1).Replace(Path.DirectorySeparatorChar, '/');
                        if (f.EndsWith(".chk"))
                        {
                            if (!File.Exists(f.Substring(0, f.Length - 4))) { orphans++; Quarantine(loc.Substring(0, loc.Length - 4)); }
                            continue;
                        }
                        if (Atomic.IsTemp(f)) { File.Delete(f); continue; }   // bug 35: by the file's name, never the path
                        if (!File.Exists(f + ".chk")) { orphans++; Quarantine(loc); continue; }
                        ChkRecord rec;
                        try { rec = ChkRecord.Parse(File.ReadAllText(f + ".chk")); }
                        catch (InvalidDataException) { orphans++; Quarantine(loc); continue; }
                        if (verify)
                        {
                            string actual;
                            using (var fs = File.OpenRead(f)) actual = Bytes.Sha256Hex(fs);
                            if (actual != rec.Sha256) { bad++; Quarantine(loc); records.Add(new KeyValuePair<string, ChkRecord>("!" + loc, rec)); continue; }
                        }
                        files++;
                        records.Add(new KeyValuePair<string, ChkRecord>(loc, rec));
                    }
                }
                var jobs = new Dictionary<string, string[]>();
                var expired = new HashSet<string>();
                foreach (var l in ReadLines(JobsLog))
                {
                    var p = l.Split('\t');
                    if (p[0] == "C" && p.Length > 1) jobs[p[1]] = p;
                    if (p[0] == "X" && p.Length > 1) expired.Add(p[1]);
                }
                foreach (var r in records) if (!jobs.ContainsKey(r.Value.Job)) jobs[r.Value.Job] = new[] { "C", r.Value.Job };
                var tmp = IndexPath + ".rebuild";
                foreach (var p in new[] { tmp, tmp + "-wal", tmp + "-shm" }) if (File.Exists(p)) File.Delete(p);
                using (var c = new SqliteConnection("Data Source=" + tmp + ";Pooling=False"))
                {
                    c.Open();
                    Schema(c);
                    using (var t = c.BeginTransaction())
                    {
                        foreach (var r in records)
                        {
                            if (r.Key.StartsWith("!"))
                            {
                                Exec(c, t, "INSERT OR REPLACE INTO resend(rel, reason) VALUES($0,$1)", r.Value.Rel, "damaged object (rebuild)");
                                continue;
                            }
                            var area = r.Key.Substring(0, r.Key.IndexOf('/'));
                            Exec(c, t, "INSERT OR REPLACE INTO objects(loc,rel,seq,kind,job,removed,size,orig,mtime,sha,enc,perm) VALUES($0,$1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11)",
                                r.Key, r.Value.Rel, r.Value.Seq, r.Value.Kind, r.Value.Job, area == Current ? null : area, r.Value.Size, r.Value.Orig, r.Value.Mtime, r.Value.Sha256, r.Value.EncPath, r.Value.PermOnly ? 1 : 0);
                        }
                        foreach (var j in jobs)
                        {
                            Func<string, long> st = k => { var e = j.Value.FirstOrDefault(x => x.StartsWith(k + "=")); long v; return e != null && long.TryParse(e.Substring(k.Length + 1), out v) ? v : 0; };
                            Exec(c, t, "INSERT OR REPLACE INTO jobs(id,status,finished,newf,updf,permf,delf,bytes,expired) VALUES($0,'OK',0,$1,$2,$3,$4,$5,$6)",
                                j.Key, st("new"), st("upd"), st("perm"), st("del"), st("bytes"), expired.Contains(j.Key) ? 1 : 0);
                        }
                        // Keep the resend requests of the old index (damage found earlier and not yet repaired).
                        if (File.Exists(IndexPath))
                            using (var old = Open())
                                foreach (var r in Query(old, "SELECT rel, reason FROM resend"))
                                    Exec(c, t, "INSERT OR IGNORE INTO resend(rel, reason) VALUES($0,$1)", r[0], r[1]);
                        t.Commit();
                    }
                    using (var cmd = c.CreateCommand()) { cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);"; cmd.ExecuteNonQuery(); }
                }
                SqliteConnection.ClearAllPools();
                foreach (var p in new[] { IndexPath + "-wal", IndexPath + "-shm" }) if (File.Exists(p)) File.Delete(p);
                if (File.Exists(IndexPath)) File.Delete(IndexPath);
                File.Move(tmp, IndexPath);
                foreach (var p in new[] { tmp + "-wal", tmp + "-shm" }) if (File.Exists(p)) File.Delete(p);
                return new Msg().Set("files", files).Set("bad", bad).Set("orphans", orphans).Set("points", jobs.Count - expired.Count);
            }
        }

        // ---------------------------------------------------------------- retention

        /// <summary>
        /// Applies the retention policy: restore points outside the policy expire, and a run folder of the retention
        /// area is deleted only when no kept point still needs any version in it. Current is never deleted.
        /// </summary>
        public Msg ApplyRetention(RetentionPolicy policy, DateTime nowUtc)
        {
            lock (Gate)
            {
                int expiredPoints = 0, deletedObjects = 0; long freed = 0;
                var result = new Msg();
                using (var c = Open())
                {
                    var jobs = Query(c, "SELECT id FROM jobs WHERE status='OK' AND expired=0 ORDER BY id").Select(r => (string)r[0]).ToList();
                    var keep = policy.Keep(jobs, nowUtc);
                    foreach (var j in jobs.Where(j => !keep.Contains(j)))
                    {
                        Exec(c, null, "UPDATE jobs SET expired=1 WHERE id=$0", j);
                        Atomic.AppendLine(JobsLog, "X\t" + j);
                        expiredPoints++;
                        result.Add("expired", new Msg().Set("id", j));
                    }
                    var kept = keep.OrderBy(x => x, StringComparer.Ordinal).ToList();
                    foreach (var folder in Query(c, "SELECT removed, MIN(job) FROM objects WHERE removed IS NOT NULL GROUP BY removed").ToList())
                    {
                        string removed = (string)folder[0], minCreated = (string)folder[1];
                        bool needed = kept.Any(p => string.CompareOrdinal(p, minCreated) >= 0 && string.CompareOrdinal(p, removed) < 0);
                        if (needed) continue;
                        foreach (var r in Query(c, "SELECT loc,size FROM objects WHERE removed=$0", removed))
                        {
                            var p = Abs((string)r[0]);
                            if (File.Exists(p)) File.Delete(p);
                            if (File.Exists(p + ".chk")) File.Delete(p + ".chk");
                            deletedObjects++; freed += (long)r[1];
                        }
                        Exec(c, null, "DELETE FROM objects WHERE removed=$0", removed);
                        result.Add("folders", new Msg().Set("id", removed));
                        var dir = Path.Combine(Dir, removed);
                        if (Directory.Exists(dir) && !Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any()) Directory.Delete(dir, true);
                    }
                }
                return result.Set("expiredPoints", expiredPoints).Set("deletedObjects", deletedObjects).Set("freed", freed);
            }
        }
    }
}
