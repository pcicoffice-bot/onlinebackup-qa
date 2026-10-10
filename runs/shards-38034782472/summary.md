# QA shards run 38034782472 (b868a137b6b00ec477a4cc609c8b24cd0af2df2c; windows-latest, select 'InterruptedRunReportedByManyPathsAtOnce AtomicSharedReadComponentTests AStateFile_IsNeverSeenHalfWritten IsTemp', 3 shards x 5, serial control)

**6 tests: 5 PASS, 1 FAIL, 0 NOT TESTED** (a test is PASS only when every job that had to run it reports PASS)

| job | state | tests | runner | minutes |
|---|---|---|---|---|
| s01-r01 | results | 4 | GitHub Actions 1000002395 | 1 |
| s01-r02 | results | 4 | GitHub Actions 1000002427 | 1 |
| s01-r03 | results | 4 | GitHub Actions 1000002333 | 1 |
| s01-r04 | results | 4 | GitHub Actions 1000002436 | 1 |
| s01-r05 | results | 4 | GitHub Actions 1000002483 | 1 |
| s02-r01 | results | 1 | GitHub Actions 1000002421 | 1 |
| s02-r02 | results | 1 | GitHub Actions 1000002492 | 1 |
| s02-r03 | results | 1 | GitHub Actions 1000002517 | 1 |
| s02-r04 | results | 1 | GitHub Actions 1000002538 | 1 |
| s02-r05 | results | 1 | GitHub Actions 1000002556 | 1 |
| s03-r01 | results | 1 | GitHub Actions 1000002569 | 1 |
| s03-r02 | results | 1 | GitHub Actions 1000002572 | 1 |
| s03-r03 | results | 1 | GitHub Actions 1000002582 | 1 |
| s03-r04 | results | 1 | GitHub Actions 1000002583 | 1 |
| s03-r05 | results | 1 | GitHub Actions 1000002587 | 1 |

## Not PASS

| test | verdict | per job |
|---|---|---|
| OnlineBackup.Tests.AtomicSharedReadComponentTests.WhileTheProductsOwnReaderReadsWithoutPause_EveryWriteSucceeds_AndNoHalfFileIsSeen | FAIL | 4 PASS / 1 FAIL / 0 NOT TESTED of 5 - s01-r02: files left in the folder: state.txt (1048576 bytes), state.txt.old159cfc79 (1048576 bytes), state.txt.oldc8670297 (1048576 bytes) |

## Repetitions

| test | PASS | FAIL | NOT TESTED |
|---|---|---|---|
| OnlineBackup.Tests.AtomicSharedReadComponentTests.AnotherProgramThatHoldsTheFileForAMoment_DelaysTheWrite_DoesNotFailIt | 5 | 0 | 0 |
| OnlineBackup.Tests.AtomicSharedReadComponentTests.TheProductReadsItsOwnFiles_OnlyThroughTheSharedReaders | 5 | 0 | 0 |
| OnlineBackup.Tests.AtomicSharedReadComponentTests.TheSharedReads_GiveExactlyWhatFileReadAllGives | 5 | 0 | 0 |
| OnlineBackup.Tests.AtomicSharedReadComponentTests.WhileTheProductsOwnReaderReadsWithoutPause_EveryWriteSucceeds_AndNoHalfFileIsSeen | 4 | 1 | 0 |
| OnlineBackup.Tests.CoreFormatsTests.AStateFile_IsNeverSeenHalfWritten_AndTwoWritersNeverFail | 5 | 0 | 0 |
| OnlineBackup.Tests.InterruptionTests.InterruptedRunReportedByManyPathsAtOnce_IsRecordedExactlyOnce | 5 | 0 | 0 |

## Serial control vs shards

**DIFFERENT in 1 tests** - the sharded run is NOT proven equal to the serial run:
- OnlineBackup.Tests.AtomicSharedReadComponentTests.WhileTheProductsOwnReaderReadsWithoutPause_EveryWriteSucceeds_AndNoHalfFileIsSeen: serial PASS, sharded FAIL
