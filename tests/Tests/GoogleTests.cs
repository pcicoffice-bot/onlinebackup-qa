using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>A stand-in for Google's token endpoint, Admin directory, Gmail and Drive APIs (JWT signatures are verified).</summary>
    public sealed class FakeGoogle : IDisposable
    {
        readonly HttpListener l = new HttpListener();
        public string Url; public string KeyJson; readonly RSA rsa = RSA.Create(2048);
        public readonly object Gate = new object();
        public Dictionary<string, string> Mail = new Dictionary<string, string>();   // id → raw message
        public List<string> HistAdded = new List<string>(), HistDeleted = new List<string>(); public int History = 100;
        public Dictionary<string, byte[]> Files = new Dictionary<string, byte[]>();  // file id → content
        public List<string> Changed = new List<string>();
        public List<string> InsertedMail = new List<string>(); public List<string> Uploads = new List<string>();
        public int BadJwt;

        public FakeGoogle()
        {
            var t = new TcpListener(IPAddress.Loopback, 0); t.Start(); int port = ((IPEndPoint)t.LocalEndpoint).Port; t.Stop();
            Url = "http://localhost:" + port; l.Prefixes.Add(Url + "/"); l.Start();
            var pem = "-----BEGIN PRIVATE KEY-----\n" + Convert.ToBase64String(rsa.ExportPkcs8PrivateKey(), Base64FormattingOptions.InsertLineBreaks) + "\n-----END PRIVATE KEY-----\n";
            KeyJson = Json.Write(new Dictionary<string, object> { { "type", "service_account" }, { "client_email", "backup@acme-proj.iam.gserviceaccount.com" }, { "private_key", pem } });
            Mail["m1"] = Raw("Invoice 2026-17", "first"); Mail["m2"] = Raw("Lunch", "second");
            Files["doc1"] = Encoding.UTF8.GetBytes("docx-export-v1"); Files["pdf1"] = Encoding.UTF8.GetBytes("pdf v1");
            new Thread(() => { while (l.IsListening) { try { var c = l.GetContext(); ThreadPool.QueueUserWorkItem(_ => Handle(c)); } catch { return; } } }) { IsBackground = true }.Start();
        }

        public static string Raw(string subject, string body) { return "From: x@acme.com\r\nTo: ann@acme.com\r\nSubject: " + subject + "\r\nDate: Mon, 5 Oct 2026 10:00:00 +0000\r\n\r\n" + body + "\r\n"; }
        static string B64u(string s) { return Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }
        static object D(params object[] kv) { var d = new Dictionary<string, object>(); for (int i = 0; i < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1]; return d; }
        void Send(HttpListenerContext c, int st, object body) { var b = body as byte[] ?? Encoding.UTF8.GetBytes(Json.Write(body)); c.Response.StatusCode = st; c.Response.ContentLength64 = b.Length; c.Response.OutputStream.Write(b, 0, b.Length); c.Response.Close(); }

        object FileMeta(string id)
        {
            return id == "doc1" ? D("id", "doc1", "name", "Plan", "mimeType", "application/vnd.google-apps.document", "parents", new List<object> { "root" }, "modifiedTime", "2026-10-05T09:00:00Z")
                : D("id", "pdf1", "name", "report.pdf", "mimeType", "application/pdf", "parents", new List<object> { "fin" }, "md5Checksum", Bytes.Hex(Bytes.Sha256(Files["pdf1"]), 8), "modifiedTime", "2026-10-05T09:00:00Z");
        }

        void Handle(HttpListenerContext c)
        {
            try
            {
                var p = c.Request.Url.AbsolutePath; var q = c.Request.QueryString; var m = c.Request.HttpMethod;
                string body; using (var r = new StreamReader(c.Request.InputStream, Encoding.UTF8)) body = r.ReadToEnd();
                if (p == "/token")
                {
                    var jwt = Uri.UnescapeDataString(body.Split('&').First(x => x.StartsWith("assertion=")).Substring(10)).Split('.');
                    Func<string, byte[]> un = s => Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/') + new string('=', (4 - s.Length % 4) % 4));
                    if (!rsa.VerifyData(Encoding.ASCII.GetBytes(jwt[0] + "." + jwt[1]), un(jwt[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) { BadJwt++; Send(c, 400, D("error", "invalid_grant")); return; }
                    var claims = Json.Obj(Json.Parse(Encoding.UTF8.GetString(un(jwt[1]))));
                    Send(c, 200, D("access_token", "tok-" + Json.Str(claims, "sub"), "expires_in", 3600)); return;
                }
                var user = (c.Request.Headers["Authorization"] ?? "").Replace("Bearer tok-", "");
                lock (Gate)
                {
                    if (p == "/admin/directory/v1/users") { Send(c, 200, D("users", new List<object> { D("primaryEmail", "ann@acme.com"), D("primaryEmail", "ben@acme.com", "suspended", true) })); return; }
                    if (user != "ann@acme.com") { Send(c, 403, D("error", D("message", "not ann"))); return; }
                    if (p == "/gmail/v1/users/me/profile") { Send(c, 200, D("historyId", History.ToString())); return; }
                    if (p == "/gmail/v1/users/me/messages" && m == "GET") { Send(c, 200, D("messages", Mail.Keys.Select(k => (object)D("id", k)).ToList())); return; }
                    if (p.StartsWith("/gmail/v1/users/me/messages/")) { var id = p.Split('/').Last(); Send(c, 200, D("id", id, "internalDate", "1791100000000", "raw", B64u(Mail[id]))); return; }
                    if (p == "/gmail/v1/users/me/history")
                    {
                        var h = new List<object>();
                        foreach (var a in HistAdded) h.Add(D("messagesAdded", new List<object> { D("message", D("id", a)) }));
                        foreach (var d in HistDeleted) h.Add(D("messagesDeleted", new List<object> { D("message", D("id", d)) }));
                        HistAdded.Clear(); HistDeleted.Clear(); Send(c, 200, D("history", h)); return;
                    }
                    if (p == "/gmail/v1/users/me/labels") { Send(c, 200, D("id", "L-restored")); return; }
                    if (p == "/gmail/v1/users/me/messages" && m == "POST") { var raw = Json.Str(Json.Obj(Json.Parse(body)), "raw"); InsertedMail.Add(Encoding.UTF8.GetString(Convert.FromBase64String(raw.Replace('-', '+').Replace('_', '/') + new string('=', (4 - raw.Length % 4) % 4)))); Send(c, 200, D("id", "new")); return; }
                    if (p == "/drive/v3/changes/startPageToken") { Send(c, 200, D("startPageToken", "t1")); return; }
                    if (p == "/drive/v3/files" && m == "GET") { Send(c, 200, D("files", new List<object> { FileMeta("doc1"), FileMeta("pdf1") })); return; }
                    if (p == "/drive/v3/files" && m == "POST") { Send(c, 200, D("id", "F-restored")); return; }
                    if (p == "/drive/v3/changes") { var ch = Changed.Select(id => (object)D("fileId", id, "file", FileMeta(id))).ToList(); Changed.Clear(); Send(c, 200, D("changes", ch, "newStartPageToken", "t2")); return; }
                    if (p == "/drive/v3/files/fin") { Send(c, 200, D("id", "fin", "name", "Finance", "parents", new List<object> { "root" })); return; }
                    if (p == "/drive/v3/files/root") { Send(c, 200, D("id", "root", "name", "My Drive")); return; }
                    if (p == "/drive/v3/files/doc1/export") { Send(c, 200, Files["doc1"]); return; }
                    if (p.StartsWith("/drive/v3/files/") && q["alt"] == "media") { Send(c, 200, Files[p.Split('/').Last()]); return; }
                    if (p == "/upload/drive/v3/files") { Uploads.Add(body); Send(c, 200, D("id", "up")); return; }
                }
                Send(c, 404, D("error", D("message", p)));
            }
            catch (Exception e) { try { Send(c, 500, D("error", D("message", e.Message))); } catch { } }
        }
        public void Dispose() { try { l.Stop(); l.Close(); } catch { } }
    }

    public class GoogleTests
    {
        static bool Have { get { var r = Environment.GetEnvironmentVariable("OB_RESTIC"); return !string.IsNullOrEmpty(r) && File.Exists(r); } }

        [Fact]
        public void GoogleWorkspace_GmailAndDrive_ItemLevel_ChangesOnly_RestoreIntoGoogle()
        {
            if (!Have) throw NotTested.Because("OB_RESTIC (the restic program) is not set");
            using (var gg = new FakeGoogle())
            using (var env = new Env())
            {
                Environment.SetEnvironmentVariable("OB_GOOGLE", gg.Url); Environment.SetEnvironmentVariable("OB_GOOGLE_TOKEN", gg.Url + "/token");
                try
                {
                    env.CreateUser("gws1", "Customer-Pass-1", 5);
                    var app = env.Agent("gws1", "Customer-Pass-1");
                    var mirror = env.Dir("mirror");
                    var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "Acme Google", Type = "GWS", Engine = "RESTIC", Vss = false, GwsAdmin = "admin@acme.com", Sources = { mirror } });
                    app.Home.SaveSecret(set.Id + "-gws", gg.KeyJson);
                    var r1 = app.Backup(set.Id);
                    Assert.True(r1.Result == "BS_STOP_SUCCESS", string.Join("\n", r1.LogLines));
                    Assert.Equal(0, gg.BadJwt);                                                         // our RS256 signatures verify
                    Assert.Equal(2, Directory.GetFiles(Path.Combine(mirror, "Gmail", "ann@acme.com"), "*.eml", SearchOption.AllDirectories).Length);
                    Assert.Equal("docx-export-v1", File.ReadAllText(Path.Combine(mirror, "Drive", "ann@acme.com", "Plan.docx")));          // a Google Doc as .docx
                    Assert.Equal("pdf v1", File.ReadAllText(Path.Combine(mirror, "Drive", "ann@acme.com", "Finance", "report.pdf")));
                    Assert.False(Directory.Exists(Path.Combine(mirror, "Gmail", "ben@acme.com")));                                        // suspended: skipped

                    lock (gg.Gate)
                    {
                        gg.Mail.Remove("m1"); gg.HistDeleted.Add("m1");
                        gg.Mail["m3"] = FakeGoogle.Raw("Quote", "third"); gg.HistAdded.Add("m3"); gg.History = 120;
                        gg.Files["pdf1"] = Encoding.UTF8.GetBytes("pdf v2"); gg.Changed.Add("pdf1");
                    }
                    Thread.Sleep(1100);
                    var r2 = app.Backup(set.Id);
                    Assert.True(r2.LogLines.Any(l => l.Contains("mail new 1 / deleted 1") && l.Contains("Drive new 0 / changed 1")), string.Join("\n", r2.LogLines.Where(l => l.Contains("Google"))));

                    var rs = app.Restic(app.Sets().First(x => x.Id == set.Id), "Customer-Pass-1");
                    var first = rs.SnapshotList().Last()["id"];
                    var files = rs.Ls(first).Where(f => f["type"] == "file").Select(f => f["path"]).ToList();
                    var back = rs.RestoreToGoogle(first, new[] { files.Single(f => f.Contains("Invoice 2026-17")), files.Single(f => f.EndsWith("report.pdf")) }, new List<string>());
                    Assert.Equal(1, back.Mail); Assert.Equal(1, back.Files); Assert.Equal(0, back.Failed);
                    Assert.Contains("Subject: Invoice 2026-17", gg.InsertedMail.Single());
                    Assert.Contains("pdf v1", gg.Uploads.Single()); Assert.Contains("F-restored", gg.Uploads.Single());
                }
                finally { Environment.SetEnvironmentVariable("OB_GOOGLE", null); Environment.SetEnvironmentVariable("OB_GOOGLE_TOKEN", null); }
            }
        }
    }
}
