# windows-latest, select 'Pilot WebRestoreTests Bug103DurationStopTests', 6 shards x 1 - run 37675796567 (a8f8543abfb3551321a9b1ec5b3a3e8b2f3d83bd)

**104 tests: 103 PASS, 1 FAIL, 0 NOT TESTED** (a test is PASS only when every job that had to run it reports PASS)

Build SHA-256: `a6989b45e3c8ca1b9ed4b408ba605b0f3eb696217f2a7ae318700608ac1e2f45` (every shard checks it before running). Mode: strict - NOT green.

| job | state | tests | runner | minutes |
|---|---|---|---|---|
| s01-r01 | results | 18 | GitHub Actions 1000001510 | 1 |
| s02-r01 | results | 18 | GitHub Actions 1000001514 | 2 |
| s03-r01 | results | 18 | GitHub Actions 1000001512 | 1 |
| s04-r01 | results | 15 | GitHub Actions 1000001511 | 2 |
| s05-r01 | results | 18 | GitHub Actions 1000001509 | 2 |
| s06-r01 | results | 17 | GitHub Actions 1000001513 | 2 |

## Not PASS

| test | verdict | per job |
|---|---|---|
| OnlineBackup.Tests.WebRestoreTests.CustomerRestoresChosenFilesFromTheWebsite_WithTheEncryptionPassword | FAIL | 0 PASS / 1 FAIL / 0 NOT TESTED of 1 - s04-r01: 6 left in the server's temp folder after the web restore: webrestore-4a834a5d2a23 [Directory], webrestore-c39b1f590d22 [Directory], webrestore-4a834a5d2a23\C [D |

103 test(s) of this build are not yet in tools/qa-shards/known-tests.txt (add them so that a later disappearance is caught): OnlineBackup.Tests.Bug103DurationStopTests.Native_ADurationStop_IsStopped_TheServerCountsIt_TheNextRunSucceeds, OnlineBackup.Tests.Bug103DurationStopTests.Restic_ADurationStop_IsStopped_TheServerCountsIt, OnlineBackup.Tests.Bug103DurationStopTests.ServiceCalls_ADurationStop_CountsAsAWarning_AnAdminStop_ChangesNothing, OnlineBackup.Tests.PilotDeltaChainComponentTests.ADeltaTheServerFailed_LeavesTheChainAsItWas_ThePointRestoresTheOldVersion_TheNextRunSendsTheDelta, OnlineBackup.Tests.PilotDeltaChainComponentTests.Differential_EachDeltaCarriesEveryChangeSinceTheFull_EveryPointRestoresIdentical, OnlineBackup.Tests.PilotDeltaChainComponentTests.Incremental_EachDeltaIsSmallAndHoldsOnlyNewChunks_EveryPointRestoresIdentical_PastTheLimitANewFullCopy, OnlineBackup.Tests.PilotDeltaChainIntegrationTests.AChangeOverTheDeltaRatio_IsSentAsANewFullCopy_ASmallOneAsADelta_EveryPointRestoresIdentical, OnlineBackup.Tests.PilotFileBackupComponentTests.KnownTree_Run2SendsOnlyTheNewChangedAndPermissionChangedFiles_DeletesOne_EveryPointRestoresIdentical, OnlineBackup.Tests.PilotFileBackupComponentTests.TheServerFailsMidRun_TheRunFails_NothingDeleted_TheOldPointStillRestores_TheNextRunSendsTheChangesAgain, OnlineBackup.Tests.PilotFilterComponentTests.ALinkedFolder_IsFollowedOnlyWithTheOption_AndALinkBackToItsParentIsNotWalkedAgain, OnlineBackup.Tests.PilotFilterComponentTests.AnIncludeOnlyFilter_KeepsOnlyItsFiles_InEveryFolder_AndTheyRestoreIdentical, OnlineBackup.Tests.PilotFilterComponentTests.SkippedFolderAndExcludeFilter_AreAbsentFromTheRestore_EverythingElseIdentical_ASiblingWithTheSamePrefixIsKept, OnlineBackup.Tests.PilotInstallComponentTests.IN01_AnInstallationThatDidNotAnswer_IsFinishedByARerun_AndAReinstallOnTheOldDrive_KeepsEveryCustomerFile, OnlineBackup.Tests.PilotInstallComponentTests.IN02_APackageWithoutItsConnectionFile_IsRefused_BeforeAnythingIsWritten, OnlineBackup.Tests.PilotInstallComponentTests.IN02_AReinstallOfANewerPackage_ReplacesEveryProgramFile_AndKeepsTheRegistrationAndKeys, OnlineBackup.Tests.PilotInstallComponentTests.IN02_InstallFromThePackage_EveryProgramFileByteIdentical_TheScriptsNot_TheDataFolderReady, OnlineBackup.Tests.PilotInstallComponentTests.IN02_UninstallRemovesOnlyItsOwnFolders_NothingLeftWhenAsked_AForeignFolderStaysByteIdentical, OnlineBackup.Tests.PilotInstallComponentTests.IN03_AGoodUpdate_GivesExactlyTheNewBytes_AFailedOne_GivesBackExactlyTheOldBytes_UnlistedFilesUntouched, OnlineBackup.Tests.PilotInstallComponentTests.IN04_AChangedPackage_AForgedOrMisplacedSignature_AreRefused_NothingInstalled_ThenTheGenuineOneInstalls, OnlineBackup.Tests.PilotInstallComponentTests.IN04_ASignedVersion_IsDownloaded_Checked_HandedToItsInstallerByteIdentical_SettingsAndDataUntouched ...
