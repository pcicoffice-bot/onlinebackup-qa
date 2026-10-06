using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using OnlineBackup.Core;

namespace OnlineBackup.Agent
{
    /// <summary>
    /// One backup run of one set:
    ///   pre-commands → VSS snapshot → begin → scan + upload (new / updated / permission-only / deleted) → commit →
    ///   post-commands (always) → snapshot removed. The local index is saved only after the server confirms the
    ///   commit, so an interrupted run is simply repeated — never half-recorded.
    /// </summary>
    public sealed class BackupRun
    {
        readonly Client client;
        readonly AgentHome home;
        readonly BackupSetInfo set;
        readonly KeySet key;
        readonly List<string> log = new List<string>();
        public int New, Updated, PermOnly, Deleted, Errors, Warnings;
        public long BytesSent;
        public string Job { get; private set; }
        public string Result { get; private set; }
        /// <summary>FULL (files / SQL complete backup) or LOG (SQL transaction logs only).</summary>
        public string Mode = "FULL";
        LocalRepo local;
        readonly Dictionary<string, int> extensions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public Func<DateTime> Clock = () => SystemClock.UtcNow;
        /// <summary>SET-030: "stop" from the admin site (asked every few files; the agent throttles the check).</summary>
        public Func<bool> StopRequested;
        Throttle throttle = new Throttle(0);

        public BackupRun(Client client, AgentHome home, BackupSetInfo set, KeySet key)
        {
            this.client = client; this.home = home; this.set = set; this.key = key;
        }

        void Log(string type, string path = "", long size = 0, long mtime = 0, long orig = 0, string message = null)
        {
            log.Add(AhsayLog.Line(Clock(), type, path, size, mtime, orig, message));
        }
        void Info(string m) { Log("info", message: m); }
        void Warn(string m) { Warnings++; Log("warn", message: m); }
        void Err(string path, string m) { Errors++; Log("err", path, message: m); }

        long lastFiles, lastBytes; string lastCurrent = ""; int lastPercent = -1;
        /// <summary>R1 (found by QA F1): the server confirmed the end of this run (commit or abort). Until then the
        /// open-run note stays, so a run whose end never reached the server (server down, line cut) is released by the next one.</summary>
        bool endConfirmed;
        System.Threading.Timer heartbeat;

        /// <summary>
        /// R1: the run's sign of life every minute, whatever it is doing (a long SQL dump, a big file, a slow prune): the
        /// server keeps the run open only while it hears from it, and closes it as interrupted when the computer dies.
        /// </summary>
        internal void StartHeartbeat()
        {
            heartbeat = new System.Threading.Timer(_ => SendProgress(lastFiles, lastBytes, lastCurrent, lastPercent), null, 60000, 60000);
        }
        internal void StopHeartbeat() { var h = heartbeat; heartbeat = null; if (h != null) h.Dispose(); }

