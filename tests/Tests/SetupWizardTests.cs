using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>SETUP-C10: the client software's installation wizard — what the person clicks, end to end, against a real server.</summary>
    public class SetupWizardTests
    {
        static Dictionary<string, object> Call(SetupUi ui, string path, Dictionary<string, object> body, int expect = 200)
        {
            var r = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + ui.Port + "/api/" + path);
            r.Method = body == null ? "GET" : "POST"; r.Headers["X-Key"] = ui.Key; r.ContentType = "application/json"; r.Timeout = 60000;
            if (body != null) { var b = Encoding.UTF8.GetBytes(Json.Write(body)); r.ContentLength = b.Length; using (var s = r.GetRequestStream()) s.Write(b, 0, b.Length); }
            HttpWebResponse resp;
            try { resp = (HttpWebResponse)r.GetResponse(); } catch (WebException e) { resp = (HttpWebResponse)e.Response; }
            using (resp) using (var rd = new StreamReader(resp.GetResponseStream()))
            {
                var text = rd.ReadToEnd();
                Assert.True((int)resp.StatusCode == expect, path + ": " + (int)resp.StatusCode + " " + text);
                return Json.Obj(Json.Parse(text));
            }
        }

        static Dictionary<string, object> Wait(SetupUi ui)
        {
            for (int i = 0; i < 300; i++) { var p = Call(ui, "progress", null); if (true.Equals(p["done"])) return p; Thread.Sleep(200); }
            throw new TimeoutException();
        }

        [Fact]
        public void NewCustomer_ExistingCustomer_WrongAddress_Agreement_AndTheKeyIsRequired()
        {
            using (var env = new Env())
            {
                env.Admin().Call("POST", "/api/admin/contract", new Msg().Add("texts", new Msg().Set("lang", "en").Set("text", "Backup service agreement v1")));
                var pkg = env.Dir("pkg");
                new XElement("CONNECTION", new XAttribute("SERVER", "https://203.0.113.9:8443"), new XAttribute("FOLDER", "ITCare")).Save(Path.Combine(pkg, "connection.xml"));   // an address that does not answer here
                new XElement("BRANDING", new XAttribute("PRODUCT", "ITCare Backup"), new XAttribute("LANGUAGE", "he")).Save(Path.Combine(pkg, "branding.xml"));
                using (var busy = new SetupUi(pkg))                                  // a port in use: the next one is taken
                using (var ui = new SetupUi(pkg))
                {
                    Assert.NotEqual(busy.Port, ui.Port);
                    ui.Fixed["install-dir"] = env.Dir("inst"); ui.Fixed["data-dir"] = env.Dir("data"); ui.Flags.Add("no-service"); ui.Flags.Add("no-shortcut");
                    // the page and its texts; nothing without the key
                    using (var wc = new WebClient()) Assert.Contains("setup", wc.DownloadString("http://127.0.0.1:" + ui.Port + "/").ToLowerInvariant());
                    var bad = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + ui.Port + "/api/info");
                    Assert.Equal(403, (int)((HttpWebResponse)Assert.Throws<WebException>(() => bad.GetResponse()).Response).StatusCode);
                    var info = Call(ui, "info", null);
                    Assert.Equal("ITCare Backup", info["product"]); Assert.Equal("he", info["language"]);

                    // the package's address does not answer from here: a clear message, then the person writes the right one
                    Assert.NotEmpty(Json.Str(Call(ui, "check", new Dictionary<string, object> { { "server", "https://203.0.113.9:8443" } }, 400), "message"));
                    var c = Call(ui, "check", new Dictionary<string, object> { { "server", env.Url }, { "lang", "en" } });
                    Assert.Equal(env.Url.TrimEnd('/'), c["server"]); Assert.Equal("Backup service agreement v1", c["contract"]);
                    Assert.Equal("https://backup.example.com:8443", SetupUi.Normalize("backup.example.com"));
                    Assert.Equal("https://backup.example.com", SetupUi.Normalize("https://backup.example.com:443"));
                    Assert.Null(SetupUi.Normalize("not a server"));

                    // a new customer who did not accept the agreement: refused, nothing created
                    var answers = new Dictionary<string, object> { { "server", env.Url.TrimEnd('/') }, { "mode", "new" }, { "company", "Dental Clinic" }, { "email", "office@clinic.example" }, { "login", "clinic" }, { "password", "Clinic-Pass-1" }, { "accept", "0" }, { "lang", "en" } };
                    Call(ui, "install", answers);
                    var p = Wait(ui);
                    Assert.Contains("not accepted", Json.Str(p, "error"));
                    Assert.DoesNotContain("clinic", env.Api.UserStore.Logins());
                    // accepted: the customer is opened and this computer registered
                    answers["accept"] = "1";
                    Call(ui, "install", answers);
                    p = Wait(ui);
                    Assert.Equal("", Json.Str(p, "error"));
                    Assert.Contains("clinic", env.Api.UserStore.Logins());
                    Assert.DoesNotContain(Json.Arr(p["lines"]).Select(x => x.ToString()), l => l.Contains("Clinic-Pass-1"));
                    var installed = XElement.Load(Path.Combine(ui.Fixed["install-dir"], "connection.xml"));
                    Assert.Equal(env.Url.TrimEnd('/'), (string)installed.Attribute("SERVER"));   // the address the person chose is the one kept
                    Call(ui, "finish", new Dictionary<string, object> { { "open", "0" } });
                    Assert.True(ui.Closed.WaitOne(2000));
                }
                // an existing customer, on another computer; a wrong password says so
                env.CreateUser("existing2026", "Customer-Pass-1");
                using (var ui = new SetupUi(pkg))
                {
                    ui.Fixed["install-dir"] = env.Dir("inst2"); ui.Fixed["data-dir"] = env.Dir("data2"); ui.Flags.Add("no-service"); ui.Flags.Add("no-shortcut");
                    var answers = new Dictionary<string, object> { { "server", env.Url.TrimEnd('/') }, { "mode", "existing" }, { "login", "existing2026" }, { "password", "wrong-password-1" }, { "accept", "1" } };
                    Call(ui, "install", answers);
                    Assert.NotEqual("", Json.Str(Wait(ui), "error"));
                    answers["password"] = "Customer-Pass-1";
                    Call(ui, "install", answers);
                    Assert.Equal("", Json.Str(Wait(ui), "error"));
                    Assert.False(string.IsNullOrEmpty(new AgentApp(ui.Fixed["data-dir"]).Home.DeviceToken));
                }
            }
        }

        [Fact]
        public void ThePublicAddressDoesNotAnswerFromInside_TheSameServerIsFoundInside_ByItsCertificateOnly()
        {
            using (var env = new Env())
            using (var front = new TlsFront(new Uri(env.Url).Port))
            {
                env.CreateUser("office2026", "Customer-Pass-1");
                var pkg = env.Dir("pkg");
                new XElement("CONNECTION", new XAttribute("SERVER", "https://203.0.113.9:" + front.Port), new XAttribute("PIN", front.Fingerprint),
                    new XAttribute("INTERNAL", "10.255.255.1"), new XAttribute("FOLDER", "Office")).Save(Path.Combine(pkg, "connection.xml"));
                using (var ui = new SetupUi(pkg))
                {
                    ui.Fixed["install-dir"] = env.Dir("inst"); ui.Fixed["data-dir"] = env.Dir("data"); ui.Flags.Add("no-service"); ui.Flags.Add("no-shortcut");
                    var c = Call(ui, "check", new Dictionary<string, object> { { "server", "https://203.0.113.9:" + front.Port } });
                    Assert.Equal("https://localhost:" + front.Port, c["server"]);   // an office address that does not answer is skipped; this computer answers (the test server knows only "localhost") Assert.Equal(true, c["inside"]); Assert.Equal(false, c["confirm"]);
                    Call(ui, "install", new Dictionary<string, object> { { "server", c["server"] }, { "pin", c["pin"] }, { "mode", "existing" }, { "login", "office2026" }, { "password", "Customer-Pass-1" }, { "accept", "1" } });
                    Assert.Equal("", Json.Str(Wait(ui), "error"));
                }
                // another server inside with another certificate is never taken for this one
                new XElement("CONNECTION", new XAttribute("SERVER", "https://203.0.113.9:" + front.Port), new XAttribute("PIN", new string('a', 64)),
                    new XAttribute("INTERNAL", "127.0.0.1"), new XAttribute("FOLDER", "Office")).Save(Path.Combine(pkg, "connection.xml"));
                using (var ui = new SetupUi(pkg))
                    Assert.NotEmpty(Json.Str(Call(ui, "check", new Dictionary<string, object> { { "server", "https://203.0.113.9:" + front.Port } }, 400), "message"));
            }
        }
    }
}
