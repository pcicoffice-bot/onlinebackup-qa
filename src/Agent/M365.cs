using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using OnlineBackup.Core;

namespace OnlineBackup.Agent
{
    /// <summary>
    /// M365-010: Microsoft Graph with an application registration of the customer's tenant (client credentials).
    /// Permissions (application, admin consent): User.Read.All, Mail.Read, Files.Read.All, Contacts.Read, Calendars.Read,
    /// Sites.Read.All, and for Teams Chat.Read.All, ChannelMessage.Read.All, Team.ReadBasic.All, Channel.ReadBasic.All
    /// (Microsoft lists the Teams message APIs as protected: the tenant requests access once) — and for restore back into Microsoft 365: Mail.ReadWrite, Files.ReadWrite.All, Contacts.ReadWrite,
    /// Calendars.ReadWrite, Sites.ReadWrite.All. Throttling (429 / 503 / 504) waits Retry-After and retries.
    /// OB_GRAPH / OB_LOGIN replace the Microsoft addresses in tests.
    /// </summary>
    public sealed class GraphClient
    {
        readonly string tenant, clientId, secret;
        string token; DateTime tokenUntil;
        public string GraphBase = Environment.GetEnvironmentVariable("OB_GRAPH") ?? "https://graph.microsoft.com/v1.0";
        public string LoginBase = Environment.GetEnvironmentVariable("OB_LOGIN") ?? "https://login.microsoftonline.com";

        public GraphClient(string tenant, string clientId, string secret)
        {
            this.tenant = tenant; this.clientId = clientId; this.secret = secret;
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; } catch (NotSupportedException) { }
        }

        public sealed class GraphException : Exception
        {
            public int Status; public string Code;
            public GraphException(int status, string code, string message) : base(message) { Status = status; Code = code; }
        }

        string Token()
        {
            if (token != null && SystemClock.UtcNow < tokenUntil) return token;
            var body = "client_id=" + Uri.EscapeDataString(clientId) + "&client_secret=" + Uri.EscapeDataString(secret)
                + "&scope=" + Uri.EscapeDataString("https://graph.microsoft.com/.default") + "&grant_type=client_credentials";
            var r = (HttpWebRequest)WebRequest.Create(LoginBase + "/" + Uri.EscapeDataString(tenant) + "/oauth2/v2.0/token");
            r.Method = "POST"; r.ContentType = "application/x-www-form-urlencoded";
            var b = Encoding.ASCII.GetBytes(body); r.ContentLength = b.Length;
            using (var s = r.GetRequestStream()) s.Write(b, 0, b.Length);
            try
            {
                using (var resp = (HttpWebResponse)r.GetResponse()) using (var rd = new StreamReader(resp.GetResponseStream()))
                {
                    var j = Json.Obj(Json.Parse(rd.ReadToEnd()));
                    token = Json.Str(j, "access_token");
                    tokenUntil = SystemClock.UtcNow.AddSeconds(Math.Max(60, Json.Num(j, "expires_in") - 120));
                    return token;
                }
            }
            catch (WebException e)
            {
                // the secret is never in the message: only Azure's error code
                string code = "LOGIN";
                try { using (var rd = new StreamReader(e.Response.GetResponseStream())) code = Json.Str(Json.Obj(Json.Parse(rd.ReadToEnd())), "error") ?? code; } catch { }
                throw new GraphException(401, code, "Microsoft 365 sign-in failed (" + code + "): check the tenant, the application id, its secret and the admin consent");
            }
        }

        string Abs(string url) { return url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : GraphBase + url; }

        HttpWebResponse Send(string method, string url, byte[] body, string contentType, Dictionary<string, string> headers = null, bool auth = true)
        {
            for (int attempt = 0; ; attempt++)
            {
                var r = (HttpWebRequest)WebRequest.Create(Abs(url));
                r.Method = method; r.Timeout = 300000; r.ReadWriteTimeout = 300000;
                if (auth) r.Headers["Authorization"] = "Bearer " + Token();
                if (headers != null) foreach (var h in headers) r.Headers[h.Key] = h.Value;
                if (body != null) { r.ContentType = contentType; r.ContentLength = body.Length; using (var s = r.GetRequestStream()) s.Write(body, 0, body.Length); }
                else if (method != "GET") r.ContentLength = 0;
                try { return (HttpWebResponse)r.GetResponse(); }
                catch (WebException e)
                {
                    var resp = e.Response as HttpWebResponse;
                    if (resp == null) { if (attempt < 4) { Thread.Sleep(2000 * (attempt + 1)); continue; } throw; }
                    int st = (int)resp.StatusCode;
                    if ((st == 429 || st == 503 || st == 504) && attempt < 6)
                    {
                        int wait; if (!int.TryParse(resp.Headers["Retry-After"], out wait)) wait = 5 * (attempt + 1);
                        resp.Close(); Thread.Sleep(TimeSpan.FromSeconds(Math.Min(120, Math.Max(1, wait)))); continue;
                    }
                    if (st == 401 && attempt == 0) { token = null; resp.Close(); continue; }
                    string code = "HTTP" + st, msg = "Graph " + st;
                    try
                    {
                        using (var rd = new StreamReader(resp.GetResponseStream()))
                        {
                            var err = Json.Child(Json.Obj(Json.Parse(rd.ReadToEnd())), "error");
                            code = Json.Str(err, "code") ?? code; msg = Json.Str(err, "message") ?? msg;
                        }
                    }
                    catch { }
                    throw new GraphException(st, code, msg);
                }
            }
        }

