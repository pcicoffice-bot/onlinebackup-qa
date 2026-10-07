using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// QA agent R (night round R) — AU-06 / AU-07, tenant isolation of SESSIONS over the real server (HTTP in-process).
    /// Focus: things the happy-path tests do not cover — a session token outliving the identity it was issued for.
    /// Agent H already proved one live customer/reseller cannot reach another's data by id/case/encoding/path; this file
    /// covers what happens to a session when the account behind it is deleted and the login name is later reused.
    /// </summary>
    public class NightR_IsolationTests
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

        /// <summary>
        /// AU-07 (isolation): a customer's session token is bound to a login NAME only, not to the account's identity.
        /// When an administrator deletes the customer and the same login name is later re-provisioned (here under a
        /// different reseller), the deleted customer still holds a valid session token — and that token reaches the NEW
        /// customer's profile and backups. A deleted account's session must not survive its deletion, and must never be
        /// usable against a different account that happens to reuse the name.
        ///
        /// Oracle: outside the product's own success text — the data the old token reads (ALIAS / OWNER in the profile XML)
        /// belongs to the re-provisioned account, which is owned by a different reseller than the one that issued the token.
        /// </summary>
        [Fact]
        public void CustomerSession_SurvivesDeletion_AndReachesAReusedLoginOfAnotherReseller()
        {
            using (var env = new Env())
            {
                var sys = env.Admin();
                sys.Call("POST", "/api/admin/vendors", new Msg().Set("id", "acme").Set("name", "Acme IT"));
                sys.Call("POST", "/api/admin/vendors", new Msg().Set("id", "beta").Set("name", "Beta IT"));

                // reseller acme gets a customer; the customer signs in and keeps a session token
                sys.Call("POST", "/api/admin/users", new Msg().Set("login", "reuse2026").Set("password", "Customer-Pass-1").Set("alias", "AcmeCustomer").Set("quotaGB", 1).Set("vendor", "acme"));
                var c = new Client(env.Url) { Retries = 0 };
                var token = c.Call("POST", "/api/login", new Msg().Set("login", "reuse2026").Set("password", "Customer-Pass-1"))["session"];
                c.Session = token;

                // control: the token works now and reads acme's customer
                var before = c.Call("GET", "/api/profile")["profile"];
                Assert.Contains("AcmeCustomer", before);
                Assert.Contains("acme", before);

                // the system administrator deletes the customer (single admin → immediate, no second approval)
                sys.Call("POST", "/api/admin/users/reuse2026/delete", new Msg());
                Assert.DoesNotContain("reuse2026", string.Join(",", env.Api.UserStore.Logins()));

                // the same login name is re-provisioned later — a DIFFERENT customer, under a DIFFERENT reseller
                sys.Call("POST", "/api/admin/users", new Msg().Set("login", "reuse2026").Set("password", "Different-Pass-9").Set("alias", "BetaCustomer").Set("quotaGB", 1).Set("vendor", "beta"));

                // the deleted customer's old session token must no longer work, and must certainly not read beta's account
                int status; string profile = "";
                try { profile = c.Call("GET", "/api/profile")["profile"] ?? ""; status = 200; }
                catch (AgentException e) { status = e.Status; }

                Assert.True(status != 200,
                    "the deleted customer's session token still authenticates after the login name was reused: GET /api/profile returned 200 reaching the re-provisioned account owned by another reseller. profile=" + profile);
                Assert.DoesNotContain("BetaCustomer", profile);
            }
        }

        /// <summary>
        /// AU-06 (coverage, currently passing): the SAME customer's own logout really ends the session on the server —
        /// the token is rejected afterwards (H-04). This is the baseline the test above contrasts with, and shows that a
        /// token the server CAN invalidate is invalidated; deletion simply does not run the same revocation.
        /// </summary>
        [Fact]
        public void CustomerSession_AfterLogout_IsRejected()
        {
            using (var env = new Env())
            {
                env.CreateUser("logout2026", "Customer-Pass-1");
                var c = new Client(env.Url) { Retries = 0 };
                c.Session = c.Call("POST", "/api/login", new Msg().Set("login", "logout2026").Set("password", "Customer-Pass-1"))["session"];
                Assert.Equal(200, Raw(env.Url.TrimEnd('/') + "/api/profile", "GET", null, new Dictionary<string, string> { { "X-Session", c.Session } }).status);
                c.Call("POST", "/api/logout", new Msg());
                var after = Raw(env.Url.TrimEnd('/') + "/api/profile", "GET", null, new Dictionary<string, string> { { "X-Session", c.Session } });
                Assert.True(after.status == 401, "a logged-out session token is still accepted: " + after.status + " " + after.body);
            }
        }
    }
}
