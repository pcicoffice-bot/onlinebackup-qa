using System;
using System.Collections.Generic;
using System.IO;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// The server's installation and update end only when the server ANSWERS (same class as UX-17, found by searching
    /// for it: a new installation checked that the server answers over HTTPS, the update restarted the service and said
    /// "updated" without looking — a server that did not come back after an update looked updated).
    ///   input    install (the server answers); update with a server that answers; update with one that does not
    ///   expected installation and the first update end with their DONE/UPDATED step after the answer check; the update
    ///            whose server does not answer ends with ERR_NO_ANSWER and never says STEP_UPDATED
    /// </summary>
    public class ServerUpdateAnswerTests
    {
        [Fact]
        public void AnUpdateWhoseServerDoesNotAnswer_IsNotUpdated()
        {
            var root = Path.Combine(Path.GetTempPath(), "obupd-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var pkg = Path.Combine(root, "pkg"); Directory.CreateDirectory(Path.Combine(pkg, "client"));
            File.WriteAllText(Path.Combine(pkg, "OnlineBackup.Server.exe"), "v1"); File.WriteAllText(Path.Combine(pkg, "client", "restic.exe"), "r");
            Environment.SetEnvironmentVariable("OB_SETUP_ROOT", Path.Combine(root, "machine"));
            var asked = new List<int>();
            try
            {
                Installer.Answers = (port, pin, say) => { asked.Add(port); say("STEP_VERIFIED|" + port); };
                var info = Installer.Info();
                var a = new SetupAnswers { Product = "Acme Cloud Backup", Company = "Acme IT", HostName = "backup.acme.example", Port = 8443, DataRoot = (string)info["suggestedDrive"],
                    AdminLogin = "admin", AdminPassword = "Strong-Pass-2026", AlertEmail = "alerts@acme.example", MailProvider = "m365", SmtpLogin = "backup@acme.example", SmtpPassword = "Mail-Pass-1" };
                var steps = new List<string>();
                Installer.Run(a, pkg, steps.Add);
                Assert.Equal(new[] { 8443 }, asked.ToArray());
                Assert.True(steps.IndexOf("STEP_VERIFIED|8443") < steps.IndexOf("STEP_DONE"));

                // an update whose server answers: checked, then "updated"
                File.WriteAllText(Path.Combine(pkg, "OnlineBackup.Server.exe"), "v2");
                var steps2 = new List<string>();
                Installer.Run(new SetupAnswers(), pkg, steps2.Add);
                Assert.Equal(new[] { 8443, 8443 }, asked.ToArray());
                Assert.True(steps2.IndexOf("STEP_VERIFIED|8443") >= 0 && steps2.IndexOf("STEP_VERIFIED|8443") < steps2.IndexOf("STEP_UPDATED"));

                // an update whose server does not come back
                Installer.Answers = (port, pin, say) => { throw new InvalidOperationException("ERR_NO_ANSWER"); };
                File.WriteAllText(Path.Combine(pkg, "OnlineBackup.Server.exe"), "v3");
                var steps3 = new List<string>();
                var e = Assert.Throws<InvalidOperationException>(() => Installer.Run(new SetupAnswers(), pkg, steps3.Add));
                Assert.Equal("ERR_NO_ANSWER", e.Message);
                Assert.DoesNotContain("STEP_UPDATED", steps3);
            }
            finally { Installer.Answers = null; Environment.SetEnvironmentVariable("OB_SETUP_ROOT", null); try { Directory.Delete(root, true); } catch (Exception) { } }
        }
    }
}
