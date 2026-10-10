# ubuntu-latest, select '', 4 shards x 1, serial control - run 38034786353 (f6b3eec466f3fd29b97361d79ebbd34aa0e49352)

**462 tests: 459 PASS, 1 FAIL, 2 NOT TESTED** (a test is PASS only when every job that had to run it reports PASS)

Build SHA-256: `45a7a3658888060728bf9cd591ccdf4eacb51c9ef965df65ccda90942737891a` (every shard checks it before running). Mode: strict - NOT green.

| job | state | tests | runner | minutes |
|---|---|---|---|---|
| s01-r01 | results | 116 | GitHub Actions 1000002345 | 15 |
| s02-r01 | results | 116 | GitHub Actions 1000002412 | 17 |
| s03-r01 | results | 115 | GitHub Actions 1000002454 | 22 |
| s04-r01 | results | 115 | GitHub Actions 1000002398 | 6 |

## Not PASS

| test | verdict | per job |
|---|---|---|
| OnlineBackup.Tests.RetentionZoneComponentTests.TheLastBackupOfAMonth_IsTheCustomersMonth_NotUtcs | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s01-r01: [declared] outcome NotExecuted: NEEDS OWNER DECISION (bug 77, docs/PRODUCT-BENCHMARK.md): GFS day/month boundaries in the server time zone or UTC |
| OnlineBackup.Tests.SqlSimpleRecoveryLogTests.LogMode_AllDatabases_ASimpleRecoveryDatabaseIsSkipped_NotAnErrorEveryRun | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s02-r01: [declared] outcome NotExecuted: NEEDS OWNER DECISION (bug 76, docs/PRODUCT-BENCHMARK.md): skip, warn or fail a SIMPLE-recovery database in log mode |
| OnlineBackup.Tests.NightP_RestoreChainTests.CaseOnlyRename_LaterPointsRestoreTheNewSpelling | FAIL | 0 PASS / 1 FAIL / 0 NOT TESTED of 1 - s03-r01: case point 2026-10-10-10-53-26 (run 1): failed=0 missing REPORTS/report.txt; extra Reports/Report.txt |

## Control (the previous method) vs shards

**SAME** - every one of 462 tests has the same result in the control run and sharded
