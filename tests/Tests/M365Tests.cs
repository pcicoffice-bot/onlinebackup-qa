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
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>A stand-in for Microsoft Graph (sign-in, users, mail folders + delta, MIME, OneDrive delta + content, throttling, restore).</summary>
    public sealed class FakeGraph : IDisposable
    {
        readonly HttpListener l = new HttpListener();
        public string Url;
        public string Secret = "app-secret-123";
        public readonly object Gate = new object();
        // folder id → (message id → MIME); deletions waiting for the next delta
        public Dictionary<string, Dictionary<string, string>> Mail = new Dictionary<string, Dictionary<string, string>>();
        public Dictionary<string, List<string>> Removed = new Dictionary<string, List<string>>();
        public Dictionary<string, string> Changed = new Dictionary<string, string>();   // message id → folder (changed since the last delta)
        public Dictionary<string, byte[]> Drive = new Dictionary<string, byte[]>();      // "Docs/report.docx" → content
        public List<string> DriveChanged = new List<string>(), DriveDeleted = new List<string>();
        public List<string> RestoredMail = new List<string>();
        public Dictionary<string, byte[]> RestoredFiles = new Dictionary<string, byte[]>();
        public Dictionary<string, object> Contacts = new Dictionary<string, object>();   // id → contact (default folder)
        public Dictionary<string, object> Events = new Dictionary<string, object>();     // id → event
        public Dictionary<string, byte[]> Site = new Dictionary<string, byte[]>();       // "Documents/Q3/plan.xlsx" → content of the "Sales Team" site
        public List<Dictionary<string, object>> ChatMsgs = new List<Dictionary<string, object>>();   // chat "ch1"
        public List<Dictionary<string, object>> ChannelMsgs = new List<Dictionary<string, object>>(), ChannelNew = new List<Dictionary<string, object>>();
        public List<string> ChatFilters = new List<string>();
        public List<string> RestoredContacts = new List<string>(), RestoredEvents = new List<string>();
        public int Throttled;
        bool throttleOnce = true; int deltaGen;

        public FakeGraph()
        {
            var t = new TcpListener(IPAddress.Loopback, 0); t.Start(); int port = ((IPEndPoint)t.LocalEndpoint).Port; t.Stop();
            Url = "http://localhost:" + port;
            l.Prefixes.Add(Url + "/"); l.Start();
            Mail["inbox"] = new Dictionary<string, string> { { "m1", Mime("Invoice 101", "first") }, { "m2", Mime("Meeting", "second") } };
            Mail["proj"] = new Dictionary<string, string> { { "m3", Mime("Project plan", "third") } };
            Mail["sent"] = new Dictionary<string, string>();
            Drive["Docs/report.docx"] = Encoding.UTF8.GetBytes("report v1");
            Drive["photo.jpg"] = new byte[5 * 1024 * 1024];
            Contacts["c1"] = D("id", "c1", "displayName", "Yossi Cohen", "givenName", "Yossi", "emailAddresses", new List<object> { D("address", "yossi@supplier.co.il") }, "changeKey", "k1");
            Contacts["c2"] = D("id", "c2", "displayName", "Rina Levi", "mobilePhone", "050-0000000");
            Events["e1"] = D("id", "e1", "subject", "Board meeting", "start", D("dateTime", "2026-10-07T09:00:00.0000000", "timeZone", "UTC"), "end", D("dateTime", "2026-10-07T10:00:00.0000000", "timeZone", "UTC"),
                "body", D("contentType", "text", "content", "agenda"), "attendees", new List<object> { D("emailAddress", D("address", "noam@contoso.com")) });
            Site["Q3/plan.xlsx"] = Encoding.UTF8.GetBytes("plan v1");
            ChatMsgs.Add(Msg("cm1", "2026-10-05T08:00:00Z", "Dana", "html", "<p>שלום <b>נועם</b></p><script>alert(1)</script>"));
            ChatMsgs.Add(Msg("cm2", "2026-10-05T08:05:00Z", "Noam", "text", "Hi Dana"));
            ChannelMsgs.Add(Msg("p1", "2026-10-05T09:00:00Z", "Dana", "text", "Q3 targets attached"));
            ((Dictionary<string, object>)ChannelMsgs[0])["attachments"] = new List<object> { D("name", "targets.xlsx") };
            var reply = Msg("p1r1", "2026-10-05T09:10:00Z", "Noam", "text", "Thanks!"); reply["replyToId"] = "p1"; ChannelMsgs.Add(reply);
            new Thread(() => { while (l.IsListening) { try { var c = l.GetContext(); ThreadPool.QueueUserWorkItem(_ => Handle(c)); } catch { return; } } }) { IsBackground = true }.Start();
        }

        public static Dictionary<string, object> Msg(string id, string when, string who, string type, string text)
        {
            return (Dictionary<string, object>)D("id", id, "createdDateTime", when, "lastModifiedDateTime", when, "messageType", "message", "from", D("user", D("displayName", who)), "body", D("contentType", type, "content", text));
        }

        public static string Mime(string subject, string body)
        {
            return "From: a@contoso.com\r\nTo: dana@contoso.com\r\nSubject: " + subject + "\r\nDate: Mon, 5 Oct 2026 10:00:00 +0000\r\nMIME-Version: 1.0\r\nContent-Type: text/plain\r\n\r\n" + body + "\r\n";
        }

        void Send(HttpListenerContext c, int st, string body, string type = "application/json")
        {
            var b = Encoding.UTF8.GetBytes(body ?? ""); c.Response.StatusCode = st; c.Response.ContentType = type; c.Response.ContentLength64 = b.Length;
            c.Response.OutputStream.Write(b, 0, b.Length); c.Response.Close();
        }
        public static object D(params object[] kv) { var d = new Dictionary<string, object>(); for (int i = 0; i < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1]; return d; }
        string Page(IEnumerable<object> items, string next = null, string delta = null)
        {
            var d = (Dictionary<string, object>)D("value", items.ToList());
            if (next != null) d["@odata.nextLink"] = Url + next; if (delta != null) d["@odata.deltaLink"] = Url + delta;
            return Json.Write(d);
        }

        void Handle(HttpListenerContext c)
        {
            try
            {
                var p = c.Request.Url.AbsolutePath.Replace("%2C", ","); var m = c.Request.HttpMethod;
                string body; using (var r = new StreamReader(c.Request.InputStream)) body = r.ReadToEnd();
                if (p.EndsWith("/oauth2/v2.0/token"))
                {
                    if (!body.Contains("client_secret=" + Uri.EscapeDataString(Secret))) { Send(c, 401, Json.Write(D("error", "invalid_client"))); return; }
                    Send(c, 200, Json.Write(D("access_token", "tok", "expires_in", 3600))); return;
                }
                if (c.Request.Headers["Authorization"] != "Bearer tok" && !p.StartsWith("/upload")) { Send(c, 401, Json.Write(D("error", D("code", "InvalidAuthenticationToken")))); return; }
                lock (Gate)
                {
                    if (p == "/v1.0/users") { Send(c, 200, Page(new object[] { D("id", "u1", "userPrincipalName", "dana@contoso.com"), D("id", "u2", "userPrincipalName", "noam@contoso.com") })); return; }
                    if (p.StartsWith("/v1.0/users/u2/")) { Send(c, 404, Json.Write(D("error", D("code", "MailboxNotEnabledForRESTAPI", "message", "no mailbox")))); return; }
                    if (p == "/v1.0/users/u1/mailFolders" && m == "GET")
                    { Send(c, 200, Page(new object[] { D("id", "inbox", "displayName", "Inbox", "childFolderCount", 1), D("id", "sent", "displayName", "Sent Items", "childFolderCount", 0) })); return; }
                    if (p == "/v1.0/users/u1/mailFolders/inbox/childFolders") { Send(c, 200, Page(new object[] { D("id", "proj", "displayName", "Projects", "childFolderCount", 0) })); return; }
                    if (p.StartsWith("/v1.0/users/u1/mailFolders/") && p.EndsWith("/messages/delta"))
                    {
                        var f = p.Split('/')[5];
                        var all = Mail[f].Keys.Select(id => (object)D("id", id, "subject", Subject(Mail[f][id]), "receivedDateTime", "2026-10-05T10:00:00Z")).ToList();
                        if (c.Request.QueryString["page"] == "2") { Send(c, 200, Page(all.Skip(1), null, "/delta/" + f + "/" + deltaGen)); return; }
                        Send(c, 200, Page(all.Take(1), p + "?page=2")); return;   // two pages
                    }
                    if (p.StartsWith("/delta/"))
                    {
                        var f = p.Split('/')[2]; var items = new List<object>();
                        foreach (var id in Removed.ContainsKey(f) ? Removed[f] : new List<string>()) items.Add(D("id", id, "@removed", D("reason", "deleted")));
                        foreach (var kv in Changed.Where(x => x.Value == f)) items.Add(D("id", kv.Key, "subject", Subject(Mail[f][kv.Key]), "receivedDateTime", "2026-10-06T08:30:00Z"));
                        Removed.Remove(f); foreach (var k in Changed.Where(x => x.Value == f).Select(x => x.Key).ToList()) Changed.Remove(k);
                        Send(c, 200, Page(items, null, "/delta/" + f + "/" + (++deltaGen))); return;
                    }
                    if (p.StartsWith("/v1.0/users/u1/messages/") && p.EndsWith("/$value"))
                    {
                        if (throttleOnce) { throttleOnce = false; Throttled++; c.Response.AddHeader("Retry-After", "1"); Send(c, 429, Json.Write(D("error", D("code", "TooManyRequests")))); return; }
                        var id = p.Split('/')[5]; var mime = Mail.Values.Where(x => x.ContainsKey(id)).Select(x => x[id]).FirstOrDefault();
                        if (mime == null) { Send(c, 404, Json.Write(D("error", D("code", "ErrorItemNotFound")))); return; }
                        Send(c, 200, mime, "message/rfc822"); return;
                    }
                    if (p == "/v1.0/users/u1/drive/root/delta" || p.StartsWith("/drivedelta/"))
                    {
                        var items = new List<object> { D("id", "root", "name", "root", "folder", D(), "root", D()) };
                        IEnumerable<string> names = p.StartsWith("/drivedelta/") ? DriveChanged : Drive.Keys;
                        foreach (var n in names)
                        {
                            var dir = n.Contains("/") ? "/" + n.Substring(0, n.LastIndexOf('/')) : "";
                            items.Add(D("id", "f-" + n.Replace("/", "_"), "name", n.Split('/').Last(), "size", Drive[n].Length, "file", D(), "lastModifiedDateTime", "2026-10-05T09:00:00Z", "cTag", "c:" + Bytes.Hex(Bytes.Sha256(Drive[n]), 8),
                                "parentReference", D("path", "/drive/root:" + dir)));
                        }
                        if (p.StartsWith("/drivedelta/")) foreach (var n in DriveDeleted) items.Add(D("id", "f-" + n.Replace("/", "_"), "deleted", D("state", "deleted")));
                        DriveChanged.Clear(); DriveDeleted.Clear();
                        Send(c, 200, Page(items, null, "/drivedelta/" + (++deltaGen))); return;
                    }
                    if (p.StartsWith("/v1.0/users/u1/drive/items/") && p.EndsWith("/content"))
                    {
                        var n = Drive.Keys.First(k => "f-" + k.Replace("/", "_") == Uri.UnescapeDataString(p.Split('/')[6])); var b = Drive[n];
                        c.Response.StatusCode = 200; c.Response.ContentLength64 = b.Length; c.Response.OutputStream.Write(b, 0, b.Length); c.Response.Close(); return;
                    }
                    if (p == "/v1.0/users/u1/contacts" && m == "GET") { Send(c, 200, Page(Contacts.Values)); return; }
                    if (p == "/v1.0/users/u1/contactFolders" && m == "GET") { Send(c, 200, Page(new object[0])); return; }
                    if (p == "/v1.0/users/u1/calendars" && m == "GET") { Send(c, 200, Page(new object[] { D("id", "cal1", "name", "Calendar") })); return; }
                    if (p == "/v1.0/users/u1/calendars/cal1/events" && m == "GET") { Send(c, 200, Page(Events.Values)); return; }
                    if (p == "/v1.0/users/u1/chats") { Send(c, 200, Page(new object[] { D("id", "ch1", "chatType", "oneOnOne", "members", new List<object> { D("displayName", "Dana"), D("displayName", "Noam") }) })); return; }
                    if (p == "/v1.0/chats/ch1/messages")
                    {
                        var f = c.Request.QueryString["$filter"]; ChatFilters.Add(f ?? "");
                        var since = f == null ? "" : f.Replace("lastModifiedDateTime gt ", "");
                        Send(c, 200, Page(ChatMsgs.Where(x => string.CompareOrdinal((string)x["lastModifiedDateTime"], since) > 0).OrderByDescending(x => (string)x["lastModifiedDateTime"]).Cast<object>())); return;
                    }
                    if (p == "/v1.0/groups") { Send(c, 200, Page(new object[] { D("id", "t1", "displayName", "Sales Team") })); return; }
                    if (p == "/v1.0/teams/t1/channels") { Send(c, 200, Page(new object[] { D("id", "c-gen", "displayName", "General") })); return; }
                    if (p == "/v1.0/teams/t1/channels/c-gen/messages/delta") { Send(c, 200, Page(ChannelMsgs.Where(x => !x.ContainsKey("replyToId")).Cast<object>(), null, "/chdelta/" + (++deltaGen))); return; }
                    if (p.StartsWith("/chdelta/")) { var n = ChannelNew.ToList(); ChannelNew.Clear(); Send(c, 200, Page(n.Where(x => !x.ContainsKey("replyToId")).Cast<object>(), null, "/chdelta/" + (++deltaGen))); return; }
                    if (p.StartsWith("/v1.0/teams/t1/channels/c-gen/messages/") && p.EndsWith("/replies")) { var pid = p.Split('/')[7]; Send(c, 200, Page(ChannelMsgs.Where(x => x.ContainsKey("replyToId") && (string)x["replyToId"] == pid).Cast<object>())); return; }
                    if (p == "/v1.0/sites") { Send(c, 200, Page(new object[] { D("id", "contoso.sharepoint.com,s1", "displayName", "Sales Team", "webUrl", "https://contoso.sharepoint.com/sites/sales") })); return; }
                    if (p == "/v1.0/sites/contoso.sharepoint.com,s1/drives") { Send(c, 200, Page(new object[] { D("id", "d1", "name", "Documents") })); return; }
                    if (p == "/v1.0/drives/d1/root/delta" || p.StartsWith("/sitedelta/"))
                    {
                        var items = new List<object>();
                        if (!p.StartsWith("/sitedelta/"))
                            foreach (var n in Site.Keys)
                                items.Add(D("id", "s-" + n.Replace("/", "_"), "name", n.Split('/').Last(), "size", Site[n].Length, "file", D(), "cTag", "c:" + Bytes.Hex(Bytes.Sha256(Site[n]), 8),
                                    "parentReference", D("path", "/drives/d1/root:/" + n.Substring(0, Math.Max(0, n.LastIndexOf('/'))))));
                        Send(c, 200, Page(items, null, "/sitedelta/" + (++deltaGen))); return;
                    }
                    if (p.StartsWith("/v1.0/drives/d1/items/") && p.EndsWith("/content"))
                    {
                        var n = Site.Keys.First(k => "s-" + k.Replace("/", "_") == Uri.UnescapeDataString(p.Split('/')[5])); var b = Site[n];
                        c.Response.StatusCode = 200; c.Response.ContentLength64 = b.Length; c.Response.OutputStream.Write(b, 0, b.Length); c.Response.Close(); return;
                    }
                    // restore into Microsoft 365
                    if (p == "/v1.0/users/u1/contactFolders" && m == "POST") { Send(c, 201, Json.Write(D("id", "rcf"))); return; }
                    if (p == "/v1.0/users/u1/contactFolders/rcf/contacts" && m == "POST") { RestoredContacts.Add(body); Send(c, 201, Json.Write(D("id", "nc"))); return; }
                    if (p == "/v1.0/users/u1/calendars" && m == "POST") { Send(c, 201, Json.Write(D("id", "rcal"))); return; }
                    if (p == "/v1.0/users/u1/calendars/rcal/events" && m == "POST") { RestoredEvents.Add(body); Send(c, 201, Json.Write(D("id", "ne"))); return; }
                    if (p.StartsWith("/v1.0/drives/d1/root:/") && p.EndsWith(":/content") && m == "PUT")
                    { RestoredFiles["site:" + Uri.UnescapeDataString(p.Substring("/v1.0/drives/d1/root:/".Length).Replace(":/content", ""))] = Encoding.UTF8.GetBytes(body); Send(c, 201, Json.Write(D("id", "x"))); return; }
                    if (p == "/v1.0/users/u1/mailFolders" && m == "POST") { Send(c, 201, Json.Write(D("id", "restored"))); return; }
                    if (p == "/v1.0/users/u1/mailFolders/restored/messages" && m == "POST") { RestoredMail.Add(Encoding.UTF8.GetString(Convert.FromBase64String(body))); Send(c, 201, Json.Write(D("id", "new"))); return; }
                    if (p.StartsWith("/v1.0/users/u1/drive/root:/") && p.EndsWith(":/content") && m == "PUT")
                    { RestoredFiles[Uri.UnescapeDataString(p.Substring("/v1.0/users/u1/drive/root:/".Length).Replace(":/content", ""))] = Encoding.UTF8.GetBytes(body); Send(c, 201, Json.Write(D("id", "x"))); return; }
                }
                Send(c, 404, Json.Write(D("error", D("code", "ResourceNotFound", "message", p))));
            }
            catch (Exception e) { try { Send(c, 500, e.Message, "text/plain"); } catch { } }
        }

        static string Subject(string mime) { var i = mime.IndexOf("Subject: ") + 9; return mime.Substring(i, mime.IndexOf('\r', i) - i); }
        public void Dispose() { try { l.Stop(); l.Close(); } catch { } }
    }

    public class M365Tests
    {
        static bool Have { get { var r = Environment.GetEnvironmentVariable("OB_RESTIC"); return !string.IsNullOrEmpty(r) && File.Exists(r); } }

        [Fact]
        public void Microsoft365_ItemLevel_OnlyChangesFetched_DeletedMailRestoredIntoTheMailbox()
        {
            if (!Have) return;
            using (var graph = new FakeGraph())
            using (var env = new Env())
            {
                Environment.SetEnvironmentVariable("OB_GRAPH", graph.Url + "/v1.0");
                Environment.SetEnvironmentVariable("OB_LOGIN", graph.Url);
                try
                {
                    env.CreateUser("m365cust", "Customer-Pass-1", 5);
                    var app = env.Agent("m365cust", "Customer-Pass-1");
                    var mirror = env.Dir("mirror");
                    var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1",
                        new BackupSetInfo { Name = "Contoso 365", Type = "M365", Engine = "RESTIC", Vss = false, M365Tenant = "contoso.onmicrosoft.com", M365ClientId = "app-id-1", Sources = { mirror } });
                    app.Home.SaveSecret(set.Id + "-m365", "wrong-secret");
                    var bad = app.Backup(set.Id);
                    Assert.Equal("BS_STOP_BY_SYSTEM_ERROR", bad.Result);
                    Assert.Contains(bad.LogLines, l => l.Contains("invalid_client"));
                    Assert.DoesNotContain(bad.LogLines, l => l.Contains("wrong-secret"));

                    app.Home.SaveSecret(set.Id + "-m365", graph.Secret);
                    var r1 = app.Backup(set.Id);
                    Assert.True(r1.Result.StartsWith("BS_STOP_SUCCESS"), string.Join("\n", r1.LogLines));
                    Assert.Equal(1, graph.Throttled);                                                  // waited Retry-After and went on
                    var mails = Directory.GetFiles(Path.Combine(mirror, "Mail", "dana@contoso.com"), "*.eml", SearchOption.AllDirectories);
                    Assert.Equal(3, mails.Length);
                    Assert.Contains(mails, f => f.Contains(Path.Combine("Inbox", "Projects")) && f.Contains("Project plan"));
                    Assert.Equal("report v1", File.ReadAllText(Path.Combine(mirror, "OneDrive", "dana@contoso.com", "Docs", "report.docx")));
                    Assert.Contains(r1.LogLines, l => l.Contains("noam@contoso.com") && l.Contains("no mailbox"));
                    Assert.Contains(r1.LogLines, l => l.Contains("sites 1") && l.Contains("contacts 2, events 1"));
                    var contactFiles = Directory.GetFiles(Path.Combine(mirror, "Contacts", "dana@contoso.com", "Contacts"), "*.json");
                    Assert.Equal(2, contactFiles.Length);
                    Assert.DoesNotContain("changeKey", File.ReadAllText(contactFiles.Single(f => f.Contains("Yossi Cohen"))));
                    Assert.Single(Directory.GetFiles(Path.Combine(mirror, "Calendar", "dana@contoso.com", "Calendar"), "2026-10-07 0900 Board meeting*.json"));
                    Assert.Equal("plan v1", File.ReadAllText(Path.Combine(mirror, "SharePoint", "Sales Team", "Documents", "Q3", "plan.xlsx")));
                    Assert.Contains(r1.LogLines, l => l.Contains("Teams chats 1 / messages 4"));            // 2 chat + 1 post + 1 reply
                    var chatHtml = File.ReadAllText(Directory.GetFiles(Path.Combine(mirror, "Teams", "Chats"), "Dana, Noam*.html").Single());
                    Assert.Contains("שלום נועם", chatHtml); Assert.Contains("Hi Dana", chatHtml);
                    Assert.DoesNotContain("<script", chatHtml); Assert.DoesNotContain("alert(1)", chatHtml);      // nothing from a message runs in the browser
                    var chanHtml = File.ReadAllText(Directory.GetFiles(Path.Combine(mirror, "Teams", "Channels", "Sales Team"), "General*.html").Single());
                    Assert.True(chanHtml.IndexOf("Q3 targets") < chanHtml.IndexOf("Thanks!")); Assert.Contains("targets.xlsx", chanHtml);

                    // the user deletes "Invoice 101", gets a new mail, edits a document
                    lock (graph.Gate)
                    {
                        graph.Mail["inbox"].Remove("m1"); graph.Removed["inbox"] = new List<string> { "m1" };
                        graph.Mail["inbox"]["m4"] = FakeGraph.Mime("Quote", "fourth"); graph.Changed["m4"] = "inbox";
                        graph.Drive["Docs/report.docx"] = Encoding.UTF8.GetBytes("report v2"); graph.DriveChanged.Add("Docs/report.docx");
                        graph.Contacts.Remove("c1");
                        graph.ChatMsgs.Add(FakeGraph.Msg("cm3", "2026-10-06T07:00:00Z", "Dana", "text", "Lunch?"));
                        graph.ChatMsgs[1]["deletedDateTime"] = "2026-10-06T07:30:00Z"; graph.ChatMsgs[1]["lastModifiedDateTime"] = "2026-10-06T07:30:00Z";
                        ((Dictionary<string, object>)graph.Events["e1"])["location"] = FakeGraph.D("displayName", "Room 2");
                    }
                    Thread.Sleep(1100);
                    var r2 = app.Backup(set.Id);
                    Assert.True(r2.Result.StartsWith("BS_STOP_SUCCESS"), string.Join("\n", r2.LogLines));
                    Assert.True(r2.LogLines.Any(l => l.Contains("mail new 1 / changed 0 / deleted 1") && l.Contains("OneDrive new 0 / changed 1")), string.Join("\n", r2.LogLines.Where(l => l.Contains("Microsoft 365"))));
                    Assert.DoesNotContain(Directory.GetFiles(mirror, "*.eml", SearchOption.AllDirectories), f => f.Contains("Invoice 101"));
                    Assert.Contains(r2.LogLines, l => l.Contains("contacts 0, events 1"));
                    Assert.Contains(r2.LogLines, l => l.Contains("Teams chats 1 / messages 2"));            // only what changed since the last run
                    Assert.Contains("lastModifiedDateTime gt 2026-10-05T08:05:00Z", graph.ChatFilters.Last());
                    var chat2 = File.ReadAllText(Directory.GetFiles(Path.Combine(mirror, "Teams", "Chats"), "Dana, Noam*.html").Single());
                    Assert.Contains("Lunch?", chat2); Assert.Contains("שלום נועם", chat2); Assert.Contains("(deleted)", chat2);   // a deleted message stays, marked                    // only the changed event was rewritten
                    Assert.DoesNotContain(Directory.GetFiles(mirror, "*.json", SearchOption.AllDirectories), f => f.Contains("Yossi Cohen"));

                    // item-level restore from the first point: the deleted mail back into the mailbox, the old document into OneDrive
                    var s = app.Sets().First(x => x.Id == set.Id);
                    var rs = app.Restic(s, "Customer-Pass-1");
                    var points = rs.SnapshotList();
                    Assert.Equal(2, points.Count);
                    var first = points.Last()["id"];
                    var files = rs.Ls(first).Where(f => f["type"] == "file").Select(f => f["path"]).ToList();
                    var invoice = files.Single(f => f.Contains("Invoice 101"));
                    var doc = files.Single(f => f.EndsWith("report.docx"));
                    var rlog = new List<string>();
                    var back = rs.RestoreToM365(first, new[] { invoice, doc }, rlog);
                    Assert.True(back.Mail == 1, string.Join("\n", rlog)); Assert.Equal(1, back.Files); Assert.Equal(0, back.Failed);
                    Assert.Contains("Subject: Invoice 101", graph.RestoredMail.Single());
                    var restoredDoc = graph.RestoredFiles.Single();   // before the SharePoint restore below
                    Assert.StartsWith("Restored ", restoredDoc.Key); Assert.EndsWith("Docs/report.docx", restoredDoc.Key);
                    Assert.Equal("report v1", Encoding.UTF8.GetString(restoredDoc.Value));

                    // the deleted contact, the event as it was (attendees not invited again) and a SharePoint file
                    var more = rs.RestoreToM365(first, new[] { files.Single(f => f.Contains("Yossi Cohen")), files.Single(f => f.Contains("Board meeting")), files.Single(f => f.EndsWith("plan.xlsx")) }, rlog);
                    Assert.True(more.Contacts == 1 && more.Events == 1 && more.Files == 1 && more.Failed == 0, string.Join("\n", rlog));
                    var contact = Json.Obj(Json.Parse(graph.RestoredContacts.Single()));
                    Assert.Equal("Yossi Cohen", Json.Str(contact, "displayName")); Assert.False(contact.ContainsKey("id")); Assert.False(contact.ContainsKey("changeKey"));
                    var ev = Json.Obj(Json.Parse(graph.RestoredEvents.Single()));
                    Assert.Equal("Board meeting", Json.Str(ev, "subject")); Assert.False(ev.ContainsKey("attendees")); Assert.False(ev.ContainsKey("location"));
                    Assert.Contains("noam@contoso.com", Json.Str(Json.Child(ev, "body"), "content"));
                    var site = graph.RestoredFiles.Single(x => x.Key.StartsWith("site:"));
                    var teamsBack = rs.RestoreToM365(first, new[] { files.Single(f => f.Contains("Teams") && f.EndsWith(".html") && f.Contains("Dana")) }, rlog);
                    Assert.Equal(1, teamsBack.Failed); Assert.Contains(rlog, l => l.Contains("cannot be written back into Teams"));
                    Assert.StartsWith("site:Restored ", site.Key); Assert.EndsWith("/Q3/plan.xlsx", site.Key); Assert.Equal("plan v1", Encoding.UTF8.GetString(site.Value));
                }
                finally { Environment.SetEnvironmentVariable("OB_GRAPH", null); Environment.SetEnvironmentVariable("OB_LOGIN", null); }
            }
        }

        [Fact]
        public void JsonReadsAndWritesGraphShapes()
        {
            var j = Json.Obj(Json.Parse("{\"value\":[{\"id\":\"a\\u05d0\",\"n\":1.5,\"ok\":true,\"x\":null}],\"@odata.nextLink\":\"https://x/y?$skip=2\"}"));
            var first = Json.Obj(Json.Arr(j["value"])[0]);
            Assert.Equal("aא", Json.Str(first, "id")); Assert.Equal(1L, Json.Num(first, "n")); Assert.Equal(true, first["ok"]); Assert.Null(first["x"]);
            Assert.Equal("https://x/y?$skip=2", Json.Str(j, "@odata.nextLink"));
            Assert.Equal("{\"a\":\"q\\\"\",\"b\":[1,true,null]}", Json.Write(new Dictionary<string, object> { { "a", "q\"" }, { "b", new List<object> { 1, true, null } } }));
        }
    }
}
