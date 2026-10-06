using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Linq;
using System.Threading;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>UPD-010: "Update" in the top bar — only a version the vendor signed, downloaded whole, handed to its own installer.</summary>
    [Collection("Updater")]
    public class UpdaterTests
    {
        static int FreePort() { var t = new TcpListener(IPAddress.Loopback, 0); t.Start(); int p = ((IPEndPoint)t.LocalEndpoint).Port; t.Stop(); return p; }

        static string Package(Env env, string version)
        {
            var pkg = env.Dir("pkg-" + version);
            Directory.CreateDirectory(Path.Combine(pkg, "server", "client"));
            File.WriteAllText(Path.Combine(pkg, "version.txt"), version);
            File.WriteAllText(Path.Combine(pkg, "server", "version.txt"), version);
            File.WriteAllText(Path.Combine(pkg, "server", OperatingSystem.IsWindows() ? "OnlineBackup.Server.exe" : "OnlineBackup.Server"), "the new server " + version);
            File.WriteAllText(Path.Combine(pkg, "server", "client", "OnlineBackup.Agent.exe"), "the new client " + version);
            return pkg;
        }

        static Dictionary<string, object> Account(LicenseCenter center, string url, string pkg)
        {
            var st = center.Portal.Load(); st.SignupOpen = true; st.CenterUrl = url; st.Package = pkg; center.Portal.Save(st);
            return center.Portal.Register(new Dictionary<string, object> { { "company", "Acme IT" }, { "contact", "Dana" }, { "email", "dana" + Guid.NewGuid().ToString("N").Substring(0, 6) + "@acme.example" }, { "password", "Partner-Pass-1" }, { "terms", "1" } });
        }

        [Fact]
        public void NewSignedVersion_IsFound_Downloaded_Checked_AndHandedToItsInstaller_AForgedOneIsRefused()
        {
            var launched = new List<string>(); var before = Updater.Launch;
            Updater.Launch = (exe, log) => { lock (launched) launched.Add(exe); };
            Updater.Reset();
            try
            {
                using (var env = new Env())
                {
                    var port = FreePort(); var url = "http://localhost:" + port;
                    var center = new LicenseCenter(Env.TestKey[0], env.Dir("center"), url + "/");
                    try
                    {
                        var acc = Account(center, url, Package(env, "9.9.9"));
                        var admin = env.Admin();
                        // not installed from a portal: no source, nothing offered
                        var st = admin.Call("GET", "/api/admin/update?check=1");
                        Assert.Equal("0", st["source"]); Assert.Equal("0", st["available"]);
                        var le = env.Cfg.Doc.Root.Element("LICENSE");
                        le.SetAttributeValue("PORTAL_URL", url); le.SetAttributeValue("PORTAL_ACCOUNT", Json.Str(acc, "id")); le.SetAttributeValue("PORTAL_TOKEN", Json.Str(acc, "token")); env.Cfg.Save();
                        st = admin.Call("GET", "/api/admin/update?check=1");
                        Assert.Equal("1", st["available"]); Assert.Equal("9.9.9", st["latest"]);
                        admin.Call("POST", "/api/admin/update");
                        for (int i = 0; i < 100 && admin.Call("GET", "/api/admin/update")["state"] == "downloading"; i++) Thread.Sleep(100);
                        st = admin.Call("GET", "/api/admin/update");
                        Assert.True(st["state"] == "installing", st["state"] + " " + st["message"]);
                        Assert.Single(launched);
                        Assert.Equal("the new server 9.9.9", File.ReadAllText(launched[0]));   // the vendor's files, whole, ready for the installer
                        Assert.Contains("update 0 → 9.9.9", string.Join("\n", Directory.GetFiles(Path.Combine(env.SystemHome, "logs", "Update")).Select(Atomic.ReadShared)));
                        // a wrong token: refused by the portal
                        le.SetAttributeValue("PORTAL_TOKEN", "wrong"); env.Cfg.Save();
                        Assert.Contains("did not answer", admin.Call("GET", "/api/admin/update?check=1")["message"]);
                    }
                    finally { center.Dispose(); }

                    // a portal that signs with another key (not the vendor's): never installed
                    Updater.Reset(); launched.Clear();
                    var port2 = FreePort(); var url2 = "http://localhost:" + port2;
                    var forged = new LicenseCenter(License.KeyGen()[0], env.Dir("forged"), url2 + "/");
                    try
                    {
                        var acc2 = Account(forged, url2, Package(env, "10.0.0"));
                        var le = env.Cfg.Doc.Root.Element("LICENSE");
                        le.SetAttributeValue("PORTAL_URL", url2); le.SetAttributeValue("PORTAL_ACCOUNT", Json.Str(acc2, "id")); le.SetAttributeValue("PORTAL_TOKEN", Json.Str(acc2, "token")); env.Cfg.Save();
                        var admin = env.Admin();
                        Assert.Equal("1", admin.Call("GET", "/api/admin/update?check=1")["available"]);
                        admin.Call("POST", "/api/admin/update");
                        for (int i = 0; i < 100 && admin.Call("GET", "/api/admin/update")["state"] == "downloading"; i++) Thread.Sleep(100);
                        var st = admin.Call("GET", "/api/admin/update");
                        Assert.Equal("failed", st["state"]); Assert.Contains("not signed", st["message"]);
                        Assert.Empty(launched);
                    }
                    finally { forged.Dispose(); }
                }
                Assert.True(Updater.Newer("1.10.0", "1.9.9")); Assert.False(Updater.Newer("0.1.64", "0.1.64")); Assert.True(Updater.Newer("2026.10.06", "2026.10.05"));
            }
            finally { Updater.Launch = before; Updater.Reset(); }
        }

        [Fact]
        public void UpdateFromFiles_PartsJoinedInOrder_OnTheServerOnly_ANonPackageIsRefused()
        {
            // UPD-030 (owner: "everything through the Update button"): the update package's parts, chosen in the browser
            var launched = new List<string>(); var before = Updater.Launch;
            Updater.Launch = (exe, log) => { lock (launched) launched.Add(exe); };
            Updater.Reset();
            try
            {
                using (var env = new Env())
                {
                    var admin = env.Admin();
                    var zip = Path.Combine(env.Dir("zip"), "u.zip");
                    System.IO.Compression.ZipFile.CreateFromDirectory(Package(env, "9.9.9"), zip);
                    var bytes = File.ReadAllBytes(zip);
                    Func<byte[], HttpWebResponse> post = b =>
                    {
                        var r = (HttpWebRequest)WebRequest.Create(env.Url + "api/admin/update/upload");
                        r.Method = "POST"; r.Headers["X-Session"] = admin.Session; r.ContentType = "application/octet-stream";
                        using (var s = r.GetRequestStream()) s.Write(b, 0, b.Length);
                        try { return (HttpWebResponse)r.GetResponse(); } catch (WebException e) { return (HttpWebResponse)e.Response; }
                    };
                    // the parts in the wrong order: refused, nothing started
                    var half = bytes.Length / 2;
                    using (var w = post(bytes.Skip(half).Concat(bytes.Take(half)).ToArray())) Assert.Equal(400, (int)w.StatusCode);
                    Assert.Empty(launched);
                    Updater.Reset();
                    // the parts joined in order: checked and handed to the new version's installer
                    using (var w = post(bytes)) Assert.Equal(200, (int)w.StatusCode);
                    Assert.Single(launched);
                    Assert.Equal("installing", admin.Call("GET", "/api/admin/update")["state"]);
                }
            }
            finally { Updater.Launch = before; Updater.Reset(); }
        }

        [Fact]
        public void PrivateStore_NewVersionFound_ReadWithTheKey_ShaChecked_KeyNeverShown()
        {
            // UPD-040: the owner's private store (GitHub contents API): latest.json + the package, read with a read-only key
            var launched = new List<string>(); var before = Updater.Launch; var api = Updater.GitHubApi;
            Updater.Launch = (exe, log) => { lock (launched) launched.Add(exe); };
            Updater.Reset();
            var port = FreePort(); var listener = new HttpListener(); listener.Prefixes.Add("http://localhost:" + port + "/"); listener.Start();
            try
            {
                using (var env = new Env())
                {
                    var zip = Path.Combine(env.Dir("z"), "p.zip");
                    System.IO.Compression.ZipFile.CreateFromDirectory(Package(env, "9.9.9"), zip);
                    var bytes = File.ReadAllBytes(zip);
                    string sha; using (var h = System.Security.Cryptography.SHA256.Create()) sha = Bytes.Hex(h.ComputeHash(bytes));
                    var good = true; var auths = new List<string>();
                    new Thread(() =>
                    {
                        while (listener.IsListening)
                        {
                            HttpListenerContext c; try { c = listener.GetContext(); } catch (Exception) { return; }
                            lock (auths) auths.Add(c.Request.Headers["Authorization"] + (c.Request.QueryString["ref"] == "updates" ? "" : " (no branch)"));
                            var path = c.Request.Url.AbsolutePath;
                            byte[] body = path.EndsWith("/latest.json") ? System.Text.Encoding.UTF8.GetBytes("{\"version\":\"9.9.9\",\"file\":\"p.zip\",\"sha256\":\"" + (good ? sha : new string('0', 64)) + "\"}")
                                : path.EndsWith("/p.zip") ? bytes : new byte[0];
                            c.Response.StatusCode = body.Length == 0 ? 404 : 200; c.Response.OutputStream.Write(body, 0, body.Length); c.Response.Close();
                        }
                    }) { IsBackground = true }.Start();
                    Updater.GitHubApi = "http://localhost:" + port;
                    var admin = env.Admin();
                    admin.Call("POST", "/api/admin/update/source", new Msg().Set("repo", "acme/crm@updates").Set("token", "github_pat_test_123").Set("auto", 1));
                    var src = admin.Call("GET", "/api/admin/update/source");
                    Assert.Equal("acme/crm@updates", src["repo"]); Assert.Equal("1", src["hasToken"]); Assert.DoesNotContain("github_pat", src.ToString());
                    Assert.DoesNotContain("github_pat_test_123", File.ReadAllText(Path.Combine(env.Cfg.SystemHome, "conf", "system.xml")));   // kept encrypted
                    Assert.Equal("1", admin.Call("GET", "/api/admin/update?check=1")["available"]);
                    admin.Call("POST", "/api/admin/update");
                    for (int i = 0; i < 100 && admin.Call("GET", "/api/admin/update")["state"] == "downloading"; i++) Thread.Sleep(100);
                    Assert.Equal("installing", admin.Call("GET", "/api/admin/update")["state"]);
                    Assert.Single(launched);
                    lock (auths) Assert.All(auths, a => Assert.Equal("Bearer github_pat_test_123", a));
                    // a damaged file (its SHA-256 differs from latest.json): not installed
                    Updater.Reset(); launched.Clear(); good = false;
                    admin.Call("GET", "/api/admin/update?check=1"); admin.Call("POST", "/api/admin/update");
                    for (int i = 0; i < 100 && admin.Call("GET", "/api/admin/update")["state"] == "downloading"; i++) Thread.Sleep(100);
                    Assert.Equal("failed", admin.Call("GET", "/api/admin/update")["state"]); Assert.Empty(launched);
                }
            }
            finally { listener.Stop(); Updater.Launch = before; Updater.GitHubApi = api; Updater.Reset(); }
        }
    }
}
