using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using OnlineBackup.Core;

namespace OnlineBackup.Agent
{
    /// <summary>
    /// Command line of the agent (the Windows service runs "service"):
    ///   register --home DIR --server URL --login U --password P [--otp 123456] [--computer NAME] [--tls builtin|system] [--pin SHA256]
    ///   addset   --home DIR --password P [--otp X] --name N [--type FILE|MSSQL|MYSQL|POSTGRESQL|ORACLE|DOMINO|SYSTEMSTATE|BAREMETAL|HYPERV|VMWARE|M365] [--db-host H] [--db-user U] [--datacenter DC] [--thumbprint SHA256] [--engine RESTIC] --source PATH [--source PATH] [--exclude *.tmp] [--hour 22] [--keytype PASSWORD|DEFAULT|CUSTOM] [--key K]
    ///   sets     --home DIR
    ///   backup   --home DIR --set ID
    ///   points   --home DIR --set ID
    ///   restore  --home DIR --set ID --password P [--otp X] [--point ID] [--target DIR] [--filter TEXT] [--overwrite]
    ///   service  --home DIR                         (run by the Windows service; interactive = console)
    ///   install-service --home DIR / uninstall-service
    ///   sql-password --home DIR --set ID --password P (SQL Server login of a set, kept on this computer only)
    ///   vmware-restore --home DIR --set ID --password P [--point ID] --vm NAME [--datastore DS [--name NEW]] [--target DIR]
    ///   oracle-restore --home DIR --set ID --password P [--point ID] --sid SID --target DIR
    ///   restore-test --home DIR --set ID
    ///   restore-image --home DIR --set ID --password P [--otp X] [--point ID] --target E:\   (bare-metal image for Windows recovery)
    /// </summary>
    public static class Program
    {
        static bool IsAdmin()
        {
            try { return new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator); }
            catch (Exception) { return false; }
        }

