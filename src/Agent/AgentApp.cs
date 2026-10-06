using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml.Linq;
using OnlineBackup.Core;

namespace OnlineBackup.Agent
{
    /// <summary>What the agent does: register, add sets, back up, restore, and the scheduler loop of the service.</summary>
    public sealed class AgentApp
    {
        public AgentHome Home { get; private set; }

        public AgentApp(string homeDir) { Home = new AgentHome(homeDir); }

        /// <summary>Windows 2003 / XP have no TLS 1.2 of their own: the agent then uses its built-in TLS automatically.</summary>
        public static bool NeedsBuiltinTls { get { return Environment.OSVersion.Platform == PlatformID.Win32NT && Environment.OSVersion.Version.Major == 5; } }

        Client NewClient(string server)
        {
            if (string.IsNullOrEmpty(server)) throw new AgentException(0, "NOT_REGISTERED", "This computer is not registered with a backup server.");
            return new Client(server, Home.Pin) { Builtin = Home.Tls == "BUILTIN" || (Home.Tls == null && NeedsBuiltinTls) };
        }

        public Client DeviceClient()
        {
            if (Home.Server == null || Home.DeviceToken == null) throw new AgentException(0, "NOT_REGISTERED", "This computer is not registered with a backup server.");
            var c = NewClient(Home.Server);
            c.Device = Home.DeviceToken;
            return c;
        }

        /// <summary>
        /// Registers this computer to a backup user (password + 2FA code when enabled). With the built-in TLS the server
        /// certificate fingerprint is pinned now (or the one given by the installer is enforced).
        /// </summary>
        public void Register(string server, string login, string password, string otp, string computer, string tls = null, string pin = null, int contractVersion = 0)
        {
            bool builtin = tls == "BUILTIN" || (tls == null && NeedsBuiltinTls);
            var c = new Client(server, pin) { Builtin = builtin };
            var r = c.Call("POST", "/api/register", new Msg().Set("login", login).Set("password", password).Set("otp", otp).Set("computer", computer ?? Environment.MachineName).Set("contractVersion", contractVersion > 0 ? (object)contractVersion : null));
            Home.SaveRegistration(server, login, computer ?? Environment.MachineName, r["device"], builtin ? "BUILTIN" : (tls ?? "SYSTEM"), builtin ? (pin ?? c.SeenPin) : pin);
        }

        /// <summary>CONTRACT-010: the IT company's contract, before sign-in (version 0 = none).</summary>
        public static Msg Contract(string server, string lang, string pin = null) { return new Client(server, pin) { Builtin = NeedsBuiltinTls }.Call("GET", "/api/contract?lang=" + Uri.EscapeDataString(lang ?? "")); }

        /// <summary>SIGNUP-010: a new customer from the client software (the contract accepted), then this computer is registered.</summary>
        public string Signup(string server, string company, string email, string phone, string login, string password, int contractVersion, string computer = null, string pin = null)
        {
            var c = new Client(server, pin) { Builtin = NeedsBuiltinTls };
            var r = c.Call("POST", "/api/signup", new Msg().Set("company", company).Set("email", email).Set("phone", phone).Set("login", login).Set("password", password).Set("contractVersion", contractVersion));
            Register(server, r["login"], password, null, computer, null, pin, contractVersion);
            return r["login"];
        }

        /// <summary>An interactive session: needed to change settings and to restore.</summary>
        public Client Interactive(string password, string otp)
        {
            var c = NewClient(Home.Server);
            var r = c.Call("POST", "/api/login", new Msg().Set("login", Home.Login).Set("password", password).Set("otp", otp));
            c.Session = r["session"];
            return c;
        }

        public Profile Profile() { return Core.Profile.Parse(DeviceClient().Call("GET", "/api/profile")["profile"]); }

        public List<BackupSetInfo> Sets() { return Profile().Sets; }

