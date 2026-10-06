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
    public interface IRestoreSource
    {
        List<string> Points();
        Msg Files(string point);
        void Fetch(string loc, string toFile);
        void Report(Msg log);
    }

    public sealed class ServerSource : IRestoreSource
    {
        readonly Client client; readonly string setId; readonly bool test;
        public ServerSource(Client client, string setId, bool test = false) { this.client = client; this.setId = setId; this.test = test; }
        public List<string> Points() { return client.Call("GET", "/api/sets/" + setId + "/points").List("points").Select(p => p["id"]).ToList(); }
        public Msg Files(string point) { return client.Call("GET", "/api/sets/" + setId + "/files" + (point == null ? "" : "?point=" + Client.Url(point))); }
        public void Fetch(string loc, string toFile) { client.Download("/api/sets/" + setId + "/object?loc=" + Client.Url(loc) + (test ? "&test=1" : ""), toFile); }
        public void Report(Msg log) { if (!test) try { client.Call("POST", "/api/sets/" + setId + "/restorelog", log); } catch (AgentException) { } }
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

    public sealed class Restore
    {
        readonly IRestoreSource source;
        readonly KeySet key;
        readonly string temp;
        public int Restored, Failed, Skipped;
        public List<string> Log = new List<string>();

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

        public void Run(string point, string target, Func<string, bool> filter, bool overwrite)
        {
            var started = SystemClock.UtcNow;
            Log.Add(AhsayLog.Line(started, "start"));
            Log.Add(AhsayLog.Info(started, "Restore point " + (point ?? "latest") + " to " + (target ?? "original location")));
            foreach (var kv in Files(point))
            {
                if (filter != null && !filter(kv.Key)) continue;
                var dest = target == null ? kv.Key : Path.Combine(target, kv.Key.Replace(":", "").TrimStart('\\', '/').Replace('\\', Path.DirectorySeparatorChar));
                try
                {
                    if (File.Exists(dest) && !overwrite) { Skipped++; Log.Add(AhsayLog.Line(SystemClock.UtcNow, "info", dest, message: "exists, skipped")); continue; }
                    RestoreFile(kv.Value, dest);
                    Restored++;
                    Log.Add(AhsayLog.Line(SystemClock.UtcNow, "new", dest, 0, kv.Value.Long("mtime"), kv.Value.Long("orig")));
                }
                catch (Exception e)
                {
                    Failed++;
                    Log.Add(AhsayLog.Line(SystemClock.UtcNow, "err", dest, message: e.Message));
                }
            }
            // Verification: the outcome is computed here and SENT (the server used to take "OK" when there was none);
            // files skipped because they exist are said, never a plain success
            Result = Failed > 0 ? "RESTORE_STOP_WITH_ERROR" : Skipped > 0 ? "RESTORE_STOP_WITH_WARNING" : "RESTORE_STOP_SUCCESS";
            if (Skipped > 0) Log.Add(AhsayLog.Line(SystemClock.UtcNow, "warn", message: Skipped + " files were not restored because a file with the same name exists (choose overwrite to replace them)"));
            Log.Add(AhsayLog.Line(SystemClock.UtcNow, "end", message: Result));
            var m = new Msg().Set("result", Result).Set("restored", Restored).Set("failed", Failed).Set("skipped", Skipped);
            foreach (var l in Log) m.Add("log", new Msg().Set("l", l));
            source.Report(m);
        }

        public void RestoreFile(Msg file, string dest)
        {
            if (file.Bool("damaged"))
                throw new InvalidDataException("This version was found damaged on the server and quarantined; it cannot be restored. Restore an earlier point — the next backup stores the file again.");
            var objects = file.List("objects").OrderBy(o => o.Int("seq")).ToList();
            var paths = new List<string>();
            try
            {
                foreach (var o in objects)
                {
                    var p = Path.Combine(temp, Guid.NewGuid().ToString("N") + ".obj");
                    source.Fetch(o["loc"], p);
                    using (var fs = File.OpenRead(p))
                        if (Bytes.Sha256Hex(fs) != o["sha"]) throw new InvalidDataException("Downloaded object does not match the server checksum.");
                    paths.Add(p);
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
                    var tmp = dest + ".restoring";
                    long total = 0;
                    // Bug 30: a chunk that failed its check (or a full disk) in the middle left "<name>.restoring" in the
                    // customer's folder — any failure while writing removes the half file
                    try
                    {
                        using (var outp = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                            foreach (var r in header.List("recipe"))
                            {
                                KeyValuePair<int, long> loc;
                                if (!where.TryGetValue(r["h"], out loc)) throw new InvalidDataException("A chunk of the file is missing from the backup chain.");
                                var chunk = BackupObject.ReadChunkAt(streams[loc.Key], loc.Value, key, r["h"]);
                                outp.Write(chunk, 0, chunk.Length);
                                total += chunk.Length;
                            }
                        if (total != header.Long("size")) throw new InvalidDataException("Restored size differs from the backed-up size.");
                    }
                    catch (Exception) { try { File.Delete(tmp); } catch (IOException) { } throw; }
                    if (File.Exists(dest)) File.Delete(dest);
                    File.Move(tmp, dest);
                    File.SetLastWriteTimeUtc(dest, RunId.FromUnixMs(header.Long("mtime")));
                }
                finally { foreach (var s in streams) s.Dispose(); }
            }
            finally { foreach (var p in paths) try { File.Delete(p); } catch (IOException) { } }
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
