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
        static readonly ConcurrentDictionary<string, long> used = new ConcurrentDictionary<string, long>();

        /// <summary>H-02: the server takes each code once, as a person's next sign-in takes the next code from the phone. The
        /// code of the next unused 30-second step (the step after the current one is accepted for drift); when both are used,
        /// waits for a new step.</summary>
        public static string Fresh(string key, string secret)
        {
            lock (used)
            {
                for (;;)
                {
                    long now = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds / 30, last;
                    if (!used.TryGetValue(key, out last)) last = -1;
                    var step = Math.Max(now, last + 1);
                    if (step <= now + 1) { used[key] = step; return Totp.Code(secret, new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(step * 30)); }
                    System.Threading.Thread.Sleep(1000);
                }
            }
        }

        public static Client Admin(string url, string login, string password, int? retries = null)
        {
            var c = new Client(url); if (retries.HasValue) c.Retries = retries.Value;
            string secret; secrets.TryGetValue(url + "|" + login, out secret);
            var r = c.Call("POST", "/api/admin/login", new Msg().Set("login", login).Set("password", password).Set("otp", secret == null ? null : Fresh(url + "|" + login, secret)));
            c.Session = r["session"];
            if (r["enroll"] == "1")
            {
                secret = c.Call("POST", "/api/admin/totp/enable")["secret"];
                c.Call("POST", "/api/admin/totp/confirm", new Msg().Set("code", Fresh(url + "|" + login, secret)));
                secrets[url + "|" + login] = secret;
            }
            return c;
        }

        /// <summary>The two-step secret of a test administrator (kept with an upgrade fixture, which is test data only).</summary>
        public static string Secret(string url, string login) { string s; return secrets.TryGetValue(url + "|" + login, out s) ? s : null; }
        public static void Remember(string url, string login, string secret) { secrets[url + "|" + login] = secret; }

        public static string Code(string url, string login) { string s; return secrets.TryGetValue(url + "|" + login, out s) ? Fresh(url + "|" + login, s) : null; }
    }
}
