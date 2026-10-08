using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using OnlineBackup.Core;

namespace OnlineBackup.Agent
{
    /// <summary>
    /// GWS-010: Google Workspace APIs with a service account and domain-wide delegation (the customer's admin allows the
    /// service account's client id for the scopes below). A token is asked per user (JWT RS256 "sub" = the user).
    /// Scopes: gmail.readonly, drive.readonly, admin.directory.user.readonly — restore back into Google also gmail.insert,
    /// gmail.labels and drive.file. Rate limits (429 / 5xx / 403 rateLimitExceeded) wait and retry.
    /// OB_GOOGLE (APIs) / OB_GOOGLE_TOKEN (token endpoint) replace Google's addresses in tests.
    /// </summary>
    public sealed class GoogleClient
    {
        public const string ReadScopes = "https://www.googleapis.com/auth/gmail.readonly https://www.googleapis.com/auth/drive.readonly https://www.googleapis.com/auth/admin.directory.user.readonly";
        public const string RestoreScopes = "https://www.googleapis.com/auth/gmail.insert https://www.googleapis.com/auth/gmail.labels https://www.googleapis.com/auth/drive.file";
        readonly string clientEmail; readonly RSAParameters key; readonly string scopes;
        readonly Dictionary<string, KeyValuePair<string, DateTime>> tokens = new Dictionary<string, KeyValuePair<string, DateTime>>();
        public string Api = Environment.GetEnvironmentVariable("OB_GOOGLE") ?? "https://www.googleapis.com";
        public string TokenUrl = Environment.GetEnvironmentVariable("OB_GOOGLE_TOKEN") ?? "https://oauth2.googleapis.com/token";

        public sealed class GoogleException : Exception { public int Status; public GoogleException(int s, string m) : base(m) { Status = s; } }

        /// <param name="serviceAccountJson">the service account's key file (client_email, private_key)</param>
        public GoogleClient(string serviceAccountJson, string scopes)
        {
            var j = Json.Obj(Json.Parse(serviceAccountJson));
            clientEmail = Json.Str(j, "client_email");
            key = Pkcs8.Rsa(Json.Str(j, "private_key"));
            this.scopes = scopes;
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; } catch (NotSupportedException) { }
        }

        static string B64u(byte[] b) { return Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }

        string Token(string user)
        {
            KeyValuePair<string, DateTime> t;
            if (tokens.TryGetValue(user, out t) && SystemClock.UtcNow < t.Value) return t.Key;
            long now = (long)(SystemClock.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            var head = B64u(Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"typ\":\"JWT\"}"));
            var claims = B64u(Encoding.UTF8.GetBytes(Json.Write(new Dictionary<string, object> { { "iss", clientEmail }, { "sub", user }, { "scope", scopes }, { "aud", TokenUrl }, { "iat", now }, { "exp", now + 3600 } })));
            var jwt = head + "." + claims + "." + B64u(Pkcs8.SignSha256(key, Encoding.ASCII.GetBytes(head + "." + claims)));
            var r = (HttpWebRequest)WebRequest.Create(TokenUrl);
            r.Method = "POST"; r.ContentType = "application/x-www-form-urlencoded";
            var b = Encoding.ASCII.GetBytes("grant_type=" + Uri.EscapeDataString("urn:ietf:params:oauth:grant-type:jwt-bearer") + "&assertion=" + jwt);
            r.ContentLength = b.Length; using (var s = r.GetRequestStream()) s.Write(b, 0, b.Length);
            try
            {
                using (var resp = (HttpWebResponse)r.GetResponse()) using (var rd = new StreamReader(resp.GetResponseStream()))
                {
                    var j = Json.Obj(Json.Parse(rd.ReadToEnd()));
                    var tok = Json.Str(j, "access_token");
                    tokens[user] = new KeyValuePair<string, DateTime>(tok, SystemClock.UtcNow.AddSeconds(Math.Max(60, Json.Num(j, "expires_in") - 120)));
                    return tok;
                }
            }
            catch (WebException e)
            {
                string code = "LOGIN";
                try { using (var rd = new StreamReader(e.Response.GetResponseStream())) code = Json.Str(Json.Obj(Json.Parse(rd.ReadToEnd())), "error") ?? code; } catch { }
                throw new GoogleException(401, "Google sign-in failed for " + user + " (" + code + "): check the service account key and its domain-wide delegation");
            }
        }

