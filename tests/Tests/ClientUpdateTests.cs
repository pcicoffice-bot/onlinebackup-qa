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
    }
}
