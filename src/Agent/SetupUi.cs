using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using OnlineBackup.Core;

namespace OnlineBackup.Agent
{
    /// <summary>
    /// SETUP-C10 (owner): the installation of the client software is a wizard, not a console script. Setup.cmd opens it
    /// in the browser (on this computer only, with a one-time key): language → the backup server (the package's address,
    /// which can be changed and is checked before going on) → the IT company's agreement → a new customer or an existing
    /// one → the installation, step by step → "Open". The same Setup.Run does the work, so the console and the silent
    /// installation (--login …) behave exactly the same.
    /// </summary>
    public sealed class SetupUi : IDisposable
    {
        HttpListener http;
        readonly string packageDir;
        readonly XElement conn, brand;
        public string Key { get; private set; }
        public int Port { get; private set; }
        public readonly ManualResetEvent Closed = new ManualResetEvent(false);
        readonly object gate = new object();
        readonly List<string> lines = new List<string>();
        bool running, done; string error; Setup.Result result;
        /// <summary>Tests: answers and switches the wizard does not ask for (install-dir, data-dir, no-service …).</summary>
        public readonly Dictionary<string, string> Fixed = new Dictionary<string, string>();
        public readonly HashSet<string> Flags = new HashSet<string>();

        public SetupUi(string packageDir, int port = 0)
        {
            this.packageDir = packageDir;
            conn = OnlineBackup.Core.Atomic.LoadXElement(Path.Combine(packageDir, "connection.xml"));
            var bp = Path.Combine(packageDir, "branding.xml");
            brand = File.Exists(bp) ? OnlineBackup.Core.Atomic.LoadXElement(bp) : new XElement("BRANDING");
            Key = Bytes.Hex(Bytes.Random(16));
            Exception last = null;
            for (int i = 0; i < 30; i++)
            {
                Port = port > 0 ? port : 18260 + i;
                // a new listener for every try: one that failed to start cannot be used again
                http = new HttpListener();
                try { http.Prefixes.Add("http://127.0.0.1:" + Port + "/"); http.Start(); last = null; break; }
                catch (HttpListenerException e) { last = e; try { http.Close(); } catch (Exception) { } if (port > 0) break; }
            }
            if (last != null) throw last;
            new Thread(Loop) { IsBackground = true }.Start();
        }

        public string Url { get { return "http://127.0.0.1:" + Port + "/#" + Key; } }

        public void Dispose() { try { http.Stop(); http.Close(); } catch (Exception) { } }

        /// <summary>Setup.cmd with no answers on Windows: the wizard; returns when the person finished or closed it.</summary>
        public static int Run(string packageDir)
        {
            using (var ui = new SetupUi(packageDir))
            {
                Console.WriteLine("The installation opens in your browser: " + ui.Url);
                try { Setup.OpenBrowser(ui.Url); } catch (Exception) { Console.WriteLine("Open this address in a browser to go on."); }
                ui.Closed.WaitOne();
                Thread.Sleep(800);   // the last answer reaches the browser
                return ui.result != null ? 0 : 1;
            }
        }

        void Loop()
        {
            while (http.IsListening)
            {
                HttpListenerContext c;
                try { c = http.GetContext(); } catch (Exception) { return; }
                ThreadPool.QueueUserWorkItem(_ => { try { Handle(c); } catch (Exception) { try { c.Response.Abort(); } catch (Exception) { } } });
            }
        }

        static string B(XElement b, string a) { return (string)b.Attribute(a) ?? ""; }

