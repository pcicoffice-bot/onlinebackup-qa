using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using OnlineBackup.Core;

namespace OnlineBackup.Server
{
    /// <summary>
    /// Off-site copy (the "1 off-site" of 3-2-1-1-0): every commit is replayed on a second server in the same order —
    /// the new objects are sent into the same staging paths, then the same journal of moves is applied there. Nothing is
    /// overwritten on the second server (append-only), and deletions of the retention area wait there for a delay of
    /// their own (default 14 days), so a mistake or an attack on the first server cannot erase the second copy at once.
    /// The second server is a normal server: agents can restore from it with the same users, devices and keys.
    /// </summary>
    public sealed class Replicator
    {
        readonly SystemConfig cfg;
        readonly Users users;
        readonly object gate = new object();
        Timer timer;
        string QueueDir { get { return Path.Combine(cfg.SystemHome, "replication", "queue"); } }

        public Replicator(SystemConfig cfg, Users users) { this.cfg = cfg; this.users = users; }

        XElement Conf { get { return cfg.Doc.Root.Element("REPLICATION"); } }
        public bool Enabled { get { return Conf != null && (string)Conf.Attribute("ENABLED") == "Y" && !string.IsNullOrEmpty((string)Conf.Attribute("URL")); } }

        public void Start() { timer = new Timer(_ => { try { RunOnce(); } catch (Exception e) { SysLog.Write(null, "System", "error: replication " + e.Message); } }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)); }
        public void Stop() { if (timer != null) timer.Dispose(); }

