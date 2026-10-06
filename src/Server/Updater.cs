using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using OnlineBackup.Core;

namespace OnlineBackup.Server
{
    /// <summary>
    /// UPD-010 (owner): "Update" in the top bar of the management website — a new version without downloading and
    /// running the whole package again. The new version comes from the software vendor's portal (the same place as the
    /// licence), is accepted only when the vendor signed it (its SHA-256, with the licence key), and is installed by the
    /// new version's own installer in update mode: the service stops, the program files are replaced, the service starts
    /// again; the data, the settings and the customers' backups are not touched.
    /// </summary>
    public static class Updater
    {
        static readonly object gate = new object();
        static Dictionary<string, object> latest; static DateTime checkedAt;
        static string state = "idle", message = "";

        /// <summary>Tests: replaces starting the new version's installer (the update itself runs only on a real server).</summary>
        public static Action<string, string> Launch = (exe, log) =>
            Process.Start(new ProcessStartInfo(exe, "update --log \"" + log + "\"") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(exe) });

        /// <summary>This program's version (version.txt beside it, written by the package build).</summary>
        public static string CurrentVersion
        {
            get { try { var p = Path.Combine(AppContext.BaseDirectory, "version.txt"); return File.Exists(p) ? File.ReadAllText(p).Trim() : "0"; } catch (Exception) { return "0"; } }
        }

        public static bool Newer(string a, string b)
        {
            Version x, y;
            if (Version.TryParse(Norm(a), out x) && Version.TryParse(Norm(b), out y)) return x > y;
            return string.CompareOrdinal(a ?? "", b ?? "") > 0;
        }
        static string Norm(string v) { v = (v ?? "").Trim(); var parts = v.Split('.').Where(p => p.Length > 0).ToList(); while (parts.Count < 2) parts.Add("0"); return string.Join(".", parts.Take(4)); }

        /// <summary>Where updates come from: the portal the server was installed from, else the licensing centre of its licence; and the proof.</summary>
        static string Source(SystemConfig cfg, out Dictionary<string, object> proof)
        {
            var el = cfg.Doc.Root.Element("LICENSE");
            var text = el == null ? "" : (string)el.Attribute("KEY") ?? "";
            proof = new Dictionary<string, object> { { "serverId", cfg.ServerId }, { "license", text }, { "account", el == null ? "" : (string)el.Attribute("PORTAL_ACCOUNT") ?? "" }, { "token", el == null ? "" : (string)el.Attribute("PORTAL_TOKEN") ?? "" } };
            var src = el == null ? null : (string)el.Attribute("PORTAL_URL");
            if (string.IsNullOrEmpty(src)) src = License.Check(text, cfg.ServerId, SystemClock.UtcNow).Center;
            return string.IsNullOrEmpty(src) ? null : src.TrimEnd('/');
        }

        // ---------------------------------------------------------------- UPD-040: a private update store (a GitHub repository the owner keeps)
        // The owner's private repository holds latest.json {version, file, sha256} and the package; this server reads it with a
        // read-only key the owner pasted (kept encrypted on this server). Installs by itself at night when "automatic" is on.

        /// <summary>Tests: a stand-in for api.github.com.</summary>
        public static string GitHubApi = "https://api.github.com";
        static XElement SourceEl(SystemConfig cfg) { return cfg.Doc.Root.Element("UPDATE_SOURCE"); }
        public static string Repo(SystemConfig cfg) { var e = SourceEl(cfg); var r = e == null ? null : (string)e.Attribute("REPO"); return string.IsNullOrEmpty(r) ? null : r; }
        public static bool AutoOn(SystemConfig cfg) { var e = SourceEl(cfg); return e != null && (string)e.Attribute("AUTO") == "Y"; }
        static string Token(SystemConfig cfg)
        {
            var e = SourceEl(cfg); var v = e == null ? null : (string)e.Attribute("TOKEN_ENC");
            if (string.IsNullOrEmpty(v)) return null;
            try { return Encoding.UTF8.GetString(KeyVault.Unprotect(cfg.SystemHome, Convert.FromBase64String(v))); } catch (Exception) { return null; }
        }
        static bool GitHub(SystemConfig cfg) { return Repo(cfg) != null && Token(cfg) != null; }

        public static Msg SourceInfo(SystemConfig cfg) { return new Msg().Set("repo", Repo(cfg) ?? "").Set("hasToken", Token(cfg) != null ? 1 : 0).Set("auto", AutoOn(cfg) ? 1 : 0); }

        public static void SaveSource(SystemConfig cfg, string repo, string token, bool auto, string who, string ip)
        {
            repo = (repo ?? "").Trim().Trim('/');
            if (repo.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(repo, "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+(@[A-Za-z0-9_./-]+)?$")) throw new InvalidOperationException("Write the repository as owner/name, e.g. pcicoffice-bot/golan-backup-downloads");
            lock (cfg)
            {
                var e = SourceEl(cfg); if (e == null) { e = new XElement("UPDATE_SOURCE"); cfg.Doc.Root.Add(e); }
                e.SetAttributeValue("REPO", repo.Length == 0 ? null : repo);
                if (!string.IsNullOrEmpty(token)) e.SetAttributeValue("TOKEN_ENC", Convert.ToBase64String(KeyVault.Protect(cfg.SystemHome, Encoding.UTF8.GetBytes(token.Trim()))));
                e.SetAttributeValue("AUTO", auto ? "Y" : "N");
                cfg.Save();
            }
            lock (gate) latest = null;
            SysLog.Write(ip, "Update", who + " update source " + (repo.Length == 0 ? "removed" : repo) + (auto ? ", automatic at night" : ""));   // never the key itself
        }

