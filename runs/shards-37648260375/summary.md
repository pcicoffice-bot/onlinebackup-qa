# QA shards run 37648260375 (5f8d9cfcb76524c4b7a8bd090d33e12e344f9ba8; windows-latest, select 'InterruptedRunReportedByManyPathsAtOnce AtomicSharedReadComponentTests AStateFile_IsNeverSeenHalfWritten', 3 shards x 3, serial control)

**5 tests: 3 PASS, 2 FAIL, 0 NOT TESTED** (a test is PASS only when every job that had to run it reports PASS)

| job | state | tests | runner | minutes |
|---|---|---|---|---|
| s01-r01 | results | 3 | GitHub Actions 1000001343 | 1 |
| s01-r02 | results | 3 | GitHub Actions 1000001341 | 1 |
| s01-r03 | results | 3 | GitHub Actions 1000001342 | 1 |
| s02-r01 | results | 1 | GitHub Actions 1000001340 | 1 |
| s02-r02 | results | 1 | GitHub Actions 1000001349 | 1 |
| s02-r03 | results | 1 | GitHub Actions 1000001350 | 1 |
| s03-r01 | results | 1 | GitHub Actions 1000001351 | 1 |
| s03-r02 | results | 1 | GitHub Actions 1000001352 | 1 |
| s03-r03 | results | 1 | GitHub Actions 1000001353 | 1 |

## Not PASS

| test | verdict | per job |
|---|---|---|
| OnlineBackup.Tests.AtomicSharedReadComponentTests.WhileTheProductsOwnReaderReadsWithoutPause_EveryWriteSucceeds_AndNoHalfFileIsSeen | FAIL | 2 PASS / 1 FAIL / 0 NOT TESTED of 3 - s01-r03: files left in the folder: state.txt (1048576 bytes), state.txt~RF3d1e8.TMP (1048576 bytes) |
| OnlineBackup.Tests.CoreFormatsTests.AStateFile_IsNeverSeenHalfWritten_AndTwoWritersNeverFail | FAIL | 0 PASS / 3 FAIL / 0 NOT TESTED of 3 - s02-r01: System.IO.IOException : The process cannot access the file because it is being used by another process.; s02-r02: System.IO.IOException : The process cannot access the file because it is being used by another process.; s02-r03: System.IO.IOException : The process cannot access the file because it is being used by another process. |

## Repetitions

| test | PASS | FAIL | NOT TESTED |
|---|---|---|---|
| OnlineBackup.Tests.AtomicSharedReadComponentTests.AnotherProgramThatHoldsTheFileForAMoment_DelaysTheWrite_DoesNotFailIt | 3 | 0 | 0 |
| OnlineBackup.Tests.AtomicSharedReadComponentTests.TheSharedReads_GiveExactlyWhatFileReadAllGives | 3 | 0 | 0 |
| OnlineBackup.Tests.AtomicSharedReadComponentTests.WhileTheProductsOwnReaderReadsWithoutPause_EveryWriteSucceeds_AndNoHalfFileIsSeen | 2 | 1 | 0 |
| OnlineBackup.Tests.CoreFormatsTests.AStateFile_IsNeverSeenHalfWritten_AndTwoWritersNeverFail | 0 | 3 | 0 |
| OnlineBackup.Tests.InterruptionTests.InterruptedRunReportedByManyPathsAtOnce_IsRecordedExactlyOnce | 3 | 0 | 0 |

## Serial control vs shards

**DIFFERENT in 1 tests** - the sharded run is NOT proven equal to the serial run:
- OnlineBackup.Tests.InterruptionTests.InterruptedRunReportedByManyPathsAtOnce_IsRecordedExactlyOnce: serial FAIL, sharded PASS
