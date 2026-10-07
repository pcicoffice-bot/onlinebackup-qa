# ubuntu-latest, select '', 4 shards x 1 - run 37678884526 (038b0ed9d337ff9d0b082f2d69b9e238ebe21c37)

**668 tests: 653 PASS, 1 FAIL, 14 NOT TESTED** (a test is PASS only when every job that had to run it reports PASS)

Build SHA-256: `7773b3c165039d37373e4078c64501a81af4bc6683c665ff04ce30de6d738302` (every shard checks it before running). Mode: strict - NOT green.

| job | state | tests | runner | minutes |
|---|---|---|---|---|
| s01-r01 | results | 158 | GitHub Actions 1000001528 | 20 |
| s02-r01 | results | 199 | GitHub Actions 1000001525 | 10 |
| s03-r01 | results | 152 | GitHub Actions 1000001527 | 19 |
| s04-r01 | results | 159 | GitHub Actions 1000001526 | 21 |

## Not PASS

| test | verdict | per job |
|---|---|---|
| OnlineBackup.Tests.NightP_RestoreChainTests.CaseOnlyRename_LaterPointsRestoreTheNewSpelling | FAIL | 0 PASS / 1 FAIL / 0 NOT TESTED of 1 - s01-r01: case point 2026-10-07-20-05-30 (run 1): failed=0 missing REPORTS/report.txt; extra Reports/Report.txt |
| OnlineBackup.Tests.SqlSimpleRecoveryLogTests.LogMode_AllDatabases_ASimpleRecoveryDatabaseIsSkipped_NotAnErrorEveryRun | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s01-r01: [declared] outcome NotExecuted: NEEDS OWNER DECISION (bug 76, docs/PRODUCT-BENCHMARK.md): skip, warn or fail a SIMPLE-recovery database in log mode |
| OnlineBackup.Tests.TempDirsComponentTests.AFolderThatCannotGo_IsReported_WithItsPath_NotSwallowed | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s01-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: an open file blocks a removal only on Windows |
| OnlineBackup.Tests.PilotVssComponentTests.ABackupWithShadowCopy_SendsALockedFile_IsASuccessWithoutError_RestoresIdentical_AndLeavesNoShadowCopy | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s02-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: Volume Shadow Copy exists only on Windows (this test runs on the hosted-Windows QA shards) |
| OnlineBackup.Tests.PilotVssComponentTests.ALockedFile_IsReadFromTheSnapshot_WithItsContentAtSnapshotTime_AndTheSnapshotIsRemovedAfterwards | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s02-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: Volume Shadow Copy exists only on Windows (this test runs on the hosted-Windows QA shards) |
| OnlineBackup.Tests.PilotVssComponentTests.WhenTheRunFails_TheShadowCopyIsRemovedToo_AndTheNextRunCompletes | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s02-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: Volume Shadow Copy exists only on Windows (this test runs on the hosted-Windows QA shards) |
| OnlineBackup.Tests.PilotVssIntegrationTests.ALockedFile_IsBackedUpThroughTheShadowCopy_RestoresIdentical_NoShadowCopyLeft | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s02-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: Volume Shadow Copy exists only on Windows (this test runs on the hosted-Windows QA shards) |
| OnlineBackup.Tests.PilotVssIntegrationTests.ARunTheServerStopsForTheQuota_RemovesItsShadowCopy_AndOnceThereIsRoom_TheNextRunCompletes | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s02-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: Volume Shadow Copy exists only on Windows (this test runs on the hosted-Windows QA shards) |
| OnlineBackup.Tests.ReliabilityTests.ServerDiskFullMidBackup_ExistingBackupsUnharmed_NextRunAfterSpaceIsFreedCompletes | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s02-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: needs restic and OB_ALLOW_MOUNT=1 (a small disk is mounted to fill it) |
| OnlineBackup.Tests.EndToEndTests.Net40AgentUnderMonoBacksUpAndRestores | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s03-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: needs mono and the .NET 4.0 agent build |
| OnlineBackup.Tests.RetentionZoneComponentTests.TheLastBackupOfAMonth_IsTheCustomersMonth_NotUtcs | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s03-r01: [declared] outcome NotExecuted: NEEDS OWNER DECISION (bug 77, docs/PRODUCT-BENCHMARK.md): GFS day/month boundaries in the server time zone or UTC |
| OnlineBackup.Tests.TlsTests.Net40AgentUnderMonoUsesTheBuiltinTls | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s03-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: needs mono and the .NET 4.0 agent build |
| OnlineBackup.Tests.UpgradeTests.MakeFixture_WhenAsked | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s03-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: a tool that makes the upgrade fixture on demand (OB_MAKE_UPGRADE_FIXTURE), not a test |
| OnlineBackup.Tests.LoadTests.BigServer_500Customers_5000Sets_PagesAndComputersStayFast | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s04-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: the load run is its own step (OB_LOAD=1; the gate runs it in job tests-extra) |
| OnlineBackup.Tests.ReadOnlyStoreComponentTests.AReadOnlyStore_StillListsItsPoints_AndServesItsObjects | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s04-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: a bind mount is not allowed here (needs root) |
