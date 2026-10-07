# QA shards run 37652457968 (e394618d620a7996f7de55853925e10c3064d60e; windows-latest, select 'TheFileNeverDisappears WhileTheProductsOwnReaderReadsWithoutPause', 2 shards x 3)

**2 tests: 0 PASS, 2 FAIL, 0 NOT TESTED** (a test is PASS only when every job that had to run it reports PASS)

| job | state | tests | runner | minutes |
|---|---|---|---|---|
| s01-r01 | results | 2 | GitHub Actions 1000001376 | 1 |
| s01-r02 | results | 2 | GitHub Actions 1000001378 | 1 |
| s01-r03 | results | 2 | GitHub Actions 1000001377 | 1 |

## Not PASS

| test | verdict | per job |
|---|---|---|
| OnlineBackup.Tests.AtomicSharedReadComponentTests.WhileReadersHoldIt_TheFileNeverDisappears_DuringAReplace | FAIL | 0 PASS / 3 FAIL / 0 NOT TESTED of 3 - s01-r01: the file did not exist 1278 times of 116407 checks during 200 replaces; s01-r02: the file did not exist 1042 times of 149114 checks during 200 replaces; s01-r03: the file did not exist 624 times of 84130 checks during 200 replaces |
| OnlineBackup.Tests.AtomicSharedReadComponentTests.WhileTheProductsOwnReaderReadsWithoutPause_EveryWriteSucceeds_AndNoHalfFileIsSeen | FAIL | 2 PASS / 1 FAIL / 0 NOT TESTED of 3 - s01-r01: files left in the folder: state.txt (1048576 bytes), state.txt.oldf9f3909f (1048576 bytes) |

## Repetitions

| test | PASS | FAIL | NOT TESTED |
|---|---|---|---|
| OnlineBackup.Tests.AtomicSharedReadComponentTests.WhileReadersHoldIt_TheFileNeverDisappears_DuringAReplace | 0 | 3 | 0 |
| OnlineBackup.Tests.AtomicSharedReadComponentTests.WhileTheProductsOwnReaderReadsWithoutPause_EveryWriteSucceeds_AndNoHalfFileIsSeen | 2 | 1 | 0 |
