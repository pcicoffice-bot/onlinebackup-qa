using System;
using System.IO;
using System.Linq;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// Sign-in and sessions alone (Users, no web server) — component contract (tests/QA/specs.py AU-01 / AU-02):
    ///   purpose  only the right person gets in, with the second step when it is on; guessing locks the user; a session
    ///            ends when it should
    ///   input    a customer "anna" with password P; wrong passwords; an unknown user; two-step switched on; a backup
    ///            code used twice; sessions at +1 min, +13 h, after sign-out; an administrator session used and not used
    ///   expected P signs in; a wrong password and an unknown user get the same 401; after 3 wrong ones even P gets 423 until
    ///            30 minutes passed, then P works and the count starts again; with two-step: no code = OTP_REQUIRED,
    ///            a wrong code = 401 (and counts), the right code or an unused backup code = in, a used backup code = 401;
    ///            a customer session lives 12 h, ends at sign-out; an administrator session away from the server ends 2 h
    ///            after its last use and lives on while used
    /// </summary>
    public class AuthComponentTests : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "obauth-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        readonly SystemConfig cfg; readonly Users users;
        DateTime now = DateTime.UtcNow;

        public AuthComponentTests()
        {
            SystemClock.Use(() => now);
            cfg = SystemConfig.Init(Path.Combine(root, "system"), "admin", "Admin-Pass-1", "localhost", new[] { Path.Combine(root, "home") + "|UNLIMITED|100" });
            users = new Users(cfg);
            users.Create("anna", "Anna-Pass-1", "Anna", null, "COMPRESSED", "anna@example.com", "203.0.113.5");
        }
        public void Dispose() { SystemClock.Use(null); try { Directory.Delete(root, true); } catch (Exception) { } }

        int Status(Action a) { try { a(); return 200; } catch (ApiException e) { return e.Status; } }
        string Code(Action a) { try { a(); return "OK"; } catch (ApiException e) { return e.Code; } }

        [Fact]
        public void RightPasswordIn_WrongOrUnknownOut_WithTheSameAnswer()
        {
            Assert.NotNull(users.CheckUser("anna", "Anna-Pass-1", null, "203.0.113.5"));
            ApiException wrong = null, unknown = null;
            try { users.CheckUser("anna", "Anna-Pass-2", null, "203.0.113.5"); } catch (ApiException e) { wrong = e; }
            try { users.CheckUser("nobody", "Anna-Pass-1", null, "203.0.113.5"); } catch (ApiException e) { unknown = e; }
            Assert.Equal(401, wrong.Status); Assert.Equal(401, unknown.Status);
            Assert.Equal(wrong.Code, unknown.Code);
            Assert.Equal(401, Status(() => users.CheckUser("anna", "", null, "203.0.113.5")));
            Assert.Equal(401, Status(() => users.CheckUser("anna", null, null, "203.0.113.5")));
        }

        [Fact]
        public void ThreeWrongPasswords_LockEvenTheRightOne_UntilTheTimePasses_ThenTheCountStartsAgain()
        {
            for (int i = 0; i < 3; i++) Assert.Equal(401, Status(() => users.CheckUser("anna", "wrong-" + i, null, "198.51.100.7")));
            Assert.Equal(423, Status(() => users.CheckUser("anna", "Anna-Pass-1", null, "198.51.100.7")));
            now = now.AddMinutes(29);
            Assert.Equal(423, Status(() => users.CheckUser("anna", "Anna-Pass-1", null, "198.51.100.7")));
            now = now.AddMinutes(2);
            Assert.NotNull(users.CheckUser("anna", "Anna-Pass-1", null, "198.51.100.7"));
            // the count started again: two wrong ones do not lock
            for (int i = 0; i < 2; i++) Assert.Equal(401, Status(() => users.CheckUser("anna", "wrong-" + i, null, "198.51.100.7")));
            Assert.NotNull(users.CheckUser("anna", "Anna-Pass-1", null, "198.51.100.7"));
        }

        [Fact]
        public void TheLockCannotBeWeakened_ByTheCustomersOwnSetting()
        {
            var p = users.LoadProfile("anna"); p.SetAttr("LOCK_ATTEMPTS", 1000); p.SetAttr("LOCK_MINUTES", 1); users.SaveProfile("anna", p);
            for (int i = 0; i < Users.MaxLockAttempts; i++) Status(() => users.CheckUser("anna", "wrong", null, "198.51.100.7"));
            Assert.Equal(423, Status(() => users.CheckUser("anna", "Anna-Pass-1", null, "198.51.100.7")));
            now = now.AddMinutes(2);
            Assert.Equal(423, Status(() => users.CheckUser("anna", "Anna-Pass-1", null, "198.51.100.7")));   // at least 5 minutes
        }

        [Fact]
        public void TwoStep_NoCodeOrWrongCodeRefused_RightCodeIn_BackupCodeOnlyOnce()
        {
            var setup = users.EnableTotp("anna", "203.0.113.5");
            var secret = setup["secret"];
            Assert.Equal("OTP", Code(() => users.ConfirmTotp("anna", "000000", "203.0.113.5")));
            users.ConfirmTotp("anna", Totp.Code(secret, DateTime.UtcNow.AddSeconds(-30)), "203.0.113.5"); // owner decision 122 (A): the confirming code is used up - confirmed with the code shown a moment earlier (the step before is accepted), the sign-in below takes the current one
            Assert.Equal("OTP_REQUIRED", Code(() => users.CheckUser("anna", "Anna-Pass-1", null, "203.0.113.5")));
            Assert.Equal("LOGIN", Code(() => users.CheckUser("anna", "Anna-Pass-1", "123456", "203.0.113.5")));
            Assert.NotNull(users.CheckUser("anna", "Anna-Pass-1", Totp.Code(secret, DateTime.UtcNow), "203.0.113.5"));
            var backup = setup.List("codes")[0]["code"];
            Assert.NotNull(users.CheckUser("anna", "Anna-Pass-1", backup, "203.0.113.5"));
            Assert.Equal("LOGIN", Code(() => users.CheckUser("anna", "Anna-Pass-1", backup, "203.0.113.5")));
            // a wrong password with the right code is still refused
            Assert.Equal("LOGIN", Code(() => users.CheckUser("anna", "Anna-Pass-2", Totp.Code(secret, DateTime.UtcNow), "203.0.113.5")));
        }

        [Fact]
        public void Sessions_LiveTheirTime_EndAtSignOut_AdministratorsSlideWhileUsed()
        {
            var t = users.NewSession("anna", false);
            Assert.Equal("anna", users.GetSession(t).Login);
            now = now.AddHours(11);
            Assert.NotNull(users.GetSession(t));
            now = now.AddHours(2);
            Assert.Null(users.GetSession(t));                          // 12 hours: over

            var t2 = users.NewSession("anna", false);
            users.EndSession(t2);
            Assert.Null(users.GetSession(t2));
            Assert.Null(users.GetSession("not-a-token"));
            Assert.Null(users.GetSession(null));

            var acc = Staff.Find(cfg, "admin");
            var a = users.NewStaffSession(new Staff.SignIn { Account = acc }, false);
            for (int i = 0; i < 5; i++) { now = now.AddMinutes(100); Assert.NotNull(users.GetSession(a)); }   // used every 100 minutes: 8 hours, still in
            now = now.AddMinutes(125);
            Assert.Null(users.GetSession(a));                          // not used for 2 hours: over
            var a2 = users.NewStaffSession(new Staff.SignIn { Account = acc }, false);
            users.EndSession(a2);
            Assert.Null(users.GetSession(a2));
            Assert.Null(new Users(cfg).GetSession(a2));                // and not back after a restart
        }

        [Fact]
        public void ASuspendedCustomer_CannotSignIn_EvenWithTheRightPassword()
        {
            var p = users.LoadProfile("anna"); p.SetAttr("DISABLED", "Y"); users.SaveProfile("anna", p);
            Assert.Equal(403, Status(() => users.CheckUser("anna", "Anna-Pass-1", null, "203.0.113.5")));
        }
    }
}
