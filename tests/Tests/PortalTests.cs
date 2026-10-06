using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>PORTAL-010..060: an IT company signs up, describes its product, downloads its server (filled-in wizard, licence by itself) and its clients.</summary>
    public class PortalTests
    {
        sealed class Web
        {
            readonly HttpClient http = new HttpClient();
            readonly string url; public string Session;
            public Web(string url) { this.url = url; }
            public HttpResponseMessage Send(string method, string op, object body = null)
            {
                var req = new HttpRequestMessage(new HttpMethod(method), url + op);
                if (body != null) req.Content = new StringContent(Json.Write(body), Encoding.UTF8, "application/json");
                if (Session != null) req.Headers.Add("X-Session", Session);
                return http.Send(req);
            }
            public Dictionary<string, object> Call(string method, string op, object body = null, int expect = 200)
            {
                var r = Send(method, op, body);
                var text = r.Content.ReadAsStringAsync().Result;
                Assert.True((int)r.StatusCode == expect, op + ": " + (int)r.StatusCode + " " + text);
                return Json.Obj(Json.Parse(text));
            }
            public byte[] Bytes(string method, string op, object body = null) { var r = Send(method, op, body); Assert.Equal(HttpStatusCode.OK, r.StatusCode); return r.Content.ReadAsByteArrayAsync().Result; }
        }

        static void Put(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, text); }

        [Fact]
        public void ByDefault_ThereIsNoTrial_TheServerStartsInTheFreeEdition()
        {
            // LIC-075: the owner's decision — no 30-day trial; a partner's server works in the free edition (10 computers, 500 GB)
            var dir = Path.Combine(Path.GetTempPath(), "ob-notrial-" + Guid.NewGuid().ToString("N"));
            try
            {
                var portal = new Portal(dir, License.KeyGen()[0], () => DateTime.UtcNow);
                Assert.Equal(0, portal.Load().TrialDays);
                var acc = new Dictionary<string, object> { { "id", "P-1" }, { "company", "Acme IT" }, { "servers", new List<object>() } };
                var e = Assert.Throws<Portal.PortalError>(() => portal.Trial(acc, "OB-ABCDEFGHJKLMNPQR"));
                Assert.Equal(409, e.Status);
                Assert.Equal(10, License.Free("test").MaxDevices); Assert.Equal(500.0, (double)License.Free("test").MaxStorageGB);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void PartnerSignsUp_BrandsItsProduct_DownloadsServerThatLicensesItself_AndItsClients()
        {
            var key = Env.TestKey;   // the owner's key pair (the public half is the one the test servers trust)
            var root = Path.Combine(Path.GetTempPath(), "obportal-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var t = new TcpListener(IPAddress.Loopback, 0); t.Start(); int port = ((IPEndPoint)t.LocalEndpoint).Port; t.Stop();
            var centerUrl = "http://localhost:" + port;
            // the server package as build-package.sh lays it out
            var pkg = Path.Combine(root, "package");
            Put(Path.Combine(pkg, "Setup.cmd"), "@echo off"); Put(Path.Combine(pkg, "server", "OnlineBackup.Server.exe"), "server v1");
            Put(Path.Combine(pkg, "server", "client", "OnlineBackup.Agent.exe"), "agent");
            Put(Path.Combine(pkg, "server", "client", "Setup.exe"), "MZ-setup");
            foreach (var f in new[] { "linux/OnlineBackup.Agent", "linux/restic", "mac/arm64/OnlineBackup.Agent", "mac/arm64/restic", "mac/x64/OnlineBackup.Agent", "mac/x64/restic" }) Put(Path.Combine(pkg, "server", "client", f), f);
            Environment.SetEnvironmentVariable("OB_SETUP_ROOT", Path.Combine(root, "machine"));
            try
            {
                using (var center = new LicenseCenter(key[0], Path.Combine(root, "center"), centerUrl + "/"))
                {
                    var st = center.Portal.Load(); st.Package = pkg; st.CenterUrl = centerUrl; st.OwnerHash = Portal.Hash("Owner-Pass-2026"); st.TrialDays = 30; st.TrialUsers = 10; center.Portal.Save(st);
                    var w = new Web(centerUrl + "/portal/api/");

                    // the page is served, in every language
                    using (var hc = new HttpClient())
                    {
                        Assert.Contains("portal.js", hc.GetStringAsync(centerUrl + "/portal").Result);
                        Assert.Contains("partner portal", hc.GetStringAsync(centerUrl + "/portal/portal.js").Result);
                        Assert.Contains("\"strings\"", hc.GetStringAsync(centerUrl + "/i18n/he.json").Result);
                    }
                    Assert.Equal(true, w.Call("GET", "info")["signupOpen"]);

                    // sign up
                    var reg = new Dictionary<string, object> { { "company", "ITCare IT" }, { "contact", "Dana Levi" }, { "email", "dana@itcare.example" }, { "phone", "03-5551234" }, { "password", "Partner-Pass-1" }, { "terms", "0" } };
                    Assert.Contains("terms", Json.Str(w.Call("POST", "register", reg, 400), "message"));
                    reg["terms"] = "1"; reg["password"] = "short";
                    Assert.Contains("8 characters", Json.Str(w.Call("POST", "register", reg, 400), "message"));   // SEC-030
                    reg["password"] = "Partner-Pass-1";
                    w.Session = Json.Str(w.Call("POST", "register", reg), "session");
                    Assert.Equal(409, (int)new Web(centerUrl + "/portal/api/").Send("POST", "register", reg).StatusCode);   // the same e-mail again
                    var me = w.Call("GET", "me");
                    Assert.Equal("ITCare IT", Json.Str(me, "company")); Assert.Null(Json.Str(me, "password")); Assert.Null(Json.Str(me, "token"));
                    foreach (var f in Directory.GetFiles(Path.Combine(root, "center"), "*", SearchOption.AllDirectories)) Assert.DoesNotContain("Partner-Pass-1", Atomic.ReadShared(f));
                    var anon = new Web(centerUrl + "/portal/api/");
                    Assert.Equal(401, (int)anon.Send("GET", "me").StatusCode);
                    Assert.Equal(401, (int)anon.Send("POST", "login", new Dictionary<string, object> { { "email", "dana@itcare.example" }, { "password", "wrong-pass-1" } }).StatusCode);
                    Assert.NotNull(Json.Str(anon.Call("POST", "login", new Dictionary<string, object> { { "email", "DANA@itcare.example" }, { "password", "Partner-Pass-1" } }), "session"));

                    // the product
                    var brand = new Dictionary<string, object> { { "PRODUCT", "ITCare Cloud Backup" }, { "SLOGAN", "Your data, always safe" }, { "COMPANY", "ITCare IT" }, { "PHONE", "03-5551234" }, { "EMAIL", "support@itcare.example" }, { "COLOR", "red" }, { "LANGUAGE", "he" } };
                    Assert.Contains("color", Json.Str(w.Call("POST", "brand", brand, 400), "message"));
                    brand["COLOR"] = "#7a2e8c";
                    w.Call("POST", "brand", brand);

                    // the server: the package + the branding preset + the portal link
                    var zip = w.Bytes("GET", "download/server");
                    var server = Path.Combine(root, "unzipped");
                    using (var z = new ZipArchive(new MemoryStream(zip))) z.ExtractToDirectory(server);
                    Assert.True(File.Exists(Path.Combine(server, "Setup.cmd")));
                    Assert.Equal("ITCare Cloud Backup", (string)XElement.Load(Path.Combine(server, "server", "branding-preset.xml")).Attribute("PRODUCT"));
                    var info = Installer.Info(Path.Combine(server, "server"));
                    Assert.Equal("Your data, always safe", Json.Str((Dictionary<string, object>)info["preset"], "slogan"));
                    Assert.Equal(true, info["portal"]);

                    // installing it: the wizard's answers (filled in from the preset) — and the licence arrives by itself
                    var a = new SetupAnswers
                    {
                        Language = "he", Product = "ITCare Cloud Backup", Slogan = "Your data, always safe", Company = "ITCare IT", HostName = "backup.itcare.example", Port = 8443, DataRoot = (string)info["suggestedDrive"],
                        AdminLogin = "admin", AdminPassword = "Strong-Pass-2026", MailProvider = "none"
                    };
                    Assert.Empty(Installer.Check(a, false));
                    var steps = new List<string>();
                    var r = Installer.Run(a, Path.Combine(server, "server"), steps.Add);
                    Assert.Contains(steps, s => s.StartsWith("STEP_LICENSE|"));
                    var cfg = SystemConfig.Load(r.SystemHome);
                    var lic = License.Check((string)cfg.Doc.Root.Element("LICENSE").Attribute("KEY"), cfg.ServerId, DateTime.UtcNow);
                    Assert.Equal("TRIAL", lic.Edition); Assert.Equal(10, lic.MaxUsers); Assert.Equal(centerUrl, lic.Center); Assert.Equal("ITCare IT", lic.Company);
                    Assert.Equal("he", (string)cfg.Doc.Root.Element("BRANDING").Attribute("LANGUAGE"));
                    Assert.Equal(cfg.ServerId, Json.Str(Json.Obj(Json.Arr(w.Call("GET", "me")["servers"])[0]), "serverId"));
                    // one trial per partner: the same server gets the same licence, another one is the owner's decision
                    Assert.Equal((string)cfg.Doc.Root.Element("LICENSE").Attribute("KEY"), Json.Str(w.Call("POST", "license", new Dictionary<string, object> { { "serverId", cfg.ServerId } }), "license"));
                    Assert.Contains("already", Json.Str(w.Call("POST", "license", new Dictionary<string, object> { { "serverId", "OB-SECOND-SERVER" } }, 402), "message"));
                    Assert.Equal(403, (int)anon.Send("POST", "autolicense", new Dictionary<string, object> { { "account", Json.Str(me, "id") }, { "token", "guess" }, { "serverId", "OB-X-123456" } }).StatusCode);

                    // the clients for that server, in the partner's name
                    var pin = new string('a', 64);
                    var one = OnlineBackup.Server.ClientPackage.ReadExe(w.Bytes("POST", "download/client", new Dictionary<string, object> { { "os", "windows" }, { "serverUrl", "https://backup.itcare.example:8443" }, { "pin", pin } }));
                    Assert.Contains(pin, Encoding.UTF8.GetString(one["connection.xml"])); Assert.Contains("ITCare Cloud Backup", Encoding.UTF8.GetString(one["branding.xml"]));   // SETUP-C60: one Setup.exe
                    var win = w.Bytes("POST", "download/client", new Dictionary<string, object> { { "os", "zip" }, { "serverUrl", "https://backup.itcare.example:8443" }, { "pin", pin } });
                    using (var z = new ZipArchive(new MemoryStream(win)))
                    {
                        var conn = XElement.Parse(new StreamReader(z.Entries.First(e => e.Name == "connection.xml").Open()).ReadToEnd());
                        Assert.Equal("https://backup.itcare.example:8443", (string)conn.Attribute("SERVER")); Assert.Equal(pin, (string)conn.Attribute("PIN"));
                        var br = XElement.Parse(new StreamReader(z.Entries.First(e => e.Name == "branding.xml").Open()).ReadToEnd());
                        Assert.Equal("Your data, always safe", (string)br.Attribute("SLOGAN")); Assert.Equal(License.OwnerProduct, (string)br.Attribute("POWERED"));   // trial: "Powered by"
                        Assert.Contains(z.Entries, e => e.FullName == "ITCare Cloud Backup-Setup/Setup.cmd");
                    }
                    foreach (var os in new[] { "mac", "linux" })
                    {
                        var tgz = w.Bytes("POST", "download/client", new Dictionary<string, object> { { "os", os }, { "serverUrl", "https://backup.itcare.example:8443" }, { "pin", pin } });
                        var names = new List<string>();
                        using (var gz = new GZipStream(new MemoryStream(tgz), CompressionMode.Decompress))
                        using (var tar = new System.Formats.Tar.TarReader(gz)) for (var e = tar.GetNextEntry(); e != null; e = tar.GetNextEntry()) names.Add(e.Name);
                        Assert.Contains("ITCare-Cloud-Backup-Setup/" + (os == "mac" ? "setup.command" : "setup.sh"), names);
                    }
                    Assert.Contains("https", Json.Str(w.Call("POST", "download/client", new Dictionary<string, object> { { "os", "windows" }, { "serverUrl", "not a url" } }, 400), "message"));

                    // the owner: every partner, a full licence, white label, disabling
                    var owner = new Web(centerUrl + "/portal/api/");
                    Assert.Equal(401, (int)owner.Send("POST", "login", new Dictionary<string, object> { { "email", "owner" }, { "password", "wrong" } }).StatusCode);
                    owner.Session = Json.Str(owner.Call("POST", "login", new Dictionary<string, object> { { "email", "owner" }, { "password", "Owner-Pass-2026" } }), "session");
                    Assert.Equal(404, (int)w.Send("GET", "owner/accounts").StatusCode);   // a partner never reaches the owner's pages
                    var acc = Json.Obj(Json.Arr(owner.Call("GET", "owner/accounts")["accounts"]).Single());
                    Assert.Equal("dana@itcare.example", Json.Str(acc, "email"));
                    var full = Json.Str(owner.Call("POST", "owner/account", new Dictionary<string, object> { { "id", Json.Str(acc, "id") }, { "serverId", cfg.ServerId }, { "edition", "PRO" }, { "users", 200L }, { "computers", 1000L }, { "storageGB", 10000L }, { "days", 365L } }), "license");
                    var pro = License.Check(full, cfg.ServerId, DateTime.UtcNow);
                    Assert.Equal("PRO", pro.Edition); Assert.Equal(200, pro.MaxUsers); Assert.True(pro.Expires > DateTime.UtcNow.AddDays(360));
                    owner.Call("POST", "owner/account", new Dictionary<string, object> { { "id", Json.Str(acc, "id") }, { "whiteLabel", "1" } });
                    using (var z = new ZipArchive(new MemoryStream(w.Bytes("POST", "download/client", new Dictionary<string, object> { { "os", "zip" }, { "serverUrl", "https://backup.itcare.example:8443" } }))))
                        Assert.Null((string)XElement.Parse(new StreamReader(z.Entries.First(e => e.Name == "branding.xml").Open()).ReadToEnd()).Attribute("POWERED"));   // paid + white label: no "Powered by"
                    owner.Call("POST", "owner/account", new Dictionary<string, object> { { "id", Json.Str(acc, "id") }, { "status", "disabled" } });
                    Assert.Equal(403, (int)anon.Send("POST", "login", new Dictionary<string, object> { { "email", "dana@itcare.example" }, { "password", "Partner-Pass-1" } }).StatusCode);
                    Assert.Equal(401, (int)w.Send("GET", "me").StatusCode);
                    owner.Call("POST", "owner/settings", new Dictionary<string, object> { { "signupOpen", "0" } });
                    reg["email"] = "other@it.example";
                    Assert.Contains("closed", Json.Str(anon.Call("POST", "register", reg, 403), "message"));
                }
            }
            finally { Environment.SetEnvironmentVariable("OB_SETUP_ROOT", null); try { Directory.Delete(root, true); } catch (Exception) { } }
        }
    }
}
