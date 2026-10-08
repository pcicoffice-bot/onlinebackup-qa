# windows-latest, select 'OnlineBackup.Tests.UpdaterTests OnlineBackup.Tests.ClientUpdateTests OnlineBackup.Tests.PilotChallengeInstallTests.IN04 OnlineBackup.Tests.PilotInstallComponentTests.IN04 OnlineBackup.Tests.PilotScopeAgentTests.WithTheSwitch_AFileSet', 1 shards x 2 - run 37765771759 (b9c2762ba952dd58f43a0c20b69cee1c33239f4f)

**12 tests: 12 PASS, 0 FAIL, 0 NOT TESTED** (a test is PASS only when every job that had to run it reports PASS)

Build SHA-256: `9797c9c0c94ae0f1a218292e0a930ede70b952e0b562018264ae59c6cd4ab92c` (every shard checks it before running). Mode: strict - green.

| job | state | tests | runner | minutes |
|---|---|---|---|---|
| s01-r01 | results | 12 | GitHub Actions 1000001792 | 1 |
| s01-r02 | results | 12 | GitHub Actions 1000001791 | 1 |

## Repetitions

| test | PASS | FAIL | NOT TESTED |
|---|---|---|---|
| OnlineBackup.Tests.ClientUpdateTests.B1_WithThePilotSwitch_TheServerOffersNoClientUpdate_TheComputerDoesNotUpdateItself_WithoutItItDoes | 2 | 0 | 0 |
| OnlineBackup.Tests.ClientUpdateTests.TheServerListsItsClientFiles_TheComputerSeesWhatChanged_DownloadsOnlyListedFiles | 2 | 0 | 0 |
| OnlineBackup.Tests.PilotChallengeInstallTests.IN04_UpdateFromFiles_APackageTheVendorNeverSigned_IsRefused_NothingInstalled | 2 | 0 | 0 |
| OnlineBackup.Tests.PilotInstallComponentTests.IN04_AChangedPackage_AForgedOrMisplacedSignature_AreRefused_NothingInstalled_ThenTheGenuineOneInstalls | 2 | 0 | 0 |
| OnlineBackup.Tests.PilotInstallComponentTests.IN04_ASignedVersion_IsDownloaded_Checked_HandedToItsInstallerByteIdentical_SettingsAndDataUntouched | 2 | 0 | 0 |
| OnlineBackup.Tests.PilotInstallComponentTests.IN04_UpdateFromFiles_ExactlyAtTheLimitTaken_OneByteOverOrAPartMissingOrNoProgram_Refused | 2 | 0 | 0 |
| OnlineBackup.Tests.PilotScopeAgentTests.WithTheSwitch_AFileSetIsCreated_BackedUp_Restored_Tested_Kept_Encrypted_Updated_AndRecovered | 2 | 0 | 0 |
| OnlineBackup.Tests.UpdaterTests.NewSignedVersion_IsFound_Downloaded_Checked_AndHandedToItsInstaller_AForgedOneIsRefused | 2 | 0 | 0 |
| OnlineBackup.Tests.UpdaterTests.PrivateStore_AVersionWithoutTheVendorsSignature_IsRefused_TheSignedOneInstalls_TheAuditSaysSignedOnlyThen | 2 | 0 | 0 |
| OnlineBackup.Tests.UpdaterTests.PrivateStore_NewVersionFound_ReadWithTheKey_ShaChecked_KeyNeverShown | 2 | 0 | 0 |
| OnlineBackup.Tests.UpdaterTests.UpdateFromFiles_OnlyWithTheVendorsSignature_UnsignedOrForgedRefused_TheAuditSaysSignedOnlyThen | 2 | 0 | 0 |
| OnlineBackup.Tests.UpdaterTests.UpdateFromFiles_PartsJoinedInOrder_OnTheServerOnly_ANonPackageIsRefused | 2 | 0 | 0 |
