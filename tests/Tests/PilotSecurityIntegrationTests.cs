using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// Pilot release blockers AU-02 / AU-05 / AU-06 through the real server (in-process, real HTTP, real files) and the real
    /// agent. Contracts (tests/QA/specs.py): a revoked device token is refused at once; data read only with its key (a wrong
    /// key reads nothing, the server holds no plaintext); a reseller sees and changes only its own customers.
    /// Oracles: SHA-256 of every source and restored file; the bytes of the server's own files; exact HTTP status codes.
    /// </summary>
    public class PilotSecurityIntegrationTests
    {
        static int Status(Action a) { try { a(); return 200; } catch (AgentException e) { return e.Status; } }
        static string Sha(string file) { using (var h = SHA256.Create()) using (var s = File.OpenRead(file)) return BitConverter.ToString(h.ComputeHash(s)); }

        static Dictionary<string, string> Tree(string dir)
        {
            return Directory.GetFiles(dir, "*", SearchOption.AllDirectories).ToDictionary(f => f.Substring(dir.Length).TrimStart(Path.DirectorySeparatorChar), Sha);
        }

        // ------------------------------------------------------------------ AU-02

        [Fact]
        public void ADisconnectedComputer_IsRefusedAtItsNextCall_TheOtherComputerGoesOn_ARegistrationAgainWorks()
        {
            using (var env = new Env())
            {
                env.CreateUser("anna", "Customer-Pass-1");
                var one = env.Agent("anna", "Customer-Pass-1", name: "one");
                var two = env.Agent("anna", "Customer-Pass-1", name: "two");
                Assert.Equal("anna", one.Profile().Get("LOGIN_NAME"));
                Assert.Equal("anna", two.Profile().Get("LOGIN_NAME"));

                Assert.Equal("1", env.Admin().Call("POST", "/api/admin/users/anna/computers/disconnect", new Msg().Set("computer", one.Home.Computer))["revoked"]);
                var refused = Assert.Throws<AgentException>(() => { var c = one.DeviceClient(); c.Retries = 0; c.Call("GET", "/api/profile"); });
                Assert.Equal(401, refused.Status); Assert.Equal("DEVICE", refused.Code);
                Assert.Equal("anna", two.Profile().Get("LOGIN_NAME"));                         // the other registration is untouched
                // a wrong password cannot register it again; the right one can
                Assert.Equal(401, Status(() => one.Register(env.Url, "anna", "Customer-Pass-2", null, one.Home.Computer)));
                one.Register(env.Url, "anna", "Customer-Pass-1", null, one.Home.Computer);
                Assert.Equal("anna", one.Profile().Get("LOGIN_NAME"));
            }
        }

        // ------------------------------------------------------------------ AU-05

        [Fact]
        public void CustomAndRandomKeySets_RestoreIdenticalOnANewComputer_AWrongKeyReadsNothing_TheServerHoldsNoReadableContent()
        {
            using (var env = new Env())
            {
                env.CreateUser("keys", "Customer-Pass-1");
                var app = env.Agent("keys", "Customer-Pass-1");
                var src = env.Dir("src");
                const string marker = "PilotSalaryMarker7Q";
                var rnd = new Random(2026);
                var big = new byte[300 * 1024]; rnd.NextBytes(big);
                File.WriteAllBytes(Path.Combine(src, "big.bin"), big);
                File.WriteAllText(Path.Combine(src, "notes.txt"), string.Concat(Enumerable.Repeat(marker + " confidential line\n", 200)));
                File.WriteAllBytes(Path.Combine(src, "empty.dat"), new byte[0]);
                var want = Tree(src);
                const string customKey = "Our own key — 2026 ✓";
                var session = app.Interactive("Customer-Pass-1", null);
                var custom = app.CreateSet(session, "Customer-Pass-1", new BackupSetInfo { Name = "Custom", Sources = { src }, Compression = "NONE" }, "CUSTOM", customKey);
                var random = app.CreateSet(session, "Customer-Pass-1", new BackupSetInfo { Name = "Random", Sources = { src }, Compression = "NONE" }, "DEFAULT");
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(custom.Id).Result);
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(random.Id).Result);

                // no content and no custom key readable anywhere on the server (job logs name files by design, like Ahsay's — so the
                // marker is only inside a file's content, stored without compression so that only the encryption can hide it; the keys folder holds the keys for recovery, sealed)
                foreach (var f in Directory.GetFiles(env.Root, "*", SearchOption.AllDirectories).Where(f => !f.StartsWith(Path.Combine(env.Root, "src")) && !f.Contains(Path.DirectorySeparatorChar + "agent-")))
                {
                    var text = Atomic.ReadShared(f);   // the server keeps some files open (Windows)
                    Assert.False(text.Contains(marker), "file content readable on the server: " + f);
                    Assert.False(text.Contains(customKey), "the custom key on the server: " + f);
                }

                // a new computer: the custom key opens its set (a wrong one is refused before anything is read)
                var fresh = env.Agent("keys", "Customer-Pass-1", name: "newpc");
                var s2 = fresh.Interactive("Customer-Pass-1", null);
                Assert.Equal("WRONG_KEY", Assert.Throws<AgentException>(() => fresh.RestoreFor(s2, custom.Id, customKey + " ")).Code);
                Assert.Equal("WRONG_KEY", Assert.Throws<AgentException>(() => fresh.RestoreFor(s2, custom.Id, "Customer-Pass-1")).Code);   // the password is not the custom key
                var t1 = env.Dir("restore-custom");
                var r1 = fresh.RestoreFor(s2, custom.Id, customKey); r1.Run(null, t1, null, false);
                Assert.Equal(0, r1.Failed);
                Assert.Equal(want, Tree(Path.Combine(t1, Env.Rel(src))));

                // a random key: no password opens it; only the key the provider keeps (key recovery)
                var third = env.Agent("keys", "Customer-Pass-1", name: "thirdpc");
                var s3 = third.Interactive("Customer-Pass-1", null);
                Assert.Equal("NO_KEY", Assert.Throws<AgentException>(() => third.RestoreFor(s3, random.Id, "Customer-Pass-1")).Code);
                Assert.Equal("WRONG_KEY", Assert.Throws<AgentException>(() => third.RestoreFor(s3, random.Id, null, KeySet.Random().ToRaw())).Code);
                var key = Convert.FromBase64String(env.Admin().Call("GET", "/api/admin/keys/keys/" + random.Id)["key"]);
                var t2 = env.Dir("restore-random");
                var r2 = third.RestoreFor(s3, random.Id, null, key); r2.Run(null, t2, null, false);
                Assert.Equal(0, r2.Failed);
                Assert.Equal(want, Tree(Path.Combine(t2, Env.Rel(src))));
            }
        }

        // ------------------------------------------------------------------ AU-06

        [Fact]
        public void AResellersAdministrator_CannotReadOrChangeAnotherResellersCustomer_AndNothingThereChanges()
        {
            using (var env = new Env())
            {
                var sys = env.Admin();
                sys.Call("POST", "/api/admin/vendors", new Msg().Set("id", "acme-it").Set("name", "Acme IT"));
                sys.Call("POST", "/api/admin/vendors/acme-it/admins", new Msg().Set("login", "acme-admin").Set("password", "Acme-Admin-Pass-1"));
                sys.Call("POST", "/api/admin/vendors", new Msg().Set("id", "beta").Set("name", "Beta"));
                sys.Call("POST", "/api/admin/vendors/beta/admins", new Msg().Set("login", "beta-admin").Set("password", "Beta-Admin-Pass-1"));
                var acme = TestAuth.Admin(env.Url, "acme-admin", "Acme-Admin-Pass-1", 0);
                var beta = TestAuth.Admin(env.Url, "beta-admin", "Beta-Admin-Pass-1", 0);
                acme.Call("POST", "/api/admin/users", new Msg().Set("login", "acme-c1").Set("password", "Customer-Pass-1").Set("alias", "a").Set("quotaGB", 1).Set("email", "a@cust.invalid"));
                beta.Call("POST", "/api/admin/users", new Msg().Set("login", "beta-c1").Set("password", "Customer-Pass-1").Set("alias", "b").Set("quotaGB", 1).Set("email", "b@cust.invalid"));
                env.CreateUser("direct", "Customer-Pass-1");
                var app = env.Agent("beta-c1", "Customer-Pass-1", name: "betapc");
                var src = env.Dir("src"); File.WriteAllText(Path.Combine(src, "a.txt"), "beta's data");
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "F", Sources = { src } });
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);
                // beta's customer locked by wrong passwords: only beta (or the system administrator) may unlock it
                var c = new Client(env.Url) { Retries = 0 };
                for (int i = 0; i < 3; i++) Status(() => c.Call("POST", "/api/login", new Msg().Set("login", "beta-c1").Set("password", "wrong-" + i)));
                var userDir = Path.Combine(env.HomeA, "beta-c1");
                var before = Tree(userDir);

                // everything acme tries on beta's customer, on the system's customer, and on a name that does not exist: the same 403
                foreach (var target in new[] { "beta-c1", "direct", "ghost" })
                {
                    Assert.Equal(403, Status(() => acme.Call("GET", "/api/admin/users/" + target + "/computers")));
                    Assert.Equal(403, Status(() => acme.Call("POST", "/api/admin/users/" + target + "/unlock")));
                    Assert.Equal(403, Status(() => acme.Call("POST", "/api/admin/users/" + target + "/computers/disconnect", new Msg().Set("computer", app.Home.Computer))));
                    Assert.Equal(403, Status(() => acme.Call("POST", "/api/admin/users/" + target + "/delete", new Msg())));
                    Assert.Equal(403, Status(() => acme.Call("GET", "/api/admin/keys/" + target + "/" + set.Id)));
                }
                Assert.Equal(403, Status(() => acme.Call("POST", "/api/admin/users/acme-c1/computers/move", new Msg().Set("computer", "PC-x").Set("target", "beta-c1"))));
                Assert.Equal(new[] { "acme-c1" }, acme.Call("GET", "/api/admin/users").List("users").Select(u => u["login"]).ToArray());

                // nothing of beta's customer changed (oracle: SHA-256 of every file in its folder), it is still locked, and its computer still backs up
                Assert.Equal(before, Tree(userDir));
                Assert.Equal("LOCKED", Assert.Throws<AgentException>(() => c.Call("POST", "/api/login", new Msg().Set("login", "beta-c1").Set("password", "Customer-Pass-1"))).Code);
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);
                // a customer's own sign-in never opens the administrators' API
                var customer = env.Agent("acme-c1", "Customer-Pass-1", name: "acmepc").Interactive("Customer-Pass-1", null);
                Assert.Equal(401, Status(() => customer.Call("GET", "/api/admin/users")));
                // beta itself may unlock its own customer
                Assert.Equal(200, Status(() => beta.Call("POST", "/api/admin/users/beta-c1/unlock")));
            }
        }
    }
}
