using System;
using System.Diagnostics;
using System.Threading;

namespace OnlineBackup.Agent
{
#if NETFRAMEWORK
    /// <summary>The Windows service of the agent (Local System, automatic start, restarts on failure).</summary>
    public sealed class AgentService : System.ServiceProcess.ServiceBase
    {
        public const string Name = "OnlineBackupAgent";
        readonly AgentApp app;
        readonly CancellationTokenSource stop = new CancellationTokenSource();
        Thread worker;

        public AgentService(AgentApp app) { this.app = app; ServiceName = Name; CanStop = true; CanShutdown = true; }

        ClientUi ui;

        protected override void OnStart(string[] args)
        {
            worker = new Thread(() => app.ServiceLoop(stop.Token, m => { try { EventLog.WriteEntry(Name, m); } catch (Exception) { } })) { IsBackground = true };
            worker.Start();
            // CLI-015: the customer's screen is served by the service (it alone reads the protected agent folder);
            // the desktop shortcut ("open") reads the address + one-time key from ui.txt beside the agent
            try { ui = ClientUi.StartForService(app); } catch (Exception e) { try { EventLog.WriteEntry(Name, "customer screen not started: " + e.Message); } catch (Exception) { } }
        }

        protected override void OnStop() { stop.Cancel(); if (ui != null) ui.Dispose(); if (worker != null) worker.Join(TimeSpan.FromSeconds(30)); }
        protected override void OnShutdown() { OnStop(); }
    }
#endif

    public static class ServiceSetup
    {
        /// <summary>Tests: answers in place of sc.exe (the arguments in, its output back).</summary>
        public static Func<string, string> ScHook;

        /// <summary>
        /// Creates and starts the service, then waits until Windows says it RUNS (UX-17, the UX review: the setup said
        /// "is installed" in green whatever happened — a service that stopped again at once, or was never created, left
        /// the computer unprotected behind a success page). Any other end is an error naming the state.
        /// </summary>
        public static string Install(string exe, string home, string displayName = "ITSguard Server Online Agent", int waitSeconds = 60)
        {
            displayName = (displayName ?? "ITSguard Server Online Agent").Replace("\"", "");
            var bin = "\"" + exe + "\" service --home \"" + home + "\"";
            var o = Sc("create OnlineBackupAgent binPath= \"" + bin.Replace("\"", "\\\"") + "\" start= auto obj= LocalSystem DisplayName= \"" + displayName + "\"");
            o += Sc("failure OnlineBackupAgent reset= 86400 actions= restart/60000/restart/60000/restart/300000");
            o += Sc("start OnlineBackupAgent");
            OnlineBackup.Core.ServiceState.WaitRunning(Sc, "OnlineBackupAgent", waitSeconds, ScHook != null, o);
            return o + "\nService OnlineBackupAgent: RUNNING";
        }

        /// <summary>SETUP-C70: before the files are replaced (repair, a newer version): the service stopped, waited for (a backup ends cleanly).</summary>
        public static void Stop(int seconds = 90)
        {
            var q = Sc("query OnlineBackupAgent");
            if (!q.Contains("STATE")) return;   // not installed
            Sc("stop OnlineBackupAgent");
            for (int i = 0; i < seconds; i++)
            {
                if (Sc("query OnlineBackupAgent").Contains("STOPPED")) return;
                System.Threading.Thread.Sleep(1000);
            }
        }

        public static string Uninstall() { return Sc("stop OnlineBackupAgent") + Sc("delete OnlineBackupAgent"); }

        static string Sc(string args)
        {
            if (ScHook != null) return ScHook(args) ?? "";
            var r = OnlineBackup.Core.ProcessRunner.Run(new ProcessStartInfo("sc.exe", args), OnlineBackup.Core.Limits.Short);
            return r.Out + r.Err + (r.TimedOut ? " (sc.exe did not answer)" : "");
        }
    }
}
