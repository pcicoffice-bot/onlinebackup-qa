using System;
using System.Linq;
using System.Xml.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// I-3 over HTTP: the set editor saves on the version it loaded. One technician saving twice in a row works (the
    /// answer carries the new version); a backup that runs meanwhile does not block the save; a save made on the settings
    /// as they were before another technician's save is refused (409 CHANGED) and changes nothing.
    /// Oracle: the server's own record of the set (GET), not the page.
    /// </summary>
    public class SetEditVersionIntegrationTests
    {
        static int Status(Action a) { try { a(); return 200; } catch (AgentException e) { return e.Status; } }

        [Fact]
        public void TheSameTechnicianSavesTwice_ABackupMeanwhileDoesNotBlock_ASaveOverAnotherOneIsRefused()
        {
            using (var env = new Env())
            {
                env.CreateUser("ver1", "Customer-Pass-1");
                var app = env.Agent("ver1", "Customer-Pass-1");
                var src = env.Dir("src"); System.IO.File.WriteAllText(System.IO.Path.Combine(src, "a.txt"), "a");
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "V", Sources = { src }, Vss = false });
                var admin = env.Admin();
                var path = "/api/admin/users/ver1/sets/" + set.Id;
                Func<Msg> get = () => admin.Call("GET", path);
                Func<Msg, Action<XElement>, string, Msg> save = (loaded, change, version) =>
                {
                    var x = XElement.Parse(loaded["set"]); change(x);
                    return admin.Call("POST", path, new Msg().Set("set", x.ToString(SaveOptions.DisableFormatting)).Set("version", version));
                };

                var a = get(); Assert.False(string.IsNullOrEmpty(a["version"]));
                Assert.Equal(a["version"], get()["version"]);                                            // the version is stable
                var r1 = save(a, x => x.SetAttributeValue("NAME", "V one"), a["version"]);
                var r2 = save(r1, x => x.SetAttributeValue("NAME", "V two"), r1["version"]);   // the same technician again
                Assert.Equal("V two", (string)XElement.Parse(get()["set"]).Attribute("NAME"));

                var b = get();
                Assert.StartsWith("BS_STOP_SUCCESS", app.Backup(set.Id).Result);                       // statistics change meanwhile
                save(b, x => x.SetAttributeValue("BANDWIDTH_KBPS", "444"), b["version"]);              // still saves

                var stale = b;                                                                          // opened before the save above
                var refused = Status(() => save(stale, x => x.SetAttributeValue("NAME", "Renamed by B"), stale["version"]));
                Assert.Equal(409, refused);
                var now = XElement.Parse(get()["set"]);
                Assert.Equal("444", (string)now.Attribute("BANDWIDTH_KBPS"));
                Assert.Equal("V two", (string)now.Attribute("NAME"));
            }
        }
    }
}
