# windows-latest, select 'WebRestore ResticPathComponentTests AiTests.WebRestoreSearch', 4 shards x 1 - run 38034797731 (c99b77715a2cd16e24fbaa1d6e6ee05f320d1a98)

**15 tests: 13 PASS, 1 FAIL, 1 NOT TESTED** (a test is PASS only when every job that had to run it reports PASS)

Build SHA-256: `dcda77944c9f4b0e811bb86050a8061d336c881efb19a2b06cedde51e3dcbfed` (every shard checks it before running). Mode: strict - NOT green.

| job | state | tests | runner | minutes |
|---|---|---|---|---|
| s01-r01 | results | 11 | GitHub Actions 1000002507 | 1 |
| s02-r01 | results | 1 | GitHub Actions 1000002511 | 1 |
| s03-r01 | results | 1 | GitHub Actions 1000002387 | 1 |
| s04-r01 | results | 2 | GitHub Actions 1000002466 | 1 |

## Not PASS

| test | verdict | per job |
|---|---|---|
| OnlineBackup.Tests.ProcessTests.HungWebRestore_IsStopped_WithAClearTimeout | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s04-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: needs Linux (the stand-in programs are shell scripts) |
| OnlineBackup.Tests.WebRestoreTests.CustomerRestoresChosenFilesFromTheWebsite_WithTheEncryptionPassword | FAIL | 0 PASS / 1 FAIL / 0 NOT TESTED of 1 - s04-r01: Assert.Empty() Failure: Collection was not empty Collection: ["C:\\Users\\runneradmin\\AppData\\Local\\Temp\\obte"···, "C:\\Users\\runneradmin\\AppData\\Local\ |
