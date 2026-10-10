# QA shards run 38034780840 (7879709280d4e9981978db246cba558de2e45797; windows-latest, select 'InterruptedRunReportedByManyPathsAtOnce AtomicSharedReadComponentTests AStateFile_IsNeverSeenHalfWritten', 3 shards x 3, serial control)

**5 tests: 3 PASS, 2 FAIL, 0 NOT TESTED** (a test is PASS only when every job that had to run it reports PASS)

| job | state | tests | runner | minutes |
|---|---|---|---|---|
| s01-r01 | results | 3 | GitHub Actions 1000002386 | 1 |
| s01-r02 | results | 3 | GitHub Actions 1000002411 | 1 |
| s01-r03 | results | 3 | GitHub Actions 1000002402 | 1 |
| s02-r01 | results | 1 | GitHub Actions 1000002440 | 2 |
| s02-r02 | results | 1 | GitHub Actions 1000002450 | 1 |
| s02-r03 | results | 1 | GitHub Actions 1000002457 | 1 |
| s03-r01 | results | 1 | GitHub Actions 1000002470 | 1 |
| s03-r02 | results | 1 | GitHub Actions 1000002552 | 1 |
| s03-r03 | results | 1 | GitHub Actions 1000002554 | 1 |

## Not PASS

| test | verdict | per job |
|---|---|---|
| OnlineBackup.Tests.CoreFormatsTests.AStateFile_IsNeverSeenHalfWritten_AndTwoWritersNeverFail | FAIL | 1 PASS / 2 FAIL / 0 NOT TESTED of 3 - s02-r02: System.IO.IOException : The process cannot access the file because it is being used by another process.; s02-r03: System.IO.IOException : The process cannot access the file because it is being used by another process. |
| OnlineBackup.Tests.InterruptionTests.InterruptedRunReportedByManyPathsAtOnce_IsRecordedExactlyOnce | FAIL | 2 PASS / 1 FAIL / 0 NOT TESTED of 3 - s03-r03: 7 of 16 calls failed: Server error. The details are in the system log. server System log: 2026-10-10 11:36:20	::1	error: POST /api/sets/1791632179516/interrupte |

## Repetitions

| test | PASS | FAIL | NOT TESTED |
|---|---|---|---|
| OnlineBackup.Tests.AtomicSharedReadComponentTests.AnotherProgramThatHoldsTheFileForAMoment_DelaysTheWrite_DoesNotFailIt | 3 | 0 | 0 |
| OnlineBackup.Tests.AtomicSharedReadComponentTests.TheSharedReads_GiveExactlyWhatFileReadAllGives | 3 | 0 | 0 |
| OnlineBackup.Tests.AtomicSharedReadComponentTests.WhileTheProductsOwnReaderReadsWithoutPause_EveryWriteSucceeds_AndNoHalfFileIsSeen | 3 | 0 | 0 |
| OnlineBackup.Tests.CoreFormatsTests.AStateFile_IsNeverSeenHalfWritten_AndTwoWritersNeverFail | 1 | 2 | 0 |
| OnlineBackup.Tests.InterruptionTests.InterruptedRunReportedByManyPathsAtOnce_IsRecordedExactlyOnce | 2 | 1 | 0 |

## Serial control vs shards

**DIFFERENT in 1 tests** - the sharded run is NOT proven equal to the serial run:
- OnlineBackup.Tests.InterruptionTests.InterruptedRunReportedByManyPathsAtOnce_IsRecordedExactlyOnce: serial PASS, sharded FAIL
