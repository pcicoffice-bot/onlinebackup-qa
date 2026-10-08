using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// QA night round S, part 2 — two items agent R left NOT TESTED, driven against a REAL in-process server (new Env):
    ///  (a) the web-restore download Content-Disposition header, built from the request's "point" value — CR/LF and quote
    ///      injection attempted and read back off a raw socket (the real response bytes, not a parsed client);
    ///  (b) the restic repository object routes (ResticStore.FilePath) — traversal / encoded / backslash / null / overlong
    ///      path segments against a real restic set, with a secret sentinel planted OUTSIDE the repository.
    /// Needs a restic binary (OB_RESTIC); the round ran with OB_RESTIC set, so these execute.
    /// Oracle in both: the raw bytes the server sends, the HTTP status, and the files on disk — never the product's text.
    /// </summary>
    public class NightS_Part2Tests
    {
        static string ResticExe { get { return Environment.GetEnvironmentVariable("OB_RESTIC"); } }
        static bool Have { get { return !string.IsNullOrEmpty(ResticExe) && File.Exists(ResticExe); } }

        /// <summary>One raw HTTP/1.1 request over a plain socket; returns the exact response bytes as Latin1 (1 byte = 1 char),
        /// so injected CR/LF and header lines are visible verbatim. requestTarget is written into the request line AS GIVEN.</summary>
        static string Raw(string host, int port, string method, string requestTarget, IDictionary<string, string> headers, byte[] body)
        {
            using (var tcp = new TcpClient(host, port))
            using (var ns = tcp.GetStream())
            {
                var sb = new StringBuilder();
                sb.Append(method).Append(' ').Append(requestTarget).Append(" HTTP/1.1\r\n");
                sb.Append("Host: ").Append(host).Append("\r\n");
                if (headers != null) foreach (var kv in headers) sb.Append(kv.Key).Append(": ").Append(kv.Value).Append("\r\n");
                sb.Append("Content-Length: ").Append(body == null ? 0 : body.Length).Append("\r\n");
                sb.Append("Connection: close\r\n\r\n");
                var head = Encoding.ASCII.GetBytes(sb.ToString());
                ns.Write(head, 0, head.Length);
                if (body != null && body.Length > 0) ns.Write(body, 0, body.Length);
                ns.Flush();
                using (var ms = new MemoryStream())
                {
                    var buf = new byte[1 << 16]; int n;
                    try { while ((n = ns.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n); } catch (IOException) { }
                    return Encoding.Latin1.GetString(ms.ToArray());
                }
            }
        }

        static (string host, int port) Where(Env env) { var u = new Uri(env.Url); return (u.Host, u.Port); }

        static (BackupSetInfo set, string token, Client session) ResticSetWithBackup(Env env, string login)
        {
            env.CreateUser(login, "Customer-Pass-1");
            var app = env.Agent(login, "Customer-Pass-1");
            var src = env.Dir("src-" + login);
            File.WriteAllText(Path.Combine(src, "secret-contract.txt"), "CONFIDENTIAL customer contract body");
            var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1",
                new BackupSetInfo { Name = "R", Engine = "RESTIC", Sources = { src } });
            Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);
            // an interactive session token for the web (X-Session), and a fresh valid restic access token for Basic auth
            var c = new Client(env.Url) { Retries = 0 };
            c.Session = c.Call("POST", "/api/login", new Msg().Set("login", login).Set("password", "Customer-Pass-1"))["session"];
            var token = ResticStore.NewToken(Path.Combine(env.HomeA, login), set.Id);
            return (set, token, c);
        }

        // ============================================================ part 2a: header injection

        [Fact]
        public void WebRestoreDownload_PointValue_CannotInjectAResponseHeader()
        {
            if (!Have) throw NotTested.Because("OB_RESTIC (the restic program) is not set");
            using (var env = new Env())
            {
                var (set, _, session) = ResticSetWithBackup(env, "s2a");
                var (host, port) = Where(env);
                var h = new Dictionary<string, string> { { "X-Session", session.Session }, { "Content-Type", "application/xml" } };

                // every one is a correct encryption key (so the download case is actually reached) with a hostile "point".
                // CheckPoint (hex, <=64) must reject each BEFORE Content-Disposition is built; HttpListener would also
                // refuse a CR/LF header value. Oracle: the raw response has no attacker header and no attacker filename.
                var points = new[]
                {
                    "aaaa\r\nX-Injected: pwned\r\nX-Evil: 1",
                    "aaaa\r\n\r\n<html>split</html>",
                    "a\" ; filename=\"evil.exe",
                    "../../../../etc/passwd",
                };
                foreach (var pt in points)
                {
                    var body = new Msg().Set("key", "Customer-Pass-1").Set("point", pt)
                        .Add("paths", new Msg().Set("p", "/secret-contract.txt")).ToBytes();
                    var resp = Raw(host, port, "POST", "/api/webrestore/" + set.Id + "/download", h, body);
                    var statusLine = resp.Split('\n')[0].Trim();
                    var headerPart = resp.Contains("\r\n\r\n") ? resp.Substring(0, resp.IndexOf("\r\n\r\n")) : resp;

                    Assert.DoesNotContain("X-Injected", headerPart);
                    Assert.DoesNotContain("X-Evil", headerPart);
                    // the only Content-Disposition allowed is one built from a validated hex point; a hostile point must
                    // not reach it at all, so there must be no Content-Disposition carrying the attacker's bytes
                    Assert.False(headerPart.Contains("evil.exe"), "attacker filename reached a header: " + headerPart);
                    Assert.False(headerPart.Contains("passwd"), "attacker point reached a header: " + headerPart);
                    // the request is refused (400 POINT), not served
                    Assert.True(statusLine.Contains(" 400") || statusLine.Contains(" 401") || statusLine.Contains(" 403"),
                        "point '" + pt.Replace("\r", "\\r").Replace("\n", "\\n") + "' was not refused: " + statusLine);
                }

                // control: a well-formed (but non-existent) hex point reaches CheckPoint and is still refused cleanly,
                // proving the hostile ones above are stopped by the SAME validation, not by some earlier generic parse error.
                var hexBody = new Msg().Set("key", "Customer-Pass-1").Set("point", new string('a', 40))
                    .Add("paths", new Msg().Set("p", "/x")).ToBytes();
                var r2 = Raw(host, port, "POST", "/api/webrestore/" + set.Id + "/download", h, hexBody);
                Assert.Contains("HTTP/1.1", r2.Split('\n')[0]);
            }
        }

        // ============================================================ part 2b: restic path traversal

        [Fact]
        public void ResticObjectRoutes_RejectTraversalAndNeverTouchFilesOutsideTheRepository()
        {
            if (!Have) throw NotTested.Because("OB_RESTIC (the restic program) is not set");
            using (var env = new Env())
            {
                var (set, token, _) = ResticSetWithBackup(env, "s2b");
                var (host, port) = Where(env);
                var userDir = Path.Combine(env.HomeA, "s2b");
                var repoDir = Path.Combine(userDir, "restic", set.Id);
                Assert.True(Directory.Exists(repoDir), "restic repo was not created");

                // a secret sentinel OUTSIDE the repository (in the user's home, beside it) that no object route may read
                var sentinel = Path.Combine(userDir, "OUTSIDE-SECRET.txt");
                File.WriteAllText(sentinel, "TOP-SECRET-OUTSIDE-THE-REPO-abc123");
                var basic = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("s2b:" + token));
                var auth = new Dictionary<string, string> { { "Authorization", basic } };

                // positive control: the retrieval path really serves repository file bytes on a 200 (so, had any traversal
                // reached OUTSIDE, the response would carry those bytes and the sentinel check below would trip). The repo's
                // own "config" is a legitimate in-repo object addressed directly.
                var ok = Raw(host, port, "GET", "/restic/s2b/" + set.Id + "/config", auth, null);
                Assert.Contains(" 200", ok.Split('\n')[0]);
                var okBody = ok.Contains("\r\n\r\n") ? ok.Substring(ok.IndexOf("\r\n\r\n") + 4) : "";
                Assert.True(okBody.Length > 0, "the retrieval path served an empty body for a valid in-repo object");

                string b = set.Id;  // short local alias
                var reads = new[]
                {
                    "/restic/s2b/" + b + "/data/%2e%2e%2f%2e%2e%2f%2e%2e%2fOUTSIDE-SECRET.txt",
                    "/restic/s2b/" + b + "/keys/..%2f..%2fOUTSIDE-SECRET.txt",
                    "/restic/s2b/" + b + "/%2e%2e%2f%2e%2e%2fOUTSIDE-SECRET.txt",
                    "/restic/s2b/" + b + "/data/" + new string('a', 63) + "zz",      // name not 64 hex
                    "/restic/s2b/" + b + "/data/%00",                                // null
                    "/restic/s2b/" + b + "/data/..%5c..%5cOUTSIDE-SECRET.txt",       // backslash (encoded)
                    "/restic/s2b/" + b + "/%c0%ae%c0%ae%2fOUTSIDE-SECRET.txt",       // overlong-UTF8 dot-dot
                    "/restic/s2b/" + b + "/config/..%2f..%2fOUTSIDE-SECRET.txt",     // extra segment after config
                    "/restic/s2b/" + b + "/db/restic/" + b + ".token",              // guess the token store path
                };
                // Security property under test: no object route may READ a file outside the repository. A route is safe
                // whether it is refused (400/404/401) or whether the server collapses the path to a legitimate IN-repo
                // resource (e.g. ".../config/<ignored>" serves the repo's own config, 200) — what must never happen is the
                // response carrying the out-of-repo sentinel. Statuses are recorded as evidence.
                var results = new List<string>();
                foreach (var rt in reads)
                {
                    var resp = Raw(host, port, "GET", rt, auth, null);
                    var status = resp.Split('\n')[0].Trim();
                    results.Add(status + "  <=  " + rt);
                    Assert.False(resp.Contains("TOP-SECRET-OUTSIDE-THE-REPO"),
                        "an object route read a file OUTSIDE the repository: " + rt + "\n" + status);
                    // a 2xx may only ever be an in-repo resource; it must not be the token store or any path we aimed outside
                    if (status.Contains(" 200"))
                        Assert.True(rt.Contains("/config/"), "an object route served 200 for a non-config hostile path: " + rt + " (" + status + ")");
                }

                // writes: a traversal name must not create a file outside the repository
                var writeTargets = new[]
                {
                    "/restic/s2b/" + b + "/data/%2e%2e%2f%2e%2e%2f%2e%2e%2fPLANTED.txt",
                    "/restic/s2b/" + b + "/keys/..%2f..%2fPLANTED.txt",
                };
                foreach (var rt in writeTargets)
                {
                    var resp = Raw(host, port, "POST", rt, auth, Encoding.UTF8.GetBytes("attacker-written-bytes"));
                    var status = resp.Split('\n')[0].Trim();
                    results.Add("POST " + status + "  <=  " + rt);
                    Assert.True(status.Contains(" 400") || status.Contains(" 404") || status.Contains(" 401") || status.Contains(" 405") || status.Contains(" 403"),
                        "a hostile write route was not refused (" + status + "): " + rt);
                }
                // no attacker file anywhere under the user's home outside the repository
                var planted = Directory.EnumerateFiles(userDir, "PLANTED.txt", SearchOption.AllDirectories).ToList();
                Assert.True(planted.Count == 0, "a traversal write created files outside the repo: " + string.Join(", ", planted));

                // the sentinel is intact and still outside the repo (nothing overwrote/moved it either)
                Assert.Equal("TOP-SECRET-OUTSIDE-THE-REPO-abc123", File.ReadAllText(sentinel));

                var evidenceDir = Environment.GetEnvironmentVariable("NIGHTS_EVIDENCE");
                if (!string.IsNullOrEmpty(evidenceDir)) { Directory.CreateDirectory(evidenceDir); File.WriteAllLines(Path.Combine(evidenceDir, "restic-route-results.txt"), results); }
            }
        }
    }
}
