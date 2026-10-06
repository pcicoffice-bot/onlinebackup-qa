using System;
using System.Collections.Concurrent;
using OnlineBackup.Agent;
using OnlineBackup.Core;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// TECH-010 in the tests: every administrator must set up two-step verification at the first sign-in. The first sign-in
    /// of each test account sets it up (the secret is kept for the test run); later sign-ins send the code.
    /// </summary>
    public static class TestAuth
    {
        static readonly ConcurrentDictionary<string, string> secrets = new ConcurrentDictionary<string, string>();

        public static Client Admin(string url, string login, string password, int? retries = null)
        {
            var c = new Client(url); if (retries.HasValue) c.Retries = retries.Value;
            string secret; secrets.TryGetValue(url + "|" + login, out secret);
            var r = c.Call("POST", "/api/admin/login", new Msg().Set("login", login).Set("password", password).Set("otp", secret == null ? null : Totp.Code(secret, DateTime.UtcNow)));
            c.Session = r["session"];
            if (r["enroll"] == "1")
            {
                secret = c.Call("POST", "/api/admin/totp/enable")["secret"];
                c.Call("POST", "/api/admin/totp/confirm", new Msg().Set("code", Totp.Code(secret, DateTime.UtcNow)));
                secrets[url + "|" + login] = secret;
            }
            return c;
        }

        /// <summary>The two-step secret of a test administrator (kept with an upgrade fixture, which is test data only).</summary>
        public static string Secret(string url, string login) { string s; return secrets.TryGetValue(url + "|" + login, out s) ? s : null; }
        public static void Remember(string url, string login, string secret) { secrets[url + "|" + login] = secret; }

        public static string Code(string url, string login) { string s; return secrets.TryGetValue(url + "|" + login, out s) ? Totp.Code(s, DateTime.UtcNow) : null; }
    }
}
