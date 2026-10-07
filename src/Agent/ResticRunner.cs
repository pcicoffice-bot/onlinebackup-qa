using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using OnlineBackup.Core;

namespace OnlineBackup.Agent
{
    /// <summary>
    /// Sets with ENGINE="RESTIC": the storage engine is restic (BSD-2, used on millions of servers), run by this agent.
    /// Windows 10 / Server 2016 and later (restic 0.18+; older Windows keep this product's own engine).
    /// The product adds around it: schedule, the repository on our server (per set, append-only + 14-day trash), the key
    /// (derived like every set: PASSWORD / DEFAULT / CUSTOM — restic gets it as its repository password), the Ahsay-format
    /// job log, reports, alerts and ITSguard export.
    /// restic is found beside the agent (restic.exe) or by OB_RESTIC (tests).
    /// </summary>
    public sealed class ResticRunner
    {
        readonly AgentApp app;
        readonly BackupSetInfo set;
        readonly KeySet key;
        public Func<DateTime> Clock = () => SystemClock.UtcNow;

        public ResticRunner(AgentApp app, BackupSetInfo set, KeySet key) { this.app = app; this.set = set; this.key = key; }

        public static string Exe
        {
            get
            {
                var env = Environment.GetEnvironmentVariable("OB_RESTIC");
                if (!string.IsNullOrEmpty(env)) return env;
                var dir = AppDomain.CurrentDomain.BaseDirectory;
                return Path.Combine(dir, Environment.OSVersion.Platform == PlatformID.Win32NT ? "restic.exe" : "restic");
            }
        }

        string Secret(string name) { return app.Home.LoadSecret(set.Id + "-" + name); }

        /// <summary>DEST-010: the set's own repository on the local disk / NAS (local-only sets).</summary>
        bool toLocal;   // DEST-010 BOTH: the second run, to the local copy
        public string LocalRepository { get { return Path.Combine(string.IsNullOrEmpty(set.LocalCopyPath) ? Path.Combine(app.Home.Dir, "local") : set.LocalCopyPath, "restic-" + set.Id); } }

        /// <summary>The repository address and access token: asked once from the server (device sign-in), kept protected here.</summary>
        void EnsureAccess()
        {
            if (set.DestMode == "LOCAL") { Directory.CreateDirectory(LocalRepository); return; }   // DEST-010: no server repository
            if (Secret("restic-token") != null) return;
            var m = app.DeviceClient().Call("POST", "/api/sets/" + set.Id + "/restic");
            app.Home.SaveSecret(set.Id + "-restic-path", m["path"]);
            app.Home.SaveSecret(set.Id + "-restic-user", m["user"]);
            app.Home.SaveSecret(set.Id + "-restic-token", m["token"]);
        }

        Dictionary<string, string> Env()
        {
            var server = app.Home.Server.TrimEnd('/');
            if (set.DestMode == "LOCAL" || toLocal)
                return new Dictionary<string, string>
                {
                    { "RESTIC_REPOSITORY", LocalRepository },
                    { "RESTIC_PASSWORD", Bytes.Hex(key.ToRaw()).Substring(0, 64) },
                    { "RESTIC_CACHE_DIR", Path.Combine(app.Home.SetDir(set.Id), "restic-cache") },
                    { "RESTIC_PROGRESS_FPS", "0.0166" }
                };
            var env = new Dictionary<string, string>
            {
                { "RESTIC_REPOSITORY", "rest:" + server + Secret("restic-path") },
                { "RESTIC_REST_USERNAME", Secret("restic-user") },
                { "RESTIC_REST_PASSWORD", Secret("restic-token") },
                // the set's key (never sent to the server): 32 bytes of the encryption key, hex
                { "RESTIC_PASSWORD", Bytes.Hex(key.ToRaw()).Substring(0, 64) },
                { "RESTIC_CACHE_DIR", Path.Combine(app.Home.SetDir(set.Id), "restic-cache") },
                { "RESTIC_PROGRESS_FPS", "0.0166" }
            };
            var ca = (string)app.Home.Config.Attribute("CACERT");
            if (!string.IsNullOrEmpty(ca)) env["RESTIC_CACERT"] = ca;
            return env;
        }

        public sealed class Result { public int Code; public bool Cancelled; public string Out = "", Err = ""; }

        /// <summary>LIVE-010: restic's progress lines while a backup runs.</summary>
        public Action<string> OnStatus;

        /// <summary>Asked every 2 seconds while restic runs a backup; true ends it.</summary>
        public Func<bool> Cancel;

        /// <summary>Runs restic; secrets only in the environment of the child process, never on its command line or in logs.</summary>
        /// <summary>OPT-010 (tests): every restic command line, to prove that each setting reaches restic.</summary>
        public static Action<string[]> Trace;
        static readonly string[] WaitsForLock = { "backup", "forget", "prune", "restore" };
        /// <summary>How long a writing command waits for a lock another command holds (a forget --prune of a big repository).</summary>
        public static string RetryLock = "10m";

        /// <summary>The restic program and its limits for this runner (tests set a stand-in and short limits).</summary>
        public string ExePath = Exe;
        public TimeSpan CommandLimit = Limits.ResticCommand, IdleLimit = Limits.ResticIdle;

