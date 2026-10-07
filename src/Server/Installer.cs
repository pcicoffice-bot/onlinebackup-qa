using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using OnlineBackup.Core;

namespace OnlineBackup.Server
{
    /// <summary>What the installation wizard asks (in plain words) — everything else is decided by the installer.
    /// Checks and progress are message codes ("STEP_COPY", "ERR_HOST", "CODE|argument"): the wizard shows them in the user's language.</summary>
    public sealed class SetupAnswers
    {
        public string Language = "", Product = "", Slogan = "", Company = "", Phone = "", Email = "", Website = "", Color = "#4F46E5", Accent = "#F97316", Logo = "";
        public string HostName = ""; public int Port = 8443;
        public string DataRoot = "";                       // e.g. "E:\" — the folders are created under it
        public string AdminLogin = "admin", AdminPassword = "";
        public string AlertEmail = "";
        public string MailProvider = "none";               // none / gmail / m365 / other
        public string SmtpHost = "", SmtpLogin = "", SmtpPassword = ""; public int SmtpPort = 587;
    }

    /// <summary>
    /// SETUP-010..060: installs (or updates) the backup server on a Windows server without any IT knowledge, from the
    /// answers of the wizard: program files, the data folders on the chosen drive, the company's branding, e-mail alerts,
    /// a certificate made for this server (pinned by the client software), HTTPS on its own port (http.sys — no IIS, no
    /// other site touched), a firewall rule, the Windows service, and a final check that the server answers.
    /// An existing installation is updated in place (files only; data and settings kept).
    /// OB_SETUP_ROOT (tests, other systems): everything under that folder, no Windows commands — they are listed instead.
    /// </summary>
    public static class Installer
    {
        public const string AppId = "{6f1f6a9e-6a3b-4b0e-9a65-6b2f0f3b1a10}", CertName = "OnlineBackup Server";
        static string TestRoot { get { return Environment.GetEnvironmentVariable("OB_SETUP_ROOT"); } }
        public static readonly List<string> Commands = new List<string>();   // what would run (OB_SETUP_ROOT)

