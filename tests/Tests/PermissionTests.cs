using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// Fault injection "permission denied" (BK-05), with the real agent program as an ordinary user — root reads every
    /// file, so as root the test makes a user and runs the agent as that user; as an ordinary user (CI) it runs directly.
    ///   input    a backed-up tree; then a subfolder the agent's user may not read, and a changed readable file
    ///   expected the run is "completed with errors" (never a plain success, never a failure of the whole run); the log names
    ///            the folder; the server shows the run as a problem; the folder's files are NOT treated as deleted — the
    ///            newest point still restores them identical (SHA-256), and the changed file in its new version
    /// </summary>
    public class PermissionTests
    {
        static bool IsRoot { get { return Environment.UserName == "root"; } }
        static string Sha(string f) { using (var s = File.OpenRead(f)) using (var h = SHA256.Create()) return Convert.ToHexString(h.ComputeHash(s)); }

        static int Sh(string file, params string[] args)
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            var r = ProcessRunner.Run(psi, TimeSpan.FromMinutes(5));
            return r.Code;
        }

        /// <summary>Runs "OnlineBackup.Agent backup" as the user obqa, from a copy of the program everybody may read.</summary>
        static string BackupAsOrdinaryUser(string home, string setId, out int code)
        {
            if (Sh("id", "obqa") != 0) Assert.Equal(0, Sh("useradd", "-M", "-s", "/usr/sbin/nologin", "obqa"));
            var bin = Path.Combine(Path.GetTempPath(), "obagent-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(bin);
            foreach (var f in Directory.GetFiles(AppContext.BaseDirectory)) File.Copy(f, Path.Combine(bin, Path.GetFileName(f)));
            Assert.Equal(0, Sh("chmod", "-R", "a+rX", bin));
            Assert.Equal(0, Sh("chown", "-R", "obqa", home));
            var psi = new ProcessStartInfo("setpriv") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "--reuid", "obqa", "--regid", "obqa", "--clear-groups", "dotnet", "exec", "--runtimeconfig", Path.Combine(bin, "OnlineBackup.Agent.runtimeconfig.json"),
                Path.Combine(bin, "OnlineBackup.Agent.dll"), "backup", "--home", home, "--set", setId }) psi.ArgumentList.Add(a);
            psi.Environment["HOME"] = bin; psi.Environment["DOTNET_CLI_HOME"] = bin;
            var r = ProcessRunner.Run(psi, TimeSpan.FromMinutes(5));
            code = r.Code;
            Sh("chown", "-R", "root", home);
            return r.Out + r.Err;
        }

        [Fact]
        public void AFolderTheAgentMayNotRead_IsAnError_NotADeletion_AndEverythingElseRestoresIdentical()
        {
            if (Environment.OSVersion.Platform != PlatformID.Unix) throw NotTested.Because("needs Linux file permissions (chmod)");
            using (var env = new Env())
            {
                var src = env.Dir("src");
                var secret = Path.Combine(src, "secret"); Directory.CreateDirectory(secret);
                File.WriteAllText(Path.Combine(src, "open.txt"), "open v1");
                File.WriteAllText(Path.Combine(secret, "payroll.xlsx"), "payroll data");
                var big = new byte[400000]; new Random(9).NextBytes(big); File.WriteAllBytes(Path.Combine(secret, "big.bin"), big);
                var want = Directory.GetFiles(src, "*", SearchOption.AllDirectories).ToDictionary(f => Path.GetRelativePath(src, f), Sha);
                env.CreateUser("perm", "Customer-Pass-1");
                var app = env.Agent("perm", "Customer-Pass-1");
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "Perm", Sources = { src } });
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);

                System.Threading.Thread.Sleep(1100);
                File.WriteAllText(Path.Combine(src, "open.txt"), "open v2"); want["open.txt"] = Sha(Path.Combine(src, "open.txt"));
                Assert.Equal(0, Sh("chmod", "000", secret));
                try
                {
                    string result, output;
                    if (IsRoot)
                    {
                        Assert.Equal(0, Sh("chmod", "a+rX", env.Root)); Assert.Equal(0, Sh("chmod", "a+rX", src));
                        int code; output = BackupAsOrdinaryUser(app.Home.Dir, set.Id, out code);
                        result = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("BS_STOP_")) ?? "(no result) " + output;
                        result = result.Split(' ')[0];
                    }
                    else { var r = app.Backup(set.Id); result = r.Result; output = string.Join("\n", r.LogLines); }

                    Assert.True(result == "BS_STOP_SUCCESS_WITH_ERROR", "result " + result + "\n" + output);
                    if (IsRoot) Assert.True(output.IndexOf("denied", StringComparison.OrdinalIgnoreCase) >= 0, output);   // really refused by the system, as the user obqa
                    Assert.Contains("secret", output);
                    var run = env.Api.Runs.Since(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1)).Where(m => m["set"] == set.Id).OrderBy(m => m["job"]).Last();
                    Assert.NotEqual("ok", run["status"]);
                }
                finally { Sh("chmod", "755", secret); }

                var target = env.Dir("restore");
                app.RestoreFor(app.Interactive("Customer-Pass-1", null), set.Id).Run(null, target, null, false);
                var root = Path.Combine(target, Env.Rel(src));
                var got = Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(f => Path.GetRelativePath(root, f), Sha);
                Assert.Equal(want.OrderBy(x => x.Key), got.OrderBy(x => x.Key));
            }
        }
    }
}
