using System;
using System.IO;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>SEC-020/030: a customer's fixed addresses for backup and restore, and its own lock after wrong passwords.</summary>
    public class SecurityTests
    {
        [Fact]
        public void AddressRanges_MatchExactlyWhatIsAllowed()
        {
            Assert.True(Users.IpAllowed(new[] { "198.51.100.0/24" }, "198.51.100.77"));
            Assert.False(Users.IpAllowed(new[] { "198.51.100.0/24" }, "198.51.101.1"));
            Assert.True(Users.IpAllowed(new[] { "203.0.113.10" }, "::ffff:203.0.113.10"));
            Assert.False(Users.IpAllowed(new[] { "203.0.113.10" }, "203.0.113.11"));
            Assert.True(Users.IpAllowed(new[] { "10.0.0.0/8", "2001:db8::/32" }, "2001:db8:1::5"));
            Assert.Equal("203.0.113.10,198.51.100.0/24", Users.NormalizeIps("203.0.113.10\n198.51.100.0/24, 203.0.113.10"));
            Assert.Throws<ApiException>(() => Users.NormalizeIps("10.0.0.1/40"));
            Assert.Throws<ApiException>(() => Users.NormalizeIps("backup.example.com"));
        }

        [Fact]
        public void FixedAddresses_BlockBackupAndSignIn_FromAnywhereElse_AndOwnLockPolicy()
        {
            using (var env = new Env())
            {
                env.CreateUser("secure", "Customer-Pass-1");
                var app = env.Agent("secure", "Customer-Pass-1");
                var src = env.Dir("src"); File.WriteAllText(Path.Combine(src, "a.txt"), "a");
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Key-Pass-1", new BackupSetInfo { Name = "Docs", Sources = { src } });
                var admin = env.Admin();
                Assert.ThrowsAny<Exception>(() => admin.Call("POST", "/api/admin/users/secure/security", new Msg().Set("allowedIps", "not-an-ip")));

                // only the office address: this test machine (127.0.0.1) is refused for backup, sign-in and restore
                admin.Call("POST", "/api/admin/users/secure/security", new Msg().Set("allowedIps", "203.0.113.10"));
                Assert.Contains("address", Assert.ThrowsAny<Exception>(() => app.Backup(set.Id)).Message);
                var c = new Client(env.Url);
                var e = Assert.ThrowsAny<Exception>(() => c.Call("POST", "/api/login", new Msg().Set("login", "secure").Set("password", "Customer-Pass-1")));
                Assert.Contains("address", e.Message);
                // a range that includes it: everything works again
                admin.Call("POST", "/api/admin/users/secure/security", new Msg().Set("allowedIps", "127.0.0.0/8\n::1\n203.0.113.10"));   // "localhost" may be IPv4 or IPv6
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);
                Assert.Contains("127.0.0.0/8", admin.Call("GET", "/api/admin/users").List("users")[0]["allowedIps"]);

                // the customer's own lock: 2 wrong passwords, locked until an administrator unlocks (0 minutes)
                admin.Call("POST", "/api/admin/users/secure/security", new Msg().Set("lockAttempts", "2").Set("lockMinutes", "0"));
                for (int i = 0; i < 2; i++) Assert.ThrowsAny<Exception>(() => c.Call("POST", "/api/login", new Msg().Set("login", "secure").Set("password", "wrong-" + i)));
                var locked = Assert.ThrowsAny<Exception>(() => c.Call("POST", "/api/login", new Msg().Set("login", "secure").Set("password", "Customer-Pass-1")));
                Assert.Contains("locked", locked.Message);
                env.Api.UserStore.Unlock("secure", "admin", "127.0.0.1");
                Assert.NotNull(c.Call("POST", "/api/login", new Msg().Set("login", "secure").Set("password", "Customer-Pass-1"))["session"]);
            }
        }

        [Fact]
        public void Customer_TurnsTwoStepVerificationOnAndOff_ProviderCanRequireIt()
        {
            using (var env = new Env())
            {
                env.CreateUser("twostep", "Customer-Pass-1");
                var app = env.Agent("twostep", "Customer-Pass-1");
                var c = app.Interactive("Customer-Pass-1", null);
                Assert.Equal("N", app.Profile().Get("TOTP_ON"));
                var r = c.Call("POST", "/api/totp/enable", new Msg());
                Assert.StartsWith("otpauth://totp/", r["uri"]); Assert.Contains(":twostep?secret=" + r["secret"] + "&issuer=", r["uri"]); Assert.Equal(10, r.List("codes").Count);
                Assert.ThrowsAny<Exception>(() => c.Call("POST", "/api/totp/confirm", new Msg().Set("code", "000000")));
                c.Call("POST", "/api/totp/confirm", new Msg().Set("code", Totp.Code(r["secret"], DateTime.UtcNow)));
                Assert.Equal("Y", app.Profile().Get("TOTP_ON"));
                Assert.Null(app.Profile().Get("TOTP_SECRET"));                                     // the secret never reaches the computer
                Assert.ThrowsAny<Exception>(() => app.Interactive("Customer-Pass-1", null));       // now the code is needed
                var c2 = app.Interactive("Customer-Pass-1", Totp.Code(r["secret"], DateTime.UtcNow));
                Assert.NotNull(app.Interactive("Customer-Pass-1", r.List("codes")[0]["code"]).Session);   // a backup code works once
                Assert.ThrowsAny<Exception>(() => app.Interactive("Customer-Pass-1", r.List("codes")[0]["code"]));
                // the provider requires it: it cannot be switched off
                env.Admin().Call("POST", "/api/admin/users/twostep/security", new Msg().Set("requireTotp", "1"));
                Assert.Contains("requires", Assert.ThrowsAny<Exception>(() => c2.Call("POST", "/api/totp/disable", new Msg())).Message);
                env.Admin().Call("POST", "/api/admin/users/twostep/security", new Msg().Set("requireTotp", "0"));
                c2.Call("POST", "/api/totp/disable", new Msg());
                Assert.Equal("N", app.Profile().Get("TOTP_ON"));
                Assert.NotNull(app.Interactive("Customer-Pass-1", null).Session);
            }
        }
    }
}
