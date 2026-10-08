using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Xml.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// Owner decision A7: "Key recovery: the customer chooses at set-up; default 'keep a recovery copy', with clear disclosure.
    /// The raw key is never handed to a device token." AZF-1 (authorization oracle): GET /api/sets/{copy}/sharedkey handed the
    /// parent set's raw key to any device token of the customer (and created the copy's empty store on the way).
    /// SET-020 keeps working: the copy on another computer receives the key at the customer's interactive sign-in there.
    /// </summary>
    public class KeyRecoveryA7Tests
    {
        const string Pw = "Customer-Pass-1";

        static Msg Call(ClientUi ui, string op, Msg body = null, string query = null)
        {
            var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + ui.Port + "/api/" + op + (query == null ? "" : "?" + query));
            req.Method = body == null ? "GET" : "POST"; req.Headers["X-Key"] = ui.Key; req.Proxy = null;
            if (body != null) { var b = body.ToBytes(); req.ContentType = "application/xml"; using (var s = req.GetRequestStream()) s.Write(b, 0, b.Length); }
            try { using (var r = (HttpWebResponse)req.GetResponse()) return Msg.Read(r.GetResponseStream()); }
            catch (WebException e) when (e.Response != null) { using (var r = (HttpWebResponse)e.Response) { var m = Msg.Read(r.GetResponseStream()); throw new AgentException((int)r.StatusCode, m["code"], m["message"]); } }
        }

        static ClientUi Start(AgentApp app)
        {
            for (int port = 19500 + new Random().Next(400); ; port++) try { return new ClientUi(app, port); } catch (HttpListenerException) { }
        }

        static string ServerKeyFile(Env env, string login, string setId) { return Path.Combine(env.HomeA, login, "db", "keys", setId + ".bin"); }

        /// <summary>A set on computer A (key recovery on, the default) and its copy, added by the IT company, on computer B.</summary>
        static string CopyOnB(Env env, AgentApp a, AgentApp b, out BackupSetInfo set, string keyType = "DEFAULT", bool keyRecovery = true)
        {
            var src = env.Dir("src-a"); File.WriteAllText(Path.Combine(src, "x.txt"), "x");
            set = a.CreateSet(a.Interactive(Pw, null), Pw, new BackupSetInfo { Name = "Files", Sources = { src } }, keyType, null, keyRecovery);
            var copyId = env.Admin().Call("POST", "/api/admin/users/acme/sets/" + set.Id + "/addcomputer", new Msg().Set("computer", b.Home.Computer))["id"];
            var srcB = env.Dir("src-b"); File.WriteAllText(Path.Combine(srcB, "y.txt"), "y");
            var cs = BackupSetInfo.FromXml(XElement.Parse(env.Admin().Call("GET", "/api/admin/users/acme/sets/" + copyId)["set"]));
            cs.Sources.Clear(); cs.Sources.Add(srcB);
            env.Admin().Call("POST", "/api/admin/users/acme/sets/" + copyId, new Msg().Set("set", cs.ToXml().ToString()));
            return copyId;
        }

        // ------------------------------------------------------------------ AZF-1

        [Fact]
        public void SharedKey_DeviceTokenRefused_NothingCreated_InteractiveSignInGetsIt()
        {
            using (var env = new Env())
            {
                env.CreateUser("acme", Pw);
                var a = env.Agent("acme", Pw, name: "a");
                var b = env.Agent("acme", Pw, name: "b");
                BackupSetInfo set; var copyId = CopyOnB(env, a, b, out set);
                Assert.True(File.Exists(ServerKeyFile(env, "acme", set.Id)));                         // the recovery copy exists (default)
                var copyStore = Path.Combine(env.HomeA, "acme", "files", copyId);

                // any device token of the customer (B's, A's): refused as "needs an interactive sign-in", nothing written
                foreach (var dev in new[] { b, a })
                {
                    var e = Assert.Throws<AgentException>(() => dev.DeviceClient().Call("GET", "/api/sets/" + copyId + "/sharedkey"));
                    Assert.Equal(401, e.Status); Assert.Equal("SESSION", e.Code);
                    Assert.False(Directory.Exists(copyStore), "the refused call created the copy's store");
                }

                // the customer's interactive sign-in (password + code) on B: the key of the first set
                var raw = Convert.FromBase64String(b.Interactive(Pw, null).Call("GET", "/api/sets/" + copyId + "/sharedkey")["key"]);
                Assert.Equal(a.Home.LoadKey(set.Id).CheckValue(), KeySet.FromRaw(raw).CheckValue());
            }
        }

        [Fact]
        public void CopyOnAnotherComputer_ScheduledRunGetsNoKey_SignInOnceInTheWindow_ThenItBacksUpUnattended()
        {
            using (var env = new Env())
            {
                env.CreateUser("acme", Pw);
                var a = env.Agent("acme", Pw, name: "a");
                var b = env.Agent("acme", Pw, name: "b");
                BackupSetInfo set; var copyId = CopyOnB(env, a, b, out set);

                // the scheduled run (device token) cannot get the key: it says what to do, nothing is backed up
                var e = Assert.Throws<AgentException>(() => b.Backup(copyId));
                Assert.Equal("NO_KEY", e.Code);
                Assert.Contains("sign in", e.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Null(b.Home.LoadKey(copyId));

                // the customer signs in once in the program on B: the copies of this computer receive their key there
                var ui = Start(b);
                try
                {
                    var r = Call(ui, "login", new Msg().Set("password", Pw));
                    Assert.Equal("1", r["ok"]);
                    Assert.Equal("1", r["keysReceived"]); Assert.Equal("0", r["keysMissing"]);
                }
                finally { ui.Dispose(); }
                Assert.NotNull(b.Home.LoadKey(copyId));

                // from now on the scheduled runs need no sign-in
                var run = b.Backup(copyId);
                Assert.Equal("BS_STOP_SUCCESS", run.Result); Assert.Equal(1, run.New);
            }
        }

        [Fact]
        public void CopyOfASetWithoutRecoveryCopy_PasswordKey_SignInDerivesIt_RandomKey_ToldMissing()
        {
            using (var env = new Env())
            {
                env.CreateUser("acme", Pw);
                var a = env.Agent("acme", Pw, name: "a");
                var b = env.Agent("acme", Pw, name: "b");
                BackupSetInfo set; var copyId = CopyOnB(env, a, b, out set, "PASSWORD", false);
                Assert.False(File.Exists(ServerKeyFile(env, "acme", set.Id)));
                var s = b.Interactive(Pw, null);
                Assert.Equal("NO_KEY", Assert.Throws<AgentException>(() => s.Call("GET", "/api/sets/" + copyId + "/sharedkey")).Code);   // nothing kept: nothing handed
                var missing = b.KeysForCopies(s, Pw);
                Assert.Empty(missing);                                                                   // derived here from the password
                Assert.Equal("BS_STOP_SUCCESS", b.Backup(copyId).Result);
            }
            using (var env = new Env())
            {
                env.CreateUser("acme", Pw);
                var a = env.Agent("acme", Pw, name: "a");
                var b = env.Agent("acme", Pw, name: "b");
                BackupSetInfo set; var copyId = CopyOnB(env, a, b, out set, "DEFAULT", false);
                Assert.Equal(new[] { copyId }, b.KeysForCopies(b.Interactive(Pw, null), Pw).ToArray());
                Assert.Equal("NO_KEY", Assert.Throws<AgentException>(() => b.Backup(copyId)).Code);
            }
        }

        // ------------------------------------------------------------------ A7: the choice at set-up

        [Fact]
        public void NewSet_DefaultKeepsARecoveryCopy_NoMeansNoCopy_AndTheServerHoldsToTheChoice()
        {
            using (var env = new Env())
            {
                env.CreateUser("acme", Pw);
                var app = env.Agent("acme", Pw, name: "a");
                var s = app.Interactive(Pw, null);
                var keep = app.CreateSet(s, Pw, new BackupSetInfo { Name = "Kept", Sources = { env.Dir("k") } });
                var none = app.CreateSet(s, Pw, new BackupSetInfo { Name = "Not kept", Sources = { env.Dir("n") } }, "DEFAULT", null, false);
                Assert.True(keep.KeyRecovery); Assert.False(none.KeyRecovery);
                Assert.True(File.Exists(ServerKeyFile(env, "acme", keep.Id)));
                Assert.False(File.Exists(ServerKeyFile(env, "acme", none.Id)));
                Assert.Equal("Y", (string)env.Api.UserStore.LoadProfile("acme").FindSet(keep.Id).Attribute("KEY_RECOVERY"));
                Assert.Equal("N", (string)env.Api.UserStore.LoadProfile("acme").FindSet(none.Id).Attribute("KEY_RECOVERY"));
                Assert.Equal("NO_KEY", Assert.Throws<AgentException>(() => env.Admin().Call("GET", "/api/admin/keys/acme/" + none.Id)).Code);

                // a key sent anyway (an older program) is not kept for a set whose customer said no
                Assert.Equal("N", s.Call("POST", "/api/sets/" + none.Id + "/key", new Msg().Set("key", Convert.ToBase64String(app.Home.LoadKey(none.Id).ToRaw())))["saved"]);
                Assert.False(File.Exists(ServerKeyFile(env, "acme", none.Id)));

                // the choice stays as made: an edit of the set (customer or IT company) does not turn it around
                var x = BackupSetInfo.FromXml(env.Api.UserStore.LoadProfile("acme").FindSet(none.Id)); x.KeyRecovery = true; x.Hour = 5;
                env.Admin().Call("POST", "/api/admin/users/acme/sets/" + none.Id, new Msg().Set("set", x.ToXml().ToString()));
                s.Call("POST", "/api/sets/" + none.Id + "/settings", new Msg().Set("set", x.ToXml().ToString()));
                Assert.Equal("N", (string)env.Api.UserStore.LoadProfile("acme").FindSet(none.Id).Attribute("KEY_RECOVERY"));
                Assert.Equal(5, app.Sets().Single(y => y.Id == none.Id).Hour);
            }
        }

        [Fact]
        public void LocalApi_TellsTheWindowTheChoiceAndItsDisclosure_InEnglishAndHebrew_AndAddSetHonoursIt()
        {
            using (var env = new Env())
            {
                env.CreateUser("acme", Pw);
                var app = env.Agent("acme", Pw, name: "a");
                var ui = Start(app);
                try
                {
                    var info = Call(ui, "keyrecovery");
                    Assert.Equal("1", info["default"]); Assert.Equal("1", info["allowed"]);
                    Assert.Contains("recovery copy", info["keepLabel"]);
                    Assert.Contains("could", info["keepText"]);                         // says plainly that the provider could read them
                    Assert.Contains("nobody", info["noneText"]);                         // and that without it a lost key is final
                    var he = Call(ui, "keyrecovery", null, "lang=he");
                    Assert.NotEqual(info["keepText"], he["keepText"]);
                    Assert.Matches("[֐-׿]", he["keepText"]); Assert.Matches("[֐-׿]", he["noneText"]); Assert.Matches("[֐-׿]", he["keepLabel"]); Assert.Matches("[֐-׿]", he["noneLabel"]);
                    foreach (var k in new[] { "title", "keepLabel", "keepText", "noneLabel", "noneText", "offText" })
                        Assert.False(string.IsNullOrEmpty(info[k]), k);

                    Assert.Equal("LOGIN", Assert.Throws<AgentException>(() => Call(ui, "addset", new Msg().Set("name", "x").Set("sources", env.Dir("x")))).Code);
                    Call(ui, "login", new Msg().Set("password", Pw));
                    var kept = Call(ui, "addset", new Msg().Set("name", "Kept").Set("sources", env.Dir("k")));
                    Assert.Equal("1", kept["keyRecovery"]);
                    Assert.True(File.Exists(ServerKeyFile(env, "acme", kept["id"])));
                    var none = Call(ui, "addset", new Msg().Set("name", "Mine only").Set("sources", env.Dir("n")).Set("keyRecovery", "0"));
                    Assert.Equal("0", none["keyRecovery"]);
                    Assert.False(File.Exists(ServerKeyFile(env, "acme", none["id"])));
                    var st = Call(ui, "state");
                    Assert.Equal("1", st.List("sets").Single(x => x["id"] == kept["id"])["keyRecovery"]);
                    Assert.Equal("0", st.List("sets").Single(x => x["id"] == none["id"])["keyRecovery"]);

                    // the IT company keeps no recovery copies: the window is told, and a set made says so
                    env.Api.UserStore.LoadProfile("acme");
                    var p = env.Api.UserStore.LoadProfile("acme"); p.SetAttr("SAVE_ENCRYPT_KEY", "N"); env.Api.UserStore.SaveProfile("acme", p);
                    Assert.Equal("0", Call(ui, "keyrecovery")["allowed"]);
                    var off = Call(ui, "addset", new Msg().Set("name", "Off").Set("sources", env.Dir("o")));
                    Assert.Equal("0", off["keyRecovery"]);
                    Assert.False(File.Exists(ServerKeyFile(env, "acme", off["id"])));
                }
                finally { ui.Dispose(); }
            }
        }
    }
}
