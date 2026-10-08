using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// BK-05 "Unreadable data is an error" (bug 2): anything that should have been backed up but could not be read is an
    /// ERROR that names it - never a silent success, never a mere warning, never a deletion - while everything readable is
    /// still backed up and restores byte-identical; once the cause is gone the next run backs it up and is a clean success.
    /// Integration: the real server (Env) and the agent's own code (AgentApp / BackupRun) over HTTP.
    /// End to end: the SHIPPED .NET 4.0 agent's command line natively on Windows against the real server.
    /// Oracles: the run's result and its err lines, the server's history (RunLog) and its job-log file, the admin site's
    /// "last result", and SHA-256 of what a restore brings back against the files on disk.
    /// Denial of read: Windows - an explicit "deny read data" entry for the current user (icacls); Linux - mode 000 as a
    /// non-root user. Root on Linux reads everything: NOT TESTED there. Every denial is checked to really deny before the run.
    /// </summary>
    public class BK05UnreadableTests
    {
        const string Pw = "Customer-Pass-1";

        // ------------------------------------------------------------------ helpers

        static string Icacls(string args)
        {
            var p = Process.Start(new ProcessStartInfo("icacls", args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd(); p.WaitForExit();
            if (p.ExitCode != 0) throw new InvalidOperationException("icacls " + args + " failed (" + p.ExitCode + "): " + o);
            return o;
        }
        static string Me() { return System.Security.Principal.WindowsIdentity.GetCurrent().User.Value; }

        /// <summary>Takes away the right to read a file's data (or list a folder) from this process; checks it took effect.</summary>
        static void Deny(string path)
        {
            bool dir = Directory.Exists(path);
            if (OperatingSystem.IsWindows()) Icacls("\"" + path + "\" /deny *" + Me() + ":(RD)");
            else if (Environment.UserName == "root") throw NotTested.Because("root reads every file on Linux (no denial possible for this process); runs on Windows (ACL) and as a non-root Linux user");
            else File.SetUnixFileMode(path, UnixFileMode.None);
            try { if (dir) Directory.GetFileSystemEntries(path); else File.ReadAllBytes(path); }
            catch (UnauthorizedAccessException) { return; }
            catch (IOException) { return; }
            Allow(path);
            throw NotTested.Because("the denial of " + path + " did not take effect for this process (it reads everything)");
        }
        static void Allow(string path)
        {
            if (OperatingSystem.IsWindows()) Icacls("\"" + path + "\" /remove:d *" + Me());
            else File.SetUnixFileMode(path, Directory.Exists(path) ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute : UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        static AgentApp NewApp(Env env, string login, IEnumerable<string> sources, out BackupSetInfo set, string engine = "", Action<BackupSetInfo> more = null)
        {
            env.CreateUser(login, Pw, 1);
            var app = env.Agent(login, Pw);
            var s = new BackupSetInfo { Name = "Docs", Engine = engine, Vss = false };   // VSS off: what is open or denied is read as it is
            s.Sources.AddRange(sources);
            if (more != null) more(s);
            var created = app.CreateSet(app.Interactive(Pw, null), Pw, s);
            set = app.Sets().First(x => x.Id == created.Id);
            Assert.False(set.Vss);
            return app;
        }

        /// <summary>The err lines of a run that name the given path (the path field, or the message).</summary>
        static List<string> ErrLinesNaming(IEnumerable<string> log, string path)
        {
            return log.Where(l => { var f = AhsayLog.Fields(l); return f.Length > 2 && f[1] == "err" && l.IndexOf(path, StringComparison.OrdinalIgnoreCase) >= 0; }).ToList();
        }
        static List<string> ErrLines(IEnumerable<string> log) { return log.Where(l => { var f = AhsayLog.Fields(l); return f.Length > 2 && f[1] == "err"; }).ToList(); }

        static string ServerJobLog(Env env, string login, string setId, string job)
        {
            var userDir = Directory.GetDirectories(env.HomeA, login, SearchOption.AllDirectories).First();
            var p = Path.Combine(userDir, "logs", setId, "Backup", job + ".log");
            Assert.True(File.Exists(p), "the server has no job log for run " + job + " (" + p + ")");
            return File.ReadAllText(p);
        }
        static Msg HistoryRow(Env env, string setId, string job)
        {
            return env.Api.Runs.Since(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1)).Single(m => m["set"] == setId && m["job"] == job);
        }
        static Msg AdminSet(Env env, string login, string setId)
        {
            return env.Admin().Call("GET", "/api/admin/users").List("users").First(u => u["login"] == login).List("sets").First(s => s["id"] == setId);
        }

        /// <summary>The server confirms the run as an error: history "bad", the admin site's last result, the job log names the path.</summary>
        static void ServerSaysError(Env env, string login, string setId, string job, string result, params string[] named)
        {
            Assert.Equal("bad", HistoryRow(env, setId, job)["status"]);
            Assert.Equal(result, AdminSet(env, login, setId)["lastResult"]);
            var text = ServerJobLog(env, login, setId, job);
            Assert.Contains(result, text);
            foreach (var n in named) Assert.True(text.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0, "the server's job log does not name " + n + ":\n" + text);
        }

        /// <summary>Restores the newest point (native engine) and returns SHA-256 by path relative to the source folder.</summary>
        static Dictionary<string, string> RestoreNewest(AgentApp app, Env env, BackupSetInfo set, string src, string name)
        {
            var target = Path.Combine(env.Root, name);
            if (set.Engine == "RESTIC")
            {
                app.Restic(set, Pw).Restore(null, target, null, new List<string>());
                return ResticTree(target, src);
            }
            var r = app.RestoreFor(app.Interactive(Pw, null), set.Id);
            r.Run(null, target, null, false);
            Assert.Equal(0, r.Failed);
            return AgentRig.Tree(AgentRig.Under(target, src));
        }
        static Dictionary<string, string> ResticTree(string target, string src)
        {
            // restic puts C:\x at target\C\x and /x at target/x, like the native restore
            var under = AgentRig.Under(target, src);
            return AgentRig.Tree(under);
        }

        static void Bump(string file, string text, int minutes)
        {
            File.WriteAllText(file, text); File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(minutes));
        }

        // ------------------------------------------------------------------ integration: happy path

        /// <summary>Everything readable: a clean success - no err and no warn line, history "ok", restore identical.</summary>
        [Fact]
        public void BK05_AllReadable_IsACleanSuccess_NoErrorNoWarning_RestoresIdentical()
        {
            using (var env = new Env())
            {
                var src = env.Dir("docs");
                File.WriteAllText(Path.Combine(src, "a.txt"), "alpha");
                Directory.CreateDirectory(Path.Combine(src, "sub", "deep"));
                File.WriteAllBytes(Path.Combine(src, "sub", "deep", "c.bin"), Enumerable.Range(0, 90000).Select(i => (byte)(i * 7)).ToArray());
                File.WriteAllText(Path.Combine(src, "sub", "b.txt"), "beta");
                var want = AgentRig.Tree(src);
                BackupSetInfo set; var app = NewApp(env, "bk05ok", new[] { src }, out set);
                var r = app.Backup(set.Id);
                Assert.Equal("BS_STOP_SUCCESS", r.Result);
                Assert.Equal(0, r.Errors); Assert.Equal(0, r.Warnings);
                Assert.Equal(3, r.New);
                Assert.Empty(ErrLines(r.LogLines));
                Assert.Equal("ok", HistoryRow(env, set.Id, r.Job)["status"]);
                Assert.Equal("BS_STOP_SUCCESS", AdminSet(env, "bk05ok", set.Id)["lastResult"]);
                Assert.Equal(want, RestoreNewest(app, env, set, src, "r1"));
            }
        }

        // ------------------------------------------------------------------ integration: failure and recovery

        /// <summary>
        /// A file changed and then denied by its permissions: the run is BS_STOP_SUCCESS_WITH_ERROR with an err line naming
        /// it (agent and server), nothing is deleted, the newest point restores every readable file identical and the denied
        /// one in its previous version; with the permission back the next run sends it and is a clean success.
        /// </summary>
        [Theory]
        [InlineData("")]
        [InlineData("RESTIC")]
        public void BK05_AFileDeniedByPermissions_IsAnErrorNamingIt_TheRestRestoreIdentical_AndItIsBackedUpOnceReadable(string engine)
        {
            if (engine == "RESTIC" && !File.Exists(Environment.GetEnvironmentVariable("OB_RESTIC") ?? "")) throw NotTested.Because("OB_RESTIC (the restic program) is not set - the RESTIC case");
            using (var env = new Env())
            {
                var src = env.Dir("docs");
                File.WriteAllText(Path.Combine(src, "a.txt"), "alpha");
                Directory.CreateDirectory(Path.Combine(src, "sub")); File.WriteAllText(Path.Combine(src, "sub", "c.txt"), "gamma");
                var secret = Path.Combine(src, "payroll.xlsx"); File.WriteAllText(secret, "payroll v1");
                var v1 = AgentRig.Sha(secret);
                var login = "bk05acl" + (engine == "" ? "n" : "r");
                BackupSetInfo set; var app = NewApp(env, login, new[] { src }, out set, engine);
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);

                Bump(secret, "payroll v2 - changed, then its permission was taken away", 1);
                var v2 = AgentRig.Sha(secret);
                File.WriteAllText(Path.Combine(src, "b.txt"), "beta (new)");
                var want = AgentRig.Tree(src);
                Deny(secret);
                BackupRun r2;
                try { r2 = app.Backup(set.Id); }
                finally { Allow(secret); }
                Assert.Equal("BS_STOP_SUCCESS_WITH_ERROR", r2.Result);
                Assert.NotEmpty(ErrLinesNaming(r2.LogLines, "payroll.xlsx"));
                Assert.Equal(0, r2.Deleted);
                ServerSaysError(env, login, set.Id, r2.Job, "BS_STOP_SUCCESS_WITH_ERROR", "payroll.xlsx");

                var got = RestoreNewest(app, env, set, src, "r2");
                foreach (var f in new[] { "a.txt", "b.txt", Path.Combine("sub", "c.txt") }) Assert.Equal(want[f], got[f]);
                if (engine == "")
                    Assert.Equal(v1, got["payroll.xlsx"]);                                   // the earlier version is kept in the newest point
                else
                {
                    // restic leaves an unreadable file out of its new snapshot; the earlier snapshot still restores it
                    var first = app.Restic(set, Pw).SnapshotList().OrderBy(m => m["time"], StringComparer.Ordinal).First()["id"];
                    var t1 = Path.Combine(env.Root, "r2first");
                    app.Restic(set, Pw).Restore(first, t1, null, new List<string>());
                    Assert.Equal(v1, ResticTree(t1, src)["payroll.xlsx"]);
                    Assert.True(!got.ContainsKey("payroll.xlsx") || got["payroll.xlsx"] == v1, "the newest point holds a payroll.xlsx that is neither version");
                }

                var r3 = app.Backup(set.Id);
                Assert.Equal("BS_STOP_SUCCESS", r3.Result);
                Assert.Empty(ErrLines(r3.LogLines));
                Assert.Equal("ok", HistoryRow(env, set.Id, r3.Job)["status"]);
                var got3 = RestoreNewest(app, env, set, src, "r3");
                Assert.Equal(v2, got3["payroll.xlsx"]);
                Assert.Equal(AgentRig.Tree(src), got3);
            }
        }

        /// <summary>A subfolder that cannot be listed: an error naming the folder (not only a warning), its files are kept,
        /// not deleted, the rest restores identical; listed again, its new file is sent and the run is a clean success.</summary>
        [Fact]
        public void BK05_ASubfolderDeniedByPermissions_IsAnErrorNamingIt_ItsFilesAreKept_AndItComesBackClean()
        {
            using (var env = new Env())
            {
                var src = env.Dir("docs");
                var locked = Path.Combine(src, "HR"); Directory.CreateDirectory(locked);
                File.WriteAllText(Path.Combine(src, "ok.txt"), "ok"); File.WriteAllText(Path.Combine(locked, "s.txt"), "s v1");
                BackupSetInfo set; var app = NewApp(env, "bk05dir", new[] { src }, out set);
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);
                var point1 = AgentRig.Tree(src);

                File.WriteAllText(Path.Combine(locked, "new.txt"), "added while the folder is denied");
                Bump(Path.Combine(src, "ok.txt"), "ok v2", 1);
                Deny(locked);
                BackupRun r2;
                try { r2 = app.Backup(set.Id); }
                finally { Allow(locked); }
                Assert.Equal("BS_STOP_SUCCESS_WITH_ERROR", r2.Result);
                Assert.NotEmpty(ErrLinesNaming(r2.LogLines, locked));
                Assert.Equal(0, r2.Deleted);
                ServerSaysError(env, "bk05dir", set.Id, r2.Job, "BS_STOP_SUCCESS_WITH_ERROR", "HR");
                var got = RestoreNewest(app, env, set, src, "r2");
                Assert.Equal(AgentRig.Sha(Path.Combine(src, "ok.txt")), got["ok.txt"]);
                Assert.Equal(point1[Path.Combine("HR", "s.txt")], got[Path.Combine("HR", "s.txt")]);   // kept, not deleted
                Assert.False(got.ContainsKey(Path.Combine("HR", "new.txt")));

                var r3 = app.Backup(set.Id);
                Assert.Equal("BS_STOP_SUCCESS", r3.Result);
                Assert.Empty(ErrLines(r3.LogLines));
                Assert.Equal(1, r3.New);
                Assert.Equal(AgentRig.Tree(src), RestoreNewest(app, env, set, src, "r3"));
            }
        }

        /// <summary>Holds an exclusive lock on a file from ANOTHER process until disposed (Windows: PowerShell; Linux: flock).</summary>
        sealed class ForeignLock : IDisposable
        {
            readonly Process p;
            public ForeignLock(string file)
            {
                ProcessStartInfo psi;
                if (OperatingSystem.IsWindows())
                    psi = new ProcessStartInfo("powershell", "-NoProfile -Command \"$f=[IO.File]::Open('" + file.Replace("'", "''") + "','Open','ReadWrite','None'); Start-Sleep -Seconds 600\"");
                else if (File.Exists("/usr/bin/flock")) psi = new ProcessStartInfo("/usr/bin/flock", "-x \"" + file + "\" sleep 600");
                else throw NotTested.Because("no program here to lock a file from another process (powershell / flock)");
                psi.UseShellExecute = false; psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
                p = Process.Start(psi);
                var until = DateTime.UtcNow.AddSeconds(60);
                while (true)
                {
                    try { using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) { } }
                    catch (IOException) { return; }   // the lock is in place
                    if (p.HasExited || DateTime.UtcNow > until) { Dispose(); throw new InvalidOperationException("the other process did not lock " + file); }
                    System.Threading.Thread.Sleep(100);
                }
            }
            public void Dispose() { try { if (!p.HasExited) { p.Kill(true); p.WaitForExit(10000); } } catch (Exception) { } }
        }

        /// <summary>A file changed and held with an exclusive lock by another program, Shadow Copy off: an error naming it,
        /// the previous version kept; released, the next run sends the new version and is a clean success.</summary>
        [Fact]
        public void BK05_AFileLockedByAnotherProcess_VssOff_IsAnErrorNamingIt_AndItIsSentOnceReleased()
        {
            using (var env = new Env())
            {
                var src = env.Dir("docs");
                File.WriteAllText(Path.Combine(src, "a.txt"), "alpha");
                var db = Path.Combine(src, "ledger.mdb"); File.WriteAllText(db, "ledger v1");
                var v1 = AgentRig.Sha(db);
                BackupSetInfo set; var app = NewApp(env, "bk05lock", new[] { src }, out set);
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);

                Bump(db, "ledger v2 - written by the accounting program that keeps it open", 1);
                var v2 = AgentRig.Sha(db);
                File.WriteAllText(Path.Combine(src, "b.txt"), "beta");
                BackupRun r2;
                using (new ForeignLock(db)) r2 = app.Backup(set.Id);
                Assert.Equal("BS_STOP_SUCCESS_WITH_ERROR", r2.Result);
                Assert.NotEmpty(ErrLinesNaming(r2.LogLines, "ledger.mdb"));
                Assert.Equal(0, r2.Deleted);
                ServerSaysError(env, "bk05lock", set.Id, r2.Job, "BS_STOP_SUCCESS_WITH_ERROR", "ledger.mdb");
                var got = RestoreNewest(app, env, set, src, "r2");
                Assert.Equal(AgentRig.Sha(Path.Combine(src, "a.txt")), got["a.txt"]);
                Assert.Equal(AgentRig.Sha(Path.Combine(src, "b.txt")), got["b.txt"]);
                Assert.Equal(v1, got["ledger.mdb"]);

                var r3 = app.Backup(set.Id);
                Assert.Equal("BS_STOP_SUCCESS", r3.Result);
                Assert.Empty(ErrLines(r3.LogLines));
                var got3 = RestoreNewest(app, env, set, src, "r3");
                Assert.Equal(v2, got3["ledger.mdb"]);
                Assert.Equal(AgentRig.Tree(src), got3);
            }
        }

        /// <summary>The whole source folder removed: a failure (not "completed with warnings, 0 files") naming the folder,
        /// on the agent and the server; nothing deleted, the point still restores every file; back, a clean success.</summary>
        [Fact]
        public void BK05_TheWholeSourceRemoved_IsAFailureNamingIt_ThePointStillRestores_AndItComesBackClean()
        {
            using (var env = new Env())
            {
                var src = env.Dir("docs");
                File.WriteAllText(Path.Combine(src, "a.txt"), "alpha");
                Directory.CreateDirectory(Path.Combine(src, "sub")); File.WriteAllBytes(Path.Combine(src, "sub", "b.bin"), new byte[50000]);
                var want = AgentRig.Tree(src);
                BackupSetInfo set; var app = NewApp(env, "bk05gone", new[] { src }, out set);
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);

                var away = src + "-elsewhere"; Directory.Move(src, away);
                var r2 = app.Backup(set.Id);
                Assert.Equal("BS_STOP_BY_SYSTEM_ERROR", r2.Result);
                Assert.NotEmpty(ErrLinesNaming(r2.LogLines, src));
                Assert.Equal(0, r2.Deleted);
                ServerSaysError(env, "bk05gone", set.Id, r2.Job, "BS_STOP_BY_SYSTEM_ERROR", src);
                Assert.Equal(want, RestoreNewest(app, env, set, src, "r2"));

                Directory.Move(away, src);
                File.WriteAllText(Path.Combine(src, "c.txt"), "gamma");
                var r3 = app.Backup(set.Id);
                Assert.Equal("BS_STOP_SUCCESS", r3.Result);
                Assert.Empty(ErrLines(r3.LogLines));
                Assert.Equal(1, r3.New); Assert.Equal(0, r3.Updated); Assert.Equal(0, r3.Deleted);
                Assert.Equal(AgentRig.Tree(src), RestoreNewest(app, env, set, src, "r3"));
            }
        }

        /// <summary>A changed file deleted after the folder was listed and before it was read: an error naming it, its earlier
        /// version kept in the point (no silent success, no deletion in that run); put back, the next run sends it cleanly.</summary>
        [Fact]
        public void BK05_AFileDeletedBetweenScanAndRead_IsAnErrorNamingIt_TheEarlierVersionIsKept_AndItComesBackClean()
        {
            using (var env = new Env())
            {
                var src = env.Dir("docs");
                File.WriteAllText(Path.Combine(src, "a.txt"), "alpha");
                var z = Path.Combine(src, "zz-report.docx"); File.WriteAllText(z, "report v1");
                var v1 = AgentRig.Sha(z);
                BackupSetInfo set; var app = NewApp(env, "bk05race", new[] { src }, out set);
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);

                Bump(z, "report v2", 1);
                File.WriteAllText(Path.Combine(src, "b.txt"), "beta");
                // the run's stop check is asked before every file: at the first one, the folder is already listed - then the
                // last file (by name) is removed, as a user or a program would while the backup runs
                bool done = false;
                var run = new BackupRun(app.DeviceClient(), app.Home, set, app.Key(set)) { StopRequested = () => { if (!done) { done = true; File.Delete(z); } return false; } };
                run.Run();
                Assert.True(done);
                Assert.Equal("BS_STOP_SUCCESS_WITH_ERROR", run.Result);
                Assert.NotEmpty(ErrLinesNaming(run.LogLines, "zz-report.docx"));
                Assert.Equal(0, run.Deleted);
                ServerSaysError(env, "bk05race", set.Id, run.Job, "BS_STOP_SUCCESS_WITH_ERROR", "zz-report.docx");
                var got = RestoreNewest(app, env, set, src, "r2");
                Assert.Equal(AgentRig.Sha(Path.Combine(src, "a.txt")), got["a.txt"]);
                Assert.Equal(AgentRig.Sha(Path.Combine(src, "b.txt")), got["b.txt"]);
                Assert.Equal(v1, got["zz-report.docx"]);

                File.WriteAllText(z, "report v3 - put back");
                var r3 = app.Backup(set.Id);
                Assert.Equal("BS_STOP_SUCCESS", r3.Result);
                Assert.Empty(ErrLines(r3.LogLines));
                Assert.Equal(AgentRig.Tree(src), RestoreNewest(app, env, set, src, "r3"));
            }
        }

        // ------------------------------------------------------------------ end to end: the shipped agent on Windows

        /// <summary>
        /// A user-like run of the SHIPPED .NET 4.0 agent's command line natively on Windows (register, addset, backup,
        /// restore) with a file denied by its ACL: the command's result says WITH_ERROR and names the file, the server's
        /// history, admin site and job log say error, the restore brings every other file back identical; with the ACL
        /// entry removed the next backup is a clean success and the file restores identical.
        /// </summary>
        [Fact]
        public void BK05_E2E_ShippedAgentOnWindows_AnAclDeniedFile_IsAnErrorOnAgentAndServer_TheRestRestoresIdentical()
        {
            if (!OperatingSystem.IsWindows()) throw NotTested.Because("the shipped .NET 4.0 agent runs natively only on Windows, and the ACL denial is a Windows one");
            var exe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Agent", "bin", "Debug", "net40", "OnlineBackup.Agent.exe"));
            if (!File.Exists(exe)) throw NotTested.Because("the .NET 4.0 agent build is not there: " + exe);
            using (var env = new Env())
            {
                env.CreateUser("bk05e2e", Pw);
                var home = Path.Combine(env.Root, "net40home");
                var src = env.Dir("Company Files");
                var bytes = new byte[200 * 1024]; new Random(5).NextBytes(bytes);
                File.WriteAllBytes(Path.Combine(src, "ledger.bin"), bytes);
                Directory.CreateDirectory(Path.Combine(src, "Letters")); File.WriteAllText(Path.Combine(src, "Letters", "note.txt"), "a letter");
                Func<string, bool, string> run = (args, mustSucceed) =>
                {
                    var p = Process.Start(new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
                    var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    if (mustSucceed) Assert.True(p.ExitCode == 0, args.Split(' ')[0] + " failed (" + p.ExitCode + "): " + o);
                    return o.Trim();
                };
                run("register --home \"" + home + "\" --server " + env.Url + " --login bk05e2e --password " + Pw + " --computer WIN-BK05", true);
                var setId = run("addset --home \"" + home + "\" --password " + Pw + " --name Docs --source \"" + src + "\"", true).Split('\n').Last().Trim();
                var o1 = run("backup --home \"" + home + "\" --set " + setId, true);
                Assert.StartsWith("BS_STOP_SUCCESS", o1);
                Assert.DoesNotContain("err: ", o1);

                var payroll = Path.Combine(src, "payroll.xlsx");
                var pb = new byte[70 * 1024]; new Random(6).NextBytes(pb); File.WriteAllBytes(payroll, pb);
                File.AppendAllText(Path.Combine(src, "Letters", "note.txt"), " - and a second page");
                File.SetLastWriteTimeUtc(Path.Combine(src, "Letters", "note.txt"), DateTime.UtcNow.AddMinutes(1));
                Deny(payroll);
                string o2;
                try { o2 = run("backup --home \"" + home + "\" --set " + setId, false); }
                finally { Allow(payroll); }
                Assert.StartsWith("BS_STOP_SUCCESS_WITH_ERROR ", o2);
                Assert.Contains(o2.Split('\n'), l => l.StartsWith("err: ") && l.Contains(payroll));
                var job2 = env.Api.Runs.Since(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1)).Where(m => m["set"] == setId).Select(m => m["job"]).OrderBy(j => j, StringComparer.Ordinal).Last();
                ServerSaysError(env, "bk05e2e", setId, job2, "BS_STOP_SUCCESS_WITH_ERROR", "payroll.xlsx");

                var t2 = Path.Combine(env.Root, "restore2");
                Assert.Contains("restored=2 failed=0", run("restore --home \"" + home + "\" --set " + setId + " --password " + Pw + " --target \"" + t2 + "\"", true));
                var got2 = AgentRig.Tree(AgentRig.Under(t2, src));
                Assert.Equal(AgentRig.Sha(Path.Combine(src, "ledger.bin")), got2["ledger.bin"]);
                Assert.Equal(AgentRig.Sha(Path.Combine(src, "Letters", "note.txt")), got2[Path.Combine("Letters", "note.txt")]);
                Assert.False(got2.ContainsKey("payroll.xlsx"));

                var o3 = run("backup --home \"" + home + "\" --set " + setId, true);
                Assert.StartsWith("BS_STOP_SUCCESS new=1 ", o3);
                Assert.DoesNotContain("err: ", o3);
                var t3 = Path.Combine(env.Root, "restore3");
                Assert.Contains("restored=3 failed=0", run("restore --home \"" + home + "\" --set " + setId + " --password " + Pw + " --target \"" + t3 + "\"", true));
                Assert.Equal(AgentRig.Tree(src), AgentRig.Tree(AgentRig.Under(t3, src)));
            }
        }

        // ------------------------------------------------------------------ adversarial: ways an unreadable file could vanish

        /// <summary>A denied file inside an include-only filter is an error; a denied file the filter leaves out, and a denied
        /// folder the user deselected, are not data to back up - no error for them.</summary>
        [Fact]
        public void BK05_Adv_ADeniedFileInsideAnIncludeFilter_IsAnError_DeniedDataOutsideTheSelectionIsNot()
        {
            using (var env = new Env())
            {
                var src = env.Dir("docs");
                File.WriteAllText(Path.Combine(src, "a.docx"), "a");
                var plan = Path.Combine(src, "plan.docx"); File.WriteAllText(plan, "plan");
                var tmp = Path.Combine(src, "cache.tmpx"); File.WriteAllText(tmp, "cache");
                var skip = Path.Combine(src, "Private"); Directory.CreateDirectory(skip); File.WriteAllText(Path.Combine(skip, "p.docx"), "p");
                BackupSetInfo set; var app = NewApp(env, "bk05flt", new[] { src }, out set, "", s =>
                {
                    s.Filters.Add(new FilterRule { Type = "WILDCARD", Include = true, Only = true, ApplyFile = true, ApplyDir = false, Patterns = { "*.docx" } });
                    s.Deselected.Add(skip);
                });
                Deny(plan); Deny(tmp); Deny(skip);
                BackupRun r;
                try { r = app.Backup(set.Id); }
                finally { Allow(plan); Allow(tmp); Allow(skip); }
                Assert.Equal("BS_STOP_SUCCESS_WITH_ERROR", r.Result);
                Assert.NotEmpty(ErrLinesNaming(r.LogLines, "plan.docx"));
                Assert.Empty(ErrLinesNaming(r.LogLines, "cache.tmpx"));
                Assert.Empty(ErrLinesNaming(r.LogLines, skip));
                Assert.Single(ErrLines(r.LogLines));
                var got = RestoreNewest(app, env, set, src, "r1");
                Assert.Equal(new[] { "a.docx" }, got.Keys.ToArray());
            }
        }

        /// <summary>A folder path longer than 260 characters on Windows: its file is either backed up and restores identical,
        /// or the run is an error naming the folder - never a silent success without it. Native agent (.NET 8 build) here;
        /// the shipped .NET 4.0 build in the next test.</summary>
        [Fact]
        public void BK05_Adv_APathLongerThan260_IsBackedUpOrAnError_NeverSilentlyLeftOut()
        {
            using (var env = new Env())
            {
                var src = env.Dir("docs");
                File.WriteAllText(Path.Combine(src, "a.txt"), "alpha");
                var deep = src; while (deep.Length < 300) deep = Path.Combine(deep, "Department-Folder-" + deep.Length);
                Directory.CreateDirectory(deep);
                var f = Path.Combine(deep, "contract.pdf"); File.WriteAllText(f, "a contract deep down");
                Assert.True(f.Length > 260);
                BackupSetInfo set; var app = NewApp(env, "bk05long", new[] { src }, out set);
                var r = app.Backup(set.Id);
                if (r.Result == "BS_STOP_SUCCESS")
                {
                    var target = Path.Combine(env.Root, "r");
                    var rs = app.RestoreFor(app.Interactive(Pw, null), set.Id); rs.Run(null, target, null, false);
                    Assert.Equal(0, rs.Failed);
                    Assert.Equal(AgentRig.Tree(src), AgentRig.Tree(AgentRig.Under(target, src)));
                }
                else
                {
                    Assert.Equal("BS_STOP_SUCCESS_WITH_ERROR", r.Result);
                    Assert.Contains(ErrLines(r.LogLines), l => AhsayLog.Fields(l)[2].Length > 0 && f.StartsWith(AhsayLog.Fields(l)[2], StringComparison.OrdinalIgnoreCase));
                }
            }
        }

        /// <summary>The same deep path through the SHIPPED .NET 4.0 agent on Windows (where MAX_PATH applies).</summary>
        [Fact]
        public void BK05_Adv_ShippedAgentOnWindows_APathLongerThan260_IsBackedUpOrAnError_NeverSilentlyLeftOut()
        {
            if (!OperatingSystem.IsWindows()) throw NotTested.Because("the shipped .NET 4.0 agent runs natively only on Windows");
            var exe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Agent", "bin", "Debug", "net40", "OnlineBackup.Agent.exe"));
            if (!File.Exists(exe)) throw NotTested.Because("the .NET 4.0 agent build is not there: " + exe);
            using (var env = new Env())
            {
                env.CreateUser("bk05long4", Pw);
                var home = Path.Combine(env.Root, "net40home");
                var src = env.Dir("docs");
                File.WriteAllText(Path.Combine(src, "a.txt"), "alpha");
                var deep = src; while (deep.Length < 300) deep = Path.Combine(deep, "Department-Folder-" + deep.Length);
                Directory.CreateDirectory(deep);
                var f = Path.Combine(deep, "contract.pdf"); File.WriteAllText(f, "a contract deep down");
                Func<string, string> run = args =>
                {
                    var p = Process.Start(new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
                    var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd(); p.WaitForExit();
                    return "exit=" + p.ExitCode + "\n" + o.Trim();
                };
                Assert.StartsWith("exit=0", run("register --home \"" + home + "\" --server " + env.Url + " --login bk05long4 --password " + Pw + " --computer WIN-LONG"));
                var setId = run("addset --home \"" + home + "\" --password " + Pw + " --name Docs --source \"" + src + "\"").Split('\n').Last().Trim();
                var o = run("backup --home \"" + home + "\" --set " + setId);
                var lines = o.Split('\n').Select(l => l.Trim()).ToList();
                if (lines[1].StartsWith("BS_STOP_SUCCESS ") || lines[1].StartsWith("BS_STOP_SUCCESS_WITH_WARNING "))
                {
                    var target = Path.Combine(env.Root, "r");
                    var ro = run("restore --home \"" + home + "\" --set " + setId + " --password " + Pw + " --target \"" + target + "\"");
                    Assert.True(ro.Contains("restored=2 failed=0"), "the backup said " + lines[1] + " without an error, but the restore: " + ro);
                    Assert.Equal(AgentRig.Tree(src), AgentRig.Tree(AgentRig.Under(target, src)));
                }
                else
                {
                    Assert.True(lines[1].StartsWith("BS_STOP_SUCCESS_WITH_ERROR "), o);
                    Assert.True(lines.Any(l => l.StartsWith("err: ") && l.Length > 5 && f.StartsWith(l.Substring(5).Split(' ')[0], StringComparison.OrdinalIgnoreCase) && l.Substring(5).Split(' ')[0].Length > src.Length), "no err line names the deep folder:\n" + o);
                }
            }
        }

        /// <summary>A file name Windows programs cannot open by its plain path (a trailing dot, made with \\?\): backed up and
        /// restorable, or an error naming it - never silently left out.</summary>
        [Fact]
        public void BK05_Adv_AFileNameWithATrailingDot_IsBackedUpOrAnError_NeverSilentlyLeftOut()
        {
            if (!OperatingSystem.IsWindows()) throw NotTested.Because("a name only Windows cannot open by its plain path (Linux opens 'name.' as is)");
            using (var env = new Env())
            {
                var src = env.Dir("docs");
                File.WriteAllText(Path.Combine(src, "a.txt"), "alpha");
                var odd = @"\\?\" + Path.Combine(src, "minutes.");
                File.WriteAllText(odd, "made by a program that uses \\\\?\\ names");
                try
                {
                    BackupSetInfo set; var app = NewApp(env, "bk05dot", new[] { src }, out set);
                    var r = app.Backup(set.Id);
                    if (r.Result == "BS_STOP_SUCCESS")
                    {
                        var target = Path.Combine(env.Root, "r");
                        var rs = app.RestoreFor(app.Interactive(Pw, null), set.Id); rs.Run(null, target, null, false);
                        Assert.True(rs.Failed == 0 && rs.Restored == 2, "the backup was a clean success; the restore: restored " + rs.Restored + ", failed " + rs.Failed);
                    }
                    else
                    {
                        Assert.Equal("BS_STOP_SUCCESS_WITH_ERROR", r.Result);
                        Assert.NotEmpty(ErrLinesNaming(r.LogLines, "minutes."));
                    }
                }
                finally { try { File.Delete(odd); } catch (Exception) { } }
            }
        }

        /// <summary>A junction inside the source that leads to a folder this computer cannot list: an error naming it.</summary>
        [Fact]
        public void BK05_Adv_AJunctionToADeniedFolder_IsAnError()
        {
            if (!OperatingSystem.IsWindows()) throw NotTested.Because("junctions are Windows'");
            using (var env = new Env())
            {
                var src = env.Dir("docs");
                File.WriteAllText(Path.Combine(src, "a.txt"), "alpha");
                var outside = env.Dir("shared-hr"); File.WriteAllText(Path.Combine(outside, "salaries.txt"), "s");
                var link = Path.Combine(src, "HR-link");
                var p = Process.Start(new ProcessStartInfo("cmd", "/c mklink /J \"" + link + "\" \"" + outside + "\"") { UseShellExecute = false, RedirectStandardOutput = true }); p.StandardOutput.ReadToEnd(); p.WaitForExit();
                Assert.True(Directory.Exists(link));
                BackupSetInfo set; var app = NewApp(env, "bk05jct", new[] { src }, out set);
                Assert.True(set.FollowLink);
                Deny(outside);
                BackupRun r;
                try { r = app.Backup(set.Id); }
                finally { Allow(outside); }
                Assert.Equal("BS_STOP_SUCCESS_WITH_ERROR", r.Result);
                Assert.NotEmpty(ErrLinesNaming(r.LogLines, "HR-link"));
                Directory.Delete(link);
            }
        }

        /// <summary>A source on a network share that is not there (a disconnected drive): an error naming it - the whole run
        /// fails when it is the only source, and is WITH_ERROR next to a readable one.</summary>
        [Fact]
        public void BK05_Adv_ADisconnectedNetworkSource_IsAnErrorNamingIt()
        {
            if (!OperatingSystem.IsWindows()) throw NotTested.Because("UNC network paths are Windows'");
            using (var env = new Env())
            {
                var unc = @"\\localhost\ob-bk05-no-such-share$\Accounts";
                var src = env.Dir("docs"); File.WriteAllText(Path.Combine(src, "a.txt"), "alpha");
                BackupSetInfo set; var app = NewApp(env, "bk05unc", new[] { unc }, out set);
                var r = app.Backup(set.Id);
                Assert.Equal("BS_STOP_BY_SYSTEM_ERROR", r.Result);
                Assert.NotEmpty(ErrLinesNaming(r.LogLines, unc));

                BackupSetInfo set2; var app2 = NewApp(env, "bk05unc2", new[] { src, unc }, out set2);
                var r2 = app2.Backup(set2.Id);
                Assert.Equal("BS_STOP_SUCCESS_WITH_ERROR", r2.Result);
                Assert.NotEmpty(ErrLinesNaming(r2.LogLines, unc));
                Assert.Equal(AgentRig.Tree(src), RestoreNewest(app2, env, set2, src, "r2"));
            }
        }

        /// <summary>A file that becomes unreadable between two runs WITHOUT changing: the run need not read it, but its
        /// backed-up version must stay restorable identical in the newest point (never dropped).</summary>
        [Fact]
        public void BK05_Adv_AnUnchangedFileThatBecomesUnreadable_StaysRestorableInTheNewestPoint()
        {
            using (var env = new Env())
            {
                var src = env.Dir("docs");
                File.WriteAllText(Path.Combine(src, "a.txt"), "alpha");
                var f = Path.Combine(src, "contract.pdf"); File.WriteAllText(f, "signed contract");
                var want = AgentRig.Tree(src);
                BackupSetInfo set; var app = NewApp(env, "bk05later", new[] { src }, out set);
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);
                Deny(f);
                BackupRun r;
                try { r = app.Backup(set.Id); }
                finally { Allow(f); }
                Assert.Equal(0, r.Deleted);
                Assert.True(r.Result == "BS_STOP_SUCCESS" || r.Result == "BS_STOP_SUCCESS_WITH_ERROR", r.Result);
                if (r.Result != "BS_STOP_SUCCESS") Assert.NotEmpty(ErrLinesNaming(r.LogLines, "contract.pdf"));
                Assert.Equal(want, RestoreNewest(app, env, set, src, "r2"));
            }
        }
    }
}
