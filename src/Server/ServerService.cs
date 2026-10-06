using System;
using OnlineBackup.Core;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace OnlineBackup.Server
{
    /// <summary>The Windows service of the server (automatic start, restart on failure).</summary>
    [SupportedOSPlatform("windows")]
    public sealed class ServerService : System.ServiceProcess.ServiceBase
    {
        public const string Name = "OnlineBackupServer";
        readonly string systemHome, prefix;
        Api api;

        public ServerService(string systemHome, string prefix) { this.systemHome = systemHome; this.prefix = prefix; ServiceName = Name; CanStop = true; CanShutdown = true; }

        protected override void OnStart(string[] args) { api = new Api(SystemConfig.Load(systemHome)); api.Start(prefix); }
        protected override void OnStop() { if (api != null) api.Dispose(); }
        protected override void OnShutdown() { OnStop(); }

        public static string Install(string exe, string systemHome, string prefix)
        {
            var bin = "\"" + exe + "\" run --system-home \"" + systemHome + "\" --prefix " + prefix;
            return Sc("create " + Name + " binPath= \"" + bin.Replace("\"", "\\\"") + "\" start= auto DisplayName= \"ITSguard Server Online\"")
                + Sc("failure " + Name + " reset= 86400 actions= restart/60000/restart/60000/restart/300000") + Sc("start " + Name);
        }

        public static string Uninstall() { return Sc("stop " + Name) + Sc("delete " + Name); }

        static string Sc(string args)
        {
            var psi = new ProcessStartInfo("sc.exe", args) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            var r = ProcessRunner.Run(psi, Limits.Install);   // static review "raw-process": no limit, stdout read before stderr (pipe deadlock)
            return r.Out + r.Err + (r.TimedOut ? psi.FileName + " did not finish in " + ProcessRunner.Describe(Limits.Install) + " and was stopped\n" : "");
        }
    }
}

namespace OnlineBackup.Server
{
    /// <summary>
    /// LIC-150: the product owner's licensing centre as a Windows service (automatic start, restart on failure).
    /// Only this service is created; nothing else on the computer (IIS, its sites and pools) is touched.
    /// The private key file is limited to Administrators and SYSTEM. HTTP is enough: every answer is signed with the
    /// owner's key and bound to the request's nonce, and a licence is no secret (it only works on its own server).
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class LicenseCenterService : System.ServiceProcess.ServiceBase
    {
        public const string Name = "OnlineBackupLicenseCenter";
        readonly string keyFile, dataDir, prefix;
        LicenseCenter center;

        public LicenseCenterService(string keyFile, string dataDir, string prefix) { this.keyFile = keyFile; this.dataDir = dataDir; this.prefix = prefix; ServiceName = Name; CanStop = true; CanShutdown = true; }

        protected override void OnStart(string[] args) { center = new LicenseCenter(System.IO.File.ReadAllText(keyFile).Trim(), dataDir, prefix); }
        protected override void OnStop() { if (center != null) center.Dispose(); }
        protected override void OnShutdown() { OnStop(); }

        /// <param name="certThumbprint">PORTAL-060: with an https:// prefix, a certificate already in the computer's store (e.g. the company's
        /// domain certificate) is bound to this port only — nothing else on the server (IIS sites, other ports) is touched.</param>
        public static string Install(string exe, string keyFile, string dataDir, string prefix, string certThumbprint = null)
        {
            if (!System.IO.File.Exists(keyFile)) throw new System.IO.FileNotFoundException("the private key file is missing: " + keyFile);
            int port = new Uri(prefix.Replace("+", "localhost").Replace("*", "localhost")).Port;
            var tls = "";
            if (prefix.StartsWith("https", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(certThumbprint)) throw new ArgumentException("an https prefix needs --cert-thumbprint (a certificate in LocalMachine\\My)");
                tls = Run("netsh.exe", "http add sslcert ipport=0.0.0.0:" + port + " certhash=" + certThumbprint.Replace(" ", "") + " appid={6f1d2f8e-5b3a-4c1e-9a7d-2f1b0c4e8a31}");
            }
            var bin = "\"" + exe + "\" license-center --key \"" + keyFile + "\" --data \"" + dataDir + "\" --prefix " + prefix;
            return tls + Run("icacls.exe", "\"" + keyFile + "\" /inheritance:r /grant:r *S-1-5-32-544:F *S-1-5-18:F")
                + Run("sc.exe", "create " + Name + " binPath= \"" + bin.Replace("\"", "\\\"") + "\" start= auto DisplayName= \"ITSguard Server Online Licensing Centre\"")
                + Run("sc.exe", "failure " + Name + " reset= 86400 actions= restart/60000/restart/60000/restart/300000")
                + Run("netsh.exe", "advfirewall firewall add rule name=\"ITSguard Server Online Licensing Centre\" dir=in action=allow protocol=TCP localport=" + port)
                + Run("sc.exe", "start " + Name);
        }

        public static string Uninstall()
        {
            return Run("sc.exe", "stop " + Name) + Run("sc.exe", "delete " + Name) + Run("netsh.exe", "advfirewall firewall delete rule name=\"ITSguard Server Online Licensing Centre\"");
        }

        static string Run(string file, string args)
        {
            var psi = new ProcessStartInfo(file, args) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            var r = ProcessRunner.Run(psi, Limits.Install);   // static review "raw-process": no limit, stdout read before stderr (pipe deadlock)
            return r.Out + r.Err + (r.TimedOut ? psi.FileName + " did not finish in " + ProcessRunner.Describe(Limits.Install) + " and was stopped\n" : "");
        }
    }
}
