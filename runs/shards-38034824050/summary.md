# windows-latest, select 'OnlineBackup.Tests.BK05UnreadableTests OnlineBackup.Tests.UnreadableComponentTests OnlineBackup.Tests.SourceTests OnlineBackup.Tests.PermissionTests', 1 shards x 2 - run 38034824050 (5fd067bf3e3c9cdaf2e7cc8459d0c01ce7a08bdf)

**24 tests: 22 PASS, 0 FAIL, 2 NOT TESTED** (a test is PASS only when every job that had to run it reports PASS)

Build SHA-256: `2ad50a58c3469edfe66d013f4c9017a3abd0473afc058817124bf818c259f78d` (every shard checks it before running). Mode: strict - NOT green.

| job | state | tests | runner | minutes |
|---|---|---|---|---|
| s01-r01 | results | 24 | GitHub Actions 1000002435 | 2 |
| s01-r02 | results | 24 | GitHub Actions 1000002407 | 2 |

## Not PASS

| test | verdict | per job |
|---|---|---|
| OnlineBackup.Tests.PermissionTests.AFolderTheAgentMayNotRead_IsAnError_NotADeletion_AndEverythingElseRestoresIdentical | NOT TESTED | 0 PASS / 0 FAIL / 2 NOT TESTED of 2 - s01-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: needs Linux file permissions (chmod); s01-r02: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: needs Linux file permissions (chmod) |
| OnlineBackup.Tests.SourceTests.SubfolderNotReadable_IsAnError_NotAWarning | NOT TESTED | 0 PASS / 0 FAIL / 2 NOT TESTED of 2 - s01-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: needs a non-root Linux user (root reads every folder; Windows has no such permission bits); s01-r02: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: needs a non-root Linux user (root reads every folder; Windows has no such permission bits) |

## Repetitions

| test | PASS | FAIL | NOT TESTED |
|---|---|---|---|
| OnlineBackup.Tests.BK05UnreadableTests.BK05_AFileDeletedBetweenScanAndRead_IsAnErrorNamingIt_TheEarlierVersionIsKept_AndItComesBackClean | 2 | 0 | 0 |
| OnlineBackup.Tests.BK05UnreadableTests.BK05_AFileDeniedByPermissions_IsAnErrorNamingIt_TheRestRestoreIdentical_AndItIsBackedUpOnceReadable(engine: "") | 2 | 0 | 0 |
| OnlineBackup.Tests.BK05UnreadableTests.BK05_AFileDeniedByPermissions_IsAnErrorNamingIt_TheRestRestoreIdentical_AndItIsBackedUpOnceReadable(engine: "RESTIC") | 2 | 0 | 0 |
| OnlineBackup.Tests.BK05UnreadableTests.BK05_AFileLockedByAnotherProcess_VssOff_IsAnErrorNamingIt_AndItIsSentOnceReleased | 2 | 0 | 0 |
| OnlineBackup.Tests.BK05UnreadableTests.BK05_ASubfolderDeniedByPermissions_IsAnErrorNamingIt_ItsFilesAreKept_AndItComesBackClean | 2 | 0 | 0 |
| OnlineBackup.Tests.BK05UnreadableTests.BK05_Adv_ADeniedFileInsideAnIncludeFilter_IsAnError_DeniedDataOutsideTheSelectionIsNot | 2 | 0 | 0 |
| OnlineBackup.Tests.BK05UnreadableTests.BK05_Adv_ADisconnectedNetworkSource_IsAnErrorNamingIt | 2 | 0 | 0 |
| OnlineBackup.Tests.BK05UnreadableTests.BK05_Adv_AFileNameWithATrailingDot_IsBackedUpOrAnError_NeverSilentlyLeftOut | 2 | 0 | 0 |
| OnlineBackup.Tests.BK05UnreadableTests.BK05_Adv_AJunctionToADeniedFolder_IsAnError | 2 | 0 | 0 |
| OnlineBackup.Tests.BK05UnreadableTests.BK05_Adv_APathLongerThan260_IsBackedUpOrAnError_NeverSilentlyLeftOut | 2 | 0 | 0 |
| OnlineBackup.Tests.BK05UnreadableTests.BK05_Adv_AnUnchangedFileThatBecomesUnreadable_StaysRestorableInTheNewestPoint | 2 | 0 | 0 |
| OnlineBackup.Tests.BK05UnreadableTests.BK05_Adv_ShippedAgentOnWindows_APathLongerThan260_IsBackedUpOrAnError_NeverSilentlyLeftOut | 2 | 0 | 0 |
| OnlineBackup.Tests.BK05UnreadableTests.BK05_AllReadable_IsACleanSuccess_NoErrorNoWarning_RestoresIdentical | 2 | 0 | 0 |
| OnlineBackup.Tests.BK05UnreadableTests.BK05_E2E_ShippedAgentOnWindows_AnAclDeniedFile_IsAnErrorOnAgentAndServer_TheRestRestoresIdentical | 2 | 0 | 0 |
| OnlineBackup.Tests.BK05UnreadableTests.BK05_TheWholeSourceRemoved_IsAFailureNamingIt_ThePointStillRestores_AndItComesBackClean | 2 | 0 | 0 |
| OnlineBackup.Tests.PermissionTests.AFolderTheAgentMayNotRead_IsAnError_NotADeletion_AndEverythingElseRestoresIdentical | 0 | 0 | 2 |
| OnlineBackup.Tests.SourceTests.OneOfTwoSourcesGone_IsSuccessWithError_ShownAsProblem_NotAsWarning | 2 | 0 | 0 |
| OnlineBackup.Tests.SourceTests.RestoreTest_WithTheSourceOffline_IsNotChecked_NotFailed | 2 | 0 | 0 |
| OnlineBackup.Tests.SourceTests.SubfolderNotReadable_IsAnError_NotAWarning | 0 | 0 | 2 |
| OnlineBackup.Tests.SourceTests.WholeSourceGone_IsAFailure_LastBackupNotRefreshed_FilesKept_AndComesBackCleanly(engine: "") | 2 | 0 | 0 |
| OnlineBackup.Tests.SourceTests.WholeSourceGone_IsAFailure_LastBackupNotRefreshed_FilesKept_AndComesBackCleanly(engine: "RESTIC") | 2 | 0 | 0 |
| OnlineBackup.Tests.UnreadableComponentTests.ALockedFile_IsAnError_NotADeletion_TheOthersRestoreIdentical_AndItIsSentOnceFree | 2 | 0 | 0 |
| OnlineBackup.Tests.UnreadableComponentTests.EverySourceGone_IsAFailure_NothingDeleted_TheIndexKeepsEveryFile_AndTheSourceComesBackWithoutResending | 2 | 0 | 0 |
| OnlineBackup.Tests.UnreadableComponentTests.OneOfTwoSourcesGone_IsSuccessWithError_NamesIt_ItsFilesAreNotDeleted | 2 | 0 | 0 |
