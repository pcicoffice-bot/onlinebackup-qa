using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// QA agent H — AU-02 / AU-07 (integration, real server over HTTP): no answer tells an outsider which customer names
    /// exist; one reseller never reaches another reseller's customers (also with case, encoding and path tricks); one
    /// customer never reaches another's sets; malformed values are a clear 4xx, never a server error.
    /// </summary>
    public class AuditH_ApiTests
    {
        static (int status, string body) Raw(string url, string method, byte[] body, Dictionary<string, string> headers = null)
        {
            var r = (HttpWebRequest)WebRequest.Create(url);
            r.Method = method; r.Timeout = 20000; r.ContentType = "application/xml";
            if (headers != null) foreach (var kv in headers) r.Headers[kv.Key] = kv.Value;
            if (body != null) { r.ContentLength = body.Length; using (var s = r.GetRequestStream()) s.Write(body, 0, body.Length); }
            else if (method != "GET") r.ContentLength = 0;
            try { using (var resp = (HttpWebResponse)r.GetResponse()) using (var sr = new StreamReader(resp.GetResponseStream())) return ((int)resp.StatusCode, sr.ReadToEnd()); }
            catch (WebException e)
            {
                var resp = e.Response as HttpWebResponse; if (resp == null) return (0, e.Message);
                using (var sr = new StreamReader(resp.GetResponseStream())) return ((int)resp.StatusCode, sr.ReadToEnd());
            }
        }
        static byte[] B(Msg m) { return m.ToBytes(); }
        static int Status(Action a) { try { a(); return 200; } catch (AgentException e) { return e.Status; } }

        // ------------------------------------------------------------------ enumeration of customer names

        [Fact]
        public void SignIn_UnknownNameAndWrongPassword_GetTheSameAnswer_WordForWord()
        {
            using (var env = new Env())
            {
                env.CreateUser("realcust", "Customer-Pass-1");
                var url = env.Url.TrimEnd('/');
                var wrong = Raw(url + "/api/login", "POST", B(new Msg().Set("login", "realcust").Set("password", "Wrong-Pass-1")));
                var unknown = Raw(url + "/api/login", "POST", B(new Msg().Set("login", "nosuchcust").Set("password", "Wrong-Pass-1")));
                Assert.Equal(401, wrong.status); Assert.Equal(401, unknown.status);
                var mw = Msg.Parse(Encoding.UTF8.GetBytes(wrong.body)); var mu = Msg.Parse(Encoding.UTF8.GetBytes(unknown.body));
                Assert.Equal(mw["error"], mu["error"]);
                Assert.True(mw["message"] == mu["message"], "the message tells which name exists: known name → \"" + mw["message"] + "\", unknown → \"" + mu["message"] + "\"");
            }
        }

        [Fact]
        public void DeviceToken_ForAnUnknownName_AnswersLikeAWrongToken_NoNameOracle()
        {
            using (var env = new Env())
            {
                env.CreateUser("realcust", "Customer-Pass-1");
                var url = env.Url.TrimEnd('/');
                Func<string, (int status, string body)> probe = login =>
                    Raw(url + "/api/profile", "GET", null, new Dictionary<string, string> { { "X-Device", Convert.ToBase64String(Encoding.UTF8.GetBytes(login)) + ".1.00" } });
                var known = probe("realcust"); var unknown = probe("nosuchcust");
                Assert.Equal(401, known.status);
                Assert.True(unknown.status == known.status && Msg.Parse(Encoding.UTF8.GetBytes(unknown.body))["error"] == Msg.Parse(Encoding.UTF8.GetBytes(known.body))["error"],
                    "a forged device token tells which customer names exist: known → " + known.status + " " + known.body + " | unknown → " + unknown.status + " " + unknown.body);
            }
        }

        // ------------------------------------------------------------------ one reseller, another reseller's customers

        [Fact]
        public void Reseller_CannotReachAnotherResellersCustomer_ByAnyRoute_OrNameTrick()
        {
            // coverage (VND-050 / AU-07, integration): every per-customer admin route, with the name as is, in capitals,
            // percent-encoded and with a trailing dot; the system administrator's control call shows the routes are real
            using (var env = new Env())
            {
                var sys = env.Admin();
                foreach (var v in new[] { "acme", "beta" })
                {
                    sys.Call("POST", "/api/admin/vendors", new Msg().Set("id", v).Set("name", v));
                    sys.Call("POST", "/api/admin/vendors/" + v + "/admins", new Msg().Set("login", v + "-admin").Set("password", "Vendor-Pass-12"));
                }
                var acme = TestAuth.Admin(env.Url, "acme-admin", "Vendor-Pass-12", 0);
                var beta = TestAuth.Admin(env.Url, "beta-admin", "Vendor-Pass-12", 0);
                beta.Call("POST", "/api/admin/users", new Msg().Set("login", "betacust").Set("password", "Customer-Pass-1").Set("alias", "B").Set("quotaGB", 1));
                acme.Call("POST", "/api/admin/users", new Msg().Set("login", "acmecust").Set("password", "Customer-Pass-1").Set("alias", "A").Set("quotaGB", 1));
                var app = env.Agent("betacust", "Customer-Pass-1", name: "beta-pc");
                var src = env.Dir("bsrc"); File.WriteAllText(Path.Combine(src, "beta-secret.txt"), "beta");
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "BetaSet", Sources = { src } });
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);

                var url = env.Url.TrimEnd('/');
                var names = new[] { "betacust", "BETACUST", "BetaCust", "%62etacust", "betacust." };
                var reached = new List<string>();
                foreach (var n in names)
                {
                    var calls = new List<(string m, string p, Msg b)> {
                        ("GET", "/api/admin/users/" + n + "/sets/" + set.Id, null),
                        ("GET", "/api/admin/users/" + n + "/sets/" + set.Id + "/runs", null),
                        ("POST", "/api/admin/users/" + n + "/sets/" + set.Id + "/run", new Msg()),
                        ("GET", "/api/admin/users/" + n + "/computers", null),
                        ("GET", "/api/admin/users/" + n + "/compliance", null),
                        ("GET", "/api/admin/users/" + n + "/folders", null),
                        ("GET", "/api/admin/keys/" + n + "/" + set.Id, null),
                        ("POST", "/api/admin/users/" + n + "/unlock", new Msg()),
                        ("POST", "/api/admin/users/" + n + "/resettotp", new Msg()),
                        ("POST", "/api/admin/users/" + n + "/security", new Msg().Set("allowedIps", "203.0.113.1")),
                        ("POST", "/api/admin/users/" + n + "/contacts", new Msg()),
                        ("POST", "/api/admin/users/" + n + "/details", new Msg().Set("alias", "owned")),
                        ("POST", "/api/admin/users/" + n + "/quota", new Msg().Set("quotaGB", "0.001")),
                        ("POST", "/api/admin/users/" + n + "/delete", new Msg()),
                        ("POST", "/api/admin/users/" + n + "/unfreeze", new Msg()),
                        ("POST", "/api/admin/users/" + n + "/computers/disconnect", new Msg().Set("computer", "PC-beta-pc")),
                        ("POST", "/api/admin/users/" + n + "/aidiagnose", new Msg().Set("set", set.Id).Set("cat", "Backup").Set("file", "x.log")),
                    };
                    foreach (var c in calls)
                    {
                        var r = Raw(url + c.p, c.m, c.b == null ? null : B(c.b), new Dictionary<string, string> { { "X-Session", acme.Session } });
                        if (r.status < 400 || r.status >= 500 || r.body.Contains("beta-secret") || r.body.Contains("BetaSet")) reached.Add(c.m + " " + c.p + " → " + r.status);
                    }
                }
                // moving a computer of its own customer into the other reseller's customer
                var mv = Raw(url + "/api/admin/users/acmecust/computers/move", "POST", B(new Msg().Set("computer", "x").Set("target", "BETACUST")), new Dictionary<string, string> { { "X-Session", acme.Session } });
                if (mv.status < 400 || mv.status >= 500) reached.Add("move into BETACUST → " + mv.status);
                // a bulk action and a service call naming the other reseller's customer
                var bulk = acme.Call("POST", "/api/admin/bulk", new Msg().Set("action", "requiretotp").Set("on", "1").Add("logins", new Msg().Set("login", "BetaCust")));
                if (bulk["done"] != "0") reached.Add("bulk on BetaCust → done=" + bulk["done"]);
                var call = Raw(url + "/api/admin/tickets", "POST", B(new Msg().Set("login", "betacust").Set("subject", "x")), new Dictionary<string, string> { { "X-Session", acme.Session } });
                if (call.status < 400 || call.status >= 500) reached.Add("ticket for betacust → " + call.status);
                Assert.True(reached.Count == 0, "reseller acme reached beta's customer:\n" + string.Join("\n", reached));

                // nothing of beta's customer changed; the system administrator's control call reaches the same set
                var prof = env.Api.UserStore.LoadProfile("betacust");
                Assert.NotEqual("owned", prof.Get("ALIAS")); Assert.True(string.IsNullOrEmpty(prof.Get("ALLOWED_IPS"))); Assert.NotEqual("Y", prof.Get("REQUIRE_TOTP"));
                Assert.Contains("BetaSet", Raw(url + "/api/admin/users/betacust/sets/" + set.Id, "GET", null, new Dictionary<string, string> { { "X-Session", sys.Session } }).body);
                Assert.Contains("BetaSet", Raw(url + "/api/admin/users/BETACUST/sets/" + set.Id, "GET", null, new Dictionary<string, string> { { "X-Session", beta.Session } }).body);
            }
        }

        [Fact]
        public void ResellerDashboard_CountsOnlyItsOwnComputers()
        {
            using (var env = new Env())
            {
                var sys = env.Admin();
                sys.Call("POST", "/api/admin/vendors", new Msg().Set("id", "acme").Set("name", "Acme"));
                sys.Call("POST", "/api/admin/vendors/acme/admins", new Msg().Set("login", "acme-admin").Set("password", "Vendor-Pass-12"));
                var acme = TestAuth.Admin(env.Url, "acme-admin", "Vendor-Pass-12", 0);
                env.CreateUser("other1", "Customer-Pass-1");                                   // the server owner's customers, not acme's
                env.Agent("other1", "Customer-Pass-1", name: "o1"); env.Agent("other1", "Customer-Pass-1", name: "o2");
                Assert.Equal("2", sys.Call("GET", "/api/admin/dashboard")["computers"]);        // control: the owner sees both
                var d = acme.Call("GET", "/api/admin/dashboard");
                Assert.Equal("0", d["customers"]);
                Assert.True(d["computers"] == "0", "a reseller with no customers sees the computers of the whole server: " + d["computers"]);
            }
        }

        // ------------------------------------------------------------------ one customer, another customer's sets

        [Fact]
        public void Customer_WithASession_CannotReachAnotherCustomersSet_ByIdCaseOrPath()
        {
            // coverage (AU-02 / AU-07, integration): FuzzTests checks a device token; here the interactive session (which
            // also opens the object download and the key upload), and set ids / names with tricks
            using (var env = new Env())
            {
                env.CreateUser("alice", "Customer-Pass-1"); env.CreateUser("bob", "Customer-Pass-1");
                var a = env.Agent("alice", "Customer-Pass-1", name: "alice-pc");
                var srcA = env.Dir("a"); File.WriteAllText(Path.Combine(srcA, "secret-of-alice.txt"), "alice data");
                var setA = a.CreateSet(a.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "A", Sources = { srcA } });
                Assert.Equal("BS_STOP_SUCCESS", a.Backup(setA.Id).Result);
                var kf = Path.Combine(env.HomeA, "alice", "db", "keys", setA.Id + ".bin");
                var keyBefore = File.Exists(kf) ? Convert.ToBase64String(File.ReadAllBytes(kf)) : null;
                var b = new Client(env.Url) { Retries = 0 };
                b.Session = b.Call("POST", "/api/login", new Msg().Set("login", "bob").Set("password", "Customer-Pass-1"))["session"];
                var url = env.Url.TrimEnd('/');
                var h = new Dictionary<string, string> { { "X-Session", b.Session } };
                var reached = new List<string>();
                foreach (var id in new[] { setA.Id, setA.Id + "%00", "%20" + setA.Id, setA.Id + ".", "0" + setA.Id })
                    foreach (var route in new[] { "points", "files", "object?loc=x", "sharedkey", "settings", "key", "begin", "restorelog", "progress", "interrupted", "restoretest", "jobs/2026-01-01-00-00-00/abort" })
                    {
                        var m = route == "points" || route == "files" || route.StartsWith("object") || route == "sharedkey" ? "GET" : "POST";
                        var r = Raw(url + "/api/sets/" + id + "/" + route, m, m == "POST" ? B(new Msg().Set("key", "AAAA").Set("job", "2026-01-01-00-00-00")) : null, h);
                        if (r.status < 400 || r.status >= 500 || r.body.Contains("secret-of-alice")) reached.Add(m + " " + id + "/" + route + " → " + r.status);
                    }
                Assert.True(reached.Count == 0, "bob reached alice's set:\n" + string.Join("\n", reached));
                // alice's set is untouched: no log, run or key was written by bob's calls
                var keyFile = Path.Combine(env.HomeA, "alice", "db", "keys", setA.Id + ".bin");
                Assert.Equal(keyBefore, File.Exists(keyFile) ? Convert.ToBase64String(File.ReadAllBytes(keyFile)) : null);
                Assert.Empty(Directory.Exists(Path.Combine(env.HomeA, "alice", "logs", setA.Id, "Restore")) ? Directory.GetFiles(Path.Combine(env.HomeA, "alice", "logs", setA.Id, "Restore")) : new string[0]);
                // control: alice's own session reaches the same route
                var ac = new Client(env.Url) { Retries = 0 };
                ac.Session = ac.Call("POST", "/api/login", new Msg().Set("login", "alice").Set("password", "Customer-Pass-1"))["session"];
                Assert.Equal(200, Raw(url + "/api/sets/" + setA.Id + "/points", "GET", null, new Dictionary<string, string> { { "X-Session", ac.Session } }).status);
            }
        }

        // ------------------------------------------------------------------ malformed values: a clear 4xx

        [Fact]
        public void MalformedValues_OnSignedInRoutes_AreAClear4xx_NeverAServerError()
        {
            using (var env = new Env())
            {
                var admin = env.Admin();
                env.CreateUser("mal", "Customer-Pass-1");
                var app = env.Agent("mal", "Customer-Pass-1");
                var src = env.Dir("m"); File.WriteAllText(Path.Combine(src, "x.txt"), "x");
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "M", Sources = { src } });
                var c = new Client(env.Url) { Retries = 0 };
                c.Session = c.Call("POST", "/api/login", new Msg().Set("login", "mal").Set("password", "Customer-Pass-1"))["session"];
                var url = env.Url.TrimEnd('/');
                var A = new Dictionary<string, string> { { "X-Session", admin.Session } };
                var C = new Dictionary<string, string> { { "X-Session", c.Session } };
                var D = new Dictionary<string, string> { { "X-Device", app.Home.DeviceToken } };
                var probes = new List<(string m, string p, Msg b, Dictionary<string, string> h)> {
                    ("POST", "/api/admin/maintenance", new Msg().Set("now", "yesterday"), A),
                    ("GET", "/api/admin/tasks?hours=99999999999999", null, A),
                    ("GET", "/api/admin/tasks?hours=x", null, A),
                    ("POST", "/api/admin/users/mal/quota", new Msg().Set("quotaGB", "1e400"), A),
                    ("POST", "/api/admin/users/mal/quota", new Msg().Set("quotaGB", "-5"), A),
                    ("POST", "/api/admin/users/mal/security", new Msg().Set("lockAttempts", "x").Set("lockMinutes", "99999999999"), A),
                    ("POST", "/api/admin/bulk", new Msg().Set("action", "quota").Set("quotaGB", "abc").Add("logins", new Msg().Set("login", "mal")), A),
                    ("POST", "/api/admin/rebuild", new Msg().Set("login", "mal").Set("set", "../../x"), A),
                    ("POST", "/api/admin/verify", new Msg().Set("login", "mal").Set("set", "abc"), A),
                    ("POST", "/api/admin/users/mal/sets/" + set.Id, new Msg().Set("set", "<BACKUP_SET/>"), A),
                    ("GET", "/api/admin/users/mal/sets/99999999", null, A),
                    ("POST", "/api/admin/users/mal/sets/" + set.Id + "/move", new Msg(), A),
                    ("POST", "/api/admin/users/mal/computers/move", new Msg().Set("computer", "x"), A),
                    ("POST", "/api/admin/tickets/deleted/99999/restore", new Msg(), A),
                    ("POST", "/api/admin/tickets/deleted/-1/restore", new Msg(), A),
                    ("GET", "/api/admin/keys/mal/..", null, A),
                    ("GET", "/api/admin/users/mal/folders?computer=../../x", null, A),
                    ("POST", "/api/admin/guard/block", new Msg().Set("ip", "not-an-ip"), A),
                    ("POST", "/api/admin/users", new Msg().Set("login", "\u0430dmin\u200b").Set("password", "Customer-Pass-1"), A),
                    ("POST", "/api/sets", new Msg().Set("set", "<BACKUP_SET TYPE=\"FILE\" />"), C),
                    ("POST", "/api/sets", new Msg().Set("set", "<x/>"), C),
                    ("POST", "/api/sets/" + set.Id + "/settings", new Msg().Set("set", "<BACKUP_SET/>"), C),
                    ("POST", "/api/sets/" + set.Id + "/key", new Msg().Set("key", "@@@"), C),
                    ("GET", "/api/sets/" + set.Id + "/files?point=../../../etc", null, C),
                    ("GET", "/api/sets/" + set.Id + "/files?point=" + new string('9', 40), null, C),
                    ("GET", "/api/sets/" + set.Id + "/object?loc=", null, C),
                    ("GET", "/api/sets/" + set.Id + "/object?loc=%2Fetc%2Fpasswd", null, C),
                    ("POST", "/api/sets/" + set.Id + "/begin?key=zz", null, D),
                    ("POST", "/api/sets/" + set.Id + "/progress", new Msg().Set("job", "../../x").Set("percent", "abc"), D),
                    ("POST", "/api/sets/" + set.Id + "/restoretest", new Msg().Set("checked", "x").Set("failed", "y"), D),
                    ("POST", "/api/sets/" + set.Id + "/interrupted", new Msg().Set("job", "2026-13-45-99-99-99").Set("started", "x"), D),
                    ("POST", "/api/sets/" + set.Id + "/resticreport", new Msg().Set("job", "x"), D),
                    ("PUT", "/api/sets/" + set.Id + "/jobs/2026-01-01-00-00-00/object", new Msg(), D),
                    ("POST", "/api/sets/" + set.Id + "/jobs/../../commit", new Msg(), D),
                    ("POST", "/api/sets/" + set.Id + "/jobs/2026-01-01-00-00-00/commit", new Msg().Set("files", "x"), D),
                    ("POST", "/api/sets/" + set.Id + "/jobs/2026-01-01-00-00-00/delete", new Msg().Add("rels", new Msg().Set("rel", "../x")), D),
                    ("POST", "/api/folders", new Msg().Set("computer", "../../x").Set("tree", "<<<"), D),
                    ("POST", "/api/tickets", new Msg().Set("subject", new string('x', 100000)), D),
                    ("POST", "/api/webrestore/" + set.Id + "/ls", new Msg().Set("key", "x"), C),
                    ("GET", "/api/client/file?name=../../../etc/passwd", null, D),
                    ("GET", "/restic/mal/" + set.Id + "/../../keys", null, null),
                    ("GET", "/restic/%00/1/config", null, null),
                };
                var errors = new List<string>();
                foreach (var p in probes)
                {
                    var r = Raw(url + p.p, p.m, p.b == null ? null : B(p.b), p.h);
                    if (r.status == 0 || r.status >= 500) errors.Add(p.m + " " + p.p + " " + (p.b == null ? "" : Encoding.UTF8.GetString(B(p.b)).Substring(0, Math.Min(80, B(p.b).Length))) + " → " + r.status + " " + (r.body ?? "").Substring(0, Math.Min(100, (r.body ?? "").Length)));
                    foreach (var leak in new[] { env.Root, "HASHED_PWD", "TOTP_SECRET", "   at OnlineBackup" })
                        if ((r.body ?? "").Contains(leak)) errors.Add(p.m + " " + p.p + " leaks " + (leak == env.Root ? "the server's folder path" : leak));
                }
                Assert.True(errors.Count == 0, "server errors / leaks:\n" + string.Join("\n", errors));
                Assert.Equal(200, Raw(url + "/api/brand", "GET", null).status);
            }
        }
    }
}
