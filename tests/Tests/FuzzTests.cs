using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// FUZZ-010: the server against junk and attacks — broken XML, huge bodies, bytes that are not text, paths that
    /// climb out of their folder, names and values that try to break out of XML / commands, every route without a
    /// sign-in, and one customer reaching for another customer's sets. The server must answer every one of them with a
    /// clear refusal (never a server error), keep running, write nothing outside its folders, and never let one customer
    /// see another's data.
    /// </summary>
    public class FuzzTests
    {
        static (int status, string body) Raw(string url, string method, byte[] body, Dictionary<string, string> headers = null)
        {
            var r = (HttpWebRequest)WebRequest.Create(url);
            r.Method = method; r.Timeout = 20000; r.ContentType = "application/xml";
            if (headers != null) foreach (var kv in headers) r.Headers[kv.Key] = kv.Value;
            if (body != null) { r.ContentLength = body.Length; using (var s = r.GetRequestStream()) s.Write(body, 0, body.Length); }
            else if (method != "GET") r.ContentLength = 0;
            try { using (var resp = (HttpWebResponse)r.GetResponse()) using (var sr = new StreamReader(resp.GetResponseStream())) return ((int)resp.StatusCode, sr.ReadToEnd()); }
            catch (WebException e)
            {
                var resp = e.Response as HttpWebResponse; if (resp == null) return (0, e.Message);
                using (var sr = new StreamReader(resp.GetResponseStream())) return ((int)resp.StatusCode, sr.ReadToEnd());
            }
        }

        static readonly string[] Junk = {
            "", "<", "<m>", "<m><f n=\"x\">", "not xml at all", "<?xml version=\"1.0\"?><!DOCTYPE m [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><m><f n=\"login\">&x;</f></m>",
            "<m><f n=\"login\">../../../etc</f><f n=\"set\">..\\..\\x</f></m>", "<m><f n=\"login\">a'; DROP TABLE users;--</f><f n=\"password\">\" OR 1=1</f></m>",
            "<m><f n=\"set\">&lt;BACKUP_SET ID=\"1\"/&gt;</f></m>", "<m><f n=\"name\">" + new string('A', 70000) + "</f></m>", "<m><l n=\"x\">" + string.Concat(Enumerable.Repeat("<i/>", 20000)) + "</l></m>",
            "<m><f n=\"ip\">999.1.1.1/77</f><f n=\"hours\">-5</f><f n=\"quotaGB\">NaN</f><f n=\"hour\">99</f></m>", "<m><f n=\"path\">C:\\Windows\\System32</f><f n=\"name\">${jndi:ldap://x}</f></m>" };

        [Fact]
        public void JunkAndAttacks_AreRefusedClearly_TheServerKeepsRunning_NothingLeaks()
        {
            using (var env = new Env())
            {
                var admin = env.Admin();
                env.CreateUser("alice", "Customer-Pass-1"); env.CreateUser("bob", "Customer-Pass-1");
                var a = env.Agent("alice", "Customer-Pass-1", name: "alice-pc"); var b = env.Agent("bob", "Customer-Pass-1", name: "bob-pc");
                var srcA = env.Dir("a"); File.WriteAllText(Path.Combine(srcA, "secret-of-alice.txt"), "alice data");
                var setA = a.CreateSet(a.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "A", Sources = { srcA } });
                Assert.Equal("BS_STOP_SUCCESS", a.Backup(setA.Id).Result);
                var setB = b.CreateSet(b.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "B", Sources = { env.Dir("b") } });
                var url = env.Url.TrimEnd('/');
                var outside = Directory.GetFiles(Path.GetDirectoryName(env.SystemHome)).Length;
                var errors = new List<string>();
                Action<string, (int status, string body)> check = (what, r) => { if (r.status == 0 || r.status >= 500) errors.Add(what + " → " + r.status + " " + (r.body ?? "").Substring(0, Math.Min(120, (r.body ?? "").Length))); };

                var routes = new[] { "/api/admin/login", "/api/admin/users", "/api/admin/settings", "/api/admin/defaults", "/api/admin/guard", "/api/admin/guard/block", "/api/admin/tickets",
                    "/api/admin/users/alice/sets/" + setA.Id, "/api/admin/users/alice/details", "/api/admin/users/alice/computers/move", "/api/admin/configbackup", "/api/admin/maintenance",
                    "/api/login", "/api/register", "/api/signup", "/api/profile", "/api/sets", "/api/sets/" + setA.Id + "/begin", "/api/sets/" + setA.Id + "/files", "/api/sets/" + setA.Id + "/restic",
                    "/api/sets/" + setA.Id + "/jobs/2026-01-01-00-00-00/commit", "/api/folders", "/api/brand", "/restic/alice/" + setA.Id + "/config", "/restore/login", "/api/restore/login" };
                var sessions = new Dictionary<string, string>[] { null, new Dictionary<string, string> { { "X-Session", admin.Session } }, new Dictionary<string, string> { { "X-Device", b.Home.DeviceToken } } };
                foreach (var route in routes)
                    foreach (var h in sessions)
                    {
                        check("GET " + route, Raw(url + route, "GET", null, h));
                        foreach (var j in Junk) check("POST " + route + " " + j.Substring(0, Math.Min(30, j.Length)), Raw(url + route, "POST", Encoding.UTF8.GetBytes(j), h));
                        check("POST bytes " + route, Raw(url + route, "POST", new byte[] { 0xff, 0xfe, 0x00, 0xc3, 0x28, 0xa0, 0xa1 }, h));
                    }
                // a body far too large
                var big = Raw(url + "/api/admin/settings", "POST", new byte[30 * 1024 * 1024], new Dictionary<string, string> { { "X-Session", admin.Session } });
                Assert.True(big.status == 413 || big.status == 0, "30 MB: " + big.status);   // refused at once (the connection may close while it is still sending)
                Assert.Equal(200, Raw(url + "/api/brand", "GET", null).status);             // and the server goes on
                // paths that climb out
                foreach (var p in new[] { "/admin/..%2f..%2fconf%2fsystem.xml", "/admin/../conf/system.xml", "/i18n/..%2f..%2fconf%2fsystem.xml", "/restic/alice/..%2f..%2fdb/keys", "/api/admin/configbackup/..%2fsystem.xml", "/admin/%00", "/" + new string('a', 5000) })
                {
                    var r = Raw(url + p, "GET", null, new Dictionary<string, string> { { "X-Session", admin.Session } });
                    check("GET " + p, r);
                    Assert.DoesNotContain("<SYSTEM", r.body ?? "");
                    Assert.DoesNotContain("HASHED_PWD", r.body ?? "");
                }
                Assert.True(errors.Count == 0, "Server errors:\n" + string.Join("\n", errors.Take(40)));

                // one customer reaching for another's set: bob's computer asks for alice's files, keys and repository
                foreach (var p in new[] { "/api/sets/" + setA.Id + "/files", "/api/sets/" + setA.Id + "/points", "/api/sets/" + setA.Id + "/sharedkey", "/api/sets/" + setA.Id + "/restic", "/api/sets/" + setA.Id + "/begin" })
                {
                    var r = Raw(url + p, p.EndsWith("files") || p.EndsWith("points") || p.EndsWith("sharedkey") ? "GET" : "POST", null, new Dictionary<string, string> { { "X-Device", b.Home.DeviceToken } });
                    Assert.True(r.status >= 400 && r.status < 500, "bob reached alice's " + p + ": " + r.status);
                    Assert.DoesNotContain("secret-of-alice", r.body ?? "");
                }
                var token = a.Home.LoadSecret(setA.Id + "-restic-token");
                Assert.True(Raw(url + "/restic/alice/" + setA.Id + "/config", "GET", null, new Dictionary<string, string> { { "Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("bob:" + (token ?? "x"))) } }).status >= 400);

                // still running and correct after all of it; nothing written outside the server's folders
                Assert.Equal("BS_STOP_SUCCESS", a.Backup(setA.Id).Result);
                Assert.Equal(outside, Directory.GetFiles(Path.GetDirectoryName(env.SystemHome)).Length);
                Assert.Empty(Directory.GetFiles(env.SystemHome, "*", SearchOption.AllDirectories).Where(f => Path.GetFileName(f).Contains("..") || Path.GetFileName(f).Contains("DROP")));
                Assert.NotNull(setB);
            }
        }
    }
}
