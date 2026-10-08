using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text;
using OnlineBackup.Agent;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// NET48-MIGRATION-PLAN 5.2.5: the .NET Framework build must hold the Windows-only parts that '#if NET40' used to select.
    /// A retarget that changes only the TFM compiles once 10 errors are fixed - and silently ships an agent without the
    /// Windows service, with its secrets NOT protected by DPAPI and without the permission (ACL) read. These checks fail then.
    /// </summary>
    public class Net48GuardTests
    {
        [Fact]
        public void TheAgentAssembly_IsTheNet48Build()
        {
            var tf = typeof(AgentHome).Assembly.GetCustomAttribute<TargetFrameworkAttribute>();
            Assert.NotNull(tf);
            Assert.Equal(".NETFramework,Version=v4.8", tf.FrameworkName);
        }

        [Fact]
        public void TheWindowsService_IsInTheBuild()
        {
            var t = typeof(AgentHome).Assembly.GetType("OnlineBackup.Agent.AgentService");
            Assert.True(t != null, "AgentService (the Windows service) is missing from the .NET Framework build");
            Assert.True(typeof(System.ServiceProcess.ServiceBase).IsAssignableFrom(t), "AgentService is not a ServiceBase");
            Assert.NotNull(typeof(AgentHome).Assembly.GetType("OnlineBackup.Agent.SetupForm"));
            Assert.NotNull(typeof(AgentHome).Assembly.GetType("OnlineBackup.Agent.ClientForm"));
        }

        [Fact]
        public void Secrets_AreProtectedByDpapi_OnWindows()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) throw NotTested.Because("DPAPI exists only on Windows");
            var dir = Path.Combine(Path.GetTempPath(), "obguard-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                var home = new AgentHome(dir);
                const string secret = "guard-secret-value-1234567890";
                home.SaveSecret("guard", secret);
                var raw = File.ReadAllBytes(Path.Combine(dir, "secrets", "guard.bin"));
                Assert.False(Encoding.UTF8.GetString(raw).Contains(secret), "the secret is stored in clear text (no DPAPI)");
                Assert.Equal(secret, home.LoadSecret("guard"));
            }
            finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
        }

        [Fact]
        public void Attributes_IncludeThePermissions_OnWindows()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) throw NotTested.Because("NTFS permissions exist only on Windows");
            var f = System.IO.Path.GetTempFileName();
            try
            {
                var with = Scanner.Attributes(new FileInfo(f), true);
                var without = Scanner.Attributes(new FileInfo(f), false);
                Assert.True(with != without, "the permission (SDDL) read is missing: " + with);
            }
            finally { File.Delete(f); }
        }
    }
}
