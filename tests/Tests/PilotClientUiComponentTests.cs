using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// Pilot release blockers, the customer's screens — COMPONENT layer: the client window's local API (ClientUi, what
    /// ClientForm.cs and client.html call) and the installation wizard (SetupUi, what setup-client.html calls), each against
    /// the recording stand-in server (StubServer), never the product's server. Contracts from tests/QA/specs.py:
    ///   UI-04 the client window works: each page opens; what the person typed is kept (a field not on the page is never
    ///         dropped by a save); a failed backup is shown failed; the pilot offers only files and folders
    ///   UI-05 the installation wizard: Welcome → server → agreement → install → finish; each step reachable; a refused
    ///         step installs nothing; it can be run again
    /// Oracle: what reached the stand-in server (the set XML it received, exact), the files installed (SHA-256), the
    /// computer's registration file.
    /// </summary>
    [Collection("ClientDir")]
    public class PilotClientUiComponentTests : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "obpilot-cui-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        public PilotClientUiComponentTests() { Directory.CreateDirectory(root); }
        public void Dispose() { SystemClock.Use(null); try { Directory.Delete(root, true); } catch (Exception) { } }

        string Dir(params string[] parts) { var d = Path.Combine(new[] { root }.Concat(parts).ToArray()); Directory.CreateDirectory(d); return d; }
        static string Sha(string f) { using (var s = File.OpenRead(f)) using (var h = SHA256.Create()) return Bytes.Hex(h.ComputeHash(s)); }

        // ================================================================== UI-04 the client window

        const string Pw = "Anna-Pass-1";
        const string Device = "YW5uYQ==.1700000000000.device-secret";
        const string FileSet = "1700000000101", OtherPcSet = "1700000000102", SqlSet = "1700000000103", NewSet = "1700000000200";

        sealed class Window : IDisposable
        {
            public readonly StubServer Server = new StubServer();
            public AgentApp App; public ClientUi Ui; public KeySet Key = KeySet.Random();
            public string Src, Skip; public TimeSpan Skew;
            public bool Down;
            public void Dispose() { if (Ui != null) Ui.Dispose(); Server.Dispose(); }
            public List<StubServer.Req> Posted(string action) { return Server.Of(action).Where(r => r.Method == "POST").ToList(); }
        }

        /// <summary>A registered computer PC-ANNA: its set "Docs" (one folder, one skipped inside, 21:30), a set of another
        /// computer of the same customer, and — with the pilot — an SQL Server set made before the switch.</summary>
        Window Open(bool pilot)
        {
            var w = new Window();
            w.Src = Dir("pc", "Docs"); w.Skip = Path.Combine(w.Src, "Temp"); Directory.CreateDirectory(w.Skip);
            File.WriteAllText(Path.Combine(w.Src, "ledger.csv"), "row,1\nrow,2\n");
            var p = Profile.Create("anna", "Anna", "x", "en", "UTC");
            p.Root.Add(new BackupSetInfo { Id = FileSet, Name = "Docs", Computer = "PC-ANNA", Sources = { w.Src }, Deselected = { w.Skip }, Hour = 21, Minute = 30, KeyCheck = w.Key.CheckValue(), KeySalt = "c2FsdA==", Vss = false }.ToXml());
            p.Root.Add(new BackupSetInfo { Id = OtherPcSet, Name = "Other PC", Computer = "PC-OTHER", Sources = { @"D:\Shared" } }.ToXml());
            if (pilot)
            {
                p.Root.Add(new BackupSetInfo { Id = SqlSet, Name = "ERP database", Type = "MSSQL", Computer = "PC-ANNA", Sources = { @"Microsoft SQL Server\SQLEXPRESS" }, KeyCheck = w.Key.CheckValue() }.ToXml());
                p.Root.SetAttributeValue("SERVER_SCOPE", "PILOT");
            }
            w.Server.ProfileXml = p.Doc.ToString();
            w.Server.Handler = r =>
            {
                if (w.Down && r.Action != "login") return StubServer.Answer.Error(503, "DOWN", "The server is being updated.");
                if (r.Action == "login") return r.Msg["password"] == Pw && r.Msg["login"] == "anna" ? StubServer.Answer.Ok(new Msg().Set("session", "session-1")) : StubServer.Answer.Error(401, "LOGIN", "Wrong user name or password.");
                if (r.Path == "/api/sets" && r.Method == "POST") { var s = BackupSetInfo.FromXml(XElement.Parse(r.Msg["set"])); s.Id = NewSet; return StubServer.Answer.Ok(new Msg().Set("set", s.ToXml().ToString(SaveOptions.DisableFormatting))); }
                return null;
            };
            w.App = new AgentApp(Dir("pc", "agent"));
            w.App.Home.SaveRegistration(w.Server.Url, "anna", "PC-ANNA", Device, "SYSTEM", null);
            w.App.Home.SaveKey(FileSet, w.Key);
            SystemClock.Use(() => DateTime.UtcNow + w.Skew);   // before the window starts: its threads take this clock with them
            for (int port = 19100 + new Random().Next(700); ; port++) try { w.Ui = new ClientUi(w.App, port); break; } catch (HttpListenerException) { }
            return w;
        }

        sealed class Answer { public int Status; public Msg Body; public string Text; public string Type; }

        static Answer Raw(ClientUi ui, string method, string path, Msg body = null, string key = "", string host = null)
        {
            var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + ui.Port + path);
            req.Method = method; req.Proxy = null; req.Timeout = 60000;
            if (key == "") key = ui.Key;
            if (key != null) req.Headers["X-Key"] = key;
            if (host != null) req.Host = host;
            if (body != null) { var b = body.ToBytes(); req.ContentType = "application/xml"; using (var s = req.GetRequestStream()) s.Write(b, 0, b.Length); }
            else if (method == "POST") req.ContentLength = 0;
            HttpWebResponse resp;
            try { resp = (HttpWebResponse)req.GetResponse(); }
            catch (WebException e) when (e.Response != null) { resp = (HttpWebResponse)e.Response; }
            using (resp) using (var rd = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                var a = new Answer { Status = (int)resp.StatusCode, Text = rd.ReadToEnd(), Type = resp.ContentType };
                try { a.Body = Msg.Parse(a.Text); } catch (Exception) { a.Body = new Msg(); }
                return a;
            }
        }

        static Msg Call(ClientUi ui, string op, Msg body = null, string query = null)
        {
            var a = Raw(ui, body == null ? "GET" : "POST", "/api/" + op + (query == null ? "" : "?" + query), body);
            if (a.Status != 200) throw new AgentException(a.Status, a.Body["code"], a.Body["message"]);
            return a.Body;
        }

        static AgentException Refused(Func<Msg> a, int status, string code)
        {
            var e = Assert.Throws<AgentException>(() => a());
            Assert.Equal(status, e.Status); Assert.Equal(code, e.Code);
            Assert.False(string.IsNullOrWhiteSpace(e.Message));
            return e;
        }

        static Msg WaitJob(ClientUi ui, string id)
        {
            for (int i = 0; i < 1200; i++)
            {
                var j = Call(ui, "jobs").List("jobs").SingleOrDefault(x => x["id"] == id);
                if (j != null && j["state"] != "running") return j;
                Thread.Sleep(100);
            }
            throw new TimeoutException("the job did not end in 120 s");
        }

        static XElement SentSet(StubServer.Req r) { return XElement.Parse(r.Msg["set"]); }

        /// <summary>
        /// UI-04 happy: every page of the window opens — the page itself, its texts in English and Hebrew, the design
        /// system; the home page lists this computer's set exactly as the server holds it (folders, skipped folder, time)
        /// and the other computer's set for restore only; the folder tree shows exactly the sub-folders; security, help and
        /// jobs answer; signing in opens the session the restore and new-set pages need.
        /// </summary>
        [Fact]
        public void UI04_EveryPageOfTheWindowOpens_TheSetsAreShownExactly_SignInOpensTheSession()
        {
            using (var w = Open(false))
            {
                var page = Raw(w.Ui, "GET", "/", key: null);
                Assert.Equal(200, page.Status); Assert.StartsWith("text/html", page.Type);
                Assert.Contains("<html", page.Text); Assert.Contains("/i18n/i18n.js", page.Text);
                Assert.Equal(L.Raw("he"), Raw(w.Ui, "GET", "/i18n/he.json", key: null).Text);
                Assert.Equal(L.Raw("en"), Raw(w.Ui, "GET", "/i18n/en.json", key: null).Text);
                Assert.Equal(200, Raw(w.Ui, "GET", "/i18n/i18n.js", key: null).Status);
                Assert.Equal(200, Raw(w.Ui, "GET", "/i18n/theme.css", key: null).Status);

                var st = Call(w.Ui, "state");
                Assert.Equal("1", st["registered"]); Assert.Equal("anna", st["login"]); Assert.Equal("PC-ANNA", st["computer"]); Assert.Equal("0", st["session"]);
                Assert.Null(st["pilot"]); Assert.Null(st["offline"]);
                var sets = st.List("sets");
                Assert.Equal(2, sets.Count);
                var mine = sets.Single(s => s["id"] == FileSet);
                Assert.Equal("1", mine["mine"]); Assert.Equal("Docs", mine["name"]); Assert.Equal(w.Src, mine["src"]); Assert.Equal(w.Skip, mine["skip"]);
                Assert.Equal("21:30", mine["hour"]); Assert.Equal("21", mine["hh"]); Assert.Equal("30", mine["mm"]);
                Assert.True(string.IsNullOrEmpty(mine["blocked"]));
                Assert.Equal("0", sets.Single(s => s["id"] == OtherPcSet)["mine"]);

                Directory.CreateDirectory(Path.Combine(w.Src, "Archive"));
                Assert.Equal(new[] { Path.Combine(w.Src, "Archive"), w.Skip }, Call(w.Ui, "dirs", null, "path=" + Uri.EscapeDataString(w.Src)).List("dirs").Select(d => d["path"]));
                Assert.Equal("1", Call(w.Ui, "dirs", null, "path=" + Uri.EscapeDataString(Path.Combine(w.Src, "missing")))["denied"]);
                Assert.NotEmpty(Call(w.Ui, "dirs").List("dirs"));                       // the drives
                Assert.Equal("anna", Call(w.Ui, "security")["login"]);
                Assert.Empty(Call(w.Ui, "jobs").List("jobs"));
                Assert.Equal("1", Call(w.Ui, "help")["ok"]);                              // the calls page (the server's answer)

                Assert.Equal("1", Call(w.Ui, "login", new Msg().Set("password", Pw))["ok"]);
                Assert.Equal("1", Call(w.Ui, "state")["session"]);
                Assert.Equal("anna", w.Posted("login").Single().Msg["login"]);
                Call(w.Ui, "logout", new Msg());
                Assert.Equal("0", Call(w.Ui, "state")["session"]);
            }
        }

        /// <summary>
        /// UI-04 happy ("typing kept"): the edit page sends what the person changed and keeps everything else exactly — a
        /// change of the time alone keeps the folders and the skipped folder as they are; a change of the folders (typed
        /// with repeats and both separators) sends each folder once and keeps the time; a Hebrew name is kept exactly.
        /// A save without any folder is refused and nothing reaches the server.
        /// </summary>
        [Fact]
        public void UI04_TypingKept_ATimeChangeKeepsTheFolders_AFolderChangeKeepsTheTime_NoFolderIsRefused()
        {
            using (var w = Open(true))
            {
                Call(w.Ui, "login", new Msg().Set("password", Pw));
                Call(w.Ui, "editset", new Msg().Set("set", FileSet).Set("hour", "6").Set("minute", "5"));
                var s1 = SentSet(w.Posted("settings").Single());
                Assert.Equal(new[] { w.Src }, s1.Elements("SEL-SOURCE").Select(x => x.Value)); Assert.Equal(new[] { w.Skip }, s1.Elements("DE-SOURCE").Select(x => x.Value));
                var t1 = s1.Elements().First(x => x.Name == "DAILY_SCHEDULE" || x.Name == "WEEKLY_SCHEDULE");
                Assert.Equal("6:5", (string)t1.Attribute("HOUR") + ":" + (string)t1.Attribute("MINUTE"));
                Assert.Equal("Docs", (string)s1.Attribute("NAME"));
                Assert.EndsWith("/api/sets/" + FileSet + "/settings", w.Posted("settings").Single().Path);

                var other = Dir("pc", "Mail");
                Call(w.Ui, "editset", new Msg().Set("set", FileSet).Set("sources", w.Src + "\n" + other + "; " + w.Src + "\n").Set("exclude", "").Set("name", "  מסמכים 2026  "));
                var s2 = SentSet(w.Posted("settings").Last());
                Assert.Equal(new[] { w.Src, other }, s2.Elements("SEL-SOURCE").Select(x => x.Value));
                Assert.Empty(s2.Elements("DE-SOURCE"));
                var t2 = s2.Elements().First(x => x.Name == "DAILY_SCHEDULE" || x.Name == "WEEKLY_SCHEDULE");
                Assert.Equal("21:30", (string)t2.Attribute("HOUR") + ":" + (string)t2.Attribute("MINUTE"));   // the time as the server holds it
                Assert.Equal("מסמכים 2026", (string)s2.Attribute("NAME"));
                Assert.Equal(w.Key.CheckValue(), (string)s2.Element("ENCRYPTING_KEY").Attribute("KEY"));      // the key travels unchanged

                Refused(() => Call(w.Ui, "editset", new Msg().Set("set", FileSet).Set("sources", " ; \n ").Set("exclude", "")), 400, "NO_SOURCE");
                Refused(() => Call(w.Ui, "editset", new Msg().Set("set", "1700000000999").Set("hour", "6")), 400, "NO_SET");
                Assert.Equal(2, w.Posted("settings").Count);                                  // nothing more reached the server
            }
        }

        /// <summary>
        /// UI-04 failure (pilot): the window of a pilot computer says so (pilot, no restic) and shows an SQL Server set
        /// made before the switch with the reason it does not run; adding a set of a blocked kind is refused with the
        /// reason and NOTHING reaches the server; "back up now" of the blocked set ends failed with the reason and no run
        /// begins; a set of folders is added as this product's own engine — never restic, no commands, no local copy —
        /// with the folders, the skipped folder and the time exactly as typed.
        /// </summary>
        [Fact]
        public void UI04_ThePilotWindow_OffersOnlyFolders_ABlockedSetShowsWhy_AndNothingOfItReachesTheServer()
        {
            using (var w = Open(true))
            {
                var st = Call(w.Ui, "state");
                Assert.Equal("1", st["pilot"]); Assert.Equal("0", st["restic"]);
                var sql = st.List("sets").Single(s => s["id"] == SqlSet);
                Assert.Contains("SQL Server", sql["blocked"]); Assert.EndsWith("is not supported in this version (Windows file backup).", sql["blocked"]);
                Assert.True(string.IsNullOrEmpty(st.List("sets").Single(s => s["id"] == FileSet)["blocked"]));

                Call(w.Ui, "login", new Msg().Set("password", Pw));
                foreach (var t in new[] { "MSSQL", "SYSTEMSTATE", "BAREMETAL", "M365", "MYSQL" })
                    Assert.Contains("not supported in this version", Refused(() => Call(w.Ui, "addset", new Msg().Set("type", t).Set("name", t).Set("sources", w.Src).Set("tenant", "x").Set("clientId", "y").Set("secret", "z")), 400, "SCOPE").Message);
                Assert.Empty(w.Posted("sets"));

                var job = Call(w.Ui, "backup", new Msg().Set("set", SqlSet))["job"];
                var j = WaitJob(w.Ui, job);
                Assert.Equal("failed", j["state"]); Assert.Contains("not supported in this version", j["detail"]);
                Assert.Empty(w.Server.Of("begin"));

                var made = Call(w.Ui, "addset", new Msg().Set("type", "FILE").Set("name", "Projects").Set("sources", w.Src).Set("exclude", w.Skip).Set("hour", "3").Set("minute", "15"));
                Assert.Equal(NewSet, made["id"]); Assert.Equal("", made["engine"] ?? "");
                var sent = SentSet(w.Posted("sets").Single());
                Assert.Equal("FILE", (string)sent.Attribute("TYPE")); Assert.Equal("", (string)sent.Attribute("ENGINE")); Assert.Equal("SERVER", (string)sent.Attribute("DEST_MODE"));
                Assert.Empty(sent.Elements("PRE_CMD")); Assert.Empty(sent.Elements("POST_CMD"));
                Assert.Equal("N", (string)sent.Element("EXTRA_LOCAL_BACKUP").Attribute("ENABLED"));
                Assert.Equal(new[] { w.Src }, sent.Elements("SEL-SOURCE").Select(x => x.Value)); Assert.Equal(new[] { w.Skip }, sent.Elements("DE-SOURCE").Select(x => x.Value));
                var t0 = sent.Elements().First(x => x.Name == "DAILY_SCHEDULE" || x.Name == "WEEKLY_SCHEDULE");
                Assert.Equal("3:15", (string)t0.Attribute("HOUR") + ":" + (string)t0.Attribute("MINUTE"));
                Assert.Equal("Projects", (string)sent.Attribute("NAME"));
                Assert.Null(Scope.Refusal(BackupSetInfo.FromXml(sent)));
            }
        }

        /// <summary>
        /// UI-04 recovery: a backup whose folder is gone is shown FAILED in the window (never ok); after the folder is back
        /// the next backup is shown ok with what it sent. The password session ends after 15 minutes without use — the next
        /// action asks for the password (401), and after signing in again the same action works. The server away: the
        /// window still opens, says it is offline and keeps the computer's registration; the server back: the sets again.
        /// </summary>
        [Fact]
        public void UI04_AFailedBackupIsShownFailed_ThenOk_AnEndedSessionAsksAgain_TheServerAwayShowsOfflineThenBack()
        {
            using (var w = Open(false))
            {
                var moved = w.Src + "-away";
                Directory.Move(w.Src, moved);
                var j1 = WaitJob(w.Ui, Call(w.Ui, "backup", new Msg().Set("set", FileSet))["job"]);
                Assert.Equal("failed", j1["state"]); Assert.False((j1["result"] ?? "").StartsWith("BS_STOP_SUCCESS"), "a backup of a missing folder shown as " + j1["result"]);
                Directory.Move(moved, w.Src);
                var j2 = WaitJob(w.Ui, Call(w.Ui, "backup", new Msg().Set("set", FileSet))["job"]);
                Assert.Equal("ok", j2["state"]); Assert.StartsWith("BS_STOP_SUCCESS", j2["result"]);
                Assert.StartsWith("New 1,", j2["detail"]);                                    // ledger.csv (Temp is skipped and empty)
                Assert.Equal(2, Call(w.Ui, "jobs").List("jobs").Count);

                Call(w.Ui, "login", new Msg().Set("password", Pw));
                w.Skew = TimeSpan.FromMinutes(16);
                Refused(() => Call(w.Ui, "editset", new Msg().Set("set", FileSet).Set("hour", "8")), 401, "LOGIN");
                Assert.Equal("0", Call(w.Ui, "state")["session"]);
                Assert.Empty(w.Posted("settings"));
                Call(w.Ui, "login", new Msg().Set("password", Pw));
                Call(w.Ui, "editset", new Msg().Set("set", FileSet).Set("hour", "8"));
                Assert.Single(w.Posted("settings"));

                w.Down = true;
                var off = Call(w.Ui, "state");
                Assert.Equal("1", off["registered"]); Assert.Equal("anna", off["login"]);
                Assert.False(string.IsNullOrEmpty(off["offline"])); Assert.Empty(off.List("sets"));
                w.Down = false;
                var back = Call(w.Ui, "state");
                Assert.Null(back["offline"]); Assert.Equal(2, back.List("sets").Count);
            }
        }

        /// <summary>
        /// UI-04 failure + boundary: without the window's key (or with another) no API answers (403 KEY); a request that
        /// names another site as its host is not served; an unknown action is refused, never a 200; a wrong password is
        /// 401 and opens no session; a language other than English and Hebrew, or a path out of /i18n/, is not served.
        /// </summary>
        [Fact]
        public void UI04_WithoutTheKeyNothingAnswers_AnotherSiteIsNotServed_AWrongPasswordOpensNoSession()
        {
            using (var w = Open(false))
            {
                foreach (var key in new string[] { null, "wrong", w.Ui.Key.ToUpperInvariant() == w.Ui.Key ? w.Ui.Key + "0" : w.Ui.Key.ToUpperInvariant() })
                {
                    var a = Raw(w.Ui, "GET", "/api/state", key: key);
                    Assert.Equal(403, a.Status); Assert.Equal("KEY", a.Body["code"]); Assert.Null(a.Body["login"]);
                }
                var evil = Raw(w.Ui, "GET", "/api/state", host: "evil.example");
                Assert.NotEqual(200, evil.Status); Assert.DoesNotContain("anna", evil.Text);
                Assert.NotEqual(200, Raw(w.Ui, "GET", "/", key: null, host: "evil.example").Status);
                var unknown = Raw(w.Ui, "POST", "/api/format-disk", new Msg());
                Assert.Equal(400, unknown.Status); Assert.Equal("OP", unknown.Body["code"]);
                var wrong = Raw(w.Ui, "POST", "/api/login", new Msg().Set("password", "Anna-Pass-2"));
                Assert.Equal(401, wrong.Status); Assert.Equal("LOGIN", wrong.Body["code"]);
                Assert.Equal("0", Call(w.Ui, "state")["session"]);
                Refused(() => Call(w.Ui, "addset", new Msg().Set("type", "FILE").Set("sources", w.Src)), 401, "LOGIN");
                Assert.Empty(w.Posted("sets"));
                Assert.Equal(404, Raw(w.Ui, "GET", "/i18n/de.json", key: null).Status);
                Assert.Equal(404, Raw(w.Ui, "GET", "/i18n/fonts/../../client.html", key: null).Status);
                Assert.Equal(404, Raw(w.Ui, "GET", "/logo", key: null).Status);            // no logo in this package: none, not a broken image
            }
        }

        // ================================================================== UI-05 the installation wizard

        sealed class Wizard : IDisposable
        {
            public readonly StubServer Server = new StubServer();
            public string Pkg, Install, Data; public SetupUi Ui;
            public int Contract = 3; public string Device = "YW5uYQ==.1700000000001.wizard-device";
            public void Dispose() { if (Ui != null) Ui.Dispose(); Server.Dispose(); }
        }

        Wizard NewWizard(string name)
        {
            var z = new Wizard();
            z.Pkg = Dir(name, "pkg");
            File.WriteAllBytes(Path.Combine(z.Pkg, "OnlineBackup.Agent.exe"), Encoding.UTF8.GetBytes("agent program " + name));
            File.WriteAllBytes(Path.Combine(z.Pkg, "OnlineBackup.Core.dll"), Encoding.UTF8.GetBytes("core library " + name));
            File.WriteAllText(Path.Combine(z.Pkg, "version.txt"), "1.2.3");
            File.WriteAllText(Path.Combine(z.Pkg, "Setup.cmd"), "@echo off");
            File.WriteAllText(Path.Combine(z.Pkg, "README.txt"), "readme");
            new XElement("BRANDING", new XAttribute("PRODUCT", "Pilot Safe Backup"), new XAttribute("LANGUAGE", "he"), new XAttribute("COMPANY", "Pilot IT")).Save(Path.Combine(z.Pkg, "branding.xml"));
            new XElement("CONNECTION", new XAttribute("SERVER", z.Server.Url.TrimEnd('/')), new XAttribute("FOLDER", "PilotSafe-" + Guid.NewGuid().ToString("N").Substring(0, 8))).Save(Path.Combine(z.Pkg, "connection.xml"));
            z.Install = Path.Combine(root, name, "Program Files", "PilotSafe"); z.Data = Path.Combine(root, name, "ProgramData", "PilotSafe");
            z.Server.Handler = r =>
            {
                if (r.Action == "contract") return StubServer.Answer.Ok(new Msg().Set("version", z.Contract).Set("install", z.Contract > 0 ? 1 : 0).Set("signup", 1).Set("signupOpen", 0).Set("text", "Pilot agreement v" + z.Contract));
                if (r.Action == "register") return r.Msg["login"] == "anna" && r.Msg["password"] == Pw ? StubServer.Answer.Ok(new Msg().Set("device", z.Device)) : StubServer.Answer.Error(401, "LOGIN", "Wrong user name or password.");
                if (r.Action == "signup") return StubServer.Answer.Error(403, "SCOPE", "Opening a new account from the client software is not supported in this version (Windows file backup).");
                return null;
            };
            z.Ui = new SetupUi(z.Pkg);
            z.Ui.Fixed["install-dir"] = z.Install; z.Ui.Fixed["data-dir"] = z.Data; z.Ui.Flags.Add("no-service"); z.Ui.Flags.Add("no-shortcut");
            return z;
        }

        static Dictionary<string, object> Step(SetupUi ui, string path, Dictionary<string, object> body, int expect = 200, string key = "")
        {
            var r = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + ui.Port + "/api/" + path);
            r.Method = body == null ? "GET" : "POST"; r.Proxy = null; r.Timeout = 60000; r.ContentType = "application/json";
            if (key == "") key = ui.Key;
            if (key != null) r.Headers["X-Key"] = key;
            if (body != null) { var b = Encoding.UTF8.GetBytes(Json.Write(body)); r.ContentLength = b.Length; using (var s = r.GetRequestStream()) s.Write(b, 0, b.Length); }
            HttpWebResponse resp;
            try { resp = (HttpWebResponse)r.GetResponse(); } catch (WebException e) when (e.Response != null) { resp = (HttpWebResponse)e.Response; }
            using (resp) using (var rd = new StreamReader(resp.GetResponseStream()))
            {
                var text = rd.ReadToEnd();
                Assert.True((int)resp.StatusCode == expect, path + ": " + (int)resp.StatusCode + " " + text);
                return text.StartsWith("{") ? Json.Obj(Json.Parse(text)) : new Dictionary<string, object>();
            }
        }

        static Dictionary<string, object> Installed(SetupUi ui)
        {
            for (int i = 0; i < 1200; i++) { var p = Step(ui, "progress", null); if (true.Equals(p["done"])) return p; Thread.Sleep(100); }
            throw new TimeoutException("the installation did not end in 120 s");
        }

        static Dictionary<string, object> Existing(string server, string password, string accept)
        {
            return new Dictionary<string, object> { { "server", server }, { "mode", "existing" }, { "login", "anna" }, { "password", password }, { "accept", accept }, { "lang", "he" } };
        }

        /// <summary>
        /// UI-05 happy: the wizard's steps in order — Welcome (the package's product, company, language and server), the
        /// server check (the address as typed becomes the full one; the agreement to read), Install (an existing customer:
        /// the program files installed byte-identical, the scripts and README not; this computer registered with the
        /// server's device token; the password never in the progress lines), Finish (the wizard closes).
        /// </summary>
        [Fact]
        public void UI05_Welcome_Server_Agreement_Install_Finish_EveryProgramFileByteIdentical_ThisComputerRegistered()
        {
            using (var z = NewWizard("happy"))
            {
                using (var wc = new WebClient { Proxy = null }) Assert.Contains("<html", wc.DownloadString("http://127.0.0.1:" + z.Ui.Port + "/"));   // the page opens without the key; its API does not
                var info = Step(z.Ui, "info", null);
                Assert.Equal("Pilot Safe Backup", info["product"]); Assert.Equal("Pilot IT", info["company"]); Assert.Equal("he", info["language"]);
                Assert.Equal(z.Server.Url.TrimEnd('/'), info["server"]); Assert.Equal(false, info["existing"]);
                var p0 = Step(z.Ui, "progress", null);
                Assert.Equal(false, p0["done"]); Assert.Empty(Json.Arr(p0["lines"]));

                var typed = z.Server.Url.TrimEnd('/') + "/";
                var c = Step(z.Ui, "check", new Dictionary<string, object> { { "server", typed }, { "lang", "he" } });
                Assert.Equal(z.Server.Url.TrimEnd('/'), c["server"]); Assert.Equal(false, c["confirm"]);
                Assert.Equal("Pilot agreement v3", c["contract"]); Assert.Equal(3L, Convert.ToInt64(c["contractVersion"]));
                Assert.Equal(false, c["signupOpen"]);                                       // the pilot server takes no new customers here
                Assert.Equal("he", z.Server.Of("contract").Last().Query["lang"]);

                Step(z.Ui, "install", Existing((string)c["server"], Pw, "1"));
                var p = Installed(z.Ui);
                Assert.Equal("", Json.Str(p, "error"));
                Assert.Equal(z.Install, Json.Str(p, "installDir"));
                Assert.DoesNotContain(Json.Arr(p["lines"]).Select(x => x.ToString()), l => l.Contains(Pw));
                var want = new[] { "OnlineBackup.Agent.exe", "OnlineBackup.Core.dll", "version.txt", "branding.xml", "connection.xml" }.ToDictionary(n => n, n => Sha(Path.Combine(z.Pkg, n)));
                var got = Directory.GetFiles(z.Install).ToDictionary(f => Path.GetFileName(f), Sha);
                Assert.Equal(want.OrderBy(x => x.Key, StringComparer.Ordinal), got.OrderBy(x => x.Key, StringComparer.Ordinal));
                var reg = z.Server.Of("register").Single().Msg;
                Assert.Equal("anna", reg["login"]); Assert.Equal("3", reg["contractVersion"]);
                var home = new AgentApp(z.Data).Home;
                Assert.Equal(z.Device, home.DeviceToken); Assert.Equal("anna", home.Login); Assert.Equal(z.Server.Url.TrimEnd('/'), home.Server);

                Step(z.Ui, "finish", new Dictionary<string, object> { { "open", "0" } });
                Assert.True(z.Ui.Closed.WaitOne(5000));
            }
        }

        /// <summary>
        /// UI-05 failure + recovery: the agreement not accepted — the installation stops with that reason and NOTHING is
        /// registered (no request reaches the server's registration); a wrong password — the server's reason, no
        /// registration; a new customer on the pilot server — the server's refusal with its reason, no account, no
        /// registration. Each time the wizard can go on: the same wizard, run again with the right answers, finishes and
        /// registers the computer once.
        /// </summary>
        [Fact]
        public void UI05_AnUnacceptedAgreement_AWrongPassword_ANewCustomerOnThePilot_RegisterNothing_ThenTheSameWizardFinishes()
        {
            using (var z = NewWizard("retry"))
            {
                var server = z.Server.Url.TrimEnd('/');
                Step(z.Ui, "install", Existing(server, Pw, "0"));
                Assert.Contains("not accepted", Json.Str(Installed(z.Ui), "error"));
                Assert.Empty(z.Server.Of("register"));
                Assert.Null(new AgentApp(z.Data).Home.DeviceToken);

                Step(z.Ui, "install", Existing(server, "Anna-Pass-2", "1"));
                Assert.Contains("Wrong user name or password", Json.Str(Installed(z.Ui), "error"));
                Assert.Null(new AgentApp(z.Data).Home.DeviceToken);

                Step(z.Ui, "install", new Dictionary<string, object> { { "server", server }, { "mode", "new" }, { "company", "Dental Clinic" }, { "email", "office@clinic.example" }, { "login", "clinic" }, { "password", "Clinic-Pass-1" }, { "accept", "1" } });
                Assert.Contains("not supported in this version", Json.Str(Installed(z.Ui), "error"));
                Assert.Single(z.Server.Of("signup"));
                Assert.Null(new AgentApp(z.Data).Home.DeviceToken);

                Step(z.Ui, "install", Existing(server, Pw, "1"));
                var p = Installed(z.Ui);
                Assert.Equal("", Json.Str(p, "error"));
                Assert.Equal(z.Device, new AgentApp(z.Data).Home.DeviceToken);
                Assert.Equal(1, z.Server.Of("register").Count(r => r.Answered == 200));
            }
        }

        /// <summary>
        /// UI-05 boundary: the address as people type it (a bare name, a port, https://…:443, spaces, other schemes, 301
        /// characters); without the wizard's key no step answers; an address that is not one, or one that does not answer,
        /// is refused at the server step with a message (the person stays on that step); a server without an agreement
        /// lets the installation go on without asking.
        /// </summary>
        [Fact]
        public void UI05_TheAddressAsTyped_TheKeyIsRequired_AWrongOrSilentServerStaysOnItsStep()
        {
            Assert.Equal("https://backup.example.com:8443", SetupUi.Normalize("backup.example.com"));
            Assert.Equal("https://backup.example.com:8443", SetupUi.Normalize("  backup.example.com/  "));
            Assert.Equal("https://backup.example.com:9443", SetupUi.Normalize("backup.example.com:9443"));
            Assert.Equal("https://backup.example.com", SetupUi.Normalize("https://backup.example.com:443"));
            Assert.Equal("http://10.0.0.5:8080", SetupUi.Normalize("http://10.0.0.5:8080"));
            Assert.Equal("http://10.0.0.5", SetupUi.Normalize("http://10.0.0.5"));
            foreach (var bad in new[] { null, "", "   ", "not a server", "ftp://backup.example.com", "backup." + new string('a', 300) + ".com" })
                Assert.Null(SetupUi.Normalize(bad));

            using (var z = NewWizard("bounds"))
            {
                foreach (var key in new[] { null, "wrong" }) Step(z.Ui, "info", null, 403, key);
                Step(z.Ui, "install", Existing(z.Server.Url, Pw, "1"), 403, null);
                Assert.Empty(z.Server.Requests);                                            // nothing without the key
                Assert.False(string.IsNullOrEmpty(Json.Str(Step(z.Ui, "check", new Dictionary<string, object> { { "server", "not a server" } }, 400), "message")));
                var silent = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); silent.Start(); var port = ((IPEndPoint)silent.LocalEndpoint).Port; silent.Stop();
                Assert.False(string.IsNullOrEmpty(Json.Str(Step(z.Ui, "check", new Dictionary<string, object> { { "server", "http://127.0.0.1:" + port } }, 400), "message")));

                z.Contract = 0;                                                            // no agreement on this server
                var c = Step(z.Ui, "check", new Dictionary<string, object> { { "server", z.Server.Url } });
                Assert.Equal("", c["contract"]);
                Step(z.Ui, "install", Existing(z.Server.Url.TrimEnd('/'), Pw, "0"));
                Assert.Equal("", Json.Str(Installed(z.Ui), "error"));
                Assert.Equal(z.Device, new AgentApp(z.Data).Home.DeviceToken);
                Assert.Equal("0", z.Server.Of("register").Single().Msg["contractVersion"] ?? "0");
            }
        }
    }
}