        public static int Main(string[] args)
        {
            if (args.Length == 0) { Console.Error.WriteLine("usage: register | addset | sets | backup | points | restore | service"); return 2; }
            var o = Parse(args);
            Func<string, string> one = k => o.ContainsKey(k) ? o[k][0] : null;
            var app = new AgentApp(one("home") ?? DefaultHome());
            try
            {
                switch (args[0])
                {
                    case "register":
                        app.Register(one("server"), one("login"), one("password"), one("otp"), one("computer"), one("tls") == null ? null : one("tls").ToUpperInvariant(), one("pin"));
                        Console.WriteLine("registered " + app.Home.Computer);
                        return 0;
                    case "signup":
                        {
                            // SIGNUP-010: a new customer from the client software; the contract is shown and must be accepted (--accept)
                            var ct = AgentApp.Contract(one("server"), one("lang"), one("pin"));
                            int cv = ct.Int("version");
                            if (cv > 0 && one("accept") != cv.ToString(System.Globalization.CultureInfo.InvariantCulture)) { Console.WriteLine(ct["text"]); Console.WriteLine("Accept the contract above with --accept " + cv); return 3; }
                            Console.WriteLine("signed up " + app.Signup(one("server"), one("company"), one("email"), one("phone"), one("login"), one("password"), cv, one("computer"), one("pin")) + " · registered " + app.Home.Computer);
                            return 0;
                        }
                    case "addset":
                        {
                            var session = app.Interactive(one("password"), one("otp"));
                            var s = new BackupSetInfo { Name = one("name") ?? "Files", Sources = o.ContainsKey("source") ? o["source"] : new List<string>() };
                            if (one("type") != null) s.Type = one("type").ToUpperInvariant();
                            if (one("engine") != null) s.Engine = one("engine").ToUpperInvariant();
                            if (s.Type == "MYSQL" || s.Type == "POSTGRESQL") { s.DbHost = one("db-host") ?? ""; s.DbPort = one("db-port") == null ? 0 : int.Parse(one("db-port")); s.SqlUser = one("db-user") ?? ""; s.Vss = false; }
                            if (s.Type == "HYPERV") s.Vss = false;
                            if (s.Type == "ORACLE" || s.Type == "VMWARE") { s.DbHost = one("db-host") ?? one("host") ?? ""; s.DbPort = one("db-port") == null ? 0 : int.Parse(one("db-port")); s.SqlUser = one("db-user") ?? one("user") ?? ""; s.Vss = false; s.VmDatacenter = one("datacenter") ?? ""; s.VmThumbprint = one("thumbprint") ?? ""; }
                            if (s.Type == "DOMINO") { s.Vss = true; if (s.Sources.Count == 0) s.Sources = Domino.DefaultSources(); }
                            if (s.Type == "M365") { s.Engine = "RESTIC"; s.M365Tenant = one("tenant") ?? ""; s.M365ClientId = one("client-id") ?? ""; s.M365Users = one("users") ?? ""; s.Vss = false; }
                            if (one("hour") != null) s.Hour = int.Parse(one("hour"));
                            if (one("minute") != null) s.Minute = int.Parse(one("minute"));
                            if (one("keep-last") != null) s.Retention = new RetentionPolicy { Unit = "JOBS", Period = int.Parse(one("keep-last")) };
                            if (o.ContainsKey("exclude")) s.Filters.Add(new FilterRule { Type = "WILDCARD", ApplyFile = true, ApplyDir = false, Patterns = o["exclude"] });
                            var created = app.CreateSet(session, one("password"), s, one("keytype") ?? "PASSWORD", one("key"));
                            Console.WriteLine(created.Id);
                            return 0;
                        }
                    case "folders":   // SRC-030: send this computer's folders to the server now
                        app.SendFolders(app.Profile(), SystemClock.UtcNow, true);
                        Console.WriteLine("ok");
                        return 0;
                    case "sets":
                        foreach (var s in app.Sets()) Console.WriteLine(s.Id + "\t" + s.Name + "\t" + string.Join(";", s.Sources.ToArray()));
                        return 0;
                    case "backup":
                        {
                            var r = app.Backup(one("set"));
                            Console.WriteLine(r.Result + " new=" + r.New + " upd=" + r.Updated + " perm=" + r.PermOnly + " del=" + r.Deleted + " bytes=" + r.BytesSent);
                            foreach (var l in r.LogLines) { var f = AhsayLog.Fields(l); if (f.Length > 4 && (f[1] == "err" || f[1] == "warn")) Console.WriteLine(f[1] + ": " + f[2] + " " + f[4]); }
                            return r.Result.StartsWith("BS_STOP_SUCCESS") ? 0 : 1;
                        }
                    case "points":
                        {
                            var pset = app.Sets().FirstOrDefault(x => x.Id == one("set"));
                            if (pset != null && pset.Engine == "RESTIC")
                            {
                                // restic points live in the set's repository: listing them needs the encryption key
                                if (one("key") == null && one("password") == null) throw new AgentException(0, "KEY", "--password (or --key) is needed to list the points of this set");
                                foreach (var p in app.Restic(pset, one("key") ?? one("password")).SnapshotList()) Console.WriteLine(p["id"] + "\t" + p["time"]);
                                return 0;
                            }
                            foreach (var p in app.DeviceClient().Call("GET", "/api/sets/" + one("set") + "/points").List("points")) Console.WriteLine(p["id"]);
                            return 0;
                        }
                    case "restore":
                        {
                            var session = app.Interactive(one("password"), one("otp"));
                            var rset = app.Sets().FirstOrDefault(x => x.Id == one("set"));
                            if (rset != null && rset.Engine == "RESTIC")
                            {
                                var rl = new List<string>();
                                if (one("target") == null) throw new AgentException(0, "TARGET", "Choose a destination folder for the restore (--target).");
                                app.Restic(rset, one("key") ?? one("password")).Restore(one("point"), one("target"), one("filter"), rl, o.ContainsKey("overwrite"));
                                Console.WriteLine("restored (restic) to " + one("target"));
                                return 0;
                            }
                            var r = app.RestoreFor(session, one("set"), one("key") ?? one("password"));
                            var filter = one("filter");
                            r.Run(one("point"), one("target"), filter == null ? null : (Func<string, bool>)(p => p.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0), o.ContainsKey("overwrite"));
                            Console.WriteLine("restored=" + r.Restored + " failed=" + r.Failed + " skipped=" + r.Skipped);
                            return r.Failed == 0 ? 0 : 1;
                        }
                    case "restore-image":
                        {
                            var session = app.Interactive(one("password"), one("otp"));
                            var r = app.RestoreFor(session, one("set"), one("key") ?? one("password"));
                            Console.WriteLine(DiskImage.PrepareRestore(r, one("point"), one("target"), m => Console.WriteLine(m)));
                            return 0;
                        }
                    case "setup":
                        {
                            var dir = AppDomain.CurrentDomain.BaseDirectory;   // also right in a single-file Linux build
                            // SETUP-C10: on Windows, a Mac and a Linux desktop, setup without answers opens the installation wizard (--console: the questions here)
                            if (Setup.HasDesktop && !o.ContainsKey("login") && !o.ContainsKey("company") && !o.ContainsKey("console"))
                            {
#if NET40
                                // SETUP-C40 (owner): on Windows a real installation program (a window), not a web page
                                if (Environment.OSVersion.Platform == PlatformID.Win32NT && !o.ContainsKey("web")) return SetupForm.Run(dir);
#endif
                                return SetupUi.Run(dir);
                            }
                            Setup.Run(dir, one, k => o.ContainsKey(k), m => Console.WriteLine(m));
                            return 0;
                        }
                    case "uninstall":
                        {
                            // SETUP-C70: "Uninstall" in Programs and Features — the same setup window, "Remove" chosen
                            var dir = AppDomain.CurrentDomain.BaseDirectory;
                            if (Environment.OSVersion.Platform == PlatformID.Win32NT && !IsAdmin())
                            {
                                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName, string.Join(" ", args.Select(a => "\"" + a + "\"").ToArray())) { UseShellExecute = true, Verb = "runas" }); return 0; }
                                catch (Exception) { Console.Error.WriteLine("Administrator permission is needed to remove the software."); return 1; }
                            }
                            var conn = File.Exists(Path.Combine(dir, "connection.xml")) ? System.Xml.Linq.XElement.Load(Path.Combine(dir, "connection.xml")) : new System.Xml.Linq.XElement("CONNECTION");
                            var folder = (string)conn.Attribute("FOLDER"); if (string.IsNullOrEmpty(folder)) folder = "OnlineBackup";
#if NET40
                            if (!o.ContainsKey("quiet") && File.Exists(Path.Combine(dir, "connection.xml"))) return SetupForm.Run(dir, true);
#endif
                            var inst = Setup.Installed(folder);
                            var br = File.Exists(Path.Combine(dir, "branding.xml")) ? System.Xml.Linq.XElement.Load(Path.Combine(dir, "branding.xml")) : new System.Xml.Linq.XElement("BRANDING");
                            Setup.Uninstall(inst != null ? inst.InstallDir : dir, inst != null ? inst.DataDir : app.Home.Dir, (string)br.Attribute("PRODUCT") ?? folder, folder, o.ContainsKey("remove-settings"), m => Console.WriteLine(m));
                            return 0;
                        }
                    case "apply-update": return ClientUpdate.Install(one("from"), one("to"));   // UPD-020: started by the service, from a copy
                    case "client-screens":
                        {
#if NET40
                            ClientForm.Screens(one("out") ?? "screens", one("lang") ?? "en");
                            return 0;
#else
                            Console.WriteLine("Windows only"); return 1;
#endif
                        }
                    case "setup-screens":
                        {
#if NET40
                            SetupForm.Screens(one("package") ?? AppDomain.CurrentDomain.BaseDirectory, one("out") ?? "screens", one("lang"));
                            return 0;
#else
                            Console.WriteLine("Windows only"); return 1;
#endif
                        }
                    case "setup-ui":
                        {
                            // the screen robot and tests: the installation wizard on a package folder, without a browser (prints its address)
                            using (var ui = new SetupUi(one("package") ?? AppDomain.CurrentDomain.BaseDirectory))
                            {
                                foreach (var k in new[] { "install-dir", "data-dir" }) if (one(k) != null) ui.Fixed[k] = one(k);
                                ui.Flags.Add("no-service"); ui.Flags.Add("no-shortcut");
                                Console.WriteLine(ui.Url);
                                ui.Closed.WaitOne();
                            }
                            return 0;
                        }
                    case "open":
                        {
                            // the desktop shortcut: open the screen the service serves (ui.txt), else serve it here
                            if (File.Exists(ClientUi.UiFile))
                            {
                                var url = File.ReadAllText(ClientUi.UiFile).Trim();
                                try
                                {
                                    using (var t = new System.Net.Sockets.TcpClient()) { t.Connect("127.0.0.1", new Uri(url).Port); }
#if NET40
                                    // CLI-100 (owner): on Windows the customer's screen is a program window, not a web page
                                    if (Environment.OSVersion.Platform == PlatformID.Win32NT && !o.ContainsKey("web")) return ClientForm.Run(url, o.ContainsKey("tray"));
#endif
                                    Setup.OpenBrowser(url); return 0;
                                }
                                catch (Exception) { }
                            }
                            goto case "ui";
                        }
                    case "db-restore":
                        {
                            // a database from a restore point, loaded into a new (or the same) database
                            var session = app.Interactive(one("password"), one("otp"));
                            var ds = app.Sets().FirstOrDefault(x => x.Id == one("set"));
                            if (ds == null || (ds.Type != "MYSQL" && ds.Type != "POSTGRESQL")) throw new AgentException(404, "NO_SET", "not a database set");
                            var tmp = Path.Combine(app.Home.Dir, "temp", "db-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                            var name = (ds.Type == "MYSQL" ? "MySQL" : "PostgreSQL") + Path.DirectorySeparatorChar + M365Sync.Safe(one("db"), 120) + ".sql";
                            string file;
                            if (ds.Engine == "RESTIC")
                            {
                                var rr = app.Restic(ds, one("key") ?? one("password"));
                                var path = rr.Ls(one("point")).Select(f => f["path"]).FirstOrDefault(pth => pth.Replace('\\', '/').EndsWith("/" + name.Replace('\\', '/')));
                                if (path == null) throw new AgentException(404, "NO_DB", "the database is not in this point");
                                rr.RestoreMany(one("point"), tmp, new[] { path }, new List<string>());
                                file = Directory.GetFiles(tmp, "*.sql", SearchOption.AllDirectories).First();
                            }
                            else
                            {
                                var r = app.RestoreFor(session, ds.Id, one("key") ?? one("password"));
                                r.Run(one("point"), tmp, p => p.Replace('\\', '/').EndsWith(name.Replace('\\', '/')), true);
                                file = Directory.GetFiles(tmp, "*.sql", SearchOption.AllDirectories).First();
                            }
                            if (one("into") != null) { DbDump.Load(ds, app.Home.LoadSecret(ds.Id + "-sql"), file, one("into")); Console.WriteLine("loaded into " + one("into")); }
                            else Console.WriteLine(file);
                            return 0;
                        }
                    case "hyperv-restore":
                        {
                            var session = app.Interactive(one("password"), one("otp"));
                            var hs = app.Sets().FirstOrDefault(x => x.Id == one("set"));
                            if (hs == null || hs.Type != "HYPERV") throw new AgentException(404, "NO_SET", "not a Hyper-V set");
                            var target = one("target") ?? throw new AgentException(0, "TARGET", "--target is required");
                            var vm = M365Sync.Safe(one("vm"), 100);
                            if (hs.Engine == "RESTIC")
                            {
                                var rr = app.Restic(hs, one("key") ?? one("password"));
                                var paths = rr.Ls(one("point")).Select(f => f["path"]).Where(pth => pth.Replace('\\', '/').Contains("/" + vm + "/")).ToList();
                                if (paths.Count == 0) throw new AgentException(404, "NO_VM", "the VM is not in this point");
                                rr.RestoreMany(one("point"), target, paths, new List<string>(), true);   // as the native engine here
                            }
                            else app.RestoreFor(session, hs.Id, one("key") ?? one("password")).Run(one("point"), target, p => p.Contains("\\" + vm + "\\"), true);
                            var folder = Directory.GetDirectories(target, vm, SearchOption.AllDirectories).First();
                            Console.WriteLine(o.ContainsKey("import") ? "imported " + HyperV.Import(folder, one("import")) : folder);
                            return 0;
                        }
                    case "vmware-restore":
                    case "oracle-restore":
                        {
                            // VMW-040 / ORA-040: a VM's (an instance's) folder from a point; VMware: uploaded and registered as a new VM
                            var session = app.Interactive(one("password"), one("otp"));
                            bool vmw = args[0] == "vmware-restore";
                            var xs = app.Sets().FirstOrDefault(x => x.Id == one("set"));
                            if (xs == null || xs.Type != (vmw ? VMware.Type : Oracle.Type)) throw new AgentException(404, "NO_SET", vmw ? "not a VMware set" : "not an Oracle set");
                            var target = one("target") ?? Path.Combine(app.Home.Dir, "temp", "restore-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                            var what = M365Sync.Safe(one(vmw ? "vm" : "sid"), vmw ? 100 : 60);
                            if (xs.Engine == "RESTIC")
                            {
                                var rr = app.Restic(xs, one("key") ?? one("password"));
                                var paths = rr.Ls(one("point")).Select(f => f["path"]).Where(pth => pth.Replace('\\', '/').Contains("/" + what + "/")).ToList();
                                if (paths.Count == 0) throw new AgentException(404, "NOT_IN_POINT", what + " is not in this point");
                                rr.RestoreMany(one("point"), target, paths, new List<string>(), true);   // as the native engine here
                            }
                            else app.RestoreFor(session, xs.Id, one("key") ?? one("password")).Run(one("point"), target, p => p.Contains("\\" + what + "\\"), true);
                            var folder = Directory.GetDirectories(target, what, SearchOption.AllDirectories).First();
                            if (vmw && one("datastore") != null)
                            {
                                VMware.Restore(xs, app.Home.LoadSecret(xs.Id + "-sql"), folder, one("datastore"), one("name") ?? (one("vm") + "-restored-" + SystemClock.Now.ToString("yyyyMMdd-HHmm", System.Globalization.CultureInfo.InvariantCulture)), Console.WriteLine);
                                try { Directory.Delete(target, true); } catch (Exception) { }
                            }
                            else Console.WriteLine(folder + (vmw ? "" : "   (see RESTORE-README.txt)"));
                            return 0;
                        }
                    case "m365-secret":
                        // the application secret of the customer's tenant: kept on this computer only (DPAPI)
                        app.Home.SaveSecret(one("set") + "-m365", one("secret"));
                        Console.WriteLine("saved");
                        return 0;
                    case "m365-restore":
                        {
                            app.Interactive(one("password"), one("otp"));
                            var ms = app.Sets().FirstOrDefault(x => x.Id == one("set"));
                            if (ms == null || ms.Type != "M365") throw new AgentException(404, "NO_SET", "not a Microsoft 365 set");
                            var lines = new List<string>();
                            var r = app.Restic(ms, one("key") ?? one("password")).RestoreToM365(one("point"), o.ContainsKey("item") ? o["item"] : null, lines);
                            Console.WriteLine("mail=" + r.Mail + " files=" + r.Files + " failed=" + r.Failed);
                            return r.Failed == 0 ? 0 : 1;
                        }
                    case "ui":
                        {
                            // CLI-010: the customer's screen on this computer (the desktop shortcut runs "ui")
                            int port = one("port") == null ? 18200 : int.Parse(one("port"));
                            ClientUi ui = null;
                            for (int i = 0; i < 20 && ui == null; i++) try { ui = new ClientUi(app, port + i); } catch (System.Net.HttpListenerException) { }
                            if (ui == null) throw new AgentException(0, "PORT", "no free local port");
                            Console.WriteLine(ui.Url);
                            if (!o.ContainsKey("no-browser")) try { Setup.OpenBrowser(ui.Url); } catch (Exception) { }
                            var stopUi = new ManualResetEvent(false);
                            Console.CancelKeyPress += (s, e) => { e.Cancel = true; stopUi.Set(); };
                            stopUi.WaitOne(o.ContainsKey("minutes") ? TimeSpan.FromMinutes(double.Parse(one("minutes"))) : TimeSpan.FromHours(12));
                            ui.Dispose();
                            return 0;
                        }
                    case "install-service":
                        Console.WriteLine(ServiceSetup.Install(System.Reflection.Assembly.GetExecutingAssembly().Location, app.Home.Dir));
                        return 0;
                    case "uninstall-service":
                        Console.WriteLine(ServiceSetup.Uninstall());
                        return 0;
                    case "sql-password":
                        app.Home.SaveSecret(one("set") + "-sql", one("password"));
                        Console.WriteLine("saved");
                        return 0;
                    case "restore-test":
                        {
                            var t = app.RestoreTest(one("set"));
                            Console.WriteLine("checked=" + t["checked"] + " ok=" + t["ok"] + " failed=" + t["failed"]);
                            return t.Int("failed") == 0 ? 0 : 1;
                        }
                    case "service":
                        {
#if NET40
                            if (!Environment.UserInteractive) { System.ServiceProcess.ServiceBase.Run(new AgentService(app)); return 0; }
#endif
                            var stop = new CancellationTokenSource();
                            Console.CancelKeyPress += (s, e) => { e.Cancel = true; stop.Cancel(); };
                            ClientUi ui = null;
#if !NET40
                            // PKG-060 Linux: systemd stops the service with SIGTERM; the customer screen is served as on Windows
                            using var term = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, c => { c.Cancel = true; stop.Cancel(); });
                            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
                                try { ui = ClientUi.StartForService(app); Console.WriteLine("customer screen on 127.0.0.1:" + new Uri(ui.Url).Port + " (address with its key in ui.txt, root only)"); } catch (Exception e) { Console.WriteLine("customer screen not started: " + e.Message); }
#endif
                            try { app.ServiceLoop(stop.Token, m => Console.WriteLine(SystemClock.Now.ToString("s") + " " + m)); }
                            finally { if (ui != null) ui.Dispose(); }
                            return 0;
                        }
                }
            }
            catch (AgentException e) { Console.Error.WriteLine("error: " + e.Message); return 1; }
            Console.Error.WriteLine("unknown command " + args[0]);
            return 2;
        }

        static string DefaultHome()
        {
            return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OnlineBackup");
        }

        static Dictionary<string, List<string>> Parse(string[] args)
        {
            var d = new Dictionary<string, List<string>>();
            for (int i = 1; i < args.Length; i++)
                if (args[i].StartsWith("--"))
                {
                    var k = args[i].Substring(2);
                    if (!d.ContainsKey(k)) d[k] = new List<string>();
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) d[k].Add(args[++i]); else d[k].Add("1");
                }
            return d;
        }
    }
}
