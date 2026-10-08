using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using OnlineBackup.Core;

namespace OnlineBackup.Agent
{
    /// <summary>
    /// DOM-010..030: HCL (IBM / Lotus) Domino. The server stays up: before each run the agent asks Domino to flush its
    /// database cache ("dbcache flush" on the server console), then the data folder, the transaction logs and notes.ini
    /// are backed up through a Volume Shadow Copy. Domino 12.0.2 and later register a VSS writer, so the copy is
    /// application-consistent; without the writer the copy is crash-consistent and Domino's transaction logging
    /// brings the databases to a consistent state when they are opened (a warning in the log says which one applies).
    /// The folders come from notes.ini (Directory=, TRANSLOG_Path=), found in the usual install folders or OB_DOMINO_INI;
    /// the set's own folder list wins when given. OB_DOMINO_CONSOLE replaces the console command in tests.
    /// Restore: chosen databases (.nsf) to a folder, then copied into the data folder under a new name (or over the
    /// original with the database closed: "drop all" / "dbcache flush").
    /// </summary>
    public static class Domino
    {
        public const string Type = "DOMINO";

        public sealed class Install { public string Ini, ProgramDir, DataDir, TranslogDir; }

        public static Install Find()
        {
            var ini = Environment.GetEnvironmentVariable("OB_DOMINO_INI");
            var candidates = new List<string>();
            if (!string.IsNullOrEmpty(ini)) candidates.Add(ini);
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? "" })
                if (root.Length > 0)
                    foreach (var d in new[] { @"HCL\Domino", @"IBM\Domino", @"Lotus\Domino" }) candidates.Add(Path.Combine(root, d, "notes.ini"));
            candidates.Add("/local/notesdata/notes.ini");
            var found = candidates.FirstOrDefault(File.Exists);
            if (found == null) return null;
            var r = new Install { Ini = found, ProgramDir = Path.GetDirectoryName(found) };
            foreach (var line in OnlineBackup.Core.Atomic.ReadAllLines(found, Encoding.Default))
            {
                var i = line.IndexOf('='); if (i <= 0) continue;
                var k = line.Substring(0, i).Trim(); var v = line.Substring(i + 1).Trim();
                if (k.Equals("Directory", StringComparison.OrdinalIgnoreCase)) r.DataDir = v;
                if (k.Equals("TRANSLOG_Path", StringComparison.OrdinalIgnoreCase) && v.Length > 0) r.TranslogDir = Path.IsPathRooted(v) ? v : Path.Combine(r.DataDir ?? r.ProgramDir, v);
            }
            if (string.IsNullOrEmpty(r.DataDir)) r.DataDir = r.ProgramDir;
            return r;
        }

        /// <summary>The folders of a Domino set: the data folder, the transaction logs (when outside it) and notes.ini.</summary>
        public static List<string> DefaultSources()
        {
            var d = Find();
            if (d == null) throw new AgentException(0, "DOMINO", "Domino was not found on this computer (notes.ini). Choose its data folder.");
            var list = new List<string> { d.DataDir };
            if (!string.IsNullOrEmpty(d.TranslogDir) && !Under(d.TranslogDir, d.DataDir)) list.Add(d.TranslogDir);
            if (!Under(d.Ini, d.DataDir)) list.Add(d.Ini);
            return list;
        }

        static bool Under(string path, string dir)
        {
            var a = Path.GetFullPath(path).TrimEnd('\\', '/'); var b = Path.GetFullPath(dir).TrimEnd('\\', '/');
            return a.Equals(b, StringComparison.OrdinalIgnoreCase) || a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>DOM-020: before the copy — flush Domino's cache and say whether the copy will be application-consistent.</summary>
        public static void Prepare(Action<string> info, Action<string> warn)
        {
            var d = Find();
            var console = Environment.GetEnvironmentVariable("OB_DOMINO_CONSOLE");
            string exe = console, args = "-c \"dbcache flush\"";
            if (string.IsNullOrEmpty(exe) && d != null)
            {
                exe = Environment.OSVersion.Platform == PlatformID.Win32NT ? Path.Combine(d.ProgramDir, "nserver.exe") : "/opt/hcl/domino/bin/server";
                if (!File.Exists(exe)) exe = null;
            }
            if (exe == null) warn("[Domino] the server console was not found: the cache was not flushed");
            else
                try
                {
                    var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                    if (d != null) psi.WorkingDirectory = d.DataDir;
                    var r = ProcessRunner.Run(psi, Limits.Short);   // R1: the 2-minute limit really applies now
                    if (r.TimedOut) warn("[Domino] dbcache flush did not answer in " + ProcessRunner.Describe(Limits.Short));
                    else if (r.Code != 0) warn("[Domino] dbcache flush exit " + r.Code);
                    else info("[Domino] database cache flushed");
                }
                catch (Exception e) { warn("[Domino] dbcache flush: " + e.Message); }
            if (Environment.OSVersion.Platform == PlatformID.Win32NT && Environment.GetEnvironmentVariable("OB_DOMINO_CONSOLE") == null)
                try
                {
                    var o = ProcessRunner.Run(new ProcessStartInfo("vssadmin", "list writers"), Limits.Short).Out;
                    if (o.IndexOf("Domino", StringComparison.OrdinalIgnoreCase) >= 0) info("[Domino] Domino VSS writer present: application-consistent copy");
                    else warn("[Domino] no Domino VSS writer (Domino 12.0.2+ has one): the copy is crash-consistent — keep transaction logging on");
                }
                catch (Exception e) { warn("[Domino] VSS writers not checked: " + e.Message); }
        }
    }
}
