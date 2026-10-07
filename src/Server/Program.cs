using System;
using OnlineBackup.Core;
using System.Collections.Generic;
using System.Threading;

namespace OnlineBackup.Server
{
    /// <summary>
    /// OnlineBackup.Server init --system-home D:\Backup\system --admin admin --password ****** --host backup.example.com:8443
    ///                           --user-home "E:\users|UNLIMITED" [--user-home "F:\users|150"]
    /// OnlineBackup.Server run  --system-home D:\Backup\system --prefix https://+:8443/
    /// OnlineBackup.Server config  --system-home D:\Backup\system [--public-url https://backup.company.co.il:8443] [--cert-pin SHA256]
    /// OnlineBackup.Server scope [pilot|full] --system-home DIR          (PILOT-010: the pilot "Windows File Backup" switch)
    /// OnlineBackup.Server server-id --system-home DIR                    (the id a licence is issued for)
    /// OnlineBackup.Server license-keygen [--out license-private.key]     (product owner, once)
    /// OnlineBackup.Server license-issue --key license-private.key --server-id OB-… --company "…" [--edition PRO] [--users 100] [--computers 500] [--storage-gb 5000] [--modules FILE,MSSQL,…] [--days 365] [--center http://license.example.com:9443]
    /// OnlineBackup.Server license-center --key license-private.key --data D:\LicenseCenter [--prefix http://+:9443/]
    /// OnlineBackup.Server license-center-install-service --key C:\LicenseCenter\license-private.key --data C:\LicenseCenter\data [--prefix http://+:9443/]
    /// OnlineBackup.Server license-center-approve|license-center-revoke --data D:\LicenseCenter --license L-…
    /// OnlineBackup.Server license-center-portal --data D:\LicenseCenter --package D:\OnlineBackup-Server-pkg --center-url https://license.example.com:9443 [--owner-password P] [--signup open|closed] [--trial-days 30] [--trial-users 10] [--trial-computers 25] [--trial-storage-gb 500]
    /// OnlineBackup.Server adduser --system-home D:\Backup\system --login U --password P [--quota-gb 10] [--email E]   (server stopped or not)
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            if (args.Length == 0) { Console.Error.WriteLine("usage: init | run (see Program.cs)"); return 2; }
            var o = Parse(args);
            Func<string, string> one = k => o.ContainsKey(k) ? o[k][0] : null;
            try
            {
                switch (args[0])
                {
                    case "init":
                        SystemConfig.Init(one("system-home"), one("admin") ?? "admin", one("password"), one("host"), o.ContainsKey("user-home") ? o["user-home"] : new List<string>());
                        Console.WriteLine("initialised " + one("system-home"));
                        return 0;
                    case "setup":
                        {
                            // SETUP-001: the installation wizard in the browser (Setup.cmd); the files beside this exe are the package
                            using (var w = new SetupWizard(AppContext.BaseDirectory))
                            {
                                Console.WriteLine("Installation wizard: " + w.Url);
                                Console.WriteLine("(keep this window open until the installation is finished)");
                                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(w.Url) { UseShellExecute = true }); }
                                catch (Exception) { Console.WriteLine("Open this address in a browser: " + w.Url); }
                                w.Closed.WaitOne(TimeSpan.FromHours(4));
                            }
                            return 0;
                        }
                    case "update":
                        {
                            // UPD-010: started by the running server with the new version unpacked beside it — the installer in
                            // update mode (stop the service, replace the program files, start it again); the data is not touched
                            var log = one("log");
                            Action<string> say = m => { try { if (log != null) System.IO.File.AppendAllText(log, SystemClock.UtcNow.ToString("o") + " " + m + Environment.NewLine); } catch (Exception) { } Console.WriteLine(m); };
                            if (Installer.Existing() == null) { say("No installed server to update."); return 1; }
                            System.Threading.Thread.Sleep(2000);   // the running server answers its last request first
                            Installer.Run(new SetupAnswers(), AppContext.BaseDirectory, say);
                            return 0;
                        }
                    case "install-service":
                        if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("Windows only"); return 2; }
                        Console.WriteLine(ServerService.Install(Environment.ProcessPath, System.IO.Path.GetFullPath(one("system-home")), one("prefix") ?? "https://+:8443/"));
                        return 0;
                    case "uninstall-service":
                        if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("Windows only"); return 2; }
                        Console.WriteLine(ServerService.Uninstall());
                        return 0;
                    case "unlock":
                        {
                            // SEC-090 (owner): locked out of the management site — on the server, as administrator:
                            //   OnlineBackup.Server.exe unlock [--login NAME] [--reset-2fa]
                            var home = one("system-home") ?? (string)Installer.Existing()?.Attribute("SYSTEM_HOME");
                            if (string.IsNullOrEmpty(home)) { Console.Error.WriteLine("No installed server here."); return 1; }
                            bool svc = OperatingSystem.IsWindows() && one("system-home") == null;
                            if (svc) RunSc("stop " + ServerService.Name, 60);   // the running server keeps its settings in memory
                            try
                            {
                                var who = Staff.Unlock(SystemConfig.Load(home), one("login"), Array.IndexOf(args, "--reset-2fa") >= 0);
                                Console.WriteLine("Unlocked: " + who + (Array.IndexOf(args, "--reset-2fa") >= 0 ? " — at the next sign-in the site shows a new QR code for the authenticator app" : ""));
                            }
                            finally { if (svc) RunSc("start " + ServerService.Name, 0); }
                            return 0;
                        }
                    case "config":
                        {
                            // PKG-050: the installer fills the address customers use and the certificate pin
                            var cfgc = SystemConfig.Load(one("system-home"));
                            if (one("public-url") != null) cfgc.Doc.Root.SetAttributeValue("PUBLIC_URL", one("public-url").TrimEnd('/'));
                            if (one("cert-pin") != null) cfgc.Doc.Root.SetAttributeValue("CERT_PIN", one("cert-pin").Replace(":", "").ToLowerInvariant());
                            cfgc.Save();
                            Console.WriteLine("saved");
                            return 0;
                        }
                    case "scope":
                        {
                            // PILOT-010: the pilot "Windows File Backup" — "pilot" refuses everything outside it, "full" is the whole
                            // product; on the server only (no web route changes it). Without a value: shows it. Run it with the server
                            // service stopped: a running server keeps its own copy of system.xml and writes it back when it saves.
                            var cfgs = SystemConfig.Load(one("system-home"));
                            var want = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1].ToLowerInvariant() : null;
                            if (want == "pilot") cfgs.Doc.Root.SetAttributeValue("SCOPE", OnlineBackup.Core.Scope.Pilot);
                            else if (want == "full") cfgs.Doc.Root.SetAttributeValue("SCOPE", null);
                            else if (want != null) { Console.Error.WriteLine("usage: scope [pilot|full] --system-home DIR"); return 2; }
                            if (want != null) cfgs.Save();
                            Console.WriteLine(cfgs.Pilot ? "pilot (Windows file backup only)" : "full");
                            if (want != null) Console.WriteLine("start the server service again (it reads the scope when it starts)");
                            return 0;
                        }
                    case "server-id":
                        Console.WriteLine(SystemConfig.Load(one("system-home")).ServerId);
                        return 0;
                    case "license-keygen":
                        {
                            // the product owner, once, on his own computer: the private key never goes anywhere else
                            var k = License.KeyGen();
                            System.IO.File.WriteAllText(one("out") ?? "license-private.key", k[0]);
                            Console.WriteLine("Private key saved to " + (one("out") ?? "license-private.key") + " — keep it safe, offline, with a backup.");
                            Console.WriteLine("Public key (send it to be built into the product):");
                            Console.WriteLine(k[1]);
                            return 0;
                        }
                    case "license-issue":
                        {
                            var key = OnlineBackup.Core.Atomic.ReadAllText(one("key")).Trim();
                            var mods = (one("modules") ?? string.Join(",", License.AllModules)).Split(',');
                            Console.WriteLine(License.Issue(key, one("id") ?? ("L-" + SystemClock.UtcNow.ToString("yyyyMMddHHmmss")), one("company") ?? "", one("edition") ?? "PRO",
                                one("server-id"), int.Parse(one("users") ?? "0"), double.Parse(one("storage-gb") ?? "0", System.Globalization.CultureInfo.InvariantCulture), mods,
                                SystemClock.UtcNow, SystemClock.UtcNow.AddDays(int.Parse(one("days") ?? "365")), int.Parse(one("computers") ?? "0"), one("center") ?? ""));
                            return 0;
                        }
                    case "license-center-install-service":
                        if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("Windows only"); return 2; }
                        Console.WriteLine(LicenseCenterService.Install(Environment.ProcessPath, System.IO.Path.GetFullPath(one("key")), System.IO.Path.GetFullPath(one("data")), one("prefix") ?? "http://+:9443/", one("cert-thumbprint")));
                        return 0;
                    case "license-center-uninstall-service":
                        if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("Windows only"); return 2; }
                        Console.WriteLine(LicenseCenterService.Uninstall());
                        return 0;
                    case "license-center":
                        if (OperatingSystem.IsWindows() && !Environment.UserInteractive)
                        {
                            System.ServiceProcess.ServiceBase.Run(new LicenseCenterService(one("key"), one("data"), one("prefix") ?? "http://+:9443/"));
                            return 0;
                        }
                        {
                            // the product owner's licensing centre
                            using (var c = new LicenseCenter(OnlineBackup.Core.Atomic.ReadAllText(one("key")).Trim(), one("data"), one("prefix") ?? "http://+:9443/"))
                            {
                                Console.WriteLine("licensing centre listening " + (one("prefix") ?? "http://+:9443/"));
                                var done = new ManualResetEvent(false);
                                Console.CancelKeyPress += (s2, e) => { e.Cancel = true; done.Set(); };
                                done.WaitOne();
                            }
                            return 0;
                        }
                    case "license-center-portal":
                        {
                            // PORTAL-010: the partner portal's settings (the portal itself runs inside the licensing centre, under /portal)
                            var portal = new Portal(one("data"), "", () => SystemClock.UtcNow);
                            var st = portal.Load();
                            if (one("package") != null) st.Package = System.IO.Path.GetFullPath(one("package"));
                            if (one("center-url") != null) st.CenterUrl = one("center-url").TrimEnd('/');
                            if (one("owner-password") != null) { if (!OnlineBackup.Core.Passwords.Ok(one("owner-password"))) { Console.Error.WriteLine("owner " + OnlineBackup.Core.Passwords.Rule); return 2; } st.OwnerHash = Portal.Hash(one("owner-password")); }
                            if (one("signup") != null) st.SignupOpen = one("signup") != "closed";
                            if (one("trial-days") != null) st.TrialDays = int.Parse(one("trial-days"));
                            if (one("trial-users") != null) st.TrialUsers = int.Parse(one("trial-users"));
                            if (one("trial-computers") != null) st.TrialComputers = int.Parse(one("trial-computers"));
                            if (one("trial-storage-gb") != null) st.TrialStorageGB = double.Parse(one("trial-storage-gb"), System.Globalization.CultureInfo.InvariantCulture);
                            portal.Save(st);
                            Console.WriteLine("portal: package=" + st.Package + " center=" + st.CenterUrl + " signup=" + (st.SignupOpen ? "open" : "closed") + " owner sign-in=" + (st.OwnerHash.Length > 0 ? "set" : "NOT SET")
                                + " trial=" + st.TrialDays + " days, " + st.TrialUsers + " users, " + st.TrialComputers + " computers, " + st.TrialStorageGB + " GB");
                            if (st.Package.Length > 0 && !System.IO.Directory.Exists(System.IO.Path.Combine(st.Package, "server"))) Console.WriteLine("warning: " + st.Package + " has no server folder (unzip OnlineBackup-Server-<version>.zip there)");
                            return 0;
                        }
                    case "license-center-list":
                        foreach (var f in System.IO.Directory.GetFiles(one("data"), "*.json")) Console.WriteLine(System.IO.Path.GetFileNameWithoutExtension(f) + "\t" + OnlineBackup.Core.Atomic.ReadAllText(f));
                        return 0;
                    case "license-center-approve":
                    case "license-center-revoke":
                        {
                            // works on the centre's data folder (also while the centre runs)
                            var p2 = System.IO.Path.Combine(one("data"), one("license") + ".json");
                            var rec = System.IO.File.Exists(p2) ? OnlineBackup.Core.Json.Obj(OnlineBackup.Core.Json.Parse(OnlineBackup.Core.Atomic.ReadAllText(p2))) : new Dictionary<string, object>();
                            rec[args[0] == "license-center-approve" ? "approved" : "revoked"] = true;
                            OnlineBackup.Core.Atomic.WriteText(p2, OnlineBackup.Core.Json.Write(rec));
                            Console.WriteLine("saved");
                            return 0;
                        }
                    case "adduser":
                        {
                            double gb;
                            long? quota = double.TryParse(one("quota-gb"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out gb) ? (long)(gb * 1024 * 1024 * 1024) : (long?)null;
                            var p = new Users(SystemConfig.Load(one("system-home"))).Create(one("login"), one("password"), one("login"), quota, "COMPRESSED", one("email") ?? "", "local");
                            Console.WriteLine("user " + p.Get("LOGIN_NAME"));
                            return 0;
                        }
                    case "run":
                        if (OperatingSystem.IsWindows() && !Environment.UserInteractive)
                        {
                            System.ServiceProcess.ServiceBase.Run(new ServerService(one("system-home"), one("prefix") ?? "https://+:8443/"));
                            return 0;
                        }
                        var cfg = SystemConfig.Load(one("system-home"));
                        using (var api = new Api(cfg))
                        {
                            api.Start(one("prefix") ?? "http://localhost:8080/");
                            Console.WriteLine("listening " + (one("prefix") ?? "http://localhost:8080/"));
                            var done = new ManualResetEvent(false);
                            Console.CancelKeyPress += (s, e) => { e.Cancel = true; done.Set(); };
                            done.WaitOne();
                        }
                        return 0;
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("error: " + e.Message);
                // night soak: a start after the machine was cut off failed once with only "Value cannot be null." — the kind
                // of error and where it came from are kept, so the next one can be traced (not reproduced in 6 kill-restarts)
                if (!(e is ApiException)) Console.Error.WriteLine("detail: " + e.GetType().FullName + " " + (e.StackTrace ?? "").Trim().Split('\n')[0].Trim());
                return 1;
            }
            Console.Error.WriteLine("unknown command " + args[0]);
            return 2;
        }

        static Dictionary<string, List<string>> Parse(string[] args)
        {
            var d = new Dictionary<string, List<string>>();
            for (int i = 1; i < args.Length; i++)
                if (args[i].StartsWith("--") && i + 1 < args.Length)
                {
                    var k = args[i].Substring(2);
                    if (!d.ContainsKey(k)) d[k] = new List<string>();
                    d[k].Add(args[++i]);
                }
            return d;
        }

        static void RunSc(string args, int waitStoppedSeconds)
        {
            try { ProcessRunner.Run(new System.Diagnostics.ProcessStartInfo("sc.exe", args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }, Limits.Short); } catch (Exception) { }
            for (int i = 0; i < waitStoppedSeconds; i++)
            {
                try
                {
                    var o = ProcessRunner.Run(new System.Diagnostics.ProcessStartInfo("sc.exe", "query " + ServerService.Name) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }, Limits.Short).Out;
                    if (o.Contains("STOPPED") || !o.Contains("STATE")) return;
                }
                catch (Exception) { return; }
                System.Threading.Thread.Sleep(1000);
            }
        }
    }
}
