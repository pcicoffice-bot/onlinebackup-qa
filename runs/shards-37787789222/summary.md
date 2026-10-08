# windows-latest, select 'OnlineBackup.Tests.PilotVss OnlineBackup.Tests.PilotFileBackupComponentTests OnlineBackup.Tests.PilotBackupComponentTests OnlineBackup.Tests.AuditB_GrowingFileTests OnlineBackup.Tests.InterruptionTests OnlineBackup.Tests.EndToEndTests OnlineBackup.Tests.SourceTests OnlineBackup.Tests.TlsTests.Net40Agent', 2 shards x 2 - run 37787789222 (ebe74d54633e96841a3270a97390a7043780f586)

**56 tests: 53 PASS, 0 FAIL, 3 NOT TESTED** (a test is PASS only when every job that had to run it reports PASS)

Build SHA-256: `2ee0de660abc936e21b3b379bfcbd1cebbb6271d69117bc589bd078d86a9fb63` (every shard checks it before running). Mode: strict - NOT green.

| job | state | tests | runner | minutes |
|---|---|---|---|---|
| s01-r01 | results | 26 | GitHub Actions 1000001859 | 6 |
| s01-r02 | results | 26 | GitHub Actions 1000001860 | 7 |
| s02-r01 | results | 30 | GitHub Actions 1000001861 | 4 |
| s02-r02 | results | 30 | GitHub Actions 1000001858 | 5 |

## Not PASS

| test | verdict | per job |
|---|---|---|
| OnlineBackup.Tests.SourceTests.SubfolderNotReadable_IsAnError_NotAWarning | NOT TESTED | 0 PASS / 0 FAIL / 2 NOT TESTED of 2 - s01-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: needs a non-root Linux user (root reads every folder; Windows has no such permission bits); s01-r02: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: needs a non-root Linux user (root reads every folder; Windows has no such permission bits) |
| OnlineBackup.Tests.EndToEndTests.Net40AgentUnderMonoBacksUpAndRestores | NOT TESTED | 0 PASS / 0 FAIL / 2 NOT TESTED of 2 - s02-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: needs mono and the .NET 4.0 agent build; s02-r02: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: needs mono and the .NET 4.0 agent build |
| OnlineBackup.Tests.TlsTests.Net40AgentUnderMonoUsesTheBuiltinTls | NOT TESTED | 0 PASS / 0 FAIL / 2 NOT TESTED of 2 - s02-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: needs mono and the .NET 4.0 agent build; s02-r02: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: needs mono and the .NET 4.0 agent build |

## Repetitions

