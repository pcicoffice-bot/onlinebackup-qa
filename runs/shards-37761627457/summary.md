# windows-latest, select 'OnlineBackup.Tests.PilotVss', 1 shards x 2 - run 37761627457 (3e288a047fedbe80a8a688a6e573ce3b45e740c0)

**15 tests: 11 PASS, 4 FAIL, 0 NOT TESTED** (a test is PASS only when every job that had to run it reports PASS)

Build SHA-256: `f164df359bb49e1e39cae8385cf7630ff32e2c2ebf41a2f6254271e8e7f0437f` (every shard checks it before running). Mode: strict - NOT green.

| job | state | tests | runner | minutes |
|---|---|---|---|---|
| s01-r01 | results | 15 | GitHub Actions 1000001760 | 2 |
| s01-r02 | results | 15 | GitHub Actions 1000001761 | 2 |

## Not PASS

| test | verdict | per job |
|---|---|---|
| OnlineBackup.Tests.PilotVssChallengeComponentTests.TwoSetsOnTheSameVolumeAtTheSameMoment_EachBacksUpItsHeldFile_RestoresIdentical_NoShadowCopyLeft | FAIL | 0 PASS / 2 FAIL / 0 NOT TESTED of 2 - s01-r01: set 2: BS_STOP_SUCCESS_WITH_ERROR 1791454344463,start,,0,,,,0 1791454344463,info,,0,"Start [ Microsoft Windows NT 10.0.26100.0 (runnervmfi6oq), OnlineBackup Age; s01-r02: set 1: BS_STOP_SUCCESS_WITH_ERROR 1791454340766,start,,0,,,,0 1791454340766,info,,0,"Start [ Microsoft Windows NT 10.0.26100.0 (runnervmfi6oq), OnlineBackup Age |
| OnlineBackup.Tests.PilotVssComponentTests.AFileRewrittenAfterTheSnapshot_ThePointHoldsTheSnapshotContent_AndTheNextRunSendsTheNewContent | FAIL | 0 PASS / 2 FAIL / 0 NOT TESTED of 2 - s01-r01: the next run did not send the content written after the snapshot (updated=0, new=0, perm=0); s01-r02: the next run did not send the content written after the snapshot (updated=0, new=0, perm=0) |
| OnlineBackup.Tests.PilotVssEndToEndTests.TheAgentKilledWhileItsSnapshotExists_TheNextRunLeavesNoShadowCopy_AndRestoresIdentical | FAIL | 0 PASS / 2 FAIL / 0 NOT TESTED of 2 - s01-r01: the shadow copy of the killed run is still on the volume after the next run: {6D82649A-3AB6-4CBD-BB28-6E1C3413C115}, {2AC6F7D2-5120-4B41-B8AD-E5AEF8FA1F6E}, {6F; s01-r02: the shadow copy of the killed run is still on the volume after the next run: {AE6FD4EE-176C-4846-AB42-FE9E4490E4B9}, {11350ACE-2411-4DD9-AF2C-3C5133C28E4C}, {6F |
| OnlineBackup.Tests.PilotVssIntegrationTests.AFileWrittenDuringTheRun_ThePointHoldsTheSnapshotContent_TheNextRunSendsTheNewContent | FAIL | 0 PASS / 2 FAIL / 0 NOT TESTED of 2 - s01-r01: the next run did not send the content written during the run (updated=0, new=0, perm=0); s01-r02: the next run did not send the content written during the run (updated=0, new=0, perm=0) |

## Repetitions

| test | PASS | FAIL | NOT TESTED |
|---|---|---|---|
| OnlineBackup.Tests.PilotVssChallengeComponentTests.ASourceOnASubstDrive_EitherIsReadThroughASnapshot_OrTheRunSaysItHadNone_NothingIsLeftBehind | 2 | 0 | 0 |
| OnlineBackup.Tests.PilotVssChallengeComponentTests.TheShadowCopyServiceIsDisabled_TheRunSaysSo_IsNotAPlainSuccess_TheOtherFilesRestoreIdentical_AndOnceBackTheNextRunCompletes | 2 | 0 | 0 |
| OnlineBackup.Tests.PilotVssChallengeComponentTests.TwoSetsOnTheSameVolumeAtTheSameMoment_EachBacksUpItsHeldFile_RestoresIdentical_NoShadowCopyLeft | 0 | 2 | 0 |
| OnlineBackup.Tests.PilotVssComponentTests.ABackupWithShadowCopy_SendsALockedFile_IsASuccessWithoutError_RestoresIdentical_AndLeavesNoShadowCopy | 2 | 0 | 0 |
| OnlineBackup.Tests.PilotVssComponentTests.ADriveLetterThatDoesNotExist_GetsNoShadowCopy_AWarningNamesIt_NothingIsLeftBehind | 2 | 0 | 0 |
| OnlineBackup.Tests.PilotVssComponentTests.AFileRewrittenAfterTheSnapshot_ThePointHoldsTheSnapshotContent_AndTheNextRunSendsTheNewContent | 0 | 2 | 0 |
| OnlineBackup.Tests.PilotVssComponentTests.ALockedFile_IsReadFromTheSnapshot_WithItsContentAtSnapshotTime_AndTheSnapshotIsRemovedAfterwards | 2 | 0 | 0 |
| OnlineBackup.Tests.PilotVssComponentTests.SourcesWithoutADriveLetter_GetNoShadowCopy_AndNoWarning | 2 | 0 | 0 |
| OnlineBackup.Tests.PilotVssComponentTests.WhenTheRunFails_TheShadowCopyIsRemovedToo_AndTheNextRunCompletes | 2 | 0 | 0 |
| OnlineBackup.Tests.PilotVssEndToEndTests.TheAgentKilledWhileItsSnapshotExists_TheNextRunLeavesNoShadowCopy_AndRestoresIdentical | 0 | 2 | 0 |
| OnlineBackup.Tests.PilotVssEndToEndTests.TheShippedAgent_BacksUpAFileHeldOpenByAnotherProgram_RestoresIdentical_SendsItsChange_ThenNothing_NoShadowCopyLeft | 2 | 0 | 0 |
| OnlineBackup.Tests.PilotVssFailureIntegrationTests.TheShadowCopyServiceIsDisabled_TheServerRecordsTheRunAsNotAPlainSuccess_WithTheReason_TheReadableFilesRestoreIdentical | 2 | 0 | 0 |
| OnlineBackup.Tests.PilotVssIntegrationTests.AFileWrittenDuringTheRun_ThePointHoldsTheSnapshotContent_TheNextRunSendsTheNewContent | 0 | 2 | 0 |
| OnlineBackup.Tests.PilotVssIntegrationTests.ALockedFile_IsBackedUpThroughTheShadowCopy_RestoresIdentical_NoShadowCopyLeft | 2 | 0 | 0 |
| OnlineBackup.Tests.PilotVssIntegrationTests.ARunTheServerStopsForTheQuota_RemovesItsShadowCopy_AndOnceThereIsRoom_TheNextRunCompletes | 2 | 0 | 0 |
