using System;
using System.Linq;
using System.Xml.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>CUST-020: several e-mails per customer; SET-040: what the customer may change in the client software.</summary>
    public class CustomerTests
    {
        static int Status(Action a) { try { a(); return 200; } catch (AgentException e) { return e.Status; } }

        [Fact]
        public void SeveralEmails_PerCustomer()
        {
            using (var env = new Env())
            {
                env.CreateUser("acme", "Customer-Pass-1");
                var admin = env.Admin();
                admin.Call("POST", "/api/admin/users/acme/contacts", new Msg().Set("notify", "FAILURE")
                    .Add("contacts", new Msg().Set("name", "IT").Set("email", "it@acme.example")).Add("contacts", new Msg().Set("name", "Office").Set("email", "office@acme.example")));
                var u = admin.Call("GET", "/api/admin/users").List("users").Single(x => x["login"] == "acme");
                Assert.Equal(new[] { "it@acme.example", "office@acme.example" }, u.List("contacts").Select(c => c["email"]));
                Assert.Equal("FAILURE", u["notify"]);
                Assert.Equal(400, Status(() => admin.Call("POST", "/api/admin/users/acme/contacts", new Msg().Add("contacts", new Msg().Set("email", "a@b.example")).Add("contacts", new Msg().Set("email", "A@b.example")))));
                Assert.Equal(400, Status(() => admin.Call("POST", "/api/admin/users/acme/contacts", new Msg().Add("contacts", new Msg().Set("email", "broken")))));
                admin.Call("POST", "/api/admin/users/acme/details", new Msg().Set("alias", "Acme Ltd").Set("phone", "03-1234567"));
                Assert.Equal("Acme Ltd", admin.Call("GET", "/api/admin/users").List("users").Single(x => x["login"] == "acme")["alias"]);
            }
        }

        [Fact]
        public void Customer_ChangesOnlyWhatItsProviderAllows()
        {
            using (var env = new Env())
            {
                env.CreateUser("acme", "Customer-Pass-1");
                var app = env.Agent("acme", "Customer-Pass-1");
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "Files", Sources = { env.Dir("src") } });
                var session = app.Interactive("Customer-Pass-1", null);
                var s = app.Sets().Single();
                s.Hour = 4; s.Sources.Add(env.Dir("more"));                                          // allowed by default
                session.Call("POST", "/api/sets/" + set.Id + "/settings", new Msg().Set("set", s.ToXml().ToString()));
                Assert.Equal(4, app.Sets().Single().Hour);
                s = app.Sets().Single(); s.Retention = new RetentionPolicy { Period = 365 };          // locked by default
                var e = Assert.Throws<AgentException>(() => session.Call("POST", "/api/sets/" + set.Id + "/settings", new Msg().Set("set", s.ToXml().ToString())));
                Assert.Equal(403, e.Status); Assert.Contains("retention", e.Message);

                // the IT company locks the schedule and opens the retention; and forbids new sets
                env.Admin().Call("POST", "/api/admin/users/acme/details", new Msg().Set("can_edit_schedule", 0).Set("can_edit_retention", 1).Set("can_add_sets", 0));
                session.Call("POST", "/api/sets/" + set.Id + "/settings", new Msg().Set("set", s.ToXml().ToString()));
                Assert.Equal(365, app.Sets().Single().Retention.Period);
                s = app.Sets().Single(); s.Hour = 5;
                Assert.Equal(403, Status(() => session.Call("POST", "/api/sets/" + set.Id + "/settings", new Msg().Set("set", s.ToXml().ToString()))));
                Assert.Equal(403, Status(() => app.CreateSet(session, "Customer-Pass-1", new BackupSetInfo { Name = "More", Sources = { env.Dir("x") } })));
                Assert.Equal("0", Msg.Parse(app.DeviceClient().Call("GET", "/api/profile").ToString()).List("rights").Single()["can_add_sets"]);
            }
        }
    }
}
