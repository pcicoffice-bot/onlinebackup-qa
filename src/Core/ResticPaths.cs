using System;

namespace OnlineBackup.Core
{
    /// <summary>Bug 100: restic names a Windows file "/C/Users/..." (the drive letter as the first folder, forward slashes) and
    /// refuses an include that is not absolute in that form ("All path filters must be absolute, starting with a forward
    /// slash"). A path as Windows writes it ("C:\Users\..." - typed, or sent to the restore API) is given to restic in its own
    /// form. Decided by the shape of the path, not by the system this runs on: a server on Linux restores a Windows
    /// computer's backup too. restic's own form, network paths and Linux paths stay as they are.</summary>
    public static class ResticPaths
    {
        public static string Of(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Length < 2 || path[1] != ':' || !IsDriveLetter(path[0])) return path;
            var rest = path.Substring(2).Replace('\\', '/');
            return "/" + char.ToUpperInvariant(path[0]) + (rest.Length == 0 || rest[0] == '/' ? rest : "/" + rest);
        }

        static bool IsDriveLetter(char c) { return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'); }
    }
}
