using System;
using OnlineBackup.Core;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace OnlineBackup.Agent
{
    /// <summary>
    /// Volume Shadow Copy: a consistent, point-in-time view of every volume in the selection, so open files (Outlook PST,
    /// accounting databases) are read in a valid state. Uses the tools built into Windows (WMI Win32_ShadowCopy through
    /// wmic, and a directory link to the snapshot device). Windows Server 2003 and later; client Windows Vista and later.
    /// Without VSS the run continues and logs a warning.
    /// </summary>
    public sealed class Vss : IDisposable
    {
        readonly Dictionary<string, string> links = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // "C:" → link folder
        readonly List<string> ids = new List<string>();

        public static bool Supported { get { return Environment.OSVersion.Platform == PlatformID.Win32NT && Environment.OSVersion.Version.Major >= 5; } }

        public static TimeSpan RetryDelay = TimeSpan.FromSeconds(10);
        string pending;   // bug 131: the snapshots (and links) not removed yet, so the next run removes them when this one was killed

        public static Vss Create(IEnumerable<string> sources, Action<string> warn, string pending = null)
        {
            if (pending != null) RemoveLeftovers(pending);
            var v = new Vss { pending = pending };
            foreach (var vol in sources.Select(s => Path.GetPathRoot(s)).Where(r => !string.IsNullOrEmpty(r) && r.Length >= 2 && r[1] == ':').Select(r => r.Substring(0, 2).ToUpperInvariant()).Distinct())
            {
                try
                {
                    string id, dev;
                    // bug 132: Windows makes one snapshot at a time - another set (or program) taking its own at that moment made
                    // this one fail, and the held files were not read; it is tried again, and only the last failure is reported
                    int attempt = 1;
                    while (!Snapshot(vol, out id, out dev, attempt < 3 ? (Action<string>)(m => { }) : warn) && attempt < 3) { attempt++; System.Threading.Thread.Sleep(RetryDelay); }
                    if (id.Length == 0 || dev.Length == 0) continue;
                    v.ids.Add(id); v.Note();
                    var link = Path.Combine(Path.GetTempPath(), "obvss_" + vol[0] + "_" + Guid.NewGuid().ToString("N").Substring(0, 6));
                    Run("cmd.exe", "/c mklink /d \"" + link + "\" \"" + dev + "\\\"");
                    if (!Directory.Exists(link)) { warn("Shadow Copy of " + vol + " created but not reachable; reading live files"); continue; }
                    v.links[vol] = link; v.Note();
                }
                catch (Exception e) { warn("Shadow Copy of " + vol + " failed: " + e.Message); }
            }
            return v.links.Count == 0 && v.ids.Count == 0 ? null : v;
        }

        /// <summary>C:\Data\a.pst → &lt;snapshot link&gt;\Data\a.pst (or the live path when the volume has no snapshot).</summary>
        public string Map(string path)
        {
            var root = Path.GetPathRoot(path);
            if (root == null || root.Length < 2) return path;
            string link;
            return links.TryGetValue(root.Substring(0, 2), out link) ? Path.Combine(link, path.Substring(root.Length)) : path;
        }

        public void Dispose()
        {
            foreach (var l in links.Values) try { Directory.Delete(l); } catch (Exception) { }
            foreach (var id in ids) try { Run("vssadmin", "delete shadows /shadow=" + id + " /quiet"); } catch (Exception) { }
            if (pending != null) try { File.Delete(pending); } catch (Exception) { }
        }

        void Note()
        {
            if (pending != null) try { File.WriteAllLines(pending, ids.Concat(links.Values.Select(l => "link " + l)).ToArray()); } catch (Exception) { }
        }

        /// <summary>bug 131: what a killed run (power cut, task manager) left - its shadow copies and links - is removed first.</summary>
        static void RemoveLeftovers(string pending)
        {
            string[] lines;
            try { if (!File.Exists(pending)) return; lines = File.ReadAllLines(pending); } catch (Exception) { return; }
            foreach (var l in lines)
                try
                {
                    if (l.StartsWith("link ", StringComparison.Ordinal)) Directory.Delete(l.Substring(5));   // the link itself (its shadow may be gone)
                    else if (Regex.IsMatch(l, "^\\{[0-9A-Fa-f-]{36}\\}$")) Run("vssadmin", "delete shadows /shadow=" + l + " /quiet");
                }
                catch (Exception) { }
            try { File.Delete(pending); } catch (Exception) { }
        }

        // wmic on the Windows that have it (2003 … 2022, 10); PowerShell's CIM on the newest, where Microsoft removed wmic
        // (Windows Server 2025, Windows 11 24H2 — found by the Windows test run)
        static string Wmic { get { var p = Path.Combine(Environment.SystemDirectory, "wbem", "WMIC.exe"); return File.Exists(p) ? p : null; } }

        static bool Snapshot(string vol, out string id, out string dev, Action<string> warn)
        {
            id = dev = "";
            string outp;
            if (Wmic != null)
            {
                outp = Run(Wmic, "shadowcopy call create Volume='" + vol + "\\'");
                id = Regex.Match(outp, "ShadowID = \"(\\{[^}]+\\})\"").Groups[1].Value;
                if (id.Length > 0) dev = Regex.Match(Run(Wmic, "shadowcopy where \"ID='" + id + "'\" get DeviceObject /value"), "DeviceObject=(\\S+)").Groups[1].Value;
            }
            else
            {
                var ps = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
                var script = "$r = Invoke-CimMethod -ClassName Win32_ShadowCopy -MethodName Create -Arguments @{Volume='" + vol + "\\';Context='ClientAccessible'}; " +
                    "if ($r.ReturnValue -ne 0) { 'ERR=' + $r.ReturnValue } else { 'ID=' + $r.ShadowID; 'DEV=' + (Get-CimInstance Win32_ShadowCopy -Filter (\"ID='\" + $r.ShadowID + \"'\")).DeviceObject }";
                outp = Run(ps, "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + script.Replace("\"", "\\\"") + "\"");
                id = Regex.Match(outp, "ID=(\\{[^}]+\\})").Groups[1].Value;
                dev = Regex.Match(outp, "DEV=(\\S+)").Groups[1].Value;
            }
            if (id.Length == 0 || dev.Length == 0) { warn("Shadow Copy of " + vol + " could not be created: " + outp.Trim()); return false; }
            return true;
        }

        static string Run(string exe, string args)
        {
            // R1 (GPT audit 7): the old ReadToEnd waited for ever; now the limit is real, and a snapshot tool that did not
            // finish is a clear failure ("no Shadow Copy"), never an empty answer read as "no id"
            var r = ProcessRunner.Run(new ProcessStartInfo(exe, args), Limits.ShadowCopy);
            if (r.TimedOut) throw new TimeoutException("the Shadow Copy service did not answer in " + ProcessRunner.Describe(Limits.ShadowCopy));
            return r.Out + r.Err;
        }
    }
}
