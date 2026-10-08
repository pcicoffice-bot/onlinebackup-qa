using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// FAILING (bug candidates) found by the adversarial challenge of the pilot security tests. Each test states the contract it
    /// holds the product to; the cause in src/ is named in the report. Kept apart from the passing challenge tests.
    /// </summary>
    [Collection("Guard")]   // Guard's state is one per process: these classes never run at the same time
    public class PilotChallengeSecurityFailingTests : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "obchsecf-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        public void Dispose() { try { Directory.Delete(root, true); } catch (Exception) { } }

        SystemConfig NewServer(string name)
        {
            var home = Path.Combine(root, name);
            return SystemConfig.Init(Path.Combine(home, "system"), "admin", "Admin-Pass-1", "localhost", new[] { Path.Combine(home, "home") + "|UNLIMITED|100" });
        }
        static int Status(Action a) { try { a(); return 200; } catch (ApiException e) { return e.Status; } }

        // ================================================================== AU-04: the count of wrong sign-ins per address and what resets it

        /// <summary>AU-04 / GUARD-010: "FAILS (10) wrong sign-ins from it within 10 minutes" block the address. One good administrator
        /// sign-in from the same address (any administrator: a reseller's own account) wipes the whole count of that address —
        /// the wrong sign-ins for OTHER names too — so 9 guesses, one own sign-in, 9 guesses... are never blocked.</summary>
        [Fact]
        public void AU04_OneGoodAdministratorSignIn_DoesNotWipeTheWrongSignInsForOtherNames_FromTheSameAddress()
        {
            var cfg = NewServer("guard");
            const string ip = "203.0.113.90";
            var noon = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Local);
            for (int i = 0; i < 9; i++) Guard.Failed(cfg, ip, "anna");
            Assert.False(Guard.IsBlocked(cfg, ip));                                   // 9: not yet (as the contract says)
            Guard.SignedIn(cfg, "acme-admin", ip, noon);                              // the attacker's own reseller account
            for (int i = 0; i < 9; i++) Guard.Failed(cfg, ip, "anna");
            Assert.True(Guard.IsBlocked(cfg, ip), "18 wrong sign-ins for 'anna' from one address within the window were not blocked: one good sign-in of another account between them reset the count");
        }

        /// <summary>The same through the real server over HTTP: the control (10 wrong customer sign-ins block the address) passes
        /// first, then 9 wrong + one reseller administrator's good sign-in + 9 wrong are never blocked.</summary>
        [Fact]
        public void AU04_OverHttp_TenWrongSignInsBlock_ButAReseller_SigningInBetween_GuessesOnForEver()
        {
            using (var env = new Env())
            {
                var sys = env.Admin();
                env.CreateUser("anna", "Customer-Pass-1");
                sys.Call("POST", "/api/admin/vendors", new Msg().Set("id", "acme-it").Set("name", "Acme IT"));
                sys.Call("POST", "/api/admin/vendors/acme-it/admins", new Msg().Set("login", "acme-admin").Set("password", "Acme-Admin-Pass-1"));
                lock (env.Cfg) { env.Cfg.Doc.Root.Add(new System.Xml.Linq.XElement("GUARD", new System.Xml.Linq.XAttribute("TRUST_PRIVATE", "N"))); env.Cfg.Save(); }   // this computer is not trusted
                Func<int, int> wrongSignIn = i => PilotChallengeSecurityTests.Http(env.Url, "POST", "/api/login", body: new Msg().Set("login", "anna").Set("password", "wrong-" + i)).Status;

                // control: 10 wrong sign-ins from this address block it (the setting and the address are right)
                for (int i = 0; i < 10; i++) wrongSignIn(i);
                var blocked = PilotChallengeSecurityTests.Http(env.Url, "GET", "/api/brand");
                Assert.Equal(403, blocked.Status); Assert.Equal("BLOCKED", blocked.Body["error"]);
                var ip = Guard.List(env.Cfg).List("blocked").Single()["ip"];
                Assert.True(Guard.Unblock(env.Cfg, ip, "test", ip));
                Assert.Equal(200, PilotChallengeSecurityTests.Http(env.Url, "GET", "/api/brand").Status);

                // the attack: 9 wrong, the reseller's own good sign-in, 9 wrong — 18 wrong sign-ins within the window
                for (int i = 0; i < 9; i++) wrongSignIn(i);
                Assert.Equal(200, PilotChallengeSecurityTests.Http(env.Url, "POST", "/api/admin/login", body: new Msg().Set("login", "acme-admin").Set("password", "Acme-Admin-Pass-1")).Status);
                for (int i = 0; i < 9; i++) wrongSignIn(i);
                var after = PilotChallengeSecurityTests.Http(env.Url, "GET", "/api/brand");
                Assert.True(after.Status == 403 && after.Body["error"] == "BLOCKED", "after 18 wrong sign-ins within 10 minutes the address answers " + after.Status + " (not blocked)");
            }
        }

        // ================================================================== AU-01: an administrator's password changed — the old sign-ins

        /// <summary>AU-01 / H-01 ("a sign-in lasts only while its reason does"): when an administrator's password is changed (it
        /// leaked), the sign-ins opened with the old password must end — as they do when the account is disabled or removed.
        /// Today they live on (2 hours sliding away from the server, 30 days on it).</summary>
        [Fact]
        public void AU01_AnAdministratorsPasswordChanged_TheSignInsOpenedWithTheOldPasswordEnd()
        {
            var cfg = NewServer("pw"); var users = new Users(cfg);
            Staff.Save(cfg, new Msg().Set("login", "dana").Set("password", "Dana-Pass-11"), "admin", "10.0.0.1");
            Vendors.Save(cfg, new Msg().Set("id", "acme-it"));
            Vendors.SaveAdmin(cfg, "acme-it", "acme-admin", "Acme-Admin-Pass-1");
            Staff.Check(cfg, "dana", "Dana-Pass-11", null, "203.0.113.5", DateTime.UtcNow);          // the old password works
            Staff.Check(cfg, "acme-admin", "Acme-Admin-Pass-1", null, "203.0.113.5", DateTime.UtcNow);
            // their sessions after the two-step (as the server makes them after the code)
            var dana = users.NewStaffSession(new Staff.SignIn { Account = Staff.Find(cfg, "dana"), Enroll = false });
            var acme = users.NewStaffSession(new Staff.SignIn { Account = Staff.Find(cfg, "acme-admin"), Enroll = false });
            Assert.NotNull(users.GetSession(dana)); Assert.NotNull(users.GetSession(acme));

            // the main administrator changes both passwords (the old ones leaked)
            Staff.Save(cfg, new Msg().Set("login", "dana").Set("password", "Dana-New-Pass-22"), "admin", "10.0.0.1");
            Vendors.SaveAdmin(cfg, "acme-it", "acme-admin", "Acme-New-Pass-22");
            Assert.Equal(401, Status(() => Staff.Check(cfg, "dana", "Dana-Pass-11", null, "203.0.113.5", DateTime.UtcNow)));   // the old password is refused...

            var survivors = new List<string>();
            if (users.GetSession(dana) != null) survivors.Add("dana (staff) still signed in");
            if (users.GetSession(acme) != null) survivors.Add("acme-admin (reseller) still signed in");
            if (new Users(cfg).GetSession(dana) != null) survivors.Add("dana's sign-in still kept across a restart");
            Assert.Empty(survivors);                                                                 // ...but not what it opened
        }

        // ================================================================== AU-01 / AU-02: two-step code replay

        /// <summary>H-02: "each code opens one sign-in". The code typed to CONFIRM the two-step set-up is not remembered as used, so
        /// the same code (seen over a shoulder, in a proxy or browser log) opens one more sign-in with the password within its
        /// 30-90 seconds — for an administrator and for a customer.</summary>
        [Fact]
        public void AU01_TheCodeThatConfirmedTheTwoStepSetUp_DoesNotOpenASignIn()
        {
            var cfg = NewServer("otp"); var users = new Users(cfg);
            var replayed = new List<string>();

            Staff.Save(cfg, new Msg().Set("login", "dana").Set("password", "Dana-Pass-11"), "admin", "10.0.0.1");
            var secret = Staff.TotpEnable(cfg, "dana")["secret"];
            var code = Totp.Code(secret, DateTime.UtcNow);
            Staff.TotpConfirm(cfg, "dana", code, DateTime.UtcNow);
            if (Status(() => Staff.Check(cfg, "dana", "Dana-Pass-11", code, "203.0.113.5", DateTime.UtcNow)) == 200) replayed.Add("administrator: the confirmation code opened a sign-in");

            users.Create("anna", "Anna-Pass-1", "Anna", null, "COMPRESSED", "anna@example.com", "203.0.113.5");
            var csecret = users.EnableTotp("anna", "203.0.113.5")["secret"];
            var ccode = Totp.Code(csecret, DateTime.UtcNow);
            users.ConfirmTotp("anna", ccode, "203.0.113.5");
            if (Status(() => users.CheckUser("anna", "Anna-Pass-1", ccode, "203.0.113.5")) == 200) replayed.Add("customer: the confirmation code opened a sign-in");

            Assert.Empty(replayed);
        }

        // ================================================================== AU-07: path traversal in the log viewer

        /// <summary>AU-07 ("path-traversal requests → clear 4xx refusals"): the log viewer's "set" parameter goes into the folder path
        /// unchecked, so login=anna&set=../../bob/logs/&lt;bob's set&gt; lists and shows bob's job logs (file names of bob's data),
        /// and an absolute folder reads *.log files anywhere on the disk under a folder named Backup / Restore / Retention.
        /// Only the server's own administrators reach the log viewer (resellers are refused), so the reach is limited.</summary>
        [Fact]
        public void AU07_TheLogViewer_ASetThatClimbsOutOfTheCustomersFolder_IsRefused_NoOtherLogIsShown()
        {
            using (var env = new Env())
            {
                var sys = env.Admin();
                env.CreateUser("anna", "Customer-Pass-1"); env.CreateUser("bob", "Customer-Pass-2");
                var bob = env.Agent("bob", "Customer-Pass-2", name: "bobpc");
                var src = env.Dir("bobsrc"); File.WriteAllText(Path.Combine(src, "BobPrivateFileZ9.txt"), "bob's");
                var bset = bob.CreateSet(bob.Interactive("Customer-Pass-2", null), "Customer-Pass-2", new BackupSetInfo { Name = "Bob", Sources = { src } });
                Assert.Equal("BS_STOP_SUCCESS", bob.Backup(bset.Id).Result);
                // control: bob's own job log names his file
                var own = PilotChallengeSecurityTests.Http(env.Url, "GET", "/api/admin/logs?cat=Backup&login=bob&set=" + bset.Id, session: sys.Session);
                Assert.Equal(200, own.Status); Assert.Contains("BobPrivateFileZ9", own.Text);

                var shown = new List<string>();
                foreach (var set in new[] { "../../bob/logs/" + bset.Id, ".." + Path.DirectorySeparatorChar + ".." + Path.DirectorySeparatorChar + "bob" + Path.DirectorySeparatorChar + "logs" + Path.DirectorySeparatorChar + bset.Id,
                                            Path.Combine(env.HomeA, "bob", "logs", bset.Id) })
                {
                    var a = PilotChallengeSecurityTests.Http(env.Url, "GET", "/api/admin/logs?cat=Backup&login=anna&set=" + Uri.EscapeDataString(set), session: sys.Session);
                    if (a.Status == 200 && a.Text.Contains("BobPrivateFileZ9")) shown.Add("set=" + set + " showed bob's job log");
                    else if (a.Status < 400 || a.Status >= 500) shown.Add("set=" + set + " answered " + a.Status + " (not a clear refusal)");
                }
                Assert.Empty(shown);
            }
        }
    }

    /// <summary>UI-04 / H-04 ("the customer's Sign out ends the sign-in on the server too"): the client window's Sign out only
    /// forgets the session in the service; the server's sign-in (12 hours, kept across restarts) is never ended.</summary>
    [Collection("ClientDir")]
    public class PilotChallengeSecurityFailingClientUiTests : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "obchsecf-ui-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        public PilotChallengeSecurityFailingClientUiTests() { Directory.CreateDirectory(root); }
        public void Dispose() { SystemClock.Use(null); try { Directory.Delete(root, true); } catch (Exception) { } }

        [Fact]
        public void UI04_SignOutInTheWindow_EndsTheSignInOnTheServerToo()
        {
            using (var w = PilotChallengeSecurityClientUiTests.Open(root))
            {
                Assert.Equal(200, PilotChallengeSecurityClientUiTests.Raw(w.Ui, "POST", "/api/login", new Msg().Set("password", PilotChallengeSecurityClientUiTests.Pw)).Status);
                Assert.Single(w.Server.Of("login"));                                                  // the server opened a sign-in
                Assert.Equal(200, PilotChallengeSecurityClientUiTests.Raw(w.Ui, "POST", "/api/logout", new Msg()).Status);
                Assert.Equal("0", PilotChallengeSecurityClientUiTests.Raw(w.Ui, "GET", "/api/state").Body["session"]);
                Assert.True(w.Server.Of("logout").Any(r => r.Method == "POST"), "the window's Sign out never told the server: the server's sign-in stays valid for 12 hours");
            }
        }
    }
}