        /// <summary>
        /// New backup set. The encryption key is fixed at creation and can never be changed (to change it: a new user and
        /// a full upload). Key types as in Ahsay: PASSWORD (from the user's password now), DEFAULT (random), CUSTOM.
        /// </summary>
        public BackupSetInfo CreateSet(Client session, string password, BackupSetInfo s, string keyType = "PASSWORD", string customKey = null)
        {
            var salt = Bytes.Random(16);
            KeySet k = keyType == "DEFAULT" ? KeySet.Random() : KeySet.Derive(keyType == "CUSTOM" ? customKey : password, salt);
            s.KeyType = keyType; s.KeySalt = Convert.ToBase64String(salt); s.KeyCheck = k.CheckValue();
            if (string.IsNullOrEmpty(s.Computer)) s.Computer = Home.Computer;
            var r = session.Call("POST", "/api/sets", new Msg().Set("set", s.ToXml().ToString(SaveOptions.DisableFormatting)));
            var created = BackupSetInfo.FromXml(XElement.Parse(r["set"]));
            Home.SaveKey(created.Id, k);
            session.Call("POST", "/api/sets/" + created.Id + "/key", new Msg().Set("key", Convert.ToBase64String(k.ToRaw())));
            return created;
        }

        /// <summary>The set key: from this computer, or re-derived from the password / custom key (restore on a new computer).</summary>
        public KeySet Key(BackupSetInfo s, string secret = null, byte[] recovered = null)
        {
            KeySet k = recovered != null ? KeySet.FromRaw(recovered) : Home.LoadKey(s.Id);
            if (k == null && secret != null && s.KeyType != "DEFAULT") k = KeySet.Derive(secret, Convert.FromBase64String(s.KeySalt));
            if (k == null && !string.IsNullOrEmpty(s.Parent))
            {
                // SET-020: a copy of a set added by the IT company — the first set's key, from this computer or (key recovery on) the server
                k = Home.LoadKey(s.Parent);
                if (k == null) try { k = KeySet.FromRaw(Convert.FromBase64String(DeviceClient().Call("GET", "/api/sets/" + s.Id + "/sharedkey")["key"])); } catch (AgentException) { }
                if (k != null && k.CheckValue() == s.KeyCheck) Home.SaveKey(s.Id, k);
            }
            if (k == null) throw new AgentException(0, "NO_KEY", "This computer does not have the set's encryption key. Enter the key or get it from your provider (key recovery).");
            if (k.CheckValue() != s.KeyCheck) throw new AgentException(0, "WRONG_KEY", "The encryption key is wrong.");
            // a replacement computer that proved the key (password, custom key or recovery) keeps it, as the first one did:
            // the set's backups go on from here without asking again
            if (Home.LoadKey(s.Id) == null) Home.SaveKey(s.Id, k);
            return k;
        }

        public BackupRun Backup(string setId, string mode = "FULL")
        {
            var s = Sets().FirstOrDefault(x => x.Id == setId);
            if (s == null) throw new AgentException(404, "NO_SET", "The backup set does not exist.");
            // one run per set at a time (the schedule and "back up now" share the service process)
            object gate;
            lock (running) { if (!running.TryGetValue(s.Id, out gate)) running[s.Id] = gate = new object(); }
            if (!Monitor.TryEnter(gate)) throw new AgentException(409, "BUSY", "A backup of this set is already running.");
            try { return BackupLocked(s, mode); } finally { Monitor.Exit(gate); }
        }

        static readonly Dictionary<string, object> running = new Dictionary<string, object>();
        /// <summary>OPT-010 (tests): the clock of the next backups — to prove the maximum duration without waiting hours.</summary>
        public Func<DateTime> RunClock;

