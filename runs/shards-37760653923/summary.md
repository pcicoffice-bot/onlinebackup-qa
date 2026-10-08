# windows-latest, select 'OnlineBackup.Tests.PilotVssComponentTests OnlineBackup.Tests.PilotVssIntegrationTests', 1 shards x 2 - run 37760653923 (6a57bcf067d2fc87ac00c4395d6429d759fd579d)

**6 tests: 6 PASS, 0 FAIL, 0 NOT TESTED** (a test is PASS only when every job that had to run it reports PASS)

Build SHA-256: `4df8b8f9b5098f10b31a6eee7586980bc135d836b0163e6fdf8cc4cff6ab0231` (every shard checks it before running). Mode: strict - green.

| job | state | tests | runner | minutes |
|---|---|---|---|---|
| s01-r01 | results | 6 | GitHub Actions 1000001746 | 1 |
| s01-r02 | results | 6 | GitHub Actions 1000001747 | 1 |

## Repetitions

| test | PASS | FAIL | NOT TESTED |
|---|---|---|---|
| OnlineBackup.Tests.PilotVssComponentTests.ABackupWithShadowCopy_SendsALockedFile_IsASuccessWithoutError_RestoresIdentical_AndLeavesNoShadowCopy | 2 | 0 | 0 |
| OnlineBackup.Tests.PilotVssComponentTests.ALockedFile_IsReadFromTheSnapshot_WithItsContentAtSnapshotTime_AndTheSnapshotIsRemovedAfterwards | 2 | 0 | 0 |
| OnlineBackup.Tests.PilotVssComponentTests.SourcesWithoutADriveLetter_GetNoShadowCopy_AndNoWarning | 2 | 0 | 0 |
| OnlineBackup.Tests.PilotVssComponentTests.WhenTheRunFails_TheShadowCopyIsRemovedToo_AndTheNextRunCompletes | 2 | 0 | 0 |
| OnlineBackup.Tests.PilotVssIntegrationTests.ALockedFile_IsBackedUpThroughTheShadowCopy_RestoresIdentical_NoShadowCopyLeft | 2 | 0 | 0 |
| OnlineBackup.Tests.PilotVssIntegrationTests.ARunTheServerStopsForTheQuota_RemovesItsShadowCopy_AndOnceThereIsRoom_TheNextRunCompletes | 2 | 0 | 0 |
