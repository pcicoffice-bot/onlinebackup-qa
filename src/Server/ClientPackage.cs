using System;
using System.Collections.Generic;
using OnlineBackup.Core;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace OnlineBackup.Server
{
    /// <summary>
    /// PKG-010..030: "Create the client software" — one ZIP with the agent (+ restic), the IT company's branding
    /// (branding.xml) and this server's address and certificate pin (connection.xml), and Setup.cmd. The customer (or the
    /// IT technician) runs Setup.cmd as administrator: the agent installs itself under the product's name.
    /// The agent files come from the "client" folder beside the server (the server's installation carries them) or OB_CLIENT_DIR.
    /// </summary>
    public static class ClientPackage
    {
        public static string ClientDir
        {
            get
            {
                var env = Environment.GetEnvironmentVariable("OB_CLIENT_DIR");
                return !string.IsNullOrEmpty(env) ? env : Path.Combine(AppContext.BaseDirectory, "client");
            }
        }

        static string Product(SystemConfig cfg, string vendor) { return Vendors.Brand(cfg, vendor, "PRODUCT", "ITSguard Server Online"); }

        /// <summary>A file / folder name from the product name (letters, digits, space, - _ only).</summary>
        public static string SafeName(string s)
        {
            var n = Regex.Replace(s ?? "", @"[^\p{L}\p{Nd} _-]", "").Trim();
            return n.Length == 0 ? "ITSguard Server Online" : n.Length > 40 ? n.Substring(0, 40).Trim() : n;
        }

        public static string FileName(SystemConfig cfg, string vendor) { return SafeName(Product(cfg, vendor)).Replace(' ', '-') + "-Setup.zip"; }

        public static string FileNameLinux(SystemConfig cfg, string vendor) { return SafeName(Product(cfg, vendor)).Replace(' ', '-') + "-Setup-linux.tar.gz"; }

        /// <summary>What a client package carries: the product, its folder name, branding.xml, connection.xml, the support line, the README language and where the agent files are.</summary>
        public sealed class Pack { public string Product, Folder, Lang = "en", Support = "", Dir; public XElement Branding, Connection, Contract; }

        /// <summary>branding.xml and connection.xml of a package: the vendor's (or the server's) product and this server's address and pin.</summary>
        static Pack FromServer(SystemConfig cfg, string vendor)
        {
            var url = (string)cfg.Doc.Root.Attribute("PUBLIC_URL");
            if (string.IsNullOrEmpty(url)) throw new ApiException(400, "PUBLIC_URL", "First set the server address for customers in the settings (for example https://backup.company.com:8443).");
            var brand = Vendors.BrandAttrs.ToDictionary(a => a, a => Vendors.Brand(cfg, vendor, a, ""));
            var p = FromBrand(brand, cfg.License.Has("WHITELABEL"), url, (string)cfg.Doc.Root.Attribute("CERT_PIN") ?? "", ClientDir);
            // SETUP-C30: this server's addresses inside the office — the installation tries them when the public address
            // does not answer from inside (a router that does not loop back); accepted only with the same certificate
            try { p.Connection.SetAttributeValue("INTERNAL", string.Join(",", LicenseCheckin.InternalAddresses())); } catch (Exception) { }
            // SETUP-C80 (owner): the IT company's contract is the license agreement of the installation (when it is shown at installation)
            try
            {
                var c = OnlineBackup.Server.Contract.Load(cfg);
                if (((int?)c.Attribute("VERSION") ?? 0) > 0 && (string)c.Attribute("INSTALL") != "N" && c.Elements("TEXT").Any())
                    p.Contract = new XElement("CONTRACT", new XAttribute("VERSION", (string)c.Attribute("VERSION")), c.Elements("TEXT").Select(t => new XElement(t)));
            }
            catch (Exception) { }
            return p;
        }

        /// <summary>PORTAL-040: the same package from branding given directly (the partner portal), for a server address and certificate pin.</summary>
        public static Pack FromBrand(IDictionary<string, string> brand, bool whiteLabel, string serverUrl, string pin, string clientDir)
        {
            Func<string, string> b = a => { string v; return brand != null && brand.TryGetValue(a, out v) && v != null ? v : ""; };
            var p = new Pack { Product = b("PRODUCT").Length > 0 ? b("PRODUCT") : "ITSguard Server Online", Dir = clientDir, Lang = L.Norm(b("LANGUAGE").Length > 0 ? b("LANGUAGE") : "en") };
            p.Folder = SafeName(p.Product);
            p.Support = (b("COMPANY") + " " + b("PHONE") + " " + b("EMAIL")).Trim();
            p.Branding = new XElement("BRANDING");
            foreach (var a in Vendors.BrandAttrs) p.Branding.SetAttributeValue(a, a == "PRODUCT" ? p.Product : b(a));
            if (!whiteLabel) p.Branding.SetAttributeValue("POWERED", License.OwnerProduct);   // LIC-070
            p.Connection = new XElement("CONNECTION", new XAttribute("SERVER", serverUrl), new XAttribute("PIN", (pin ?? "").Replace(":", "").Trim().ToLowerInvariant()), new XAttribute("FOLDER", p.Folder));
            return p;
        }

        public static string FileName(Pack p, string os) { return p.Folder.Replace(' ', '-') + (os == "linux" ? "-Setup-linux.tar.gz" : os == "mac" ? "-Setup-mac.tar.gz" : os == "zip" ? "-Setup.zip" : "-Setup.exe"); }

        /// <summary>
        /// PKG-060: the Linux client (x64, no .NET to install): the agent, restic, branding / connection and setup.sh, as a
        /// .tar.gz (keeps the executable bits). Installed with: tar xzf …; sudo ./&lt;folder&gt;-Setup/setup.sh
        /// </summary>
        public static byte[] BuildLinux(SystemConfig cfg, string vendor) { return BuildLinux(FromServer(cfg, vendor)); }

        public static byte[] BuildLinux(Pack p)
        {
            var product = p.Product; var folder = p.Folder; var branding = p.Branding; var connection = p.Connection;
            var dir = Path.Combine(p.Dir, "linux");
            if (!File.Exists(Path.Combine(dir, "OnlineBackup.Agent")) || !File.Exists(Path.Combine(dir, "restic")))
                throw new ApiException(500, "CLIENT_FILES", "The Linux client files are missing on the server (" + dir + "). Reinstall the server package.");
            const UnixFileMode exec = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
            const UnixFileMode plain = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
            var root = folder.Replace(' ', '-') + "-Setup/";
            using (var ms = new MemoryStream())
            {
                using (var gz = new System.IO.Compression.GZipStream(ms, CompressionLevel.Optimal, true))
                using (var tar = new System.Formats.Tar.TarWriter(gz, System.Formats.Tar.TarEntryFormat.Pax, false))
                {
                    Action<string, byte[], UnixFileMode> add = (name, data, mode) =>
                    {
                        var e = new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, root + name) { Mode = mode, ModificationTime = DateTimeOffset.UtcNow, DataStream = new MemoryStream(data) };
                        tar.WriteEntry(e);
                    };
                    add("OnlineBackup.Agent", File.ReadAllBytes(Path.Combine(dir, "OnlineBackup.Agent")), exec);
                    add("restic", File.ReadAllBytes(Path.Combine(dir, "restic")), exec);
                    var notices = Path.Combine(p.Dir, "THIRD-PARTY-NOTICES.txt");
                    if (File.Exists(notices)) add("THIRD-PARTY-NOTICES.txt", File.ReadAllBytes(notices), plain);
                    add("branding.xml", new UTF8Encoding(false).GetBytes(branding.ToString()), plain);
                    add("connection.xml", new UTF8Encoding(false).GetBytes(connection.ToString()), plain);
                    add("setup.sh", Encoding.ASCII.GetBytes(
                        "#!/bin/sh\n" +
                        "# Installation - run as root: sudo ./setup.sh  (unattended: sudo ./setup.sh --login USER --password PASS)\n" +
                        "[ \"$(id -u)\" = 0 ] || { echo \"Please run as root: sudo $0\"; exit 1; }\n" +
                        "cd \"$(dirname \"$0\")\" || exit 1\n" +
                        "exec ./OnlineBackup.Agent setup \"$@\"\n"), exec);
                    add("README.txt", new UTF8Encoding(false).GetBytes(product + "\n\n" +
                        "Linux x64 (systemd). Install: sudo ./setup.sh  - then the user name and password you received.\n" +
                        "Unattended: sudo ./setup.sh --login USER --password PASS\n" +
                        "Support: " + p.Support + "\n"), plain);
                }
                return ms.ToArray();
            }
        }

        public static string FileNameMac(SystemConfig cfg, string vendor) { return SafeName(Product(cfg, vendor)).Replace(' ', '-') + "-Setup-mac.tar.gz"; }

        /// <summary>
        /// PKG-070: the macOS client (Apple silicon and Intel, no .NET to install): both agents and restic builds, branding /
        /// connection and setup.command (double-click, or sudo ./setup.command in Terminal). It picks the build for this Mac,
        /// clears the download quarantine and runs the agent's setup as root: LaunchDaemon + an app in Applications.
        /// </summary>
        public static byte[] BuildMac(SystemConfig cfg, string vendor) { return BuildMac(FromServer(cfg, vendor)); }

        public static byte[] BuildMac(Pack p)
        {
            var product = p.Product; var folder = p.Folder; var branding = p.Branding; var connection = p.Connection;
            var dir = Path.Combine(p.Dir, "mac");
            foreach (var arch in new[] { "arm64", "x64" })
                foreach (var f in new[] { "OnlineBackup.Agent", "restic" })
                    if (!File.Exists(Path.Combine(dir, arch, f))) throw new ApiException(500, "CLIENT_FILES", "The Mac client files are missing on the server (" + Path.Combine(dir, arch) + "). Reinstall the server package.");
            const UnixFileMode exec = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
            const UnixFileMode plain = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
            var root = folder.Replace(' ', '-') + "-Setup/";
            using (var ms = new MemoryStream())
            {
                using (var gz = new System.IO.Compression.GZipStream(ms, CompressionLevel.Optimal, true))
                using (var tar = new System.Formats.Tar.TarWriter(gz, System.Formats.Tar.TarEntryFormat.Pax, false))
                {
                    Action<string, byte[], UnixFileMode> add = (name, data, mode) =>
                        tar.WriteEntry(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, root + name) { Mode = mode, ModificationTime = DateTimeOffset.UtcNow, DataStream = new MemoryStream(data) });
                    foreach (var arch in new[] { "arm64", "x64" })
                    {
                        add(arch + "/OnlineBackup.Agent", File.ReadAllBytes(Path.Combine(dir, arch, "OnlineBackup.Agent")), exec);
                        add(arch + "/restic", File.ReadAllBytes(Path.Combine(dir, arch, "restic")), exec);
                    }
                    var notices = Path.Combine(p.Dir, "THIRD-PARTY-NOTICES.txt");
                    if (File.Exists(notices)) add("THIRD-PARTY-NOTICES.txt", File.ReadAllBytes(notices), plain);
                    add("branding.xml", new UTF8Encoding(false).GetBytes(branding.ToString()), plain);
                    add("connection.xml", new UTF8Encoding(false).GetBytes(connection.ToString()), plain);
                    add("setup.command", Encoding.ASCII.GetBytes(SetupCommand()), exec);
                    add("README.txt", new UTF8Encoding(false).GetBytes(product + "\n\n" +
                        "macOS 10.15 or later (Apple silicon or Intel). Install: double-click setup.command (if macOS refuses: right-click -> Open),\n" +
                        "or in Terminal: sudo ./setup.command  - then the user name and password you received.\n" +
                        "Unattended: sudo ./setup.command --login USER --password PASS\n" +
                        "After installing: System Settings -> Privacy & Security -> Full Disk Access -> add the program shown by the installer.\n" +
                        "Support: " + p.Support + "\n"), plain);
                }
                return ms.ToArray();
            }
        }

        /// <summary>setup.command: the build for this Mac's processor, quarantine cleared, the agent's setup as root.</summary>
        public static string SetupCommand()
        {
            return "#!/bin/sh\n" +
                "# Installation on a Mac - double-click, or in Terminal: sudo ./setup.command  (unattended: --login USER --password PASS)\n" +
                "cd \"$(dirname \"$0\")\" || exit 1\n" +
                "if [ \"$(id -u)\" != 0 ]; then echo \"Administrator password of this Mac:\"; exec sudo \"$0\" \"$@\"; fi\n" +
                "case \"$(uname -m)\" in arm64) ARCH=arm64;; *) ARCH=x64;; esac\n" +
                "xattr -dr com.apple.quarantine . 2>/dev/null\n" +
                "cp branding.xml connection.xml \"$ARCH/\" && [ -f THIRD-PARTY-NOTICES.txt ] && cp THIRD-PARTY-NOTICES.txt \"$ARCH/\"\n" +
                "cd \"$ARCH\" || exit 1\n" +
                "/usr/bin/codesign --force --sign - OnlineBackup.Agent 2>/dev/null   # Apple silicon runs only signed programs (ad-hoc is enough)\n" +
                "./OnlineBackup.Agent setup \"$@\"\n" +
                "echo; echo \"Press Enter to close.\"; read x\n";
        }

        public static byte[] Build(SystemConfig cfg, string vendor) { return Build(FromServer(cfg, vendor)); }

        public static byte[] Build(Pack p)
        {
            var product = p.Product; var folder = p.Folder; var branding = p.Branding; var connection = p.Connection;
            var dir = p.Dir;
            if (!File.Exists(Path.Combine(dir, "OnlineBackup.Agent.exe"))) throw new ApiException(500, "CLIENT_FILES", "The client files are missing on the server (" + dir + "). Reinstall the server package.");
            using (var ms = new MemoryStream())
            {
                using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
                {
                    var root = folder + "-Setup/";
                    foreach (var f in Directory.GetFiles(dir).Where(f => !f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) && Path.GetFileName(f) != "branding.xml" && Path.GetFileName(f) != "contract.xml"))
                        zip.CreateEntryFromFile(f, root + Path.GetFileName(f), CompressionLevel.Optimal);
                    Add(zip, root + "branding.xml", branding.ToString());
                    Add(zip, root + "connection.xml", connection.ToString());
                    if (p.Contract != null) Add(zip, root + "contract.xml", p.Contract.ToString());
                    AddAscii(zip, root + "Setup.cmd",          // plain ASCII: cmd.exe reads neither a BOM nor UTF-8
                        "@echo off\r\n" +
                        "rem Installation - double-click: asks for administrator permission and opens the installation wizard\r\n" +
                        "net session >nul 2>&1 || (powershell -NoProfile -Command \"$a='%*'; if ($a) { Start-Process -FilePath '%~f0' -ArgumentList $a -Verb RunAs } else { Start-Process -FilePath '%~f0' -Verb RunAs }\" 2>nul & exit /b)\r\n" +
                        "cd /d \"%~dp0\"\r\n" +
                        "\"%~dp0OnlineBackup.Agent.exe\" setup %*\r\n" +
                        "if errorlevel 1 pause\r\n");
                    var lang = p.Lang;   // I18N-030: the README in the company's language (and English)
                    Func<string, string> readme = lg =>
                        L.T(lg, "Installation: double-click Setup.exe — the installation program opens.") + "\r\n" +
                        L.T(lg, "Silent installation (technician): Setup.cmd --login USER --password PASS") + "\r\n" +
                        L.T(lg, "Windows 2003 or later (.NET Framework 4.0). On Windows 10 / Server 2016 and later the backup uses the restic engine.") + "\r\n" +
                        L.T(lg, "Support: {0}", p.Support) + "\r\n";
                    Add(zip, root + "README.txt", product + "\r\n\r\n" + readme(lang) + (lang == "en" ? "" : "\r\n" + readme("en")));
                }
                return ms.ToArray();
            }
        }

        /// <summary>
        /// SETUP-C60 (owner: "one file you click: it unpacks to a temporary folder and opens the installation"): the Windows
        /// client as one &lt;product&gt;-Setup.exe — Setup.exe followed by the package's files (gzip: the count, then for each
        /// file its name and bytes), the length of that and the mark OBSETUP1 at the very end. Setup.exe reads itself.
        /// </summary>
        public static byte[] BuildExe(Pack p)
        {
            var dir = p.Dir;
            var stub = Path.Combine(dir, "Setup.exe");
            if (!File.Exists(Path.Combine(dir, "OnlineBackup.Agent.exe")) || !File.Exists(stub)) throw new ApiException(500, "CLIENT_FILES", "The client files are missing on the server (" + dir + "). Reinstall the server package.");
            var files = new List<KeyValuePair<string, byte[]>>();
            foreach (var f in Directory.GetFiles(dir).OrderBy(x => x, StringComparer.Ordinal))
            {
                var n = Path.GetFileName(f);
                if (n.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) || n == "branding.xml" || n == "connection.xml" || n == "contract.xml" || n.Equals("Setup.exe", StringComparison.OrdinalIgnoreCase) || n.Equals("Setup.exe.config", StringComparison.OrdinalIgnoreCase)) continue;
                files.Add(new KeyValuePair<string, byte[]>(n, File.ReadAllBytes(f)));
            }
            files.Add(new KeyValuePair<string, byte[]>("branding.xml", new UTF8Encoding(true).GetBytes(p.Branding.ToString())));
            files.Add(new KeyValuePair<string, byte[]>("connection.xml", new UTF8Encoding(true).GetBytes(p.Connection.ToString())));
            if (p.Contract != null) files.Add(new KeyValuePair<string, byte[]>("contract.xml", new UTF8Encoding(true).GetBytes(p.Contract.ToString())));
            using (var ms = new MemoryStream())
            {
                var head = File.ReadAllBytes(stub); ms.Write(head, 0, head.Length);
                long start = ms.Position;
                using (var gz = new System.IO.Compression.GZipStream(ms, CompressionLevel.Optimal, true))
                using (var w = new BinaryWriter(gz, Encoding.UTF8, true))
                {
                    w.Write(files.Count);
                    foreach (var kv in files) { var nb = Encoding.UTF8.GetBytes(kv.Key); w.Write(nb.Length); w.Write(nb); w.Write((long)kv.Value.Length); w.Write(kv.Value); }
                }
                long len = ms.Position - start;
                var tail = new BinaryWriter(ms, Encoding.ASCII, true); tail.Write(len); tail.Write(Encoding.ASCII.GetBytes(PayloadMark)); tail.Flush();
                return ms.ToArray();
            }
        }

        public const string PayloadMark = "OBSETUP1";

        /// <summary>The files inside a one-file Setup.exe (tests, and the same reading as Setup.exe itself).</summary>
        public static Dictionary<string, byte[]> ReadExe(byte[] exe)
        {
            var mark = Encoding.ASCII.GetString(exe, exe.Length - 8, 8);
            if (mark != PayloadMark) throw new InvalidDataException("not a one-file setup");
            var len = BitConverter.ToInt64(exe, exe.Length - 16);
            var r = new Dictionary<string, byte[]>();
            using (var gz = new System.IO.Compression.GZipStream(new MemoryStream(exe, (int)(exe.Length - 16 - len), (int)len), CompressionMode.Decompress))
            using (var br = new BinaryReader(gz, Encoding.UTF8))
            {
                var n = br.ReadInt32();
                for (int i = 0; i < n; i++) { var name = Encoding.UTF8.GetString(br.ReadBytes(br.ReadInt32())); var size = br.ReadInt64(); r[name] = br.ReadBytes((int)size); }
            }
            return r;
        }

        public static string FileNameExe(Pack p) { return p.Folder.Replace(' ', '-') + "-Setup.exe"; }
        public static string FileNameExe(SystemConfig cfg, string vendor) { return SafeName(Product(cfg, vendor)).Replace(' ', '-') + "-Setup.exe"; }
        public static byte[] BuildExe(SystemConfig cfg, string vendor) { return BuildExe(FromServer(cfg, vendor)); }

        static void AddAscii(ZipArchive z, string name, string text)
        {
            var e = z.CreateEntry(name, CompressionLevel.Optimal);
            using (var w = new StreamWriter(e.Open(), new ASCIIEncoding())) w.Write(text);
        }

        static void Add(ZipArchive z, string name, string text)
        {
            var e = z.CreateEntry(name, CompressionLevel.Optimal);
            using (var w = new StreamWriter(e.Open(), new UTF8Encoding(true))) w.Write(text);
        }
    }
}
