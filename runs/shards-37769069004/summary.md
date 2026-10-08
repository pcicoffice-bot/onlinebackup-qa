# [suite net48] windows-latest, select 'OnlineBackup.Tests.AgentTlsComponentTests. OnlineBackup.Tests.AtomicReplaceRefusedComponentTests. OnlineBackup.Tests.AtomicSharedReadComponentTests. OnlineBackup.Tests.AuditB_FilterTests. OnlineBackup.Tests.CoreFormatsTests. OnlineBackup.Tests.FolderSelectionComponentTests. OnlineBackup.Tests.LocalStateComponentTests. OnlineBackup.Tests.OpenRunComponentTests. OnlineBackup.Tests.PilotAgentNetworkComponentTests. OnlineBackup.Tests.PilotAgentSettingsComponentTests. OnlineBackup.Tests.PilotAgentTlsComponentTests. OnlineBackup.Tests.PilotFileBackupComponentTests. OnlineBackup.Tests.PilotDeltaChainComponentTests. OnlineBackup.Tests.PilotFilterComponentTests. OnlineBackup.Tests.PilotStopComponentTests. OnlineBackup.Tests.PilotResourcesComponentTests. OnlineBackup.Tests.PilotProcessComponentTests. OnlineBackup.Tests.PilotVssComponentTests. OnlineBackup.Tests.PilotMaxDurationComponentTests. OnlineBackup.Tests.PilotRebootComponentTests. OnlineBackup.Tests.PilotScopeComponentTests. OnlineBackup.Tests.ResticPathComponentTests. OnlineBackup.Tests.RestoreComponentTests. OnlineBackup.Tests.RestorePathMappingComponentTests. OnlineBackup.Tests.RetentionZoneComponentTests. OnlineBackup.Tests.RunRequestComponentTests. OnlineBackup.Tests.SetupPayloadTests. OnlineBackup.Tests.SystemStateTargetComponentTests. OnlineBackup.Tests.UnreadableComponentTests.', 2 shards x 1 - run 37769069004 (424be278ac80ea2c886b1e467c6c02b622a9e702)

**110 tests: 109 PASS, 0 FAIL, 1 NOT TESTED** (a test is PASS only when every job that had to run it reports PASS)

Build SHA-256: `9b318e9b06b3051abf843a27672832ca54f7556437f2d0a44a5590d4ec2085f0` (every shard checks it before running). Mode: strict - NOT green.

| job | state | tests | runner | minutes |
|---|---|---|---|---|
| s01-r01 | results | 53 | GitHub Actions 1000001802 | 1 |
| s02-r01 | results | 57 | GitHub Actions 1000001803 | 3 |

## Not PASS

| test | verdict | per job |
|---|---|---|
| OnlineBackup.Tests.RetentionZoneComponentTests.TheLastBackupOfAMonth_IsTheCustomersMonth_NotUtcs | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s01-r01: [declared] outcome NotExecuted: NEEDS OWNER DECISION (bug 77, docs/PRODUCT-BENCHMARK.md): GFS day/month boundaries in the server time zone or UTC |
