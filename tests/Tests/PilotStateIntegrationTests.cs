using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// Pilot release blocker SH-05 — INTEGRATION layer: the real server in-process, real HTTP, the real agent, real files.
    /// Contract (tests/QA/specs.py SH-05): a run that changes most files with one new extension → retention frozen,
    /// administrators alerted. Oracle: the files restored from the point before the attack against their SHA-256 taken
    /// before it.
    /// </summary>
    public class PilotStateIntegrationTests
    {
        static string Sha(string f) { using (var s = File.OpenRead(f)) using (var h = SHA256.Create()) return Bytes.Hex(h.ComputeHash(s)); }
        static Dictionary<string, string> Tree(string dir)
        {
            return Directory.Exists(dir) ? Directory.GetFiles(dir, "*", SearchOption.AllDirectories).ToDictionary(f => Path.GetRelativePath(dir, f), Sha) : new Dictionary<string, string>();
        }

        /// <summary>
        /// SH-05 failure + recovery + integrity: every document of a customer is replaced by an encrypted ".locked" copy
        /// while the mail server is down. The alert cannot be sent — the retention is frozen all the same, and the
        /// administrators see it in the admin site (the customer frozen, the set and run suspect). 40 days of nightly
        /// maintenance later the point from before the attack is still there and restores identical (SHA-256). Only
        /// the administrator's release ends the freeze; the next maintenance then applies the retention again.
        /// </summary>
        [Fact]
        public void SH05_AnAttackWhileTheMailServerIsDown_StillFreezesRetention_ShownToTheAdmin_ThePointBeforeItRestoresIdentical_UntilReleased()
        {
            using (var env = new Env())
            {
                var dead = new TcpListener(IPAddress.Loopback, 0); dead.Start(); int deadPort = ((IPEndPoint)dead.LocalEndpoint).Port; dead.Stop();
                var admin = env.Admin();
                admin.Call("POST", "/api/admin/settings", new Msg().Set("smtpSet", "1").Set("senderEmail", "backup@example.invalid").Set("contactsSet", "1")
                    .Add("smtp", new Msg().Set("host", "127.0.0.1").Set("port", deadPort).Set("security", "NONE")).Add("contacts", new Msg().Set("name", "Ops").Set("email", "ops@example.invalid")));
                env.CreateUser("lock2026", "Customer-Pass-1");
                var app = env.Agent("lock2026", "Customer-Pass-1");
                var src = env.Dir("src");
                var rnd = new Random(7);
                for (int i = 0; i < 80; i++) { var b = new byte[2000 + i]; rnd.NextBytes(b); File.WriteAllBytes(Path.Combine(src, "doc" + i + ".docx"), b); }
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "Docs", Sources = { src } });
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);
                var beforeAttack = Tree(src);
                System.Threading.Thread.Sleep(1100);   // a distinct run id (one per second)

                // the attack: every document replaced by an encrypted copy with a new extension
                for (int i = 0; i < 80; i++)
                {
                    var f = Path.Combine(src, "doc" + i + ".docx");
                    var b = File.ReadAllBytes(f); for (int k = 0; k < b.Length; k++) b[k] ^= 0x5A;
                    File.WriteAllBytes(f + ".locked", b); File.Delete(f);
                }
                app.Backup(set.Id);

                var users = admin.Call("GET", "/api/admin/users").List("users");
                var u = users.Single(x => x["login"] == "lock2026");
                Assert.Equal("1", u["frozen"]);                                                            // frozen although no mail could be sent
                Assert.False(string.IsNullOrEmpty(u.List("sets").Single(s => s["id"] == set.Id)["suspect"]));

                // 40 days of maintenance: nothing from before the attack is removed
                admin.Call("POST", "/api/admin/maintenance", new Msg().Set("now", RunId.From(DateTime.UtcNow.AddDays(40))));
                var session = app.Interactive("Customer-Pass-1", null);
                var points = app.RestoreFor(session, set.Id).Points();
                Assert.Equal(2, points.Count);
                var target = env.Dir("restore-before-attack");
                var r = app.RestoreFor(session, set.Id); r.Run(points[0], target, null, false);
                Assert.Equal(0, r.Failed);
                Assert.Equal(beforeAttack, Tree(Path.Combine(target, Env.Rel(src))));                       // the customer's documents, byte for byte

                // only the administrator releases it; then retention applies again
                admin.Call("POST", "/api/admin/users/lock2026/unfreeze");
                Assert.Equal("0", admin.Call("GET", "/api/admin/users").List("users").Single(x => x["login"] == "lock2026")["frozen"]);
                admin.Call("POST", "/api/admin/maintenance", new Msg().Set("now", RunId.From(DateTime.UtcNow.AddDays(40))));
                Assert.Single(app.RestoreFor(app.Interactive("Customer-Pass-1", null), set.Id).Points());
            }
        }
    }
}