        public void Enqueue(Msg ev)
        {
            if (!Enabled) return;
            Directory.CreateDirectory(QueueDir);
            Atomic.WriteText(Path.Combine(QueueDir, SystemClock.UtcNow.Ticks.ToString("D20", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".xml"), ev.ToString());
        }

        public int Pending { get { return Directory.Exists(QueueDir) ? Directory.GetFiles(QueueDir, "*.xml").Length : 0; } }

        /// <summary>Sends the queue in order; stops at the first failure and retries it next time (order is never broken).</summary>
        public int RunOnce()
        {
            if (!Enabled || !Directory.Exists(QueueDir)) return 0;
            lock (gate)
            {
                int done = 0;
                foreach (var f in Directory.GetFiles(QueueDir, "*.xml").OrderBy(x => x, StringComparer.Ordinal))
                {
                    var ev = Msg.Parse(OnlineBackup.Core.Atomic.ReadAllText(f));
                    try { Send(ev); }
                    catch (Exception e)
                    {
                        SysLog.Write(null, "System", "replication waiting (" + ev["type"] + " " + ev["login"] + "): " + e.Message);
                        break;
                    }
                    File.Delete(f);
                    done++;
                }
                return done;
            }
        }

        // QA round Q (Q-F1, High): a customer that existed before replication was switched on (or was made with `adduser`)
        // had no "user" event: the second server answered "The user does not exist." to its first commit, the queue stops at
        // the first failure — so nothing more was ever copied, for ANY customer. Before a customer's first event of this
        // process the second server is asked to have the user (idempotent) and gets its settings (db).
        readonly HashSet<string> ensured = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void EnsureOnReplica(string login)
        {
            long quota = 0;
            try { long.TryParse(users.LoadProfile(login).Get("QUOTA") ?? "0", NumberStyles.Integer, CultureInfo.InvariantCulture, out quota); } catch (Exception) { }
            Call("POST", "/api/replica/user", new Msg().Set("login", login).Set("quota", quota));
            ensured.Add(login);
        }

        void Send(Msg ev)
        {
            var login = ev["login"];
            if (ev["type"] != "user" && !string.IsNullOrEmpty(login) && !ensured.Contains(login))
            {
                EnsureOnReplica(login);
                if (ev["type"] != "db") Send(new Msg().Set("type", "db").Set("login", login));
            }
            var userDir = users.UserDir(login);
            switch (ev["type"])
            {
                case "user":
                    Call("POST", "/api/replica/user", new Msg().Set("login", login).Set("quota", ev["quota"]));
                    ensured.Add(login);
                    break;
                case "db":
                    foreach (var f in Directory.GetFiles(Path.Combine(userDir, "db"), "*", SearchOption.AllDirectories).Where(x => !Atomic.IsTemp(x)))   // bug 35: by the file's name, never the path
                    {
                        var rel = f.Substring(userDir.Length + 1).Replace(Path.DirectorySeparatorChar, '/');
                        if (rel.StartsWith("db/keys/"))
                        {
                            // Saved keys are protected per machine (DPAPI): sent unprotected over TLS and protected again by the second server.
                            var tmp = Path.GetTempFileName();
                            try { File.WriteAllBytes(tmp, KeyVault.Unprotect(cfg.SystemHome, OnlineBackup.Core.Atomic.ReadAllBytes(f))); PutFile(login, rel, tmp, true, true); }
                            finally { File.Delete(tmp); }
                        }
                        else PutFile(login, rel, f, true);
                    }
                    break;
                case "log":
                    if (File.Exists(Path.Combine(userDir, ev["path"]))) PutFile(login, ev["path"], Path.Combine(userDir, ev["path"]), true);
                    break;
                case "commit":
                    {
                        var set = ev["set"];
                        var store = new SetStore(userDir, set);
                        foreach (var m in ev.List("moves"))
                        {
                            if (!m["src"].StartsWith("jobs/")) continue;
                            var now = store.Locate(m["dst"], ev["job"]);
                            if (now == null) continue;   // already removed by retention: the replica's move will skip it
                            PutFile(login, "files/" + set + "/" + m["src"], Path.Combine(store.Dir, now.Replace('/', Path.DirectorySeparatorChar)), false);
                        }
                        Call("POST", "/api/replica/commit", ev);
                        break;
                    }
                case "retention":
                    Call("POST", "/api/replica/retention", ev);
                    break;
            }
        }

        void PutFile(string login, string path, string file, bool overwrite, bool protect = false)
        {
            var url = (string)Conf.Attribute("URL");
            var r = (HttpWebRequest)WebRequest.Create(url.TrimEnd('/') + "/api/replica/file?login=" + Uri.EscapeDataString(login) + "&path=" + Uri.EscapeDataString(path) + (overwrite ? "&overwrite=1" : "") + (protect ? "&protect=1" : ""));
            r.Method = "PUT"; r.Headers["X-Replica-Token"] = Token(); r.AllowWriteStreamBuffering = false; r.SendChunked = true; r.Timeout = 600000;
            using (var s = r.GetRequestStream()) using (var fs = File.OpenRead(file)) fs.CopyTo(s);
            Check(r);
        }

        void Call(string method, string path, Msg body)
        {
            var url = (string)Conf.Attribute("URL");
            var r = (HttpWebRequest)WebRequest.Create(url.TrimEnd('/') + path);
            r.Method = method; r.Headers["X-Replica-Token"] = Token(); r.Timeout = 600000;
            var b = body.ToBytes();
            r.ContentLength = b.Length;
            using (var s = r.GetRequestStream()) s.Write(b, 0, b.Length);
            Check(r);
        }

        static void Check(HttpWebRequest r)
        {
            try { using (var resp = (HttpWebResponse)r.GetResponse()) { } }
            catch (WebException e)
            {
                var resp = e.Response as HttpWebResponse;
                string msg = e.Message;
                if (resp != null) using (var s = resp.GetResponseStream()) try { msg = Msg.Read(s)["message"] ?? msg; } catch { }
                throw new IOException(msg);
            }
        }

        string Token()
        {
            var enc = (string)Conf.Attribute("TOKEN_ENC");
            return string.IsNullOrEmpty(enc) ? "" : Encoding.UTF8.GetString(KeyVault.Unprotect(cfg.SystemHome, Convert.FromBase64String(enc)));
        }

        // ---------------------------------------------------------------- the second server's side

        public static bool IsReplica(SystemConfig cfg) { return cfg.Doc.Root.Element("REPLICA_RECEIVER") != null; }

        public static void CheckToken(SystemConfig cfg, string token)
        {
            var r = cfg.Doc.Root.Element("REPLICA_RECEIVER");
            if (r == null || string.IsNullOrEmpty(token) || Bytes.Hex(Bytes.Sha256(Encoding.UTF8.GetBytes(token))) != (string)r.Attribute("TOKEN_HASH"))
                throw new ApiException(401, "REPLICA", "Secondary server: wrong token.");
        }

        /// <summary>Deletions requested by the first server wait here until their date.</summary>
        public static void QueueDeletes(SystemConfig cfg, string login, string set, IEnumerable<string> folders)
        {
            var days = int.Parse((string)cfg.Doc.Root.Element("REPLICA_RECEIVER").Attribute("DELETE_DELAY_DAYS") ?? "14", CultureInfo.InvariantCulture);
            var p = Path.Combine(cfg.SystemHome, "replication", "pending-deletes.log");
            foreach (var f in folders)
                Atomic.AppendLine(p, RunId.From(SystemClock.UtcNow.AddDays(days)) + "\t" + login + "\t" + set + "\t" + f);
        }

        public static int RunDueDeletes(SystemConfig cfg, Users users, DateTime nowUtc)
        {
            var p = Path.Combine(cfg.SystemHome, "replication", "pending-deletes.log");
            if (!File.Exists(p)) return 0;
            var keep = new List<string>(); int done = 0;
            foreach (var line in OnlineBackup.Core.Atomic.ReadAllLines(p).Where(l => l.Length > 0))
            {
                var x = line.Split('\t');
                if (string.CompareOrdinal(x[0], RunId.From(nowUtc)) > 0) { keep.Add(line); continue; }
                try { new SetStore(users.UserDir(x[1]), x[2]).DeleteRunFolder(x[3]); done++; }
                catch (Exception e) { SysLog.Write(null, "System", "error: replica delete " + line + ": " + e.Message); keep.Add(line); }
            }
            Atomic.WriteText(p, string.Join("\r\n", keep) + (keep.Count > 0 ? "\r\n" : ""));
            return done;
        }
    }
}
