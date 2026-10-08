using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// Bug 126 (found on Windows, runs 21/22: InterruptionTests.ServerDownMidBackupAndBack failed 4 of 4 with bug 119's change,
    /// passed 2 of 2 without it - qa-shards-23/24). Bug 119 asks "must this run stop?" also WHILE a file is sent. The question
    /// went to the server with the full retry policy of a real call: 4 tries, 1-4 s pauses, 300 s each. With the server gone
    /// (refused at once on Linux, slowly on Windows) or hung (accepting, never answering) every question held the upload for
    /// 10 s up to 20 minutes, and the agent took far longer to notice the server was gone. Oracle: the question is answered
    /// within seconds whatever the server does; an unanswered question is "go on" (the run's own calls report the outage),
    /// and a stop the server did set is still seen.
    /// </summary>
    public class StopProbeComponentTests
    {
        static void PointAt(AgentApp app, string url) { var h = app.Home; h.SaveRegistration(url, h.Login, h.Computer, h.DeviceToken, h.Tls, h.Pin); }

        static TimeSpan Time(Func<bool> check, out bool answer, int limitSeconds)
        {
            var sw = Stopwatch.StartNew();
            var t = Task.Run(check);
            Assert.True(t.Wait(TimeSpan.FromSeconds(limitSeconds)), "the stop question was not answered within " + limitSeconds + " s");
            answer = t.Result;
            return sw.Elapsed;
        }

        [Fact]
        public void ServerGone_TheQuestionIsAnsweredAtOnce_GoOn()
        {
            using (var env = new Env())
            {
                env.CreateUser("p126", "Customer-Pass-1");
                var app = env.Agent("p126", "Customer-Pass-1", name: "fs01");
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "Files", Sources = { env.Dir("src") } });
                var free = new TcpListener(IPAddress.Loopback, 0); free.Start(); var port = ((IPEndPoint)free.LocalEndpoint).Port; free.Stop();
                PointAt(app, "http://127.0.0.1:" + port + "/");                                  // nobody listens there
                bool answer; var took = Time(app.StopCheck(set, DateTime.UtcNow), out answer, 60);
                Assert.False(answer);
                Assert.True(took < TimeSpan.FromSeconds(3), "the stop question held the run " + took.TotalSeconds.ToString("0.0") + " s with the server gone");
            }
        }

        [Fact]
        public void ServerHung_TheQuestionIsAnsweredWithinItsLimit_GoOn()
        {
            using (var env = new Env())
            {
                env.CreateUser("h126", "Customer-Pass-1");
                var app = env.Agent("h126", "Customer-Pass-1", name: "fs01");
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "Files", Sources = { env.Dir("src") } });
                var hung = new TcpListener(IPAddress.Loopback, 0); hung.Start();                // accepts, never answers
                var held = new System.Collections.Concurrent.ConcurrentBag<TcpClient>();
                var accepting = Task.Run(() => { try { while (true) held.Add(hung.AcceptTcpClient()); } catch (Exception) { } });
                try
                {
                    PointAt(app, "http://127.0.0.1:" + ((IPEndPoint)hung.LocalEndpoint).Port + "/");
                    bool answer; var took = Time(app.StopCheck(set, DateTime.UtcNow), out answer, 90);
                    Assert.False(answer);
                    Assert.True(took < TimeSpan.FromSeconds(20), "the stop question held the run " + took.TotalSeconds.ToString("0.0") + " s with a hung server");
                }
                finally { hung.Stop(); foreach (var c in held) c.Dispose(); }
            }
        }

        [Fact]
        public void AStopSetOnTheServer_IsStillSeen_ByTheQuickQuestion()
        {
            using (var env = new Env())
            {
                env.CreateUser("s126", "Customer-Pass-1");
                var app = env.Agent("s126", "Customer-Pass-1", name: "fs01");
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "Files", Sources = { env.Dir("src") } });
                var started = DateTime.UtcNow.AddSeconds(-5);
                var check = app.StopCheck(set, started);
                Assert.False(check());
                env.Admin().Call("POST", "/api/admin/users/s126/sets/" + set.Id + "/stop", new Msg());
                Thread.Sleep(21000);                                                              // the question is asked at most every 20 s
                Assert.True(check());
            }
        }
    }
}