        public Result Run(params string[] args)
        {
            // Bug 98 (CI gate, J6): a backup that met the repository locked for a moment by another of the product's own
            // commands (forget --prune after a run, the restore test) failed at once - "repository is already locked
            // exclusively" - and the run was a system error. The commands that write wait for the lock (restic 0.16+).
            if (args.Length > 0 && Array.IndexOf(WaitsForLock, args[0]) >= 0 && Array.IndexOf(args, "--retry-lock") < 0)
                args = args.Concat(new[] { "--retry-lock", RetryLock }).ToArray();
            var trace = Trace; if (trace != null) trace(args);
            var psi = new ProcessStartInfo(ExePath, string.Join(" ", args.Select(Quote).ToArray()))
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            foreach (var kv in Env()) psi.EnvironmentVariables[kv.Key] = kv.Value;
            var r = new Result();
            // Verification (class "raw-process"): restic had no time limit — a restic that hung (a stalled connection) was a
            // backup that never ended, kept alive by its heartbeat. Now: a backup that writes nothing for ResticIdle (it
            // reports every minute) or any command over ResticCommand is stopped and is a clear error.
            var outText = new StringBuilder();
            var idle = args.Length > 0 && args[0] == "backup" ? IdleLimit : (TimeSpan?)null;
            var pr = ProcessRunner.Run(psi, CommandLimit, idle,
                p => { if (set.LowPriority) try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception) { } },   // RES-010
                cancel: () => Cancel != null && Cancel(),                                                                      // SET-030 / maximum duration
                onLine: line => { if (OnStatus != null && line.Contains("\"message_type\":\"status\"")) { try { OnStatus(line); } catch (Exception) { } } });
            r.Cancelled = pr.Cancelled; r.Code = pr.Code;
            r.Out = string.Join("\n", pr.Out.Split('\n').Where(l => !l.Contains("\"message_type\":\"status\"")).ToArray());
            r.Err = pr.Err;
            if (pr.TimedOut) { r.Code = -1; r.Err += (pr.Idle ? "restic stopped answering for " + ProcessRunner.Describe(IdleLimit) : "restic did not finish in " + ProcessRunner.Describe(CommandLimit)) + " and was stopped\n"; }
            return r;
        }

        static string Quote(string a)
        {
            if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
            return "\"" + a.Replace("\\\"", "\\\\\"").Replace("\"", "\\\"") + (a.EndsWith("\\") ? "\\" : "") + "\"";
        }

        /// <summary>
        /// DEST-010 BOTH: after the backup to the server, the same backup to the local copy (a disk or NAS) — a fast restore
        /// without the internet. A failure here is a warning: the backup on the server stands.
        /// </summary>
        void LocalCopy(List<string> args, BackupRun run, List<string> log)
        {
            toLocal = true;
            try
            {
                Directory.CreateDirectory(LocalRepository);
                EnsureRepository(log);
                var r = Run(args.ToArray());
                if (r.Code != 0 && r.Code != 3) { run.Warnings++; log.Add(AhsayLog.Line(Clock(), "warn", message: "[Local Copy] restic exit " + r.Code + ": " + Last(r.Err))); return; }
                var forget = new List<string> { "forget", "--prune", "--tag", "set:" + set.Id, "--host", app.Home.Computer, "--group-by", "host,tags", "--keep-within", Math.Max(1, set.LocalCopyDays) + "d" };
                Run(forget.ToArray());
                log.Add(AhsayLog.Info(Clock(), "[Local Copy] to " + LocalRepository + " (" + Math.Max(1, set.LocalCopyDays) + " days kept)"));
            }
            catch (Exception e) { run.Warnings++; log.Add(AhsayLog.Line(Clock(), "warn", message: "[Local Copy] " + e.Message)); }
            finally { toLocal = false; }
        }

        /// <summary>The repository is created on the first run (restic init); "already exists" is fine.</summary>
        void EnsureRepository(List<string> log)
        {
            var flag = Path.Combine(app.Home.SetDir(set.Id), toLocal ? "restic-init-local.txt" : "restic-init.txt");
            if (File.Exists(flag)) return;
            var cat = Run("cat", "config");
            if (cat.Code != 0)
            {
                // Agent J (J-4): only a repository that is not there is created. restic 0.17+ answers 10 for "no repository"
                // and 12 for "wrong password or no key found"; a wrong key used to fall through to `init`, whose "config
                // file already exists" read like a server fault. Any other answer (the line, the server) is told as it is.
                var said = (cat.Err + "\n" + cat.Out);
                if (cat.Code == 12 || Regex.IsMatch(said, "wrong password|no key found", RegexOptions.IgnoreCase))
                    throw new AgentException(0, "WRONG_KEY", "The encryption key of this set does not open its backup (wrong key or password) — nothing was changed in the backup");
                if (cat.Code != 10 && !Regex.IsMatch(said, "Is there a repository|repository does not exist", RegexOptions.IgnoreCase))
                    throw new AgentException(0, "RESTIC", "The backup could not be reached: " + Last(cat.Err));
                var init = Run("init");
                if (init.Code != 0) throw new AgentException(0, "RESTIC_INIT", "restic init: " + Last(init.Err));
                log.Add(AhsayLog.Info(Clock(), "Repository created (restic)"));
            }
            File.WriteAllText(flag, RunId.From(Clock()));
        }

        static string Last(string s) { var l = (s ?? "").Trim().Split('\n'); return l[l.Length - 1].Trim(); }

