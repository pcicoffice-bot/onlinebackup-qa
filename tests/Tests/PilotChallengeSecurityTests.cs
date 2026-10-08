using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
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
    /// Adversarial challenge of the pilot security tests (AU-02 / AU-05 / AU-06 / AU-07) that passed. The challenged tests tried
    /// a few routes each (5 routes of the admin API for a reseller; one revoked token on /api/profile; a wrong key only through
    /// the check value). These tests try EVERY route of the admin API and of the customers' API with the wrong reseller, the
    /// wrong customer, the wrong kind of token, a revoked token and an expired sign-in, and look for secrets everywhere the
    /// server writes or answers. Oracles: exact HTTP status classes, SHA-256 of every file of the other customer before and
    /// after, the strings that must never appear (typed passwords, two-step secrets, device secrets, other customers' names).
    /// Everything is local (127.0.0.1 / localhost).
    /// </summary>
    [Collection("Guard")]   // Guard's state is one per process: these classes never run at the same time
    public class PilotChallengeSecurityTests
    {
        internal sealed class Ans { public int Status; public string Text; public Msg Body; }
        static readonly HttpClient http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(120) };

        /// <summary>One raw request: exactly the headers given (X-Session / X-Device), nothing retried.</summary>
        internal static Ans Http(string baseUrl, string method, string path, string session = null, string device = null, Msg body = null)
        {
            var req = new HttpRequestMessage(new HttpMethod(method), baseUrl.TrimEnd('/') + path);
            if (session != null) req.Headers.TryAddWithoutValidation("X-Session", session);
            if (device != null) req.Headers.TryAddWithoutValidation("X-Device", device);
            if (body != null) req.Content = new ByteArrayContent(body.ToBytes());
            else if (method == "POST" || method == "PUT") req.Content = new ByteArrayContent(new Msg().ToBytes());
            using (var resp = http.SendAsync(req).GetAwaiter().GetResult())
            {
                var a = new Ans { Status = (int)resp.StatusCode, Text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult() };
                try { a.Body = Msg.Parse(a.Text); } catch (Exception) { a.Body = new Msg(); }
                return a;
            }
        }

        internal static string Sha(string file) { using (var h = SHA256.Create()) using (var s = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) return Bytes.Hex(h.ComputeHash(s)); }
        internal static Dictionary<string, string> Tree(string dir)
        {
            return Directory.GetFiles(dir, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).ToDictionary(f => f.Substring(dir.Length), Sha);
        }
        static string E(string s) { return Uri.EscapeDataString(s); }
        /// <summary>Every file under a folder except those inside one sub-folder, with its SHA-256.</summary>
        static Dictionary<string, string> Outside(string dir, string except)
        {
            return Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Where(f => !f.StartsWith(except + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                .OrderBy(f => f, StringComparer.Ordinal).ToDictionary(f => f.Substring(dir.Length), Sha);
        }

        static BackupSetInfo SetWithBackup(Env env, AgentApp app, string password, string name, string fileName, string content)
        {
            var src = env.Dir("src-" + name + "-" + Guid.NewGuid().ToString("N").Substring(0, 4));
            File.WriteAllText(Path.Combine(src, fileName), content);
            var set = app.CreateSet(app.Interactive(password, null), password, new BackupSetInfo { Name = name, Sources = { src } });
            Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);
            return set;
        }

        // ================================================================== AU-06: every admin route, the wrong reseller

        [Fact]
        public void AU06_AResellersAdministrator_EveryAdminRoute_OnAnotherResellersOrTheSystemsCustomer_IsRefused_NothingChanges()
        {
            using (var env = new Env())
            {
                var sys = env.Admin();
                sys.Call("POST", "/api/admin/vendors", new Msg().Set("id", "acme-it").Set("name", "Acme IT"));
                sys.Call("POST", "/api/admin/vendors/acme-it/admins", new Msg().Set("login", "acme-admin").Set("password", "Acme-Admin-Pass-1"));
                sys.Call("POST", "/api/admin/vendors", new Msg().Set("id", "beta").Set("name", "Beta").Set("brandPRODUCT", "BetaBrandW3"));
                sys.Call("POST", "/api/admin/vendors/beta/admins", new Msg().Set("login", "beta-admin").Set("password", "Beta-Admin-Pass-1"));
                var acme = TestAuth.Admin(env.Url, "acme-admin", "Acme-Admin-Pass-1", 0);
                var beta = TestAuth.Admin(env.Url, "beta-admin", "Beta-Admin-Pass-1", 0);
                acme.Call("POST", "/api/admin/users", new Msg().Set("login", "acme-c1").Set("password", "Customer-Pass-1").Set("alias", "a").Set("quotaGB", 1).Set("email", "a@cust.invalid"));
                beta.Call("POST", "/api/admin/users", new Msg().Set("login", "beta-c1").Set("password", "Customer-Pass-1").Set("alias", "BetaAliasZ7").Set("quotaGB", 1).Set("email", "b@cust.invalid"));
                env.CreateUser("direct", "Customer-Pass-1");
                var bapp = env.Agent("beta-c1", "Customer-Pass-1", name: "betapc");
                var bset = SetWithBackup(env, bapp, "Customer-Pass-1", "BetaSetNameQ4", "BetaFileP2.txt", "beta's data");
                var dapp = env.Agent("direct", "Customer-Pass-1", name: "directpc");
                var dset = SetWithBackup(env, dapp, "Customer-Pass-1", "DirectSetNameR8", "DirectFileJ5.txt", "the system's customer's data");
                var ticket = sys.Call("POST", "/api/admin/tickets", new Msg().Set("login", "beta-c1").Set("subject", "BetaTicketK2").Set("priority", "Normal").Set("status", "New"))["id"];
                Assert.False(string.IsNullOrEmpty(ticket));
                var secrets = new[] { "BetaAliasZ7", "BetaSetNameQ4", "BetaFileP2", "BetaTicketK2", "DirectSetNameR8", "DirectFileJ5", "BetaBrandW3" };

                var watched = new[] { Path.Combine(env.HomeA, "beta-c1"), Path.Combine(env.HomeA, "direct") };
                var before = watched.Select(Tree).ToList();
                var sysXml = Path.Combine(env.SystemHome, "conf", "system.xml"); var usersXml = Path.Combine(env.SystemHome, "conf", "users.xml");
                var sysBefore = Sha(sysXml); var usersBefore = Sha(usersXml);

                var wrong = new List<string>();
                Action<string, string, Msg> refused = (m, p, b) =>
                {
                    var a = Http(env.Url, m, p, session: acme.Session, body: b);
                    // Q-AU06: on Windows, http.sys answers 400 itself to a URL holding %00 - the product never sees the request
                    // (run qa-shards-22: 400 with no product error). Only that exact case: a 400 the product sent, or any other
                    // status, is still wrong; and what follows (nothing shown, nothing changed on disk) is checked as before.
                    var beforeTheProduct = a.Status == 400 && p.Contains("%00") && string.IsNullOrEmpty(a.Body["error"]) && !a.Text.Contains("\"error\"") && !a.Text.Contains("<error");
                    if (a.Status != 403 && a.Status != 404 && !beforeTheProduct) wrong.Add(m + " " + p + " -> " + a.Status + " " + a.Body["error"]);
                    foreach (var s in secrets) if (a.Text.Contains(s)) wrong.Add(m + " " + p + " showed " + s);
                };
                var set = new Msg().Set("set", "<BACKUP_SET ID=\"1\" NAME=\"x\"><DAILY_SCHEDULE HOUR=\"1\" MINUTE=\"0\"/></BACKUP_SET>");
                // the targets: another reseller's customer, the system's own customer, the same name in capitals, encoded names
                foreach (var t in new[] { "beta-c1", "direct", "BETA-C1", "beta%2Dc1", "beta-c1%2F", "acme-c1%2F..%2Fbeta-c1", "..%2Fbeta-c1", "beta-c1%00" })
                    foreach (var sid in new[] { bset.Id, dset.Id })
                    {
                        refused("GET", "/api/admin/users/" + t + "/sets/" + sid, null);
                        refused("POST", "/api/admin/users/" + t + "/sets/" + sid, set);
                        refused("GET", "/api/admin/users/" + t + "/sets/" + sid + "/runs", null);
                        foreach (var a in new[] { "run", "stop", "addcomputer", "removecomputer", "move" })
                            refused("POST", "/api/admin/users/" + t + "/sets/" + sid + "/" + a, new Msg().Set("computer", "PC-betapc"));
                        refused("GET", "/api/admin/keys/" + t + "/" + sid, null);
                        refused("POST", "/api/admin/users/" + t + "/untrash?set=" + sid, new Msg());
                        refused("POST", "/api/admin/users/" + t + "/aidiagnose", new Msg().Set("set", sid).Set("cat", "Backup").Set("file", "x.log"));
                        refused("POST", "/api/admin/users/" + t + "/delete", new Msg().Set("set", sid));
                    }
                foreach (var t in new[] { "beta-c1", "direct", "BETA-C1", "beta-c1%2F" })
                {
                    refused("GET", "/api/admin/users/" + t + "/computers", null);
                    refused("POST", "/api/admin/users/" + t + "/computers/disconnect", new Msg().Set("computer", "PC-betapc"));
                    refused("POST", "/api/admin/users/" + t + "/computers/move", new Msg().Set("computer", "PC-betapc").Set("target", "acme-c1"));
                    refused("GET", "/api/admin/users/" + t + "/folders?computer=PC-betapc", null);
                    refused("GET", "/api/admin/users/" + t + "/compliance", null);
                    refused("POST", "/api/admin/users/" + t + "/browse", new Msg().Set("computer", "PC-betapc").Set("path", "C:\\"));
                    foreach (var a in new[] { "unlock", "delete", "resettotp", "unfreeze" }) refused("POST", "/api/admin/users/" + t + "/" + a, new Msg());
                    refused("POST", "/api/admin/users/" + t + "/contacts", new Msg().Add("contacts", new Msg().Set("name", "x").Set("email", "x@evil.invalid")));
                    refused("POST", "/api/admin/users/" + t + "/details", new Msg().Set("alias", "taken").Set("can_add_sets", "1"));
                    refused("POST", "/api/admin/users/" + t + "/security", new Msg().Set("allowedIps", "203.0.113.1").Set("requireTotp", "0"));
                    refused("POST", "/api/admin/users/" + t + "/quota", new Msg().Set("quotaGB", "999"));
                }
                // a customer of its own moved to another reseller's customer, or the system's
                refused("POST", "/api/admin/users/acme-c1/computers/move", new Msg().Set("computer", "PC-x").Set("target", "direct"));
                // the system's own pages
                foreach (var p in new[] { "homes", "guard", "configbackup", "staff", "settings", "logs?cat=System", "logs?cat=Backup&login=beta-c1&set=" + bset.Id, "license", "time", "contract",
                                          "defaults", "templates", "deletes", "recycle", "vendors", "ticketsettings", "update/source", "tickets/deleted" })
                    refused("GET", "/api/admin/" + p, null);
                foreach (var p in new[] { "guard", "guard/unblock", "guard/block", "staff", "staff/admin/resettotp", "staff/beta-admin/delete", "settings", "rebuild", "verify", "maintenance", "testmail", "replicate", "aitest",
                                          "update", "update/source", "vendors", "vendors/beta/admins", "deletes/settings", "time", "contract", "defaults", "templates", "license", "configbackup/now", "ticketsettings" })
                    refused("POST", "/api/admin/" + p, new Msg().Set("login", "beta-c1").Set("set", bset.Id).Set("ip", "203.0.113.9").Set("password", "Taken-Over-1").Set("dual", "0").Set("id", "beta").Set("disabled", "1"));
                // service calls of another reseller's customer: not found, never shown, never changed
                refused("GET", "/api/admin/tickets/" + ticket, null);
                refused("POST", "/api/admin/tickets/" + ticket + "/note", new Msg().Set("text", "acme was here"));
                refused("POST", "/api/admin/tickets/" + ticket + "/delete", new Msg().Set("revision", "0"));
                refused("POST", "/api/admin/tickets", new Msg().Set("id", ticket).Set("subject", "hijacked").Set("login", "acme-c1"));
                refused("POST", "/api/admin/tickets", new Msg().Set("login", "beta-c1").Set("subject", "new call on beta's customer"));
                refused("POST", "/api/admin/tickets", new Msg().Set("login", "direct").Set("subject", "new call on the system's customer"));
                Assert.True(wrong.Count == 0, wrong.Count + " not refused:\n" + string.Join("\n", wrong));   // every one, in full (Assert.Empty cut the status off)

                // the pages a reseller may open show only its own (none of the other customers' names or data)
                foreach (var p in new[] { "tickets?scope=all", "tickets?scope=all&login=beta-c1", "tasks?hours=744", "dashboard", "checks", "live", "users", "me" })
                {
                    var a = Http(env.Url, "GET", "/api/admin/" + p, session: acme.Session);
                    Assert.Equal(200, a.Status);
                    foreach (var s in secrets) Assert.False(a.Text.Contains(s), p + " showed " + s);
                    Assert.False(a.Text.Contains("beta-c1") || a.Text.Contains(">direct<") || a.Text.Contains("\"direct\""), p + " named another customer: " + a.Text.Substring(0, Math.Min(300, a.Text.Length)));
                }
                // one action on many customers: each foreign one refused on its own row, nothing done
                var bulk = Http(env.Url, "POST", "/api/admin/bulk", session: acme.Session, body: new Msg().Set("action", "quota").Set("quotaGB", "500")
                    .Add("logins", new Msg().Set("login", "beta-c1")).Add("logins", new Msg().Set("login", "direct")).Add("logins", new Msg().Set("login", "BETA-C1")));
                Assert.Equal(200, bulk.Status);
                Assert.Equal("0", bulk.Body["done"]);
                Assert.All(bulk.Body.List("results"), r => Assert.Equal("0", r["ok"]));

                // nothing of the others changed (SHA-256 of every file), nor the server's settings or customer list
                for (int i = 0; i < watched.Length; i++) Assert.Equal(before[i], Tree(watched[i]));
                Assert.Equal(sysBefore, Sha(sysXml));
                Assert.Equal(usersBefore, Sha(usersXml));
                Assert.Equal("1", sys.Call("GET", "/api/admin/tickets/" + ticket)["id"] == ticket ? "1" : "0");

                // a new customer "for beta" made by acme is acme's (the vendor in the body is ignored for a reseller)
                acme.Call("POST", "/api/admin/users", new Msg().Set("login", "acme-sneak").Set("password", "Customer-Pass-1").Set("alias", "s").Set("quotaGB", 1).Set("vendor", "beta").Set("email", "s@cust.invalid"));
                Assert.Equal("acme-it", Profile.Load(Path.Combine(env.HomeA, "acme-sneak", "db", "Profile.xml")).Get("OWNER"));
                // acme's branding change is acme's only
                acme.Call("POST", "/api/admin/brand", new Msg().Set("brandPRODUCT", "AcmeBrandV1"));
                Assert.Equal("BetaBrandW3", Vendors.Brand(env.Cfg, "beta", "PRODUCT", ""));
                Assert.Equal("AcmeBrandV1", Vendors.Brand(env.Cfg, "acme-it", "PRODUCT", ""));
                // beta's computer still backs up
                Assert.Equal("BS_STOP_SUCCESS", bapp.Backup(bset.Id).Result);
            }
        }

        // ================================================================== AU-07 / AU-06: every customer route, another customer's set or run

        [Fact]
        public void AU07_ACustomer_EveryRouteOfAnotherCustomersSetOrRun_IsNotFound_NothingThereChanges()
        {
            using (var env = new Env())
            {
                env.CreateUser("anna", "Customer-Pass-1"); env.CreateUser("bob", "Customer-Pass-2");
                var anna = env.Agent("anna", "Customer-Pass-1", name: "annapc"); var bob = env.Agent("bob", "Customer-Pass-2", name: "bobpc");
                var aset = SetWithBackup(env, anna, "Customer-Pass-1", "AnnaSet", "anna.txt", "anna's");
                var bset = SetWithBackup(env, bob, "Customer-Pass-2", "BobSetNameT6", "BobFileU3.txt", "bob's");
                // bob has a run open right now
                var begin = Http(env.Url, "GET", "/api/sets/" + bset.Id + "/begin", device: bob.Home.DeviceToken);
                Assert.Equal(200, begin.Status);
                var job = begin.Body["job"];
                var bobDir = Path.Combine(env.HomeA, "bob");
                var before = Tree(bobDir);
                var outside = Outside(env.HomeA, Path.Combine(env.HomeA, "anna"));
                var annaSession = anna.Interactive("Customer-Pass-1", null).Session;

                var wrong = new List<string>();
                foreach (var cred in new[] { "session", "device" })
                {
                    Func<string, string, Msg, Ans> call = (m, p, b) => cred == "session" ? Http(env.Url, m, p, session: annaSession, body: b) : Http(env.Url, m, p, device: anna.Home.DeviceToken, body: b);
                    foreach (var sid in new[] { bset.Id, "%2E%2E%2F" + bset.Id, bset.Id + "%2F..%2F" + aset.Id, ".." + "%5C" + bset.Id, " " + bset.Id, bset.Id + "%00" })
                    {
                        var routes = new List<Tuple<string, string, Msg>>
                        {
                            Tuple.Create("GET", "/api/sets/" + sid + "/points", (Msg)null),
                            Tuple.Create("GET", "/api/sets/" + sid + "/files", (Msg)null),
                            Tuple.Create("GET", "/api/sets/" + sid + "/object?loc=x&test=1", (Msg)null),
                            Tuple.Create("POST", "/api/sets/" + sid + "/key", new Msg().Set("key", Convert.ToBase64String(new byte[96]))),
                            Tuple.Create("POST", "/api/sets/" + sid + "/settings", new Msg().Set("set", "<BACKUP_SET/>")),
                            Tuple.Create("GET", "/api/sets/" + sid + "/sharedkey", (Msg)null),
                            Tuple.Create("POST", "/api/sets/" + sid + "/restic", (Msg)null),
                            Tuple.Create("POST", "/api/sets/" + sid + "/resticreport", new Msg().Set("result", "BS_STOP_SUCCESS")),
                            Tuple.Create("POST", "/api/sets/" + sid + "/restoretest", new Msg().Set("checked", 1).Set("failed", 1)),
                            Tuple.Create("POST", "/api/sets/" + sid + "/restorelog", new Msg().Set("result", "RESTORE_STOP_SUCCESS")),
                            Tuple.Create("GET", "/api/sets/" + sid + "/begin", (Msg)null),
                            Tuple.Create("POST", "/api/sets/" + sid + "/interrupted", new Msg().Set("job", job)),
                            Tuple.Create("POST", "/api/sets/" + sid + "/progress", new Msg().Set("job", job)),
                            Tuple.Create("POST", "/api/sets/" + sid + "/jobs/" + job + "/delete", new Msg().Add("rels", new Msg().Set("rel", "AAAAAAAAAAAAAAAAAAAAAAAAAA"))),
                            Tuple.Create("POST", "/api/sets/" + sid + "/jobs/" + job + "/commit", new Msg().Set("result", "BS_STOP_SUCCESS")),
                            Tuple.Create("POST", "/api/sets/" + sid + "/jobs/" + job + "/abort", new Msg().Set("result", "BS_STOP_BY_USER")),
                            Tuple.Create("PUT", "/api/sets/" + sid + "/jobs/" + job + "/object?rel=AAAAAAAAAAAAAAAAAAAAAAAAAA&seq=0&kind=F", new Msg()),
                            Tuple.Create("POST", "/api/webrestore/" + sid + "/points", new Msg().Set("key", "x")),
                        };
                        foreach (var r in routes)
                        {
                            var a = call(r.Item1, r.Item2, r.Item3);
                            if (a.Status == 200 || a.Status >= 500) wrong.Add(cred + " " + r.Item1 + " " + r.Item2 + " -> " + a.Status + " " + a.Body["error"]);
                            if (a.Text.Contains("BobFileU3") || a.Text.Contains("BobSetNameT6")) wrong.Add(cred + " " + r.Item2 + " showed bob's data");
                        }
                    }
                    // the profile, the quota and the calls of the computer are its own customer's
                    foreach (var p in new[] { "/api/profile", "/api/quota" })
                    {
                        var a = call("GET", p, null);
                        Assert.Equal(200, a.Status);
                        Assert.False(a.Text.Contains("BobSetNameT6") || a.Text.Contains(bset.Id), p + " showed bob's set");
                    }
                    // a folder list for "another computer" whose name climbs out stays in anna's own folder
                    Assert.Equal(200, call("POST", "/api/folders", new Msg().Set("computer", "..\\..\\bob\\db\\Profile.xml").Set("dirs", "C:\\x")).Status);
                }
                // anna's own set, bob's run id: no run of bob's is touched
                foreach (var a in new[] { "commit", "delete", "abort" })
                    Http(env.Url, "POST", "/api/sets/" + aset.Id + "/jobs/" + job + "/" + a, session: annaSession, body: new Msg().Set("result", "BS_STOP_BY_USER"));
                Assert.Empty(wrong);
                Assert.Equal(before, Tree(bobDir));
                // nothing was written anywhere outside anna's own folder (the climbing computer name included)
                Assert.Equal(outside, Outside(env.HomeA, Path.Combine(env.HomeA, "anna")));
                Assert.True(File.Exists(Path.Combine(bobDir, "db", "Profile.xml")));
                // bob's run is still his to end, and his next backup is whole
                Assert.Equal(200, Http(env.Url, "POST", "/api/sets/" + bset.Id + "/jobs/" + job + "/abort", device: bob.Home.DeviceToken, body: new Msg().Set("result", "BS_STOP_BY_USER")).Status);
                Assert.Equal("BS_STOP_SUCCESS", bob.Backup(bset.Id).Result);
            }
        }

        // ================================================================== AU-02: the wrong kind of token on every route

        [Fact]
        public void AU02_ARevokedDevice_AnEndedOrExpiredSignIn_AndTheWrongKindOfToken_AreRefusedOnEveryRoute()
        {
            var skew = TimeSpan.Zero;
            SystemClock.Use(() => DateTime.UtcNow + skew);   // before the server starts: its threads take this clock
            try
            {
                using (var env = new Env())
                {
                    var sys = env.Admin();
                    env.CreateUser("anna", "Customer-Pass-1");
                    var anna = env.Agent("anna", "Customer-Pass-1", name: "annapc");
                    var spare = env.Agent("anna", "Customer-Pass-1", name: "sparepc");
                    var aset = SetWithBackup(env, anna, "Customer-Pass-1", "AnnaSet", "anna.txt", "anna's");
                    Assert.Equal("1", sys.Call("POST", "/api/admin/users/anna/computers/disconnect", new Msg().Set("computer", spare.Home.Computer))["revoked"]);
                    var good = anna.Home.DeviceToken.Split('.');
                    var ended = anna.Interactive("Customer-Pass-1", null).Session;
                    Assert.Equal(200, Http(env.Url, "POST", "/api/logout", session: ended).Status);
                    var routes = new[]
                    {
                        "GET /api/profile", "GET /api/quota", "GET /api/client/files", "POST /api/folders", "POST /api/totp/enable", "POST /api/totp/disable", "GET /api/tickets",
                        "POST /api/sets", "GET /api/sets/" + aset.Id + "/points", "GET /api/sets/" + aset.Id + "/files", "POST /api/sets/" + aset.Id + "/key",
                        "POST /api/sets/" + aset.Id + "/settings", "GET /api/sets/" + aset.Id + "/sharedkey", "GET /api/sets/" + aset.Id + "/begin",
                        "POST /api/sets/" + aset.Id + "/restorelog", "POST /api/sets/" + aset.Id + "/progress", "POST /api/webrestore/sets",
                    };
                    var wrong = new List<string>();
                    Action<string, string, string> refused = (what, session, device) =>
                    {
                        foreach (var r in routes)
                        {
                            var mp = r.Split(' ');
                            var a = Http(env.Url, mp[0], mp[1], session: session, device: device, body: mp[0] == "POST" ? new Msg().Set("set", "<BACKUP_SET/>").Set("computer", "x") : null);
                            if (a.Status != 401) wrong.Add(what + ": " + r + " -> " + a.Status + " " + a.Body["error"]);
                        }
                    };
                    refused("no token", null, null);
                    refused("revoked computer", null, spare.Home.DeviceToken);
                    refused("damaged secret", null, good[0] + "." + good[1] + "." + new string('0', good[2].Length));
                    refused("another id", null, good[0] + "." + (long.Parse(good[1]) - 1) + "." + good[2]);
                    refused("the administrator's sign-in", sys.Session, null);
                    refused("an unknown sign-in", Bytes.Hex(Bytes.Random(24)), null);
                    refused("a signed-out sign-in", ended, null);
                    // the customer's sign-in never opens the administrators' API, on any route
                    var live = anna.Interactive("Customer-Pass-1", null).Session;
                    foreach (var p in new[] { "users", "settings", "staff", "keys/anna/" + aset.Id, "guard", "logs?cat=Access", "users/anna/computers", "tasks", "dashboard" })
                    {
                        var a = Http(env.Url, "GET", "/api/admin/" + p, session: live);
                        if (a.Status != 401) wrong.Add("customer on admin " + p + " -> " + a.Status);
                    }
                    // the computer's token never opens it either
                    if (Http(env.Url, "GET", "/api/admin/users", device: anna.Home.DeviceToken).Status != 401) wrong.Add("device on admin users");
                    // a customer's sign-in ends after 12 hours (to the second), a restart does not bring it back
                    Assert.Equal(200, Http(env.Url, "GET", "/api/profile", session: live).Status);
                    skew = TimeSpan.FromHours(12).Add(TimeSpan.FromSeconds(1));
                    refused("an expired sign-in", live, null);
                    Assert.Empty(wrong);
                    // the right token still works throughout (the refusals did not lock the computer out)
                    Assert.Equal(200, Http(env.Url, "GET", "/api/profile", device: anna.Home.DeviceToken).Status);
                }
            }
            finally { SystemClock.Use(null); }
        }

        // ================================================================== AU-05: a damaged key check value

        [Fact]
        public void AU05_ADamagedKeyCheckValue_TheRightKeyIsRefused_AWrongKeyThatMatchesTheDamagedCheckReadsNothing()
        {
            using (var env = new Env())
            {
                env.CreateUser("keys", "Customer-Pass-1");
                var app = env.Agent("keys", "Customer-Pass-1");
                var src = env.Dir("src");
                var rnd = new Random(5);
                for (int i = 0; i < 3; i++) { var b = new byte[40000 + i]; rnd.NextBytes(b); File.WriteAllBytes(Path.Combine(src, "f" + i + ".bin"), b); }
                var want = Tree(src);
                const string right = "Right custom key 2026", wrongKey = "Wrong custom key 2026";
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "Custom", Sources = { src } }, "CUSTOM", right);
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);

                // the server's copy of the check value is damaged so that it matches the WRONG key (a tampered or corrupted profile)
                var profile = Path.Combine(env.HomeA, "keys", "db", "Profile.xml");
                var doc = XDocument.Load(profile);
                var ek = doc.Root.Elements("BACKUP_SET").Single(e => (string)e.Attribute("ID") == set.Id).Element("ENCRYPTING_KEY");
                var original = (string)ek.Attribute("KEY");
                var forged = KeySet.Derive(wrongKey, Convert.FromBase64String((string)ek.Attribute("SALT"))).CheckValue();
                Assert.NotEqual(original, forged);
                ek.SetAttributeValue("KEY", forged); doc.Save(profile);

                var pc = env.Agent("keys", "Customer-Pass-1", name: "newpc");
                var s = pc.Interactive("Customer-Pass-1", null);
                // the right key no longer matches the check: refused before anything is read
                Assert.Equal("WRONG_KEY", Assert.Throws<AgentException>(() => pc.RestoreFor(s, set.Id, right)).Code);
                // the wrong key passes the damaged check, but the data itself refuses it: no file is written, never a wrong one
                var target = env.Dir("restore-wrong");
                Restore r = null; Exception failed = null;
                try { r = pc.RestoreFor(s, set.Id, wrongKey); r.Run(null, target, null, false); } catch (Exception e) { failed = e; }
                Assert.Empty(Directory.GetFiles(target, "*", SearchOption.AllDirectories));
                Assert.True(failed != null || (r.Restored == 0 && r.Failed > 0), "a wrong key was not refused by the data: restored " + (r == null ? -1 : r.Restored));

                // the check value repaired: the right key restores every file identical on a third computer
                doc = XDocument.Load(profile);
                doc.Root.Elements("BACKUP_SET").Single(e => (string)e.Attribute("ID") == set.Id).Element("ENCRYPTING_KEY").SetAttributeValue("KEY", original); doc.Save(profile);
                var third = env.Agent("keys", "Customer-Pass-1", name: "thirdpc");
                var t3 = env.Dir("restore-right");
                var r3 = third.RestoreFor(third.Interactive("Customer-Pass-1", null), set.Id, right); r3.Run(null, t3, null, false);
                Assert.Equal(0, r3.Failed);
                Assert.Equal(want, Tree(Path.Combine(t3, Env.Rel(src))));
            }
        }

        // ================================================================== AU-07: secrets in logs, run history, error messages, the admin site's answers

        [Fact]
        public void AU07_TypedPasswordsTwoStepSecretsAndDeviceSecrets_AreNowhereOnTheServer_NorInTheAdminSitesAnswers()
        {
            using (var env = new Env())
            {
                var sys = env.Admin();
                env.CreateUser("anna", "Anna-Real-Pass-1");
                var anna = env.Agent("anna", "Anna-Real-Pass-1", name: "annapc");
                var set = SetWithBackup(env, anna, "Anna-Real-Pass-1", "AnnaSet", "anna.txt", "anna's");
                var c = new Client(env.Url) { Retries = 0 };
                Func<Action, int> st = a => { try { a(); return 200; } catch (AgentException e) { return e.Status; } };
                // typos of passwords (a typo is often almost the real password) on every sign-in door
                Assert.Equal(401, st(() => c.Call("POST", "/api/login", new Msg().Set("login", "anna").Set("password", "Anna-Typo-Q1x"))));
                Assert.Equal(401, st(() => c.Call("POST", "/api/register", new Msg().Set("login", "anna").Set("password", "Anna-Typo-Q2x").Set("computer", "PC-X"))));
                Assert.Equal(401, st(() => c.Call("POST", "/api/admin/login", new Msg().Set("login", "admin").Set("password", "Admin-Typo-Q3x"))));
                Assert.Equal(401, st(() => c.Call("POST", "/api/login", new Msg().Set("login", "nobody-Q5x").Set("password", "Ghost-Typo-Q4x"))));
                // anna sets up two-step: its secret and backup codes are the customer's alone
                var session = anna.Interactive("Anna-Real-Pass-1", null);
                var en = session.Call("POST", "/api/totp/enable", new Msg());
                var secret = en["secret"]; var codes = en.List("codes").Select(x => x["code"]).ToList();
                session.Call("POST", "/api/totp/confirm", new Msg().Set("code", Totp.Code(secret, DateTime.UtcNow)));
                Assert.Equal(401, st(() => c.Call("POST", "/api/login", new Msg().Set("login", "anna").Set("password", "Anna-Real-Pass-1").Set("otp", "Otp-Typo-Q6x"))));
                var deviceSecret = anna.Home.DeviceToken.Split('.')[2];
                var hash = Profile.Load(Path.Combine(env.HomeA, "anna", "db", "Profile.xml")).Get("HASHED_PWD");
                Assert.False(string.IsNullOrEmpty(hash));

                var never = new[] { "Anna-Typo-Q1x", "Anna-Typo-Q2x", "Admin-Typo-Q3x", "Ghost-Typo-Q4x", "Otp-Typo-Q6x", "Anna-Real-Pass-1", "Admin-Pass-1", deviceSecret }.Concat(codes).ToList();
                var found = new List<string>();
                foreach (var f in Directory.GetFiles(env.Root, "*", SearchOption.AllDirectories).Where(f => !f.Contains(Path.DirectorySeparatorChar + "annapc-") && !f.Contains(Path.DirectorySeparatorChar + "src-")))
                {
                    string text; try { text = Atomic.ReadShared(f); } catch (Exception) { continue; }
                    foreach (var s in never) if (text.Contains(s)) found.Add(s + " in " + f.Substring(env.Root.Length));
                    // the two-step secret is kept only in the customer's own profile (the server must check the codes)
                    if (text.Contains(secret) && !(Path.GetDirectoryName(f) == Path.Combine(env.HomeA, "anna", "db") && Path.GetFileName(f).StartsWith("Profile.xml", StringComparison.Ordinal))) found.Add("the two-step secret in " + f.Substring(env.Root.Length));
                }
                // what the admin site receives: no hash, no secret, no code, no typed password
                foreach (var p in new[] { "users", "staff", "settings", "logs?cat=Access", "logs?cat=System", "logs?cat=Admin", "logs?cat=BackupErrors", "users/anna/computers", "tasks?hours=744",
                                          "users/anna/sets/" + set.Id, "users/anna/sets/" + set.Id + "/runs", "logs?cat=Backup&login=anna&set=" + set.Id, "dashboard", "vendors", "guard", "license", "me" })
                {
                    var a = Http(env.Url, "GET", "/api/admin/" + p, session: sys.Session);
                    Assert.Equal(200, a.Status);
                    foreach (var s in never.Concat(new[] { secret, hash })) if (a.Text.Contains(s)) found.Add(s + " in the answer of " + p);
                }
                // what the computer receives about its customer: no hash, no two-step secret, no codes
                var prof = Http(env.Url, "GET", "/api/profile", device: anna.Home.DeviceToken);
                foreach (var s in new[] { secret, hash }.Concat(codes)) if (prof.Text.Contains(s)) found.Add("the profile sent to the computer holds a secret");
                Assert.Empty(found);
            }
        }

        // ================================================================== AU-02: a deleted customer's tokens and the next customer of that name

        [Fact]
        public void AU02_ADeletedCustomersComputerAndSignIn_NeverOpenTheNextCustomerOfTheSameName()
        {
            var root = Path.Combine(Path.GetTempPath(), "obchsec-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                var home = Path.Combine(root, "srv");
                var cfg = SystemConfig.Init(Path.Combine(home, "system"), "admin", "Admin-Pass-1", "localhost", new[] { Path.Combine(home, "home") + "|UNLIMITED|100" });
                var users = new Users(cfg);
                users.Create("anna", "Anna-Pass-1", "Anna", null, "COMPRESSED", "anna@example.com", "203.0.113.5");
                var oldDevice = users.RegisterDevice("anna", "PC-ONE", "203.0.113.5");
                var oldSession = users.NewSession("anna", false);
                Assert.Equal("anna", users.CheckDevice(oldDevice, "203.0.113.5"));
                Assert.Null(Recycle.Request(cfg, users, "anna", null, "admin", "10.0.0.1", DateTime.UtcNow));   // one administrator: deleted at once
                Assert.Equal(401, Status(() => users.CheckDevice(oldDevice, "203.0.113.5")));
                Assert.Null(users.GetSession(oldSession));
                // another customer gets the name (another reseller's, say): the old computer and sign-in open nothing of it
                users.Create("anna", "Other-Pass-22", "Another Anna", null, "COMPRESSED", "other@example.com", "198.51.100.1");
                Assert.Equal(401, Status(() => users.CheckDevice(oldDevice, "203.0.113.5")));
                Assert.Null(users.GetSession(oldSession));
                Assert.Null(new Users(cfg).GetSession(oldSession));                                  // nor after a restart (kept sessions)
                var fresh = users.RegisterDevice("anna", "PC-ONE", "198.51.100.1");
                Assert.Equal("anna", users.CheckDevice(fresh, "198.51.100.1"));
                Assert.Equal(401, Status(() => users.CheckDevice(oldDevice, "203.0.113.5")));
                // the old password is not the new customer's
                Assert.Equal(401, Status(() => users.CheckUser("anna", "Anna-Pass-1", null, "203.0.113.5")));
            }
            finally { try { Directory.Delete(root, true); } catch (Exception) { } }
        }

        static int Status(Action a) { try { a(); return 200; } catch (ApiException e) { return e.Status; } }
    }

    /// <summary>
    /// UI-04 challenge: the client window's local API (ClientUi) — the challenged test tried only /api/state without the key.
    /// Here every operation of the window, with no key, a wrong key, and from another site (Host), and every operation that needs
    /// the customer's password after the 15 minutes; the oracle is what reached the recording stand-in server (nothing).
    /// </summary>
    [Collection("ClientDir")]
    public class PilotChallengeSecurityClientUiTests : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "obchsec-ui-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        public PilotChallengeSecurityClientUiTests() { Directory.CreateDirectory(root); }
        public void Dispose() { SystemClock.Use(null); try { Directory.Delete(root, true); } catch (Exception) { } }

        internal const string Pw = "Anna-Pass-1", SetId = "1700000000101";
        internal sealed class Window : IDisposable
        {
            public readonly StubServer Server = new StubServer();
            public AgentApp App; public ClientUi Ui; public string Src; public TimeSpan Skew;
            public void Dispose() { if (Ui != null) Ui.Dispose(); Server.Dispose(); }
        }

        internal static Window Open(string root)
        {
            var w = new Window();
            w.Src = Path.Combine(root, "pc", "Docs"); Directory.CreateDirectory(w.Src);
            File.WriteAllText(Path.Combine(w.Src, "ledger.csv"), "row,1\n");
            var key = KeySet.Random();
            var p = Profile.Create("anna", "Anna", "x", "en", "UTC");
            p.Root.Add(new BackupSetInfo { Id = SetId, Name = "Docs", Computer = "PC-ANNA", Sources = { w.Src }, Hour = 21, Minute = 30, KeyCheck = key.CheckValue(), KeySalt = "c2FsdA==", Vss = false }.ToXml());
            w.Server.ProfileXml = p.Doc.ToString();
            w.Server.Handler = r =>
            {
                if (r.Action == "login") return r.Msg["password"] == Pw && r.Msg["login"] == "anna" ? StubServer.Answer.Ok(new Msg().Set("session", "session-1")) : StubServer.Answer.Error(401, "LOGIN", "Wrong user name or password.");
                return null;
            };
            var agentDir = Path.Combine(root, "pc", "agent"); Directory.CreateDirectory(agentDir);
            w.App = new AgentApp(agentDir);
            w.App.Home.SaveRegistration(w.Server.Url, "anna", "PC-ANNA", "YW5uYQ==.1700000000000.device-secret", "SYSTEM", null);
            w.App.Home.SaveKey(SetId, key);
            SystemClock.Use(() => DateTime.UtcNow + w.Skew);
            for (int port = 19100 + new Random().Next(700); ; port++) try { w.Ui = new ClientUi(w.App, port); break; } catch (HttpListenerException) { }
            return w;
        }

        internal sealed class Answer { public int Status; public Msg Body; public string Text; public WebHeaderCollection Headers; }

        internal static Answer Raw(ClientUi ui, string method, string path, Msg body = null, string key = "", string host = null, string origin = null)
        {
            var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + ui.Port + path);
            req.Method = method; req.Proxy = null; req.Timeout = 60000;
            if (key == "") key = ui.Key;
            if (key != null) req.Headers["X-Key"] = key;
            if (host != null) req.Host = host;
            if (origin != null) req.Headers["Origin"] = origin;
            if (body != null) { var b = body.ToBytes(); req.ContentType = "application/xml"; using (var s = req.GetRequestStream()) s.Write(b, 0, b.Length); }
            else if (method == "POST") req.ContentLength = 0;
            HttpWebResponse resp;
            try { resp = (HttpWebResponse)req.GetResponse(); }
            catch (WebException e) when (e.Response != null) { resp = (HttpWebResponse)e.Response; }
            using (resp) using (var rd = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                var a = new Answer { Status = (int)resp.StatusCode, Text = rd.ReadToEnd(), Headers = resp.Headers };
                try { a.Body = Msg.Parse(a.Text); } catch (Exception) { a.Body = new Msg(); }
                return a;
            }
        }

        static readonly string[] Ops = { "state", "login", "logout", "security", "totp-enable", "totp-confirm", "totp-disable", "backup", "jobs", "points", "files", "restore",
                                         "addset", "editset", "help", "dirs", "server-check", "connect", "update" };

        [Fact]
        public void UI04_EveryOperationOfTheWindow_WithoutTheKey_WithAWrongKey_OrFromAnotherSite_IsRefused_NothingReachesTheServer()
        {
            using (var w = Open(root))
            {
                var body = new Msg().Set("password", Pw).Set("set", SetId).Set("target", Path.Combine(root, "out")).Set("sources", w.Src).Set("type", "FILE").Set("code", "123456")
                    .Set("server", w.Server.Url).Set("login", "anna").Set("subject", "x").Set("apply", "1");
                var wrong = new List<string>();
                var shortKey = w.Ui.Key.Substring(0, w.Ui.Key.Length - 1);
                foreach (var op in Ops)
                    foreach (var method in new[] { "GET", "POST" })
                    {
                        foreach (var key in new[] { null, "wrong", shortKey, w.Ui.Key.ToUpperInvariant() == w.Ui.Key ? w.Ui.Key + "0" : w.Ui.Key.ToUpperInvariant() })
                        {
                            var a = Raw(w.Ui, method, "/api/" + op + "?set=" + SetId + "&path=" + Uri.EscapeDataString(root), method == "POST" ? body : null, key: key);
                            if (a.Status != 403) wrong.Add(method + " " + op + " key=" + (key ?? "none") + " -> " + a.Status);
                            if (a.Text.Contains(root) || a.Text.Contains("anna") || a.Text.Contains("Docs")) wrong.Add(method + " " + op + " showed data without the key");
                        }
                        // the right key from a page of another site (DNS rebinding: the Host is the other site's name)
                        foreach (var host in new[] { "evil.example", "evil.example:" + w.Ui.Port, "127.0.0.1.evil.example", "localhost.evil.example" })
                        {
                            var a = Raw(w.Ui, method, "/api/" + op, method == "POST" ? body : null, host: host);
                            // refused by the window (403) or already by the listener for a name it does not serve (404): never answered
                            if (a.Status != 403 && a.Status != 404) wrong.Add(method + " " + op + " host=" + host + " -> " + a.Status);
                            if (a.Text.Contains("anna") || a.Text.Contains("Docs") || a.Text.Contains(root)) wrong.Add(method + " " + op + " host=" + host + " showed data");
                        }
                    }
                // a browser's preflight from another site is never allowed (no CORS header), so no page can send the key
                var pre = Raw(w.Ui, "OPTIONS", "/api/state", key: null, origin: "http://evil.example");
                Assert.NotEqual(200, pre.Status);
                Assert.Null(pre.Headers["Access-Control-Allow-Origin"]);
                var withKey = Raw(w.Ui, "GET", "/api/state", origin: "http://evil.example");
                Assert.Null(withKey.Headers["Access-Control-Allow-Origin"]);
                Assert.Empty(wrong);
                // nothing reached the server but the one state call above (made with the key): no sign-in, no set, no backup, no ticket, no restore
                Assert.Empty(w.Server.Requests.Where(r => r.Method == "POST" || r.Action == "begin" || r.Action == "files" || r.Action == "tickets"));
                Assert.Empty(Directory.Exists(Path.Combine(root, "out")) ? Directory.GetFiles(Path.Combine(root, "out"), "*", SearchOption.AllDirectories) : new string[0]);
            }
        }

        [Fact]
        public void UI04_AfterFifteenMinutesWithoutUse_EveryOperationThatNeedsThePassword_AsksAgain_NothingReachesTheServer()
        {
            using (var w = Open(root))
            {
                Assert.Equal(200, Raw(w.Ui, "POST", "/api/login", new Msg().Set("password", Pw)).Status);
                Assert.Equal("1", Raw(w.Ui, "GET", "/api/state").Body["session"]);
                // 14:59 later it still works (and the use moves the end on)
                w.Skew = TimeSpan.FromMinutes(15).Subtract(TimeSpan.FromSeconds(1));
                Assert.Equal(200, Raw(w.Ui, "POST", "/api/totp-enable", new Msg()).Status);
                // 15 minutes and one second after the last use: everything that needs the password asks again
                w.Skew = w.Skew + TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(1);
                var before = w.Server.Requests.Count;
                var needs = new[] { "totp-enable", "totp-confirm", "totp-disable", "points", "files", "restore", "addset", "editset" };
                var wrong = new List<string>();
                foreach (var op in needs)
                {
                    var a = Raw(w.Ui, "POST", "/api/" + op + "?set=" + SetId, new Msg().Set("set", SetId).Set("target", Path.Combine(root, "out")).Set("sources", w.Src).Set("type", "FILE").Set("code", "123456").Set("name", "renamed"));
                    if (a.Status != 401 || a.Body["code"] != "LOGIN") wrong.Add(op + " -> " + a.Status + " " + a.Body["code"]);
                }
                Assert.Empty(wrong);
                Assert.Empty(w.Server.Requests.Skip(before).Where(r => r.Method == "POST" || r.Action == "files" || r.Action == "points"));
                Assert.Equal("0", Raw(w.Ui, "GET", "/api/state").Body["session"]);
                Assert.False(Directory.Exists(Path.Combine(root, "out")) && Directory.GetFiles(Path.Combine(root, "out"), "*", SearchOption.AllDirectories).Length > 0);
                // signing in again opens it again
                Assert.Equal(200, Raw(w.Ui, "POST", "/api/login", new Msg().Set("password", Pw)).Status);
                Assert.Equal(200, Raw(w.Ui, "POST", "/api/totp-enable", new Msg()).Status);
            }
        }
    }
}
