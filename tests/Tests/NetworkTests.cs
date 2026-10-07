using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// Network and upload — component contract (tests/QA/specs.py AG-05):
    ///   purpose  survive network trouble: a cut, a server that does not answer, a request whose answer is lost
    ///   input    a scripted server: the connection closed before an answer, a refusal (400), a server that never answers,
    ///            an upload whose connection is cut in the middle; then the real server behind a proxy that cuts every
    ///            connection once in the middle of a backup
    ///   expected a cut is sent again and the call succeeds; a refusal is not sent again; a server that never answers ends
    ///            at the time limit with a NETWORK error; a cut upload is a NETWORK error (never a raw exception); the real
    ///            backup completes despite the cut and every file restores identical (SHA-256), small and large alike
    /// </summary>
    public class NetworkTests
    {
        /// <summary>A server that answers each new connection with the next scripted action.</summary>
        sealed class Scripted : IDisposable
        {
            readonly TcpListener l = new TcpListener(IPAddress.Loopback, 0);
            readonly Queue<Action<TcpClient>> script;
            public int Connections;
            public string Url { get { return "http://127.0.0.1:" + ((IPEndPoint)l.LocalEndpoint).Port + "/"; } }
            public Scripted(params Action<TcpClient>[] steps)
            {
                script = new Queue<Action<TcpClient>>(steps); l.Start();
                new Thread(() =>
                {
                    while (true)
                    {
                        TcpClient c; try { c = l.AcceptTcpClient(); } catch (Exception) { return; }
                        Interlocked.Increment(ref Connections);
                        Action<TcpClient> step; lock (script) step = script.Count > 0 ? script.Dequeue() : Answer(200, new Msg().Set("ok", 1));
                        new Thread(() => { try { step(c); } catch (Exception) { } }) { IsBackground = true }.Start();
                    }
                }) { IsBackground = true }.Start();
            }
            public void Dispose() { l.Stop(); }
        }

        static void ReadHead(NetworkStream s)
        {
            var b = new List<byte>(); int x;
            while ((x = s.ReadByte()) >= 0) { b.Add((byte)x); if (b.Count >= 4 && b[b.Count - 1] == '\n' && b[b.Count - 2] == '\r' && b[b.Count - 3] == '\n' && b[b.Count - 4] == '\r') break; }
        }
        static Action<TcpClient> Answer(int status, Msg m)
        {
            return c =>
            {
                using (c) { var s = c.GetStream(); ReadHead(s); Thread.Sleep(50);
                    var body = m.ToBytes(); var head = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " X\r\nContent-Type: application/xml\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                    s.Write(head, 0, head.Length); s.Write(body, 0, body.Length); s.Flush(); Thread.Sleep(100); }
            };
        }
        static Action<TcpClient> CloseAtOnce() { return c => { var s = c.GetStream(); ReadHead(s); c.Client.LingerState = new LingerOption(true, 0); c.Close(); }; }
        static Action<TcpClient> NeverAnswer() { return c => { var s = c.GetStream(); ReadHead(s); Thread.Sleep(60000); c.Close(); }; }
        static Action<TcpClient> CutAfter(int bytes) { return c => { var s = c.GetStream(); ReadHead(s); var buf = new byte[bytes]; int got = 0, n; while (got < bytes && (n = s.Read(buf, got, bytes - got)) > 0) got += n; c.Client.LingerState = new LingerOption(true, 0); c.Close(); }; }

        [Fact]
        public void ACut_IsSentAgain_AndTheCallSucceeds()
        {
            using (var srv = new Scripted(CloseAtOnce(), Answer(200, new Msg().Set("value", "42"))))
            {
                var c = new Client(srv.Url) { Retries = 3 };
                Assert.Equal("42", c.Call("POST", "/api/x", new Msg().Set("a", 1))["value"]);
                Assert.Equal(2, srv.Connections);
            }
        }

        [Fact]
        public void ARefusal_IsNotSentAgain()
        {
            using (var srv = new Scripted(Answer(400, new Msg().Set("error", "BAD").Set("message", "no"))))
            {
                var e = Assert.Throws<AgentException>(() => new Client(srv.Url).Call("POST", "/api/x", new Msg()));
                Assert.Equal(400, e.Status); Assert.Equal("BAD", e.Code);
                Thread.Sleep(300);
                Assert.Equal(1, srv.Connections);
            }
        }

        [Fact]
        public void AServerThatNeverAnswers_EndsAtTheLimit_WithANetworkError()
        {
            using (var srv = new Scripted(NeverAnswer(), NeverAnswer()))
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var e = Assert.Throws<AgentException>(() => new Client(srv.Url) { Retries = 1, TimeoutMs = 1500 }.Call("GET", "/api/x"));
                Assert.Equal("NETWORK", e.Code);
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), "took " + sw.Elapsed);
            }
        }

        [Fact]
        public void AnUploadCutInTheMiddle_IsANetworkError_NotARawException()
        {
            using (var srv = new Scripted(CutAfter(64 * 1024)))
            {
                var data = new byte[4 * 1024 * 1024]; new Random(1).NextBytes(data);
                var e = Assert.Throws<AgentException>(() => new Client(srv.Url) { TimeoutMs = 10000 }.Put("/api/up", new Dictionary<string, string>(), s => { for (int i = 0; i < data.Length; i += 65536) s.Write(data, i, 65536); }));
                Assert.Equal("NETWORK", e.Code);
                // an error reading the source is the source's, not the network's
                using (var srv2 = new Scripted())
                {
                    var src = Assert.Throws<IOException>(() => new Client(srv2.Url).Put("/api/up", new Dictionary<string, string>(), s => { s.Write(data, 0, 10); throw new IOException("disk read error"); }));
                    Assert.Equal("disk read error", src.Message);
                }
            }
        }

        /// <summary>Forwards to the server; cuts every open connection once, after the given number of bytes went up.
        /// The server answers only its own host name and port, so "Host: localhost:proxy" is rewritten to the server's.</summary>
        sealed class CutProxy : IDisposable
        {
            readonly TcpListener l = new TcpListener(IPAddress.Loopback, 0);
            readonly int target; long up; readonly long cutAt; int cut;
            readonly List<TcpClient> open = new List<TcpClient>();
            public int Port { get { return ((IPEndPoint)l.LocalEndpoint).Port; } }
            public bool DidCut { get { return cut == 1; } }
            public CutProxy(int targetPort, long cutAtBytes)
            {
                target = targetPort; cutAt = cutAtBytes; l.Start();
                new Thread(() =>
                {
                    while (true)
                    {
                        TcpClient a; try { a = l.AcceptTcpClient(); } catch (Exception) { return; }
                        var b = new TcpClient();   // a late connection after the server is gone must not end the test process
                        try { b.Connect(IPAddress.Loopback, target); } catch (Exception) { try { a.Close(); b.Close(); } catch (Exception) { } continue; }
                        lock (open) { open.Add(a); open.Add(b); }
                        Pump(a, b, true); Pump(b, a, false);
                    }
                }) { IsBackground = true }.Start();
            }
            void Pump(TcpClient from, TcpClient to, bool upward)
            {
                new Thread(() =>
                {
                    var buf = new byte[16384]; int n;
                    try
                    {
                        var fs = from.GetStream(); var ts = to.GetStream();
                        while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                        {
                            if (upward && Interlocked.Add(ref up, n) > cutAt && Interlocked.Exchange(ref cut, 1) == 0)
                            {
                                lock (open) foreach (var c in open) try { c.Client.LingerState = new LingerOption(true, 0); c.Close(); } catch (Exception) { }
                                return;
                            }
                            if (upward) Rewrite(buf, n);
                            ts.Write(buf, 0, n);
                        }
                    }
                    catch (Exception) { }
                    try { to.Client.Shutdown(SocketShutdown.Send); } catch (Exception) { }
                }) { IsBackground = true }.Start();
            }
            void Rewrite(byte[] buf, int n)
            {
                var from = Encoding.ASCII.GetBytes("localhost:" + Port); var to = Encoding.ASCII.GetBytes("localhost:" + target);
                for (int i = 0; i + from.Length <= n; i++)
                {
                    int k = 0; while (k < from.Length && buf[i + k] == from[k]) k++;
                    if (k == from.Length) Buffer.BlockCopy(to, 0, buf, i, to.Length);
                }
            }
            public void Dispose() { l.Stop(); lock (open) foreach (var c in open) try { c.Close(); } catch (Exception) { } }
        }

        static string Sha(string f) { using (var s = File.OpenRead(f)) using (var h = SHA256.Create()) return Convert.ToHexString(h.ComputeHash(s)); }

        /// <summary>Integration: the real server and agent; the line is cut once in the middle of the upload.</summary>
        [Fact]
        public void RealBackup_TheLineIsCutOnceMidUpload_TheBackupCompletes_AndRestoresIdentical()
        {
            var before = BackupRun.UploadRetryDelay;
            try
            {
                BackupRun.UploadRetryDelay = a => 200;
                using (var env = new Env())
                {
                    var src = env.Dir("src");
                    for (int i = 0; i < 40; i++) File.WriteAllText(Path.Combine(src, "small" + i + ".txt"), "small file " + i);
                    for (int i = 0; i < 3; i++) { var b = new byte[3 * 1024 * 1024 + i]; new Random(i).NextBytes(b); File.WriteAllBytes(Path.Combine(src, "large" + i + ".bin"), b); }
                    env.CreateUser("net", "Customer-Pass-1");
                    var app = env.Agent("net", "Customer-Pass-1");
                    var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "Net", Sources = { src } });
                    var serverPort = new Uri(env.Url).Port;
                    using (var proxy = new CutProxy(serverPort, 4L * 1024 * 1024))
                    {
                        Assert.Equal(serverPort.ToString().Length, proxy.Port.ToString().Length);   // the rewrite keeps the length
                        var reg = app.Home;
                        reg.SaveRegistration("http://localhost:" + proxy.Port + "/", "net", reg.Computer, reg.DeviceToken);
                        var r = app.Backup(set.Id);
                        Assert.True(proxy.DidCut, "the proxy never cut the line");
                        Assert.True(r.Result == "BS_STOP_SUCCESS" || r.Result == "BS_STOP_SUCCESS_WITH_WARNING", r.Result + "\n" + string.Join("\n", r.LogLines));
                        Assert.Contains(r.LogLines, l => l.Contains("sending it again"));
                        reg.SaveRegistration(env.Url, "net", reg.Computer, reg.DeviceToken);   // the line is whole again
                    }
                    var target = env.Dir("restore");
                    app.RestoreFor(app.Interactive("Customer-Pass-1", null), set.Id).Run(null, target, null, false);
                    foreach (var f in Directory.GetFiles(src)) Assert.Equal(Sha(f), Sha(Path.Combine(target, Env.Rel(src), Path.GetFileName(f))));
                }
            }
            finally { BackupRun.UploadRetryDelay = before; }
        }
    }
}
