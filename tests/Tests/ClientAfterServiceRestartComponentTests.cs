using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// Bug 89 (Windows run 12, W06): the program window read the service's address and key (ui.txt) once, when it opened.
    /// The service makes a new key (and may take another port) every time it starts — after a restart of the service (an
    /// update, a crash and Windows restarting it, a technician) every action in the open window, and in the tray icon that
    /// starts with Windows, failed with "Wrong key: open the screen from the desktop shortcut." until the customer closed it.
    /// Oracle: the window's local API (LocalApi) against two real service UIs, one after the other, with ui.txt rewritten
    /// by the second exactly as the service writes it.
    /// </summary>
    public class ClientAfterServiceRestartComponentTests
    {
        static int FreePort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }

        [Theory]
        [InlineData(false, false)]   // the restarted service has the same port (only the key is new)
        [InlineData(true, false)]    // ... another port (the first one was still taken)
        [InlineData(true, true)]     // ... and the window's call is a POST (as "Back up now" and a restore are)
        public void TheOpenWindow_KeepsWorking_AfterTheServiceRestarts(bool newPort, bool post)
        {
            using (var env = new Env())
            {
                env.CreateUser("uirestart", "Customer-Pass-1");
                var app = env.Agent("uirestart", "Customer-Pass-1");
                var ui = Path.Combine(env.Dir("install"), "ui.txt");
                var port = FreePort();
                var first = new ClientUi(app, port);
                File.WriteAllText(ui, first.Url);
                var api = new LocalApi(File.ReadAllText(ui), () => File.ReadAllText(ui));
                Assert.NotNull(api.Call("state", null, null));                         // control: the window works
                first.Dispose();

                var second = new ClientUi(app, newPort ? FreePort() : port);           // the service started again
                try
                {
                    Assert.NotEqual(first.Key, second.Key);
                    File.WriteAllText(ui, second.Url);
                    Msg m = null; Exception err = null;
                    try { m = api.Call("state", post ? new Msg() : null, null); } catch (Exception e) { err = e; }
                    Assert.True(err == null, "the open window after the service restarted: " + (err == null ? "" : err.Message));
                    Assert.NotNull(m);
                }
                finally { second.Dispose(); }
            }
        }

        [Fact]
        public void AWrongKeyThatStaysWrong_IsStillRefused_NoLoop()
        {
            using (var env = new Env())
            {
                env.CreateUser("uiwrong", "Customer-Pass-1");
                var app = env.Agent("uiwrong", "Customer-Pass-1");
                var service = new ClientUi(app, FreePort());
                try
                {
                    var bad = "http://127.0.0.1:" + service.Port + "/#0123456789abcdef0123456789abcdef";
                    var api = new LocalApi(bad, () => bad);                            // ui.txt says the same wrong thing
                    var e = Assert.Throws<AgentException>(() => api.Call("state", null, null));
                    Assert.Equal(403, e.Status);
                }
                finally { service.Dispose(); }
            }
        }
    }
}