        static HttpWebRequest GitHubGet(SystemConfig cfg, string path, int timeoutMs)
        {
            // "owner/name@branch": the files on that branch (e.g. the updates branch of a repository that holds other work too)
            var repo = Repo(cfg); var at = repo.IndexOf('@');
            var url = GitHubApi + "/repos/" + (at < 0 ? repo : repo.Substring(0, at)) + "/contents/" + path + (at < 0 ? "" : "?ref=" + Uri.EscapeDataString(repo.Substring(at + 1)));
            var r = (HttpWebRequest)WebRequest.Create(url);
            r.Headers["Authorization"] = "Bearer " + Token(cfg); r.Accept = "application/vnd.github.raw"; r.UserAgent = "OnlineBackup-Server";
            r.Timeout = timeoutMs; r.ReadWriteTimeout = timeoutMs;
            return r;
        }

        /// <summary>Night-time: installs a newer version by itself when the owner turned it on (02:00–05:00 server time).</summary>
        public static void Auto(SystemConfig cfg, DateTime localNow)
        {
            if (!AutoOn(cfg) || !GitHub(cfg) || localNow.Hour < 2 || localNow.Hour >= 5) return;
            var st = Status(cfg, true);
            if (true.Equals(st["available"])) Start(cfg, "automatic", "server");
        }

        static HttpWebRequest Post(string url, Dictionary<string, object> body, int timeoutMs)
        {
            var r = (HttpWebRequest)WebRequest.Create(url);
            r.Method = "POST"; r.ContentType = "application/json"; r.Timeout = timeoutMs; r.ReadWriteTimeout = timeoutMs;
            var b = Encoding.UTF8.GetBytes(Json.Write(body)); r.ContentLength = b.Length;
            using (var s = r.GetRequestStream()) s.Write(b, 0, b.Length);
            return r;
        }

        public static Dictionary<string, object> Status(SystemConfig cfg, bool check)
        {
            Dictionary<string, object> proof;
            var src = Source(cfg, out proof);
            var current = CurrentVersion;
            string error = null;
            bool gh = GitHub(cfg);
            lock (gate)
                if ((src != null || gh) && (check || latest == null || (SystemClock.UtcNow - checkedAt).TotalHours > 6))
                {
                    try
                    {
                        using (var w = (HttpWebResponse)(gh ? GitHubGet(cfg, "latest.json", 20000) : Post(src + "/portal/api/update/latest", proof, 20000)).GetResponse())
                        using (var rd = new StreamReader(w.GetResponseStream(), Encoding.UTF8)) latest = Json.Obj(Json.Parse(rd.ReadToEnd()));
                    }
                    catch (Exception e) { error = "The update service did not answer: " + e.Message; latest = null; }
                    checkedAt = SystemClock.UtcNow;
                }
            var lv = latest == null ? null : Json.Str(latest, "version");
            return new Dictionary<string, object> {
                { "current", current }, { "latest", lv ?? "" }, { "available", lv != null && Newer(lv, current) }, { "source", src != null || gh }, { "auto", AutoOn(cfg) },
                { "state", state }, { "message", error ?? message } };
        }

