using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using OnlineBackup.Core;

namespace OnlineBackup.Agent
{
    /// <summary>
    /// Restore runs on the agent — only it has the key. For every file of the chosen point: download its chain
    /// (full + deltas), rebuild the file chunk by chunk (each chunk authenticated), check the size, write to a
    /// temporary name and rename, then set the file time. Requires an interactive sign-in (password + 2FA).
    /// </summary>
    /// <summary>Where a restore reads from: the backup server, or the local copy (no internet needed).</summary>
    /// <summary>M-2: a source that can fetch the exact version a run made, wherever a later commit moved it.</summary>
    public interface IVersionedRestoreSource { void Fetch(string loc, string job, string toFile); }

    public interface IRestoreSource
    {
        List<string> Points();
        Msg Files(string point);
        void Fetch(string loc, string toFile);
        void Report(Msg log);
    }

    public sealed class ServerSource : IRestoreSource, IVersionedRestoreSource
    {
        public void Fetch(string loc, string job, string toFile) { client.Download("/api/sets/" + setId + "/object?loc=" + Client.Url(loc) + "&job=" + Client.Url(job) + (test ? "&test=1" : ""), toFile); }
        readonly Client client; readonly string setId; readonly bool test;
        public ServerSource(Client client, string setId, bool test = false) { this.client = client; this.setId = setId; this.test = test; }
        public List<string> Points() { return client.Call("GET", "/api/sets/" + setId + "/points").List("points").Select(p => p["id"]).ToList(); }
        public Msg Files(string point) { return client.Call("GET", "/api/sets/" + setId + "/files" + (point == null ? "" : "?point=" + Client.Url(point))); }
        public void Fetch(string loc, string toFile) { client.Download("/api/sets/" + setId + "/object?loc=" + Client.Url(loc) + (test ? "&test=1" : ""), toFile); }
        public void Report(Msg log) { if (!test) client.Call("POST", "/api/sets/" + setId + "/restorelog", log); }
    }

    public sealed class LocalSource : IRestoreSource
    {
        readonly LocalRepo repo;
        public LocalSource(LocalRepo repo) { this.repo = repo; }
        public List<string> Points() { return repo.Points(); }
        public Msg Files(string point) { return repo.FilesAt(point); }
        public void Fetch(string loc, string toFile) { File.Copy(repo.ObjectPath(loc), toFile, true); }
        public void Report(Msg log) { Atomic.WriteText(Path.Combine(repo.Dir, "restore-" + RunId.From(SystemClock.UtcNow) + ".log"), string.Join("\r\n", log.List("log").Select(l => l["l"]).ToArray())); }
    }

    public enum ExistingFiles { Ask, Skip, Overwrite }

    public sealed class Restore
    {
        readonly IRestoreSource source;
        readonly KeySet key;
        readonly string temp;
        public int Restored, Failed, Skipped;
        /// <summary>D-6: why the server did not get this restore's record (null = it did). It used to be swallowed: a restore
        /// that failed left no failed record anywhere but on this computer's screen.</summary>
        public string ReportError;
        public List<string> Log = new List<string>();
        /// <summary>UI-Q1: files whose content on the disk was checked after writing and matched the backup — of them, the files
        /// checked by the whole-file SHA-256 the backup recorded (backups from this version on); the others (older backups,
        /// no whole-file SHA-256 recorded) were checked by every chunk's keyed SHA-256 id (HMAC-SHA-256) and the size.
        /// Counted only when the check really ran.</summary>
        public int Verified, VerifiedSha256;
        /// <summary>UI-Q1: files whose content did not match the backup — named; not put in place; counted in Failed.</summary>
        public List<string> Mismatched = new List<string>();
        /// <summary>UI-Q2: the files already at the destination when a restore without a decision (ExistingFiles.Ask) refused to start.</summary>
        public List<string> Existing = new List<string>();
        /// <summary>Fault injection (tests): runs on the written temporary file before it is checked — e.g. a disk or a filter
        /// driver that changes it. Never set by the product.</summary>
        public Action<string> AfterWrite;

        public sealed class ConflictList { public int Total; public List<string> Existing = new List<string>(); }

        public Restore(Client client, BackupSetInfo set, KeySet key, string tempDir) : this(new ServerSource(client, set.Id), key, tempDir) { }

        public Restore(IRestoreSource source, KeySet key, string tempDir)
        {
            this.source = source; this.key = key; temp = tempDir;
            Directory.CreateDirectory(temp);
        }

