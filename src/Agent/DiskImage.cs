using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using OnlineBackup.Core;

namespace OnlineBackup.Agent
{
    /// <summary>
    /// Bare-metal backup: an image of the whole computer (every volume Windows needs to start, plus chosen data volumes),
    /// made by Windows' own tool so that it restores with Windows' own recovery — the same method the leaders use on
    /// Windows (wbadmin / Windows Server Backup images are what "System Image Recovery" in Windows RE reads).
    ///   2008 / Vista and later: wbadmin start backup -allCritical to a staging volume or share → WindowsImageBackup\PC\…\*.vhd(x)
    ///   2003 / XP: ntbackup of all fixed volumes + System State to one .bkf (restore: install Windows, then ntbackup restore).
    /// The image files are sent like any large file: content-defined chunks, so a 200GB disk with 1GB changed sends ≈1GB.
    /// The dated folder name ("Backup 2026-10-03 220000") is stored as "Backup" so the same disk keeps one delta chain;
    /// the real name is kept in Bare Metal\image.xml and put back at restore.
    /// Sources: [0] = staging target (a volume not being backed up, e.g. E:, or \\nas\share); [1..] = extra volumes (D:).
    /// </summary>
    public static class DiskImage
    {
        public const string Type = "BAREMETAL";
        public const string Prefix = "Bare Metal";
        const string Images = "WindowsImageBackup";

        static string Env(string n) { var v = Environment.GetEnvironmentVariable(n); return string.IsNullOrEmpty(v) ? null : v; }
        static bool IsWindows { get { return Environment.OSVersion.Platform == PlatformID.Win32NT; } }
        /// <summary>wbadmin (Vista / 2008 and later), otherwise ntbackup. OB_WBADMIN / OB_NTBACKUP replace the tools in tests.</summary>
        static bool Modern { get { return Env("OB_NTBACKUP") == null && (Env("OB_WBADMIN") != null || (IsWindows && Environment.OSVersion.Version.Major >= 6)); } }

        public static IEnumerable<SourceItem> Run(BackupSetInfo set, Action<string> info, Action<string> warn, List<string> unreachable)
        {
            var items = new List<SourceItem>();
            if (!IsWindows && Env("OB_WBADMIN") == null && Env("OB_NTBACKUP") == null) { warn("Bare-metal backup runs on Windows only"); unreachable.Add(Prefix); return items; }
            if (set.Sources.Count == 0) { warn("Bare-metal backup: no staging location"); unreachable.Add(Prefix); return items; }
            var staging = set.Sources[0].Trim();
            if (staging.Length <= 3 && staging.Length >= 1 && char.IsLetter(staging[0]) && (staging.Length == 1 || staging[1] == ':')) staging = staging.Substring(0, 1).ToUpperInvariant() + ":\\";
            var extra = set.Sources.Skip(1).Select(v => v.TrimEnd('\\')).Where(v => v.Length > 0).ToList();
            try
            {
                Directory.CreateDirectory(staging);
                var manifest = new XElement("IMAGE",
                    new XAttribute("COMPUTER", Environment.MachineName), new XAttribute("OS", Environment.OSVersion.VersionString),
                    new XAttribute("CREATED", SystemClock.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)),
                    new XAttribute("EXTRA_VOLUMES", string.Join(",", extra.ToArray())));
                if (Modern) Wbadmin(staging, extra, manifest, items, info);
                else Ntbackup(staging, extra, manifest, items, info);
                var mf = Path.Combine(staging, "image.xml");
                Atomic.WriteText(mf, manifest.ToString());
                items.Add(new SourceItem { Path = Prefix + "\\image.xml", ReadPath = mf, Info = new FileInfo(mf) });
                long total = items.Sum(i => i.Info.Length);
                info("[Bare Metal Backup] Image ready: " + items.Count + " files, " + Fmt(total) + " (only changed blocks are sent)");
            }
            catch (Win32Exception)
            {
                warn("Bare-metal backup: the Windows backup tool is missing — install the \"Windows Server Backup\" feature (Server Manager → Features)");
                unreachable.Add(Prefix); items.Clear();
            }
            catch (Exception e) { warn("Bare-metal backup failed: " + e.Message); unreachable.Add(Prefix); items.Clear(); }
            return items;
        }

