using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Xml.Linq;
using OnlineBackup.Core;

namespace OnlineBackup.Server
{
    public sealed class ApiException : Exception
    {
        public int Status { get; private set; }
        public string Code { get; private set; }
        public ApiException(int status, string code, string message) : base(message) { Status = status; Code = code; }
    }

    /// <summary>
    /// Server logs, each kind in its own folder so a fault is diagnosed from the right file:
    /// System, Access (agent connections, logins, lockouts), Admin (every administrator change), BackupErrors
    /// (errors and warnings of all users, one file a day), Email, Update. No password or key is ever written.
    /// </summary>
    public static class SysLog
    {
        static string root;
        static readonly object Gate = new object();
        public static void Init(string systemHome) { root = Path.Combine(systemHome, "logs"); }
        /// <summary>The licensing centre's own logs, unless a backup server in the same process already writes them.</summary>
        public static void InitIfUnset(string home) { if (root == null) Init(home); }

        public static void Write(string ip, string category, string line)
        {
            if (root == null) return;
            var now = SystemClock.UtcNow;
            lock (Gate) Atomic.AppendLine(Path.Combine(root, category, now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log"),
                now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "\t" + (ip ?? "-") + "\t" + line);
        }

        public static string Dir(string category) { return Path.Combine(root, category); }
    }

    /// <summary>
    /// System settings (Ahsay "System Settings"): every field is empty at install and filled by the IT company that
    /// runs the server — nothing in the code belongs to one company. Stored in &lt;System Home&gt;\conf\system.xml.
    /// </summary>
    public sealed class SystemConfig
    {
        public string SystemHome { get; private set; }
        public XDocument Doc { get; private set; }
        string PathXml { get { return Path.Combine(SystemHome, "conf", "system.xml"); } }

        public static SystemConfig Load(string systemHome)
        {
            var c = new SystemConfig { SystemHome = Path.GetFullPath(systemHome) };
            if (!File.Exists(c.PathXml)) throw new InvalidOperationException("Server is not initialised: run 'init' first.");
            c.Doc = OnlineBackup.Core.Atomic.LoadXml(c.PathXml);
            c.EnsureServerId();
            return c;
        }

        /// <summary>LIC-010: a random identity of this installation (the licence is issued for it).</summary>
        public string ServerId { get { return (string)Doc.Root.Attribute("SERVER_ID"); } }
        void EnsureServerId()
        {
            if (!string.IsNullOrEmpty(ServerId)) return;
            Doc.Root.SetAttributeValue("SERVER_ID", "OB-" + Bytes.Hex(Bytes.Random(8)).ToUpperInvariant());
            Save();
        }

        License licenseCache; DateTime licenseAt;
        /// <summary>The licence's clock (tests move it).</summary>
        public Func<DateTime> Clock = () => SystemClock.UtcNow;
        /// <summary>The current licence (checked again every 10 minutes, at once after a change).</summary>
        public License License
        {
            get
            {
                if (licenseCache == null || (SystemClock.UtcNow - licenseAt).TotalMinutes > 10)
                {
                    var e = Doc.Root.Element("LICENSE");
                    licenseCache = License.Effective(e == null ? null : (string)e.Attribute("KEY"), ServerId, Clock(), e);
                    licenseAt = SystemClock.UtcNow;
                }
                return licenseCache;
            }
        }
        public void ResetLicense() { licenseCache = null; }

        /// <summary>First-run setup: administrator, address and at least one user home.</summary>
        public static SystemConfig Init(string systemHome, string admin, string password, string hostName, IEnumerable<string> userHomes)
        {
            var c = new SystemConfig { SystemHome = Path.GetFullPath(systemHome) };
            Directory.CreateDirectory(Path.Combine(c.SystemHome, "conf"));
            Directory.CreateDirectory(Path.Combine(c.SystemHome, "policy"));
            var sys = new XElement("SYSTEM", new XAttribute("HOST_NAME", hostName ?? ""), new XAttribute("POLICY_HOME", Path.Combine(c.SystemHome, "policy")),
                new XElement("ADMIN", new XAttribute("LOGIN_NAME", admin), new XAttribute("HASHED_PWD", PasswordHash.Create(password)), new XAttribute("TOTP_SECRET", "")),
                new XElement("SECURITY", new XAttribute("AUTO_LOCK_ATTEMPTS", "3"), new XAttribute("LOCK_MINUTES", "30"), new XAttribute("SINGLE_LEVEL_ACCESS", "Y")),
                new XElement("REPORT_SENDER", new XAttribute("NAME", ""), new XAttribute("EMAIL", "")),
                new XElement("BRANDING", new XAttribute("PRODUCT", ""), new XAttribute("COMPANY", ""), new XAttribute("PHONE", ""), new XAttribute("EMAIL", ""), new XAttribute("WEBSITE", ""), new XAttribute("COLOR", "")),
                new XElement("LICENSE", new XAttribute("KEY", "")));
            foreach (var h in userHomes)
            {
                // "path" or "path|maxQps" where maxQps = UNLIMITED | NOT_USED | percent; optional "|capacityGB" for tests.
                var p = h.Split('|');
                sys.Add(new XElement("USER_HOME", new XAttribute("PATH", Path.GetFullPath(p[0])), new XAttribute("MAX_QPS", p.Length > 1 ? p[1] : "UNLIMITED"),
                    new XAttribute("CAPACITY_GB", p.Length > 2 ? p[2] : "")));
                Directory.CreateDirectory(p[0]);
            }
            c.Doc = new XDocument(sys);
            c.EnsureServerId();
            c.Save();
            var policy = Path.Combine(c.SystemHome, "policy", "default.xml");
            if (!File.Exists(policy)) Atomic.WriteText(policy, DefaultPolicy().ToString());
            return c;
        }

