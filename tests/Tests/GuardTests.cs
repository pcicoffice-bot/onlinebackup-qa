using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Xml.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>GUARD-010: an address that guesses passwords, sprays names or scans the server is blocked.</summary>
    public class GuardTests
    {
        static Env Server()
        {
            var env = new Env();
            // the tests come from this computer — which is never blocked; here it counts as an outside address
            lock (env.Cfg) { env.Cfg.Doc.Root.Add(new XElement("GUARD", new XAttribute("TRUST_PRIVATE", "N"))); env.Cfg.Save(); }
            return env;
        }

        static string TryLogin(Env env, string login, string password)
        {
            var c = new Client(env.Url) { Retries = 0 };
            try { c.Call("POST", "/api/admin/login", new Msg().Set("login", login).Set("password", password)); return "ok"; }
            catch (AgentException e) { return e.Code; }
        }

        static List<string> Alerts(Env env)
        {
            var got = new List<string>();
            var before = Guard.Mail;
            Guard.Mail = (c, subject, html) => { if (c == env.Cfg) lock (got) got.Add(subject + " | " + html); else if (before != null) before(c, subject, html); };
            return got;
        }

        static void WaitFor(Func<bool> ok) { for (int i = 0; i < 50 && !ok(); i++) Thread.Sleep(100); }

        [Fact]
        public void PasswordGuessing_BlocksTheAddress_ForEverything_UntilUnblocked_AndSurvivesARestart()
        {
            using (var env = Server())
            {
                var admin = env.Admin();   // signed in before the attack
                env.CreateUser("victim", "Customer-Pass-1");
                var mails = Alerts(env);
                for (int i = 0; i < 10; i++) Assert.Contains(TryLogin(env, "admin", "guess-" + i), new[] { "LOGIN", "LOCKED" });   // the account locks after 3; the tries still count
                // 10 or more wrong passwords in 10 minutes: the address is blocked — also the right password, also the backups
                Assert.Equal("BLOCKED", TryLogin(env, "admin", "Admin-Pass-1"));
                Assert.Equal("BLOCKED", Assert.Throws<AgentException>(() => new Client(env.Url) { Retries = 0 }.Call("GET", "/api/brand")).Code);
                WaitFor(() => mails.Any(m => m.StartsWith("An address was blocked")));
                Assert.Contains(mails, m => m.StartsWith("An address was blocked") && m.Contains("wrong sign-ins"));
                Assert.DoesNotContain(mails, m => m.Contains("guess-"));                         // never a password in a mail
                foreach (var f in System.IO.Directory.GetFiles(env.SystemHome, "*", System.IO.SearchOption.AllDirectories))   // never a password on the disk
                    Assert.DoesNotContain("guess-", Atomic.ReadShared(f));

                // kept on the disk: a restart of the server does not let it in
                Guard.Reset();
                Assert.True(Guard.IsBlocked(env.Cfg, "127.0.0.1") || Guard.IsBlocked(env.Cfg, "::1"));
                // the administrator lets it in (from another address — here directly)
                Guard.Unblock(env.Cfg, "127.0.0.1", "test", "-"); Guard.Unblock(env.Cfg, "::1", "test", "-");
                Assert.NotEqual("BLOCKED", TryLogin(env, "admin", "wrong"));
            }
        }

        [Fact]
        public void PasswordSpraying_ManyNames_FewTries_IsBlockedToo()
        {
            using (var env = Server())
            {
                foreach (var n in new[] { "office", "backup", "manager" }) Assert.Equal("LOGIN", TryLogin(env, n, "Summer2026!"));
                Assert.Equal("LOGIN", TryLogin(env, "sales", "Summer2026!"));                 // the 4th name blocks
                Assert.Equal("BLOCKED", TryLogin(env, "anyone", "x"));
                Assert.Contains("different names", Guard.List(env.Cfg).List("blocked").Single()["reason"]);
            }
        }

        [Fact]
        public void Scanning_ManyUnknownAddresses_IsBlocked()
        {
            using (var env = Server())
            {
                var c = new Client(env.Url) { Retries = 0 };
                for (int i = 0; i < 30; i++) Assert.Throws<AgentException>(() => c.Call("GET", (i % 2 == 0 ? "/wp-login" : "/api/.env") + i));
                Assert.Equal("BLOCKED", Assert.Throws<AgentException>(() => c.Call("GET", "/api/brand-x")).Code);
                Assert.Contains("scanning", Guard.List(env.Cfg).List("blocked").Single()["reason"]);
            }
        }

        [Fact]
        public void OfficeNetwork_AndThisServer_AreNeverBlocked_AndTheRulesAreSetByTheAdministrator()
        {
            using (var env = new Env())   // default: private addresses and this computer are trusted
            {
                for (int i = 0; i < 15; i++) TryLogin(env, "nobody" + i, "x");
                Assert.NotEqual("BLOCKED", TryLogin(env, "nobody", "x"));
                Assert.True(Guard.Trusted(env.Cfg, "192.168.1.20")); Assert.True(Guard.Trusted(env.Cfg, "10.0.0.5")); Assert.False(Guard.Trusted(env.Cfg, "203.0.113.5"));

                var admin = env.Admin();
                var r = admin.Call("POST", "/api/admin/guard", new Msg().Set("on", 1).Set("fails", 6).Set("users", 3).Set("probes", 40).Set("blockHours", 48).Set("trusted", "198.51.100.0/24"));
                Assert.Equal("6", r["fails"]); Assert.Equal("48", r["blockHours"]); Assert.True(Guard.Trusted(env.Cfg, "198.51.100.7"));
                Assert.Throws<AgentException>(() => admin.Call("POST", "/api/admin/guard", new Msg().Set("trusted", "not-an-address")));
                // block by hand, see it, let it in again
                admin.Call("POST", "/api/admin/guard/block", new Msg().Set("ip", "203.0.113.5").Set("hours", 2).Set("reason", "seen in the firewall"));
                var l = admin.Call("GET", "/api/admin/guard").List("blocked");
                Assert.Equal("203.0.113.5", l.Single()["ip"]); Assert.Equal("seen in the firewall", l.Single()["reason"]);
                Assert.Throws<AgentException>(() => admin.Call("POST", "/api/admin/guard/block", new Msg().Set("ip", "192.168.1.20")));   // the office: never
                admin.Call("POST", "/api/admin/guard/unblock", new Msg().Set("ip", "203.0.113.5"));
                Assert.Empty(admin.Call("GET", "/api/admin/guard").List("blocked"));
            }
        }

        [Fact]
        public void ABlock_EndsByItself_AfterTheBlockingHours()
        {
            using (var env = Server())
            {
                Guard.Block(env.Cfg, "203.0.113.9", "test", "test", 24);
                Assert.True(Guard.IsBlocked(env.Cfg, "203.0.113.9"));
                var at = DateTime.UtcNow;
                try { Guard.Clock = () => at.AddHours(25); Assert.False(Guard.IsBlocked(env.Cfg, "203.0.113.9")); }
                finally { Guard.Clock = () => DateTime.UtcNow; }
            }
        }

        [Fact]
        public void AdministratorSignIn_AfterFailures_IsReported()
        {
            using (var env = Server())
            {
                env.Admin();
                var mails = Alerts(env);
                for (int i = 0; i < 2; i++) TryLogin(env, "admin", "wrong-" + i);
                // two more failures for the same name after the first sign-in's lock is cleared: 3 in all, then the right one
                TryLogin(env, "admin", "wrong-3");
                lock (env.Cfg) { foreach (var e in env.Cfg.Doc.Root.Descendants().Where(x => x.Attribute("LOCKED_UNTIL") != null)) e.SetAttributeValue("LOCKED_UNTIL", null); env.Cfg.Save(); }
                env.Admin();
                WaitFor(() => mails.Any(m => m.StartsWith("Administrator sign-in to check")));
                Assert.Contains(mails, m => m.StartsWith("Administrator sign-in to check") && m.Contains("wrong passwords before it"));
            }
        }
    }
}
