using System.Collections.Generic;
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Xml.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>PKG: the IT company fills in its details on its server and downloads the client software in its own name.</summary>
    [Collection("ClientDir")]   // OB_CLIENT_DIR is one for the whole test run
    public class PackageTests
    {
        static byte[] Download(Env env, string session, out string fileName, out int status, string os = null)
        {
            var r = (HttpWebRequest)WebRequest.Create(env.Url + "api/admin/clientpackage" + (os == null ? "" : "?os=" + os));
            r.Headers["X-Session"] = session; fileName = null;
            try
            {
                using (var resp = (HttpWebResponse)r.GetResponse()) using (var ms = new MemoryStream())
                {
                    status = (int)resp.StatusCode; fileName = resp.Headers["Content-Disposition"];
                    resp.GetResponseStream().CopyTo(ms); return ms.ToArray();
                }
            }
            catch (WebException e) { status = (int)((HttpWebResponse)e.Response).StatusCode; return null; }
        }

        [Fact]
        public void ClientSoftwareCarriesTheCompanysNameAndServer_AndInstallsFromThePackage()
        {
            using (var env = new Env())
            {
                var client = env.Dir("client-files");
                File.WriteAllText(Path.Combine(client, "OnlineBackup.Agent.exe"), "agent");
                File.WriteAllText(Path.Combine(client, "OnlineBackup.Core.dll"), "core");
                File.WriteAllText(Path.Combine(client, "restic.exe"), "restic");
                File.WriteAllText(Path.Combine(client, "OnlineBackup.Agent.pdb"), "symbols");
                File.WriteAllText(Path.Combine(client, "Setup.exe"), "MZ-setup-program");
                Environment.SetEnvironmentVariable("OB_CLIENT_DIR", client);
                try
                {
                    var admin = env.Admin();
                    string fn; int st;
                    Assert.Null(Download(env, admin.Session, out fn, out st));
                    Assert.Equal(400, st);                                                     // the server address is required first

                    admin.Call("POST", "/api/admin/settings", new Msg().Set("publicUrl", env.Url).Set("certPin", "AB:CD:EF")
                        .Set("brandPRODUCT", "ITCare Cloud Backup").Set("brandSLOGAN", "המידע שלך, תמיד מוגן").Set("brandCOMPANY", "נטסייף מחשוב").Set("brandPHONE", "03-5551234").Set("brandCOLOR", "#7a2e8c"));
                    // SETUP-C60: Windows is one file — Setup.exe with the package's files after it
                    var exe = Download(env, admin.Session, out fn, out st);
                    Assert.Equal(200, st);
                    Assert.Contains("ITCare-Cloud-Backup-Setup.exe", fn);
                    Assert.Equal("MZ-setup-program", Encoding.ASCII.GetString(exe, 0, 16));
                    var inside = OnlineBackup.Server.ClientPackage.ReadExe(exe);
                    Assert.Equal("agent", Encoding.UTF8.GetString(inside["OnlineBackup.Agent.exe"]));
                    Assert.Contains("ITCare Cloud Backup", Encoding.UTF8.GetString(inside["branding.xml"]));
                    Assert.Contains("abcdef", Encoding.UTF8.GetString(inside["connection.xml"]));
                    Assert.False(inside.ContainsKey("Setup.exe")); Assert.False(inside.ContainsKey("OnlineBackup.Agent.pdb"));
                    Assert.False(inside.ContainsKey("contract.xml"));                          // no contract yet: the standard license agreement
                    // SETUP-C80: the IT company's contract is the installation's license agreement
                    admin.Call("POST", "/api/admin/contract", new Msg().Set("install", 1).Add("texts", new Msg().Set("lang", "he").Set("text", "חוזה השירות של נטסייף")));
                    var withContract = OnlineBackup.Server.ClientPackage.ReadExe(Download(env, admin.Session, out fn, out st));
                    var ct = XElement.Parse(Encoding.UTF8.GetString(withContract["contract.xml"]).TrimStart('\uFEFF'));
                    Assert.Equal("1", (string)ct.Attribute("VERSION")); Assert.Equal("חוזה השירות של נטסייף", ct.Element("TEXT").Value);
                    admin.Call("POST", "/api/admin/contract", new Msg().Set("install", 0));
                    Assert.False(OnlineBackup.Server.ClientPackage.ReadExe(Download(env, admin.Session, out fn, out st)).ContainsKey("contract.xml"));   // not shown at installation
                    admin.Call("POST", "/api/admin/contract", new Msg().Set("install", 1).Add("texts", new Msg().Set("lang", "he").Set("text", "")));   // no contract again

                    var zip = Download(env, admin.Session, out fn, out st, "zip");
                    Assert.Equal(200, st);
                    Assert.Contains("ITCare-Cloud-Backup-Setup.zip", fn);

                    var outDir = env.Dir("unzipped");
                    using (var z = new ZipArchive(new MemoryStream(zip))) z.ExtractToDirectory(outDir);
                    var pkg = Path.Combine(outDir, "ITCare Cloud Backup-Setup");
                    Assert.True(File.Exists(Path.Combine(pkg, "OnlineBackup.Agent.exe")));
                    Assert.True(File.Exists(Path.Combine(pkg, "restic.exe")));
                    Assert.False(File.Exists(Path.Combine(pkg, "OnlineBackup.Agent.pdb")));
                    var brand = XElement.Load(Path.Combine(pkg, "branding.xml"));
                    Assert.Equal("ITCare Cloud Backup", (string)brand.Attribute("PRODUCT"));
                    Assert.Equal("המידע שלך, תמיד מוגן", (string)brand.Attribute("SLOGAN"));
                    Assert.Equal("נטסייף מחשוב", (string)brand.Attribute("COMPANY"));
                    var conn = XElement.Load(Path.Combine(pkg, "connection.xml"));
                    Assert.Equal(env.Url.TrimEnd('/'), (string)conn.Attribute("SERVER"));
                    Assert.Equal("abcdef", (string)conn.Attribute("PIN"));
                    var cmd = File.ReadAllBytes(Path.Combine(pkg, "Setup.cmd"));
                    Assert.Equal((byte)'@', cmd[0]);                                           // no BOM: cmd.exe must read it
                    Assert.True(cmd.All(b => b < 128));

                    // install from the package (the agent files here are placeholders; registration is real)
                    env.CreateUser("cust9", "Customer-Pass-1");
                    var inst = env.Dir("install"); var data = env.Dir("data");
                    Func<string, string> opt = k => k == "login" ? "cust9" : k == "password" ? "Customer-Pass-1" : k == "install-dir" ? inst : k == "data-dir" ? data : null;
                    // SETUP-C50: the installation program only installs — the program connects afterwards
                    var inst0 = env.Dir("install0"); var data0 = env.Dir("data0");
                    Setup.Run(pkg, k => k == "install-dir" ? inst0 : k == "data-dir" ? data0 : null, k => k != "mac", m => { });
                    Assert.True(File.Exists(Path.Combine(inst0, "OnlineBackup.Agent.exe")));
                    Assert.Null(new AgentHome(data0).DeviceToken);
                    var r = Setup.Run(pkg, opt, k => k != "no-register" && k != "mac", m => { });
                    Assert.Equal("ITCare Cloud Backup", r.Product);
                    Assert.True(File.Exists(Path.Combine(inst, "branding.xml")));
                    Assert.True(File.Exists(Path.Combine(inst, "connection.xml")));
                    Assert.False(File.Exists(Path.Combine(inst, "Setup.cmd")));
                    var home = new AgentHome(data);
                    Assert.Equal(env.Url.TrimEnd('/'), home.Server);
                    Assert.Equal("abcdef", home.Pin);
                    Assert.NotNull(home.DeviceToken);
                    Assert.Throws<AgentException>(() => Setup.Run(pkg, k => k == "login" ? "cust9" : k == "password" ? "wrong-password" : k == "install-dir" ? inst : k == "data-dir" ? env.Dir("data2") : null, k => k != "no-register" && k != "mac", m => { }));

                    // a vendor's administrator gets the package in the vendor's own name
                    admin.Call("POST", "/api/admin/vendors", new Msg().Set("id", "acme").Set("brandPRODUCT", "Acme Safe"));
                    admin.Call("POST", "/api/admin/vendors/acme/admins", new Msg().Set("login", "acme-adm").Set("password", "Acme-Admin-Pass-1"));
                    var c = TestAuth.Admin(env.Url, "acme-adm", "Acme-Admin-Pass-1");
                    var vz = Download(env, c.Session, out fn, out st, "zip");
                    Assert.Contains("Acme-Safe-Setup.zip", fn);
                    using (var z = new ZipArchive(new MemoryStream(vz)))
                        Assert.Contains("Acme Safe", new StreamReader(z.Entries.First(e => e.Name == "branding.xml").Open(), Encoding.UTF8).ReadToEnd());
                }
                finally { Environment.SetEnvironmentVariable("OB_CLIENT_DIR", null); }
            }
        }

        [Fact]
        public void LinuxClientPackage_KeepsExecutableBits_AndInstallsUnderTheProductName()
        {
            if (!OperatingSystem.IsLinux()) throw NotTested.Because("the package is built with Linux tools (tar, file modes)");
            using (var env = new Env())
            {
                var client = env.Dir("client-files"); var linux = Path.Combine(client, "linux"); Directory.CreateDirectory(linux);
                File.WriteAllText(Path.Combine(client, "OnlineBackup.Agent.exe"), "agent");
                File.WriteAllText(Path.Combine(client, "THIRD-PARTY-NOTICES.txt"), "restic BSD-2");
                File.WriteAllText(Path.Combine(linux, "OnlineBackup.Agent"), "linux agent");
                File.WriteAllText(Path.Combine(linux, "restic"), "linux restic");
                Environment.SetEnvironmentVariable("OB_CLIENT_DIR", client);
                try
                {
                    var admin = env.Admin();
                    admin.Call("POST", "/api/admin/settings", new Msg().Set("publicUrl", env.Url).Set("brandPRODUCT", "ITCare Cloud Backup"));
                    var req = (HttpWebRequest)WebRequest.Create(env.Url + "api/admin/clientpackage?os=linux"); req.Headers["X-Session"] = admin.Session;
                    byte[] tgz; string fn;
                    using (var resp = (HttpWebResponse)req.GetResponse()) using (var ms = new MemoryStream()) { fn = resp.Headers["Content-Disposition"]; resp.GetResponseStream().CopyTo(ms); tgz = ms.ToArray(); }
                    Assert.Contains("ITCare-Cloud-Backup-Setup-linux.tar.gz", fn);

                    var outDir = env.Dir("untar");
                    var modes = new Dictionary<string, UnixFileMode>();
                    using (var gz = new System.IO.Compression.GZipStream(new MemoryStream(tgz), System.IO.Compression.CompressionMode.Decompress))
                    using (var tar = new System.Formats.Tar.TarReader(gz))
                        for (var e = tar.GetNextEntry(); e != null; e = tar.GetNextEntry()) { modes[e.Name] = e.Mode; e.ExtractToFile(Path.Combine(outDir, Path.GetFileName(e.Name)), true); }
                    const string root = "ITCare-Cloud-Backup-Setup/";
                    Assert.True(modes[root + "setup.sh"].HasFlag(UnixFileMode.UserExecute));
                    Assert.True(modes[root + "OnlineBackup.Agent"].HasFlag(UnixFileMode.OtherExecute));
                    Assert.True(modes[root + "restic"].HasFlag(UnixFileMode.UserExecute));
                    Assert.False(modes[root + "branding.xml"].HasFlag(UnixFileMode.UserExecute));
                    Assert.True(modes.ContainsKey(root + "THIRD-PARTY-NOTICES.txt"));
                    Assert.False(modes.ContainsKey(root + "OnlineBackup.Agent.exe"));            // only the Linux files
                    var sh = File.ReadAllText(Path.Combine(outDir, "setup.sh"));
                    Assert.StartsWith("#!/bin/sh\n", sh); Assert.DoesNotContain("\r", sh); Assert.Contains("./OnlineBackup.Agent setup", sh);

                    env.CreateUser("linuxcust", "Customer-Pass-1");
                    var inst = env.Dir("opt"); var data = Path.Combine(env.Dir("varlib"), "ITCare");
                    Func<string, string> opt = k => k == "login" ? "linuxcust" : k == "password" ? "Customer-Pass-1" : k == "install-dir" ? inst : k == "data-dir" ? data : null;
                    var r = Setup.Run(outDir, opt, k => k == "no-service", m => { });
                    Assert.True(File.Exists(Path.Combine(inst, "OnlineBackup.Agent")) && File.Exists(Path.Combine(inst, "restic")));
                    Assert.False(File.Exists(Path.Combine(inst, "setup.sh")));
                    Assert.True(File.GetUnixFileMode(Path.Combine(inst, "OnlineBackup.Agent")).HasFlag(UnixFileMode.UserExecute));
                    Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(data));   // root only
                    Assert.NotNull(new AgentHome(data).DeviceToken);
                    var unit = Setup.SystemdUnit(Path.Combine(inst, "OnlineBackup.Agent"), data, "ITCare Cloud Backup");
                    Assert.Contains("ExecStart=\"" + Path.Combine(inst, "OnlineBackup.Agent") + "\" service --home \"" + data + "\"", unit);
                    Assert.Contains("Restart=on-failure", unit); Assert.Contains("WantedBy=multi-user.target", unit);
                }
                finally { Environment.SetEnvironmentVariable("OB_CLIENT_DIR", null); }
            }
        }

        [Fact]
        public void MacClientPackage_BothProcessors_LaunchDaemonAndApp()
        {
            if (!OperatingSystem.IsLinux()) throw NotTested.Because("the package is built with Linux tools (tar, file modes)");
            using (var env = new Env())
            {
                var client = env.Dir("client-files");
                foreach (var a in new[] { "arm64", "x64" })
                {
                    var d = Path.Combine(client, "mac", a); Directory.CreateDirectory(d);
                    File.WriteAllText(Path.Combine(d, "OnlineBackup.Agent"), "mac agent " + a); File.WriteAllText(Path.Combine(d, "restic"), "mac restic " + a);
                }
                Environment.SetEnvironmentVariable("OB_CLIENT_DIR", client);
                try
                {
                    var admin = env.Admin();
                    admin.Call("POST", "/api/admin/settings", new Msg().Set("publicUrl", env.Url).Set("brandPRODUCT", "ITCare Cloud Backup"));
                    var req = (HttpWebRequest)WebRequest.Create(env.Url + "api/admin/clientpackage?os=mac"); req.Headers["X-Session"] = admin.Session;
                    byte[] tgz; string fn;
                    using (var resp = (HttpWebResponse)req.GetResponse()) using (var ms = new MemoryStream()) { fn = resp.Headers["Content-Disposition"]; resp.GetResponseStream().CopyTo(ms); tgz = ms.ToArray(); }
                    Assert.Contains("ITCare-Cloud-Backup-Setup-mac.tar.gz", fn);
                    var outDir = env.Dir("untar-mac");
                    var modes = new Dictionary<string, UnixFileMode>();
                    using (var gz = new System.IO.Compression.GZipStream(new MemoryStream(tgz), System.IO.Compression.CompressionMode.Decompress))
                    using (var tar = new System.Formats.Tar.TarReader(gz))
                        for (var e = tar.GetNextEntry(); e != null; e = tar.GetNextEntry())
                        {
                            modes[e.Name] = e.Mode;
                            var dst = Path.Combine(outDir, e.Name.Substring(e.Name.IndexOf('/') + 1)); Directory.CreateDirectory(Path.GetDirectoryName(dst)); e.ExtractToFile(dst, true);
                        }
                    const string root = "ITCare-Cloud-Backup-Setup/";
                    Assert.True(modes[root + "setup.command"].HasFlag(UnixFileMode.UserExecute));
                    Assert.True(modes[root + "arm64/OnlineBackup.Agent"].HasFlag(UnixFileMode.OtherExecute));
                    Assert.True(modes[root + "x64/restic"].HasFlag(UnixFileMode.UserExecute));
                    var cmd = File.ReadAllText(Path.Combine(outDir, "setup.command"));
                    Assert.DoesNotContain("\r", cmd); Assert.Contains("uname -m", cmd); Assert.Contains("com.apple.quarantine", cmd); Assert.Contains("codesign --force --sign -", cmd);

                    // the setup a Mac runs (as setup.command does: the branding beside the agent of this processor)
                    var arm = Path.Combine(outDir, "arm64");
                    foreach (var f in new[] { "branding.xml", "connection.xml" }) File.Copy(Path.Combine(outDir, f), Path.Combine(arm, f), true);
                    env.CreateUser("maccust", "Customer-Pass-1");
                    var inst = env.Dir("usrlocal"); var data = Path.Combine(env.Dir("appsupport"), "ITCare"); var daemons = env.Dir("LaunchDaemons"); var apps = env.Dir("Applications");
                    Func<string, string> opt = k => k == "login" ? "maccust" : k == "password" ? "Customer-Pass-1" : k == "install-dir" ? inst : k == "data-dir" ? data : k == "unit-dir" ? daemons : k == "apps-dir" ? apps : null;
                    var lines = new List<string>();
                    var r = Setup.Run(arm, opt, k => k == "mac", lines.Add);
                    Assert.Equal("mac agent arm64", File.ReadAllText(Path.Combine(inst, "OnlineBackup.Agent")));
                    Assert.NotNull(new AgentHome(data).DeviceToken);
                    var plist = File.ReadAllText(Path.Combine(daemons, "com.onlinebackup.itcare-cloud-backup.plist"));
                    Assert.Contains("<string>" + Path.Combine(inst, "OnlineBackup.Agent") + "</string><string>service</string><string>--home</string><string>" + data + "</string>", plist);
                    Assert.Contains("<key>RunAtLoad</key><true/>", plist);
                    var launcher = File.ReadAllText(Path.Combine(apps, "ITCare Cloud Backup.app", "Contents", "MacOS", "launcher"));
                    Assert.Contains("with administrator privileges", launcher); Assert.Contains(Path.Combine(inst, "ui.txt"), launcher);
                    Assert.Contains("<key>CFBundleExecutable</key><string>launcher</string>", File.ReadAllText(Path.Combine(apps, "ITCare Cloud Backup.app", "Contents", "Info.plist")));
                    Assert.Contains(lines, l => l.Contains("Full Disk Access"));
                    Assert.True(r.Service && r.Shortcut);
                }
                finally { Environment.SetEnvironmentVariable("OB_CLIENT_DIR", null); }
            }
        }
    }
}
