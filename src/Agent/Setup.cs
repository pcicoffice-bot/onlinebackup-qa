using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using OnlineBackup.Core;

namespace OnlineBackup.Agent
{
    /// <summary>
    /// PKG-040: "Setup.cmd" of the client package runs "OnlineBackup.Agent.exe setup": installs the agent under the IT
    /// company's product name — Program Files\&lt;product&gt;, the data in ProgramData\&lt;product&gt; (SYSTEM + Administrators
    /// only), registration with the customer's user name and password, the Windows service (display name = product) and a
    /// desktop / Start-menu shortcut that opens the customer's screen. Works on Windows 2003 (.NET 4.0, no PowerShell).
    /// Unattended: setup --login U --password P [--otp X] [--accept-contract]; a new customer: add --company C --email E.
    /// Tests: --install-dir / --data-dir / --no-service.
    /// PKG-060 Linux (setup.sh as root): /opt/&lt;folder&gt;, data in /var/lib/&lt;folder&gt; (root only, 700), a systemd service.
    /// PKG-070 macOS (setup.command as root): /usr/local/&lt;folder&gt;, data in /Library/Application Support/&lt;folder&gt; (700),
    /// a LaunchDaemon (starts with the Mac, restarts after a crash) and /Applications/&lt;product&gt;.app that opens the screen.
    /// </summary>
    public static class Setup
    {
        public sealed class Result { public string InstallDir, DataDir, Product, Server; public bool Service, Shortcut; }

