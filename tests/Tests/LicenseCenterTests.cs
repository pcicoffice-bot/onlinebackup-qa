using System;
using System.Net;
using System.Net.Sockets;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>LIC-100..140: the licensing centre and the server's daily check-in (as Ahsay's licence server).</summary>
    public class LicenseCenterTests
    {
        [Fact]
        public void UpdateLicence_BringsTheNewQuotaTheOwnerIssued_OnlyGenuineAndOnlyForThisServer()
        {
            // LIC-150: a partner buys more → the owner issues a new licence in the portal → "Update licence" on the server
            var t = new TcpListener(IPAddress.Loopback, 0); t.Start(); int port = ((IPEndPoint)t.LocalEndpoint).Port; t.Stop();
            var centerUrl = "http://localhost:" + port;
            using (var env = new Env(licensed: false))
            {
                var center = new LicenseCenter(Env.TestKey[0], env.Dir("center"), centerUrl + "/");
                try
                {
                    var st = center.Portal.Load(); st.SignupOpen = true; st.CenterUrl = centerUrl; center.Portal.Save(st);
                    var acc = center.Portal.Register(new System.Collections.Generic.Dictionary<string, object> { { "company", "Acme IT" }, { "contact", "Dana" }, { "email", "dana@acme.example" }, { "password", "Partner-Pass-1" }, { "terms", "1" } });
                    var admin = env.Admin();
                    // no portal and no centre known: say so (paste the licence)
                    Assert.Equal("NO_SOURCE", admin.Call("GET", "/api/admin/license?update=1")["updated"]);
                    // installed from the partner's package: the portal's address and the package token are kept
                    var le = env.Cfg.Doc.Root.Element("LICENSE");
                    le.SetAttributeValue("PORTAL_URL", centerUrl); le.SetAttributeValue("PORTAL_ACCOUNT", Json.Str(acc, "id")); le.SetAttributeValue("PORTAL_TOKEN", Json.Str(acc, "token")); env.Cfg.Save();
                    Assert.Equal("CURRENT", admin.Call("GET", "/api/admin/license?update=1")["updated"]);   // nothing issued yet
                    // the owner issues 40 customers / 120 computers / 2 TB for this server
                    center.Portal.Issue(center.Portal.Account(Json.Str(acc, "id")), new System.Collections.Generic.Dictionary<string, object> { { "serverId", env.Cfg.ServerId }, { "users", 40d }, { "computers", 120d }, { "storageGB", 2000d }, { "days", 365d }, { "edition", "PRO" } });
                    var l = admin.Call("GET", "/api/admin/license?update=1");
                    Assert.Equal("UPDATED", l["updated"]); Assert.Equal("PRO", l["edition"]); Assert.Equal("40", l["maxUsers"]); Assert.Equal("120", l["maxDevices"]);
                    Assert.Equal("CURRENT", admin.Call("GET", "/api/admin/license?update=1")["updated"]);
                    // more bought later: found again, now with the current licence as the proof (no token needed)
                    le.SetAttributeValue("PORTAL_TOKEN", "wrong"); le.SetAttributeValue("PORTAL_URL", null); env.Cfg.Save();
                    center.Portal.Issue(center.Portal.Account(Json.Str(acc, "id")), new System.Collections.Generic.Dictionary<string, object> { { "serverId", env.Cfg.ServerId }, { "users", 80d }, { "computers", 300d }, { "storageGB", 5000d }, { "days", 365d }, { "edition", "PRO" } });
                    Assert.Equal("80", admin.Call("GET", "/api/admin/license?update=1")["maxUsers"]);
                    // another server, a wrong token and no licence: refused
                    var rq = (HttpWebRequest)WebRequest.Create(centerUrl + "/portal/api/latestlicense"); rq.Method = "POST"; rq.ContentType = "application/json";
                    var bb = System.Text.Encoding.UTF8.GetBytes(Json.Write(new System.Collections.Generic.Dictionary<string, object> { { "serverId", env.Cfg.ServerId }, { "account", Json.Str(acc, "id") }, { "token", "wrong" } }));
                    using (var rs = rq.GetRequestStream()) rs.Write(bb, 0, bb.Length);
                    var ex = Assert.Throws<WebException>(() => rq.GetResponse());
                    Assert.Equal(403, (int)((HttpWebResponse)ex.Response).StatusCode);
                }
                finally { center.Dispose(); }
            }
        }

        [Fact]
        public void CheckIn_UnreachableMeans7DayTemporary_AddressChangeNeedsApproval_RevokedAndForgedAnswersRefused()
        {
            var t = new TcpListener(IPAddress.Loopback, 0); t.Start(); int port = ((IPEndPoint)t.LocalEndpoint).Port; t.Stop();
            var centerUrl = "http://localhost:" + port; var prefix = centerUrl + "/";
            using (var smtp = new FakeSmtp())
            using (var env = new Env(licensed: false))
            {
                var data = env.Dir("center");
                Environment.SetEnvironmentVariable("OB_LOCAL_IPS", "10.0.0.5");
                try
                {
                    var admin = env.Admin();
                    admin.Call("POST", "/api/admin/settings", new Msg().Set("smtpSet", "1").Set("senderEmail", "backup@example.invalid").Set("contactsSet", "1")
                        .Add("smtp", new Msg().Set("host", "127.0.0.1").Set("port", smtp.Port).Set("security", "NONE")).Add("contacts", new Msg().Set("name", "Ops").Set("email", "ops@example.invalid")));
                    env.SetLicense("PRO", 0, 0, centerUrl);
                    Func<Msg> check = () => admin.Call("GET", "/api/admin/license?check=1");

                    var center = new LicenseCenter(Env.TestKey[0], data, prefix);
                    var l = check();
                    Assert.Equal("PRO", l["edition"]); Assert.Equal("OK", l["online"]); Assert.Null(l["temporaryUntil"]);

                    // the centre cannot be reached: still PRO for 7 days ("temporary"), and the administrators get a mail
                    center.Dispose();
                    l = check();
                    Assert.Equal("PRO", l["edition"]); Assert.Equal("UNREACHABLE", l["online"]); Assert.NotNull(l["temporaryUntil"]);
                    Assert.True(smtp.Has("7 days"), "temporary licence mail");
                    env.Cfg.Clock = () => DateTime.UtcNow.AddDays(8); env.Cfg.ResetLicense();
                    Assert.Equal("FREE", admin.Call("GET", "/api/admin/license")["edition"]);       // after 7 days: the free edition
                    env.Cfg.Clock = () => DateTime.UtcNow;

                    // back online: full licence again
                    center = new LicenseCenter(Env.TestKey[0], data, prefix);
                    l = check();
                    Assert.Equal("PRO", l["edition"]); Assert.Equal("OK", l["online"]); Assert.Null(l["temporaryUntil"]);

                    // the server moved (internal address changed): temporary until the licensing centre approves
                    Environment.SetEnvironmentVariable("OB_LOCAL_IPS", "10.0.0.9");
                    l = check();
                    Assert.Equal("IP_CHANGED", l["online"]); Assert.NotNull(l["temporaryUntil"]); Assert.Contains("must approve", l["reason"]);
                    center.Approve("L-TEST");
                    l = check();
                    Assert.Equal("OK", l["online"]); Assert.Null(l["temporaryUntil"]);
                    Environment.SetEnvironmentVariable("OB_LOCAL_IPS", "10.0.0.9");
                    Assert.Equal("OK", check()["online"]);                                          // the new address is now the bound one

                    // revoked: the free edition at once
                    center.Revoke("L-TEST");
                    l = check();
                    Assert.Equal("FREE", l["edition"]);
                    center.Dispose();

                    // a "centre" without the owner's key cannot say OK
                    using (new LicenseCenter(License.KeyGen()[0], env.Dir("fake-center"), prefix))
                    {
                        env.SetLicense("PRO", 0, 0, centerUrl);
                        l = check();
                        Assert.Equal("UNREACHABLE", l["online"]); Assert.NotNull(l["temporaryUntil"]);
                        Assert.Contains("not signed", l["reason"]);
                    }
                }
                finally { Environment.SetEnvironmentVariable("OB_LOCAL_IPS", null); }
            }
        }
    }
}
