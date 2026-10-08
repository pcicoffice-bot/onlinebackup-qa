using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using OnlineBackup.Core;

namespace OnlineBackup.Server
{
    /// <summary>
    /// The HTTP(S) API for agents and administrators. Messages are small XML (Core.Msg); objects stream as raw bytes.
    /// In production it listens on HTTPS on a dedicated port (certificate bound to the port by the installer).
    /// </summary>
    public sealed partial class Api : IDisposable
    {
        Timer licenseTimer, updateTimer;
        readonly SystemConfig cfg;
        readonly Users users;
        readonly HttpListener listener = new HttpListener();
        readonly CancellationTokenSource stop = new CancellationTokenSource();
        readonly Mailer mailer;
        readonly Exporter exporter;
        public Replicator Replication { get; private set; }
        Timer maintenance;
        public Users UserStore { get { return users; } }
        public readonly Tickets Calls;
        public readonly RunLog Runs;
        readonly Dictionary<string, Msg> live = new Dictionary<string, Msg>();
        /// <summary>LIVE-010: the backups running now (reported in the last 3 minutes; a finished run leaves at once).</summary>
        public List<Msg> Live(Func<string, bool> visible = null)
        {
            var since = RunId.UnixMs(SystemClock.UtcNow.AddMinutes(-3));
            // a run that stopped answering is left out at once; SweepInterrupted closes it as interrupted after SetStore.Lease
            lock (live) return live.Values.Where(m => m.Long("time") >= since && (visible == null || visible(m["login"]))).OrderBy(m => m.Long("started")).ToList();
        }
        void LiveDone(string login, string setId) { lock (live) live.Remove(login + "/" + setId); }

        /// <summary>
        /// R1 (GPT audit 1–3): the runs whose agent died. Every minute: a running backup that sent no sign of life (progress,
        /// object, heartbeat) for <see cref="SetStore.Lease"/> is closed — its open run released and recorded in the history
        /// as a failure (log, tasks, service call, mail). Also the open runs left on disk (a server restart forgets the
        /// live list). A run that answers again later commits normally; its real result replaces this log.
        /// </summary>
        public void SweepInterrupted(DateTime nowUtc)
        {
            var cut = RunId.UnixMs(nowUtc - SetStore.Lease);
            List<Msg> stale;
            lock (live) { stale = live.Values.Where(m => m.Long("time") < cut).ToList(); foreach (var m in stale) live.Remove(m["login"] + "/" + m["set"]); }
            var seen = new HashSet<string>();
            foreach (var m in stale)
                try
                {
                    var login = m["login"]; var setId = m["set"];
                    // bug 113: a set deleted (to the recycle bin) while its backup ran - opening its store here made a new empty
                    // files/<set> folder, and the set could no longer come back from the bin
                    if (!Directory.Exists(Path.Combine(users.UserDir(login), "files", setId))) continue;
                    var store = new SetStore(users.UserDir(login), setId);
                    store.ExpireStale(nowUtc);
                    if (!string.IsNullOrEmpty(m["job"])) { seen.Add(login + "/" + setId + "/" + m["job"]); RecordInterrupted(login, setId, m["job"], m.Long("started"), "no sign of life from the computer for " + (int)SetStore.Lease.TotalMinutes + " minutes"); }
                }
                catch (Exception e) { SysLog.Write(null, "System", "error: interrupted run " + m["login"] + "/" + m["set"] + ": " + e.Message); }
            foreach (var login in users.Logins())
                try
                {
                    var files = Path.Combine(users.UserDir(login), "files");
                    if (!Directory.Exists(files)) continue;
                    foreach (var setDir in Directory.GetDirectories(files))
                        foreach (var jd in SetStore.OpenJobDirs(setDir).Where(d => SetStore.LeaseExpired(d, nowUtc)).ToList())
                        {
                            var setId = Path.GetFileName(setDir); var job = Path.GetFileName(jd);
                            var gone = new SetStore(users.UserDir(login), setId).ExpireStale(nowUtc);
                            foreach (var g in gone) if (seen.Add(login + "/" + setId + "/" + g)) RecordInterrupted(login, setId, g, 0, "no sign of life from the computer for " + (int)SetStore.Lease.TotalMinutes + " minutes");
                        }
                }
                catch (Exception e) { SysLog.Write(null, "System", "error: open runs " + login + ": " + e.Message); }
        }