        static void Wbadmin(string staging, List<string> extra, XElement manifest, List<SourceItem> items, Action<string> info)
        {
            var tool = Env("OB_WBADMIN") ?? "wbadmin";
            var target = staging.TrimEnd('\\');
            if (target.Length == 1) target += ":";
            var args = "start backup -backupTarget:" + Quote(target) + " -allCritical" + (extra.Count > 0 ? " -include:" + string.Join(",", extra.ToArray()) : "") + " -quiet";
            info("[Bare Metal Backup] wbadmin " + args);
            var started = DateTime.UtcNow;
            Exec(tool, args);
            var root = Path.Combine(staging, Images);
            if (!Directory.Exists(root)) throw new IOException("wbadmin finished but " + root + " was not created");
            var pcs = new DirectoryInfo(root).GetDirectories();
            var pc = pcs.FirstOrDefault(d => d.Name.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                     ?? pcs.OrderByDescending(d => d.LastWriteTimeUtc).FirstOrDefault();
            if (pc == null) throw new IOException("no computer folder in " + root);
            var latest = pc.GetDirectories("Backup *").OrderByDescending(d => d.Name, StringComparer.Ordinal).FirstOrDefault();
            if (latest == null) throw new IOException("no image in " + pc.FullName);
            // Bug 75 (AP-02, Agent B note): the newest image must be this run's — a wbadmin that ended with 0 and wrote
            // nothing made an earlier run's image this run's, as a success
            latest.Refresh();
            var written = new[] { latest.LastWriteTimeUtc }.Concat(latest.GetFiles("*", SearchOption.AllDirectories).Select(f => f.LastWriteTimeUtc)).Max();
            if (written < started.AddSeconds(-2))
                throw new IOException("wbadmin ended without writing a new image: the newest one (" + latest.Name + ") is from an earlier run");
            manifest.Add(new XAttribute("TOOL", "wbadmin"), new XAttribute("IMAGE_COMPUTER", pc.Name), new XAttribute("BACKUP_FOLDER", latest.Name));
            foreach (var f in pc.GetFiles("*", SearchOption.AllDirectories))
            {
                var rel = f.FullName.Substring(pc.FullName.Length + 1).Replace(Path.DirectorySeparatorChar, '\\');
                var top = rel.Split('\\')[0];
                if (top.StartsWith("Backup ", StringComparison.Ordinal))
                {
                    if (top != latest.Name) continue;                        // older versions kept by Windows on the staging volume
                    rel = "Backup" + rel.Substring(top.Length);
                }
                items.Add(new SourceItem { Path = Prefix + "\\" + Images + "\\" + pc.Name + "\\" + rel, ReadPath = f.FullName, Info = f });
                if (f.Extension.Equals(".vhd", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".vhdx", StringComparison.OrdinalIgnoreCase))
                    manifest.Add(new XElement("DISK", new XAttribute("FILE", f.Name), new XAttribute("SIZE", f.Length)));
            }
        }

        static void Ntbackup(string staging, List<string> extra, XElement manifest, List<SourceItem> items, Action<string> info)
        {
            var tool = Env("OB_NTBACKUP") ?? "ntbackup";
            var bkf = Path.Combine(staging, "baremetal.bkf");
            var stagingRoot = Path.GetPathRoot(Path.GetFullPath(staging));
            var vols = IsWindows
                ? DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && !d.Name.Equals(stagingRoot, StringComparison.OrdinalIgnoreCase)).Select(d => d.Name).ToList()
                : new List<string>();
            foreach (var v in extra) { var x = v.TrimEnd('\\') + "\\"; if (!vols.Contains(x, StringComparer.OrdinalIgnoreCase)) vols.Add(x); }
            var bks = Path.Combine(staging, "baremetal.bks");                // ntbackup selection file (UTF-16)
            File.WriteAllText(bks, string.Join("\r\n", vols.Concat(new[] { "SystemState" }).ToArray()) + "\r\n", Encoding.Unicode);
            var args = "backup \"@" + bks + "\" /J \"Bare Metal\" /F \"" + bkf + "\" /SNAP:on /M normal /V:no /L:s";
            info("[Bare Metal Backup] ntbackup " + string.Join(" ", vols.ToArray()) + " SystemState → " + bkf);
            if (File.Exists(bkf)) File.Delete(bkf);                          // /F appends otherwise
            Exec(tool, args);
            if (!File.Exists(bkf)) throw new IOException("ntbackup finished but " + bkf + " was not created");
            manifest.Add(new XAttribute("TOOL", "ntbackup"), new XAttribute("VOLUMES", string.Join(",", vols.ToArray())));
            items.Add(new SourceItem { Path = Prefix + "\\baremetal.bkf", ReadPath = bkf, Info = new FileInfo(bkf) });
        }