        public static Result Run(string packageDir, Func<string, string> opt, Func<string, bool> flag, Action<string> say)
        {
            var conn = XElement.Load(Path.Combine(packageDir, "connection.xml"));
            var brandPath = Path.Combine(packageDir, "branding.xml");
            var brand = File.Exists(brandPath) ? XElement.Load(brandPath) : new XElement("BRANDING");
            var product = (string)brand.Attribute("PRODUCT"); if (string.IsNullOrEmpty(product)) product = "ITSguard Server Online";
            var folder = (string)conn.Attribute("FOLDER"); if (string.IsNullOrEmpty(folder)) folder = "OnlineBackup";
            bool windows = Environment.OSVersion.Platform == PlatformID.Win32NT;
            bool mac = !windows && (flag("mac") || IsMac);
            var r = new Result
            {
                Product = product, Server = string.IsNullOrEmpty(opt("server")) ? (string)conn.Attribute("SERVER") : opt("server"),
                InstallDir = opt("install-dir") ?? (windows ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), folder) : mac ? "/usr/local/" + folder.Replace(' ', '-') : "/opt/" + folder),
                DataDir = opt("data-dir") ?? (windows ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), folder) : mac ? "/Library/Application Support/" + folder : "/var/lib/" + folder)
            };
            say(product + " — " + r.Server);

            // 1. files (SETUP-C70: an installation already here — the program window and the service stop first)
            if (windows && !flag("no-service"))
            {
                foreach (var p in Process.GetProcessesByName("OnlineBackup.Client")) try { p.Kill(); p.WaitForExit(5000); } catch (Exception) { }
                try { ServiceSetup.Stop(); } catch (Exception) { }
            }
            Directory.CreateDirectory(r.InstallDir);
            if (!string.Equals(Path.GetFullPath(packageDir).TrimEnd('\\', '/'), Path.GetFullPath(r.InstallDir).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                foreach (var f in Directory.GetFiles(packageDir).Where(f => !f.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) && !f.EndsWith(".sh", StringComparison.OrdinalIgnoreCase) && (!f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f).Equals("version.txt", StringComparison.OrdinalIgnoreCase))))
                {
                    var dst = Path.Combine(r.InstallDir, Path.GetFileName(f));
                    try
                    {
                        if (!windows && File.Exists(dst)) File.Delete(dst);   // a running binary is replaced, not overwritten in place
                        File.Copy(f, dst, true);
                    }
                    catch (IOException) when (f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) { throw new AgentException(0, "BUSY", "The software is running — stop the service (" + product + ") and run again."); }
                }
            if (!windows)
                foreach (var x in new[] { "OnlineBackup.Agent", "restic" }) if (File.Exists(Path.Combine(r.InstallDir, x))) Exec("chmod", "755 \"" + Path.Combine(r.InstallDir, x) + "\"");
            say("Installed to " + r.InstallDir);

            // SETUP-C20: another server than the package's (chosen in the wizard) comes with its own pin (or none: a public certificate)
            var pin = !string.IsNullOrEmpty(opt("server")) && !SameServer(opt("server"), (string)conn.Attribute("SERVER")) ? opt("pin") : (string)conn.Attribute("PIN");
            if (string.IsNullOrEmpty(pin)) pin = null;
            if (!SameServer(r.Server, (string)conn.Attribute("SERVER")))
            {
                var c2 = new XElement(conn); c2.SetAttributeValue("SERVER", r.Server); c2.SetAttributeValue("PIN", string.IsNullOrEmpty(pin) ? null : pin);
                try { c2.Save(Path.Combine(r.InstallDir, "connection.xml")); } catch (Exception) { }
            }

            // 2. CONTRACT-010: the IT company's contract first — the installation goes on only when it is accepted
            Directory.CreateDirectory(r.DataDir);
            var app = new AgentApp(r.DataDir);
            // SETUP-C50: the installation only installs; the program then connects (server, user name and password).
            // The silent installation (--login / --company) still connects here, in one step.
            if (!flag("no-register")) Connect(app, r.Server, pin, r.DataDir, opt, flag, say);

            if (windows)
                Exec("icacls", "\"" + r.DataDir + "\" /inheritance:r /grant:r SYSTEM:(OI)(CI)F Administrators:(OI)(CI)F");
            else
                Exec("chmod", "700 \"" + r.DataDir + "\"");

            // 3. service and shortcut
            var exe = Path.Combine(r.InstallDir, windows ? "OnlineBackup.Agent.exe" : "OnlineBackup.Agent");
            if (windows && !flag("no-service"))
            {
                say(ServiceSetup.Install(exe, r.DataDir, product)); r.Service = true;
                try { RegisterUninstall(r, folder, (string)brand.Attribute("COMPANY")); } catch (Exception e) { say("Not added to Programs and Features: " + e.Message); }
            }
            if (mac)
            {
                var label = LaunchdLabel(folder);
                var plist = Path.Combine(opt("unit-dir") ?? "/Library/LaunchDaemons", label + ".plist");
                if (!flag("no-service"))
                {
                    File.WriteAllText(plist, LaunchdPlist(label, exe, r.DataDir));
                    Exec("chmod", "644 \"" + plist + "\"");
                    Exec("launchctl", "bootout system/" + label);              // an older version, when updating
                    if (Exec("launchctl", "bootstrap system \"" + plist + "\"") != 0) Exec("launchctl", "load -w \"" + plist + "\"");
                    r.Service = true;
                    say("Service: " + label + " (sudo launchctl print system/" + label + ")");
                }
                var appBundle = Path.Combine(opt("apps-dir") ?? "/Applications", ClientSafe(product) + ".app");
                if (!flag("no-shortcut"))
                    try { MacApp(appBundle, product, exe, ClientUi.UiFileIn(r.InstallDir)); r.Shortcut = true; say("App: " + appBundle); }
                    catch (Exception e) { say("App not created: " + e.Message); }
                say("Important: System Settings → Privacy & Security → Full Disk Access → add " + exe + " (otherwise macOS hides Documents, Desktop and Mail from the backup).");
                say("Done. Open \"" + product + "\" from Applications to add backups and restore.");
                return r;
            }
            if (!windows)
            {
                var unitPath = Path.Combine(opt("unit-dir") ?? "/etc/systemd/system", folder.Replace(' ', '-') + ".service");
                if (!flag("no-service"))
                {
                    File.WriteAllText(unitPath, SystemdUnit(exe, r.DataDir, product));
                    Exec("systemctl", "daemon-reload");
                    Exec("systemctl", "enable --now \"" + Path.GetFileName(unitPath) + "\"");
                    r.Service = true;
                }
                if (r.Service) say("Service: " + Path.GetFileName(unitPath) + " (systemctl status " + Path.GetFileNameWithoutExtension(unitPath) + ")");
                say("Done. Backup sets: " + exe + " addset --home " + r.DataDir + " ... ; customer screen: " + exe + " open --home " + r.DataDir);
                return r;
            }
            if (windows && !flag("no-shortcut"))
                try
                {
                    // CLI-100: the shortcuts open the program window (OnlineBackup.Client.exe); the agent stays the service
                    var gui = Path.Combine(r.InstallDir, "OnlineBackup.Client.exe");
                    if (File.Exists(gui))
                    {
                        foreach (var d in new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms) })
                            Shortcut(Path.Combine(d, ClientSafe(product) + ".lnk"), gui, "", r.InstallDir, product, 1);
                        Shortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), ClientSafe(product) + ".lnk"), gui, "--tray", r.InstallDir, product, 7);
                        r.Shortcut = true;
                        say("Done. Open \"" + product + "\" from the desktop to add backups and restore.");
                        return r;
                    }
                    foreach (var d in new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms) })
                        Shortcut(Path.Combine(d, ClientSafe(product) + ".lnk"), exe, "open --home \"" + r.DataDir + "\"", r.InstallDir, product);
                    r.Shortcut = true;
                }
                catch (Exception e) { say("Shortcut not created: " + e.Message); }
            say("Done. Open \"" + product + "\" from the desktop to add backups and restore.");
            return r;
        }

        /// <summary>
        /// Connects this computer to its backup server: the IT company's contract (accepted), then an existing customer
        /// (user name and password) or a new one (SIGNUP-010); restic gets the same pinned certificate (TLS-030).
        /// Used by the silent installation and by the program's sign-in screen (through the service).
        /// </summary>
        public static string Connect(AgentApp app, string server, string pin, string dataDir, Func<string, string> opt, Func<string, bool> flag, Action<string> say)
        {
        int contract = 0;
        try
        {
            var ct = AgentApp.Contract(server, opt("lang") ?? System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, string.IsNullOrEmpty(pin) ? null : pin);
            contract = ct.Int("version");
            // SIGNUP-020: a new customer accepts the contract the server shows at sign-up, even when the installation does not show it
            var isNew = opt("company") != null;
            if (contract > 0 && (ct.Int("install") == 1 || (isNew && ct.Int("signup") == 1)))
            {
                say(ct["text"]);
                var ok = flag("accept-contract") || (opt("login") == null && (Ask("Do you accept the agreement above? (yes / no): ", false) ?? "").Trim().ToLowerInvariant().StartsWith("y"));
                if (!ok) throw new AgentException(0, "CONTRACT", "The agreement was not accepted — the installation stopped. (Unattended: --accept-contract)");
                say("Agreement version " + contract + " accepted");
            }
            else contract = 0;
        }
        catch (AgentException e) when (e.Code != "CONTRACT") { }

        // 3. registration: an existing customer (user name and password), or SIGNUP-010 a new one opened here
        string login;
        var password = opt("password");
        if (opt("company") != null || (opt("login") == null && (Ask("Are you a new customer? (yes / no): ", false) ?? "").Trim().ToLowerInvariant().StartsWith("y")))
        {
            var company = opt("company") ?? Ask("Company name: ", false);
            var email = opt("email") ?? Ask("E-mail: ", false);
            login = opt("login") ?? Ask("Choose a user name: ", false);
            password = password ?? Ask("Choose a password (8+ characters with a letter): ", true);
            login = app.Signup(server, company, email, opt("phone"), login, password, contract, Environment.MachineName, string.IsNullOrEmpty(pin) ? null : pin);
            say("New customer " + login + " — registered " + Environment.MachineName);
        }
        else
        {
            login = opt("login") ?? Ask("User name: ", false);
            password = password ?? Ask("Password: ", true);
            app.Register(server, login, password, opt("otp"), Environment.MachineName, null, string.IsNullOrEmpty(pin) ? null : pin, contract);
            say("Registered " + Environment.MachineName + " as " + login);
        }
        // TLS-030: restic must trust the same (pinned) server certificate: fetched once, checked against the pin, kept as PEM
        if (!string.IsNullOrEmpty(pin) && server.StartsWith("https:", StringComparison.OrdinalIgnoreCase))
            try
            {
                var pem = Path.Combine(dataDir, "server.pem");
                File.WriteAllText(pem, ServerCertificatePem(new Uri(server), pin));
                var cfg = app.Home.Config; cfg.SetAttributeValue("CACERT", pem); app.Home.Config = cfg;
            }
            catch (Exception e) { say("Server certificate not saved for restic: " + e.Message); }
            return login;
        }

        // ---------------------------------------------------------------- SETUP-C70: Programs and Features, repair, remove

        const string UninstallRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\";
        static string UninstallKey(string folder) { return UninstallRoot + "OnlineBackup." + folder; }

        /// <summary>The program in Windows' "Programs and Features" (Apps): its name, version, company, size, icon, and "Uninstall".</summary>
        static void RegisterUninstall(Result r, string folder, string company)
        {
            using (var k = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(UninstallKey(folder)))
            {
                var ver = ""; try { ver = File.ReadAllText(Path.Combine(r.InstallDir, "version.txt")).Trim(); } catch (Exception) { }
                long kb = 0; try { kb = Directory.GetFiles(r.InstallDir).Sum(f => new FileInfo(f).Length) / 1024; } catch (Exception) { }
                var agent = Path.Combine(r.InstallDir, "OnlineBackup.Agent.exe"); var gui = Path.Combine(r.InstallDir, "OnlineBackup.Client.exe");
                k.SetValue("DisplayName", r.Product);
                k.SetValue("DisplayVersion", ver);
                k.SetValue("Publisher", string.IsNullOrEmpty(company) ? r.Product : company);
                k.SetValue("InstallLocation", r.InstallDir);
                k.SetValue("DataLocation", r.DataDir);
                k.SetValue("DisplayIcon", File.Exists(gui) ? gui : agent);
                k.SetValue("UninstallString", "\"" + agent + "\" uninstall");
                k.SetValue("QuietUninstallString", "\"" + agent + "\" uninstall --quiet");
                k.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, kb), Microsoft.Win32.RegistryValueKind.DWord);
                k.SetValue("NoModify", 1, Microsoft.Win32.RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, Microsoft.Win32.RegistryValueKind.DWord);
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            }
        }

        /// <summary>An installation already on this computer (Programs and Features, or the program in its folder): its folders and version; null when there is none.</summary>
        public static Result Installed(string folder)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return null;
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(UninstallKey(folder)))
                    if (k != null && File.Exists(Path.Combine((string)k.GetValue("InstallLocation") ?? "", "OnlineBackup.Agent.exe")))
                        return new Result { InstallDir = (string)k.GetValue("InstallLocation"), DataDir = (string)k.GetValue("DataLocation"), Product = (string)k.GetValue("DisplayName"), Server = (string)k.GetValue("DisplayVersion") };
            }
            catch (Exception) { }
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), folder);
            if (File.Exists(Path.Combine(dir, "OnlineBackup.Agent.exe")))
                return new Result { InstallDir = dir, DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), folder), Product = folder, Server = "" };
            return null;
        }

        /// <summary>
        /// Removes the software from this computer: the program window, the service, the shortcuts, Programs and Features and
        /// the program folder. The backups on the server stay; this computer's settings stay too unless removeSettings
        /// (then a new installation connects as a new start: the user name and password again).
        /// </summary>
        public static void Uninstall(string installDir, string dataDir, string product, string folder, bool removeSettings, Action<string> say)
        {
            foreach (var p in Process.GetProcessesByName("OnlineBackup.Client")) try { p.Kill(); p.WaitForExit(5000); } catch (Exception) { }
            say(ServiceSetup.Uninstall().Trim().Length > 0 ? "Service stopped and removed" : "Service removed");
            foreach (var d in new[] { Environment.SpecialFolder.CommonDesktopDirectory, Environment.SpecialFolder.CommonPrograms, Environment.SpecialFolder.CommonStartup })
                try { var l = Path.Combine(Environment.GetFolderPath(d), ClientSafe(product) + ".lnk"); if (File.Exists(l)) File.Delete(l); } catch (Exception) { }
            say("Shortcuts removed");
            try { Microsoft.Win32.Registry.LocalMachine.DeleteSubKeyTree(UninstallKey(folder)); } catch (Exception) { }
            if (removeSettings && !string.IsNullOrEmpty(dataDir) && Directory.Exists(dataDir) && IsOwnFolder(dataDir))
                try { Directory.Delete(dataDir, true); say("This computer's settings removed"); } catch (Exception e) { say("Settings not removed: " + e.Message); }
            if (!string.IsNullOrEmpty(installDir) && Directory.Exists(installDir) && IsOwnFolder(installDir))
            {
                var self = Process.GetCurrentProcess().MainModule.FileName;
                if (self.StartsWith(installDir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                    // this program runs from that folder: removed a moment after it ends
                    Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 4 127.0.0.1 >nul & rd /s /q \"" + installDir + "\"") { CreateNoWindow = true, UseShellExecute = false });
                else
                    try { Directory.Delete(installDir, true); } catch (Exception e) { say("Some files are in use and stay until a restart: " + e.Message); }
                say("Program files removed");
            }
            say("Done. The backups on the server are kept.");
        }

        /// <summary>Only a folder of this software is ever removed (the agent or its settings inside, never a drive or a system folder).</summary>
        static bool IsOwnFolder(string dir)
        {
            var full = Path.GetFullPath(dir).TrimEnd('\\', '/');
            if (full.Length < 8 || Path.GetPathRoot(full).TrimEnd('\\', '/') == full) return false;
            foreach (var f in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.Windows, Environment.SpecialFolder.System })
                if (string.Equals(full, Environment.GetFolderPath(f).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return false;
            return File.Exists(Path.Combine(full, "OnlineBackup.Agent.exe")) || File.Exists(Path.Combine(full, "agent.xml")) || File.Exists(Path.Combine(full, "connection.xml"));
        }

        public static bool SameServer(string a, string b) { return string.Equals((a ?? "").Trim().TrimEnd('/'), (b ?? "").Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase); }

        /// <summary>SETUP-C20: the SHA-256 of the server's certificate and whether Windows trusts it as it is (a public certificate).</summary>
        public static string Fingerprint(Uri url, out bool trusted, int timeoutMs = 10000)
        {
            byte[] raw = null; bool ok = false;
            using (var tcp = new System.Net.Sockets.TcpClient())
            {
                var ar = tcp.BeginConnect(url.Host, url.Port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) throw new AgentException(0, "CONNECT", "No answer from " + url.Host + ":" + url.Port + ". Inside the office, write the server's internal address; from outside, the port must be forwarded in the router.");
                tcp.EndConnect(ar);
                tcp.ReceiveTimeout = tcp.SendTimeout = timeoutMs;   // a port that takes the connection but never answers TLS
                using (var ssl = new System.Net.Security.SslStream(tcp.GetStream(), false, (s, c, ch, e) => { if (c != null) raw = c.GetRawCertData(); ok = e == System.Net.Security.SslPolicyErrors.None; return true; }))
                    ssl.AuthenticateAsClient(url.Host);
            }
            if (raw == null) throw new AgentException(0, "CERT", "no certificate");
            trusted = ok;
            return Bytes.Hex(Bytes.Sha256(raw));
        }

        /// <summary>The server's certificate as PEM, accepted only when its SHA-256 is the pin.</summary>
        public static string ServerCertificatePem(Uri url, string pin)
        {
            byte[] raw = null;   // copied inside the callback: the certificate object dies with the connection
            using (var tcp = new System.Net.Sockets.TcpClient(url.Host, url.Port))
            using (var ssl = new System.Net.Security.SslStream(tcp.GetStream(), false, (s, c, ch, e) => { if (c != null) raw = c.GetRawCertData(); return true; }))
                ssl.AuthenticateAsClient(url.Host);
            if (raw == null) throw new AgentException(0, "CERT", "no certificate");
            if (Bytes.Hex(Bytes.Sha256(raw)) != pin.Replace(":", "").ToLowerInvariant()) throw new AgentException(0, "CERT", "The server certificate does not match the pin");
            return "-----BEGIN CERTIFICATE-----\n" + Convert.ToBase64String(raw, Base64FormattingOptions.InsertLineBreaks).Replace("\r\n", "\n") + "\n-----END CERTIFICATE-----\n";
        }

        /// <summary>The systemd unit of the Linux agent: restarts after a crash, stops by SIGTERM (the run in progress is ended cleanly).</summary>
        public static string SystemdUnit(string exe, string dataDir, string product)
        {
            return "[Unit]\nDescription=" + product + "\nWants=network-online.target\nAfter=network-online.target\n\n"
                + "[Service]\nType=simple\nExecStart=\"" + exe + "\" service --home \"" + dataDir + "\"\nRestart=on-failure\nRestartSec=60\nKillSignal=SIGTERM\nTimeoutStopSec=120\n"
                + "Environment=DOTNET_CLI_TELEMETRY_OPTOUT=1\n\n[Install]\nWantedBy=multi-user.target\n";
        }

        /// <summary>A screen to show the installation wizard on: Windows, a Mac, or a Linux desktop (a server without one keeps the questions in the console).</summary>
        public static bool HasDesktop
        {
            get
            {
                if (Environment.OSVersion.Platform == PlatformID.Win32NT || IsMac) return true;
                return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
            }
        }

        static bool IsMac { get { try { return File.Exists("/System/Library/CoreServices/SystemVersion.plist"); } catch (Exception) { return false; } } }

        /// <summary>The LaunchDaemon's name: com.onlinebackup.&lt;folder&gt; (letters, digits and dashes).</summary>
        public static string LaunchdLabel(string folder)
        {
            var id = new string(folder.ToLowerInvariant().Select(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ? c : '-').ToArray()).Trim('-');
            return "com.onlinebackup." + (id.Length == 0 ? "agent" : id);
        }

        static string X(string s) { return System.Security.SecurityElement.Escape(s); }

        /// <summary>The macOS LaunchDaemon: runs as root from boot, restarted after a crash, stopped with SIGTERM (the run in progress ends cleanly).</summary>
        public static string LaunchdPlist(string label, string exe, string dataDir)
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n<plist version=\"1.0\">\n<dict>\n"
                + "  <key>Label</key><string>" + X(label) + "</string>\n"
                + "  <key>ProgramArguments</key><array><string>" + X(exe) + "</string><string>service</string><string>--home</string><string>" + X(dataDir) + "</string></array>\n"
                + "  <key>RunAtLoad</key><true/>\n  <key>KeepAlive</key><dict><key>SuccessfulExit</key><false/></dict>\n  <key>ThrottleInterval</key><integer>60</integer>\n"
                + "  <key>ExitTimeOut</key><integer>120</integer>\n"
                + "  <key>StandardOutPath</key><string>" + X(Path.Combine(dataDir, "service.log")) + "</string>\n  <key>StandardErrorPath</key><string>" + X(Path.Combine(dataDir, "service.log")) + "</string>\n"
                + "</dict>\n</plist>\n";
        }

        /// <summary>
        /// /Applications/&lt;product&gt;.app: asks for the administrator password (the screen's address with its key is readable
        /// by root only), then opens the customer's screen in the default browser as the signed-in user.
        /// </summary>
        public static void MacApp(string appDir, string product, string exe, string uiFile)
        {
            var contents = Path.Combine(appDir, "Contents");
            Directory.CreateDirectory(Path.Combine(contents, "MacOS"));
            File.WriteAllText(Path.Combine(contents, "Info.plist"), "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n<plist version=\"1.0\">\n<dict>\n"
                + "  <key>CFBundleName</key><string>" + X(product) + "</string>\n  <key>CFBundleDisplayName</key><string>" + X(product) + "</string>\n"
                + "  <key>CFBundleIdentifier</key><string>" + X(LaunchdLabel(product) + ".app") + "</string>\n  <key>CFBundleExecutable</key><string>launcher</string>\n"
                + "  <key>CFBundlePackageType</key><string>APPL</string>\n  <key>CFBundleVersion</key><string>1.0</string>\n  <key>LSMinimumSystemVersion</key><string>10.15</string>\n"
                + "</dict>\n</plist>\n");
            var launcher = Path.Combine(contents, "MacOS", "launcher");
            var q = uiFile.Replace("'", "");
            File.WriteAllText(launcher, "#!/bin/sh\n# " + product + ": the customer's screen (served by the background service on 127.0.0.1)\n"
                + "URL=$(/usr/bin/osascript -e 'do shell script \"cat \\\"" + q + "\\\"\" with administrator privileges' 2>/dev/null)\n"
                + "case \"$URL\" in http://127.0.0.1:*) exec /usr/bin/open \"$URL\";; esac\n"
                + "/usr/bin/osascript -e 'display alert \"" + product.Replace("\"", "").Replace("'", "") + "\" message \"The backup service is not running. Restart the Mac or contact support.\"'\n");
            Exec("chmod", "755 \"" + launcher + "\"");
        }

        static string ClientSafe(string s) { var bad = Path.GetInvalidFileNameChars(); return new string(s.Where(c => Array.IndexOf(bad, c) < 0).ToArray()).Trim(); }

        /// <summary>A .lnk through the Windows Script Host (present from Windows 2000), by late binding.</summary>
        static void Shortcut(string lnk, string target, string args, string workDir, string description, int windowStyle = 7)
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            var shell = Activator.CreateInstance(t);
            var sc = t.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
            var st = sc.GetType();
            st.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { target });
            st.InvokeMember("Arguments", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { args });
            st.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { workDir });
            st.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { description });
            st.InvokeMember("WindowStyle", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { windowStyle });   // 1 normal window, 7 minimised
            st.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, sc, null);
        }

        static string Ask(string prompt, bool secret)
        {
            Console.Write(prompt);
            if (!secret) return (Console.ReadLine() ?? "").Trim();
            var sb = new StringBuilder();
            while (true)
            {
                ConsoleKeyInfo k;
                try { k = Console.ReadKey(true); }
                catch (InvalidOperationException) { return (Console.ReadLine() ?? "").Trim(); }   // input redirected
                if (k.Key == ConsoleKey.Enter) { Console.WriteLine(); return sb.ToString(); }
                if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
                if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
            }
        }

        public static int Exec(string exe, string args)
        {
            // static review "raw-process": stderr was redirected and never read (a chatty program filled the pipe and hung
            // the installation), and there was no limit (systemctl / launchctl that never answers)
            try { var r = ProcessRunner.Run(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }, Limits.Install); return r.TimedOut ? -1 : r.Code; }
            catch (Exception) { return -1; }
        }

        /// <summary>Opens an address in the default browser (Windows, macOS, Linux desktop).</summary>
        public static void OpenBrowser(string url)
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT) { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); return; }
            Process.Start(new ProcessStartInfo(IsMac ? "/usr/bin/open" : "xdg-open", "\"" + url + "\"") { UseShellExecute = false });
        }
    }
}
