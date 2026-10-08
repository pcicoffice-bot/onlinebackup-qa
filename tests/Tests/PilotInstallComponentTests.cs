using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// Pilot release blockers, install and update — COMPONENT layer (no backup server is started). Contracts from
    /// tests/QA/specs.py:
    ///   IN-01 install the server: service running, certificate bound, port open, admin site answers
    ///   IN-02 install / uninstall / reinstall the client: registration kept on reinstall, nothing left after uninstall
    ///   IN-03 update the client all-or-nothing: all files new and the service back, or all old and the result says why
    ///   IN-04 update the server only with a signed package: signed installed with data kept, changed refused
    ///   IN-05 build the Windows client package with branding and the server inside
    /// Oracle: SHA-256 of the files before / after, computed here independently of the product; exact counts.
    /// </summary>
    [Collection("ClientDir")]
    public class PilotInstallComponentTests : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "obpilot-inst-" + Guid.NewGuid().ToString("N").Substring(0, 8));

        public PilotInstallComponentTests() { Directory.CreateDirectory(root); }

        public void Dispose()
        {
            ClientUpdate.ScHook = null; ClientUpdate.StopWait = ClientUpdate.StartWait = TimeSpan.FromSeconds(60);
            ServiceSetup.ScHook = null;
            try { Directory.Delete(root, true); } catch (Exception) { }
        }

        static string Sha(string f) { using (var s = File.OpenRead(f)) using (var h = SHA256.Create()) return Bytes.Hex(h.ComputeHash(s)); }
        static string Sha(byte[] b) { return Bytes.Hex(Bytes.Sha256(b)); }
        static byte[] Rnd(int n, int seed) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }

        /// <summary>SHA-256 of every file under a folder, by its path relative to the folder.</summary>
        static Dictionary<string, string> Tree(string dir)
        {
            return Directory.Exists(dir) ? Directory.GetFiles(dir, "*", SearchOption.AllDirectories).ToDictionary(f => Path.GetRelativePath(dir, f), Sha) : new Dictionary<string, string>();
        }

        string Dir(string name) { var d = Path.Combine(root, name); Directory.CreateDirectory(d); return d; }

        // ================================================================== IN-01 the server's installation

        static SetupAnswers Answers(string dataRoot, string password = "Strong-Pass-2026")
        {
            return new SetupAnswers { Product = "Pilot Cloud Backup", Company = "Pilot IT", HostName = "backup.pilot.example", Port = 8443, DataRoot = dataRoot,
                AdminLogin = "admin", AdminPassword = password, MailProvider = "none" };
        }

        /// <summary>
        /// IN-01 recovery: (1) a first installation whose server does not answer at the final check is NOT reported done;
        /// Setup run again finishes it as the same installation (same data folder, same certificate pin, no second
        /// certificate). (2) Windows reinstalled, the backups drive kept: a new installation on the same folder takes over
        /// the existing data — every customer file and the customer list byte-identical — and the pin it reports is the
        /// one in the configuration.
        /// </summary>
        [Fact]
        public void IN01_AnInstallationThatDidNotAnswer_IsFinishedByARerun_AndAReinstallOnTheOldDrive_KeepsEveryCustomerFile()
        {
            var pkg = Dir("pkg"); Directory.CreateDirectory(Path.Combine(pkg, "client"));
            var exeName = OperatingSystem.IsWindows() ? "OnlineBackup.Server.exe" : "OnlineBackup.Server";
            File.WriteAllBytes(Path.Combine(pkg, exeName), Rnd(4096, 1)); File.WriteAllBytes(Path.Combine(pkg, "client", "restic.exe"), Rnd(2048, 2));
            var oldRoot = Environment.GetEnvironmentVariable("OB_SETUP_ROOT");
            Environment.SetEnvironmentVariable("OB_SETUP_ROOT", Path.Combine(root, "machine"));
            try
            {
                var dataRoot = (string)Installer.Info()["suggestedDrive"];
                var a = Answers(dataRoot);
                Assert.Empty(Installer.Check(a, false));
                int certsBefore; lock (Installer.Commands) certsBefore = Installer.Commands.Count(c => c.StartsWith("certificate ", StringComparison.Ordinal));
                // (1) the server does not answer at the end of the first installation
                Installer.Answers = (port, pin, say) => { throw new InvalidOperationException("ERR_NO_ANSWER"); };
                var steps1 = new List<string>();
                var e = Assert.Throws<InvalidOperationException>(() => Installer.Run(a, pkg, steps1.Add));
                Assert.Equal("ERR_NO_ANSWER", e.Message);
                Assert.DoesNotContain("STEP_DONE", steps1);                                  // never "installed" without an answer
                var first = Installer.Existing();
                Assert.NotNull(first);
                var systemHome = (string)first.Attribute("SYSTEM_HOME");
                var pin1 = (string)SystemConfig.Load(systemHome).Doc.Root.Attribute("CERT_PIN");
                Assert.Matches("^[0-9a-f]{64}$", pin1);

                // Setup again, now the server answers: the same installation is finished
                var asked = new List<string>();
                Installer.Answers = (port, pin, say) => { asked.Add(port + "|" + pin); say("STEP_VERIFIED|" + port); };
                var steps2 = new List<string>();
                var r2 = Installer.Run(a, pkg, steps2.Add);
                Assert.True(r2.Update);
                Assert.Equal(systemHome, r2.SystemHome);
                Assert.Equal(pin1, r2.Pin);
                Assert.Equal(new[] { "8443|" + pin1 }, asked.ToArray());                     // the answer was checked against the pinned certificate
                Assert.True(steps2.IndexOf("STEP_VERIFIED|8443") >= 0 && steps2.IndexOf("STEP_VERIFIED|8443") < steps2.IndexOf("STEP_UPDATED"));
                lock (Installer.Commands) Assert.Equal(certsBefore + 1, Installer.Commands.Count(c => c.StartsWith("certificate ", StringComparison.Ordinal)));   // one certificate, not two
                Assert.Equal(Sha(Path.Combine(pkg, exeName)), Sha(Path.Combine(Installer.InstallDir, exeName)));

                // (2) customer data on the backups drive
                var cfg = SystemConfig.Load(systemHome);
                var usersHome = Path.Combine(Installer.DataFolder(dataRoot), "users");
                new Users(cfg).Create("acme2026", "Customer-Pass-1", "Acme", null, "COMPRESSED", "it@acme.example", "203.0.113.5");
                var objects = Path.Combine(usersHome, "acme2026", "files", "1700000000001", "Current");
                Directory.CreateDirectory(objects);
                File.WriteAllBytes(Path.Combine(objects, "obj.000"), Rnd(100000, 3));
                var dataBefore = Tree(usersHome);
                var usersXml = Path.Combine(systemHome, "conf", "users.xml");
                var usersXmlBefore = File.Exists(usersXml) ? Sha(usersXml) : null;
                Assert.True(dataBefore.Count >= 2, "the customer data of the test is in place");

                // Windows is reinstalled: the program folder (and its install.xml) is gone, the backups drive is kept
                Directory.Delete(Installer.InstallDir, true);
                Assert.Null(Installer.Existing());
                var steps3 = new List<string>();
                var r3 = Installer.Run(Answers(dataRoot, "Another-Pass-2027"), pkg, steps3.Add);
                Assert.False(r3.Update);
                Assert.Contains("STEP_DONE", steps3);
                Assert.Equal(systemHome, r3.SystemHome);
                Assert.Equal(dataBefore, Tree(usersHome));                                   // every customer file byte-identical
                if (usersXmlBefore != null) Assert.Equal(usersXmlBefore, Sha(usersXml));     // the customer list kept
                Assert.Equal(r3.Pin, (string)SystemConfig.Load(systemHome).Doc.Root.Attribute("CERT_PIN"));   // the clients are told the pin of the certificate bound now
                Assert.NotNull(new Users(SystemConfig.Load(systemHome)).LoadProfile("acme2026"));
            }
            finally { Installer.Answers = null; Environment.SetEnvironmentVariable("OB_SETUP_ROOT", oldRoot); }
        }

        // ================================================================== IN-02 the client's installation

        /// <summary>A client package as the server builds it: the program files, branding.xml, connection.xml, Setup.cmd, README.txt.</summary>
        string Package(string name, int seed, string folder = null)
        {
            var pkg = Dir(name);
            File.WriteAllBytes(Path.Combine(pkg, "OnlineBackup.Agent.exe"), Rnd(30000, seed));
            File.WriteAllBytes(Path.Combine(pkg, "OnlineBackup.Core.dll"), Rnd(20000, seed + 1));
            File.WriteAllBytes(Path.Combine(pkg, "restic.exe"), Rnd(25000, seed + 2));
            File.WriteAllText(Path.Combine(pkg, "version.txt"), "1." + seed + ".0");
            new XElement("BRANDING", new XAttribute("PRODUCT", "Pilot Safe Backup")).Save(Path.Combine(pkg, "branding.xml"));
            new XElement("CONNECTION", new XAttribute("SERVER", "https://backup.pilot.example:8443"), new XAttribute("PIN", new string('a', 64)), new XAttribute("FOLDER", folder ?? "PilotSafe" + seed)).Save(Path.Combine(pkg, "connection.xml"));
            File.WriteAllText(Path.Combine(pkg, "Setup.cmd"), "@echo off");
            File.WriteAllText(Path.Combine(pkg, "README.txt"), "readme");
            return pkg;
        }

        static Func<string, bool> Flags(params string[] on) { return k => on.Contains(k); }
        static Func<string, string> Opts(string install, string data) { return k => k == "install-dir" ? install : k == "data-dir" ? data : null; }
        static readonly string[] Quiet = { "no-register", "no-service", "no-shortcut" };

        /// <summary>
        /// IN-02 happy + integrity: every program file of the package is installed byte-identical (SHA-256); the
        /// installation scripts and the README are not; the data folder exists; the product is the package's.
        /// </summary>
        [Fact]
        public void IN02_InstallFromThePackage_EveryProgramFileByteIdentical_TheScriptsNot_TheDataFolderReady()
        {
            var pkg = Package("pkg", 10);
            var inst = Path.Combine(root, "Program Files", "PilotSafe"); var data = Path.Combine(root, "ProgramData", "PilotSafe");
            var r = Setup.Run(pkg, Opts(inst, data), Flags(Quiet), m => { });
            Assert.Equal("Pilot Safe Backup", r.Product);
            Assert.Equal("https://backup.pilot.example:8443", r.Server);
            var want = new[] { "OnlineBackup.Agent.exe", "OnlineBackup.Core.dll", "restic.exe", "version.txt", "branding.xml", "connection.xml" }.ToDictionary(n => n, n => Sha(Path.Combine(pkg, n)));
            Assert.Equal(want.OrderBy(x => x.Key), Tree(inst).OrderBy(x => x.Key));          // exactly these, byte-identical
            Assert.True(Directory.Exists(data));
            Assert.Null(new AgentHome(data).DeviceToken);                                      // the installation only installs (SETUP-C50)
        }

        /// <summary>
        /// IN-02 recovery: a reinstall (or repair, or a newer package) over an existing installation replaces every program
        /// file with the new bytes and keeps this computer's registration and set keys byte-identical — the computer goes
        /// on backing up as itself, no new sign-in.
        /// </summary>
        [Fact]
        public void IN02_AReinstallOfANewerPackage_ReplacesEveryProgramFile_AndKeepsTheRegistrationAndKeys()
        {
            var v1 = Package("v1", 20, "PilotSafe"); var v2 = Package("v2", 30, "PilotSafe");
            var inst = Path.Combine(root, "inst"); var data = Path.Combine(root, "data");
            Setup.Run(v1, Opts(inst, data), Flags(Quiet), m => { });
            var home = new AgentHome(data);
            home.SaveRegistration("https://backup.pilot.example:8443", "acme2026", "PC-1", "YWNtZTIwMjY=.1700000000000.secret", null, new string('a', 64));
            home.SaveKey("1700000000123", KeySet.Random());
            Directory.CreateDirectory(home.SetDir("1700000000123")); File.WriteAllText(Path.Combine(home.SetDir("1700000000123"), "state.txt"), "#index\nfile\t1\n");
            var dataBefore = Tree(data);

            Setup.Run(v2, Opts(inst, data), Flags(Quiet), m => { });
            foreach (var n in new[] { "OnlineBackup.Agent.exe", "OnlineBackup.Core.dll", "restic.exe", "version.txt" })
                Assert.Equal(Sha(Path.Combine(v2, n)), Sha(Path.Combine(inst, n)));
            Assert.Equal(dataBefore, Tree(data));                                              // registration, keys, local index: untouched
            Assert.Equal("YWNtZTIwMjY=.1700000000000.secret", new AgentHome(data).DeviceToken);
        }

        /// <summary>IN-02 failure: a package without its connection file (an incomplete download) is refused before anything is written.</summary>
        [Fact]
        public void IN02_APackageWithoutItsConnectionFile_IsRefused_BeforeAnythingIsWritten()
        {
            var pkg = Package("pkg", 40);
            File.Delete(Path.Combine(pkg, "connection.xml"));
            var inst = Path.Combine(root, "inst"); var data = Path.Combine(root, "data");
            Assert.ThrowsAny<Exception>(() => Setup.Run(pkg, Opts(inst, data), Flags(Quiet), m => { }));
            Assert.False(Directory.Exists(inst));
            Assert.False(Directory.Exists(data));
        }

        /// <summary>
        /// IN-02 boundary + integrity (uninstall): the service is stopped and deleted; with "remove settings" both of the
        /// software's folders are gone (nothing left); without it the registration stays byte-identical for a reinstall;
        /// a folder that is NOT the software's (a mistyped path to the customer's documents) is never deleted — every file
        /// in it stays byte-identical, even with "remove settings".
        /// </summary>
        [Fact]
        public void IN02_UninstallRemovesOnlyItsOwnFolders_NothingLeftWhenAsked_AForeignFolderStaysByteIdentical()
        {
            var calls = new List<string>();
            ServiceSetup.ScHook = a => { calls.Add(a); return "[SC] OK"; };
            var product = "Pilot Uninstall " + Guid.NewGuid().ToString("N").Substring(0, 6);
            var folder = "PilotUninst" + Guid.NewGuid().ToString("N").Substring(0, 6);

            // keep the settings: the program folder goes, the registration stays
            var pkg = Package("pkg", 50, folder);
            var inst = Path.Combine(root, "inst1"); var data = Path.Combine(root, "data1");
            Setup.Run(pkg, Opts(inst, data), Flags(Quiet), m => { });
            new AgentHome(data).SaveRegistration("https://backup.pilot.example:8443", "acme2026", "PC-1", "a.b.c");
            var dataBefore = Tree(data);
            Setup.Uninstall(inst, data, product, folder, false, m => { });
            Assert.Contains(calls, c => c.StartsWith("stop OnlineBackupAgent", StringComparison.Ordinal));
            Assert.Contains(calls, c => c.StartsWith("delete OnlineBackupAgent", StringComparison.Ordinal));
            Assert.False(Directory.Exists(inst));
            Assert.Equal(dataBefore, Tree(data));

            // remove the settings too: nothing left
            var inst2 = Path.Combine(root, "inst2"); var data2 = Path.Combine(root, "data2");
            Setup.Run(pkg, Opts(inst2, data2), Flags(Quiet), m => { });
            new AgentHome(data2).SaveRegistration("https://backup.pilot.example:8443", "acme2026", "PC-1", "a.b.c");
            Setup.Uninstall(inst2, data2, product, folder, true, m => { });
            Assert.False(Directory.Exists(inst2));
            Assert.False(Directory.Exists(data2));

            // a folder that is not the software's: untouched, whatever was asked
            var docs = Dir("Documents"); var other = Dir("OtherProgram");
            File.WriteAllBytes(Path.Combine(docs, "contract.docx"), Rnd(5000, 51)); Directory.CreateDirectory(Path.Combine(docs, "2026"));
            File.WriteAllBytes(Path.Combine(docs, "2026", "ledger.xlsx"), Rnd(7000, 52));
            File.WriteAllBytes(Path.Combine(other, "settings.dat"), Rnd(300, 53));
            var docsBefore = Tree(docs); var otherBefore = Tree(other);
            Setup.Uninstall(docs, other, product, folder, true, m => { });
            Assert.Equal(docsBefore, Tree(docs));
            Assert.Equal(otherBefore, Tree(other));
        }

        // ================================================================== IN-03 the client's update

        sealed class Upd
        {
            public string From, To; public string State = "RUNNING"; public Func<string, string> Behave; public List<string> Calls = new List<string>();
        }

        Upd Stage(Dictionary<string, byte[]> installed, Dictionary<string, byte[]> update)
        {
            var u = new Upd { From = Dir("stage-" + Guid.NewGuid().ToString("N").Substring(0, 6)), To = Dir("install-" + Guid.NewGuid().ToString("N").Substring(0, 6)) };
            foreach (var kv in installed) File.WriteAllBytes(Path.Combine(u.To, kv.Key), kv.Value);
            foreach (var kv in update) File.WriteAllBytes(Path.Combine(u.From, kv.Key), kv.Value);
            File.WriteAllText(Path.Combine(u.From, "version.txt"), "9.9.9");
            File.WriteAllLines(Path.Combine(u.From, "update-files.txt"), update.Keys.ToArray());
            ClientUpdate.StopWait = TimeSpan.FromSeconds(1); ClientUpdate.StartWait = TimeSpan.FromSeconds(1);
            ClientUpdate.ScHook = a =>
            {
                u.Calls.Add(a);
                if (u.Behave != null) { var r = u.Behave(a); if (r != null) return r; }
                if (a.StartsWith("stop")) u.State = "STOPPED"; else if (a.StartsWith("start")) u.State = "RUNNING";
                return "STATE : " + u.State;
            };
            return u;
        }

        /// <summary>
        /// IN-03 integrity: after a good update every listed file holds exactly the new bytes (SHA-256 of the staged file),
        /// the files the update does not list (this computer's own connection, other programs' files) are byte-identical;
        /// after an update whose new version does not start, every file holds exactly the OLD bytes again and a file new in
        /// that version is gone — never a mix.
        /// </summary>
        [Fact]
        public void IN03_AGoodUpdate_GivesExactlyTheNewBytes_AFailedOne_GivesBackExactlyTheOldBytes_UnlistedFilesUntouched()
        {
            var old = new Dictionary<string, byte[]> { { "OnlineBackup.Agent.exe", Rnd(60000, 61) }, { "OnlineBackup.Core.dll", Rnd(40000, 62) }, { "connection.xml", Rnd(300, 63) }, { "OnlineBackup.Agent.exe.config", Rnd(500, 64) } };
            var neu = new Dictionary<string, byte[]> { { "OnlineBackup.Agent.exe", Rnd(61000, 71) }, { "OnlineBackup.Core.dll", Rnd(39000, 72) }, { "BouncyCastle.Crypto.dll", Rnd(30000, 73) } };

            var good = Stage(old, neu);
            Assert.Equal(0, ClientUpdate.Install(good.From, good.To));
            foreach (var kv in neu) Assert.Equal(Sha(kv.Value), Sha(Path.Combine(good.To, kv.Key)));
            Assert.Equal(Sha(old["connection.xml"]), Sha(Path.Combine(good.To, "connection.xml")));
            Assert.Equal(Sha(old["OnlineBackup.Agent.exe.config"]), Sha(Path.Combine(good.To, "OnlineBackup.Agent.exe.config")));
            Assert.StartsWith("OK\t9.9.9", File.ReadAllText(ClientUpdate.ResultPath(good.To)));
            Assert.Equal("RUNNING", good.State);

            var bad = Stage(old, neu);
            int starts = 0;
            bad.Behave = a => { if (a.StartsWith("start")) { starts++; bad.State = starts == 1 ? "STOPPED" : "RUNNING"; return "STATE : " + bad.State; } return null; };
            Assert.NotEqual(0, ClientUpdate.Install(bad.From, bad.To));
            var after = Tree(bad.To); after.Remove("update-result.txt");
            Assert.Equal(old.ToDictionary(kv => kv.Key, kv => Sha(kv.Value)).OrderBy(x => x.Key), after.OrderBy(x => x.Key));   // exactly the old files, byte for byte
            Assert.StartsWith("FAILED", File.ReadAllText(ClientUpdate.ResultPath(bad.To)));
            Assert.Equal("RUNNING", bad.State);
        }

        // ================================================================== IN-04 the server's update

        /// <summary>A stand-in for the vendor's update portal (POST update/latest, POST update/package) — not the product.</summary>
        sealed class Portal : IDisposable
        {
            readonly HttpListener http = new HttpListener();
            public string Url; public string Latest = "{}"; public byte[] Package = new byte[0]; public int PackageCalls;
            public Portal()
            {
                var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); int port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
                Url = "http://localhost:" + port; http.Prefixes.Add(Url + "/"); http.Start();
                new Thread(() =>
                {
                    while (http.IsListening)
                    {
                        HttpListenerContext c; try { c = http.GetContext(); } catch (Exception) { return; }
                        try
                        {
                            c.Request.InputStream.CopyTo(Stream.Null);
                            byte[] b = c.Request.Url.AbsolutePath.EndsWith("/update/latest") ? Encoding.UTF8.GetBytes(Latest) : c.Request.Url.AbsolutePath.EndsWith("/update/package") ? Package : null;
                            if (c.Request.Url.AbsolutePath.EndsWith("/update/package")) Interlocked.Increment(ref PackageCalls);
                            c.Response.StatusCode = b == null ? 404 : 200; b = b ?? new byte[0];
                            c.Response.ContentLength64 = b.Length; c.Response.OutputStream.Write(b, 0, b.Length); c.Response.OutputStream.Close();
                        }
                        catch (Exception) { try { c.Response.Abort(); } catch (Exception) { } }
                    }
                }) { IsBackground = true }.Start();
            }
            public void Dispose() { try { http.Stop(); http.Close(); } catch (Exception) { } }
        }

        static string ServerExe { get { return OperatingSystem.IsWindows() ? "OnlineBackup.Server.exe" : "OnlineBackup.Server"; } }

        static byte[] UpdateZip(byte[] program, string version, bool withProgram = true)
        {
            using (var ms = new MemoryStream())
            {
                using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
                {
                    if (withProgram) using (var s = z.CreateEntry("server/" + ServerExe).Open()) s.Write(program, 0, program.Length);
                    using (var s = z.CreateEntry("server/version.txt").Open()) { var v = Encoding.ASCII.GetBytes(version); s.Write(v, 0, v.Length); }
                    using (var s = z.CreateEntry("server/client/restic.exe").Open()) { var r = Rnd(3000, 9); s.Write(r, 0, r.Length); }
                }
                return ms.ToArray();
            }
        }

        static string Signed(string privateKey, string version, string sha)
        {
            using (var ec = ECDsa.Create())
            {
                ec.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKey), out _);
                return Convert.ToBase64String(ec.SignData(Encoding.UTF8.GetBytes("OBUPDATE|" + version + "|" + sha), HashAlgorithmName.SHA256));
            }
        }

        static string LatestJson(string version, string sha, string signature)
        {
            return Json.Write(new Dictionary<string, object> { { "version", version }, { "sha256", sha }, { "signature", signature }, { "file", "OnlineBackup-Server-" + version + "-Windows.zip" } });
        }

        /// <summary>The update thread has ended (installing or failed): a poll with a generous limit, never a fixed sleep.</summary>
        static string WaitUpdate(SystemConfig cfg)
        {
            var until = DateTime.UtcNow.AddSeconds(90);
            while (DateTime.UtcNow < until)
            {
                var st = (string)Updater.Status(cfg, false)["state"];
                if (st == "installing" || st == "failed") return st;
                Thread.Sleep(50);
            }
            return "timeout";
        }

        /// <summary>The vendor's key pair of this test (the debug build takes the verification key from OB_LICENSE_PUBKEY).</summary>
        sealed class VendorKey : IDisposable
        {
            public readonly string[] Pair = License.KeyGen(); readonly string before = Environment.GetEnvironmentVariable("OB_LICENSE_PUBKEY");
            public VendorKey() { Environment.SetEnvironmentVariable("OB_LICENSE_PUBKEY", Pair[1]); }
            public void Dispose() { Environment.SetEnvironmentVariable("OB_LICENSE_PUBKEY", before); }
        }

        SystemConfig UpdCfg(string portalUrl)
        {
            var cfg = SystemConfig.Init(Path.Combine(root, "system-" + Guid.NewGuid().ToString("N").Substring(0, 6)), "admin", "Admin-Pass-1", "localhost", new[] { Path.Combine(root, "users") + "|UNLIMITED|100" });
            cfg.Doc.Root.Element("LICENSE").SetAttributeValue("PORTAL_URL", portalUrl);
            cfg.Save();
            return cfg;
        }

        /// <summary>
        /// IN-04 happy + integrity: a version the vendor signed (its version and SHA-256) is downloaded, its SHA-256 checked,
        /// unpacked and handed to its own installer exactly once — the program handed on is byte-identical to the vendor's;
        /// the server's settings and the customers' data are not touched by the update.
        /// </summary>
        [Fact]
        public void IN04_ASignedVersion_IsDownloaded_Checked_HandedToItsInstallerByteIdentical_SettingsAndDataUntouched()
        {
            using (var key = new VendorKey())
            using (var portal = new Portal())
            {
                var cfg = UpdCfg(portal.Url);
                var program = Rnd(200000, 81);
                portal.Package = UpdateZip(program, "99.1.0");
                var sha = Sha(portal.Package);
                portal.Latest = LatestJson("99.1.0", sha, Signed(key.Pair[0], "99.1.0", sha));
                var custFile = Path.Combine(root, "users", "acme", "files", "obj.000"); Directory.CreateDirectory(Path.GetDirectoryName(custFile)); File.WriteAllBytes(custFile, Rnd(5000, 82));
                var settingsBefore = Sha(Path.Combine(cfg.SystemHome, "conf", "system.xml")); var custBefore = Sha(custFile);
                var launched = new List<string>(); var launch = Updater.Launch;
                Updater.Reset(); Updater.Launch = (exe, log) => { lock (launched) launched.Add(exe); };
                try
                {
                    Assert.Equal(true, Updater.Status(cfg, true)["available"]);
                    Updater.Start(cfg, "admin", "127.0.0.1");
                    Assert.Equal("installing", WaitUpdate(cfg));
                    var exe = Assert.Single(launched);
                    Assert.Equal(Sha(program), Sha(exe));                                         // the vendor's program, byte for byte
                    Assert.StartsWith(Path.Combine(cfg.SystemHome, "update"), exe);
                    Assert.Equal(1, portal.PackageCalls);
                    Assert.Equal(settingsBefore, Sha(Path.Combine(cfg.SystemHome, "conf", "system.xml")));
                    Assert.Equal(custBefore, Sha(custFile));
                }
                finally { Updater.Launch = launch; Updater.Reset(); }
            }
        }

        /// <summary>
        /// IN-04 failure + recovery: a package changed on the way (one byte), a signature by another key, a signature of
        /// another version — each is refused with its reason, nothing is handed to an installer, the damaged download is not
        /// left behind; then the genuine signed version installs (a refusal never blocks the next update).
        /// </summary>
        [Fact]
        public void IN04_AChangedPackage_AForgedOrMisplacedSignature_AreRefused_NothingInstalled_ThenTheGenuineOneInstalls()
        {
            using (var key = new VendorKey())
            using (var portal = new Portal())
            {
                var cfg = UpdCfg(portal.Url);
                var program = Rnd(150000, 91);
                var genuine = UpdateZip(program, "99.2.0"); var sha = Sha(genuine);
                var launched = new List<string>(); var launch = Updater.Launch;
                Updater.Reset(); Updater.Launch = (exe, log) => { lock (launched) launched.Add(exe); };
                try
                {
                    Func<string> attempt = () => { Updater.Start(cfg, "admin", "127.0.0.1"); Assert.Equal("failed", WaitUpdate(cfg)); return (string)Updater.Status(cfg, false)["message"]; };

                    // the package changed on the way: one byte in the middle
                    var changed = (byte[])genuine.Clone(); changed[changed.Length / 2] ^= 0x40;
                    portal.Package = changed; portal.Latest = LatestJson("99.2.0", sha, Signed(key.Pair[0], "99.2.0", sha));
                    Assert.Contains("SHA-256", attempt());
                    Assert.Empty(Directory.GetFiles(Path.Combine(cfg.SystemHome, "update"), "*.zip"));   // the damaged download is not kept
                    // signed by another key
                    portal.Package = genuine; portal.Latest = LatestJson("99.2.0", sha, Signed(License.KeyGen()[0], "99.2.0", sha));
                    Assert.Contains("not signed", attempt());
                    // the vendor's signature of another version put on this one
                    portal.Latest = LatestJson("99.2.0", sha, Signed(key.Pair[0], "99.1.9", sha));
                    Assert.Contains("not signed", attempt());
                    Assert.Empty(launched);

                    // the genuine one
                    portal.Latest = LatestJson("99.2.0", sha, Signed(key.Pair[0], "99.2.0", sha));
                    Updater.Start(cfg, "admin", "127.0.0.1");
                    Assert.Equal("installing", WaitUpdate(cfg));
                    Assert.Equal(Sha(program), Sha(Assert.Single(launched)));
                }
                finally { Updater.Launch = launch; Updater.Reset(); }
            }
        }

        /// <summary>
        /// IN-04 boundary + failure (update from files, UPD-030): a package exactly at the size limit is taken, one byte
        /// over is refused; a package with a part missing (cut) and a package without a server program are refused; nothing
        /// is handed to an installer for any refused one; the accepted one hands on the program byte-identical.
        /// </summary>
        [Fact]
        public void IN04_UpdateFromFiles_ExactlyAtTheLimitTaken_OneByteOverOrAPartMissingOrNoProgram_Refused()
        {
            using var key = new VendorKey();                                                    // owner decision 106 (A): files must carry the vendor's signature
            var cfg = UpdCfg("");
            var program = Rnd(120000, 101);
            var zip = UpdateZip(program, "99.3.0");
            var sig = Signed(key.Pair[0], "99.3.0", Sha(zip));
            var launched = new List<string>(); var launch = Updater.Launch;
            Updater.Reset(); Updater.Launch = (exe, log) => { lock (launched) launched.Add(exe); };
            try
            {
                var over = Assert.Throws<InvalidOperationException>(() => Updater.FromUpload(cfg, new MemoryStream(zip), zip.Length - 1, "admin", "127.0.0.1", sig));
                Assert.Contains("too large", over.Message);
                var cut = zip.Take(zip.Length - 2000).ToArray();                                  // the last part never arrived
                Assert.Throws<InvalidOperationException>(() => Updater.FromUpload(cfg, new MemoryStream(cut), zip.Length, "admin", "127.0.0.1", sig));
                var noProgram = UpdateZip(program, "99.3.0", false);
                Assert.Contains("no server program", Assert.Throws<InvalidOperationException>(() => Updater.FromUpload(cfg, new MemoryStream(noProgram), zip.Length, "admin", "127.0.0.1", sig)).Message);
                Assert.Empty(launched);

                Assert.Equal("99.3.0", Updater.FromUpload(cfg, new MemoryStream(zip), zip.Length, "admin", "127.0.0.1", sig));
                Assert.Equal(Sha(program), Sha(Assert.Single(launched)));
            }
            finally { Updater.Launch = launch; Updater.Reset(); }
        }

        // ================================================================== IN-05 the Windows client package

        string ClientFiles(string name)
        {
            var d = Dir(name);
            File.WriteAllBytes(Path.Combine(d, "OnlineBackup.Agent.exe"), Rnd(80000, 111));
            File.WriteAllBytes(Path.Combine(d, "OnlineBackup.Core.dll"), Rnd(50000, 112));
            File.WriteAllBytes(Path.Combine(d, "OnlineBackup.Client.exe"), Rnd(40000, 113));
            File.WriteAllBytes(Path.Combine(d, "restic.exe"), Rnd(90000, 114));
            File.WriteAllText(Path.Combine(d, "version.txt"), "2.0.0");
            File.WriteAllBytes(Path.Combine(d, "OnlineBackup.Agent.pdb"), Rnd(1000, 115));
            var stub = new byte[4096]; stub[0] = (byte)'M'; stub[1] = (byte)'Z'; Array.Copy(Rnd(4000, 116), 0, stub, 96, 4000);
            File.WriteAllBytes(Path.Combine(d, "Setup.exe"), stub);
            File.WriteAllBytes(Path.Combine(d, "Setup.exe.config"), Rnd(200, 117));
            return d;
        }

        static readonly string[] Shipped = { "OnlineBackup.Agent.exe", "OnlineBackup.Core.dll", "OnlineBackup.Client.exe", "restic.exe", "version.txt" };

        static Dictionary<string, string> Brand(string product)
        {
            return new Dictionary<string, string> { { "PRODUCT", product }, { "COMPANY", "Pilot IT" }, { "PHONE", "03-5550000" }, { "EMAIL", "support@pilot.example" }, { "LANGUAGE", "en" } };
        }

        /// <summary>
        /// IN-05 happy + integrity (Windows): the one-file Setup.exe starts with the installation program's own bytes and
        /// carries every client program file byte-identical (SHA-256), none of the build leftovers (.pdb, a second
        /// Setup.exe), and this server's address and pin (normalized) with the company's branding; the ZIP package carries
        /// the same program files byte-identical under the product's folder with Setup.cmd.
        /// </summary>
        [Fact]
        public void IN05_TheWindowsSetup_CarriesEveryClientFileByteIdentical_ThisServerAndTheBranding()
        {
            var dir = ClientFiles("client");
            var p = ClientPackage.FromBrand(Brand("Pilot Safe Backup"), false, "https://backup.pilot.example:8443", "AB:CD:EF:01", dir);
            var exe = ClientPackage.BuildExe(p);
            var stub = File.ReadAllBytes(Path.Combine(dir, "Setup.exe"));
            Assert.Equal(Sha(stub), Sha(exe.Take(stub.Length).ToArray()));                       // the installation program itself, untouched
            var inside = ClientPackage.ReadExe(exe);
            Assert.Equal(Shipped.Concat(new[] { "branding.xml", "connection.xml" }).OrderBy(x => x, StringComparer.Ordinal), inside.Keys.OrderBy(x => x, StringComparer.Ordinal));
            foreach (var n in Shipped) Assert.Equal(Sha(Path.Combine(dir, n)), Sha(inside[n]));
            var conn = XElement.Parse(Encoding.UTF8.GetString(inside["connection.xml"]).TrimStart('﻿'));
            Assert.Equal("https://backup.pilot.example:8443", (string)conn.Attribute("SERVER"));
            Assert.Equal("abcdef01", (string)conn.Attribute("PIN"));
            Assert.Equal("Pilot Safe Backup", (string)conn.Attribute("FOLDER"));
            var brand = XElement.Parse(Encoding.UTF8.GetString(inside["branding.xml"]).TrimStart('﻿'));
            Assert.Equal("Pilot Safe Backup", (string)brand.Attribute("PRODUCT"));
            Assert.Equal("Pilot IT", (string)brand.Attribute("COMPANY"));
            Assert.Equal("Pilot-Safe-Backup-Setup.exe", ClientPackage.FileNameExe(p));

            using (var z = new ZipArchive(new MemoryStream(ClientPackage.Build(p))))
            {
                Func<string, byte[]> read = n => { using (var s = z.GetEntry("Pilot Safe Backup-Setup/" + n).Open()) using (var ms = new MemoryStream()) { s.CopyTo(ms); return ms.ToArray(); } };
                foreach (var n in Shipped) Assert.Equal(Sha(Path.Combine(dir, n)), Sha(read(n)));
                Assert.NotNull(z.GetEntry("Pilot Safe Backup-Setup/Setup.cmd"));
                Assert.Null(z.GetEntry("Pilot Safe Backup-Setup/OnlineBackup.Agent.pdb"));
                Assert.Equal(z.Entries.Count, z.Entries.Select(e => e.FullName).Distinct().Count());   // no name twice
            }
        }

        /// <summary>
        /// IN-05 boundary: a product name with characters Windows cannot hold in a file name, and a very long one — the
        /// folder and the Setup.exe name hold none of them (at most 40 characters of the name), Hebrew letters are kept.
        /// </summary>
        [Fact]
        public void IN05_AProductNameWindowsCannotHoldInAFileName_GivesSafeNames_HebrewKept()
        {
            var dir = ClientFiles("client");
            var bad = Path.GetInvalidFileNameChars().Concat(new[] { '*', '?', '"', '<', '>', '|', ':', '/', '\\' }).ToArray();
            foreach (var name in new[] { "Acme: \"Safe\" Backup* <Pro>|?/\\", "גיבוי ענן של אקמה", new string('x', 70) + " Backup" })
            {
                var p = ClientPackage.FromBrand(Brand(name), true, "https://backup.pilot.example:8443", "", dir);
                Assert.True(p.Folder.Length > 0 && p.Folder.Length <= 40, p.Folder);
                Assert.True(p.Folder.IndexOfAny(bad) < 0, p.Folder);
                Assert.False(p.Folder.EndsWith(".") || p.Folder.EndsWith(" "), p.Folder);
                Assert.True(ClientPackage.FileNameExe(p).IndexOfAny(bad) < 0, ClientPackage.FileNameExe(p));
                Assert.Equal(name, (string)p.Branding.Attribute("PRODUCT"));                       // the screens still show the real name
            }
            Assert.Equal("גיבוי ענן של אקמה", ClientPackage.FromBrand(Brand("גיבוי ענן של אקמה"), true, "https://x.example", "", dir).Folder);
        }

        /// <summary>
        /// IN-05 failure: the server without the client files (no agent, no Setup.exe program) refuses with CLIENT_FILES and a
        /// plain reason instead of an empty or broken package; a downloaded Setup.exe cut short is recognized as damaged.
        /// </summary>
        [Fact]
        public void IN05_MissingClientFiles_AreRefusedClearly_ACutDownloadIsDamaged()
        {
            var noStub = ClientFiles("client-nostub"); File.Delete(Path.Combine(noStub, "Setup.exe"));
            var e1 = Assert.Throws<ApiException>(() => ClientPackage.BuildExe(ClientPackage.FromBrand(Brand("Pilot"), true, "https://x.example", "", noStub)));
            Assert.Equal("CLIENT_FILES", e1.Code); Assert.Contains("missing", e1.Message);
            var noAgent = ClientFiles("client-noagent"); File.Delete(Path.Combine(noAgent, "OnlineBackup.Agent.exe"));
            Assert.Equal("CLIENT_FILES", Assert.Throws<ApiException>(() => ClientPackage.Build(ClientPackage.FromBrand(Brand("Pilot"), true, "https://x.example", "", noAgent))).Code);
            Assert.Equal("CLIENT_FILES", Assert.Throws<ApiException>(() => ClientPackage.BuildExe(ClientPackage.FromBrand(Brand("Pilot"), true, "https://x.example", "", noAgent))).Code);

            var exe = ClientPackage.BuildExe(ClientPackage.FromBrand(Brand("Pilot"), true, "https://x.example", "", ClientFiles("client-ok")));
            Assert.ThrowsAny<Exception>(() => ClientPackage.ReadExe(exe.Take(exe.Length - 1000).ToArray()));   // cut off: never read as a package
        }

        /// <summary>
        /// IN-05 integrity: one byte changed anywhere in the files appended to Setup.exe (a damaged download or disk) is
        /// refused when the package is read — never read as a package whose program files are silently different.
        /// </summary>
        [Fact]
        public void IN05_OneChangedByteAnywhereInTheSetupPayload_IsRefused_NeverReadAsDifferentFiles()
        {
            var dir = ClientFiles("client-dmg");
            var exe = ClientPackage.BuildExe(ClientPackage.FromBrand(Brand("Pilot"), true, "https://x.example", "", dir));
            var stubLen = new FileInfo(Path.Combine(dir, "Setup.exe")).Length;
            var len = exe.Length - 16 - stubLen;
            var silent = new List<long>();
            for (int k = 1; k < 40; k++)
            {
                var bad = (byte[])exe.Clone(); var at = stubLen + len * k / 40; bad[at] ^= 0x10;
                Dictionary<string, byte[]> got;
                try { got = ClientPackage.ReadExe(bad); } catch (Exception) { continue; }   // refused: right
                if (!Shipped.All(n => got.ContainsKey(n) && Sha(Path.Combine(dir, n)) == Sha(got[n]))) silent.Add(at);
            }
            Assert.True(silent.Count == 0, "a changed byte at these offsets gave different program files without an error: " + string.Join(",", silent));
        }
    }
}
