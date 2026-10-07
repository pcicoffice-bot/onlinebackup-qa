using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// Pilot release blockers of the server's storage — the real server in this process, real HTTP, real files, the real
    /// agent (Env). Contracts in tests/QA/specs.py; the oracle is the SHA-256 of the source files (or of the fixture's
    /// manifest), read outside the product.
    ///   ST-08 a version-1 server whose set index was lost: the server opens it, rebuilds the index from the old objects,
    ///         a new computer of the customer restores every old point byte-identical, and new backups go on beside them
    ///   ST-09 a deleted set survives a server restart in the recycle bin and comes back restorable byte-identical; a
    ///         customer cannot be restored over a new customer of the same name (409), which stays untouched
    ///   ST-10 a copy folder that fails does not stop the settings backup; the downloaded backup restores a server whose
    ///         settings were lost: the administrator and the customer sign in, the old point restores identical, backups go on
    /// </summary>
    public class PilotStorageIntegrationTests
    {
        static string Sha(byte[] b) { using (var h = SHA256.Create()) return Bytes.Hex(h.ComputeHash(b)); }
        static Dictionary<string, string> Tree(string dir)
        {
            return Directory.GetFiles(dir, "*", SearchOption.AllDirectories).ToDictionary(f => f.Substring(dir.Length).Replace('\\', '/').TrimStart('/'), f => Sha(File.ReadAllBytes(f)));
        }
        static void AssertSame(Dictionary<string, string> want, Dictionary<string, string> got, string what)
        {
            Assert.True(want.OrderBy(k => k.Key, StringComparer.Ordinal).SequenceEqual(got.OrderBy(k => k.Key, StringComparer.Ordinal)),
                what + ": " + string.Join(", ", got.Keys.OrderBy(k => k)) + " ≠ " + string.Join(", ", want.Keys.OrderBy(k => k)));
        }
        static byte[] Rnd(int n, int seed) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }

        static void Restart(Env env, bool reload = false)
        {
            env.Api.Dispose();
            if (reload) env.Cfg = SystemConfig.Load(env.SystemHome);
            env.Api = new Api(env.Cfg);
            env.Api.Start(env.Url);
        }

        static Dictionary<string, string> RestoreTree(Env env, AgentApp app, string password, string setId, string point, string source, string name)
        {
            var target = env.Dir(name);
            var r = app.RestoreFor(app.Interactive(password, null), setId, password);
            r.Run(point, target, null, false);
            Assert.Equal(0, r.Failed);
            return Tree(Path.Combine(target, Env.Rel(source)));
        }

        // ================================================================== ST-08

        const string UpgradeRoot = "@@UPGRADE_ROOT@@", UpgradeLogin = "upgrade2026", UpgradePassword = "Customer-Pass-1";

        static bool IsText(string f)
        {
            var b = File.ReadAllBytes(f);
            if (b.Length > 4 * 1024 * 1024 || Array.IndexOf(b, (byte)0) >= 0) return false;
            try { new UTF8Encoding(false, true).GetString(b); return true; } catch (DecoderFallbackException) { return false; }
        }

        [Fact]
        public void Upgrade_AVersion1ServerWhoseSetIndexWasLost_OpensAndRebuildsIt_EveryOldPointRestoresIdentical_AndBackupsGoOn()
        {
            string zip = null;
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
                if (Directory.Exists(Path.Combine(d.FullName, "tests", "Tests"))) { zip = Path.Combine(d.FullName, "tests", "Fixtures", "upgrade", "v1-2026-10.zip"); break; }
            if (zip == null || !File.Exists(zip)) throw NotTested.Because("tests/Fixtures/upgrade/v1-2026-10.zip is not in this checkout");
            using (var env = new Env())
            {
                env.Api.Dispose();
                foreach (var part in new[] { "system", "homeA" }) Directory.Delete(Path.Combine(env.Root, part), true);
                var stage = Path.Combine(env.Root, "stage");
                ZipFile.ExtractToDirectory(zip, stage);
                foreach (var f in Directory.GetFiles(stage, "*", SearchOption.AllDirectories))
                {
                    var to = Path.Combine(env.Root, f.Substring(stage.Length + 1));
                    Directory.CreateDirectory(Path.GetDirectoryName(to));
                    if (IsText(f)) File.WriteAllText(to, File.ReadAllText(f).Replace(UpgradeRoot, env.Root)); else File.Copy(f, to);
                }
                var m = System.Xml.Linq.XElement.Parse(File.ReadAllText(Path.Combine(env.Root, "manifest.xml")));
                string setId = (string)m.Attribute("SET"), original = (string)m.Attribute("ORIGINAL"), adminTotp = (string)m.Attribute("ADMIN_TOTP");
                var points = m.Elements("POINT").Select(p => p.Elements("FILE").ToDictionary(f => (string)f.Attribute("PATH"), f => (string)f.Attribute("SHA256"))).ToList();
                // the set's index is lost (a disk error, a restore of the folders without it)
                var setDir = Path.Combine(env.HomeA, UpgradeLogin, "files", setId);
                foreach (var f in Directory.GetFiles(setDir, "index.db*")) File.Delete(f);
                var objectsBefore = Tree(setDir).Where(k => !k.Key.EndsWith(".db", StringComparison.Ordinal)).ToDictionary(k => k.Key, k => k.Value);

                env.Cfg = SystemConfig.Load(env.SystemHome);
                env.SetLicense("PRO", 0, 0);
                env.Api = new Api(env.Cfg);
                env.Api.Start(env.Url);
                if (!string.IsNullOrEmpty(adminTotp)) TestAuth.Remember(env.Url, "admin", adminTotp);
                Assert.Contains(UpgradeLogin, env.Admin().Call("GET", "/api/admin/users").ToString());

                var app = env.Agent(UpgradeLogin, UpgradePassword, name: "new-pc");
                var listed = app.RestoreFor(app.Interactive(UpgradePassword, null), setId, UpgradePassword).Points();
                Assert.Equal(points.Count, listed.Count);
                for (int i = 0; i < listed.Count; i++)
                    AssertSame(points[i], RestoreTree(env, app, UpgradePassword, setId, listed[i], original, "restore-" + i), "old point " + i + " after the index was rebuilt");
                foreach (var kv in objectsBefore) Assert.Equal(kv.Value, Sha(File.ReadAllBytes(Path.Combine(setDir, kv.Key))));   // no old object changed

                // backups go on beside the old points
                var src = ((string)m.Attribute("SOURCE")).Replace(UpgradeRoot, env.Root);
                Directory.CreateDirectory(src);
                File.WriteAllBytes(Path.Combine(src, "after the upgrade.bin"), Rnd(300000, 8));
                app.Sets();
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(setId).Result);
                var after = app.RestoreFor(app.Interactive(UpgradePassword, null), setId, UpgradePassword).Points();
                Assert.Equal(points.Count + 1, after.Count);
                AssertSame(Tree(src), RestoreTree(env, app, UpgradePassword, setId, null, src, "restore-new"), "the new point");
                AssertSame(points[0], RestoreTree(env, app, UpgradePassword, setId, after[0], original, "restore-first-again"), "the first old point after a new backup");
            }
        }

        // ================================================================== ST-09

        [Fact]
        public void RecycleBin_ADeletedSetSurvivesARestart_AndComesBackRestorableIdentical_ACustomerIsNotRestoredOverANewOneOfTheSameName()
        {
            using (var env = new Env())
            {
                env.CreateUser("acme", "Customer-Pass-1");
                var app = env.Agent("acme", "Customer-Pass-1");
                var src = env.Dir("src");
                File.WriteAllBytes(Path.Combine(src, "ledger.bin"), Rnd(400000, 1));
                File.WriteAllText(Path.Combine(src, "notes.txt"), "kept in the recycle bin");
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "Office", Sources = { src } });
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);
                var want = Tree(src);
                var admin = env.Admin();

                Assert.Equal("1", admin.Call("POST", "/api/admin/users/acme/delete", new Msg().Set("set", set.Id))["deleted"]);
                Assert.Empty(env.Api.UserStore.LoadProfile("acme").SetElements);
                Restart(env, reload: true);                                                       // the server restarts with the set in the bin
                admin = env.Admin();
                var item = admin.Call("GET", "/api/admin/recycle").List("items").Single();
                Assert.Equal("set", item["kind"]); Assert.Equal("Office", item["name"]);
                admin.Call("POST", "/api/admin/recycle/" + Uri.EscapeDataString(item["id"]) + "/restore");
                Assert.Empty(admin.Call("GET", "/api/admin/recycle").List("items"));
                AssertSame(want, RestoreTree(env, app, "Customer-Pass-1", set.Id, null, src, "restore-1"), "the set restored from the bin");
                File.WriteAllText(Path.Combine(src, "notes.txt"), "changed after the restore from the bin");
                System.Threading.Thread.Sleep(1100);
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);                      // and goes on
                AssertSame(Tree(src), RestoreTree(env, app, "Customer-Pass-1", set.Id, null, src, "restore-2"), "the next backup");

                // the customer is deleted; a new customer of the same name is created; the old one cannot be put over it
                admin.Call("POST", "/api/admin/users/acme/delete");
                env.CreateUser("acme", "Another-Pass-9");
                var u = admin.Call("GET", "/api/admin/recycle").List("items").Single(x => x["kind"] == "user");
                var e = Assert.Throws<AgentException>(() => admin.Call("POST", "/api/admin/recycle/" + Uri.EscapeDataString(u["id"]) + "/restore"));
                Assert.Equal(409, e.Status);
                Assert.Empty(env.Api.UserStore.LoadProfile("acme").SetElements);                 // the new customer is untouched
                Assert.True(PasswordHash.Verify("Another-Pass-9", env.Api.UserStore.LoadProfile("acme").Get("HASHED_PWD")));
                Assert.Contains(admin.Call("GET", "/api/admin/recycle").List("items"), x => x["id"] == u["id"]);   // and the old one still waits in the bin
            }
        }

        // ================================================================== ST-10

        [Fact]
        public void SettingsBackup_ACopyFolderThatFails_StillMakesIt_AndTheDownloadedBackupRestoresAServerWhoseSettingsWereLost()
        {
            using (var env = new Env())
            {
                env.CreateUser("acme", "Customer-Pass-1");
                var app = env.Agent("acme", "Customer-Pass-1");
                var src = env.Dir("src");
                File.WriteAllBytes(Path.Combine(src, "data.bin"), Rnd(300000, 2));
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "Office", Sources = { src } });
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);
                var want = Tree(src);
                var admin = env.Admin();
                admin.Call("POST", "/api/admin/settings", new Msg().Set("brandCOMPANY", "Pilot IT"));

                // the copy folder cannot be made (its parent is a file): the backup is made all the same
                var blocker = Path.Combine(env.Root, "a-file"); File.WriteAllText(blocker, "x");
                admin.Call("POST", "/api/admin/configbackup", new Msg().Set("copyTo", Path.Combine(blocker, "copies")));
                var r = admin.Call("POST", "/api/admin/configbackup/now");
                var name = r["made"];
                Assert.Single(r.List("backups"));
                Assert.False(Directory.Exists(Path.Combine(blocker, "copies")));
                var local = Path.Combine(env.SystemHome, "config-backups", name);
                var downloaded = Path.Combine(env.Dir("downloads"), name);
                admin.Download("/api/admin/configbackup/" + Uri.EscapeDataString(name), downloaded);
                Assert.Equal(Sha(File.ReadAllBytes(local)), Sha(File.ReadAllBytes(downloaded)));
                var confBefore = Tree(Path.Combine(env.SystemHome, "conf")).Where(k => !k.Key.Contains("session", StringComparison.OrdinalIgnoreCase)).ToDictionary(k => k.Key, k => k.Value);

                // the server's settings are lost and the customer's profile is damaged
                env.Api.Dispose();
                foreach (var part in new[] { "conf", "policy" }) Directory.Delete(Path.Combine(env.SystemHome, part), true);
                File.WriteAllText(Path.Combine(env.HomeA, "acme", "db", "Profile.xml"), "<damaged");
                // restored as ConfigBackup documents: "system" over the System Home, users\<login>\db over that customer's db
                using (var z = ZipFile.OpenRead(downloaded))
                    foreach (var en in z.Entries.Where(x => !x.FullName.EndsWith("/") && x.FullName != "BACKUP.xml"))
                    {
                        var p = en.FullName.Split('/');
                        var to = p[0] == "system" ? Path.Combine(new[] { env.SystemHome }.Concat(p.Skip(1)).ToArray())
                                                  : Path.Combine(new[] { Path.Combine(env.HomeA, p[1], "db") }.Concat(p.Skip(3)).ToArray());
                        Directory.CreateDirectory(Path.GetDirectoryName(to));
                        en.ExtractToFile(to, true);
                    }
                foreach (var kv in confBefore) Assert.Equal(kv.Value, Sha(File.ReadAllBytes(Path.Combine(env.SystemHome, "conf", kv.Key))));
                env.Cfg = SystemConfig.Load(env.SystemHome);
                env.Api = new Api(env.Cfg);
                env.Api.Start(env.Url);

                admin = env.Admin();
                Assert.Contains("Pilot IT", admin.Call("GET", "/api/admin/settings").ToString());
                Assert.Contains("Office", admin.Call("GET", "/api/admin/users/acme/sets/" + set.Id)["set"]);
                AssertSame(want, RestoreTree(env, app, "Customer-Pass-1", set.Id, null, src, "restore-1"), "the point after the settings were restored");
                File.WriteAllBytes(Path.Combine(src, "data.bin"), Rnd(300000, 3));
                System.Threading.Thread.Sleep(1100);
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);
                AssertSame(Tree(src), RestoreTree(env, app, "Customer-Pass-1", set.Id, null, src, "restore-2"), "the next backup");
            }
        }
    }
}
