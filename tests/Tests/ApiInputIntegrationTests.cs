using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// AU-07 with the real server program as its own process (OnlineBackup.Server init + run), over real HTTP: a request
    /// whose message is deeply nested must be refused like any junk — and the server must still be there afterwards.
    /// The server runs with 1 MB thread stacks (DOTNET_DefaultStackSize), the size its request threads have on Windows,
    /// where it runs as a service; Linux gives 8 MB, which only moves the depth.
    ///   input    POST /api/login (no sign-in needed) with a message of 10 nested lists, then of 6 000 (about 130 KB)
    ///   expected each is answered with a 4xx, and GET /api/brand answers 200 after each (the process is alive)
    /// </summary>
    public class ApiInputIntegrationTests : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "obdeep-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Process server;
        public void Dispose()
        {
            try { if (server != null && !server.HasExited) { server.Kill(true); server.WaitForExit(); } } catch (Exception) { }
            try { Directory.Delete(root, true); } catch (Exception) { }
        }

        ProcessStartInfo Server(string licenceKey, params string[] args)
        {
            var bin = AppContext.BaseDirectory;
            var psi = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "exec", "--runtimeconfig", Path.Combine(bin, "OnlineBackup.Server.runtimeconfig.json"), Path.Combine(bin, "OnlineBackup.Server.dll") }) psi.ArgumentList.Add(a);
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["OB_LICENSE_PUBKEY"] = licenceKey;
            psi.Environment["DOTNET_DefaultStackSize"] = "100000";   // hex: 1 MB, as on Windows
            return psi;
        }

        static string Nested(int depth)
        {
            var sb = new StringBuilder("<m><f n=\"login\">x</f>");
            for (int i = 0; i < depth; i++) sb.Append("<l n=\"a\"><i>");
            for (int i = 0; i < depth; i++) sb.Append("</i></l>");
            return sb.Append("</m>").ToString();
        }

        [Fact]
        public void ADeeplyNestedMessage_IsRefused_AndTheServerStaysUp()
        {
            var sys = Path.Combine(root, "system"); var users = Path.Combine(root, "users");
            var licenceKey = Env.TestKey[1];   // the test servers' licence key (a real server program, as in Env)
            var init = ProcessRunner.Run(Server(licenceKey, "init", "--system-home", sys, "--admin", "admin", "--password", "Admin-Pass-1", "--host", "localhost", "--user-home", users + "|UNLIMITED"), TimeSpan.FromMinutes(2));
            Assert.True(init.Code == 0, init.Out + init.Err);
            var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); int port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
            var url = "http://localhost:" + port + "/";
            var output = new StringBuilder();
            server = new Process { StartInfo = Server(licenceKey, "run", "--system-home", sys, "--prefix", url) };
            server.OutputDataReceived += (s, e) => { lock (output) output.AppendLine(e.Data); };
            server.ErrorDataReceived += (s, e) => { lock (output) output.AppendLine(e.Data); };
            server.Start(); server.BeginOutputReadLine(); server.BeginErrorReadLine();
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) })
            {
                Func<bool> alive = () => { try { return !server.HasExited && http.GetAsync(url + "api/brand").GetAwaiter().GetResult().StatusCode == HttpStatusCode.OK; } catch (Exception) { return false; } };
                var until = DateTime.UtcNow.AddSeconds(60);
                while (!alive() && DateTime.UtcNow < until) System.Threading.Thread.Sleep(300);
                Assert.True(alive(), "the server did not start: " + output);

                foreach (var depth in new[] { 10, 6000 })
                {
                    int status;
                    try { status = (int)http.PostAsync(url + "api/login", new StringContent(Nested(depth), Encoding.UTF8, "application/xml")).GetAwaiter().GetResult().StatusCode; }
                    catch (Exception e) { status = -1; output.AppendLine("request: " + e.GetType().Name + " " + e.Message); }
                    System.Threading.Thread.Sleep(500);
                    string tail; lock (output) tail = output.ToString(); if (tail.Length > 600) tail = tail.Substring(0, 600);
                    Assert.True(alive(), "depth " + depth + ": the server process is gone (exit " + (server.HasExited ? server.ExitCode.ToString() : "-") + "): " + tail);
                    Assert.InRange(status, 400, 499);
                }
            }
        }
    }
}
