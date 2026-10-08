using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Xml.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;
using static OnlineBackup.Tests.PilotScopeServerTests;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// PILOT-010, adversarial challenge (owner condition: what is outside the pilot "Windows File Backup" is TECHNICALLY
    /// blocked, not only labelled). Each test tries one path the PilotScope* tests do not cover — another route, odd values
    /// in the set's XML, alternate URLs, a run opened before the switch, a restart, the switch itself — and checks with an
    /// independent oracle (the files on disk, SHA-256 manifests, the stored profile, the run history) that nothing outside
    /// the pilot ran and nothing of a blocked set changed. A path that is blocked has a passing test so it stays blocked.
    /// </summary>
    public class PilotChallengeScopeTests
    {
        static HttpClient Http() { return new HttpClient(new HttpClientHandler { UseProxy = false }); }

        /// <summary>The call must be refused by the pilot (403 SCOPE); anything else is a bypass, named with what came back.</summary>
        static void MustBeScopeRefused(Func<Msg> call, string what)
        {
            string got;
            try { call(); got = "it was carried out (200 OK)"; }
            catch (AgentException e)
            {
                if (e.Status == 403 && e.Code == Scope.Code && e.Message.Contains("not supported in this version")) return;
                got = e.Status + " " + e.Code + ": " + e.Message;
            }
            Assert.True(false, "BYPASS with the pilot switch on: " + what + " - expected 403 SCOPE, got " + got);
        }

        static string ProfilePath(Env env, string login) { return Path.Combine(env.HomeA, login, "db", "Profile.xml"); }
        static XElement StoredSet(Env env, string id) { return Profile.Load(ProfilePath(env, "pilot")).FindSet(id); }

        static string MarkerCommand(string marker)
        {
            return Environment.OSVersion.Platform == PlatformID.Win32NT ? "cmd /c echo x > \"" + marker + "\"" : "touch \"" + marker + "\"";
        }

        static Dictionary<string, string> Source(string dir)
        {
            for (int i = 0; i < 4; i++) { var b = new byte[30000 + i * 311]; new Random(i + 7).NextBytes(b); File.WriteAllBytes(Path.Combine(dir, "f" + i + ".bin"), b); }
            return Manifest(dir);
        }

        /// <summary>A FILE set with this product's engine, backed up once with the switch off, then made blocked (a command after
        /// the backup, BK-09) by the IT company — still before the switch. Its server data exists and is restorable.</summary>
        static BackupSetInfo BackedUpThenBlocked(Env env, AgentApp app, string srcDir)
        {
            var set = app.CreateSet(app.Interactive(Pw, null), Pw, new BackupSetInfo { Name = "Documents", Sources = { srcDir } });
            Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);
            var admin = env.Admin();
            var d = admin.Call("GET", "/api/admin/users/pilot/sets/" + set.Id);
            var s = BackupSetInfo.FromXml(XElement.Parse(d["set"])); s.PostCommands.Add("echo done");
            admin.Call("POST", "/api/admin/users/pilot/sets/" + set.Id, new Msg().Set("set", s.ToXml(XElement.Parse(d["set"])).ToString(SaveOptions.DisableFormatting)).Set("version", d["version"]));
            return app.Sets().First(x => x.Id == set.Id);
        }

        // ================================================================== routes the existing tests do not cover

        [Fact]
        public void MovingABlockedSetToAnotherComputer_IsRefused_AndTheSetIsNotChanged()
        {
            using (var env = new Env())
            {
                env.CreateUser("pilot", Pw);
                var app = env.Agent("pilot", Pw);
                var set = BackupSetInfo.FromXml(XElement.Parse(CreateSet(app.Interactive(Pw, null), SetOf("MSSQL", env.Dir("src")))["set"]));
                var admin = env.Admin();
                admin.Call("POST", "/api/admin/users/pilot/sets/" + set.Id + "/move", new Msg().Set("computer", "PC-FIRST"));   // control: taken with the switch off
                PilotOn(env);
                var before = StoredSet(env, set.Id).ToString(SaveOptions.DisableFormatting);
                MustBeScopeRefused(() => admin.Call("POST", "/api/admin/users/pilot/sets/" + set.Id + "/move", new Msg().Set("computer", "PC-OTHER")),
                    "the admin site moves a blocked SQL Server set to another computer (POST /api/admin/users/{login}/sets/{id}/move)");
                Assert.Equal(before, StoredSet(env, set.Id).ToString(SaveOptions.DisableFormatting));
            }
        }

        [Fact]
        public void PuttingBackTheResticTrash_OfABlockedResticSet_IsRefused_AndTheRepositoryIsByteIdentical()
        {
            using (var env = new Env())
            {
                env.CreateUser("pilot", Pw);
                var app = env.Agent("pilot", Pw);
                var set = BackupSetInfo.FromXml(XElement.Parse(CreateSet(app.Interactive(Pw, null), SetOf("RESTIC", env.Dir("src")))["set"]));
                // the set's repository on the server, as restic left it: a config and one snapshot file a prune moved to the trash
                var store = new ResticStore(Path.Combine(env.HomeA, "pilot"), set.Id);
                store.Create();
                File.WriteAllBytes(Path.Combine(store.Dir, "config"), Encoding.UTF8.GetBytes("restic-config-bytes"));
                var name = new string('a', 64);
                Directory.CreateDirectory(Path.Combine(store.Dir, ".trash", "snapshots"));
                File.WriteAllBytes(Path.Combine(store.Dir, ".trash", "snapshots", name + ".1700000000000"), Encoding.UTF8.GetBytes("snapshot-bytes"));
                var before = Manifest(store.Dir);
                var admin = env.Admin();
                PilotOn(env);
                MustBeScopeRefused(() => admin.Call("POST", "/api/admin/users/pilot/untrash?set=" + set.Id, new Msg()),
                    "the admin site puts back the restic trash of a blocked restic set (POST /api/admin/users/{login}/untrash?set=, RST-070)");
                Assert.Equal(before, Manifest(store.Dir));
                Assert.False(File.Exists(Path.Combine(store.Dir, "snapshots", name)));
            }
        }

        [Fact]
        public void ARestoreTestReport_ForABlockedSet_IsRefused_NothingIsRecorded_AndTheSetIsNotChanged()
        {
            using (var env = new Env())
            {
                env.CreateUser("pilot", Pw);
                var app = env.Agent("pilot", Pw);
                var set = BackupSetInfo.FromXml(XElement.Parse(CreateSet(app.Interactive(Pw, null), SetOf("POSTCMD", env.Dir("src")))["set"]));
                PilotOn(env);
                var before = StoredSet(env, set.Id).ToString(SaveOptions.DisableFormatting);
                // the agent never tests such a set (PilotScopeAgentTests); an agent that does not know the switch reports one anyway
                MustBeScopeRefused(() => app.DeviceClient().Call("POST", "/api/sets/" + set.Id + "/restoretest", new Msg().Set("checked", 5).Set("ok", 5).Set("failed", 0)),
                    "a device reports a restore test of a blocked set (POST /api/sets/{id}/restoretest)");
                Assert.Equal(before, StoredSet(env, set.Id).ToString(SaveOptions.DisableFormatting));
                Assert.Null(StoredSet(env, set.Id).Attribute("LAST_RESTORE_TEST"));
                Assert.Equal(0, env.Api.Runs.Since(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1), l => true).Count());
            }
        }

        [Fact]
        public void TheDataOfABlockedSet_CannotBeRestoredThroughTheServer_EvenByAClientThatSkipsItsOwnCheck()
        {
            using (var env = new Env())
            {
                env.CreateUser("pilot", Pw);
                var app = env.Agent("pilot", Pw);
                var srcDir = env.Dir("src"); Source(srcDir);
                var s = BackedUpThenBlocked(env, app, srcDir);
                PilotOn(env);
                Refused(() => app.RestoreFor(app.Interactive(Pw, null), s.Id, Pw), "commands");   // control: this agent refuses

                // the same restore, by a client that does not check (an older agent, a script on the customer's session)
                var session = app.Interactive(Pw, null);
                var target = env.Dir("restored");
                string got = null;
                try
                {
                    var r = new Restore(session, s, app.Key(s), Path.Combine(app.Home.Dir, "temp"));
                    r.Run(null, target, null, false);
                    got = "restore result " + r.Result + ", " + r.Restored + " files restored";
                }
                catch (AgentException e) { if (!(e.Status == 403 && e.Code == Scope.Code)) got = e.Status + " " + e.Code + ": " + e.Message; }
                int restored = Directory.GetFiles(target, "*", SearchOption.AllDirectories).Length;
                Assert.True(got == null && restored == 0, "BYPASS with the pilot switch on: the server served the data of a blocked set for a restore (GET /api/sets/{id}/points, files, object) - " + got + "; files written: " + restored);
            }
        }

        [Fact]
        public void ARunOfABlockedSet_OpenedBeforeTheSwitch_CannotBeCommittedAfterIt_AndItsDataIsByteIdentical()
        {
            using (var env = new Env())
            {
                env.CreateUser("pilot", Pw);
                var app = env.Agent("pilot", Pw);
                var srcDir = env.Dir("src"); Source(srcDir);
                var s = BackedUpThenBlocked(env, app, srcDir);
                var dev = app.DeviceClient();
                var job = dev.Call("POST", "/api/sets/" + s.Id + "/begin?key=" + Guid.NewGuid().ToString("N"))["job"];   // the run starts, then the server is switched
                var dataDir = Path.Combine(env.HomeA, "pilot", "files", s.Id);
                var points = new SetStore(Path.Combine(env.HomeA, "pilot"), s.Id).Points().Count;
                var before = Manifest(dataDir);
                PilotOn(env);
                MustBeScopeRefused(() => dev.Call("POST", "/api/sets/" + s.Id + "/jobs/" + job + "/commit", new Msg().Set("result", "BS_STOP_SUCCESS")),
                    "a run of a blocked set opened before the switch is committed after it (POST /api/sets/{id}/jobs/{job}/commit)");
                Assert.Equal(points, new SetStore(Path.Combine(env.HomeA, "pilot"), s.Id).Points().Count);
                Assert.Equal(before, Manifest(dataDir));
            }
        }

        [Fact]
        public void TheWebsiteRestorePage_IsNotServedUnderTheAdminPathEither()
        {
            using (var env = new Env())
            {
                PilotOn(env);
                using (var http = Http())
                {
                    Assert.Equal(403, (int)http.GetAsync(env.Url + "restore").Result.StatusCode);   // control: the page's own address is refused
                    foreach (var p in new[] { "admin/restore.html", "admin/restore.js" })
                    {
                        var r = http.GetAsync(env.Url + p).Result;
                        Assert.True((int)r.StatusCode != 200, "BYPASS with the pilot switch on: GET /" + p + " serves the website restore page (" + (int)r.StatusCode + ", " + r.Content.ReadAsByteArrayAsync().Result.Length + " bytes) that /restore refuses");
                    }
                }
            }
        }

        // ================================================================== paths that ARE blocked (kept blocked by these tests)

        [Fact]
        public void BulkActions_ATemplateMadeBeforeTheSwitch_DoesNotChangeABlockedSet_AndBackUpNowSkipsIt()
        {
            using (var env = new Env())
            {
                env.CreateUser("pilot", Pw);
                var app = env.Agent("pilot", Pw);
                var session = app.Interactive(Pw, null);
                var sql = BackupSetInfo.FromXml(XElement.Parse(CreateSet(session, SetOf("MSSQL", env.Dir("a")))["set"]));
                var files = BackupSetInfo.FromXml(XElement.Parse(CreateSet(session, SetOf("FILE", env.Dir("b")))["set"]));
                var admin = env.Admin();
                admin.Call("POST", "/api/admin/templates", new Msg().Set("name", "sql-night").Set("type", "MSSQL").Set("set", new BackupSetInfo { Type = "MSSQL", Hour = 3, Minute = 17 }.ToXml().ToString()));
                PilotOn(env);
                var sqlBefore = StoredSet(env, sql.Id).ToString(SaveOptions.DisableFormatting);
                var r = admin.Call("POST", "/api/admin/bulk", new Msg().Set("action", "template").Set("template", "sql-night").Add("logins", new Msg().Set("login", "pilot")));
                var one = r.List("results").Single();
                Assert.Equal("0", one["ok"]);
                Assert.Contains("not supported in this version", one["error"] ?? "");
                Assert.Equal(sqlBefore, StoredSet(env, sql.Id).ToString(SaveOptions.DisableFormatting));

                r = admin.Call("POST", "/api/admin/bulk", new Msg().Set("action", "run").Add("logins", new Msg().Set("login", "pilot")));
                Assert.Equal("1", r.List("results").Single()["computers"]);   // only the set of files
                Assert.True(string.IsNullOrEmpty((string)StoredSet(env, sql.Id).Attribute("RUN_REQUEST")) || (string)StoredSet(env, sql.Id).Attribute("RUN_REQUEST") == "0");
                Assert.NotEqual("0", (string)StoredSet(env, files.Id).Attribute("RUN_REQUEST") ?? "0");
            }
        }

        [Fact]
        public void TheCustomersOwnSettingsRoute_CannotAddACommandOrALocalCopy_EvenWithTheRight()
        {
            using (var env = new Env())
            {
                env.CreateUser("pilot", Pw);
                var app = env.Agent("pilot", Pw);
                var session = app.Interactive(Pw, null);
                var set = BackupSetInfo.FromXml(XElement.Parse(CreateSet(session, SetOf("FILE", env.Dir("src")))["set"]));
                env.Admin().Call("POST", "/api/admin/users/pilot/details", new Msg().Set("can_edit_options", 1).Set("can_edit_destination", 1));
                PilotOn(env);
                var cur = StoredSet(env, set.Id);
                var withCmd = BackupSetInfo.FromXml(cur); withCmd.PreCommands.Add("echo before");
                MustBeScopeRefused(() => session.Call("POST", "/api/sets/" + set.Id + "/settings", new Msg().Set("set", withCmd.ToXml(new XElement(cur)).ToString(SaveOptions.DisableFormatting))), "the customer adds a command");
                var both = BackupSetInfo.FromXml(cur); both.DestMode = "BOTH"; both.LocalCopyPath = env.Dir("lc");
                MustBeScopeRefused(() => session.Call("POST", "/api/sets/" + set.Id + "/settings", new Msg().Set("set", both.ToXml(new XElement(cur)).ToString(SaveOptions.DisableFormatting))), "the customer adds a local copy");
                var engine = BackupSetInfo.FromXml(cur); engine.Engine = "RESTIC";   // the engine is kept as it was (never taken from the request)
                session.Call("POST", "/api/sets/" + set.Id + "/settings", new Msg().Set("set", engine.ToXml(new XElement(cur)).ToString(SaveOptions.DisableFormatting)));
                Assert.Equal("", BackupSetInfo.FromXml(StoredSet(env, set.Id)).Engine);
                Assert.Empty(BackupSetInfo.FromXml(StoredSet(env, set.Id)).PreCommands);
            }
        }

        /// <summary>Odd spellings in the set's XML (case, spaces, unknown engine names, commands in other fields). The pilot is
        /// held when such a set is either refused, or - when taken - runs as a plain set of files with this product's engine:
        /// no restic repository, no local copy, no command run (checked on disk, not by what the product says).</summary>
        [Theory]
        [InlineData("type-lower", "TYPE", "mssql")]
        [InlineData("type-space-before", "TYPE", " MSSQL")]
        [InlineData("type-file-space", "TYPE", "FILE ")]
        [InlineData("type-file-lower", "TYPE", "file")]
        [InlineData("engine-lower", "ENGINE", "restic")]
        [InlineData("engine-title", "ENGINE", "Restic")]
        [InlineData("engine-space", "ENGINE", " RESTIC")]
        [InlineData("engine-suffix", "ENGINE", "RESTIC2")]
        [InlineData("dest-lower", "DEST_MODE", "both")]
        [InlineData("dest-space", "DEST_MODE", " BOTH")]
        [InlineData("dest-local-title", "DEST_MODE", "Local")]
        [InlineData("localcopy-lower", "LOCALCOPY", "y")]
        [InlineData("localcopy-yes", "LOCALCOPY", "YES")]
        [InlineData("cmd-lower-attr", "CMD", "path")]
        [InlineData("cmd-lower-element", "CMD", "pre_cmd")]
        [InlineData("cmd-in-workingdir", "CMD", "WORKING_DIR")]
        public void OddSpellingsInTheSetXml_AreRefused_OrRunAsAPlainFileSet(string _, string field, string value)
        {
            using (var env = new Env())
            {
                env.CreateUser("pilot", Pw);
                var app = env.Agent("pilot", Pw);
                var srcDir = env.Dir("src"); var src = Source(srcDir);
                var localDir = Path.Combine(env.Root, "localcopy");
                var marker = Path.Combine(env.Root, "command-ran.txt");
                PilotOn(env);
                app.Profile();
                // the set as the agent makes it (key and all), then the odd value written into its XML
                var s = new BackupSetInfo { Name = "Odd", Sources = { srcDir }, LocalCopyPath = localDir };
                var salt = Bytes.Random(16); var k = KeySet.Derive(Pw, salt);
                s.KeyType = "PASSWORD"; s.KeySalt = Convert.ToBase64String(salt); s.KeyCheck = k.CheckValue(); s.Computer = app.Home.Computer;
                var x = s.ToXml();
                switch (field)
                {
                    case "TYPE": case "ENGINE": case "DEST_MODE": x.SetAttributeValue(field, value); break;
                    case "LOCALCOPY": x.Element("EXTRA_LOCAL_BACKUP").SetAttributeValue("ENABLED", value); break;
                    case "CMD":
                        if (value == "path") x.Add(new XElement("PRE_CMD", new XAttribute("ID", "1"), new XAttribute("NAME", "c"), new XAttribute("path", MarkerCommand(marker))));
                        else if (value == "pre_cmd") x.Add(new XElement("pre_cmd", new XAttribute("ID", "1"), new XAttribute("NAME", "c"), new XAttribute("PATH", MarkerCommand(marker))));
                        else x.Add(new XElement("POST_CMD", new XAttribute("ID", "1"), new XAttribute("NAME", MarkerCommand(marker)), new XAttribute("PATH", ""), new XAttribute("WORKING_DIR", MarkerCommand(marker))));
                        break;
                }
                var session = app.Interactive(Pw, null);
                int count = app.Profile().Sets.Count;
                Msg made = null;
                try { made = session.Call("POST", "/api/sets", new Msg().Set("set", x.ToString(SaveOptions.DisableFormatting))); }
                catch (AgentException e) { Assert.InRange(e.Status, 400, 499); }
                if (made == null) Assert.Equal(count, app.Profile().Sets.Count);   // refused: nothing added
                else TakenRunsAsAPlainFileSet(env, app, made, k, src, srcDir, localDir, marker);
            }
        }

        /// <summary>A set the server took: it must be a plain set of files with this product's engine, and run as one.</summary>
        static void TakenRunsAsAPlainFileSet(Env env, AgentApp app, Msg made, KeySet k, Dictionary<string, string> src, string srcDir, string localDir, string marker)
        {
            {
                var created = BackupSetInfo.FromXml(XElement.Parse(made["set"]));
                app.Home.SaveKey(created.Id, k);
                Assert.NotEqual("RESTIC", created.Engine);
                Assert.Equal("FILE", created.Type);
                var run = app.Backup(created.Id);
                Assert.Equal("BS_STOP_SUCCESS", run.Result);
                Assert.Equal(src.Count, run.New);
                Assert.False(Directory.Exists(Path.Combine(env.HomeA, "pilot", "restic")), "a restic repository was made on the server");
                Assert.False(File.Exists(Path.Combine(app.Home.SetDir(created.Id), "restic-init.txt")), "restic was started on the computer");
                Assert.True(!Directory.Exists(localDir) || Directory.GetFiles(localDir, "*", SearchOption.AllDirectories).Length == 0, "a local copy was written");
                Assert.False(File.Exists(marker), "a command ran");
                Assert.Equal(src, Manifest(srcDir));
            }
        }

        [Fact]
        public void AlternateUrls_OfTheBlockedRoutes_TrailingSlashDoubleSlashCaseAndEncoding_AreNeverServed()
        {
            using (var env = new Env())
            {
                env.CreateUser("pilot", Pw);
                var app = env.Agent("pilot", Pw);
                var session = app.Interactive(Pw, null);
                var set = BackupSetInfo.FromXml(XElement.Parse(CreateSet(session, SetOf("RESTIC", env.Dir("src")))["set"]));
                var token = app.DeviceClient().Call("POST", "/api/sets/" + set.Id + "/restic", new Msg())["token"];   // issued before the switch
                var admin = env.Admin();
                PilotOn(env);
                var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes("pilot:" + token));
                using (var http = Http())
                {
                    Func<HttpMethod, string, string, string, byte[], HttpResponseMessage> send = (m, path, header, value, body) =>
                    {
                        var req = new HttpRequestMessage(m, env.Url + path);
                        if (header == "Authorization") req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", value);
                        else if (header != null) req.Headers.Add(header, value);
                        if (body != null) req.Content = new ByteArrayContent(body);
                        return http.SendAsync(req).Result;
                    };
                    var tried = new List<string>();
                    Action<HttpResponseMessage, string, bool> notServed = (r, what, must403) =>
                    {
                        int st = (int)r.StatusCode;
                        tried.Add(what + " -> " + st);
                        Assert.True(st != 200 && (!must403 || st == 403), "BYPASS with the pilot switch on: " + what + " answered " + st + " " + r.Content.ReadAsStringAsync().Result);
                    };
                    // the restic repository (RS-03), with the old token
                    foreach (var p in new[] { "restic/pilot/" + set.Id + "/config", "restic/pilot/" + set.Id + "/config/", "restic//pilot/" + set.Id + "/config", "restic/pilot//" + set.Id + "//config", "restic/pilot/" + set.Id + "/snapshots/" })
                        notServed(send(HttpMethod.Get, p, "Authorization", basic, null), "GET /" + p, true);
                    notServed(send(HttpMethod.Get, "Restic/pilot/" + set.Id + "/config", "Authorization", basic, null), "GET /Restic/... (case)", false);
                    notServed(send(HttpMethod.Get, "%72estic/pilot/" + set.Id + "/config", "Authorization", basic, null), "GET /%72estic/... (encoded)", false);
                    // the website restore (RS-04)
                    foreach (var p in new[] { "api/webrestore/sets/", "api//webrestore/sets", "api/webrestore//sets" })
                        notServed(send(HttpMethod.Get, p, "X-Session", session.Session, null), "GET /" + p, true);
                    notServed(send(HttpMethod.Get, "API/webrestore/sets", "X-Session", session.Session, null), "GET /API/webrestore/sets (case)", false);
                    notServed(send(HttpMethod.Get, "api/WebRestore/sets", "X-Session", session.Session, null), "GET /api/WebRestore/sets (case)", false);
                    notServed(send(HttpMethod.Get, "restore/", null, null, null), "GET /restore/", true);
                    notServed(send(HttpMethod.Get, "restore/restore.js", null, null, null), "GET /restore/restore.js", true);
                    // a new restic token, through another spelling of the route
                    notServed(send(HttpMethod.Post, "api/sets/" + set.Id + "/restic/", "X-Device", app.Home.DeviceToken, new Msg().ToBytes()), "POST /api/sets/{id}/restic/", true);
                    notServed(send(HttpMethod.Post, "api//sets/" + set.Id + "//restic", "X-Device", app.Home.DeviceToken, new Msg().ToBytes()), "POST /api//sets/{id}//restic", true);
                    // sign-up (AU-03)
                    var signup = new Msg().Set("company", "New Co").Set("email", "a@b.example").Set("login", "newco").Set("password", Pw).ToBytes();
                    notServed(send(HttpMethod.Post, "api/signup/", null, null, signup), "POST /api/signup/", true);
                    notServed(send(HttpMethod.Post, "api//signup", null, null, signup), "POST /api//signup", true);
                    Assert.DoesNotContain("newco", env.Api.UserStore.Logins());
                    // replication received (ST-07)
                    notServed(send(HttpMethod.Post, "api/replica//user/", "X-Replica-Token", "t", new Msg().Set("login", "x").ToBytes()), "POST /api/replica//user/", true);
                    // AI (UI-08)
                    notServed(send(HttpMethod.Get, "api/admin/insights/", "X-Session", admin.Session, null), "GET /api/admin/insights/", true);
                    notServed(send(HttpMethod.Post, "api/admin//aitest", "X-Session", admin.Session, new Msg().ToBytes()), "POST /api/admin//aitest", true);
                    Assert.True(tried.Count >= 18);
                }
            }
        }

        [Fact]
        public void TheSwitch_CannotBeTurnedOffFromAnyWebRoute_AndARestartedServerReadsIt()
        {
            using (var env = new Env())
            {
                env.CreateUser("pilot", Pw);
                var app = env.Agent("pilot", Pw);
                var admin = env.Admin();
                PilotOn(env);
                // every spelling of "scope" the settings route might take, and the settings that touch the system's root
                admin.Call("POST", "/api/admin/settings", new Msg().Set("scope", "full").Set("SCOPE", "").Set("Scope", "FULL").Set("pilot", 0).Set("host", "localhost").Set("senderName", "Pilot IT"));
                try { admin.Call("POST", "/api/admin/defaults", new Msg().Set("scope", "full").Set("SCOPE", "")); } catch (AgentException) { }
                try { admin.Call("POST", "/api/admin/time", new Msg().Set("scope", "full")); } catch (AgentException) { }
                try { admin.Call("POST", "/api/admin/contract", new Msg().Set("scope", "full")); } catch (AgentException) { }
                try { admin.Call("POST", "/api/admin/brand", new Msg().Set("scope", "full")); } catch (AgentException) { }
                Assert.True(env.Cfg.Pilot);
                Assert.Equal("PILOT", (string)XDocument.Load(Path.Combine(env.SystemHome, "conf", "system.xml")).Root.Attribute("SCOPE"));
                Assert.Equal("PILOT", admin.Call("GET", "/api/admin/me")["scope"]);

                // the server restarts (the service is started again): it reads the switch from system.xml
                env.Api.Dispose();
                var cfg2 = SystemConfig.Load(env.SystemHome);
                env.Api = new Api(cfg2); env.Cfg = cfg2;
                env.Api.Start(env.Url);
                Assert.True(cfg2.Pilot);
                Refused(() => CreateSet(app.Interactive(Pw, null), SetOf("MSSQL", env.Dir("src"))), "SQL Server");
                Assert.Equal("PILOT", (string)app.Profile().Root.Attribute("SERVER_SCOPE"));
            }
        }

        [Fact]
        public void ABlockedResticSet_KeepsItsRepositoryAndItsTrash_ByteIdentical_ThroughTheNightlyMaintenance()
        {
            using (var env = new Env())
            {
                env.CreateUser("pilot", Pw);
                var app = env.Agent("pilot", Pw);
                var set = BackupSetInfo.FromXml(XElement.Parse(CreateSet(app.Interactive(Pw, null), SetOf("RESTIC", env.Dir("src")))["set"]));
                var store = new ResticStore(Path.Combine(env.HomeA, "pilot"), set.Id);
                store.Create();
                File.WriteAllBytes(Path.Combine(store.Dir, "config"), Encoding.UTF8.GetBytes("restic-config-bytes"));
                var trash = Path.Combine(store.Dir, ".trash", "data");
                Directory.CreateDirectory(trash);
                var old = Path.Combine(trash, new string('b', 64) + ".1600000000000");
                File.WriteAllBytes(old, Encoding.UTF8.GetBytes("pruned-data"));
                File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-100));   // far past the trash delay (14 days)
                var before = Manifest(store.Dir);
                PilotOn(env);
                var m = env.Api.Maintenance(DateTime.UtcNow.AddDays(400));
                Assert.Empty(m.List("errors"));
                Assert.Equal(before, Manifest(store.Dir));
                Assert.True(File.Exists(old));
            }
        }

        /// <summary>
        /// Owner decision 115 (B, 2026-10-08, docs/OWNER-DECISIONS.md): with the pilot switch on, a set of a blocked kind is kept
        /// as it is - "verify" changes nothing at all; the administrator's explicit "rebuild index" IS allowed as a repair:
        /// it may rewrite only the index (index.db and its SQLite side files), every backed-up object stays byte-identical,
        /// and the repair is written to the server's admin log and the customer's Rebuild log.
        /// </summary>
        [Fact]
        public void ARebuildOrVerify_OfABlockedSet_KeepsEveryBackedUpObjectByteIdentical_TheRebuildIsALoggedRepair()
        {
            using (var env = new Env())
            {
                env.CreateUser("pilot", Pw);
                var app = env.Agent("pilot", Pw);
                var srcDir = env.Dir("src"); Source(srcDir);
                var s = BackedUpThenBlocked(env, app, srcDir);
                var dataDir = Path.Combine(env.HomeA, "pilot", "files", s.Id);
                var admin = env.Admin();
                PilotOn(env);
                var before = Manifest(dataDir);
                Assert.True(before.Keys.Any(k => !IsIndex(k)), "the blocked set has backed-up objects to compare");
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                foreach (var op in new[] { "verify", "rebuild" })
                {
                    admin.Call("POST", "/api/admin/" + op, new Msg().Set("login", "pilot").Set("set", s.Id).Set("verify", "1"));
                    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                    var after = Manifest(dataDir);
                    var changed = before.Keys.Union(after.Keys).Where(k => !before.ContainsKey(k) || !after.ContainsKey(k) || before[k] != after[k]).ToList();
                    if (op == "verify") Assert.True(changed.Count == 0, "verify changed the stored data of a blocked set: " + string.Join(", ", changed.Take(8)));
                    else Assert.True(changed.All(IsIndex), "the rebuild changed more than the index of a blocked set: " + string.Join(", ", changed.Where(k => !IsIndex(k)).Take(8)));
                }
                var rebuildLogs = Path.Combine(env.HomeA, "pilot", "logs", "Rebuild");
                Assert.True(Directory.Exists(rebuildLogs) && Directory.GetFiles(rebuildLogs).Length == 1, "the repair is written to the customer's Rebuild log");
            }
        }
        static bool IsIndex(string rel) { var n = Path.GetFileName(rel); return n == "index.db" || n.StartsWith("index.db-", StringComparison.Ordinal); }

        [Fact]
        public void ABlockedSetPutBackFromTheRecycleBin_StaysBlocked()
        {
            using (var env = new Env())
            {
                env.CreateUser("pilot", Pw);
                var app = env.Agent("pilot", Pw);
                var set = BackupSetInfo.FromXml(XElement.Parse(CreateSet(app.Interactive(Pw, null), SetOf("SYSTEMSTATE", env.Dir("src")))["set"]));
                var admin = env.Admin();
                Assert.Equal("1", admin.Call("POST", "/api/admin/users/pilot/delete", new Msg().Set("set", set.Id))["deleted"]);   // one administrator: at once
                PilotOn(env);
                var item = admin.Call("GET", "/api/admin/recycle").List("items").Single(i => i["set"] == set.Id);
                // put back (the customer's data, undeleted): it is the set it was - never run, refused for every run and change
                admin.Call("POST", "/api/admin/recycle/" + Uri.EscapeDataString(item["id"]) + "/restore", new Msg());
                Assert.NotNull(StoredSet(env, set.Id));
                Refused(() => app.DeviceClient().Call("POST", "/api/sets/" + set.Id + "/begin?key=" + Guid.NewGuid().ToString("N")), "System State");
                Refused(() => admin.Call("POST", "/api/admin/users/pilot/sets/" + set.Id + "/run", new Msg()), "System State");
                Refused(() => app.Backup(set.Id), "System State");
            }
        }

        // ================================================================== AG-08: what the server can see of an old Windows

        [Theory]
        [InlineData("1.0.0.0; Microsoft Windows NT 5.1.2600 Service Pack 3", true)]
        [InlineData("1.0.0.0; microsoft windows nt 5.2.3790", true)]       // case
        [InlineData("1.0.0.0; Microsoft Windows NT  5.1.2600", true)]      // two spaces
        [InlineData("1.0.0.0; Microsoft Windows NT 05.1.2600", true)]      // a leading zero
        [InlineData("1.0.0.0; Microsoft Windows NT 4.0", true)]
        [InlineData("1.0.0.0; Microsoft Windows NT 6.1.7601 Service Pack 1", false)]
        [InlineData("1.0.0.0; Microsoft Windows NT 10.0.19045.0", false)]
        [InlineData("", false)]                                              // an older agent without X-Agent: served (see the report)
        public void OldWindows_IsRecognisedInEverySpellingTheAgentCanSend(string agent, bool old)
        {
            Assert.Equal(old, Scope.OldWindows(agent));
        }
    }
}