        void Handle(HttpListenerContext ctx)
        {
            var req = ctx.Request; var path = req.Url.AbsolutePath;
            var host = (req.Headers["Host"] ?? "").Split(':')[0];
            if (host != "127.0.0.1" && host != "localhost") { Text(ctx, 403, "text/plain", "forbidden"); return; }   // DNS rebinding
            if (req.HttpMethod == "GET" && (path == "/" || path == "/index.html"))
            {
                using (var rs0 = typeof(SetupUi).Assembly.GetManifestResourceStream("OnlineBackup.Agent.setup-client.html"))
                using (var r = new StreamReader(rs0, Encoding.UTF8)) Text(ctx, 200, "text/html; charset=utf-8", r.ReadToEnd());
                return;
            }
            if (req.HttpMethod == "GET" && path.StartsWith("/i18n/"))
            {
                var name = path.Substring(6);
                string type; var asset = WebAssets.Get(name, out type);
                if (asset != null) { Bin(ctx, 200, type, asset); return; }
                if (name == "i18n.js") { using (var rs = typeof(L).Assembly.GetManifestResourceStream("i18n.js")) using (var rd = new StreamReader(rs, Encoding.UTF8)) Text(ctx, 200, "application/javascript; charset=utf-8", rd.ReadToEnd()); return; }
                if (name.EndsWith(".json") && L.Languages.Contains(name.Substring(0, name.Length - 5))) { Text(ctx, 200, "application/json; charset=utf-8", L.Raw(name.Substring(0, name.Length - 5))); return; }
                Text(ctx, 404, "text/plain", "not found"); return;
            }
            if (req.HttpMethod == "GET" && path == "/logo")
            {
                var logo = B(brand, "LOGO"); int c = logo.IndexOf(',');
                if (!logo.StartsWith("data:image/") || c < 0) { Text(ctx, 404, "text/plain", ""); return; }
                Bin(ctx, 200, logo.Substring(5, logo.IndexOf(';') - 5), Convert.FromBase64String(logo.Substring(c + 1)));
                return;
            }
            if (!path.StartsWith("/api/")) { Text(ctx, 404, "text/plain", "not found"); return; }
            if (req.Headers["X-Key"] != Key) { Text(ctx, 403, "application/json", "{\"message\":\"forbidden\"}"); return; }
            Dictionary<string, object> q = new Dictionary<string, object>();
            if (req.HttpMethod == "POST") using (var rd = new StreamReader(req.InputStream, Encoding.UTF8)) { var body = rd.ReadToEnd(); if (body.Length > 0) q = Json.Obj(Json.Parse(body)); }
            Func<string, string> s = k => { var v = Json.Str(q, k); return string.IsNullOrEmpty(v) ? null : v.Trim(); };
            try
            {
                switch (path)
                {
                    case "/api/info":
                        {
                            var existing = File.Exists(Path.Combine(DataDir(), "agent.xml"));
                            Ok(ctx, new Dictionary<string, object> {
                                { "product", B(brand, "PRODUCT").Length > 0 ? B(brand, "PRODUCT") : "Backup" }, { "slogan", B(brand, "SLOGAN") }, { "company", B(brand, "COMPANY") },
                                { "phone", B(brand, "PHONE") }, { "email", B(brand, "EMAIL") }, { "color", B(brand, "COLOR") }, { "accent", B(brand, "ACCENT") }, { "logo", B(brand, "LOGO").StartsWith("data:image/") },
                                { "language", L.Norm(B(brand, "LANGUAGE")) }, { "server", (string)conn.Attribute("SERVER") ?? "" }, { "computer", Environment.MachineName }, { "existing", existing } });
                            return;
                        }
                    case "/api/check":
                        {
                            Ok(ctx, CheckServer(conn, s("server"), s("lang") ?? "en"));
                            return;
                        }
                    case "/api/install":
                        {
                            lock (gate)
                            {
                                if (running) { Ok(ctx, new Dictionary<string, object> { { "ok", true } }); return; }
                                running = true; done = false; error = null; lines.Clear();
                            }
                            var answers = new Dictionary<string, string>();
                            foreach (var k in new[] { "server", "pin", "company", "email", "phone", "login", "password", "otp", "lang" }) if (s(k) != null) answers[k] = s(k);
                            if (Json.Str(q, "mode") != "new") answers.Remove("company");
                            foreach (var kv in Fixed) answers[kv.Key] = kv.Value;
                            var accept = Json.Str(q, "accept") == "1";
                            new Thread(() =>
                            {
                                try
                                {
                                    var r = Setup.Run(packageDir, k => answers.ContainsKey(k) ? answers[k] : null, k => k == "accept-contract" ? accept : Flags.Contains(k), m => { lock (gate) lines.Add(m); });
                                    lock (gate) result = r;
                                }
                                catch (Exception e) { lock (gate) error = e.Message; }
                                finally { lock (gate) { done = true; running = false; } }
                            }) { IsBackground = true }.Start();
                            Ok(ctx, new Dictionary<string, object> { { "ok", true } });
                            return;
                        }
                    case "/api/progress":
                        lock (gate)
                            Ok(ctx, new Dictionary<string, object> { { "lines", lines.Where(l => !l.Contains("assword")).Cast<object>().ToList() }, { "done", done }, { "error", error ?? "" },
                                { "installDir", result == null ? "" : result.InstallDir } });
                        return;
                    case "/api/finish":
                        {
                            // "Open": the customer's screen, served by the service that just started (or by this program)
                            if (result != null && Json.Str(q, "open") == "1")
                                try
                                {
                                    var exe = Path.Combine(result.InstallDir, Environment.OSVersion.Platform == PlatformID.Win32NT ? "OnlineBackup.Agent.exe" : "OnlineBackup.Agent");
                                    var psi = new System.Diagnostics.ProcessStartInfo(exe, "open --home \"" + result.DataDir + "\"") { UseShellExecute = false, CreateNoWindow = true };
                                    System.Diagnostics.Process.Start(psi);
                                }
                                catch (Exception) { }
                            Ok(ctx, new Dictionary<string, object> { { "ok", true } });
                            Closed.Set();
                            return;
                        }
                }
                Text(ctx, 404, "application/json", "{\"message\":\"not found\"}");
            }
            catch (AgentException e) { Text(ctx, 400, "application/json; charset=utf-8", Json.Write(new Dictionary<string, object> { { "message", e.Message } })); }
            catch (Exception e) { Text(ctx, 400, "application/json; charset=utf-8", Json.Write(new Dictionary<string, object> { { "message", "No connection to the server: " + e.Message } })); }
        }

