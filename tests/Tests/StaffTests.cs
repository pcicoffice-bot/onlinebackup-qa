using System.IO;
using System;
using System.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>TECH-010: administrators only (no roles) — two-step mandatory for each, lock after wrong passwords.</summary>
    public class StaffTests
    {
        static int Status(Action a) { try { a(); return 200; } catch (AgentException e) { return e.Status; } }

        [Fact]
        public void TwoStep_IsMandatory_NothingOpensBeforeItIsSetUp()
        {
            using (var env = new Env())
            {
                var c = new Client(env.Url) { Retries = 0 };
                var r = c.Call("POST", "/api/admin/login", new Msg().Set("login", "admin").Set("password", "Admin-Pass-1"));
                Assert.Equal("1", r["enroll"]); c.Session = r["session"];
                Assert.Equal(403, Status(() => c.Call("GET", "/api/admin/users")));               // only the set-up
                Assert.Equal("1", c.Call("GET", "/api/admin/me")["enroll"]);
                var secret = c.Call("POST", "/api/admin/totp/enable")["secret"];
                Assert.Equal(400, Status(() => c.Call("POST", "/api/admin/totp/confirm", new Msg().Set("code", "000000"))));
                c.Call("POST", "/api/admin/totp/confirm", new Msg().Set("code", Totp.Code(secret, DateTime.UtcNow)));
                c.Call("GET", "/api/admin/users");                                                  // now everything opens
                var c2 = new Client(env.Url) { Retries = 0 };
                Assert.Equal(401, Status(() => c2.Call("POST", "/api/admin/login", new Msg().Set("login", "admin").Set("password", "Admin-Pass-1"))));   // a code is needed from now on
                Assert.Equal("0", c2.Call("POST", "/api/admin/login", new Msg().Set("login", "admin").Set("password", "Admin-Pass-1").Set("otp", Totp.Code(secret, DateTime.UtcNow)))["enroll"]);
            }
        }

        [Fact]
        public void FixedAddress_SignsInWithoutTheCode_OthersStillNeedIt()
        {
            // SEC-100 (owner): "a fixed address that does not ask for two-step"
            using (var env = new Env())
            {
                var admin = env.Admin();   // has two-step
                Func<string, int> signIn = otp => Status(() => new Client(env.Url) { Retries = 0 }.Call("POST", "/api/admin/login", new Msg().Set("login", "admin").Set("password", "Admin-Pass-1").Set("otp", otp ?? "")));
                Assert.Equal(401, signIn(null));                                                                   // no code: refused
                admin.Call("POST", "/api/admin/guard", new Msg().Set("on", 1).Set("noCode", "127.0.0.1, ::1"));
                Assert.Equal(200, signIn(null));                                                                   // from the listed address: the password is enough
                Assert.Contains("127.0.0.1", admin.Call("GET", "/api/admin/guard")["noCode"]);
                admin.Call("POST", "/api/admin/guard", new Msg().Set("on", 1).Set("noCode", "203.0.113.9"));
                Assert.Equal(401, signIn(null));                                                                   // another address listed: the code again
                // SEC-110 (owner): on the server itself no code, and the sign-in is kept 30 days
                env.Cfg.Doc.Root.SetAttributeValue("LOCAL_TRUST", null);
                var r = new Client(env.Url) { Retries = 0 }.Call("POST", "/api/admin/login", new Msg().Set("login", "admin").Set("password", "Admin-Pass-1"));
                Assert.Equal("1", r["remember"]); Assert.False(string.IsNullOrEmpty(r["session"]));
                // SEC-120: the same sign-in after a restart (a new server process reads the kept, hashed sign-ins)
                var afterRestart = new Users(env.Cfg).GetSession(r["session"]);
                Assert.NotNull(afterRestart); Assert.Equal("admin", afterRestart.Login);
                Assert.Null(new Users(env.Cfg).GetSession("not-a-session"));
                Assert.DoesNotContain(r["session"], File.ReadAllText(Path.Combine(env.Cfg.SystemHome, "conf", "kept-sessions.xml")));   // only a hash is kept
                // SEC-130 (owner: "after the update it went back to the sign-in"): from outside too the sign-in outlives a restart,
                // and "Sign out" ends it on the server
                env.Cfg.Doc.Root.SetAttributeValue("LOCAL_TRUST", "N");
                var away = TestAuth.Admin(env.Url, "admin", "Admin-Pass-1");
                var kept = new Users(env.Cfg).GetSession(away.Session);
                Assert.NotNull(kept); Assert.True(kept.Expires < DateTime.UtcNow.AddHours(3));                    // 2 hours without use, not 30 days
                away.Call("POST", "/api/admin/logout");
                Assert.Null(new Users(env.Cfg).GetSession(away.Session));
                Assert.Equal(401, Status(() => away.Call("GET", "/api/admin/me")));
            }
        }

        [Fact]
        public void Administrators_AddChangeDelete_LockAndUnlock()
        {
            using (var env = new Env())
            {
                var admin = env.Admin();
                Assert.Equal(400, Status(() => admin.Call("POST", "/api/admin/staff", new Msg().Set("login", "dana").Set("password", "short1"))));      // 8+ with a letter
                Assert.Equal(400, Status(() => admin.Call("POST", "/api/admin/staff", new Msg().Set("login", "dana").Set("password", "Dana-Pass-1").Set("email", "not-mail"))));
                var list = admin.Call("POST", "/api/admin/staff", new Msg().Set("login", "dana").Set("name", "Dana Levi").Set("password", "Dana-Pass-1").Set("email", "dana@example.invalid"));
                Assert.Equal(2, list.List("staff").Count);
                Assert.Contains(list.List("staff"), x => x["login"] == "dana" && x["totp"] == "0");
                Assert.Equal(new[] { "dana@example.invalid" }, Staff.Mails(env.Cfg, "dana"));

                var dana = TestAuth.Admin(env.Url, "dana", "Dana-Pass-1");                         // signs in, sets up two-step, can do everything
                dana.Call("GET", "/api/admin/users");
                dana.Call("GET", "/api/admin/staff");

                // SEC-090: on the server itself wrong passwords never lock (the owner at the console)
                env.Cfg.Doc.Root.SetAttributeValue("LOCAL_TRUST", null);
                for (int i = 0; i < 10; i++) Status(() => new Client(env.Url) { Retries = 0 }.Call("POST", "/api/admin/login", new Msg().Set("login", "dana").Set("password", "wrong-1")));
                TestAuth.Admin(env.Url, "dana", "Dana-Pass-1");
                // from anywhere else wrong passwords lock the account (cannot be switched off); the main administrator unlocks it
                env.Cfg.Doc.Root.SetAttributeValue("LOCAL_TRUST", "N");
                {
                for (int i = 0; i < 10; i++) Status(() => new Client(env.Url) { Retries = 0 }.Call("POST", "/api/admin/login", new Msg().Set("login", "dana").Set("password", "wrong-1")));
                Assert.Equal(423, Status(() => new Client(env.Url) { Retries = 0 }.Call("POST", "/api/admin/login", new Msg().Set("login", "dana").Set("password", "Dana-Pass-1").Set("otp", TestAuth.Code(env.Url, "dana")))));
                admin.Call("POST", "/api/admin/staff/dana/unlock");
                TestAuth.Admin(env.Url, "dana", "Dana-Pass-1");
                }
                // the console on the server: unlock, and start two-step again
                for (int i = 0; i < 10; i++) Status(() => new Client(env.Url) { Retries = 0 }.Call("POST", "/api/admin/login", new Msg().Set("login", "dana").Set("password", "wrong-1")));
                Assert.Equal("dana", Staff.Unlock(env.Cfg, "dana", true));
                Assert.Equal("1", new Client(env.Url) { Retries = 0 }.Call("POST", "/api/admin/login", new Msg().Set("login", "dana").Set("password", "Dana-Pass-1"))["enroll"]);

                Assert.Equal(400, Status(() => admin.Call("POST", "/api/admin/staff/admin/delete")));   // the main administrator stays
                admin.Call("POST", "/api/admin/staff/dana/delete");
                Assert.Single(admin.Call("GET", "/api/admin/staff").List("staff"));
                Assert.Equal(401, Status(() => new Client(env.Url) { Retries = 0 }.Call("POST", "/api/admin/login", new Msg().Set("login", "dana").Set("password", "Dana-Pass-1"))));
            }
        }
    }
}
