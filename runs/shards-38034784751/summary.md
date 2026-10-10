# QA shards run 38034784751 (4fc56c4412ee43f8dc4e3ab7a9f3ef0a692ac99d; windows-latest, select 'AtomicSharedReadComponentTests AtomicReplaceRefused CoreFormatsTests InterruptionTests', 4 shards x 5, serial control)

**23 tests: 23 PASS, 0 FAIL, 0 NOT TESTED** (a test is PASS only when every job that had to run it reports PASS)

| job | state | tests | runner | minutes |
|---|---|---|---|---|
| s01-r01 | results | 13 | GitHub Actions 1000002360 | 4 |
| s01-r02 | results | 13 | GitHub Actions 1000002559 | 4 |
| s01-r03 | results | 13 | GitHub Actions 1000002279 | 4 |
| s01-r04 | results | 13 | GitHub Actions 1000002555 | 4 |
| s01-r05 | results | 13 | GitHub Actions 1000002530 | 4 |
| s02-r01 | results | 5 | GitHub Actions 1000002584 | 1 |
| s02-r02 | results | 5 | GitHub Actions 1000002557 | 1 |
| s02-r03 | results | 5 | GitHub Actions 1000002361 | 1 |
| s02-r04 | results | 5 | GitHub Actions 1000002396 | 1 |
| s02-r05 | results | 5 | GitHub Actions 1000002513 | 1 |
| s03-r01 | results | 3 | GitHub Actions 1000002585 | 1 |
| s03-r02 | results | 3 | GitHub Actions 1000002588 | 1 |
| s03-r03 | results | 3 | GitHub Actions 1000002589 | 1 |
| s03-r04 | results | 3 | GitHub Actions 1000002591 | 1 |
| s03-r05 | results | 3 | GitHub Actions 1000002596 | 1 |
| s04-r01 | results | 2 | GitHub Actions 1000002598 | 1 |
| s04-r02 | results | 2 | GitHub Actions 1000002599 | 1 |
| s04-r03 | results | 2 | GitHub Actions 1000002600 | 1 |
| s04-r04 | results | 2 | GitHub Actions 1000002602 | 1 |
| s04-r05 | results | 2 | GitHub Actions 1000002603 | 1 |

## Repetitions

| test | PASS | FAIL | NOT TESTED |
|---|---|---|---|
| OnlineBackup.Tests.InterruptionTests.AgentKilledMidBackup_NextBackupRunsAtOnce_HistoryShowsTheFailure_NotRunning(engine: "") | 5 | 0 | 0 |
| OnlineBackup.Tests.InterruptionTests.AgentKilledMidBackup_NextBackupRunsAtOnce_HistoryShowsTheFailure_NotRunning(engine: "RESTIC") | 5 | 0 | 0 |
| OnlineBackup.Tests.InterruptionTests.BeginSentTwice_GivesTheSameRun_NoFalseFailure_NoOrphan | 5 | 0 | 0 |
| OnlineBackup.Tests.InterruptionTests.ComputerNeverComesBack_RunIsClosedAsInterrupted_AndAnotherProcessCanBackUp | 5 | 0 | 0 |
| OnlineBackup.Tests.InterruptionTests.EndOfRunSentTwice_IsRecordedOnce_AndTheRepeatIsNotAnError(how: "abort") | 5 | 0 | 0 |
| OnlineBackup.Tests.InterruptionTests.EndOfRunSentTwice_IsRecordedOnce_AndTheRepeatIsNotAnError(how: "commit") | 5 | 0 | 0 |
| OnlineBackup.Tests.InterruptionTests.EndOfRunSentTwice_IsRecordedOnce_AndTheRepeatIsNotAnError(how: "resticreport") | 5 | 0 | 0 |
| OnlineBackup.Tests.InterruptionTests.InterruptedRunReportedByManyPathsAtOnce_IsRecordedExactlyOnce | 5 | 0 | 0 |
| OnlineBackup.Tests.InterruptionTests.LiveBackupThatKeepsReporting_IsNotClosedBySweeper | 5 | 0 | 0 |
| OnlineBackup.Tests.InterruptionTests.ProgressThatArrivesAfterTheEnd_DoesNotBringTheRunBack(engine: "") | 5 | 0 | 0 |
| OnlineBackup.Tests.InterruptionTests.ProgressThatArrivesAfterTheEnd_DoesNotBringTheRunBack(engine: "RESTIC") | 5 | 0 | 0 |
| OnlineBackup.Tests.InterruptionTests.ServerDownMidBackupAndBack_NextBackupRunsAtOnce | 5 | 0 | 0 |
| OnlineBackup.Tests.InterruptionTests.TwoDifferentResticRunsWithTheSameName_AreBothRecorded | 5 | 0 | 0 |
| OnlineBackup.Tests.AtomicSharedReadComponentTests.AnotherProgramThatHoldsTheFileForAMoment_DelaysTheWrite_DoesNotFailIt | 5 | 0 | 0 |
| OnlineBackup.Tests.AtomicSharedReadComponentTests.TheProductReadsItsOwnFiles_OnlyThroughTheSharedReaders | 5 | 0 | 0 |
| OnlineBackup.Tests.AtomicSharedReadComponentTests.TheSharedReads_GiveExactlyWhatFileReadAllGives | 5 | 0 | 0 |
| OnlineBackup.Tests.AtomicSharedReadComponentTests.WhileReadersHoldIt_TheFileNeverDisappears_DuringAReplace | 5 | 0 | 0 |
| OnlineBackup.Tests.AtomicSharedReadComponentTests.WhileTheProductsOwnReaderReadsWithoutPause_EveryWriteSucceeds_AndNoHalfFileIsSeen | 5 | 0 | 0 |
| OnlineBackup.Tests.CoreFormatsTests.AMessage_ReadsBackExactly | 5 | 0 | 0 |
| OnlineBackup.Tests.CoreFormatsTests.AStateFile_IsNeverSeenHalfWritten_AndTwoWritersNeverFail | 5 | 0 | 0 |
| OnlineBackup.Tests.CoreFormatsTests.ATemporaryFile_IsKnownByItsName_NeverByItsPath | 5 | 0 | 0 |
| OnlineBackup.Tests.AtomicReplaceRefusedComponentTests.ARefusalThatEnds_TheWriteWaitsAndSucceeds | 5 | 0 | 0 |
| OnlineBackup.Tests.AtomicReplaceRefusedComponentTests.ARefusalThatNeverEnds_FailsLoudly_AndLeavesNoTemporaryFile | 5 | 0 | 0 |

## Serial control vs shards

**SAME** - every one of 23 tests has the same result serially and sharded