        /// <summary>
        /// SETUP-C20/C30: the address as typed → a full address that answers, with the package's certificate, a public one,
        /// or one the person confirms by its fingerprint; the package's address that does not answer from inside the office
        /// is replaced by the same server's address inside (same certificate only). Shared by the Windows program and the web wizard.
        /// </summary>
        public static Dictionary<string, object> CheckServer(XElement conn, string typed, string lang)
        {
            // the address as typed → a full address; it must answer, and its certificate must be the package's,
            // a public one, or one the person confirms by its fingerprint
            var server = Normalize(typed);
            if (server == null) throw new AgentException(400, "SERVER", "Write the server address, e.g. https://backup.company.com:8443");
            var pkg = (string)conn.Attribute("SERVER");
            // SETUP-C30: the package's address does not answer from here (inside the office the router often does
            // not loop back to its own public address): the same server is tried by its addresses inside the
            // office and on this computer — only an answer with the package's certificate is taken
            string viaInside = null;
            if (Setup.SameServer(server, pkg) && server.StartsWith("https:", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty((string)conn.Attribute("PIN")))
            {
                bool t0;
                try { Setup.Fingerprint(new Uri(server), out t0); }
                catch (Exception)
                {
                    var port = new Uri(server).Port; var want = ((string)conn.Attribute("PIN")).Replace(":", "").ToLowerInvariant();
                    foreach (var near in ((string)conn.Attribute("INTERNAL") ?? "").Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).Concat(new[] { "localhost" }))
                        try { bool t1; if (Setup.Fingerprint(new Uri("https://" + near + ":" + port), out t1, 3000) == want) { viaInside = "https://" + near + ":" + port; break; } }
                        catch (Exception) { }
                    if (viaInside != null) server = viaInside;
                }
            }
            string pin = null, fp = null; bool trusted = true;
            if (server.StartsWith("https:", StringComparison.OrdinalIgnoreCase))
            {
                fp = Setup.Fingerprint(new Uri(server), out trusted);
                var pkgPin = ((string)conn.Attribute("PIN") ?? "").Replace(":", "").ToLowerInvariant();
                if (pkgPin.Length > 0 && fp == pkgPin) { pin = fp; trusted = true; }   // the same server by another address (e.g. its address inside the office)
                else if (Setup.SameServer(server, pkg) && pkgPin.Length > 0) throw new AgentException(0, "CERT", "The server's certificate is not the one this software was made for. Ask your IT company.");
                else if (!trusted) pin = fp;
            }
            var ct = AgentApp.Contract(server, lang, pin);
            return new Dictionary<string, object> { { "server", server }, { "inside", viaInside != null }, { "pin", pin ?? "" }, { "fingerprint", fp ?? "" }, { "confirm", !trusted },
                { "contractVersion", (long)ct.Int("version") }, { "contract", ct.Int("install") == 1 ? ct["text"] ?? "" : "" }, { "signup", ct.Int("signup") == 1 },
                // SIGNUP-020: "signup" above says whether the contract is shown to a new customer — not whether the server takes
                // new customers (that is signupOpen; a server before 0.1.100 does not say, so it is open)
                { "contractNew", ct.Int("version") > 0 && (ct.Int("install") == 1 || ct.Int("signup") == 1) ? ct["text"] ?? "" : "" }, { "signupOpen", ct.Int("signupOpen", 1) == 1 } };
        }

        string DataDir()
        {
            var folder = (string)conn.Attribute("FOLDER"); if (string.IsNullOrEmpty(folder)) folder = "OnlineBackup";
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), folder);
        }

        /// <summary>"backup.company.com" → "https://backup.company.com:8443"; a full address stays as it is.</summary>
        public static string Normalize(string typed)
        {
            var t = (typed ?? "").Trim().TrimEnd('/');
            if (t.Length == 0 || t.Length > 300 || t.Any(char.IsWhiteSpace)) return null;
            if (!t.Contains("://")) t = "https://" + t;
            Uri u;
            if (!Uri.TryCreate(t, UriKind.Absolute, out u) || (u.Scheme != "https" && u.Scheme != "http") || string.IsNullOrEmpty(u.Host)) return null;
            var port = u.IsDefaultPort && !typed.Contains(":" + u.Port) ? (u.Scheme == "https" ? 8443 : 80) : u.Port;
            return u.Scheme + "://" + u.Host + ((u.Scheme == "https" && port == 443) || (u.Scheme == "http" && port == 80) ? "" : ":" + port);
        }

        static void Ok(HttpListenerContext ctx, Dictionary<string, object> o) { Text(ctx, 200, "application/json; charset=utf-8", Json.Write(o)); }
        static void Text(HttpListenerContext ctx, int status, string type, string text) { Bin(ctx, status, type, Encoding.UTF8.GetBytes(text)); }
        static void Bin(HttpListenerContext ctx, int status, string type, byte[] b)
        {
            var r = ctx.Response; r.StatusCode = status; r.ContentType = type; r.ContentLength64 = b.Length;
            r.Headers["Cache-Control"] = "no-store"; r.Headers["X-Content-Type-Options"] = "nosniff"; r.Headers["X-Frame-Options"] = "DENY";
            r.OutputStream.Write(b, 0, b.Length); r.Close();
        }
    }
}