        public static string InstallDir { get { return TestRoot != null ? Path.Combine(TestRoot, "Program Files", "OnlineBackup Server") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OnlineBackup Server"); } }
        static string InfoFile { get { return Path.Combine(InstallDir, "install.xml"); } }

        /// <summary>The existing installation (its system folder and address), or null.</summary>
        public static XElement Existing()
        {
            try { if (File.Exists(InfoFile)) { var x = XElement.Load(InfoFile); if (File.Exists(Path.Combine((string)x.Attribute("SYSTEM_HOME") ?? "", "conf", "system.xml"))) return x; } } catch (Exception) { }
            return null;
        }

        // ---------------------------------------------------------------- what the wizard shows before asking

        public static Dictionary<string, object> Info(string packageServerDir = null)
        {
            var m = new Dictionary<string, object> { { "computer", Environment.MachineName }, { "os", Environment.OSVersion.VersionString }, { "admin", IsAdmin() },
                { "portFree", PortFree(8443) }, { "dryRun", TestRoot != null }, { "serverOs", WindowsServer() } };
            var ex = Existing();
            if (ex != null) { m["existing"] = true; m["existingUrl"] = (string)ex.Attribute("URL"); m["existingHome"] = (string)ex.Attribute("SYSTEM_HOME"); }
            var sys = Path.GetPathRoot(Environment.SystemDirectory) ?? "/";
            DriveInfo best = null; var drives = new List<object>();
            foreach (var d in Drives())
            {
                bool isSys = string.Equals(d.Name, sys, StringComparison.OrdinalIgnoreCase);
                drives.Add(new Dictionary<string, object> { { "name", d.Name }, { "freeGB", d.AvailableFreeSpace / 1073741824 }, { "totalGB", d.TotalSize / 1073741824 }, { "system", isSys } });
                bool bestIsSys = best != null && string.Equals(best.Name, sys, StringComparison.OrdinalIgnoreCase);
                if (best == null || (!isSys && (bestIsSys || d.AvailableFreeSpace > best.AvailableFreeSpace))) best = d;
            }
            m["drives"] = drives;
            m["suggestedDrive"] = best == null ? "" : (TestRoot != null ? TestRoot : best.Name);
            m["localIps"] = LocalAddresses().Cast<object>().ToList();
            m["publicIp"] = PublicIp();
            // PORTAL-020: a package downloaded from the partner portal carries the company's product (the wizard is filled in)
            var preset = packageServerDir == null ? null : Path.Combine(packageServerDir, "branding-preset.xml");
            if (preset != null && File.Exists(preset))
                try { var p = XElement.Load(preset); m["preset"] = Vendors.BrandAttrs.ToDictionary(k => k.ToLowerInvariant(), k => (object)((string)p.Attribute(k) ?? "")); }
                catch (Exception) { }
            m["portal"] = packageServerDir != null && File.Exists(Path.Combine(packageServerDir, "portal.xml"));
            return m;
        }

        /// <summary>
        /// SETUP-030 (owner): the backup server is installed on Windows Server only — Windows 10 / 11 accept 20 connections at
        /// a time, and the customers' backups would fail. A dry run (tests, the screen robot) follows OB_SETUP_OS.
        /// </summary>
        public static bool WindowsServer()
        {
            if (TestRoot != null) return Environment.GetEnvironmentVariable("OB_SETUP_OS") != "client";
            if (!OperatingSystem.IsWindows()) return false;
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    var type = k == null ? null : k.GetValue("InstallationType") as string;   // "Server", "Server Core", "Client"
                    if (!string.IsNullOrEmpty(type)) return type.StartsWith("Server", StringComparison.OrdinalIgnoreCase);
                    var name = k == null ? null : k.GetValue("ProductName") as string;
                    return name != null && name.IndexOf("Server", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch (Exception) { return false; }
        }

        static IEnumerable<DriveInfo> Drives()
        {
            if (TestRoot != null) { Directory.CreateDirectory(TestRoot); return new[] { new DriveInfo(Path.GetPathRoot(Path.GetFullPath(TestRoot))) }; }
            return DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady);
        }

        static bool IsAdmin()
        {
            if (TestRoot != null || !OperatingSystem.IsWindows()) return true;
            using (var id = System.Security.Principal.WindowsIdentity.GetCurrent()) return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }

        /// <summary>SETUP-040: the folder of the backups — a drive alone ("E:\\") gets "OnlineBackup" under it, a folder is used as written.</summary>
        public static string DataFolder(string chosen)
        {
            var p = (chosen ?? "").Trim();
            var root = Path.GetPathRoot(p) ?? "";
            return p.TrimEnd('\\', '/').Length <= root.TrimEnd('\\', '/').Length ? Path.Combine(root, "OnlineBackup") : Path.GetFullPath(p);
        }

        /// <summary>A full path on an existing local drive, not a network path, not inside Windows or the program folders.</summary>
        public static bool FolderOk(string chosen)
        {
            var p = (chosen ?? "").Trim();
            if (p.StartsWith("\\\\", StringComparison.Ordinal) || p.StartsWith("//", StringComparison.Ordinal) || !Path.IsPathRooted(p) || p.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;
            if (OperatingSystem.IsWindows() && !Regex.IsMatch(p, @"^[A-Za-z]:[\\/]")) return false;
            string full;
            try { full = DataFolder(p); } catch (Exception) { return false; }
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return false;
            if (TestRoot == null && OperatingSystem.IsWindows())
                foreach (var sys in new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.UserProfile })
                {
                    var d = Environment.GetFolderPath(sys);
                    if (!string.IsNullOrEmpty(d) && (full.Equals(d, StringComparison.OrdinalIgnoreCase) || full.StartsWith(d.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))) return false;
                }
            return true;
        }

        public static bool PortFree(int port)
        {
            var ex = Existing();
            if (ex != null && (string)ex.Attribute("PORT") == port.ToString(CultureInfo.InvariantCulture)) return true;   // our own service holds it
            if (TestRoot != null || !OperatingSystem.IsWindows()) return true;
            // SETUP-030: a port of another program is never taken — also not one an IIS site is set to use while it is
            // stopped, and not one with another program's HTTPS certificate binding (deleting it would break that site)
            try { if (IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == port)) return false; } catch (Exception) { }
            try { var iis = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "inetsrv", "config", "applicationHost.config"); if (File.Exists(iis) && IisBindsPort(OnlineBackup.Core.Atomic.ReadAllText(iis), port)) return false; } catch (Exception) { }
            return !ForeignSslBinding(ShowSsl(port));
        }

        /// <summary>Whether a site in IIS's configuration is bound to the port (running or stopped): bindingInformation="ip:port:host".</summary>
        public static bool IisBindsPort(string applicationHostConfig, int port)
        {
            return Regex.IsMatch(applicationHostConfig ?? "", "bindingInformation=\"[^\":]*:" + port.ToString(CultureInfo.InvariantCulture) + ":[^\"]*\"");
        }

