using System;
using System.IO;
using System.Linq;
using System.Net;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>SRC-030 / SET-040: the client software — its folder tree, a new backup with skipped folders, changing a backup within the rights.</summary>
    public class ClientUiTests
    {
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
            for (int port = 18700 + new Random().Next(800); ; port++) try { return new ClientUi(app, port); } catch (HttpListenerException) { }
        }

        [Fact]
        public void SignInScreen_ChecksTheServer_ConnectsThisComputer_ThenNeverAgain()
        {
            // SETUP-C50: the installation only installs; the program's first screen connects (server, user name, password)
            using (var env = new Env())
            {
                env.CreateUser("acme", "Customer-Pass-1");
                var app = new AgentApp(env.Dir("pc-new"));
                var ui = Start(app);
                try
                {
                    Assert.Equal("0", Call(ui, "state")["registered"]);
                    var chk = Call(ui, "server-check", new Msg().Set("server", env.Url).Set("lang", "en"));
                    Assert.Equal(env.Url.TrimEnd('/'), chk["server"]);
                    Assert.Equal("0", chk["confirm"]);
                    Assert.ThrowsAny<AgentException>(() => Call(ui, "server-check", new Msg().Set("server", "not an address")));
                    Assert.ThrowsAny<AgentException>(() => Call(ui, "connect", new Msg().Set("server", chk["server"]).Set("mode", "existing").Set("login", "acme").Set("password", "wrong-password")));
                    Assert.Equal("0", Call(ui, "state")["registered"]);
                    Assert.Equal("acme", Call(ui, "connect", new Msg().Set("server", chk["server"]).Set("mode", "existing").Set("login", "acme").Set("password", "Customer-Pass-1").Set("lang", "en"))["login"]);
                    var st = Call(ui, "state");
                    Assert.Equal("1", st["registered"]); Assert.Equal("acme", st["login"]); Assert.Equal("1", st["session"]);   // the first backup can be added at once
                    var again = Assert.Throws<AgentException>(() => Call(ui, "connect", new Msg().Set("server", chk["server"]).Set("login", "acme").Set("password", "Customer-Pass-1")));
                    Assert.Equal("CONNECTED", again.Code);
                    Assert.Equal("CONNECTED", Assert.Throws<AgentException>(() => Call(ui, "server-check", new Msg().Set("server", env.Url))).Code);
                }
                finally { ui.Dispose(); }
            }
        }

        [Fact]
        public void SignInScreen_NewCustomer_ContractShownOnlyAtSignup_ThenClosed()
        {
            // SIGNUP-020 (owner: "a new user cannot be connected"): the contract's "shown at sign-up" was read as "no new accounts"
            using (var env = new Env())
            {
                var admin = env.Admin();
                admin.Call("POST", "/api/admin/contract", new Msg().Set("install", 0).Set("signup", 1).Add("texts", new Msg().Set("lang", "en").Set("text", "Service agreement")));
                var ui = Start(new AgentApp(env.Dir("pc-signup")));
                try
                {
                    var chk = Call(ui, "server-check", new Msg().Set("server", env.Url).Set("lang", "en"));
                    Assert.Equal("1", chk["signupOpen"]);
                    Assert.Equal("", chk["contract"] ?? "");                 // an existing customer: not at installation
                    Assert.Equal("Service agreement", chk["contractNew"]);    // a new customer reads it
                    var body = new Msg().Set("server", chk["server"]).Set("mode", "new").Set("company", "Dental Clinic").Set("email", "office@clinic.example")
                        .Set("login", "clinic").Set("password", "Clinic-Pass-1").Set("lang", "en").Set("contractVersion", chk["contractVersion"]);
                    Assert.Equal("CONTRACT", Assert.Throws<AgentException>(() => Call(ui, "connect", new Msg().Set("accept", "0").Set("server", body["server"]).Set("mode", "new").Set("company", "Dental Clinic").Set("email", "office@clinic.example").Set("login", "clinic").Set("password", "Clinic-Pass-1"))).Code);
                    Assert.Equal("clinic", Call(ui, "connect", body.Set("accept", "1"))["login"]);
                    Assert.Equal("1", Call(ui, "state")["registered"]);
                    Assert.Equal("1", env.Api.UserStore.LoadProfile("clinic").Get("CONTRACT_VERSION"));
                }
                finally { ui.Dispose(); }
                admin.Call("POST", "/api/admin/contract", new Msg().Set("signupOpen", 0));
                var ui2 = Start(new AgentApp(env.Dir("pc-closed")));
                try { Assert.Equal("0", Call(ui2, "server-check", new Msg().Set("server", env.Url).Set("lang", "en"))["signupOpen"]); }
                finally { ui2.Dispose(); }
            }
        }

        [Fact]
        public void Tree_NewBackupWithSkippedFolders_ChangeWithinRights_OnlyThisComputersSets()
        {
            using (var env = new Env())
            {
                env.CreateUser("acme", "Customer-Pass-1");
                var app = env.Agent("acme", "Customer-Pass-1", name: "pc1");
                var other = env.Agent("acme", "Customer-Pass-1", name: "pc2");
                var src = env.Dir("src"); Directory.CreateDirectory(Path.Combine(src, "Docs")); Directory.CreateDirectory(Path.Combine(src, "Temp"));
                File.WriteAllText(Path.Combine(src, "Docs", "a.txt"), "a"); File.WriteAllText(Path.Combine(src, "Temp", "b.tmp"), "b");
                other.CreateSet(other.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "Other PC", Sources = { env.Dir("o") } });
                var ui = Start(app);
                try
                {
                    // the tree: the drives, then any folder's sub-folders, live
                    Assert.NotEmpty(Call(ui, "dirs").List("dirs"));
                    var subs = Call(ui, "dirs", null, "path=" + Uri.EscapeDataString(src)).List("dirs").Select(d => d["path"]).ToList();
                    Assert.Equal(new[] { Path.Combine(src, "Docs"), Path.Combine(src, "Temp") }, subs);
                    Assert.Equal("1", Call(ui, "dirs", null, "path=" + Uri.EscapeDataString(Path.Combine(src, "missing")))["denied"]);

                    // a new backup with a skipped folder inside the chosen one
                    Assert.Throws<AgentException>(() => Call(ui, "addset", new Msg().Set("type", "FILE").Set("name", "Docs").Set("sources", src)));   // signed in first
                    Call(ui, "login", new Msg().Set("password", "Customer-Pass-1"));
                    Call(ui, "addset", new Msg().Set("type", "FILE").Set("name", "Docs").Set("sources", src).Set("exclude", Path.Combine(src, "Temp")).Set("hour", "21"));
                    var st = Call(ui, "state");
                    var mine = st.List("sets").Where(s => s["mine"] == "1").ToList();
                    Assert.Single(mine); Assert.Equal("Docs", mine[0]["name"]); Assert.Equal(Path.Combine(src, "Temp"), mine[0]["skip"]);
                    Assert.Contains(st.List("sets"), s => s["mine"] == "0" && s["name"] == "Other PC");   // the other computer's set: for restore only
                    var set = app.Sets().Single(s => s.Name == "Docs");
                    var r = app.Backup(set.Id);
                    Assert.Equal("BS_STOP_SUCCESS", r.Result); Assert.Equal(1, r.New);                        // Temp skipped

                    // change: other folders and time
                    Call(ui, "editset", new Msg().Set("set", set.Id).Set("sources", Path.Combine(src, "Docs")).Set("exclude", "").Set("hour", "6").Set("minute", "30"));
                    var now = app.Sets().Single(s => s.Id == set.Id);
                    Assert.Equal(new[] { Path.Combine(src, "Docs") }, now.Sources); Assert.Empty(now.Deselected); Assert.Equal(6, now.Hour); Assert.Equal(30, now.Minute);

                    // the IT company locks the folders: the time still changes, the folders are refused
                    env.Admin().Call("POST", "/api/admin/users/acme/details", new Msg().Set("can_edit_sources", 0));
                    Assert.Equal("0", Call(ui, "state")["canSources"]);
                    Assert.Throws<AgentException>(() => Call(ui, "editset", new Msg().Set("set", set.Id).Set("sources", src).Set("exclude", "").Set("hour", "6").Set("minute", "30")));
                    Call(ui, "editset", new Msg().Set("set", set.Id).Set("hour", "7").Set("minute", "0"));
                    now = app.Sets().Single(s => s.Id == set.Id);
                    Assert.Equal(7, now.Hour); Assert.Equal(new[] { Path.Combine(src, "Docs") }, now.Sources);

                    // help: a call to the IT company from the software, with this computer's name
                    var h = Call(ui, "help", new Msg().Set("subject", "Restore of an old file").Set("description", "From last week").Set("set", set.Id));
                    Assert.Equal("1", h["sent"]);
                    Assert.Contains(Call(ui, "help").List("tickets"), t => t["subject"] == "Restore of an old file" && t["status"] == "New");
                    var adm = env.Admin().Call("GET", "/api/admin/tickets?scope=all").List("tickets").Single(t => t["subject"] == "Restore of an old file");
                    Assert.Equal("acme", adm["login"]); Assert.Equal(app.Home.Computer, adm["computer"]);
                    env.Admin().Call("POST", "/api/admin/ticketsettings", new Msg().Set("clientcalls", 0));
                    Assert.Equal("1", Call(ui, "help")["off"]);
                }
                finally { ui.Dispose(); }
            }
        }
    }
}
