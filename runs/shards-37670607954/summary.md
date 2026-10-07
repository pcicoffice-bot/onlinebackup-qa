# windows-latest, select 'WebRestore ResticPathComponentTests AiTests.WebRestoreSearch', 4 shards x 1 - run 37670607954 (9a3fd4a6cf403f5988e2dc5168444ffea8dbfe4c)

**15 tests: 13 PASS, 1 FAIL, 1 NOT TESTED** (a test is PASS only when every job that had to run it reports PASS)

Build SHA-256: `dc3a201acf1e7045b4e563b440317626162cf1dbef07df938ccd8c1ea2a719fe` (every shard checks it before running). Mode: strict - NOT green.

| job | state | tests | runner | minutes |
|---|---|---|---|---|
| s01-r01 | results | 11 | GitHub Actions 1000001484 | 1 |
| s02-r01 | results | 1 | GitHub Actions 1000001485 | 1 |
| s03-r01 | results | 1 | GitHub Actions 1000001486 | 1 |
| s04-r01 | results | 2 | GitHub Actions 1000001487 | 1 |

## Not PASS

| test | verdict | per job |
|---|---|---|
| OnlineBackup.Tests.ProcessTests.HungWebRestore_IsStopped_WithAClearTimeout | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s04-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: needs Linux (the stand-in programs are shell scripts) |
| OnlineBackup.Tests.WebRestoreTests.CustomerRestoresChosenFilesFromTheWebsite_WithTheEncryptionPassword | FAIL | 0 PASS / 1 FAIL / 0 NOT TESTED of 1 - s04-r01: Assert.Empty() Failure: Collection was not empty Collection: ["C:\\Users\\runneradmin\\AppData\\Local\\Temp\\obte"···, "C:\\Users\\runneradmin\\AppData\\Local\ |
