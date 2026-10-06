using System;
using System.Collections.Generic;
using System.Linq;

namespace OnlineBackup.Agent
{
    /// <summary>
    /// The folders ticked in the folder tree of "New backup" / "Change the backup": a ticked folder is included with
    /// everything under it; a sub-folder of an included one that is unticked is excluded. Without the window, so it is
    /// tested (FolderSelectionComponentTests).
    /// </summary>
    public sealed class FolderSelection
    {
        public readonly List<string> Included = new List<string>(), Excluded = new List<string>();

        /// <summary>Applies a tick / untick of <paramref name="path"/>; false when the line is not a folder (nothing changes).</summary>
        public bool Toggle(string path, bool on)
        {
            // UX-27: "No access to this folder." and the "…" placeholder are lines with no folder: refused (they threw
            // NullReferenceException in the window once a real folder was ticked)
            if (string.IsNullOrEmpty(path)) return false;
            var p = path;
            Included.RemoveAll(x => x.Equals(p, StringComparison.OrdinalIgnoreCase)); Excluded.RemoveAll(x => x.Equals(p, StringComparison.OrdinalIgnoreCase));
            bool insideIncluded = Included.Any(x => p.StartsWith(x.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
            if (on && !insideIncluded) Included.Add(p);
            if (!on && insideIncluded) Excluded.Add(p);
            return true;
        }
    }
}
