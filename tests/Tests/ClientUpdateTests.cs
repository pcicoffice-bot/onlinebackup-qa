using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>UPD-020: the computers find what changed in the client their server carries, and download only that, checked.</summary>
    [Collection("ClientDir")]   // OB_CLIENT_DIR is one for the whole test run
    public class ClientUpdateTests
    {
        [Fact]
        public void TheServerListsItsClientFiles_TheComputerSeesWhatChanged_DownloadsOnlyListedFiles()
        {
            var dir = Path.Combine(Path.GetTempPath(), "obclient-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            var before = Environment.GetEnvironmentVariable("OB_CLIENT_DIR");
            Environment.SetEnvironmentVariable("OB_CLIENT_DIR", dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "OnlineBackup.Agent.exe"), "a newer agent");
                File.WriteAllText(Path.Combine(dir, "version.txt"), "0.2.0");
                File.WriteAllText(Path.Combine(dir, "connection.xml"), "<CONNECTION SERVER=\"x\" />");   // never offered: each computer keeps its own
                using (var env = new Env())
                {
                    env.CreateUser("upd2026", "Customer-Pass-1");
                    var app = env.Agent("upd2026", "Customer-Pass-1");
                    List<Msg> changed;
                    var st = ClientUpdate.Check(app, out changed);
                    Assert.Equal("0.2.0", st["latest"]);
                    Assert.Contains(changed, f => f["name"] == "OnlineBackup.Agent.exe");
                    Assert.DoesNotContain(changed, f => f["name"] == "connection.xml");
                    var got = Path.Combine(env.Root, "got.exe");
                    app.DeviceClient().Download("/api/client/file?name=OnlineBackup.Agent.exe", got);
                    Assert.Equal("a newer agent", File.ReadAllText(got));
                    foreach (var bad in new[] { "connection.xml", "..%2Fsystem.xml", "C:%5Cwindows%5Cwin.ini" })
                        Assert.Throws<AgentException>(() => app.DeviceClient().Download("/api/client/file?name=" + bad, got));
                    // without the computer's token: nothing
                    Assert.Throws<AgentException>(() => new Client(env.Url).Call("GET", "/api/client/files"));
                }
            }
            finally { Environment.SetEnvironmentVariable("OB_CLIENT_DIR", before); try { Directory.Delete(dir, true); } catch { } }
        }

        /// <summary>
        /// Owner decision B1 (pilot): automatic agent update is OFF in Pilot 1 until agent updates are signed and refuse older
        /// versions. The computer's update — by itself every 6 hours (ClientUpdate.Auto) and from the program's "Update"
        /// alike — asks its server for GET /api/client/files and then GET /api/client/file. With the pilot switch the server
        /// offers neither (403 SCOPE, the reason given), so the computer finds nothing to install and no update is staged;
        /// without the switch the same computer is offered the newer client as before (the control).
        /// </summary>
        [Fact]
        public void B1_WithThePilotSwitch_TheServerOffersNoClientUpdate_TheComputerDoesNotUpdateItself_WithoutItItDoes()
        {
            var dir = Path.Combine(Path.GetTempPath(), "obclient-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            var before = Environment.GetEnvironmentVariable("OB_CLIENT_DIR");
            Environment.SetEnvironmentVariable("OB_CLIENT_DIR", dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "OnlineBackup.Agent.exe"), "a newer agent, not signed");
                File.WriteAllText(Path.Combine(dir, "version.txt"), "0.3.0");
                using (var env = new Env())
                {
                    env.CreateUser("upd2027", "Customer-Pass-1");
                    var app = env.Agent("upd2027", "Customer-Pass-1");
                    PilotScopeServerTests.PilotOn(env);
                    List<Msg> changed = null;
                    PilotScopeServerTests.Refused(() => ClientUpdate.Check(app, out changed), "client");
                    PilotScopeServerTests.Refused(() => app.DeviceClient().Call("GET", "/api/client/files"), "client");
                    var got = Path.Combine(env.Root, "got-b1.exe");
                    PilotScopeServerTests.Refused(() => app.DeviceClient().Download("/api/client/file?name=OnlineBackup.Agent.exe", got), "client");
                    Assert.False(File.Exists(got) && File.ReadAllText(got) == "a newer agent, not signed");
                    PilotScopeServerTests.Refused(() => ClientUpdate.Apply(app, m => { }), "client");
                    Assert.False(Directory.Exists(Path.Combine(app.Home.Dir, "update")), "an agent update was staged with the pilot switch on");

                    // the control: without the switch the newer client is offered as before
                    PilotScopeServerTests.PilotOff(env);
                    var st = ClientUpdate.Check(app, out changed);
                    Assert.Equal("0.3.0", st["latest"]);
                    Assert.Contains(changed, f => f["name"] == "OnlineBackup.Agent.exe");
                }
            }
            finally { Environment.SetEnvironmentVariable("OB_CLIENT_DIR", before); try { Directory.Delete(dir, true); } catch { } }
        }
    }
}
