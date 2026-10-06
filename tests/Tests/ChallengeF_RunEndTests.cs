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
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// QA Agent F (static review challenger): the end of a run (Api.EndedReply, the sweep, Begin's expiry).
    /// Static-review hit 5e32a27303 (Api.cs EndedReply) was triaged OK: "a kept answer that cannot be read: the repeat
    /// still gets ok + repeat". The fallback answers "ok" to a commit whenever ANY log of the run exists — also when that
    /// log is the server's own "interrupted" record and nothing was ever committed (default-success).
    /// </summary>
    public class ChallengeF_RunEndTests
    {
        /// <summary>Forwards to the server; the first time a request for ".../commit" goes up, runs a hook before forwarding it
        /// (the computer slept / the line was down for a few minutes between its last upload and its commit).</summary>
        sealed class PauseBeforeCommitProxy : IDisposable
        {
            readonly TcpListener l = new TcpListener(IPAddress.Loopback, 0);
            readonly int target; readonly Action hook; int fired;
            readonly List<TcpClient> open = new List<TcpClient>();
            public int Port { get { return ((IPEndPoint)l.LocalEndpoint).Port; } }
            public bool Fired { get { return fired == 1; } }
            public PauseBeforeCommitProxy(int targetPort, Action hook)
            {
                target = targetPort; this.hook = hook; l.Start();
                new Thread(() =>
                {
                    while (true)
                    {
                        TcpClient a; try { a = l.AcceptTcpClient(); } catch (Exception) { return; }
                        var b = new TcpClient(); b.Connect(IPAddress.Loopback, target);
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
                            if (upward)
                            {
                                Rewrite(buf, n);
                                var text = Encoding.ASCII.GetString(buf, 0, n);
                                if (text.Contains("/commit HTTP/") && Interlocked.Exchange(ref fired, 1) == 0) hook();
                            }
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

        /// <summary>
        /// The real agent backs up a file; between its last upload and its commit the computer is silent for more than the
        /// lease (a laptop that went to sleep, a line that was down for 6 minutes). The server's minute sweep closes the run
        /// as interrupted (correct). When the computer wakes, its commit reaches the server: the run is gone, so the commit
        /// must fail — the agent must not record the files as stored. Found: EndedReply sees the "interrupted" log and
        /// answers { ok, repeat }; the agent reports BS_STOP_SUCCESS, saves its local state as if the files were stored, and
        /// the next backup sends nothing: the file is on no restore point (silent data loss).
        /// Oracle: the server's own restore point list and a real restore + SHA-256 — not the agent's result.
        /// </summary>
        [Theory]
        [InlineData(false)]   // control: no pause — must pass (shows the oracle can pass)
        [InlineData(true)]    // the computer is silent for 6 minutes before its commit
        public void CommitAfterTheSweepClosedTheRun_IsNotReportedAsSuccess(bool silentBeforeCommit) { Scenario(silentBeforeCommit, true); }

        /// <summary>The consequence alone (without the check of the agent's result): after the "successful" run, the next
        /// backup of the unchanged folder must still bring the file to the server.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void CommitAfterTheSweepClosedTheRun_TheFileIsOnTheServerAfterTheNextBackup(bool silentBeforeCommit) { Scenario(silentBeforeCommit, false); }

        static void Scenario(bool silentBeforeCommit, bool checkAgentResult)
        {
            using (var env = new Env())
            {
                var src = env.Dir("src");
                var data = new byte[200000]; new Random(7).NextBytes(data);
                File.WriteAllBytes(Path.Combine(src, "contract.pdf"), data);
                env.CreateUser("sleepy", "Customer-Pass-1");
                var app = env.Agent("sleepy", "Customer-Pass-1");
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "Sleepy", Sources = { src } });
                var serverPort = new Uri(env.Url).Port;
                BackupRun r;
                using (var proxy = new PauseBeforeCommitProxy(serverPort, () => { if (silentBeforeCommit) env.Api.SweepInterrupted(DateTime.UtcNow.AddMinutes(6)); }))
                {
                    Assert.Equal(serverPort.ToString().Length, proxy.Port.ToString().Length);
                    var reg = app.Home;
                    reg.SaveRegistration("http://localhost:" + proxy.Port + "/", "sleepy", reg.Computer, reg.DeviceToken);
                    r = app.Backup(set.Id);
                    Assert.True(proxy.Fired, "the proxy never saw the commit");
                    reg.SaveRegistration(env.Url, "sleepy", reg.Computer, reg.DeviceToken);
                }
                if (silentBeforeCommit)   // the sweep did its part: the run is in the history as interrupted
                    Assert.Contains(env.Api.Runs.Since(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1)), m => m["set"] == set.Id && m["job"] == r.Job && m["status"] == "bad");
                var points = app.RestoreFor(app.Interactive("Customer-Pass-1", null), set.Id).Points();
                var committed = points.Contains(r.Job);
                // the agent's result must agree with the server's records: success only if the run is a restore point
                if (checkAgentResult) Assert.False(r.Result.StartsWith("BS_STOP_SUCCESS", StringComparison.Ordinal) && !committed,
                    "the agent reported " + r.Result + " for run " + r.Job + " but the server has no such restore point (points: " + string.Join(",", points) + ")");

                if (checkAgentResult) return;
                // and the file must reach the server at the latest with the next backup (nothing changed on the computer)
                var r2 = app.Backup(set.Id);
                Assert.StartsWith("BS_STOP_SUCCESS", r2.Result);
                var target = env.Dir("restore");
                app.RestoreFor(app.Interactive("Customer-Pass-1", null), set.Id).Run(null, target, null, false);
                var restored = Path.Combine(target, Env.Rel(src), "contract.pdf");
                Assert.True(File.Exists(restored), "contract.pdf is on no restore point after two 'successful' backups");
                Assert.Equal(Sha(Path.Combine(src, "contract.pdf")), Sha(restored));
            }
        }
    }
}