        /// <summary>
        /// R1: a run of this set that died with its process (power cut, reboot, killed) left its note behind: the server is
        /// told at once — the run is released (no waiting for its lease) and recorded as interrupted. The note of a run that
        /// is still alive in another process (same process id and start time) is left alone.
        /// </summary>
        public bool ReportInterrupted(string setId)
        {
            var path = BackupRun.MarkerPath(Home, setId);
            if (!File.Exists(path)) return false;
            string[] f;
            try { f = File.ReadAllText(path).Trim().Split('\t'); } catch (IOException) { return false; }
            if (f.Length >= 4)
            {
                int pid; long startMs;
                if (int.TryParse(f[2], out pid) && long.TryParse(f[3], out startMs))
                    try
                    {
                        var p = System.Diagnostics.Process.GetProcessById(pid);
                        if (!p.HasExited && Math.Abs(RunId.UnixMs(p.StartTime.ToUniversalTime()) - startMs) < 2000 && !(pid == System.Diagnostics.Process.GetCurrentProcess().Id && !Running(setId)))
                            return false;   // that run is alive
                    }
                    catch (ArgumentException) { } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
            }
            long started = 0; if (f.Length > 1) long.TryParse(f[1], out started);
            DeviceClient().Call("POST", "/api/sets/" + setId + "/interrupted", new Msg().Set("job", f[0]).Set("started", started).Set("why", "the backup program stopped in the middle of the run"));
            BackupRun.ClearMarker(Home, setId);
            return true;
        }

        static bool Running(string setId)
        {
            object gate; lock (running) if (!running.TryGetValue(setId, out gate)) return false;
            if (!Monitor.TryEnter(gate)) return true;
            Monitor.Exit(gate); return false;
        }

        BackupRun BackupLocked(BackupSetInfo s, string mode)
        {
            BackupRun run;
            try { ReportInterrupted(s.Id); } catch (AgentException) { }   // the server is told before the new run begins
            var stop = StopCheck(s, SystemClock.UtcNow);
            using (Resources.LowPriority(s.LowPriority && s.Engine != "RESTIC"))
            {
                try
                {
                    if (s.Engine == "RESTIC") { if (mode == "LOG") throw new AgentException(0, "MODE", "restic sets have no log mode"); var rr = Restic(s); rr.StopRequested = stop; if (RunClock != null) rr.Clock = RunClock; run = rr.Backup(); }
                    else { run = new BackupRun(DeviceClient(), Home, s, Key(s)) { Mode = mode, StopRequested = stop }; if (RunClock != null) run.Clock = RunClock; run.Run(); }
                }
                catch (AgentException e) when (e.Code == "NETWORK")
                {
                    // cut off by the internet: noted, so it starts again as soon as the server answers (MISS-020)
                    NoteOffline(SystemClock.UtcNow);
                    File.WriteAllText(Path.Combine(Home.SetDir(s.Id), mode == "LOG" ? "last-log.txt" : "last-attempt.txt"), RunId.From(SystemClock.UtcNow) + "\tNETWORK");
                    throw;
                }
            }
            File.WriteAllText(Path.Combine(Home.SetDir(s.Id), mode == "LOG" ? "last-log.txt" : "last-attempt.txt"), RunId.From(SystemClock.UtcNow) + "\t" + run.Result);
            return run;
        }

        // ---------------------------------------------------------------- MISS-020: the internet went down and came back

        string OfflineFile { get { return Path.Combine(Home.Dir, "offline-since.txt"); } }
        string BackFile { get { return Path.Combine(Home.Dir, "offline-last.txt"); } }

        /// <summary>The server could not be reached (the internet or the server is down): noted from the first time.</summary>
        public void NoteOffline(DateTime nowUtc)
        {
            try { if (!File.Exists(OfflineFile)) File.WriteAllText(OfflineFile, RunId.UnixMs(nowUtc).ToString(System.Globalization.CultureInfo.InvariantCulture)); } catch (IOException) { }
        }

        /// <summary>The server answers again: the offline period is kept (from, to) for the missed backups.</summary>
        public void NoteOnline(DateTime nowUtc)
        {
            try
            {
                if (!File.Exists(OfflineFile)) return;
                File.WriteAllText(BackFile, File.ReadAllText(OfflineFile).Trim() + "\t" + RunId.UnixMs(nowUtc).ToString(System.Globalization.CultureInfo.InvariantCulture));
                File.Delete(OfflineFile);
            }
            catch (IOException) { }
        }

