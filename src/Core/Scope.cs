using System;
using System.Collections.Generic;

namespace OnlineBackup.Core
{
    /// <summary>
    /// PILOT-010 (owner): the pilot "Windows File Backup". One switch on the server (system.xml SYSTEM/@SCOPE="PILOT", set
    /// with "OnlineBackup.Server scope --system-home DIR pilot|full"; never from the website) limits the product to backing up
    /// files and folders of Windows computers with this product's own engine. What is outside it is refused, not hidden only:
    /// the server refuses to create or change such a set and refuses the routes of the features outside it; the agent never
    /// runs such a set even when one reaches it (the server tells it the scope in the profile it sends, ROOT/@SERVER_SCOPE);
    /// the admin site and the client window do not offer them. Sets and data made before the switch are kept as they are —
    /// refused and not run, never changed or deleted. Without the switch nothing changes.
    /// </summary>
    public static class Scope
    {
        public const string Pilot = "PILOT";
        public const string Code = "SCOPE";
        public const string Version = " is not supported in this version (Windows file backup).";

        static readonly Dictionary<string, string> TypeNames = new Dictionary<string, string>
        {
            { "MSSQL", "SQL Server backup" }, { "MYSQL", "MySQL backup" }, { "POSTGRESQL", "PostgreSQL backup" }, { "ORACLE", "Oracle backup" },
            { "DOMINO", "HCL Domino backup" }, { "SYSTEMSTATE", "System State backup" }, { "BAREMETAL", "Whole-computer (bare-metal) image backup" },
            { "HYPERV", "Hyper-V backup" }, { "VMWARE", "VMware backup" }, { "M365", "Microsoft 365 backup" }, { "GWS", "Google Workspace backup" }
        };

        public static bool IsPilot(string scope) { return string.Equals(scope, Pilot, StringComparison.OrdinalIgnoreCase); }

        /// <summary>The message of a feature outside the pilot: "&lt;what&gt; is not supported in this version (Windows file backup)."</summary>
        public static string Refused(string what) { return what + Version; }

        /// <summary>Why the set is outside the pilot (null: it is inside — files and folders, own engine, no commands, no local copy).</summary>
        public static string Refusal(BackupSetInfo s)
        {
            if (s == null) return null;
            if (!string.Equals(s.Type ?? "FILE", "FILE", StringComparison.OrdinalIgnoreCase))
            {
                string n; return Refused(TypeNames.TryGetValue((s.Type ?? "").ToUpperInvariant(), out n) ? n : "This backup type (" + s.Type + ")");
            }
            if (string.Equals(s.Engine, "RESTIC", StringComparison.OrdinalIgnoreCase)) return Refused("The restic engine");
            if (s.PreCommands.Count > 0 || s.PostCommands.Count > 0) return Refused("Running commands before or after the backup");
            if (s.LocalCopy || (s.DestMode != null && s.DestMode != "SERVER")) return Refused("A local copy (local disk or network folder)");
            return null;
        }

        /// <summary>AG-08: Windows of the XP / 2003 era (NT 5.x and older) — what the agent reports in X-Agent ("version; OS").</summary>
        public static bool OldWindows(string agentInfo)
        {
            if (string.IsNullOrEmpty(agentInfo)) return false;
            var i = agentInfo.IndexOf("Windows NT ", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return false;
            var rest = agentInfo.Substring(i + 11);
            var dot = rest.IndexOf('.');
            int major;
            return int.TryParse(dot < 0 ? rest.Trim() : rest.Substring(0, dot), out major) && major < 6;
        }

        public const string OldWindowsMessage = "Windows XP / Server 2003 (the .NET 4.0 era)" + Version;
    }
}
