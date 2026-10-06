using System;
using System.Collections.Generic;
using OnlineBackup.Agent;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// The Windows service of the client, installed (ServiceSetup.Install) — component contract (UX review finding UX-17,
    /// confirmed in the code: the Finish page said "is installed" in green whatever happened to the service):
    ///   input    sc.exe answers (simulated): the service created and started → RUNNING; started but STOPPED again (a
    ///            missing file, a crash at start); still START_PENDING for the whole wait; "create" refused (access denied)
    ///   expected RUNNING → the installation goes on; every other case → an error that names the service and its state
    ///            (the setup then shows "The installation did not finish"), never a silent success
    /// </summary>
    public class ServiceInstallComponentTests : IDisposable
    {
        public void Dispose() { ServiceSetup.ScHook = null; OnlineBackup.Server.ServerService.ScHook = null; }

        static Func<string, string> Sc(List<string> calls, params string[] queryAnswers)
        {
            int q = 0;
            return args =>
            {
                calls.Add(args);
                if (args.StartsWith("create")) return "[SC] CreateService SUCCESS";
                if (args.StartsWith("query")) { var a = queryAnswers[Math.Min(q, queryAnswers.Length - 1)]; q++; return "SERVICE_NAME: OnlineBackupAgent\n        STATE              : " + a; }
                return "[SC] OK";
            };
        }

        [Fact]
        public void Running_TheInstallationGoesOn()
        {
            var calls = new List<string>(); ServiceSetup.ScHook = Sc(calls, "2  START_PENDING", "4  RUNNING");
            var o = ServiceSetup.Install(@"C:\P\OnlineBackup.Agent.exe", @"C:\D", "Backup", 5);
            Assert.Contains("create", calls[0]);
            Assert.Contains(calls, c => c.StartsWith("start"));
            Assert.Contains("RUNNING", o);
        }

        [Fact]
        public void StartedButStoppedAgain_IsAnError_NotAnInstalledMessage()
        {
            ServiceSetup.ScHook = Sc(new List<string>(), "2  START_PENDING", "1  STOPPED");
            var e = Assert.ThrowsAny<Exception>(() => ServiceSetup.Install(@"C:\P\OnlineBackup.Agent.exe", @"C:\D", "Backup", 5));
            Assert.Contains("did not start", e.Message);
            Assert.Contains("STOPPED", e.Message);
        }

        [Fact]
        public void NeverRunning_WithinTheWait_IsAnError()
        {
            ServiceSetup.ScHook = Sc(new List<string>(), "2  START_PENDING");
            var e = Assert.ThrowsAny<Exception>(() => ServiceSetup.Install(@"C:\P\OnlineBackup.Agent.exe", @"C:\D", "Backup", 2));
            Assert.Contains("did not start", e.Message);
        }

        [Fact]
        public void CreateRefused_IsAnError()
        {
            ServiceSetup.ScHook = a => a.StartsWith("create") ? "[SC] OpenSCManager FAILED 5:\n\nAccess is denied." : a.StartsWith("query") ? "[SC] EnumQueryServicesStatus:OpenService FAILED 1060" : "[SC] FAILED";
            var e = Assert.ThrowsAny<Exception>(() => ServiceSetup.Install(@"C:\P\OnlineBackup.Agent.exe", @"C:\D", "Backup", 2));
            Assert.Contains("did not start", e.Message);
        }
    
        [Fact]
        public void TheServersService_Too_RunningOrAnError()
        {
            OnlineBackup.Server.ServerService.ScHook = Sc(new List<string>(), "2  START_PENDING", "4  RUNNING");
            OnlineBackup.Server.ServerService.Install(@"C:\P\OnlineBackup.Server.exe", @"C:\S", "https://+:8443/", 5);
            OnlineBackup.Server.ServerService.ScHook = Sc(new List<string>(), "1  STOPPED");
            var e = Assert.ThrowsAny<Exception>(() => OnlineBackup.Server.ServerService.Install(@"C:\P\OnlineBackup.Server.exe", @"C:\S", "https://+:8443/", 5));
            Assert.Contains("did not start", e.Message);
        }
    }
}
