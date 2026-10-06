using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OnlineBackup.Agent
{
    /// <summary>
    /// SRC-030: the folders of this computer, for choosing what to back up from the admin site (like the tree of Acronis and
    /// MSP360 — Ahsay's web console has only typed paths). Folder names only, never files or their contents: the drives
    /// three levels deep, the set sources and every folder the IT company opened, at most 15,000 folders, shallow levels first.
    /// </summary>
    public static class FolderScan
    {
        public const int Max = 15000;

        public static List<string> Roots()
        {
            if (Path.DirectorySeparatorChar == '\\')
            {
                var l = new List<string>();
                foreach (var d in DriveInfo.GetDrives())
                {
                    try { if (d.DriveType == DriveType.Fixed && d.IsReady) l.Add(d.RootDirectory.FullName); } catch (Exception) { }
                }
                return l;
            }
            return new List<string> { "/" };
        }

        static readonly string[] SkipUnix = { "proc", "sys", "dev", "run", "snap" };

        /// <summary>Breadth first under each start folder, to its depth; the list is sorted, without duplicates.</summary>
        public static List<string> Scan(IEnumerable<KeyValuePair<string, int>> starts, int max = Max)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var q = new Queue<KeyValuePair<string, int>>();
            foreach (var s in starts) if (Directory.Exists(s.Key)) { q.Enqueue(s); seen.Add(s.Key); }
            while (q.Count > 0 && seen.Count < max)
            {
                var cur = q.Dequeue();
                if (cur.Value <= 0) continue;
                string[] subs;
                try { subs = Directory.GetDirectories(cur.Key); } catch (Exception) { continue; }   // no access: the folder shows without children
                Array.Sort(subs, StringComparer.OrdinalIgnoreCase);
                foreach (var d in subs)
                {
                    if (seen.Count >= max) break;
                    var name = Path.GetFileName(d);
                    if (name.StartsWith("$", StringComparison.Ordinal) || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)) continue;
                    if (cur.Key == "/" && SkipUnix.Contains(name)) continue;
                    try
                    {
                        var a = File.GetAttributes(d);
                        if ((a & FileAttributes.ReparsePoint) != 0) continue;   // junctions ("Documents and Settings") would loop
                        if ((a & FileAttributes.System) != 0 && (a & FileAttributes.Hidden) != 0) continue;
                    }
                    catch (Exception) { continue; }
                    if (seen.Add(d)) q.Enqueue(new KeyValuePair<string, int>(d, cur.Value - 1));
                }
            }
            var l = seen.ToList(); l.Sort(StringComparer.OrdinalIgnoreCase);
            return l;
        }
    }
}
