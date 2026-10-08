using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using OnlineBackup.Core;

namespace OnlineBackup.Server
{
    /// <summary>
    /// LIC-100..140: the product owner's licensing centre (as Ahsay's licence server). Every licensed backup server checks
    /// in daily: licence id, server id, its internal addresses, its usage. The centre remembers the addresses of the first
    /// check-in; when the external (as the centre sees it) or the internal addresses change, it answers IP_CHANGED until the
    /// owner approves the move (license-center approve). A revoked licence answers REVOKED. Answers are signed with the
    /// owner's private key and carry the server's nonce, so they cannot be forged or replayed.
    /// Run by the owner: OnlineBackup.Server license-center --key license-private.key --data D:\LicenseCenter [--prefix http://+:9443/]  (or as a service: license-center-install-service)
    /// </summary>
    public sealed class LicenseCenter : IDisposable
    {
        readonly HttpListener http = new HttpListener();
        readonly string dataDir, privateKey;
        readonly object gate = new object();
        public Func<DateTime> Clock = () => SystemClock.UtcNow;

        public Portal Portal { get; private set; }

        public LicenseCenter(string privateKeyB64, string dataDir, string prefix)
        {
            privateKey = privateKeyB64; this.dataDir = dataDir;
            Directory.CreateDirectory(dataDir);
            SysLog.InitIfUnset(dataDir);
            Portal = new Portal(dataDir, privateKeyB64, () => Clock());   // PORTAL-010: the partner portal on the same port
            http.Prefixes.Add(prefix); http.Start();
            new Thread(() => { while (http.IsListening) { try { var c = http.GetContext(); ThreadPool.QueueUserWorkItem(_ => Handle(c)); } catch { return; } } }) { IsBackground = true }.Start();
        }

        public void Dispose() { try { http.Stop(); http.Close(); } catch { } }

        string RecordPath(string licenseId) { return Path.Combine(dataDir, M365Safe(licenseId) + ".json"); }
        static string M365Safe(string s) { return new string((s ?? "").Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray()); }

        public Dictionary<string, object> Record(string licenseId)
        {
            var p = RecordPath(licenseId);
            return File.Exists(p) ? Json.Obj(Json.Parse(OnlineBackup.Core.Atomic.ReadAllText(p))) : null;
        }
        void Save(string licenseId, Dictionary<string, object> r) { Atomic.WriteText(RecordPath(licenseId), Json.Write(r)); }

        /// <summary>LIC-130: the owner approves a server's new addresses (the next check-in binds them).</summary>
        public void Approve(string licenseId) { lock (gate) { var r = Record(licenseId) ?? new Dictionary<string, object>(); r["approved"] = true; Save(licenseId, r); } }
        public void Revoke(string licenseId) { lock (gate) { var r = Record(licenseId) ?? new Dictionary<string, object>(); r["revoked"] = true; Save(licenseId, r); } }

