using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Xml.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;
using static OnlineBackup.Tests.PilotScopeServerTests;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// PILOT-010: the agent's side of the pilot "Windows File Backup" — it never runs (backup, restore, restore test) a set
    /// outside the pilot, even one that reaches it; the client window offers only files and folders with the product's own
    /// engine; and everything inside the pilot keeps working with the switch on: a set of files is created, backed up,
    /// restored byte for byte, tested automatically (RS-06), kept by its retention, encrypted, updated and recovered.
    /// </summary>
    public class PilotScopeAgentTests
    {
        static byte[] Rnd(int n, int seed) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }
        static string Sha(string f) { using (var s = SHA256.Create()) return Convert.ToBase64String(s.ComputeHash(File.ReadAllBytes(f))); }

        static Dictionary<string, string> Source(string dir)
        {
            for (int i = 0; i < 6; i++) File.WriteAllBytes(Path.Combine(dir, "f" + i + ".bin"), Rnd(40000 + i * 777, i));
            Directory.CreateDirectory(Path.Combine(dir, "sub"));
            File.WriteAllText(Path.Combine(dir, "sub", "note.txt"), "PILOT-PLAINTEXT-MARKER the contents of a customer's file");
            return Directory.GetFiles(dir, "*", SearchOption.AllDirectories).ToDictionary(f => f, Sha);
        }

        /// <summary>Every source file restored under target, identical (SHA-256) — the independent check.</summary>
        static void SameAsSource(Dictionary<string, string> src, string target)
        {
            foreach (var kv in src)
            {
                var r = Path.Combine(target, Env.Rel(kv.Key));
                Assert.True(File.Exists(r), "not restored: " + r);
                Assert.Equal(kv.Value, Sha(r));
            }
        }

        // ------------------------------------------------------------------ inside the pilot: everything works with the switch on

        [Fact]
        public void WithTheSwitch_AFileSetIsCreated_BackedUp_Restored_Tested_Kept_Encrypted_Updated_AndRecovered()
        {
            using (var env = new Env())
            {
                PilotOn(env);
                env.CreateUser("pilot", Pw);
                var app = env.Agent("pilot", Pw);
                var srcDir = env.Dir("src");
                var src = Source(srcDir);
                var set = app.CreateSet(app.Interactive(Pw, null), Pw, new BackupSetInfo { Name = "Documents", Sources = { srcDir } });
                Assert.Equal("", set.Engine);   // this product's own engine
                Assert.Equal("FILE", set.Type);

                var r1 = app.Backup(set.Id);
                Assert.Equal("BS_STOP_SUCCESS", r1.Result);
                Assert.Equal(src.Count, r1.New);
                File.WriteAllBytes(Path.Combine(srcDir, "f0.bin"), Rnd(41000, 99));   // a second version
                src[Path.Combine(srcDir, "f0.bin")] = Sha(Path.Combine(srcDir, "f0.bin"));
                var r2 = app.Backup(set.Id);
                Assert.Equal("BS_STOP_SUCCESS", r2.Result);
                Assert.Equal(1, r2.Updated);

                // restore (RS-01): every file identical to the source
                var target = env.Dir("restored");
                var rest = app.RestoreFor(app.Interactive(Pw, null), set.Id, Pw);
                rest.Run(null, target, null, false);
                Assert.Equal("RESTORE_STOP_SUCCESS", rest.Result);
                SameAsSource(src, target);

                // the automatic restore test (RS-06)
                var t = app.RestoreTest(set.Id, 7);
                Assert.True(t.Int("checked") > 0); Assert.Equal(0, t.Int("failed"));
                Assert.StartsWith("OK ", (string)Profile.Load(Path.Combine(env.HomeA, "pilot", "db", "Profile.xml")).FindSet(set.Id).Attribute("RESTORE_TEST_RESULT"));

                // encryption: no stored file carries the content or the name in clear
                foreach (var f in Directory.GetFiles(Path.Combine(env.HomeA, "pilot", "files", set.Id), "*", SearchOption.AllDirectories))
                {
                    var text = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(f));
                    Assert.DoesNotContain("PILOT-PLAINTEXT-MARKER", text);
                    Assert.DoesNotContain("note.txt", text);
                }

                // retention: the nightly maintenance applies the set's retention (it is not one of the kept-as-they-are sets)
                var m = env.Api.Maintenance(DateTime.UtcNow);
                Assert.Contains(m.List("sets"), x => x["set"] == set.Id && x["expiredPoints"] != null);
                Assert.Empty(m.List("errors"));

                // update: owner decision B1 — the computers' automatic update is off in Pilot 1 (not signed yet): with the switch
                // the server offers no client update (403 SCOPE)
                Refused(() => app.DeviceClient().Call("GET", "/api/client/files"), "client");

                // recovery: a new computer, with the key the server kept (key recovery), restores the same files
                var key = env.Admin().Call("GET", "/api/admin/keys/pilot/" + set.Id)["key"];
                var pc2 = new AgentApp(env.Dir("pc2"));
                pc2.Register(env.Url, "pilot", Pw, null, "PC-NEW");
                var target2 = env.Dir("restored2");
                var rest2 = pc2.RestoreFor(pc2.Interactive(Pw, null), set.Id, null, Convert.FromBase64String(key));
                rest2.Run(null, target2, null, false);
                Assert.Equal("RESTORE_STOP_SUCCESS", rest2.Result);
                SameAsSource(src, target2);
            }
        }

        // ------------------------------------------------------------------ outside the pilot: the agent never runs it

        [Theory]
        [InlineData("MSSQL", "SQL Server")]
        [InlineData("SYSTEMSTATE", "System State")]
        [InlineData("HYPERV", "Hyper-V")]
        [InlineData("RESTIC", "restic")]
        [InlineData("POSTCMD", "commands")]
        [InlineData("LOCALCOPY", "local copy")]
        public void ASetOutsideThePilot_ThatReachesTheAgent_IsNeverRun_BackupRestoreOrTest(string kind, string names)
        {
            using (var env = new Env())
            {
                env.CreateUser("pilot", Pw);
                var app = env.Agent("pilot", Pw);
                var srcDir = env.Dir("src"); Source(srcDir);
                var s = SetOf(kind, srcDir);
                var marker = Path.Combine(env.Root, "post-command-ran.txt");
                if (kind == "POSTCMD") s.PostCommands = new List<string> { Environment.OSVersion.Platform == PlatformID.Win32NT ? "cmd /c echo x > \"" + marker + "\"" : "touch \"" + marker + "\"" };
                // made with the switch off (the server still took such sets): the set reaches the agent in its profile
                var session = app.Interactive(Pw, null);
                var set = BackupSetInfo.FromXml(XElement.Parse(CreateSet(session, s)["set"]));
                PilotOn(env);

                var e = Refused(() => app.Backup(set.Id), names);
                Refused(() => app.Backup(set.Id, "LOG"), names);
                Refused(() => app.RestoreTest(set.Id), names);
                Refused(() => app.RestoreFor(app.Interactive(Pw, null), set.Id, Pw), names);
                if (set.Engine == "RESTIC") Refused(() => app.Restic(set, Pw), names);
                if (kind == "LOCALCOPY") Refused(() => app.RestoreLocal(set, Pw), names);
                Assert.False(File.Exists(marker));   // the command never ran
                // nothing started on the server: no run, no history
                var setDir = Path.Combine(env.HomeA, "pilot", "files", set.Id);
                Assert.Empty(Directory.Exists(setDir) ? SetStore.OpenJobDirs(setDir).ToList() : new List<string>());
                Assert.False(Directory.Exists(Path.Combine(env.HomeA, "pilot", "logs", set.Id, "Backup")) && Directory.GetFiles(Path.Combine(env.HomeA, "pilot", "logs", set.Id, "Backup")).Length > 0);
                // a set inside the pilot on the same computer still runs
                var ok = app.CreateSet(app.Interactive(Pw, null), Pw, new BackupSetInfo { Name = "Files", Sources = { srcDir } });
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(ok.Id).Result);
            }
        }

        [Fact]
        public void TheAgentRemembersTheScope_ARestartedAgentRefusesBeforeItHearsFromTheServer()
        {
            using (var env = new Env())
            {
                env.CreateUser("pilot", Pw);
                var app = env.Agent("pilot", Pw);
                var set = BackupSetInfo.FromXml(XElement.Parse(CreateSet(app.Interactive(Pw, null), SetOf("RESTIC", env.Dir("src")))["set"]));
                PilotOn(env);
                app.Profile();   // the agent hears the scope once
                env.Api.Dispose();   // the server is gone (no internet)
                var again = new AgentApp(app.Home.Dir);
                Refused(() => again.Restic(set, Pw), "restic");
            }
        }

        // ------------------------------------------------------------------ data of a blocked set: kept byte for byte

        [Fact]
        public void ABackedUpSetMadeBlocked_IsKeptByteForByte_NotRunNotRetained_AndBackInThePilotItRestoresIdentical()
        {
            using (var env = new Env())
            {
                env.CreateUser("pilot", Pw);
                var app = env.Agent("pilot", Pw);
                var srcDir = env.Dir("src");
                var src = Source(srcDir);
                var set = app.CreateSet(app.Interactive(Pw, null), Pw, new BackupSetInfo { Name = "Documents", Sources = { srcDir }, Retention = new RetentionPolicy { Unit = "DAYS", Period = 1 } });
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);
                File.WriteAllBytes(Path.Combine(srcDir, "f1.bin"), Rnd(1234, 5));
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);
                // before the switch, the IT company added a command after the backup (BK-09)
                var admin = env.Admin();
                var d = admin.Call("GET", "/api/admin/users/pilot/sets/" + set.Id);
                var s = BackupSetInfo.FromXml(XElement.Parse(d["set"])); s.PostCommands.Add("echo done");
                admin.Call("POST", "/api/admin/users/pilot/sets/" + set.Id, new Msg().Set("set", s.ToXml(XElement.Parse(d["set"])).ToString(SaveOptions.DisableFormatting)).Set("version", d["version"]));
                var dataDir = Path.Combine(env.HomeA, "pilot", "files", set.Id);
                var before = Manifest(dataDir);
                Assert.NotEmpty(before);

                PilotOn(env);
                Refused(() => app.Backup(set.Id), "commands");
                var m = env.Api.Maintenance(DateTime.UtcNow.AddDays(400));   // its retention (1 day) would expire the older version
                Assert.Empty(m.List("errors"));
                Assert.Equal(before, Manifest(dataDir));
                var retLogs = Path.Combine(env.HomeA, "pilot", "logs", set.Id, "Retention");
                Assert.Contains(Directory.GetFiles(retLogs).SelectMany(File.ReadAllLines), l => l.Contains("not supported in this version"));

                // the IT company removes the command: the set is in the pilot again — its old backups restore identical
                d = admin.Call("GET", "/api/admin/users/pilot/sets/" + set.Id);
                s = BackupSetInfo.FromXml(XElement.Parse(d["set"])); s.PostCommands.Clear();
                admin.Call("POST", "/api/admin/users/pilot/sets/" + set.Id, new Msg().Set("set", s.ToXml(XElement.Parse(d["set"])).ToString(SaveOptions.DisableFormatting)).Set("version", d["version"]));
                var target = env.Dir("restored");
                var rest = app.RestoreFor(app.Interactive(Pw, null), set.Id, Pw);
                rest.Run(null, target, null, false);
                Assert.Equal("RESTORE_STOP_SUCCESS", rest.Result);
                SameAsSource(Directory.GetFiles(srcDir, "*", SearchOption.AllDirectories).ToDictionary(f => f, Sha), target);
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);
            }
        }

        // ------------------------------------------------------------------ the client window

        static Msg Call(ClientUi ui, string op, Msg body = null)
        {
            var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + ui.Port + "/api/" + op);
            req.Method = body == null ? "GET" : "POST"; req.Headers["X-Key"] = ui.Key; req.Proxy = null;
            if (body != null) { var b = body.ToBytes(); req.ContentType = "application/xml"; using (var s = req.GetRequestStream()) s.Write(b, 0, b.Length); }
            try { using (var r = (HttpWebResponse)req.GetResponse()) return Msg.Read(r.GetResponseStream()); }
            catch (WebException e) when (e.Response != null) { using (var r = (HttpWebResponse)e.Response) { var m = Msg.Read(r.GetResponseStream()); throw new AgentException((int)r.StatusCode, m["code"] ?? m["error"], m["message"]); } }
        }

        static ClientUi Start(AgentApp app) { for (int port = 19500 + new Random().Next(800); ; port++) try { return new ClientUi(app, port); } catch (HttpListenerException) { } }

        [Fact]
        public void TheClientWindow_KnowsThePilot_OffersNoRestic_AndRefusesABlockedType()
        {
            using (var env = new Env())
            {
                env.CreateUser("pilot", Pw);
                var app = env.Agent("pilot", Pw);
                var srcDir = env.Dir("src"); Source(srcDir);
                var mssql = BackupSetInfo.FromXml(XElement.Parse(CreateSet(app.Interactive(Pw, null), SetOf("MSSQL", srcDir))["set"]));
                PilotOn(env);
                var ui = Start(app);
                try
                {
                    var st = Call(ui, "state");
                    Assert.Equal("1", st["pilot"]);
                    Assert.Equal("0", st["restic"]);
                    var shown = st.List("sets").First(x => x["id"] == mssql.Id);
                    Assert.Contains("not supported in this version", shown["blocked"] ?? "");
                    Call(ui, "login", new Msg().Set("password", Pw));
                    var e = Assert.Throws<AgentException>(() => Call(ui, "addset", new Msg().Set("type", "MSSQL").Set("name", "SQL").Set("sources", "")));
                    Assert.Equal("SCOPE", e.Code); Assert.Contains("not supported in this version", e.Message);
                    Assert.Equal(1, app.Profile().Sets.Count);
                    var made = Call(ui, "addset", new Msg().Set("type", "FILE").Set("name", "Docs").Set("sources", srcDir));
                    Assert.Equal("", made["engine"] ?? "");
                    Assert.Equal("", app.Profile().Sets.First(x => x.Id == made["id"]).Engine);
                }
                finally { ui.Dispose(); }
            }
        }

        [Fact]
        public void TheClientWindow_WhereResticIsPresent_StillMakesFileSetsWithTheOwnEngine()
        {
            if (!ClientUi.ResticSupported) throw NotTested.Because("restic is not present on this machine (OB_RESTIC): without it the window never chooses restic anyway");
            using (var env = new Env())
            {
                env.CreateUser("pilot", Pw);
                var app = env.Agent("pilot", Pw);
                var srcDir = env.Dir("src"); Source(srcDir);
                var ui = Start(app);
                try
                {
                    Call(ui, "login", new Msg().Set("password", Pw));
                    Assert.Equal("RESTIC", Call(ui, "addset", new Msg().Set("type", "FILE").Set("name", "Before").Set("sources", srcDir))["engine"]);   // control
                    PilotOn(env);
                    var made = Call(ui, "addset", new Msg().Set("type", "FILE").Set("name", "Docs").Set("sources", srcDir));
                    Assert.Equal("", made["engine"] ?? "");
                    Assert.Equal("BS_STOP_SUCCESS", app.Backup(made["id"]).Result);
                }
                finally { ui.Dispose(); }
            }
        }
    }
}