        static string Get(string json, string key)
        {
            var m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(\"(?<s>(?:[^\"\\\\]|\\\\.)*)\"|(?<n>-?[0-9.eE+]+))");
            return !m.Success ? null : m.Groups["s"].Success ? Regex.Unescape(m.Groups["s"].Value) : m.Groups["n"].Value;
        }
        static long N(string json, string key) { double d; return double.TryParse(Get(json, key) ?? "0", NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? (long)d : 0; }

        /// <summary>
        /// One backup: pre-commands → restic backup (VSS on Windows) → retention (restic forget --prune; the server keeps
        /// what it removes 14 days in its trash) → post-commands → report to the server. Result codes as Ahsay.
        /// </summary>
        /// <summary>SET-030: "stop" from the admin site.</summary>
        public Func<bool> StopRequested;
        sealed class StoppedException : Exception { public readonly bool ByAdmin; public StoppedException(bool byAdmin) { ByAdmin = byAdmin; } }

        public BackupRun Backup()
        {
            var run = new BackupRun(app.DeviceClient(), app.Home, set, key);
            var log = run.Lines;
            var started = Clock();
            string result = "BS_STOP_SUCCESS";
            bool clean = false;   // restic finished normally: no lock of this run can be left behind
            Msg report = new Msg().Set("job", NewJobId(started)).Set("started", RunId.UnixMs(started));
            log.Add(AhsayLog.Line(started, "start"));
            log.Add(AhsayLog.Info(started, "Start [ " + Environment.OSVersion.VersionString + " (" + app.Home.Computer + "), engine restic ]"));
            run.Finished(null, report["job"]);   // the run id from the start: progress and heartbeat carry it
            BackupRun.WriteMarker(app.Home, set.Id, report["job"], started);
            run.Progress(0, 0, "");
            run.StartHeartbeat();
            try
            {
                if (!Commands.Run(set.PreCommands, "pre", m => log.Add(AhsayLog.Info(Clock(), m)), m => { run.Warnings++; log.Add(AhsayLog.Line(Clock(), "warn", message: m)); }) && set.StopOnPreCommandFailure)
                    throw new AgentException(0, "PRE", "Pre-command failed: backup stopped by the set settings");
                EnsureAccess();
                EnsureRepository(log);
                RecoverInterrupted(log);
                if (set.Type == "M365") M365Pull(run, log);
                if (set.Type == "GWS") GooglePull(run, log);
                var args = new List<string> { "backup", "--json", "--host", app.Home.Computer, "--tag", "set:" + set.Id };
                // COMP-010 / RES-010: compression (maximum by default), the upload limit, waiting while the computer is busy
                args.Add("--compression"); args.Add(set.Compression == "NONE" ? "off" : set.Compression == "FAST" ? "auto" : "max");
                if (set.BandwidthKbps > 0) { args.Add("--limit-upload"); args.Add(set.BandwidthKbps.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
                Resources.WaitWhileBusy(set.BusyCpuPercent, m => log.Add(AhsayLog.Info(Clock(), m)), StopRequested);
                if (set.Type == Domino.Type) Domino.Prepare(m => log.Add(AhsayLog.Info(Clock(), m)), m => { run.Warnings++; log.Add(AhsayLog.Line(Clock(), "warn", message: m)); });
                if ((set.Vss && set.Type == "FILE" || set.Type == Domino.Type) && Environment.OSVersion.Platform == PlatformID.Win32NT) args.Add("--use-fs-snapshot");
                if (set.Type == "M365" || set.Type == "GWS") { args.Add("--exclude"); args.Add(".state"); }
                foreach (var f in set.Filters.Where(f => !f.Include && (f.Type == "WILDCARD" || f.Type == "EXACT") && (f.ApplyFile || f.ApplyDir)))   // exclusion patterns (files and folders)
                    foreach (var pt in f.Patterns) { args.Add("--exclude"); args.Add(pt); }
                foreach (var d in set.Deselected) { args.Add("--exclude"); args.Add(d); }
                var wanted = set.Sources;
                if (set.Type == DbDump.MySql || set.Type == DbDump.Postgres)
                {
                    // DB-020: the dumps first; restic backs up the dump folder (stable paths: only changed parts travel)
                    var dumped = DbDump.Dump(app.Home, set, app.Home.LoadSecret(set.Id + "-sql"), m => log.Add(AhsayLog.Info(Clock(), m)), m => { run.Warnings++; log.Add(AhsayLog.Line(Clock(), "warn", message: m)); }, new List<string>());
                    if (dumped.Count == 0) throw new AgentException(0, "DB", "No database was dumped");
                    wanted = new List<string> { DbDump.Folder(app.Home, set) };
                }
                if (set.Type == Oracle.Type) wanted = new List<string> { Oracle.Backup(app.Home, set, app.Home.LoadSecret(set.Id + "-sql"), m => log.Add(AhsayLog.Info(Clock(), m)), m => { run.Warnings++; log.Add(AhsayLog.Line(Clock(), "warn", message: m)); }) };
                if (set.Type == VMware.Type) wanted = new List<string> { VMware.Backup(app.Home, set, app.Home.LoadSecret(set.Id + "-sql"), m => log.Add(AhsayLog.Info(Clock(), m)), m => { run.Warnings++; log.Add(AhsayLog.Line(Clock(), "warn", message: m)); }) };
                if (set.Type == HyperV.Type) wanted = new List<string> { HyperV.Export(app.Home, set, m => log.Add(AhsayLog.Info(Clock(), m)), m => { run.Warnings++; log.Add(AhsayLog.Line(Clock(), "warn", message: m)); }) };
                var sources = wanted.Where(s => { bool ok = Directory.Exists(s) || File.Exists(s); if (!ok) { run.Errors++; log.Add(AhsayLog.Line(Clock(), "err", s, message: "Source not found (kept in earlier backups)")); } return ok; }).ToList();
                if (sources.Count == 0) throw new AgentException(0, "NO_SOURCE", "No source to back up");
                args.AddRange(sources);
                log.Add(AhsayLog.Info(Clock(), "Backup with restic: " + string.Join(", ", sources.ToArray())));
                var deadline = set.DurationHours > 0 ? started.AddHours(set.DurationHours) : DateTime.MaxValue;
                bool byAdmin = false;
                Cancel = () => Clock() > deadline || (StopRequested != null && (byAdmin = StopRequested()));
                Result r;
                OnStatus = (line) => { double pd; double.TryParse(Get(line, "percent_done") ?? "", System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out pd);
                    run.Progress(N(line, "files_done"), N(line, "bytes_done"), Get(line, "current_files") ?? "", (int)(pd * 100)); };
                try { r = Run(args.ToArray()); } finally { Cancel = null; OnStatus = null; }
                if (r.Cancelled)
                {
                    run.Warnings++;
                    log.Add(AhsayLog.Line(Clock(), byAdmin ? "info" : "warn", message: byAdmin ? "Backup stopped from the admin site; the data sent is reused in the next run" : "Backup stopped after the maximum duration of " + set.DurationHours + " hours; the rest continues in the next run"));
                    if (byAdmin) run.Warnings--;
                    throw new StoppedException(byAdmin);
                }
                string summary = null;
                // bug 40 (Agent J, J-1): with --json restic writes the files it could not read to its ERROR stream; only the
                // output stream was read, so an unreadable file was a "warning" that named nothing — both streams now
                foreach (var line in (r.Out + "\n" + r.Err).Split('\n'))
                {
                    var t = line.Trim();
                    if (t.Length == 0 || t[0] != '{') continue;
                    var type = Get(t, "message_type");
                    if (type == "summary") summary = t;
                    else if (type == "error") { run.Errors++; log.Add(AhsayLog.Line(Clock(), "err", Get(t, "item") ?? "", message: Get(t, "message") ?? Get(t, "during") ?? "error")); }
                }
                if (r.Code != 0 && r.Code != 3) throw new AgentException(0, "RESTIC", "restic backup exit " + r.Code + ": " + Last(r.Err));
                if (summary == null) throw new AgentException(0, "RESTIC", "restic backup gave no summary: " + Last(r.Err));
                run.New = (int)N(summary, "files_new"); run.Updated = (int)N(summary, "files_changed"); run.BytesSent = N(summary, "data_added");
                report.Set("files", N(summary, "total_files_processed")).Set("orig", N(summary, "total_bytes_processed")).Set("snapshot", Get(summary, "snapshot_id"));
                log.Add(AhsayLog.Info(Clock(), "Snapshot " + Get(summary, "snapshot_id") + ": new " + run.New + ", changed " + run.Updated + ", unchanged " + N(summary, "files_unmodified")
                    + ", files " + N(summary, "total_files_processed") + ", added " + N(summary, "data_added") + " bytes"));
                if (r.Code == 3 && run.Errors == 0) { run.Errors++; log.Add(AhsayLog.Line(Clock(), "err", message: "Some files could not be read (restic exit 3) and are not in this point")); }

                // Agent J (J-5): one group per set and computer — restic groups by host AND paths by default, so after the set's
                // folders changed every older snapshot sat in its own group, kept forever as that group's latest
                var forget = new List<string> { "forget", "--prune", "--tag", "set:" + set.Id, "--host", app.Home.Computer, "--group-by", "host,tags" };
                forget.AddRange(KeepArgs(set.Retention));
                var fr = Run(forget.ToArray());
                if (fr.Code != 0) { run.Warnings++; log.Add(AhsayLog.Line(Clock(), "warn", message: "Retention (restic forget --prune): " + Last(fr.Err))); }
                clean = fr.Code == 0;
                if (fr.Code == 0) log.Add(AhsayLog.Info(Clock(), "Retention policy " + set.Retention.Describe() + " applied (removed data stays 14 days in the server trash)"));
                if (set.DestMode == "BOTH") LocalCopy(args, run, log);
            }
            catch (StoppedException e) { result = e.ByAdmin ? "BS_STOP_BY_USER" : "BS_STOP_SUCCESS_WITH_WARNING"; }
            catch (AgentException e)
            {
                result = e.Code == "PRE" ? "BS_STOP_BY_PRE_COMMAND" : e.Message.Contains("(507)") ? StorageFull(log) : "BS_STOP_BY_SYSTEM_ERROR";
                run.Errors++; log.Add(AhsayLog.Line(Clock(), "err", message: e.Message));
            }
            catch (Exception e) { result = "BS_STOP_BY_SYSTEM_ERROR"; run.Errors++; log.Add(AhsayLog.Line(Clock(), "err", message: e.Message)); }
            finally
            {
                EndRunFlag(clean);
                // Agent F (F-2): the post-commands run while the run still signs life to the server — a post-command
                // longer than the lease had the run recorded as interrupted (a failed backup in the history, a mail)
                Commands.Run(set.PostCommands, "post", m => log.Add(AhsayLog.Info(Clock(), m)), m => { run.Warnings++; log.Add(AhsayLog.Line(Clock(), "warn", message: m)); });
                run.StopHeartbeat();
            }

            if (result == "BS_STOP_SUCCESS" && run.Errors > 0) result = "BS_STOP_SUCCESS_WITH_ERROR";
            else if (result == "BS_STOP_SUCCESS" && run.Warnings > 0) result = "BS_STOP_SUCCESS_WITH_WARNING";
            log.Add(AhsayLog.Info(Clock(), "Backup finished: " + result));
            log.Add(AhsayLog.Line(Clock(), "end", message: result));
            run.Finished(result, report["job"]);
            // Bug 22 (found by the scheduler's tests): the scheduler counts from the last successful backup, which only the
            // native engine wrote — a restic set was "due" again within the hour and backed up every 15–20 minutes all day
            if (result.StartsWith("BS_STOP_SUCCESS", StringComparison.Ordinal))
                try { var st = new LocalState(app.Home.SetDir(set.Id)); st.LastSuccess = report["job"]; st.LastSuccessLocalMs = RunId.UnixMs(started); st.Save(); }
                catch (Exception e) { log.Add(AhsayLog.Line(Clock(), "warn", message: "The time of this backup could not be saved: " + e.Message)); }
            report.Set("result", result).Set("new", run.New).Set("upd", run.Updated).Set("del", 0).Set("perm", 0).Set("bytes", run.BytesSent);
            var prev = Path.Combine(app.Home.SetDir(set.Id), "restic-files.txt");
            long prevFiles; if (File.Exists(prev) && long.TryParse(OnlineBackup.Core.Atomic.ReadAllText(prev).Trim(), out prevFiles)) report.Set("prevFiles", prevFiles);
            if (report["files"] != null) File.WriteAllText(prev, report["files"]);
            foreach (var l in log) report.Add("log", new Msg().Set("l", l));
            bool delivered = false;
            try { app.DeviceClient().Call("POST", "/api/sets/" + set.Id + "/resticreport", report); delivered = true; }
            catch (AgentException e) { log.Add(AhsayLog.Line(Clock(), "warn", message: "Report not delivered: " + e.Message)); }
            if (delivered) BackupRun.ClearMarker(app.Home, set.Id);   // else the next run tells the server this one did not end normally
            return run;
        }

        /// <summary>
        /// The run's id (its start, to the second) — never the id of an earlier run of this set: a run that failed at once and
        /// the next one could start in the same second, and the second's report then replaced the first in the history.
        /// </summary>
        string NewJobId(DateTime started)
        {
            var f = Path.Combine(app.Home.SetDir(set.Id), "last-job.txt");
            DateTime last;
            try { if (File.Exists(f) && RunId.TryParse(OnlineBackup.Core.Atomic.ReadAllText(f).Trim(), out last) && started <= last) started = last.AddSeconds(1); } catch (IOException) { }
            var id = RunId.From(started);
            Atomic.WriteText(f, id);
            return id;
        }

        /// <summary>507 from the server: the user's quota (as Ahsay: BS_STOP_QUOTA_EXCEEDED) or the server's disk — asked live.</summary>
        string StorageFull(List<string> log)
        {
            try
            {
                var q = app.DeviceClient().Call("GET", "/api/quota");
                if (q.Int("quotaRefused") == 1 || (q.Long("quota") > 0 && q.Long("used") * 100 >= q.Long("quota") * 95))
                {
                    log.Add(AhsayLog.Line(Clock(), "err", message: "Quota exceeded (" + q["used"] + " of " + q["quota"] + " bytes): existing backups are kept, new data is not accepted"));
                    return "BS_STOP_QUOTA_EXCEEDED";
                }
            }
            catch (AgentException) { }
            log.Add(AhsayLog.Line(Clock(), "err", message: "The backup server has no free disk space: existing backups are kept (the administrators were alerted)"));
            return "BS_STOP_BY_SYSTEM_ERROR";
        }

        /// <summary>M365-040: Microsoft 365 sets — the tenant into the mirror folder (Sources[0]) before restic backs it up.</summary>
        void M365Pull(BackupRun run, List<string> log)
        {
            var secret = app.Home.LoadSecret(set.Id + "-m365");
            if (string.IsNullOrEmpty(secret)) throw new AgentException(0, "M365", "Microsoft 365: the application secret is not set on this computer (m365-secret)");
            if (set.Sources.Count == 0) throw new AgentException(0, "M365", "Microsoft 365: no mirror folder");
            var users = (set.M365Users ?? "").Split(new[] { ';', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            var sync = new M365Sync(new GraphClient(set.M365Tenant, set.M365ClientId, secret), set.Sources[0],
                m => log.Add(AhsayLog.Info(Clock(), m)), m => { run.Warnings++; log.Add(AhsayLog.Line(Clock(), "warn", message: m)); });
            try { sync.Run(users); }
            catch (GraphClient.GraphException e) { throw new AgentException(0, "M365", e.Message); }
        }

        /// <summary>GWS-040: Google Workspace sets — Gmail and Drive into the mirror folder before restic backs it up.</summary>
        void GooglePull(BackupRun run, List<string> log)
        {
            var key = app.Home.LoadSecret(set.Id + "-gws");
            if (string.IsNullOrEmpty(key)) throw new AgentException(0, "GWS", "Google Workspace: the service account key is not set on this computer");
            if (set.Sources.Count == 0) throw new AgentException(0, "GWS", "Google Workspace: no mirror folder");
            var users = (set.M365Users ?? "").Split(new[] { ';', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            var sync = new GoogleSync(new GoogleClient(key, GoogleClient.ReadScopes), set.Sources[0],
                m => log.Add(AhsayLog.Info(Clock(), m)), m => { run.Warnings++; log.Add(AhsayLog.Line(Clock(), "warn", message: m)); });
            try { sync.Run(users, set.GwsAdmin); }
            catch (GoogleClient.GoogleException e) { throw new AgentException(0, "GWS", e.Message); }
        }

        /// <summary>GWS-050: chosen items of a point back into Google (label / folder "Restored &lt;date&gt;").</summary>
        public GoogleRestore RestoreToGoogle(string snapshot, IEnumerable<string> includes, List<string> log)
        {
            var key = app.Home.LoadSecret(set.Id + "-gws");
            if (string.IsNullOrEmpty(key)) throw new AgentException(0, "GWS", "Google Workspace: the service account key is not set on this computer");
            var tmp = Path.Combine(app.Home.Dir, "temp", "gws-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                RestoreMany(snapshot, tmp, includes, log);
                var back = new GoogleRestore(new GoogleClient(key, GoogleClient.RestoreScopes), SystemClock.UtcNow);
                foreach (var f in Directory.GetFiles(tmp, "*", SearchOption.AllDirectories))
                {
                    var rel = f.Substring(tmp.Length).Replace('\\', '/');
                    if (rel.Contains("/.state/")) continue;
                    try { back.Item(f, rel, safe => safe); }   // the mirror keeps the address as it is (Safe() leaves @ and .)
                    catch (Exception e) { back.Failed++; log.Add(AhsayLog.Line(Clock(), "err", rel, message: "restore to Google: " + e.Message)); }
                }
                log.Add(AhsayLog.Info(Clock(), "Restored to Google Workspace: mail " + back.Mail + ", files " + back.Files + ", failed " + back.Failed));
                return back;
            }
            finally { try { Directory.Delete(tmp, true); } catch (Exception) { } }
        }

        /// <summary>M365-050: chosen items of a point back into Microsoft 365 ("Restored &lt;date&gt;" folders).</summary>
        public M365Restore RestoreToM365(string snapshot, IEnumerable<string> includes, List<string> log)
        {
            var secret = app.Home.LoadSecret(set.Id + "-m365");
            if (string.IsNullOrEmpty(secret)) throw new AgentException(0, "M365", "Microsoft 365: the application secret is not set on this computer");
            var tmp = Path.Combine(app.Home.Dir, "temp", "m365-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                RestoreMany(snapshot, tmp, includes, log);
                var back = new M365Restore(new GraphClient(set.M365Tenant, set.M365ClientId, secret), SystemClock.UtcNow);
                foreach (var f in Directory.GetFiles(tmp, "*", SearchOption.AllDirectories))
                {
                    var rel = f.Substring(tmp.Length).Replace('\\', '/');
                    if (rel.Contains("/.state/")) continue;
                    try { back.Item(f, rel); }
                    catch (Exception e) { back.Failed++; log.Add(AhsayLog.Line(Clock(), "err", rel, message: "restore to Microsoft 365: " + e.Message)); }
                }
                log.Add(AhsayLog.Info(Clock(), "Restored to Microsoft 365: mail " + back.Mail + ", files " + back.Files + ", contacts " + back.Contacts + ", events " + back.Events + ", failed " + back.Failed));
                return back;
            }
            finally { try { Directory.Delete(tmp, true); } catch (Exception) { } }
        }

        string RunningFlag { get { return Path.Combine(app.Home.SetDir(set.Id), "restic-running.txt"); } }

        /// <summary>
        /// A run that died (power cut, killed, crash) leaves restic's lock in the repository; a later prune would refuse
        /// to run. Every run marks its start; if the mark of a run that is no longer alive is found, that run's locks are
        /// removed (only this agent works on the set's repository — one repository per set and computer).
        /// </summary>
        void RecoverInterrupted(List<string> log)
        {
            if (UnlockAfterDeadRun(log) == false) throw new AgentException(0, "BUSY", "Another run of this set is still working");
            // Agent F (F-3): the flag names this process by its id AND its start time — an id alone is reused by Windows and
            // Linux for any later program, and a flag naming a live stranger refused every backup of the set from then on
            var me = Process.GetCurrentProcess();
            File.WriteAllText(RunningFlag, me.Id.ToString(CultureInfo.InvariantCulture) + "\t" + RunId.UnixMs(me.StartTime.ToUniversalTime()).ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>The end of this process's run: a clean run removes its flag; a run that may have left locks keeps a flag
        /// that names no live process (the next run removes the locks) — never this process's id, which stays alive in the
        /// service and would refuse the runs of another program (the window, the command line) as "still working".</summary>
        void EndRunFlag(bool clean)
        {
            try { if (clean) File.Delete(RunningFlag); else File.WriteAllText(RunningFlag, "interrupted"); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        /// <summary>True only when the flag names a process that is alive now, is not this one, and started when the flag's
        /// writer started (to 2 s) — a reused id is another program. A flag without a start time (older versions, or an
        /// interrupted run) names no provable live run.</summary>
        static bool FlagNamesLiveRun(string flag)
        {
            var f = flag.Trim().Split('\t');
            int pid; long startMs;
            if (f.Length < 2 || !int.TryParse(f[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out pid)
                || !long.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out startMs)) return false;
            if (pid == Process.GetCurrentProcess().Id) return false;
            try
            {
                var p = Process.GetProcessById(pid);
                return !p.HasExited && Math.Abs(RunId.UnixMs(p.StartTime.ToUniversalTime()) - startMs) < 2000;
            }
            catch (ArgumentException) { return false; } catch (InvalidOperationException) { return false; }
            catch (System.ComponentModel.Win32Exception) { return false; }
        }

        /// <summary>The locks of a run of this set that is no longer alive are removed. null: no such run; true: removed;
        /// false: the run is still alive (its locks are real).</summary>
        bool? UnlockAfterDeadRun(List<string> log)
        {
            if (!File.Exists(RunningFlag)) return null;
            if (FlagNamesLiveRun(OnlineBackup.Core.Atomic.ReadAllText(RunningFlag))) return false;
            var u = Run("unlock", "--remove-all");
            log.Add(AhsayLog.Info(Clock(), "The previous run was interrupted: its repository locks were removed (" + (u.Code == 0 ? "ok" : Last(u.Err)) + ")"));
            return true;
        }

        /// <summary>The set's retention in restic terms (the latest snapshot is always kept by restic).</summary>
        public static List<string> KeepArgs(RetentionPolicy p)
        {
            var a = new List<string>();
            if (p.Unit == "JOBS") { a.Add("--keep-last"); a.Add(Math.Max(1, p.Period).ToString(CultureInfo.InvariantCulture)); }
            else { a.Add("--keep-within"); a.Add(Math.Max(1, p.Period).ToString(CultureInfo.InvariantCulture) + "d"); }
            Action<string, int> k = (n, v) => { if (v > 0) { a.Add(n); a.Add(v.ToString(CultureInfo.InvariantCulture)); } };
            k("--keep-daily", p.Daily); k("--keep-weekly", p.Weekly); k("--keep-monthly", p.Monthly); k("--keep-yearly", p.Yearly);
            if (p.Quarterly > 0) { a.Add("--keep-monthly"); a.Add(Math.Max(p.Monthly, p.Quarterly * 3).ToString(CultureInfo.InvariantCulture)); }
            return a;
        }

        public List<string> Snapshots()
        {
            EnsureAccess();
            var r = Run("snapshots", "--json", "--tag", "set:" + set.Id);
            if (r.Code != 0) throw new AgentException(0, "RESTIC", "restic snapshots: " + Last(r.Err));
            return Regex.Matches(r.Out, "\"short_id\"\\s*:\\s*\"([0-9a-f]+)\"").Cast<Match>().Select(m => m.Groups[1].Value).ToList();
        }

        /// <summary>Restore a snapshot ("latest" by default) to a folder; optional path filter (restic --include).</summary>
        public void Restore(string snapshot, string target, string include, List<string> log, bool overwrite = false)
        {
            RestoreMany(snapshot, target, string.IsNullOrEmpty(include) ? null : new[] { include }, log, overwrite);
        }

        /// <summary>restic's --include is a pattern: [ * ? in a real name ("Report [final].docx") are made literal ([[] [*] [?]).</summary>
        public static string GlobLiteral(string path)
        {
            var sb = new StringBuilder();
            foreach (var c in path) { if (c == '[' || c == '*' || c == '?') sb.Append('[').Append(c).Append(']'); else sb.Append(c); }
            return sb.ToString();
        }

        /// <summary>Bug 39 (Agent J, J-2): the restore ignored "Replace existing files" and restic's default replaces
        /// everything — a file the customer changed after the backup was overwritten by the old version without being asked.
        /// Existing files are now kept unless <paramref name="overwrite"/>.
        /// Bug 48 (found by ReliabilityTests after bug 39): restic writes straight into the target, so a restore cut midway
        /// (line, power, kill) left half files under their real names — and the next restore kept them as "existing": the
        /// customer got a damaged file with no error. restic now restores into a fresh folder beside the files
        /// (".ob-restoring-…", same volume) and only a restore that ended well is moved into place, one rename per file or
        /// new folder; a cut restore leaves nothing under a real name, and its folder is removed then or by the next restore.
        /// J-3: a restore that brought nothing (the chosen path is not in the point) is an error, not "OK".</summary>
        public void RestoreMany(string snapshot, string target, IEnumerable<string> includes, List<string> log, bool overwrite = false)
        {
            EnsureAccess();
            Directory.CreateDirectory(target);
            foreach (var old in Directory.GetDirectories(target, StagePrefix + "*"))
                try { Directory.Delete(old, true); } catch (Exception e) { log.Add(AhsayLog.Line(Clock(), "warn", message: "A folder of an earlier cut restore could not be removed: " + old + " (" + e.Message + ")")); }
            var stage = Path.Combine(target, StagePrefix + Guid.NewGuid().ToString("N").Substring(0, 12));
            Directory.CreateDirectory(stage);
            try
            {
                var args = new List<string> { "restore", string.IsNullOrEmpty(snapshot) ? "latest" : snapshot, "--target", stage, "--tag", "set:" + set.Id };
                if (includes != null) foreach (var inc in includes) { args.Add("--include"); args.Add(GlobLiteral(inc)); }
                var r = Run(args.ToArray());
                log.Add(AhsayLog.Info(Clock(), "restic restore " + (snapshot ?? "latest") + " to " + target + ": exit " + r.Code));
                if (r.Code != 0) throw new AgentException(0, "RESTIC", "restic restore: " + Last(r.Err) + " — nothing was put in place; run the restore again");
                var c = new int[3];   // placed, kept (existed), replaced
                Place(stage, target, overwrite, c);
                if (c[0] + c[1] + c[2] == 0) throw new AgentException(0, "RESTIC", "Nothing was restored: the chosen files are not in this backup point");
                log.Add(AhsayLog.Info(Clock(), "Restored: " + c[0] + " new, " + c[2] + " replaced, " + c[1] + " kept (already there" + (overwrite ? "" : "; 'Replace existing files' was not chosen") + ")"));
            }
            finally { try { if (Directory.Exists(stage)) Directory.Delete(stage, true); } catch (Exception) { } }
        }

        const string StagePrefix = ".ob-restoring-";

        static bool IsLink(FileSystemInfo i) { return (i.Attributes & FileAttributes.ReparsePoint) != 0; }

        /// <summary>Moves what restic restored into place. A folder that is not there yet moves whole (one rename); an
        /// existing one is entered. An existing file (or link) is kept, or replaced when asked. A link is never followed.</summary>
        static void Place(string from, string to, bool overwrite, int[] c)
        {
            foreach (var e in new DirectoryInfo(from).GetFileSystemInfos())
            {
                var dest = Path.Combine(to, e.Name);
                var isDir = e is DirectoryInfo && !IsLink(e);
                var there = File.Exists(dest) || Directory.Exists(dest);
                if (isDir && !there) { c[0] += Math.Max(1, Count((DirectoryInfo)e)); Directory.Move(e.FullName, dest); }
                else if (isDir && Directory.Exists(dest) && !IsLink(new DirectoryInfo(dest))) Place(e.FullName, dest, overwrite, c);
                else if (!there) { MoveEntry(e, dest); c[0]++; }
                else if (overwrite && File.Exists(dest) && !isDir) { File.Delete(dest); MoveEntry(e, dest); c[2]++; }
                else c[1]++;
            }
        }

        static void MoveEntry(FileSystemInfo e, string dest) { if (e is DirectoryInfo) Directory.Move(e.FullName, dest); else File.Move(e.FullName, dest); }

        static int Count(DirectoryInfo d)
        {
            int n = 0;
            foreach (var e in d.GetFileSystemInfos()) n += e is DirectoryInfo && !IsLink(e) ? Count((DirectoryInfo)e) : 1;
            return n;
        }

        /// <summary>The snapshots of the set, newest first: id, time, files of the run.</summary>
        public List<Msg> SnapshotList()
        {
            EnsureAccess();
            var r = Run("snapshots", "--json", "--tag", "set:" + set.Id);
            if (r.Code != 0) throw new AgentException(0, "RESTIC", "restic snapshots: " + Last(r.Err));
            // each snapshot: its "time" comes before its "short_id" (newer restic adds a nested "summary" object in between)
            var list = new List<Msg>(); string time = null;
            foreach (Match m in Regex.Matches(r.Out, "\"(time|short_id)\"\\s*:\\s*\"([^\"]+)\""))
            {
                if (m.Groups[1].Value == "time") time = m.Groups[2].Value;
                else { list.Add(new Msg().Set("id", m.Groups[2].Value).Set("time", time ?? "")); time = null; }
            }
            list.Reverse();
            return list;
        }

        /// <summary>Files of a snapshot (restic ls --json): path, size, mtime, type.</summary>
        public List<Msg> Ls(string snapshot)
        {
            EnsureAccess();
            var r = Run("ls", "--json", string.IsNullOrEmpty(snapshot) ? "latest" : snapshot);
            if (r.Code != 0) throw new AgentException(0, "RESTIC", "restic ls: " + Last(r.Err));
            var list = new List<Msg>();
            foreach (var line in r.Out.Split('\n'))
            {
                var t = line.Trim();
                if (t.Length == 0 || t[0] != '{' || Get(t, "struct_type") != "node") continue;
                list.Add(new Msg().Set("path", Get(t, "path")).Set("type", Get(t, "type")).Set("size", N(t, "size")).Set("mtime", Get(t, "mtime")));
            }
            return list;
        }

        /// <summary>Monthly: restic check reading 5% of the data (every byte authenticated with the key).</summary>
        public Msg Check(string subset = "5%")
        {
            EnsureAccess();
            // Bug 23: a backup that was stopped (by the administrator, at its maximum duration) or died leaves restic's lock;
            // restic check then answered "repo already locked" and the restore test showed FAILED for a sound backup
            var notes = new List<string>();
            if (UnlockAfterDeadRun(notes) == true) try { File.Delete(RunningFlag); } catch (IOException) { }
            var r = Run("check", "--read-data-subset", subset);
            if (r.Code != 0 && Regex.IsMatch(r.Err + r.Out, "already locked|repository is locked", RegexOptions.IgnoreCase))
                return new Msg().Set("checked", 0).Set("ok", 0).Set("failed", 0).Set("message", "Not checked: the backup is in use by a running backup — checked next time");
            var ok = r.Code == 0;
            return new Msg().Set("checked", 1).Set("ok", ok ? 1 : 0).Set("failed", ok ? 0 : 1).Set("message", ok ? "restic check OK" : Last(r.Err + r.Out));
        }
    }
}