        /// <summary>
        /// Restores an image to a drive or share (USB disk, NAS) in the layout Windows' recovery looks for, and writes the
        /// steps beside it. Returns the steps. The target must be the root of a drive or a share for Windows RE to find it.
        /// </summary>
        public static string PrepareRestore(Restore r, string point, string target, Action<string> say)
        {
            if (string.IsNullOrEmpty(target)) throw new AgentException(0, "TARGET", "Choose where to restore the image (a USB drive or a network share).");
            Directory.CreateDirectory(target);
            var work = Path.Combine(target, Prefix);
            say("downloading the image to " + target);
            r.Run(point, target, p => p.StartsWith(Prefix + "\\", StringComparison.OrdinalIgnoreCase), true);
            if (r.Failed > 0) throw new AgentException(0, "RESTORE", "Restoring the image failed for " + r.Failed + " files. Do not use a partial image.");
            var mf = Path.Combine(work, "image.xml");
            if (!File.Exists(mf)) throw new AgentException(0, "RESTORE", "This point has no computer image.");
            var m = OnlineBackup.Core.Atomic.LoadXElement(mf);
            string steps;
            if ((string)m.Attribute("TOOL") == "wbadmin")
            {
                var pc = (string)m.Attribute("IMAGE_COMPUTER");
                var src = Path.Combine(work, Images);
                var dst = Path.Combine(target, Images);
                if (Directory.Exists(dst)) Directory.Delete(dst, true);
                Directory.Move(src, dst);
                var from = Path.Combine(dst, pc, "Backup");
                var to = Path.Combine(dst, pc, (string)m.Attribute("BACKUP_FOLDER"));
                if (Directory.Exists(from) && from != to) Directory.Move(from, to);
                steps =
                    "Whole-computer restore (bare metal) — " + m.Attribute("COMPUTER").Value + " (" + m.Attribute("OS").Value + ")\r\n" +
                    "1. Connect this drive to the computer being restored (or make sure the share is reachable on the network).\r\n" +
                    "2. Boot from installation media of the same Windows version (or the recovery media) and choose 'Repair your computer'.\r\n" +
                    "3. Troubleshoot → System Image Recovery. The image is found automatically in the folder " + Images + ".\r\n" +
                    "   From a network share: Select a system image → Advanced → Search for a system image on the network.\r\n" +
                    "4. Confirm formatting the disks and restore. The computer restarts with the system and data of the backup.\r\n" +
                    "Note: the new disk must be at least as large as the original one. The folder " + Images + " must be at the root of the drive or share.\r\n";
            }
            else
            {
                var bkf = Path.Combine(target, "baremetal.bkf");
                if (File.Exists(bkf)) File.Delete(bkf);
                File.Move(Path.Combine(work, "baremetal.bkf"), bkf);
                steps =
                    "Whole-computer restore — Windows 2003 / XP — " + m.Attribute("COMPUTER").Value + "\r\n" +
                    "1. Install Windows with the same version and Service Pack, the same drive letter and the same computer name.\r\n" +
                    "2. Run ntbackup, Restore, and choose the file baremetal.bkf. Select all drives and System State.\r\n" +
                    "3. Advanced → Replace existing files always. Restore and restart.\r\n";
            }
            Atomic.WriteText(Path.Combine(target, "RESTORE-README.txt"), steps);
            try { Directory.Delete(work, true); } catch (IOException) { }
            say("image ready at " + target);
            return steps;
        }

        static string Quote(string s) { return s.IndexOf(' ') >= 0 ? "\"" + s + "\"" : s; }

        static string Fmt(long b)
        {
            double v = b; string[] u = { "B", "KB", "MB", "GB", "TB" }; int i = 0;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return v.ToString(i == 0 ? "0" : "0.0", CultureInfo.InvariantCulture) + u[i];
        }

        static void Exec(string exe, string args)
        {
            ProcessRunner.Check(new ProcessStartInfo(exe, args), Limits.Image);   // R1: both streams read, killed with its children at the limit
        }
    }
}
