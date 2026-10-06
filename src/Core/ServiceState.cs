using System;
using System.Text.RegularExpressions;
using System.Threading;

namespace OnlineBackup.Core
{
    /// <summary>
    /// A Windows service that was just started: wait until Windows says it RUNS. UX-17 (the UX review): the client setup
    /// said "is installed" whatever happened to its service; the same was true of the server's service and of the server
    /// update. One check for all of them; anything but RUNNING is an error naming the state.
    /// </summary>
    public static class ServiceState
    {
        /// <summary>RUNNING / STOPPED / START_PENDING … from "sc query", or "" when there is no such service.</summary>
        public static string Of(string scQuery)
        {
            var m = Regex.Match(scQuery ?? "", @"STATE\s*:\s*\d+\s+([A-Z_]+)");
            return m.Success ? m.Groups[1].Value : "";
        }

        /// <summary>Polls "sc query &lt;name&gt;" through <paramref name="sc"/> once a second (fast in tests) up to
        /// <paramref name="seconds"/>; returns when RUNNING, throws otherwise (a service that stopped again after its start
        /// will not run by itself).</summary>
        public static void WaitRunning(Func<string, string> sc, string name, int seconds, bool fast = false, string output = "")
        {
            var state = "";
            for (int i = 0; i <= seconds; i++)
            {
                state = Of(sc("query " + name));
                if (state == "RUNNING") return;
                if (state == "STOPPED" && i >= 3) break;
                if (i < seconds) Thread.Sleep(fast ? 10 : 1000);
            }
            throw new InvalidOperationException("The service " + name + " did not start: " + (state.Length > 0 ? state : "not created") + ". " + (output ?? "").Trim());
        }
    }
}
