# ubuntu-latest, select '', 4 shards x 1 - run 38034806429 (0af9ca07b1c7dd5c20e3f786bc11a8a39010ed6e)

**708 tests: 692 PASS, 2 FAIL, 14 NOT TESTED** (a test is PASS only when every job that had to run it reports PASS)

Build SHA-256: `9459af66ca4b34b6bf675a1cbb583dfc85f3ca3f2a50e3b30cbe98c57b4e06ca` (every shard checks it before running). Mode: strict - NOT green.

| job | state | tests | runner | minutes |
|---|---|---|---|---|
| s01-r01 | results | 129 | GitHub Actions 1000002429 | 22 |
| s02-r01 | results | 209 | GitHub Actions 1000002414 | 8 |
| s03-r01 | results | 202 | GitHub Actions 1000002474 | 21 |
| s04-r01 | results | 168 | GitHub Actions 1000002380 | 19 |

## Not PASS

| test | verdict | per job |
|---|---|---|
| OnlineBackup.Tests.NightP_RestoreChainTests.CaseOnlyRename_LaterPointsRestoreTheNewSpelling | FAIL | 0 PASS / 1 FAIL / 0 NOT TESTED of 1 - s01-r01: case point 2026-10-10-10-38-27 (run 1): failed=0 missing REPORTS/report.txt; extra Reports/Report.txt |
| OnlineBackup.Tests.ReliabilityTests.ServerDiskFullMidBackup_ExistingBackupsUnharmed_NextRunAfterSpaceIsFreedCompletes | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s01-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: needs restic and OB_ALLOW_MOUNT=1 (a small disk is mounted to fill it) |
| OnlineBackup.Tests.ReadOnlyStoreComponentTests.AReadOnlyStore_StillListsItsPoints_AndServesItsObjects | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s02-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: a bind mount is not allowed here (needs root) |
| OnlineBackup.Tests.TempDirsComponentTests.AFolderThatCannotGo_IsReported_WithItsPath_NotSwallowed | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s02-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: an open file blocks a removal only on Windows |
| OnlineBackup.Tests.UpgradeTests.MakeFixture_WhenAsked | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s02-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: a tool that makes the upgrade fixture on demand (OB_MAKE_UPGRADE_FIXTURE), not a test |
| OnlineBackup.Tests.EndToEndTests.Net40AgentUnderMonoBacksUpAndRestores | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s03-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: needs mono and the .NET 4.0 agent build |
| OnlineBackup.Tests.LoadTests.BigServer_500Customers_5000Sets_PagesAndComputersStayFast | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s03-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: the load run is its own step (OB_LOAD=1; the gate runs it in job tests-extra) |
| OnlineBackup.Tests.PilotChallengeInstallTests.IN04_UpdateFromFiles_APackageTheVendorNeverSigned_IsRefused_NothingInstalled | FAIL | 0 PASS / 1 FAIL / 0 NOT TESTED of 1 - s03-r01: a package without the vendor's signature was installed from files: version 99.4.0, handed to /tmp/obpilot-chal-05fede44/system/update/uploaded/server/OnlineBack |
| OnlineBackup.Tests.SqlSimpleRecoveryLogTests.LogMode_AllDatabases_ASimpleRecoveryDatabaseIsSkipped_NotAnErrorEveryRun | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s03-r01: [declared] outcome NotExecuted: NEEDS OWNER DECISION (bug 76, docs/PRODUCT-BENCHMARK.md): skip, warn or fail a SIMPLE-recovery database in log mode |
| OnlineBackup.Tests.PilotVssComponentTests.ABackupWithShadowCopy_SendsALockedFile_IsASuccessWithoutError_RestoresIdentical_AndLeavesNoShadowCopy | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s04-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: Volume Shadow Copy exists only on Windows (this test runs on the hosted-Windows QA shards) |
| OnlineBackup.Tests.PilotVssComponentTests.ALockedFile_IsReadFromTheSnapshot_WithItsContentAtSnapshotTime_AndTheSnapshotIsRemovedAfterwards | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s04-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: Volume Shadow Copy exists only on Windows (this test runs on the hosted-Windows QA shards) |
| OnlineBackup.Tests.PilotVssComponentTests.WhenTheRunFails_TheShadowCopyIsRemovedToo_AndTheNextRunCompletes | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s04-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: Volume Shadow Copy exists only on Windows (this test runs on the hosted-Windows QA shards) |
| OnlineBackup.Tests.PilotVssIntegrationTests.ALockedFile_IsBackedUpThroughTheShadowCopy_RestoresIdentical_NoShadowCopyLeft | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s04-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: Volume Shadow Copy exists only on Windows (this test runs on the hosted-Windows QA shards) |
| OnlineBackup.Tests.PilotVssIntegrationTests.ARunTheServerStopsForTheQuota_RemovesItsShadowCopy_AndOnceThereIsRoom_TheNextRunCompletes | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s04-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: Volume Shadow Copy exists only on Windows (this test runs on the hosted-Windows QA shards) |
| OnlineBackup.Tests.RetentionZoneComponentTests.TheLastBackupOfAMonth_IsTheCustomersMonth_NotUtcs | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s04-r01: [declared] outcome NotExecuted: NEEDS OWNER DECISION (bug 77, docs/PRODUCT-BENCHMARK.md): GFS day/month boundaries in the server time zone or UTC |
| OnlineBackup.Tests.TlsTests.Net40AgentUnderMonoUsesTheBuiltinTls | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s04-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: needs mono and the .NET 4.0 agent build |
