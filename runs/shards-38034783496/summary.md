# QA shards run 38034783496 (c8141553731653edc613b8d2d64057c0ab68ed35; windows-latest, select 'TheFileNeverDisappears WhileTheProductsOwnReaderReadsWithoutPause', 2 shards x 3)

**2 tests: 1 PASS, 1 FAIL, 0 NOT TESTED** (a test is PASS only when every job that had to run it reports PASS)

| job | state | tests | runner | minutes |
|---|---|---|---|---|
| s01-r01 | results | 2 | GitHub Actions 1000002226 | 1 |
| s01-r02 | results | 2 | GitHub Actions 1000002227 | 1 |
| s01-r03 | results | 2 | GitHub Actions 1000002260 | 1 |

## Not PASS

| test | verdict | per job |
|---|---|---|
| OnlineBackup.Tests.AtomicSharedReadComponentTests.WhileReadersHoldIt_TheFileNeverDisappears_DuringAReplace | FAIL | 0 PASS / 3 FAIL / 0 NOT TESTED of 3 - s01-r01: the file did not exist 782 times of 75978 checks during 200 replaces; s01-r02: the file did not exist 762 times of 49334 checks during 200 replaces; s01-r03: the file did not exist 676 times of 59426 checks during 200 replaces |

## Repetitions

| test | PASS | FAIL | NOT TESTED |
|---|---|---|---|
| OnlineBackup.Tests.AtomicSharedReadComponentTests.WhileReadersHoldIt_TheFileNeverDisappears_DuringAReplace | 0 | 3 | 0 |
| OnlineBackup.Tests.AtomicSharedReadComponentTests.WhileTheProductsOwnReaderReadsWithoutPause_EveryWriteSucceeds_AndNoHalfFileIsSeen | 3 | 0 | 0 |
