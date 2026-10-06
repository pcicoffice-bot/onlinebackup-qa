using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>SETUP-010: the installation from the wizard's answers (OB_SETUP_ROOT: no Windows commands, they are listed).</summary>
    public class SetupTests
    {
        [Fact]
        public void SETUP040_TheBackupsFolder_IsChosenFreely_ButNeverANetworkOrNonsensePath()
        {
            var root = Path.GetPathRoot(Path.GetTempPath());
            Assert.Equal(Path.Combine(root, "OnlineBackup"), Installer.DataFolder(root));                // a drive alone: a folder on it
            var mine = Path.Combine(root, "BackupsTest-" + Guid.NewGuid().ToString("N").Substring(0, 6), "Customers");   // not under a user profile (refused on Windows)
            Assert.Equal(Path.GetFullPath(mine), Installer.DataFolder(mine));                        // a folder: as written
            Assert.True(Installer.FolderOk(mine));                                                   // need not exist yet
            Assert.False(Installer.FolderOk(@"\\nas\backups"));                                    // a network path (the service cannot reach it)
            Assert.False(Installer.FolderOk("backups"));                                             // not a full path
            Assert.False(Installer.FolderOk(""));
        }

        [Fact]
        public void SETUP030_NeverTakesAPortOfIis_OrAnotherProgramsCertificate()
        {
            var cfg = "<sites><site name=\"Default Web Site\"><bindings><binding protocol=\"https\" bindingInformation=\"*:443:\" sslFlags=\"0\" />" +
                      "<binding protocol=\"http\" bindingInformation=\"10.0.0.5:8080:intranet.local\" /></bindings></site></sites>";
            Assert.True(Installer.IisBindsPort(cfg, 443));
            Assert.True(Installer.IisBindsPort(cfg, 8080));                 // a stopped site still owns its port
            Assert.False(Installer.IisBindsPort(cfg, 8443));
            Assert.False(Installer.IisBindsPort(cfg, 44));
            var foreign = "IP:port : 0.0.0.0:443\r\nCertificate Hash : ab12\r\nApplication ID : {4dc3e181-e14b-4a21-b022-59fc669b0914}\r\n";
            Assert.True(Installer.ForeignSslBinding(foreign));             // IIS's binding: never deleted
            Assert.False(Installer.ForeignSslBinding("IP:port : 0.0.0.0:8443\r\nApplication ID : " + Installer.AppId.ToUpperInvariant() + "\r\n"));   // ours: replaced on update
            Assert.False(Installer.ForeignSslBinding("The system cannot find the file specified."));   // no binding
            Assert.False(Installer.ForeignSslBinding(""));
        }

        [Fact]
        public void WizardAnswers_AreChecked_ThenInstall_ThenARerunOnlyUpdates()
        {
            var root = Path.Combine(Path.GetTempPath(), "obsetup-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var pkg = Path.Combine(root, "pkg"); Directory.CreateDirectory(Path.Combine(pkg, "client"));
            File.WriteAllText(Path.Combine(pkg, "OnlineBackup.Server.exe"), "v1"); File.WriteAllText(Path.Combine(pkg, "client", "restic.exe"), "r");
            Environment.SetEnvironmentVariable("OB_SETUP_ROOT", Path.Combine(root, "machine"));
            try
            {
                var bad = new SetupAnswers { HostName = "bad host!", Port = 80, AdminLogin = "a", AdminPassword = "short", AlertEmail = "nope", MailProvider = "gmail" };
                var errs = Installer.Check(bad, false);
                foreach (var code in new[] { "ERR_PRODUCT", "ERR_COMPANY", "ERR_HOST", "ERR_PORT_RANGE", "ERR_DRIVE", "ERR_LOGIN", "ERR_PASSWORD", "ERR_ALERT_EMAIL", "ERR_SMTP_LOGIN" }) Assert.Contains(code, errs);
                Assert.All(errs, e => Assert.Matches("^[A-Z_]+(\\|.*)?$", e));                          // codes: the wizard says them in the user's language

                var info = Installer.Info();
                var a = new SetupAnswers
                {
                    Product = "Acme Cloud Backup", Company = "Acme IT", HostName = "backup.acme.example", Port = 8443, DataRoot = (string)info["suggestedDrive"],
                    AdminLogin = "admin", AdminPassword = "Strong-Pass-2026", AlertEmail = "alerts@acme.example", MailProvider = "m365", SmtpLogin = "backup@acme.example", SmtpPassword = "Mail-Pass-1"
                };
                Assert.Empty(Installer.Check(a, false));
                var steps = new List<string>();
                var r = Installer.Run(a, pkg, steps.Add);
                Assert.False(r.Update);
                Assert.Equal("https://backup.acme.example:8443/admin", r.AdminUrl);
                Assert.Matches("^[0-9a-f]{64}$", r.Pin);
                Assert.Contains("STEP_CERT", steps); Assert.Contains("STEP_DONE", steps);
                Assert.True(File.Exists(Path.Combine(Installer.InstallDir, "client", "restic.exe")));
                var cfg = SystemConfig.Load(r.SystemHome);
                Assert.Equal("Acme Cloud Backup", (string)cfg.Doc.Root.Element("BRANDING").Attribute("PRODUCT"));
                Assert.Equal("smtp.office365.com", (string)cfg.Doc.Root.Element("SMTP").Attribute("HOST"));
                Assert.DoesNotContain("Mail-Pass-1", File.ReadAllText(Path.Combine(r.SystemHome, "conf", "system.xml")));   // stored protected only
                Assert.Equal(r.Pin, (string)cfg.Doc.Root.Attribute("CERT_PIN"));
                Assert.Equal("https://backup.acme.example:8443", (string)cfg.Doc.Root.Attribute("PUBLIC_URL"));
                Assert.Equal("admin", (string)cfg.Admin.Attribute("LOGIN_NAME"));
                lock (Installer.Commands)
                {
                    Assert.Contains(Installer.Commands, c => c.StartsWith("netsh.exe http add sslcert ipport=0.0.0.0:8443 certhash="));
                    Assert.Contains(Installer.Commands, c => c.Contains("advfirewall firewall add rule") && c.Contains("localport=8443"));
                    Assert.DoesNotContain(Installer.Commands, c => c.IndexOf("iisreset", StringComparison.OrdinalIgnoreCase) >= 0 || c.IndexOf("appcmd", StringComparison.OrdinalIgnoreCase) >= 0);   // IIS is never touched
                }

                // running Setup again: an update — new files, the same data, settings and certificate
                File.WriteAllText(Path.Combine(pkg, "OnlineBackup.Server.exe"), "v2");
                Assert.Empty(Installer.Check(new SetupAnswers(), true));
                var steps2 = new List<string>();
                var r2 = Installer.Run(new SetupAnswers(), pkg, steps2.Add);
                Assert.True(r2.Update); Assert.Equal(r.Pin, r2.Pin); Assert.Equal(r.SystemHome, r2.SystemHome);
                Assert.Equal("v2", File.ReadAllText(Path.Combine(Installer.InstallDir, "OnlineBackup.Server.exe")));
                Assert.Contains("STEP_UPDATED", steps2); Assert.DoesNotContain("STEP_CERT", steps2);
                Assert.Equal("Acme Cloud Backup", (string)SystemConfig.Load(r.SystemHome).Doc.Root.Element("BRANDING").Attribute("PRODUCT"));
            }
            finally { Environment.SetEnvironmentVariable("OB_SETUP_ROOT", null); try { Directory.Delete(root, true); } catch (Exception) { } }
        }

        [Fact]
        public void WindowsServerOnly_AndTheDriveIsChosenByItself()
        {
            // SETUP-030 / SETUP-040: a client edition of Windows is refused; the drive is the one with the most free space
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "obsetup-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var oldRoot = Environment.GetEnvironmentVariable("OB_SETUP_ROOT");
            Environment.SetEnvironmentVariable("OB_SETUP_ROOT", root);
            try
            {
                Environment.SetEnvironmentVariable("OB_SETUP_OS", "client");
                Assert.False((bool)Installer.Info()["serverOs"]);
                Assert.Contains("ERR_NOT_SERVER", Installer.Check(new SetupAnswers(), false));
                Assert.DoesNotContain("ERR_NOT_SERVER", Installer.Check(new SetupAnswers(), true));   // an update of an existing server is not stopped
                Environment.SetEnvironmentVariable("OB_SETUP_OS", null);
                Assert.True((bool)Installer.Info()["serverOs"]);
                Assert.DoesNotContain("ERR_NOT_SERVER", Installer.Check(new SetupAnswers(), false));
                Assert.Equal(root, Installer.Info()["suggestedDrive"]);
            }
            finally { Environment.SetEnvironmentVariable("OB_SETUP_OS", null); Environment.SetEnvironmentVariable("OB_SETUP_ROOT", oldRoot); try { System.IO.Directory.Delete(root, true); } catch (Exception) { } }
        }
    }
}
