# windows-latest, select 'OnlineBackup.Tests.BK05UnreadableTests OnlineBackup.Tests.UnreadableComponentTests OnlineBackup.Tests.SourceTests', 1 shards x 2 - run 37762306694 (77cbc644fd0067067e63a7681bf81aa785700a37)

**23 tests: 21 PASS, 1 FAIL, 1 NOT TESTED** (a test is PASS only when every job that had to run it reports PASS)

Build SHA-256: `3037c445bc539efb365d1317b522cf88b6d6464c58b62c17f57ab1b19e0ac03f` (every shard checks it before running). Mode: strict - NOT green.

| job | state | tests | runner | minutes |
|---|---|---|---|---|
| s01-r01 | results | 23 | GitHub Actions 1000001769 | 2 |
| s01-r02 | results | 23 | GitHub Actions 1000001768 | 3 |

## Not PASS

| test | verdict | per job |
|---|---|---|
| OnlineBackup.Tests.BK05UnreadableTests.BK05_AFileDeniedByPermissions_IsAnErrorNamingIt_TheRestRestoreIdentical_AndItIsBackedUpOnceReadable(engine: "RESTIC") | FAIL | 0 PASS / 2 FAIL / 0 NOT TESTED of 2 - s01-r01: System.UnauthorizedAccessException : Access to the path 'C:\Users\runneradmin\AppData\Local\Temp\obtest-720df0db4a\r2read\C\Users\runneradmin\AppData\Local\Temp; s01-r02: System.UnauthorizedAccessException : Access to the path 'C:\Users\runneradmin\AppData\Local\Temp\obtest-7e6c60d834\r2read\C\Users\runneradmin\AppData\Local\Temp |
| OnlineBackup.Tests.SourceTests.SubfolderNotReadable_IsAnError_NotAWarning | NOT TESTED | 0 PASS / 0 FAIL / 2 NOT TESTED of 2 - s01-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: needs a non-root Linux user (root reads every folder; Windows has no such permission bits); s01-r02: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: needs a non-root Linux user (root reads every folder; Windows has no such permission bits) |

## Repetitions

| test | PASS | FAIL | NOT TESTED |
|---|---|---|---|
| OnlineBackup.Tests.BK05UnreadableTests.BK05_AFileDeletedBetweenScanAndRead_IsAnErrorNamingIt_TheEarlierVersionIsKept_AndItComesBackClean | 2 | 0 | 0 |
| OnlineBackup.Tests.BK05UnreadableTests.BK05_AFileDeniedByPermissions_IsAnErrorNamingIt_TheRestRestoreIdentical_AndItIsBackedUpOnceReadable(engine: "") | 2 | 0 | 0 |
| OnlineBackup.Tests.BK05UnreadableTests.BK05_AFileDeniedByPermissions_IsAnErrorNamingIt_TheRestRestoreIdentical_AndItIsBackedUpOnceReadable(engine: "RESTIC") | 0 | 2 | 0 |
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
| OnlineBackup.Tests.SourceTests.OneOfTwoSourcesGone_IsSuccessWithError_ShownAsProblem_NotAsWarning | 2 | 0 | 0 |
| OnlineBackup.Tests.SourceTests.RestoreTest_WithTheSourceOffline_IsNotChecked_NotFailed | 2 | 0 | 0 |
| OnlineBackup.Tests.SourceTests.SubfolderNotReadable_IsAnError_NotAWarning | 0 | 0 | 2 |
| OnlineBackup.Tests.SourceTests.WholeSourceGone_IsAFailure_LastBackupNotRefreshed_FilesKept_AndComesBackCleanly(engine: "") | 2 | 0 | 0 |
| OnlineBackup.Tests.SourceTests.WholeSourceGone_IsAFailure_LastBackupNotRefreshed_FilesKept_AndComesBackCleanly(engine: "RESTIC") | 2 | 0 | 0 |
| OnlineBackup.Tests.UnreadableComponentTests.ALockedFile_IsAnError_NotADeletion_TheOthersRestoreIdentical_AndItIsSentOnceFree | 2 | 0 | 0 |
| OnlineBackup.Tests.UnreadableComponentTests.EverySourceGone_IsAFailure_NothingDeleted_TheIndexKeepsEveryFile_AndTheSourceComesBackWithoutResending | 2 | 0 | 0 |
| OnlineBackup.Tests.UnreadableComponentTests.OneOfTwoSourcesGone_IsSuccessWithError_NamesIt_ItsFilesAreNotDeleted | 2 | 0 | 0 |