        public List<string> Points() { return source.Points(); }

        /// <summary>The file list of a point, with real names (decrypted here).</summary>
        public List<KeyValuePair<string, Msg>> Files(string point)
        {
            return source.Files(point).List("files").Select(f => new KeyValuePair<string, Msg>(NameCipher.DecryptPath(key, f["enc"]), f)).ToList();
        }

        /// <param name="target">null = original location; otherwise a folder (C:\Data\a.txt → target\C\Data\a.txt).</param>
        /// <param name="overwrite">replace existing files (otherwise existing files are skipped).</param>
        /// <summary>RESTORE_STOP_SUCCESS / RESTORE_STOP_WITH_WARNING (files skipped) / RESTORE_STOP_WITH_ERROR (files failed).</summary>
        public string Result;

        static string DestOf(string path, string target)
        {
            return target == null ? path : Path.Combine(target, path.Replace(":", "").TrimStart('\\', '/').Replace('\\', Path.DirectorySeparatorChar));
        }

        /// <summary>UI-Q2: before a restore — how many files it would write and which of them already exist at the destination
        /// (null target = the original location).</summary>
        public ConflictList Conflicts(string point, string target, Func<string, bool> filter)
        {
            var c = new ConflictList();
            foreach (var kv in Files(point))
            {
                if (filter != null && !filter(kv.Key)) continue;
                c.Total++;
                var dest = DestOf(kv.Key, target);
                if (File.Exists(dest)) c.Existing.Add(dest);
            }
            return c;
        }

        /// <summary>Existing callers: true = replace existing files, false = keep them (said in the result, WITH_WARNING).</summary>
        public void Run(string point, string target, Func<string, bool> filter, bool overwrite)
        {
            Run(point, target, filter, overwrite ? ExistingFiles.Overwrite : ExistingFiles.Skip);
        }

        /// <summary>UI-Q2: <paramref name="existing"/> = the caller's decision for files already at the destination. Ask (no
        /// decision): when any exists nothing is written, <see cref="Existing"/> names them and an AgentException 409 EXISTS
        /// is thrown — no restore record is sent (no restore ran).</summary>
        public void Run(string point, string target, Func<string, bool> filter, ExistingFiles existing)
        {
            var files = Files(point).Where(kv => filter == null || filter(kv.Key)).ToList();
            if (existing == ExistingFiles.Ask)
            {
                Existing = files.Select(kv => DestOf(kv.Key, target)).Where(File.Exists).ToList();
                if (Existing.Count > 0)
                    throw new AgentException(409, "EXISTS", Existing.Count.ToString(CultureInfo.InvariantCulture) + " of the " + files.Count.ToString(CultureInfo.InvariantCulture)
                        + " files already exist at " + (target ?? "the original location") + " — choose to overwrite them, to skip them, or cancel. Nothing was restored.");
            }
            bool overwrite = existing == ExistingFiles.Overwrite;
            var started = SystemClock.UtcNow;
            Log.Add(AhsayLog.Line(started, "start"));
            Log.Add(AhsayLog.Info(started, "Restore point " + (point ?? "latest") + " to " + (target ?? "original location") + (overwrite ? " (existing files are replaced)" : "")));
            foreach (var kv in files)
            {
                var dest = DestOf(kv.Key, target);
                try
                {
                    // Ask: a file that appeared after the check above is kept, never replaced without a decision
                    if (File.Exists(dest) && !overwrite) { Skipped++; Log.Add(AhsayLog.Line(SystemClock.UtcNow, "info", dest, message: "exists, skipped")); continue; }
                    var how = RestoreFile(kv.Value, dest);
                    Restored++; Verified++; if (how == "sha256") VerifiedSha256++;
                    Log.Add(AhsayLog.Line(SystemClock.UtcNow, "new", dest, 0, kv.Value.Long("mtime"), kv.Value.Long("orig")));
                }
                catch (Exception e)
                {
                    Failed++;
                    if (e.Data.Contains(IntegrityKey)) Mismatched.Add(dest);
                    Log.Add(AhsayLog.Line(SystemClock.UtcNow, "err", dest, message: e.Message));
                }
            }
            // Verification: the outcome is computed here and SENT (the server used to take "OK" when there was none);
            // files skipped because they exist are said, never a plain success
            Result = Failed > 0 ? "RESTORE_STOP_WITH_ERROR" : Skipped > 0 ? "RESTORE_STOP_WITH_WARNING" : "RESTORE_STOP_SUCCESS";
            if (Skipped > 0) Log.Add(AhsayLog.Line(SystemClock.UtcNow, "warn", message: Skipped + " files were not restored because a file with the same name exists (choose overwrite to replace them)"));
            // UI-Q1: what was checked, and how — never "verified" for a file whose check did not run
            Log.Add(AhsayLog.Line(SystemClock.UtcNow, Mismatched.Count > 0 ? "err" : "info", message: "Integrity check after restore: " + Verified + " files verified ("
                + VerifiedSha256 + " by whole-file SHA-256, " + (Verified - VerifiedSha256) + " by chunk HMAC-SHA-256 and size — backed up before whole-file SHA-256 was recorded)"
                + (Mismatched.Count > 0 ? "; " + Mismatched.Count + " did NOT match the backup and were not put in place: " + string.Join(", ", Mismatched.Take(20).ToArray()) : "")));
            Log.Add(AhsayLog.Line(SystemClock.UtcNow, "end", message: Result));
            var m = new Msg().Set("result", Result).Set("restored", Restored).Set("failed", Failed).Set("skipped", Skipped)
                .Set("verified", Verified).Set("verifiedSha256", VerifiedSha256).Set("mismatched", Mismatched.Count);
            foreach (var l in Log) m.Add("log", new Msg().Set("l", l));
            try { source.Report(m); }
            catch (AgentException e) { ReportError = e.Message; }
        }