        /// <summary>R1: the note "a run of this set is going on" on this computer — left behind only when the process dies.</summary>
        internal static string MarkerPath(AgentHome home, string setId) { return Path.Combine(home.Dir, "sets", setId, "open-run.txt"); }
        internal static void WriteMarker(AgentHome home, string setId, string job, DateTime started)
        {
            var me = System.Diagnostics.Process.GetCurrentProcess();
            home.SetDir(setId);
            File.WriteAllText(MarkerPath(home, setId), job + "\t" + RunId.UnixMs(started) + "\t" + me.Id + "\t" + RunId.UnixMs(me.StartTime.ToUniversalTime()));
        }
        internal static void ClearMarker(AgentHome home, string setId) { try { File.Delete(MarkerPath(home, setId)); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

        /// <summary>LIVE-010: "running now" for the admin site — files and bytes so far, the current file; never stops the backup.</summary>
        internal void Progress(long files, long bytes, string current, int percent = -1)
        {
            lastFiles = files; lastBytes = bytes; lastCurrent = current ?? ""; lastPercent = percent;
            SendProgress(files, bytes, current, percent);
        }

        void SendProgress(long files, long bytes, string current, int percent)
        {
            try { client.Call("POST", "/api/sets/" + set.Id + "/progress", new Msg().Set("job", Job).Set("files", files).Set("bytes", bytes).Set("current", current).Set("percent", percent >= 0 ? (object)percent : null)); } catch (Exception) { }
        }

        public IList<string> LogLines { get { return log; } }
        internal List<string> Lines { get { return log; } }
        /// <summary>For runs of another engine (restic): the final result and run id.</summary>
        internal void Finished(string result, string job) { Result = result; Job = job; }
        internal void EndConfirmed() { endConfirmed = true; }

        // Bug 20: this run's own name for "begin" — when the answer is lost and the client sends it again, the server gives the
        // same run back instead of "another backup is running"
        readonly string beginKey = Guid.NewGuid().ToString("N");

        public string Run()
        {
            var started = Clock();
            Log("start");
            Info("Start [ " + Environment.OSVersion.VersionString + " (" + home.Computer + "), OnlineBackup Agent " + typeof(BackupRun).Assembly.GetName().Version + " ]");
            var tmp = string.IsNullOrEmpty(set.WorkingDir) ? Path.Combine(home.Dir, "temp") : set.WorkingDir;
            Directory.CreateDirectory(tmp);
            Info("Using Temporary Directory " + tmp);
            Vss snapshot = null;
            string endCode = "BS_STOP_SUCCESS";
            try
            {
                if (!Commands.Run(set.PreCommands, "pre", Info, Warn) && set.StopOnPreCommandFailure)
                {
                    endCode = "BS_STOP_BY_PRE_COMMAND";
                    Err("", "Pre-command failed: backup stopped by the set settings");
                    var stopped = Finish(endCode, null);
                    // TIME-020 found it: a run stopped before it began must reach the server too — else no call, no alert,
                    // nothing in the tasks until "no backup for 2 days"
                    try { Job = client.Call("POST", "/api/sets/" + set.Id + "/begin?key=" + beginKey)["job"]; client.Call("POST", "/api/sets/" + set.Id + "/jobs/" + Job + "/abort", LogMsg()); } catch (AgentException) { }
                    return stopped;
                }
                if (set.Type == Domino.Type) Domino.Prepare(Info, Warn);
                if (set.Vss && Vss.Supported && (set.Type == "FILE" || set.Type == Domino.Type))   // the image tool takes its own snapshot
                {
                    Info("Start creating Shadow Copy Set ...");
                    snapshot = Vss.Create(set.Sources, Warn);
                    if (snapshot != null) Info("Shadow Copy Set successfully created");
                }
                else if (set.Vss && Environment.OSVersion.Platform == PlatformID.Win32NT) Warn("Shadow Copy is not available here: open files may be skipped");
                else if (set.Vss) Info("Shadow Copy does not apply on this platform");

                Msg begin;
                try { begin = client.Call("POST", "/api/sets/" + set.Id + "/begin?key=" + beginKey); }
                catch (AgentException e)
                {
                    endCode = e.Code == "QUOTA" ? "BS_STOP_QUOTA_EXCEEDED" : "BS_STOP_BY_SYSTEM_ERROR";
                    Err("", e.Message);
                    return Finish(endCode, null);
                }
                Job = begin["job"];
                WriteMarker(home, set.Id, Job, started);
                StartHeartbeat();
                if (begin.Bool("quotaWarning")) Warn("Quota almost full: " + begin["used"] + " / " + begin["quota"] + " bytes");
                var resend = new HashSet<string>(begin.List("resend").Select(m => m["rel"]));

                var state = new LocalState(home.SetDir(set.Id));
                if (!state.Exists) Restore.RebuildLocalState(client, set, key, state, Info);
                else if (!string.IsNullOrEmpty(begin["last"]) && begin["last"] != state.LastSuccess)
                {
                    // Agent B / D (B-3, D-3): the index is not the server's last backup — this computer stopped between the
                    // server's commit and its own save, or another copy of it (a cloned disk, a program folder put back)
                    // backed up the set. Trusted as it was, changes went as deltas the server refused, or files that differ
                    // from the other copy's were "unchanged": a success whose newest point did not hold this computer's files
                    Warn("This computer's index of the set is not the server's last backup (" + (string.IsNullOrEmpty(state.LastSuccess) ? "none" : state.LastSuccess) + " / " + begin["last"]
                        + "): another copy of this computer backed it up, or the last run was not recorded here. The index is rebuilt from the server and every difference is sent.");
                    state.Forget();
                    Restore.RebuildLocalState(client, set, key, state, Info);
                }
                var pendingChunks = new Dictionary<string, List<KeyValuePair<string, int>>>();
                var next = new Dictionary<string, LocalState.Entry>(StringComparer.Ordinal);
                var deadline = set.DurationHours > 0 ? started.AddHours(set.DurationHours) : DateTime.MaxValue;
                bool stoppedByDuration = false, stoppedByAdmin = false;

                if (set.LocalCopy)
                    try { local = new LocalRepo(set.LocalCopyPath, home.Login, set.Id); local.Begin(Job); Info("[Local Copy] to " + local.Dir); }
                    catch (Exception e) { Warn("[Local Copy] not available: " + e.Message); local = null; }
                var unreachable = new List<string>();
                var sqlPassword = home.LoadSecret(set.Id + "-sql");
                Sources.CurrentHome = home;
                throttle = new Throttle(set.BandwidthKbps);
                if (throttle.On) Info("Upload limit " + set.BandwidthKbps + " KB/s");
                DateTime nextBusyCheck = DateTime.MinValue, nextProgress = DateTime.MinValue;
                int seenItems = 0;
                foreach (var item in Sources.Items(set, Mode, snapshot, tmp, sqlPassword, Info, Warn, unreachable))
                {
                    seenItems++;
                    if (set.BusyCpuPercent > 0 && Clock() >= nextBusyCheck) { Resources.WaitWhileBusy(set.BusyCpuPercent, Info, StopRequested); nextBusyCheck = Clock().AddMinutes(1); }
                    if (Clock() > deadline) { stoppedByDuration = true; break; }
                    if (StopRequested != null && StopRequested()) { stoppedByAdmin = true; break; }
                    if (Clock() >= nextProgress) { nextProgress = Clock().AddSeconds(30); Progress(New + Updated, BytesSent, item.Path); }
                    var file = item.Info;
                    var rel = NameCipher.RelPath(key, item.Path);
                    LocalState.Entry old;
                    state.Files.TryGetValue(rel, out old);
                    var src = item.ReadPath;
                    long size, mtime; string attrs;
                    try { size = file.Length; mtime = RunId.UnixMs(file.LastWriteTimeUtc); attrs = Scanner.Attributes(file, set.BackupPermissions); }
                    catch (Exception e) { Err(item.Path, "Cannot read file information: " + e.Message); if (old != null) next[rel] = old; continue; }
                    var entry = new LocalState.Entry { Rel = rel, Path = item.Path, Size = size, Mtime = mtime, Attrs = attrs };
                    bool force = resend.Contains(rel);
                    try
                    {
                        if (old == null || force || old.Size != size || old.Mtime != mtime) CountExtension(item.Path);
                        if (old == null || force)
                        {
                            var sent = SendFull(src, item.Path, rel, entry, pendingChunks, false);
                            Log(old == null ? "new" : "upd", item.Path, sent, mtime, size);
                            if (old == null) New++; else Updated++;
                        }
                        else if (old.Size != size || old.Mtime != mtime)
                        {
                            long sent = SendChange(src, item.Path, rel, old, entry, state, pendingChunks, false);
                            Log("upd", item.Path, sent, mtime, size);
                            Updated++;
                        }
                        else if (old.Attrs != attrs && old.Attrs != "?")   // "?" = index rebuilt from the server: adopt, don't resend
                        {
                            long sent = SendChange(src, item.Path, rel, old, entry, state, pendingChunks, true);
                            Log("perm", item.Path, sent, mtime, size);
                            PermOnly++;
                        }
                        else { entry.Seq = old.Seq; entry.DeltaBytes = old.DeltaBytes; }
                        next[rel] = entry;
                    }
                    catch (AgentException e) when (e.Code == "QUOTA")
                    {
                        Err(item.Path, e.Message);
                        endCode = "BS_STOP_QUOTA_EXCEEDED";
                        if (old != null) next[rel] = old;
                        break;
                    }
                    catch (AgentException e) when (e.Code == "NETWORK" || e.Code == "NO_JOB")
                    {
                        // The connection is gone: stop and let the scheduler run the backup again when it is back.
                        Err(item.Path, e.Message);
                        client.Call("POST", "/api/sets/" + set.Id + "/jobs/" + Job + "/abort", LogMsg()).ToString();
                        return Finish("BS_STOP_BY_SYSTEM_ERROR", null);
                    }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                    {
                        Err(item.Path, "Cannot read file: " + e.Message);
                        if (old != null) next[rel] = old;
                    }
                }

                // R1 (GPT audit 4): what should have been backed up but could not be read is an error, never a warning —
                // and when nothing at all could be read, the run failed (its earlier backups are kept, nothing is "deleted")
                var missing = unreachable.Where(u => !u.StartsWith(Sources.KeepOnly, StringComparison.Ordinal)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                for (int i = 0; i < unreachable.Count; i++) if (unreachable[i].StartsWith(Sources.KeepOnly, StringComparison.Ordinal)) unreachable[i] = unreachable[i].Substring(Sources.KeepOnly.Length);
                foreach (var u in missing) Err(u, "Not backed up: this source could not be read now (its earlier backups are kept)");
                // a database, system-state, image or VM set always has something to send: none at all is a failure too
                bool appSet = !string.IsNullOrEmpty(set.Type) && set.Type != "FILE" && Mode != "LOG";
                if ((missing.Count > 0 || appSet) && seenItems == 0 && !stoppedByAdmin && !stoppedByDuration && endCode == "BS_STOP_SUCCESS")
                {
                    endCode = "BS_STOP_BY_SYSTEM_ERROR";
                    Err("", appSet && missing.Count == 0 ? "Nothing was backed up: no database or data to back up was found" : "Nothing was backed up: none of the selected sources could be read");
                }

                // Files that disappeared from the selection: moved to the retention area on the server (never erased).
                // A log-only run sees only the new .trn files: nothing else is "gone".
                if (Mode == "LOG") { foreach (var e in state.Files.Values) if (!next.ContainsKey(e.Rel)) next[e.Rel] = e; }
                else if (!stoppedByDuration && !stoppedByAdmin && endCode != "BS_STOP_QUOTA_EXCEEDED")
                {
                    // A drive, share or folder that is offline or unreadable right now is not a deletion: keep its files as they are.
                    foreach (var e in state.Files.Values)
                        if (!next.ContainsKey(e.Rel) && unreachable.Any(u => Under(e.Path, u)))
                            next[e.Rel] = e;
                    var gone = state.Files.Values.Where(e => !next.ContainsKey(e.Rel)).ToList();
                    if (gone.Count > 0)
                    {
                        var del = new Msg();
                        foreach (var g in gone) { del.Add("rels", new Msg().Set("rel", g.Rel)); Log("del", g.Path, 0, g.Mtime, g.Size); Deleted++; if (local != null) local.Delete(g.Rel); }
                        client.Call("POST", "/api/sets/" + set.Id + "/jobs/" + Job + "/delete", del);
                    }
                }
                else foreach (var e in state.Files.Values) if (!next.ContainsKey(e.Rel)) next[e.Rel] = e;   // not scanned: keep as is
                if (stoppedByAdmin) Info("Backup stopped from the admin site; what was sent is kept and the rest continues in the next run");
                if (stoppedByDuration) Warn("Backup stopped after the maximum duration of " + set.DurationHours + " hours; the rest continues in the next run");

                if (stoppedByAdmin && endCode == "BS_STOP_SUCCESS") endCode = "BS_STOP_BY_USER";
                if (endCode == "BS_STOP_SUCCESS") endCode = Errors > 0 ? "BS_STOP_SUCCESS_WITH_ERROR" : Warnings > 0 ? "BS_STOP_SUCCESS_WITH_WARNING" : "BS_STOP_SUCCESS";
                Info("Total New Files = " + New);
                Info("Total Updated Files = " + Updated);
                Info("Total Permission Updated Files = " + PermOnly);
                Info("Total Deleted Files = " + Deleted);
                var result = Finish(endCode, started);
                var commit = new Msg().Set("new", New).Set("upd", Updated).Set("perm", PermOnly).Set("del", Deleted).Set("bytes", BytesSent).Set("started", RunId.UnixMs(started))
                    .Set("prevFiles", state.Files.Count).Set("result", endCode);
                // For the mass-change ("ransomware") check on the server: the most common extension among changed files.
                if (extensions.Count > 0) { var top = extensions.OrderByDescending(kv => kv.Value).First(); commit.Set("topExt", top.Key).Set("topExtCount", top.Value); }
                foreach (var l in log) commit.Add("log", new Msg().Set("l", l));
                var ok = client.Call("POST", "/api/sets/" + set.Id + "/jobs/" + Job + "/commit", commit);
                endConfirmed = true;
                if (ok.Int("refused") > 0)
                {
                    Err("", ok.Int("refused") + " changed file(s) were not stored by the server (their earlier version is missing); they are sent in full in the next backup");
                    if (result.StartsWith("BS_STOP_SUCCESS", StringComparison.Ordinal)) result = Result = "BS_STOP_SUCCESS_WITH_ERROR";
                }
                // Committed: now (and only now) the local index moves forward.
                state.Files = next;
                foreach (var kv in pendingChunks) state.SaveChunks(kv.Key, kv.Value);
                foreach (var kv in pendingBase) state.SaveBaseChunks(kv.Key, kv.Value);
                foreach (var e in next.Values.Where(e => e.Size < set.MinDeltaFileSize)) state.DropChunks(e.Rel);
                state.LastSuccess = Job;
                state.Save();
                if (local != null)
                    try
                    {
                        local.Commit(Job, commit);
                        var freed = local.ApplyRetention(set.LocalCopyDays, Clock());
                        Info("[Local Copy] completed" + (freed > 0 ? ", retention removed " + freed + " run folders" : ""));
                    }
                    catch (Exception e) { Warnings++; Info("[Local Copy] failed: " + e.Message); }
                if (ok.Long("bad") > 0) Warnings++;
                return result;
            }
            catch (Exception e)
            {
                // R1: any failure (not only the network: a full disk, a file error, a bug) closes the run on the server,
                // so it never stays open and blocking, and is counted as failed
                Err("", e is AgentException ? e.Message : e.GetType().Name + ": " + e.Message);
                // R1 (found by QA F6): a server that broke the upload off — ask it why, and say it in the log
                var why = ServerSideReason(client);
                if (why != null) Err("", why);
                Finish(why != null && why.StartsWith("Quota") ? "BS_STOP_QUOTA_EXCEEDED" : "BS_STOP_BY_SYSTEM_ERROR", null);
                if (Job != null) try { client.Call("POST", "/api/sets/" + set.Id + "/jobs/" + Job + "/abort", LogMsg()); endConfirmed = true; } catch (AgentException) { }
                if (local != null) try { local.Abort(); } catch (Exception) { }
                return Result;
            }
            finally
            {
                StopHeartbeat();
                if (Job != null && endConfirmed) ClearMarker(home, set.Id);
                Commands.Run(set.PostCommands, "post", Info, Warn);
                if (snapshot != null) { snapshot.Dispose(); Info("Deleting Shadow Copy snapshot"); }
                try { var dumps = Path.Combine(tmp, "mssql"); if (Directory.Exists(dumps)) Directory.Delete(dumps, true); } catch (Exception) { }
            }
        }

        /// <summary>The server's own view of a failed upload: its disk is full, or the customer's quota is used up (null: neither).</summary>
        internal static string ServerSideReason(Client client)
        {
            try
            {
                var q = client.Call("GET", "/api/quota");
                if (q.Int("diskFull") == 1) return "The backup server's disk is full: new backups cannot be stored. Existing backups are not affected — your provider must free space.";
                if (q.Int("quotaRefused") == 1 || (q.Long("quota") > 0 && q.Long("used") >= q.Long("quota"))) return "Quota exceeded (" + q["used"] + " of " + q["quota"] + " bytes): existing backups are kept, new data is not accepted.";
            }
            catch (Exception) { }
            return null;
        }

        void CountExtension(string path)
        {
            var ext = Path.GetExtension(path) ?? "";
            int n; extensions.TryGetValue(ext, out n); extensions[ext] = n + 1;
        }

        string Finish(string code, DateTime? started)
        {
            Info(code == "BS_STOP_SUCCESS" ? "Backup Completed Successfully" : "Backup finished: " + code);
            Log("end", message: code);
            Result = code;
            return code;
        }

        Msg LogMsg()
        {
            var m = new Msg();
            foreach (var l in log) m.Add("log", new Msg().Set("l", l));
            if (Result != null) m.Set("result", Result);   // the server counts the run by it (service calls, tasks, mails)
            return m;
        }

        // differential sets: the chunk lists of the new full copies of this run (kept once the run is committed)
        readonly Dictionary<string, List<KeyValuePair<string, int>>> pendingBase = new Dictionary<string, List<KeyValuePair<string, int>>>();

        /// <summary>New full copy (seq 0): every chunk of the file. The old chain moves to the retention area on the server.</summary>
        long SendFull(string src, string path, string rel, LocalState.Entry entry, Dictionary<string, List<KeyValuePair<string, int>>> pending, bool permOnly)
        {
            var chunks = new List<KeyValuePair<string, int>>();
            long sent = Upload(rel, 0, "F", path, entry, permOnly, src, null, chunks);
            entry.Seq = 0; entry.DeltaBytes = 0;
            if (entry.Size >= set.MinDeltaFileSize) { pending[rel] = chunks; if (set.DeltaType == "D") pendingBase[rel] = chunks; }
            return sent;
        }

        /// <summary>
        /// A changed file: below the delta threshold → a new full copy. Above it → a delta with only the chunks that are new
        /// (content-defined, so an insert in the middle costs one chunk), unless the chain is too long or the change is
        /// larger than MAX_DELTA_RATIO — then a new full copy (Ahsay 100 deltas / 50%).
        /// </summary>
        long SendChange(string src, string path, string rel, LocalState.Entry old, LocalState.Entry entry, LocalState state,
            Dictionary<string, List<KeyValuePair<string, int>>> pending, bool permOnly)
        {
            // incremental: against the last version (smallest); differential: against the last full copy (a restore needs the full + one delta)
            var prev = entry.Size >= set.MinDeltaFileSize || permOnly ? (set.DeltaType == "D" ? state.LoadBaseChunks(rel) : state.LoadChunks(rel)) : null;
            if (prev == null || old.Seq + 1 >= set.MaxDeltaNo || (entry.Size < set.MinDeltaFileSize && !permOnly))
                return SendFull(src, path, rel, entry, pending, permOnly);
            var known = new HashSet<string>(prev.Select(c => c.Key));
            // Dry pass: how much would the delta carry?
            long newBytes = 0;
            if (!permOnly)
                using (var fs = Open(src))
                    foreach (var c in Chunker.ForFileSize(entry.Size).Split(fs))
                        if (!known.Contains(BackupObject.ChunkId(key, c))) newBytes += c.Length;
            long carried = set.DeltaType == "D" ? newBytes : old.DeltaBytes + newBytes;
            if (entry.Size > 0 && carried * 100 / entry.Size > set.MaxDeltaRatio)
                return SendFull(src, path, rel, entry, pending, permOnly);
            var chunks = new List<KeyValuePair<string, int>>();
            int seq = old.Seq + 1;
            long sent = Upload(rel, seq, "D", path, entry, permOnly, src, known, chunks);
            entry.Seq = seq; entry.DeltaBytes = carried;
            pending[rel] = chunks;
            return sent;
        }

        /// <summary>Whether a backed-up path is inside an unreachable source (both separators: SQL / image names use '\\' everywhere).</summary>
        static bool Under(string path, string source)
        {
            var u = source.TrimEnd('\\', '/');
            return path.Equals(u, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(u + "\\", StringComparison.OrdinalIgnoreCase) || path.StartsWith(u + "/", StringComparison.OrdinalIgnoreCase);
        }

        static FileStream Open(string p) { return new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16); }

        /// <summary>
        /// Streams one object: chunks → compress → encrypt → server. The server returns the SHA-256 it computed of what it
        /// stored; it must equal ours, or the object is sent again (end-to-end check, up to 3 times).
        /// </summary>
        /// <summary>The wait before sending an object again after the connection broke (tests make it short).</summary>
        public static Func<int, int> UploadRetryDelay = attempt => 2000 * attempt * attempt;

        long Upload(string rel, int seq, string kind, string path, LocalState.Entry entry, bool permOnly, string src, HashSet<string> known, List<KeyValuePair<string, int>> chunksOut)
        {
            for (int attempt = 1; ; attempt++)
            {
                chunksOut.Clear();
                BackupObject.Writer w = null;
                var q = "/api/sets/" + set.Id + "/jobs/" + Job + "/object?rel=" + Client.Url(rel) + "&seq=" + seq + "&kind=" + kind
                    + "&orig=" + entry.Size.ToString(CultureInfo.InvariantCulture) + "&mtime=" + entry.Mtime.ToString(CultureInfo.InvariantCulture) + "&perm=" + (permOnly ? "Y" : "N");
                var headers = new Dictionary<string, string> { { "X-Enc", NameCipher.EncryptPath(key, path) } };
                Stream localStream = null;
                TeeStream tee = null;
                if (local != null) try { localStream = local.Open(rel, seq); } catch (Exception e) { Warn("[Local Copy] " + e.Message); }
                Msg resp;
                try
                {
                resp = client.Put(q, headers, s =>
                {
                    tee = new TeeStream(throttle.On ? new ThrottledStream(s, throttle) : s, localStream);
                    w = new BackupObject.Writer(tee, key) { Compression = set.Compression };
                    var storedHere = new HashSet<string>();
                    using (var fs = Open(src))
                        foreach (var c in Chunker.ForFileSize(entry.Size).Split(fs))
                        {
                            var id = BackupObject.ChunkId(key, c);
                            chunksOut.Add(new KeyValuePair<string, int>(id, c.Length));
                            if ((known == null || !known.Contains(id)) && storedHere.Add(id)) w.AddChunk(id, c);
                        }
                    // Agent B (B-2): the size in the object is what was READ, not what the folder listing said — a file
                    // written during the backup (a log, a mail store; with VSS the listing is even the live file) made a
                    // point whose restore always refused the file as "size differs"
                    long read = 0; foreach (var c in chunksOut) read += c.Value;
                    if (read != entry.Size) Info("Changed while it was being backed up (" + entry.Size + " → " + read + " bytes): the point holds what was read; the next backup sends it again: " + path);
                    var header = new Msg().Set("path", path).Set("size", read).Set("mtime", entry.Mtime).Set("attrs", entry.Attrs).Set("kind", kind).Set("seq", seq);
                    foreach (var c in chunksOut) header.Add("recipe", new Msg().Set("h", c.Key).Set("n", c.Value));
                    w.Finish(header);
                });
                }
                catch (AgentException e) when (e.Code == "NETWORK" && attempt < 3)
                {
                    // Bug 24: a short break of the connection no longer ends the whole backup — the object is sent again
                    if (localStream != null) try { localStream.Dispose(); } catch (Exception) { }
                    Warn("The connection broke while sending " + path + "; sending it again (" + e.Message + ")");
                    System.Threading.Thread.Sleep(UploadRetryDelay(attempt));
                    continue;
                }
                if (localStream != null) localStream.Dispose();
                if (resp["sha256"] == w.Sha256 && resp.Long("size") == w.Length)
                {
                    BytesSent += w.Length;
                    if (local != null && tee != null && tee.SecondOk)
                        local.Staged(new ChkRecord { Rel = rel, Seq = seq, Kind = kind, Job = Job, EncPath = headers["X-Enc"], Size = w.Length, Orig = entry.Size, Mtime = entry.Mtime, Sha256 = w.Sha256, PermOnly = permOnly });
                    else if (local != null && tee != null) Warn("[Local Copy] could not write " + path + ": " + tee.SecondError);
                    return w.Length;
                }
                if (attempt >= 3) throw new IOException("The server stored a different copy than was sent (3 attempts).");
                Warn("Upload check mismatch, sending again: " + path);
            }
        }
    }

    /// <summary>Walks the selected sources with the set filters (Ahsay FILTER) and the de-selected paths.</summary>
    /// <summary>The real place of a folder (a link / junction followed to the end).</summary>
    public static class Links
    {
        public static string RealPath(string dir)
        {
            try
            {
#if NET40
                if (Environment.OSVersion.Platform == PlatformID.Win32NT) { var r = Final(dir); if (!string.IsNullOrEmpty(r)) return r; }
                return Path.GetFullPath(dir).TrimEnd('\\', '/');
#else
                var full = Path.GetFullPath(dir).TrimEnd('\\', '/');
                // each part of the path, a link resolved where it is one
                var root = Path.GetPathRoot(full) ?? ""; var acc = root;
                foreach (var part in full.Substring(root.Length).Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    acc = Path.Combine(acc, part);
                    var t = Directory.ResolveLinkTarget(acc, true);
                    if (t != null) acc = Path.GetFullPath(t.FullName).TrimEnd('\\', '/');
                }
                return acc.Length > 0 ? acc : full;
#endif
            }
            catch (Exception) { return Path.GetFullPath(dir).TrimEnd('\\', '/'); }
        }
#if NET40
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        static extern uint GetFinalPathNameByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle h, System.Text.StringBuilder buf, uint size, uint flags);
        static string Final(string dir)
        {
            using (var h = CreateFile(dir, 0, 7, IntPtr.Zero, 3, 0x02000000 /* BACKUP_SEMANTICS: a folder */, IntPtr.Zero))
            {
                if (h.IsInvalid) return null;
                var sb = new System.Text.StringBuilder(1024);
                var n = GetFinalPathNameByHandle(h, sb, (uint)sb.Capacity, 0);
                if (n == 0 || n >= sb.Capacity) return null;
                var r = sb.ToString(); if (r.StartsWith(@"\\?\UNC\")) r = @"\\" + r.Substring(8); else if (r.StartsWith(@"\\?\")) r = r.Substring(4);
                return r.TrimEnd('\\');
            }
        }
#endif
    }

    public static class Scanner
    {
        public static IEnumerable<FileInfo> Files(BackupSetInfo set, Action<string> warn, List<string> unreachable = null, Action<string> info = null)
        {
            foreach (var src in set.Sources)
            {
                if (File.Exists(src)) { yield return new FileInfo(src); continue; }
                if (!Directory.Exists(src)) { warn("Source not found (files kept, not treated as deleted): " + src); if (unreachable != null) unreachable.Add(src); continue; }
                var stack = new Stack<DirectoryInfo>();
                // bug 37 (Agent B): a followed link to its own parent (Windows profiles have such junctions) was walked
                // again and again — the same files backed up many times, until the path was too long. Every folder's
                // real place is remembered; a link to one already walked, or to one of its own parents, is not followed.
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Links.RealPath(src) };
                stack.Push(new DirectoryInfo(src));
                while (stack.Count > 0)
                {
                    var d = stack.Pop();
                    FileSystemInfo[] items;
                    try { items = d.GetFileSystemInfos(); }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                    {
                        warn("Cannot list " + d.FullName + " (files kept): " + e.Message);
                        if (unreachable != null) unreachable.Add(d.FullName);
                        continue;
                    }
                    foreach (var it in items.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        bool isDir = (it.Attributes & FileAttributes.Directory) != 0;
                        if (Excluded(set, it.FullName, it.Name, isDir)) continue;
                        if (isDir)
                        {
                            if ((it.Attributes & FileAttributes.ReparsePoint) != 0)
                            {
                                if (!set.FollowLink) continue;
                                var real = Links.RealPath(it.FullName);
                                var parent = Links.RealPath(d.FullName);
                                if (!seen.Add(real) || parent.StartsWith(real.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || parent.Equals(real, StringComparison.OrdinalIgnoreCase))
                                { (info ?? warn)("Link not followed (it leads back to a folder already backed up): " + it.FullName + " -> " + real); continue; }   // not data left out: an information
                            }
                            else seen.Add(Links.RealPath(it.FullName));
                            stack.Push((DirectoryInfo)it);
                        }
                        else yield return (FileInfo)it;
                    }
                }
            }
        }

        public static bool Excluded(BackupSetInfo set, string full, string name, bool isDir)
        {
            foreach (var d in set.Deselected)
                if (full.Equals(d, StringComparison.OrdinalIgnoreCase) || full.StartsWith(d.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
            bool anyInclude = false, included = false;
            foreach (var f in set.Filters)
            {
                bool m = f.Matches(full, name, isDir) || (isDir && f.Type == "CONTAIN" && f.Patterns.Any(p => p.StartsWith("\\") && ("\\" + name).Equals(p, StringComparison.OrdinalIgnoreCase)));
                if (f.Include && f.Only && (isDir ? f.ApplyDir : f.ApplyFile)) { anyInclude = true; if (m) included = true; }
                else if (!f.Include && m) return true;
            }
            return anyInclude && !included;
        }

        /// <summary>Attributes + NTFS permissions (SDDL) — a change here alone is a "Permission Updated File".</summary>
        public static string Attributes(FileInfo f, bool withAcl)
        {
            var s = ((int)f.Attributes & ~(int)FileAttributes.Archive).ToString(CultureInfo.InvariantCulture);
            if (withAcl && Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
#if NET40
                try { s += "|" + f.GetAccessControl(System.Security.AccessControl.AccessControlSections.Access).GetSecurityDescriptorSddlForm(System.Security.AccessControl.AccessControlSections.Access); }
                catch (Exception) { }
#endif
            }
            return Bytes.Hex(Bytes.Sha256(Encoding.UTF8.GetBytes(s)), 8) + ":" + s.Split('|')[0];
        }
    }

    /// <summary>Pre / post backup commands (Ahsay PRE_CMD / POST_CMD): output and exit code go to the job log.</summary>
    public static class Commands
    {
        public static bool Run(IEnumerable<string> commands, string kind, Action<string> info, Action<string> warn)
        {
            bool ok = true;
            var list = commands.ToList();
            if (list.Count == 0) return true;
            info("Start running " + kind + "-commands");
            foreach (var c in list)
            {
                try
                {
                    bool win = Environment.OSVersion.Platform == PlatformID.Win32NT;
                    var psi = new ProcessStartInfo(win ? "cmd.exe" : "/bin/sh", win ? "/c " + c : "-c \"" + c.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"");
                    // R1: the old ReadToEnd waited for ever — the 1-hour limit never worked; now the command and what it started are stopped
                    var r = ProcessRunner.Run(psi, Limits.Command);
                    var output = (r.Out + r.Err).Trim();
                    if (output.Length > 0) info("[" + kind + "-command output] " + output.Replace("\r", " ").Replace("\n", " ").Substring(0, Math.Min(500, output.Length)));
                    if (r.TimedOut) { warn(kind + "-command timed out after " + ProcessRunner.Describe(Limits.Command) + " and was stopped: " + c); ok = false; continue; }
                    if (r.Code != 0) { warn(kind + "-command exit code " + r.Code + ": " + c); ok = false; }
                }
                catch (Exception e) { warn(kind + "-command failed: " + c + ": " + e.Message); ok = false; }
            }
            info("Finished running " + kind + "-commands");
            return ok;
        }
    }
}