        /// <summary>Downloads the newest version, checks the vendor's signature and starts its installer in update mode.</summary>
        public static void Start(SystemConfig cfg, string who, string ip)
        {
            lock (gate)
            {
                if (state == "downloading" || state == "installing") return;
                state = "downloading"; message = "";
            }
            new Thread(() =>
            {
                try
                {
                    Dictionary<string, object> proof;
                    bool gh = GitHub(cfg);
                    var src = Source(cfg, out proof);
                    if (src == null && !gh) throw new InvalidOperationException("No update source is known for this server.");
                    var st = Status(cfg, true);
                    if (!true.Equals(st["available"])) throw new InvalidOperationException("No newer version.");
                    var info = latest;
                    var version = Json.Str(info, "version"); var sha = (Json.Str(info, "sha256") ?? "").ToLowerInvariant();
                    // the vendor's portal: only a version the vendor signed (the same key as the licences);
                    // the owner's own private store: the owner's key reads it, the SHA-256 in latest.json checks the file
                    if (!gh && !License.VerifyCenter(Encoding.UTF8.GetBytes("OBUPDATE|" + version + "|" + sha), Convert.FromBase64String(Json.Str(info, "signature") ?? "")))
                        throw new InvalidOperationException("The new version is not signed by the software vendor — not installed.");
                    if (gh && !System.Text.RegularExpressions.Regex.IsMatch(sha, "^[0-9a-f]{64}$")) throw new InvalidOperationException("latest.json has no SHA-256 — not installed.");
                    var dir = Path.Combine(cfg.SystemHome, "update"); Directory.CreateDirectory(dir);
                    var zip = Path.Combine(dir, "OnlineBackup-Server-" + Safe(version) + ".zip");
                    var file = Json.Str(info, "file") ?? ("OnlineBackup-Server-" + version + "-Windows.zip");
                    if (!System.Text.RegularExpressions.Regex.IsMatch(file, "^[A-Za-z0-9_.-]+$")) throw new InvalidOperationException("latest.json names a wrong file.");
                    using (var w = (HttpWebResponse)(gh ? GitHubGet(cfg, file, 30 * 60 * 1000) : Post(src + "/portal/api/update/package", proof, 30 * 60 * 1000)).GetResponse())
                    using (var rs = w.GetResponseStream()) using (var fs = File.Create(zip)) rs.CopyTo(fs, 1 << 20);
                    string got; using (var fs = File.OpenRead(zip)) using (var h = SHA256.Create()) got = Bytes.Hex(h.ComputeHash(fs));
                    if (got != sha) { File.Delete(zip); throw new InvalidOperationException("The downloaded version is damaged (its SHA-256 does not match) — not installed."); }
                    var unpack = Path.Combine(dir, Safe(version));
                    if (Directory.Exists(unpack)) Directory.Delete(unpack, true);
                    ZipFile.ExtractToDirectory(zip, unpack);
                    var exe = Path.Combine(unpack, "server", OperatingSystem.IsWindows() ? "OnlineBackup.Server.exe" : "OnlineBackup.Server");
                    if (!File.Exists(exe)) throw new InvalidOperationException("The new version has no server program.");
                    var log = Path.Combine(dir, "update-" + Safe(version) + ".log");
                    SysLog.Write(ip, "Update", who + " update " + CurrentVersion + " → " + version + " (signed, SHA-256 " + sha.Substring(0, 12) + "…)");
                    lock (gate) { state = "installing"; message = version; }
                    Launch(exe, log);   // the new installer stops this service, replaces the files and starts it again
                }
                catch (Exception e)
                {
                    lock (gate) { state = "failed"; message = e.Message; }
                    SysLog.Write(ip, "Update", "update failed: " + e.Message);
                }
            }) { IsBackground = true }.Start();
        }

        /// <summary>
        /// UPD-030 (owner: "everything through the Update button"): an update brought by the administrator — the parts of
        /// the update package chosen in the browser, in order — checked (a server package with its version) and installed
        /// like a downloaded one. Only from the server itself (the caller checks), never pushed from outside.
        /// </summary>
        public static string FromUpload(SystemConfig cfg, Stream body, long maxBytes, string who, string ip)
        {
            lock (gate)
            {
                if (state == "downloading" || state == "installing") throw new InvalidOperationException("An update is already running.");
                state = "downloading"; message = "";
            }
            try
            {
                var dir = Path.Combine(cfg.SystemHome, "update"); Directory.CreateDirectory(dir);
                var zip = Path.Combine(dir, "uploaded.zip");
                using (var fs = File.Create(zip))
                {
                    var buf = new byte[1 << 20]; int n; long total = 0;
                    while ((n = body.Read(buf, 0, buf.Length)) > 0) { total += n; if (total > maxBytes) throw new InvalidOperationException("The file is too large."); fs.Write(buf, 0, n); }
                }
                string sha; using (var fs = File.OpenRead(zip)) using (var h = SHA256.Create()) sha = Bytes.Hex(h.ComputeHash(fs));
                var unpack = Path.Combine(dir, "uploaded");
                if (Directory.Exists(unpack)) Directory.Delete(unpack, true);
                try { ZipFile.ExtractToDirectory(zip, unpack); }
                catch (Exception) { throw new InvalidOperationException("These are not the parts of an update package (or a part is missing or out of order)."); }
                var exe = Path.Combine(unpack, "server", OperatingSystem.IsWindows() ? "OnlineBackup.Server.exe" : "OnlineBackup.Server");
                var vf = Path.Combine(unpack, "server", "version.txt");
                if (!File.Exists(exe) || !File.Exists(vf)) throw new InvalidOperationException("This package has no server program.");
                var version = File.ReadAllText(vf).Trim();
                var log = Path.Combine(dir, "update-" + Safe(version) + ".log");
                SysLog.Write(ip, "Update", who + " update from files " + CurrentVersion + " → " + version + " (SHA-256 " + sha.Substring(0, 12) + "…)");
                lock (gate) { state = "installing"; message = version; }
                Launch(exe, log);
                return version;
            }
            catch (Exception e)
            {
                lock (gate) { state = "failed"; message = e.Message; }
                SysLog.Write(ip, "Update", "update from files failed: " + e.Message);
                throw;
            }
        }

        static string Safe(string v) { return new string((v ?? "").Where(c => char.IsLetterOrDigit(c) || c == '.' || c == '-').ToArray()); }

        /// <summary>Tests: back to the start.</summary>
        public static void Reset() { lock (gate) { latest = null; state = "idle"; message = ""; } }
    }
}
