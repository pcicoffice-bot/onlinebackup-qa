# windows-latest, select 'OnlineBackup.Tests.BK05UnreadableTests', 1 shards x 2 - run 37761613225 (6c05b14052e9ce8f99cb09c535df4b3288be790a)

**15 tests: 11 PASS, 1 FAIL, 3 NOT TESTED** (a test is PASS only when every job that had to run it reports PASS)

Build SHA-256: `c40d30fb9b542717218135d0cbeee5b650572a8b6566a4b76fb30f0a8c5fe858` (every shard checks it before running). Mode: strict - NOT green.

| job | state | tests | runner | minutes |
|---|---|---|---|---|
| s01-r01 | results | 15 | GitHub Actions 1000001759 | 2 |
| s01-r02 | results | 15 | GitHub Actions 1000001758 | 2 |

## Not PASS

| test | verdict | per job |
|---|---|---|
| OnlineBackup.Tests.BK05UnreadableTests.BK05_AFileDeniedByPermissions_IsAnErrorNamingIt_TheRestRestoreIdentical_AndItIsBackedUpOnceReadable(engine: "RESTIC") | FAIL | 0 PASS / 2 FAIL / 0 NOT TESTED of 2 - s01-r01: Assert.Equal() Failure: Strings differ                           ↓ (pos 15) Expected: "BS_STOP_SUCCESS_WITH_ERROR" Actual:   "BS_STOP_SUCCESS"; s01-r02: Assert.Equal() Failure: Strings differ                           ↓ (pos 15) Expected: "BS_STOP_SUCCESS_WITH_ERROR" Actual:   "BS_STOP_SUCCESS" |
| OnlineBackup.Tests.BK05UnreadableTests.BK05_ASubfolderDeniedByPermissions_IsAnErrorNamingIt_ItsFilesAreKept_AndItComesBackClean | NOT TESTED | 0 PASS / 0 FAIL / 2 NOT TESTED of 2 - s01-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: the denial of C:\Users\runneradmin\AppData\Local\Temp\obtest-7095c3f039\docs\HR did not take effect for this; s01-r02: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: the denial of C:\Users\runneradmin\AppData\Local\Temp\obtest-d435998ed3\docs\HR did not take effect for this |
| OnlineBackup.Tests.BK05UnreadableTests.BK05_Adv_ADeniedFileInsideAnIncludeFilter_IsAnError_DeniedDataOutsideTheSelectionIsNot | NOT TESTED | 0 PASS / 0 FAIL / 2 NOT TESTED of 2 - s01-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: the denial of C:\Users\runneradmin\AppData\Local\Temp\obtest-4af13a841f\docs\Private did not take effect for; s01-r02: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: the denial of C:\Users\runneradmin\AppData\Local\Temp\obtest-a3d6e5dddc\docs\Private did not take effect for |
| OnlineBackup.Tests.BK05UnreadableTests.BK05_Adv_AJunctionToADeniedFolder_IsAnError | NOT TESTED | 0 PASS / 0 FAIL / 2 NOT TESTED of 2 - s01-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: the denial of C:\Users\runneradmin\AppData\Local\Temp\obtest-cb84f16203\shared-hr did not take effect for th; s01-r02: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: the denial of C:\Users\runneradmin\AppData\Local\Temp\obtest-c876fe9835\shared-hr did not take effect for th |

## Repetitions

| test | PASS | FAIL | NOT TESTED |
|---|---|---|---|
| OnlineBackup.Tests.BK05UnreadableTests.BK05_AFileDeletedBetweenScanAndRead_IsAnErrorNamingIt_TheEarlierVersionIsKept_AndItComesBackClean | 2 | 0 | 0 |
| OnlineBackup.Tests.BK05UnreadableTests.BK05_AFileDeniedByPermissions_IsAnErrorNamingIt_TheRestRestoreIdentical_AndItIsBackedUpOnceReadable(engine: "") | 2 | 0 | 0 |
| OnlineBackup.Tests.BK05UnreadableTests.BK05_AFileDeniedByPermissions_IsAnErrorNamingIt_TheRestRestoreIdentical_AndItIsBackedUpOnceReadable(engine: "RESTIC") | 0 | 2 | 0 |
| OnlineBackup.Tests.BK05UnreadableTests.BK05_AFileLockedByAnotherProcess_VssOff_IsAnErrorNamingIt_AndItIsSentOnceReleased | 2 | 0 | 0 |
| OnlineBackup.Tests.BK05UnreadableTests.BK05_ASubfolderDeniedByPermissions_IsAnErrorNamingIt_ItsFilesAreKept_AndItComesBackClean | 0 | 0 | 2 |
| OnlineBackup.Tests.BK05UnreadableTests.BK05_Adv_ADeniedFileInsideAnIncludeFilter_IsAnError_DeniedDataOutsideTheSelectionIsNot | 0 | 0 | 2 |
| OnlineBackup.Tests.BK05UnreadableTests.BK05_Adv_ADisconnectedNetworkSource_IsAnErrorNamingIt | 2 | 0 | 0 |
| OnlineBackup.Tests.BK05UnreadableTests.BK05_Adv_AFileNameWithATrailingDot_IsBackedUpOrAnError_NeverSilentlyLeftOut | 2 | 0 | 0 |
| OnlineBackup.Tests.BK05UnreadableTests.BK05_Adv_AJunctionToADeniedFolder_IsAnError | 0 | 0 | 2 |
| OnlineBackup.Tests.BK05UnreadableTests.BK05_Adv_APathLongerThan260_IsBackedUpOrAnError_NeverSilentlyLeftOut | 2 | 0 | 0 |
| OnlineBackup.Tests.BK05UnreadableTests.BK05_Adv_AnUnchangedFileThatBecomesUnreadable_StaysRestorableInTheNewestPoint | 2 | 0 | 0 |
| OnlineBackup.Tests.BK05UnreadableTests.BK05_Adv_ShippedAgentOnWindows_APathLongerThan260_IsBackedUpOrAnError_NeverSilentlyLeftOut | 2 | 0 | 0 |
| OnlineBackup.Tests.BK05UnreadableTests.BK05_AllReadable_IsACleanSuccess_NoErrorNoWarning_RestoresIdentical | 2 | 0 | 0 |
| OnlineBackup.Tests.BK05UnreadableTests.BK05_E2E_ShippedAgentOnWindows_AnAclDeniedFile_IsAnErrorOnAgentAndServer_TheRestRestoresIdentical | 2 | 0 | 0 |
| OnlineBackup.Tests.BK05UnreadableTests.BK05_TheWholeSourceRemoved_IsAFailureNamingIt_ThePointStillRestores_AndItComesBackClean | 2 | 0 | 0 |
