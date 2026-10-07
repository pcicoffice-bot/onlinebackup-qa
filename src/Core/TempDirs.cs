using System;
using System.IO;
using System.Threading;

namespace OnlineBackup.Core
{
    /// <summary>
    /// Bug 101: removing a temporary folder of restored (decrypted) files. A restore puts the original attributes back - on
    /// Windows restic restores C:\Users read-only, and Directory.Delete fails on a read-only folder; the failure was swallowed
    /// and the customer's decrypted files stayed on the disk. Here the read-only attribute is cleared first (on the folder's own
    /// entries only: a link inside is removed as a link, never followed to what it points at), a held file gets a few retries,
    /// and a folder that still could not be removed is reported, never swallowed.
    /// </summary>
    public static class TempDirs
    {
        /// <summary>Removes the folder and everything in it. null when it is gone (or was never there); otherwise why not.</summary>
        public static string Remove(string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return null;
            Exception last = null;
            for (int attempt = 1; attempt <= 5; attempt++)
            {
                try { Writable(path); Directory.Delete(path, true); return null; }
                catch (IOException e) { last = e; }
                catch (UnauthorizedAccessException e) { last = e; }
                if (!Directory.Exists(path)) return null;
                Thread.Sleep(100 * attempt);   // a virus scanner or indexer that still holds a file just written
            }
            return path + " could not be removed: " + last.Message;
        }

        static void Writable(string dir)
        {
            Clear(dir);
            foreach (var f in Directory.GetFiles(dir)) Clear(f);
            foreach (var d in Directory.GetDirectories(dir))
            {
                if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) continue;   // a link: removed as a link, not followed
                Writable(d);
            }
        }

        static void Clear(string p)
        {
            try
            {
                var a = File.GetAttributes(p);
                if ((a & FileAttributes.ReparsePoint) != 0) return;
                if ((a & FileAttributes.ReadOnly) != 0) File.SetAttributes(p, a & ~FileAttributes.ReadOnly);
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }   // the delete that follows says what is wrong
        }
    }
}
