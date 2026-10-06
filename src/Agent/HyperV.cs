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
    /// HV-010..040: Hyper-V virtual machines with Windows' own Export-VM (a running VM is exported online through a
    /// production checkpoint — consistent, application-aware — on Windows Server 2016 and later). Each VM is exported
    /// into a fixed folder (&lt;staging&gt;\&lt;VM&gt;), replacing the previous export only after a complete new one, so the
    /// set backs up stable paths: restic sends only the changed blocks of the virtual disks.
    /// Sources: VM names (none / "*" = every VM of the host). Staging: the set's working folder, else the agent's.
    /// Restore: the export folder back to disk, then Import-VM (-Copy -GenerateNewId: next to the original if it still exists).
    /// OB_POWERSHELL replaces powershell.exe in tests.
    /// </summary>
    public static class HyperV
    {
        public const string Type = "HYPERV";

        static string Ps { get { var e = Environment.GetEnvironmentVariable("OB_POWERSHELL"); return string.IsNullOrEmpty(e) ? "powershell.exe" : e; } }

        public static string Run(string script)
        {
            var psi = new ProcessStartInfo(Ps, "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + script.Replace("\"", "\\\"") + "\"")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
            var r = ProcessRunner.Run(psi, Limits.HyperV);   // R1: a stuck export stops at the limit
            if (r.TimedOut) throw new TimeoutException("PowerShell (Hyper-V) did not finish in " + ProcessRunner.Describe(Limits.HyperV) + " and was stopped");
            var e2 = r.Err.Trim();
            if (r.Code != 0 || e2.Length > 0) throw new InvalidOperationException("PowerShell: " + (e2.Length > 0 ? e2 : "exit " + r.Code));
            return r.Out;
        }

        static string Q(string s) { return "'" + (s ?? "").Replace("'", "''") + "'"; }

        public static string Staging(AgentHome home, BackupSetInfo set)
        {
            return string.IsNullOrEmpty(set.WorkingDir) ? Path.Combine(home.SetDir(set.Id), "hyperv") : Path.Combine(set.WorkingDir, "hyperv-" + set.Id);
        }

        public static List<string> Vms()
        {
            return Run("Get-VM | ForEach-Object { $_.Name }").Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        }

        /// <summary>HV-020: exports the chosen VMs; returns the staging folder. A VM that fails keeps its last export.</summary>
        public static string Export(AgentHome home, BackupSetInfo set, Action<string> info, Action<string> warn)
        {
            var staging = Staging(home, set);
            Directory.CreateDirectory(staging);
            var vms = set.Sources.Where(s => !string.IsNullOrWhiteSpace(s) && s != "*").Select(s => s.Trim()).ToList();
            if (vms.Count == 0) vms = Vms();
            if (vms.Count == 0) throw new AgentException(0, "HYPERV", "No virtual machine found on this host");
            info("[Hyper-V] virtual machines: " + string.Join(", ", vms.ToArray()));
            int ok = 0;
            foreach (var vm in vms)
            {
                var name = M365Sync.Safe(vm, 100);
                var final = Path.Combine(staging, name); var tmp = Path.Combine(staging, name + ".new");
                try
                {
                    if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
                    Directory.CreateDirectory(tmp);
                    var t0 = SystemClock.UtcNow;
                    Run("$ErrorActionPreference='Stop'; Export-VM -Name " + Q(vm) + " -Path " + Q(tmp));
                    var exported = Directory.GetDirectories(tmp).FirstOrDefault() ?? tmp;   // Export-VM writes <path>\<VM name>\…
                    if (!Directory.EnumerateFiles(exported, "*", SearchOption.AllDirectories).Any()) throw new InvalidOperationException("the export is empty");
                    if (Directory.Exists(final)) Directory.Delete(final, true);
                    Directory.Move(exported, final);
                    if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
                    long size = Directory.EnumerateFiles(final, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
                    info("[Hyper-V] " + vm + ": exported " + size + " bytes in " + (int)(SystemClock.UtcNow - t0).TotalSeconds + " s");
                    ok++;
                }
                catch (Exception e)
                {
                    warn("[Hyper-V] " + vm + " not exported (the last export is kept): " + e.Message);
                    try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch (IOException) { }
                }
            }
            if (ok == 0) throw new AgentException(0, "HYPERV", "No virtual machine was exported");
            return staging;
        }

        /// <summary>HV-030: imports a restored export folder as a new VM (new id, files copied under the target).</summary>
        public static string Import(string exportFolder, string target)
        {
            var vmcx = Directory.GetFiles(exportFolder, "*.vmcx", SearchOption.AllDirectories).FirstOrDefault()
                       ?? Directory.GetFiles(exportFolder, "*.xml", SearchOption.AllDirectories).FirstOrDefault(f => f.Contains("Virtual Machines"));
            if (vmcx == null) throw new AgentException(0, "HYPERV", "No VM configuration in " + exportFolder);
            return Run("$ErrorActionPreference='Stop'; (Import-VM -Path " + Q(vmcx) + " -Copy -GenerateNewId -VirtualMachinePath " + Q(Path.Combine(target, "Virtual Machines"))
                + " -VhdDestinationPath " + Q(Path.Combine(target, "Virtual Hard Disks")) + " -SnapshotFilePath " + Q(Path.Combine(target, "Snapshots")) + ").Name").Trim();
        }
    }
}