| test | PASS | FAIL | NOT TESTED |
|---|---|---|---|
| OnlineBackup.Tests.InterruptionTests.AgentKilledMidBackup_NextBackupRunsAtOnce_HistoryShowsTheFailure_NotRunning(engine: "") | 2 | 0 | 0 |
| OnlineBackup.Tests.InterruptionTests.AgentKilledMidBackup_NextBackupRunsAtOnce_HistoryShowsTheFailure_NotRunning(engine: "RESTIC") | 2 | 0 | 0 |
| OnlineBackup.Tests.InterruptionTests.BeginSentTwice_GivesTheSameRun_NoFalseFailure_NoOrphan | 2 | 0 | 0 |
| OnlineBackup.Tests.InterruptionTests.ComputerNeverComesBack_RunIsClosedAsInterrupted_AndAnotherProcessCanBackUp | 2 | 0 | 0 |
| OnlineBackup.Tests.InterruptionTests.EndOfRunSentTwice_IsRecordedOnce_AndTheRepeatIsNotAnError(how: "abort") | 2 | 0 | 0 |
| OnlineBackup.Tests.InterruptionTests.EndOfRunSentTwice_IsRecordedOnce_AndTheRepeatIsNotAnError(how: "commit") | 2 | 0 | 0 |
| OnlineBackup.Tests.InterruptionTests.EndOfRunSentTwice_IsRecordedOnce_AndTheRepeatIsNotAnError(how: "resticreport") | 2 | 0 | 0 |
| OnlineBackup.Tests.InterruptionTests.InterruptedRunReportedByManyPathsAtOnce_IsRecordedExactlyOnce | 2 | 0 | 0 |
| OnlineBackup.Tests.InterruptionTests.LiveBackupThatKeepsReporting_IsNotClosedBySweeper | 2 | 0 | 0 |
| OnlineBackup.Tests.InterruptionTests.ProgressThatArrivesAfterTheEnd_DoesNotBringTheRunBack(engine: "") | 2 | 0 | 0 |
| OnlineBackup.Tests.InterruptionTests.ProgressThatArrivesAfterTheEnd_DoesNotBringTheRunBack(engine: "RESTIC") | 2 | 0 | 0 |
| OnlineBackup.Tests.InterruptionTests.ServerDownMidBackupAndBack_NextBackupRunsAtOnce | 2 | 0 | 0 |
| OnlineBackup.Tests.InterruptionTests.TwoDifferentResticRunsWithTheSameName_AreBothRecorded | 2 | 0 | 0 |
| OnlineBackup.Tests.PilotVssChallengeComponentTests.ASourceOnASubstDrive_EitherIsReadThroughASnapshot_OrTheRunSaysItHadNone_NothingIsLeftBehind | 2 | 0 | 0 |
| OnlineBackup.Tests.PilotVssChallengeComponentTests.TheShadowCopyServiceIsDisabled_TheRunSaysSo_IsNotAPlainSuccess_TheOtherFilesRestoreIdentical_AndOnceBackTheNextRunCompletes | 2 | 0 | 0 |
| OnlineBackup.Tests.PilotVssChallengeComponentTests.TwoSetsOnTheSameVolumeAtTheSameMoment_EachBacksUpItsHeldFile_RestoresIdentical_NoShadowCopyLeft | 2 | 0 | 0 |
| OnlineBackup.Tests.PilotVssEndToEndTests.TheAgentKilledWhileItsSnapshotExists_TheNextRunLeavesNoShadowCopy_AndRestoresIdentical | 2 | 0 | 0 |
| OnlineBackup.Tests.PilotVssEndToEndTests.TheShippedAgent_BacksUpAFileHeldOpenByAnotherProgram_RestoresIdentical_SendsItsChange_ThenNothing_NoShadowCopyLeft | 2 | 0 | 0 |
| OnlineBackup.Tests.PilotVssIntegrationTests.AFileWrittenDuringTheRun_ThePointHoldsTheSnapshotContent_TheNextRunSendsTheNewContent | 2 | 0 | 0 |
| OnlineBackup.Tests.PilotVssIntegrationTests.ALockedFile_IsBackedUpThroughTheShadowCopy_RestoresIdentical_NoShadowCopyLeft | 2 | 0 | 0 |
| OnlineBackup.Tests.PilotVssIntegrationTests.ARunTheServerStopsForTheQuota_RemovesItsShadowCopy_AndOnceThereIsRoom_TheNextRunCompletes | 2 | 0 | 0 |
| OnlineBackup.Tests.SourceTests.OneOfTwoSourcesGone_IsSuccessWithError_ShownAsProblem_NotAsWarning | 2 | 0 | 0 |
| OnlineBackup.Tests.SourceTests.RestoreTest_WithTheSourceOffline_IsNotChecked_NotFailed | 2 | 0 | 0 |
| OnlineBackup.Tests.SourceTests.SubfolderNotReadable_IsAnError_NotAWarning | 0 | 0 | 2 |
| OnlineBackup.Tests.SourceTests.WholeSourceGone_IsAFailure_LastBackupNotRefreshed_FilesKept_AndComesBackCleanly(engine: "") | 2 | 0 | 0 |
| OnlineBackup.Tests.SourceTests.WholeSourceGone_IsAFailure_LastBackupNotRefreshed_FilesKept_AndComesBackCleanly(engine: "RESTIC") | 2 | 0 | 0 |
| OnlineBackup.Tests.AuditB_GrowingFileTests.AFileThatGrowsAfterTheFolderWasListed_IsRestorable_OrTheRunSaysItIsNot(grows: False) | 2 | 0 | 0 |
| OnlineBackup.Tests.AuditB_GrowingFileTests.AFileThatGrowsAfterTheFolderWasListed_IsRestorable_OrTheRunSaysItIsNot(grows: True) | 2 | 0 | 0 |
| OnlineBackup.Tests.EndToEndTests.AnOfflineSourceIsNotTreatedAsDeleted | 2 | 0 | 0 |
| OnlineBackup.Tests.EndToEndTests.CrashDuringCommitIsRolledForward_AndAnUncommittedRunLeavesNothing | 2 | 0 | 0 |
| OnlineBackup.Tests.EndToEndTests.DamagedObjectIsFoundQuarantinedAndResentFromTheSource | 2 | 0 | 0 |
| OnlineBackup.Tests.EndToEndTests.DifferentialChain_EveryPointRestoresExactly_AndEachDeltaCarriesAllChangesSinceTheFull | 2 | 0 | 0 |
| OnlineBackup.Tests.EndToEndTests.EveryPointOfADeltaChainRestoresExactly_AndALongChainStartsANewFullCopy | 2 | 0 | 0 |
| OnlineBackup.Tests.EndToEndTests.FullCycle_NewUpdatedPermissionDeleted_DeltaForLargeFiles_RestoreAnyPoint | 2 | 0 | 0 |
| OnlineBackup.Tests.EndToEndTests.KeyRecoveryAndRestoreOnANewComputer | 2 | 0 | 0 |
| OnlineBackup.Tests.EndToEndTests.LockoutAfterThreeFailures_TwoFactorWithBackupCodes_DeviceRunsWithoutCode | 2 | 0 | 0 |
| OnlineBackup.Tests.EndToEndTests.LostLocalIndexIsRebuiltFromTheServerWithoutResendingEverything | 2 | 0 | 0 |
| OnlineBackup.Tests.EndToEndTests.Net40AgentUnderMonoBacksUpAndRestores | 0 | 0 | 2 |
| OnlineBackup.Tests.EndToEndTests.NewUsersGoToTheHomeWithTheLowestQuotaRatio | 2 | 0 | 0 |
| OnlineBackup.Tests.EndToEndTests.PreAndPostCommandsRunAndAreLogged_SchedulerCatchesUpMissedRuns | 2 | 0 | 0 |
| OnlineBackup.Tests.EndToEndTests.QuotaStopsNewBackupsAndKeepsExistingOnes | 2 | 0 | 0 |
| OnlineBackup.Tests.EndToEndTests.RebuildRecreatesTheIndexFromTheDisk | 2 | 0 | 0 |
| OnlineBackup.Tests.EndToEndTests.RetentionDeletesOnlyWhatNoKeptPointNeeds_CurrentIsNeverTouched | 2 | 0 | 0 |
| OnlineBackup.Tests.EndToEndTests.TenSetsPerUser | 2 | 0 | 0 |
| OnlineBackup.Tests.EndToEndTests.TruncatedUploadIsRejectedAndNeverCommitted | 2 | 0 | 0 |
| OnlineBackup.Tests.PilotFileBackupComponentTests.KnownTree_Run2SendsOnlyTheNewChangedAndPermissionChangedFiles_DeletesOne_EveryPointRestoresIdentical | 2 | 0 | 0 |
| OnlineBackup.Tests.PilotFileBackupComponentTests.TheServerFailsMidRun_TheRunFails_NothingDeleted_TheOldPointStillRestores_TheNextRunSendsTheChangesAgain | 2 | 0 | 0 |
| OnlineBackup.Tests.PilotVssComponentTests.ABackupWithShadowCopy_SendsALockedFile_IsASuccessWithoutError_RestoresIdentical_AndLeavesNoShadowCopy | 2 | 0 | 0 |
| OnlineBackup.Tests.PilotVssComponentTests.ADriveLetterThatDoesNotExist_GetsNoShadowCopy_AWarningNamesIt_NothingIsLeftBehind | 2 | 0 | 0 |
| OnlineBackup.Tests.PilotVssComponentTests.AFileRewrittenAfterTheSnapshot_ThePointHoldsTheSnapshotContent_AndTheNextRunSendsTheNewContent | 2 | 0 | 0 |
| OnlineBackup.Tests.PilotVssComponentTests.ALockedFile_IsReadFromTheSnapshot_WithItsContentAtSnapshotTime_AndTheSnapshotIsRemovedAfterwards | 2 | 0 | 0 |
| OnlineBackup.Tests.PilotVssComponentTests.SourcesWithoutADriveLetter_GetNoShadowCopy_AndNoWarning | 2 | 0 | 0 |
| OnlineBackup.Tests.PilotVssComponentTests.WhenTheRunFails_TheShadowCopyIsRemovedToo_AndTheNextRunCompletes | 2 | 0 | 0 |
| OnlineBackup.Tests.PilotVssFailureIntegrationTests.TheShadowCopyServiceIsDisabled_TheServerRecordsTheRunAsNotAPlainSuccess_WithTheReason_TheReadableFilesRestoreIdentical | 2 | 0 | 0 |
| OnlineBackup.Tests.TlsTests.Net40AgentUnderMonoUsesTheBuiltinTls | 0 | 0 | 2 |
| OnlineBackup.Tests.TlsTests.Net40Agent_NativeOnWindows_Tls12OnlyServer_BacksUpAndRestoresIdentical | 2 | 0 | 0 |
