# QA shards run 37650865595 (fcea2a623c3273177db8604274b008b345ae8159; windows-latest, select 'InterruptedRunReportedByManyPathsAtOnce AtomicSharedReadComponentTests AStateFile_IsNeverSeenHalfWritten IsTemp', 3 shards x 5, serial control)

**6 tests: 4 PASS, 2 FAIL, 0 NOT TESTED** (a test is PASS only when every job that had to run it reports PASS)

| job | state | tests | runner | minutes |
|---|---|---|---|---|
| s01-r01 | results | 4 | GitHub Actions 1000001358 | 1 |
| s01-r02 | results | 4 | GitHub Actions 1000001357 | 1 |
| s01-r03 | results | 4 | GitHub Actions 1000001361 | 1 |
| s01-r04 | results | 4 | GitHub Actions 1000001362 | 1 |
| s01-r05 | results | 4 | GitHub Actions 1000001356 | 1 |
| s02-r01 | results | 1 | GitHub Actions 1000001360 | 1 |
| s02-r02 | results | 1 | GitHub Actions 1000001364 | 1 |
| s02-r03 | results | 1 | GitHub Actions 1000001365 | 1 |
| s02-r04 | results | 1 | GitHub Actions 1000001366 | 1 |
| s02-r05 | results | 1 | GitHub Actions 1000001367 | 1 |
| s03-r01 | results | 1 | GitHub Actions 1000001368 | 1 |
| s03-r02 | results | 1 | GitHub Actions 1000001369 | 1 |
| s03-r03 | results | 1 | GitHub Actions 1000001370 | 1 |
| s03-r04 | results | 1 | GitHub Actions 1000001371 | 1 |
| s03-r05 | results | 1 | GitHub Actions 1000001372 | 1 |

## Not PASS

| test | verdict | per job |
|---|---|---|
| OnlineBackup.Tests.AtomicSharedReadComponentTests.WhileTheProductsOwnReaderReadsWithoutPause_EveryWriteSucceeds_AndNoHalfFileIsSeen | FAIL | 3 PASS / 2 FAIL / 0 NOT TESTED of 5 - s01-r01: files left in the folder: state.txt (1048576 bytes), state.txt.old7ec79aee (1048576 bytes); s01-r05: files left in the folder: state.txt (1048576 bytes), state.txt.old7fa57b82 (1048576 bytes) |
| OnlineBackup.Tests.InterruptionTests.InterruptedRunReportedByManyPathsAtOnce_IsRecordedExactlyOnce | FAIL | 4 PASS / 1 FAIL / 0 NOT TESTED of 5 - s03-r05: 1 of 16 calls failed: Unknown device. server System log: |

## Repetitions

| test | PASS | FAIL | NOT TESTED |
|---|---|---|---|
| OnlineBackup.Tests.AtomicSharedReadComponentTests.AnotherProgramThatHoldsTheFileForAMoment_DelaysTheWrite_DoesNotFailIt | 5 | 0 | 0 |
| OnlineBackup.Tests.AtomicSharedReadComponentTests.TheProductReadsItsOwnFiles_OnlyThroughTheSharedReaders | 5 | 0 | 0 |
| OnlineBackup.Tests.AtomicSharedReadComponentTests.TheSharedReads_GiveExactlyWhatFileReadAllGives | 5 | 0 | 0 |
| OnlineBackup.Tests.AtomicSharedReadComponentTests.WhileTheProductsOwnReaderReadsWithoutPause_EveryWriteSucceeds_AndNoHalfFileIsSeen | 3 | 2 | 0 |
| OnlineBackup.Tests.CoreFormatsTests.AStateFile_IsNeverSeenHalfWritten_AndTwoWritersNeverFail | 5 | 0 | 0 |
| OnlineBackup.Tests.InterruptionTests.InterruptedRunReportedByManyPathsAtOnce_IsRecordedExactlyOnce | 4 | 1 | 0 |

## Serial control vs shards

**DIFFERENT in 1 tests** - the sharded run is NOT proven equal to the serial run:
- OnlineBackup.Tests.InterruptionTests.InterruptedRunReportedByManyPathsAtOnce_IsRecordedExactlyOnce: serial PASS, sharded FAIL
