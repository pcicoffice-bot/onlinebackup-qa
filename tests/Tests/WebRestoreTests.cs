using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>WEB-010: restore from the website with real restic: sign-in, the encryption password, points, browse, ZIP.</summary>
    public class WebRestoreTests
    {
        static bool Have { get { var r = Environment.GetEnvironmentVariable("OB_RESTIC"); return !string.IsNullOrEmpty(r) && File.Exists(r); } }

        static byte[] Download(Env env, string session, string setId, Msg body, out int status)
        {
            var r = (HttpWebRequest)WebRequest.Create(env.Url + "api/webrestore/" + setId + "/download");
            r.Method = "POST"; r.Headers["X-Session"] = session; r.ContentType = "application/xml";
            var b = body.ToBytes(); r.ContentLength = b.Length; using (var s = r.GetRequestStream()) s.Write(b, 0, b.Length);
            try { using (var resp = (HttpWebResponse)r.GetResponse()) using (var ms = new MemoryStream()) { status = (int)resp.StatusCode; resp.GetResponseStream().CopyTo(ms); return ms.ToArray(); } }
            catch (WebException e) { status = (int)((HttpWebResponse)e.Response).StatusCode; return null; }
        }

        [Fact]
        public void CustomerRestoresChosenFilesFromTheWebsite_WithTheEncryptionPassword()
        {
            if (!Have) return;
            using (var env = new Env())
            {
                env.CreateUser("webcust", "Customer-Pass-1");
                var app = env.Agent("webcust", "Customer-Pass-1");
                var src = env.Dir("src"); Directory.CreateDirectory(Path.Combine(src, "חשבוניות"));
                File.WriteAllText(Path.Combine(src, "חשבוניות", "Invoice [7].pdf"), "invoice seven");
                File.WriteAllText(Path.Combine(src, "notes.txt"), "v1");
                var big = new byte[700000]; new Random(5).NextBytes(big); File.WriteAllBytes(Path.Combine(src, "data.bin"), big);
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "My-Secret-Key-9", new BackupSetInfo { Name = "Office", Engine = "RESTIC", Sources = { src } });
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);
                File.WriteAllText(Path.Combine(src, "notes.txt"), "v2");
                System.Threading.Thread.Sleep(1100);
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);

                // the page is served
                using (var wc = new WebClient()) { wc.Encoding = Encoding.UTF8; Assert.Contains("restore.js", wc.DownloadString(env.Url + "restore")); Assert.Contains("webrestore", wc.DownloadString(env.Url + "restore/restore.js")); }

                var c = new Client(env.Url);
                Assert.ThrowsAny<Exception>(() => c.Call("GET", "/api/webrestore/sets"));          // signed in only
                c.Session = c.Call("POST", "/api/login", new Msg().Set("login", "webcust").Set("password", "Customer-Pass-1"))["session"];
                var sets = c.Call("GET", "/api/webrestore/sets");
                var s = sets.List("sets").Single();
                Assert.Equal("Office", s["name"]); Assert.Equal("1", s["web"]);

                var wrong = Assert.ThrowsAny<Exception>(() => c.Call("POST", "/api/webrestore/" + set.Id + "/points", new Msg().Set("key", "guess")));
                Assert.Contains("wrong", wrong.Message);
                var points = c.Call("POST", "/api/webrestore/" + set.Id + "/points", new Msg().Set("key", "My-Secret-Key-9")).List("points");
                Assert.Equal(2, points.Count);
                var newest = points[0]["id"]; var oldest = points[1]["id"];

                // browse from the top down to the invoices folder
                var top = c.Call("POST", "/api/webrestore/" + set.Id + "/ls", new Msg().Set("key", "My-Secret-Key-9").Set("point", oldest).Set("path", "")).List("files");
                Assert.NotEmpty(top);
                var srcItems = c.Call("POST", "/api/webrestore/" + set.Id + "/ls", new Msg().Set("key", "My-Secret-Key-9").Set("point", oldest).Set("path", src)).List("files");
                Assert.Equal(new[] { "data.bin", "notes.txt", "חשבוניות" }, srcItems.Select(f => f["name"]).OrderBy(n => n, StringComparer.Ordinal).ToArray());
                Assert.Equal("dir", srcItems.Single(f => f["name"] == "חשבוניות")["type"]);
                var inv = c.Call("POST", "/api/webrestore/" + set.Id + "/ls", new Msg().Set("key", "My-Secret-Key-9").Set("point", oldest).Set("path", src + "/חשבוניות")).List("files").Single();
                Assert.Equal("Invoice [7].pdf", inv["name"]);

                // download the old notes and the invoice: exactly what was backed up at that point
                var body = new Msg().Set("key", "My-Secret-Key-9").Set("point", oldest);
                body.Add("paths", new Msg().Set("p", src + "/notes.txt")); body.Add("paths", new Msg().Set("p", inv["path"]));
                int st; var zip = Download(env, c.Session, set.Id, body, out st);
                Assert.Equal(200, st);
                using (var z = new ZipArchive(new MemoryStream(zip)))
                {
                    Assert.Equal(2, z.Entries.Count);
                    Assert.Equal("v1", new StreamReader(z.Entries.Single(e => e.Name == "notes.txt").Open()).ReadToEnd());
                    Assert.Equal("invoice seven", new StreamReader(z.Entries.Single(e => e.Name == "Invoice [7].pdf").Open()).ReadToEnd());
                }
                // a folder brings everything under it; the newest point has the new notes
                var body2 = new Msg().Set("key", "My-Secret-Key-9").Set("point", newest); body2.Add("paths", new Msg().Set("p", src));
                using (var z = new ZipArchive(new MemoryStream(Download(env, c.Session, set.Id, body2, out st))))
                {
                    Assert.Equal(3, z.Entries.Count);
                    Assert.Equal("v2", new StreamReader(z.Entries.Single(e => e.Name == "notes.txt").Open()).ReadToEnd());
                    using (var ms = new MemoryStream()) { z.Entries.Single(e => e.Name == "data.bin").Open().CopyTo(ms); Assert.Equal(big, ms.ToArray()); }
                }
                Assert.Null(Download(env, c.Session, set.Id, new Msg().Set("key", "nope").Set("point", newest), out st)); Assert.Equal(403, st);
                // nothing left behind on the server, and nothing written into the repository
                Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(env.Cfg.SystemHome, "temp")));
                // another user cannot reach this set
                env.CreateUser("other1", "Customer-Pass-1");
                var o = new Client(env.Url); o.Session = o.Call("POST", "/api/login", new Msg().Set("login", "other1").Set("password", "Customer-Pass-1"))["session"];
                Assert.ThrowsAny<Exception>(() => o.Call("POST", "/api/webrestore/" + set.Id + "/points", new Msg().Set("key", "My-Secret-Key-9")));
                // five wrong passwords stop further tries for a while
                for (int i = 0; i < 4; i++) Assert.ThrowsAny<Exception>(() => c.Call("POST", "/api/webrestore/" + set.Id + "/points", new Msg().Set("key", "guess" + i)));
                var locked = Assert.ThrowsAny<Exception>(() => c.Call("POST", "/api/webrestore/" + set.Id + "/points", new Msg().Set("key", "My-Secret-Key-9")));
                Assert.Contains("15 minutes", locked.Message);
            }
        }
    }
}
