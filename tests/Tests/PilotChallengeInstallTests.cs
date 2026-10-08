using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// An independent challenge of the pilot tests of install / reboot / state (PilotInstall*, PilotReboot*, PilotState*):
    /// where their oracle could pass while the product is wrong, a stronger test of the same contract (tests/QA/specs.py).
    ///   IN-04 "update the server only with a signed package": the update from files (UPD-030) was tested only with
    ///         packages that are not signed at all and was expected to install them — a package the vendor never signed
    ///         must be refused there too (component: Updater.FromUpload)
    ///   IN-05 the one-file Setup.exe: the earlier test changed 39 sampled bytes and compared only the program files —
    ///         the connection (this server's address and pin) and the branding were never compared; here every byte of
    ///         the end of the payload is changed in turn and every file must be refused or identical
    ///   SH-01 the history of every run: a cut line was tested only as the LAST line of a day; the next run written
    ///         after it must still show with its own fields; "kept 400 days" was tested only at the day's first second —
    ///         a run younger than 400 days must still be there
    ///   SH-07 after the licence's grace the earlier test only asked Users.CheckDevice, not a backup — here an existing
    ///         computer beyond the basic edition's limit really backs up through the real server and restores identical
    /// </summary>
    public class PilotChallengeInstallTests : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "obpilot-chal-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        public PilotChallengeInstallTests() { Directory.CreateDirectory(root); }
        public void Dispose() { try { Directory.Delete(root, true); } catch (Exception) { } }

        const string Pw = "Customer-Pass-1";
        static string Sha(byte[] b) { return Bytes.Hex(Bytes.Sha256(b)); }
        static string ShaFile(string f) { return Sha(File.ReadAllBytes(f)); }
        static byte[] Rnd(int n, int seed) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }
        static Dictionary<string, string> Tree(string dir)
        {
            return Directory.Exists(dir) ? Directory.GetFiles(dir, "*", SearchOption.AllDirectories).ToDictionary(f => Path.GetRelativePath(dir, f).Replace('\\', '/'), ShaFile) : new Dictionary<string, string>();
        }
        string Dir(string name) { var d = Path.Combine(root, name); Directory.CreateDirectory(d); return d; }

        // ================================================================== IN-05

        /// <summary>
        /// IN-05 integrity (challenge of IN05_OneChangedByteAnywhereInTheSetupPayload_IsRefused_NeverReadAsDifferentFiles):
        /// every single byte of the last 4 KB of the files appended to Setup.exe — where this server's connection (address,
        /// pin), the branding, the version and the end of the gzip stream are — changed in turn: each damaged Setup.exe is
        /// refused when read, or EVERY file read from it (connection.xml and branding.xml included) is byte-identical.
        /// </summary>
        [Fact]
        public void IN05_EveryByteOfTheSetupPayloadsEnd_Changed_IsRefusedOrEveryFileIdentical_TheConnectionIncluded()
        {
            var dir = Dir("client");
            File.WriteAllBytes(Path.Combine(dir, "OnlineBackup.Agent.exe"), Rnd(80000, 111));
            File.WriteAllBytes(Path.Combine(dir, "OnlineBackup.Core.dll"), Rnd(50000, 112));
            File.WriteAllBytes(Path.Combine(dir, "restic.exe"), Rnd(90000, 114));
            File.WriteAllText(Path.Combine(dir, "version.txt"), "2.0.0");
            var stub = new byte[4096]; stub[0] = (byte)'M'; stub[1] = (byte)'Z'; Array.Copy(Rnd(4000, 116), 0, stub, 96, 4000);
            File.WriteAllBytes(Path.Combine(dir, "Setup.exe"), stub);
            var brand = new Dictionary<string, string> { { "PRODUCT", "Pilot Safe Backup" }, { "COMPANY", "Pilot IT" }, { "PHONE", "03-5550000" }, { "EMAIL", "support@pilot.example" }, { "LANGUAGE", "en" } };
            var exe = ClientPackage.BuildExe(ClientPackage.FromBrand(brand, true, "https://backup.pilot.example:8443", "AB:CD:EF:01", dir));
            var goodFiles = ClientPackage.ReadExe(exe);
            var good = goodFiles.ToDictionary(kv => kv.Key, kv => Sha(kv.Value));
            Assert.Contains("connection.xml", good.Keys); Assert.Contains("branding.xml", good.Keys);
            long payloadEnd = exe.Length - 16, from = Math.Max(stub.Length, payloadEnd - 4096);
            var silent = new List<string>(); string example = null;
            for (long at = from; at < payloadEnd; at++)
            {
                var bad = (byte[])exe.Clone(); bad[at] ^= 0x01;
                Dictionary<string, byte[]> got;
                try { got = ClientPackage.ReadExe(bad); } catch (Exception) { continue; }   // refused: right
                var diff = good.Keys.Where(n => !got.ContainsKey(n) || Sha(got[n]) != good[n]).Concat(got.Keys.Where(n => !good.ContainsKey(n))).ToList();
                if (diff.Count > 0) silent.Add(at + ":" + string.Join("/", diff));
                if (diff.Count > 0 && example == null) example = diff[0] + " read as\n" + Encoding.UTF8.GetString(got[diff[0]]) + "\ninstead of\n" + Encoding.UTF8.GetString(goodFiles[diff[0]]);
            }
            Assert.True(silent.Count == 0, silent.Count + " changed bytes gave different files without an error: " + string.Join(", ", silent.Take(20)) + "\nfor example " + example);
        }

        // ================================================================== SH-07

        /// <summary>
        /// SH-07 boundary (challenge of SH07_AfterTheGrace_TheBasicEditionKeepsEveryExistingComputerBackingUp...): through
        /// the real server. 12 computers registered under a PRO licence bound to a licensing centre that stops answering;
        /// 8 days later the server runs the basic edition (10 computers). The 12th computer — beyond the basic limit —
        /// really backs up (a changed and a new file), the point restores identical (SHA-256); only a 13th, new computer is
        /// refused, with the licence as the reason.
        /// </summary>
        [Fact]
        public void SH07_AfterTheGrace_AnExistingComputerBeyondTheBasicLimit_ReallyBacksUp_AndRestoresIdentical_ANewOneIsRefused()
        {
            using (var env = new Env())
            {
                env.SetLicense("PRO", 50, 2000, "http://localhost:9/");
                env.CreateUser("grace2026", Pw);
                var apps = Enumerable.Range(1, 12).Select(i => env.Agent("grace2026", Pw, null, "pc" + i)).ToList();
                var app = apps.Last();
                var src = Path.Combine(root, "src"); Directory.CreateDirectory(src);
                File.WriteAllBytes(Path.Combine(src, "ledger.xlsx"), Rnd(120000, 1)); File.WriteAllText(Path.Combine(src, "a.txt"), "one");
                var set = app.CreateSet(app.Interactive(Pw, null), Pw, new BackupSetInfo { Name = "Files", Sources = { src } });
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);

                var el = env.Cfg.Doc.Root.Element("LICENSE");
                el.SetAttributeValue("ONLINE_STATUS", "UNREACHABLE");
                el.SetAttributeValue("TEMP_SINCE", DateTime.UtcNow.AddDays(-8).ToString("o", CultureInfo.InvariantCulture));
                env.Cfg.Save(); env.Cfg.ResetLicense();
                Assert.Equal("FREE", env.Cfg.License.Edition);
                Assert.Equal(10, env.Cfg.License.MaxDevices);

                File.WriteAllText(Path.Combine(src, "a.txt"), "two, after the grace"); File.SetLastWriteTimeUtc(Path.Combine(src, "a.txt"), DateTime.UtcNow.AddMinutes(1));
                File.WriteAllBytes(Path.Combine(src, "new.pdf"), Rnd(50000, 2));
                System.Threading.Thread.Sleep(1100);
                var r = app.Backup(set.Id);
                Assert.True(r.Result == "BS_STOP_SUCCESS", r.Result + "\n" + string.Join("\n", r.LogLines));
                var target = Path.Combine(root, "restore");
                var rs = app.RestoreFor(app.Interactive(Pw, null), set.Id); rs.Run(null, target, null, false);
                Assert.Equal(0, rs.Failed);
                Assert.Equal(Tree(src).OrderBy(k => k.Key), Tree(Path.Combine(target, Env.Rel(src))).OrderBy(k => k.Key));

                var e = Assert.Throws<AgentException>(() => env.Agent("grace2026", Pw, null, "pc13"));
                Assert.Contains("licen", e.Message, StringComparison.OrdinalIgnoreCase);
            }
        }

        // ================================================================== IN-04

        /// <summary>
        /// IN-04 failure (challenge of IN04_UpdateFromFiles_ExactlyAtTheLimitTaken_OneByteOverOrAPartMissingOrNoProgram_Refused,
        /// which installed packages nobody signed and expected that): the contract is "update the server only with a signed
        /// package; a changed package is refused". The administrator's update from files (UPD-030) is given the vendor's
        /// genuine package with one byte of the server program changed (a package the vendor never signed: a tampered
        /// download, a planted file) — it must be refused and nothing handed to an installer.
        /// </summary>
        [Fact]
        public void IN04_UpdateFromFiles_APackageTheVendorNeverSigned_IsRefused_NothingInstalled()
        {
            var cfg = SystemConfig.Init(Path.Combine(root, "system"), "admin", "Admin-Pass-1", "localhost", new[] { Path.Combine(root, "users") + "|UNLIMITED|100" });
            var program = Rnd(120000, 201); program[60000] ^= 0x01;                              // not the vendor's bytes
            byte[] zip;
            using (var ms = new MemoryStream())
            {
                using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
                {
                    using (var s = z.CreateEntry("server/" + (OperatingSystem.IsWindows() ? "OnlineBackup.Server.exe" : "OnlineBackup.Server")).Open()) s.Write(program, 0, program.Length);
                    using (var s = z.CreateEntry("server/version.txt").Open()) { var v = Encoding.ASCII.GetBytes("99.4.0"); s.Write(v, 0, v.Length); }
                }
                zip = ms.ToArray();
            }
            var launched = new List<string>(); var launch = Updater.Launch;
            Updater.Reset(); Updater.Launch = (exe, log) => { lock (launched) launched.Add(exe); };
            try
            {
                string version = null; Exception refused = null;
                try { version = Updater.FromUpload(cfg, new MemoryStream(zip), zip.Length, "admin", "127.0.0.1"); } catch (Exception e) { refused = e; }
                Assert.True(refused != null && launched.Count == 0, "a package without the vendor's signature was installed from files: version " + version + ", handed to " + string.Join(", ", launched));
            }
            finally { Updater.Launch = launch; Updater.Reset(); }
        }

        // ================================================================== SH-01

        static SystemConfig HistoryCfg(string rootDir) { return SystemConfig.Init(Path.Combine(rootDir, "system"), "admin", "Admin-Pass-1", "localhost", new[] { Path.Combine(rootDir, "home") + "|UNLIMITED|100" }); }

        /// <summary>
        /// SH-01 recovery (challenge of SH01_EveryRunIsInTheHistory_..._ACutLineSkipped_400DaysKept, whose cut line was the
        /// last line of the day): the power fails while a run's line is written (the line is cut, no line end); the server
        /// starts again and the next run ends. The run before the cut and the run after it are both in the history, each
        /// with its own job, customer, set and result — the line after the cut is not glued to the cut one.
        /// </summary>
        [Fact]
        public void SH01_TheRunWrittenAfterALineCutByAPowerFailure_IsInTheHistoryWithItsOwnFields()
        {
            var cfg = HistoryCfg(root);
            var set = new BackupSetInfo { Id = "1700000000078", Name = "Office", Computer = "PC-1" };
            var t = new DateTime(2026, 10, 6, 9, 0, 0, DateTimeKind.Utc);
            new RunLog(cfg).Add(t, "acme2026", set, "Backup", "2026-10-06-09-00-00", new Msg().Set("result", "BS_STOP_SUCCESS").Set("new", 3), null);
            File.AppendAllText(Path.Combine(cfg.SystemHome, "runs", "20261006.log"), RunId.UnixMs(t.AddMinutes(5)) + "\tacme2026\t1700000000078\tOff");   // the power fails here
            var after = new RunLog(SystemConfig.Load(cfg.SystemHome));                                                                                      // the server starts again
            after.Add(t.AddMinutes(30), "acme2026", set, "Backup", "2026-10-06-09-30-00", new Msg().Set("result", "BS_STOP_BY_SYSTEM_ERROR"), null);

            var rows = after.Since(t.AddHours(-1), t.AddHours(1));
            var all = string.Join(" | ", rows.Select(r => r["job"] + "/" + r["login"] + "/" + r["set"] + "/" + r["result"]));
            Assert.True(rows.Any(r => r["job"] == "2026-10-06-09-30-00"), "the run after the cut line is missing from the history: " + all);
            var next = rows.Single(r => r["job"] == "2026-10-06-09-30-00");
            Assert.Equal("acme2026", next["login"]); Assert.Equal("1700000000078", next["set"]); Assert.Equal("BS_STOP_BY_SYSTEM_ERROR", next["result"]); Assert.Equal("bad", next["status"]);
            Assert.Equal("BS_STOP_SUCCESS", rows.Single(r => r["job"] == "2026-10-06-09-00-00")["result"]);
            Assert.All(rows, r => Assert.Matches("^\\d{4}(-\\d{2}){5}$", r["job"] ?? ""));                                                                // no glued row
        }

        /// <summary>
        /// SH-01 boundary (challenge of the same test, whose "400 days kept" was checked only at the first second of the
        /// day): the run log is "kept 400 days" (RunLog). A run written at 23:50 is still in the history 399 days and
        /// 23 hours later, and gone after 400 days.
        /// </summary>
        [Fact]
        public void SH01_ARunYoungerThan400Days_IsStillInTheHistory()
        {
            var cfg = HistoryCfg(root);
            var set = new BackupSetInfo { Id = "1700000000079", Name = "Office", Computer = "PC-1" };
            var t = new DateTime(2026, 10, 5, 23, 50, 0, DateTimeKind.Utc);
            var log = new RunLog(cfg);
            log.Add(t, "acme2026", set, "Backup", "2026-10-05-23-50-00", new Msg().Set("result", "BS_STOP_SUCCESS"), null);
            var now = t.AddDays(400).AddHours(-1);                                                                        // 399 days 23 hours later
            log.Purge(now);
            Assert.True(log.Since(t.AddMinutes(-1), now).Any(r => r["job"] == "2026-10-05-23-50-00"), "a run 399 days and 23 hours old was removed from the history (kept 400 days)");
            log.Purge(t.AddDays(400).AddMinutes(1));
            Assert.DoesNotContain(log.Since(t.AddMinutes(-1), t.AddDays(401)), r => r["job"] == "2026-10-05-23-50-00");
        }
    }
}