        HttpWebResponse Send(string user, string method, string url, byte[] body, string contentType)
        {
            for (int attempt = 0; ; attempt++)
            {
                var r = (HttpWebRequest)WebRequest.Create(url.StartsWith("http") ? url : Api + url);
                r.Method = method; r.Timeout = 300000; r.ReadWriteTimeout = 300000;
                r.Headers["Authorization"] = "Bearer " + Token(user);
                if (body != null) { r.ContentType = contentType; r.ContentLength = body.Length; using (var s = r.GetRequestStream()) s.Write(body, 0, body.Length); }
                else if (method != "GET") r.ContentLength = 0;
                try { return (HttpWebResponse)r.GetResponse(); }
                catch (WebException e)
                {
                    var resp = e.Response as HttpWebResponse;
                    if (resp == null) { if (attempt < 4) { Thread.Sleep(2000 * (attempt + 1)); continue; } throw; }
                    int st = (int)resp.StatusCode; string text = "";
                    try { using (var rd = new StreamReader(resp.GetResponseStream())) text = rd.ReadToEnd(); } catch { }
                    bool rate = st == 429 || st >= 500 || (st == 403 && (text.Contains("rateLimitExceeded") || text.Contains("userRateLimitExceeded")));
                    if (rate && attempt < 6) { Thread.Sleep(TimeSpan.FromSeconds(Math.Min(64, Math.Pow(2, attempt)))); continue; }
                    string msg = text;
                    try { msg = Json.Str(Json.Child(Json.Obj(Json.Parse(text)), "error"), "message") ?? text; } catch { }
                    throw new GoogleException(st, "Google " + st + ": " + msg);
                }
            }
        }

        public Dictionary<string, object> Get(string user, string url)
        {
            using (var resp = Send(user, "GET", url, null, null)) using (var rd = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) return Json.Obj(Json.Parse(rd.ReadToEnd()));
        }

        public Dictionary<string, object> Call(string user, string method, string url, string json, string contentType = "application/json")
        {
            using (var resp = Send(user, method, url, json == null ? null : Encoding.UTF8.GetBytes(json), contentType))
            using (var rd = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) { var t = rd.ReadToEnd(); return t.Length == 0 ? new Dictionary<string, object>() : Json.Obj(Json.Parse(t)); }
        }

        public Dictionary<string, object> Upload(string user, string url, byte[] body, string contentType)
        {
            using (var resp = Send(user, "POST", url, body, contentType))
            using (var rd = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) { var t = rd.ReadToEnd(); return t.Length == 0 ? new Dictionary<string, object>() : Json.Obj(Json.Parse(t)); }
        }

