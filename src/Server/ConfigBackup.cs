using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using OnlineBackup.Core;

namespace OnlineBackup.Server
{
    /// <summary>
    /// CFGBK-010: the server's own settings, backed up every day (like Ahsay's "System Settings Backup") — the server
    /// settings, contract, policies and templates, service calls, and every customer's profile, computers and kept keys
    /// (the keys stay protected as they are on the server). Never the backups themselves. &lt;system&gt;\config-backups\,
    /// the last 30, and optionally a copy in another folder (a second disk or a network share).
    /// To restore: stop the service, unzip "system" over the System Home and each "users\&lt;login&gt;\db" over that
    /// customer's db folder, start the service.
    /// </summary>
    public static class ConfigBackup
    {
        public const int Keep = 30;
        static readonly string[] SystemParts = { "conf", "contract", "policy", "tickets", "stats" };

        static string Dir(SystemConfig cfg) { return Path.Combine(cfg.SystemHome, "config-backups"); }

        static XElement Settings(SystemConfig cfg)
        {
            var e = cfg.Doc.Root.Element("CONFIG_BACKUP");
            if (e == null) { e = new XElement("CONFIG_BACKUP"); cfg.Doc.Root.Add(e); }
            return e;
        }

        public static string CopyTo(SystemConfig cfg) { var e = cfg.Doc.Root.Element("CONFIG_BACKUP"); return e == null ? "" : (string)e.Attribute("COPY_TO") ?? ""; }

        /// <summary>Makes one backup now; returns its file name.</summary>
        public static string Make(SystemConfig cfg, Users users, DateTime nowUtc, string by, string ip)
        {
            var dir = Dir(cfg); Directory.CreateDirectory(dir);
            var name = "config-" + nowUtc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".zip";
            var tmp = Path.Combine(dir, name + ".tmp");
            using (var fs = File.Create(tmp))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                foreach (var part in SystemParts) AddDir(zip, Path.Combine(cfg.SystemHome, part), "system/" + part);
                foreach (var login in users.Logins()) AddDir(zip, Path.Combine(users.UserDir(login), "db"), "users/" + login + "/db");
                var info = zip.CreateEntry("BACKUP.xml");
                using (var w = new StreamWriter(info.Open()))
                    w.Write(new XElement("CONFIG_BACKUP", new XAttribute("TIME", RunId.UnixMs(nowUtc)), new XAttribute("SERVER", cfg.ServerId ?? ""), new XAttribute("USERS", users.Logins().Count())).ToString());
            }
            var path = Path.Combine(dir, name);
            File.Move(tmp, path);
            var copy = CopyTo(cfg);
            if (copy.Length > 0)
                try { Directory.CreateDirectory(copy); File.Copy(path, Path.Combine(copy, name), true); Purge(copy); }
                catch (Exception e) { SysLog.Write(ip, "System", "warn: settings backup copy to " + copy + " failed: " + e.Message); }
            Purge(dir);
            SysLog.Write(ip, "System", "settings backup " + name + (by != null ? " by " + by : ""));
            return name;
        }

        static void AddDir(ZipArchive zip, string src, string at)
        {
            if (!Directory.Exists(src)) return;
            foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            {
                if (f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                var rel = f.Substring(src.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');
                try
                {
                    var entry = zip.CreateEntry(at + "/" + rel, CompressionLevel.Optimal);
                    using (var i = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var o = entry.Open()) i.CopyTo(o);
                }
                catch (IOException) { }   // a file being written right now: the next backup has it
            }
        }

        static void Purge(string dir)
        {
            foreach (var old in Directory.GetFiles(dir, "config-*.zip").OrderByDescending(x => x, StringComparer.Ordinal).Skip(Keep)) try { File.Delete(old); } catch (IOException) { }
        }

        /// <summary>Once a day (from the maintenance run).</summary>
        public static bool Due(SystemConfig cfg, DateTime nowUtc)
        {
            var dir = Dir(cfg);
            if (!Directory.Exists(dir)) return true;
            var last = Directory.GetFiles(dir, "config-*.zip").Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max();
            return (nowUtc - last).TotalHours >= 23;
        }

        public static Msg List(SystemConfig cfg)
        {
            var m = new Msg().Set("copyTo", CopyTo(cfg)).Set("keep", Keep);
            var dir = Dir(cfg);
            if (Directory.Exists(dir))
                foreach (var f in Directory.GetFiles(dir, "config-*.zip").OrderByDescending(x => x, StringComparer.Ordinal))
                    m.Add("backups", new Msg().Set("name", Path.GetFileName(f)).Set("size", new FileInfo(f).Length).Set("time", RunId.UnixMs(File.GetLastWriteTimeUtc(f))));
            return m;
        }

        public static void Save(SystemConfig cfg, string copyTo, string by, string ip)
        {
            copyTo = (copyTo ?? "").Trim();
            if (copyTo.Length > 0 && (!Path.IsPathRooted(copyTo) || copyTo.IndexOfAny(Path.GetInvalidPathChars()) >= 0)) throw new ApiException(400, "PATH", "Write a full folder path, for example E:\\ServerSettings or \\\\nas\\backup.");
            lock (cfg) { Settings(cfg).SetAttributeValue("COPY_TO", copyTo.Length == 0 ? null : copyTo); cfg.Save(); }
            SysLog.Write(ip, "Admin", by + " settings backup copy folder: " + (copyTo.Length == 0 ? "none" : copyTo));
        }

        /// <summary>The path of one backup, for download — only a name from the list.</summary>
        public static string PathOf(SystemConfig cfg, string name)
        {
            if (string.IsNullOrEmpty(name) || name != Path.GetFileName(name) || !name.StartsWith("config-", StringComparison.Ordinal) || !name.EndsWith(".zip", StringComparison.Ordinal)) throw new ApiException(404, "NOT_FOUND", "Not found.");
            var p = Path.Combine(Dir(cfg), name);
            if (!File.Exists(p)) throw new ApiException(404, "NOT_FOUND", "Not found.");
            return p;
        }
    }
}
