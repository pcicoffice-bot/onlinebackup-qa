using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// Pilot release blockers, install and update — INTEGRATION layer: the real server in-process, real HTTP, real files,
    /// the real agent. Contracts: tests/QA/specs.py IN-01, IN-02, IN-03, IN-05. Oracle: SHA-256 of the source files
    /// against the files restored / installed, computed here.
    /// </summary>
    [Collection("ClientDir")]
    public class PilotInstallIntegrationTests
    {
        static string Sha(string f) { using (var s = File.OpenRead(f)) using (var h = SHA256.Create()) return Bytes.Hex(h.ComputeHash(s)); }
        static string Sha(byte[] b) { return Bytes.Hex(Bytes.Sha256(b)); }
        static byte[] Rnd(int n, int seed) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }
        static Dictionary<string, string> Tree(string dir)
        {
            return Directory.Exists(dir) ? Directory.GetFiles(dir, "*", SearchOption.AllDirectories).ToDictionary(f => Path.GetRelativePath(dir, f), Sha) : new Dictionary<string, string>();
        }
        static int FreePort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); int p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }

        /// <summary>Restores the newest point (or the given one) of a set into a new folder and returns the SHA-256 tree of the source folder inside it.</summary>
        /// <param name="rel">where the source folder lands under the restore folder (Env.Rel of the source)</param>
        static Dictionary<string, string> RestoreTree(AgentApp app, string password, string setId, string rel, string target, string point = null, string secret = null)
        {
            var r = app.RestoreFor(app.Interactive(password, null), setId, secret);
            r.Run(point, target, null, false);
            Assert.Equal(0, r.Failed);
            return Tree(Path.Combine(target, rel));
        }

        static byte[] Get(string url, string session, out int status, out string disposition)
        {
            var r = (HttpWebRequest)WebRequest.Create(url); if (session != null) r.Headers["X-Session"] = session; disposition = null;
            try
            {
                using (var resp = (HttpWebResponse)r.GetResponse()) using (var ms = new MemoryStream())
                { status = (int)resp.StatusCode; disposition = resp.Headers["Content-Disposition"]; resp.GetResponseStream().CopyTo(ms); return ms.ToArray(); }
            }
            catch (WebException e)
            {
                if (e.Response == null) { status = 0; return null; }
                status = (int)((HttpWebResponse)e.Response).StatusCode;
                using (var ms = new MemoryStream()) { e.Response.GetResponseStream().CopyTo(ms); return ms.ToArray(); }
            }
        }

        // ================================================================== IN-01

        /// <summary>
        /// IN-01 happy + failure + recovery: the server installed by the wizard's answers (dry run: no Windows commands)
        /// starts on the folders it created and answers over HTTP — the final check of the installation is a real request
        /// to it; the wizard's administrator signs in; a customer backs up through it. An update whose server does not come
        /// back is not reported done; the update run again (the server answers) is done, and every backed-up point still
        /// restores identical (SHA-256) — the data survived the update.
        /// </summary>
        [Fact]
        public void IN01_TheInstalledServerAnswers_ItsAdminSignsIn_ACustomerBacksUp_AnUpdateWithoutAnswerIsNotDone_TheRerunKeepsEveryPoint()
        {
            var root = Path.Combine(Path.GetTempPath(), "obpilot-in01-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var pkg = Path.Combine(root, "pkg"); Directory.CreateDirectory(Path.Combine(pkg, "client"));
            var exeName = OperatingSystem.IsWindows() ? "OnlineBackup.Server.exe" : "OnlineBackup.Server";
            File.WriteAllBytes(Path.Combine(pkg, exeName), Rnd(5000, 1)); File.WriteAllBytes(Path.Combine(pkg, "client", "restic.exe"), Rnd(3000, 2));
            var oldRoot = Environment.GetEnvironmentVariable("OB_SETUP_ROOT");
            Environment.SetEnvironmentVariable("OB_SETUP_ROOT", Path.Combine(root, "machine"));
            var url = "http://localhost:" + FreePort() + "/";
            Api server = null; bool serviceStarts = true;
            Action stopServer = () => { if (server != null) { server.Dispose(); server = null; } };
            try
            {
                // the final check of the installer: the service starts (the test starts the real server on the installed folders) and must answer
                Installer.Answers = (port, pin, say) =>
                {
                    if (serviceStarts && server == null) { server = new Api(SystemConfig.Load((string)Installer.Existing().Attribute("SYSTEM_HOME"))); server.Start(url); }
                    int st; string d; Get(url + "api/brand", null, out st, out d);
                    if (st != 200) throw new InvalidOperationException("ERR_NO_ANSWER");
                    say("STEP_VERIFIED|" + port);
                };
                var a = new SetupAnswers { Product = "Pilot Cloud Backup", Company = "Pilot IT", HostName = "backup.pilot.example", Port = 8443, DataRoot = (string)Installer.Info()["suggestedDrive"],
                    AdminLogin = "admin", AdminPassword = "Strong-Pass-2026", MailProvider = "none" };
                Assert.Empty(Installer.Check(a, false));
                var steps = new List<string>();
                var r = Installer.Run(a, pkg, steps.Add);
                Assert.True(steps.IndexOf("STEP_VERIFIED|8443") >= 0 && steps.IndexOf("STEP_VERIFIED|8443") < steps.IndexOf("STEP_DONE"));

                // the wizard's administrator, a customer, a backup through the installed server
                var admin = TestAuth.Admin(url, "admin", "Strong-Pass-2026");
                admin.Call("POST", "/api/admin/users", new Msg().Set("login", "acme2026").Set("password", "Customer-Pass-1").Set("alias", "Acme").Set("quotaGB", 1).Set("quotaType", "COMPRESSED").Set("email", "it@acme.example"));
                var app = new AgentApp(Path.Combine(root, "agent"));
                app.Register(url, "acme2026", "Customer-Pass-1", null, "PC-PILOT");
                var src = Path.Combine(root, "src"); Directory.CreateDirectory(Path.Combine(src, "Accounts"));
                File.WriteAllBytes(Path.Combine(src, "Accounts", "ledger.xlsx"), Rnd(200000, 3)); File.WriteAllText(Path.Combine(src, "notes.txt"), "first");
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "Files", Sources = { src } });
                var rel = Env.Rel(src);
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);
                var first = Tree(src);
                Assert.Equal(first, RestoreTree(app, "Customer-Pass-1", set.Id, rel, Path.Combine(root, "r1")));

                // an update: the service stops; the new version's server does not come back
                stopServer(); serviceStarts = false;
                File.WriteAllBytes(Path.Combine(pkg, exeName), Rnd(5000, 4));
                var steps2 = new List<string>();
                Assert.Equal("ERR_NO_ANSWER", Assert.Throws<InvalidOperationException>(() => Installer.Run(new SetupAnswers(), pkg, steps2.Add)).Message);
                Assert.DoesNotContain("STEP_UPDATED", steps2);
                Assert.ThrowsAny<Exception>(() => new Client(url) { Retries = 0 }.Call("GET", "/api/brand"));   // and it really does not answer

                // the update again: the server answers — done, and the data is all there
                serviceStarts = true;
                var steps3 = new List<string>();
                var r3 = Installer.Run(new SetupAnswers(), pkg, steps3.Add);
                Assert.True(r3.Update); Assert.Equal(r.SystemHome, r3.SystemHome); Assert.Equal(r.Pin, r3.Pin);
                Assert.Contains("STEP_UPDATED", steps3);
                Assert.Equal(Sha(Path.Combine(pkg, exeName)), Sha(Path.Combine(Installer.InstallDir, exeName)));
                File.WriteAllText(Path.Combine(src, "notes.txt"), "second, after the update"); File.SetLastWriteTimeUtc(Path.Combine(src, "notes.txt"), DateTime.UtcNow.AddMinutes(1));
                System.Threading.Thread.Sleep(1100);   // a distinct run id (one per second)
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);
                Assert.Equal(Tree(src), RestoreTree(app, "Customer-Pass-1", set.Id, rel, Path.Combine(root, "r2")));
                var points = app.RestoreFor(app.Interactive("Customer-Pass-1", null), set.Id).Points();
                Assert.Equal(2, points.Count);
                Assert.Equal(first, RestoreTree(app, "Customer-Pass-1", set.Id, rel, Path.Combine(root, "r3"), points[0]));   // the point from before the update
                Assert.NotNull(TestAuth.Admin(url, "admin", "Strong-Pass-2026").Session);
            }
            finally
            {
                stopServer();
                Installer.Answers = null; Environment.SetEnvironmentVariable("OB_SETUP_ROOT", oldRoot);
                try { Directory.Delete(root, true); } catch (Exception) { }
            }
        }

        // ================================================================== IN-02

        /// <summary>
        /// IN-02 failure + recovery + integrity: the Windows package (ZIP) downloaded from the server is installed on a
        /// computer. A wrong password at the installation refuses and registers nothing; the right one registers; the
        /// computer backs up. A reinstall over it keeps the registration (no sign-in) and the next backup goes on. Uninstall
        /// (keeping the settings) removes the program; the backups stay on the server — a new computer with the customer's
        /// password restores them identical (SHA-256); installing again on this computer reconnects it without a password.
        /// </summary>
        [Fact]
        public void IN02_AWrongPasswordRegistersNothing_TheRightOneBacksUp_AReinstallKeepsIt_UninstallKeepsTheBackups_AndAReinstallReconnects()
        {
            using (var env = new Env())
            {
                var client = env.Dir("client-files");
                foreach (var n in new[] { "OnlineBackup.Agent.exe", "OnlineBackup.Core.dll", "restic.exe" }) File.WriteAllBytes(Path.Combine(client, n), Rnd(20000, n.Length));
                File.WriteAllBytes(Path.Combine(client, "Setup.exe"), Rnd(3000, 9));
                var before = Environment.GetEnvironmentVariable("OB_CLIENT_DIR");
                Environment.SetEnvironmentVariable("OB_CLIENT_DIR", client);
                var sc = new List<string>(); ServiceSetup.ScHook = x => { sc.Add(x); return "[SC] OK"; };
                try
                {
                    var admin = env.Admin();
                    admin.Call("POST", "/api/admin/settings", new Msg().Set("publicUrl", env.Url).Set("brandPRODUCT", "Pilot Install Test"));
                    env.CreateUser("acme2026", "Customer-Pass-1");
                    int st; string disp;
                    var zip = Get(env.Url + "api/admin/clientpackage?os=zip", admin.Session, out st, out disp);
                    Assert.Equal(200, st);
                    var unz = env.Dir("unzipped");
                    using (var z = new ZipArchive(new MemoryStream(zip))) z.ExtractToDirectory(unz);
                    var pkg = Path.Combine(unz, "Pilot Install Test-Setup");
                    var folder = (string)XElement.Load(Path.Combine(pkg, "connection.xml")).Attribute("FOLDER");
                    var inst = Path.Combine(env.Root, "Program Files", folder); var data = Path.Combine(env.Root, "ProgramData", folder);
                    Func<string, Func<string, string>> opts = pw => k => k == "install-dir" ? inst : k == "data-dir" ? data : k == "login" ? (pw == null ? null : "acme2026") : k == "password" ? pw : null;
                    Func<string, bool> noService = k => k == "no-service" || k == "no-shortcut";
                    Func<string, bool> reinstall = k => k == "no-service" || k == "no-shortcut" || k == "no-register";

                    // a wrong password: refused, nothing registered
                    Assert.Throws<AgentException>(() => Setup.Run(pkg, opts("Wrong-Pass-9"), noService, m => { }));
                    Assert.Null(new AgentHome(data).DeviceToken);
                    // the right one
                    Setup.Run(pkg, opts("Customer-Pass-1"), noService, m => { });
                    var token = new AgentHome(data).DeviceToken;
                    Assert.NotNull(token);
                    foreach (var n in new[] { "OnlineBackup.Agent.exe", "OnlineBackup.Core.dll", "restic.exe" }) Assert.Equal(Sha(Path.Combine(client, n)), Sha(Path.Combine(inst, n)));
                    var app = new AgentApp(data);
                    var src = env.Dir("src"); File.WriteAllBytes(Path.Combine(src, "contract.pdf"), Rnd(150000, 11)); File.WriteAllText(Path.Combine(src, "a.txt"), "one");
                    var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "Docs", Sources = { src } });
                    var rel = Env.Rel(src);
                    Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);

                    // reinstall (repair) over it: the registration is kept, the backups go on without a sign-in
                    Setup.Run(pkg, opts(null), reinstall, m => { });
                    Assert.Equal(token, new AgentHome(data).DeviceToken);
                    File.WriteAllText(Path.Combine(src, "a.txt"), "two"); File.SetLastWriteTimeUtc(Path.Combine(src, "a.txt"), DateTime.UtcNow.AddMinutes(1));
                    System.Threading.Thread.Sleep(1100);
                    Assert.Equal("BS_STOP_SUCCESS", new AgentApp(data).Backup(set.Id).Result);
                    var want = Tree(src);

                    // uninstall, keeping this computer's settings
                    Setup.Uninstall(inst, data, "Pilot Install Test", folder, false, m => { });
                    Assert.False(Directory.Exists(inst));
                    Assert.Contains(sc, x => x.StartsWith("delete OnlineBackupAgent", StringComparison.Ordinal));
                    // the backups stay on the server: another computer with the customer's password restores them identical
                    var other = env.Agent("acme2026", "Customer-Pass-1", null, "other");
                    Assert.Equal(want, RestoreTree(other, "Customer-Pass-1", set.Id, rel, env.Dir("restore-other"), null, "Customer-Pass-1"));
                    // installing again here: connected as before, no password
                    Setup.Run(pkg, opts(null), reinstall, m => { });
                    Assert.Equal(token, new AgentHome(data).DeviceToken);
                    File.WriteAllText(Path.Combine(src, "b.txt"), "three");
                    System.Threading.Thread.Sleep(1100);
                    Assert.Equal("BS_STOP_SUCCESS", new AgentApp(data).Backup(set.Id).Result);
                    Assert.Equal(Tree(src), RestoreTree(new AgentApp(data), "Customer-Pass-1", set.Id, rel, env.Dir("restore-again")));
                }
                finally { ServiceSetup.ScHook = null; Environment.SetEnvironmentVariable("OB_CLIENT_DIR", before); }
            }
        }

        // ================================================================== IN-03

        /// <summary>
        /// IN-03 failure + recovery: the client files the server carries are listed with their SHA-256 and downloaded by
        /// the computer over HTTP — each downloaded file is exactly the server's (SHA-256 against the list and the source).
        /// Installed while one file of this computer cannot be replaced: every file is back to its old bytes and the result
        /// says FAILED. Once the blocked file is free, the same download installs: every file is the server's bytes, OK.
        /// </summary>
        [Fact]
        public void IN03_TheServersClientFiles_DownloadedChecked_OneBlockedFilePutsAllBack_ThenTheUpdateInstallsCompletely()
        {
            using (var env = new Env())
            {
                var client = env.Dir("client-files");
                var newFiles = new Dictionary<string, byte[]> { { "OnlineBackup.Agent.exe", Rnd(90000, 21) }, { "OnlineBackup.Core.dll", Rnd(60000, 22) }, { "OnlineBackup.Client.exe", Rnd(50000, 23) } };
                foreach (var kv in newFiles) File.WriteAllBytes(Path.Combine(client, kv.Key), kv.Value);
                File.WriteAllText(Path.Combine(client, "version.txt"), "7.1.0");
                var before = Environment.GetEnvironmentVariable("OB_CLIENT_DIR");
                Environment.SetEnvironmentVariable("OB_CLIENT_DIR", client);
                try
                {
                    env.CreateUser("upd2026", "Customer-Pass-1");
                    var app = env.Agent("upd2026", "Customer-Pass-1");
                    List<Msg> changed;
                    var st = ClientUpdate.Check(app, out changed);
                    Assert.Equal("7.1.0", st["latest"]);
                    var stage = env.Dir("stage");
                    foreach (var kv in newFiles)
                    {
                        var listed = changed.Single(f => f["name"] == kv.Key);
                        Assert.Equal(Sha(kv.Value), listed["sha256"]);
                        app.DeviceClient().Download("/api/client/file?name=" + kv.Key, Path.Combine(stage, kv.Key));
                        Assert.Equal(Sha(kv.Value), Sha(Path.Combine(stage, kv.Key)));
                    }
                    File.WriteAllText(Path.Combine(stage, "version.txt"), st["latest"]);
                    File.WriteAllLines(Path.Combine(stage, "update-files.txt"), newFiles.Keys.ToArray());

                    var install = env.Dir("install");
                    var oldFiles = newFiles.ToDictionary(kv => kv.Key, kv => Rnd(kv.Value.Length - 100, kv.Key.Length + 50));
                    foreach (var kv in oldFiles) File.WriteAllBytes(Path.Combine(install, kv.Key), kv.Value);
                    var state = "RUNNING";
                    ClientUpdate.StopWait = ClientUpdate.StartWait = TimeSpan.FromSeconds(1);
                    ClientUpdate.ScHook = x => { if (x.StartsWith("stop")) state = "STOPPED"; else if (x.StartsWith("start")) state = "RUNNING"; return "STATE : " + state; };

                    // one file cannot be replaced (a folder in its place: neither copying over it nor moving it aside works)
                    var blocked = Path.Combine(install, "OnlineBackup.Client.exe");
                    File.Delete(blocked); Directory.CreateDirectory(Path.Combine(blocked, "in-use"));
                    Assert.NotEqual(0, ClientUpdate.Install(stage, install));
                    Assert.StartsWith("FAILED", File.ReadAllText(ClientUpdate.ResultPath(install)));
                    foreach (var n in new[] { "OnlineBackup.Agent.exe", "OnlineBackup.Core.dll" }) Assert.Equal(Sha(oldFiles[n]), Sha(Path.Combine(install, n)));
                    Assert.Equal("RUNNING", state);

                    // the file is free again: the same update installs completely
                    Directory.Delete(blocked, true); File.WriteAllBytes(blocked, oldFiles["OnlineBackup.Client.exe"]);
                    Assert.Equal(0, ClientUpdate.Install(stage, install));
                    foreach (var kv in newFiles) Assert.Equal(Sha(kv.Value), Sha(Path.Combine(install, kv.Key)));
                    Assert.StartsWith("OK\t7.1.0", File.ReadAllText(ClientUpdate.ResultPath(install)));
                    Assert.Equal("RUNNING", state);
                }
                finally
                {
                    ClientUpdate.ScHook = null; ClientUpdate.StopWait = ClientUpdate.StartWait = TimeSpan.FromSeconds(60);
                    Environment.SetEnvironmentVariable("OB_CLIENT_DIR", before);
                }
            }
        }

        // ================================================================== IN-05

        /// <summary>
        /// IN-05 failure + boundary (Windows package from the admin site): without the server's address for customers the
        /// download is refused (400) with the reason; with the client files missing on the server (no Setup.exe program)
        /// it is refused (500, CLIENT_FILES) with the reason — never an empty or broken file; a product name full of
        /// characters Windows cannot hold gives a safe Setup.exe name, and the Setup.exe carries this server's address.
        /// </summary>
        [Fact]
        public void IN05_TheWindowsDownload_RefusesWithoutAddressOrClientFiles_WithTheReason_AndAHostileNameGivesASafeFileName()
        {
            using (var env = new Env())
            {
                var client = env.Dir("client-files");
                File.WriteAllBytes(Path.Combine(client, "OnlineBackup.Agent.exe"), Rnd(30000, 31));
                File.WriteAllBytes(Path.Combine(client, "OnlineBackup.Core.dll"), Rnd(20000, 32));
                var before = Environment.GetEnvironmentVariable("OB_CLIENT_DIR");
                Environment.SetEnvironmentVariable("OB_CLIENT_DIR", client);
                try
                {
                    var admin = env.Admin();
                    int st; string disp;
                    var body = Get(env.Url + "api/admin/clientpackage", admin.Session, out st, out disp);
                    Assert.Equal(400, st);
                    Assert.Contains("address", Encoding.UTF8.GetString(body));

                    admin.Call("POST", "/api/admin/settings", new Msg().Set("publicUrl", env.Url).Set("brandPRODUCT", "Acme: \"Safe\" <Backup>*?|"));
                    body = Get(env.Url + "api/admin/clientpackage", admin.Session, out st, out disp);
                    Assert.Equal(500, st);
                    Assert.Contains("CLIENT_FILES", Encoding.UTF8.GetString(body));
                    Assert.Contains("missing", Encoding.UTF8.GetString(body));

                    File.WriteAllBytes(Path.Combine(client, "Setup.exe"), Rnd(4000, 33));
                    var exe = Get(env.Url + "api/admin/clientpackage", admin.Session, out st, out disp);
                    Assert.Equal(200, st);
                    var name = disp.Substring(disp.IndexOf("filename=\"", StringComparison.Ordinal) + 10).TrimEnd('"');
                    Assert.EndsWith("-Setup.exe", name);
                    Assert.True(name.IndexOfAny(new[] { '*', '?', '"', '<', '>', '|', ':', '/', '\\' }) < 0, name);
                    var inside = ClientPackage.ReadExe(exe);
                    Assert.Equal(env.Url.TrimEnd('/'), (string)XElement.Parse(Encoding.UTF8.GetString(inside["connection.xml"]).TrimStart('﻿')).Attribute("SERVER"));
                    Assert.Equal(Sha(Path.Combine(client, "OnlineBackup.Agent.exe")), Sha(inside["OnlineBackup.Agent.exe"]));
                }
                finally { Environment.SetEnvironmentVariable("OB_CLIENT_DIR", before); }
            }
        }
    }
}