        void Handle(HttpListenerContext c)
        {
            try
            {
                var path = c.Request.Url.AbsolutePath;
                if (path == "/" || path.StartsWith("/portal") || path.StartsWith("/i18n/"))
                {
                    if (path == "/") { c.Response.Redirect("/portal"); c.Response.Close(); return; }
                    var ip = c.Request.RemoteEndPoint == null ? "-" : c.Request.RemoteEndPoint.Address.ToString();
                    Portal.Handle(c, ip); return;
                }
                if (c.Request.HttpMethod != "POST" || path.TrimEnd('/') != "/license/checkin") { Reply(c, 404, "{}"); return; }
                string body; using (var rd = new StreamReader(c.Request.InputStream, Encoding.UTF8)) body = rd.ReadToEnd();
                var q = Json.Obj(Json.Parse(body));
                var licText = Json.Str(q, "license") ?? ""; var serverId = Json.Str(q, "serverId") ?? ""; var nonce = Json.Str(q, "nonce") ?? "";
                var external = c.Request.RemoteEndPoint == null ? "" : c.Request.RemoteEndPoint.Address.ToString();
                var forwarded = c.Request.Headers["X-Forwarded-For"]; if (!string.IsNullOrEmpty(forwarded)) external = forwarded.Split(',')[0].Trim();
                var internals = string.Join(",", Json.Arr(q.ContainsKey("internal") ? q["internal"] : null).Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)).OrderBy(x => x).ToArray());
                // the licence itself must be genuine and for this server
                var lic = License.Check(licText, serverId, Clock());
                string status, message = "";
                lock (gate)
                {
                    if (!lic.Valid) { status = "INVALID"; message = lic.Reason; }
                    else
                    {
                        var r = Record(lic.Id) ?? new Dictionary<string, object> { { "company", lic.Company }, { "serverId", serverId } };
                        if (r.ContainsKey("revoked") && true.Equals(r["revoked"])) { status = "REVOKED"; message = "The licence was revoked by the licensing centre"; }
                        else if (!r.ContainsKey("external") || (r.ContainsKey("approved") && true.Equals(r["approved"])))
                        {
                            r["external"] = external; r["internal"] = internals; r["approved"] = false; status = "OK";
                        }
                        else if (Json.Str(r, "external") != external || Json.Str(r, "internal") != internals)
                        {
                            status = "IP_CHANGED";
                            message = "The server address changed (external " + Json.Str(r, "external") + " → " + external + ", internal " + Json.Str(r, "internal") + " → " + internals + "). The licensing centre must approve it.";
                            r["pendingExternal"] = external; r["pendingInternal"] = internals;
                        }
                        else status = "OK";
                        r["lastSeen"] = Clock().ToString("o", CultureInfo.InvariantCulture); r["lastStatus"] = status;
                        r["users"] = Json.Num(q, "users"); r["devices"] = Json.Num(q, "devices"); r["storageGB"] = q.ContainsKey("storageGB") ? q["storageGB"] : 0d; r["version"] = Json.Str(q, "version");
                        Save(lic.Id, r);
                    }
                }
                var answer = Json.Write(new Dictionary<string, object> { { "status", status }, { "message", message }, { "nonce", nonce }, { "serverId", serverId }, { "time", Clock().ToString("o", CultureInfo.InvariantCulture) } });
                var bytes = Encoding.UTF8.GetBytes(answer);
                using (var ec = ECDsa.Create())
                {
                    ec.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKey), out _);
                    Reply(c, 200, Json.Write(new Dictionary<string, object> { { "answer", Convert.ToBase64String(bytes) }, { "signature", Convert.ToBase64String(ec.SignData(bytes, HashAlgorithmName.SHA256)) } }));
                }
            }
            catch (Exception e) { try { Reply(c, 500, Json.Write(new Dictionary<string, object> { { "error", e.Message } })); } catch { } }
        }

        static void Reply(HttpListenerContext c, int status, string json)
        {
            var b = Encoding.UTF8.GetBytes(json); c.Response.StatusCode = status; c.Response.ContentType = "application/json"; c.Response.ContentLength64 = b.Length;
            c.Response.OutputStream.Write(b, 0, b.Length); c.Response.Close();
        }
    }

    /// <summary>
    /// LIC-110..120: the backup server's side. Once a day (and when a licence is saved) it checks in with the licensing
    /// centre named in its licence. OK → the licence is fully valid. No answer, or IP_CHANGED → the licence becomes
    /// temporary: it keeps working 7 days, the administrators get a mail; after 7 days without an OK it falls back to the
    /// free edition. REVOKED → the free edition at once. The state lives in system.xml (LICENSE: ONLINE_STATUS, LAST_OK, TEMP_SINCE).
    /// </summary>
    public static class LicenseCheckin
    {
        public const int GraceDays = 7;

        public static List<string> InternalAddresses()
        {
            var env = Environment.GetEnvironmentVariable("OB_LOCAL_IPS");
            if (!string.IsNullOrEmpty(env)) return env.Split(',').Select(x => x.Trim()).OrderBy(x => x).ToList();
            try
            {
                return NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses).Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.Address.ToString()).Distinct().OrderBy(x => x).ToList();
            }
            catch (Exception) { return new List<string>(); }
        }

        /// <summary>Checks in now; returns the status (OK / TEMPORARY / REVOKED / NONE when the licence has no centre).</summary>
        public static string Run(SystemConfig cfg, Users users, Mailer mailer, DateTime nowUtc)
        {
            var lic = License.Check(LicenseText(cfg), cfg.ServerId, nowUtc);
            if (!lic.Valid || string.IsNullOrEmpty(lic.Center)) return "NONE";
            var el = cfg.Doc.Root.Element("LICENSE");
            var nonce = Bytes.Hex(Bytes.Random(16));
            string status, message;
            try
            {
                var req = Json.Write(new Dictionary<string, object>
                {
                    { "license", LicenseText(cfg) }, { "serverId", cfg.ServerId }, { "nonce", nonce }, { "internal", InternalAddresses().Cast<object>().ToList() },
                    { "users", users.Logins().Count() }, { "devices", users.ActiveComputers() }, { "version", typeof(LicenseCheckin).Assembly.GetName().Version.ToString() }
                });
                var r = (HttpWebRequest)WebRequest.Create(lic.Center.TrimEnd('/') + "/license/checkin");
                r.Method = "POST"; r.ContentType = "application/json"; r.Timeout = 30000;
                var b = Encoding.UTF8.GetBytes(req); r.ContentLength = b.Length;
                using (var s = r.GetRequestStream()) s.Write(b, 0, b.Length);
                Dictionary<string, object> resp;
                using (var w = (HttpWebResponse)r.GetResponse()) using (var rd = new StreamReader(w.GetResponseStream(), Encoding.UTF8)) resp = Json.Obj(Json.Parse(rd.ReadToEnd()));
                var answer = Convert.FromBase64String(Json.Str(resp, "answer") ?? "");
                if (!License.VerifyCenter(answer, Convert.FromBase64String(Json.Str(resp, "signature") ?? ""))) throw new InvalidOperationException("the centre's answer is not signed by the product owner");
                var a = Json.Obj(Json.Parse(Encoding.UTF8.GetString(answer)));
                if (Json.Str(a, "nonce") != nonce || Json.Str(a, "serverId") != cfg.ServerId) throw new InvalidOperationException("the centre's answer is not for this request");
                status = Json.Str(a, "status"); message = Json.Str(a, "message") ?? "";
            }
            catch (Exception e) { status = "UNREACHABLE"; message = "No connection to the licensing centre: " + e.Message; }

            string result;
            lock (cfg)
            {
                var before = (string)el.Attribute("ONLINE_STATUS");
                el.SetAttributeValue("ONLINE_STATUS", status); el.SetAttributeValue("ONLINE_MESSAGE", message);
                el.SetAttributeValue("LAST_CHECK", nowUtc.ToString("o", CultureInfo.InvariantCulture));
                if (status == "OK") { el.SetAttributeValue("LAST_OK", nowUtc.ToString("o", CultureInfo.InvariantCulture)); el.SetAttributeValue("TEMP_SINCE", null); result = "OK"; }
                else if (status == "REVOKED" || status == "INVALID") result = "REVOKED";
                else
                {
                    if (el.Attribute("TEMP_SINCE") == null)
                    {
                        el.SetAttributeValue("TEMP_SINCE", nowUtc.ToString("o", CultureInfo.InvariantCulture));
                        mailer.Alert("⚠ The licence is now temporary (" + GraceDays + " days)", "<p>" + Fmt.H(message) + "</p><p>The server keeps working fully for " + GraceDays + " days. After that, without approval from the licensing centre, it switches to the basic edition.</p>");
                    }
                    result = "TEMPORARY";
                }
                if (result == "REVOKED" && before != status) mailer.Alert("✗ The licence was revoked", "<p>" + Fmt.H(message) + "</p><p>The server switched to the basic edition.</p>");
                cfg.Save(); cfg.ResetLicense();
            }
            return result;
        }

        /// <summary>
        /// LIC-150: "Update licence" — asks the vendor's portal / licensing centre for the newest licence issued for this
        /// server (e.g. after more computers or storage were bought) and puts it in place when it is genuine and for this
        /// server. Returns UPDATED, CURRENT (nothing newer) or NO_SOURCE (no portal and no centre known: paste the licence).
        /// </summary>
        public static string Update(SystemConfig cfg, Users users, Mailer mailer, DateTime nowUtc)
        {
            var el = cfg.Doc.Root.Element("LICENSE");
            var current = LicenseText(cfg) ?? "";
            var lic = License.Check(current, cfg.ServerId, nowUtc);
            var source = (string)el?.Attribute("PORTAL_URL");
            if (string.IsNullOrEmpty(source)) source = lic.Center;
            if (string.IsNullOrEmpty(source)) return "NO_SOURCE";
            var body = Json.Write(new Dictionary<string, object> { { "serverId", cfg.ServerId }, { "license", current }, { "account", (string)el?.Attribute("PORTAL_ACCOUNT") ?? "" }, { "token", (string)el?.Attribute("PORTAL_TOKEN") ?? "" } });
            string fresh;
            try
            {
                var r = (HttpWebRequest)WebRequest.Create(source.TrimEnd('/') + "/portal/api/latestlicense");
                r.Method = "POST"; r.ContentType = "application/json"; r.Timeout = 30000;
                var b = Encoding.UTF8.GetBytes(body); r.ContentLength = b.Length;
                using (var st = r.GetRequestStream()) st.Write(b, 0, b.Length);
                using (var w = (HttpWebResponse)r.GetResponse()) using (var rd = new StreamReader(w.GetResponseStream(), Encoding.UTF8)) fresh = Json.Str(Json.Obj(Json.Parse(rd.ReadToEnd())), "license") ?? "";
            }
            catch (WebException e)
            {
                string m = e.Message;
                try { using (var rd = new StreamReader(((HttpWebResponse)e.Response).GetResponseStream())) m = Json.Str(Json.Obj(Json.Parse(rd.ReadToEnd())), "message") ?? m; } catch (Exception) { }
                throw new ApiException(502, "LICENSE_SOURCE", "The licensing centre did not answer: " + m);
            }
            if (fresh.Length == 0 || fresh == current) return "CURRENT";
            var n = License.Check(fresh, cfg.ServerId, nowUtc);
            if (!n.Valid) throw new ApiException(502, "LICENSE", "The licence from the licensing centre is not valid: " + n.Reason);
            lock (cfg)
            {
                el.SetAttributeValue("KEY", fresh); el.SetAttributeValue("TEMP_SINCE", null); el.SetAttributeValue("ONLINE_STATUS", null);
                cfg.Save(); cfg.ResetLicense();
            }
            if (!string.IsNullOrEmpty(n.Center)) Run(cfg, users, mailer, nowUtc);
            return "UPDATED";
        }

        static string LicenseText(SystemConfig cfg) { var e = cfg.Doc.Root.Element("LICENSE"); return e == null ? null : (string)e.Attribute("KEY"); }
    }
}