        public string RestoreFile(Msg file, string dest)
        {
            if (file.Bool("damaged"))
                throw new InvalidDataException("This version was found damaged on the server and quarantined; it cannot be restored. Restore an earlier point — the next backup stores the file again.");
            var objects = file.List("objects").OrderBy(o => o.Int("seq")).ToList();
            var paths = new List<string>();
            string how = null;
            try
            {
                foreach (var o in objects)
                {
                    var p = Path.Combine(temp, Guid.NewGuid().ToString("N") + ".obj");
                    paths.Add(p);   // Agent N (N-3): kept for the clean-up before the download — a failed one left an empty file
                    var versioned = source as IVersionedRestoreSource;
                    if (versioned != null && !string.IsNullOrEmpty(o["job"])) versioned.Fetch(o["loc"], o["job"], p); else source.Fetch(o["loc"], p);
                    using (var fs = File.OpenRead(p))
                        if (Bytes.Sha256Hex(fs) != o["sha"]) throw new InvalidDataException("Downloaded object does not match the server checksum.");
                }
                // Map every chunk id → (object, offset) over the chain; the recipe of the last object is the file.
                var streams = paths.Select(p => (Stream)File.OpenRead(p)).ToList();
                try
                {
                    var where = new Dictionary<string, KeyValuePair<int, long>>();
                    Msg header = null;
                    for (int i = 0; i < streams.Count; i++)
                    {
                        var h = BackupObject.ReadHeader(streams[i], key);
                        foreach (var kv in BackupObject.ChunkOffsets(streams[i], h)) where[kv.Key] = new KeyValuePair<int, long>(i, kv.Value);
                        header = h;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dest)));
                    // Bug 74 (RS-02, Agent B note): "<name>.restoring" with FileMode.Create overwrote, then moved away, a customer's
                    // own file of exactly that name. A name that cannot exist yet, created new (never over a file)
                    var tmp = dest + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".ob-restoring";
                    // Bug 78: a restore killed while writing leaves its own temporary file; the next restore of the file removes
                    // the product's leftovers (exactly "<name>.<8 hex>.ob-restoring"), never anything else
                    var folder = Path.GetDirectoryName(Path.GetFullPath(dest)); var mine = new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(Path.GetFileName(dest)) + "\\.[0-9a-f]{8}\\.ob-restoring$");
                    foreach (var old in Directory.GetFiles(folder, Path.GetFileName(dest) + ".*.ob-restoring"))
                        if (mine.IsMatch(Path.GetFileName(old))) try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    long total = 0;
                    // Bug 30: a chunk that failed its check (or a full disk) in the middle left "<name>.restoring" in the
                    // customer's folder — any failure while writing removes the half file
                    try
                    {
                        using (var outp = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write))
                            foreach (var r in header.List("recipe"))
                            {
                                KeyValuePair<int, long> loc;
                                if (!where.TryGetValue(r["h"], out loc)) throw new InvalidDataException("A chunk of the file is missing from the backup chain.");
                                var chunk = BackupObject.ReadChunkAt(streams[loc.Key], loc.Value, key, r["h"]);
                                outp.Write(chunk, 0, chunk.Length);
                                total += chunk.Length;
                            }
                        if (total != header.Long("size")) throw new InvalidDataException("Restored size differs from the backed-up size.");
                        if (AfterWrite != null) AfterWrite(tmp);
                        how = VerifyOnDisk(tmp, header);   // UI-Q1: before the file is put in place — a bad copy never replaces a file
                    }
                    catch (Exception) { try { File.Delete(tmp); } catch (IOException) { } throw; }
                    if (File.Exists(dest)) File.Delete(dest);
                    File.Move(tmp, dest);
                    File.SetLastWriteTimeUtc(dest, RunId.FromUnixMs(header.Long("mtime")));
                }
                finally { foreach (var s in streams) s.Dispose(); }
            }
            finally { foreach (var p in paths) try { File.Delete(p); } catch (IOException) { } }
            return how;
        }

        const string IntegrityKey = "ob-integrity";

        static InvalidDataException Mismatch(string why)
        {
            var e = new InvalidDataException("The restored file failed its integrity check (" + why + ") — it was not put in place.");
            e.Data[IntegrityKey] = true;
            return e;
        }

        /// <summary>UI-Q1: the written file is read back from the disk and checked against what the backup recorded: every chunk's
        /// keyed id (HMAC-SHA-256 of the chunk) in order, the size, and — when the backup recorded it ("fsha", from this version
        /// on) — the SHA-256 of the whole file. Returns "sha256" or "chunks" (how it was checked); throws on any difference.</summary>
        string VerifyOnDisk(string file, Msg header)
        {
            var fsha = header["fsha"];
            long total = 0;
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                foreach (var r in header.List("recipe"))
                {
                    int n = r.Int("n", -1);
                    if (n < 0) throw Mismatch("the backup's chunk list is damaged");
                    var buf = new byte[n]; int got = 0;
                    while (got < n) { int k = fs.Read(buf, got, n - got); if (k <= 0) break; got += k; }
                    if (got != n) throw Mismatch("the file on the disk is shorter than the backup");
                    if (BackupObject.ChunkId(key, buf) != r["h"]) throw Mismatch("the content at byte " + total.ToString(CultureInfo.InvariantCulture) + " differs from the backup");
                    sha.TransformBlock(buf, 0, n, null, 0);
                    total += n;
                }
                if (fs.ReadByte() != -1) throw Mismatch("the file on the disk is longer than the backup");
                if (total != header.Long("size")) throw Mismatch("size differs from the backup");
                sha.TransformFinalBlock(new byte[0], 0, 0);
                if (fsha != null && !string.Equals(Bytes.Hex(sha.Hash), fsha, StringComparison.OrdinalIgnoreCase)) throw Mismatch("SHA-256 differs from the one the backup recorded");
            }
            return fsha != null ? "sha256" : "chunks";
        }

        /// <summary>
        /// The local index was lost (new disk, reinstall): rebuilt from the server's latest point, so the next run sends
        /// only real changes. Chunk lists are not rebuilt; a large file that changes is sent once as a full copy.
        /// </summary>
        public static void RebuildLocalState(Client client, BackupSetInfo set, KeySet key, LocalState state, Action<string> info)
        {
            Msg m;
            try { m = client.Call("GET", "/api/sets/" + set.Id + "/files"); } catch (AgentException) { return; }
            foreach (var f in m.List("files"))
            {
                string path;
                if (f.Bool("damaged")) continue;   // B-4: not on the server: left out of the index, so it is sent again
                try { path = NameCipher.DecryptPath(key, f["enc"]); } catch (Exception) { continue; }
                var objs = f.List("objects");
                state.Files[f["rel"]] = new LocalState.Entry
                {
                    Rel = f["rel"], Path = path, Size = f.Long("orig"), Mtime = f.Long("mtime"), Attrs = "?",
                    Seq = objs.Count == 0 ? 0 : objs.Max(o => o.Int("seq"))
                };
            }
            if (state.Files.Count > 0) info("Local index rebuilt from the server: " + state.Files.Count.ToString(CultureInfo.InvariantCulture) + " files");
        }
    }
}
