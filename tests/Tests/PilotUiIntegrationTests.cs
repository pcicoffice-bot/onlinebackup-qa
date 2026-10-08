using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// Pilot release blockers, the user interfaces — INTEGRATION layer: the real server in-process (new Env), real HTTP,
    /// real files; the admin site through its JSON API (what app.js calls), the client window through ClientUi against
    /// that server, the installation wizard through SetupUi against it. Contracts (tests/QA/specs.py UI-01..05, UI-07).
    /// Oracle: the files on disk (SHA-256 of the shipped page files, Profile.xml read with XDocument, restored files),
    /// exact HTTP status codes and values.
    /// </summary>
    [Collection("ClientDir")]
    public class PilotUiIntegrationTests
    {
        const string Pw = "Customer-Pass-1";

        static HttpClient Http() { return new HttpClient(new HttpClientHandler { UseProxy = false }); }
        static string Sha(byte[] b) { using (var h = SHA256.Create()) return Bytes.Hex(h.ComputeHash(b)); }
        static string Sha(string f) { return Sha(File.ReadAllBytes(f)); }

        static AgentException Refused(Action a, int status, string code)
        {
            var e = Assert.Throws<AgentException>(a);
            Assert.Equal(status, e.Status); Assert.Equal(code, e.Code);
            Assert.False(string.IsNullOrWhiteSpace(e.Message));
            return e;
        }

        static BackupSetInfo FileSet(AgentApp app, string src, string name = "Docs")
        {
            return app.CreateSet(app.Interactive(Pw, null), Pw, new BackupSetInfo { Name = name, Sources = { src }, Vss = false });
        }

        /// <summary>The data each page of the admin site loads first (app.js api('GET', …) of the menu's pages).</summary>
        static readonly string[] Pages = { "me", "dashboard", "live", "users", "tasks?hours=24", "checks", "templates", "tickets?scope=open", "ticketsettings", "staff",
            "defaults", "settings", "guard", "time", "contract", "license", "vendors", "configbackup", "deletes", "recycle", "homes" };

        // ================================================================== UI-01

        /// <summary>
        /// UI-01 happy + recovery: the admin site's page and every file it loads are served byte-identical to the shipped
        /// source, with the strict headers; signed in, every page's data opens (200) — a customer's set, its reports and
        /// computers too; a reload (a new connection with the same session) is still signed in; sign-out ends the session
        /// on the server (401 for the old one), and a new sign-in works again. With the pilot switch the same pages open,
        /// the admin site is told the scope, and the AI page — not offered in the pilot — is refused with the reason.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void UI01_EveryPageAndItsDataOpen_AReloadKeepsTheSignIn_SignOutEndsIt(bool pilot)
        {
            using (var env = new Env())
            {
                var repo = PilotAdminUiComponentTests.RepoRoot();
                env.CreateUser("anna", Pw);
                var app = env.Agent("anna", Pw);
                var set = FileSet(app, env.Dir("src"));
                if (pilot) PilotScopeServerTests.PilotOn(env);
                using (var http = Http())
                {
                    var files = new Dictionary<string, string>
                    {
                        { "admin/", Path.Combine(repo, "src", "Server", "Web", "index.html") }, { "admin/app.js", Path.Combine(repo, "src", "Server", "Web", "app.js") },
                        { "admin/app.css", Path.Combine(repo, "src", "Server", "Web", "app.css") }, { "i18n/i18n.js", Path.Combine(repo, "src", "Core", "i18n", "i18n.js") },
                        { "i18n/qrcode.js", Path.Combine(repo, "src", "Core", "i18n", "qrcode.js") }, { "i18n/theme.css", Path.Combine(repo, "src", "Core", "i18n", "theme.css") },
                        { "i18n/he.json", Path.Combine(repo, "src", "Core", "i18n", "he.json") }, { "i18n/en.json", Path.Combine(repo, "src", "Core", "i18n", "en.json") },
                    };
                    foreach (var kv in files)
                    {
                        var r = http.GetAsync(env.Url + kv.Key).Result;
                        Assert.True((int)r.StatusCode == 200, kv.Key + " " + (int)r.StatusCode);
                        Assert.Equal(Sha(kv.Value), Sha(r.Content.ReadAsByteArrayAsync().Result));
                        if (kv.Key.StartsWith("admin/"))
                        {
                            Assert.Contains("frame-ancestors 'none'", string.Join(";", r.Headers.GetValues("Content-Security-Policy")));
                            Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").Single());
                        }
                    }
                }

                var admin = env.Admin();
                foreach (var p in Pages) admin.Call("GET", "/api/admin/" + p);
                admin.Call("GET", "/api/admin/users/anna/sets/" + set.Id);
                admin.Call("GET", "/api/admin/users/anna/sets/" + set.Id + "/runs");
                admin.Call("GET", "/api/admin/users/anna/computers");
                var me = admin.Call("GET", "/api/admin/me");
                Assert.Equal("admin", me["admin"]); Assert.Equal("0", me["enroll"]);
                Assert.Equal(pilot ? "PILOT" : null, me["scope"]);
                Assert.Contains(admin.Call("GET", "/api/admin/users").List("users"), u => u["login"] == "anna" && u.List("sets").Any(s => s["id"] == set.Id));
                if (pilot) Assert.Contains("not supported in this version", Refused(() => admin.Call("GET", "/api/admin/insights"), 403, "SCOPE").Message);

                var reload = new Client(env.Url) { Session = admin.Session };                    // the page reloaded
                Assert.Equal("admin", reload.Call("GET", "/api/admin/me")["admin"]);
                admin.Call("POST", "/api/admin/logout", new Msg());
                Refused(() => reload.Call("GET", "/api/admin/me"), 401, "SESSION");
                Refused(() => admin.Call("GET", "/api/admin/dashboard"), 401, "SESSION");
                var again = env.Admin();
                Assert.NotEqual(admin.Session, again.Session);
                Assert.Equal("admin", again.Call("GET", "/api/admin/me")["admin"]);
            }
        }

        /// <summary>
        /// UI-01 failure: without a sign-in, with a session never issued, with a customer's session, with a session that
        /// signed out — every page's data is refused 401 and nothing of the customer appears in the answer; a wrong
        /// password is 401; files that are not the site's are not served.
        /// </summary>
        [Fact]
        public void UI01_WithoutAnAdministratorSession_EveryPageIsRefused_NothingOfTheCustomersLeaks()
        {
            using (var env = new Env())
            {
                env.CreateUser("anna", Pw);
                var app = env.Agent("anna", Pw);
                FileSet(app, env.Dir("src"), "Anna's ledger");
                var customer = app.Interactive(Pw, null);
                var ended = env.Admin(); ended.Call("POST", "/api/admin/logout", new Msg());
                foreach (var c in new[] { new Client(env.Url) { Session = Bytes.Hex(Bytes.Random(24)) }, new Client(env.Url) { Session = customer.Session }, ended })
                    foreach (var p in Pages)
                    {
                        var e = Refused(() => c.Call("GET", "/api/admin/" + p), 401, "SESSION");
                        Assert.DoesNotContain("anna", e.Message); Assert.DoesNotContain("ledger", e.Message);
                    }
                Refused(() => new Client(env.Url).Call("GET", "/api/admin/users"), 401, "SESSION");
                Assert.Equal(401, Assert.Throws<AgentException>(() => new Client(env.Url).Call("POST", "/api/admin/login", new Msg().Set("login", "admin").Set("password", "Admin-Pass-2"))).Status);
                using (var http = Http())
                    foreach (var p in new[] { "admin/system.xml", "admin/..%2Fconf%2Fsystem.xml", "admin/setup.js", "admin/portal.html", "i18n/de.json", "i18n/fonts/..%2F..%2Fsystem.xml" })
                        Assert.NotEqual(200, (int)http.GetAsync(env.Url + p).Result.StatusCode);
            }
        }

        // ================================================================== UI-02

        static Msg SaveSet(Client admin, string id, XElement e, string version) { return admin.Call("POST", "/api/admin/users/anna/sets/" + id, new Msg().Set("set", e.ToString(SaveOptions.DisableFormatting)).Set("version", version)); }
        static XElement Stored(Env env, string id) { return XDocument.Load(Path.Combine(env.HomeA, "anna", "db", "Profile.xml")).Root.Elements("BACKUP_SET").Single(x => (string)x.Attribute("ID") == id); }

        /// <summary>
        /// UI-02 happy (pilot on): a change on every tab of the set editor, saved over HTTP, is read back exactly by the
        /// editor's next open, is what Profile.xml holds, and reaches the computer — the profile the agent reads has the
        /// same settings.
        /// </summary>
        [Fact]
        public void UI02_TheSetEditorOverHttp_EveryTabSavedAndReadBackExactly_AndTheComputerGetsIt()
        {
            using (var env = new Env())
            {
                env.CreateUser("anna", Pw);
                var app = env.Agent("anna", Pw);
                var src = env.Dir("data"); var src2 = env.Dir("mail"); var skip = Path.Combine(src, "Temp");
                var set = FileSet(app, src);
                PilotScopeServerTests.PilotOn(env);
                var admin = env.Admin();
                var d = admin.Call("GET", "/api/admin/users/anna/sets/" + set.Id);
                Assert.Null(d["scope"]);
                var saved = SaveSet(admin, set.Id, PilotAdminUiComponentTests.EveryTabChanged(XElement.Parse(d["set"]), src, src2, skip), d["version"]);
                Assert.Equal(PilotAdminUiComponentTests.HebrewName, saved["name"]);
                Assert.NotEqual(d["version"], saved["version"]);
                PilotAdminUiComponentTests.AssertEveryTabStored(XElement.Parse(admin.Call("GET", "/api/admin/users/anna/sets/" + set.Id)["set"]), src, src2, skip);
                PilotAdminUiComponentTests.AssertEveryTabStored(Stored(env, set.Id), src, src2, skip);
                PilotAdminUiComponentTests.AssertEveryTabStored(app.Profile().FindSet(set.Id), src, src2, skip);   // what the computer reads
                var onPc = app.Sets().Single(s => s.Id == set.Id);
                Assert.Equal(new[] { src, src2 }, onPc.Sources); Assert.Equal(new[] { skip }, onPc.Deselected);
                Assert.Equal("FAST", onPc.Compression); Assert.Equal(512, onPc.BandwidthKbps); Assert.Equal("D", onPc.DeltaType);
                Assert.Null(Scope.Refusal(onPc));
            }
        }

        /// <summary>
        /// UI-02 failure + concurrency (pilot on): the saves the editor must refuse are refused over HTTP with their reason
        /// — a time with no day ticked, no time at all, an empty name, a command added (not offered in the pilot) — and
        /// Profile.xml is byte-identical afterwards; the editor's next open shows the stored values. Two technicians: the
        /// second save made on the old version is refused 409 and the first one's change stays; after a reload it saves.
        /// </summary>
        [Fact]
        public void UI02_RefusedSavesOverHttp_LeaveTheSetAsItWas_TheSecondTechniciansStaleSaveIsRefused()
        {
            using (var env = new Env())
            {
                env.CreateUser("anna", Pw);
                var app = env.Agent("anna", Pw);
                var set = FileSet(app, env.Dir("data"));
                PilotScopeServerTests.PilotOn(env);
                var admin = env.Admin();
                var d = admin.Call("GET", "/api/admin/users/anna/sets/" + set.Id);
                var profile = Path.Combine(env.HomeA, "anna", "db", "Profile.xml");
                var sha = Sha(profile);
                Func<Action<XElement>, XElement> edit = f => { var e = XElement.Parse(d["set"]); f(e); return e; };

                Refused(() => SaveSet(admin, set.Id, edit(e =>
                {
                    e.Elements("DAILY_SCHEDULE").Remove(); e.Elements("WEEKLY_SCHEDULE").Remove();
                    var w = new XElement("WEEKLY_SCHEDULE", new XAttribute("HOUR", 9), new XAttribute("MINUTE", 0), new XAttribute("DURATION", -1));
                    foreach (var k in new[] { "SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT" }) w.SetAttributeValue(k, "N");
                    e.Add(w);
                }), d["version"]), 400, "SCHEDULE");
                Refused(() => SaveSet(admin, set.Id, edit(e => { e.Elements("DAILY_SCHEDULE").Remove(); e.Elements("WEEKLY_SCHEDULE").Remove(); }), d["version"]), 400, "SCHEDULE");
                Refused(() => SaveSet(admin, set.Id, edit(e => e.SetAttributeValue("NAME", "")), d["version"]), 400, "NAME");
                Refused(() => SaveSet(admin, set.Id, edit(e => e.Add(new XElement("POST_CMD", new XAttribute("PATH", "shutdown /s")))), d["version"]), 403, "SCOPE");
                Assert.Equal(sha, Sha(profile));
                var again = admin.Call("GET", "/api/admin/users/anna/sets/" + set.Id);
                Assert.Equal(d["set"], again["set"]); Assert.Equal(d["version"], again["version"]);

                // two technicians on one set
                var dana = admin.Call("GET", "/api/admin/users/anna/sets/" + set.Id); var yossi = admin.Call("GET", "/api/admin/users/anna/sets/" + set.Id);
                var e1 = XElement.Parse(dana["set"]); e1.SetAttributeValue("NAME", "Dana's name");
                SaveSet(admin, set.Id, e1, dana["version"]);
                var e2 = XElement.Parse(yossi["set"]); e2.SetAttributeValue("LOW_PRIORITY", "N");
                Refused(() => SaveSet(admin, set.Id, e2, yossi["version"]), 409, "CHANGED");
                Assert.Equal("Dana's name", (string)Stored(env, set.Id).Attribute("NAME")); Assert.Equal("Y", (string)Stored(env, set.Id).Attribute("LOW_PRIORITY"));
                var reload = admin.Call("GET", "/api/admin/users/anna/sets/" + set.Id); var e3 = XElement.Parse(reload["set"]); e3.SetAttributeValue("LOW_PRIORITY", "N");
                SaveSet(admin, set.Id, e3, reload["version"]);
                Assert.Equal("Dana's name", (string)Stored(env, set.Id).Attribute("NAME")); Assert.Equal("N", (string)Stored(env, set.Id).Attribute("LOW_PRIORITY"));
                Assert.Equal("Dana's name", app.Sets().Single(s => s.Id == set.Id).Name);
            }
        }

        // ================================================================== UI-03

        /// <summary>
        /// UI-03 failure + recovery: a run whose folder is gone is red everywhere the admin site looks — the tasks of 24
        /// hours, the dashboard, the set's reports, the customer's set (last result, the last good backup NOT moved); a
        /// run whose computer died in the middle is shown running while it reports, then — no sign of life past the lease —
        /// it is not running any more and is a failure in the history. The next good run is green, the last good backup
        /// moves, and both failures stay in the history.
        /// </summary>
        [Fact]
        public void UI03_AFailedRunIsRed_ADeadRunIsFailedNotRunning_TheNextGoodRunIsGreen_TheFailuresStay()
        {
            using (var env = new Env())
            {
                env.CreateUser("anna", Pw);
                var app = env.Agent("anna", Pw);
                var src = env.Dir("src"); File.WriteAllText(Path.Combine(src, "a.txt"), "alpha");
                var set = FileSet(app, src);
                PilotScopeServerTests.PilotOn(env);
                var admin = env.Admin();
                Func<Msg> mySet = () => admin.Call("GET", "/api/admin/users").List("users").Single(u => u["login"] == "anna").List("sets").Single(s => s["id"] == set.Id);

                Directory.Move(src, src + "-gone");
                var r1 = app.Backup(set.Id);
                Assert.False(r1.Result.StartsWith("BS_STOP_SUCCESS"), r1.Result);
                var tasks = admin.Call("GET", "/api/admin/tasks?hours=24");
                Assert.Equal("1", tasks["total"]); Assert.Equal("1", tasks["bad"]); Assert.Null(tasks["ok"]);
                Assert.Equal("bad", tasks.List("tasks").Single()["status"]);
                var dash = admin.Call("GET", "/api/admin/dashboard");
                Assert.Equal("0", dash["ok"]); Assert.Equal("1", dash["bad"]); Assert.Single(dash.List("attention"));
                Assert.Equal("bad", admin.Call("GET", "/api/admin/users/anna/sets/" + set.Id + "/runs").List("runs").Single()["status"]);
                Assert.Equal(r1.Result, mySet()["lastResult"]);
                Assert.True(string.IsNullOrEmpty(mySet()["lastBackup"]) || mySet()["lastBackup"] == "0", "a failed run moved the last good backup: " + mySet()["lastBackup"]);

                // a run whose computer died: running while it reports, then failed, not running
                Directory.Move(src + "-gone", src);
                var dc = app.DeviceClient();
                var job = dc.Call("POST", "/api/sets/" + set.Id + "/begin?key=" + Guid.NewGuid().ToString("N"))["job"];
                dc.Call("POST", "/api/sets/" + set.Id + "/progress", new Msg().Set("job", job).Set("files", 3).Set("bytes", 1000).Set("percent", 10).Set("current", "a.txt"));
                Assert.Equal(job, admin.Call("GET", "/api/admin/live").List("live").Single()["job"]);
                env.Api.SweepInterrupted(DateTime.UtcNow + SetStore.Lease + TimeSpan.FromMinutes(1));
                Assert.Empty(admin.Call("GET", "/api/admin/live").List("live"));
                tasks = admin.Call("GET", "/api/admin/tasks?hours=24");
                Assert.Equal("2", tasks["bad"]);
                var dead = tasks.List("tasks").Single(t => t["job"] == job);
                Assert.Equal("bad", dead["status"]); Assert.Equal("BS_STOP_BY_SYSTEM_ERROR", dead["result"]);
                Assert.Equal("BS_STOP_BY_SYSTEM_ERROR", mySet()["lastResult"]);

                // recovery: the next good run
                var r3 = app.Backup(set.Id);
                Assert.Equal("BS_STOP_SUCCESS", r3.Result);
                tasks = admin.Call("GET", "/api/admin/tasks?hours=24");
                Assert.Equal("3", tasks["total"]); Assert.Equal("1", tasks["ok"]); Assert.Equal("2", tasks["bad"]);
                Assert.Equal("ok", tasks.List("tasks").First()["status"]);                   // newest first
                Assert.Equal("BS_STOP_SUCCESS", mySet()["lastResult"]);
                Assert.False(string.IsNullOrEmpty(mySet()["lastBackup"]) || mySet()["lastBackup"] == "0");
                dash = admin.Call("GET", "/api/admin/dashboard");
                Assert.Equal("1", dash["ok"]); Assert.Equal("2", dash["bad"]);
                Assert.Equal(new[] { "ok", "bad", "bad" }, admin.Call("GET", "/api/admin/users/anna/sets/" + set.Id + "/runs").List("runs").OrderByDescending(x => x.Long("time")).Select(x => x["status"]));
            }
        }

        // ================================================================== UI-04

        static ClientUi Window(AgentApp app)
        {
            for (int port = 19900 + new Random().Next(700); ; port++) try { return new ClientUi(app, port); } catch (HttpListenerException) { }
        }

        static Msg Call(ClientUi ui, string op, Msg body = null, string query = null)
        {
            var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + ui.Port + "/api/" + op + (query == null ? "" : "?" + query));
            req.Method = body == null ? "GET" : "POST"; req.Headers["X-Key"] = ui.Key; req.Proxy = null; req.Timeout = 120000;
            if (body != null) { var b = body.ToBytes(); req.ContentType = "application/xml"; using (var s = req.GetRequestStream()) s.Write(b, 0, b.Length); }
            try { using (var r = (HttpWebResponse)req.GetResponse()) return Msg.Read(r.GetResponseStream()); }
            catch (WebException e) when (e.Response != null) { using (var r = (HttpWebResponse)e.Response) { var m = Msg.Read(r.GetResponseStream()); throw new AgentException((int)r.StatusCode, m["code"], m["message"]); } }
        }

        static Msg Job(ClientUi ui, string id)
        {
            for (int i = 0; i < 1200; i++) { var j = Call(ui, "jobs").List("jobs").SingleOrDefault(x => x["id"] == id); if (j != null && j["state"] != "running") return j; Thread.Sleep(100); }
            throw new TimeoutException("the job did not end in 120 s");
        }

        /// <summary>
        /// UI-04 happy (pilot on): the client window against the real server — the home page shows this computer's set and
        /// an SQL Server set made before the switch with the reason it does not run; a new set of folders is made with this
        /// product's own engine; changing only its time keeps its folders; "Back up now" ends ok; the restore pages list
        /// the point and its files and restore them byte-identical; adding an SQL Server set is refused and adds nothing.
        /// </summary>
        [Fact]
        public void UI04_TheClientWindowOnAPilotServer_EveryPage_NewSetEditBackupRestore_TheBlockedSetShowsWhy()
        {
            using (var env = new Env())
            {
                env.CreateUser("anna", Pw);
                var app = env.Agent("anna", Pw);
                var sql = BackupSetInfo.FromXml(XElement.Parse(PilotScopeServerTests.CreateSet(app.Interactive(Pw, null), PilotScopeServerTests.SetOf("MSSQL", env.Dir("x")))["set"]));
                PilotScopeServerTests.PilotOn(env);
                var src = env.Dir("docs"); var skip = Path.Combine(src, "Temp"); Directory.CreateDirectory(skip);
                File.WriteAllText(Path.Combine(src, "ledger.csv"), "row,1\nrow,2\n"); File.WriteAllText(Path.Combine(skip, "x.tmp"), "skip me");
                var ui = Window(app);
                try
                {
                    var st = Call(ui, "state");
                    Assert.Equal("1", st["pilot"]); Assert.Equal("0", st["restic"]);
                    Assert.Contains("SQL Server", st.List("sets").Single(s => s["id"] == sql.Id)["blocked"]);
                    Call(ui, "login", new Msg().Set("password", Pw));
                    var before = app.Profile().Sets.Count;
                    Assert.Contains("not supported in this version", Refused(() => Call(ui, "addset", new Msg().Set("type", "MSSQL").Set("sources", "ERP")), 400, "SCOPE").Message);
                    Assert.Equal(before, app.Profile().Sets.Count);

                    var id = Call(ui, "addset", new Msg().Set("type", "FILE").Set("name", "Docs").Set("sources", src).Set("exclude", skip).Set("hour", "21").Set("minute", "30"))["id"];
                    var made = app.Sets().Single(s => s.Id == id);
                    Assert.Equal("", made.Engine); Assert.Equal(new[] { src }, made.Sources); Assert.Equal(new[] { skip }, made.Deselected); Assert.Equal(21, made.Hour); Assert.Equal(30, made.Minute);
                    Call(ui, "editset", new Msg().Set("set", id).Set("hour", "6").Set("minute", "45"));
                    made = app.Sets().Single(s => s.Id == id);
                    Assert.Equal(6, made.Hour); Assert.Equal(45, made.Minute); Assert.Equal(new[] { src }, made.Sources); Assert.Equal(new[] { skip }, made.Deselected);

                    var b = Job(ui, Call(ui, "backup", new Msg().Set("set", id))["job"]);
                    Assert.Equal("ok", b["state"]); Assert.Equal("BS_STOP_SUCCESS", b["result"]);
                    var points = Call(ui, "points", null, "set=" + id).List("points");
                    Assert.Single(points);
                    var files = Call(ui, "files", null, "set=" + id + "&point=" + Uri.EscapeDataString(points[0]["id"])).List("files").Select(f => f["path"]).ToList();
                    Assert.Single(files); Assert.EndsWith("ledger.csv", files[0]);
                    var target = env.Dir("restored");
                    var rj = Job(ui, Call(ui, "restore", new Msg().Set("set", id).Set("point", points[0]["id"]).Set("target", target))["job"]);
                    Assert.Equal("ok", rj["state"]);
                    var back = Directory.GetFiles(target, "*", SearchOption.AllDirectories);
                    Assert.Single(back);
                    Assert.Equal(Sha(Path.Combine(src, "ledger.csv")), Sha(back[0]));
                }
                finally { ui.Dispose(); }
            }
        }

        /// <summary>
        /// UI-04 failure + recovery: the server stops — the window still opens, keeps the computer's registration, says it
        /// is offline, and a "Back up now" ends FAILED (never ok); the server comes back — the window shows the sets again
        /// and the next backup ends ok.
        /// </summary>
        [Fact]
        public void UI04_TheServerStops_TheWindowSaysOffline_ABackupIsShownFailed_TheServerBack_ItWorksAgain()
        {
            using (var env = new Env())
            {
                env.CreateUser("anna", Pw);
                var app = env.Agent("anna", Pw);
                var src = env.Dir("docs"); File.WriteAllText(Path.Combine(src, "a.txt"), "alpha");
                var set = FileSet(app, src);
                PilotScopeServerTests.PilotOn(env);
                var ui = Window(app);
                try
                {
                    Assert.Single(Call(ui, "state").List("sets"));
                    env.Api.Dispose();
                    var off = Call(ui, "state");
                    Assert.Equal("1", off["registered"]); Assert.Equal("anna", off["login"]);
                    Assert.False(string.IsNullOrEmpty(off["offline"])); Assert.Empty(off.List("sets"));
                    var failed = Job(ui, Call(ui, "backup", new Msg().Set("set", set.Id))["job"]);
                    Assert.Equal("failed", failed["state"]);

                    env.Api = new Api(env.Cfg); env.Api.Start(env.Url);
                    var on = Call(ui, "state");
                    Assert.Null(on["offline"]); Assert.Equal(set.Id, on.List("sets").Single()["id"]);
                    var ok = Job(ui, Call(ui, "backup", new Msg().Set("set", set.Id))["job"]);
                    Assert.Equal("ok", ok["state"]); Assert.Equal("BS_STOP_SUCCESS", ok["result"]);
                }
                finally { ui.Dispose(); }
            }
        }

        // ================================================================== UI-05

        static Dictionary<string, object> Step(SetupUi ui, string path, Dictionary<string, object> body)
        {
            var r = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + ui.Port + "/api/" + path);
            r.Method = body == null ? "GET" : "POST"; r.Headers["X-Key"] = ui.Key; r.ContentType = "application/json"; r.Timeout = 60000; r.Proxy = null;
            if (body != null) { var b = Encoding.UTF8.GetBytes(Json.Write(body)); r.ContentLength = b.Length; using (var s = r.GetRequestStream()) s.Write(b, 0, b.Length); }
            HttpWebResponse resp;
            try { resp = (HttpWebResponse)r.GetResponse(); } catch (WebException e) when (e.Response != null) { resp = (HttpWebResponse)e.Response; }
            using (resp) using (var rd = new StreamReader(resp.GetResponseStream()))
            {
                var text = rd.ReadToEnd();
                Assert.True((int)resp.StatusCode == 200, path + ": " + (int)resp.StatusCode + " " + text);
                return Json.Obj(Json.Parse(text));
            }
        }

        static Dictionary<string, object> Installed(SetupUi ui)
        {
            for (int i = 0; i < 1200; i++) { var p = Step(ui, "progress", null); if (true.Equals(p["done"])) return p; Thread.Sleep(100); }
            throw new TimeoutException();
        }

        /// <summary>
        /// UI-05 happy + failure (pilot on): the installation wizard against the real pilot server — the server step shows
        /// the agreement and that the server takes no new customers; a new customer is refused by the server with the
        /// reason, and no account is opened; an existing customer installs: the program files byte-identical, this computer
        /// registered (the server lists it), the agreement version recorded for the customer.
        /// </summary>
        [Fact]
        public void UI05_TheWizardOnAPilotServer_ANewCustomerIsRefusedWithTheReason_AnExistingOneInstallsAndIsRegistered()
        {
            using (var env = new Env())
            {
                env.CreateUser("anna", Pw);
                var admin = env.Admin();
                admin.Call("POST", "/api/admin/contract", new Msg().Set("install", 1).Add("texts", new Msg().Set("lang", "he").Set("text", "הסכם שירות גיבוי")).Add("texts", new Msg().Set("lang", "en").Set("text", "Backup service agreement")));
                PilotScopeServerTests.PilotOn(env);
                var pkg = env.Dir("pkg");
                File.WriteAllText(Path.Combine(pkg, "OnlineBackup.Agent.exe"), "agent"); File.WriteAllText(Path.Combine(pkg, "version.txt"), "1.0.0"); File.WriteAllText(Path.Combine(pkg, "Setup.cmd"), "@echo off");
                new XElement("CONNECTION", new XAttribute("SERVER", env.Url.TrimEnd('/')), new XAttribute("FOLDER", "PilotUi-" + Guid.NewGuid().ToString("N").Substring(0, 6))).Save(Path.Combine(pkg, "connection.xml"));
                new XElement("BRANDING", new XAttribute("PRODUCT", "Pilot Backup"), new XAttribute("LANGUAGE", "he")).Save(Path.Combine(pkg, "branding.xml"));
                using (var ui = new SetupUi(pkg))
                {
                    ui.Fixed["install-dir"] = env.Dir("inst"); ui.Fixed["data-dir"] = env.Dir("data"); ui.Flags.Add("no-service"); ui.Flags.Add("no-shortcut");
                    var c = Step(ui, "check", new Dictionary<string, object> { { "server", env.Url }, { "lang", "he" } });
                    Assert.Equal("הסכם שירות גיבוי", c["contract"]); Assert.Equal(false, c["signupOpen"]);

                    Step(ui, "install", new Dictionary<string, object> { { "server", c["server"] }, { "mode", "new" }, { "company", "Dental Clinic" }, { "email", "office@clinic.example" }, { "login", "clinic" }, { "password", "Clinic-Pass-1" }, { "accept", "1" } });
                    Assert.Contains("not supported in this version", Json.Str(Installed(ui), "error"));
                    Assert.DoesNotContain("clinic", env.Api.UserStore.Logins());
                    Assert.Null(new AgentApp(ui.Fixed["data-dir"]).Home.DeviceToken);

                    Step(ui, "install", new Dictionary<string, object> { { "server", c["server"] }, { "mode", "existing" }, { "login", "anna" }, { "password", Pw }, { "accept", "1" }, { "lang", "he" } });
                    Assert.Equal("", Json.Str(Installed(ui), "error"));
                    foreach (var n in new[] { "OnlineBackup.Agent.exe", "version.txt", "connection.xml", "branding.xml" }) Assert.Equal(Sha(Path.Combine(pkg, n)), Sha(Path.Combine(ui.Fixed["install-dir"], n)));
                    Assert.False(File.Exists(Path.Combine(ui.Fixed["install-dir"], "Setup.cmd")));
                    var installed = new AgentApp(ui.Fixed["data-dir"]);
                    Assert.False(string.IsNullOrEmpty(installed.Home.DeviceToken));
                    Assert.Equal("anna", installed.Home.Login);
                    installed.Profile();                                                                // the registration works against the server
                    Assert.Contains(">" + Environment.MachineName + "<", admin.Call("GET", "/api/admin/users/anna/computers").ToString());
                    Assert.Equal(env.Api.UserStore.LoadProfile("anna").Get("CONTRACT_VERSION"), c["contractVersion"].ToString());
                }
            }
        }

        // ================================================================== UI-07

        /// <summary>
        /// UI-07 happy + boundary: the server and the client window serve exactly the shipped Hebrew and English
        /// dictionaries (no other language in the pilot); a message the pilot server sends to the set editor — the second
        /// technician's 409 — and a pilot refusal arrive in a form the Hebrew dictionary translates (Hebrew letters, the
        /// reason's values kept).
        /// </summary>
        [Fact]
        public void UI07_TheServerAndTheWindowServeTheHebrewDictionary_TheEditorsMessagesTranslateToHebrew()
        {
            using (var env = new Env())
            {
                var repo = PilotAdminUiComponentTests.RepoRoot();
                env.CreateUser("anna", Pw);
                var app = env.Agent("anna", Pw);
                var set = FileSet(app, env.Dir("data"));
                PilotScopeServerTests.PilotOn(env);
                var ui = Window(app);
                try
                {
                    using (var http = Http())
                    {
                        foreach (var lang in new[] { "he", "en" })
                        {
                            var disk = Sha(Path.Combine(repo, "src", "Core", "i18n", lang + ".json"));
                            var r = http.GetAsync(env.Url + "i18n/" + lang + ".json").Result;
                            Assert.Equal(200, (int)r.StatusCode); Assert.StartsWith("application/json", r.Content.Headers.ContentType.ToString());
                            Assert.Equal(disk, Sha(r.Content.ReadAsByteArrayAsync().Result));
                            Assert.Equal(disk, Sha(http.GetAsync("http://127.0.0.1:" + ui.Port + "/i18n/" + lang + ".json").Result.Content.ReadAsByteArrayAsync().Result));
                        }
                        foreach (var other in new[] { "ar", "de", "fr", "zh" })
                        {
                            Assert.Equal(404, (int)http.GetAsync(env.Url + "i18n/" + other + ".json").Result.StatusCode);
                            Assert.Equal(404, (int)http.GetAsync("http://127.0.0.1:" + ui.Port + "/i18n/" + other + ".json").Result.StatusCode);
                        }
                    }
                    var admin = env.Admin();
                    var d = admin.Call("GET", "/api/admin/users/anna/sets/" + set.Id);
                    SaveSet(admin, set.Id, XElement.Parse(d["set"]), d["version"]);
                    var e = XElement.Parse(d["set"]); e.SetAttributeValue("NAME", "x");
                    SaveSet(admin, set.Id, e, d["version"]);
                    var stale = Refused(() => SaveSet(admin, set.Id, XElement.Parse(d["set"]).Also(x => x.SetAttributeValue("NAME", "y")), d["version"]), 409, "CHANGED");
                    var he = L.Tr("he", stale.Message);
                    Assert.NotEqual(stale.Message, he); Assert.Matches("[א-ת]", he);
                    var scope = Refused(() => SaveSet(admin, set.Id, XElement.Parse(admin.Call("GET", "/api/admin/users/anna/sets/" + set.Id)["set"]).Also(x => x.Add(new XElement("PRE_CMD", new XAttribute("PATH", "x")))), admin.Call("GET", "/api/admin/users/anna/sets/" + set.Id)["version"]), 403, "SCOPE");
                    Assert.Contains("אינו נתמך בגרסה זו", L.Tr("he", scope.Message));
                }
                finally { ui.Dispose(); }
            }
        }
    }

    static class PilotUiXml
    {
        public static XElement Also(this XElement e, Action<XElement> f) { f(e); return e; }
    }
}
