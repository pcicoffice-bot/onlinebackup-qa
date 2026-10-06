using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Xml.Linq;
using OnlineBackup.Core;

namespace OnlineBackup.Server
{
    /// <summary>RST-010..060: restic's REST protocol (rest-server compatible) over the product's users, quotas, logs and alerts.</summary>
    public sealed partial class Api
    {
        /// <summary>Bytes stored per user since its statistics were last updated (cleared by UpdateStats).</summary>
        readonly Dictionary<string, long> resticSince = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, DateTime> quotaHit = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, List<DateTime>> resticFails = new Dictionary<string, List<DateTime>>();

        /// <summary>RST-010: /restic/&lt;login&gt;/&lt;set&gt;/[config | &lt;type&gt;/[&lt;name&gt;]] with HTTP Basic (login + the set's access token).</summary>
        void Restic(HttpListenerContext ctx, string[] seg, string ip)
        {
            var req = ctx.Request; var res = ctx.Response;
            try
            {
                if (seg.Length < 3) throw new ApiException(404, "NOT_FOUND", "not found");
                var login = Uri.UnescapeDataString(seg[1]); var setId = seg[2];
                ResticAuth(req, login, setId, ip);
                var prof = users.LoadProfile(login);
                var set = prof.FindSet(setId);
                if (set == null || BackupSetInfo.FromXml(set).Engine != "RESTIC") throw new ApiException(404, "NOT_FOUND", "not found");
                var store = new ResticStore(users.UserDir(login), setId);
                string type = seg.Length > 3 ? seg[3] : null, name = seg.Length > 4 ? seg[4] : null;
                var m = req.HttpMethod;

                if (type == null)
                {
                    if (m == "POST" && req.QueryString["create"] == "true") { store.Create(); ResticEnd(res, 200); return; }
                    if (m == "DELETE") throw new ApiException(403, "APPEND_ONLY", "the repository is never deleted by an agent");
                    throw new ApiException(400, "BAD", "bad request");
                }
                if (type == "config") name = null;
                else if (Array.IndexOf(ResticStore.Types, type) < 0) throw new ApiException(404, "NOT_FOUND", "not found");

                if (name == null && type != "config")
                {
                    if (m != "GET") throw new ApiException(405, "METHOD", "method");
                    bool v2 = (req.Headers["Accept"] ?? "").Contains("application/vnd.x.restic.rest.v2");
                    var json = Encoding.UTF8.GetBytes(store.List(type, v2));
                    res.StatusCode = 200;
                    res.ContentType = v2 ? "application/vnd.x.restic.rest.v2" : "application/vnd.x.restic.rest.v1";
                    res.ContentLength64 = json.Length; res.OutputStream.Write(json, 0, json.Length); res.Close();
                    return;
                }

                var path = store.FilePath(type, name);
                switch (m)
                {
                    case "HEAD":
                        if (!File.Exists(path)) throw new ApiException(404, "NOT_FOUND", "not found");
                        res.StatusCode = 200; res.ContentLength64 = new FileInfo(path).Length; res.Close();
                        return;
                    case "GET":
                        ResticGet(req, res, path);
                        return;
                    case "POST":
                        {
                            // RST-090: the quota counts the statistics of the last report plus what was stored since (a first
                            // backup of 100GB must stop at the quota, not after it). Quota and a full server disk both answer 507 (restic stops
                            // at once on 507; it retries other codes for minutes) — the agent then asks /api/quota which one it was.
                            long quota = prof.GetLong("QUOTA");
                            long since; lock (resticSince) resticSince.TryGetValue(login, out since);
                            long left = quota > 0 ? quota - Usage(prof) - since : -1;
                            long wrote;
                            try
                            {
                                LicenseStorageCheck();
                                if (quota > 0 && left <= 0) throw new ApiException(507, "QUOTA", "quota exceeded");
                                wrote = store.Save(type, name, req.InputStream, left);
                            }
                            catch (ApiException e) when (e.Code == "QUOTA") { lock (resticSince) quotaHit[login] = SystemClock.UtcNow; throw; }
                            catch (IOException e) when (DiskFull(e)) { DiskFullAlert(store.Dir, ip); throw new ApiException(507, "DISK_FULL", "the backup server's disk is full"); }
                            lock (resticSince) resticSince[login] = since + wrote;
                            ResticEnd(res, 200);
                            return;
                        }
                    case "DELETE":
                        store.Delete(type, name);
                        if (type == "snapshots") SysLog.Write(ip, "Access", "restic snapshot to trash " + login + "/" + setId + " " + name);
                        ResticEnd(res, 200);
                        return;
                }
                throw new ApiException(405, "METHOD", "method");
            }
            catch (ApiException e)
            {
                // RST-016: a HEAD answer carries no body — on Windows (http.sys) writing one throws and the request would
                // never be answered (restic's "HEAD config" before "init" timed out on the owner's server, 2.83.2)
                try
                {
                    if (e.Status == 401) res.AddHeader("WWW-Authenticate", "Basic realm=\"backup\"");
                    res.StatusCode = e.Status;
                    if (req.HttpMethod == "HEAD") { res.ContentLength64 = 0; res.Close(); }
                    else
                    {
                        var b = Encoding.UTF8.GetBytes(e.Message);
                        res.ContentType = "text/plain; charset=utf-8"; res.ContentLength64 = b.Length;
                        res.OutputStream.Write(b, 0, b.Length); res.Close();
                    }
                }
                catch { try { res.Abort(); } catch { } }
            }
            catch (Exception e)
            {
                SysLog.Write(ip, "System", "error: restic " + e.Message);
                try { res.StatusCode = 500; res.ContentLength64 = 0; res.Close(); } catch { try { res.Abort(); } catch { } }
            }
        }