        public void Save() { Atomic.WriteText(PathXml, Doc.ToString()); }

        public XElement Admin { get { return Doc.Root.Element("ADMIN"); } }
        public int AutoLockAttempts { get { return int.Parse((string)Doc.Root.Element("SECURITY").Attribute("AUTO_LOCK_ATTEMPTS") ?? "3", CultureInfo.InvariantCulture); } }
        public int LockMinutes { get { return int.Parse((string)Doc.Root.Element("SECURITY").Attribute("LOCK_MINUTES") ?? "30", CultureInfo.InvariantCulture); } }

        public sealed class UserHome { public string Path; public string MaxQps; public double CapacityBytes; }

        public List<UserHome> Homes
        {
            get
            {
                return Doc.Root.Elements("USER_HOME").Select(e =>
                {
                    var h = new UserHome { Path = (string)e.Attribute("PATH"), MaxQps = (string)e.Attribute("MAX_QPS") ?? "UNLIMITED" };
                    double gb;
                    if (double.TryParse((string)e.Attribute("CAPACITY_GB") ?? "", NumberStyles.Float, CultureInfo.InvariantCulture, out gb) && gb > 0) h.CapacityBytes = gb * 1024 * 1024 * 1024;
                    else { try { h.CapacityBytes = new DriveInfo(System.IO.Path.GetPathRoot(h.Path)).TotalSize; } catch { h.CapacityBytes = 0; } }
                    return h;
                }).ToList();
            }
        }

        /// <summary>Template for new users (Ahsay Policy Group): the most common values of the existing installation.</summary>
        public static XElement DefaultPolicy()
        {
            return new XElement("POLICY", new XAttribute("NAME", "Default"),
                new XElement("USER", new XAttribute("QUOTA_GB", "50"), new XAttribute("QUOTA_TYPE", "COMPRESSED"), new XAttribute("MAX_BACKUP_SET", "10"),
                    new XAttribute("LANGUAGE", "iw"), new XAttribute("TIMEZONE", "GMT+02:00 (IDT)"), new XAttribute("SAVE_ENCRYPT_KEY", "Y"), new XAttribute("REQUIRE_TOTP", "N")),
                new XElement("BACKUP_SET", new XAttribute("HOUR", "22"), new XAttribute("MINUTE", "0"), new XAttribute("RETENTION_DAYS", "30"), new XAttribute("LOG_RETENTION_DAYS", "60"),
                    new XAttribute("VSS", "Y"), new XAttribute("MIN_DELTA_FILE_SIZE", "26214400"), new XAttribute("MAX_DELTA_NO", "100"), new XAttribute("MAX_DELTA_RATIO", "50"),
                    new XAttribute("LOCAL_COPY", "N"), new XAttribute("LOCAL_COPY_PATH", @"C:\LocalBackup"), new XAttribute("LOCAL_COPY_DAYS", "7")),
                new XElement("GLOBAL_FILTER", new XAttribute("TYPE", "CONTAIN"), new XAttribute("APPLY_DIR", "Y"), new XAttribute("APPLY_FILE", "N"),
                    new XElement("PATTERN", "$Recycle.Bin"), new XElement("PATTERN", "System Volume Information"), new XElement("PATTERN", "Temporary Internet Files"),
                    new XElement("PATTERN", "\\Temp"), new XElement("PATTERN", "\\Cache")),
                new XElement("GLOBAL_FILTER", new XAttribute("TYPE", "END_WITH"), new XAttribute("APPLY_DIR", "N"), new XAttribute("APPLY_FILE", "Y"),
                    new XElement("PATTERN", "pagefile.sys"), new XElement("PATTERN", "hiberfil.sys"), new XElement("PATTERN", "swapfile.sys"), new XElement("PATTERN", ".tmp")));
        }

        public XElement Policy()
        {
            var p = Path.Combine(SystemHome, "policy", "default.xml");
            return File.Exists(p) ? OnlineBackup.Core.Atomic.LoadXElement(p) : DefaultPolicy();
        }
    }

    /// <summary>
    /// Encryption keys saved for recovery (Ahsay SAVE_ENCRYPT_KEY): protected with DPAPI on Windows (machine scope),
    /// with a server key file elsewhere (tests). Only an administrator can read one back, and it is logged.
    /// </summary>
    public static class KeyVault
    {
        public static byte[] Protect(string systemHome, byte[] data)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return ProtectedData.Protect(data, null, DataProtectionScope.LocalMachine);
            return Cipher.Encrypt(ServerKey(systemHome), data);
        }

        public static byte[] Unprotect(string systemHome, byte[] data)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return ProtectedData.Unprotect(data, null, DataProtectionScope.LocalMachine);
            return Cipher.Decrypt(ServerKey(systemHome), data);
        }

        static KeySet ServerKey(string systemHome)
        {
            var p = Path.Combine(systemHome, "conf", "vault.key");
            if (!File.Exists(p)) Atomic.WriteBytes(p, Bytes.Random(96));
            return KeySet.FromRaw(OnlineBackup.Core.Atomic.ReadAllBytes(p));
        }
    }
}
