# windows-latest, select 'OnlineBackup.Tests.UpdaterTests OnlineBackup.Tests.ClientUpdateTests OnlineBackup.Tests.PilotChallengeInstallTests.IN04 OnlineBackup.Tests.PilotInstallComponentTests.IN04 OnlineBackup.Tests.PilotScopeAgentTests.WithTheSwitch_AFileSet', 1 shards x 2 - run 38034831115 (eb25795be61b8b16e7c45f15b4d01eaee8307abb)

**12 tests: 12 PASS, 0 FAIL, 0 NOT TESTED** (a test is PASS only when every job that had to run it reports PASS)

Build SHA-256: `dd76c1d9d95cbd38e5a2eb3e4d43a547a3749df70e8f099409725df57104d9fa` (every shard checks it before running). Mode: strict - green.

| job | state | tests | runner | minutes |
|---|---|---|---|---|
| s01-r01 | results | 12 | GitHub Actions 1000002356 | 1 |
| s01-r02 | results | 12 | GitHub Actions 1000002378 | 1 |

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
