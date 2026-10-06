using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using OnlineBackup.Agent;
using OnlineBackup.Core;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// A stand-in for the backup server, for component tests of the agent: it speaks the agent's protocol (begin, object,
    /// delete, commit, abort, progress, interrupted, files, quota, profile), RECORDS every request with its body, and
    /// answers what the test tells it to. It is not the product's server (no SetStore, no Api), so a test that uses it
    /// tests the agent's unit alone; the oracle is what reached this recorder, and the files restored from the objects
    /// it received (SHA-256 against the source).
    /// </summary>
    public sealed class StubServer : IDisposable
    {
        public sealed class Req
        {
            public string Method, Path, Enc; public System.Collections.Specialized.NameValueCollection Query; public byte[] Body; public DateTime At; public int Answered;
            public Msg Msg { get { try { return Msg.Parse(Body); } catch (Exception) { return new Msg(); } } }
            public string Action { get { var s = Path.Split('/'); return s[s.Length - 1]; } }
            public string Job { get { var s = Path.Split('/'); var i = Array.IndexOf(s, "jobs"); return i >= 0 && i + 1 < s.Length ? s[i + 1] : null; } }
        }
        public sealed class Answer
        {
            public int Status = 200; public Msg Body = new Msg().Set("ok", 1); public int DelayMs; public bool Drop;
            public static Answer Ok(Msg m) { return new Answer { Body = m }; }
            public static Answer Error(int status, string code, string message) { return new Answer { Status = status, Body = new Msg().Set("error", code).Set("message", message) }; }
        }

        readonly HttpListener listener = new HttpListener();
        readonly List<Req> requests = new List<Req>();
        public string Url { get; private set; }
        /// <summary>The test's own answer to a request (null = the default answer below).</summary>
        public Func<Req, Answer> Handler = r => null;
        /// <summary>Called when a request arrives, before it is answered (e.g. to look at the computer's files at that moment).</summary>
        public Action<Req> OnRequest = r => { };
        public Msg Files = new Msg();
        public Msg Quota = new Msg();
        public string ProfileXml;
        int jobs;
        DateTime jobClock = new DateTime(2026, 10, 6, 10, 0, 0, DateTimeKind.Utc);

        public StubServer()
        {
            var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); int port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
            Url = "http://localhost:" + port + "/";
            listener.Prefixes.Add(Url); listener.Start();
            new Thread(Loop) { IsBackground = true }.Start();
        }

        public List<Req> Requests { get { lock (requests) return requests.ToList(); } }
        public List<Req> Of(string action) { return Requests.Where(r => r.Action == action).ToList(); }

        void Loop()
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = listener.GetContext(); } catch (Exception) { return; }
                new Thread(() => Handle(ctx)) { IsBackground = true }.Start();
            }
        }

        void Handle(HttpListenerContext ctx)
        {
            try
            {
                var ms = new MemoryStream(); ctx.Request.InputStream.CopyTo(ms);
                var r = new Req { Method = ctx.Request.HttpMethod, Path = ctx.Request.Url.AbsolutePath.TrimEnd('/'), Query = ctx.Request.QueryString, Enc = ctx.Request.Headers["X-Enc"], Body = ms.ToArray(), At = DateTime.UtcNow };
                lock (requests) requests.Add(r);
                OnRequest(r);
                var a = Handler(r) ?? Default(r);
                r.Answered = a.Drop ? -1 : a.Status;
                if (a.DelayMs > 0) Thread.Sleep(a.DelayMs);
                // the line breaks while the answer arrives (HttpListener's Abort alone still sends "200 OK" here): a promised body cut short
                if (a.Drop) { ctx.Response.StatusCode = 200; ctx.Response.ContentLength64 = 100; ctx.Response.OutputStream.Write(new byte[] { 60, 109, 62 }, 0, 3); ctx.Response.OutputStream.Flush(); ctx.Response.Abort(); return; }
                var b = a.Body.ToBytes();
                ctx.Response.StatusCode = a.Status; ctx.Response.ContentType = "application/xml; charset=utf-8"; ctx.Response.ContentLength64 = b.Length;
                ctx.Response.OutputStream.Write(b, 0, b.Length); ctx.Response.OutputStream.Close();
            }
            catch (Exception) { try { ctx.Response.Abort(); } catch (Exception) { } }
        }

        Answer Default(Req r)
        {
            switch (r.Action)
            {
                case "begin": lock (requests) { jobs++; return Answer.Ok(new Msg().Set("job", RunId.From(jobClock.AddMinutes(jobs)))); }
                case "object": using (var h = SHA256.Create()) return Answer.Ok(new Msg().Set("sha256", Bytes.Hex(h.ComputeHash(r.Body))).Set("size", r.Body.Length));
                case "files": return Answer.Ok(Files);
                case "quota": return Answer.Ok(Quota);
                case "profile": return ProfileXml == null ? Answer.Error(404, "NOT_FOUND", "Not found.") : Answer.Ok(new Msg().Set("profile", ProfileXml));
                default: return Answer.Ok(new Msg().Set("ok", 1));
            }
        }

        /// <summary>The runs this server recorded as ended by a commit (the default answer to a commit is "ok").</summary>
        public List<string> CommittedJobs { get { return Of("commit").Where(c => c.Answered == 200).Select(c => c.Job).ToList(); } }

        /// <summary>
        /// The newest point as a real server would hold it after the committed runs: for every file the last object sent in a
        /// committed run (a well-formed object only), minus the files a committed run deleted.
        /// </summary>
        public IRestoreSource Point(KeySet key)
        {
            var committed = new HashSet<string>(CommittedJobs);
            var src = new RecordedSource();
            var current = new Dictionary<string, List<Req>>();   // per file: its full copy and the deltas after it
            foreach (var r in Requests)
            {
                if (r.Job == null || !committed.Contains(r.Job)) continue;
                if (r.Action == "object" && r.Method == "PUT" && r.Answered == 200 && WellFormed(r.Body, key))   // what the server stored: accepted and whole
                {
                    var rel = r.Query["rel"];
                    if (r.Query["seq"] == "0" || !current.ContainsKey(rel)) current[rel] = new List<Req>();
                    current[rel].RemoveAll(x => x.Query["seq"] == r.Query["seq"]);
                    current[rel].Add(r);
                }
                if (r.Action == "delete") foreach (var d in r.Msg.List("rels")) current.Remove(d["rel"]);
            }
            foreach (var kv in current)
            {
                var last = kv.Value[kv.Value.Count - 1];
                var f = new Msg().Set("rel", kv.Key).Set("enc", last.Enc).Set("orig", last.Query["orig"]).Set("mtime", last.Query["mtime"]);
                foreach (var o in kv.Value)
                {
                    var loc = "Current/" + src.Objects.Count; src.Objects[loc] = o.Body;
                    string sha; using (var h = SHA256.Create()) sha = Bytes.Hex(h.ComputeHash(o.Body));
                    f.Add("objects", new Msg().Set("loc", loc).Set("seq", o.Query["seq"]).Set("kind", o.Query["kind"]).Set("size", o.Body.Length).Set("sha", sha));
                }
                src.Point.Add("files", f);
            }
            return src;
        }

        /// <summary>The newest point's file list as the server's GET /files gives it (what a lost local index is rebuilt from).</summary>
        public Msg PointFiles(KeySet key) { return Point(key).Files(null); }

        public static bool WellFormed(byte[] obj, KeySet key)
        {
            try { using (var s = new MemoryStream(obj)) { var h = BackupObject.ReadHeader(s, key); return h != null; } } catch (Exception) { return false; }
        }

        sealed class RecordedSource : IRestoreSource
        {
            public readonly Dictionary<string, byte[]> Objects = new Dictionary<string, byte[]>();
            public readonly Msg Point = new Msg();
            public List<string> Points() { return new List<string> { "2026-10-06-10-00-00" }; }
            public Msg Files(string point) { return Point; }
            public void Fetch(string loc, string toFile) { File.WriteAllBytes(toFile, Objects[loc]); }
            public void Report(Msg log) { }
        }

        public void Dispose() { try { listener.Stop(); listener.Close(); } catch (Exception) { } }
    }

    /// <summary>One computer for agent component tests: a source folder, the agent's folder, one set, its key, a stand-in server.</summary>
    public sealed class AgentRig : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "obrig-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        public readonly StubServer Server = new StubServer();
        public readonly KeySet Key = KeySet.Random();
        public readonly AgentHome Home;
        public readonly BackupSetInfo Set;
        public string Src { get { return Path.Combine(Root, "src"); } }
        public AgentRig(params string[] moreSources)
        {
            Directory.CreateDirectory(Src);
            Home = new AgentHome(Path.Combine(Root, "agent"));
            Set = new BackupSetInfo { Id = "1700000000123", Name = "Rig", Vss = false };
            Set.Sources.Add(Src); foreach (var m in moreSources) Set.Sources.Add(m);
        }
        public Client NewClient() { return new Client(Server.Url) { Device = "device-of-the-test", Retries = 0 }; }
        public BackupRun Backup(Func<bool> stop = null) { var r = new BackupRun(NewClient(), Home, Set, Key) { StopRequested = stop }; r.Run(); return r; }
        public string File(string rel, string text) { var p = Path.Combine(Src, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)); System.IO.File.WriteAllText(p, text); return p; }
        public string File(string rel, byte[] data) { var p = Path.Combine(Src, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)); System.IO.File.WriteAllBytes(p, data); return p; }
        public string StateFile { get { return Path.Combine(Home.Dir, "sets", Set.Id, "state.txt"); } }
        public string MarkerFile { get { return Path.Combine(Home.Dir, "sets", Set.Id, "open-run.txt"); } }
        public static string Sha(string f) { using (var s = System.IO.File.OpenRead(f)) using (var h = SHA256.Create()) return Bytes.Hex(h.ComputeHash(s)); }
        /// <summary>SHA-256 of every file under a folder, by path relative to it.</summary>
        public static Dictionary<string, string> Tree(string dir)
        {
            return Directory.Exists(dir) ? Directory.GetFiles(dir, "*", SearchOption.AllDirectories).ToDictionary(f => Path.GetRelativePath(dir, f), Sha) : new Dictionary<string, string>();
        }
        /// <summary>Restores the newest point the stand-in server holds into a new folder; returns SHA-256 by path relative to the source.</summary>
        public Dictionary<string, string> Restored(string name = "restore")
        {
            var target = Path.Combine(Root, name);
            var r = new Restore(Server.Point(Key), Key, Path.Combine(Root, "rtmp")); r.Run(null, target, null, false);
            return Tree(Path.Combine(target, Src.TrimStart('/')));
        }
        public void Dispose() { Server.Dispose(); try { Directory.Delete(Root, true); } catch (Exception) { } }
    }
}
