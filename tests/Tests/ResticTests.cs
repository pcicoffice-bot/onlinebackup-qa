using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>The restic engine end to end: real restic (OB_RESTIC) against this product's server. Skipped without restic.</summary>
    public class ResticTests
    {
        static bool Have { get { var r = Environment.GetEnvironmentVariable("OB_RESTIC"); return !string.IsNullOrEmpty(r) && File.Exists(r); } }

        static int Http(string method, string url, string user, string pass, byte[] body = null)
        {
            var r = (HttpWebRequest)WebRequest.Create(url);
            r.Method = method;
            if (user != null) r.Headers["Authorization"] = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + pass));
            if (body != null) { r.ContentLength = body.Length; using (var s = r.GetRequestStream()) s.Write(body, 0, body.Length); }
            else if (method != "GET" && method != "HEAD") r.ContentLength = 0;
            try { using (var resp = (HttpWebResponse)r.GetResponse()) return (int)resp.StatusCode; }
            catch (WebException e) { var resp = e.Response as HttpWebResponse; if (resp == null) throw; return (int)resp.StatusCode; }
        }

        [Fact]
        public void ResticBacksUpToOurServer_OnlyChangesAreSent_RestoresExactly_AndTheServerProtectsTheRepository()
        {
            if (!Have) throw NotTested.Because("OB_RESTIC (the restic program) is not set");
            using (var env = new Env())
            {
                env.CreateUser("rst2026", "Customer-Pass-1");
                var app = env.Agent("rst2026", "Customer-Pass-1");
                var src = env.Dir("src");
                Directory.CreateDirectory(Path.Combine(src, "הנהלת חשבונות"));
                for (int i = 0; i < 30; i++) File.WriteAllText(Path.Combine(src, "הנהלת חשבונות", "f" + i + ".txt"), new string('x', 1000 + i));
                var big = new byte[48 * 1024 * 1024]; new Random(3).NextBytes(big); File.WriteAllBytes(Path.Combine(src, "mail.pst"), big);
                var s = new BackupSetInfo { Name = "Files", Engine = "RESTIC", Sources = { src } };
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", s);

                var r1 = app.Backup(set.Id);
                Assert.True(r1.Result == "BS_STOP_SUCCESS", string.Join("\n", r1.LogLines));
                Assert.Equal(31, r1.New);

                // 1MB changed inside the 48MB file: only the chunks around the change go up (restic chunks are 0.5–8MB at
                // content-defined, per-repository random boundaries, so up to two neighbouring chunks are resent)
                using (var f = new FileStream(Path.Combine(src, "mail.pst"), FileMode.Open)) { f.Position = 20 * 1024 * 1024; var x = new byte[1024 * 1024]; new Random(4).NextBytes(x); f.Write(x, 0, x.Length); }
                var r2 = app.Backup(set.Id);
                Assert.True(r2.Result == "BS_STOP_SUCCESS", string.Join("\n", r2.LogLines));
                Assert.Equal(1, r2.Updated);
                Assert.True(r2.BytesSent < 24L * 1024 * 1024, "sent " + r2.BytesSent);

                // restore (restic restores the absolute path under the target) and compare every byte
                var target = env.Dir("restore");
                var rs = app.Restic(app.Sets().First(x => x.Id == set.Id));
                rs.Restore(null, target, null, new System.Collections.Generic.List<string>());
                var restored = Path.Combine(target, Env.Rel(src));
                foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
                    Assert.True(File.ReadAllBytes(file).SequenceEqual(File.ReadAllBytes(restored + file.Substring(src.Length))), "differs: " + file);
                Assert.Equal(1, rs.Check("100%").Int("ok"));

                // the server: job log + statistics as for every backup
                var userDir = Directory.GetDirectories(env.HomeA, "rst2026", SearchOption.AllDirectories).First();
                Assert.Equal(2, Directory.GetFiles(Path.Combine(userDir, "logs", set.Id, "Backup")).Length);
                var repo = new ResticStore(userDir, set.Id);
                Assert.True(repo.Exists);

                var url = env.Url.TrimEnd('/') + "/restic/rst2026/" + set.Id + "/";
                var token = app.Home.LoadSecret(set.Id + "-restic-token");
                Assert.Equal(401, Http("GET", url + "keys/", "rst2026", "wrong-token"));
                Assert.Equal(200, Http("GET", url + "keys/", "rst2026", token));
                // a damaged upload (content ≠ its SHA-256 name) is refused
                Assert.Equal(400, Http("POST", url + "data/" + new string('a', 64), "rst2026", token, Encoding.ASCII.GetBytes("not the content of this name")));
                // the repository and its configuration cannot be deleted by an agent
                Assert.Equal(403, Http("DELETE", url, "rst2026", token));
                Assert.Equal(403, Http("DELETE", url + "config", "rst2026", token));

                // "ransomware" deletes every snapshot with the agent's own credentials → all in the server trash, restorable
                var snaps = Directory.GetFiles(Path.Combine(repo.Dir, "snapshots"));
                foreach (var sn in snaps) Assert.Equal(200, Http("DELETE", url + "snapshots/" + Path.GetFileName(sn), "rst2026", token));
                Assert.Empty(rs.Snapshots());
                Assert.Equal(0, repo.PurgeTrash(DateTime.UtcNow, ResticStore.TrashDays));                // nothing older than 14 days
                Assert.Equal(snaps.Length, repo.RestoreTrash());
                Assert.Equal(2, rs.Snapshots().Count);

                // key-less scrub on the server finds a damaged pack and quarantines it
                Assert.Equal(0, repo.Scrub(long.MaxValue).Int("bad"));
                var pack = Directory.GetFiles(Path.Combine(repo.Dir, "data"), "*", SearchOption.AllDirectories).First();
                var bytes = File.ReadAllBytes(pack); bytes[bytes.Length / 2] ^= 0xFF; File.WriteAllBytes(pack, bytes);
                File.Delete(Path.Combine(repo.Dir, ".scrub"));
                var sc = repo.Scrub(long.MaxValue);
                Assert.Equal(1, sc.Int("bad"));
                Assert.False(File.Exists(pack));
                Assert.Equal(0, rs.Check("100%").Int("ok"));                                       // and restic itself reports the loss
            }
        }

        [Fact]
        public void ResticRespectsTheQuota()
        {
            if (!Have) throw NotTested.Because("OB_RESTIC (the restic program) is not set");
            using (var env = new Env())
            {
                env.CreateUser("rstq", "Customer-Pass-1", quotaGB: 2.0 / 1024);   // 2 MB
                var app = env.Agent("rstq", "Customer-Pass-1");
                var src = env.Dir("src");
                var big = new byte[6 * 1024 * 1024]; new Random(5).NextBytes(big); File.WriteAllBytes(Path.Combine(src, "big.bin"), big);
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "Q", Engine = "RESTIC", Sources = { src } });
                var r = app.Backup(set.Id);
                Assert.True(r.Result == "BS_STOP_QUOTA_EXCEEDED", r.Result + "\n" + string.Join("\n", r.LogLines));
            }
        }

        [Fact]
        public void BackupKilledMidwayLeavesNothingBroken_TheNextRunCompletesAndEverythingRestores()
        {
            if (!Have) throw NotTested.Because("OB_RESTIC (the restic program) is not set");
            using (var env = new Env())
            {
                env.CreateUser("rstk", "Customer-Pass-1");
                var app = env.Agent("rstk", "Customer-Pass-1");
                var src = env.Dir("src");
                for (int i = 0; i < 6; i++) { var b = new byte[40 * 1024 * 1024]; new Random(20 + i).NextBytes(b); File.WriteAllBytes(Path.Combine(src, "f" + i + ".bin"), b); }
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "K", Engine = "RESTIC", Sources = { src } });
                var first = app.Backup(set.Id);      // creates the repository
                Assert.True(first.Result == "BS_STOP_SUCCESS", string.Join("\n", first.LogLines));
                for (int i = 0; i < 6; i++) { var b = new byte[40 * 1024 * 1024]; new Random(40 + i).NextBytes(b); File.WriteAllBytes(Path.Combine(src, "f" + i + ".bin"), b); }

                // "power cut": restic is killed in the middle of the second backup (240MB of new data)
                var t = new Thread(() => { try { app.Backup(set.Id); } catch { } });
                t.Start();
                var userDir = Directory.GetDirectories(env.HomeA, "rstk", SearchOption.AllDirectories).First();
                var repo = new ResticStore(userDir, set.Id);
                long before = repo.Size(); var until = DateTime.UtcNow.AddSeconds(60);
                while (repo.Size() < before + 30L * 1024 * 1024 && DateTime.UtcNow < until) Thread.Sleep(50);
                var killed = ReliabilityTests.KillRestic(src);   // only this test's restic (its source folder is on the command line)
                t.Join();
                Assert.True(killed > 0, "the fault was not injected: no restic of this backup was running to be killed");
                // and the server was "cut" in the middle of writing a file: a half-written temporary file stays behind
                File.WriteAllBytes(Path.Combine(repo.Dir, "data", "00", new string('0', 64) + ".deadbeef.tmp"), new byte[12345]);

                var r = app.Backup(set.Id);          // the next scheduled run
                Assert.True(r.Result == "BS_STOP_SUCCESS", string.Join("\n", r.LogLines));
                var rs = app.Restic(app.Sets().First(x => x.Id == set.Id));
                Assert.Equal(1, rs.Check("100%").Int("ok"));
                var target = env.Dir("restore");
                rs.Restore(null, target, null, new System.Collections.Generic.List<string>());
                var restored = Path.Combine(target, Env.Rel(src));
                foreach (var file in Directory.GetFiles(src))
                    Assert.True(File.ReadAllBytes(file).SequenceEqual(File.ReadAllBytes(Path.Combine(restored, Path.GetFileName(file)))), "differs: " + file);
                Assert.Equal(0, repo.Scrub(long.MaxValue).Int("bad"));
            }
        }

        [Fact]
        public void SingleFileRestore_NamesWithBracketsAndStars()
        {
            if (!Have) throw NotTested.Because("OB_RESTIC (the restic program) is not set");
            using (var env = new Env())
            {
                env.CreateUser("rstb", "Customer-Pass-1");
                var app = env.Agent("rstb", "Customer-Pass-1");
                var src = env.Dir("src");
                File.WriteAllText(Path.Combine(src, "Report [final].docx"), "final");
                File.WriteAllText(Path.Combine(src, "Report f.docx"), "other");
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "B", Engine = "RESTIC", Sources = { src } });
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);
                var rs = app.Restic(app.Sets().First(x => x.Id == set.Id));
                var path = rs.Ls(null).Select(f => f["path"]).Single(p => p.EndsWith("Report [final].docx"));
                var t = env.Dir("restore");
                rs.RestoreMany(null, t, new[] { path }, new System.Collections.Generic.List<string>());
                var dir = Path.Combine(t, Env.Rel(src));
                Assert.Equal("final", File.ReadAllText(Path.Combine(dir, "Report [final].docx")));
                Assert.False(File.Exists(Path.Combine(dir, "Report f.docx")));                 // "[final]" is not a pattern
            }
        }
    }
}