        /// <summary>RST-095: the live quota of a user (statistics + stored since) for the agent's 507 diagnosis.</summary>
        Msg QuotaNow(string login)
        {
            var p = users.LoadProfile(login);
            long since; DateTime hit; bool refused;
            lock (resticSince) { resticSince.TryGetValue(login, out since); refused = quotaHit.TryGetValue(login, out hit) && (SystemClock.UtcNow - hit).TotalHours < 1; }
            return new Msg().Set("quota", p.GetLong("QUOTA")).Set("used", Usage(p) + since).Set("quotaRefused", refused ? 1 : 0)
                .Set("diskFull", (SystemClock.UtcNow - lastDiskFullAlert).TotalHours < 1 ? 1 : 0);
        }

        /// <summary>RST-080: ENOSPC (Linux 28) / ERROR_DISK_FULL (112) / ERROR_HANDLE_DISK_FULL (39).</summary>
        static bool DiskFull(IOException e)
        {
            int code = e.HResult & 0xFFFF;
            return code == 112 || code == 39 || code == 28 || e.Message.IndexOf("No space left", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        DateTime lastDiskFullAlert = DateTime.MinValue;
        void DiskFullAlert(string dir, string ip)
        {
            SysLog.Write(ip, "System", "error: disk full writing " + dir);
            lock (resticFails)
            {
                if ((SystemClock.UtcNow - lastDiskFullAlert).TotalHours < 1) return;
                lastDiskFullAlert = SystemClock.UtcNow;
            }
            mailer.Alert("✗ The backup server's disk is full", "<p>Backups are failing because the backup server's disk has no free space (" + Fmt.H(Path.GetPathRoot(dir)) + "). Existing backups are not affected. Free up space or add a disk (Auto User Home Allocation).</p>");
        }

        static void ResticEnd(HttpListenerResponse res, int status) { res.StatusCode = status; res.ContentLength64 = 0; res.Close(); }

        /// <summary>RST-015: Basic auth; 10 failures from one address in 10 minutes → refused for the rest of the window.</summary>
        void ResticAuth(HttpListenerRequest req, string login, string setId, string ip)
        {
            lock (resticFails)
            {
                List<DateTime> f;
                if (resticFails.TryGetValue(ip, out f)) { f.RemoveAll(t => (SystemClock.UtcNow - t).TotalMinutes > 10); if (f.Count >= 10) throw new ApiException(429, "LOCKED", "too many failures"); }
            }
            string user = null, pass = null;
            var h = req.Headers["Authorization"];
            if (h != null && h.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
                try { var s = Encoding.UTF8.GetString(Convert.FromBase64String(h.Substring(6).Trim())); int i = s.IndexOf(':'); if (i > 0) { user = s.Substring(0, i); pass = s.Substring(i + 1); } } catch (FormatException) { }
            bool ok = false;
            try
            {
                ok = user != null && string.Equals(user, login, StringComparison.OrdinalIgnoreCase)
                     && ResticStore.CheckToken(users.UserDir(login), setId, pass);
                if (ok)
                {
                    var p = users.LoadProfile(login);
                    if (p.Get("STATUS") != "ENABLE" || p.Get("DISABLED") == "Y") throw new ApiException(403, "SUSPENDED", "user suspended");
                    users.CheckIp(p, ip);   // SEC-030
                }
            }
            catch (ApiException e) when (e.Status != 403) { ok = false; }
            if (ok) return;
            lock (resticFails) { List<DateTime> f; if (!resticFails.TryGetValue(ip, out f)) resticFails[ip] = f = new List<DateTime>(); f.Add(SystemClock.UtcNow); }
            SysLog.Write(ip, "Access", "restic access refused " + login + "/" + setId);
            throw new ApiException(401, "AUTH", "unauthorized");
        }

        /// <summary>RST-020: the whole file, or "Range: bytes=a-b" → 206 (restic reads single blobs out of packs).</summary>
        static void ResticGet(HttpListenerRequest req, HttpListenerResponse res, string path)
        {
            if (!File.Exists(path)) throw new ApiException(404, "NOT_FOUND", "not found");
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            {
                long len = fs.Length, from = 0, to = len - 1;
                var range = req.Headers["Range"];
                if (!string.IsNullOrEmpty(range) && range.StartsWith("bytes="))
                {
                    var r = range.Substring(6).Split('-');
                    if (r[0].Length > 0) { from = long.Parse(r[0], CultureInfo.InvariantCulture); if (r.Length > 1 && r[1].Length > 0) to = Math.Min(len - 1, long.Parse(r[1], CultureInfo.InvariantCulture)); }
                    else if (r.Length > 1) { from = Math.Max(0, len - long.Parse(r[1], CultureInfo.InvariantCulture)); }
                    if (from > to || from >= len) { res.AddHeader("Content-Range", "bytes */" + len); throw new ApiException(416, "RANGE", "range"); }
                    res.StatusCode = 206;
                    res.AddHeader("Content-Range", "bytes " + from + "-" + to + "/" + len);
                }
                else res.StatusCode = 200;
                res.ContentType = "application/octet-stream";
                res.ContentLength64 = to - from + 1;
                fs.Position = from;
                var buf = new byte[1 << 16]; long left = to - from + 1;
                while (left > 0) { int n = fs.Read(buf, 0, (int)Math.Min(buf.Length, left)); if (n <= 0) break; res.OutputStream.Write(buf, 0, n); left -= n; }
                res.Close();
            }
        }

        /// <summary>RST-050: statistics of a restic set in the Ahsay attributes: data = repository, retain = trash.</summary>
        Msg ResticStats(string login, string setId, XElement e, Msg body)
        {
            var rs = new ResticStore(users.UserDir(login), setId);
            long trash = 0;
            var t = Path.Combine(rs.Dir, ".trash");
            if (Directory.Exists(t)) trash = Directory.EnumerateFiles(t, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
            long files = body != null && body["files"] != null ? body.Long("files") : long.Parse((string)e.Attribute("NO_OF_FILES") ?? "0", CultureInfo.InvariantCulture);
            long orig = body != null && body["orig"] != null ? body.Long("orig") : long.Parse((string)e.Attribute("TOTAL_UNCOMPRESS_FILE_SIZE") ?? "0", CultureInfo.InvariantCulture);
            return new Msg().Set("dataSize", rs.Size() - trash).Set("dataFiles", files).Set("dataOrig", orig)
                .Set("retainSize", trash).Set("retainFiles", 0).Set("retainOrig", 0);
        }

        /// <summary>RST-060: nightly — trash older than 14 days removed (never while retention is frozen) and a key-less scrub.</summary>
        Msg ResticMaintenance(string login, Profile p, BackupSetInfo s, DateTime nowUtc)
        {
            var rs = new ResticStore(users.UserDir(login), s.Id);
            var m = new Msg().Set("login", login).Set("set", s.Id).Set("engine", "RESTIC");
            if (!rs.Exists) return m;
            long freed = p.Get("RETENTION_FROZEN") == "Y" ? 0 : rs.PurgeTrash(nowUtc, ResticStore.TrashDays);
            var scrub = rs.Scrub(4L * 1024 * 1024 * 1024);
            var line = AhsayLog.Info(SystemClock.UtcNow, "Repository check: " + scrub["checked"] + " files verified by SHA-256, damaged " + scrub["bad"] + "; trash freed " + freed + " bytes");
            Atomic.AppendLine(Path.Combine(users.UserDir(login), "logs", s.Id, "Retention", nowUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log"), line);
            if (scrub.Int("bad") > 0)
                mailer.Alert("✗ Damaged data found — " + login, "<p>The nightly integrity check found " + scrub["bad"] + " damaged files in the repository of the backup set " + Fmt.H(s.Name) + ". They were quarantined. Run a backup and a check (restic check) on the customer's computer.</p>", VendorOf(login));
            return m.Set("checked", scrub["checked"]).Set("bad", scrub["bad"]).Set("freed", freed);
        }
    }
}
