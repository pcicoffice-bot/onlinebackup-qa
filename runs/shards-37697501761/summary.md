# ubuntu-latest, select '', 4 shards x 1 - run 37697501761 (271d5494dce545c0c20957e2fcda7664f06c47d4)

**781 tests: 763 PASS, 4 FAIL, 14 NOT TESTED** (a test is PASS only when every job that had to run it reports PASS)

Build SHA-256: `451b22c1351d2ec19961f9b1644c2b240e9c260e71b6602853509824ad63fe80` (every shard checks it before running). Mode: strict - NOT green.

| job | state | tests | runner | minutes |
|---|---|---|---|---|
| s01-r01 | results | 163 | GitHub Actions 1000001595 | 22 |
| s02-r01 | results | 230 | GitHub Actions 1000001596 | 13 |
| s03-r01 | results | 223 | GitHub Actions 1000001599 | 14 |
| s04-r01 | results | 165 | GitHub Actions 1000001601 | 21 |

## Not PASS

| test | verdict | per job |
|---|---|---|
| OnlineBackup.Tests.EndToEndTests.Net40AgentUnderMonoBacksUpAndRestores | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s01-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: needs mono and the .NET 4.0 agent build |
| OnlineBackup.Tests.LoadTests.BigServer_500Customers_5000Sets_PagesAndComputersStayFast | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s01-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: the load run is its own step (OB_LOAD=1; the gate runs it in job tests-extra) |
| OnlineBackup.Tests.NightP_RestoreChainTests.CaseOnlyRename_LaterPointsRestoreTheNewSpelling | FAIL | 0 PASS / 1 FAIL / 0 NOT TESTED of 1 - s01-r01: case point 2026-10-07-22-46-24 (run 1): failed=0 missing REPORTS/report.txt; extra Reports/Report.txt |
| OnlineBackup.Tests.PilotChallengeInstallTests.IN04_UpdateFromFiles_APackageTheVendorNeverSigned_IsRefused_NothingInstalled | FAIL | 0 PASS / 1 FAIL / 0 NOT TESTED of 1 - s01-r01: a package without the vendor's signature was installed from files: version 99.4.0, handed to /tmp/obpilot-chal-e1168f71/system/update/uploaded/server/OnlineBack |
| OnlineBackup.Tests.PilotVssComponentTests.ABackupWithShadowCopy_SendsALockedFile_IsASuccessWithoutError_RestoresIdentical_AndLeavesNoShadowCopy | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s01-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: Volume Shadow Copy exists only on Windows (this test runs on the hosted-Windows QA shards) |
| OnlineBackup.Tests.PilotVssComponentTests.ALockedFile_IsReadFromTheSnapshot_WithItsContentAtSnapshotTime_AndTheSnapshotIsRemovedAfterwards | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s01-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: Volume Shadow Copy exists only on Windows (this test runs on the hosted-Windows QA shards) |
| OnlineBackup.Tests.PilotVssComponentTests.WhenTheRunFails_TheShadowCopyIsRemovedToo_AndTheNextRunCompletes | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s01-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: Volume Shadow Copy exists only on Windows (this test runs on the hosted-Windows QA shards) |
| OnlineBackup.Tests.PilotChallengeScopeTests.ARebuildOrVerify_OfABlockedSet_FromTheAdminSite_LeavesItsDataByteIdentical | FAIL | 0 PASS / 1 FAIL / 0 NOT TESTED of 1 - s02-r01: BYPASS with the pilot switch on: the admin site's rebuild changed the stored data of a blocked set (verify ran (200); rebuild ran (200)): /index.db |
| OnlineBackup.Tests.SqlSimpleRecoveryLogTests.LogMode_AllDatabases_ASimpleRecoveryDatabaseIsSkipped_NotAnErrorEveryRun | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s02-r01: [declared] outcome NotExecuted: NEEDS OWNER DECISION (bug 76, docs/PRODUCT-BENCHMARK.md): skip, warn or fail a SIMPLE-recovery database in log mode |
| OnlineBackup.Tests.PilotVssIntegrationTests.ALockedFile_IsBackedUpThroughTheShadowCopy_RestoresIdentical_NoShadowCopyLeft | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s03-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: Volume Shadow Copy exists only on Windows (this test runs on the hosted-Windows QA shards) |
| OnlineBackup.Tests.PilotVssIntegrationTests.ARunTheServerStopsForTheQuota_RemovesItsShadowCopy_AndOnceThereIsRoom_TheNextRunCompletes | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s03-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: Volume Shadow Copy exists only on Windows (this test runs on the hosted-Windows QA shards) |
| OnlineBackup.Tests.RetentionZoneComponentTests.TheLastBackupOfAMonth_IsTheCustomersMonth_NotUtcs | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s03-r01: [declared] outcome NotExecuted: NEEDS OWNER DECISION (bug 77, docs/PRODUCT-BENCHMARK.md): GFS day/month boundaries in the server time zone or UTC |
| OnlineBackup.Tests.TempDirsComponentTests.AFolderThatCannotGo_IsReported_WithItsPath_NotSwallowed | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s03-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: an open file blocks a removal only on Windows |
| OnlineBackup.Tests.TlsTests.Net40AgentUnderMonoUsesTheBuiltinTls | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s03-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: needs mono and the .NET 4.0 agent build |
| OnlineBackup.Tests.UpgradeTests.MakeFixture_WhenAsked | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s03-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: a tool that makes the upgrade fixture on demand (OB_MAKE_UPGRADE_FIXTURE), not a test |
| OnlineBackup.Tests.PilotChallengeSecurityFailingTests.AU01_TheCodeThatConfirmedTheTwoStepSetUp_DoesNotOpenASignIn | FAIL | 0 PASS / 1 FAIL / 0 NOT TESTED of 1 - s04-r01: Assert.Empty() Failure: Collection was not empty Collection: ["administrator: the confirmation code opened a sign"···, "customer: the confirmation code opened a |
| OnlineBackup.Tests.ReadOnlyStoreComponentTests.AReadOnlyStore_StillListsItsPoints_AndServesItsObjects | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s04-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: a bind mount is not allowed here (needs root) |
| OnlineBackup.Tests.ReliabilityTests.ServerDiskFullMidBackup_ExistingBackupsUnharmed_NextRunAfterSpaceIsFreedCompletes | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s04-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: needs restic and OB_ALLOW_MOUNT=1 (a small disk is mounted to fill it) |
