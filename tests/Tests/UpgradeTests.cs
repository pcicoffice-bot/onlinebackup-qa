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
    /// UPG-010: an upgrade never loses a customer's backups. tests/Fixtures/upgrade holds the folders of a server as an
    /// earlier version left them (settings, users, sets, history of several backups, logs, service calls). Every version
    /// must open each of them as they are: the administrator signs in and sees the customer, a new computer of the customer
    /// signs in with the customer's password, every restore point restores byte for byte, and new backups go on beside
    /// the old ones. A fixture is never changed: each release that changes a stored format adds a new one
    /// (OB_MAKE_UPGRADE_FIXTURE=label dotnet test --filter UpgradeTests).
    /// </summary>
    public class UpgradeTests
    {
        const string Root = "@@UPGRADE_ROOT@@";
        const string Login = "upgrade2026", Password = "Customer-Pass-1";

        static string FixtureDir()
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            {
                var p = Path.Combine(d.FullName, "tests", "Fixtures", "upgrade");
                if (Directory.Exists(Path.Combine(d.FullName, "tests", "Tests"))) { Directory.CreateDirectory(p); return p; }
            }
            throw new DirectoryNotFoundException("tests/Fixtures/upgrade");
        }

        static string Sha(byte[] b) { using (var h = SHA256.Create()) return Bytes.Hex(h.ComputeHash(b)); }
        static byte[] Rnd(int n, int seed) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }

        /// <summary>The source as it was at each backup: always the same content, so the fixture only records hashes.</summary>
        static void Source(string src, int point)
        {
            Directory.CreateDirectory(Path.Combine(src, "הנהלת חשבונות", "2026"));
            File.WriteAllText(Path.Combine(src, "readme.txt"), "point " + point);
            File.WriteAllText(Path.Combine(src, "הנהלת חשבונות", "2026", "ledger.csv"), string.Join("\n", Enumerable.Range(0, 200 + point * 10).Select(i => i + ",row")));
            // 6 MB that compresses well, changed in the middle at each point: the fixture keeps a full copy and its deltas
            var sb = new StringBuilder(); var rnd = new Random(5);
            for (int i = 0; sb.Length < 6 * 1024 * 1024; i++) sb.Append(i).Append(i >= 60000 && i < 60000 + 40 * point ? ",changed at point " + point : ",mailbox item ").Append(rnd.Next(1000)).Append('\n');
            File.WriteAllText(Path.Combine(src, "mail.pst"), sb.ToString());
            if (point == 0) File.WriteAllText(Path.Combine(src, "deleted-later.docx"), "only in the first point");
            else if (File.Exists(Path.Combine(src, "deleted-later.docx"))) File.Delete(Path.Combine(src, "deleted-later.docx"));
            if (point >= 2) File.WriteAllText(Path.Combine(src, "new in point 2.txt"), "added");
        }

        static Dictionary<string, string> Tree(string dir)
        {
            return Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                .ToDictionary(f => f.Substring(dir.Length).Replace('\\', '/').TrimStart('/'), f => Sha(File.ReadAllBytes(f)));
        }

        static bool IsText(string f)
        {
            var b = File.ReadAllBytes(f);
            if (b.Length > 4 * 1024 * 1024 || Array.IndexOf(b, (byte)0) >= 0) return false;
            try { new UTF8Encoding(false, true).GetString(b); return true; } catch (DecoderFallbackException) { return false; }
        }

        [Fact]
        public void MakeFixture_WhenAsked()
        {
            var label = Environment.GetEnvironmentVariable("OB_MAKE_UPGRADE_FIXTURE");
            if (string.IsNullOrEmpty(label)) return;
            string zip = Path.Combine(FixtureDir(), label + ".zip");
            Assert.False(File.Exists(zip), "a fixture is never replaced: " + zip);
            using (var env = new Env())
            {
                var admin = env.Admin();
                admin.Call("POST", "/api/admin/settings", new Msg().Set("brandPRODUCT", "Upgrade Backup").Set("brandCOMPANY", "Upgrade IT"));
                env.CreateUser(Login, Password);
                var app = env.Agent(Login, Password, name: "old-pc");
                var src = env.Dir("source");
                Source(src, 0);
                var set = app.CreateSet(app.Interactive(Password, null), Password,
                    new BackupSetInfo { Name = "Office files", Sources = { src }, MinDeltaFileSize = 1024 * 1024, Retention = new RetentionPolicy { Unit = "JOBS", Period = 30 } });
                var points = new List<Dictionary<string, string>>();
                for (int p = 0; p < 3; p++)
                {
                    if (p > 0) { System.Threading.Thread.Sleep(1100); Source(src, p); }
                    Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);
                    points.Add(Tree(src));
                }
                env.Api.Dispose();
                var manifest = new XmlManifest { AdminTotp = TestAuth.Secret(env.Url, "admin"), SetId = set.Id, Source = src.Replace(env.Root, Root), Original = src, Points = points };
                var stage = Path.Combine(env.Root, "stage");
                foreach (var part in new[] { "system", "homeA" })
                    foreach (var f in Directory.GetFiles(Path.Combine(env.Root, part), "*", SearchOption.AllDirectories))
                    {
                        var to = Path.Combine(stage, f.Substring(env.Root.Length + 1));
                        Directory.CreateDirectory(Path.GetDirectoryName(to));
                        if (IsText(f)) File.WriteAllText(to, File.ReadAllText(f).Replace(env.Root, Root)); else File.Copy(f, to);
                    }
                File.WriteAllText(Path.Combine(stage, "manifest.xml"), manifest.ToXml());
                ZipFile.CreateFromDirectory(stage, zip);
            }
        }

        [Fact]
        public void EveryEarlierVersion_OpensAsItIs_RestoresEveryPoint_AndGoesOn()
        {
            var zips = Directory.GetFiles(FixtureDir(), "*.zip");
            Assert.NotEmpty(zips);
            foreach (var zip in zips)
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
                        if (IsText(f)) File.WriteAllText(to, File.ReadAllText(f).Replace(Root, env.Root)); else File.Copy(f, to);
                    }
                    var manifest = XmlManifest.Parse(File.ReadAllText(Path.Combine(env.Root, "manifest.xml")));
                    var name = Path.GetFileName(zip);

                    // the server opens the old folders; the licence is issued again for its identity (a test key)
                    env.Cfg = SystemConfig.Load(env.SystemHome);
                    env.SetLicense("PRO", 0, 0);
                    env.Api = new Api(env.Cfg);
                    env.Api.Start(env.Url);
                    if (manifest.AdminTotp != null) TestAuth.Remember(env.Url, "admin", manifest.AdminTotp);
                    var admin = env.Admin();
                    var users = admin.Call("GET", "/api/admin/users").ToString();
                    Assert.Contains(Login, users); Assert.Contains("Office files", users);
                    Assert.Contains("Office files", admin.Call("GET", "/api/admin/users/" + Login + "/sets/" + manifest.SetId)["set"]);
                    foreach (var page in new[] { "/api/admin/settings", "/api/admin/defaults", "/api/admin/guard", "/api/admin/tickets" }) admin.Call("GET", page);

                    // a new computer of the customer: every old point, byte for byte
                    var app = env.Agent(Login, Password, name: "new-pc");
                    var session = app.Interactive(Password, null);
                    var points = app.RestoreFor(session, manifest.SetId, Password).Points();
                    Assert.True(points.Count == manifest.Points.Count, name + ": " + points.Count + " points");
                    for (int i = 0; i < points.Count; i++)
                    {
                        var target = env.Dir("restore-" + i);
                        var r = app.RestoreFor(session, manifest.SetId, Password);
                        r.Run(points[i], target, null, false);
                        Assert.Equal(0, r.Failed);
                        // the files come back under the path they had when they were backed up (that path is inside the encrypted backup)
                        var got = Tree(Path.Combine(target, Env.Rel(manifest.Original)));
                        Assert.True(got.OrderBy(k => k.Key).SequenceEqual(manifest.Points[i].OrderBy(k => k.Key)),
                            name + " point " + i + ": " + string.Join(", ", got.Keys.OrderBy(k => k)) + " ≠ " + string.Join(", ", manifest.Points[i].Keys.OrderBy(k => k)));
                    }

                    // and goes on: a new backup of the same set from the new computer, beside the old points
                    var src = manifest.Source.Replace(Root, env.Root);
                    Source(src, 2); File.WriteAllText(Path.Combine(src, "after the upgrade.txt"), "new");
                    app.Sets();
                    Assert.Equal("BS_STOP_SUCCESS", app.Backup(manifest.SetId).Result);
                    Assert.Equal(manifest.Points.Count + 1, app.RestoreFor(session, manifest.SetId, Password).Points().Count);
                    var latest = env.Dir("restore-latest");
                    var rl = app.RestoreFor(session, manifest.SetId, Password); rl.Run(null, latest, null, false);
                    Assert.Equal("new", File.ReadAllText(Path.Combine(latest, Env.Rel(src), "after the upgrade.txt")));
                }
        }

        sealed class XmlManifest
        {
            public string SetId, Source, AdminTotp, Original; public List<Dictionary<string, string>> Points;
            public string ToXml()
            {
                return new System.Xml.Linq.XElement("UPGRADE_FIXTURE", new System.Xml.Linq.XAttribute("SET", SetId), new System.Xml.Linq.XAttribute("SOURCE", Source), new System.Xml.Linq.XAttribute("ADMIN_TOTP", AdminTotp ?? ""), new System.Xml.Linq.XAttribute("ORIGINAL", Original),
                    Points.Select(p => new System.Xml.Linq.XElement("POINT", p.Select(kv => new System.Xml.Linq.XElement("FILE", new System.Xml.Linq.XAttribute("PATH", kv.Key), new System.Xml.Linq.XAttribute("SHA256", kv.Value)))))).ToString();
            }
            public static XmlManifest Parse(string xml)
            {
                var e = System.Xml.Linq.XElement.Parse(xml);
                return new XmlManifest
                {
                    SetId = (string)e.Attribute("SET"), AdminTotp = string.IsNullOrEmpty((string)e.Attribute("ADMIN_TOTP")) ? null : (string)e.Attribute("ADMIN_TOTP"), Source = (string)e.Attribute("SOURCE"), Original = (string)e.Attribute("ORIGINAL"),
                    Points = e.Elements("POINT").Select(p => p.Elements("FILE").ToDictionary(f => (string)f.Attribute("PATH"), f => (string)f.Attribute("SHA256"))).ToList()
                };
            }
        }
    }
}
