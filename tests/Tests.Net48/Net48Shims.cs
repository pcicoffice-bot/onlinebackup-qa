// PROTOTYPE (NET48-MIGRATION-PLAN.md), compiled ONLY into Tests.Net48: the shared test sources (tests/Tests/*.cs, linked
// unchanged) call a few .NET 8 APIs that .NET Framework 4.8 lacks. A type declared in the tests' own namespace wins over
// the System.IO / System one the files import, so these forwarders stand in for them here and nowhere else. Each member
// forwards to the framework one; the four additions (GetRelativePath, CreateSymbolicLink x2, IsPrivilegedProcess) are
// implemented with the Win32 / framework calls the .NET 8 versions use. Nothing in the product is touched.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using IO = System.IO;

namespace OnlineBackup.Tests
{
    public static class Path
    {
        public static readonly char DirectorySeparatorChar = IO.Path.DirectorySeparatorChar;
        public static readonly char AltDirectorySeparatorChar = IO.Path.AltDirectorySeparatorChar;
        public static string Combine(params string[] parts) { return IO.Path.Combine(parts); }
        public static string GetTempPath() { return IO.Path.GetTempPath(); }
        public static string GetFullPath(string p) { return IO.Path.GetFullPath(p); }
        public static string GetDirectoryName(string p) { return IO.Path.GetDirectoryName(p); }
        public static string GetFileName(string p) { return IO.Path.GetFileName(p); }
        public static string GetFileNameWithoutExtension(string p) { return IO.Path.GetFileNameWithoutExtension(p); }
        public static string GetExtension(string p) { return IO.Path.GetExtension(p); }
        public static string GetPathRoot(string p) { return IO.Path.GetPathRoot(p); }
        public static string GetRandomFileName() { return IO.Path.GetRandomFileName(); }
        public static char[] GetInvalidFileNameChars() { return IO.Path.GetInvalidFileNameChars(); }
        public static bool IsPathRooted(string p) { return IO.Path.IsPathRooted(p); }

        /// <summary>.NET 8's Path.GetRelativePath for the case the tests use (a path under, or equal to, the base folder; Windows: case-insensitive); otherwise by the URI of both.</summary>
        public static string GetRelativePath(string relativeTo, string path)
        {
            var b = IO.Path.GetFullPath(relativeTo).TrimEnd('\\', '/');
            var p = IO.Path.GetFullPath(path);
            var cmp = Environment.OSVersion.Platform == PlatformID.Win32NT ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (string.Equals(b, p.TrimEnd('\\', '/'), cmp)) return ".";
            if (p.StartsWith(b + IO.Path.DirectorySeparatorChar, cmp)) return p.Substring(b.Length + 1);
            var rel = new Uri(b + IO.Path.DirectorySeparatorChar).MakeRelativeUri(new Uri(p));
            return Uri.UnescapeDataString(rel.ToString()).Replace('/', IO.Path.DirectorySeparatorChar);
        }
    }

