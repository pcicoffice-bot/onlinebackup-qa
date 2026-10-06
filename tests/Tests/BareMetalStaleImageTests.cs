using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// AP-02 (Agent B note, proven here with a stand-in for wbadmin): the bare-metal backup took the newest "Backup *"
    /// folder on the staging volume without checking that THIS run wrote it. A wbadmin that ended with 0 but wrote nothing
    /// (a full staging disk, a policy, a tool that answered and did nothing) made yesterday's image this run's — a success.
    /// </summary>
    [Collection("environment variables")]
    public class BareMetalStaleImageTests
    {
        static void Script(string path, string body) { File.WriteAllText(path, "#!/bin/sh\n" + body); Process.Start("chmod", "+x \"" + path + "\"").WaitForExit(); }

        [Fact]
        public void AWbadminThatWritesNothing_IsNotASuccessWithTheOldImage()
        {
            if (Environment.OSVersion.Platform != PlatformID.Unix) return;
            using (var env = new Env())
            {
                var writes = Path.Combine(env.Root, "writes");
                File.WriteAllText(writes, "1");
                var fake = Path.Combine(env.Root, "wbadmin.sh");
                Script(fake,
                    "for a in \"$@\"; do case \"$a\" in -backupTarget:*) T=\"${a#-backupTarget:}\";; esac; done\n" +
                    "[ -f '" + writes + "' ] || exit 0\n" +                                   // the second time: exit 0, nothing written
                    "P=\"$T/WindowsImageBackup/PC1\"; D=\"$P/Backup $(date +%Y-%m-%d\\ %H%M%S)\"\n" +
                    "mkdir -p \"$D\"; head -c 1048576 /dev/urandom > \"$D/disk0.vhdx\"\n");
                Environment.SetEnvironmentVariable("OB_WBADMIN", fake);
                try
                {
                    env.CreateUser("bmstale", "Customer-Pass-1");
                    var app = env.Agent("bmstale", "Customer-Pass-1");
                    var stage = env.Dir("stage");
                    var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "Server", Type = DiskImage.Type, Sources = { stage } });
                    Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);                      // control: a real image
                    Thread.Sleep(2100);
                    File.Delete(writes);
                    var r = app.Backup(set.Id);
                    Assert.True(!r.Result.StartsWith("BS_STOP_SUCCESS", StringComparison.Ordinal) || r.Result == "BS_STOP_SUCCESS_WITH_ERROR",
                        "wbadmin wrote no image, and the run ended " + r.Result + " with the old one:\n" + string.Join("\n", r.LogLines.Where(l => l.Contains("Bare") || l.Contains("err") || l.Contains("warn"))));
                }
                finally { Environment.SetEnvironmentVariable("OB_WBADMIN", null); }
            }
        }
    }
}
