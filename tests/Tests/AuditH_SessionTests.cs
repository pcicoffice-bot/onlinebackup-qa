using System;
using System.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// QA agent H — AU-01 / AU-02 (integration, real server over HTTP): a sign-in must end when the reason for it ends —
    /// sign-out, a removed / disabled administrator, a disabled reseller, a suspended customer, a customer whose fixed
    /// addresses changed — and a one-time code is used once.
    /// </summary>
    public class AuditH_SessionTests
    {
        static int Status(Action a) { try { a(); return 200; } catch (AgentException e) { return e.Status; } }
        static string Code(Action a) { try { a(); return "OK"; } catch (AgentException e) { return e.Code; } }

        // ------------------------------------------------------------------ AU-01 administrators

        [Fact]
        public void AdminTwoStepCode_IsAcceptedOnlyOnce_NotReplayedWithinItsWindow()
        {
            using (var env = new Env())
            {
                env.Admin();                                               // enrols the two-step secret of "admin"
                var secret = TestAuth.Secret(env.Url, "admin");
                // wait for the start of a fresh 30 s step, so both sign-ins below use the same code inside its own window
                var now = DateTime.UtcNow; var into = (int)(now - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds % 30;
                if (into > 20) System.Threading.Thread.Sleep((31 - into) * 1000);
                var code = TestAuth.Code(env.Url, "admin");   // owner decision 122 (A): the step that confirmed the set-up is used up - the next unused code
                var first = new Client(env.Url) { Retries = 0 }.Call("POST", "/api/admin/login", new Msg().Set("login", "admin").Set("password", "Admin-Pass-1").Set("otp", code));
                Assert.NotNull(first["session"]);
                // an observer of the first sign-in (shoulder, proxy log, phishing page) replays the same code at once
                var replay = Status(() => new Client(env.Url) { Retries = 0 }.Call("POST", "/api/admin/login", new Msg().Set("login", "admin").Set("password", "Admin-Pass-1").Set("otp", code)));
                Assert.True(replay == 401, "the same two-step code was accepted a second time (status " + replay + ")");
            }
        }

        [Fact]
        public void DeletedAdministrator_OpenSessionEndsAtOnce()
        {
            using (var env = new Env())
            {
                var admin = env.Admin();
                admin.Call("POST", "/api/admin/staff", new Msg().Set("login", "dana").Set("name", "Dana").Set("password", "Dana-Pass-1"));
                var dana = TestAuth.Admin(env.Url, "dana", "Dana-Pass-1");
                Assert.Equal(200, Status(() => dana.Call("GET", "/api/admin/users")));
                admin.Call("POST", "/api/admin/staff/dana/delete");
                Assert.Equal(401, Status(() => new Client(env.Url) { Retries = 0 }.Call("POST", "/api/admin/login", new Msg().Set("login", "dana").Set("password", "Dana-Pass-1"))));   // sign-in refused (control)
                var after = Status(() => dana.Call("GET", "/api/admin/users"));
                Assert.True(after == 401, "a deleted administrator's open session still works: GET /api/admin/users → " + after);
            }
        }

        [Fact]
        public void DisabledAdministrator_OpenSessionEndsAtOnce()
        {
            using (var env = new Env())
            {
                var admin = env.Admin();
                admin.Call("POST", "/api/admin/staff", new Msg().Set("login", "eli").Set("name", "Eli").Set("password", "Eli-Pass-12"));
                var eli = TestAuth.Admin(env.Url, "eli", "Eli-Pass-12");
                Assert.Equal(200, Status(() => eli.Call("GET", "/api/admin/staff")));
                admin.Call("POST", "/api/admin/staff", new Msg().Set("login", "eli").Set("name", "Eli").Set("disabled", "1"));
                var after = Status(() => eli.Call("POST", "/api/admin/staff", new Msg().Set("login", "eli2").Set("name", "Backdoor").Set("password", "Back-Door-1")));
                Assert.True(after == 401, "a disabled administrator's open session can still add administrators: " + after);
            }
        }

        [Fact]
        public void DisabledReseller_ItsAdministratorsOpenSessionEndsAtOnce()
        {
            using (var env = new Env())
            {
                var sys = env.Admin();
                sys.Call("POST", "/api/admin/vendors", new Msg().Set("id", "beta").Set("name", "Beta"));
                sys.Call("POST", "/api/admin/vendors/beta/admins", new Msg().Set("login", "beta-admin").Set("password", "Beta-Admin-Pass-1"));
                var beta = TestAuth.Admin(env.Url, "beta-admin", "Beta-Admin-Pass-1", 0);
                Assert.Equal(200, Status(() => beta.Call("POST", "/api/admin/users", new Msg().Set("login", "beta-c1").Set("password", "Customer-Pass-1").Set("alias", "c1").Set("quotaGB", 1))));
                sys.Call("POST", "/api/admin/vendors", new Msg().Set("id", "beta").Set("disabled", "1"));
                var after = Status(() => beta.Call("GET", "/api/admin/users"));
                Assert.True(after == 401 || after == 403, "a disabled reseller's administrator still reads its customers: " + after);
            }
        }

        // ------------------------------------------------------------------ AU-02 customers

        static Client CustomerSession(Env env, string login)
        {
            var c = new Client(env.Url) { Retries = 0 };
            c.Session = c.Call("POST", "/api/login", new Msg().Set("login", login).Set("password", "Customer-Pass-1"))["session"];
            return c;
        }

        [Fact]
        public void CustomerSignOut_EndsTheSessionOnTheServer()
        {
            using (var env = new Env())
            {
                env.CreateUser("anna", "Customer-Pass-1");
                var c = CustomerSession(env, "anna");
                Assert.Equal(200, Status(() => c.Call("GET", "/api/profile")));
                // the web restore page and the client window have "Sign out": the server must forget the session too
                var bye = Status(() => c.Call("POST", "/api/logout"));
                var after = Status(() => c.Call("GET", "/api/profile"));
                Assert.True(bye == 200 && after == 401, "customer sign-out: POST /api/logout → " + bye + ", the same session afterwards → " + after);
            }
        }

        [Fact]
        public void SuspendedCustomer_OpenSessionIsRefused_LikeItsDeviceToken()
        {
            using (var env = new Env())
            {
                env.CreateUser("susan", "Customer-Pass-1");
                var app = env.Agent("susan", "Customer-Pass-1");
                var c = CustomerSession(env, "susan");
                Assert.Equal(200, Status(() => c.Call("GET", "/api/profile")));
                var users = env.Api.UserStore;
                lock (users.ProfileLock) { var p = users.LoadProfile("susan"); p.SetAttr("DISABLED", "Y"); users.SaveProfile("susan", p); }
                // control: the device token of the same customer is refused at once
                var dev = new Client(env.Url) { Retries = 0, Device = app.Home.DeviceToken };
                Assert.Equal(403, Status(() => dev.Call("GET", "/api/profile")));
                var after = Status(() => c.Call("GET", "/api/profile"));
                Assert.True(after == 403 || after == 401, "a suspended customer's open session still works: " + after);
            }
        }

        [Fact]
        public void CustomerFixedAddresses_AlsoApplyToAnOpenSession()
        {
            using (var env = new Env())
            {
                env.CreateUser("fixed", "Customer-Pass-1");
                var app = env.Agent("fixed", "Customer-Pass-1");
                var c = CustomerSession(env, "fixed");
                Assert.Equal(200, Status(() => c.Call("GET", "/api/profile")));
                env.Admin().Call("POST", "/api/admin/users/fixed/security", new Msg().Set("allowedIps", "203.0.113.10"));
                var dev = new Client(env.Url) { Retries = 0, Device = app.Home.DeviceToken };
                Assert.Equal(403, Status(() => dev.Call("GET", "/api/profile")));                // control: the device token is refused
                var after = Status(() => c.Call("GET", "/api/profile"));
                Assert.True(after == 403, "a session from an address that is no longer allowed still works: " + after);
            }
        }

        [Fact]
        public void CustomerTwoStepCode_IsAcceptedOnlyOnce()
        {
            using (var env = new Env())
            {
                env.CreateUser("otpuser", "Customer-Pass-1");
                var c = CustomerSession(env, "otpuser");
                var r = c.Call("POST", "/api/totp/enable", new Msg());
                c.Call("POST", "/api/totp/confirm", new Msg().Set("code", Totp.Code(r["secret"], DateTime.UtcNow.AddSeconds(-30))));   // owner decision 122 (A): the confirming code is used up - confirmed with the code shown a moment earlier
                var into = (int)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds % 30;
                if (into > 20) System.Threading.Thread.Sleep((31 - into) * 1000);
                var code = Totp.Code(r["secret"], DateTime.UtcNow);
                Func<string> login = () => Code(() => new Client(env.Url) { Retries = 0 }.Call("POST", "/api/login", new Msg().Set("login", "otpuser").Set("password", "Customer-Pass-1").Set("otp", code)));
                Assert.Equal("OK", login());
                var again = login();
                Assert.True(again == "LOGIN", "the same customer two-step code was accepted a second time: " + again);
            }
        }

        [Fact]
        public void RevokedComputer_DeviceTokenIsRefused_OtherComputerKeepsWorking()
        {
            // coverage (AU-02, integration): "revoking one device leaves the others" — checked over HTTP with the real tokens
            using (var env = new Env())
            {
                env.CreateUser("twopc", "Customer-Pass-1");
                var a = env.Agent("twopc", "Customer-Pass-1", name: "pc-a");
                var b = env.Agent("twopc", "Customer-Pass-1", name: "pc-b");
                var ca = new Client(env.Url) { Retries = 0, Device = a.Home.DeviceToken };
                var cb = new Client(env.Url) { Retries = 0, Device = b.Home.DeviceToken };
                Assert.Equal(200, Status(() => ca.Call("GET", "/api/profile")));
                env.Admin().Call("POST", "/api/admin/users/twopc/computers/disconnect", new Msg().Set("computer", "PC-pc-a"));
                Assert.Equal(401, Status(() => ca.Call("GET", "/api/profile")));
                Assert.Equal(200, Status(() => cb.Call("GET", "/api/profile")));
                // a token with its secret changed by one character is refused (not only an unknown id)
                var t = b.Home.DeviceToken; var forged = t.Substring(0, t.Length - 1) + (t.EndsWith("0") ? "1" : "0");
                Assert.Equal(401, Status(() => new Client(env.Url) { Retries = 0, Device = forged }.Call("GET", "/api/profile")));
            }
        }
    }
}
