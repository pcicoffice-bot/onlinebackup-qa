using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// PILOT-010 (owner): the pilot "Windows File Backup" — with the server's one switch (system.xml SCOPE="PILOT") every
    /// capability outside the pilot is REFUSED by the server (403 SCOPE "... is not supported in this version"), not only
    /// hidden; a set of a blocked kind made before the switch is kept unchanged (refused, not run, its data never touched);
    /// without the switch nothing changes (each refusal has its control with the switch off).
    /// </summary>
    public class PilotScopeServerTests
    {
        public const string Pw = "Customer-Pass-1";

        public static void PilotOn(Env env) { env.Cfg.Doc.Root.SetAttributeValue("SCOPE", "PILOT"); env.Cfg.Save(); }
        public static void PilotOff(Env env) { env.Cfg.Doc.Root.SetAttributeValue("SCOPE", null); env.Cfg.Save(); }

        /// <summary>The refusal of the pilot: 403, code SCOPE, a message that says why.</summary>
        public static AgentException Refused(Action a, string names = null)
        {
            var e = Assert.Throws<AgentException>(a);
            Assert.Equal(403, e.Status);
            Assert.Equal("SCOPE", e.Code);
            Assert.Contains("not supported in this version", e.Message);
            if (names != null) Assert.Contains(names, e.Message);
            return e;
        }

        /// <summary>A set of each kind that the server takes with the switch off (all its other rules met).</summary>
        public static BackupSetInfo SetOf(string kind, string dir)
        {
            switch (kind)
            {
                case "MSSQL": return new BackupSetInfo { Name = "SQL", Type = "MSSQL", Sources = { @"Microsoft SQL Server\SQLEXPRESS" } };
                case "ORACLE": return new BackupSetInfo { Name = "Oracle", Type = "ORACLE", Sources = { "ORCL" } };
                case "BAREMETAL": return new BackupSetInfo { Name = "Image", Type = "BAREMETAL", Sources = { dir } };
                case "M365": return new BackupSetInfo { Name = "M365", Type = "M365", Engine = "RESTIC", M365Tenant = "contoso.onmicrosoft.com", M365ClientId = "00000000-1111", Sources = { dir } };
                case "GWS": return new BackupSetInfo { Name = "GWS", Type = "GWS", Engine = "RESTIC", Sources = { dir } };
                case "RESTIC": return new BackupSetInfo { Name = "Files restic", Engine = "RESTIC", Sources = { dir } };
                case "PRECMD": return new BackupSetInfo { Name = "Files cmd", Sources = { dir }, PreCommands = { "echo before" } };
                case "POSTCMD": return new BackupSetInfo { Name = "Files cmd", Sources = { dir }, PostCommands = { "echo after" } };
                case "LOCALCOPY": return new BackupSetInfo { Name = "Files local", Sources = { dir }, LocalCopy = true, LocalCopyPath = Path.Combine(dir, "..", "localcopy") };
                case "BOTH": return new BackupSetInfo { Name = "Files both", Sources = { dir }, DestMode = "BOTH", LocalCopyPath = Path.Combine(dir, "..", "localcopy") };
                case "LOCAL": return new BackupSetInfo { Name = "Files local only", Engine = "RESTIC", Sources = { dir }, DestMode = "LOCAL", LocalCopyPath = Path.Combine(dir, "..", "localcopy") };
                case "FILE": return new BackupSetInfo { Name = "Files", Sources = { dir } };
                default: return new BackupSetInfo { Name = kind, Type = kind };   // MYSQL, POSTGRESQL, DOMINO, SYSTEMSTATE, HYPERV, VMWARE: none chosen = all
            }
        }

        public static Msg CreateSet(Client session, BackupSetInfo s) { return session.Call("POST", "/api/sets", new Msg().Set("set", s.ToXml().ToString(SaveOptions.DisableFormatting))); }

        static HttpClient Http() { return new HttpClient(new HttpClientHandler { UseProxy = false }); }

        // ------------------------------------------------------------------ DB-01..04, AP-01..06, RS-03, BK-09, BK-10: creating a set

        [Theory]
        [InlineData("MSSQL", "SQL Server")]          // DB-01
        [InlineData("MYSQL", "MySQL")]               // DB-02
        [InlineData("POSTGRESQL", "PostgreSQL")]     // DB-02
        [InlineData("ORACLE", "Oracle")]             // DB-03
        [InlineData("DOMINO", "Domino")]             // DB-04
        [InlineData("SYSTEMSTATE", "System State")]  // AP-01
        [InlineData("BAREMETAL", "bare-metal")]      // AP-02
        [InlineData("HYPERV", "Hyper-V")]            // AP-03
        [InlineData("VMWARE", "VMware")]             // AP-04
        [InlineData("M365", "Microsoft 365")]        // AP-05
        [InlineData("GWS", "Google Workspace")]      // AP-06
        [InlineData("RESTIC", "restic")]             // RS-03
        [InlineData("PRECMD", "commands")]           // BK-09
        [InlineData("POSTCMD", "commands")]          // BK-09
        [InlineData("LOCALCOPY", "local copy")]      // BK-10
        [InlineData("BOTH", "local copy")]           // BK-10
        [InlineData("LOCAL", "restic")]              // BK-10 (local-only is a restic set)
        public void NewSetOfABlockedKind_IsRefusedWithTheReason_NothingIsAdded_AndWithoutTheSwitchItIsTaken(string kind, string names)
        {
            using (var env = new Env())
            {
                env.CreateUser("pilot", Pw);
                var app = env.Agent("pilot", Pw);
                var src = env.Dir("src");
                var session = app.Interactive(Pw, null);

                // the control: without the switch the server takes this set (the product as it was)
                var made = BackupSetInfo.FromXml(XElement.Parse(CreateSet(session, SetOf(kind, src))["set"]));
                Assert.NotNull(app.Profile().FindSet(made.Id));

                PilotOn(env);
                int before = app.Profile().Sets.Count;
                Refused(() => CreateSet(session, SetOf(kind, src)), names);
                Assert.Equal(before, app.Profile().Sets.Count);   // nothing was added

                // a set of files with this product's own engine is taken with the switch on
                Assert.NotNull(BackupSetInfo.FromXml(XElement.Parse(CreateSet(session, SetOf("FILE", src))["set"])).Id);
            }
        }

        // ------------------------------------------------------------------ a set of a blocked kind made before the switch

        [Theory]
        [InlineData("MSSQL")]
        [InlineData("SYSTEMSTATE")]
        [InlineData("RESTIC")]
        [InlineData("POSTCMD")]
        [InlineData("LOCALCOPY")]
        public void ExistingBlockedSet_EveryChangeAndRunIsRefused_TheSetStaysExactlyAsItWas(string kind)
        {
            using (var env = new Env())
            {
                env.CreateUser("pilot", Pw);
                var app = env.Agent("pilot", Pw);
                var session = app.Interactive(Pw, null);
                var set = BackupSetInfo.FromXml(XElement.Parse(CreateSet(session, SetOf(kind, env.Dir("src")))["set"]));
                var admin = env.Admin();
                var detail = admin.Call("GET", "/api/admin/users/pilot/sets/" + set.Id);
                var version = detail["version"];
                PilotOn(env);

                // the admin site: save (unchanged settings), back up now, add a computer — all refused, with the reason
                Refused(() => admin.Call("POST", "/api/admin/users/pilot/sets/" + set.Id, new Msg().Set("set", detail["set"]).Set("version", version)));
                Refused(() => admin.Call("POST", "/api/admin/users/pilot/sets/" + set.Id + "/run", new Msg()));
                Refused(() => admin.Call("POST", "/api/admin/users/pilot/sets/" + set.Id + "/addcomputer", new Msg().Set("computer", "PC-OTHER")));
                // the client software: changing it
                Refused(() => session.Call("POST", "/api/sets/" + set.Id + "/settings", new Msg().Set("set", detail["set"])));
                // an agent that does not know the switch (an older version) cannot start a run of it either
                if (set.Engine != "RESTIC") Refused(() => app.DeviceClient().Call("POST", "/api/sets/" + set.Id + "/begin?key=" + Guid.NewGuid().ToString("N")));
                // the admin site says why, on the set itself
                Assert.Contains("not supported in this version", admin.Call("GET", "/api/admin/users/pilot/sets/" + set.Id)["scope"] ?? "");

                // kept exactly as it was: the same settings, never deleted, no run request left behind
                var e = Profile.Load(Path.Combine(env.HomeA, "pilot", "db", "Profile.xml")).FindSet(set.Id);
                Assert.NotNull(e);
                Assert.Equal(version, SetControl.Version(e));
                Assert.True(string.IsNullOrEmpty((string)e.Attribute("RUN_REQUEST")) || (string)e.Attribute("RUN_REQUEST") == "0");
                var setDir = Path.Combine(env.HomeA, "pilot", "files", set.Id);
                Assert.Empty(Directory.Exists(setDir) ? SetStore.OpenJobDirs(setDir).ToList() : new List<string>());   // no run was opened

                // the control: switched off, the same save is taken again (nothing was broken by the refusals)
                PilotOff(env);
                admin.Call("POST", "/api/admin/users/pilot/sets/" + set.Id, new Msg().Set("set", detail["set"]).Set("version", version));
            }
        }

        [Theory]
        [InlineData("POSTCMD", "commands")]
        [InlineData("LOCALCOPY", "local copy")]
        public void AnOptionOutsideThePilot_CannotBeAddedToAFileSet_ButRemovingItIsTaken(string kind, string names)
        {
            using (var env = new Env())
            {
                env.CreateUser("pilot", Pw);
                var app = env.Agent("pilot", Pw);
                var session = app.Interactive(Pw, null);
                var src = env.Dir("src");
                var set = BackupSetInfo.FromXml(XElement.Parse(CreateSet(session, SetOf("FILE", src))["set"]));
                var admin = env.Admin();
                PilotOn(env);
                var d = admin.Call("GET", "/api/admin/users/pilot/sets/" + set.Id);
                var s = BackupSetInfo.FromXml(XElement.Parse(d["set"]));
                var blocked = SetOf(kind, src);
                s.PostCommands = blocked.PostCommands; s.LocalCopy = blocked.LocalCopy; s.LocalCopyPath = blocked.LocalCopyPath;
                Refused(() => admin.Call("POST", "/api/admin/users/pilot/sets/" + set.Id, new Msg().Set("set", s.ToXml(XElement.Parse(d["set"])).ToString(SaveOptions.DisableFormatting)).Set("version", d["version"])), names);
                Assert.Empty(app.Profile().Sets.First(x => x.Id == set.Id).PostCommands);
                Assert.False(app.Profile().Sets.First(x => x.Id == set.Id).LocalCopy);

                // made blocked before the switch, the option is removed with the switch on: the set is back in the pilot
                PilotOff(env);
                admin.Call("POST", "/api/admin/users/pilot/sets/" + set.Id, new Msg().Set("set", s.ToXml(XElement.Parse(d["set"])).ToString(SaveOptions.DisableFormatting)).Set("version", d["version"]));
                PilotOn(env);
                var d2 = admin.Call("GET", "/api/admin/users/pilot/sets/" + set.Id);
                var s2 = BackupSetInfo.FromXml(XElement.Parse(d2["set"]));
                s2.PostCommands.Clear(); s2.LocalCopy = false; s2.DestMode = "SERVER";
                admin.Call("POST", "/api/admin/users/pilot/sets/" + set.Id, new Msg().Set("set", s2.ToXml(XElement.Parse(d2["set"])).ToString(SaveOptions.DisableFormatting)).Set("version", d2["version"]));
                Assert.Null(Scope.Refusal(app.Profile().Sets.First(x => x.Id == set.Id)));
            }
        }

        [Fact]
        public void SetTemplates_OfABlockedKind_AreRefused_AFileTemplateIsTaken()
        {
            using (var env = new Env())
            {
                var admin = env.Admin();
                PilotOn(env);
                Refused(() => admin.Call("POST", "/api/admin/templates", new Msg().Set("name", "sql").Set("type", "MSSQL").Set("set", new BackupSetInfo { Type = "MSSQL" }.ToXml().ToString())), "SQL Server");
                Refused(() => admin.Call("POST", "/api/admin/templates", new Msg().Set("name", "cmd").Set("type", "FILE").Set("set", new BackupSetInfo { PreCommands = { "x" } }.ToXml().ToString())), "commands");
                var ok = admin.Call("POST", "/api/admin/templates", new Msg().Set("name", "files").Set("type", "FILE").Set("set", new BackupSetInfo().ToXml().ToString()));
                Assert.Single(ok.List("templates"));
            }
        }

        // ------------------------------------------------------------------ RS-03 / RS-04: the restic engine and the website restore

        [Fact]
        public void ResticRoutes_AndTheWebsiteRestore_AreRefused()
        {
            using (var env = new Env())
            {
                env.CreateUser("pilot", Pw);
                var app = env.Agent("pilot", Pw);
                var session = app.Interactive(Pw, null);
                var set = BackupSetInfo.FromXml(XElement.Parse(CreateSet(session, SetOf("RESTIC", env.Dir("src")))["set"]));
                var token = app.DeviceClient().Call("POST", "/api/sets/" + set.Id + "/restic", new Msg());   // control: issued with the switch off
                PilotOn(env);
                Refused(() => app.DeviceClient().Call("POST", "/api/sets/" + set.Id + "/restic", new Msg()), "restic");
                Refused(() => app.DeviceClient().Call("POST", "/api/sets/" + set.Id + "/resticreport", new Msg().Set("result", "BS_STOP_SUCCESS")), "restic");
                Refused(() => session.Call("GET", "/api/webrestore/sets"));
                using (var http = Http())
                {
                    // the repository itself, with the token issued before the switch
                    var req = new HttpRequestMessage(HttpMethod.Get, env.Url + "restic/pilot/" + set.Id + "/config");
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("pilot:" + token["token"])));
                    var r = http.SendAsync(req).Result;
                    Assert.Equal(403, (int)r.StatusCode);
                    Assert.Contains("not supported in this version", r.Content.ReadAsStringAsync().Result);
                    Assert.Equal(403, (int)http.GetAsync(env.Url + "restore").Result.StatusCode);   // the website restore page
                }
                Assert.Equal(0, env.Api.Runs.Since(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1), l => true).Count());   // the refused report recorded nothing
            }
        }

        // ------------------------------------------------------------------ ST-07: replication to a second server

        [Fact]
        public void Replication_CannotBeSwitchedOnOrRun_NorReceived_AndNothingIsQueued()
        {
            using (var env = new Env())
            {
                var admin = env.Admin();
                PilotOn(env);
                Refused(() => admin.Call("POST", "/api/admin/settings", new Msg().Set("replicationOn", 1).Set("replicationUrl", "https://second:8443").Set("replicationToken", "t")), "second server");
                Refused(() => admin.Call("POST", "/api/admin/settings", new Msg().Set("replicaReceiverToken", "receiver-token-123")), "second server");
                Refused(() => admin.Call("POST", "/api/admin/replicate", new Msg()), "second server");
                var c = new Client(env.Url);
                Refused(() => c.Call("POST", "/api/replica/user", new Msg().Set("login", "x")), "second server");
                Assert.False(env.Api.Replication.Enabled);
                // other settings are still saved
                admin.Call("POST", "/api/admin/settings", new Msg().Set("senderName", "Pilot IT"));
                Assert.Equal("Pilot IT", admin.Call("GET", "/api/admin/settings")["senderName"]);
            }
        }

        [Fact]
        public void Replication_SetUpBeforeTheSwitch_StopsSending_AndItsQueueIsKept()
        {
            using (var env = new Env())
            {
                var admin = env.Admin();
                admin.Call("POST", "/api/admin/settings", new Msg().Set("replicationOn", 1).Set("replicationUrl", "http://127.0.0.1:9/").Set("replicationToken", "t"));
                Assert.True(env.Api.Replication.Enabled);   // control
                env.Api.Replication.Enqueue(new Msg().Set("type", "user").Set("login", "x"));
                Assert.Equal(1, env.Api.Replication.Pending);
                PilotOn(env);
                Assert.False(env.Api.Replication.Enabled);
                env.Api.Replication.Enqueue(new Msg().Set("type", "user").Set("login", "y"));
                Assert.Equal(0, env.Api.Replication.RunOnce());
                Assert.Equal(1, env.Api.Replication.Pending);   // nothing new queued, nothing sent, what was waiting is kept
            }
        }

        // ------------------------------------------------------------------ AG-08: Windows XP / 2003 (the .NET 4.0 agent's old Windows)

        [Fact]
        public void AnAgentOnWindowsXpOr2003_IsRefused_AModernWindowsAgentIsServed()
        {
            using (var env = new Env())
            {
                env.CreateUser("pilot", Pw);
                var app = env.Agent("pilot", Pw);
                const string Xp = "1.0.0.0; Microsoft Windows NT 5.2.3790 Service Pack 2", Win10 = "1.0.0.0; Microsoft Windows NT 10.0.19045.0";
                Func<HttpClient, string, HttpResponseMessage> profile = (http, agent) =>
                {
                    var req = new HttpRequestMessage(HttpMethod.Get, env.Url + "api/profile");
                    req.Headers.Add("X-Device", app.Home.DeviceToken); req.Headers.Add("X-Agent", agent);
                    return http.SendAsync(req).Result;
                };
                Func<HttpClient, string, HttpResponseMessage> register = (http, agent) =>
                {
                    var req = new HttpRequestMessage(HttpMethod.Post, env.Url + "api/register") { Content = new ByteArrayContent(new Msg().Set("login", "pilot").Set("password", Pw).Set("computer", "OLD-PC").ToBytes()) };
                    req.Headers.Add("X-Agent", agent);
                    return http.SendAsync(req).Result;
                };
                using (var http = Http())
                {
                    Assert.Equal(200, (int)profile(http, Xp).StatusCode);   // control: switch off
                    PilotOn(env);
                    var r = profile(http, Xp);
                    Assert.Equal(403, (int)r.StatusCode);
                    var m = Msg.Parse(r.Content.ReadAsByteArrayAsync().Result);
                    Assert.Equal("SCOPE", m["error"]); Assert.Contains("not supported in this version", m["message"]);
                    Assert.Equal(403, (int)register(http, Xp).StatusCode);
                    Assert.Equal(200, (int)profile(http, Win10).StatusCode);
                    Assert.Equal(200, (int)register(http, Win10).StatusCode);
                }
            }
        }

        // ------------------------------------------------------------------ AU-03: sign-up from the client software

        [Fact]
        public void SignupFromTheClient_IsRefused_AndTheServerSaysItTakesNoNewAccounts()
        {
            using (var env = new Env())
            {
                var c = new Client(env.Url);
                Assert.Equal("1", c.Call("GET", "/api/contract")["signupOpen"]);   // control
                PilotOn(env);
                Assert.Equal("0", c.Call("GET", "/api/contract")["signupOpen"]);
                Refused(() => c.Call("POST", "/api/signup", new Msg().Set("company", "New Co").Set("email", "a@b.example").Set("login", "newco").Set("password", Pw)), "account");
                Assert.DoesNotContain("newco", env.Api.UserStore.Logins());
            }
        }

        // ------------------------------------------------------------------ SH-04: service calls from backup results

        [Fact]
        public void BackupResults_OpenNoServiceCall_ACallOpenedByHandStillWorks()
        {
            using (var env = new Env())
            {
                env.CreateUser("pilot", Pw);
                var app = env.Agent("pilot", Pw);
                var session = app.Interactive(Pw, null);
                var a = BackupSetInfo.FromXml(XElement.Parse(CreateSet(session, SetOf("FILE", env.Dir("a")))["set"]));
                var b = BackupSetInfo.FromXml(XElement.Parse(CreateSet(session, SetOf("FILE", env.Dir("b")))["set"]));
                var admin = env.Admin();
                admin.Call("POST", "/api/admin/ticketsettings", new Msg().Set("fail", 1).Set("clientcalls", 1));
                Action<string> failedRun = id => app.DeviceClient().Call("POST", "/api/sets/" + id + "/interrupted", new Msg().Set("job", RunId.From(DateTime.UtcNow.AddMinutes(-5))).Set("started", RunId.UnixMs(DateTime.UtcNow.AddMinutes(-5))).Set("why", "test"));
                failedRun(a.Id);
                Assert.Single(env.Api.Calls.List("all", "pilot"));   // control: the failed run opened a call
                PilotOn(env);
                failedRun(b.Id);
                Assert.Single(env.Api.Calls.List("all", "pilot"));   // no new call
                Assert.Null(env.Api.Calls.Auto("fail", "pilot", b.Id, b.Name, "PC", "x", "y"));
                // a call the customer opens by hand is not a backup result: it still works
                app.DeviceClient().Call("POST", "/api/tickets", new Msg().Set("subject", "help").Set("description", "please"));
                Assert.Equal(2, env.Api.Calls.List("all", "pilot").Count);
            }
        }

        // ------------------------------------------------------------------ SH-06: moving a computer to another customer

        [Fact]
        public void MovingAComputerToAnotherCustomer_IsRefused_DisconnectingStillWorks()
        {
            using (var env = new Env())
            {
                env.CreateUser("pilot", Pw); env.CreateUser("other", Pw);
                var app = env.Agent("pilot", Pw);
                var session = app.Interactive(Pw, null);
                CreateSet(session, SetOf("FILE", env.Dir("src")));
                var admin = env.Admin();
                PilotOn(env);
                Refused(() => admin.Call("POST", "/api/admin/users/pilot/computers/move", new Msg().Set("computer", app.Home.Computer).Set("target", "other")), "customer");
                Assert.Single(app.Profile().Sets);
                Assert.Empty(Profile.Load(Path.Combine(env.HomeA, "other", "db", "Profile.xml")).Sets);
                Assert.Equal("1", admin.Call("POST", "/api/admin/users/pilot/computers/disconnect", new Msg().Set("computer", app.Home.Computer))["revoked"]);
            }
        }

        // ------------------------------------------------------------------ UI-06: the partner portal / licensing centre

        [Fact]
        public void ThePilotServer_DoesNotServeThePartnerPortalOrTheLicensingCentre()
        {
            using (var env = new Env())
            {
                PilotOn(env);
                using (var http = Http())
                {
                    Assert.Equal(404, (int)http.GetAsync(env.Url + "portal").Result.StatusCode);
                    Assert.Equal(404, (int)http.GetAsync(env.Url + "admin/portal.html").Result.StatusCode);
                    Assert.Equal(404, (int)http.GetAsync(env.Url + "admin/portal.js").Result.StatusCode);
                    Assert.NotEqual(200, (int)http.GetAsync(env.Url + "api/portal/info").Result.StatusCode);   // no such route: an unknown device (401)
                }
            }
        }

        // ------------------------------------------------------------------ UI-08: AI

        [Fact]
        public void AiFeatures_AreRefused_AndTheAssistantCannotBeSwitchedOn()
        {
            using (var env = new Env())
            {
                env.CreateUser("pilot", Pw);
                var admin = env.Admin();
                admin.Call("POST", "/api/admin/settings", new Msg().Set("aiOn", 1).Set("aiKey", "sk-test-key"));
                Assert.True(Ai.On(env.Cfg));   // control
                PilotOn(env);
                Assert.False(Ai.On(env.Cfg)); Assert.False(Ai.AutoDiagnose(env.Cfg)); Assert.False(Ai.Search(env.Cfg));
                Refused(() => admin.Call("POST", "/api/admin/users/pilot/aidiagnose", new Msg().Set("set", "1").Set("cat", "Backup").Set("file", "x.log")), "AI");
                Refused(() => admin.Call("GET", "/api/admin/insights"), "AI");
                Refused(() => admin.Call("POST", "/api/admin/aitest", new Msg()), "AI");
                Refused(() => admin.Call("POST", "/api/admin/settings", new Msg().Set("aiOn", 1)), "AI");
                Assert.ThrowsAny<Exception>(() => Ai.Diagnose(env.Cfg, "en", "FILE", "", new[] { "x" }));
            }
        }

        // ------------------------------------------------------------------ Mac and Linux client packages

        [Theory]
        [InlineData("linux", "Linux")]
        [InlineData("mac", "Mac")]
        public void MacAndLinuxClientPackages_AreRefused(string os, string names)
        {
            using (var env = new Env())
            {
                var admin = env.Admin();
                admin.Call("POST", "/api/admin/settings", new Msg().Set("publicUrl", "https://backup.example.com:8443"));
                // the control: without the switch the package is not refused by the scope (here it may fail only for its files)
                try { admin.Call("GET", "/api/admin/clientpackage?os=" + os); } catch (AgentException e) { Assert.NotEqual("SCOPE", e.Code); } catch (FormatException) { } catch (System.Xml.XmlException) { }
                PilotOn(env);
                Refused(() => admin.Call("GET", "/api/admin/clientpackage?os=" + os), names);
            }
        }

        // ------------------------------------------------------------------ the scope is told to the agent and to the admin site

        [Fact]
        public void TheScope_IsInTheProfileTheAgentReads_AndInWhoAmI_OnlyWithTheSwitch()
        {
            using (var env = new Env())
            {
                env.CreateUser("pilot", Pw);
                var app = env.Agent("pilot", Pw);
                var admin = env.Admin();
                Assert.Null(app.Profile().Root.Attribute("SERVER_SCOPE"));
                Assert.Null(admin.Call("GET", "/api/admin/me")["scope"]);
                PilotOn(env);
                Assert.Equal("PILOT", (string)app.Profile().Root.Attribute("SERVER_SCOPE"));
                Assert.Equal("PILOT", admin.Call("GET", "/api/admin/me")["scope"]);
                // only in the answer: the stored profile is not changed
                Assert.Null(Profile.Load(Path.Combine(env.HomeA, "pilot", "db", "Profile.xml")).Root.Attribute("SERVER_SCOPE"));
            }
        }

        public static Dictionary<string, string> Manifest(string dir)
        {
            var m = new Dictionary<string, string>();
            if (!Directory.Exists(dir)) return m;
            using (var sha = SHA256.Create())
                foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                    m[f.Substring(dir.Length)] = Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(f)));
            return m;
        }
    }
}
