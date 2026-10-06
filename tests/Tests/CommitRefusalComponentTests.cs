using System;
using System.IO;
using System.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// B-3 (second layer): a change the server refuses at commit (a delta whose earlier version was quarantined while the
    /// run was going) is not in the new point. The run must not be reported as a success — neither by the computer nor
    /// in the server's own record of the run — and the next backup must store the file (oracle: SHA-256 of the restore).
    /// </summary>
    public class CommitRefusalComponentTests
    {
        static byte[] Rnd(int n, int seed) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }
        static byte[] Change(byte[] cur, int at, int seed) { return cur.Take(at).Concat(Rnd(2048, seed)).Concat(cur.Skip(at + 512)).ToArray(); }
        static string Sha(byte[] b) { return Bytes.Hex(Bytes.Sha256(b)); }

        [Fact]
        public void ADeltaRefusedAtCommit_IsAnErrorOfTheRun_AndTheNextBackupStoresTheFile()
        {
            using (var env = new Env())
            {
                env.CreateUser("refuse1", "Customer-Pass-1");
                var app = env.Agent("refuse1", "Customer-Pass-1");
                var src = env.Dir("src");
                var path = Path.Combine(src, "ledger.mdb");
                var v1 = Rnd(8 * 1024 * 1024, 5); File.WriteAllBytes(path, v1);
                var created = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1",
                    new BackupSetInfo { Name = "R", Sources = { src }, MinDeltaFileSize = 1024 * 1024, Vss = false });
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(created.Id).Result);
                System.Threading.Thread.Sleep(1100);
                var v2 = Change(v1, 2 * 1024 * 1024, 6); File.WriteAllBytes(path, v2);
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(created.Id).Result);
                var userDir = Path.Combine(env.HomeA, "refuse1");
                var current = Path.Combine(userDir, "files", created.Id, SetStore.Current);
                var delta1 = Directory.GetFiles(current, "*.001", SearchOption.AllDirectories).Single();

                System.Threading.Thread.Sleep(1100);
                var v3 = Change(v2, 5 * 1024 * 1024, 7); File.WriteAllBytes(path, v3);
                var set = app.Sets().First(s => s.Id == created.Id);
                bool damaged = false;
                // after begin (the resend list is already read), before the file is sent: the delta of version 2 rots and is quarantined
                var run = new BackupRun(app.DeviceClient(), app.Home, set, app.Key(set))
                {
                    StopRequested = () =>
                    {
                        if (!damaged) { damaged = true; var b = File.ReadAllBytes(delta1); b[b.Length / 2] ^= 0xFF; File.WriteAllBytes(delta1, b); Assert.Equal(1, new SetStore(userDir, created.Id).VerifyAll().Int("bad")); }
                        return false;
                    }
                };
                var result = run.Run();
                Assert.True(damaged);
                var recorded = (string)env.Api.UserStore.LoadProfile("refuse1").FindSet(created.Id).Attribute("LAST_RESULT");
                Assert.True(result != "BS_STOP_SUCCESS", "the computer reported " + result + " although the server refused the change");
                Assert.True(recorded != "BS_STOP_SUCCESS", "the server recorded " + recorded + " although it refused the change");

                Assert.StartsWith("BS_STOP_SUCCESS", app.Backup(created.Id).Result);
                var target = env.Dir("restore");
                var rs = app.RestoreFor(app.Interactive("Customer-Pass-1", null), created.Id);
                rs.Run(null, target, null, false);
                Assert.Equal(Sha(v3), Sha(File.ReadAllBytes(Path.Combine(target, Env.Rel(src), "ledger.mdb"))));
            }
        }
    }
}
