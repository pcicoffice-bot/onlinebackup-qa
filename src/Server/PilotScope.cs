using OnlineBackup.Core;

namespace OnlineBackup.Server
{
    /// <summary>
    /// PILOT-010: the server's side of the pilot "Windows File Backup" (Core.Scope). The switch is system.xml
    /// SYSTEM/@SCOPE="PILOT", set on the server itself ("OnlineBackup.Server scope --system-home DIR pilot|full") — no web
    /// route changes it. With it, a refusal is 403 SCOPE with the reason ("... is not supported in this version").
    /// </summary>
    public static class PilotScope
    {
        public static ApiException Refused(string what) { return new ApiException(403, Scope.Code, Scope.Refused(what)); }

        /// <summary>Refuses a feature outside the pilot (nothing without the switch).</summary>
        public static void Check(SystemConfig cfg, string what) { if (cfg.Pilot) throw Refused(what); }

        /// <summary>Refuses a set outside the pilot — to create, change, copy to another computer or run (nothing without the switch).</summary>
        public static void CheckSet(SystemConfig cfg, BackupSetInfo s)
        {
            if (!cfg.Pilot) return;
            var why = Scope.Refusal(s);
            if (why != null) throw new ApiException(403, Scope.Code, why);
        }

        /// <summary>The reason a set is outside the pilot, only with the switch (for the admin site and the nightly maintenance).</summary>
        public static string Why(SystemConfig cfg, BackupSetInfo s) { return cfg.Pilot ? Scope.Refusal(s) : null; }

        /// <summary>AG-08: a computer on Windows XP / 2003 (its X-Agent) is refused with the switch.</summary>
        public static void CheckAgent(SystemConfig cfg, string agentInfo) { if (cfg.Pilot && Scope.OldWindows(agentInfo)) throw new ApiException(403, Scope.Code, Scope.OldWindowsMessage); }

        public const string Restic = "The restic engine";
        public const string WebRestore = "Restore from the website (restic sets only)";
        public const string Replication = "Replication to a second server";
        public const string Signup = "Opening a new account from the client software";
        public const string MoveComputer = "Moving a computer to another customer";
        public const string Ai = "The AI assistant (explanations, insights and forecasts)";
    }
}
