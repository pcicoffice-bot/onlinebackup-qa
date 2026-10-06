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
    public class BareMetalTests
    {
        static void Script(string path, string body)
        {
            File.WriteAllText(path, "#!/bin/sh\n" + body);
            Process.Start("chmod", "+x \"" + path + "\"").WaitForExit();
        }

        static bool Same(string a, string b) { return File.ReadAllBytes(a).SequenceEqual(File.ReadAllBytes(b)); }

        [Fact]
        public void WholeComputerImageIsSentAsChangedBlocksOnly_AndRestoresInTheLayoutWindowsRecoveryReads()
        {
            if (Environment.OSVersion.Platform != PlatformID.Unix) return;
            using (var env = new Env())
            {
                // A stand-in for wbadmin: writes a 16MB "disk" into a new dated folder each run (like Windows does),
                // keeping the older folder; a "change" file makes it overwrite 64KB in the middle.
                var disk = Path.Combine(env.Root, "disk.bin");
                var b = new byte[16 * 1024 * 1024]; new Random(7).NextBytes(b); File.WriteAllBytes(disk, b);
                var fake = Path.Combine(env.Root, "wbadmin.sh");
                Script(fake,
                    "for a in \"$@\"; do case \"$a\" in -backupTarget:*) T=\"${a#-backupTarget:}\";; esac; done\n" +
                    "[ \"$1 $2\" = 'start backup' ] || exit 5\n" +
                    "echo \"$*\" | grep -q -- -allCritical || exit 6\n" +
                    "P=\"$T/WindowsImageBackup/PC1\"; D=\"$P/Backup $(date +%Y-%m-%d\\ %H%M%S)\"\n" +
                    "mkdir -p \"$D\" \"$P/Catalog\"\n" +
                    "cp '" + disk + "' \"$D/disk0.vhdx\"\n" +
                    "if [ -f '" + env.Root + "/change' ]; then head -c 65536 /dev/urandom | dd of=\"$D/disk0.vhdx\" bs=1 seek=8000000 conv=notrunc 2>/dev/null; fi\n" +
                    "date +%s%N > \"$P/Catalog/GlobalCatalog\"; echo media > \"$P/MediaId\"\n");
                Environment.SetEnvironmentVariable("OB_WBADMIN", fake);
                try
                {
                    env.CreateUser("bm2026", "Customer-Pass-1");
                    var app = env.Agent("bm2026", "Customer-Pass-1");
                    var stage = env.Dir("stage");
                    var s = new BackupSetInfo { Name = "Server", Type = DiskImage.Type, Sources = { stage }, MinDeltaFileSize = 1024 * 1024 };
                    var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", s);

                    var r1 = app.Backup(set.Id);
                    Assert.Equal("BS_STOP_SUCCESS", r1.Result);
                    Assert.Equal(4, r1.New);                                       // disk, catalog, MediaId, image.xml
                    Assert.True(r1.BytesSent > 15 * 1024 * 1024);
                    var firstFolder = Directory.GetDirectories(Path.Combine(stage, "WindowsImageBackup", "PC1"), "Backup *").Single();

                    Thread.Sleep(1100);
                    File.WriteAllText(Path.Combine(env.Root, "change"), "1");
                    var r2 = app.Backup(set.Id);
                    Assert.Equal("BS_STOP_SUCCESS", r2.Result);
                    Assert.Equal(0, r2.New); Assert.Equal(0, r2.Deleted);           // new dated folder → same files, one chain
                    Assert.True(r2.BytesSent < 6 * 1024 * 1024, "sent " + r2.BytesSent);
                    var latest = Directory.GetDirectories(Path.Combine(stage, "WindowsImageBackup", "PC1"), "Backup *").OrderBy(x => x, StringComparer.Ordinal).Last();
                    Assert.NotEqual(firstFolder, latest);

                    var restore = app.RestoreFor(app.Interactive("Customer-Pass-1", null), set.Id);
                    Assert.Contains(@"Bare Metal\WindowsImageBackup\PC1\Backup\disk0.vhdx", restore.Files(null).Select(f => f.Key));
                    var usb = env.Dir("usb");
                    var steps = DiskImage.PrepareRestore(restore, null, usb, m => { });
                    var img = Path.Combine(usb, "WindowsImageBackup", "PC1", Path.GetFileName(latest), "disk0.vhdx");
                    Assert.True(Same(Path.Combine(latest, "disk0.vhdx"), img), "latest image differs");
                    Assert.True(File.Exists(Path.Combine(usb, "WindowsImageBackup", "PC1", "MediaId")));
                    Assert.False(Directory.Exists(Path.Combine(usb, "Bare Metal")));
                    Assert.Contains("System Image Recovery", File.ReadAllText(Path.Combine(usb, "RESTORE-README.txt")));
                    Assert.Contains("System Image Recovery", steps);

                    // The earlier point gives the earlier disk, under its own folder name.
                    var first = restore.Points().OrderBy(p => p, StringComparer.Ordinal).First();
                    var old = env.Dir("usb-old");
                    DiskImage.PrepareRestore(app.RestoreFor(app.Interactive("Customer-Pass-1", null), set.Id), first, old, m => { });
                    Assert.True(Same(disk, Path.Combine(old, "WindowsImageBackup", "PC1", Path.GetFileName(firstFolder), "disk0.vhdx")), "first image differs");
                }
                finally { Environment.SetEnvironmentVariable("OB_WBADMIN", null); }
            }
        }

        [Fact]
        public void ImageToolFailureKeepsThePreviousImage_AndWindows2003UsesNtbackup()
        {
            if (Environment.OSVersion.Platform != PlatformID.Unix) return;
            using (var env = new Env())
            {
                var fake = Path.Combine(env.Root, "ntbackup.sh");
                Script(fake,
                    "[ -f '" + env.Root + "/fail' ] && { echo 'The operation failed' >&2; exit 3; }\n" +
                    "F=$(echo \"$*\" | sed -n 's/.*\\/F \\([^ ]*\\) .*/\\1/p')\n" +
                    "echo \"$*\" | grep -q '@' || exit 4\n" +
                    "head -c 2000000 /dev/zero | tr '\\\\0' 'B' > \"$F\"\n");
                Environment.SetEnvironmentVariable("OB_NTBACKUP", fake);
                try
                {
                    env.CreateUser("bm2003", "Customer-Pass-1");
                    var app = env.Agent("bm2003", "Customer-Pass-1");
                    var stage = env.Dir("stage");
                    var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1",
                        new BackupSetInfo { Name = "Old server", Type = DiskImage.Type, Sources = { stage } });
                    var r1 = app.Backup(set.Id);
                    Assert.True(r1.Result == "BS_STOP_SUCCESS", string.Join("\n", r1.LogLines));
                    Assert.Contains("SystemState", File.ReadAllText(Path.Combine(stage, "baremetal.bks"), System.Text.Encoding.Unicode));

                    Thread.Sleep(1100);
                    File.WriteAllText(Path.Combine(env.Root, "fail"), "1");
                    var r2 = app.Backup(set.Id);
                    Assert.NotEqual("BS_STOP_SUCCESS", r2.Result);                  // reported, never silent
                    Assert.Equal(0, r2.Deleted);                                    // and the last good image stays

                    var usb = env.Dir("usb");
                    var steps = DiskImage.PrepareRestore(app.RestoreFor(app.Interactive("Customer-Pass-1", null), set.Id), null, usb, m => { });
                    Assert.Equal(2000000, new FileInfo(Path.Combine(usb, "baremetal.bkf")).Length);
                    Assert.Contains("ntbackup", steps);
                }
                finally { Environment.SetEnvironmentVariable("OB_NTBACKUP", null); }
            }
        }
    }
}
