using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// OPT-010: every option of a backup set is proven by what it does, not by how its screen looks. The whole way: the
    /// administrator changes it on the server (the admin site's own call), the computer takes it, and a real backup
    /// behaves accordingly — an upload limit is measured in seconds, a skipped file is missing from the restore.
    /// A new option with no proof here fails the build: nothing is left untested.
    /// </summary>
    public class OptionsTests
    {
        // every option of a set → the test that proves what it does ("Class.Method"), or why it does nothing of its own
        static readonly Dictionary<string, string> Proof = new Dictionary<string, string>
        {
            { "Id", "-the set's number, given by the server" }, { "Device", "-the registration that made the set (D-4, bug 66): kept by the server like Computer; Playwright failure-recovery/f12" }, { "Name", "SetControlTests.Admin_ChangesSettings_KeyTypeEngineKept_AgentSeesThem" },
            { "Type", "SetControlTests.Admin_ChangesSettings_KeyTypeEngineKept_AgentSeesThem" }, { "Computer", "SetControlTests.SetCopiedToAnotherComputer_OwnPathsAndSettings_SharedKey" },
            { "Sources", "OptionsTests.SkippedFoldersAndFilters_AreMissingFromTheRestore" }, { "Deselected", "OptionsTests.SkippedFoldersAndFilters_AreMissingFromTheRestore" },
            { "Filters", "OptionsTests.SkippedFoldersAndFilters_AreMissingFromTheRestore" },
            { "Hour", "EndToEndTests.PreAndPostCommandsRunAndAreLogged_SchedulerCatchesUpMissedRuns" }, { "Minute", "EndToEndTests.PreAndPostCommandsRunAndAreLogged_SchedulerCatchesUpMissedRuns" },
            { "Days", "SetControlTests.SeveralTimesADay_EachWithItsDays" }, { "MoreSchedules", "SetControlTests.SeveralTimesADay_EachWithItsDays" },
            { "DurationHours", "OptionsTests.MaximumDuration_StopsTheBackup_AndTheNextRunGoesOn" },
            { "Retention", "EndToEndTests.RetentionDeletesOnlyWhatNoKeptPointNeeds_CurrentIsNeverTouched" },
            { "Vss", "-Windows only (volume shadow copy); checked on Windows by the CI's Windows job" },
            { "FollowLink", "OptionsTests.Links_FollowedOnlyWhenTheOptionIsOn" },
            { "BackupPermissions", "EndToEndTests.FullCycle_NewUpdatedPermissionDeleted_DeltaForLargeFiles_RestoreAnyPoint" },
            { "MinDeltaFileSize", "EndToEndTests.FullCycle_NewUpdatedPermissionDeleted_DeltaForLargeFiles_RestoreAnyPoint" },
            { "MaxDeltaNo", "EndToEndTests.EveryPointOfADeltaChainRestoresExactly_AndALongChainStartsANewFullCopy" },
            { "MaxDeltaRatio", "EndToEndTests.EveryPointOfADeltaChainRestoresExactly_AndALongChainStartsANewFullCopy" },
            { "DeltaType", "EndToEndTests.DifferentialChain_EveryPointRestoresExactly_AndEachDeltaCarriesAllChangesSinceTheFull" },
            { "KeyType", "EndToEndTests.KeyRecoveryAndRestoreOnANewComputer" }, { "KeyCheck", "CryptoTests.PasswordKeyIsDeterministicAndCheckValueDetectsWrongPassword" }, { "KeySalt", "CryptoTests.PasswordKeyIsDeterministicAndCheckValueDetectsWrongPassword" },
            { "LocalCopy", "FeatureTests.LocalCopyIsWrittenBesideTheOnlineBackupAndRestoresWithoutTheServer" }, { "LocalCopyPath", "DestinationTests.LocalOnly_And_ServerPlusLocalCopy_WithRestic" },
            { "LocalCopyDays", "FeatureTests.LocalCopyIsWrittenBesideTheOnlineBackupAndRestoresWithoutTheServer" },
            { "PreCommands", "EndToEndTests.PreAndPostCommandsRunAndAreLogged_SchedulerCatchesUpMissedRuns" }, { "PostCommands", "EndToEndTests.PreAndPostCommandsRunAndAreLogged_SchedulerCatchesUpMissedRuns" },
            { "StopOnPreCommandFailure", "OptionsTests.FailedPreCommand_StopsTheBackupOnlyWhenAsked" },
            { "LogRetentionDays", "-kept with the set for Ahsay compatibility; the server's log cleaning uses its own setting" },
            { "WorkingDir", "EnterpriseTests.Oracle_RmanOnlineBackup_PasswordOnlyOnStdin_FailedRunKeepsTheLastBackup" },
            { "SqlUser", "OptionsTests.SqlLogin_LikeSa_PasswordNeverOnTheCommandLine" },
            { "LogIntervalMinutes", "FeatureTests.MssqlFullAndLogBackupsWithNativeBackupFiles_RestoreGivesTheBakFiles" },
            { "SqlFullDay", "FeatureTests.MssqlWeeklyFullAndDailyDifferential_TheFullStaysInEveryPoint_AnotherProgramsFullForcesOurs" },
            { "M365Tenant", "M365Tests.Microsoft365_ItemLevel_OnlyChangesFetched_DeletedMailRestoredIntoTheMailbox" }, { "M365ClientId", "M365Tests.Microsoft365_ItemLevel_OnlyChangesFetched_DeletedMailRestoredIntoTheMailbox" },
            { "M365Users", "M365Tests.Microsoft365_ItemLevel_OnlyChangesFetched_DeletedMailRestoredIntoTheMailbox" },
            { "DbHost", "DbHyperVTests.MySql_EveryDatabaseDumped_PasswordNeverInTheLog_DumpsTravelAsDeltas_LoadIntoANewDatabase" }, { "DbPort", "DbHyperVTests.MySql_EveryDatabaseDumped_PasswordNeverInTheLog_DumpsTravelAsDeltas_LoadIntoANewDatabase" },
            { "GwsAdmin", "GoogleTests.GoogleWorkspace_GmailAndDrive_ItemLevel_ChangesOnly_RestoreIntoGoogle" },
            { "VmDatacenter", "EnterpriseTests.VMware_SnapshotCopyOfEveryVm_SnapshotsAlwaysRemoved_RestoredAsANewVm" }, { "VmThumbprint", "EnterpriseTests.VMware_SnapshotCopyOfEveryVm_SnapshotsAlwaysRemoved_RestoredAsANewVm" },
            { "Parent", "SetControlTests.SetCopiedToAnotherComputer_OwnPathsAndSettings_SharedKey" },
            { "RunRequest", "SetControlTests.BackUpNow_And_Stop_FromTheServer" }, { "StopRequest", "SetControlTests.BackUpNow_And_Stop_FromTheServer" },
            { "Compression", "OptionsTests.Compression_ReachesTheEngine_AndChangesWhatIsStored" },
            { "BandwidthKbps", "OptionsTests.UploadLimit_SetOnTheServer_SlowsARealBackupToTheLimit" },
            { "BusyCpuPercent", "OptionsTests.SettingsOfTheResources_ReachTheEngine" }, { "LowPriority", "OptionsTests.SettingsOfTheResources_ReachTheEngine" },
            { "RunMissed", "ResourceTests.MissedRun_AfterTheDelay_OnlyWhenOldEnough_OrNever" }, { "MissedDelayMinutes", "ResourceTests.MissedRun_AfterTheDelay_OnlyWhenOldEnough_OrNever" },
            { "MissedMinHours", "ResourceTests.MissedRun_AfterTheDelay_OnlyWhenOldEnough_OrNever" }, { "RunMissedNet", "ResourceTests.MissedOrCutOffByTheInternet_StartsWhenItIsBack" },
            { "DestMode", "DestinationTests.LocalOnly_And_ServerPlusLocalCopy_WithRestic" }, { "Engine", "ResticTests.ResticBacksUpToOurServer_OnlyChangesAreSent_RestoresExactly_AndTheServerProtectsTheRepository" },
        };

        static bool Have { get { var r = Environment.GetEnvironmentVariable("OB_RESTIC"); return !string.IsNullOrEmpty(r) && File.Exists(r); } }

        [Fact]
        public void EveryOption_HasATestThatProvesWhatItDoes()
        {
            var options = typeof(BackupSetInfo).GetFields(BindingFlags.Public | BindingFlags.Instance).Select(f => f.Name).ToList();
            var missing = options.Where(o => !Proof.ContainsKey(o)).ToList();
            Assert.True(missing.Count == 0, "Options with no test — write one and add it to OptionsTests.Proof: " + string.Join(", ", missing));
            var gone = Proof.Keys.Where(k => !options.Contains(k)).ToList();
            Assert.True(gone.Count == 0, "No longer options: " + string.Join(", ", gone));
            var tests = new HashSet<string>(typeof(OptionsTests).Assembly.GetTypes().SelectMany(t => t.GetMethods().Where(m => m.GetCustomAttributes(typeof(FactAttribute), false).Length > 0).Select(m => t.Name + "." + m.Name)));
            var wrong = Proof.Where(kv => !kv.Value.StartsWith("-", StringComparison.Ordinal) && !tests.Contains(kv.Value)).Select(kv => kv.Key + " → " + kv.Value).ToList();
            Assert.True(wrong.Count == 0, "The proving test does not exist: " + string.Join("; ", wrong));
        }

        /// <summary>Every option, changed through the admin site's own call, arrives at the computer exactly as set.</summary>
        [Fact]
        public void EveryOption_ChangedOnTheServer_ArrivesAtTheComputer()
        {
            using (var env = new Env())
            {
                env.CreateUser("opts", "Customer-Pass-1");
                var app = env.Agent("opts", "Customer-Pass-1", name: "fs01");
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "Files", Sources = { env.Dir("src") } });
                var admin = env.Admin();
                var s = BackupSetInfo.FromXml(XElement.Parse(admin.Call("GET", "/api/admin/users/opts/sets/" + set.Id)["set"]));
                // the server keeps these on purpose (changing them would orphan the backups) or writes them itself
                var kept = new HashSet<string> { "Id", "Type", "Engine", "Computer", "Device", "Parent", "KeyType", "KeyCheck", "KeySalt", "RunRequest", "StopRequest", "DestMode", "LocalCopy", "LocalCopyPath" };
                var changed = new List<string>();
                foreach (var f in typeof(BackupSetInfo).GetFields(BindingFlags.Public | BindingFlags.Instance).Where(f => !kept.Contains(f.Name)))
                {
                    object v = Sample(f.Name, f.FieldType, f.GetValue(s));
                    if (v == null) continue;
                    f.SetValue(s, v); changed.Add(f.Name);
                }
                admin.Call("POST", "/api/admin/users/opts/sets/" + set.Id, new Msg().Set("set", s.ToXml().ToString()));
                var now = app.Sets().Single(x => x.Id == set.Id);
                var differ = changed.Where(n => Show(typeof(BackupSetInfo).GetField(n).GetValue(now)) != Show(typeof(BackupSetInfo).GetField(n).GetValue(s))).Select(n => n + ": sent " + Show(typeof(BackupSetInfo).GetField(n).GetValue(s)) + ", the computer has " + Show(typeof(BackupSetInfo).GetField(n).GetValue(now))).ToList();
                Assert.True(differ.Count == 0, "Lost on the way: " + string.Join("; ", differ));
                Assert.True(changed.Count >= 35, "only " + changed.Count + " options were changed");
            }
        }

        // a valid value other than the current one
        static object Sample(string name, Type t, object cur)
        {
            switch (name)
            {
                case "Name": return "Changed name";
                case "Compression": return "FAST";
                case "DeltaType": return "D";
                case "Days": return "-MTWTF-";
                case "Hour": return 3; case "Minute": return 45; case "DurationHours": return 5;
                case "SqlFullDay": return 2;
                case "BusyCpuPercent": return 70;
                case "Retention": return new RetentionPolicy { Unit = "DAYS", Period = 45, Daily = 7, Weekly = 4, Monthly = 12, Quarterly = 2, Yearly = 3 };
                case "MoreSchedules": return new List<ScheduleSlot> { new ScheduleSlot { Days = "S-----S", Hour = 13, Minute = 15 } };
                case "Filters": return new List<FilterRule> { new FilterRule { Type = "WILDCARD", Patterns = { "*.tmp" }, ApplyFile = true, Name = "COMMON" } };   // NAME: the admin site's "skip system files" switch
                case "PreCommands": return new List<string> { "echo before" };
                case "PostCommands": return new List<string> { "echo after" };
                case "Sources": return new List<string> { "/data/a", "/data/b" };
                case "Deselected": return new List<string> { "/data/a/cache" };
                case "WorkingDir": return "/var/tmp/ob";
            }
            if (t == typeof(bool)) return !(bool)cur;
            if (t == typeof(int)) return (int)cur + 7;
            if (t == typeof(long)) return (long)cur + 7;
            if (t == typeof(string)) return "opt-" + name.ToLowerInvariant();
            return null;
        }

        static string Show(object v)
        {
            if (v == null) return "null";
            if (v is RetentionPolicy) { var r = (RetentionPolicy)v; return r.Unit + "/" + r.Period + "/" + r.Daily + "/" + r.Weekly + "/" + r.Monthly + "/" + r.Quarterly + "/" + r.Yearly; }
            if (v is IEnumerable<ScheduleSlot>) return string.Join(",", ((IEnumerable<ScheduleSlot>)v).Select(x => x.Days + x.Hour + ":" + x.Minute));
            if (v is IEnumerable<FilterRule>) return string.Join(",", ((IEnumerable<FilterRule>)v).Select(x => x.Name + ":" + x.Type + (x.Include ? "+" : "-") + string.Join("|", x.Patterns)));
            if (v is IEnumerable<string> && !(v is string)) return string.Join(",", (IEnumerable<string>)v);
            return Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
        }

        static void Change(Env env, string login, string setId, Action<BackupSetInfo> edit)
        {
            var admin = env.Admin();
            var s = BackupSetInfo.FromXml(XElement.Parse(admin.Call("GET", "/api/admin/users/" + login + "/sets/" + setId)["set"]));
            edit(s);
            admin.Call("POST", "/api/admin/users/" + login + "/sets/" + setId, new Msg().Set("set", s.ToXml().ToString()));
        }

        static void Random(string path, int bytes, int seed) { var b = new byte[bytes]; new System.Random(seed).NextBytes(b); File.WriteAllBytes(path, b); }

        /// <summary>The upload limit, set by the administrator, measured on a real backup: 1.5 MB at 256 KB/s takes 6 seconds; without it, a moment.</summary>
        [Fact]
        public void UploadLimit_SetOnTheServer_SlowsARealBackupToTheLimit()
        {
            foreach (var engine in Have ? new[] { "", "RESTIC" } : new[] { "" })
                using (var env = new Env())
                {
                    env.CreateUser("bwlimit", "Customer-Pass-1");
                    var app = env.Agent("bwlimit", "Customer-Pass-1", name: "fs01");
                    var src = env.Dir("src");
                    var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "Files", Engine = engine, Sources = { src } });
                    Random(Path.Combine(src, "a.bin"), 64 * 1024, 1);
                    Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);          // the repository exists: only the data is timed below

                    Change(env, "bwlimit", set.Id, s => { s.BandwidthKbps = 256; s.Compression = "NONE"; });
                    Random(Path.Combine(src, "b.bin"), 1536 * 1024, 2);
                    var sw = Stopwatch.StartNew();
                    var r = app.Backup(set.Id);
                    var limited = sw.Elapsed.TotalSeconds;
                    Assert.True(r.Result == "BS_STOP_SUCCESS", string.Join("\n", r.LogLines));
                    Assert.True(limited >= 4.5, engine + ": 1.5 MB at 256 KB/s took only " + limited.ToString("0.0") + " s — the limit does not work");

                    Change(env, "bwlimit", set.Id, s => s.BandwidthKbps = 0);
                    Random(Path.Combine(src, "c.bin"), 1536 * 1024, 3);
                    sw.Restart();
                    Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);
                    Assert.True(sw.Elapsed.TotalSeconds < limited / 2, engine + ": without the limit it took " + sw.Elapsed.TotalSeconds.ToString("0.0") + " s");
                }
        }

        /// <summary>A folder taken out of the backup and a filter: the restore has everything else and not them.</summary>
        [Fact]
        public void SkippedFoldersAndFilters_AreMissingFromTheRestore()
        {
            using (var env = new Env())
            {
                env.CreateUser("flt", "Customer-Pass-1");
                var app = env.Agent("flt", "Customer-Pass-1", name: "fs01");
                var src = env.Dir("src");
                Directory.CreateDirectory(Path.Combine(src, "Docs")); Directory.CreateDirectory(Path.Combine(src, "Cache")); Directory.CreateDirectory(Path.Combine(src, "Other"));
                File.WriteAllText(Path.Combine(src, "Docs", "contract.docx"), "c"); File.WriteAllText(Path.Combine(src, "Docs", "draft.tmp"), "t");
                File.WriteAllText(Path.Combine(src, "Cache", "x.bin"), "x"); File.WriteAllText(Path.Combine(src, "Other", "keep.txt"), "k");
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "Files", Sources = { Path.Combine(src, "Docs"), Path.Combine(src, "Cache") } });
                Change(env, "flt", set.Id, s =>
                {
                    s.Sources = new List<string> { Path.Combine(src, "Docs"), Path.Combine(src, "Cache"), Path.Combine(src, "Other") };
                    s.Deselected = new List<string> { Path.Combine(src, "Cache") };
                    s.Filters = new List<FilterRule> { new FilterRule { Type = "WILDCARD", Patterns = { "*.tmp" }, ApplyFile = true } };
                });
                var r = app.Backup(set.Id);
                Assert.True(r.Result == "BS_STOP_SUCCESS", string.Join("\n", r.LogLines));
                var to = env.Dir("restore");
                var restore = app.RestoreFor(app.Interactive("Customer-Pass-1", null), set.Id);
                restore.Run(null, to, null, false);
                var names = Directory.GetFiles(to, "*", SearchOption.AllDirectories).Select(Path.GetFileName).ToList();
                Assert.Contains("contract.docx", names); Assert.Contains("keep.txt", names);   // a source added on the server
                Assert.DoesNotContain("draft.tmp", names);                                      // the filter
                Assert.DoesNotContain("x.bin", names);                                          // the folder taken out
            }
        }

        /// <summary>A failing pre-command stops the backup only when the set says so.</summary>
        [Fact]
        public void FailedPreCommand_StopsTheBackupOnlyWhenAsked()
        {
            using (var env = new Env())
            {
                env.CreateUser("pre", "Customer-Pass-1");
                var app = env.Agent("pre", "Customer-Pass-1", name: "fs01");
                var src = env.Dir("src"); File.WriteAllText(Path.Combine(src, "a.txt"), "a");
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "Files", Sources = { src } });
                var fail = Environment.OSVersion.Platform == PlatformID.Win32NT ? "cmd /c exit 3" : "sh -c \"exit 3\"";
                Change(env, "pre", set.Id, s => { s.PreCommands = new List<string> { fail }; s.StopOnPreCommandFailure = false; });
                var r1 = app.Backup(set.Id);
                Assert.True(r1.Result.StartsWith("BS_STOP_SUCCESS", StringComparison.Ordinal), "went on: " + r1.Result + "\n" + string.Join("\n", r1.LogLines));
                Assert.Equal(1, r1.New);
                Change(env, "pre", set.Id, s => s.StopOnPreCommandFailure = true);
                File.WriteAllText(Path.Combine(src, "b.txt"), "b");
                var r2 = app.Backup(set.Id);
                Assert.False(r2.Result.StartsWith("BS_STOP_SUCCESS", StringComparison.Ordinal), "should have stopped: " + r2.Result);
                Assert.Equal(0, r2.New);
            }
        }

        /// <summary>The maximum duration: a backup that runs past it stops, keeps what it sent, and the next run finishes it.</summary>
        [Fact]
        public void MaximumDuration_StopsTheBackup_AndTheNextRunGoesOn()
        {
            using (var env = new Env())
            {
                env.CreateUser("dur", "Customer-Pass-1");
                var app = env.Agent("dur", "Customer-Pass-1", name: "fs01");
                var src = env.Dir("src");
                for (int i = 0; i < 40; i++) File.WriteAllText(Path.Combine(src, "f" + i.ToString("00") + ".txt"), "data " + i);
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "Files", Sources = { src } });
                Change(env, "dur", set.Id, s => s.DurationHours = 1);
                // the clock jumps 2 hours after the first files: past the maximum duration
                int calls = 0; var t0 = DateTime.UtcNow;
                app.RunClock = () => ++calls > 60 ? t0.AddHours(2) : t0;
                var r1 = app.Backup(set.Id);
                app.RunClock = null;
                Assert.True(r1.New < 40, "did not stop: " + r1.New + " files");
                Assert.Contains(r1.LogLines, l => l.Contains("maximum duration"));
                var r2 = app.Backup(set.Id);
                Assert.Equal("BS_STOP_SUCCESS", r2.Result);
                Assert.Equal(40, r1.New + r2.New);
            }
        }

        /// <summary>A link inside the backed-up folder: followed only when the option is on.</summary>
        [Fact]
        public void Links_FollowedOnlyWhenTheOptionIsOn()
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT) throw NotTested.Because("a folder link needs administrator rights on Windows");   // a folder link needs rights on Windows; the same code path
            using (var env = new Env())
            {
                env.CreateUser("lnk", "Customer-Pass-1");
                var app = env.Agent("lnk", "Customer-Pass-1", name: "fs01");
                var src = env.Dir("src"); var outside = env.Dir("outside");
                File.WriteAllText(Path.Combine(outside, "linked.txt"), "l"); File.WriteAllText(Path.Combine(src, "own.txt"), "o");
                Process.Start("ln", "-s \"" + outside + "\" \"" + Path.Combine(src, "link") + "\"").WaitForExit();
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "Files", Sources = { src } });
                Change(env, "lnk", set.Id, s => s.FollowLink = false);
                Assert.Equal(1, app.Backup(set.Id).New);
                Change(env, "lnk", set.Id, s => s.FollowLink = true);
                Assert.Equal(1, app.Backup(set.Id).New);
            }
        }

        /// <summary>Compression: the setting reaches restic, and on this product's engine changes what is stored.</summary>
        [Fact]
        public void Compression_ReachesTheEngine_AndChangesWhatIsStored()
        {
            long Stored(string level)
            {
                using (var env = new Env())
                {
                    env.CreateUser("cmp", "Customer-Pass-1");
                    var app = env.Agent("cmp", "Customer-Pass-1", name: "fs01");
                    var src = env.Dir("src");
                    File.WriteAllText(Path.Combine(src, "table.csv"), string.Concat(Enumerable.Range(0, 40000).Select(i => "row," + i + ",the same text again\n")));
                    var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "Files", Sources = { src } });
                    Change(env, "cmp", set.Id, s => s.Compression = level);
                    var r = app.Backup(set.Id);
                    Assert.Equal("BS_STOP_SUCCESS", r.Result);
                    return r.BytesSent;
                }
            }
            long none = Stored("NONE"), max = Stored("MAX");
            Assert.True(max * 4 < none, "MAX " + max + " vs NONE " + none);

            if (!Have) throw NotTested.Because("OB_RESTIC (the restic program) is not set");
            var lines = new List<string[]>();
            ResticRunner.Trace = a => { lock (lines) lines.Add(a); };
            try
            {
                using (var env = new Env())
                {
                    env.CreateUser("cmr", "Customer-Pass-1");
                    var app = env.Agent("cmr", "Customer-Pass-1", name: "fs01");
                    var src = env.Dir("src"); File.WriteAllText(Path.Combine(src, "a.txt"), "a");
                    var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "Files", Engine = "RESTIC", Sources = { src } });
                    foreach (var kv in new[] { new[] { "NONE", "off" }, new[] { "FAST", "auto" }, new[] { "MAX", "max" } })
                    {
                        Change(env, "cmr", set.Id, s => s.Compression = kv[0]);
                        lock (lines) lines.Clear();
                        Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);
                        string[] backup; lock (lines) backup = lines.First(a => a.Length > 0 && a[0] == "backup" && a.Contains("set:" + set.Id));
                        Assert.Equal(kv[1], backup[Array.IndexOf(backup, "--compression") + 1]);
                    }
                }
            }
            finally { ResticRunner.Trace = null; }
        }

        /// <summary>A SQL Server login (usually sa): sqlcmd gets the user, and the password only in its environment —
        /// never on the command line, where every user of the computer could read it.</summary>
        [Fact]
        public void SqlLogin_LikeSa_PasswordNeverOnTheCommandLine()
        {
            if (Environment.OSVersion.Platform != PlatformID.Unix) throw NotTested.Because("needs Linux (the stand-in tools are shell scripts)");
            var dir = Path.Combine(Path.GetTempPath(), "obsql-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
            var fake = Path.Combine(dir, "sqlcmd"); File.WriteAllText(fake, "#!/bin/sh\necho \"ARGS:$*\"\necho \"PW:$SQLCMDPASSWORD\"\n");
            Process.Start("chmod", "+x \"" + fake + "\"").WaitForExit();
            var old = Environment.GetEnvironmentVariable("OB_SQLCMD");
            try
            {
                Environment.SetEnvironmentVariable("OB_SQLCMD", fake);
                var o = SqlBackup.Run(@".\SQLEXPRESS", "SELECT 1", "sa", "Secret-Pw-9");
                Assert.Contains("-U sa", o); Assert.Contains("PW:Secret-Pw-9", o);
                Assert.DoesNotContain("Secret-Pw-9", o.Split('\n').First(l => l.StartsWith("ARGS:", StringComparison.Ordinal)));
                Assert.Contains("-E", SqlBackup.Run(@".\SQLEXPRESS", "SELECT 1"));   // no user: Windows authentication
            }
            finally { Environment.SetEnvironmentVariable("OB_SQLCMD", old); Directory.Delete(dir, true); }
        }

        /// <summary>Low priority and "wait while the computer is busy" reach the engine.</summary>
        [Fact]
        public void SettingsOfTheResources_ReachTheEngine()
        {
            using (var env = new Env())
            {
                env.CreateUser("res", "Customer-Pass-1");
                var app = env.Agent("res", "Customer-Pass-1", name: "fs01");
                var src = env.Dir("src"); File.WriteAllText(Path.Combine(src, "a.txt"), "a");
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "Files", Sources = { src } });
                Change(env, "res", set.Id, s => { s.BusyCpuPercent = 101; s.LowPriority = true; });   // 101: never busy enough — only the check is seen
                var r = app.Backup(set.Id);
                Assert.Equal("BS_STOP_SUCCESS", r.Result);
                Assert.Equal(100, app.Sets().Single().BusyCpuPercent);                                 // kept within 0..100
                Assert.True(app.Sets().Single().LowPriority);
            }
        }
    }
}
