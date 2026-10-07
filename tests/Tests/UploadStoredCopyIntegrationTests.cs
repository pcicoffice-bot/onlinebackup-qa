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
    /// The upload's end-to-end check (Agent L, mutant M20 survived every test): after each object the server answers the
    /// SHA-256 of what it STORED; when that differs from what was sent the object is sent again, and after 3 differing
    /// answers the file is not counted as backed up. A proxy between the real agent and the real server changes one hex
    /// digit of that answer (the stored copy "differs").
    /// </summary>
    public class UploadStoredCopyIntegrationTests
    {
        /// <summary>Forwards to the server; in the answers, changes the first digit after n="sha256"> while flips remain.</summary>
        sealed class ShaFlipProxy : IDisposable
        {
            readonly TcpListener l = new TcpListener(IPAddress.Loopback, 0);
            readonly int target; int left; int flipped; int refused;
            readonly List<TcpClient> open = new List<TcpClient>();
            public int Port { get { return ((IPEndPoint)l.LocalEndpoint).Port; } }
            public int Flipped { get { return flipped; } }
            public int Refused { get { return refused; } }
            public ShaFlipProxy(int targetPort, int flips)
            {
                target = targetPort; left = flips; l.Start();
                new Thread(() =>
                {
                    while (true)
                    {
                        TcpClient a; try { a = l.AcceptTcpClient(); } catch (Exception) { return; }
                        // a late connection after the server is gone must not throw on this thread: an unhandled exception
                        // in a background thread ends the whole test process (CI gate run 37557981765: aborted after 7 tests)
                        var b = new TcpClient();
                        try { b.Connect(IPAddress.Loopback, target); }
                        catch (Exception) { Interlocked.Increment(ref refused); try { a.Close(); b.Close(); } catch (Exception) { } continue; }
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
                            if (upward) Replace(buf, n, "localhost:" + Port, "localhost:" + target); else Flip(buf, n);
                            ts.Write(buf, 0, n);
                        }
                    }
                    catch (Exception) { }
                    try { to.Client.Shutdown(SocketShutdown.Send); } catch (Exception) { }
                }) { IsBackground = true }.Start();
            }
            static void Replace(byte[] buf, int n, string a, string b)
            {
                var from = Encoding.ASCII.GetBytes(a); var to = Encoding.ASCII.GetBytes(b);
                for (int i = 0; i + from.Length <= n; i++)
                {
                    int k = 0; while (k < from.Length && buf[i + k] == from[k]) k++;
                    if (k == from.Length) Buffer.BlockCopy(to, 0, buf, i, to.Length);
                }
            }
            void Flip(byte[] buf, int n)
            {
                var mark = Encoding.ASCII.GetBytes("n=\"sha256\">");
                for (int i = 0; i + mark.Length < n; i++)
                {
                    int k = 0; while (k < mark.Length && buf[i + k] == mark[k]) k++;
                    if (k < mark.Length) continue;
                    if (Interlocked.Decrement(ref left) < 0) return;
                    var j = i + mark.Length; buf[j] = (byte)(buf[j] == (byte)'0' ? '1' : '0');   // same length: Content-Length still right
                    Interlocked.Increment(ref flipped);
                }
            }
            public void Dispose() { l.Stop(); lock (open) foreach (var c in open) try { c.Close(); } catch (Exception) { } }
        }

        static string Sha(string f) { using (var s = File.OpenRead(f)) using (var h = SHA256.Create()) return Convert.ToHexString(h.ComputeHash(s)); }

        static void Run(int flips, Action<Env, AgentApp, BackupSetInfo, string, BackupRun, ShaFlipProxy> check)
        {
            using (var env = new Env())
            {
                var src = env.Dir("src");
                var b = new byte[300 * 1024]; new Random(5).NextBytes(b); File.WriteAllBytes(Path.Combine(src, "one.bin"), b);
                env.CreateUser("upck", "Customer-Pass-1");
                var app = env.Agent("upck", "Customer-Pass-1");
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "U", Sources = { src } });
                var serverPort = new Uri(env.Url).Port;
                using (var proxy = new ShaFlipProxy(serverPort, flips))
                {
                    Assert.Equal(serverPort.ToString().Length, proxy.Port.ToString().Length);   // the host rewrite keeps the length
                    var reg = app.Home;
                    reg.SaveRegistration("http://localhost:" + proxy.Port + "/", "upck", reg.Computer, reg.DeviceToken);
                    var r = app.Backup(set.Id);
                    reg.SaveRegistration(env.Url, "upck", reg.Computer, reg.DeviceToken);
                    check(env, app, set, src, r, proxy);
                }
            }
        }

        [Fact]
        public void TheServerOnceAnswersADifferentStoredCopy_TheObjectIsSentAgain_TheBackupRestoresIdentical()
        {
            Run(1, (env, app, set, src, r, proxy) =>
            {
                Assert.Equal(1, proxy.Flipped);                                            // the fault really happened
                Assert.Contains(r.LogLines, l => l.Contains("Upload check mismatch, sending again"));
                Assert.True(r.Result == "BS_STOP_SUCCESS" || r.Result == "BS_STOP_SUCCESS_WITH_WARNING", r.Result + "\n" + string.Join("\n", r.LogLines));
                var target = env.Dir("restore");
                app.RestoreFor(app.Interactive("Customer-Pass-1", null), set.Id).Run(null, target, null, false);
                Assert.Equal(Sha(Path.Combine(src, "one.bin")), Sha(Path.Combine(target, Env.Rel(src), "one.bin")));
            });
        }

        [Fact]
        public void TheServerAlwaysAnswersADifferentStoredCopy_TheFileIsNotCountedAsBackedUp()
        {
            Run(1000, (env, app, set, src, r, proxy) =>
            {
                Assert.True(proxy.Flipped >= 3, "flipped " + proxy.Flipped);              // the fault really happened, every attempt
                Assert.NotEqual("BS_STOP_SUCCESS", r.Result);
                Assert.NotEqual("BS_STOP_SUCCESS_WITH_WARNING", r.Result);
                Assert.Contains(r.LogLines, l => l.Contains("different copy than was sent"));
            });
        }
        [Fact]
        public void TheProxy_AConnectionAfterTheServerIsGone_DoesNotEndTheTestProcess()
        {
            var gone = new TcpListener(IPAddress.Loopback, 0); gone.Start(); var port = ((IPEndPoint)gone.LocalEndpoint).Port; gone.Stop();
            using (var proxy = new ShaFlipProxy(port, 0))
            {
                for (int i = 0; i < 2; i++) using (var c = new TcpClient()) { c.Connect(IPAddress.Loopback, proxy.Port); Thread.Sleep(300); }
                var until = DateTime.UtcNow.AddSeconds(5);
                while (proxy.Refused < 2 && DateTime.UtcNow < until) Thread.Sleep(50);
                Assert.Equal(2, proxy.Refused);                                             // the second one was still accepted
            }
        }
    }
}
