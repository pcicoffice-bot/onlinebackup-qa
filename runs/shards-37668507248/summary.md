# ubuntu-latest, select '', 4 shards x 1, serial control - run 37668507248 (95c3867692d34135b6c2cdba748b714fda1eb026)

**473 tests: 464 PASS, 1 FAIL, 8 NOT TESTED** (a test is PASS only when every job that had to run it reports PASS)

Build SHA-256: `bcc259de8c470dfd1d83df091d2d0b10af9b009c1b4c0c0c6f3f0497edb358ff` (every shard checks it before running). Mode: strict - NOT green.

| job | state | tests | runner | minutes |
|---|---|---|---|---|
| s01-r01 | results | 71 | GitHub Actions 1000001451 | 17 |
| s02-r01 | results | 121 | GitHub Actions 1000001449 | 14 |
| s03-r01 | results | 152 | GitHub Actions 1000001450 | 16 |
| s04-r01 | results | 129 | GitHub Actions 1000001448 | 14 |

## Not PASS

| test | verdict | per job |
|---|---|---|
| OnlineBackup.Tests.NightP_RestoreChainTests.CaseOnlyRename_LaterPointsRestoreTheNewSpelling | FAIL | 0 PASS / 1 FAIL / 0 NOT TESTED of 1 - s01-r01: case point 2026-10-07-18-44-01 (run 1): failed=0 missing REPORTS/report.txt; extra Reports/Report.txt |
| OnlineBackup.Tests.ReadOnlyStoreComponentTests.AReadOnlyStore_StillListsItsPoints_AndServesItsObjects | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s01-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: a bind mount is not allowed here (needs root) |
| OnlineBackup.Tests.LoadTests.BigServer_500Customers_5000Sets_PagesAndComputersStayFast | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s02-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: the load run is its own step (OB_LOAD=1; the gate runs it in job tests-extra) |
| OnlineBackup.Tests.EndToEndTests.Net40AgentUnderMonoBacksUpAndRestores | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s03-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: needs mono and the .NET 4.0 agent build |
| OnlineBackup.Tests.RetentionZoneComponentTests.TheLastBackupOfAMonth_IsTheCustomersMonth_NotUtcs | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s03-r01: [declared] outcome NotExecuted: NEEDS OWNER DECISION (bug 77, docs/PRODUCT-BENCHMARK.md): GFS day/month boundaries in the server time zone or UTC |
| OnlineBackup.Tests.TlsTests.Net40AgentUnderMonoUsesTheBuiltinTls | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s03-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: needs mono and the .NET 4.0 agent build |
| OnlineBackup.Tests.UpgradeTests.MakeFixture_WhenAsked | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s03-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: a tool that makes the upgrade fixture on demand (OB_MAKE_UPGRADE_FIXTURE), not a test |
| OnlineBackup.Tests.ReliabilityTests.ServerDiskFullMidBackup_ExistingBackupsUnharmed_NextRunAfterSpaceIsFreedCompletes | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s04-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: needs restic and OB_ALLOW_MOUNT=1 (a small disk is mounted to fill it) |
| OnlineBackup.Tests.SqlSimpleRecoveryLogTests.LogMode_AllDatabases_ASimpleRecoveryDatabaseIsSkipped_NotAnErrorEveryRun | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s04-r01: [declared] outcome NotExecuted: NEEDS OWNER DECISION (bug 76, docs/PRODUCT-BENCHMARK.md): skip, warn or fail a SIMPLE-recovery database in log mode |

## Control (the previous method) vs shards

**SAME** - every one of 473 tests has the same result in the control run and sharded
