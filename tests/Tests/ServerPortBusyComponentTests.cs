using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using OnlineBackup.Server;
using Xunit;
using Xunit.Abstractions;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// Night soak: after the container killed the server, its first start failed with only "error: Value cannot be null."
    /// (the address was still taken). The reason — the port is in use — must be what the server says, not an error of
    /// its own clean-up. Oracle: the exception of `using (var api = new Api(cfg)) api.Start(prefix)` (Program.Run's code)
    /// with the prefix's port held by another listener.
    /// </summary>
    public class ServerPortBusyComponentTests
    {
        readonly ITestOutputHelper output;
        public ServerPortBusyComponentTests(ITestOutputHelper output) { this.output = output; }

        [Fact]
        public void APortInUse_IsWhatTheServerSays()
        {
            using (var env = new Env())
            {
                // another program holds the port (a plain socket, as a server process that has not let go of it yet)
                var busy = TcpListener.Create(0); busy.Start(); var port = ((IPEndPoint)busy.LocalEndpoint).Port;   // Q32: IPv4 AND IPv6 (on the CI runner the server binds IPv6; an IPv4-only holder left its port free)
                try
                {
                    var cfg = env.Cfg;
                    var e = Record.Exception(() => { using (var api = new Api(cfg)) api.Start("http://localhost:" + port + "/"); });
                    Assert.NotNull(e);
                    output.WriteLine(e.ToString());
                    Assert.DoesNotContain("Value cannot be null", e.Message);
                    Assert.True(e is HttpListenerException || e.Message.IndexOf("in use", StringComparison.OrdinalIgnoreCase) >= 0, "the server said: " + e.GetType().Name + ": " + e.Message);
                }
                finally { busy.Stop(); }
            }
        }
    }
}
