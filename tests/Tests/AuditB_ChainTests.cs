using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// QA agent B (code audit) — delta chains (BK-02), local state lost / behind (AG-06), damaged object (ST-04).
    /// Real server (Env) + real agent; the oracle is the SHA of the bytes on the customer's disk vs the bytes restored.
    /// </summary>
    public class AuditB_ChainTests
    {
        static byte[] Rnd(int n, int seed) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }
        static byte[] Change(byte[] cur, int at, int seed) { return cur.Take(at).Concat(Rnd(2048, seed)).Concat(cur.Skip(at + 512)).ToArray(); }
        static string Sha(byte[] b) { return Bytes.Hex(Bytes.Sha256(b)); }

        static void CopyDir(string from, string to)
        {
            if (Directory.Exists(to)) Directory.Delete(to, true);
            Directory.CreateDirectory(to);
            foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                var d = Path.Combine(to, f.Substring(from.Length + 1));
                Directory.CreateDirectory(Path.GetDirectoryName(d));
                File.Copy(f, d, true);
            }
        }

        /// <summary>
        /// The agent's local index is written only AFTER the server confirmed the commit (BackupRun.Run: commit → state.Save).
        /// A power cut / kill / crash between the two (or a local state folder put back from an older copy — AG-06) leaves
        /// the index one run behind the server. The next changed large file is sent as delta seq N+1 that the server already
        /// has; SetStore.PrepareCommit refuses it ("delta without base" → resend) WITHOUT telling the agent: the run ends
        /// BS_STOP_SUCCESS, the log says "upd", but the new version is not in the restore point. If the customer's file is
        /// lost before the next run, that version is gone although the report said it was backed up.
        /// </summary>
        [Fact]
        public void LocalIndexOneRunBehind_TheNextChangedVersion_IsStored_OrTheRunSaysItWasNot()
        {
            using (var env = new Env())
            {
                env.CreateUser("auditb1", "Customer-Pass-1");
                var app = env.Agent("auditb1", "Customer-Pass-1");
                var src = env.Dir("src");
                var path = Path.Combine(src, "ledger.mdb");
                var v1 = Rnd(24 * 1024 * 1024, 21);
                File.WriteAllBytes(path, v1);
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1",
                    new BackupSetInfo { Name = "Chain", Sources = { src }, MinDeltaFileSize = 1024 * 1024 });
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);
                var setDir = app.Home.SetDir(set.Id);
                var saved = Path.Combine(env.Root, "state-after-run1");
                CopyDir(setDir, saved);                                  // the local index as it was after run 1

                System.Threading.Thread.Sleep(1100);
                var v2 = Change(v1, 4 * 1024 * 1024, 2);
                File.WriteAllBytes(path, v2);
                var r2 = app.Backup(set.Id);
                Assert.Equal("BS_STOP_SUCCESS", r2.Result);
                Assert.Equal(1, r2.Updated);
                var current = Path.Combine(env.HomeA, "auditb1", "files", set.Id, SetStore.Current);
                Assert.Single(Directory.GetFiles(current, "*.001", SearchOption.AllDirectories));   // run 2 really sent a delta

                // the agent died after the server committed run 2 but before it saved its index
                CopyDir(saved, setDir);

                System.Threading.Thread.Sleep(1100);
                var v3 = Change(v2, 12 * 1024 * 1024, 3);
                File.WriteAllBytes(path, v3);
                var r3 = app.Backup(set.Id);

                var target = env.Dir("restore");
                var rs = app.RestoreFor(app.Interactive("Customer-Pass-1", null), set.Id);
                rs.Run(null, target, null, false);
                var restored = File.ReadAllBytes(Path.Combine(target, Env.Rel(src), "ledger.mdb"));
                bool stored = Sha(restored) == Sha(v3);
                bool runSaidSo = r3.Result != "BS_STOP_SUCCESS";
                Assert.True(stored || runSaidSo,
                    "run 3 ended " + r3.Result + " (updated=" + r3.Updated + "), but the latest restore point holds "
                    + (Sha(restored) == Sha(v2) ? "version 2" : Sha(restored) == Sha(v1) ? "version 1" : "unknown bytes") + ", not version 3");
            }
        }

        /// <summary>
        /// Verify (ST-04) quarantines a damaged object and deletes it from the index. When that object is the LAST delta of
        /// an incremental chain, SetStore.FilesAt still lists the file (it only checks that the chain starts with a full copy)
        /// with the chain cut one short: the restore of the latest point writes the PREVIOUS version and reports
        /// RESTORE_STOP_SUCCESS. The customer believes he got the latest data.
        /// </summary>
        [Theory]
        [InlineData("002")]   // the last delta (version 3) is damaged → the restore wrote version 2 as "success"
        [InlineData("000")]   // the full copy is damaged → FilesAt drops the whole file from every point, the restore says "success" without it
        public void DamagedLastDeltaQuarantined_RestoreOfThatPoint_NeverSilentlyGivesAnOlderVersion(string damaged)
        {
            using (var env = new Env())
            {
                env.CreateUser("auditb2", "Customer-Pass-1");
                var app = env.Agent("auditb2", "Customer-Pass-1");
                var src = env.Dir("src");
                var path = Path.Combine(src, "ledger.mdb");
                var v1 = Rnd(24 * 1024 * 1024, 31);
                File.WriteAllBytes(path, v1);
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1",
                    new BackupSetInfo { Name = "Chain", Sources = { src }, MinDeltaFileSize = 1024 * 1024 });
                app.Backup(set.Id);
                var versions = new List<byte[]> { v1 };
                for (int v = 2; v <= 3; v++)
                {
                    System.Threading.Thread.Sleep(1100);
                    var next = Change(versions.Last(), v * 4 * 1024 * 1024, v);
                    File.WriteAllBytes(path, next); versions.Add(next);
                    Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);
                }
                var userDir = Path.Combine(env.HomeA, "auditb2");
                var current = Path.Combine(userDir, "files", set.Id, SetStore.Current);
                var last = Directory.GetFiles(current, "*." + damaged, SearchOption.AllDirectories).Single();   // .002 = the delta of version 3, .000 = the full copy
                var bytes = File.ReadAllBytes(last); bytes[bytes.Length / 2] ^= 0xFF; File.WriteAllBytes(last, bytes);   // bit rot
                var verify = new SetStore(userDir, set.Id).VerifyAll();
                Assert.Equal(1, verify.Int("bad"));

                var target = env.Dir("restore");
                var rs = app.RestoreFor(app.Interactive("Customer-Pass-1", null), set.Id);
                rs.Run(null, target, null, false);
                var f = Path.Combine(target, Env.Rel(src), "ledger.mdb");
                var got = File.Exists(f) ? File.ReadAllBytes(f) : null;
                bool latest = got != null && Sha(got) == Sha(versions[2]);
                bool said = rs.Result != "RESTORE_STOP_SUCCESS" || rs.Failed > 0;
                Assert.True(latest || said,
                    "restore of the latest point ended " + rs.Result + " (restored=" + rs.Restored + ", failed=" + rs.Failed + ") and wrote "
                    + (got == null ? "nothing" : Sha(got) == Sha(versions[1]) ? "version 2 (older) without any warning" : Sha(got) == Sha(versions[0]) ? "version 1" : "unknown bytes"));
            }
        }
    }
}
