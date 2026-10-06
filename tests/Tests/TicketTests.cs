using System;
using System.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>TICKETS-010: service calls — the ITSguard rules (TCK-*) in the backup server, and the calls it opens by itself.</summary>
    public class TicketTests
    {
        static Msg Call(string login, string subject, string priority = "Normal", string status = "New", string assignee = null)
        {
            return new Msg().Set("login", login).Set("subject", subject).Set("priority", priority).Set("status", status).Set("assignee", assignee);
        }

        [Fact]
        public void Save_DefaultHandler_Sla_CloseNeedsHandler_Revision()
        {
            using (var env = new Env())
            {
                var t = env.Api.Calls; var now = new DateTime(2026, 10, 4, 8, 0, 0, DateTimeKind.Utc); t.Clock = () => now;
                env.Api.Calls.SaveSettings(new Msg().Set("defaultAssignee", "dana").Set("hoursUrgent", 2));
                var told = new System.Collections.Generic.List<string>(); t.Assigned = (x, by) => told.Add(x["assignee"] + "<" + by);
                var c = t.Save(Call("acme", "Backup failed", "Urgent"), "avi", "10.0.0.5");
                Assert.Equal("1", c["id"]);
                Assert.Equal("dana", c["assignee"]);                                   // TCK-390: no handler → the default one, with a note
                Assert.Contains(c.List("notes"), n => n["kind"] == "assign" && n["text"] == "default");
                Assert.Equal(now.AddHours(2), DateTime.Parse(c["due"]).ToUniversalTime());
                Assert.Equal("ok", t.Sla(c, now.AddMinutes(30))["state"]);
                Assert.Equal("warn", t.Sla(c, now.AddMinutes(70))["state"]);          // half the time used
                Assert.Equal("late", t.Sla(c, now.AddHours(3))["state"]);

                // TCK-350: no closing without a handler
                var e = Assert.Throws<ApiException>(() => t.Save(Call("acme", "x", "Normal", "Resolved", "").Set("id", c["id"]).Set("revision", c["rev"]), "avi", null));
                Assert.Equal("ASSIGNEE", e.Code);
                // the revision stops two people overwriting each other
                var c2 = t.Save(Call("acme", "Backup failed", "Urgent", "InProgress", "dana").Set("id", c["id"]).Set("revision", c["rev"]), "avi", null);
                Assert.Throws<ApiException>(() => t.Save(Call("acme", "Backup failed", "Urgent", "Waiting", "dana").Set("id", c["id"]).Set("revision", c["rev"]), "yossi", null));
                Assert.Equal(new[] { "dana<avi" }, told);                                   // the handler is told once (TCN mail on assignment)
                Assert.Contains(c2.List("notes"), n => n["kind"] == "status" && n["from"] == "New" && n["to"] == "InProgress" && n["ip"] == null);
                var c3 = t.Save(Call("acme", "Backup failed", "Urgent", "Resolved", "dana").Set("id", c["id"]).Set("revision", c2["rev"]), "dana", null);
                Assert.Equal("done", t.Sla(c3)["state"]);
                Assert.Equal("1", t.Sla(c3)["met"]);
            }
        }

        [Fact]
        public void Deferred_ComesBack_Delete_Restore()
        {
            using (var env = new Env())
            {
                var t = env.Api.Calls; var now = new DateTime(2026, 10, 4, 8, 0, 0, DateTimeKind.Utc); t.Clock = () => now;
                var c = t.Save(Call("acme", "Install on the new PC", "Low", "New", "yossi"), "avi", null);
                Assert.Throws<ApiException>(() => t.Save(Call("acme", "Install on the new PC", "Low", "Deferred", "yossi").Set("id", c["id"]).Set("revision", c["rev"]).Set("followUp", "2026-10-01"), "avi", null));
                c = t.Save(Call("acme", "Install on the new PC", "Low", "Deferred", "yossi").Set("id", c["id"]).Set("revision", c["rev"]).Set("followUp", "2026-10-07"), "avi", null);
                Assert.Equal("2026-10-07T08:00:00.0000000Z", c["followUp"]);           // a day alone = 08:00
                Assert.Empty(t.List("open")); Assert.Single(t.List("future"));
                now = new DateTime(2026, 10, 7, 8, 1, 0, DateTimeKind.Utc);
                var back = t.List("open");                                               // TCK-285: back in progress with a new SLA
                Assert.Single(back); Assert.Equal("InProgress", back[0]["status"]);
                Assert.Equal(now.AddHours(72), DateTime.Parse(back[0]["due"]).ToUniversalTime());

                t.Delete(c["id"], -1, "avi");
                Assert.Empty(t.List("all")); Assert.Single(t.Deleted());
                var r = t.Restore(0, "avi");
                Assert.Equal(c["id"], r["id"]); Assert.Empty(t.Deleted());
            }
        }

        [Fact]
        public void BackupFailures_OpenOneCall_AtThreshold_CloseItselfOnSuccess()
        {
            using (var env = new Env())
            {
                env.CreateUser("acme", "Customer-Pass-1");
                var t = env.Api.Calls; var p = env.Api.UserStore.LoadProfile("acme");
                int fails = 0, warns = 0;
                Assert.Null(t.BackupResult("acme", "S1", "Files", "FS01", "BS_STOP_BY_SYSTEM_ERROR", ref fails, ref warns, "disk D: not found", p));   // 1 of 2
                var c = t.BackupResult("acme", "S1", "Files", "FS01", "BS_STOP_SUCCESS_WITH_ERROR", ref fails, ref warns, "disk D: not found", p);   // with errors = a failure
                Assert.NotNull(c);
                Assert.Equal("Urgent", c["priority"]); Assert.Equal("auto", c["source"]); Assert.Equal("FS01", c["computer"]);
                t.BackupResult("acme", "S1", "Files", "FS01", "BS_STOP_BY_SYSTEM_ERROR", ref fails, ref warns, "again", p);
                Assert.Single(t.List("open"));                                            // never a second call for the same set
                Assert.Equal(2, t.Get(c["id"]).List("notes").Count(n => n["kind"] == "note" || n["kind"] == "open"));
                t.BackupResult("acme", "S1", "Files", "FS01", "BS_STOP_SUCCESS", ref fails, ref warns, null, p);
                Assert.Empty(t.List("all"));                                              // nobody worked on it → removed (kept in the bin)
                Assert.Equal(0, fails);

                // worked on by a technician → marked resolved, not removed
                fails = 1;
                var c2 = t.BackupResult("acme", "S1", "Files", "FS01", "BS_STOP_BY_SYSTEM_ERROR", ref fails, ref warns, "x", p);
                t.AddNote(c2["id"], "Asked the receptionist to plug the drive back", "dana", "10.0.0.7");
                t.BackupResult("acme", "S1", "Files", "FS01", "BS_STOP_SUCCESS", ref fails, ref warns, null, p);
                var closed = t.Get(c2["id"]);
                Assert.Equal("Resolved", closed["status"]);
                Assert.Contains(closed.List("notes"), n => n["kind"] == "autoclose");
            }
        }

        [Fact]
        public void Thresholds_General_And_PerCustomer()
        {
            using (var env = new Env())
            {
                env.CreateUser("acme", "Customer-Pass-1");
                var t = env.Api.Calls;
                t.SaveSettings(new Msg().Set("fail", 3).Set("warn", 2).Set("urgencyfail", "High"));
                var p = env.Api.UserStore.LoadProfile("acme");
                int f = 0, w = 0;
                Assert.Null(t.BackupResult("acme", "S1", "Files", "PC", "BS_STOP_BY_SYSTEM_ERROR", ref f, ref w, null, p));
                Assert.Null(t.BackupResult("acme", "S1", "Files", "PC", "BS_STOP_BY_SYSTEM_ERROR", ref f, ref w, null, p));
                Assert.Equal("High", t.BackupResult("acme", "S1", "Files", "PC", "BS_STOP_BY_SYSTEM_ERROR", ref f, ref w, null, p)["priority"]);
                Assert.Null(t.BackupResult("acme", "S2", "Mail", "PC", "BS_STOP_SUCCESS_WITH_WARNING", ref f, ref w, null, p));
                Assert.NotNull(t.BackupResult("acme", "S2", "Mail", "PC", "BS_STOP_SUCCESS_WITH_WARNING", ref f, ref w, null, p));

                // a customer with its own rule: a call at the first failure
                p.SetAttr("TICKET_FAIL", "1"); int f2 = 0, w2 = 0;
                Assert.NotNull(t.BackupResult("acme", "S3", "SQL", "SQL01", "BS_STOP_BY_SYSTEM_ERROR", ref f2, ref w2, null, p));
                // off
                t.SaveSettings(new Msg().Set("fail", 0)); int f3 = 5, w3 = 0;
                Assert.Null(t.BackupResult("acme", "S4", "X", "PC", "BS_STOP_BY_SYSTEM_ERROR", ref f3, ref w3, null, env.Api.UserStore.LoadProfile("acme")));
                Assert.Throws<ApiException>(() => t.SaveSettings(new Msg().Set("hoursNormal", 0)));
            }
        }

        [Fact]
        public void Api_Admin_And_Client()
        {
            using (var env = new Env())
            {
                env.CreateUser("acme", "Customer-Pass-1");
                var admin = env.Admin();
                var s = admin.Call("POST", "/api/admin/ticketsettings", new Msg().Set("defaultAssignee", "admin").Set("missedhours", 24));
                Assert.Equal("24", s["missedhours"]);
                var c = admin.Call("POST", "/api/admin/tickets", Call("acme", "Restore a deleted folder", "High"));
                Assert.Equal("admin", c["assignee"]); Assert.Equal("ok", c["sla"]);
                Assert.Throws<AgentException>(() => admin.Call("POST", "/api/admin/tickets", Call("nobody", "x")));
                admin.Call("POST", "/api/admin/tickets/" + c["id"] + "/note", new Msg().Set("text", "Called the customer"));
                var full = admin.Call("GET", "/api/admin/tickets/" + c["id"]);
                Assert.Contains(full.List("notes"), n => n["text"] == "Called the customer" && n["user"] == "admin" && !string.IsNullOrEmpty(n["ip"]));
                Assert.Single(admin.Call("GET", "/api/admin/tickets?scope=mine").List("tickets"));

                // the customer opens a call from the client and sees only its own calls
                var cl = new Client(env.Url);
                cl.Session = cl.Call("POST", "/api/login", new Msg().Set("login", "acme").Set("password", "Customer-Pass-1"))["session"];
                var mine = cl.Call("POST", "/api/tickets", new Msg().Set("subject", "I cannot find yesterday's file").Set("computer", "LAPTOP-RINA"));
                Assert.Equal(2, mine.List("tickets").Count);
                var all = admin.Call("GET", "/api/admin/tickets?scope=all&login=acme").List("tickets");
                Assert.Contains(all, x => x["source"] == "client" && x["computer"] == "LAPTOP-RINA");
                admin.Call("POST", "/api/admin/ticketsettings", new Msg().Set("clientcalls", 0));
                Assert.Throws<AgentException>(() => cl.Call("POST", "/api/tickets", new Msg().Set("subject", "x")));
            }
        }
    }
}