        /// <summary>Whether netsh's answer for the port shows a certificate binding of another program (any application id but ours).</summary>
        public static bool ForeignSslBinding(string netshShow)
        {
            var ids = Regex.Matches(netshShow ?? "", @"\{[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\}").Cast<Match>().Select(m => m.Value).ToList();
            return ids.Count > 0 && !ids.Any(i => string.Equals(i, AppId, StringComparison.OrdinalIgnoreCase));
        }

        static string ShowSsl(int port)
        {
            if (TestRoot != null || !OperatingSystem.IsWindows()) return "";
            try
            {
                var psi = new ProcessStartInfo("netsh.exe", "http show sslcert ipport=0.0.0.0:" + port.ToString(CultureInfo.InvariantCulture)) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                return ProcessRunner.Run(psi, Limits.Short).Out;
            }
            catch (Exception) { return ""; }
        }

        static List<string> LocalAddresses()
        {
            try
            {
                return NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses).Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).Select(a => a.Address.ToString()).Distinct().ToList();
            }
            catch (Exception) { return new List<string>(); }
        }

        /// <summary>The address the internet sees (a public echo service; empty when there is no internet).</summary>
        static string PublicIp()
        {
            var test = Environment.GetEnvironmentVariable("OB_SETUP_PUBLIC_IP");
            if (test != null) return test;
            try
            {
                var r = (HttpWebRequest)WebRequest.Create("https://api.ipify.org"); r.Timeout = 4000;
                using (var resp = r.GetResponse()) using (var rd = new StreamReader(resp.GetResponseStream())) { var ip = rd.ReadToEnd().Trim(); IPAddress a; return IPAddress.TryParse(ip, out a) ? ip : ""; }
            }
            catch (Exception) { return ""; }
        }

        // ---------------------------------------------------------------- checks of the answers, in plain words

        public static List<string> Check(SetupAnswers a, bool update)
        {
            var e = new List<string>();
            if (update) return e;
            if (!WindowsServer()) e.Add("ERR_NOT_SERVER");
            if (string.IsNullOrWhiteSpace(a.Product)) e.Add("ERR_PRODUCT");
            if (string.IsNullOrWhiteSpace(a.Company)) e.Add("ERR_COMPANY");
            if (!Regex.IsMatch(a.HostName ?? "", @"^[A-Za-z0-9]([A-Za-z0-9\-\.]{0,251}[A-Za-z0-9])?$")) e.Add("ERR_HOST");
            if (a.Port < 1024 || a.Port > 65535) e.Add("ERR_PORT_RANGE");
            else if (!PortFree(a.Port)) e.Add("ERR_PORT_BUSY|" + a.Port);
            if (string.IsNullOrWhiteSpace(a.DataRoot)) e.Add("ERR_DRIVE");
            else if (!FolderOk(a.DataRoot)) e.Add("ERR_FOLDER");
            if (!Regex.IsMatch(a.AdminLogin ?? "", @"^[A-Za-z0-9._-]{3,32}$")) e.Add("ERR_LOGIN");
            var pw = a.AdminPassword ?? "";
            if (!Passwords.Ok(pw)) e.Add("ERR_PASSWORD");
            if (!string.IsNullOrEmpty(a.AlertEmail) && !Regex.IsMatch(a.AlertEmail, @"^[^@\s]+@[^@\s]+\.[^@\s]+$")) e.Add("ERR_ALERT_EMAIL");
            if (a.MailProvider != "none")
            {
                if (string.IsNullOrEmpty(a.SmtpLogin) || string.IsNullOrEmpty(a.SmtpPassword)) e.Add("ERR_SMTP_LOGIN");
                if (a.MailProvider == "other" && string.IsNullOrEmpty(a.SmtpHost)) e.Add("ERR_SMTP_HOST");
            }
            if (!string.IsNullOrEmpty(a.Color) && !Regex.IsMatch(a.Color, "^#[0-9a-fA-F]{6}$")) e.Add("ERR_COLOR");
            if (!string.IsNullOrEmpty(a.Accent) && !Regex.IsMatch(a.Accent, "^#[0-9a-fA-F]{6}$")) e.Add("ERR_COLOR");
            if (!string.IsNullOrEmpty(a.Logo)) try { Vendors.CheckLogo(a.Logo); } catch (ApiException) { e.Add("ERR_LOGO"); }
            return e;
        }

        // ---------------------------------------------------------------- the installation

        public sealed class Result { public string AdminUrl = "", Pin = "", SystemHome = "", UsersHome = ""; public bool Update; }

        static void Exec(string file, string args, Action<string> say, bool mustSucceed = true)
        {
            if (TestRoot != null) { lock (Commands) Commands.Add(file + " " + args); return; }
            var psi = new ProcessStartInfo(file, args) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            // static review "raw-process": no limit, and stdout was read to the end before stderr (a full stderr pipe hung setup)
            var r = ProcessRunner.Run(psi, Limits.Install);
            if (r.TimedOut && mustSucceed) throw new TimeoutException(file + " " + args.Split(' ')[0] + " did not finish in " + ProcessRunner.Describe(Limits.Install) + " and was stopped");
            if (r.TimedOut) say(file + " " + args.Split(' ')[0] + " did not finish in " + ProcessRunner.Describe(Limits.Install) + " and was stopped");
            else if (r.Code != 0 && mustSucceed) throw new InvalidOperationException(file + " " + args.Split(' ')[0] + ": " + (r.Out + r.Err).Trim());
        }

        public static Result Run(SetupAnswers a, string packageServerDir, Action<string> say)
        {
            var ex = Existing();
            var r = new Result { Update = ex != null };
            var dir = InstallDir;
            var exe = Path.Combine(dir, OperatingSystem.IsWindows() ? "OnlineBackup.Server.exe" : "OnlineBackup.Server");

            // 1. the program files (the service is stopped first when it exists)
            say("STEP_STOP");
            Exec("sc.exe", "stop " + ServerService.Name, say, false);
            if (TestRoot == null) System.Threading.Thread.Sleep(3000);
            say("STEP_COPY");
            Directory.CreateDirectory(dir);
            if (!string.Equals(Path.GetFullPath(packageServerDir).TrimEnd('\\', '/'), Path.GetFullPath(dir).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                foreach (var f in Directory.GetFiles(packageServerDir, "*", SearchOption.AllDirectories))
                {
                    var dst = Path.Combine(dir, Path.GetRelativePath(packageServerDir, f));
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    File.Copy(f, dst, true);
                }

            if (r.Update)
            {
                r.SystemHome = (string)ex.Attribute("SYSTEM_HOME"); r.AdminUrl = (string)ex.Attribute("URL") + "/admin";
                r.Pin = (string)SystemConfig.Load(r.SystemHome).Doc.Root.Attribute("CERT_PIN") ?? "";
                say("STEP_RESTART");
                Exec("sc.exe", "start " + ServerService.Name, say, false);
                // like a new installation: "updated" only when the server answers again (UX-17 class: a server that did
                // not come back after an update looked updated)
                int port; if (!int.TryParse((string)ex.Attribute("PORT"), out port)) port = Uri.TryCreate((string)ex.Attribute("URL"), UriKind.Absolute, out var u) ? u.Port : 443;
                Answer(port, r.Pin, say);
                say("STEP_UPDATED");
                return r;
            }

            // 2. the data folders and the first settings
            var root = DataFolder(a.DataRoot);
            r.SystemHome = Path.Combine(root, "system"); r.UsersHome = Path.Combine(root, "users");
            say("STEP_FOLDERS|" + root);
            var hostPort = a.HostName.Trim() + ":" + a.Port;
            var cfg = File.Exists(Path.Combine(r.SystemHome, "conf", "system.xml")) ? SystemConfig.Load(r.SystemHome) : SystemConfig.Init(r.SystemHome, a.AdminLogin, a.AdminPassword, hostPort, new[] { r.UsersHome });
            if (TestRoot == null) Exec("icacls.exe", "\"" + root + "\" /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F", say, false);
            var b = cfg.Doc.Root.Element("BRANDING");
            b.SetAttributeValue("PRODUCT", a.Product.Trim()); b.SetAttributeValue("COMPANY", a.Company.Trim()); b.SetAttributeValue("PHONE", (a.Phone ?? "").Trim());
            b.SetAttributeValue("SLOGAN", (a.Slogan ?? "").Trim()); b.SetAttributeValue("EMAIL", (a.Email ?? "").Trim()); b.SetAttributeValue("WEBSITE", (a.Website ?? "").Trim()); b.SetAttributeValue("COLOR", a.Color ?? ""); b.SetAttributeValue("ACCENT", a.Accent ?? "");
            if (!string.IsNullOrEmpty(a.Logo)) b.SetAttributeValue("LOGO", a.Logo);
            say("STEP_BRAND");
            if (a.MailProvider != "none")
            {
                string host = a.SmtpHost; int port = a.SmtpPort;
                if (a.MailProvider == "gmail") { host = "smtp.gmail.com"; port = 587; }
                if (a.MailProvider == "m365") { host = "smtp.office365.com"; port = 587; }
                cfg.Doc.Root.Elements("SMTP").Remove();
                cfg.Doc.Root.Add(new XElement("SMTP", new XAttribute("HOST", host), new XAttribute("PORT", port.ToString(CultureInfo.InvariantCulture)), new XAttribute("SECURITY", "STARTTLS"),
                    new XAttribute("LOGIN", a.SmtpLogin), new XAttribute("PASSWORD_ENC", Convert.ToBase64String(KeyVault.Protect(cfg.SystemHome, Encoding.UTF8.GetBytes(a.SmtpPassword))))));
                var sender = cfg.Doc.Root.Element("REPORT_SENDER");
                sender.SetAttributeValue("NAME", a.Product.Trim()); sender.SetAttributeValue("EMAIL", a.SmtpLogin);
                say("STEP_MAIL|" + host);
            }
            if (!string.IsNullOrEmpty(a.AlertEmail))
            {
                cfg.Doc.Root.Elements("ADMIN_CONTACT").Remove();
                cfg.Doc.Root.Add(new XElement("ADMIN_CONTACT", new XAttribute("NAME", a.Company.Trim()), new XAttribute("EMAIL", a.AlertEmail.Trim())));
            }
            if (!string.IsNullOrEmpty(a.Language)) b.SetAttributeValue("LANGUAGE", L.Norm(a.Language));
            cfg.Save();
            PortalLicense(cfg, packageServerDir, say);

            // 3. the certificate of this server (the client software trusts exactly this one)
            say("STEP_CERT");
            var cert = MakeCertificate(a.HostName.Trim());
            r.Pin = Bytes.Hex(Bytes.Sha256(cert.RawData));
            if (TestRoot == null && OperatingSystem.IsWindows())
                using (var store = new X509Store(StoreName.My, StoreLocation.LocalMachine)) { store.Open(OpenFlags.ReadWrite); store.Add(cert); }
            else lock (Commands) Commands.Add("certificate " + cert.Thumbprint);

            // 4. HTTPS on its own port (no IIS), the firewall, the service
            say("STEP_HTTPS|" + a.Port);
            var prefix = "https://+:" + a.Port + "/";
            if (ForeignSslBinding(ShowSsl(a.Port))) throw new InvalidOperationException("ERR_PORT_BUSY|" + a.Port);   // never another program's binding
            Exec("netsh.exe", "http delete sslcert ipport=0.0.0.0:" + a.Port, say, false);   // only ours (an earlier installation)
            Exec("netsh.exe", "http add sslcert ipport=0.0.0.0:" + a.Port + " certhash=" + cert.Thumbprint + " appid=" + AppId, say);
            Exec("netsh.exe", "http add urlacl url=" + prefix + " sddl=D:(A;;GX;;;SY)", say, false);
            say("STEP_FIREWALL|" + a.Port);
            Exec("netsh.exe", "advfirewall firewall delete rule name=\"OnlineBackup Server " + a.Port + "\"", say, false);
            Exec("netsh.exe", "advfirewall firewall add rule name=\"OnlineBackup Server " + a.Port + "\" dir=in action=allow protocol=TCP localport=" + a.Port, say);
            var url = "https://" + hostPort;
            cfg.Doc.Root.SetAttributeValue("PUBLIC_URL", url);
            cfg.Doc.Root.SetAttributeValue("CERT_PIN", r.Pin);
            cfg.Save();
            say("STEP_SERVICE");
            if (TestRoot == null && OperatingSystem.IsWindows()) ServerService.Install(exe, r.SystemHome, prefix);
            else lock (Commands) Commands.Add("service " + exe + " " + r.SystemHome + " " + prefix);
            new XElement("INSTALL", new XAttribute("SYSTEM_HOME", r.SystemHome), new XAttribute("URL", url), new XAttribute("PORT", a.Port), new XAttribute("DATE", SystemClock.UtcNow.ToString("o", CultureInfo.InvariantCulture))).Save(InfoFile);
            r.AdminUrl = url + "/admin";

            // 5. the server answers?
            Answer(a.Port, r.Pin, say);
            say("STEP_DONE");
            return r;
        }

        /// <summary>PORTAL-030: a package from the partner portal fetches this server's trial licence by itself (no copying of IDs).</summary>
        static void PortalLicense(SystemConfig cfg, string packageServerDir, Action<string> say)
        {
            var pf = Path.Combine(packageServerDir, "portal.xml");
            if (!File.Exists(pf)) return;
            try
            {
                var p = XElement.Load(pf);
                // LIC-150: kept for "Update licence" — a licence bought later arrives with one click
                var le = cfg.Doc.Root.Element("LICENSE");
                le.SetAttributeValue("PORTAL_URL", (string)p.Attribute("URL")); le.SetAttributeValue("PORTAL_ACCOUNT", (string)p.Attribute("ACCOUNT")); le.SetAttributeValue("PORTAL_TOKEN", (string)p.Attribute("TOKEN"));
                cfg.Save();
                if ((string)p.Attribute("TRIAL") == "0") { say("STEP_LICENSE_FREE"); return; }   // LIC-075: the free edition needs no licence
                var body = Json.Write(new Dictionary<string, object> { { "account", (string)p.Attribute("ACCOUNT") }, { "token", (string)p.Attribute("TOKEN") }, { "serverId", cfg.ServerId } });
                using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) })
                {
                    var res = http.PostAsync(((string)p.Attribute("URL")).TrimEnd('/') + "/portal/api/autolicense", new System.Net.Http.StringContent(body, Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
                    var j = Json.Obj(Json.Parse(res.Content.ReadAsStringAsync().GetAwaiter().GetResult()));
                    if (!res.IsSuccessStatusCode || string.IsNullOrEmpty(Json.Str(j, "license"))) throw new InvalidOperationException(Json.Str(j, "message") ?? ("HTTP " + (int)res.StatusCode));
                    var lic = License.Check(Json.Str(j, "license"), cfg.ServerId, SystemClock.UtcNow);
                    if (!lic.Valid) throw new InvalidOperationException(lic.Reason);
                    cfg.Doc.Root.Element("LICENSE").SetAttributeValue("KEY", Json.Str(j, "license"));
                    cfg.Save(); cfg.ResetLicense();
                    say("STEP_LICENSE|" + lic.Expires.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                }
            }
            catch (Exception e) { say("STEP_LICENSE_LATER|" + e.Message); }
        }

        static X509Certificate2 MakeCertificate(string host)
        {
            using (var rsa = RSA.Create(3072))
            {
                var req = new CertificateRequest("CN=" + host, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                var san = new SubjectAlternativeNameBuilder();
                IPAddress ip;
                if (IPAddress.TryParse(host, out ip)) san.AddIpAddress(ip); else san.AddDnsName(host);
                san.AddDnsName("localhost");
                req.CertificateExtensions.Add(san.Build());
                req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
                req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
                using (var c = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10)))
                {
                    var pfx = c.Export(X509ContentType.Pfx, "x");
                    var flags = X509KeyStorageFlags.Exportable | (OperatingSystem.IsWindows() ? X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet : X509KeyStorageFlags.EphemeralKeySet);
                    var result = new X509Certificate2(pfx, "x", flags);
                    if (OperatingSystem.IsWindows()) result.FriendlyName = CertName;
                    return result;
                }
            }
        }

        /// <summary>Tests: in place of the real check that the server answers over HTTPS (port, pin, progress).</summary>
        public static Action<int, string, Action<string>> Answers;
        static void Answer(int port, string pin, Action<string> say) { if (Answers != null) Answers(port, pin, say); else if (TestRoot == null) Verify(port, pin, say); }

        static void Verify(int port, string pin, Action<string> say)
        {
            say("STEP_VERIFY");
            for (int i = 0; i < 15; i++)
            {
                try
                {
                    using (var tcp = new TcpClient("127.0.0.1", port))
                    using (var ssl = new System.Net.Security.SslStream(tcp.GetStream(), false, (s, c, ch, e) => c != null && Bytes.Hex(Bytes.Sha256(c.GetRawCertData())) == pin))
                    {
                        ssl.AuthenticateAsClient("localhost");
                        say("STEP_VERIFIED|" + port);
                        return;
                    }
                }
                catch (Exception) { System.Threading.Thread.Sleep(2000); }
            }
            throw new InvalidOperationException("ERR_NO_ANSWER");
        }
    }
}