        /// <summary>
        /// One interrupted run into the history, the same way as any failed run (job log, tasks, service call, mail) —
        /// once: a run that already has its log (committed, reported, or recorded before) is left as it is.
        /// </summary>
        // Bug 19 / 20: the end of one run can arrive more than once — the agent's client sends a request again when the answer
        // does not come (a cut connection, the 5-minute timeout while a big commit runs), and an interrupted run is reported by
        // the agent, the minute sweep and the next Begin. Every end of a run goes through the set's gate, and a run that
        // already has its log has ended: the repeat gets the same answer and records nothing again (no second history row,
        // mail or service call), and is never an error that makes the computer think a stored backup failed.
        readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> endGates = new System.Collections.Concurrent.ConcurrentDictionary<string, object>();
        object EndGate(string login, string setId) { return endGates.GetOrAdd(login + "/" + setId, _ => new object()); }
        string EndedLog(string login, string setId, string job) { return Path.Combine(users.UserDir(login), "logs", setId, "Backup", job + ".log"); }
        // the commit's answer, kept beside the set's data (not among the logs, whose folder keeps Ahsay's layout) for 30 days
        string ReplyFile(string login, string setId, string job) { return Path.Combine(users.UserDir(login), "files", setId, "ended", job + ".xml"); }
        void KeepReply(string login, string setId, string job, Msg reply, string ip)
        {
            try
            {
                var rp = ReplyFile(login, setId, job); Directory.CreateDirectory(Path.GetDirectoryName(rp));
                Atomic.WriteBytes(rp, reply.ToBytes());
                foreach (var old in Directory.GetFiles(Path.GetDirectoryName(rp), "*.xml")) if ((SystemClock.UtcNow - File.GetLastWriteTimeUtc(old)).TotalDays > 30) File.Delete(old);
            }
            catch (Exception e) { SysLog.Write(ip, "System", "error: run reply " + e.Message); }
        }
        static string ReportDigest(Msg b)
        {
            using (var h = System.Security.Cryptography.SHA256.Create())
                return Bytes.Hex(h.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", b.List("log").Select(l => l["l"] ?? "")) + "|" + b["result"])));
        }
        Msg EndedReply(string login, string setId, string job)
        {
            var log = EndedLog(login, setId, job);
            if (!File.Exists(log)) return null;
            try { var rp = ReplyFile(login, setId, job); if (File.Exists(rp)) return Msg.Parse(OnlineBackup.Core.Atomic.ReadAllBytes(rp)).Set("repeat", 1); } catch (Exception) { }
            return new Msg().Set("ok", 1).Set("job", job).Set("repeat", 1);
        }

        /// <summary>The first frame of the product's own code in an exception (the system log line of a server error).</summary>
        static string Where(Exception e)
        {
            var st = e.StackTrace ?? "";
            foreach (var l in st.Split('\n')) if (l.Contains("OnlineBackup.")) return l.Trim();
            return st.Split('\n')[0].Trim();
        }

        void RecordInterrupted(string login, string setId, string job, long startedMs, string why)
        {
            if (!RunId.TryParse(job ?? "", out _)) return;
            LiveDone(login, setId);
            var prof = users.LoadProfile(login);
            var set = prof.Sets.FirstOrDefault(x => x.Id == setId);
            if (set == null) return;
            var now = SystemClock.UtcNow;
            var started = startedMs > 0 ? RunId.FromUnixMs(startedMs) : RunId.TryParse(job, out var t) ? t : now;
            var b = new Msg().Set("result", "BS_STOP_BY_SYSTEM_ERROR").Set("started", RunId.UnixMs(started)).Set("new", 0).Set("upd", 0).Set("del", 0).Set("bytes", 0);
            foreach (var l in new[] { AhsayLog.Line(started, "start"),
                AhsayLog.Line(now, "err", message: "The backup was interrupted and did not finish: " + why + " (the computer was shut down or restarted, or the backup program was stopped). Nothing of this run was kept; the next backup sends everything again that is needed."),
                AhsayLog.Line(now, "end", message: "BS_STOP_BY_SYSTEM_ERROR") })
                b.Add("log", new Msg().Set("l", l));
            string logFile;
            lock (EndGate(login, setId))
            {
                if (File.Exists(EndedLog(login, setId, job))) return;
                logFile = WriteJobLog(login, setId, "Backup", job, b);
            }
            SetLastResult(login, setId, "BS_STOP_BY_SYSTEM_ERROR");
            SysLog.Write(null, "Access", "backup interrupted " + login + "/" + setId + " " + job + ": " + why);
            TicketRun(login, set, b);
            Runs.Add(now, login, set, "Backup", job, b, logFile);
            try { mailer.BackupReport(prof, set, job, b, set.Engine == "RESTIC" ? ResticStats(login, setId, prof.FindSet(setId), null) : new SetStore(users.UserDir(login), setId).Stats()); } catch (Exception e) { SysLog.Write(null, "System", "error: report mail " + e.Message); }
            exporter.Log(login, setId, job, logFile);
        }
        readonly Dictionary<string, int> testDownloads = new Dictionary<string, int>();

        public Api(SystemConfig cfg)
        {
            this.cfg = cfg;
            // LOAD-010: many computers at the same moment (the hour of the backups) — threads ready at once, not added one
            // every half second by the thread pool (a sudden wave waited up to 2 seconds)
            int w, io; ThreadPool.GetMinThreads(out w, out io); if (w < 64) ThreadPool.SetMinThreads(64, Math.Max(io, 64));
            users = new Users(cfg);
            SysLog.Init(cfg.SystemHome);
            mailer = new Mailer(cfg);
            exporter = new Exporter(cfg);
            if (Guard.Mail == null) Guard.Mail = (c, subject, html) => new Mailer(c).Alert(subject, html);   // GUARD-010
            Replication = new Replicator(cfg, users);
            Calls = new Tickets(cfg) { Clock = () => cfg.Clock() };
            Runs = new RunLog(cfg);
            // TCN (ITSguard): the handler gets an e-mail when a call is assigned to them
            Calls.Assigned = (t, by) => Task.Run(() => mailer.Send(Staff.Mails(cfg, t["assignee"]), "Service call #" + t["id"] + " — " + t["subject"],
                "<p>" + Fmt.H(by == Tickets.SystemUser ? "The server" : by) + " assigned you the service call #" + Fmt.H(t["id"]) + ": " + Fmt.H(t["subject"]) + "</p><p>" + Fmt.H(t["login"]) + " " + Fmt.H(t["computer"]) + " — " + Fmt.H(t["priority"]) + "</p>", "ticket"));
        }

        public void Start(string prefix)
        {
            listener.Prefixes.Add(prefix);
            listener.Start();
            SysLog.Write(null, "System", "server started " + prefix);
            Task.Run(() => Loop());
            // Recover interrupted commits of every set before agents connect, then daily maintenance.
            foreach (var login in users.Logins())
                try { foreach (var s in users.LoadProfile(login).Sets) new SetStore(users.UserDir(login), s.Id); }
                catch (Exception e) { SysLog.Write(null, "System", "error: recovery " + login + ": " + e.Message); }
            // LIC-110: check in with the licensing centre soon after start, then daily
            licenseTimer = new Timer(_ => { try { LicenseCheckin.Run(cfg, users, mailer, cfg.Clock()); } catch (Exception e) { SysLog.Write(null, "System", "error: licence check-in " + e.Message); } },
                null, TimeSpan.FromSeconds(20), TimeSpan.FromHours(24));
            maintenance = new Timer(_ => { try { Maintenance(SystemClock.UtcNow); } catch (Exception e) { SysLog.Write(null, "System", "error: maintenance " + e.Message); } },
                null, TimeSpan.FromHours(1), TimeSpan.FromHours(24));
            // UPD-040: the owner's automatic updates — checked every hour, installed only at night
            updateTimer = new Timer(_ => { try { Updater.Auto(cfg, DateTime.Now); } catch (Exception e) { SysLog.Write(null, "Update", "automatic update: " + e.Message); } }, null, TimeSpan.FromMinutes(10), TimeSpan.FromHours(1));
            Replication.Start();
            sweepTimer = new Timer(_ => { try { SweepInterrupted(SystemClock.UtcNow); } catch (Exception e) { SysLog.Write(null, "System", "error: interrupted runs " + e.Message); } }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        }
        Timer sweepTimer;

        public void Dispose()
        {
            stop.Cancel();
            Replication.Stop();
            if (maintenance != null) maintenance.Dispose();
            if (sweepTimer != null) sweepTimer.Dispose();
            if (licenseTimer != null) licenseTimer.Dispose(); if (updateTimer != null) updateTimer.Dispose();
            try { listener.Stop(); listener.Close(); } catch { }
        }

        void Loop()
        {
            while (!stop.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = listener.GetContext(); } catch { return; }
                ThreadPool.QueueUserWorkItem(_ => Handle(ctx));
            }
        }

        void Handle(HttpListenerContext ctx)
        {
            // Never let one request take the server down (an exception on a pool thread ends the process).
            try { HandleSafe(ctx); } catch (Exception e) { try { SysLog.Write(null, "System", "error: request: " + e.Message); } catch { } }
        }

        void HandleSafe(HttpListenerContext ctx)
        {
            string ip = "-";
            try { if (ctx.Request.RemoteEndPoint != null) ip = ctx.Request.RemoteEndPoint.Address.ToString(); } catch { }
            try
            {
                // GUARD-010: an address that guessed passwords or scanned the server is refused everything
                Guard.Tried = null;
                if (Guard.IsBlocked(cfg, ip) && !Guard.Trusted(cfg, ip)) throw new ApiException(403, "BLOCKED", "Too many failed attempts from your address — it is blocked for a while. Ask your provider if this is a mistake.");
                var path = ctx.Request.Url.AbsolutePath.TrimEnd('/');
                var seg = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                if (seg.Length >= 1 && seg[0] == "admin")
                {
                    if (seg.Length > 1 && (seg[1] == "restore.html" || seg[1] == "restore.js")) PilotScope.Check(cfg, PilotScope.WebRestore);   // bug 114: also under /admin
                    AdminUi.Serve(ctx, seg); return;
                }
                // I18N-010; R1 (found by the AI QA): the fonts are one level deeper (/i18n/fonts/x.woff2) — every one was a 404
                if (seg.Length >= 2 && seg.Length <= 3 && seg[0] == "i18n") { AdminUi.ServeI18n(ctx, string.Join("/", seg.Skip(1))); return; }
                if (seg.Length >= 1 && seg[0] == "restore") { PilotScope.Check(cfg, PilotScope.WebRestore); AdminUi.Serve(ctx, seg.Length == 1 ? new[] { "admin", "restore.html" } : new[] { "admin", seg[1] }); return; }   // WEB-010
                if (seg.Length >= 1 && seg[0] == "restic") { Restic(ctx, seg, ip); return; }
                if (seg.Length < 2 || seg[0] != "api") throw new ApiException(404, "NOT_FOUND", "Not found.");
                if (seg[1] == "brand")
                {
                    var b = cfg.Doc.Root.Element("BRANDING") ?? new XElement("BRANDING");
                    var bp = (string)b.Attribute("PRODUCT");   // BRAND-010: every screen shows the brand — name, slogan, logo — also before sign-in
                    Reply(ctx, 200, new Msg().Set("brandPRODUCT", string.IsNullOrEmpty(bp) ? License.OwnerProduct : bp).Set("brandSLOGAN", (string)b.Attribute("SLOGAN")).Set("brandLOGO", (string)b.Attribute("LOGO")).Set("brandCOLOR", (string)b.Attribute("COLOR")).Set("brandACCENT", (string)b.Attribute("ACCENT")).Set("brandCOMPANY", (string)b.Attribute("COMPANY")).Set("brandLANGUAGE", L.Norm((string)b.Attribute("LANGUAGE"))));
                    return;
                }
                if (seg[1] == "admin") Admin(ctx, seg, ip);
                else if (seg[1] == "replica") Replica(ctx, seg, ip);
                else Agent(ctx, seg, ip);
            }
            catch (Exception e) when (e is System.Xml.XmlException || e is FormatException || e is OverflowException || e is ArgumentNullException)
            {
                // FUZZ-010: a request with missing or malformed values is the sender's mistake — a clear refusal, not a server error
                SysLog.Write(ip, "System", "warn: refused request " + ctx.Request.HttpMethod + " " + ctx.Request.Url.AbsolutePath + ": " + e.GetType().Name);
                Reply(ctx, 400, new Msg().Set("error", "BAD_REQUEST").Set("message", "The request is not valid."));
            }
            catch (ApiException e)
            {
                // GUARD-010: wrong sign-ins (also on a locked account) and scanning count toward blocking the address
                try
                {
                    if ((e.Status == 401 && e.Code == "LOGIN") || (e.Status == 423 && e.Code == "LOCKED")) Guard.Failed(cfg, ip, Guard.Tried);
                    else if ((e.Status == 404 && e.Code == "NOT_FOUND") || (e.Status == 401 && (e.Code == "DEVICE" || e.Code == "SESSION") && string.IsNullOrEmpty(ctx.Request.Headers["X-Device"]) && string.IsNullOrEmpty(ctx.Request.Headers["X-Session"]))) Guard.Refused(cfg, ip);   // a scanner: no token at all
                }
                catch (Exception) { }
                Reply(ctx, e.Status, new Msg().Set("error", e.Code).Set("message", e.Message));
            }
            catch (Exception e)
            {
                SysLog.Write(ip, "System", "error: " + ctx.Request.HttpMethod + " " + ctx.Request.Url.AbsolutePath + ": " + e.GetType().Name + " " + e.Message + " @ " + Where(e));   // where, so a 500 can be traced (run 37594312039)
                Reply(ctx, 500, new Msg().Set("error", "SERVER").Set("message", "Server error. The details are in the system log."));
            }
        }

        static void Reply(HttpListenerContext ctx, int status, Msg m)
        {
            try
            {
                var b = m.ToBytes();
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = "application/xml; charset=utf-8";
                ctx.Response.ContentLength64 = b.Length;
                ctx.Response.OutputStream.Write(b, 0, b.Length);
                ctx.Response.OutputStream.Close();
            }
            catch { }
        }

        /// <summary>FUZZ-010: the message of a request — refused clearly (never a server error) when it is not one, or too large.</summary>
        static Msg Body(HttpListenerContext ctx)
        {
            const int Max = 20 * 1024 * 1024;
            if (ctx.Request.ContentLength64 > Max) throw new ApiException(413, "TOO_LARGE", "The request is too large.");
            var ms = new MemoryStream(); var buf = new byte[81920]; int n;
            while ((n = ctx.Request.InputStream.Read(buf, 0, buf.Length)) > 0) { ms.Write(buf, 0, n); if (ms.Length > Max) throw new ApiException(413, "TOO_LARGE", "The request is too large."); }
            try { return Msg.Parse(ms.ToArray()); }
            catch (Exception e) when (e is System.Xml.XmlException || e is ArgumentException || e is InvalidOperationException) { throw new ApiException(400, "BAD_REQUEST", "The request is not valid."); }
        }
        static string Q(HttpListenerContext ctx, string k) { return ctx.Request.QueryString[k]; }
        static void Need(bool cond) { if (!cond) throw new ApiException(404, "NOT_FOUND", "Not found."); }

        // ---------------------------------------------------------------- agent

        void Agent(HttpListenerContext ctx, string[] seg, string ip)
        {
            string method = ctx.Request.HttpMethod;
            PilotScope.CheckAgent(cfg, ctx.Request.Headers["X-Agent"]);   // PILOT-010 / AG-08: Windows XP / 2003
            // CONTRACT-010: the contract before sign-in (installer, sign-up); SIGNUP-010: a new customer from the client software
            if (seg[1] == "contract" && method == "GET") { Reply(ctx, 200, Contract.Public(cfg, Q(ctx, "lang"))); return; }
            if (seg[1] == "signup" && method == "POST")
            {
                PilotScope.Check(cfg, PilotScope.Signup);   // PILOT-010 / AU-03
                var b = Body(ctx);
                var p = Contract.Signup(users, cfg, b, ip, SystemClock.UtcNow);
                var newLogin = p.Get("LOGIN_NAME");
                exporter.Profile(newLogin, users.UserDir(newLogin));
                Replication.Enqueue(new Msg().Set("type", "user").Set("login", newLogin).Set("quota", p.Get("QUOTA")));
                Replication.Enqueue(new Msg().Set("type", "db").Set("login", newLogin));
                mailer.Alert("New customer — " + p.Get("ALIAS"), "<p>" + Fmt.H(p.Get("ALIAS")) + " (" + Fmt.H(newLogin) + ", " + Fmt.H(b["email"]) + ") signed up from the client software.</p>");
                Reply(ctx, 200, new Msg().Set("login", newLogin));
                return;
            }
            if (seg[1] == "register" && method == "POST")
            {
                var b = Body(ctx);
                var p = users.CheckUser(b["login"], b["password"], b["otp"], ip);
                ContractGate(p, b, ip);
                if (p.Get("REQUIRE_TOTP") == "Y" && string.IsNullOrEmpty(p.Get("TOTP_SECRET")))
                    SysLog.Write(ip, "Access", "warn: " + b["login"] + " registered a device without 2FA although the policy requires it");
                Reply(ctx, 200, new Msg().Set("device", users.RegisterDevice(p.Get("LOGIN_NAME"), b["computer"], ip)));
                return;
            }
            if (seg[1] == "login" && method == "POST")
            {
                var b = Body(ctx);
                var p = users.CheckUser(b["login"], b["password"], b["otp"], ip);
                ContractGate(p, b, ip);
                Reply(ctx, 200, new Msg().Set("session", users.NewSession(p.Get("LOGIN_NAME"), false)).Set("totp", string.IsNullOrEmpty(p.Get("TOTP_SECRET")) ? "N" : "Y"));
                return;
            }

            // Everything else: a device token (scheduled work) or an interactive session (password + 2FA).
            string login; bool interactive = false;
            var session = users.GetSession(ctx.Request.Headers["X-Session"]);
            if (session != null && !session.Admin)
            {
                // H-04: the customer's "Sign out" ends the sign-in on the server too
                if (seg.Length == 2 && seg[1] == "logout" && method == "POST") { users.EndSession(ctx.Request.Headers["X-Session"]); Reply(ctx, 200, new Msg().Set("ok", 1)); return; }
                users.CheckSessionUser(session.Login, ip);
                login = session.Login; interactive = true;
            }
            else login = users.CheckDevice(ctx.Request.Headers["X-Device"], ip, ctx.Request.Headers["X-Agent"]);
            Func<bool> requireInteractive = () => { if (!interactive) throw new ApiException(401, "SESSION", "This action requires signing in with a password and verification code."); return true; };

            // UPD-020: the client software on the computers updates itself from this server — the files of the Windows client
            // this server carries (its own version), compared by SHA-256; only changed files are downloaded.
            // Owner decision B1 (pilot): off in Pilot 1 until agent updates are signed and refuse older versions — with the
            // switch neither the list nor a file is offered (the computer's automatic update and its "Update" use these two)
            if (seg[1] == "client" && seg.Length == 3 && (seg[2] == "files" || seg[2] == "file") && method == "GET") PilotScope.Check(cfg, PilotScope.ClientUpdate);
            if (seg[1] == "client" && seg.Length == 3 && seg[2] == "files" && method == "GET") { Reply(ctx, 200, ClientFiles.List()); return; }
            if (seg[1] == "client" && seg.Length == 3 && seg[2] == "file" && method == "GET")
            {
                var path = ClientFiles.Path(Q(ctx, "name")) ?? throw new ApiException(404, "NOT_FOUND", "No such file.");
                var res = ctx.Response; res.StatusCode = 200; res.ContentType = "application/octet-stream";
                using (var f = File.OpenRead(path)) { res.ContentLength64 = f.Length; f.CopyTo(res.OutputStream, 1 << 20); }
                res.Close(); return;
            }
            if (seg[1] == "quota" && method == "GET") { Reply(ctx, 200, QuotaNow(login)); return; }
            if (seg[1] == "folders" && seg.Length == 2 && method == "POST") { FolderTree.Save(users, login, Body(ctx)); Reply(ctx, 200, new Msg().Set("ok", 1)); return; }
            if (seg[1] == "profile" && method == "GET") { var pr = users.LoadProfile(login); Reply(ctx, 200, new Msg().Set("profile", SafeProfile(pr, cfg.Pilot)).Add("rights", SetControl.RightsMsg(pr))); return; }

            if (seg[1] == "totp" && seg.Length == 3 && method == "POST")
            {
                requireInteractive();
                if (seg[2] == "enable") { Reply(ctx, 200, users.EnableTotp(login, ip)); return; }
                if (seg[2] == "confirm") { users.ConfirmTotp(login, Body(ctx)["code"], ip); Reply(ctx, 200, new Msg().Set("ok", 1)); return; }
                if (seg[2] == "disable")
                {
                    // SEC-010: the customer switches it off (signed in with it) — not when the provider requires it
                    if (users.LoadProfile(login).Get("REQUIRE_TOTP") == "Y") throw new ApiException(403, "TOTP_REQUIRED", "Your provider requires two-step verification.");
                    users.ResetTotp(login, login, ip); Reply(ctx, 200, new Msg().Set("ok", 1)); return;
                }
            }

            if (seg[1] == "webrestore") { PilotScope.Check(cfg, PilotScope.WebRestore); requireInteractive(); WebRestoreApi(ctx, seg, login, ip); return; }
            // TICKETS-030: the customer opens a call from the client ("help") and sees its own calls — never another customer's
            if (seg[1] == "tickets" && seg.Length == 2)
            {
                if (!Calls.Read().ClientCalls) throw new ApiException(403, "OFF", "Service calls from the software are switched off by your provider.");
                if (method == "POST")
                {
                    var b = Body(ctx);
                    Calls.Save(new Msg().Set("login", login).Set("computer", b["computer"]).Set("set", b["set"]).Set("subject", b["subject"]).Set("description", b["description"])
                        .Set("priority", "Normal").Set("status", "New").Set("channel", "client").Set("source", "client"), login, ip);
                }
                var m = new Msg();
                foreach (var t in Calls.List("all", login)) m.Add("tickets", new Msg().Set("id", t["id"]).Set("subject", t["subject"]).Set("status", t["status"]).Set("opened", t["opened"]).Set("updated", t["updated"]));
                Reply(ctx, 200, m); return;
            }

            if (seg[1] == "sets" && seg.Length == 2 && method == "POST")
            {
                requireInteractive();
                var b = Body(ctx);
                if (!SetControl.Can(users.LoadProfile(login), "CAN_ADD_SETS")) throw new ApiException(403, "LOCKED", "Your IT provider adds the backup sets.");
                var s = BackupSetInfo.FromXml(XElement.Parse(b["set"]));
                s = users.CreateSet(login, s, ip);
                Reply(ctx, 200, new Msg().Set("set", users.LoadProfile(login).FindSet(s.Id).ToString(SaveOptions.DisableFormatting)));
                return;
            }

            if (seg[1] == "sets" && seg.Length >= 4)
            {
                var setId = seg[2];
                var prof = users.LoadProfile(login);
                Need(prof.FindSet(setId) != null);
                // bug 114 (scope challenge): a set outside the pilot was refused only at "begin" - its data could still be listed and
                // downloaded, a run opened before the switch committed, and a restore test recorded. Refused here, on the server, for
                // every route that reads or changes its data; "stop"-like routes (abort, interrupted, progress) stay open.
                if (cfg.Pilot && (seg[3] == "points" || seg[3] == "files" || seg[3] == "object" || seg[3] == "restoretest" || seg[3] == "restorelog"
                    || (seg[3] == "jobs" && seg.Length > 5 && (seg[5] == "object" || seg[5] == "delete" || seg[5] == "commit"))))
                    PilotScope.CheckSet(cfg, BackupSetInfo.FromXml(prof.FindSet(setId)));
                // AZF-1: the routes that never touch the set's backups do not open (create) its store - a refused call changes nothing
                var store = seg[3] == "key" || seg[3] == "settings" || seg[3] == "sharedkey" ? null : new SetStore(users.UserDir(login), setId);
                switch (seg[3])
                {
                    case "key":
                        requireInteractive();
                        // owner decision A7: kept only when the IT company keeps recovery copies AND the customer chose it for this set
                        if (prof.Get("SAVE_ENCRYPT_KEY") == "Y" && BackupSetInfo.FromXml(prof.FindSet(setId)).KeyRecovery)
                        {
                            var raw = Convert.FromBase64String(Body(ctx)["key"]);
                            Atomic.WriteBytes(Path.Combine(users.UserDir(login), "db", "keys", setId + ".bin"), KeyVault.Protect(cfg.SystemHome, raw));
                            SysLog.Write(ip, "Access", "encryption key saved for recovery " + login + "/" + setId);
                        }
                        Reply(ctx, 200, new Msg().Set("saved", prof.Get("SAVE_ENCRYPT_KEY") == "Y" && BackupSetInfo.FromXml(prof.FindSet(setId)).KeyRecovery ? "Y" : "N"));
                        return;
                    case "settings":
                        {
                            // SET-040: the customer changes its set — only what its IT company allows
                            requireInteractive(); Need(method == "POST");
                            SetControl.CustomerSave(users, login, setId, BackupSetInfo.FromXml(XElement.Parse(Body(ctx)["set"])), ip);
                            Reply(ctx, 200, new Msg().Set("set", users.LoadProfile(login).FindSet(setId).ToString(SaveOptions.DisableFormatting)));
                            return;
                        }
                    case "sharedkey":
                        {
                            // SET-020: a copy of a set on another computer of the same customer uses the first set's key. The server
                            // hands it only when the customer chose key recovery (the key is already kept here) — else the customer
                            // enters the encryption password on that computer.
                            // Owner decision A7 / AZF-1: "the raw key is never handed to a device token" - only to the customer's
                            // interactive sign-in (password + code) on that computer; the program asks for it once, at sign-in.
                            requireInteractive();
                            var parent = (string)prof.FindSet(setId).Attribute("PARENT_SET");
                            var kp = string.IsNullOrEmpty(parent) ? null : Path.Combine(users.UserDir(login), "db", "keys", parent + ".bin");
                            if (kp == null || prof.Get("SAVE_ENCRYPT_KEY") != "Y" || !File.Exists(kp)) throw new ApiException(404, "NO_KEY", "Enter the set's encryption password on this computer (key recovery is off).");
                            SysLog.Write(ip, "Access", "shared set key given to the copy " + login + "/" + setId + " (from " + parent + ")");
                            Reply(ctx, 200, new Msg().Set("key", Convert.ToBase64String(KeyVault.Unprotect(cfg.SystemHome, OnlineBackup.Core.Atomic.ReadAllBytes(kp)))));
                            return;
                        }
                    case "restic":
                        {
                            // RST-030: a new access token of the set's restic repository (the previous one stops working)
                            PilotScope.Check(cfg, PilotScope.Restic);   // PILOT-010 / RS-03
                            Need(method == "POST" && prof.FindSet(setId) != null && BackupSetInfo.FromXml(prof.FindSet(setId)).Engine == "RESTIC");
                            var rs = new ResticStore(users.UserDir(login), setId);
                            if (!Directory.Exists(rs.Dir)) rs.Create();
                            var token = ResticStore.NewToken(users.UserDir(login), setId);
                            SysLog.Write(ip, "Access", "restic token issued " + login + "/" + setId);
                            Reply(ctx, 200, new Msg().Set("path", "/restic/" + Uri.EscapeDataString(login) + "/" + setId + "/").Set("user", login).Set("token", token));
                            return;
                        }
                    case "resticreport":
                        {
                            // RST-040: the agent's report of one restic run → the same job log, statistics, alerts and mail as any backup
                            PilotScope.Check(cfg, PilotScope.Restic);   // PILOT-010 / RS-03
                            Need(method == "POST");
                            var b = Body(ctx); NormalizeResult(b);
                            var job = b["job"] ?? RunId.From(SystemClock.UtcNow);
                            if (!System.Text.RegularExpressions.Regex.IsMatch(job, @"^\d{4}(-\d{2}){5}$")) throw new ApiException(400, "JOB", "invalid job");
                            lock (EndGate(login, setId))
                            {
                            // the computer names restic runs itself: the same id with the same report is a repeat (answered,
                            // not recorded again); the same id with another report is another run — it gets the next free id
                            var digest = ReportDigest(b);
                            while (File.Exists(EndedLog(login, setId, job)))
                            {
                                var rp = ReplyFile(login, setId, job);
                                if (File.Exists(rp) && Msg.Parse(OnlineBackup.Core.Atomic.ReadAllBytes(rp))["digest"] == digest) { Reply(ctx, 200, new Msg().Set("ok", 1).Set("job", job).Set("repeat", 1)); return; }
                                RunId.TryParse(job, out var jt); job = RunId.From(jt.AddSeconds(1)); b.Set("job", job);
                            }
                            KeepReply(login, setId, job, new Msg().Set("ok", 1).Set("job", job).Set("digest", digest), ip);
                            var logFile = WriteJobLog(login, setId, "Backup", job, b);
                            UpdateStats(login, setId, b);
                            var p2 = users.LoadProfile(login);
                            var set2 = p2.Sets.First(x => x.Id == setId);
                            CheckMassChange(login, p2, set2, job, b);
                            mailer.BackupReport(p2, set2, job, b, ResticStats(login, setId, p2.FindSet(setId), null));
                            AfterRun(login, set2, job, b, logFile);
                            TicketRun(login, set2, b);
                            Runs.Add(SystemClock.UtcNow, login, set2, "Backup", job, b, logFile); LiveDone(login, setId);
                            exporter.Profile(login, users.UserDir(login));
                            exporter.Log(login, setId, job, logFile);
                            }
                            Reply(ctx, 200, new Msg().Set("ok", 1));
                            return;
                        }
                    case "points": Reply(ctx, 200, store.PointsMsg()); return;
                    case "files": Reply(ctx, 200, store.FilesAt(Q(ctx, "point"))); return;
                    case "object":
                        if (Q(ctx, "test") == "1" && !interactive)
                        {
                            // The automatic restore test of the agent: a small daily allowance, logged.
                            var k = login + "/" + setId + "/" + SystemClock.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                            lock (testDownloads) { int n; testDownloads.TryGetValue(k, out n); if (n >= 200) throw new ApiException(429, "LIMIT", "The daily restore-test limit was exceeded."); testDownloads[k] = n + 1; }
                            SysLog.Write(ip, "Access", "restore-test download " + login + "/" + setId);
                        }
                        else requireInteractive();
                        StreamFile(ctx, store.ObjectPath(Q(ctx, "loc"), Q(ctx, "job")));
                        return;
                    case "restoretest":
                        {
                            var b = Body(ctx);
                            var rtLog = WriteJobLog(login, setId, "Restore", RunId.From(SystemClock.UtcNow) + "-test", b);
                            Runs.Add(SystemClock.UtcNow, login, BackupSetInfo.FromXml(prof.FindSet(setId)), "RestoreTest", RunId.From(SystemClock.UtcNow), b, rtLog);
                            bool passed = b.Int("failed") == 0 && b.Int("checked") > 0;
                            lock (users.ProfileLock)
                            {
                                var p = users.LoadProfile(login);
                                var e = p.FindSet(setId);
                                e.SetAttributeValue("LAST_RESTORE_TEST", RunId.UnixMs(SystemClock.UtcNow));
                                // R1 (found by QA F5): nothing to compare (the source is offline, or no file is unchanged since its
                                // backup) is not a failed test — "FAILED 0/0" told the technician the backup was broken
                                e.SetAttributeValue("RESTORE_TEST_RESULT", b.Int("checked") == 0 ? "NOT_CHECKED 0/0" : (passed ? "OK " : "FAILED ") + b["ok"] + "/" + b["checked"]);
                                users.SaveProfile(login, p);
                            }
                            if (!passed && b.Int("checked") > 0)
                                TicketSafe(() => { var p3 = users.LoadProfile(login); var s3 = p3.Sets.FirstOrDefault(x => x.Id == setId); Calls.Auto("restoretest", login, setId, s3 == null ? setId : s3.Name, s3 == null ? null : s3.Computer, "Restore test failed — " + (s3 == null ? setId : s3.Name), b["ok"] + " / " + b["checked"], p3); });
                            if (!passed && b.Int("checked") > 0)
                                mailer.Alert("✗ Restore test failed — " + login, "<p>The automatic restore test of the backup set " + Fmt.H(setId) + " failed: " + Fmt.H(b["ok"]) + " of " + Fmt.H(b["checked"]) + " files are identical to the source.</p>", VendorOf(login));
                            Reply(ctx, 200, new Msg().Set("ok", 1));
                            return;
                        }
                    case "restorelog":
                        requireInteractive();
                        { var rb = Body(ctx); NormalizeRestoreResult(rb); var rl = WriteJobLog(login, setId, "Restore", RunId.From(SystemClock.UtcNow), rb); Runs.Add(SystemClock.UtcNow, login, BackupSetInfo.FromXml(prof.FindSet(setId)), "Restore", RunId.From(SystemClock.UtcNow), rb, rl); }
                        Reply(ctx, 200, new Msg().Set("ok", 1));
                        return;
                    case "begin": PilotScope.CheckSet(cfg, BackupSetInfo.FromXml(prof.FindSet(setId))); Reply(ctx, 200, Begin(login, prof, store, ip, Q(ctx, "key"))); return;
                    case "interrupted":
                        {
                            // R1: the agent found that its own previous run of this set died (power cut, reboot, killed):
                            // released at once and recorded as a failed run
                            Need(method == "POST");
                            var b = Body(ctx);
                            store.AbortIfOpen(b["job"]);
                            RecordInterrupted(login, setId, b["job"], b.Long("started"), b["why"] ?? "the backup program stopped in the middle of the run");
                            Reply(ctx, 200, new Msg().Set("ok", 1));
                            return;
                        }
                    case "progress":
                        {
                            // LIVE-010: a running backup's progress (every 30 s / restic every minute), kept in memory only
                            var b = Body(ctx); var se = prof.FindSet(setId);
                            // Bug 26: a report that arrives after the run ended (a slow request, the heartbeat at the end)
                            // brought the set back to "Running now" for 5 minutes — an ended run stays ended
                            if (RunId.TryParse(b["job"] ?? "", out _) && File.Exists(EndedLog(login, setId, b["job"]))) { Reply(ctx, 200, new Msg().Set("ok", 1).Set("ended", 1)); return; }
                            store.Touch(b["job"]);   // R1: a sign of life of the open run
                            var m = new Msg().Set("login", login).Set("set", setId).Set("setName", (string)se.Attribute("NAME")).Set("computer", (string)se.Attribute("SCHEDULE_HOST")).Set("job", b["job"])
                                .Set("files", b["files"]).Set("bytes", b["bytes"]).Set("current", b["current"]).Set("percent", b["percent"]).Set("time", RunId.UnixMs(SystemClock.UtcNow));
                            lock (live) { Msg old; if (live.TryGetValue(login + "/" + setId, out old) && old["job"] == b["job"]) m.Set("started", old["started"]); else m.Set("started", RunId.UnixMs(SystemClock.UtcNow)); live[login + "/" + setId] = m; }
                            Reply(ctx, 200, new Msg().Set("ok", 1)); return;
                        }
                }
                if (seg.Length >= 5 && seg[3] == "jobs")
                {
                    var job = seg[4];
                    var action = seg.Length > 5 ? seg[5] : "";
                    if (action == "object" && method == "PUT") { Reply(ctx, 200, Upload(ctx, login, prof, store, job)); return; }
                    if (action == "delete" && method == "POST") { foreach (var r in Body(ctx).List("rels")) store.StageDelete(job, r["rel"]); Reply(ctx, 200, new Msg().Set("ok", 1)); return; }
                    if (action == "commit" && method == "POST") { Reply(ctx, 200, Commit(login, store, job, Body(ctx), ip)); return; }
                    if (action == "abort" && method == "POST")
                    {
                        var b = Body(ctx);
                        if (string.IsNullOrEmpty(b["result"]) || b["result"].StartsWith("BS_STOP_SUCCESS", StringComparison.Ordinal)) b.Set("result", "BS_STOP_BY_SYSTEM_ERROR");   // an aborted run is never a success
                        lock (EndGate(login, setId))
                        {
                        var done = EndedReply(login, setId, job);
                        if (done != null) { Reply(ctx, 200, done); return; }   // already ended (a repeat, or a commit whose answer was lost)
                        store.Abort(job);
                        SetLastResult(login, setId, b["result"]);
                        var logFile = WriteJobLog(login, setId, "Backup", job, b);
                        exporter.Log(login, setId, job, logFile);
                        if (logFile != null) Replication.Enqueue(new Msg().Set("type", "log").Set("login", login).Set("path", "logs/" + setId + "/Backup/" + Path.GetFileName(logFile)));
                        var set = prof.Sets.First(x => x.Id == setId);
                        mailer.BackupReport(prof, set, job, b, store.Stats());
                        AfterRun(login, set, job, b, logFile);
                        TicketRun(login, set, b);
                        Runs.Add(SystemClock.UtcNow, login, set, "Backup", job, b, logFile); LiveDone(login, setId);
                        }
                        Reply(ctx, 200, new Msg().Set("ok", 1));
                        return;
                    }
                }
            }
            throw new ApiException(404, "NOT_FOUND", "Not found.");
        }

        /// <summary>The profile as the agent sees it: no password hashes, no 2FA secrets, no LAN passwords.</summary>
        static string SafeProfile(Profile p, bool pilot = false)
        {
            var d = new XDocument(p.Doc);
            if (pilot) d.Root.SetAttributeValue("SERVER_SCOPE", Scope.Pilot);   // PILOT-010: the agent never runs a set outside the pilot
            var u = d.Root.Element("USER");
            u.SetAttributeValue("TOTP_ON", string.IsNullOrEmpty((string)u.Attribute("TOTP_SECRET")) ? "N" : "Y");
            foreach (var a in new[] { "HASHED_PWD", "PASSWORD", "TOTP_SECRET", "TOTP_BACKUP_CODES", "TOTP_PENDING", "TOTP_PENDING_CODES", "RESET_PWD" }) u.SetAttributeValue(a, null);
            foreach (var s in d.Root.Elements("BACKUP_SET")) { s.SetAttributeValue("LAN_PASSWORD", null); s.SetAttributeValue("ADMIN_PASSWORD", null); }
            return d.ToString(SaveOptions.DisableFormatting);
        }

        long Usage(Profile p)
        {
            return p.Get("QUOTA_TYPE") == "UNCOMPRESSED" ? p.GetLong("TOTAL_UNCOMPRESS_FILE_SIZE") + p.GetLong("TOTAL_RETAIN_UNCOMPRESS") : p.GetLong("DATA_SIZE") + p.GetLong("RETAIN_SIZE");
        }

        Msg Begin(string login, Profile prof, SetStore store, string ip, string key = null)
        {
            LicenseStorageCheck();
            long quota = prof.GetLong("QUOTA"), used = Usage(prof);
            if (quota > 0 && used >= quota) throw new ApiException(507, "QUOTA", "The quota is full: new backups are stopped, existing backups are kept.");
            var job = store.BeginJob(SystemClock.UtcNow, key);
            foreach (var dead in store.Expired) RecordInterrupted(login, store.SetId, dead, 0, "no sign of life from the computer for " + (int)SetStore.Lease.TotalMinutes + " minutes");
            var m = new Msg().Set("job", job).Set("quota", quota).Set("used", used).Set("quotaType", prof.Get("QUOTA_TYPE"));
            if (quota > 0 && used * 100 >= quota * Math.Max(1, prof.GetLong("QUOTA_REMIND_PERCENTAGE") == 0 ? 90 : prof.GetLong("QUOTA_REMIND_PERCENTAGE"))) m.Set("quotaWarning", 1);
            foreach (var rel in store.Resend()) m.Add("resend", new Msg().Set("rel", rel));
            m.Set("last", store.LastCommitted() ?? "");
            SysLog.Write(ip, "Access", "backup started " + login + "/" + store.SetId + " " + job);
            return m;
        }

        Msg Upload(HttpListenerContext ctx, string login, Profile prof, SetStore store, string job)
        {
            var meta = new ChkRecord
            {
                Rel = Q(ctx, "rel"), Seq = int.Parse(Q(ctx, "seq") ?? "-1", CultureInfo.InvariantCulture), Kind = Q(ctx, "kind"),
                EncPath = ctx.Request.Headers["X-Enc"] ?? "", Orig = long.Parse(Q(ctx, "orig") ?? "0", CultureInfo.InvariantCulture),
                Mtime = long.Parse(Q(ctx, "mtime") ?? "0", CultureInfo.InvariantCulture), PermOnly = Q(ctx, "perm") == "Y"
            };
            long quota = prof.GetLong("QUOTA");
            long room = quota <= 0 ? long.MaxValue : quota - Usage(prof) - store.StagedBytes(job);
            if (room <= 0) throw new ApiException(507, "QUOTA", "The user's quota was exceeded.");
            ChkRecord rec;
            // R1 (found by QA F6): a full disk was an unexplained broken connection for the native engine (restic had it) —
            // now the same clear 507 and the same alert to the administrators
            try { rec = store.StageObject(job, meta, ctx.Request.InputStream, room); }
            catch (IOException e) when (DiskFull(e)) { DiskFullAlert(store.Dir, Q(ctx, "ip") ?? "-"); throw new ApiException(507, "DISK_FULL", "The backup server's disk is full: new backups cannot be stored. Existing backups are not affected — the provider must free space."); }
            return new Msg().Set("sha256", rec.Sha256).Set("size", rec.Size);
        }

        /// <summary>
        /// Verification (class "default-success"): the result of a run as the server will use it everywhere. A run reported
        /// without a result is never a success: it is taken from the log's end line, and without one the run failed.
        /// </summary>
        public static void NormalizeResult(Msg body)
        {
            if (!string.IsNullOrEmpty(body["result"])) return;
            var end = body.List("log").Select(l => AhsayLog.Fields(l["l"] ?? "")).LastOrDefault(f => f.Length > 4 && f[1] == "end");
            if (end != null && end[4].StartsWith("BS_STOP_", StringComparison.Ordinal)) { body.Set("result", end[4]); return; }
            body.Set("result", "BS_STOP_BY_SYSTEM_ERROR");
            body.Add("log", new Msg().Set("l", AhsayLog.Line(SystemClock.UtcNow, "err", message: "The computer did not report how the backup ended — it is counted as failed.")));
        }

        /// <summary>Verification: a restore report without a result is not "OK" — from its end line, else counted as failed.</summary>
        public static void NormalizeRestoreResult(Msg body)
        {
            if (!string.IsNullOrEmpty(body["result"])) return;
            var end = body.List("log").Select(l => AhsayLog.Fields(l["l"] ?? "")).LastOrDefault(f => f.Length > 4 && f[1] == "end");
            body.Set("result", end != null && end[4].StartsWith("RESTORE_STOP_", StringComparison.Ordinal) ? end[4] : "RESTORE_STOP_WITH_ERROR");
        }

        Msg Commit(string login, SetStore store, string job, Msg body, string ip)
        {
            lock (EndGate(login, store.SetId))
            {
                // bug 42 (Agent F, F-1): a commit that arrives after the server closed its run (no sign of life for longer
                // than the lease: a laptop asleep, a line down) was answered "ok" — the computer kept its files as stored and
                // never sent them again: on no restore point. Only a run that was really committed has a kept reply; any
                // other ended run refuses the commit, so the computer counts the run as failed and sends the files again.
                if (File.Exists(EndedLog(login, store.SetId, job)) && !File.Exists(ReplyFile(login, store.SetId, job)))
                    throw new ApiException(409, "RUN_CLOSED", "The server closed this run before it ended (no sign of life for too long); it was recorded as interrupted. The files are sent again in the next backup.");
                var done = EndedReply(login, store.SetId, job);
                if (done != null) return done;
                var reply = CommitOnce(login, store, job, body, ip);
                KeepReply(login, store.SetId, job, reply, ip);
                return reply;
            }
        }

        Msg CommitOnce(string login, SetStore store, string job, Msg body, string ip)
        {
            NormalizeResult(body);
            Msg verify;
            try { verify = store.Commit(job, body); }
            catch (IOException e) when (DiskFull(e)) { DiskFullAlert(store.Dir, ip); throw new ApiException(507, "DISK_FULL", "The backup server's disk is full: new backups cannot be stored. Existing backups are not affected — the provider must free space."); }
            var moves = store.LastMoves;
            if (store.LastRefused.Count > 0)
            {
                // Agent B (B-3): changes refused here are not in this point — the run must not be reported as a full success
                body.Add("log", new Msg().Set("l", AhsayLog.Line(SystemClock.UtcNow, "err", message: store.LastRefused.Count + " changed file(s) were not stored: their earlier version is not on the server. They are sent in full in the next backup.")));
                if ((body["result"] ?? "").StartsWith("BS_STOP_SUCCESS", StringComparison.Ordinal)) body.Set("result", "BS_STOP_SUCCESS_WITH_ERROR");
                verify.Set("refused", store.LastRefused.Count);
            }
            var logFile = WriteJobLog(login, store.SetId, "Backup", job, body);
            UpdateStats(login, store.SetId, body);
            SysLog.Write(ip, "Access", "backup committed " + login + "/" + store.SetId + " " + job + " bad=" + verify["bad"]);
            var prof = users.LoadProfile(login);
            var set = prof.Sets.First(x => x.Id == store.SetId);
            var stats = store.Stats();
            if (verify.Int("bad") > 0)
                mailer.Alert("✗ Damaged data found — " + login, "<p>The integrity check after the backup found " + verify["bad"] + " damaged files in the backup set " + Fmt.H(set.Name) + ". They were quarantined, and the agent will send them again in the next run.</p>", VendorOf(login));
            CheckMassChange(login, prof, set, job, body);
            long quota = prof.GetLong("QUOTA");
            { var qp = Calls.Read(prof).QuotaPercent; if (quota > 0 && qp > 0 && Usage(prof) * 100 >= quota * qp)
                TicketSafe(() => Calls.Auto("quota", login, null, null, null, "Quota almost full — " + login, Fmt.Size(Usage(prof)) + " / " + Fmt.Size(quota), prof)); }
            if (quota > 0 && Usage(prof) * 100 >= quota * Math.Max(1, prof.GetLong("QUOTA_REMIND_PERCENTAGE") == 0 ? 90 : prof.GetLong("QUOTA_REMIND_PERCENTAGE")))
                mailer.Alert("⚠ Quota almost full — " + login, "<p>The user " + Fmt.H(login) + " has used " + Fmt.Size(Usage(prof)) + " of " + Fmt.Size(quota) + ".</p>", VendorOf(login));
            mailer.BackupReport(prof, set, job, body, stats);
            AfterRun(login, set, job, body, logFile);
            TicketRun(login, set, body);
            Runs.Add(SystemClock.UtcNow, login, set, "Backup", job, body, logFile); LiveDone(login, set.Id);
            exporter.Profile(login, users.UserDir(login));
            exporter.Log(login, store.SetId, job, logFile);
            var ev = new Msg().Set("type", "commit").Set("login", login).Set("set", store.SetId).Set("job", job)
                .Set("stats", string.Join("\t", new[] { "new", "upd", "perm", "del", "bytes" }.Select(k => k + "=" + body.Long(k))));
            foreach (var m in moves) ev.Add("moves", new Msg().Set("src", m[0]).Set("dst", m[1]));
            Replication.Enqueue(ev);
            Replication.Enqueue(new Msg().Set("type", "db").Set("login", login));
            if (logFile != null) Replication.Enqueue(new Msg().Set("type", "log").Set("login", login).Set("path", "logs/" + store.SetId + "/Backup/" + Path.GetFileName(logFile)));
            return verify.Set("job", job);
        }

        /// <summary>
        /// Suspected ransomware (as ITSguard already checks for Ahsay): an unusual jump in changed / deleted files, or many
        /// changed files with one extension — against the fixed limits at first, then against what is normal for this set
        /// (AI-050, Insights.Check). The user's retention is frozen — no old version is deleted — until an administrator
        /// releases it, and the administrators are alerted. Every completed run is kept for the learning and the forecasts.
        /// </summary>
        void CheckMassChange(string login, Profile prof, BackupSetInfo set, string job, Msg body)
        {
            var sec = cfg.Doc.Root.Element("SECURITY");
            int minFiles = int.Parse((string)sec.Attribute("RANSOM_MIN_FILES") ?? "50", CultureInfo.InvariantCulture);
            int percent = int.Parse((string)sec.Attribute("RANSOM_PERCENT") ?? "30", CultureInfo.InvariantCulture);
            var userDir = users.UserDir(login);
            var run = new RunStat { Time = SystemClock.UtcNow, Prev = body.Long("prevFiles"), New = body.Long("new"), Upd = body.Long("upd"), Del = body.Long("del"), Bytes = body.Long("bytes"),
                TopExt = body["topExt"] ?? "", TopExtCount = body.Long("topExtCount"), Usage = Usage(prof) };
            var history = Insights.History(userDir, set.Id);
            var v = Insights.Check(history, run, minFiles, percent);
            run.Suspect = v.Suspect;
            if ((body["result"] ?? "BS_STOP_BY_SYSTEM_ERROR").StartsWith("BS_STOP_SUCCESS", StringComparison.Ordinal))
                try { Insights.Record(userDir, set.Id, run); } catch (Exception e) { SysLog.Write(null, "System", "error: run statistics " + login + "/" + set.Id + " " + e.Message); }
            if (!v.Suspect) return;
            lock (users.ProfileLock)
            {
                var p = users.LoadProfile(login);
                p.SetAttr("RETENTION_FROZEN", "Y"); p.SetAttr("RETENTION_FROZEN_TIME", RunId.UnixMs(SystemClock.UtcNow));
                p.FindSet(set.Id).SetAttributeValue("SUSPECT_RANSOMWARE", job);
                users.SaveProfile(login, p);
            }
            var why = v.Why;
            TicketSafe(() => Calls.Auto("ransom", login, set.Id, set.Name, set.Computer, "Suspected ransomware — " + set.Name, why, prof));
            SysLog.Write(null, "BackupErrors", login + "\t" + set.Id + "\t" + job + "\tSUSPECTED RANSOMWARE: " + why + " — retention frozen");
            SysLog.Write(null, "System", "warn: suspected ransomware " + login + "/" + set.Id + " " + job + ": " + why + (v.Learned ? " (learned)" : ""));
            mailer.Alert("🛑 Suspected ransomware — " + login, "<p>In the backup set " + Fmt.H(set.Name) + " of " + Fmt.H(login) + " an unusual change was detected in run " + Fmt.H(job) + ": " + Fmt.H(why) + ".</p><p>Deleting old versions is frozen for this user until it is released manually in the management website.</p>", VendorOf(login));
        }

        /// <summary>
        /// AI-020/030: a run that did not complete cleanly — in the background (the agent is not kept waiting), the AI explains
        /// the job log to the administrators, the explanation is kept beside the log, and a ticket is opened when someone must act.
        /// </summary>
        void AfterRun(string login, BackupSetInfo set, string job, Msg body, string logFile)
        {
            var result = body["result"] ?? "BS_STOP_BY_SYSTEM_ERROR";
            if (result == "BS_STOP_SUCCESS" || result == "BS_STOP_SUCCESS_WITH_WARNING" || result == "BS_STOP_BY_USER" || logFile == null || !Ai.AutoDiagnose(cfg)) return;   // failures and errors only (warnings: on request in Logs; a stop on purpose is no failure — bug 25)
            Task.Run(() =>
            {
                try
                {
                    var vendor = VendorOf(login);
                    var lang = mailer.Lang(vendor);
                    var d = Ai.Diagnose(cfg, lang, set.Type, set.Engine, Atomic.ReadLinesShared(logFile));
                    Atomic.WriteBytes(logFile + ".ai", d.ToBytes());
                    bool ticket = d.Int("needsTechnician") == 1 && Ai.Ticket(cfg, login, set.Name, job, d);
                    var html = "<p>" + Fmt.H(L.T(lang, "Backup set")) + ": " + Fmt.H(set.Name) + " — " + Fmt.H(login) + " — " + Fmt.H(job) + " — " + Fmt.H(result) + "</p>" + Ai.DiagnosisHtml(lang, d)
                        + (ticket ? "<p>" + Fmt.H(L.T(lang, "A service ticket was opened.")) + "</p>" : "");
                    mailer.Alert("✗ AI diagnosis — " + login + " — " + set.Name, html, vendor);
                }
                catch (ApiException e) { SysLog.Write(null, "System", "warn: AI diagnosis " + login + "/" + job + ": " + e.Code); }
                catch (Exception e) { SysLog.Write(null, "System", "error: AI diagnosis " + login + "/" + job + ": " + e.GetType().Name); }
            });
        }

        /// <summary>Job log in Ahsay CSV under &lt;user&gt;\logs\&lt;set&gt;\&lt;kind&gt;\; errors and warnings also go to the server's BackupErrors log.</summary>
        string WriteJobLog(string login, string setId, string kind, string job, Msg body)
        {
            var lines = body.List("log").Select(l => l["l"]).Where(l => !string.IsNullOrEmpty(l)).ToList();
            if (lines.Count == 0) return null;
            var p = Path.Combine(users.UserDir(login), "logs", setId, kind, job + ".log");
            if (kind != "Backup") for (int n = 1; File.Exists(p); n++) p = Path.Combine(users.UserDir(login), "logs", setId, kind, job + "-" + n + ".log");   // never overwrite another log
            Atomic.WriteText(p, string.Join("\r\n", lines) + "\r\n");
            foreach (var l in lines)
            {
                var f = AhsayLog.Fields(l);
                if (f.Length > 1 && (f[1] == "err" || f[1] == "warn" || (f[1] == "end" && f.Length > 4 && f[4] != "BS_STOP_SUCCESS")))
                    SysLog.Write(null, "BackupErrors", login + "\t" + setId + "\t" + job + "\t" + l);
            }
            return p;
        }

        /// <summary>Statistics into Profile.xml (the same attributes Ahsay writes; ITSguard reads them).</summary>
        /// <summary>A run that backed up (possibly with warnings or some files in error), as opposed to one that failed or was stopped.</summary>
        public static bool Completed(string result) { return (result ?? "BS_STOP_BY_SYSTEM_ERROR").StartsWith("BS_STOP_SUCCESS", StringComparison.Ordinal); }

        /// <summary>R1: the last result of a set whose run ended without statistics (aborted, interrupted).</summary>
        void SetLastResult(string login, string setId, string result)
        {
            lock (users.ProfileLock)
            {
                var p = users.LoadProfile(login); var e = p.FindSet(setId); if (e == null) return;
                e.SetAttributeValue("LAST_RESULT", result); e.SetAttributeValue("LAST_RESULT_TIME", RunId.UnixMs(SystemClock.UtcNow));
                users.SaveProfile(login, p);
            }
        }

        void UpdateStats(string login, string setId, Msg body)
        {
            lock (resticSince) resticSince.Remove(login);
            totalAt = DateTime.MinValue;   // the licence storage total is counted again
            lock (users.ProfileLock)
            {
                var p = users.LoadProfile(login);
                long dataSize = 0, dataFiles = 0, dataOrig = 0, retSize = 0, retFiles = 0, retOrig = 0;
                foreach (var s in p.Sets)
                {
                    var e = p.FindSet(s.Id);
                    Action<string, object> a = (n, v) => e.SetAttributeValue(n, Convert.ToString(v, CultureInfo.InvariantCulture));
                    // Bug 29: one set whose store cannot be read (a damaged index) made this recount throw — and with it the
                    // commit of every OTHER set of the customer (stored, but a failure on the computer, no history, no mail).
                    // Such a set keeps its last known sizes and is reported; the others are counted and the run recorded.
                    Msg st;
                    try { st = s.Engine == "RESTIC" ? ResticStats(login, s.Id, e, s.Id == setId ? body : null) : new SetStore(users.UserDir(login), s.Id).Stats(); }
                    catch (Exception x) when (!(x is ApiException))
                    {
                        SysLog.Write(null, "System", "error: sizes of " + login + "/" + s.Id + " cannot be read: " + x.Message);
                        st = new Msg().Set("dataSize", (string)e.Attribute("TOTAL_BSET_SIZE")).Set("dataOrig", (string)e.Attribute("TOTAL_UNCOMPRESS_FILE_SIZE")).Set("dataFiles", (string)e.Attribute("NO_OF_FILES"))
                            .Set("retainSize", (string)e.Attribute("TOTAL_BSET_RETAIN_FILE_SIZE")).Set("retainOrig", (string)e.Attribute("TOTAL_BSET_RETAIN_UNCOMPRESS")).Set("retainFiles", (string)e.Attribute("TOTAL_BSET_RETAIN_FILE_NO"));
                    }
                    a("TOTAL_BSET_SIZE", st["dataSize"]); a("TOTAL_UNCOMPRESS_FILE_SIZE", st["dataOrig"]); a("NO_OF_FILES", st["dataFiles"]);
                    a("TOTAL_BSET_RETAIN_FILE_SIZE", st["retainSize"]); a("TOTAL_BSET_RETAIN_UNCOMPRESS", st["retainOrig"]); a("TOTAL_BSET_RETAIN_FILE_NO", st["retainFiles"]);
                    if (s.Id == setId && body != null)
                    {
                        var now = RunId.UnixMs(SystemClock.UtcNow);
                        // R1: "Last backup" is the last run that backed up (success, with warnings, or with some files in
                        // error); a failed run only sets the last result — a failing set must never look up to date
                        a("LAST_RESULT", body["result"] ?? "BS_STOP_BY_SYSTEM_ERROR"); a("LAST_RESULT_TIME", now);
                        if (Completed(body["result"])) { a("LAST_BACKUP_COMPLETE", now); a("LAST_BACKUP_RUN", body.Long("started", now)); }
                        a("TOTAL_BSET_UPLOAD_SIZE", (long.Parse((string)e.Attribute("TOTAL_BSET_UPLOAD_SIZE") ?? "0", CultureInfo.InvariantCulture) + body.Long("bytes")));
                        a("TOTAL_BSET_UPLOAD_NO", (long.Parse((string)e.Attribute("TOTAL_BSET_UPLOAD_NO") ?? "0", CultureInfo.InvariantCulture) + body.Long("new") + body.Long("upd") + body.Long("perm")));
                    }
                    dataSize += st.Long("dataSize"); dataFiles += st.Long("dataFiles"); dataOrig += st.Long("dataOrig");
                    retSize += st.Long("retainSize"); retFiles += st.Long("retainFiles"); retOrig += st.Long("retainOrig");
                }
                p.SetAttr("DATA_SIZE", dataSize); p.SetAttr("DATA_FILE", dataFiles); p.SetAttr("TOTAL_UNCOMPRESS_FILE_SIZE", dataOrig);
                p.SetAttr("RETAIN_SIZE", retSize); p.SetAttr("RETAIN_FILE", retFiles); p.SetAttr("TOTAL_RETAIN_UNCOMPRESS", retOrig);
                if (body != null)
                {
                    if (Completed(body["result"])) p.SetAttr("LAST_BACKUP", RunId.UnixMs(SystemClock.UtcNow));
                    p.SetAttr("TOTAL_UPLOAD_SIZE", p.GetLong("TOTAL_UPLOAD_SIZE") + body.Long("bytes"));
                    p.SetAttr("TOTAL_UPLOAD_FILE", p.GetLong("TOTAL_UPLOAD_FILE") + body.Long("new") + body.Long("upd") + body.Long("perm"));
                    p.SetAttr("LAST_UPLOAD_SIZE", body.Long("bytes"));
                    p.SetAttr("LAST_UPLOAD_FILE", body.Long("new") + body.Long("upd") + body.Long("perm"));
                }
                users.SaveProfile(login, p);
            }
        }

        static void StreamFile(HttpListenerContext ctx, string path)
        {
            using (var fs = File.OpenRead(path))
            {
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/octet-stream";
                ctx.Response.ContentLength64 = fs.Length;
                fs.CopyTo(ctx.Response.OutputStream, 1 << 16);
            }
            ctx.Response.OutputStream.Close();
        }

        // ---------------------------------------------------------------- administrator

        void Admin(HttpListenerContext ctx, string[] seg, string ip)
        {
            string method = ctx.Request.HttpMethod;
            if (seg.Length == 3 && seg[2] == "login" && method == "POST")
            {
                var b = Body(ctx);
                var si = Staff.Check(cfg, b["login"], b["password"], b["otp"], ip, SystemClock.UtcNow);
                Guard.SignedIn(cfg, si.Account.Login, ip, SystemClock.Now);
                bool local = Staff.LocalTrusted(cfg, ip);
                Reply(ctx, 200, new Msg().Set("session", users.NewStaffSession(si, local)).Set("vendor", si.Account.Vendor).Set("enroll", si.Enroll ? 1 : 0).Set("remember", local ? 1 : 0));
                return;
            }
            var s = users.GetSession(ctx.Request.Headers["X-Session"]);
            if (s == null || !s.Admin) throw new ApiException(401, "SESSION", "Administrator sign-in is required.");
            var admin = s.Login;
            if (seg.Length == 3 && seg[2] == "logout" && method == "POST") { users.EndSession(ctx.Request.Headers["X-Session"]); Reply(ctx, 200, new Msg().Set("ok", 1)); return; }
            // TECH-010: two-step is mandatory — until it is set up only the set-up opens
            var area = seg.Length >= 3 ? seg[2] : "";
            if (s.Enroll && area != "totp" && area != "me") throw new ApiException(403, "TOTP_ENROLL", "Set up two-step verification first.");
            // VND-050: a vendor's administrator reaches only its own users (and its branding); the rest is the system's
            var vendor = s.Vendor ?? ""; bool super = vendor.Length == 0;
            if (!super)
            {
                bool allowed = (seg.Length >= 3 && (seg[2] == "tickets" || seg[2] == "totp" || seg[2] == "tasks" || seg[2] == "dashboard" || seg[2] == "bulk" || seg[2] == "live" || seg[2] == "checks"))   // TICKETS-010: only the vendor's own customers (filtered below)
                    || (seg.Length == 3 && (seg[2] == "users" || seg[2] == "brand" || seg[2] == "me" || seg[2] == "clientpackage" || seg[2] == "insights"))
                    || (seg.Length >= 4 && seg[2] == "users" && Owns(seg[3], vendor))
                    || (seg.Length == 5 && seg[2] == "keys" && Owns(seg[3], vendor));
                if (!allowed) throw new ApiException(403, "VENDOR", "Only the system administrator can do this.");
            }
            if (seg.Length >= 3 && seg[2] == "tickets") { AdminTickets(ctx, seg, method, admin, vendor, super, ip); return; }
            // DEL-010/020: deletion requests, the recycle bin, and the two-administrator rule
            if (seg.Length >= 3 && (seg[2] == "deletes" || seg[2] == "recycle"))
            {
                if (!super) throw new ApiException(403, "VENDOR", "Only the system administrator can do this.");
                if (seg[2] == "deletes" && seg.Length == 3 && method == "GET") { var m = new Msg().Set("dual", Recycle.Dual(cfg) ? 1 : 0); foreach (var r in Recycle.Requests(cfg)) m.Add("requests", r); Reply(ctx, 200, m); return; }
                if (seg[2] == "deletes" && seg.Length == 4 && seg[3] == "settings" && method == "POST") { Recycle.SetDual(cfg, Body(ctx).Bool("dual")); SysLog.Write(ip, "Admin", admin + " two-administrator deletion: " + (Recycle.Dual(cfg) ? "on" : "off")); Reply(ctx, 200, new Msg().Set("dual", Recycle.Dual(cfg) ? 1 : 0)); return; }
                if (seg[2] == "deletes" && seg.Length == 5 && method == "POST" && seg[4] == "approve") { Recycle.Approve(cfg, users, seg[3], admin, ip, SystemClock.UtcNow); Reply(ctx, 200, new Msg().Set("deleted", 1)); return; }
                if (seg[2] == "deletes" && seg.Length == 5 && method == "POST" && seg[4] == "cancel") { Recycle.Cancel(cfg, seg[3], admin, ip); Reply(ctx, 200, new Msg().Set("ok", 1)); return; }
                if (seg[2] == "recycle" && seg.Length == 3 && method == "GET") { var m = new Msg(); foreach (var r in users.Recycled()) m.Add("items", r); Reply(ctx, 200, m); return; }
                if (seg[2] == "recycle" && seg.Length == 5 && method == "POST" && seg[4] == "restore") { users.RestoreRecycled(Uri.UnescapeDataString(seg[3]), admin, ip); Reply(ctx, 200, new Msg().Set("ok", 1)); return; }
                Need(false);
            }
            // CHK-010: the backup checks (small, unchanged, sharp change, short retention, added / removed)
            if (seg.Length == 3 && seg[2] == "checks" && method == "GET") { Reply(ctx, 200, BackupChecks.Report(cfg, users, Runs, SystemClock.UtcNow, l => super || Owns(l, vendor))); return; }
            if (seg.Length == 3 && seg[2] == "live" && method == "GET") { var m = new Msg(); foreach (var x in Live(l => super || Owns(l, vendor))) m.Add("live", x); Reply(ctx, 200, m); return; }
            if (seg.Length == 3 && seg[2] == "tasks" && method == "GET")
            {
                // TASKS-010: every run of the last N hours (24 by default), all customers — the vendor's own only
                int hours = Math.Max(1, Math.Min(24 * 31, int.Parse(Q(ctx, "hours") ?? "24", CultureInfo.InvariantCulture)));
                var now = SystemClock.UtcNow; var m = new Msg();
                var rows = Runs.Since(now.AddHours(-hours), now, l => super || Owns(l, vendor));
                foreach (var g in rows.GroupBy(r => r["status"])) m.Set(g.Key, g.Count());
                m.Set("total", rows.Count);
                foreach (var r in rows.Take(5000)) m.Add("tasks", r);
                Reply(ctx, 200, m); return;
            }
            // TPL-010 set templates; BULK-010 one action on many customers; DASH-010 the dashboard's numbers
            if (seg.Length >= 3 && seg[2] == "templates")
            {
                if (!super) throw new ApiException(403, "VENDOR", "Only the system administrator can do this.");
                if (seg.Length == 3 && method == "GET") { Reply(ctx, 200, Templates.List(cfg)); return; }
                if (seg.Length == 3 && method == "POST") { var b = Body(ctx); Templates.Save(cfg, b["name"], b["type"], BackupSetInfo.FromXml(XElement.Parse(b["set"])), admin, ip); Reply(ctx, 200, Templates.List(cfg)); return; }
                if (seg.Length == 5 && seg[4] == "delete" && method == "POST") { Templates.Delete(cfg, Uri.UnescapeDataString(seg[3]), admin, ip); Reply(ctx, 200, Templates.List(cfg)); return; }
                Need(false);
            }
            if (seg.Length == 3 && seg[2] == "bulk" && method == "POST")
            {
                var b = Body(ctx); var m = new Msg(); int ok = 0;
                foreach (var item in b.List("logins"))
                {
                    var login = item["login"]; var r = new Msg().Set("login", login);
                    try
                    {
                        if (!super && !Owns(login, vendor)) throw new ApiException(403, "VENDOR", "This customer is not yours.");
                        switch (b["action"])
                        {
                            case "quota":
                                {
                                    long q = (long)(double.Parse(b["quotaGB"], CultureInfo.InvariantCulture) * 1024 * 1024 * 1024);
                                    lock (users.ProfileLock) { var p = users.LoadProfile(login); p.SetAttr("QUOTA", q); if (!string.IsNullOrEmpty(b["quotaType"])) p.SetAttr("QUOTA_TYPE", b["quotaType"]); users.SaveProfile(login, p); }
                                    users.UpdateQuotaIndex(login, q); break;
                                }
                            case "rights":
                                lock (users.ProfileLock) { var p = users.LoadProfile(login); foreach (var rr in SetControl.Rights) if (b[rr[0].ToLowerInvariant()] != null) p.SetAttr(rr[0], b.Bool(rr[0].ToLowerInvariant()) ? "Y" : "N"); users.SaveProfile(login, p); }
                                break;
                            case "template": r.Set("sets", Templates.Apply(cfg, users, b["template"], login, admin, ip)); break;
                            case "run": { int n = 0; foreach (var st in users.LoadProfile(login).Sets.Where(x => PilotScope.Why(cfg, x) == null)) n += SetControl.Request(users, login, st.Id, true, admin, ip, SystemClock.UtcNow); r.Set("computers", n); break; }
                            case "requiretotp": lock (users.ProfileLock) { var p = users.LoadProfile(login); p.SetAttr("REQUIRE_TOTP", b.Bool("on") ? "Y" : "N"); users.SaveProfile(login, p); } break;
                            default: throw new ApiException(400, "ACTION", "Unknown action.");
                        }
                        r.Set("ok", 1); ok++;
                    }
                    catch (ApiException e) { r.Set("ok", 0).Set("error", e.Message); }
                    m.Add("results", r);
                }
                SysLog.Write(ip, "Admin", admin + " bulk " + b["action"] + " on " + b.List("logins").Count + " customers (" + ok + " done)");
                Reply(ctx, 200, m.Set("done", ok)); return;
            }
            if (seg.Length == 3 && seg[2] == "dashboard" && method == "GET")
            {
                var now = SystemClock.UtcNow; var m = new Msg();
                var logins = users.Logins().Where(l => super || Owns(l, vendor)).ToList();
                int sets = 0; long used = 0;
                foreach (var l in logins) try { var p = users.LoadProfile(l); sets += p.SetElements.Count(); used += Usage(p); } catch (Exception) { }
                m.Set("customers", logins.Count).Set("sets", sets).Set("usedBytes", used).Set("computers", users.ActiveComputers(super ? null : logins));   // H-12: a reseller counts its own customers' computers
                var runs = Runs.Since(now.AddHours(-24), now, l => super || Owns(l, vendor));
                m.Set("tasks", runs.Count).Set("ok", runs.Count(r => r["status"] == "ok")).Set("warn", runs.Count(r => r["status"] == "warn")).Set("bad", runs.Count(r => r["status"] == "bad"));
                foreach (var r in runs.Where(x => x["status"] == "bad" || x["status"] == "warn").Take(10)) m.Add("attention", r);
                Func<Msg, bool> mineCalls = t => super || ((t["login"] ?? "").Length > 0 && Owns(t["login"], vendor));
                m.Set("callsOpen", Calls.List("open", null, null, admin, mineCalls).Count).Set("callsLate", Calls.List("late", null, null, admin, mineCalls).Count).Set("callsMine", Calls.List("mine", null, null, admin, mineCalls).Count);
                var lic = cfg.License; m.Set("licenseCustomers", lic.MaxUsers).Set("licenseComputers", lic.MaxDevices).Set("licenseStorageGB", lic.MaxStorageGB).Set("edition", lic.Valid ? lic.Edition : "FREE");
                for (int d = 13; d >= 0; d--)
                {
                    var day = now.Date.AddDays(-d); var dr = Runs.Since(day, day.AddDays(1).AddTicks(-1), l => super || Owns(l, vendor)).Where(x => x["kind"] == "Backup" && RunId.FromUnixMs(x.Long("time")) < day.AddDays(1)).ToList();
                    m.Add("days", new Msg().Set("day", day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Set("ok", dr.Count(x => x["status"] == "ok")).Set("warn", dr.Count(x => x["status"] == "warn")).Set("bad", dr.Count(x => x["status"] == "bad")));
                }
                Reply(ctx, 200, m); return;
            }
            if (seg.Length == 3 && seg[2] == "time")
            {
                if (!super) throw new ApiException(403, "VENDOR", "Only the system administrator can do this.");
                Reply(ctx, 200, method == "POST" ? TimeSettings.Save(cfg, Body(ctx), admin, ip) : TimeSettings.Get(cfg)); return;
            }
            if (seg.Length == 3 && seg[2] == "contract")
            {
                if (!super) throw new ApiException(403, "VENDOR", "Only the system administrator can do this.");
                Reply(ctx, 200, method == "POST" ? Contract.Save(cfg, Body(ctx), admin, ip) : Contract.Admin(cfg)); return;
            }
            if (seg.Length == 3 && seg[2] == "ticketsettings")
            {
                if (method == "POST") { Calls.SaveSettings(Body(ctx)); SysLog.Write(ip, "Admin", admin + " service call settings saved"); }
                Reply(ctx, 200, Calls.SettingsMsg(Calls.Read())); return;
            }
            if (seg.Length == 3 && seg[2] == "me" && method == "GET")
            {
                // VND-065: who is signed in, and the branding the management screens show (the vendor's own, else the server's)
                var me = new Msg().Set("admin", admin).Set("vendor", vendor).Set("enroll", s.Enroll ? 1 : 0).Set("scope", cfg.Pilot ? Scope.Pilot : null);
                var vv = Vendors.Find(cfg, vendor);
                if (vv != null) me.Set("vendorName", (string)vv.Attribute("NAME"));
                foreach (var a in Vendors.BrandAttrs)
                {
                    me.Set("brand" + a, Vendors.Brand(cfg, vendor, a, ""));
                    if (vv != null) me.Set("own" + a, (string)vv.Attribute(a));
                }
                Reply(ctx, 200, me); return;
            }
                        if (seg.Length >= 3 && seg[2] == "vendors")
            {
                if (method == "POST" && !cfg.License.Has("VENDORS")) throw new ApiException(402, "LICENSE", "Reseller management is not included in the licence.");
                // VND-010/020: vendors and their administrators (system administrator only — checked above)
                if (seg.Length == 3 && method == "GET")
                {
                    var m = new Msg();
                    foreach (var v in Vendors.All(cfg))
                    {
                        var vm = new Msg().Set("id", (string)v.Attribute("ID")).Set("name", (string)v.Attribute("NAME")).Set("maxUsers", (string)v.Attribute("MAX_USERS"))
                            .Set("maxQuotaGB", (string)v.Attribute("MAX_QUOTA_GB")).Set("disabled", (string)v.Attribute("DISABLED") == "Y" ? 1 : 0)
                            .Set("users", users.Logins().Count(l => string.Equals(users.LoadProfile(l).Get("OWNER"), (string)v.Attribute("ID"), StringComparison.OrdinalIgnoreCase)));
                        foreach (var a in Vendors.BrandAttrs) vm.Set("brand" + a, (string)v.Attribute(a));
                        foreach (var a in v.Elements("VENDOR_ADMIN")) vm.Add("admins", new Msg().Set("login", (string)a.Attribute("LOGIN_NAME")));
                        m.Add("vendors", vm);
                    }
                    Reply(ctx, 200, m); return;
                }
                if (seg.Length == 3 && method == "POST") { var v = Vendors.Save(cfg, Body(ctx)); SysLog.Write(ip, "Admin", admin + " vendor saved " + (string)v.Attribute("ID")); Reply(ctx, 200, new Msg().Set("id", (string)v.Attribute("ID"))); return; }
                if (seg.Length == 5 && seg[4] == "admins" && method == "POST")
                {
                    var b = Body(ctx);
                    Vendors.SaveAdmin(cfg, seg[3], b["login"], b["password"]);
                    SysLog.Write(ip, "Admin", admin + " vendor administrator " + b["login"] + " @ " + seg[3]);
                    Reply(ctx, 200, new Msg().Set("ok", 1)); return;
                }
                throw new ApiException(404, "NOT_FOUND", "Not found.");
            }
            if (seg.Length == 3 && seg[2] == "brand" && !super && method == "POST")
            {
                // VND-060: a vendor edits its own branding only
                var b = Body(ctx); var own = new Msg().Set("id", vendor);
                foreach (var a in Vendors.BrandAttrs) if (b["brand" + a] != null) own.Set("brand" + a, b["brand" + a]);
                Vendors.Save(cfg, own);
                SysLog.Write(ip, "Admin", admin + " branding of vendor " + vendor);
                Reply(ctx, 200, new Msg().Set("ok", 1)); return;
            }
            if (seg.Length == 3 && seg[2] == "users" && method == "POST")
            {
                var b = Body(ctx);
                double gb;
                long? quota = double.TryParse(b["quotaGB"], NumberStyles.Float, CultureInfo.InvariantCulture, out gb) ? (long)(gb * 1024 * 1024 * 1024) : (long?)null;
                var lic = cfg.License;   // LIC-030: number of users in the licence
                if (lic.MaxUsers > 0 && users.Logins().Count() >= lic.MaxUsers) throw new ApiException(402, "LICENSE", "You have reached the number of customers in the licence (" + lic.MaxUsers + "). To upgrade, contact the software vendor.");
                var owner = super ? (b["vendor"] ?? "") : vendor;
                if (owner.Length > 0) Vendors.CheckLimits(cfg, users, owner, quota);
                var p = users.Create(b["login"], b["password"], b["alias"], quota, b["quotaType"], b["email"], ip);
                if (owner.Length > 0)
                    lock (users.ProfileLock) { p = users.LoadProfile(p.Get("LOGIN_NAME")); p.SetAttr("OWNER", owner.ToLowerInvariant()); users.SaveProfile(p.Get("LOGIN_NAME"), p); }
                exporter.Profile(p.Get("LOGIN_NAME"), users.UserDir(p.Get("LOGIN_NAME")));
                Replication.Enqueue(new Msg().Set("type", "user").Set("login", p.Get("LOGIN_NAME")).Set("quota", p.Get("QUOTA")));
                Replication.Enqueue(new Msg().Set("type", "db").Set("login", p.Get("LOGIN_NAME")));
                Reply(ctx, 200, new Msg().Set("login", p.Get("LOGIN_NAME")).Set("home", Path.GetDirectoryName(users.UserDir(p.Get("LOGIN_NAME")))));
                return;
            }
            if (seg.Length == 3 && seg[2] == "users" && method == "GET")
            {
                var m = new Msg();
                foreach (var login in users.Logins())
                {
                    var p = users.LoadProfile(login);
                    if (!super && !string.Equals(p.Get("OWNER"), vendor, StringComparison.OrdinalIgnoreCase)) continue;
                    var u = new Msg().Set("login", login).Set("vendor", p.Get("OWNER") ?? "").Set("quota", p.Get("QUOTA")).Set("quotaType", p.Get("QUOTA_TYPE")).Set("dataSize", p.Get("DATA_SIZE"))
                        .Set("dataOrig", p.Get("TOTAL_UNCOMPRESS_FILE_SIZE")).Set("dataFiles", p.Get("DATA_FILE")).Set("retainSize", p.Get("RETAIN_SIZE"))
                        .Set("retainOrig", p.Get("TOTAL_RETAIN_UNCOMPRESS")).Set("retainFiles", p.Get("RETAIN_FILE")).Set("lastBackup", p.Get("LAST_BACKUP"))
                        .Set("locked", p.GetLong("USER_LOCKED_TIME") > 0 && RunId.FromUnixMs(p.GetLong("USER_LOCKED_TIME")).AddMinutes(cfg.LockMinutes) > SystemClock.UtcNow ? 1 : 0)
                        .Set("totp", string.IsNullOrEmpty(p.Get("TOTP_SECRET")) ? 0 : 1).Set("frozen", p.Get("RETENTION_FROZEN") == "Y" ? 1 : 0)
                        .Set("allowedIps", p.Get("ALLOWED_IPS")).Set("lockAttempts", p.Get("LOCK_ATTEMPTS")).Set("lockMinutes", p.Get("LOCK_MINUTES")).Set("requireTotp", p.Get("REQUIRE_TOTP") == "Y" ? 1 : 0);
                    u.Set("alias", p.Get("ALIAS")).Set("phone", p.Get("PHONE")).Set("notify", p.Get("EMAIL_NOTIFY") ?? "ALL").Set("signup", p.Get("SIGNUP")).Set("contractVersion", p.Get("CONTRACT_VERSION"));
                    foreach (var c in p.User.Elements("CONTACT")) u.Add("contacts", new Msg().Set("name", (string)c.Attribute("NAME")).Set("email", (string)c.Attribute("EMAIL")));
                    u.Add("rights", SetControl.RightsMsg(p));
                    foreach (var set in p.SetElements)
                        u.Add("sets", new Msg().Set("id", (string)set.Attribute("ID")).Set("name", (string)set.Attribute("NAME")).Set("type", (string)set.Attribute("TYPE"))
                            .Set("dataSize", (string)set.Attribute("TOTAL_BSET_SIZE")).Set("dataOrig", (string)set.Attribute("TOTAL_UNCOMPRESS_FILE_SIZE")).Set("dataFiles", (string)set.Attribute("NO_OF_FILES"))
                            .Set("retainSize", (string)set.Attribute("TOTAL_BSET_RETAIN_FILE_SIZE")).Set("retainOrig", (string)set.Attribute("TOTAL_BSET_RETAIN_UNCOMPRESS")).Set("retainFiles", (string)set.Attribute("TOTAL_BSET_RETAIN_FILE_NO"))
                            .Set("lastBackup", (string)set.Attribute("LAST_BACKUP_COMPLETE")).Set("lastResult", (string)set.Attribute("LAST_RESULT")).Set("lastResultTime", (string)set.Attribute("LAST_RESULT_TIME")).Set("restoreTest", (string)set.Attribute("RESTORE_TEST_RESULT")).Set("suspect", (string)set.Attribute("SUSPECT_RANSOMWARE"))
                            .Set("computer", (string)set.Attribute("SCHEDULE_HOST")).Set("parent", (string)set.Attribute("PARENT_SET")).Set("lastRestoreTest", (string)set.Attribute("LAST_RESTORE_TEST")).Set("engine", (string)set.Attribute("ENGINE"))
                            .Set("sources", string.Join("\n", set.Elements("SEL-SOURCE").Select(x => x.Value).Take(20))).Set("skipped", set.Elements("DE-SOURCE").Count()));
                    m.Add("users", u);
                }
                Reply(ctx, 200, m);
                return;
            }
            if (seg.Length == 3 && seg[2] == "homes" && method == "GET") { var m = new Msg(); foreach (var h in users.HomesReport()) m.Add("homes", h); Reply(ctx, 200, m); return; }
            if (seg.Length == 5 && seg[2] == "users" && seg[4] == "compliance" && method == "GET")
            {
                // AI-080: the customer's data-protection report and restore certificate (printable HTML)
                var login = Uri.UnescapeDataString(seg[3]);
                var p = users.LoadProfile(login);
                var now = SystemClock.UtcNow;
                var vendorOf = p.Get("OWNER");
                var lang = L.Norm(Q(ctx, "lang") ?? mailer.Lang(vendorOf));
                var facts = p.Sets.Select(x => Compliance.Facts(users.UserDir(login), p, x, now)).ToList();
                var html = Compliance.Html(lang, Vendors.Brand(cfg, vendorOf, "PRODUCT", "ITSguard Server Online") + (Vendors.Brand(cfg, vendorOf, "SLOGAN", "").Length > 0 ? " — " + Vendors.Brand(cfg, vendorOf, "SLOGAN", "") : ""), Vendors.Brand(cfg, vendorOf, "COMPANY", ""), p, facts,
                    Replicator.IsReplica(cfg) || (string)(cfg.Doc.Root.Element("REPLICATION") ?? new XElement("R")).Attribute("ENABLED") == "Y",
                    !string.IsNullOrEmpty(p.Get("TOTP_SECRET")), !string.IsNullOrEmpty((string)cfg.Admin.Attribute("TOTP_SECRET")), now);
                SysLog.Write(ip, "Admin", admin + " compliance report " + login);
                var bytes = Encoding.UTF8.GetBytes(html);
                ctx.Response.StatusCode = 200; ctx.Response.ContentType = "text/html; charset=utf-8"; ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length); ctx.Response.OutputStream.Close();
                return;
            }
            if (seg.Length == 5 && seg[2] == "users" && seg[4] == "aidiagnose" && method == "POST")
            {
                PilotScope.Check(cfg, PilotScope.Ai);   // PILOT-010 / UI-08
                // AI-020: explain one job log (kept beside the log; "again" asks anew)
                var login = Uri.UnescapeDataString(seg[3]);
                var b = Body(ctx);
                var p = users.LoadProfile(login);
                var set = p.Sets.FirstOrDefault(x => x.Id == b["set"]);
                Need(set != null && new[] { "Backup", "Restore" }.Contains(b["cat"]) && !string.IsNullOrEmpty(b["file"]) && b["file"].IndexOfAny(new[] { '/', '\\' }) < 0 && !b["file"].Contains(".."));
                var file = Path.Combine(users.UserDir(login), "logs", set.Id, b["cat"], b["file"]);
                Need(File.Exists(file));
                if (File.Exists(file + ".ai") && !b.Bool("again")) { Reply(ctx, 200, Msg.Parse(OnlineBackup.Core.Atomic.ReadAllBytes(file + ".ai")).Set("cached", 1)); return; }
                var d = Ai.Diagnose(cfg, L.Norm(b["lang"]), set.Type, set.Engine, Atomic.ReadLinesShared(file));
                Atomic.WriteBytes(file + ".ai", d.ToBytes());
                SysLog.Write(ip, "Admin", admin + " AI diagnosis " + login + "/" + set.Id + " " + b["file"]);
                if (b.Bool("ticket")) d.Set("ticket", Ai.Ticket(cfg, login, set.Name, b["file"], d) ? 1 : 0);
                Reply(ctx, 200, d); return;
            }
            if (seg.Length == 3 && seg[2] == "insights" && method == "GET")
            {
                PilotScope.Check(cfg, PilotScope.Ai);
                Reply(ctx, 200, InsightsMsg(super ? null : vendor, SystemClock.UtcNow)); return;
            }
            if (seg.Length == 3 && seg[2] == "aitest" && method == "POST")
            {
                PilotScope.Check(cfg, PilotScope.Ai);
                var d = Ai.Diagnose(cfg, "en", "FILE", "", new[] { AhsayLog.Line(SystemClock.UtcNow, "err", message: "Test: access to C:\\Data\\report.xlsx is denied") });
                Reply(ctx, 200, new Msg().Set("ok", 1).Set("summary", d["summary"])); return;
            }
            // SET-010/020/030: a customer's set from the admin site — its settings, its computers, back up now / stop
            if (seg.Length >= 6 && seg[2] == "users" && seg[4] == "sets")
            {
                var login = Uri.UnescapeDataString(seg[3]); var sid = seg[5];
                if (seg.Length == 6 && method == "GET") { Reply(ctx, 200, SetControl.Detail(users, login, sid)); return; }
                // REP-020: the set's own reports — every backup, restore and restore test of the last 60 days
                if (seg.Length == 7 && seg[6] == "runs" && method == "GET")
                {
                    var now = SystemClock.UtcNow; var m = new Msg();
                    foreach (var r in Runs.Since(now.AddDays(-60), now, l => l == login).Where(x => x["set"] == sid)) m.Add("runs", r);
                    Reply(ctx, 200, m); return;
                }
                if (seg.Length == 6 && method == "POST")
                {
                    var body = Body(ctx);
                    var incoming = XElement.Parse(body["set"]);
                    // I-2: a set without a backup time (or a time with no day) is refused — it used to fall back to every day at 22:00
                    var times = incoming.Elements().Where(x => x.Name == "DAILY_SCHEDULE" || x.Name == "WEEKLY_SCHEDULE").ToList();
                    if (times.Count == 0 || times.Any(x => x.Name == "WEEKLY_SCHEDULE" && new[] { "SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT" }.All(k => (string)x.Attribute(k) != "Y")))
                        throw new ApiException(400, "SCHEDULE", "Tick at least one day for each backup time.");
                    var saved = SetControl.Save(users, login, sid, BackupSetInfo.FromXml(incoming), admin, ip, body["version"]);
                    exporter.Profile(login, users.UserDir(login));
                    Reply(ctx, 200, SetControl.Detail(users, login, sid).Set("name", saved.Name)); return;
                }
                if (seg.Length == 7 && method == "POST")
                {
                    var b = Body(ctx);
                    switch (seg[6])
                    {
                        case "run": case "stop":
                            Reply(ctx, 200, new Msg().Set("computers", SetControl.Request(users, login, sid, seg[6] == "run", admin, ip, SystemClock.UtcNow))); return;
                        case "addcomputer": Reply(ctx, 200, new Msg().Set("id", SetControl.AddComputer(users, login, sid, b["computer"], admin, ip))); return;
                        case "removecomputer": SetControl.RemoveComputer(users, login, sid, b["computer"], admin, ip); Reply(ctx, 200, new Msg().Set("ok", 1)); return;
                        case "move": SetControl.Move(users, login, sid, b["computer"], admin, ip); Reply(ctx, 200, new Msg().Set("ok", 1)); return;
                    }
                }
                Need(false);
            }
            // SRC-030: the folders of a customer's computer for the set editor's tree; "open" asks the computer for more
            // CFGBK-010: the server settings backup — list, copy folder, back up now, download one
            if (seg.Length == 3 && seg[2] == "defaults")   // DEF-010: defaults for new customers
            {
                if (!super) throw new ApiException(403, "VENDOR", "Only the server's administrators.");
                if (method == "POST") NewDefaults.Save(cfg, Body(ctx), admin, ip);
                Reply(ctx, 200, NewDefaults.Get(cfg)); return;
            }
            if (seg.Length >= 3 && seg[2] == "guard")   // GUARD-010: blocked addresses and the rules
            {
                if (!super) throw new ApiException(403, "VENDOR", "Only the server's administrators.");
                var b = method == "POST" ? Body(ctx) : null;
                if (seg.Length == 4 && seg[3] == "unblock" && method == "POST") Guard.Unblock(cfg, b["ip"], admin, ip);
                else if (seg.Length == 4 && seg[3] == "block" && method == "POST")
                {
                    if (Guard.Trusted(cfg, b["ip"])) throw new ApiException(400, "TRUSTED", "This address is never blocked (this server, the office network or the trusted list).");
                    int hours; int.TryParse(b["hours"] ?? "24", out hours); Guard.Block(cfg, (b["ip"] ?? "").Trim(), string.IsNullOrWhiteSpace(b["reason"]) ? "blocked by hand" : b["reason"], admin, Math.Max(1, Math.Min(24 * 365, hours)));
                }
                else if (seg.Length == 3 && method == "POST")
                {
                    var g = new Guard.Settings { On = b["on"] != "0", TrustPrivate = b["trustPrivate"] != "0" };
                    int v; if (int.TryParse(b["blockHours"], out v)) g.BlockHours = v; if (int.TryParse(b["fails"], out v)) g.Fails = v; if (int.TryParse(b["users"], out v)) g.Users = v; if (int.TryParse(b["probes"], out v)) g.Probes = v;
                    g.Trusted = (b["trusted"] ?? "").Split(new[] { ',', ';', ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                    g.NoCode = b["noCode"] != null ? b["noCode"].Split(new[] { ',', ';', ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).ToList() : Guard.Read(cfg).NoCode;
                    Guard.Save(cfg, g, admin, ip);
                }
                else if (!(seg.Length == 3 && method == "GET")) throw new ApiException(404, "NOT_FOUND", "Not found.");
                Reply(ctx, 200, Guard.List(cfg)); return;
            }
            if (seg.Length >= 3 && seg[2] == "configbackup")
            {
                if (!super) throw new ApiException(403, "VENDOR", "Only the server's administrators.");
                if (seg.Length == 3 && method == "GET") { Reply(ctx, 200, ConfigBackup.List(cfg)); return; }
                if (seg.Length == 3 && method == "POST") { ConfigBackup.Save(cfg, Body(ctx)["copyTo"], admin, ip); Reply(ctx, 200, ConfigBackup.List(cfg)); return; }
                if (seg.Length == 4 && seg[3] == "now" && method == "POST") { var made = ConfigBackup.Make(cfg, users, SystemClock.UtcNow, admin, ip); Reply(ctx, 200, ConfigBackup.List(cfg).Set("made", made)); return; }
                if (seg.Length == 4 && method == "GET")
                {
                    var file = ConfigBackup.PathOf(cfg, Uri.UnescapeDataString(seg[3]));
                    SysLog.Write(ip, "Admin", admin + " downloaded the settings backup " + Path.GetFileName(file));
                    var res = ctx.Response;
                    res.StatusCode = 200; res.ContentType = "application/zip"; res.ContentLength64 = new FileInfo(file).Length;
                    res.AddHeader("Content-Disposition", "attachment; filename=\"" + Path.GetFileName(file) + "\"");
                    res.Headers["Cache-Control"] = "no-store";
                    using (var f = File.OpenRead(file)) f.CopyTo(res.OutputStream);
                    res.Close(); return;
                }
                Need(false);
            }
            // COMP-010: the customer's computers — list, disconnect, move to another customer with its sets and backups
            if (seg.Length == 5 && seg[2] == "users" && seg[4] == "computers" && method == "GET") { Reply(ctx, 200, users.ComputerList(Uri.UnescapeDataString(seg[3]))); return; }
            if (seg.Length == 6 && seg[2] == "users" && seg[4] == "computers" && method == "POST")
            {
                var login = Uri.UnescapeDataString(seg[3]); var b = Body(ctx);
                if (seg[5] == "disconnect") { Reply(ctx, 200, new Msg().Set("revoked", users.Disconnect(login, b["computer"], admin, ip))); return; }
                if (seg[5] == "move")
                {
                    PilotScope.Check(cfg, PilotScope.MoveComputer);   // PILOT-010 / SH-06
                    if (!super && !Owns(b["target"] ?? "", vendor)) throw new ApiException(403, "VENDOR", "This customer is not yours.");
                    var moved = users.MoveComputer(login, b["computer"], b["target"], admin, ip);
                    exporter.Profile(login, users.UserDir(login)); exporter.Profile(b["target"], users.UserDir(b["target"]));
                    Reply(ctx, 200, new Msg().Set("sets", moved.Count)); return;
                }
                Need(false);
            }
            if (seg.Length == 5 && seg[2] == "users" && seg[4] == "folders" && method == "GET") { Reply(ctx, 200, FolderTree.Get(users, Uri.UnescapeDataString(seg[3]), Q(ctx, "computer"))); return; }
            if (seg.Length == 5 && seg[2] == "users" && method == "POST")
            {
                var login = seg[3];
                switch (seg[4])
                {
                    case "browse": { var bb = Body(ctx); FolderTree.Request(users, Uri.UnescapeDataString(login), bb["computer"], bb["path"], SystemClock.UtcNow); Reply(ctx, 200, new Msg().Set("ok", 1)); return; }
                    case "unlock": users.Unlock(login, admin, ip); Reply(ctx, 200, new Msg().Set("ok", 1)); return;
                    case "delete":
                        {
                            // DEL-010/020: to the recycle bin — with a second administrator's approval when there are two or more
                            var r = Recycle.Request(cfg, users, login, Body(ctx)["set"], admin, ip, SystemClock.UtcNow);
                            if (r != null) mailer.Alert("Deletion waits for approval — " + login, "<p>" + Fmt.H(admin) + " asked to delete " + Fmt.H(r["kind"] == "user" ? "the customer " + login : "the backup set " + r["name"] + " of " + login) + ". Another administrator must approve it in the admin site.</p>");
                            Reply(ctx, 200, r == null ? new Msg().Set("deleted", 1) : r.Set("pending", 1)); return;
                        }
                    case "contacts":
                        {
                            // CUST-020: several e-mails per customer (reports and alerts go to all) — never one per set
                            var b = Body(ctx);
                            var list = b.List("contacts").Select(c => new { name = (c["name"] ?? "").Trim(), email = (c["email"] ?? "").Trim() }).Where(c => c.email.Length > 0).ToList();
                            foreach (var c in list) if (!c.email.Contains("@") || c.email.Contains(" ") || c.email.Length > 200) throw new ApiException(400, "EMAIL", "The e-mail is not valid: " + c.email);
                            if (list.Select(c => c.email.ToLowerInvariant()).Distinct().Count() != list.Count) throw new ApiException(400, "EMAIL", "The same e-mail appears twice.");
                            lock (users.ProfileLock)
                            {
                                var p = users.LoadProfile(login);
                                p.User.Elements("CONTACT").Remove();
                                foreach (var c in list) p.AddContact(c.name, c.email);
                                if (b["notify"] != null) { if (!new[] { "ALL", "FAILURE", "NONE" }.Contains(b["notify"])) throw new ApiException(400, "NOTIFY", "Unknown choice."); p.SetAttr("EMAIL_NOTIFY", b["notify"]); }
                                users.SaveProfile(login, p);
                            }
                            SysLog.Write(ip, "Admin", admin + " contacts of " + login + ": " + string.Join(", ", list.Select(c => c.email)));
                            Reply(ctx, 200, new Msg().Set("ok", 1)); return;
                        }
                    case "details":
                        {
                            // the customer's name, phone, notes, and what it may change in the client software (SET-040)
                            var b = Body(ctx);
                            lock (users.ProfileLock)
                            {
                                var p = users.LoadProfile(login);
                                if (b["alias"] != null) { var a2 = b["alias"].Trim(); if (a2.Length == 0 || a2.Length > 120) throw new ApiException(400, "ALIAS", "Write the customer's name."); p.SetAttr("ALIAS", a2); }
                                if (b["phone"] != null) p.SetAttr("PHONE", b["phone"].Trim());
                                if (b["notes"] != null) p.SetAttr("Notes", b["notes"]);
                                foreach (var r in SetControl.Rights) { var v = b[r[0].ToLowerInvariant()]; if (v != null) p.SetAttr(r[0], b.Bool(r[0].ToLowerInvariant()) ? "Y" : "N"); }
                                users.SaveProfile(login, p);
                            }
                            SysLog.Write(ip, "Admin", admin + " details of " + login);
                            Reply(ctx, 200, new Msg().Set("ok", 1)); return;
                        }
                    case "untrash":
                        {
                            // RST-070: puts back everything a restic prune / forget moved to the trash (e.g. after ransomware)
                            var sid = Q(ctx, "set");
                            Need(sid != null && users.LoadProfile(login).FindSet(sid) != null);
                            PilotScope.CheckSet(cfg, BackupSetInfo.FromXml(users.LoadProfile(login).FindSet(sid)));   // bug 114: a blocked restic set's repository stays as it is
                            int n = new ResticStore(users.UserDir(login), sid).RestoreTrash();
                            SysLog.Write(ip, "Admin", "restic trash restored " + login + "/" + sid + " files=" + n + " by " + admin);
                            Reply(ctx, 200, new Msg().Set("restored", n)); return;
                        }
                    case "resettotp": users.ResetTotp(login, admin, ip); Reply(ctx, 200, new Msg().Set("ok", 1)); return;
                    case "security":
                        {
                            // SEC-020/030: the customer's lock policy, fixed addresses and required two-step verification
                            var b = Body(ctx);
                            lock (users.ProfileLock)
                            {
                                var p = users.LoadProfile(login);
                                if (b["allowedIps"] != null) p.SetAttr("ALLOWED_IPS", Users.NormalizeIps(b["allowedIps"]));
                                if (b["lockAttempts"] != null) p.SetAttr("LOCK_ATTEMPTS", b["lockAttempts"].Trim().Length == 0 ? "" : Users.ClampAttempts(b.Int("lockAttempts", 3)).ToString(CultureInfo.InvariantCulture));
                                if (b["lockMinutes"] != null) p.SetAttr("LOCK_MINUTES", b["lockMinutes"].Trim().Length == 0 ? "" : Users.ClampMinutes(b.Int("lockMinutes", 30)).ToString(CultureInfo.InvariantCulture));
                                if (b["requireTotp"] != null) p.SetAttr("REQUIRE_TOTP", b.Bool("requireTotp") ? "Y" : "N");
                                users.SaveProfile(login, p);
                                SysLog.Write(ip, "Admin", admin + " security " + login + ": addresses [" + p.Get("ALLOWED_IPS") + "] lock " + p.Get("LOCK_ATTEMPTS") + "/" + p.Get("LOCK_MINUTES") + " 2FA " + p.Get("REQUIRE_TOTP"));
                            }
                            Reply(ctx, 200, new Msg().Set("ok", 1)); return;
                        }
                    case "quota":
                        {
                            var b = Body(ctx);
                            long q = (long)(double.Parse(b["quotaGB"], CultureInfo.InvariantCulture) * 1024 * 1024 * 1024);
                            Profile p;
                            lock (users.ProfileLock)
                            {
                                p = users.LoadProfile(login);
                                p.SetAttr("QUOTA", q);
                                if (!string.IsNullOrEmpty(b["quotaType"])) p.SetAttr("QUOTA_TYPE", b["quotaType"]);
                                users.SaveProfile(login, p);
                            }
                            users.UpdateQuotaIndex(login, q);
                            SysLog.Write(ip, "Admin", admin + " quota " + login + " = " + q + " " + p.Get("QUOTA_TYPE"));
                            Reply(ctx, 200, new Msg().Set("ok", 1)); return;
                        }
                }
            }
            if (seg.Length == 3 && (seg[2] == "rebuild" || seg[2] == "verify") && method == "POST")
            {
                var b = Body(ctx);
                // H-13: a set that is not the customer's (or no set id at all) is a clear 404, never a server error —
                // and never a new empty store folder made for a well-formed id that does not exist
                if (users.LoadProfile(b["login"] ?? "").FindSet(b["set"] ?? "") == null) throw new ApiException(404, "NO_SET", "The backup set does not exist.");
                var store = new SetStore(users.UserDir(b["login"]), b["set"]);
                var r = seg[2] == "rebuild" ? store.Rebuild(b.Bool("verify")) : store.VerifyAll();
                if (seg[2] == "rebuild")
                {
                    lock (users.ProfileLock)
                    {
                        var p = users.LoadProfile(b["login"]);
                        p.SetAttr("LAST_STORAGE_REBUILD", RunId.UnixMs(SystemClock.UtcNow));
                        users.SaveProfile(b["login"], p);
                    }
                    UpdateStats(b["login"], b["set"], null);
                    Atomic.WriteText(Path.Combine(users.UserDir(b["login"]), "logs", "Rebuild", RunId.From(SystemClock.UtcNow) + ".log"), r.ToString());
                }
                SysLog.Write(ip, "Admin", admin + " " + seg[2] + " " + b["login"] + "/" + b["set"] + " " + r);
                Reply(ctx, 200, r);
                return;
            }
            if (seg.Length == 3 && seg[2] == "maintenance" && method == "POST")
            {
                var b = Body(ctx);
                DateTime now = string.IsNullOrEmpty(b["now"]) ? SystemClock.UtcNow : RunId.Parse(b["now"]);
                Reply(ctx, 200, Maintenance(now));
                SysLog.Write(ip, "Admin", admin + " ran maintenance");
                return;
            }
            if (seg.Length == 5 && seg[2] == "keys" && method == "GET")
            {
                var p = Path.Combine(users.UserDir(seg[3]), "db", "keys", seg[4] + ".bin");
                if (!File.Exists(p)) throw new ApiException(404, "NO_KEY", "The key is not stored on the server (key storage is off for this user).");
                SysLog.Write(ip, "Admin", admin + " READ ENCRYPTION KEY of " + seg[3] + "/" + seg[4]);
                Reply(ctx, 200, new Msg().Set("key", Convert.ToBase64String(KeyVault.Unprotect(cfg.SystemHome, OnlineBackup.Core.Atomic.ReadAllBytes(p)))));
                return;
            }
            if (seg.Length == 5 && seg[2] == "users" && seg[4] == "unfreeze" && method == "POST")
            {
                lock (users.ProfileLock)
                {
                    var p = users.LoadProfile(seg[3]);
                    p.SetAttr("RETENTION_FROZEN", "N");
                    foreach (var e in p.SetElements) e.SetAttributeValue("SUSPECT_RANSOMWARE", null);
                    users.SaveProfile(seg[3], p);
                }
                SysLog.Write(ip, "Admin", admin + " released the retention freeze of " + seg[3]);
                Reply(ctx, 200, new Msg().Set("ok", 1));
                return;
            }
            if (seg.Length == 3 && seg[2] == "clientpackage" && method == "GET")
            {
                // PKG-010: the customer's installation in the IT company's name (vendor administrators: their own branding)
                var os = ctx.Request.QueryString["os"] ?? "windows";   // PKG-060 linux, PKG-070 mac
                bool linux = os == "linux", mac = os == "mac", asZip = os == "zip";   // SETUP-C60: Windows is one Setup.exe; the ZIP stays for technicians (silent installation)
                if (linux) PilotScope.Check(cfg, "The Linux client");   // PILOT-010: Windows only
                if (mac) PilotScope.Check(cfg, "The Mac client");
                var zip = linux ? ClientPackage.BuildLinux(cfg, vendor) : mac ? ClientPackage.BuildMac(cfg, vendor) : asZip ? ClientPackage.Build(cfg, vendor) : ClientPackage.BuildExe(cfg, vendor);
                SysLog.Write(ip, "Admin", admin + " downloaded the " + (linux ? "Linux " : mac ? "Mac " : asZip ? "Windows ZIP " : "Windows ") + "client package (" + zip.Length + " bytes)");
                var res = ctx.Response;
                res.StatusCode = 200; res.ContentType = linux || mac ? "application/gzip" : asZip ? "application/zip" : "application/vnd.microsoft.portable-executable"; res.ContentLength64 = zip.Length;
                res.AddHeader("Content-Disposition", "attachment; filename=\"" + (linux ? ClientPackage.FileNameLinux(cfg, vendor) : mac ? ClientPackage.FileNameMac(cfg, vendor) : asZip ? ClientPackage.FileName(cfg, vendor) : ClientPackage.FileNameExe(cfg, vendor)) + "\"");
                res.OutputStream.Write(zip, 0, zip.Length); res.Close();
                return;
            }
            if (seg.Length == 4 && seg[2] == "update" && seg[3] == "source")
            {
                // UPD-040: the owner's private update store and its read-only key (the key is never sent back)
                if (!super) throw new ApiException(403, "RIGHTS", "Only the server's administrator can update it.");
                if (method == "POST") { var b = Body(ctx); try { Updater.SaveSource(cfg, b["repo"], b["token"], b.Bool("auto"), admin, ip); } catch (InvalidOperationException e) { throw new ApiException(400, "SOURCE", e.Message); } }
                Reply(ctx, 200, Updater.SourceInfo(cfg)); return;
            }
            if (seg.Length == 4 && seg[2] == "update" && seg[3] == "upload" && method == "POST")
            {
                // UPD-030: the update package's parts, joined by the browser — only the main administrator, only on the server itself
                if (!super) throw new ApiException(403, "RIGHTS", "Only the server's administrator can update it.");
                System.Net.IPAddress a;
                if (!System.Net.IPAddress.TryParse(ip ?? "", out a) || !System.Net.IPAddress.IsLoopback(a)) throw new ApiException(403, "LOCAL_ONLY", "An update from files is done on the server itself: open https://localhost:8443/admin there.");
                try { Reply(ctx, 200, new Msg().Set("version", Updater.FromUpload(cfg, ctx.Request.InputStream, 600L << 20, admin, ip, ctx.Request.Headers["X-Update-Signature"]))); }
                catch (InvalidOperationException e) { throw new ApiException(400, "UPDATE", e.Message); }
                return;
            }
            if (seg.Length == 3 && seg[2] == "update")
            {
                // UPD-010: "Update" in the top bar — the newest version from the software vendor, signed by the vendor
                if (method == "POST") { if (!super) throw new ApiException(403, "RIGHTS", "Only the server's administrator can update it."); Updater.Start(cfg, admin, ip); }
                var st = Updater.Status(cfg, Q(ctx, "check") == "1");
                var m = new Msg(); foreach (var kv in st) m.Set(kv.Key, Convert.ToString(kv.Value is bool b ? (b ? 1 : 0) : kv.Value, CultureInfo.InvariantCulture));
                Reply(ctx, 200, m); return;
            }
            if (seg.Length == 3 && seg[2] == "license")
            {
                // LIC-060: this server's id and licence; a new licence is pasted here
                if (method == "POST")
                {
                    var key = (Body(ctx)["key"] ?? "").Trim();
                    var check = License.Check(key, cfg.ServerId, SystemClock.UtcNow);
                    if (key.Length > 0 && !check.Valid) throw new ApiException(400, "LICENSE", check.Reason);
                    lock (cfg)
                    {
                        var el = cfg.Doc.Root.Element("LICENSE"); if (el == null) { el = new System.Xml.Linq.XElement("LICENSE"); cfg.Doc.Root.Add(el); }
                        el.SetAttributeValue("KEY", key); el.SetAttributeValue("TEMP_SINCE", null); el.SetAttributeValue("ONLINE_STATUS", null); cfg.Save(); cfg.ResetLicense();
                    }
                    if (key.Length > 0) LicenseCheckin.Run(cfg, users, mailer, cfg.Clock());
                    SysLog.Write(ip, "Admin", admin + " licence " + (key.Length > 0 ? check.Id + " " + check.Edition : "removed"));
                }
                if (method == "GET" && Q(ctx, "check") == "1") LicenseCheckin.Run(cfg, users, mailer, cfg.Clock());
                string updated = null;
                if (method == "GET" && Q(ctx, "update") == "1")
                {
                    updated = LicenseCheckin.Update(cfg, users, mailer, cfg.Clock());
                    SysLog.Write(ip, "Admin", admin + " licence update: " + updated + (updated == "UPDATED" ? " " + cfg.License.Id + " " + cfg.License.Edition : ""));
                }
                var le = cfg.Doc.Root.Element("LICENSE");
                Reply(ctx, 200, cfg.License.ToMsg().Set("updated", updated).Set("serverId", cfg.ServerId).Set("online", le == null ? null : (string)le.Attribute("ONLINE_STATUS")).Set("lastOk", le == null ? null : (string)le.Attribute("LAST_OK")).Set("users", users.Logins().Count()).Set("devices", users.ActiveComputers())); return;
                return;
            }
            if (seg.Length == 3 && seg[2] == "settings")
            {
                if (method == "GET") { Reply(ctx, 200, SettingsMsg()); return; }
                SaveSettings(Body(ctx), admin, ip);
                Reply(ctx, 200, SettingsMsg());
                return;
            }
            if (seg.Length == 3 && seg[2] == "testmail" && method == "POST")
            {
                var b = Body(ctx);
                bool sent = mailer.Send(new[] { b["to"] }, "Test e-mail", "<p>This is a test e-mail from the backup server.</p>", "test");
                Reply(ctx, 200, new Msg().Set("sent", sent ? 1 : 0));
                return;
            }
            if (seg.Length == 4 && seg[2] == "totp" && method == "POST")
            {
                // every administrator sets up its own two-step (mandatory: the first sign-in starts here)
                if (seg[3] == "enable") { Reply(ctx, 200, Staff.TotpEnable(cfg, admin)); return; }
                if (seg[3] == "confirm")
                {
                    Staff.TotpConfirm(cfg, admin, Body(ctx)["code"], SystemClock.UtcNow);
                    s.Enroll = false;
                    SysLog.Write(ip, "Admin", admin + " set up two-step verification");
                    Reply(ctx, 200, new Msg().Set("ok", 1));
                    return;
                }
            }
            // TECH-010: the administrators (the system's administrators — a vendor keeps its own)
            if (seg.Length >= 3 && seg[2] == "staff")
            {
                if (!super) throw new ApiException(403, "VENDOR", "Only the system administrator can do this.");
                if (seg.Length == 3 && method == "GET") { Reply(ctx, 200, Staff.List(cfg)); return; }
                if (seg.Length == 3 && method == "POST") { Staff.Save(cfg, Body(ctx), admin, ip); Reply(ctx, 200, Staff.List(cfg)); return; }
                if (seg.Length == 5 && method == "POST") { Staff.Act(cfg, Uri.UnescapeDataString(seg[3]), seg[4], admin, ip); Reply(ctx, 200, Staff.List(cfg)); return; }
                Need(false);
            }
            if (seg.Length == 3 && seg[2] == "logs" && method == "GET")
            {
                Reply(ctx, 200, Logs(Q(ctx, "cat"), Q(ctx, "file"), Q(ctx, "login"), Q(ctx, "set"), Q(ctx, "filter")));
                return;
            }
            if (seg.Length == 3 && seg[2] == "replicate" && method == "POST")
            {
                PilotScope.Check(cfg, PilotScope.Replication);   // PILOT-010 / ST-07
                if (!cfg.License.Has("REPLICATION")) throw new ApiException(402, "LICENSE", "A second server is not included in the licence.");
                Reply(ctx, 200, new Msg().Set("sent", Replication.RunOnce()).Set("pending", Replication.Pending));
                return;
            }
            throw new ApiException(404, "NOT_FOUND", "Not found.");
        }

        // ---------------------------------------------------------------- system settings (no password ever leaves the server)

        Msg SettingsMsg()
        {
            var r = cfg.Doc.Root;
            Func<string, XElement> el = n => r.Element(n) ?? new XElement(n);
            var m = new Msg().Set("host", (string)r.Attribute("HOST_NAME")).Set("admin", (string)cfg.Admin.Attribute("LOGIN_NAME"))
                .Set("publicUrl", (string)r.Attribute("PUBLIC_URL")).Set("certPin", (string)r.Attribute("CERT_PIN"))
                .Set("adminTotp", string.IsNullOrEmpty((string)cfg.Admin.Attribute("TOTP_SECRET")) ? 0 : 1)
                .Set("autoLock", (string)el("SECURITY").Attribute("AUTO_LOCK_ATTEMPTS")).Set("lockMinutes", (string)el("SECURITY").Attribute("LOCK_MINUTES"))
                .Set("singleLevel", (string)el("SECURITY").Attribute("SINGLE_LEVEL_ACCESS"))
                .Set("ransomMinFiles", (string)el("SECURITY").Attribute("RANSOM_MIN_FILES") ?? "50").Set("ransomPercent", (string)el("SECURITY").Attribute("RANSOM_PERCENT") ?? "30")
                .Set("senderName", (string)el("REPORT_SENDER").Attribute("NAME")).Set("senderEmail", (string)el("REPORT_SENDER").Attribute("EMAIL"))
                .Set("exportProfiles", (string)el("EXPORT").Attribute("PROFILES")).Set("exportLogs", (string)el("EXPORT").Attribute("LOGS"))
                .Set("replicationUrl", (string)el("REPLICATION").Attribute("URL")).Set("replicationOn", (string)el("REPLICATION").Attribute("ENABLED") == "Y" ? 1 : 0)
                .Set("replicationHasToken", string.IsNullOrEmpty((string)el("REPLICATION").Attribute("TOKEN_ENC")) ? 0 : 1).Set("replicationPending", Replication.Pending)
                .Set("isReplica", Replicator.IsReplica(cfg) ? 1 : 0);
            foreach (var a in Vendors.BrandAttrs) m.Set("brand" + a, (string)el("BRANDING").Attribute(a));
            foreach (var smtp in r.Elements("SMTP"))
                m.Add("smtp", new Msg().Set("host", (string)smtp.Attribute("HOST")).Set("port", (string)smtp.Attribute("PORT")).Set("security", (string)smtp.Attribute("SECURITY"))
                    .Set("login", (string)smtp.Attribute("LOGIN")).Set("hasPassword", string.IsNullOrEmpty((string)smtp.Attribute("PASSWORD_ENC")) ? 0 : 1));
            foreach (var c in r.Elements("ADMIN_CONTACT")) m.Add("contacts", new Msg().Set("name", (string)c.Attribute("NAME")).Set("email", (string)c.Attribute("EMAIL")));
            foreach (var h in users.HomesReport()) m.Add("homes", h);
            var aiSettings = Ai.SettingsMsg(cfg); foreach (var k in aiSettings.Keys) m.Set(k, aiSettings[k]);
            return m;
        }

        void SaveSettings(Msg b, string admin, string ip)
        {
            var r = cfg.Doc.Root;
            Func<string, XElement> el = n => { var e = r.Element(n); if (e == null) { e = new XElement(n); r.Add(e); } return e; };
            // PILOT-010: the second server (ST-07) and the AI assistant (UI-08) cannot be set up — refused before anything is saved
            if (b.Bool("replicationOn") || !string.IsNullOrWhiteSpace(b["replicationUrl"]) || !string.IsNullOrEmpty(b["replicationToken"]) || !string.IsNullOrEmpty(b["replicaReceiverToken"])) PilotScope.Check(cfg, PilotScope.Replication);
            if (b.Bool("aiOn") || !string.IsNullOrEmpty(b["aiKey"]) || b.Bool("aiAutoDiagnose") || b.Bool("aiSearch") || !string.IsNullOrWhiteSpace(b["aiTicketUrl"])) PilotScope.Check(cfg, PilotScope.Ai);
            if (b["host"] != null) r.SetAttributeValue("HOST_NAME", b["host"]);
            if (b["publicUrl"] != null)
            {
                var u = b["publicUrl"].Trim().TrimEnd('/');
                Uri parsed;
                if (u.Length > 0 && (!Uri.TryCreate(u, UriKind.Absolute, out parsed) || (parsed.Scheme != "https" && parsed.Scheme != "http"))) throw new ApiException(400, "URL", "Server address for customers: for example https://backup.company.com:8443");
                r.SetAttributeValue("PUBLIC_URL", u);
            }
            if (b["certPin"] != null) r.SetAttributeValue("CERT_PIN", b["certPin"].Replace(":", "").Trim().ToLowerInvariant());
            if (b["autoLock"] != null) el("SECURITY").SetAttributeValue("AUTO_LOCK_ATTEMPTS", Users.ClampAttempts(b.Int("autoLock", 3)));
            if (b["lockMinutes"] != null) el("SECURITY").SetAttributeValue("LOCK_MINUTES", Users.ClampMinutes(b.Int("lockMinutes", 30)));
            if (b["singleLevel"] != null) el("SECURITY").SetAttributeValue("SINGLE_LEVEL_ACCESS", b["singleLevel"]);
            if (b["ransomMinFiles"] != null) el("SECURITY").SetAttributeValue("RANSOM_MIN_FILES", Math.Max(1, b.Int("ransomMinFiles", 50)));
            if (b["ransomPercent"] != null) el("SECURITY").SetAttributeValue("RANSOM_PERCENT", Math.Max(1, b.Int("ransomPercent", 30)));
            if (b["senderName"] != null) el("REPORT_SENDER").SetAttributeValue("NAME", b["senderName"]);
            if (b["senderEmail"] != null) el("REPORT_SENDER").SetAttributeValue("EMAIL", b["senderEmail"]);
            if (b["exportProfiles"] != null) el("EXPORT").SetAttributeValue("PROFILES", b["exportProfiles"]);
            if (b["exportLogs"] != null) el("EXPORT").SetAttributeValue("LOGS", b["exportLogs"]);
            if (b["replicationUrl"] != null) el("REPLICATION").SetAttributeValue("URL", b["replicationUrl"]);
            if (b["replicationOn"] != null) el("REPLICATION").SetAttributeValue("ENABLED", b.Bool("replicationOn") ? "Y" : "N");
            if (!string.IsNullOrEmpty(b["replicationToken"])) el("REPLICATION").SetAttributeValue("TOKEN_ENC", Convert.ToBase64String(KeyVault.Protect(cfg.SystemHome, Encoding.UTF8.GetBytes(b["replicationToken"]))));
            if (!string.IsNullOrEmpty(b["replicaReceiverToken"]))
                el("REPLICA_RECEIVER").SetAttributeValue("TOKEN_HASH", Bytes.Hex(Bytes.Sha256(Encoding.UTF8.GetBytes(b["replicaReceiverToken"]))));
            if (b["replicaDeleteDelayDays"] != null) el("REPLICA_RECEIVER").SetAttributeValue("DELETE_DELAY_DAYS", Math.Max(0, b.Int("replicaDeleteDelayDays", 14)));
            Ai.SaveSettings(cfg, b);
            Vendors.CheckLogo(b["brandLOGO"]);
            foreach (var a in Vendors.BrandAttrs) if (b["brand" + a] != null) el("BRANDING").SetAttributeValue(a, b["brand" + a]);
            if (b["smtpSet"] == "1")
            {
                var old = r.Elements("SMTP").ToList();
                old.ForEach(x => x.Remove());
                int i = 0;
                foreach (var sm in b.List("smtp"))
                {
                    var e = new XElement("SMTP", new XAttribute("HOST", sm["host"] ?? ""), new XAttribute("PORT", sm["port"] ?? "25"), new XAttribute("SECURITY", sm["security"] ?? "STARTTLS"), new XAttribute("LOGIN", sm["login"] ?? ""));
                    if (!string.IsNullOrEmpty(sm["password"])) e.SetAttributeValue("PASSWORD_ENC", Convert.ToBase64String(KeyVault.Protect(cfg.SystemHome, Encoding.UTF8.GetBytes(sm["password"]))));
                    else if (i < old.Count && (string)old[i].Attribute("HOST") == sm["host"]) e.SetAttributeValue("PASSWORD_ENC", (string)old[i].Attribute("PASSWORD_ENC"));   // unchanged password kept
                    r.Add(e); i++;
                }
            }
            if (b["contactsSet"] == "1")
            {
                r.Elements("ADMIN_CONTACT").Remove();
                foreach (var c in b.List("contacts")) r.Add(new XElement("ADMIN_CONTACT", new XAttribute("NAME", c["name"] ?? ""), new XAttribute("EMAIL", c["email"] ?? "")));
            }
            if (b["homesSet"] == "1")
            {
                var existing = r.Elements("USER_HOME").ToList();
                foreach (var h in b.List("homes"))
                {
                    var path = Path.GetFullPath(h["path"]);
                    var e = existing.FirstOrDefault(x => string.Equals((string)x.Attribute("PATH"), path, StringComparison.OrdinalIgnoreCase));
                    if (e == null) { Directory.CreateDirectory(path); e = new XElement("USER_HOME", new XAttribute("PATH", path), new XAttribute("CAPACITY_GB", "")); r.Add(e); }
                    e.SetAttributeValue("MAX_QPS", h["maxQps"] ?? "UNLIMITED");
                }
            }
            cfg.Save();
            SysLog.Write(ip, "Admin", admin + " changed the system settings (" + string.Join(",", b.Keys.Where(k => !k.ToLowerInvariant().Contains("password") && !k.ToLowerInvariant().Contains("token")).ToArray()) + ")");
        }

        /// <summary>The log viewer: categories of the server, and per user / set the Backup, Restore, Retention and Rebuild logs.</summary>
        Msg Logs(string cat, string file, string login, string set, string filter)
        {
            var m = new Msg();
            string dir;
            if (!string.IsNullOrEmpty(login))
            {
                if (!new[] { "Backup", "Restore", "Retention", "Rebuild" }.Contains(cat)) throw new ApiException(400, "BAD_CAT", "Invalid log type.");
                // bug 123 (AU-07): the set went into the path unchecked - "../../bob/logs/<id>" or an absolute folder read another
                // customer's job log. A set id is letters, digits, '-' and '_' only.
                if (!string.IsNullOrEmpty(set) && !System.Text.RegularExpressions.Regex.IsMatch(set, "^[A-Za-z0-9_-]{1,64}$")) throw new ApiException(400, "BAD_SET", "Invalid backup set.");
                dir = cat == "Rebuild" ? Path.Combine(users.UserDir(login), "logs", "Rebuild") : Path.Combine(users.UserDir(login), "logs", set ?? "", cat);
            }
            else
            {
                if (!new[] { "System", "Access", "Admin", "BackupErrors", "Email", "Update" }.Contains(cat)) throw new ApiException(400, "BAD_CAT", "Invalid log type.");
                dir = SysLog.Dir(cat);
            }
            if (!Directory.Exists(dir)) return m;
            var files = Directory.GetFiles(dir, "*.log").OrderByDescending(f => f, StringComparer.Ordinal).ToList();
            foreach (var f in files.Take(200)) m.Add("files", new Msg().Set("name", Path.GetFileName(f)).Set("size", new FileInfo(f).Length));
            var pick = string.IsNullOrEmpty(file) ? files.FirstOrDefault() : files.FirstOrDefault(f => Path.GetFileName(f) == file);
            if (pick != null)
            {
                var lines = Atomic.ReadLinesShared(pick).ToList();
                if (!string.IsNullOrEmpty(filter)) lines = lines.Where(l => l.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                m.Set("file", Path.GetFileName(pick)).Set("text", string.Join("\n", lines.Skip(Math.Max(0, lines.Count - 2000))));
            }
            return m;
        }

        // ---------------------------------------------------------------- the second server (off-site copy)

        void Replica(HttpListenerContext ctx, string[] seg, string ip)
        {
            PilotScope.Check(cfg, PilotScope.Replication);   // PILOT-010 / ST-07: this server does not receive either
            Replicator.CheckToken(cfg, ctx.Request.Headers["X-Replica-Token"]);
            var method = ctx.Request.HttpMethod;
            if (seg.Length == 3 && seg[2] == "user" && method == "POST")
            {
                var b = Body(ctx);
                users.EnsureReplicaUser(b["login"], b.Long("quota"), ip);
                Reply(ctx, 200, new Msg().Set("ok", 1));
                return;
            }
            if (seg.Length == 3 && seg[2] == "file" && method == "PUT")
            {
                var login = Q(ctx, "login"); var path = Q(ctx, "path") ?? "";
                if (!(path.StartsWith("files/") || path.StartsWith("db/") || path.StartsWith("logs/")) || path.Contains("..")) throw new ApiException(400, "BAD_PATH", "Invalid path.");
                var userDir = users.UserDir(login);
                var p = Path.Combine(userDir, path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                // Objects are never overwritten (append-only); settings, devices, keys and logs follow the first server.
                bool overwrite = Q(ctx, "overwrite") == "1" && !path.StartsWith("files/");
                if (Q(ctx, "protect") == "1" && path.StartsWith("db/keys/"))
                {
                    var ms = new MemoryStream(); ctx.Request.InputStream.CopyTo(ms);
                    ReceiveInto(p, new MemoryStream(KeyVault.Protect(cfg.SystemHome, ms.ToArray())), true);
                }
                else ReceiveInto(p, ctx.Request.InputStream, overwrite);
                Reply(ctx, 200, new Msg().Set("ok", 1));
                return;
            }
            if (seg.Length == 3 && seg[2] == "commit" && method == "POST")
            {
                var b = Body(ctx);
                var store = new SetStore(users.UserDir(b["login"]), b["set"]);
                store.ApplyReplicated(b["job"], b.List("moves").Select(m => new[] { m["src"], m["dst"] }).ToList(), b["stats"] ?? "");
                UpdateStats(b["login"], null, null);
                Reply(ctx, 200, new Msg().Set("ok", 1));
                return;
            }
            if (seg.Length == 3 && seg[2] == "retention" && method == "POST")
            {
                var b = Body(ctx);
                var store = new SetStore(users.UserDir(b["login"]), b["set"]);
                store.ExpireJobs(b.List("expired").Select(x => x["id"]));
                Replicator.QueueDeletes(cfg, b["login"], b["set"], b.List("folders").Select(x => x["id"]));
                Reply(ctx, 200, new Msg().Set("ok", 1));
                return;
            }
            throw new ApiException(404, "NOT_FOUND", "Not found.");
        }

        static void ReceiveInto(string p, Stream body, bool overwrite)
        {
            var tmp = p + ".recv" + Guid.NewGuid().ToString("N").Substring(0, 6);
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write)) { body.CopyTo(fs); fs.Flush(true); }
            if (File.Exists(p))
            {
                if (!overwrite)
                {
                    bool same;
                    using (var a = File.OpenRead(p)) using (var b2 = File.OpenRead(tmp)) same = Bytes.Sha256Hex(a) == Bytes.Sha256Hex(b2);
                    File.Delete(tmp);
                    if (!same) throw new ApiException(409, "IMMUTABLE", "An existing file on the secondary server is never overwritten.");
                    return;
                }
                File.Replace(tmp, p, null);
                return;
            }
            File.Move(tmp, p);
        }

        /// <summary>Daily: retention of every set, log retention, statistics. Never touches Current.</summary>
        public Msg Maintenance(DateTime nowUtc)
        {
            var m = new Msg();
            // Bug 28: a task of the nightly maintenance that fails (retention, settings backup, set sizes, …) was only in the
            // system log — retention could fail for months unseen until the disk was full. Every failure is collected,
            // returned, and sent to the administrators in one alert.
            var errors = new List<string>();
            Action<string, Exception> fail = (what, e) => { SysLog.Write(null, "System", "error: " + what + " " + e.Message); errors.Add(what + ": " + e.Message); };
            try { Runs.Purge(nowUtc); } catch (Exception e) { fail("run history", e); }
            try { m.Set("recycleErased", users.PurgeRecycled(nowUtc)); } catch (Exception e) { fail("recycle bin", e); }   // DEL-010
            foreach (var login in users.Logins())
            {
                Profile p;
                try { p = users.LoadProfile(login); } catch (Exception e) { fail("profile " + login, e); continue; }
                bool frozen = p.Get("RETENTION_FROZEN") == "Y";
                bool replica = Replicator.IsReplica(cfg);
                foreach (var s in p.Sets)
                {
                    try
                    {
                        // PILOT-010: a set outside the pilot is kept exactly as it is — no retention, no trash purge, no log removal,
                        // no "did not run" alert (it is not run on purpose); one line in its retention log says why
                        var outside = PilotScope.Why(cfg, s);
                        if (outside != null)
                        {
                            Atomic.AppendLine(Path.Combine(users.UserDir(login), "logs", s.Id, "Retention", nowUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log"),
                                AhsayLog.Line(SystemClock.UtcNow, "warn", message: "Kept as it is, nothing deleted: " + outside));
                            continue;
                        }
                        MissedBackupCheck(login, p, s, nowUtc);
                        if (s.Engine == "RESTIC") { if (!replica) m.Add("sets", ResticMaintenance(login, p, s, nowUtc)); continue; }
                        var store = new SetStore(users.UserDir(login), s.Id);
                        if (replica) continue;   // the second server deletes only what the first one asks for, after its delay
                        if (frozen)
                        {
                            Atomic.AppendLine(Path.Combine(users.UserDir(login), "logs", s.Id, "Retention", nowUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log"),
                                AhsayLog.Line(SystemClock.UtcNow, "warn", message: "Retention frozen (suspected ransomware): nothing deleted"));
                            continue;
                        }
                        var r = store.ApplyRetention(s.Retention, nowUtc);
                        if (r.List("folders").Count + r.List("expired").Count > 0)
                        {
                            var ev = new Msg().Set("type", "retention").Set("login", login).Set("set", s.Id);
                            foreach (var x in r.List("folders")) ev.Add("folders", x);
                            foreach (var x in r.List("expired")) ev.Add("expired", x);
                            Replication.Enqueue(ev);
                        }
                        var line = AhsayLog.Info(SystemClock.UtcNow, "Retention policy " + s.Retention.Describe() + ": expired points " + r["expiredPoints"] + ", deleted objects " + r["deletedObjects"] + ", freed " + r["freed"] + " bytes");
                        Atomic.AppendLine(Path.Combine(users.UserDir(login), "logs", s.Id, "Retention", nowUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log"), line);
                        m.Add("sets", r.Set("login", login).Set("set", s.Id));
                        var logRoot = Path.Combine(users.UserDir(login), "logs", s.Id);
                        if (Directory.Exists(logRoot))
                            foreach (var f in Directory.EnumerateFiles(logRoot, "*.log", SearchOption.AllDirectories).ToList())
                                if ((nowUtc - File.GetLastWriteTimeUtc(f)).TotalDays > s.LogRetentionDays) { File.Delete(f); if (File.Exists(f + ".ai")) File.Delete(f + ".ai"); }   // with the AI explanation kept beside it
                    }
                    catch (Exception e) { fail("retention " + login + "/" + s.Name + " (" + s.Id + ")", e); }
                }
                UpdateStats(login, null, null);
                exporter.Profile(login, users.UserDir(login));
            }
            try { if (Replicator.IsReplica(cfg)) m.Set("replicaDeletes", Replicator.RunDueDeletes(cfg, users, nowUtc)); } catch (Exception e) { fail("replica deletes", e); }
            try { BackupChecks.Snapshot(cfg, users, nowUtc); } catch (Exception e) { fail("set sizes", e); }   // CHK-010
            try { if (!Replicator.IsReplica(cfg) && ConfigBackup.Due(cfg, nowUtc)) m.Set("settingsBackup", ConfigBackup.Make(cfg, users, nowUtc, null, null)); } catch (Exception e) { fail("settings backup", e); }   // CFGBK-010
            try { DiskForecastCheck(nowUtc); } catch (Exception e) { fail("disk forecast", e); }
            if (errors.Count > 0)
            {
                foreach (var x in errors) m.Add("errors", new Msg().Set("error", x));
                try { mailer.Alert("✗ Nightly maintenance: " + errors.Count + " task(s) failed", "<p>These tasks of the nightly maintenance failed and will be tried again tomorrow. Until they succeed, old versions are not deleted and the disk keeps filling.</p><ul>" + string.Join("", errors.Select(x => "<li>" + Fmt.H(x) + "</li>")) + "</ul>"); }
                catch (Exception e) { SysLog.Write(null, "System", "error: maintenance alert " + e.Message); }
            }
            return m;
        }

        /// <summary>AI-060: the free space of every storage folder, once a day; an alert (at most weekly) when one fills within 30 days.</summary>
        void DiskForecastCheck(DateTime nowUtc)
        {
            var homes = users.HomesReport();
            Insights.RecordDisks(cfg.SystemHome, homes, nowUtc);
            var soon = Insights.DiskForecast(cfg.SystemHome, homes, nowUtc).Where(d => d["days"] != null && long.Parse(d["days"], CultureInfo.InvariantCulture) < 30).ToList();
            if (soon.Count == 0) return;
            var mark = Path.Combine(cfg.SystemHome, "stats", "disk-alert.txt");
            DateTime last;
            if (File.Exists(mark) && RunId.TryParse(OnlineBackup.Core.Atomic.ReadAllText(mark).Trim(), out last) && (nowUtc - last).TotalDays < 7) return;
            Atomic.WriteText(mark, RunId.From(nowUtc));
            mailer.Alert("⚠ Storage forecast — the backup disk fills up soon", string.Join("", soon.Select(d => "<p>" + Fmt.H(d["path"]) + ": " + Fmt.Size(d.Long("free")) + " free, full in about " + d["days"] + " days at the current pace.</p>")));
        }

        /// <summary>AI-060/070: forecasts for the management site — storage folders, customers whose quota fills within 60 days, sets likely to miss their next backup.</summary>
        Msg InsightsMsg(string vendor, DateTime nowUtc)
        {
            var m = new Msg().Set("ai", Ai.On(cfg) ? 1 : 0);
            if (vendor == null) foreach (var d in Insights.DiskForecast(cfg.SystemHome, users.HomesReport(), nowUtc)) m.Add("disks", d);
            foreach (var login in users.Logins())
            {
                Profile p;
                try { p = users.LoadProfile(login); } catch (Exception) { continue; }
                if (vendor != null && !string.Equals(p.Get("OWNER"), vendor, StringComparison.OrdinalIgnoreCase)) continue;
                var dir = users.UserDir(login);
                var sets = p.Sets;
                var qd = Insights.QuotaDays(dir, sets, p.GetLong("QUOTA"), nowUtc);
                if (qd.HasValue && qd.Value < 60)
                    m.Add("quota", new Msg().Set("login", login).Set("alias", p.Get("ALIAS")).Set("used", Usage(p)).Set("quota", p.GetLong("QUOTA")).Set("days", (long)Math.Floor(qd.Value)));
                foreach (var s in sets)
                {
                    var h = Insights.History(dir, s.Id);
                    var r = Insights.MissRisk(h, nowUtc);
                    if (r.Int("risk") >= 30)
                        m.Add("risk", r.Set("login", login).Set("alias", p.Get("ALIAS")).Set("set", s.Id).Set("setName", s.Name).Set("computer", s.Computer));
                    var normal = h.Where(x => !x.Suspect && x.Prev > 0).Select(x => x.ChangeRate).ToList();
                    m.Add("learned", new Msg().Set("login", login).Set("set", s.Id).Set("setName", s.Name).Set("runs", h.Count)
                        .Set("learned", normal.Count >= Insights.MinRunsToLearn ? 1 : 0));
                }
            }
            return m;
        }

        /// <summary>A set that has not completed a backup for 2 days: one alert a day to the administrators (and ITSguard opens a call).</summary>
        long totalStored; DateTime totalAt;
        /// <summary>LIC-035: all stored data of the server against the licence (refreshed every 10 minutes); over it, new data stops.</summary>
        void LicenseStorageCheck()
        {
            var lic = cfg.License;
            if (lic.MaxStorageGB <= 0) return;
            if ((SystemClock.UtcNow - totalAt).TotalMinutes > 10)
            {
                totalStored = users.Logins().Sum(l => { try { var p = users.LoadProfile(l); return p.GetLong("DATA_SIZE") + p.GetLong("RETAIN_SIZE"); } catch (Exception) { return 0L; } });
                totalAt = SystemClock.UtcNow;
            }
            if (totalStored >= (long)(lic.MaxStorageGB * 1024 * 1024 * 1024))
                throw new ApiException(507, "LICENSE_STORAGE", "The server's backup volume reached the licence limit (" + lic.MaxStorageGB + " GB). Existing backups are kept.");
        }

        /// <summary>Whether a user belongs to a vendor (an unknown user counts as not owned).</summary>
        bool Owns(string login, string vendor)
        {
            try { return string.Equals(users.LoadProfile(Uri.UnescapeDataString(login)).Get("OWNER"), vendor, StringComparison.OrdinalIgnoreCase); }
            catch (Exception) { return false; }
        }

        // ---------------------------------------------------------------- WEB-010: restore from the web (the customer's own session)

        static readonly Dictionary<string, KeyValuePair<int, DateTime>> wrongKeys = new Dictionary<string, KeyValuePair<int, DateTime>>();

        void WebRestoreApi(HttpListenerContext ctx, string[] seg, string login, string ip)
        {
            var method = ctx.Request.HttpMethod;
            var prof = users.LoadProfile(login);
            if (seg.Length == 3 && seg[2] == "sets" && method == "GET")
            {
                var vendor = VendorOf(login);
                var m = new Msg().Set("login", login).Set("product", Vendors.Brand(cfg, vendor, "PRODUCT", "ITSguard Server Online")).Set("slogan", Vendors.Brand(cfg, vendor, "SLOGAN", "")).Set("color", Vendors.Brand(cfg, vendor, "COLOR", "")).Set("accent", Vendors.Brand(cfg, vendor, "ACCENT", ""))
                    .Set("logo", Vendors.Brand(cfg, vendor, "LOGO", ""));
                foreach (var e in prof.SetElements)
                {
                    var s = BackupSetInfo.FromXml(e);
                    m.Add("sets", new Msg().Set("id", s.Id).Set("name", s.Name).Set("type", s.Type).Set("web", s.Engine == "RESTIC" && s.KeyType != "DEFAULT" && new ResticStore(users.UserDir(login), s.Id).Exists ? 1 : 0));
                }
                Reply(ctx, 200, m);
                return;
            }
            Need(seg.Length == 4 && method == "POST");
            var e2 = prof.FindSet(seg[2]); Need(e2 != null);
            var set = BackupSetInfo.FromXml(e2);
            var store = new ResticStore(users.UserDir(login), set.Id);
            Need(store.Exists);
            var b = Body(ctx);
            // wrong keys: 5 within 15 minutes stop further tries for 15 minutes (the key cannot be guessed here)
            lock (wrongKeys)
            {
                KeyValuePair<int, DateTime> w;
                if (wrongKeys.TryGetValue(login, out w) && w.Key >= 5 && SystemClock.UtcNow < w.Value) throw new ApiException(429, "WAIT", "Too many wrong encryption passwords. Try again in 15 minutes.");
            }
            string password;
            try { password = WebRestore.Password(set, b["key"]); }
            catch (ApiException x) when (x.Code == "WRONG_KEY")
            {
                lock (wrongKeys)
                {
                    KeyValuePair<int, DateTime> w;
                    int n = wrongKeys.TryGetValue(login, out w) && SystemClock.UtcNow < w.Value ? w.Key + 1 : 1;
                    wrongKeys[login] = new KeyValuePair<int, DateTime>(n, SystemClock.UtcNow.AddMinutes(15));
                }
                SysLog.Write(ip, "Access", "warn: web restore: wrong encryption password " + login + "/" + set.Id);
                throw;
            }
            lock (wrongKeys) wrongKeys.Remove(login);
            switch (seg[3])
            {
                case "points":
                    { var m = new Msg(); foreach (var p in WebRestore.Points(store.Dir, password)) m.Add("points", p); Reply(ctx, 200, m); return; }
                case "search":
                    {
                        // AI-040: "the Excel file Dana edited on Tuesday" → filters (AI on) or keywords (AI off)
                        var q = (b["q"] ?? "").Trim();
                        if (q.Length == 0) throw new ApiException(400, "QUERY", "Type what to look for.");
                        bool ai = Ai.Search(cfg) && b["mode"] != "keywords";
                        var filter = ai ? Ai.ParseQuery(cfg, q, SystemClock.Now, L.Norm(b["lang"])) : SearchFilter.Keywords(q);
                        var m = new Msg().Set("ai", ai ? 1 : 0).Set("explanation", filter.Explanation)
                            .Set("from", filter.From.HasValue ? filter.From.Value.ToString("s", CultureInfo.InvariantCulture) : null).Set("to", filter.To.HasValue ? filter.To.Value.ToString("s", CultureInfo.InvariantCulture) : null);
                        foreach (var f in WebRestore.Find(store.Dir, password, filter, !ai)) m.Add("files", f);
                        SysLog.Write(ip, "Access", login + " searched " + set.Id + (ai ? " (AI)" : "") + ": " + m.List("files").Count + " results");
                        Reply(ctx, 200, m); return;
                    }
                case "ls":
                    { var m = new Msg(); foreach (var f in WebRestore.Ls(store.Dir, password, b["point"], b["path"])) m.Add("files", f); Reply(ctx, 200, m); return; }
                case "download":
                    {
                        var paths = b.List("paths").Select(x => x["p"]).ToList();
                        var temp = Path.Combine(cfg.SystemHome, "temp"); Directory.CreateDirectory(temp);
                        var tmpZip = Path.Combine(temp, "webrestore-" + Guid.NewGuid().ToString("N") + ".zip");
                        try
                        {
                            long n;
                            using (var f = File.Create(tmpZip)) n = WebRestore.Zip(store.Dir, password, b["point"], paths, temp, f);
                            SysLog.Write(ip, "Access", login + " restored " + n + " files of " + set.Id + " point " + b["point"] + " from the web");
                            var res = ctx.Response;
                            res.StatusCode = 200; res.ContentType = "application/zip"; res.ContentLength64 = new FileInfo(tmpZip).Length;
                            res.AddHeader("Content-Disposition", "attachment; filename=\"restore-" + b["point"] + ".zip\"");
                            res.Headers["Cache-Control"] = "no-store";
                            using (var f = File.OpenRead(tmpZip)) f.CopyTo(res.OutputStream);
                            res.Close();
                        }
                        finally { try { File.Delete(tmpZip); } catch (Exception) { } }
                        return;
                    }
            }
            Need(false);
        }

        // ---------------------------------------------------------------- service calls (TICKETS-010)

        /// <summary>TICKETS-010: the calls of the admin site. A vendor's administrator sees and changes only its own customers' calls.</summary>
        void AdminTickets(HttpListenerContext ctx, string[] seg, string method, string admin, string vendor, bool super, string ip)
        {
            Func<string, bool> visible = (login) => super || (login.Length > 0 && Owns(login, vendor));
            Func<Msg, bool> sees = (t) => super || ((t["login"] ?? "").Length > 0 && Owns(t["login"], vendor));
            Func<string, Msg> mine = (id) => { var t = Calls.Get(id); if (t == null || !sees(t)) throw new ApiException(404, "TICKET", "The call was not found."); return t; };
            Func<Msg, Msg> full = (t) => { var r = Calls.Row(t); foreach (var n in t.List("notes")) r.Add("notes", n); return r; };
            if (seg.Length == 3 && method == "GET")
            {
                var m = new Msg();
                foreach (var t in Calls.List(Q(ctx, "scope") ?? "open", Q(ctx, "login"), Q(ctx, "q"), admin, sees)) m.Add("tickets", Calls.Row(t));
                Reply(ctx, 200, m); return;
            }
            if (seg.Length == 3 && method == "POST")
            {
                var b = Body(ctx);
                if (!string.IsNullOrEmpty(b["id"])) mine(b["id"]);
                if (!visible(b["login"] ?? "")) throw new ApiException(403, "VENDOR", "This customer is not yours.");
                if (!string.IsNullOrEmpty(b["login"]) && !users.Logins().Contains(b["login"])) throw new ApiException(400, "CUSTOMER", "The customer was not found.");
                Reply(ctx, 200, full(Calls.Save(b, admin, ip))); return;
            }
            if (seg.Length == 4 && seg[3] == "deleted" && method == "GET" && super)
            {
                var m = new Msg(); int i = 0;
                foreach (var x in Calls.Deleted()) m.Add("deleted", Calls.Row(x.List("call").First()).Set("index", i++).Set("deletedAt", x["deleted"]).Set("deletedBy", x["by"]));
                Reply(ctx, 200, m); return;
            }
            if (seg.Length == 6 && seg[3] == "deleted" && seg[5] == "restore" && method == "POST" && super) { Reply(ctx, 200, full(Calls.Restore(int.Parse(seg[4], CultureInfo.InvariantCulture), admin))); return; }
            if (seg.Length == 4 && method == "GET") { Reply(ctx, 200, full(mine(seg[3]))); return; }
            if (seg.Length == 5 && seg[4] == "note" && method == "POST") { mine(seg[3]); Reply(ctx, 200, full(Calls.AddNote(seg[3], Body(ctx)["text"], admin, ip))); return; }
            if (seg.Length == 5 && seg[4] == "delete" && method == "POST") { mine(seg[3]); Calls.Delete(seg[3], Body(ctx).Int("revision", -1), admin); SysLog.Write(ip, "Admin", admin + " deleted service call " + seg[3]); Reply(ctx, 200, new Msg().Set("ok", 1)); return; }
            Need(false);
        }

        /// <summary>CONTRACT-010: a customer who has not accepted the current contract accepts it before signing in.</summary>
        void ContractGate(Profile p, Msg b, string ip)
        {
            if (!Contract.Due(cfg, p)) return;
            int v = Contract.Version(cfg);
            if (b.Int("contractVersion") != v) throw new ApiException(412, "CONTRACT", "Read and accept the contract (version " + v + ").");
            Contract.Accept(users, cfg, p.Get("LOGIN_NAME"), v, ip);
        }

        /// <summary>A call must never stop a backup report: any failure is logged and the run goes on.</summary>
        void TicketSafe(Action a) { try { a(); } catch (Exception e) { SysLog.Write(null, "System", "error: service call " + e.GetType().Name + " " + e.Message); } }

        /// <summary>Failures / warnings in a row per set (kept in the set's profile entry) → open or close the automatic calls.</summary>
        void TicketRun(string login, BackupSetInfo set, Msg body)
        {
            TicketSafe(() =>
            {
                lock (users.ProfileLock)
                {
                    var p = users.LoadProfile(login); var e = p.FindSet(set.Id);
                    int fails = (int?)e.Attribute("TICKET_FAILS") ?? 0, warns = (int?)e.Attribute("TICKET_WARNS") ?? 0;
                    var lines = body.List("log").Select(l => l["l"]).Where(l => !string.IsNullOrEmpty(l)).Reverse().Take(5).Reverse();
                    Calls.BackupResult(login, set.Id, set.Name, set.Computer, body["result"] ?? "BS_STOP_BY_SYSTEM_ERROR", ref fails, ref warns, string.Join("\n", lines), p, body["stop"] == "duration");
                    e.SetAttributeValue("TICKET_FAILS", fails); e.SetAttributeValue("TICKET_WARNS", warns);
                    users.SaveProfile(login, p);
                }
            });
        }

        string VendorOf(string login) { try { return users.LoadProfile(login).Get("OWNER"); } catch (Exception) { return null; } }

        /// <summary>Bug 128: a set is late after 48 hours, or — when it runs less often than daily — after its longest gap plus a day.</summary>
        public static int LateAfterHours(BackupSetInfo s) { int gap = LongestScheduleGapHours(s); return gap > 24 ? Math.Max(48, gap + 24) : 48; }

        /// <summary>Bug 128: the longest time between two scheduled runs of the set over a week, in whole hours (0 = no
        /// scheduled run). A daily set is 24; once a week is 168.</summary>
        public static int LongestScheduleGapHours(BackupSetInfo s)
        {
            var slots = new[] { new ScheduleSlot { Days = s.Days ?? "", Hour = s.Hour, Minute = s.Minute } }.Concat(s.MoreSchedules ?? new List<ScheduleSlot>());
            var at = new SortedSet<int>();   // minutes from Sunday 00:00
            foreach (var slot in slots)
                for (int d = 0; d < 7; d++)
                    if (slot.Days != null && slot.Days.Length > d && slot.Days[d] != '-') at.Add(d * 1440 + slot.Hour * 60 + slot.Minute);
            if (at.Count == 0) return 0;
            var t = at.ToList();
            int gap = t[0] + 7 * 1440 - t[t.Count - 1];
            for (int i = 1; i < t.Count; i++) gap = Math.Max(gap, t[i] - t[i - 1]);
            return (gap + 59) / 60;
        }

        void MissedBackupCheck(string login, Profile p, BackupSetInfo s, DateTime nowUtc)
        {
            var e = p.FindSet(s.Id);
            long last; long.TryParse((string)e.Attribute("LAST_BACKUP_COMPLETE") ?? "0", out last);
            long created; long.TryParse(s.Id, out created);
            var since = RunId.FromUnixMs(Math.Max(last, created));
            var ts = Calls.Read(p);   // TICKETS-020: a call after the customer's "hours without a backup"
            // Bug 128: a set that runs less often than daily is late only after its longest gap plus one day (a daily set: 48 h
            // and the call's hours, as before) — a weekly set was reported every week, two days after each good backup
            int late = LongestScheduleGapHours(s) > 24 ? LateAfterHours(s) : 0;
            if (ts.MissedHours > 0 && (nowUtc - since).TotalHours >= Math.Max(ts.MissedHours, late))
                TicketSafe(() => Calls.Auto("missed", login, s.Id, s.Name, s.Computer, "Backup did not run — " + s.Name, "no completed backup since " + since.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC", p));
            if ((nowUtc - since).TotalHours < LateAfterHours(s)) return;
            // Agent M (M-6): two maintenance runs at once (the daily timer and an administrator's button) both read the old
            // mark and both alerted. The mark is checked and set in one step, on the profile as it is now
            lock (users.ProfileLock)
            {
                var fresh = users.LoadProfile(login);
                var fe = fresh.FindSet(s.Id); if (fe == null) return;
                long alerted; long.TryParse((string)fe.Attribute("LAST_MISSED_ALERT") ?? "0", out alerted);
                if (alerted > 0 && (nowUtc - RunId.FromUnixMs(alerted)).TotalHours < 24) return;
                fe.SetAttributeValue("LAST_MISSED_ALERT", RunId.UnixMs(nowUtc));
                users.SaveProfile(login, fresh);
            }
            SysLog.Write(null, "BackupErrors", login + "\t" + s.Id + "\t-\tMISSED BACKUP: no completed backup since " + since.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
            mailer.Alert("⚠ Backup did not run — " + login, "<p>The backup set " + Fmt.H(s.Name) + " of " + Fmt.H(login) + " has not completed a backup since " + TimeSettings.Local(cfg, since).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture) + ".</p>", VendorOf(login));
        }
    }
}
