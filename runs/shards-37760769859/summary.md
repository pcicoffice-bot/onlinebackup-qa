# windows-latest, select 'OnlineBackup.Tests.UnreadableComponentTests OnlineBackup.Tests.SourceTests', 1 shards x 2 - run 37760769859 (ef11620c37580d48641c463eaaa329fb7f3fe4d3)

**8 tests: 7 PASS, 0 FAIL, 1 NOT TESTED** (a test is PASS only when every job that had to run it reports PASS)

Build SHA-256: `fdacdbc82edaed42044b8c55d4c2bafcb6f80f8a213ecbfe8bc6b413d4c424cd` (every shard checks it before running). Mode: strict - NOT green.

| job | state | tests | runner | minutes |
|---|---|---|---|---|
| s01-r01 | results | 8 | GitHub Actions 1000001750 | 2 |
| s01-r02 | results | 8 | GitHub Actions 1000001749 | 2 |

## Not PASS

| test | verdict | per job |
|---|---|---|
| OnlineBackup.Tests.SourceTests.SubfolderNotReadable_IsAnError_NotAWarning | NOT TESTED | 0 PASS / 0 FAIL / 2 NOT TESTED of 2 - s01-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: needs a non-root Linux user (root reads every folder; Windows has no such permission bits); s01-r02: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: needs a non-root Linux user (root reads every folder; Windows has no such permission bits) |

## Repetitions

| test | PASS | FAIL | NOT TESTED |
|---|---|---|---|
| OnlineBackup.Tests.SourceTests.OneOfTwoSourcesGone_IsSuccessWithError_ShownAsProblem_NotAsWarning | 2 | 0 | 0 |
| OnlineBackup.Tests.SourceTests.RestoreTest_WithTheSourceOffline_IsNotChecked_NotFailed | 2 | 0 | 0 |
| OnlineBackup.Tests.SourceTests.SubfolderNotReadable_IsAnError_NotAWarning | 0 | 0 | 2 |
| OnlineBackup.Tests.SourceTests.WholeSourceGone_IsAFailure_LastBackupNotRefreshed_FilesKept_AndComesBackCleanly(engine: "") | 2 | 0 | 0 |
| OnlineBackup.Tests.SourceTests.WholeSourceGone_IsAFailure_LastBackupNotRefreshed_FilesKept_AndComesBackCleanly(engine: "RESTIC") | 2 | 0 | 0 |
| OnlineBackup.Tests.UnreadableComponentTests.ALockedFile_IsAnError_NotADeletion_TheOthersRestoreIdentical_AndItIsSentOnceFree | 2 | 0 | 0 |
| OnlineBackup.Tests.UnreadableComponentTests.EverySourceGone_IsAFailure_NothingDeleted_TheIndexKeepsEveryFile_AndTheSourceComesBackWithoutResending | 2 | 0 | 0 |
| OnlineBackup.Tests.UnreadableComponentTests.OneOfTwoSourcesGone_IsSuccessWithError_NamesIt_ItsFilesAreNotDeleted | 2 | 0 | 0 |
