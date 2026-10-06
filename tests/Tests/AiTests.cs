using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>A stand-in for the Claude API (and a ticket system): records every request, answers with a JSON text block.</summary>
    sealed class FakeClaude : IDisposable
    {
        readonly HttpListener l = new HttpListener();
        public readonly string Url;
        public string Answer = "{}";
        public string StopReason = "end_turn";
        public readonly List<KeyValuePair<string, string>> Requests = new List<KeyValuePair<string, string>>();   // path+headers, body
        public FakeClaude()
        {
            var t = new TcpListener(IPAddress.Loopback, 0); t.Start(); int port = ((IPEndPoint)t.LocalEndpoint).Port; t.Stop();
            Url = "http://127.0.0.1:" + port + "/";
            l.Prefixes.Add(Url); l.Start();
            new Thread(() =>
            {
                while (l.IsListening)
                {
                    HttpListenerContext c;
                    try { c = l.GetContext(); } catch { return; }
                    var body = new StreamReader(c.Request.InputStream, Encoding.UTF8).ReadToEnd();
                    var head = c.Request.Url.PathAndQuery + "\n" + string.Join("\n", c.Request.Headers.AllKeys.Select(k => k.ToLowerInvariant() + ": " + c.Request.Headers[k]));
                    lock (Requests) Requests.Add(new KeyValuePair<string, string>(head, body));
                    string reply = c.Request.Url.AbsolutePath.StartsWith("/tickets") ? "{\"id\":1}"
                        : Json.Write(new Dictionary<string, object>
                        {
                            ["id"] = "msg_test", ["type"] = "message", ["role"] = "assistant", ["model"] = "claude-opus-5-5",
                            ["content"] = new List<object> { new Dictionary<string, object> { ["type"] = "text", ["text"] = Answer } },
                            ["stop_reason"] = StopReason, ["stop_sequence"] = null,
                            ["usage"] = new Dictionary<string, object> { ["input_tokens"] = 10, ["output_tokens"] = 20 },
                        });
                    var b = Encoding.UTF8.GetBytes(reply);
                    c.Response.ContentType = "application/json"; c.Response.ContentLength64 = b.Length; c.Response.OutputStream.Write(b, 0, b.Length); c.Response.Close();
                }
            }) { IsBackground = true }.Start();
            Environment.SetEnvironmentVariable("OB_AI_BASE_URL", Url.TrimEnd('/'));
        }
        public void Dispose() { Environment.SetEnvironmentVariable("OB_AI_BASE_URL", null); try { l.Stop(); l.Close(); } catch { } }
    }

    /// <summary>AI-010..080: the AI assistant (with a fake Claude), the learned ransomware check, the forecasts, the report.</summary>
    public class AiTests
    {
        const string Diagnosis = "{\"summary\":\"The backup could not read the Finance folder.\",\"cause\":\"Access denied for the backup service account.\",\"steps\":[\"Give SYSTEM read access to D:\\\\Finance\",\"Run the backup again\"],\"severity\":\"critical\",\"category\":\"permissions\",\"needsTechnician\":true}";

        static string FailedLog(BackupSetInfo set)
        {
            var now = DateTime.UtcNow;
            return string.Join("\r\n", new[]
            {
                AhsayLog.Line(now, "start", message: "Backup started"),
                AhsayLog.Line(now, "info", message: "Connecting with password=Hunter2Secret to the share"),
                AhsayLog.Line(now, "err", message: "Access to D:\\Finance\\report.xlsx is denied"),
                AhsayLog.Line(now, "end", message: "BS_STOP_BY_SYSTEM_ERROR"),
            }) + "\r\n";
        }

        [Fact]
        public void FailedJob_IsExplainedByTheAi_WithoutSecrets_AndOpensATicket()
        {
            using (var fake = new FakeClaude())
            using (var env = new Env())
            {
                env.CreateUser("aicust", "Customer-Pass-1");
                var app = env.Agent("aicust", "Customer-Pass-1");
                var src = env.Dir("src"); File.WriteAllText(Path.Combine(src, "a.txt"), "a");
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Key-Pass-1", new BackupSetInfo { Name = "Finance", Sources = { src } });
                var logDir = Path.Combine(env.Api.UserStore.UserDir("aicust"), "logs", set.Id, "Backup"); Directory.CreateDirectory(logDir);
                File.WriteAllText(Path.Combine(logDir, "2026-10-01-22-00-00.log"), FailedLog(set));
                var admin = env.Admin();
                var ask = new Msg().Set("set", set.Id).Set("cat", "Backup").Set("file", "2026-10-01-22-00-00.log").Set("lang", "en");

                // off by default: a clear message, nothing sent
                var off = Assert.ThrowsAny<Exception>(() => admin.Call("POST", "/api/admin/users/aicust/aidiagnose", ask));
                Assert.Contains("AI assistant is off", off.Message);
                Assert.Empty(fake.Requests);

                // switched on with a key and a ticket address; the key never comes back
                admin.Call("POST", "/api/admin/settings", new Msg().Set("aiOn", "1").Set("aiKey", "sk-ant-test-KEY-123").Set("aiTicketUrl", fake.Url + "tickets").Set("aiTicketToken", "tick-tok-9"));
                var st = admin.Call("GET", "/api/admin/settings");
                Assert.Equal("1", st["aiOn"]); Assert.Equal("1", st["aiHasKey"]); Assert.Equal("claude-opus-5-5", st["aiModel"]);
                Assert.DoesNotContain("sk-ant-test", st.ToString());
                foreach (var file in Directory.GetFiles(env.SystemHome, "*", SearchOption.AllDirectories))
                    Assert.DoesNotContain("sk-ant-test", File.ReadAllText(file));   // stored protected, never in a log

                fake.Answer = Diagnosis;
                var d = admin.Call("POST", "/api/admin/users/aicust/aidiagnose", ask.Set("ticket", "1"));
                Assert.Equal("The backup could not read the Finance folder.", d["summary"]);
                Assert.Equal("critical", d["severity"]); Assert.Equal("1", d["needsTechnician"]);
                Assert.Equal(2, d.List("steps").Count);
                Assert.Equal("1", d["ticket"]);

                var call = fake.Requests.First(r => r.Key.StartsWith("/v1/messages"));
                Assert.Contains("x-api-key: sk-ant-test-KEY-123", call.Key);
                Assert.Contains("server-side-fallback-2026-07-01", call.Key);
                Assert.Contains("\"claude-opus-5-5\"", call.Value);
                Assert.Contains("json_schema", call.Value);
                Assert.Contains("report.xlsx is denied", call.Value);            // the log is what it reads
                Assert.DoesNotContain("Hunter2Secret", call.Value);              // secrets are masked
                var ticket = fake.Requests.First(r => r.Key.StartsWith("/tickets"));
                Assert.Contains("authorization: Bearer tick-tok-9", ticket.Key);
                Assert.Contains("Access denied for the backup service account.", ticket.Value);

                // kept beside the log: the second look costs nothing
                int n = fake.Requests.Count;
                var again = admin.Call("POST", "/api/admin/users/aicust/aidiagnose", ask.Set("ticket", "0"));
                Assert.Equal("1", again["cached"]); Assert.Equal(n, fake.Requests.Count);

                // a failed run is explained automatically in the background, kept beside its log
                var cc = new Client(env.Url);
                cc.Session = cc.Call("POST", "/api/login", new Msg().Set("login", "aicust").Set("password", "Customer-Pass-1"))["session"];
                var abort = new Msg();
                foreach (var line in FailedLog(set).Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries)) abort.Add("log", new Msg().Set("l", line));
                var job = cc.Call("POST", "/api/sets/" + set.Id + "/begin", new Msg())["job"];
                cc.Call("POST", "/api/sets/" + set.Id + "/jobs/" + job + "/abort", abort);
                var auto = Path.Combine(logDir, job + ".log.ai");
                for (int i = 0; i < 100 && !File.Exists(auto); i++) Thread.Sleep(100);
                Assert.True(File.Exists(auto));
                Assert.Equal("permissions", Msg.Parse(File.ReadAllBytes(auto))["category"]);

                // a refusal is reported, not shown as an answer
                fake.StopReason = "refusal";
                var refused = Assert.ThrowsAny<Exception>(() => admin.Call("POST", "/api/admin/users/aicust/aidiagnose", ask.Set("again", "1")));
                Assert.Contains("declined", refused.Message);
            }
        }

        [Fact]
        public void Redact_MasksSecretsAndKeepsTheEnd()
        {
            var r = Ai.Redact(new[] { "first line", "token: abc.def", "Authorization Bearer eyJhbGciOi", "key AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "last error" });
            Assert.DoesNotContain("abc.def", r); Assert.DoesNotContain("eyJhbGciOi", r); Assert.DoesNotContain("AAAAAAAAAAAAAAAAAAAA", r);
            Assert.Contains("last error", r);
            var big = Ai.Redact(Enumerable.Range(0, 5000).Select(i => "line " + i), 2000);
            Assert.Contains("line 4999", big); Assert.DoesNotContain("line 0\n", big);
        }

        static RunStat Run(int day, long prev, long upd, long del, string ext = "docx", long extCount = 5, long nw = 3)
        {
            return new RunStat { Time = new DateTime(2026, 9, 1, 22, 0, 0, DateTimeKind.Utc).AddDays(day), Prev = prev, Upd = upd, Del = del, New = nw, TopExt = ext, TopExtCount = extCount };
        }

        [Fact]
        public void Ransomware_LearnsWhatIsNormalForEachSet()
        {
            // before 7 runs: the fixed rule (50 files and 30%)
            Assert.True(Insights.Check(new List<RunStat>(), Run(0, 1000, 350, 0), 50, 30).Suspect);
            Assert.False(Insights.Check(new List<RunStat>(), Run(0, 1000, 100, 0), 50, 30).Suspect);

            // a set that changes ~40% every night (a database dump folder): 45% is normal, not ransomware
            var busy = Enumerable.Range(0, 10).Select(i => Run(i, 1000, 380 + i * 4, 10)).ToList();
            var v = Insights.Check(busy, Run(11, 1000, 440, 10), 50, 30);
            Assert.True(v.Learned); Assert.False(v.Suspect);
            Assert.True(Insights.Check(busy, Run(11, 1000, 950, 10), 50, 30).Suspect);        // nearly everything: always suspect

            // a quiet set (~1%): 15% changed is far from normal although below the fixed 30%
            var quiet = Enumerable.Range(0, 10).Select(i => Run(i, 2000, 15 + i % 3, 2)).ToList();
            var q = Insights.Check(quiet, Run(11, 2000, 300, 0), 50, 30);
            Assert.True(q.Suspect); Assert.Contains("normal for this set", q.Why);
            Assert.False(Insights.Check(quiet, Run(11, 2000, 40, 0), 50, 30).Suspect);       // below the minimum of files

            // a new extension suddenly dominating the run (encrypted copies)
            var e = Insights.Check(quiet, Run(11, 2000, 20, 0, "locked", 400, 400), 50, 30);
            Assert.True(e.Suspect); Assert.Contains("locked", e.Why);
        }

        [Fact]
        public void Forecasts_DiskQuotaAndComputersAtRisk()
        {
            var now = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
            // 10 GB a day towards a 500 GB limit from 300 GB → about 20 days
            var pts = Enumerable.Range(0, 20).Select(i => new KeyValuePair<DateTime, double>(now.AddDays(-19 + i), 110e9 + i * 10e9)).ToList();
            Assert.InRange(Insights.DaysUntil(pts, 500e9, now).Value, 19, 21);
            Assert.Null(Insights.DaysUntil(pts.Select(p => new KeyValuePair<DateTime, double>(p.Key, 5)).ToList(), 10, now));   // not growing

            // the disk file: 50 GB free, losing 5 GB a day
            var home = Path.Combine(Path.GetTempPath(), "obdisk-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                for (int i = 0; i < 10; i++) Insights.RecordDisks(home, new[] { new Msg().Set("path", "E:\\users").Set("free", (long)(95e9 - i * 5e9)) }, now.AddDays(-9 + i));
                var f = Insights.DiskForecast(home, new[] { new Msg().Set("path", "E:\\users").Set("free", (long)50e9) }, now).Single();
                Assert.InRange(long.Parse(f["days"]), 9, 11);
            }
            finally { try { Directory.Delete(home, true); } catch { } }

            // a computer that backs up every night, then stopped three days ago
            var daily = Enumerable.Range(0, 20).Select(i => new RunStat { Time = now.AddDays(-23 + i).Date.AddHours(22) }).ToList();
            var late = Insights.MissRisk(daily, now);
            Assert.True(late.Int("risk") >= 60, late.ToString()); Assert.Contains("no backup for", late["why"]);
            var regular = Enumerable.Range(0, 20).Select(i => new RunStat { Time = now.AddDays(-20 + i).Date.AddHours(22) }).Where(r => r.Time < now).ToList();
            Assert.True(Insights.MissRisk(regular, now).Int("risk") < 30);
            Assert.Equal("1", Insights.MissRisk(daily.Take(2).ToList(), now)["learning"]);
        }

        [Fact]
        public void CompletedBackups_FeedTheLearning_AndTheReportAndInsights()
        {
            using (var env = new Env())
            {
                env.CreateUser("repcust", "Customer-Pass-1");
                var app = env.Agent("repcust", "Customer-Pass-1");
                var src = env.Dir("src"); File.WriteAllText(Path.Combine(src, "a.txt"), "a");
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Key-Pass-1", new BackupSetInfo { Name = "Office files", Sources = { src } });
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);
                File.WriteAllText(Path.Combine(src, "b.txt"), "b");
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);
                Assert.Equal(2, Insights.History(env.Api.UserStore.UserDir("repcust"), set.Id).Count);

                var admin = env.Admin();
                var ins = admin.Call("GET", "/api/admin/insights");
                Assert.Equal("0", ins["ai"]);
                Assert.Equal("0", ins.List("learned").Single()["learned"]);
                Assert.Single(ins.List("disks"));

                foreach (var lang in new[] { "en", "he" })
                {
                    var r = (HttpWebRequest)WebRequest.Create(env.Url + "api/admin/users/repcust/compliance?lang=" + lang);
                    r.Headers["X-Session"] = admin.Session;
                    string html;
                    using (var resp = (HttpWebResponse)r.GetResponse()) { Assert.StartsWith("text/html", resp.ContentType); html = new StreamReader(resp.GetResponseStream(), Encoding.UTF8).ReadToEnd(); }
                    Assert.Contains("Office files", html);
                    Assert.Contains(L.T(lang, "Restore certificate"), html);
                    Assert.Contains(L.T(lang, "Not tested yet"), html);
                    Assert.Contains(lang == "he" ? "dir='rtl'" : "dir='ltr'", html);
                }
            }
        }

        [Fact]
        public void WebRestoreSearch_ByKeywords_AndInPlainWords_SendsNoFileNames()
        {
            var restic = Environment.GetEnvironmentVariable("OB_RESTIC");
            if (string.IsNullOrEmpty(restic) || !File.Exists(restic)) return;
            using (var fake = new FakeClaude())
            using (var env = new Env())
            {
                env.CreateUser("findcust", "Customer-Pass-1");
                var app = env.Agent("findcust", "Customer-Pass-1");
                var src = env.Dir("src"); Directory.CreateDirectory(Path.Combine(src, "Dana"));
                File.WriteAllText(Path.Combine(src, "Dana", "Budget 2026.xlsx"), "numbers");
                File.WriteAllText(Path.Combine(src, "Dana", "letter.docx"), "words");
                File.WriteAllText(Path.Combine(src, "Secret-Plan.xlsx"), "other");
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "My-Secret-Key-9", new BackupSetInfo { Name = "Office", Engine = "RESTIC", Sources = { src } });
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);

                var c = new Client(env.Url);
                c.Session = c.Call("POST", "/api/login", new Msg().Set("login", "findcust").Set("password", "Customer-Pass-1"))["session"];
                // AI off: keywords in the file name
                var k = c.Call("POST", "/api/webrestore/" + set.Id + "/search", new Msg().Set("key", "My-Secret-Key-9").Set("q", "budget"));
                Assert.Equal("0", k["ai"]);
                Assert.Equal("Budget 2026.xlsx", k.List("files").Single()["name"]);
                Assert.Empty(fake.Requests);

                // AI on: "the Excel file Dana edited this week" → extensions + folder + dates
                env.Admin().Call("POST", "/api/admin/settings", new Msg().Set("aiOn", "1").Set("aiKey", "sk-ant-test-KEY-123"));
                fake.Answer = "{\"nameContains\":[],\"extensions\":[\"xlsx\",\"xls\"],\"pathContains\":[\"Dana\",\"דנה\"],\"modifiedFrom\":\"" + DateTime.Now.AddDays(-7).ToString("yyyy-MM-dd") + "T00:00:00\",\"modifiedTo\":\"" + DateTime.Now.AddDays(1).ToString("yyyy-MM-dd") + "T00:00:00\",\"explanation\":\"Excel files in Dana's folder from the last week\"}";
                var a = c.Call("POST", "/api/webrestore/" + set.Id + "/search", new Msg().Set("key", "My-Secret-Key-9").Set("q", "the Excel file Dana edited this week").Set("lang", "en"));
                Assert.Equal("1", a["ai"]);
                Assert.Equal("Excel files in Dana's folder from the last week", a["explanation"]);
                var f = a.List("files").Single();
                Assert.Equal("Budget 2026.xlsx", f["name"]); Assert.False(string.IsNullOrEmpty(f["point"]));
                var sent = fake.Requests.Single().Value;
                Assert.Contains("the Excel file Dana edited this week", sent);
                Assert.DoesNotContain("Budget", sent); Assert.DoesNotContain("Secret-Plan", sent); Assert.DoesNotContain("My-Secret-Key", sent);

                // the found file downloads from its point
                var dl = (HttpWebRequest)WebRequest.Create(env.Url + "api/webrestore/" + set.Id + "/download");
                dl.Method = "POST"; dl.Headers["X-Session"] = c.Session; dl.ContentType = "application/xml";
                var body = new Msg().Set("key", "My-Secret-Key-9").Set("point", f["point"]); body.Add("paths", new Msg().Set("p", f["path"]));
                var bytes = body.ToBytes(); using (var s = dl.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
                using (var resp = (HttpWebResponse)dl.GetResponse()) Assert.Equal(200, (int)resp.StatusCode);
            }
        }
    }
}