        public void Download(string user, string url, string toFile)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(toFile));
            var tmp = toFile + ".part";
            using (var resp = Send(user, "GET", url, null, null)) using (var s = resp.GetResponseStream()) using (var f = File.Create(tmp)) s.CopyTo(f, 1 << 16);
            if (File.Exists(toFile)) File.Delete(toFile);
            File.Move(tmp, toFile);
        }
    }

    /// <summary>PKCS#8 RSA private key (PEM) → RSAParameters, and SHA-256 signatures — .NET 4.0 has no PEM import.</summary>
    public static class Pkcs8
    {
        public static RSAParameters Rsa(string pem)
        {
            var b64 = string.Concat((pem ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("-----")).ToArray());
            var der = Convert.FromBase64String(b64);
            int i = 0;
            Seq(der, ref i);                       // PrivateKeyInfo
            Int(der, ref i);                       // version
            Seq(der, ref i); Skip(der, ref i);     // AlgorithmIdentifier: OID (+ NULL)
            if (der[i] == 0x05) Skip(der, ref i);
            if (der[i] != 0x04) throw new FormatException("PKCS#8: OCTET STRING expected");
            i++; Len(der, ref i);
            Seq(der, ref i);                       // RSAPrivateKey
            Int(der, ref i);
            var p = new RSAParameters { Modulus = Int(der, ref i), Exponent = Int(der, ref i), D = Int(der, ref i), P = Int(der, ref i), Q = Int(der, ref i), DP = Int(der, ref i), DQ = Int(der, ref i), InverseQ = Int(der, ref i) };
            // CryptoAPI wants D the modulus' length and the CRT parts half of it
            int n = p.Modulus.Length, h = (n + 1) / 2;
            p.D = Pad(p.D, n); p.P = Pad(p.P, h); p.Q = Pad(p.Q, h); p.DP = Pad(p.DP, h); p.DQ = Pad(p.DQ, h); p.InverseQ = Pad(p.InverseQ, h);
            return p;
        }

        static int Len(byte[] d, ref int i)
        {
            int l = d[i++];
            if ((l & 0x80) == 0) return l;
            int n = l & 0x7f; l = 0; for (int k = 0; k < n; k++) l = (l << 8) | d[i++]; return l;
        }
        static void Seq(byte[] d, ref int i) { if (d[i++] != 0x30) throw new FormatException("PKCS#8: SEQUENCE expected"); Len(d, ref i); }
        static void Skip(byte[] d, ref int i) { i++; int l = Len(d, ref i); i += l; }
        static byte[] Int(byte[] d, ref int i)
        {
            if (d[i++] != 0x02) throw new FormatException("PKCS#8: INTEGER expected");
            int l = Len(d, ref i); var v = new byte[l]; Array.Copy(d, i, v, 0, l); i += l;
            int z = 0; while (z < v.Length - 1 && v[z] == 0) z++;
            return z == 0 ? v : v.Skip(z).ToArray();
        }
        static byte[] Pad(byte[] v, int len) { if (v.Length >= len) return v; var r = new byte[len]; Array.Copy(v, 0, r, len - v.Length, v.Length); return r; }

        public static byte[] SignSha256(RSAParameters key, byte[] data)
        {
#if NETFRAMEWORK
            using (var rsa = new RSACryptoServiceProvider(new CspParameters(24)))   // PROV_RSA_AES: SHA-256 on every Windows
            {
                rsa.ImportParameters(key);
                return rsa.SignData(data, "SHA256");
            }
#else
            using (var rsa = RSA.Create()) { rsa.ImportParameters(key); return rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1); }
#endif
        }
    }

    /// <summary>
    /// GWS-020: Gmail and Google Drive of every user into the local mirror, item by item: Gmail\&lt;user&gt;\&lt;yyyy-MM&gt;\&lt;date
    /// subject [id]&gt;.eml (the raw message) and Drive\&lt;user&gt;\&lt;path&gt; (Google Docs / Sheets / Slides exported as
    /// .docx / .xlsx / .pptx). Incremental: Gmail history (historyId), Drive changes (page token); an expired history
    /// reads the mailbox again (existing messages are skipped). State in &lt;mirror&gt;\.state\g-&lt;user&gt;.json.
    /// </summary>
    public sealed class GoogleSync
    {
        readonly GoogleClient g; readonly string mirror; readonly Action<string> info, warn;
        public int Users, MailNew, MailDeleted, FilesNew, FilesChanged, FilesDeleted, Errors; public long Downloaded;
        static readonly Dictionary<string, string[]> Exports = new Dictionary<string, string[]>
        {
            { "application/vnd.google-apps.document", new[] { "application/vnd.openxmlformats-officedocument.wordprocessingml.document", ".docx" } },
            { "application/vnd.google-apps.spreadsheet", new[] { "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ".xlsx" } },
            { "application/vnd.google-apps.presentation", new[] { "application/vnd.openxmlformats-officedocument.presentationml.presentation", ".pptx" } },
            { "application/vnd.google-apps.drawing", new[] { "application/pdf", ".pdf" } }
        };

        public GoogleSync(GoogleClient g, string mirror, Action<string> info, Action<string> warn) { this.g = g; this.mirror = mirror; this.info = info; this.warn = warn; }

        string Full(string rel) { return Path.Combine(mirror, rel.Replace('/', Path.DirectorySeparatorChar)); }
        static string E(string s) { return Uri.EscapeDataString(s ?? ""); }

        sealed class State { public Dictionary<string, object> Data = new Dictionary<string, object>(); public Dictionary<string, object> Items = new Dictionary<string, object>(); public Dictionary<string, object> Tags = new Dictionary<string, object>(); }
        State Load(string user)
        {
            var p = Path.Combine(mirror, ".state", "g-" + M365Sync.Safe(user) + ".json"); var s = new State();
            if (!File.Exists(p)) return s;
            var j = Json.Obj(Json.Parse(OnlineBackup.Core.Atomic.ReadAllText(p, Encoding.UTF8)));
            s.Data = Json.Obj(j.ContainsKey("data") ? j["data"] : null); s.Items = Json.Obj(j.ContainsKey("items") ? j["items"] : null); s.Tags = Json.Obj(j.ContainsKey("tags") ? j["tags"] : null);
            return s;
        }
        void Save(string user, State s) { Atomic.WriteText(Path.Combine(mirror, ".state", "g-" + M365Sync.Safe(user) + ".json"), Json.Write(new Dictionary<string, object> { { "data", s.Data }, { "items", s.Items }, { "tags", s.Tags } })); }
        void Remove(State st, string key) { object rel; if (!st.Items.TryGetValue(key, out rel)) return; var f = Full((string)rel); if (File.Exists(f)) File.Delete(f); st.Items.Remove(key); st.Tags.Remove(key); }

        /// <param name="users">the users to back up; empty = every active user of the domain (needs adminUser)</param>
        public void Run(ICollection<string> users, string adminUser)
        {
            Directory.CreateDirectory(mirror);
            var list = users != null && users.Count > 0 ? users.ToList() : DomainUsers(adminUser);
            foreach (var u in list)
            {
                Users++;
                var st = Load(u); var name = M365Sync.Safe(u);
                try { Gmail(u, name, st); } catch (GoogleClient.GoogleException e) { Errors++; warn("[" + u + "] Gmail: " + e.Message); }
                try { Drive(u, name, st); } catch (GoogleClient.GoogleException e) { Errors++; warn("[" + u + "] Drive: " + e.Message); }
                Save(u, st);
            }
            info("Google Workspace: users " + Users + ", mail new " + MailNew + " / deleted " + MailDeleted + ", Drive new " + FilesNew + " / changed " + FilesChanged + " / deleted " + FilesDeleted
                + ", downloaded " + Downloaded + " bytes, errors " + Errors);
        }

        List<string> DomainUsers(string admin)
        {
            if (string.IsNullOrEmpty(admin)) throw new GoogleClient.GoogleException(400, "Google Workspace: give the users, or an administrator to list them");
            var r = new List<string>(); string page = null;
            do
            {
                var j = g.Get(admin, "/admin/directory/v1/users?customer=my_customer&maxResults=500" + (page == null ? "" : "&pageToken=" + E(page)));
                foreach (var u in Json.Arr(j.ContainsKey("users") ? j["users"] : null).Select(Json.Obj))
                    if (!true.Equals(u.ContainsKey("suspended") ? u["suspended"] : false)) r.Add(Json.Str(u, "primaryEmail"));
                page = Json.Str(j, "nextPageToken");
            } while (page != null);
            return r;
        }

        void Gmail(string u, string name, State st)
        {
            var profile = g.Get(u, "/gmail/v1/users/me/profile");
            var hist = Json.Str(st.Data, "historyId");
            var added = new List<string>(); var deleted = new List<string>();
            bool full = hist == null;
            if (!full)
                try
                {
                    string page = null;
                    do
                    {
                        var j = g.Get(u, "/gmail/v1/users/me/history?startHistoryId=" + E(hist) + "&historyTypes=messageAdded&historyTypes=messageDeleted" + (page == null ? "" : "&pageToken=" + E(page)));
                        foreach (var h in Json.Arr(j.ContainsKey("history") ? j["history"] : null).Select(Json.Obj))
                        {
                            foreach (var a in Json.Arr(h.ContainsKey("messagesAdded") ? h["messagesAdded"] : null)) added.Add(Json.Str(Json.Child(Json.Obj(a), "message"), "id"));
                            foreach (var d in Json.Arr(h.ContainsKey("messagesDeleted") ? h["messagesDeleted"] : null)) deleted.Add(Json.Str(Json.Child(Json.Obj(d), "message"), "id"));
                        }
                        page = Json.Str(j, "nextPageToken");
                    } while (page != null);
                }
                catch (GoogleClient.GoogleException e) when (e.Status == 404) { full = true; }   // history too old: read again
            if (full)
            {
                string page = null;
                do
                {
                    var j = g.Get(u, "/gmail/v1/users/me/messages?maxResults=500&includeSpamTrash=false" + (page == null ? "" : "&pageToken=" + E(page)));
                    foreach (var m in Json.Arr(j.ContainsKey("messages") ? j["messages"] : null)) added.Add(Json.Str(Json.Obj(m), "id"));
                    page = Json.Str(j, "nextPageToken");
                } while (page != null);
            }
            foreach (var id in deleted.Distinct()) if (st.Items.ContainsKey("m:" + id)) { Remove(st, "m:" + id); MailDeleted++; }
            foreach (var id in added.Distinct().Where(x => !deleted.Contains(x)))
            {
                if (st.Items.ContainsKey("m:" + id)) continue;
                try
                {
                    var m = g.Get(u, "/gmail/v1/users/me/messages/" + E(id) + "?format=raw");
                    var raw = Convert.FromBase64String(Pad(Json.Str(m, "raw").Replace('-', '+').Replace('_', '/')));
                    long ms; long.TryParse(Json.Str(m, "internalDate") ?? "0", out ms);
                    var when = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms);
                    var subject = Header(raw, "Subject");
                    var rel = "Gmail/" + name + "/" + when.ToString("yyyy-MM", CultureInfo.InvariantCulture) + "/" + when.ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture) + " " + M365Sync.Safe(subject, 70) + " [" + M365Sync.Safe(id, 24) + "].eml";
                    Directory.CreateDirectory(Path.GetDirectoryName(Full(rel)));
                    File.WriteAllBytes(Full(rel), raw);
                    Downloaded += raw.Length; st.Items["m:" + id] = rel; MailNew++;
                }
                catch (GoogleClient.GoogleException e) when (e.Status == 404) { }   // deleted meanwhile
                catch (GoogleClient.GoogleException e) { Errors++; warn("Gmail " + u + " " + id + ": " + e.Message); }
            }
            st.Data["historyId"] = Json.Str(profile, "historyId");
        }

        static string Pad(string s) { return s + new string('=', (4 - s.Length % 4) % 4); }
        static string Header(byte[] raw, string name)
        {
            var head = Encoding.UTF8.GetString(raw, 0, Math.Min(raw.Length, 16384));
            foreach (var line in head.Split('\n')) { if (line.Trim().Length == 0) break; if (line.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase)) return line.Substring(name.Length + 1).Trim(); }
            return "";
        }

        readonly Dictionary<string, KeyValuePair<string, string>> folders = new Dictionary<string, KeyValuePair<string, string>>();   // id → (name, parent)
        string PathOf(string u, string parent, int depth = 0)
        {
            if (string.IsNullOrEmpty(parent) || depth > 40) return "";
            KeyValuePair<string, string> f;
            if (!folders.TryGetValue(parent, out f))
            {
                try
                {
                    var j = g.Get(u, "/drive/v3/files/" + E(parent) + "?fields=id,name,parents&supportsAllDrives=true");
                    var ps = Json.Arr(j.ContainsKey("parents") ? j["parents"] : null);
                    f = new KeyValuePair<string, string>(Json.Str(j, "name"), ps.Count > 0 ? Convert.ToString(ps[0], CultureInfo.InvariantCulture) : null);
                }
                catch (GoogleClient.GoogleException) { f = new KeyValuePair<string, string>("", null); }
                folders[parent] = f;
            }
            if (f.Value == null) return "";   // My Drive itself
            var up = PathOf(u, f.Value, depth + 1);
            return (up.Length > 0 ? up + "/" : "") + M365Sync.Safe(f.Key, 100);
        }

        void Drive(string u, string name, State st)
        {
            folders.Clear();
            var token = Json.Str(st.Data, "driveToken");
            if (token == null)
            {
                // first run: every file, then the changes from here on
                var start = Json.Str(g.Get(u, "/drive/v3/changes/startPageToken"), "startPageToken");
                string page = null;
                do
                {
                    var j = g.Get(u, "/drive/v3/files?pageSize=1000&q=" + E("trashed=false and 'me' in owners") + "&fields=" + E("nextPageToken,files(id,name,mimeType,parents,md5Checksum,modifiedTime,size)") + (page == null ? "" : "&pageToken=" + E(page)));
                    foreach (var f in Json.Arr(j.ContainsKey("files") ? j["files"] : null).Select(Json.Obj)) File1(u, name, f, st);
                    page = Json.Str(j, "nextPageToken");
                } while (page != null);
                st.Data["driveToken"] = start;
                return;
            }
            string p2 = token;
            while (p2 != null)
            {
                var j = g.Get(u, "/drive/v3/changes?pageToken=" + E(p2) + "&pageSize=1000&fields=" + E("nextPageToken,newStartPageToken,changes(fileId,removed,file(id,name,mimeType,parents,md5Checksum,modifiedTime,size,trashed))"));
                foreach (var c in Json.Arr(j.ContainsKey("changes") ? j["changes"] : null).Select(Json.Obj))
                {
                    var id = Json.Str(c, "fileId"); var f = Json.Child(c, "file");
                    if (true.Equals(c.ContainsKey("removed") ? c["removed"] : false) || f == null || true.Equals(f.ContainsKey("trashed") ? f["trashed"] : false))
                    { if (st.Items.ContainsKey("f:" + id)) { Remove(st, "f:" + id); FilesDeleted++; } continue; }
                    File1(u, name, f, st);
                }
                if (j.ContainsKey("newStartPageToken")) { st.Data["driveToken"] = Json.Str(j, "newStartPageToken"); p2 = null; }
                else p2 = Json.Str(j, "nextPageToken");
            }
        }

        void File1(string u, string name, Dictionary<string, object> f, State st)
        {
            var id = Json.Str(f, "id"); var mime = Json.Str(f, "mimeType") ?? "";
            if (mime == "application/vnd.google-apps.folder" || (mime.StartsWith("application/vnd.google-apps.") && !Exports.ContainsKey(mime))) return;   // folders, forms, maps…
            var parents = Json.Arr(f.ContainsKey("parents") ? f["parents"] : null);
            var dir = parents.Count > 0 ? PathOf(u, Convert.ToString(parents[0], CultureInfo.InvariantCulture)) : "";
            string[] export; Exports.TryGetValue(mime, out export);
            var fileName = M365Sync.Safe(Json.Str(f, "name"), 120) + (export != null ? export[1] : "");
            var rel = "Drive/" + name + "/" + (dir.Length > 0 ? dir + "/" : "") + fileName;
            var tag = Json.Str(f, "md5Checksum") ?? Json.Str(f, "modifiedTime");
            bool known = st.Items.ContainsKey("f:" + id);
            if (known && (string)st.Items["f:" + id] == rel && Json.Str(st.Tags, "f:" + id) == tag && File.Exists(Full(rel))) return;
            if (known && (string)st.Items["f:" + id] != rel) Remove(st, "f:" + id);
            try
            {
                g.Download(u, export != null ? "/drive/v3/files/" + E(id) + "/export?mimeType=" + E(export[0]) : "/drive/v3/files/" + E(id) + "?alt=media", Full(rel));
                DateTime mod;
                if (DateTime.TryParse(Json.Str(f, "modifiedTime"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out mod)) File.SetLastWriteTimeUtc(Full(rel), mod);
                Downloaded += new FileInfo(Full(rel)).Length;
                st.Items["f:" + id] = rel; st.Tags["f:" + id] = tag;
                if (known) FilesChanged++; else FilesNew++;
            }
            catch (GoogleClient.GoogleException e) { Errors++; warn("Drive " + rel + ": " + e.Message); }
        }
    }

    /// <summary>GWS-030: restore into Google — a message into Gmail (label "Restored &lt;date&gt;"), a file into Drive (folder "Restored &lt;date&gt;").</summary>
    public sealed class GoogleRestore
    {
        readonly GoogleClient g; readonly string name;
        readonly Dictionary<string, string> labels = new Dictionary<string, string>(), folders = new Dictionary<string, string>();
        public int Mail, Files, Failed;
        public GoogleRestore(GoogleClient g, DateTime nowUtc) { this.g = g; name = "Restored " + nowUtc.ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture); }

        public void Item(string localFile, string mirrorRel, Func<string, string> userOfSafeName)
        {
            var parts = mirrorRel.Replace('\\', '/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            int k = Array.FindIndex(parts, p => p == "Gmail" || p == "Drive");
            if (k < 0 || parts.Length < k + 3) throw new ArgumentException("not a Google Workspace item: " + mirrorRel);
            var user = userOfSafeName(parts[k + 1]);
            if (parts[k] == "Gmail")
            {
                string label;
                if (!labels.TryGetValue(user, out label))
                    labels[user] = label = Json.Str(g.Call(user, "POST", "/gmail/v1/users/me/labels", Json.Write(new Dictionary<string, object> { { "name", name }, { "labelListVisibility", "labelShow" }, { "messageListVisibility", "show" } })), "id");
                var raw = Convert.ToBase64String(OnlineBackup.Core.Atomic.ReadAllBytes(localFile)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                g.Call(user, "POST", "/gmail/v1/users/me/messages?internalDateSource=dateHeader", Json.Write(new Dictionary<string, object> { { "raw", raw }, { "labelIds", new List<object> { label } } }));
                Mail++;
            }
            else
            {
                string folder;
                if (!folders.TryGetValue(user, out folder))
                    folders[user] = folder = Json.Str(g.Call(user, "POST", "/drive/v3/files", Json.Write(new Dictionary<string, object> { { "name", name }, { "mimeType", "application/vnd.google-apps.folder" } })), "id");
                var boundary = "ob" + Guid.NewGuid().ToString("N");
                var meta = Json.Write(new Dictionary<string, object> { { "name", string.Join(" - ", parts.Skip(k + 2).ToArray()) }, { "parents", new List<object> { folder } } });
                var head = Encoding.UTF8.GetBytes("--" + boundary + "\r\nContent-Type: application/json; charset=UTF-8\r\n\r\n" + meta + "\r\n--" + boundary + "\r\nContent-Type: application/octet-stream\r\n\r\n");
                var tail = Encoding.ASCII.GetBytes("\r\n--" + boundary + "--");
                var body = head.Concat(OnlineBackup.Core.Atomic.ReadAllBytes(localFile)).Concat(tail).ToArray();
                g.Upload(user, (g.Api.EndsWith("/") ? g.Api.TrimEnd('/') : g.Api) + "/upload/drive/v3/files?uploadType=multipart", body, "multipart/related; boundary=" + boundary);
                Files++;
            }
        }
    }
}
