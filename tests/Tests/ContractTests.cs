using System;
using System.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>CONTRACT-010 and SIGNUP-010: the contract at sign-up / installation, new customers from the client software.</summary>
    public class ContractTests
    {
        static int Status(Action a) { try { a(); return 200; } catch (AgentException e) { return e.Status; } }

        [Fact]
        public void Signup_FromTheClient_WithTheContract()
        {
            using (var env = new Env())
            {
                var admin = env.Admin();
                Assert.Equal("0", AgentApp.Contract(env.Url, "he")["version"]);
                var c = admin.Call("POST", "/api/admin/contract", new Msg().Add("texts", new Msg().Set("lang", "he").Set("text", "הסכם שירות גיבוי — גרסה ראשונה")).Add("texts", new Msg().Set("lang", "en").Set("text", "Backup service agreement")));
                Assert.Equal("1", c["version"]);
                Assert.Equal("הסכם שירות גיבוי — גרסה ראשונה", AgentApp.Contract(env.Url, "he")["text"]);
                Assert.Equal("Backup service agreement", AgentApp.Contract(env.Url, "de")["text"]);   // another language: English

                var app = new AgentApp(env.Dir("new-pc"));
                Assert.Equal(412, Status(() => app.Signup(env.Url, "Dental Clinic", "office@clinic.example", "03-5550000", "clinic", "Clinic-Pass-1", 0)));   // not accepted
                Assert.Equal(400, Status(() => app.Signup(env.Url, "Dental Clinic", "no-mail", null, "clinic", "Clinic-Pass-1", 1)));
                Assert.Equal(400, Status(() => app.Signup(env.Url, "Dental Clinic", "office@clinic.example", null, "clinic", "short", 1)));   // 8+ with a letter
                Assert.Equal("clinic", app.Signup(env.Url, "Dental Clinic", "office@clinic.example", "03-5550000", "clinic", "Clinic-Pass-1", 1, "RECEPTION-PC"));
                var p = env.Api.UserStore.LoadProfile("clinic");
                Assert.Equal("1", p.Get("CONTRACT_VERSION")); Assert.Equal("CLIENT", p.Get("SIGNUP")); Assert.Equal("Dental Clinic", p.Get("ALIAS"));
                Assert.Equal("RECEPTION-PC", app.Home.Computer);
                Assert.Equal(409, Status(() => new AgentApp(env.Dir("x")).Signup(env.Url, "Other", "a@b.example", null, "clinic", "Clinic-Pass-1", 1)));   // the name is taken

                // closed by the IT company: customers come only from the provider
                admin.Call("POST", "/api/admin/contract", new Msg().Set("signupOpen", 0));
                Assert.Equal(403, Status(() => new AgentApp(env.Dir("y")).Signup(env.Url, "Shop", "a@shop.example", null, "shop", "Shop-Pass-1", 1)));
            }
        }

        [Fact]
        public void NewVersion_AcceptedAtTheNextSignIn_AndPerAddressLimit()
        {
            using (var env = new Env())
            {
                env.CreateUser("acme", "Customer-Pass-1");
                var app = env.Agent("acme", "Customer-Pass-1");                                     // no contract yet: no question
                var admin = env.Admin();
                admin.Call("POST", "/api/admin/contract", new Msg().Add("texts", new Msg().Set("lang", "en").Set("text", "v1")));
                Assert.Equal(412, Status(() => app.Interactive("Customer-Pass-1", null)));            // must accept first
                var c = new Client(env.Url);
                c.Call("POST", "/api/login", new Msg().Set("login", "acme").Set("password", "Customer-Pass-1").Set("contractVersion", 1));
                app.Interactive("Customer-Pass-1", null);                                             // accepted: signs in
                app.Sets();                                                                           // scheduled work is never stopped by the contract

                admin.Call("POST", "/api/admin/contract", new Msg().Set("reaccept", 0).Add("texts", new Msg().Set("lang", "en").Set("text", "v2")));
                app.Interactive("Customer-Pass-1", null);                                             // "accept again" off: an accepted customer goes on
                admin.Call("POST", "/api/admin/contract", new Msg().Set("reaccept", 1).Add("texts", new Msg().Set("lang", "en").Set("text", "v3")));
                Assert.Equal(412, Status(() => app.Interactive("Customer-Pass-1", null)));

                // at most 5 sign-ups an hour from one address
                for (int i = 0; i < 5; i++) Status(() => new AgentApp(env.Dir("s" + i)).Signup(env.Url, "Co " + i, "a" + i + "@x.example", null, "co" + i, "Company-Pass-1", 3));
                Assert.Equal(429, Status(() => new AgentApp(env.Dir("s9")).Signup(env.Url, "Co 9", "a9@x.example", null, "co9", "Company-Pass-1", 3)));
            }
        }

        [Fact]
        public void ClientInstaller_ShowsTheContract_AndOpensANewCustomer()
        {
            using (var env = new Env())
            {
                env.Admin().Call("POST", "/api/admin/contract", new Msg().Add("texts", new Msg().Set("lang", "en").Set("text", "Backup service agreement v1")));
                var pkg = env.Dir("pkg");
                System.IO.File.WriteAllText(System.IO.Path.Combine(pkg, "connection.xml"), new System.Xml.Linq.XElement("CONNECTION", new System.Xml.Linq.XAttribute("SERVER", env.Url), new System.Xml.Linq.XAttribute("FOLDER", "ITCare")).ToString());
                var said = new System.Collections.Generic.List<string>();
                Func<string, string> opt = k => k == "company" ? "Dental Clinic" : k == "email" ? "office@clinic.example" : k == "login" ? "clinic" : k == "password" ? "Clinic-Pass-1"
                    : k == "install-dir" ? env.Dir("inst") : k == "data-dir" ? env.Dir("data") : k == "lang" ? "en" : null;
                var e = Assert.Throws<AgentException>(() => Setup.Run(pkg, opt, k => k == "no-service", said.Add));   // not accepted: stops
                Assert.Equal("CONTRACT", e.Code);
                Assert.Contains("Backup service agreement v1", said);
                Assert.DoesNotContain("clinic", env.Api.UserStore.Logins());
                Setup.Run(pkg, opt, k => k == "no-service" || k == "accept-contract", said.Add);
                Assert.Contains("clinic", env.Api.UserStore.Logins());
                Assert.Equal("1", env.Api.UserStore.LoadProfile("clinic").Get("CONTRACT_VERSION"));
            }
        }
    }
}