        /// <summary>The last offline period (local time), or null.</summary>
        public Tuple<DateTime, DateTime> LastOffline()
        {
            try
            {
                if (!File.Exists(BackFile)) return null;
                var f = File.ReadAllText(BackFile).Trim().Split('\t');
                long a, b;
                if (f.Length != 2 || !long.TryParse(f[0], out a) || !long.TryParse(f[1], out b)) return null;
                return Tuple.Create(RunId.FromUnixMs(a).ToLocalTime(), RunId.FromUnixMs(b).ToLocalTime());
            }
            catch (IOException) { return null; }
        }

        /// <summary>
        /// SET-030: "stop" from the admin site — true when the set got a stop request after this run started. The profile is
        /// read at most every 20 seconds; a server that cannot be reached never stops the backup.
        /// </summary>
        public Func<bool> StopCheck(BackupSetInfo s, DateTime startedUtc)
        {
            long since = RunId.UnixMs(startedUtc); DateTime next = DateTime.MinValue; bool stop = s.StopRequest > since;
            return () =>
            {
                if (stop) return true;
                if (SystemClock.UtcNow < next) return false;
                next = SystemClock.UtcNow.AddSeconds(20);
                try { var cur = Sets().FirstOrDefault(x => x.Id == s.Id); stop = cur != null && cur.StopRequest > since; } catch (Exception) { }
                return stop;
            };
        }