    static class Native
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CreateSymbolicLinkW(string link, string target, int flags);
        /// <summary>As .NET 8: SYMBOLIC_LINK_FLAG_ALLOW_UNPRIVILEGED_CREATE first, then without it (older Windows); a failure is an IOException.</summary>
        public static void Symlink(string link, string target, bool dir)
        {
            int f = dir ? 1 : 0;
            if (CreateSymbolicLinkW(link, target, f | 2)) return;
            if (CreateSymbolicLinkW(link, target, f)) return;
            var e = Marshal.GetLastWin32Error();
            if (e == 5 || e == 1314) throw new UnauthorizedAccessException("CreateSymbolicLink: error " + e + " (" + link + ")");
            throw new IO.IOException("CreateSymbolicLink: error " + e + " (" + link + ")");
        }
    }

    public static class Directory
    {
        public static IO.DirectoryInfo CreateDirectory(string p) { return IO.Directory.CreateDirectory(p); }
        public static void Delete(string p) { IO.Directory.Delete(p); }
        public static void Delete(string p, bool recursive) { IO.Directory.Delete(p, recursive); }
        public static bool Exists(string p) { return IO.Directory.Exists(p); }
        public static string[] GetDirectories(string p) { return IO.Directory.GetDirectories(p); }
        public static string[] GetDirectories(string p, string pattern) { return IO.Directory.GetDirectories(p, pattern); }
        public static string[] GetDirectories(string p, string pattern, IO.SearchOption o) { return IO.Directory.GetDirectories(p, pattern, o); }
        public static string[] GetFileSystemEntries(string p) { return IO.Directory.GetFileSystemEntries(p); }
        public static string[] GetFileSystemEntries(string p, string pattern, IO.SearchOption o) { return IO.Directory.GetFileSystemEntries(p, pattern, o); }
        public static string[] GetFiles(string p) { return IO.Directory.GetFiles(p); }
        public static string[] GetFiles(string p, string pattern) { return IO.Directory.GetFiles(p, pattern); }
        public static string[] GetFiles(string p, string pattern, IO.SearchOption o) { return IO.Directory.GetFiles(p, pattern, o); }
        public static IEnumerable<string> EnumerateFiles(string p, string pattern, IO.SearchOption o) { return IO.Directory.EnumerateFiles(p, pattern, o); }
        public static void Move(string a, string b) { IO.Directory.Move(a, b); }
        public static IO.FileSystemInfo CreateSymbolicLink(string path, string target) { Native.Symlink(path, target, true); return new IO.DirectoryInfo(path); }
    }

    public static class File
    {
        public static void AppendAllText(string p, string t) { IO.File.AppendAllText(p, t); }
        public static void Delete(string p) { IO.File.Delete(p); }
        public static bool Exists(string p) { return IO.File.Exists(p); }
        public static DateTime GetLastWriteTimeUtc(string p) { return IO.File.GetLastWriteTimeUtc(p); }
        public static void SetLastWriteTimeUtc(string p, DateTime t) { IO.File.SetLastWriteTimeUtc(p, t); }
        public static IO.FileAttributes GetAttributes(string p) { return IO.File.GetAttributes(p); }
        public static void SetAttributes(string p, IO.FileAttributes a) { IO.File.SetAttributes(p, a); }
        public static byte[] ReadAllBytes(string p) { return IO.File.ReadAllBytes(p); }
        public static string[] ReadAllLines(string p) { return IO.File.ReadAllLines(p); }
        public static string ReadAllText(string p) { return IO.File.ReadAllText(p); }
        public static string ReadAllText(string p, Encoding e) { return IO.File.ReadAllText(p, e); }
        public static void WriteAllBytes(string p, byte[] b) { IO.File.WriteAllBytes(p, b); }
        public static void WriteAllLines(string p, IEnumerable<string> l) { IO.File.WriteAllLines(p, l); }
        public static void WriteAllText(string p, string t) { IO.File.WriteAllText(p, t); }
        public static void WriteAllText(string p, string t, Encoding e) { IO.File.WriteAllText(p, t, e); }
        public static void Replace(string a, string b, string c) { IO.File.Replace(a, b, c); }
        public static void Copy(string a, string b) { IO.File.Copy(a, b); }
        public static void Copy(string a, string b, bool over) { IO.File.Copy(a, b, over); }
        public static void Move(string a, string b) { IO.File.Move(a, b); }
        public static IO.FileStream Open(string p, IO.FileMode m, IO.FileAccess a, IO.FileShare s) { return IO.File.Open(p, m, a, s); }
        public static IO.FileStream OpenRead(string p) { return IO.File.OpenRead(p); }
        public static IO.FileStream Create(string p) { return IO.File.Create(p); }
        public static IO.FileSystemInfo CreateSymbolicLink(string path, string target) { Native.Symlink(path, target, false); return new IO.FileInfo(path); }
    }

    public static class Environment
    {
        public static OperatingSystem OSVersion { get { return System.Environment.OSVersion; } }
        public static string NewLine { get { return System.Environment.NewLine; } }
        public static string MachineName { get { return System.Environment.MachineName; } }
        public static int ProcessorCount { get { return System.Environment.ProcessorCount; } }
        public static string GetEnvironmentVariable(string n) { return System.Environment.GetEnvironmentVariable(n); }
        public static void SetEnvironmentVariable(string n, string v) { System.Environment.SetEnvironmentVariable(n, v); }
        public static string GetFolderPath(System.Environment.SpecialFolder f) { return System.Environment.GetFolderPath(f); }
        /// <summary>As .NET 8: an administrator (elevated) on Windows.</summary>
        public static bool IsPrivilegedProcess
        {
            get
            {
                using (var id = System.Security.Principal.WindowsIdentity.GetCurrent())
                    return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
        }
    }

    /// <summary>The one member of the net8 test Env (EndToEndTests.cs, which needs the server) that AgentRig uses.</summary>
    public static class Env
    {
        public static string Rel(string source) { return source.Replace(":", "").TrimStart('\\', '/'); }
    }
}
