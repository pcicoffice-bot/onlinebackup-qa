# windows-latest, select 'OnlineBackup.Tests.AuditH_ComponentTests OnlineBackup.Tests.StopProbeComponentTests OnlineBackup.Tests.InterruptionTests', 1 shards x 2 - run 37721914538 (529a5e6f569c01c49b7c016646a3bd6c9da0a666)

**22 tests: 22 PASS, 0 FAIL, 0 NOT TESTED** (a test is PASS only when every job that had to run it reports PASS)

Build SHA-256: `80546955312e190150ecfe265302088ad4297f7ca16bb25f1ee17ac246f40f60` (every shard checks it before running). Mode: strict - green.

| job | state | tests | runner | minutes |
|---|---|---|---|---|
| s01-r01 | results | 22 | GitHub Actions 1000001674 | 4 |
| s01-r02 | results | 22 | GitHub Actions 1000001675 | 5 |

6 test(s) of this build are not yet in tools/qa-shards/known-tests.txt (add them so that a later disappearance is caught): OnlineBackup.Tests.AuditH_ComponentTests.AdminSignIn_OnASlowDisk_UnknownName_StillTakesAboutAsLongAsAWrongPassword, OnlineBackup.Tests.AuditH_ComponentTests.SignIn_OnASlowDisk_UnknownName_StillTakesAboutAsLongAsAWrongPassword, OnlineBackup.Tests.StopProbeComponentTests.AFailedCall_DoesNotPauseAfterItsLastTry, OnlineBackup.Tests.StopProbeComponentTests.AStopSetOnTheServer_IsStillSeen_ByTheQuickQuestion, OnlineBackup.Tests.StopProbeComponentTests.ServerGone_TheQuestionIsAnsweredAtOnce_GoOn, OnlineBackup.Tests.StopProbeComponentTests.ServerHung_TheQuestionIsAnsweredWithinItsLimit_GoOn

## Repetitions

| test | PASS | FAIL | NOT TESTED |
|---|---|---|---|
| OnlineBackup.Tests.AuditH_ComponentTests.AdminSignIn_OnASlowDisk_UnknownName_StillTakesAboutAsLongAsAWrongPassword | 2 | 0 | 0 |
| OnlineBackup.Tests.AuditH_ComponentTests.Keys_PasswordRandomAndCustom_WrongKeyRefused_CheckValueDiffers_EveryChangedByteDetected | 2 | 0 | 0 |
| OnlineBackup.Tests.AuditH_ComponentTests.SignIn_OnASlowDisk_UnknownName_StillTakesAboutAsLongAsAWrongPassword | 2 | 0 | 0 |
| OnlineBackup.Tests.AuditH_ComponentTests.SignIn_UnknownName_TakesAboutAsLongAsAWrongPassword_NoTimingOracle | 2 | 0 | 0 |
| OnlineBackup.Tests.AuditH_ComponentTests.SystemTls_WithAPin_RefusesAnotherCertificate_EvenOneTheSystemTrusts | 2 | 0 | 0 |
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
| OnlineBackup.Tests.StopProbeComponentTests.AFailedCall_DoesNotPauseAfterItsLastTry | 2 | 0 | 0 |
| OnlineBackup.Tests.StopProbeComponentTests.AStopSetOnTheServer_IsStillSeen_ByTheQuickQuestion | 2 | 0 | 0 |
| OnlineBackup.Tests.StopProbeComponentTests.ServerGone_TheQuestionIsAnsweredAtOnce_GoOn | 2 | 0 | 0 |
| OnlineBackup.Tests.StopProbeComponentTests.ServerHung_TheQuestionIsAnsweredWithinItsLimit_GoOn | 2 | 0 | 0 |