        /// <summary>SET-030: "back up now" from the admin site — a request newer than the last one this computer handled.</summary>
        public bool RunRequested(BackupSetInfo s)
        {
            if (s.RunRequest <= 0) return false;
            var f = Path.Combine(Home.SetDir(s.Id), "run-request.txt");
            long done; long.TryParse(File.Exists(f) ? File.ReadAllText(f).Trim() : "0", out done);
            if (s.RunRequest <= done) return false;
            File.WriteAllText(f, s.RunRequest.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return true;
        }

        /// <summary>SQL transaction-log backups every LOG_INTERVAL_MINUTES between the full backups.</summary>
        public bool LogDue(BackupSetInfo s, DateTime utc)
        {
            if (s.Type != "MSSQL" || s.LogIntervalMinutes <= 0) return false;
            var p = Path.Combine(Home.SetDir(s.Id), "last-log.txt");
            DateTime last;
            return !File.Exists(p) || !RunId.TryParse(File.ReadAllText(p).Split('\t')[0], out last) || (utc - last).TotalMinutes >= s.LogIntervalMinutes;
        }

        /// <summary>The restic engine for a set (ENGINE="RESTIC").</summary>
        /// <summary>Tests: adjusts every restic runner this app makes (a stand-in program, short limits).</summary>
        public Action<ResticRunner> ResticSetup;

        public ResticRunner Restic(BackupSetInfo s, string secret = null) { var r = new ResticRunner(this, s, Key(s, secret)); var f = ResticSetup; if (f != null) f(r); return r; }

        public bool RestoreTestDue(BackupSetInfo s, DateTime utc)
        {
            if (s.Engine == "RESTIC")
            {
                var la = Path.Combine(Home.SetDir(s.Id), "last-attempt.txt");
                if (!File.Exists(la) || !File.ReadAllText(la).Contains("BS_STOP_SUCCESS")) return false;
            }
            else
            {
                var st = new LocalState(Home.SetDir(s.Id));
                if (string.IsNullOrEmpty(st.LastSuccess) || s.Type != "FILE") return false;
            }
            var p = Path.Combine(Home.SetDir(s.Id), "last-restore-test.txt");
            DateTime last;
            if (File.Exists(p) && RunId.TryParse(File.ReadAllText(p).Trim(), out last) && (utc - last).TotalDays < 30) return false;
            File.WriteAllText(p, RunId.From(utc));
            return true;
        }

        public Restore RestoreFor(Client session, string setId, string secret = null, byte[] recoveredKey = null)
        {
            var s = Core.Profile.Parse(session.Call("GET", "/api/profile")["profile"]).Sets.FirstOrDefault(x => x.Id == setId);
            if (s == null) throw new AgentException(404, "NO_SET", "The backup set does not exist.");
            return new Restore(session, s, Key(s, secret, recoveredKey), Path.Combine(Home.Dir, "temp"));
        }

        /// <summary>Restore from the local copy (no internet): the key from this computer or from the password.</summary>
        public Restore RestoreLocal(BackupSetInfo s, string secret = null, byte[] recoveredKey = null)
        {
            return new Restore(new LocalSource(new LocalRepo(s.LocalCopyPath, Home.Login, s.Id)), Key(s, secret, recoveredKey), Path.Combine(Home.Dir, "temp"));
        }

        /// <summary>
        /// Automatic restore test (the "0" of 3-2-1-1-0): a random sample of files whose source did not change since the
        /// last backup is restored to a temporary folder and compared byte by byte with the source. The result is reported
        /// to the server (LAST_RESTORE_TEST) and ITSguard alerts when a set has no successful test for 30 days.
        /// </summary>
        public Msg RestoreTest(string setId, int sample = 5, int? seed = null)
        {
            var s = Sets().FirstOrDefault(x => x.Id == setId);
            if (s == null) throw new AgentException(404, "NO_SET", "The backup set does not exist.");
            if (s.Engine == "RESTIC")
            {
                // restic check reading 5% of the data: every byte read is authenticated with the set's key
                var t = Restic(s).Check();
                t.Add("log", new Msg().Set("l", AhsayLog.Line(SystemClock.UtcNow, t.Int("ok") == 1 ? "info" : "err", message: "restore test (restic check --read-data-subset 5%): " + t["message"])));
                DeviceClient().Call("POST", "/api/sets/" + s.Id + "/restoretest", t);
                return t;
            }
            var key = Key(s);
            var client = DeviceClient();
            var state = new LocalState(Home.SetDir(s.Id));
            var r = new Restore(new ServerSource(client, s.Id, true), key, Path.Combine(Home.Dir, "temp"));
            var candidates = r.Files(null).Where(kv =>
            {
                var fi = new FileInfo(kv.Key);
                return fi.Exists && fi.Length == kv.Value.Long("orig") && RunId.UnixMs(fi.LastWriteTimeUtc) == kv.Value.Long("mtime") && fi.Length < 512L * 1024 * 1024;
            }).ToList();
            var rnd = seed == null ? new Random() : new Random(seed.Value);
            var pick = candidates.OrderBy(x => rnd.Next()).Take(sample).ToList();
            int ok = 0, failed = 0;
            var dir = Path.Combine(Home.Dir, "temp", "restoretest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            var log = new Msg();
            try
            {
                foreach (var kv in pick)
                {
                    var dest = Path.Combine(dir, Guid.NewGuid().ToString("N"));
                    try
                    {
                        r.RestoreFile(kv.Value, dest);
                        bool same = FilesEqual(dest, kv.Key);
                        if (same) ok++; else failed++;
                        log.Add("log", new Msg().Set("l", AhsayLog.Line(SystemClock.UtcNow, same ? "info" : "err", kv.Key, message: same ? "restore test: identical" : "restore test: DIFFERENT from the source")));
                    }
                    catch (Exception e) { failed++; log.Add("log", new Msg().Set("l", AhsayLog.Line(SystemClock.UtcNow, "err", kv.Key, message: "restore test failed: " + e.Message))); }
                }
            }
            finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
            log.Set("checked", ok + failed).Set("ok", ok).Set("failed", failed).Set("candidates", candidates.Count);
            client.Call("POST", "/api/sets/" + s.Id + "/restoretest", log);
            return log;
        }

        static bool FilesEqual(string a, string b)
        {
            using (var fa = File.OpenRead(a))
            using (var fb = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (fa.Length != fb.Length) return false;
                var ba = new byte[1 << 16]; var bb = new byte[1 << 16];
                while (true)
                {
                    int ra = fa.Read(ba, 0, ba.Length), rb = fb.Read(bb, 0, bb.Length);
                    if (ra != rb) return false;
                    if (ra == 0) return true;
                    for (int i = 0; i < ra; i++) if (ba[i] != bb[i]) return false;
                }
            }
        }

        // ---------------------------------------------------------------- scheduler (the service)

        /// <summary>
        /// The last scheduled time at or before "now" (daily, or the chosen week days). A run missed because the computer
        /// was off or offline is caught up as soon as it can run — no reminder needed.
        /// </summary>
        public static DateTime? LastSlot(BackupSetInfo s, DateTime nowLocal)
        {
            // SCHED-020: the latest of all the set's schedules
            DateTime? best = LastSlot(s.Days, s.Hour, s.Minute, nowLocal);
            foreach (var x in s.MoreSchedules) { var v = LastSlot(x.Days, x.Hour, x.Minute, nowLocal); if (v != null && (best == null || v > best)) best = v; }
            return best;
        }

        static DateTime? LastSlot(string days, int hour, int minute, DateTime nowLocal)
        {
            for (int back = 0; back < 8; back++)
            {
                var day = nowLocal.Date.AddDays(-back);
                if (days.Length == 7 && days[(int)day.DayOfWeek] == '-') continue;
                var slot = day.AddHours(hour).AddMinutes(minute);
                if (slot <= nowLocal) return slot;
            }
            return null;
        }

        public bool Due(BackupSetInfo s, DateTime nowLocal)
        {
            var slot = LastSlot(s, nowLocal);
            if (slot == null) return false;
            var st = new LocalState(Home.SetDir(s.Id));
            DateTime last;
            var lastOk = RunId.TryParse(st.LastSuccess, out last) ? last.ToLocalTime() : DateTime.MinValue;
            if (lastOk >= slot.Value) return false;
            // MISS-010: a slot missed (the computer was off or offline) runs when the computer is back — unless the IT company
            // chose otherwise: not at all, only when the last backup is old enough, and after a short delay (the computer and
            // the network settle first; the delay counts from the moment the agent saw the missed slot)
            var off = LastOffline();
            var attemptFile = Path.Combine(Home.SetDir(s.Id), "last-attempt.txt");
            DateTime attempt = DateTime.MinValue; string attemptResult = "";
            if (File.Exists(attemptFile))
            {
                var af = File.ReadAllText(attemptFile).Split('\t');
                if (RunId.TryParse(af[0], out attempt)) attempt = attempt.ToLocalTime(); else attempt = DateTime.MinValue;
                attemptResult = af.Length > 1 ? af[1].Trim() : "";
            }
            // MISS-020: the slot came while the internet was down, or the run was cut off by it — as soon as it is back
            bool cutOff = attemptResult == "NETWORK" && attempt >= slot.Value;
            bool missedOffline = off != null && slot.Value >= off.Item1.AddMinutes(-1) && slot.Value <= off.Item2;
            if (cutOff || (missedOffline && (nowLocal - slot.Value).TotalMinutes > 15))
            {
                if (!s.RunMissedNet) return false;
                if (cutOff && off != null && off.Item2 > attempt) return true;           // back since the run was cut off: now
                if (!cutOff && attempt < off.Item2) return true;                          // missed while offline: when back
            }
            else if ((nowLocal - slot.Value).TotalMinutes > 15)
            {
                if (!s.RunMissed) return false;
                if (s.MissedMinHours > 0 && lastOk > nowLocal.AddHours(-s.MissedMinHours)) return false;
                if (s.MissedDelayMinutes > 0)
                {
                    var seenFile = Path.Combine(Home.SetDir(s.Id), "missed-seen.txt");
                    var key = slot.Value.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    var seen = File.Exists(seenFile) ? File.ReadAllText(seenFile).Split('\t') : new string[0];
                    long first;
                    if (seen.Length != 2 || seen[0] != key || !long.TryParse(seen[1], out first)) { File.WriteAllText(seenFile, key + "\t" + nowLocal.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)); return false; }
                    if ((nowLocal - new DateTime(first)).TotalMinutes < s.MissedDelayMinutes) return false;
                }
            }
            if (attempt != DateTime.MinValue && (nowLocal - attempt).TotalMinutes < 15) return false;   // retry every 15 minutes
            return true;
        }

        /// <summary>
        /// SRC-030: this computer's folders for the admin site's tree — every 6 hours, and within a minute when the IT
        /// company opens a folder there (a BROWSE request in the profile). The opened folders are kept and sent every time.
        /// </summary>
        public bool SendFolders(Profile prof, DateTime nowUtc, bool force = false)
        {
            var stateFile = Path.Combine(Home.Dir, "folders-sent.txt"); var openFile = Path.Combine(Home.Dir, "folders-open.txt");
            long last = 0; if (File.Exists(stateFile)) long.TryParse(File.ReadAllText(stateFile).Trim(), out last);
            var open = File.Exists(openFile) ? File.ReadAllLines(openFile).Where(x => x.Length > 0).ToList() : new List<string>();
            bool asked = false;
            foreach (var b in prof.Root.Elements("BROWSE").Where(x => string.Equals((string)x.Attribute("COMPUTER"), Home.Computer, StringComparison.OrdinalIgnoreCase)))
            {
                long at; long.TryParse((string)b.Attribute("AT"), out at);
                if (at <= last) continue;
                asked = true;
                var path = (string)b.Attribute("PATH");
                if (!string.IsNullOrEmpty(path) && !open.Contains(path, StringComparer.OrdinalIgnoreCase)) open.Add(path);
            }
            long now = RunId.UnixMs(nowUtc);
            if (!force && !asked && now - last < 6 * 3600 * 1000L) return false;
            if (open.Count > 200) open = open.Skip(open.Count - 200).ToList();
            var starts = FolderScan.Roots().Select(r => new KeyValuePair<string, int>(r, 3)).ToList();
            foreach (var s in prof.Sets.Where(x => x.Computer == null || x.Computer.Equals(Home.Computer, StringComparison.OrdinalIgnoreCase)))
                foreach (var src in s.Sources) starts.Add(new KeyValuePair<string, int>(src, 1));
            foreach (var o in open) starts.Add(new KeyValuePair<string, int>(o, 2));
            var dirs = FolderScan.Scan(starts);
            DeviceClient().Call("POST", "/api/folders", new Msg().Set("computer", Home.Computer).Set("dirs", string.Join("\n", dirs.ToArray())).Set("sep", Path.DirectorySeparatorChar.ToString()));
            File.WriteAllLines(openFile, open.ToArray());
            File.WriteAllText(stateFile, now.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return true;
        }

        public void ServiceLoop(CancellationToken stop, Action<string> say)
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    Profile prof;
                    try { prof = Profile(); }
                    catch (AgentException e) when (e.Code == "NETWORK") { NoteOffline(SystemClock.UtcNow); throw; }
                    NoteOnline(SystemClock.UtcNow);
                    // R1: after a restart, the runs that died with the computer are reported at once (not at the next backup)
                    foreach (var s in prof.Sets) try { if (ReportInterrupted(s.Id)) say(s.Name + ": the previous backup was interrupted — reported"); } catch (AgentException) { }
                    ClientUpdate.Auto(this, say);   // UPD-020: the newest client from the server, by itself
                    try { SendFolders(prof, SystemClock.UtcNow); } catch (Exception e) { say("folders: " + e.Message); }
                    foreach (var s in prof.Sets.Where(x => string.IsNullOrEmpty(x.Computer) || x.Computer.Equals(Home.Computer, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (RunRequested(s) || Due(s, SystemClock.Now)) { var r = Backup(s.Id); say(s.Name + ": " + r.Result); }
                        else if (LogDue(s, SystemClock.UtcNow)) { var r = Backup(s.Id, "LOG"); say(s.Name + " (log): " + r.Result); }
                        if (RestoreTestDue(s, SystemClock.UtcNow)) { var t = RestoreTest(s.Id); say(s.Name + " restore test: " + t["ok"] + "/" + t["checked"]); }
                    }
                }
                catch (AgentException e) { say("waiting: " + e.Message); }
                catch (Exception e) { say("error: " + e.Message); }
                stop.WaitHandle.WaitOne(TimeSpan.FromMinutes(1));
            }
        }
    }
}