        public Dictionary<string, object> Get(string url)
        {
            using (var resp = Send("GET", url, null, null)) using (var rd = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return Json.Obj(Json.Parse(rd.ReadToEnd()));
        }

        /// <summary>All pages of a collection (@odata.nextLink); the last page's @odata.deltaLink comes back in deltaLink.</summary>
        public IEnumerable<Dictionary<string, object>> All(string url, Action<string> deltaLink = null)
        {
            while (url != null)
            {
                var page = Get(url);
                foreach (var v in Json.Arr(page.ContainsKey("value") ? page["value"] : null)) yield return Json.Obj(v);
                url = Json.Str(page, "@odata.nextLink");
                if (url == null && deltaLink != null) deltaLink(Json.Str(page, "@odata.deltaLink"));
            }
        }

        public void Download(string url, string toFile)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(toFile));
            var tmp = toFile + ".part";
            using (var resp = Send("GET", url, null, null)) using (var s = resp.GetResponseStream()) using (var f = File.Create(tmp)) s.CopyTo(f, 1 << 16);
            if (File.Exists(toFile)) File.Delete(toFile);
            File.Move(tmp, toFile);
        }

        public Dictionary<string, object> Call(string method, string url, string json)
        {
            using (var resp = Send(method, url, json == null ? null : Encoding.UTF8.GetBytes(json), "application/json"))
            using (var rd = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) { var t = rd.ReadToEnd(); return t.Length == 0 ? new Dictionary<string, object>() : Json.Obj(Json.Parse(t)); }
        }

        public Dictionary<string, object> Raw(string method, string url, byte[] body, string contentType, Dictionary<string, string> headers = null, bool auth = true)
        {
            using (var resp = Send(method, url, body, contentType, headers, auth))
            using (var rd = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) { var t = rd.ReadToEnd(); return t.Length == 0 ? new Dictionary<string, object>() : Json.Obj(Json.Parse(t)); }
        }
    }

    /// <summary>
    /// M365-020: brings the tenant into a local mirror, item by item, with Graph delta queries (each run fetches only what
    /// changed): Mail\&lt;user&gt;\&lt;folder path&gt;\&lt;date subject [id]&gt;.eml (full MIME), OneDrive\&lt;user&gt;\&lt;path&gt;,
    /// Contacts\&lt;user&gt;\&lt;folder&gt;\&lt;name [id]&gt;.json, Calendar\&lt;user&gt;\&lt;calendar&gt;\&lt;start subject [id]&gt;.json and
    /// SharePoint\&lt;site&gt;\&lt;library&gt;\&lt;path&gt; (team sites, so also the files of Microsoft Teams channels),
    /// Teams\Chats\&lt;chat&gt;.json + .html and Teams\Channels\&lt;team&gt;\&lt;channel&gt;.json + .html (messages with their replies;
    /// only messages changed since the last run are fetched; Teams has no way to write messages back, so they restore as files).
    /// Contacts and events are small: they are read in full each run and a file is rewritten only when its content changed.
    /// The set's list (M365Users): addresses with '@' are users, other entries are sites (name or address); empty = everything.
    /// restic then backs the mirror up: every run is a restore point, an item restores by its path and date.
    /// State (delta links, item → file) in &lt;mirror&gt;\.state\&lt;user id&gt;.json, written after each folder.
    /// A failed item is reported and fetched again next run; nothing is deleted from the mirror unless Graph says so.
    /// </summary>
    public sealed class M365Sync
    {
        readonly GraphClient g; readonly string mirror; readonly Action<string> info, warn;
        public int Users, Sites, Chats, ChatMessages, MailNew, MailChanged, MailDeleted, FilesNew, FilesChanged, FilesDeleted, Contacts, Events, Errors;
        public long Downloaded;

        public M365Sync(GraphClient g, string mirror, Action<string> info, Action<string> warn) { this.g = g; this.mirror = mirror; this.info = info; this.warn = warn; }

        public static string Safe(string s, int max = 80)
        {
            var bad = new HashSet<char>(Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }));
            var t = new string((s ?? "").Select(c => bad.Contains(c) || c < 32 ? '_' : c).ToArray()).Trim().TrimEnd('.');
            if (t.Length > max) t = t.Substring(0, max).Trim();
            return t.Length == 0 ? "_" : t;
        }

        static string E(string id) { return Uri.EscapeDataString(id ?? ""); }

        static string ShortId(string id) { return Bytes.Hex(Bytes.Sha256(Encoding.UTF8.GetBytes(id ?? "")), 5); }

        sealed class UserState
        {
            public Dictionary<string, object> Delta = new Dictionary<string, object>();   // folderId / "drive" → deltaLink
            public Dictionary<string, object> Items = new Dictionary<string, object>();   // item id → mirror-relative path
            public Dictionary<string, object> Tags = new Dictionary<string, object>();    // OneDrive item id → cTag (changes with the content)
        }

        UserState Load(string userId)
        {
            var p = Path.Combine(mirror, ".state", userId + ".json");
            var s = new UserState();
            if (!File.Exists(p)) return s;
            var j = Json.Obj(Json.Parse(File.ReadAllText(p, Encoding.UTF8)));
            s.Delta = Json.Obj(j.ContainsKey("delta") ? j["delta"] : null);
            s.Items = Json.Obj(j.ContainsKey("items") ? j["items"] : null);
            s.Tags = Json.Obj(j.ContainsKey("tags") ? j["tags"] : null);
            return s;
        }

        void Save(string userId, UserState s)
        {
            Atomic.WriteText(Path.Combine(mirror, ".state", userId + ".json"), Json.Write(new Dictionary<string, object> { { "delta", s.Delta }, { "items", s.Items }, { "tags", s.Tags } }));
        }

        string Full(string rel) { return Path.Combine(mirror, rel.Replace('/', Path.DirectorySeparatorChar)); }

        void Remove(UserState st, string id)
        {
            object rel;
            if (!st.Items.TryGetValue(id, out rel)) return;
            var f = Full((string)rel);
            if (File.Exists(f)) File.Delete(f);
            st.Items.Remove(id);
        }

        /// <param name="only">user principal names and sites to back up (empty = every user with a mailbox / OneDrive and every site)</param>
        public void Run(ICollection<string> only)
        {
            Directory.CreateDirectory(mirror);
            var users = only == null ? new List<string>() : only.Where(x => x.Contains("@")).ToList();
            var sites = only == null ? new List<string>() : only.Where(x => !x.Contains("@")).ToList();
            bool all = users.Count == 0 && sites.Count == 0;
            if (all || users.Count > 0)
                foreach (var u in g.All("/users?$select=id,userPrincipalName,displayName,accountEnabled&$top=999"))
                {
                    var upn = Json.Str(u, "userPrincipalName"); var id = Json.Str(u, "id");
                    if (!all && !users.Any(x => string.Equals(x, upn, StringComparison.OrdinalIgnoreCase))) continue;
                    Users++;
                    var st = Load(id);
                    var name = Safe(upn);
                    bool mailbox = true;
                    try { Mail(id, name, st); }
                    catch (GraphClient.GraphException e) when (e.Code == "MailboxNotEnabledForRESTAPI" || e.Code == "ResourceNotFound" || e.Status == 404) { info("[" + upn + "] no mailbox"); mailbox = false; }
                    catch (GraphClient.GraphException e) { Errors++; warn("[" + upn + "] mail: " + e.Code + " " + e.Message); }
                    if (mailbox)
                    {
                        try { Contacts_(id, name, st); }
                        catch (GraphClient.GraphException e) { Errors++; warn("[" + upn + "] contacts: " + e.Code + " " + e.Message); }
                        try { Calendar(id, name, st); }
                        catch (GraphClient.GraphException e) { Errors++; warn("[" + upn + "] calendar: " + e.Code + " " + e.Message); }
                    }
                    if (mailbox)
                        try { TeamsChats(id); }
                        catch (GraphClient.GraphException e) when (e.Status == 403) { Errors++; warn("[" + upn + "] Teams chats: " + e.Code + " " + e.Message + " (Chat.Read.All / protected API access?)"); }
                        catch (GraphClient.GraphException e) { Errors++; warn("[" + upn + "] Teams chats: " + e.Code + " " + e.Message); }
                    try { Drive("/users/" + E(id) + "/drive", "drive", "OneDrive/" + name, st); }
                    catch (GraphClient.GraphException e) when (e.Status == 404 || e.Code == "ResourceNotFound") { info("[" + upn + "] no OneDrive"); }
                    catch (GraphClient.GraphException e) { Errors++; warn("[" + upn + "] OneDrive: " + e.Code + " " + e.Message); }
                    Save(id, st);
                }
            if (all || sites.Count > 0) SharePoint(all ? null : sites);
            if (all || sites.Count > 0) TeamsChannels(all ? null : sites);
            if (teamsState != null) Save("teams", teamsState);
            info("Microsoft 365: users " + Users + ", sites " + Sites + ", mail new " + MailNew + " / changed " + MailChanged + " / deleted " + MailDeleted
                + ", OneDrive new " + FilesNew + " / changed " + FilesChanged + " / deleted " + FilesDeleted + ", contacts " + Contacts + ", events " + Events + ", Teams chats " + Chats + " / messages " + ChatMessages
                + ", downloaded " + Downloaded + " bytes, errors " + Errors);
        }

        // ---------------------------------------------------------------- M365-070: Microsoft Teams messages

        UserState teamsState;
        readonly HashSet<string> chatsDone = new HashSet<string>();
        UserState TeamsState { get { return teamsState ?? (teamsState = Load("teams")); } }

        /// <summary>A user's chats (1:1, group, meeting). A chat shared by several users is fetched once per run.</summary>
        void TeamsChats(string userId)
        {
            foreach (var chat in g.All("/users/" + E(userId) + "/chats?$expand=members&$top=50"))
            {
                var cid = Json.Str(chat, "id");
                if (!chatsDone.Add(cid)) continue;
                var title = Json.Str(chat, "topic");
                if (string.IsNullOrEmpty(title))
                    title = string.Join(", ", Json.Arr(chat.ContainsKey("members") ? chat["members"] : null).Select(m => Json.Str(Json.Obj(m), "displayName")).Where(n => !string.IsNullOrEmpty(n)).OrderBy(n => n).ToArray());
                if (string.IsNullOrEmpty(title)) title = Json.Str(chat, "chatType") ?? "chat";
                var key = "chat:" + cid;
                var since = Json.Str(TeamsState.Delta, key);
                var url = "/chats/" + E(cid) + "/messages?$top=50&$orderby=lastModifiedDateTime desc" + (since == null ? "" : "&$filter=lastModifiedDateTime gt " + since);
                var changed = g.All(url).ToList();
                Chats++;
                if (changed.Count == 0 && since != null) continue;
                var newest = Merge(TeamsState, "Teams/Chats/" + Safe(title, 90) + " [" + ShortId(cid) + "]", cid, title, changed);
                if (newest != null) TeamsState.Delta[key] = newest;
            }
        }

        /// <summary>Every team's channels, by delta; each changed message with all its replies.</summary>
        void TeamsChannels(List<string> only)
        {
            List<Dictionary<string, object>> teams;
            try { teams = g.All("/groups?$filter=resourceProvisioningOptions/Any(x:x eq 'Team')&$select=id,displayName&$top=999").ToList(); }
            catch (GraphClient.GraphException e) { Errors++; warn("Teams: " + e.Code + " " + e.Message + " (Team.ReadBasic.All?)"); return; }
            foreach (var t in teams)
            {
                var tid = Json.Str(t, "id"); var tname = Json.Str(t, "displayName") ?? tid;
                if (only != null && !only.Any(x => string.Equals(x, tname, StringComparison.OrdinalIgnoreCase))) continue;
                try
                {
                    foreach (var ch in g.All("/teams/" + E(tid) + "/channels?$select=id,displayName"))
                    {
                        var chid = Json.Str(ch, "id"); var chname = Json.Str(ch, "displayName") ?? chid;
                        var key = "channel:" + chid;
                        var start = Json.Str(TeamsState.Delta, key) ?? "/teams/" + E(tid) + "/channels/" + E(chid) + "/messages/delta";
                        string next = null;
                        var changed = new List<Dictionary<string, object>>();
                        try { changed.AddRange(g.All(start, d => next = d)); }
                        catch (GraphClient.GraphException e) when (e.Status == 410) { changed.AddRange(g.All("/teams/" + E(tid) + "/channels/" + E(chid) + "/messages/delta", d => next = d)); }
                        foreach (var m in changed.ToList())
                            if (!m.ContainsKey("@removed") && Json.Str(m, "id") != null)
                                changed.AddRange(g.All("/teams/" + E(tid) + "/channels/" + E(chid) + "/messages/" + E(Json.Str(m, "id")) + "/replies?$top=50"));
                        if (changed.Count > 0) Merge(TeamsState, "Teams/Channels/" + Safe(tname, 80) + "/" + Safe(chname, 80) + " [" + ShortId(chid) + "]", chid, tname + " › " + chname, changed);
                        if (next != null) TeamsState.Delta[key] = next;
                    }
                }
                catch (GraphClient.GraphException e) { Errors++; warn("[" + tname + "] Teams channels: " + e.Code + " " + e.Message + (e.Status == 403 ? " (ChannelMessage.Read.All / protected API access?)" : "")); }
            }
        }

        /// <summary>Merges changed messages into the conversation's .json (all messages by id) and renders its .html; returns the newest change time.</summary>
        string Merge(UserState st, string relBase, string convId, string title, List<Dictionary<string, object>> changed)
        {
            var jsonId = "tj:" + convId; var htmlId = "th:" + convId;
            var messages = new Dictionary<string, object>();
            object oldRel;
            if (st.Items.TryGetValue(jsonId, out oldRel) && File.Exists(Full((string)oldRel)))
                try { messages = Json.Obj(Json.Parse(File.ReadAllText(Full((string)oldRel), Encoding.UTF8))); } catch (FormatException) { }
            string newest = null;
            foreach (var m in changed)
            {
                var id = Json.Str(m, "id"); if (id == null) continue;
                var mod = Json.Str(m, "lastModifiedDateTime") ?? Json.Str(m, "createdDateTime");
                if (mod != null && (newest == null || string.CompareOrdinal(mod, newest) > 0)) newest = mod;
                if (m.ContainsKey("@removed")) { if (messages.ContainsKey(id)) { var keep = Json.Obj(messages[id]); keep["deletedDateTime"] = mod ?? ""; } continue; }   // a deleted message stays in the backup, marked
                var d = new Dictionary<string, object>();
                foreach (var kv in m) if (!kv.Key.StartsWith("@odata", StringComparison.Ordinal)) d[kv.Key] = kv.Value;
                messages[id] = d;
                ChatMessages++;
            }
            WriteIfChanged(st, jsonId, relBase + ".json", Json.Write(messages));
            WriteIfChanged(st, htmlId, relBase + ".html", Transcript(title, messages));
            return newest;
        }

        static string Plain(string html)
        {
            var t = Regex.Replace(html ?? "", "<(script|style)[^>]*>.*?</\\1>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            t = Regex.Replace(t, "<br\\s*/?>|</p>|</div>", "\n", RegexOptions.IgnoreCase);
            return WebUtility.HtmlDecode(Regex.Replace(t, "<[^>]+>", "")).Trim();
        }

        /// <summary>A readable page of the conversation (text only: nothing from the messages runs in the browser).</summary>
        static string Transcript(string title, Dictionary<string, object> messages)
        {
            var sb = new StringBuilder("<!doctype html><meta charset=\"utf-8\"><title>" + WebUtility.HtmlEncode(title) + "</title><style>body{font-family:Segoe UI,Arial;max-width:900px;margin:20px auto;unicode-bidi:plaintext}.m{border-bottom:1px solid #ddd;padding:8px 0}.r{margin-right:30px;margin-left:30px}.h{color:#666;font-size:12px}.d{color:#b00}pre{white-space:pre-wrap;font-family:inherit;margin:4px 0;unicode-bidi:plaintext}</style><h2>" + WebUtility.HtmlEncode(title) + "</h2>");
            var all = messages.Values.Select(Json.Obj).ToList();
            Func<Dictionary<string, object>, string> when = m => Json.Str(m, "createdDateTime") ?? "";
            foreach (var m in all.Where(x => Json.Str(x, "replyToId") == null).OrderBy(when, StringComparer.Ordinal))
            {
                Row(sb, m, false);
                foreach (var r in all.Where(x => Json.Str(x, "replyToId") == Json.Str(m, "id")).OrderBy(when, StringComparer.Ordinal)) Row(sb, r, true);
            }
            return sb.ToString();
        }

        static void Row(StringBuilder sb, Dictionary<string, object> m, bool reply)
        {
            var from = Json.Child(m, "from"); var user = Json.Child(from, "user"); var app = Json.Child(from, "application");
            var who = Json.Str(user, "displayName") ?? Json.Str(app, "displayName") ?? "";
            var body = Json.Child(m, "body"); var text = Json.Str(body, "content") ?? "";
            if ((Json.Str(body, "contentType") ?? "").Equals("html", StringComparison.OrdinalIgnoreCase)) text = Plain(text);
            var files = Json.Arr(m.ContainsKey("attachments") ? m["attachments"] : null).Select(a => Json.Str(Json.Obj(a), "name")).Where(n => !string.IsNullOrEmpty(n)).ToList();
            var deleted = !string.IsNullOrEmpty(Json.Str(m, "deletedDateTime"));
            sb.Append("<div class=\"m" + (reply ? " r" : "") + "\"><div class=\"h\">" + WebUtility.HtmlEncode((Json.Str(m, "createdDateTime") ?? "").Replace("T", " ").Split('.')[0].TrimEnd('Z')) + " · <b>" + WebUtility.HtmlEncode(who) + "</b>"
                + (deleted ? " <span class=\"d\">(deleted)</span>" : "") + "</div><pre>" + WebUtility.HtmlEncode(text) + "</pre>"
                + (files.Count > 0 ? "<div class=\"h\">📎 " + WebUtility.HtmlEncode(string.Join(", ", files.ToArray())) + "</div>" : "") + "</div>");
        }

        /// <summary>M365-060: SharePoint sites (and so the files of Teams channels): every document library, by delta.</summary>
        void SharePoint(List<string> only)
        {
            List<Dictionary<string, object>> list;
            try { list = g.All("/sites?search=*&$select=id,displayName,name,webUrl&$top=999").ToList(); }
            catch (GraphClient.GraphException e) { Errors++; warn("SharePoint: " + e.Code + " " + e.Message + " (Sites.Read.All?)"); return; }
            foreach (var site in list)
            {
                var sid = Json.Str(site, "id"); var title = Json.Str(site, "displayName") ?? Json.Str(site, "name") ?? sid; var web = Json.Str(site, "webUrl") ?? "";
                if (only != null && !only.Any(x => string.Equals(x, title, StringComparison.OrdinalIgnoreCase) || string.Equals(x.TrimEnd('/'), web.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))) continue;
                Sites++;
                var key = "site-" + ShortId(sid);
                var st = Load(key);
                try
                {
                    foreach (var d in g.All("/sites/" + E(sid) + "/drives?$select=id,name"))
                    {
                        var did = Json.Str(d, "id");
                        try { Drive("/drives/" + E(did), "drive:" + did, "SharePoint/" + Safe(title) + "/" + Safe(Json.Str(d, "name"), 100), st); }
                        catch (GraphClient.GraphException e) { Errors++; warn("[" + title + "] " + Json.Str(d, "name") + ": " + e.Code + " " + e.Message); }
                        Save(key, st);
                    }
                }
                catch (GraphClient.GraphException e) { Errors++; warn("[" + title + "] libraries: " + e.Code + " " + e.Message); }
                Save(key, st);
            }
        }

        /// <summary>Writes a small item only when its content changed (unchanged files keep their time, restic skips them).</summary>
        bool WriteIfChanged(UserState st, string id, string rel, string text)
        {
            object old;
            if (st.Items.TryGetValue(id, out old) && (string)old != rel) Remove(st, id);
            var full = Full(rel);
            var bytes = Encoding.UTF8.GetBytes(text);
            if (File.Exists(full) && File.ReadAllBytes(full).SequenceEqual(bytes)) { st.Items[id] = rel; return false; }
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            Atomic.WriteText(full, text);
            st.Items[id] = rel;
            return true;
        }

        /// <summary>Removes the items of a kind ("c:" / "e:") that were not seen in this full read.</summary>
        void Sweep(UserState st, string kind, HashSet<string> seen)
        {
            foreach (var id in st.Items.Keys.Where(k => k.StartsWith(kind, StringComparison.Ordinal) && !seen.Contains(k)).ToList()) Remove(st, id);
        }

        static string Clean(Dictionary<string, object> item)
        {
            var d = new Dictionary<string, object>();
            foreach (var kv in item) if (!kv.Key.StartsWith("@odata", StringComparison.Ordinal) && kv.Key != "changeKey") d[kv.Key] = kv.Value;
            return Json.Write(d);
        }

        void Contacts_(string userId, string name, UserState st)
        {
            var seen = new HashSet<string>();
            var folders = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("/users/" + E(userId) + "/contacts", "Contacts") };
            foreach (var f in g.All("/users/" + E(userId) + "/contactFolders?$top=100"))
                folders.Add(new KeyValuePair<string, string>("/users/" + E(userId) + "/contactFolders/" + E(Json.Str(f, "id")) + "/contacts", Safe(Json.Str(f, "displayName"), 60)));
            foreach (var f in folders)
                foreach (var c in g.All(f.Key + "?$top=500"))
                {
                    var id = "c:" + Json.Str(c, "id");
                    if (!seen.Add(id)) continue;
                    var rel = "Contacts/" + name + "/" + f.Value + "/" + Safe(Json.Str(c, "displayName") ?? "(no name)", 70) + " [" + ShortId(id) + "].json";
                    if (WriteIfChanged(st, id, rel, Clean(c))) Contacts++;
                }
            Sweep(st, "c:", seen);
        }

        void Calendar(string userId, string name, UserState st)
        {
            var seen = new HashSet<string>();
            foreach (var cal in g.All("/users/" + E(userId) + "/calendars?$select=id,name&$top=100"))
            {
                var cname = Safe(Json.Str(cal, "name"), 60);
                // single events and series masters (with their recurrence); occurrences are generated from the master
                foreach (var ev in g.All("/users/" + E(userId) + "/calendars/" + E(Json.Str(cal, "id")) + "/events?$top=500"))
                {
                    var id = "e:" + Json.Str(ev, "id");
                    if (!seen.Add(id)) continue;
                    var start = Json.Str(Json.Child(ev, "start"), "dateTime") ?? "";
                    var when = start.Length >= 16 ? start.Substring(0, 10) + " " + start.Substring(11, 2) + start.Substring(14, 2) : "0000-00-00 0000";
                    var rel = "Calendar/" + name + "/" + cname + "/" + when + " " + Safe(Json.Str(ev, "subject") ?? "(no subject)", 70) + " [" + ShortId(id) + "].json";
                    if (WriteIfChanged(st, id, rel, Clean(ev))) Events++;
                }
            }
            Sweep(st, "e:", seen);
        }

        void Mail(string userId, string name, UserState st)
        {
            var folders = new List<KeyValuePair<string, string>>();   // id, path
            Action<string, string> walk = null;
            walk = (url, prefix) =>
            {
                foreach (var f in g.All(url))
                {
                    var fid = Json.Str(f, "id"); var path = prefix + Safe(Json.Str(f, "displayName"), 60);
                    folders.Add(new KeyValuePair<string, string>(fid, path));
                    if (Json.Num(f, "childFolderCount") > 0) walk("/users/" + E(userId) + "/mailFolders/" + E(fid) + "/childFolders?$top=100&includeHiddenFolders=true", path + "/");
                }
            };
            walk("/users/" + E(userId) + "/mailFolders?$top=100&includeHiddenFolders=true", "");
            foreach (var f in folders)
            {
                var key = "mail:" + f.Key;
                var start = Json.Str(st.Delta, key) ?? "/users/" + E(userId) + "/mailFolders/" + E(f.Key) + "/messages/delta?$select=id,subject,receivedDateTime";
                string next = null;
                try { foreach (var m in g.All(start, d => next = d)) MailItem(userId, name, f.Value, m, st); }
                catch (GraphClient.GraphException e) when (e.Status == 410 || e.Code == "SyncStateNotFound" || e.Code == "syncStateNotFound")
                {
                    // the delta token expired: read the folder again in full (unchanged items are skipped by their file)
                    st.Delta.Remove(key);
                    foreach (var m in g.All("/users/" + E(userId) + "/mailFolders/" + E(f.Key) + "/messages/delta?$select=id,subject,receivedDateTime", d => next = d)) MailItem(userId, name, f.Value, m, st);
                }
                if (next != null) st.Delta[key] = next;
                Save(userId, st);
            }
        }

        void MailItem(string userId, string name, string folderPath, Dictionary<string, object> m, UserState st)
        {
            var id = Json.Str(m, "id");
            if (m.ContainsKey("@removed")) { if (st.Items.ContainsKey(id)) { Remove(st, id); MailDeleted++; } return; }
            DateTime when;
            var date = DateTime.TryParse(Json.Str(m, "receivedDateTime"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out when) ? when.ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture) : "0000-00-00 0000";
            var rel = "Mail/" + name + "/" + folderPath + "/" + date + " " + Safe(Json.Str(m, "subject"), 70) + " [" + ShortId(id) + "].eml";
            bool known = st.Items.ContainsKey(id);
            if (known && (string)st.Items[id] != rel) Remove(st, id);   // moved / renamed
            try
            {
                g.Download("/users/" + E(userId) + "/messages/" + E(id) + "/$value", Full(rel));
                Downloaded += new FileInfo(Full(rel)).Length;
                st.Items[id] = rel;
                if (known) MailChanged++; else MailNew++;
            }
            catch (GraphClient.GraphException e) { Errors++; warn("mail " + rel + ": " + e.Code + " " + e.Message); }
        }

        /// <param name="driveBase">"/users/{id}/drive" (OneDrive) or "/drives/{id}" (a SharePoint library)</param>
        void Drive(string driveBase, string key, string prefix, UserState st)
        {
            var start = Json.Str(st.Delta, key) ?? driveBase + "/root/delta";
            string next = null;
            try { foreach (var it in g.All(start, d => next = d)) DriveItem(driveBase, prefix, it, st); }
            catch (GraphClient.GraphException e) when (e.Status == 410)
            {
                st.Delta.Remove(key);
                foreach (var it in g.All(driveBase + "/root/delta", d => next = d)) DriveItem(driveBase, prefix, it, st);
            }
            if (next != null) st.Delta[key] = next;
        }

        void DriveItem(string driveBase, string prefix, Dictionary<string, object> it, UserState st)
        {
            var id = Json.Str(it, "id");
            if (it.ContainsKey("deleted")) { if (st.Items.ContainsKey(id)) { Remove(st, id); st.Tags.Remove(id); FilesDeleted++; } return; }
            if (!it.ContainsKey("file")) return;   // folders and the root: their files carry the path
            var parent = Json.Str(Json.Child(it, "parentReference"), "path") ?? "";
            int colon = parent.IndexOf(':');
            var dir = colon >= 0 ? parent.Substring(colon + 1) : "";
            var parts = dir.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries).Select(p => Safe(Uri.UnescapeDataString(p), 100));
            var rel = prefix + "/" + string.Join("/", parts.Concat(new[] { Safe(Json.Str(it, "name"), 120) }).ToArray());
            bool known = st.Items.ContainsKey(id);
            if (known && (string)st.Items[id] != rel) Remove(st, id);
            var full = Full(rel);
            DateTime mod;
            bool hasMod = DateTime.TryParse(Json.Str(it, "lastModifiedDateTime"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out mod);
            // unchanged: the same content tag (cTag changes with the content even when size and time stay); without one, size + time
            var ctag = Json.Str(it, "cTag");
            if (known && File.Exists(full) && (ctag != null ? Json.Str(st.Tags, id) == ctag : new FileInfo(full).Length == Json.Num(it, "size") && hasMod && File.GetLastWriteTimeUtc(full) == mod)) return;
            try
            {
                g.Download(driveBase + "/items/" + E(id) + "/content", full);
                if (hasMod) File.SetLastWriteTimeUtc(full, mod);
                Downloaded += new FileInfo(full).Length;
                st.Items[id] = rel;
                if (ctag != null) st.Tags[id] = ctag;
                if (known) FilesChanged++; else FilesNew++;
            }
            catch (GraphClient.GraphException e) { Errors++; warn(rel + ": " + e.Code + " " + e.Message); }
        }
    }

    /// <summary>
    /// M365-030: restore items back into Microsoft 365 (into a "Restored &lt;date&gt;" folder, never over existing items):
    /// an .eml into the user's mailbox (Graph creates the message from its MIME), a file into the user's OneDrive
    /// (simple upload up to 4MB, otherwise an upload session in 10MB parts), a contact into a new contact folder, an event
    /// into a new calendar (without its attendees, so no invitation is sent; they are listed in the event's text), a
    /// SharePoint file into its library. The user / site is found from the item's path.
    /// </summary>
    public sealed class M365Restore
    {
        readonly GraphClient g; readonly string folderName;
        readonly Dictionary<string, string> users = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> mailFolders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> contactFolders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> calendars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> sites;
        readonly Dictionary<string, string> libraries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public int Mail, Files, Contacts, Events, Failed;

        public M365Restore(GraphClient g, DateTime nowUtc) { this.g = g; folderName = "Restored " + nowUtc.ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture); }

        string UserId(string safeUpn)
        {
            if (users.Count == 0) foreach (var u in g.All("/users?$select=id,userPrincipalName&$top=999")) users[M365Sync.Safe(Json.Str(u, "userPrincipalName"))] = Json.Str(u, "id");
            string id;
            if (!users.TryGetValue(safeUpn, out id)) throw new GraphClient.GraphException(404, "USER", "The user " + safeUpn + " is not in the tenant");
            return id;
        }

        static string E(string id) { return Uri.EscapeDataString(id ?? ""); }

        static readonly string[] ContactFields = { "givenName", "surname", "middleName", "nickName", "displayName", "title", "initials", "generation", "jobTitle", "companyName", "department",
            "officeLocation", "profession", "manager", "assistantName", "spouseName", "birthday", "personalNotes", "emailAddresses", "businessPhones", "homePhones", "mobilePhone",
            "businessAddress", "homeAddress", "otherAddress", "businessHomePage", "imAddresses", "categories", "children", "fileAs", "yomiGivenName", "yomiSurname", "yomiCompanyName" };
        static readonly string[] EventFields = { "subject", "body", "start", "end", "location", "locations", "isAllDay", "recurrence", "sensitivity", "showAs", "importance",
            "categories", "isReminderOn", "reminderMinutesBeforeStart", "originalStartTimeZone", "originalEndTimeZone" };

        static Dictionary<string, object> Writable(Dictionary<string, object> item, string[] fields)
        {
            var d = new Dictionary<string, object>();
            foreach (var f in fields) if (item.ContainsKey(f) && item[f] != null) d[f] = item[f];
            return d;
        }

        void SiteFile(string localFile, string[] parts, int k)
        {
            if (parts.Length < k + 4) throw new ArgumentException("not a SharePoint file: " + string.Join("/", parts));
            if (sites == null)
            {
                sites = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var s in g.All("/sites?search=*&$select=id,displayName,name&$top=999")) sites[M365Sync.Safe(Json.Str(s, "displayName") ?? Json.Str(s, "name") ?? Json.Str(s, "id"))] = Json.Str(s, "id");
            }
            string sid;
            if (!sites.TryGetValue(parts[k + 1], out sid)) throw new GraphClient.GraphException(404, "SITE", "The site " + parts[k + 1] + " is not in the tenant");
            var lk = sid + "|" + parts[k + 2];
            string did;
            if (!libraries.TryGetValue(lk, out did))
            {
                foreach (var d in g.All("/sites/" + E(sid) + "/drives?$select=id,name")) libraries[sid + "|" + M365Sync.Safe(Json.Str(d, "name"), 100)] = Json.Str(d, "id");
                if (!libraries.TryGetValue(lk, out did)) throw new GraphClient.GraphException(404, "LIBRARY", "The library " + parts[k + 2] + " is not in the site " + parts[k + 1]);
            }
            Upload(localFile, "/drives/" + E(did) + "/root:/" + Uri.EscapeDataString(folderName) + "/" + string.Join("/", parts.Skip(k + 3).Select(Uri.EscapeDataString).ToArray()));
            Files++;
        }

        static readonly string[] Kinds = { "Mail", "OneDrive", "Contacts", "Calendar", "SharePoint" };

        /// <param name="mirrorRel">the item's path inside the mirror: Mail|OneDrive|Contacts|Calendar/&lt;user&gt;/… or SharePoint/&lt;site&gt;/&lt;library&gt;/…</param>
        public void Item(string localFile, string mirrorRel)
        {
            var parts = mirrorRel.Replace('\\', '/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            int k = Array.FindIndex(parts, p => Kinds.Contains(p));
            if (k < 0 && parts.Contains("Teams")) throw new ArgumentException("Teams messages cannot be written back into Teams: restore them to a folder (.html to read, .json complete)");
            if (k < 0 || parts.Length < k + 3) throw new ArgumentException("not a Microsoft 365 item: " + mirrorRel);
            if (parts[k] == "SharePoint") { SiteFile(localFile, parts, k); return; }
            var userId = UserId(parts[k + 1]);
            if (parts[k] == "Contacts")
            {
                string fid;
                if (!contactFolders.TryGetValue(userId, out fid))
                    contactFolders[userId] = fid = Json.Str(g.Call("POST", "/users/" + E(userId) + "/contactFolders", Json.Write(new Dictionary<string, object> { { "displayName", folderName } })), "id");
                g.Call("POST", "/users/" + E(userId) + "/contactFolders/" + E(fid) + "/contacts", Json.Write(Writable(Json.Obj(Json.Parse(File.ReadAllText(localFile, Encoding.UTF8))), ContactFields)));
                Contacts++;
                return;
            }
            if (parts[k] == "Calendar")
            {
                string cid;
                if (!calendars.TryGetValue(userId, out cid))
                    calendars[userId] = cid = Json.Str(g.Call("POST", "/users/" + E(userId) + "/calendars", Json.Write(new Dictionary<string, object> { { "name", folderName } })), "id");
                var ev = Json.Obj(Json.Parse(File.ReadAllText(localFile, Encoding.UTF8)));
                var w = Writable(ev, EventFields);
                var who = Json.Arr(ev.ContainsKey("attendees") ? ev["attendees"] : null).Select(a => Json.Str(Json.Child(Json.Obj(a), "emailAddress"), "address")).Where(a => !string.IsNullOrEmpty(a)).ToList();
                if (who.Count > 0)
                {
                    // the attendees are not invited again: they are written into the event's text
                    var body = Json.Child(ev, "body"); var type = Json.Str(body, "contentType") ?? "text"; var content = Json.Str(body, "content") ?? "";
                    var note = "Attendees: " + string.Join(", ", who.ToArray());
                    w["body"] = new Dictionary<string, object> { { "contentType", type }, { "content", type.Equals("html", StringComparison.OrdinalIgnoreCase) ? "<p>" + System.Net.WebUtility.HtmlEncode(note) + "</p>" + content : note + "\n\n" + content } };
                }
                g.Call("POST", "/users/" + E(userId) + "/calendars/" + E(cid) + "/events", Json.Write(w));
                Events++;
                return;
            }
            if (parts[k] == "Mail")
            {
                string fid;
                if (!mailFolders.TryGetValue(userId, out fid))
                {
                    var f = g.Call("POST", "/users/" + userId + "/mailFolders", Json.Write(new Dictionary<string, object> { { "displayName", folderName } }));
                    mailFolders[userId] = fid = Json.Str(f, "id");
                }
                var mime = Encoding.ASCII.GetBytes(Convert.ToBase64String(File.ReadAllBytes(localFile)));
                g.Raw("POST", "/users/" + userId + "/mailFolders/" + fid + "/messages", mime, "text/plain");
                Mail++;
            }
            else
            {
                var rel = string.Join("/", parts.Skip(k + 2).Select(Uri.EscapeDataString).ToArray());
                Upload(localFile, "/users/" + userId + "/drive/root:/" + Uri.EscapeDataString(folderName) + "/" + rel);
                Files++;
            }
        }

        /// <param name="target">the drive path ".../root:/folder/file" (without ":/content")</param>
        void Upload(string localFile, string target)
        {
            var len = new FileInfo(localFile).Length;
            if (len <= 4 * 1024 * 1024) { g.Raw("PUT", target + ":/content", File.ReadAllBytes(localFile), "application/octet-stream"); return; }
            var session = g.Call("POST", target + ":/createUploadSession", Json.Write(new Dictionary<string, object> { { "item", new Dictionary<string, object> { { "@microsoft.graph.conflictBehavior", "rename" } } } }));
            var url = Json.Str(session, "uploadUrl");
            const int part = 10 * 327680;   // a multiple of 320 KiB
            var buf = new byte[part];
            using (var f = File.OpenRead(localFile))
                for (long pos = 0; pos < len;)
                {
                    int n = f.Read(buf, 0, (int)Math.Min(part, len - pos));
                    var chunk = new byte[n]; Array.Copy(buf, chunk, n);
                    g.Raw("PUT", url, chunk, "application/octet-stream", new Dictionary<string, string> { { "Content-Range", "bytes " + pos + "-" + (pos + n - 1) + "/" + len } }, false);
                    pos += n;
                }
        }
    }
}
