using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using OnlineBackup.Core;

namespace OnlineBackup.Server
{
    /// <summary>
    /// SETUP-001: the installation wizard. "Setup.cmd" (double-click; it asks for administrator rights) runs
    /// "OnlineBackup.Server.exe setup", which serves the wizard on 127.0.0.1 only, with a one-time key, and opens it in
    /// the browser. Plain questions in the user's language (English default, more languages by the browser's language);
    /// the installation itself is <see cref="Installer"/>.
    /// </summary>
    public sealed class SetupWizard : IDisposable
    {
        readonly HttpListener http = new HttpListener();
        readonly string packageDir;
        public readonly string Key = Bytes.Hex(Bytes.Random(16));
        public readonly int Port;
        public readonly ManualResetEvent Closed = new ManualResetEvent(false);
        readonly object gate = new object();
        readonly List<string> lines = new List<string>();
        bool running, done; string error; Installer.Result result;

        public SetupWizard(string packageServerDir)
        {
            packageDir = packageServerDir;
            var t = new TcpListener(IPAddress.Loopback, 0); t.Start(); Port = ((IPEndPoint)t.LocalEndpoint).Port; t.Stop();
            http.Prefixes.Add("http://127.0.0.1:" + Port + "/"); http.Start();
            new Thread(() => { while (http.IsListening) { try { var c = http.GetContext(); ThreadPool.QueueUserWorkItem(_ => Handle(c)); } catch { return; } } }) { IsBackground = true }.Start();
        }

        public string Url { get { return "http://127.0.0.1:" + Port + "/#" + Key; } }

        public void Dispose() { try { http.Stop(); http.Close(); } catch { } }

        void Handle(HttpListenerContext c)
        {
            try
            {
                var p = c.Request.Url.AbsolutePath;
                if (p == "/favicon.ico") { c.Response.StatusCode = 204; c.Response.Close(); return; }
                if (c.Request.Headers["Host"] != "127.0.0.1:" + Port) { Reply(c, 403, "{}"); return; }   // another site in the browser cannot reach it
                if (p.StartsWith("/i18n/")) { AdminUi.ServeI18n(c, p.Substring(6)); return; }
                if (p == "/" || p == "/setup.js")
                {
                    using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(p == "/" ? "web.setup.html" : "web.setup.js"))
                    {
                        c.Response.ContentType = p == "/" ? "text/html; charset=utf-8" : "application/javascript; charset=utf-8";
                        c.Response.Headers["Content-Security-Policy"] = "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; script-src 'self'; frame-ancestors 'none'";
                        c.Response.Headers["Cache-Control"] = "no-store";
                        c.Response.ContentLength64 = s.Length; s.CopyTo(c.Response.OutputStream); c.Response.Close();
                    }
                    return;
                }
                if (c.Request.Headers["X-Key"] != Key) { Reply(c, 403, "{}"); return; }
                string body; using (var rd = new StreamReader(c.Request.InputStream, Encoding.UTF8)) body = rd.ReadToEnd();
                switch (p)
                {
                    case "/api/info": Reply(c, 200, Json.Write(Installer.Info(packageDir))); return;
                    case "/api/check":
                        {
                            var a = Answers(body);
                            Reply(c, 200, Json.Write(new Dictionary<string, object> { { "errors", Installer.Check(a, Installer.Existing() != null).Cast<object>().ToList() } }));
                            return;
                        }
                    case "/api/install":
                        {
                            var a = Answers(body);
                            bool update = Installer.Existing() != null;
                            var errors = Installer.Check(a, update);
                            if (errors.Count > 0) { Reply(c, 200, Json.Write(new Dictionary<string, object> { { "errors", errors.Cast<object>().ToList() } })); return; }
                            lock (gate) { if (running) { Reply(c, 409, "{}"); return; } running = true; done = false; error = null; lines.Clear(); }
                            new Thread(() =>
                            {
                                try { var r = Installer.Run(a, packageDir, l => { lock (gate) lines.Add(l); }); lock (gate) result = r; }
                                catch (Exception e) { lock (gate) error = e.Message; }
                                finally { lock (gate) { done = true; running = false; } }
                            }) { IsBackground = true }.Start();
                            Reply(c, 200, "{\"started\":true}");
                            return;
                        }
                    case "/api/progress":
                        lock (gate)
                        {
                            var m = new Dictionary<string, object> { { "lines", lines.Cast<object>().ToList() }, { "done", done }, { "error", error } };
                            if (result != null) m["result"] = new Dictionary<string, object> { { "adminUrl", result.AdminUrl }, { "pin", result.Pin }, { "systemHome", result.SystemHome }, { "usersHome", result.UsersHome }, { "update", result.Update } };
                            Reply(c, 200, Json.Write(m));
                        }
                        return;
                    case "/api/close": Reply(c, 200, "{}"); Closed.Set(); return;
                }
                Reply(c, 404, "{}");
            }
            catch (Exception e) { try { Reply(c, 500, Json.Write(new Dictionary<string, object> { { "error", e.Message } })); } catch { } }
        }

        static SetupAnswers Answers(string body)
        {
            var j = Json.Obj(Json.Parse(body));
            Func<string, string> s = k => Json.Str(j, k) ?? "";
            Func<string, int, int> n = (k, d) => { int v; return int.TryParse(s(k), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : d; };
            return new SetupAnswers
            {
                Language = s("language"), Product = s("product"), Slogan = s("slogan"), Company = s("company"), Phone = s("phone"), Email = s("email"), Website = s("website"), Color = s("color"), Accent = s("accent"), Logo = s("logo"),
                HostName = s("hostName").Trim(), Port = n("port", 8443), DataRoot = s("dataRoot"), AdminLogin = s("adminLogin").Trim(), AdminPassword = s("adminPassword"),
                AlertEmail = s("alertEmail").Trim(), MailProvider = s("mailProvider") == "" ? "none" : s("mailProvider"), SmtpHost = s("smtpHost").Trim(), SmtpPort = n("smtpPort", 587),
                SmtpLogin = s("smtpLogin").Trim(), SmtpPassword = s("smtpPassword")
            };
        }

        static void Reply(HttpListenerContext c, int status, string json)
        {
            var b = Encoding.UTF8.GetBytes(json); c.Response.StatusCode = status; c.Response.ContentType = "application/json"; c.Response.Headers["Cache-Control"] = "no-store";
            c.Response.ContentLength64 = b.Length; c.Response.OutputStream.Write(b, 0, b.Length); c.Response.Close();
        }
    }
}
