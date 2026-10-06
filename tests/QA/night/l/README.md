# QA agent L (night, snapshot 4c6deea) — breaking the QA system itself

Everything here was run on this machine (Linux, 4 cores, user root, mono installed, net40 agent NOT built,
OB_RESTIC = the night's restic). Evidence files: `mutation-logs/` (one log per mutant + one per baseline),
`counting_proof.py` (run: `python3 -I tests/QA/night/l/counting_proof.py`), `guards.py` (lists every early-return guard).
Mutation harness: `mut.py` (baseline of the same filter first, then ONE textual change in src, `dotnet test --filter`,
verdict = failures that are not in the baseline, then `git checkout -- <file>`; `git diff --stat src` printed after each).

## Findings

L-1 (High) Guarded tests return at line 1 and are reported Passed; the coverage matrix counts them as evidence.
  65 xUnit tests start with `if (<tool/OS missing>) return;` (table below). xUnit reports them Passed.
  capabilities.py/ledger.py/report.py read the trx outcome only, so a no-op is a verified dimension/layer.
  Proven (counting_proof.py case 1, on the committed tests/QA/reports/trx/xunit.trx): UI-09 integration layer = VERIFIED,
  its only test LoadTests.BigServer_* "Passed" in 3.8 ms (guard OB_LOAD != "1"). ST-06 failure dimension counts
  ReliabilityTests.ServerDiskFullMidBackup_* (2.0 ms, guard OB_ALLOW_MOUNT != "1" — set by NO script or workflow: it has never
  run anywhere). BK-05 failure counts SourceTests.SubfolderNotReadable_* (0.5 ms, returns when the user is root).
  Tonight's full run (scratchpad/full-wt TestResults/full.trx): EndToEndTests.Net40AgentUnderMonoBacksUpAndRestores 1.3 ms and
  TlsTests.Net40AgentUnderMonoUsesTheBuiltinTls 1.2 ms Passed — mono IS installed, but src/Agent/bin/Debug/net40 does not exist
  (the night run did not build net40); AG-08 would count them.
  Windows CI job (online-backup.yml `windows`): no OB_RESTIC there and Platform != Unix → all 65 return; the job is also
  continue-on-error. The xUnit "Tests (Windows)" result proves nothing about these 65.
  Fix: make a missing precondition visible — xUnit `Assert.Skip`/SkippableFact (outcome NotExecuted → "NOT TESTED" in the ledger),
  or have capabilities.results() treat a test whose source starts with a guard and whose duration < 50 ms as Skipped; and add a
  CI step that fails if any listed test was a no-op (e.g. grep the trx for those names with duration < 10 ms).

L-2 (High) capabilities.results(): several tests in ONE Playwright spec file share one key and the LAST one wins.
  `r[key] = ...` per spec, key = file name. A file whose first test failed and last passed is "Passed".
  Proven: counting_proof.py case 2 (synthetic last-run.json, j3 file with failed+passed → "Passed").
  Today ui-01 (2 tests), ui-02 (6), ui-03 (3) have several tests per file; report.py counts E2E pass/fail per key.
  Fix: key per spec title, or aggregate per file with Failed > Skipped > Passed (as done for xUnit).

L-3 (High) ReliabilityTests.RestoreInterruptedMidway_RunAgain_GivesIdenticalFiles never injects its fault.
  It waits for 60 MB in <target>/<source path> before KillRestic(). The restic restore writes into
  <target>/.ob-restoring-* and moves into place only when restic has finished, so that folder fills only after the restore
  is complete: the kill hits nothing and the test checks an uninterrupted restore.
  Proven: QaL_FaultInjectionProofTests with the old wait condition (temporary edit of the NEW test only) →
  "restore alive when killed: False; bytes in target then: 262144000; ... met while restic ran: False; first restore: no exception"
  → FAIL "the fault was not injected". With the condition on the staging folder it PASSES: restore killed while alive,
  the first restore fails, the second gives every file identical (SHA-256), no .ob-restoring-* left.
  Also: KillRestic() / ResticTests (line 140) kill EVERY process named restic on the machine (other tests', other agents').
  Fix: replace the wait condition (see the new test), assert the kill hit a live restic, kill only the test's own restic.

L-4 (Medium) QA_EXTRA_RESULTS lets an environment variable mark any test id Passed without a run (capabilities.py results()).
  Proven: counting_proof.py case 3 — RS-01 e2e layer NONE → VERIFIED with QA_EXTRA_RESULTS='{"qa:journeys/j4":"Passed","win:W05":"Passed"}'.
  Fix: remove it, or accept only from a signed evidence file and print every injected id in the ledger.

L-5 (Low) E2E "restore + SHA-256" credit is a prefix match: 'qa:failure-recovery/f1' credits f10..f19 (counting_proof.py case 4).
  Today f10..f15 all compare SHA-256, so no wrong credit yet. Fix: exact ids.

L-6 (Low) test ids resolve by method name in any class (`x:*.Method`): 12 ids in capabilities.py name a class that does not
  hold the method (e.g. x:AiTests.JsonReadsAndWritesGraphShapes is in M365Tests; 10 x:UnitTests.* are in CryptoTests /
  FormatTests / ChunkerTests). Fix: assert every x:Class.Method exists in Class (check is in this report's analysis).

L-7 (Medium) Mutation survivors — product checks no existing test protects (details in the table):
  restore SHA-256 check (M03), LocalState #end count (M01), the restore temp name (M15), the restore size check (M19),
  AgentApp.Mine both branches (M18/M18c), the upload stored-copy check in BackupRun (M20, still unprotected).
  New tests kill M01, M03, M15, M18, M18c, M19 (QaL_MutationGapTests). M20 has no test yet.

L-8 (Medium) Weak or misleading checks
  - IndexLinesLostComponentTests tears the "#end" line itself, so the "damaged line" check catches it and the count check is
    never exercised (M01 survived).
  - RestoreComponentTests' damaged-object test re-computes the index sha of the damaged object, so it tests chunk
    authentication, not the index checksum (M03 survived).
  - RestoreTempNameComponentTests uses the OLD name "<name>.restoring" (M15 survived with a fixed ".ob-restoring" name).
  - BareMetalStaleImageTests accepts BS_STOP_SUCCESS_WITH_ERROR as "not a success" (Assert.True(!success || WITH_ERROR)) —
    NEEDS OWNER DECISION whether a stale image may be "success with error".
  - win-e2e.ps1 W21/W12 "the backups are still on the server" check only that the run HISTORY has rows (Runs ... 'Backup'),
    not that a restore point / data exists. W18 (before-reboot) PASSes on two non-checks ([bool] of a saved string,
    Test-Path of the state file it just wrote).
  - J1: console errors containing "401" anywhere are filtered out (wanted401), not only the one wrong-password refusal.
  - F9: no assertion that the restore was still running when the server was killed (both branches pass if the restore
    ended before the kill).
  - qa.yml (public mirror) runs `npx playwright test` without the skipped==0 check that online-backup.yml has: F6/F8/F13/F14
    skipped there still give a green job.

L-9 (Info) Baseline failures at 4c6deea (not caused by mutations; also in tonight's full run):
  EndToEndTests.LockoutAfterThreeFailures_TwoFactorWithBackupCodes_DeviceRunsWithoutCode ("Wrong user name, password or code"
  at EndToEndTests.cs:394 — the test reuses the TOTP code of /api/totp/confirm in the same 30 s step; H-02 now refuses a
  step twice), UnreadableComponentTests.EverySourceGone_* (compares "#last" of the index before/after a later successful
  run), OptionsTests.EveryOption_HasATestThatProvesWhatItDoes, OptionsTests.EveryOption_ChangedOnTheServer_ArrivesAtTheComputer.
  Such failing tests also mask mutants in any filter that contains them (my M03 first run).

## Guards (xUnit tests that return before testing): 65
"here" = this machine tonight (root, Linux, OB_RESTIC set, mono present, net40 not built); "CI ubuntu" = online-backup.yml
tests job (non-root, OB_RESTIC set; mono presence on ubuntu-latest NOT verified — if absent the two Net40 tests are no-ops
there too); "CI windows" = the windows job (no OB_RESTIC). LoadTests runs in CI only in its own step with OB_LOAD=1, not in
the trx the ledger reads.

| file:line | test | guard | here | CI ubuntu | CI windows |
|---|---|---|---|---|---|
| AiTests.cs:247 | WebRestoreSearch_ByKeywords_AndInPlainWords_SendsNoFileNames | `string.IsNullOrEmpty(restic) &#124;&#124; !File.Exists(restic)` | runs | runs | NO-OP |
| AuditB_DbDumpTests.cs:30 | PostgresRolesDumpFails_ItIsAnError_AndTheLastRolesDumpStaysInTheLatestPoint | `Environment.OSVersion.Platform != PlatformID.Unix` | runs | runs | NO-OP |
| AuditJ_ResticEngineTests.cs:102 | UnreadableFile_ExitCode3_IsAnErrorNamedInTheLog | `!Have &#124;&#124; !CanDropCaps` | runs | runs | NO-OP |
| AuditJ_ResticEngineTests.cs:136 | WrongKey_FreshComputer_NeverCreatesANewRepository_OldSnapshotsIntact | `!Have` | runs | runs | NO-OP |
| AuditJ_ResticEngineTests.cs:161 | WrongKey_FreshComputer_ErrorSaysWrongKey_NotRepositoryCreation | `!Have` | runs | runs | NO-OP |
| AuditJ_ResticEngineTests.cs:183 | Retention_KeepLastOne_KeepsTheNewestSnapshot | `!Have` | runs | runs | NO-OP |
| AuditJ_ResticEngineTests.cs:213 | Retention_AfterTheSourcesChange_OldSnapshotsAreStillRemoved | `!Have` | runs | runs | NO-OP |
| AuditJ_ResticEngineTests.cs:238 | Restore_UnicodeSpacesAndLongNames_ByteIdentical_WholeAndSingleFile | `!Have` | runs | runs | NO-OP |
| AuditJ_ResticEngineTests.cs:275 | Restore_PathNotInThePoint_IsNotReportedAsSuccess | `!Have` | runs | runs | NO-OP |
| AuditJ_ResticServerTests.cs:61 | WindowRestore_ReplaceExistingUnchecked_KeepsTheNewerLocalFile | `!Have` | runs | runs | NO-OP |
| AuditJ_ResticServerTests.cs:102 | PartialSnapshot_ServerJobLog_ShowsErrorAndTheMissingFile | `!Have &#124;&#124; !OperatingSystem.IsLinux() &#124;&#124; !File.Exists("/usr/bin/setpriv")` | runs | runs | NO-OP |
| AuditJ_ResticServerTests.cs:137 | WrongKey_AgainstServerRepository_NothingChanges_RunIsAFailureOnTheServer | `!Have` | runs | runs | NO-OP |
| BareMetalStaleImageTests.cs:25 | AWbadminThatWritesNothing_IsNotASuccessWithTheOldImage | `Environment.OSVersion.Platform != PlatformID.Unix` | runs | runs | NO-OP |
| BareMetalTests.cs:25 | WholeComputerImageIsSentAsChangedBlocksOnly_AndRestoresInTheLayoutWindowsRecoveryReads | `Environment.OSVersion.Platform != PlatformID.Unix` | runs | runs | NO-OP |
| BareMetalTests.cs:90 | ImageToolFailureKeepsThePreviousImage_AndWindows2003UsesNtbackup | `Environment.OSVersion.Platform != PlatformID.Unix` | runs | runs | NO-OP |
| ChallengeF_ResticRunTests.cs:54 | RunFlagOfAnotherLiveProgram_DoesNotBlockTheBackup | `!Unix` | runs | runs | NO-OP |
| ChallengeF_ResticRunTests.cs:100 | ResticCommandLongerThanTheLease_IsNotRecordedAsAnInterruptedBackup | `!Unix` | runs | runs | NO-OP |
| DbHyperVTests.cs:43 | MySql_EveryDatabaseDumped_PasswordNeverInTheLog_DumpsTravelAsDeltas_LoadIntoANewDatabase | `Environment.OSVersion.Platform != PlatformID.Unix` | runs | runs | NO-OP |
| DbHyperVTests.cs:74 | MySqlAndPostgres_OnRestic_AndAFailingDatabaseIsReported | `!Restic` | runs | runs | NO-OP |
| DbHyperVTests.cs:96 | HyperV_VmsExportedOnline_OnlyChangedDiskBlocksSent_RestoredAndImported | `!Restic` | runs | runs | NO-OP |
| DestinationTests.cs:16 | LocalOnly_And_ServerPlusLocalCopy_WithRestic | `string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OB_RESTIC"))` | runs | runs | NO-OP |
| EndToEndTests.cs:638 | Net40AgentUnderMonoBacksUpAndRestores | `mono == null &#124;&#124; !File.Exists(exe)` | NO-OP | runs | NO-OP |
| EnterpriseTests.cs:158 | VMware_SnapshotCopyOfEveryVm_SnapshotsAlwaysRemoved_RestoredAsANewVm | `!Have &#124;&#124; OperatingSystem.IsWindows()` | runs | runs | NO-OP |
| EnterpriseTests.cs:215 | Oracle_RmanOnlineBackup_PasswordOnlyOnStdin_FailedRunKeepsTheLastBackup | `!Have &#124;&#124; OperatingSystem.IsWindows()` | runs | runs | NO-OP |
| EnterpriseTests.cs:263 | Domino_FoldersFromNotesIni_CacheFlushedBeforeTheCopy | `!Have &#124;&#124; OperatingSystem.IsWindows()` | runs | runs | NO-OP |
| FeatureTests.cs:303 | MssqlFullAndLogBackupsWithNativeBackupFiles_RestoreGivesTheBakFiles | `Environment.OSVersion.Platform != PlatformID.Unix` | runs | runs | NO-OP |
| FeatureTests.cs:346 | MssqlWeeklyFullAndDailyDifferential_TheFullStaysInEveryPoint_AnotherProgramsFullForcesOurs | `Environment.OSVersion.Platform != PlatformID.Unix` | runs | runs | NO-OP |
| GoogleTests.cs:106 | GoogleWorkspace_GmailAndDrive_ItemLevel_ChangesOnly_RestoreIntoGoogle | `!Have` | runs | runs | NO-OP |
| InterruptionTests.cs:87 | AgentKilledMidBackup_NextBackupRunsAtOnce_HistoryShowsTheFailure_NotRunning | `engine == "RESTIC" && !File.Exists(Environment.GetEnvironmentVariable("OB_RESTIC") ?? "")` | runs | runs | NO-OP |
| LoadTests.cs:27 | BigServer_500Customers_5000Sets_PagesAndComputersStayFast | `Environment.GetEnvironmentVariable("OB_LOAD") != "1"` | NO-OP | NO-OP | NO-OP |
| M365Tests.cs:206 | Microsoft365_ItemLevel_OnlyChangesFetched_DeletedMailRestoredIntoTheMailbox | `!Have` | runs | runs | NO-OP |
| OptionsTests.cs:269 | Links_FollowedOnlyWhenTheOptionIsOn | `Environment.OSVersion.Platform == PlatformID.Win32NT` | runs | runs | NO-OP |
| OptionsTests.cs:336 | SqlLogin_LikeSa_PasswordNeverOnTheCommandLine | `Environment.OSVersion.Platform != PlatformID.Unix` | runs | runs | NO-OP |
| PackageTests.cs:132 | LinuxClientPackage_KeepsExecutableBits_AndInstallsUnderTheProductName | `!OperatingSystem.IsLinux()` | runs | runs | NO-OP |
| PackageTests.cs:185 | MacClientPackage_BothProcessors_LaunchDaemonAndApp | `!OperatingSystem.IsLinux()` | runs | runs | NO-OP |
| PermissionTests.cs:55 | AFolderTheAgentMayNotRead_IsAnError_NotADeletion_AndEverythingElseRestoresIdentical | `Environment.OSVersion.Platform != PlatformID.Unix` | runs | runs | NO-OP |
| ProcessTests.cs:22 | StuckProgram_IsKilledWithItsChildren_AtTheLimit | `!Unix` | runs | runs | NO-OP |
| ProcessTests.cs:39 | ProgramWritingMuchToBothStreams_IsReadToTheEnd | `!Unix` | runs | runs | NO-OP |
| ProcessTests.cs:50 | StuckPreCommand_EndsAtTheLimit_AndTheBackupGoesOn | `!Unix` | runs | runs | NO-OP |
| ProcessTests.cs:84 | HungRestic_BackupEndsAsAFailure_AtTheIdleLimit_AndTheNextBackupRestoresIdentical | `!Unix &#124;&#124; string.IsNullOrEmpty(real) &#124;&#124; !File.Exists(real)` | runs | runs | NO-OP |
| ProcessTests.cs:121 | HungWebRestore_IsStopped_WithAClearTimeout | `!Unix` | runs | runs | NO-OP |
| ProcessTests.cs:146 | InstallStep_ChattyOnStderr_DoesNotHang_AndAStuckStepEndsAtTheLimit | `!Unix` | runs | runs | NO-OP |
| ReliabilityTests.cs:52 | RestoreInterruptedMidway_RunAgain_GivesIdenticalFiles | `!Have` | runs | runs | NO-OP |
| ReliabilityTests.cs:75 | Volume_20000SmallFilesAnd1GB_BackupChangeRestore | `!Have` | runs | runs | NO-OP |
| ReliabilityTests.cs:116 | ServerDiskFullMidBackup_ExistingBackupsUnharmed_NextRunAfterSpaceIsFreedCompletes | `!Have &#124;&#124; Environment.GetEnvironmentVariable("OB_ALLOW_MOUNT") != "1"` | NO-OP | NO-OP | NO-OP |
| ResticComponentTests.cs:54 | KnownTree_BacksUp_ChecksClean_RestoresIdentical_SecondRunAddsNothing | `!Have` | runs | runs | NO-OP |
| ResticComponentTests.cs:80 | AnotherKey_ReadsNothing | `!Have` | runs | runs | NO-OP |
| ResticComponentTests.cs:94 | DamagedPack_IsFoundByCheck_AndItsRestoreFailsLoudly | `!Have` | runs | runs | NO-OP |
| ResticComponentTests.cs:111 | StoppedInTheMiddle_RepositoryChecksClean_NextBackupCompletes_RestoresIdentical | `!Have` | runs | runs | NO-OP |
| ResticTests.cs:33 | ResticBacksUpToOurServer_OnlyChangesAreSent_RestoresExactly_AndTheServerProtectsTheRepository | `!Have` | runs | runs | NO-OP |
| ResticTests.cs:105 | ResticRespectsTheQuota | `!Have` | runs | runs | NO-OP |
| ResticTests.cs:121 | BackupKilledMidwayLeavesNothingBroken_TheNextRunCompletesAndEverythingRestores | `!Have` | runs | runs | NO-OP |
| ResticTests.cs:161 | SingleFileRestore_NamesWithBracketsAndStars | `!Have` | runs | runs | NO-OP |
| SchedulerTests.cs:94 | AfterASuccessfulScheduledBackup_TheSetIsNotDueAgain_UntilItsNextTime | `engine == "RESTIC" && !File.Exists(Environment.GetEnvironmentVariable("OB_RESTIC") ?? "")` | runs | runs | NO-OP |
| SourceTests.cs:41 | WholeSourceGone_IsAFailure_LastBackupNotRefreshed_FilesKept_AndComesBackCleanly | `engine == "RESTIC" && !File.Exists(Environment.GetEnvironmentVariable("OB_RESTIC") ?? "")` | runs | runs | NO-OP |
| SourceTests.cs:114 | SubfolderNotReadable_IsAnError_NotAWarning | `OperatingSystem.IsWindows() &#124;&#124; Environment.UserName == "root"` | NO-OP | runs | NO-OP |
| SqlScaleTests.cs:18 | NotEnoughRoom_TheDatabaseIsSkippedWithAClearMessage_BigOnesGetLargerBuffers | `Environment.OSVersion.Platform != PlatformID.Unix` | runs | runs | NO-OP |
| SqlScaleTests.cs:71 | FailingDatabase_IsAnError_AllFailing_IsAFailure | `Environment.OSVersion.Platform != PlatformID.Unix` | runs | runs | NO-OP |
| SqlScaleTests.cs:98 | SqlcmdWritingMuchToStderr_DoesNotHangTheBackup | `Environment.OSVersion.Platform != PlatformID.Unix` | runs | runs | NO-OP |
| SqlScaleTests.cs:126 | HungDatabaseBackup_IsStoppedAtTheLimit_TheRunEnds | `Environment.OSVersion.Platform != PlatformID.Unix` | runs | runs | NO-OP |
| SqlScaleTests.cs:153 | NoDatabaseFound_IsAFailure_NotAnEmptySuccess | `Environment.OSVersion.Platform != PlatformID.Unix` | runs | runs | NO-OP |
| SqlSimpleRecoveryLogTests.cs:22 | LogMode_AllDatabases_ASimpleRecoveryDatabaseIsSkipped_NotAnErrorEveryRun | `Environment.OSVersion.Platform != PlatformID.Unix` | runs | runs | NO-OP |
| TlsTests.cs:136 | Net40AgentUnderMonoUsesTheBuiltinTls | `mono == null &#124;&#124; !File.Exists(exe)` | NO-OP | runs | NO-OP |
| UpgradeTests.cs:73 | MakeFixture_WhenAsked | `string.IsNullOrEmpty(label)` | NO-OP | NO-OP | NO-OP |
| WebRestoreTests.cs:31 | CustomerRestoresChosenFilesFromTheWebsite_WithTheEncryptionPassword | `!Have` | runs | runs | NO-OP |

## Mutation table
Filters, diffs and outputs: mutation-logs/<id>.log; baselines: mutation-logs/baseline-*.log (all baselines passed fully).

| id | behaviour | mutation (one change) | tests run (filter) | result |
|---|---|---|---|---|
| M01 | LocalState #end count | `if (false && v2 && end != entries) throw` | IndexLinesLost, LocalStateComponent, AuditB_LocalState (8) | SURVIVED |
| M01b | same | same | + QaL_MutationGapTests | KILLED by QaL LinesLost_EndLineIntact_* |
| M02 | VerifyRows quarantine | damaged-but-readable object counted ok | SetStoreComponent, ChallengeF_Store, CommitRefusal | KILLED (SetStoreComponent.ADamagedObject_*, CommitRefusal) |
| M03 | Restore.RestoreFile SHA-256 check | `if (false && Sha256Hex != sha) throw` | RestoreComponent, AuditB_*, EndToEnd, UnitTests, ChallengeF_*, RestoreTempName, SetStoreComponent, UnreadableComponent (56) | SURVIVED (the 2 failures are the baseline failures of L-9) |
| M03b | same | same | RestoreComponent, RestoreTempName, QaL | KILLED by QaL AValidObjectOfAnotherFile_* |
| M04 | delta without base refused | `if (false && have != rec.Seq - 1 ...)` | AuditB_Chain, CommitRefusal, SetStoreComponent, LeaseComponent, ChallengeF_Store | KILLED (CommitRefusal) |
| M05 | commit journal written | journal write skipped | same | KILLED (LeaseComponent.ACommittingRun_*, SetStoreComponent.AStoppedRun_*) |
| M06 | journal rolled forward | RecoverPending moves skipped | same | KILLED (SetStoreComponent.AStoppedRun_*) |
| M07a | lease renewed by signs of life | LeaseTime ignores the lease file | LeaseComponent, ChallengeF_Store, Sweep, ChallengeF_ResticRun, RunId, OpenRun | KILLED |
| M07b | committing run never expired | journal guard removed in ExpireStale | same | KILLED (LeaseComponent.ACommittingRun_*) |
| M07c | lease length | `<= Lease + Lease` | same | KILLED |
| M08 | BackupRun size header = bytes read | `.Set("size", entry.Size)` | AuditB_GrowingFile | KILLED (grows: True) |
| M09 | restic Place() keeps existing files | always overwrite | AuditJ_ResticServer, AuditJ_ResticEngine | KILLED (WindowRestore_ReplaceExistingUnchecked_*) — would SURVIVE without OB_RESTIC (guard) |
| M10 | restic restore of nothing is an error | `if (false) throw` | same | KILLED (Restore_PathNotInThePoint_*) — same guard caveat |
| M11 | CheckSessionUser: fixed addresses | CheckIp removed | AuditH_Session, AuthComponent, Security | KILLED (CustomerFixedAddresses_*) |
| M12 | CheckSessionUser: suspended | suspended check removed | same | KILLED (SuspendedCustomer_*) |
| M13 | TOTP step used once | `step < TOTP_LAST_STEP` | same | KILLED (CustomerTwoStepCode_IsAcceptedOnlyOnce) |
| M14 | SetControl.Version check | `if (false && version != Version(e))` | SetEditVersionIntegration, SetControl | KILLED |
| M15 | restore temp name unpredictable | `var tmp = dest + ".ob-restoring";` | RestoreTempName, RestoreComponent, ChallengeF_RunEnd | SURVIVED |
| M15b | same | same | + QaL | KILLED by QaL ACustomerFileNamedLikeTheCurrentTemporarySuffix_* |
| M16 | Due(): not again after success | lastOk check removed | Scheduler, ClockSkewSchedule, TimeMachine | KILLED (4 tests) |
| M17 | DiskImage stale-image check | `if (false)` | BareMetal* | KILLED (BareMetalStaleImage) — SURVIVES on Windows (Unix guard) |
| M18 | Mine(): computer name | check removed | Computer, Guard, Scheduler, ClientUi | SURVIVED |
| M18b | same | same | Computer, Guard, QaL (first version) | SURVIVED (device/key branch covers the first scenario) |
| M18e | same | same | QaL ASetOfAnotherComputer_* (with a set made for another computer) | KILLED |
| M18c | Mine(): key of a same-named computer | `return true;` | Computer, Guard, Scheduler, ClientUi | SURVIVED |
| M18d | same | same | Computer, Guard, QaL | KILLED |
| M19 | restore size check | `if (false) throw` | RestoreComponent, AuditB_GrowingFile, AuditB_Chain | SURVIVED |
| M19b | same | same | + QaL | KILLED by QaL AnObjectWhoseChunksDoNotMakeItsStatedSize_* |
| M20 | upload: server's stored copy checked | `if (true)` | Network, AuditB_Store, ApiInputIntegration | SURVIVED (no test; not added) |

Totals: 20 behaviours, 23 first-round mutants (30 mutant runs). Existing suite: 16 killed, 7 survived (M01, M03, M15,
M18, M18c, M19, M20). With the new QaL tests: only M20 survives.
