# windows-latest, select 'Pilot WebRestoreTests Bug103DurationStopTests', 6 shards x 1 - run 38034801362 (d749bfee223b3acb6d4379015b0643d9579d26bc)

**104 tests: 103 PASS, 1 FAIL, 0 NOT TESTED** (a test is PASS only when every job that had to run it reports PASS)

Build SHA-256: `bd4f83474ebb43dc5fa87255c3436c9f71e473b6295c00166c98ca204c97faf9` (every shard checks it before running). Mode: strict - NOT green.

| job | state | tests | runner | minutes |
|---|---|---|---|---|
| s01-r01 | results | 18 | GitHub Actions 1000002404 | 1 |
| s02-r01 | results | 18 | GitHub Actions 1000002362 | 1 |
| s03-r01 | results | 18 | GitHub Actions 1000002419 | 1 |
| s04-r01 | results | 15 | GitHub Actions 1000002490 | 3 |
| s05-r01 | results | 18 | GitHub Actions 1000002455 | 2 |
| s06-r01 | results | 17 | GitHub Actions 1000002473 | 2 |

## Not PASS

| test | verdict | per job |
|---|---|---|
| OnlineBackup.Tests.WebRestoreTests.CustomerRestoresChosenFilesFromTheWebsite_WithTheEncryptionPassword | FAIL | 0 PASS / 1 FAIL / 0 NOT TESTED of 1 - s04-r01: 6 left in the server's temp folder after the web restore: webrestore-1ef6332adb2e [Directory], webrestore-8cdd016a5339 [Directory], webrestore-1ef6332adb2e\C [D |

103 test(s) of this build are not yet in tools/qa-shards/known-tests.txt (add them so that a later disappearance is caught): OnlineBackup.Tests.Bug103DurationStopTests.Native_ADurationStop_IsStopped_TheServerCountsIt_TheNextRunSucceeds, OnlineBackup.Tests.Bug103DurationStopTests.Restic_ADurationStop_IsStopped_TheServerCountsIt, OnlineBackup.Tests.Bug103DurationStopTests.ServiceCalls_ADurationStop_CountsAsAWarning_AnAdminStop_ChangesNothing, OnlineBackup.Tests.PilotDeltaChainComponentTests.ADeltaTheServerFailed_LeavesTheChainAsItWas_ThePointRestoresTheOldVersion_TheNextRunSendsTheDelta, OnlineBackup.Tests.PilotDeltaChainComponentTests.Differential_EachDeltaCarriesEveryChangeSinceTheFull_EveryPointRestoresIdentical, OnlineBackup.Tests.PilotDeltaChainComponentTests.Incremental_EachDeltaIsSmallAndHoldsOnlyNewChunks_EveryPointRestoresIdentical_PastTheLimitANewFullCopy, OnlineBackup.Tests.PilotDeltaChainIntegrationTests.AChangeOverTheDeltaRatio_IsSentAsANewFullCopy_ASmallOneAsADelta_EveryPointRestoresIdentical, OnlineBackup.Tests.PilotFileBackupComponentTests.KnownTree_Run2SendsOnlyTheNewChangedAndPermissionChangedFiles_DeletesOne_EveryPointRestoresIdentical, OnlineBackup.Tests.PilotFileBackupComponentTests.TheServerFailsMidRun_TheRunFails_NothingDeleted_TheOldPointStillRestores_TheNextRunSendsTheChangesAgain, OnlineBackup.Tests.PilotFilterComponentTests.ALinkedFolder_IsFollowedOnlyWithTheOption_AndALinkBackToItsParentIsNotWalkedAgain, OnlineBackup.Tests.PilotFilterComponentTests.AnIncludeOnlyFilter_KeepsOnlyItsFiles_InEveryFolder_AndTheyRestoreIdentical, OnlineBackup.Tests.PilotFilterComponentTests.SkippedFolderAndExcludeFilter_AreAbsentFromTheRestore_EverythingElseIdentical_ASiblingWithTheSamePrefixIsKept, OnlineBackup.Tests.PilotInstallComponentTests.IN01_AnInstallationThatDidNotAnswer_IsFinishedByARerun_AndAReinstallOnTheOldDrive_KeepsEveryCustomerFile, OnlineBackup.Tests.PilotInstallComponentTests.IN02_APackageWithoutItsConnectionFile_IsRefused_BeforeAnythingIsWritten, OnlineBackup.Tests.PilotInstallComponentTests.IN02_AReinstallOfANewerPackage_ReplacesEveryProgramFile_AndKeepsTheRegistrationAndKeys, OnlineBackup.Tests.PilotInstallComponentTests.IN02_InstallFromThePackage_EveryProgramFileByteIdentical_TheScriptsNot_TheDataFolderReady, OnlineBackup.Tests.PilotInstallComponentTests.IN02_UninstallRemovesOnlyItsOwnFolders_NothingLeftWhenAsked_AForeignFolderStaysByteIdentical, OnlineBackup.Tests.PilotInstallComponentTests.IN03_AGoodUpdate_GivesExactlyTheNewBytes_AFailedOne_GivesBackExactlyTheOldBytes_UnlistedFilesUntouched, OnlineBackup.Tests.PilotInstallComponentTests.IN04_AChangedPackage_AForgedOrMisplacedSignature_AreRefused_NothingInstalled_ThenTheGenuineOneInstalls, OnlineBackup.Tests.PilotInstallComponentTests.IN04_ASignedVersion_IsDownloaded_Checked_HandedToItsInstallerByteIdentical_SettingsAndDataUntouched ...
