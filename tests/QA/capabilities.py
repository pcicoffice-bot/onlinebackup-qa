#!/usr/bin/env python3
"""
The capability inventory of OnlineBackup and its coverage — the source of truth for docs/CAPABILITIES.md and
tests/QA/COVERAGE-MATRIX.md (written by this script; never edited by hand).

Each capability: id, area, name, critical (a customer loses data or trust when it breaks), code (where it lives), and
per dimension the tests that prove it:
  happy · failure · boundary · recovery · concurrency · integrity (restore + SHA-256 / byte compare) · security
A dimension is a list of test ids, or NA('why') when it does not apply. An empty list = NOT TESTED.
Test ids: x:Class.Method (xUnit, tests/Tests) · qa:path (tests/QA, Playwright) · win:step (tests/QA/windows, real Windows).
Status: FULL = every applicable dimension has a passing test; PARTIAL = the happy path and some; NONE = no happy path.
A capability whose tests exist but did not run in the last run is not counted (see `run_results`).
"""
import json, os, re, sys

class NA(str): pass

D = ['happy', 'failure', 'boundary', 'recovery', 'concurrency', 'integrity', 'security']
C = []
def cap(id, area, name, critical, code, regression=(), **dims):
    for k in dims: assert k in D, k
    C.append(dict(id=id, area=area, name=name, critical=critical, code=code, regression=list(regression), dims={k: dims.get(k, []) for k in D}))

E2E = 'x:EndToEndTests.'
# ------------------------------------------------------------------ backup engines
cap('BK-01', 'Backup', 'File backup, native engine (first, incremental: new / changed / deleted / permissions)', True, 'A/BackupRun.cs, S/SetStore.cs',
    happy=[E2E + 'FullCycle_NewUpdatedPermissionDeleted_DeltaForLargeFiles_RestoreAnyPoint', 'qa:journeys/j3', 'qa:journeys/j4', 'x:PilotFileBackupComponentTests.KnownTree_Run2SendsOnlyTheNewChangedAndPermissionChangedFiles_DeletesOne_EveryPointRestoresIdentical'],
    failure=['x:SourceTests.OneOfTwoSourcesGone_IsSuccessWithError_ShownAsProblem_NotAsWarning', 'x:SourceTests.WholeSourceGone_IsAFailure_LastBackupNotRefreshed_FilesKept_AndComesBackCleanly', 'x:PilotFileBackupComponentTests.TheServerFailsMidRun_TheRunFails_NothingDeleted_TheOldPointStillRestores_TheNextRunSendsTheChangesAgain'],
    boundary=['x:ReliabilityTests.Volume_20000SmallFilesAnd1GB_BackupChangeRestore', 'qa:journeys/j4', 'x:PilotFileBackupComponentTests.KnownTree_Run2SendsOnlyTheNewChangedAndPermissionChangedFiles_DeletesOne_EveryPointRestoresIdentical'],
    recovery=['x:InterruptionTests.AgentKilledMidBackup_NextBackupRunsAtOnce_HistoryShowsTheFailure_NotRunning', 'qa:failure-recovery/f1', 'qa:failure-recovery/f2', 'x:PilotFileBackupComponentTests.TheServerFailsMidRun_TheRunFails_NothingDeleted_TheOldPointStillRestores_TheNextRunSendsTheChangesAgain'],
    concurrency=['qa:failure-recovery/f3'],
    integrity=['qa:journeys/j3', 'qa:journeys/j4', E2E + 'DamagedObjectIsFoundQuarantinedAndResentFromTheSource', 'x:PilotFileBackupComponentTests.KnownTree_Run2SendsOnlyTheNewChangedAndPermissionChangedFiles_DeletesOne_EveryPointRestoresIdentical', 'x:PilotFileBackupComponentTests.TheServerFailsMidRun_TheRunFails_NothingDeleted_TheOldPointStillRestores_TheNextRunSendsTheChangesAgain'],
    security=['x:CryptoTests.TamperedOrWrongKeyIsRejected'], regression=[1, 2, 11])
cap('BK-02', 'Backup', 'Delta chains (incremental / differential), long chain → new full', True, 'A/BackupRun.cs, C/Chunker.cs',
    happy=[E2E + 'EveryPointOfADeltaChainRestoresExactly_AndALongChainStartsANewFullCopy', E2E + 'DifferentialChain_EveryPointRestoresExactly_AndEachDeltaCarriesAllChangesSinceTheFull', 'x:PilotDeltaChainComponentTests.Incremental_EachDeltaIsSmallAndHoldsOnlyNewChunks_EveryPointRestoresIdentical_PastTheLimitANewFullCopy', 'x:PilotDeltaChainComponentTests.Differential_EachDeltaCarriesEveryChangeSinceTheFull_EveryPointRestoresIdentical'],
    failure=['x:PilotDeltaChainComponentTests.ADeltaTheServerFailed_LeavesTheChainAsItWas_ThePointRestoresTheOldVersion_TheNextRunSendsTheDelta'], boundary=['x:ChunkerTests.InsertInTheMiddleChangesOnlyNearbyChunks', 'x:PilotDeltaChainComponentTests.Incremental_EachDeltaIsSmallAndHoldsOnlyNewChunks_EveryPointRestoresIdentical_PastTheLimitANewFullCopy', 'x:PilotDeltaChainIntegrationTests.AChangeOverTheDeltaRatio_IsSentAsANewFullCopy_ASmallOneAsADelta_EveryPointRestoresIdentical'], recovery=['x:PilotDeltaChainComponentTests.ADeltaTheServerFailed_LeavesTheChainAsItWas_ThePointRestoresTheOldVersion_TheNextRunSendsTheDelta'], concurrency=NA('one run per set'),
    integrity=[E2E + 'EveryPointOfADeltaChainRestoresExactly_AndALongChainStartsANewFullCopy', 'x:PilotDeltaChainComponentTests.Incremental_EachDeltaIsSmallAndHoldsOnlyNewChunks_EveryPointRestoresIdentical_PastTheLimitANewFullCopy', 'x:PilotDeltaChainComponentTests.Differential_EachDeltaCarriesEveryChangeSinceTheFull_EveryPointRestoresIdentical', 'x:PilotDeltaChainComponentTests.ADeltaTheServerFailed_LeavesTheChainAsItWas_ThePointRestoresTheOldVersion_TheNextRunSendsTheDelta', 'x:PilotDeltaChainIntegrationTests.AChangeOverTheDeltaRatio_IsSentAsANewFullCopy_ASmallOneAsADelta_EveryPointRestoresIdentical'], security=NA('same as BK-01'))
cap('BK-03', 'Backup', 'File backup, restic engine', True, 'A/ResticRunner.cs, S/ResticStore.cs, S/ApiRestic.cs',
    happy=['qa:journeys/j6', 'x:ResticComponentTests.KnownTree_BacksUp_ChecksClean_RestoresIdentical_SecondRunAddsNothing', 'x:ResticTests.ResticBacksUpToOurServer_OnlyChangesAreSent_RestoresExactly_AndTheServerProtectsTheRepository'],
    failure=['x:ResticComponentTests.AnotherKey_ReadsNothing', 'x:ResticComponentTests.DamagedPack_IsFoundByCheck_AndItsRestoreFailsLoudly', 'x:SourceTests.WholeSourceGone_IsAFailure_LastBackupNotRefreshed_FilesKept_AndComesBackCleanly', 'x:ProcessTests.HungRestic_BackupEndsAsAFailure_AtTheIdleLimit_AndTheNextBackupRestoresIdentical'],
    boundary=['x:ResticComponentTests.KnownTree_BacksUp_ChecksClean_RestoresIdentical_SecondRunAddsNothing', 'x:ResticTests.SingleFileRestore_NamesWithBracketsAndStars'],
    recovery=['x:ResticComponentTests.StoppedInTheMiddle_RepositoryChecksClean_NextBackupCompletes_RestoresIdentical', 'x:ResticTests.BackupKilledMidwayLeavesNothingBroken_TheNextRunCompletesAndEverythingRestores', 'x:InterruptionTests.AgentKilledMidBackup_NextBackupRunsAtOnce_HistoryShowsTheFailure_NotRunning'],
    concurrency=[], integrity=['qa:journeys/j6', 'x:ResticComponentTests.KnownTree_BacksUp_ChecksClean_RestoresIdentical_SecondRunAddsNothing', 'x:ResticComponentTests.DamagedPack_IsFoundByCheck_AndItsRestoreFailsLoudly', 'x:ResticTests.ResticBacksUpToOurServer_OnlyChangesAreSent_RestoresExactly_AndTheServerProtectsTheRepository'],
    security=['x:ResticComponentTests.AnotherKey_ReadsNothing', 'x:ResticTests.ResticBacksUpToOurServer_OnlyChangesAreSent_RestoresExactly_AndTheServerProtectsTheRepository'], regression=[1, 4, 17, 21, 22, 23])
cap('BK-04', 'Backup', 'Filters, skipped folders, links', True, 'A/BackupRun.cs (Scanner)',
    happy=['x:OptionsTests.SkippedFoldersAndFilters_AreMissingFromTheRestore', 'x:FormatTests.FiltersExcludeLikeAhsay', 'x:PilotFilterComponentTests.SkippedFolderAndExcludeFilter_AreAbsentFromTheRestore_EverythingElseIdentical_ASiblingWithTheSamePrefixIsKept', 'x:PilotFilterComponentTests.AnIncludeOnlyFilter_KeepsOnlyItsFiles_InEveryFolder_AndTheyRestoreIdentical'], failure=NA('configuration only'),
    boundary=['x:OptionsTests.Links_FollowedOnlyWhenTheOptionIsOn', 'x:PilotFilterComponentTests.SkippedFolderAndExcludeFilter_AreAbsentFromTheRestore_EverythingElseIdentical_ASiblingWithTheSamePrefixIsKept', 'x:PilotFilterComponentTests.ALinkedFolder_IsFollowedOnlyWithTheOption_AndALinkBackToItsParentIsNotWalkedAgain'], recovery=NA('stateless'), concurrency=NA('stateless'),
    integrity=['x:OptionsTests.SkippedFoldersAndFilters_AreMissingFromTheRestore', 'x:PilotFilterComponentTests.SkippedFolderAndExcludeFilter_AreAbsentFromTheRestore_EverythingElseIdentical_ASiblingWithTheSamePrefixIsKept', 'x:PilotFilterComponentTests.AnIncludeOnlyFilter_KeepsOnlyItsFiles_InEveryFolder_AndTheyRestoreIdentical', 'x:PilotFilterComponentTests.ALinkedFolder_IsFollowedOnlyWithTheOption_AndALinkBackToItsParentIsNotWalkedAgain'], security=NA('no access decision'))
cap('BK-05', 'Backup', 'Unreadable data is an error (permission denied, locked file, folder gone)', True, 'A/BackupRun.cs, A/Sources.cs',
    happy=['x:SourceTests.OneOfTwoSourcesGone_IsSuccessWithError_ShownAsProblem_NotAsWarning'],
    failure=['x:PermissionTests.AFolderTheAgentMayNotRead_IsAnError_NotADeletion_AndEverythingElseRestoresIdentical', 'x:SourceTests.SubfolderNotReadable_IsAnError_NotAWarning'], boundary=[], recovery=['qa:failure-recovery/f5'], concurrency=NA('-'),
    integrity=['x:PermissionTests.AFolderTheAgentMayNotRead_IsAnError_NotADeletion_AndEverythingElseRestoresIdentical', 'qa:failure-recovery/f5'], security=NA('-'), regression=[2])
cap('BK-06', 'Backup', 'Volume Shadow Copy (open files)', True, 'A/Vss.cs', happy=['x:PilotVssComponentTests.ALockedFile_IsReadFromTheSnapshot_WithItsContentAtSnapshotTime_AndTheSnapshotIsRemovedAfterwards', 'x:PilotVssComponentTests.ABackupWithShadowCopy_SendsALockedFile_IsASuccessWithoutError_RestoresIdentical_AndLeavesNoShadowCopy', 'x:PilotVssIntegrationTests.ALockedFile_IsBackedUpThroughTheShadowCopy_RestoresIdentical_NoShadowCopyLeft'], failure=['x:PilotVssComponentTests.WhenTheRunFails_TheShadowCopyIsRemovedToo_AndTheNextRunCompletes', 'x:PilotVssIntegrationTests.ARunTheServerStopsForTheQuota_RemovesItsShadowCopy_AndOnceThereIsRoom_TheNextRunCompletes'], boundary=['x:PilotVssComponentTests.SourcesWithoutADriveLetter_GetNoShadowCopy_AndNoWarning'], recovery=['x:PilotVssComponentTests.ALockedFile_IsReadFromTheSnapshot_WithItsContentAtSnapshotTime_AndTheSnapshotIsRemovedAfterwards', 'x:PilotVssComponentTests.WhenTheRunFails_TheShadowCopyIsRemovedToo_AndTheNextRunCompletes', 'x:PilotVssIntegrationTests.ARunTheServerStopsForTheQuota_RemovesItsShadowCopy_AndOnceThereIsRoom_TheNextRunCompletes'], concurrency=NA('-'), integrity=['x:PilotVssComponentTests.ALockedFile_IsReadFromTheSnapshot_WithItsContentAtSnapshotTime_AndTheSnapshotIsRemovedAfterwards', 'x:PilotVssComponentTests.ABackupWithShadowCopy_SendsALockedFile_IsASuccessWithoutError_RestoresIdentical_AndLeavesNoShadowCopy', 'x:PilotVssIntegrationTests.ALockedFile_IsBackedUpThroughTheShadowCopy_RestoresIdentical_NoShadowCopyLeft', 'x:PilotVssIntegrationTests.ARunTheServerStopsForTheQuota_RemovesItsShadowCopy_AndOnceThereIsRoom_TheNextRunCompletes'], security=NA('-'), regression=[5])
cap('BK-07', 'Backup', 'Maximum duration, stop from the admin site', False, 'A/BackupRun.cs, S/SetControl.cs',
    happy=['x:OptionsTests.MaximumDuration_StopsTheBackup_AndTheNextRunGoesOn', 'x:SetControlTests.BackUpNow_And_Stop_FromTheServer', 'x:PilotStopComponentTests.AtTheMaximumDuration_WhatWasSentRestoresIdentical_NothingIsDeleted_TheNextRunSendsOnlyTheRest', 'x:PilotMaxDurationComponentTests.AtTheMaximumDuration_TheRunIsReportedAsStopped_NotAsASuccess'], failure=NA('-'), boundary=['x:PilotStopComponentTests.ExactlyAtTheLimit_TheRunGoesOn_OneTickPast_ItStops_WithoutALimit_ItNeverStops'],
    recovery=['x:OptionsTests.MaximumDuration_StopsTheBackup_AndTheNextRunGoesOn', 'x:PilotStopComponentTests.AtTheMaximumDuration_WhatWasSentRestoresIdentical_NothingIsDeleted_TheNextRunSendsOnlyTheRest'], concurrency=NA('-'), integrity=['x:PilotStopComponentTests.AtTheMaximumDuration_WhatWasSentRestoresIdentical_NothingIsDeleted_TheNextRunSendsOnlyTheRest', 'x:PilotStopComponentTests.ExactlyAtTheLimit_TheRunGoesOn_OneTickPast_ItStops_WithoutALimit_ItNeverStops'], security=NA('-'))
cap('BK-08', 'Backup', 'Upload limit, compression, low priority, wait while busy', False, 'A/Resources.cs, A/BackupRun.cs',
    happy=['x:OptionsTests.UploadLimit_SetOnTheServer_SlowsARealBackupToTheLimit', 'x:OptionsTests.Compression_ReachesTheEngine_AndChangesWhatIsStored', 'x:ResourceTests.UploadLimit_And_Compression', 'x:PilotResourcesComponentTests.Compression_MaxStoresFarLessThanNone_BothRestoreIdentical', 'x:PilotResourcesComponentTests.UploadLimit_ARunNeverSendsFasterThanTheLimit_AndRestoresIdentical'],
    failure=NA('-'), boundary=['x:PilotResourcesComponentTests.Compression_OfDataThatDoesNotCompress_StoresNoMoreThanWithout_AndRestoresIdentical', 'x:PilotResourcesComponentTests.UploadLimit_ARunNeverSendsFasterThanTheLimit_AndRestoresIdentical', 'x:PilotResourcesIntegrationTests.UploadLimitAndCompressionFromTheServer_NeverFasterThanTheLimit_TextStoredSmaller_PhotoNotBigger_RestoresIdentical'], recovery=NA('-'), concurrency=NA('-'), integrity=['x:OptionsTests.Compression_ReachesTheEngine_AndChangesWhatIsStored', 'x:PilotResourcesComponentTests.Compression_MaxStoresFarLessThanNone_BothRestoreIdentical', 'x:PilotResourcesComponentTests.Compression_OfDataThatDoesNotCompress_StoresNoMoreThanWithout_AndRestoresIdentical', 'x:PilotResourcesComponentTests.UploadLimit_ARunNeverSendsFasterThanTheLimit_AndRestoresIdentical', 'x:PilotResourcesIntegrationTests.UploadLimitAndCompressionFromTheServer_NeverFasterThanTheLimit_TextStoredSmaller_PhotoNotBigger_RestoresIdentical'], security=NA('-'))
cap('BK-09', 'Backup', 'Pre- and post-commands (with time limit)', False, 'A/BackupRun.cs (Commands)',
    happy=[E2E + 'PreAndPostCommandsRunAndAreLogged_SchedulerCatchesUpMissedRuns'], failure=['x:OptionsTests.FailedPreCommand_StopsTheBackupOnlyWhenAsked'],
    boundary=['x:ProcessTests.StuckPreCommand_EndsAtTheLimit_AndTheBackupGoesOn'], recovery=NA('-'), concurrency=NA('-'), integrity=NA('-'), security=[], regression=[5])
cap('BK-10', 'Backup', 'Local copy beside the online backup', False, 'A/LocalRepo.cs',
    happy=['x:FeatureTests.LocalCopyIsWrittenBesideTheOnlineBackupAndRestoresWithoutTheServer', 'x:DestinationTests.LocalOnly_And_ServerPlusLocalCopy_WithRestic'], failure=[], boundary=[], recovery=[], concurrency=NA('-'),
    integrity=['x:FeatureTests.LocalCopyIsWrittenBesideTheOnlineBackupAndRestoresWithoutTheServer'], security=NA('-'))
# ------------------------------------------------------------------ application sources
cap('DB-01', 'Databases', 'SQL Server: full, differential, log; free-space check', True, 'A/Sources.cs (SqlBackup)',
    happy=['x:FeatureTests.MssqlFullAndLogBackupsWithNativeBackupFiles_RestoreGivesTheBakFiles', 'x:FeatureTests.MssqlWeeklyFullAndDailyDifferential_TheFullStaysInEveryPoint_AnotherProgramsFullForcesOurs'],
    failure=['x:SqlScaleTests.FailingDatabase_IsAnError_AllFailing_IsAFailure', 'x:SqlScaleTests.NoDatabaseFound_IsAFailure_NotAnEmptySuccess'],
    boundary=['x:SqlScaleTests.NotEnoughRoom_TheDatabaseIsSkippedWithAClearMessage_BigOnesGetLargerBuffers', 'x:SqlScaleTests.SqlcmdWritingMuchToStderr_DoesNotHangTheBackup'],
    recovery=['x:SqlScaleTests.HungDatabaseBackup_IsStoppedAtTheLimit_TheRunEnds'], concurrency=NA('-'),
    integrity=['x:FeatureTests.MssqlFullAndLogBackupsWithNativeBackupFiles_RestoreGivesTheBakFiles'], security=['x:OptionsTests.SqlLogin_LikeSa_PasswordNeverOnTheCommandLine'], regression=[3, 5, 6])
cap('DB-02', 'Databases', 'MySQL / PostgreSQL dumps', True, 'A/DbDump.cs',
    happy=['x:DbHyperVTests.MySql_EveryDatabaseDumped_PasswordNeverInTheLog_DumpsTravelAsDeltas_LoadIntoANewDatabase'], failure=['x:DbHyperVTests.MySqlAndPostgres_OnRestic_AndAFailingDatabaseIsReported'],
    boundary=[], recovery=[], concurrency=NA('-'), integrity=['x:DbHyperVTests.MySql_EveryDatabaseDumped_PasswordNeverInTheLog_DumpsTravelAsDeltas_LoadIntoANewDatabase'],
    security=['x:DbHyperVTests.MySql_EveryDatabaseDumped_PasswordNeverInTheLog_DumpsTravelAsDeltas_LoadIntoANewDatabase'], regression=[3, 5])
cap('DB-03', 'Databases', 'Oracle RMAN', False, 'A/Oracle.cs', happy=['x:EnterpriseTests.Oracle_RmanOnlineBackup_PasswordOnlyOnStdin_FailedRunKeepsTheLastBackup'],
    failure=['x:EnterpriseTests.Oracle_RmanOnlineBackup_PasswordOnlyOnStdin_FailedRunKeepsTheLastBackup'], boundary=[], recovery=[], concurrency=NA('-'), integrity=[], security=['x:EnterpriseTests.Oracle_RmanOnlineBackup_PasswordOnlyOnStdin_FailedRunKeepsTheLastBackup'])
cap('DB-04', 'Databases', 'HCL Domino', False, 'A/Domino.cs', happy=['x:EnterpriseTests.Domino_FoldersFromNotesIni_CacheFlushedBeforeTheCopy'], failure=[], boundary=[], recovery=[], concurrency=NA('-'), integrity=[], security=NA('-'))
cap('AP-01', 'Applications', 'System State (wbadmin / ntbackup)', True, 'A/Sources.cs (SystemState)', happy=[], failure=[], boundary=[], recovery=[], concurrency=NA('-'), integrity=[], security=NA('-'), regression=[5])
cap('AP-02', 'Applications', 'Bare-metal image', True, 'A/DiskImage.cs', happy=['x:BareMetalTests.WholeComputerImageIsSentAsChangedBlocksOnly_AndRestoresInTheLayoutWindowsRecoveryReads'],
    failure=['x:BareMetalTests.ImageToolFailureKeepsThePreviousImage_AndWindows2003UsesNtbackup'], boundary=[], recovery=[], concurrency=NA('-'),
    integrity=['x:BareMetalTests.WholeComputerImageIsSentAsChangedBlocksOnly_AndRestoresInTheLayoutWindowsRecoveryReads'], security=NA('-'))
cap('AP-03', 'Applications', 'Hyper-V virtual machines', False, 'A/HyperV.cs', happy=['x:DbHyperVTests.HyperV_VmsExportedOnline_OnlyChangedDiskBlocksSent_RestoredAndImported'], failure=[], boundary=[], recovery=[], concurrency=NA('-'),
    integrity=['x:DbHyperVTests.HyperV_VmsExportedOnline_OnlyChangedDiskBlocksSent_RestoredAndImported'], security=NA('-'))
cap('AP-04', 'Applications', 'VMware ESXi / vCenter', False, 'A/VMware.cs', happy=['x:EnterpriseTests.VMware_SnapshotCopyOfEveryVm_SnapshotsAlwaysRemoved_RestoredAsANewVm'],
    failure=['x:EnterpriseTests.VMware_SnapshotCopyOfEveryVm_SnapshotsAlwaysRemoved_RestoredAsANewVm'], boundary=[], recovery=[], concurrency=NA('-'), integrity=[], security=[])
cap('AP-05', 'Applications', 'Microsoft 365 (mail, OneDrive, Teams)', False, 'A/M365.cs', happy=['x:M365Tests.Microsoft365_ItemLevel_OnlyChangesFetched_DeletedMailRestoredIntoTheMailbox'], failure=[], boundary=[], recovery=[], concurrency=NA('-'),
    integrity=['x:M365Tests.Microsoft365_ItemLevel_OnlyChangesFetched_DeletedMailRestoredIntoTheMailbox'], security=[])
cap('AP-06', 'Applications', 'Google Workspace', False, 'A/GoogleWorkspace.cs', happy=['x:GoogleTests.GoogleWorkspace_GmailAndDrive_ItemLevel_ChangesOnly_RestoreIntoGoogle'], failure=[], boundary=[], recovery=[], concurrency=NA('-'),
    integrity=['x:GoogleTests.GoogleWorkspace_GmailAndDrive_ItemLevel_ChangesOnly_RestoreIntoGoogle'], security=[])
cap('AP-07', 'Applications', 'External programs: time limit, both streams, process tree', True, 'C/ProcessRunner.cs',
    happy=['x:ProcessTests.ProgramWritingMuchToBothStreams_IsReadToTheEnd', 'x:PilotProcessIntegrationTests.PreAndPostCommandsWritingMuchToBothStreams_AreReadToTheEnd_TheBackupSucceedsAndRestoresIdentical'], failure=['x:ProcessTests.StuckProgram_IsKilledWithItsChildren_AtTheLimit', 'x:PilotProcessComponentTests.AStuckPreCommandWithAChild_IsStoppedAtTheLimitWithItsChild_TheBackupGoesOnAndRestoresIdentical'],
    boundary=['x:SqlScaleTests.SqlcmdWritingMuchToStderr_DoesNotHangTheBackup', 'x:PilotProcessIntegrationTests.PreAndPostCommandsWritingMuchToBothStreams_AreReadToTheEnd_TheBackupSucceedsAndRestoresIdentical'], recovery=['x:SqlScaleTests.HungDatabaseBackup_IsStoppedAtTheLimit_TheRunEnds', 'x:PilotProcessComponentTests.AStuckPreCommandWithAChild_IsStoppedAtTheLimitWithItsChild_TheBackupGoesOnAndRestoresIdentical'], concurrency=NA('-'), integrity=NA('-'), security=[], regression=[5])
# ------------------------------------------------------------------ restore
cap('RS-01', 'Restore', 'Restore all / a folder / a file, native, any point', True, 'A/Restore.cs',
    happy=['x:RestoreComponentTests.EveryFile_Identical_WithItsTime_AndTheFilterGivesOnlyItsFile', 'qa:journeys/j4', E2E + 'FullCycle_NewUpdatedPermissionDeleted_DeltaForLargeFiles_RestoreAnyPoint', 'x:PilotRestoreIntegrationTests.RS01_ThreePoints_Point2OneFile_ExactlyThatFile_ByteIdenticalToPoint2_Point1Whole'], failure=['x:RestoreComponentTests.ADamagedOrMissingObject_IsAFailedFile_TheOthersAreRestored_NoHalfFileLeft', 'x:PilotRestoreIntegrationTests.RS01_AnObjectDamagedOnTheServerDisk_IsAFailedFile_TheOthersRestoreIdentical_NoHalfFile_ServerToldError'],
    boundary=['x:RestoreComponentTests.EveryFile_Identical_WithItsTime_AndTheFilterGivesOnlyItsFile', 'qa:journeys/j8', 'qa:journeys/j4', 'x:PilotRestoreIntegrationTests.RS01_ThreePoints_Point2OneFile_ExactlyThatFile_ByteIdenticalToPoint2_Point1Whole'], recovery=['x:RestoreComponentTests.ADamagedOrMissingObject_IsAFailedFile_TheOthersAreRestored_NoHalfFileLeft', 'qa:failure-recovery/f4', 'x:ReliabilityTests.RestoreInterruptedMidway_RunAgain_GivesIdenticalFiles'], concurrency=[],
    integrity=['x:RestoreComponentTests.EveryFile_Identical_WithItsTime_AndTheFilterGivesOnlyItsFile', 'qa:journeys/j3', 'qa:journeys/j4', 'qa:failure-recovery/f4', 'x:PilotRestoreIntegrationTests.RS01_ThreePoints_Point2OneFile_ExactlyThatFile_ByteIdenticalToPoint2_Point1Whole', 'x:PilotRestoreIntegrationTests.RS01_AnObjectDamagedOnTheServerDisk_IsAFailedFile_TheOthersRestoreIdentical_NoHalfFile_ServerToldError'], security=['x:WebRestoreTests.CustomerRestoresChosenFilesFromTheWebsite_WithTheEncryptionPassword'])
cap('RS-02', 'Restore', 'Restore to the original place: overwrite rules, existing files', True, 'A/Restore.cs', happy=['x:RestoreComponentTests.OriginalPlace_WithoutOverwrite_KeepsTheFileAndSaysSo_WithOverwrite_ReplacesIt', 'qa:journeys/j4', 'x:PilotRestoreIntegrationTests.RS02_OriginalPlace_OverwriteOff_KeepsAndCountsSkipped_On_Replaces_ServerGetsTheResult'], failure=['x:RestoreComponentTests.ADamagedOrMissingObject_IsAFailedFile_TheOthersAreRestored_NoHalfFileLeft', 'x:PilotRestoreIntegrationTests.RS02_OriginalPlace_Overwrite_WithADamagedObject_TheExistingFileIsUntouched_ResultIsError'], boundary=['x:RestoreComponentTests.OriginalPlace_WithoutOverwrite_KeepsTheFileAndSaysSo_WithOverwrite_ReplacesIt', 'x:PilotRestoreIntegrationTests.RS02_OriginalPlace_OverwriteOff_KeepsAndCountsSkipped_On_Replaces_ServerGetsTheResult'], recovery=['x:RestoreComponentTests.ADamagedOrMissingObject_IsAFailedFile_TheOthersAreRestored_NoHalfFileLeft', 'x:PilotRestoreIntegrationTests.RS02_OriginalPlace_Overwrite_WithADamagedObject_TheExistingFileIsUntouched_ResultIsError'], concurrency=NA('-'), integrity=['x:RestoreComponentTests.OriginalPlace_WithoutOverwrite_KeepsTheFileAndSaysSo_WithOverwrite_ReplacesIt', 'qa:journeys/j4', 'x:PilotRestoreIntegrationTests.RS02_OriginalPlace_OverwriteOff_KeepsAndCountsSkipped_On_Replaces_ServerGetsTheResult', 'x:PilotRestoreIntegrationTests.RS02_OriginalPlace_Overwrite_WithADamagedObject_TheExistingFileIsUntouched_ResultIsError'], security=NA('-'))
cap('RS-03', 'Restore', 'Restore, restic engine', True, 'A/ResticRunner.cs', happy=['qa:journeys/j6', 'x:ResticComponentTests.KnownTree_BacksUp_ChecksClean_RestoresIdentical_SecondRunAddsNothing', 'x:ResticTests.ResticBacksUpToOurServer_OnlyChangesAreSent_RestoresExactly_AndTheServerProtectsTheRepository'],
    failure=['x:ResticComponentTests.DamagedPack_IsFoundByCheck_AndItsRestoreFailsLoudly', 'x:ResticComponentTests.AnotherKey_ReadsNothing'],
    boundary=['x:ResticComponentTests.KnownTree_BacksUp_ChecksClean_RestoresIdentical_SecondRunAddsNothing', 'x:ResticTests.SingleFileRestore_NamesWithBracketsAndStars'],
    recovery=['x:ResticComponentTests.StoppedInTheMiddle_RepositoryChecksClean_NextBackupCompletes_RestoresIdentical', 'x:ReliabilityTests.RestoreInterruptedMidway_RunAgain_GivesIdenticalFiles'], concurrency=NA('-'),
    integrity=['qa:journeys/j6', 'x:ResticComponentTests.KnownTree_BacksUp_ChecksClean_RestoresIdentical_SecondRunAddsNothing', 'x:ResticTests.ResticBacksUpToOurServer_OnlyChangesAreSent_RestoresExactly_AndTheServerProtectsTheRepository'],
    security=['x:ResticComponentTests.AnotherKey_ReadsNothing'], regression=[23])
cap('RS-04', 'Restore', 'Restore from the website (download)', True, 'S/WebRestore.cs, Web/restore.js', happy=['x:WebRestoreTests.CustomerRestoresChosenFilesFromTheWebsite_WithTheEncryptionPassword'], failure=[], boundary=[], recovery=[],
    concurrency=NA('-'), integrity=['x:WebRestoreTests.CustomerRestoresChosenFilesFromTheWebsite_WithTheEncryptionPassword'], security=['x:WebRestoreTests.CustomerRestoresChosenFilesFromTheWebsite_WithTheEncryptionPassword'])
cap('RS-05', 'Restore', 'Restore on a new computer (key recovery, local index rebuilt)', True, 'A/AgentApp.cs (Key), A/Restore.cs',
    happy=[E2E + 'KeyRecoveryAndRestoreOnANewComputer', E2E + 'LostLocalIndexIsRebuiltFromTheServerWithoutResendingEverything', 'x:PilotRestoreComponentTests.RS05_NewComputer_PasswordKeyComesBack_EveryFileRestoresIdentical_AndTheKeyIsKept', 'x:PilotRestoreIntegrationTests.RS05_NewComputer_RandomKey_WithoutRecoveryNoKey_RecoveredKeyRestoresAll_IndexRebuilt_NothingResent'], failure=['x:PilotRestoreComponentTests.RS05_NewComputer_WrongPassword_IsRefused_NoKeyIsKept_ARandomKeyNeedsRecovery', 'x:PilotRestoreIntegrationTests.RS05_NewComputer_RandomKey_WithoutRecoveryNoKey_RecoveredKeyRestoresAll_IndexRebuilt_NothingResent'], boundary=NA('-'), recovery=[E2E + 'LostLocalIndexIsRebuiltFromTheServerWithoutResendingEverything', 'x:PilotRestoreComponentTests.RS05_NewComputer_RecoveredKey_Restores_AndTheLocalIndexIsRebuiltFromTheServerList', 'x:PilotRestoreIntegrationTests.RS05_NewComputer_RandomKey_WithoutRecoveryNoKey_RecoveredKeyRestoresAll_IndexRebuilt_NothingResent'],
    concurrency=NA('-'), integrity=[E2E + 'KeyRecoveryAndRestoreOnANewComputer', 'x:PilotRestoreComponentTests.RS05_NewComputer_PasswordKeyComesBack_EveryFileRestoresIdentical_AndTheKeyIsKept', 'x:PilotRestoreComponentTests.RS05_NewComputer_RecoveredKey_Restores_AndTheLocalIndexIsRebuiltFromTheServerList', 'x:PilotRestoreIntegrationTests.RS05_NewComputer_RandomKey_WithoutRecoveryNoKey_RecoveredKeyRestoresAll_IndexRebuilt_NothingResent'], security=[E2E + 'KeyRecoveryAndRestoreOnANewComputer', 'x:PilotRestoreComponentTests.RS05_NewComputer_WrongPassword_IsRefused_NoKeyIsKept_ARandomKeyNeedsRecovery'])
cap('RS-06', 'Restore', 'Automatic restore test', True, 'A/AgentApp.cs (RestoreTest)', happy=['x:FeatureTests.AutomaticRestoreTestComparesWithTheSourceAndReportsToTheServer', 'x:PilotRestoreComponentTests.RS06_UnchangedSources_RestoreInIsolation_AllEqual_ReportedToTheServer_TempFolderGone', 'x:PilotRestoreIntegrationTests.RS06_RestoreTest_ComparesWithTheSource_DamagedInStorageFails_ChangedSourceNotACandidate_TempGone_ServerRecords'],
    failure=['x:SourceTests.RestoreTest_WithTheSourceOffline_IsNotChecked_NotFailed', 'x:PilotRestoreComponentTests.RS06_AnObjectDamagedInStorage_IsAFailedFile_NotPassed_TheOthersStillChecked_TempFolderGone', 'x:PilotRestoreComponentTests.RS06_ARestoreThatSucceedsButGivesOtherBytes_IsReportedDifferent_TheComparisonIsWithTheSource', 'x:PilotRestoreIntegrationTests.RS06_RestoreTest_ComparesWithTheSource_DamagedInStorageFails_ChangedSourceNotACandidate_TempGone_ServerRecords', 'x:PilotRestoreComponentTests.RS06_AReportTheServerDidNotTake_IsTriedAgain_NotMarkedDoneFor30Days'], boundary=['x:PilotRestoreComponentTests.RS06_AChangedOrMissingSource_IsNotACandidate_NothingToCompare_IsZeroChecked_NeverFailed', 'x:PilotRestoreComponentTests.RS06_TheSampleSize_IsRespected_EachPickedFileOnce', 'x:PilotRestoreComponentTests.RS06_Due_OnlyAfterASuccessfulBackup_ThenOncePer30Days', 'x:PilotRestoreIntegrationTests.RS06_RestoreTest_ComparesWithTheSource_DamagedInStorageFails_ChangedSourceNotACandidate_TempGone_ServerRecords', 'x:RestoreTestDueComponentTests.AfterAStoppedRun_NoRestoreTest_AfterASuccessfulRun_ARestoreTest'], recovery=['x:PilotRestoreComponentTests.RS06_AReportTheServerDidNotTake_IsTriedAgain_NotMarkedDoneFor30Days'], concurrency=NA('-'), integrity=['x:FeatureTests.AutomaticRestoreTestComparesWithTheSourceAndReportsToTheServer', 'x:PilotRestoreComponentTests.RS06_UnchangedSources_RestoreInIsolation_AllEqual_ReportedToTheServer_TempFolderGone', 'x:PilotRestoreComponentTests.RS06_AnObjectDamagedInStorage_IsAFailedFile_NotPassed_TheOthersStillChecked_TempFolderGone', 'x:PilotRestoreComponentTests.RS06_ARestoreThatSucceedsButGivesOtherBytes_IsReportedDifferent_TheComparisonIsWithTheSource', 'x:PilotRestoreIntegrationTests.RS06_RestoreTest_ComparesWithTheSource_DamagedInStorageFails_ChangedSourceNotACandidate_TempGone_ServerRecords'], security=NA('-'), regression=[12])
# ------------------------------------------------------------------ server storage
cap('ST-01', 'Server storage', 'Run commit (journal, roll forward), truncated/half objects refused', True, 'S/SetStore.cs',
    happy=['x:SetStoreComponentTests.TwoRuns_EveryPointHoldsExactlyItsFiles_AndTheStoredBytesAreTheSentOnes', E2E + 'FullCycle_NewUpdatedPermissionDeleted_DeltaForLargeFiles_RestoreAnyPoint'],
    failure=['x:SetStoreComponentTests.TruncatedWrongOrEscapingObjects_AreRefused_AndLeaveNoFile', E2E + 'TruncatedUploadIsRejectedAndNeverCommitted', 'x:InterruptionTests.EndOfRunSentTwice_IsRecordedOnce_AndTheRepeatIsNotAnError'],
    boundary=['x:SetStoreComponentTests.TruncatedWrongOrEscapingObjects_AreRefused_AndLeaveNoFile'], recovery=['x:SetStoreComponentTests.AStoppedRun_LeavesNoTrace_AndAJournaledRun_IsCompletedByTheNextStart', E2E + 'CrashDuringCommitIsRolledForward_AndAnUncommittedRunLeavesNothing', 'qa:failure-recovery/f1'],
    concurrency=['x:SetStoreComponentTests.ASecondRunOfTheSameSet_IsRefusedWhileTheFirstIsOpen', 'qa:failure-recovery/f3'],
    integrity=['x:SetStoreComponentTests.TwoRuns_EveryPointHoldsExactlyItsFiles_AndTheStoredBytesAreTheSentOnes', 'x:SetStoreComponentTests.AStoppedRun_LeavesNoTrace_AndAJournaledRun_IsCompletedByTheNextStart', E2E + 'CrashDuringCommitIsRolledForward_AndAnUncommittedRunLeavesNothing'], security=['x:FuzzTests.JunkAndAttacks_AreRefusedClearly_TheServerKeepsRunning_NothingLeaks'], regression=[11])
cap('ST-02', 'Server storage', 'Run lease, interrupted runs closed and recorded', True, 'S/SetStore.cs, S/Api.cs (SweepInterrupted)',
    happy=['x:LeaseComponentTests.OpenWhileAlive_ClosedFiveMinutesAfterTheLastSignOfLife_NamedOnce_ThenTheSetBeginsAgain', 'x:InterruptionTests.LiveBackupThatKeepsReporting_IsNotClosedBySweeper'], failure=['x:LeaseComponentTests.TheComputersReport_ClosesAnOpenRunAtOnce_AndIsHarmlessOtherwise', 'x:InterruptionTests.ComputerNeverComesBack_RunIsClosedAsInterrupted_AndAnotherProcessCanBackUp'],
    boundary=['x:LeaseComponentTests.OpenWhileAlive_ClosedFiveMinutesAfterTheLastSignOfLife_NamedOnce_ThenTheSetBeginsAgain', ], recovery=['x:LeaseComponentTests.ACommittingRun_IsNeverExpired', 'x:InterruptionTests.AgentKilledMidBackup_NextBackupRunsAtOnce_HistoryShowsTheFailure_NotRunning', 'x:InterruptionTests.ServerDownMidBackupAndBack_NextBackupRunsAtOnce', 'qa:journeys/j5'],
    concurrency=['qa:failure-recovery/f3'], integrity=['x:LeaseComponentTests.ACommittingRun_IsNeverExpired', 'qa:journeys/j5'], security=NA('-'), regression=[1, 11])
cap('ST-03', 'Server storage', 'Retention (days / jobs / GFS), never the current version', True, 'S/SetStore.cs (ApplyRetention)',
    happy=['qa:journeys/j8', 'x:RetentionTests.Policy_KeepsExactlyTheExpectedPoints', E2E + 'RetentionDeletesOnlyWhatNoKeptPointNeeds_CurrentIsNeverTouched', 'x:FormatTests.RetentionByDaysJobsAndAdvanced',
           'x:RetentionTests.RealServer_KeepsOnlyThePolicysPoints_AndEachKeptPointRestoresIdentical_DeltaChainsIncluded', 'x:PilotStorageComponentTests.Retention_OnTheStore_ExpiresOnlyPointsOutsideThePolicy_NeverAVersionAKeptPointNeeds_EveryKeptPointIsTheBytesSent', 'x:PilotStorageComponentTests.Retention_Gfs_TheLastBackupOfEachMonth_AtMidMonthNoon_IsKept_TheOthersGo'],
    failure=NA('a policy has no failing input; a bad policy value is a boundary'), boundary=['x:RetentionTests.Boundaries_NoPoints_OnePoint_AllTooOld_ThePointExactlyAtTheLimit', 'x:TimeMachineTests.TwentyFiveDays_SchedulesVersionsAlertsServiceCallsAndMails', 'x:PilotStorageComponentTests.Retention_OnTheStore_ExpiresOnlyPointsOutsideThePolicy_NeverAVersionAKeptPointNeeds_EveryKeptPointIsTheBytesSent'],
    recovery=['x:RetentionTests.RealServer_KeepsOnlyThePolicysPoints_AndEachKeptPointRestoresIdentical_DeltaChainsIncluded', 'x:PilotStorageComponentTests.Retention_AnInterruptedRun_IsFinishedByTheNext_AndALostIndexDoesNotBringExpiredPointsBack'], concurrency=[],
    integrity=['qa:journeys/j8', 'x:RetentionTests.RealServer_KeepsOnlyThePolicysPoints_AndEachKeptPointRestoresIdentical_DeltaChainsIncluded', E2E + 'RetentionDeletesOnlyWhatNoKeptPointNeeds_CurrentIsNeverTouched', 'x:PilotStorageComponentTests.Retention_OnTheStore_ExpiresOnlyPointsOutsideThePolicy_NeverAVersionAKeptPointNeeds_EveryKeptPointIsTheBytesSent', 'x:PilotStorageComponentTests.Retention_AnInterruptedRun_IsFinishedByTheNext_AndALostIndexDoesNotBringExpiredPointsBack', 'x:PilotStorageComponentTests.Retention_Gfs_TheLastBackupOfEachMonth_AtMidMonthNoon_IsKept_TheOthersGo'], security=NA('-'))
cap('ST-04', 'Server storage', 'Verify, damaged object quarantined and resent; index rebuild', True, 'S/SetStore.cs (VerifyAll, Rebuild)',
    happy=['x:SetStoreComponentTests.ADamagedObject_IsQuarantinedAndAskedAgain_ALostIndexIsRebuilt', E2E + 'DamagedObjectIsFoundQuarantinedAndResentFromTheSource', E2E + 'RebuildRecreatesTheIndexFromTheDisk'], failure=['x:SetStoreComponentTests.ADamagedObject_IsQuarantinedAndAskedAgain_ALostIndexIsRebuilt', E2E + 'DamagedObjectIsFoundQuarantinedAndResentFromTheSource', 'x:RetentionTests.ADamagedSet_DoesNotBreakTheBackupsOfTheCustomersOtherSets', 'x:RetentionTests.AFailingMaintenanceTask_IsAlerted_AndTheOtherSetsAreStillMaintained'], boundary=['x:SetStoreComponentTests.ADamagedObject_IsQuarantinedAndAskedAgain_ALostIndexIsRebuilt', ],
    recovery=['x:SetStoreComponentTests.ADamagedObject_IsQuarantinedAndAskedAgain_ALostIndexIsRebuilt', E2E + 'RebuildRecreatesTheIndexFromTheDisk'], concurrency=[], integrity=['x:SetStoreComponentTests.ADamagedObject_IsQuarantinedAndAskedAgain_ALostIndexIsRebuilt', E2E + 'DamagedObjectIsFoundQuarantinedAndResentFromTheSource'], security=NA('-'))
cap('ST-05', 'Server storage', 'Quota (compressed / original), stop new backups, keep existing', True, 'S/Api.cs (Begin, Upload)',
    happy=[E2E + 'QuotaStopsNewBackupsAndKeepsExistingOnes', 'x:ResticTests.ResticRespectsTheQuota'], failure=[E2E + 'QuotaStopsNewBackupsAndKeepsExistingOnes'], boundary=[], recovery=[], concurrency=NA('-'),
    integrity=[E2E + 'QuotaStopsNewBackupsAndKeepsExistingOnes'], security=NA('-'))
cap('ST-06', 'Server storage', 'Server disk full', True, 'S/Api.cs, S/ApiRestic.cs', happy=NA('a failure capability'),
    failure=['qa:failure-recovery/f6', 'x:ReliabilityTests.ServerDiskFullMidBackup_ExistingBackupsUnharmed_NextRunAfterSpaceIsFreedCompletes'], boundary=[], recovery=['qa:failure-recovery/f6'], concurrency=NA('-'),
    integrity=['qa:failure-recovery/f6'], security=NA('-'), regression=[13])
cap('ST-07', 'Server storage', 'Replication to a second server', False, 'S/Replication.cs', happy=['x:FeatureTests.SecondServerReceivesEveryCommitInOrder_AgentsRestoreFromIt_DeletionsWaitForTheDelay'], failure=[], boundary=[], recovery=[], concurrency=[],
    integrity=['x:FeatureTests.SecondServerReceivesEveryCommitInOrder_AgentsRestoreFromIt_DeletionsWaitForTheDelay'], security=[])
cap('ST-08', 'Server storage', 'Earlier versions of the storage open as they are (upgrade with existing data)', True, 'S/SetStore.cs, S/Users.cs',
    happy=['x:UpgradeTests.EveryEarlierVersion_OpensAsItIs_RestoresEveryPoint_AndGoesOn', 'x:PilotStorageComponentTests.Upgrade_AVersion1Store_OpensAsItIs_EveryFileOfEveryPointIsTheManifestsSha256_AndItsSettingsAndUsersOpen', 'x:PilotStorageIntegrationTests.Upgrade_AVersion1ServerWhoseSetIndexWasLost_OpensAndRebuildsIt_EveryOldPointRestoresIdentical_AndBackupsGoOn'], failure=['x:PilotStorageComponentTests.Upgrade_ADamagedObjectInAVersion1Index_IsQuarantined_TheOtherFilesOfEveryPointStayIdentical'], boundary=NA('-'), recovery=['x:PilotStorageComponentTests.Upgrade_AVersion1StoreWithItsIndexLost_IsRebuiltFromTheOldObjects_AndNewRunsGoOnBesideTheOldPoints', 'x:PilotStorageIntegrationTests.Upgrade_AVersion1ServerWhoseSetIndexWasLost_OpensAndRebuildsIt_EveryOldPointRestoresIdentical_AndBackupsGoOn'], concurrency=NA('-'), integrity=['x:UpgradeTests.EveryEarlierVersion_OpensAsItIs_RestoresEveryPoint_AndGoesOn', 'x:PilotStorageComponentTests.Upgrade_AVersion1Store_OpensAsItIs_EveryFileOfEveryPointIsTheManifestsSha256_AndItsSettingsAndUsersOpen', 'x:PilotStorageComponentTests.Upgrade_ADamagedObjectInAVersion1Index_IsQuarantined_TheOtherFilesOfEveryPointStayIdentical', 'x:PilotStorageComponentTests.Upgrade_AVersion1StoreWithItsIndexLost_IsRebuiltFromTheOldObjects_AndNewRunsGoOnBesideTheOldPoints', 'x:PilotStorageIntegrationTests.Upgrade_AVersion1ServerWhoseSetIndexWasLost_OpensAndRebuildsIt_EveryOldPointRestoresIdentical_AndBackupsGoOn'], security=NA('-'))
cap('ST-09', 'Server storage', 'Recycle bin for deleted sets / customers', False, 'S/Recycle.cs', happy=['x:RecycleTests.OneAdministrator_DeletesAtOnce_ToTheRecycleBin_AndRestores', 'x:PilotStorageComponentTests.RecycleBin_ASetAndACustomer_GoAtOnce_ComeBackByteIdentical_AndAreErasedOnlyAfter14Days', 'x:PilotStorageIntegrationTests.RecycleBin_ADeletedSetSurvivesARestart_AndComesBackRestorableIdentical_ACustomerIsNotRestoredOverANewOneOfTheSameName'], failure=['x:PilotStorageComponentTests.RecycleBin_ARestoreOverAnExistingSetOrCustomer_IsRefused_AndChangesNothing', 'x:PilotStorageIntegrationTests.RecycleBin_ADeletedSetSurvivesARestart_AndComesBackRestorableIdentical_ACustomerIsNotRestoredOverANewOneOfTheSameName'], boundary=['x:PilotStorageComponentTests.RecycleBin_ASetAndACustomer_GoAtOnce_ComeBackByteIdentical_AndAreErasedOnlyAfter14Days', 'x:PilotStorageComponentTests.RecycleBin_SurvivesARestart_AndTwoAdministrators_TheAskerCannotApprove_AnOldRequestDeletesNothing'], recovery=['x:PilotStorageComponentTests.RecycleBin_SurvivesARestart_AndTwoAdministrators_TheAskerCannotApprove_AnOldRequestDeletesNothing', 'x:PilotStorageIntegrationTests.RecycleBin_ADeletedSetSurvivesARestart_AndComesBackRestorableIdentical_ACustomerIsNotRestoredOverANewOneOfTheSameName'], concurrency=NA('-'), integrity=['x:PilotStorageComponentTests.RecycleBin_ASetAndACustomer_GoAtOnce_ComeBackByteIdentical_AndAreErasedOnlyAfter14Days', 'x:PilotStorageComponentTests.RecycleBin_SurvivesARestart_AndTwoAdministrators_TheAskerCannotApprove_AnOldRequestDeletesNothing', 'x:PilotStorageIntegrationTests.RecycleBin_ADeletedSetSurvivesARestart_AndComesBackRestorableIdentical_ACustomerIsNotRestoredOverANewOneOfTheSameName'],
    security=['x:RecycleTests.TwoAdministrators_ASecondOneApproves', 'x:PilotStorageComponentTests.RecycleBin_SurvivesARestart_AndTwoAdministrators_TheAskerCannotApprove_AnOldRequestDeletesNothing'])
cap('ST-10', 'Server storage', 'Backup of the server settings', False, 'S/ConfigBackup.cs', happy=['x:ConfigBackupTests.SettingsBackup_NowAndDaily_WithACopy_WithoutTheData', 'x:PilotStorageComponentTests.SettingsBackup_HoldsExactlyTheSettings_NoBackupData_AndRestoresThemByteIdentical_EvenWhenTheSystemFolderIsLost', 'x:PilotStorageIntegrationTests.SettingsBackup_ACopyFolderThatFails_StillMakesIt_AndTheDownloadedBackupRestoresAServerWhoseSettingsWereLost'], failure=['x:PilotStorageComponentTests.SettingsBackup_KeepsTheLast30_DailyAfter23Hours_ACopyFolderThatFails_DoesNotStopTheLocalBackup', 'x:PilotStorageIntegrationTests.SettingsBackup_ACopyFolderThatFails_StillMakesIt_AndTheDownloadedBackupRestoresAServerWhoseSettingsWereLost'], boundary=['x:PilotStorageComponentTests.SettingsBackup_KeepsTheLast30_DailyAfter23Hours_ACopyFolderThatFails_DoesNotStopTheLocalBackup'], recovery=['x:PilotStorageComponentTests.SettingsBackup_HoldsExactlyTheSettings_NoBackupData_AndRestoresThemByteIdentical_EvenWhenTheSystemFolderIsLost', 'x:PilotStorageComponentTests.SettingsBackup_ACrashedEarlierBackup_DoesNotCount_AndTheNextOneIsComplete', 'x:PilotStorageIntegrationTests.SettingsBackup_ACopyFolderThatFails_StillMakesIt_AndTheDownloadedBackupRestoresAServerWhoseSettingsWereLost'], concurrency=NA('-'), integrity=['x:PilotStorageComponentTests.SettingsBackup_HoldsExactlyTheSettings_NoBackupData_AndRestoresThemByteIdentical_EvenWhenTheSystemFolderIsLost', 'x:PilotStorageComponentTests.SettingsBackup_ACrashedEarlierBackup_DoesNotCount_AndTheNextOneIsComplete', 'x:PilotStorageIntegrationTests.SettingsBackup_ACopyFolderThatFails_StillMakesIt_AndTheDownloadedBackupRestoresAServerWhoseSettingsWereLost'], security=['x:PilotStorageComponentTests.SettingsBackup_KeepsTheLast30_DailyAfter23Hours_ACopyFolderThatFails_DoesNotStopTheLocalBackup'])
# ------------------------------------------------------------------ agent runtime
cap('AG-01', 'Agent', 'Scheduler: times, days, several a day, missed runs (computer off, offline)', True, 'A/AgentApp.cs (Due, ServiceLoop)',
    happy=['qa:journeys/j7', 'x:SchedulerTests.ThreeTimesADay_EachRunsOnce_NotTwiceAfterARestart_TheMissedOneOnce', 'x:SchedulerTests.AfterASuccessfulScheduledBackup_TheSetIsNotDueAgain_UntilItsNextTime',
           'x:SetControlTests.SeveralTimesADay_EachWithItsDays', E2E + 'PreAndPostCommandsRunAndAreLogged_SchedulerCatchesUpMissedRuns'],
    failure=['x:SchedulerTests.AFailedRun_IsTriedAgainEvery15Minutes_NotEveryMinute_AndStopsOnceItWorks'],
    boundary=['x:SchedulerTests.Boundary_NoDayOfTheWeekChosen_NeverDue_AndASlotExactlyNowIsDue', 'x:ResourceTests.MissedRun_AfterTheDelay_OnlyWhenOldEnough_OrNever', 'x:TimeMachineTests.TwentyFiveDays_SchedulesVersionsAlertsServiceCallsAndMails'],
    recovery=['qa:journeys/j7', 'x:SchedulerTests.ThreeTimesADay_EachRunsOnce_NotTwiceAfterARestart_TheMissedOneOnce', 'x:ResourceTests.MissedOrCutOffByTheInternet_StartsWhenItIsBack'],
    concurrency=NA('one service loop per computer'), integrity=NA('decides when, stores nothing'), security=NA('-'), regression=[22])
cap('AG-02', 'Agent', '"Back up now" and "Stop" from the server reach the computer', True, 'A/AgentApp.cs (RunRequested, StopCheck)',
    happy=['x:SetControlTests.BackUpNow_And_Stop_FromTheServer', 'qa:journeys/j3'], failure=[], boundary=NA('-'), recovery=['qa:journeys/j5'], concurrency=['qa:failure-recovery/f3'], integrity=['qa:journeys/j3'], security=NA('-'))
cap('AG-03', 'Agent', 'Settings changed on the server reach the computer', True, 'S/SetControl.cs, A/AgentApp.cs (Profile)',
    happy=['x:OptionsTests.EveryOption_ChangedOnTheServer_ArrivesAtTheComputer', 'qa:journeys/j2'], failure=[], boundary=[], recovery=NA('-'), concurrency=[], integrity=NA('-'), security=['x:CustomerTests.Customer_ChangesOnlyWhatItsProviderAllows'])
cap('AG-04', 'Agent', 'Heartbeat, open-run note, report of a dead run', True, 'A/BackupRun.cs, A/AgentApp.cs (ReportInterrupted)',
    happy=['x:InterruptionTests.LiveBackupThatKeepsReporting_IsNotClosedBySweeper'], failure=['x:InterruptionTests.AgentKilledMidBackup_NextBackupRunsAtOnce_HistoryShowsTheFailure_NotRunning'], boundary=[],
    recovery=['x:InterruptionTests.ServerDownMidBackupAndBack_NextBackupRunsAtOnce', 'qa:failure-recovery/f1'], concurrency=[], integrity=['qa:journeys/j5'], security=NA('-'), regression=[1, 11])
cap('AG-05', 'Agent', 'Network: server unreachable, line cut, reconnect', True, 'A/Client.cs, A/AgentApp.cs',
    happy=['x:NetworkTests.ACut_IsSentAgain_AndTheCallSucceeds', 'x:NetworkTests.RealBackup_TheLineIsCutOnceMidUpload_TheBackupCompletes_AndRestoresIdentical'],
    failure=['x:NetworkTests.ARefusal_IsNotSentAgain', 'x:NetworkTests.AnUploadCutInTheMiddle_IsANetworkError_NotARawException', 'qa:failure-recovery/f2', 'x:InterruptionTests.EndOfRunSentTwice_IsRecordedOnce_AndTheRepeatIsNotAnError'],
    boundary=['x:NetworkTests.AServerThatNeverAnswers_EndsAtTheLimit_WithANetworkError', 'x:InterruptionTests.BeginSentTwice_GivesTheSameRun_NoFalseFailure_NoOrphan'],
    recovery=['x:NetworkTests.ACut_IsSentAgain_AndTheCallSucceeds', 'x:NetworkTests.RealBackup_TheLineIsCutOnceMidUpload_TheBackupCompletes_AndRestoresIdentical', 'qa:failure-recovery/f2', 'x:ResourceTests.MissedOrCutOffByTheInternet_StartsWhenItIsBack'],
    concurrency=NA('-'), integrity=['x:NetworkTests.RealBackup_TheLineIsCutOnceMidUpload_TheBackupCompletes_AndRestoresIdentical', 'qa:failure-recovery/f2', 'x:UploadStoredCopyIntegrationTests.TheServerOnceAnswersADifferentStoredCopy_TheObjectIsSentAgain_TheBackupRestoresIdentical', 'x:UploadStoredCopyIntegrationTests.TheServerAlwaysAnswersADifferentStoredCopy_TheFileIsNotCountedAsBackedUp'], security=NA('-'), regression=[20, 24])
cap('AG-06', 'Agent', 'Local state (chunk index, keys) — lost, damaged', True, 'A/LocalState.cs', happy=[E2E + 'LostLocalIndexIsRebuiltFromTheServerWithoutResendingEverything'], failure=[], boundary=[], recovery=[E2E + 'LostLocalIndexIsRebuiltFromTheServerWithoutResendingEverything'],
    concurrency=[], integrity=[E2E + 'LostLocalIndexIsRebuiltFromTheServerWithoutResendingEverything'], security=['x:CryptoTests.PasswordKeyIsDeterministicAndCheckValueDetectsWrongPassword'])
cap('AG-07', 'Agent', 'TLS: built-in TLS 1.2 for old Windows, certificate pin', True, 'A/BuiltinTls.cs, A/Client.cs',
    happy=['x:TlsTests.BuiltinTls12WithPinnedCertificate_BacksUpAndRestores_WrongPinIsRefused', 'x:TlsTests.ModernWindowsWithTheCompanysSelfSignedCertificate_PinnedForTheAgentAndForRestic'],
    failure=['x:TlsTests.BuiltinTls12WithPinnedCertificate_BacksUpAndRestores_WrongPinIsRefused'], boundary=NA('-'), recovery=NA('-'), concurrency=NA('-'),
    integrity=['x:TlsTests.BuiltinTls12WithPinnedCertificate_BacksUpAndRestores_WrongPinIsRefused'], security=['x:TlsTests.BuiltinTls12WithPinnedCertificate_BacksUpAndRestores_WrongPinIsRefused'])
cap('AG-08', 'Agent', 'The agent on .NET 4.0 (Windows 2003 / XP era)', False, 'A (net40)', happy=[E2E + 'Net40AgentUnderMonoBacksUpAndRestores', 'x:TlsTests.Net40AgentUnderMonoUsesTheBuiltinTls'], failure=[], boundary=NA('-'), recovery=[], concurrency=NA('-'),
    integrity=[E2E + 'Net40AgentUnderMonoBacksUpAndRestores'], security=NA('-'))
# ------------------------------------------------------------------ accounts, security
cap('AU-01', 'Accounts and security', 'Administrator sign-in: password, mandatory two-step, lock, sessions, sign-out', True, 'S/Staff.cs, S/Users.cs',
    happy=['x:AuthComponentTests.Sessions_LiveTheirTime_EndAtSignOut_AdministratorsSlideWhileUsed', 'qa:journeys/j1', 'x:StaffTests.TwoStep_IsMandatory_NothingOpensBeforeItIsSetUp'], failure=['x:AuthComponentTests.ThreeWrongPasswords_LockEvenTheRightOne_UntilTheTimePasses_ThenTheCountStartsAgain', 'qa:journeys/j1', 'x:StaffTests.Administrators_AddChangeDelete_LockAndUnlock'],
    boundary=['x:AuthComponentTests.TheLockCannotBeWeakened_ByTheCustomersOwnSetting', 'x:CryptoTests.PasswordRule_AtLeast8Characters_WithALetter'], recovery=['x:AuthComponentTests.ThreeWrongPasswords_LockEvenTheRightOne_UntilTheTimePasses_ThenTheCountStartsAgain', 'x:StaffTests.FixedAddress_SignsInWithoutTheCode_OthersStillNeedIt'], concurrency=[], integrity=NA('-'),
    security=['x:CryptoTests.TheLockAfterWrongPasswords_CannotBeSwitchedOffOrWeakened', 'x:GuardTests.AdministratorSignIn_AfterFailures_IsReported'])
cap('AU-02', 'Accounts and security', 'Customer sign-in, register a computer, device token', True, 'S/Users.cs, A/AgentApp.cs (Register)',
    happy=['x:AuthComponentTests.RightPasswordIn_WrongOrUnknownOut_WithTheSameAnswer', 'x:ClientUiTests.SignInScreen_ChecksTheServer_ConnectsThisComputer_ThenNeverAgain', 'x:PilotSecurityComponentTests.DeviceToken_ValidIsAllowed_RevokedIsRefusedAtOnce_TheOtherComputerGoesOn'],
    failure=['x:AuthComponentTests.TwoStep_NoCodeOrWrongCodeRefused_RightCodeIn_BackupCodeOnlyOnce', 'x:AuthComponentTests.ASuspendedCustomer_CannotSignIn_EvenWithTheRightPassword', E2E + 'LockoutAfterThreeFailures_TwoFactorWithBackupCodes_DeviceRunsWithoutCode', 'x:PilotSecurityComponentTests.DeviceToken_ForgedOrDamaged_IsRefusedLikeAnUnknownOne', 'x:PilotSecurityIntegrationTests.ADisconnectedComputer_IsRefusedAtItsNextCall_TheOtherComputerGoesOn_ARegistrationAgainWorks'],
    boundary=['x:AuthComponentTests.RightPasswordIn_WrongOrUnknownOut_WithTheSameAnswer', 'x:PilotSecurityComponentTests.ComputerLimitOfTheLicence_ExactlyTheLimitIsAccepted_OneMoreRefused_TheSameComputerAgainIsNotANewOne'], recovery=['x:AuthComponentTests.ThreeWrongPasswords_LockEvenTheRightOne_UntilTheTimePasses_ThenTheCountStartsAgain', 'x:PilotSecurityComponentTests.ARevokedComputer_RegistersAgain_GetsANewToken_TheOldStaysRefused', 'x:PilotSecurityIntegrationTests.ADisconnectedComputer_IsRefusedAtItsNextCall_TheOtherComputerGoesOn_ARegistrationAgainWorks'], concurrency=[],
    integrity=NA('-'), security=[E2E + 'LockoutAfterThreeFailures_TwoFactorWithBackupCodes_DeviceRunsWithoutCode', 'x:SecurityTests.Customer_TurnsTwoStepVerificationOnAndOff_ProviderCanRequireIt', 'x:PilotSecurityComponentTests.DeviceToken_ValidIsAllowed_RevokedIsRefusedAtOnce_TheOtherComputerGoesOn', 'x:PilotSecurityComponentTests.DeviceToken_ForgedOrDamaged_IsRefusedLikeAnUnknownOne'])
cap('AU-03', 'Accounts and security', 'Sign-up from the client with the contract', False, 'S/Contract.cs', happy=['x:ContractTests.Signup_FromTheClient_WithTheContract'], failure=['x:ContractTests.NewVersion_AcceptedAtTheNextSignIn_AndPerAddressLimit'], boundary=[], recovery=NA('-'), concurrency=[], integrity=NA('-'),
    security=['x:ContractTests.NewVersion_AcceptedAtTheNextSignIn_AndPerAddressLimit'])
cap('AU-04', 'Accounts and security', 'Guard: IP blocking (guessing, spraying, scanning)', False, 'S/Guard.cs', happy=['x:GuardTests.PasswordGuessing_BlocksTheAddress_ForEverything_UntilUnblocked_AndSurvivesARestart', 'x:PilotSecurityComponentTests.Guard_TenWrongSignIns_BlockThatAddress_OnlyIt_AndTheAdministratorIsAlerted'], failure=NA('-'),
    boundary=['x:GuardTests.ABlock_EndsByItself_AfterTheBlockingHours', 'x:PilotSecurityComponentTests.Guard_Limits_AreExact_OldFailuresLeaveTheWindow_SprayingAndScanningBlockAtTheirCount'], recovery=['x:GuardTests.PasswordGuessing_BlocksTheAddress_ForEverything_UntilUnblocked_AndSurvivesARestart', 'x:PilotSecurityComponentTests.Guard_ABlockSurvivesARestart_UnblockLetsItIn_AndItsCountStartsAgain'], concurrency=[], integrity=NA('-'), security=['x:GuardTests.Scanning_ManyUnknownAddresses_IsBlocked', 'x:PilotSecurityComponentTests.Guard_TenWrongSignIns_BlockThatAddress_OnlyIt_AndTheAdministratorIsAlerted', 'x:PilotSecurityComponentTests.Guard_Limits_AreExact_OldFailuresLeaveTheWindow_SprayingAndScanningBlockAtTheirCount'])
cap('AU-05', 'Accounts and security', 'Encryption: keys (password / random / custom), check value, tamper detection', True, 'C/Crypto.cs, C/BackupObject.cs',
    happy=['x:CryptoTests.EncryptDecryptRoundTrip', 'x:PilotSecurityComponentTests.Encryption_KnownAnswers_Pbkdf2Rfc6070_AesNist_HmacCheckValue_NameSegments', 'x:PilotSecurityIntegrationTests.CustomAndRandomKeySets_RestoreIdenticalOnANewComputer_AWrongKeyReadsNothing_TheServerHoldsNoReadableContent'], failure=['x:CryptoTests.TamperedOrWrongKeyIsRejected', 'x:PilotSecurityComponentTests.Encryption_EveryChangedByte_EveryCut_AndEveryOtherKey_IsRefused_NeverAWrongPlaintext', 'x:PilotSecurityComponentTests.ABackupObject_WithAChangedByteAnywhere_IsRefused_NeverRestoredWrong', 'x:PilotSecurityIntegrationTests.CustomAndRandomKeySets_RestoreIdenticalOnANewComputer_AWrongKeyReadsNothing_TheServerHoldsNoReadableContent'], boundary=['x:PilotSecurityComponentTests.Encryption_Sizes_EmptyOneByteBlockEdgesAndOneMegabyte_RoundTripExactly'], recovery=[E2E + 'KeyRecoveryAndRestoreOnANewComputer', 'x:PilotSecurityComponentTests.KeyRecovery_PasswordCustomAndRandomKeys_OnANewComputer_ReadTheSameBytes_TheWrongOneReadsNothing', 'x:PilotSecurityIntegrationTests.CustomAndRandomKeySets_RestoreIdenticalOnANewComputer_AWrongKeyReadsNothing_TheServerHoldsNoReadableContent'], concurrency=NA('-'), integrity=['x:CryptoTests.EncryptDecryptRoundTrip', 'x:PilotSecurityComponentTests.Encryption_KnownAnswers_Pbkdf2Rfc6070_AesNist_HmacCheckValue_NameSegments', 'x:PilotSecurityComponentTests.KeyRecovery_PasswordCustomAndRandomKeys_OnANewComputer_ReadTheSameBytes_TheWrongOneReadsNothing', 'x:PilotSecurityComponentTests.ABackupObject_WithAChangedByteAnywhere_IsRefused_NeverRestoredWrong', 'x:PilotSecurityIntegrationTests.CustomAndRandomKeySets_RestoreIdenticalOnANewComputer_AWrongKeyReadsNothing_TheServerHoldsNoReadableContent'], security=['x:CryptoTests.TamperedOrWrongKeyIsRejected', 'x:PilotSecurityComponentTests.Encryption_EveryChangedByte_EveryCut_AndEveryOtherKey_IsRefused_NeverAWrongPlaintext', 'x:PilotSecurityComponentTests.ABackupObject_WithAChangedByteAnywhere_IsRefused_NeverRestoredWrong'])
cap('AU-06', 'Accounts and security', 'Customer limits, vendors (resellers) see only theirs', False, 'S/Vendors.cs, S/SetControl.cs', happy=['x:VendorTests.EachVendorSeesOnlyItsCustomers_WithinItsLimits_AndMailsCarryItsBrand', 'x:PilotSecurityComponentTests.Resellers_EachWithinItsOwnLimits_AnotherResellersCustomersNeverCount', 'x:PilotSecurityComponentTests.Customers_ADeviceTokenOrSessionOpensOnlyItsOwnAccount'], failure=['x:PilotSecurityComponentTests.ResellerAdministrators_AreTheirResellersOnly_AndLoseTheSignInAtOnceWhenTheResellerIsDisabled', 'x:PilotSecurityIntegrationTests.AResellersAdministrator_CannotReadOrChangeAnotherResellersCustomer_AndNothingThereChanges'], boundary=['x:PilotSecurityComponentTests.Resellers_EachWithinItsOwnLimits_AnotherResellersCustomersNeverCount'], recovery=NA('-'), concurrency=NA('-'), integrity=NA('-'),
    security=['x:VendorTests.EachVendorSeesOnlyItsCustomers_WithinItsLimits_AndMailsCarryItsBrand', 'x:CustomerTests.Customer_ChangesOnlyWhatItsProviderAllows', 'x:PilotSecurityComponentTests.ResellerAdministrators_AreTheirResellersOnly_AndLoseTheSignInAtOnceWhenTheResellerIsDisabled', 'x:PilotSecurityComponentTests.Customers_ADeviceTokenOrSessionOpensOnlyItsOwnAccount', 'x:PilotSecurityIntegrationTests.AResellersAdministrator_CannotReadOrChangeAnotherResellersCustomer_AndNothingThereChanges'])
cap('AU-07', 'Accounts and security', 'API refuses junk and attacks clearly', True, 'S/Api.cs', happy=NA('-'), failure=['x:FuzzTests.JunkAndAttacks_AreRefusedClearly_TheServerKeepsRunning_NothingLeaks'], boundary=['x:FuzzTests.JunkAndAttacks_AreRefusedClearly_TheServerKeepsRunning_NothingLeaks'],
    recovery=NA('-'), concurrency=[], integrity=NA('-'), security=['x:FuzzTests.JunkAndAttacks_AreRefusedClearly_TheServerKeepsRunning_NothingLeaks'])
# ------------------------------------------------------------------ install, update
cap('IN-01', 'Install and update', 'Server installation (wizard / script), Windows service, certificate, port', True, 'S/Installer.cs, S/SetupWizard.cs, install-server.ps1',
    happy=['x:SetupTests.WizardAnswers_AreChecked_ThenInstall_ThenARerunOnlyUpdates', 'win:W01', 'x:PilotInstallIntegrationTests.IN01_TheInstalledServerAnswers_ItsAdminSignsIn_ACustomerBacksUp_AnUpdateWithoutAnswerIsNotDone_TheRerunKeepsEveryPoint'], failure=['x:SetupTests.SETUP030_NeverTakesAPortOfIis_OrAnotherProgramsCertificate', 'x:ServiceInstallComponentTests.TheServersService_Too_RunningOrAnError', 'x:PilotInstallIntegrationTests.IN01_TheInstalledServerAnswers_ItsAdminSignsIn_ACustomerBacksUp_AnUpdateWithoutAnswerIsNotDone_TheRerunKeepsEveryPoint'], boundary=['x:SetupTests.SETUP040_TheBackupsFolder_IsChosenFreely_ButNeverANetworkOrNonsensePath'],
    recovery=['x:PilotInstallComponentTests.IN01_AnInstallationThatDidNotAnswer_IsFinishedByARerun_AndAReinstallOnTheOldDrive_KeepsEveryCustomerFile', 'x:PilotInstallIntegrationTests.IN01_TheInstalledServerAnswers_ItsAdminSignsIn_ACustomerBacksUp_AnUpdateWithoutAnswerIsNotDone_TheRerunKeepsEveryPoint'], concurrency=NA('-'), integrity=NA('-'), security=[])
cap('IN-02', 'Install and update', 'Client installation (Setup.exe wizard), service, uninstall, reinstall', True, 'Setup/Program.cs, A/SetupForm.cs, A/Setup.cs',
    happy=['x:PackageTests.ClientSoftwareCarriesTheCompanysNameAndServer_AndInstallsFromThePackage', 'win:W03', 'x:PilotInstallComponentTests.IN02_InstallFromThePackage_EveryProgramFileByteIdentical_TheScriptsNot_TheDataFolderReady', 'x:PilotInstallIntegrationTests.IN02_AWrongPasswordRegistersNothing_TheRightOneBacksUp_AReinstallKeepsIt_UninstallKeepsTheBackups_AndAReinstallReconnects'], failure=['x:PilotInstallComponentTests.IN02_APackageWithoutItsConnectionFile_IsRefused_BeforeAnythingIsWritten', 'x:ServiceInstallComponentTests.StartedButStoppedAgain_IsAnError_NotAnInstalledMessage', 'x:PilotInstallIntegrationTests.IN02_AWrongPasswordRegistersNothing_TheRightOneBacksUp_AReinstallKeepsIt_UninstallKeepsTheBackups_AndAReinstallReconnects'], boundary=['x:PilotInstallComponentTests.IN02_UninstallRemovesOnlyItsOwnFolders_NothingLeftWhenAsked_AForeignFolderStaysByteIdentical'], recovery=['win:W15', 'x:PilotInstallComponentTests.IN02_AReinstallOfANewerPackage_ReplacesEveryProgramFile_AndKeepsTheRegistrationAndKeys', 'x:PilotInstallIntegrationTests.IN02_AWrongPasswordRegistersNothing_TheRightOneBacksUp_AReinstallKeepsIt_UninstallKeepsTheBackups_AndAReinstallReconnects'], concurrency=NA('-'), integrity=['win:W15', 'x:PilotInstallComponentTests.IN02_InstallFromThePackage_EveryProgramFileByteIdentical_TheScriptsNot_TheDataFolderReady', 'x:PilotInstallComponentTests.IN02_UninstallRemovesOnlyItsOwnFolders_NothingLeftWhenAsked_AForeignFolderStaysByteIdentical', 'x:PilotInstallComponentTests.IN02_AReinstallOfANewerPackage_ReplacesEveryProgramFile_AndKeepsTheRegistrationAndKeys', 'x:PilotInstallIntegrationTests.IN02_AWrongPasswordRegistersNothing_TheRightOneBacksUp_AReinstallKeepsIt_UninstallKeepsTheBackups_AndAReinstallReconnects'], security=[])
cap('IN-03', 'Install and update', 'Client update (all or nothing)', True, 'A/ClientUpdate.cs',
    happy=['x:UpdateInstallTests.Normal_EveryFileNew_Checked_ServiceRunning_ResultOk', 'x:ClientUpdateTests.TheServerListsItsClientFiles_TheComputerSeesWhatChanged_DownloadsOnlyListedFiles'],
    failure=['x:UpdateInstallTests.AFileThatCannotBePutBack_IsNamed_TheResultNeverClaimsACleanRollback', 'x:UpdateInstallTests.ServiceDoesNotStop_NothingIsChanged_ResultFailed', 'x:UpdateInstallTests.OneFileCannotBeReplaced_AllFilesBackToThePreviousVersion', 'x:PilotInstallIntegrationTests.IN03_TheServersClientFiles_DownloadedChecked_OneBlockedFilePutsAllBack_ThenTheUpdateInstallsCompletely'], boundary=[],
    recovery=['x:UpdateInstallTests.NewVersionDoesNotStart_PreviousVersionPutBackAndStarted', 'x:PilotInstallIntegrationTests.IN03_TheServersClientFiles_DownloadedChecked_OneBlockedFilePutsAllBack_ThenTheUpdateInstallsCompletely'], concurrency=[], integrity=['x:PilotInstallComponentTests.IN03_AGoodUpdate_GivesExactlyTheNewBytes_AFailedOne_GivesBackExactlyTheOldBytes_UnlistedFilesUntouched', 'x:PilotInstallIntegrationTests.IN03_TheServersClientFiles_DownloadedChecked_OneBlockedFilePutsAllBack_ThenTheUpdateInstallsCompletely'], security=['x:ClientUpdateTests.TheServerListsItsClientFiles_TheComputerSeesWhatChanged_DownloadsOnlyListedFiles'], regression=[9, 10])
cap('IN-04', 'Install and update', 'Server update (signed, SHA-256, from files)', True, 'S/Updater.cs',
    happy=['x:UpdateSignatureComponentTests.OnlyTheVendorsSignatureOfThisExactVersionAndFile_IsAccepted', 'x:UpdaterTests.NewSignedVersion_IsFound_Downloaded_Checked_AndHandedToItsInstaller_AForgedOneIsRefused', 'x:PilotInstallComponentTests.IN04_ASignedVersion_IsDownloaded_Checked_HandedToItsInstallerByteIdentical_SettingsAndDataUntouched'], failure=['x:UpdateSignatureComponentTests.OnlyTheVendorsSignatureOfThisExactVersionAndFile_IsAccepted', 'x:UpdaterTests.UpdateFromFiles_PartsJoinedInOrder_OnTheServerOnly_ANonPackageIsRefused', 'x:PilotInstallComponentTests.IN04_AChangedPackage_AForgedOrMisplacedSignature_AreRefused_NothingInstalled_ThenTheGenuineOneInstalls', 'x:PilotInstallComponentTests.IN04_UpdateFromFiles_ExactlyAtTheLimitTaken_OneByteOverOrAPartMissingOrNoProgram_Refused'], boundary=['x:UpdateSignatureComponentTests.OnlyTheVendorsSignatureOfThisExactVersionAndFile_IsAccepted', 'x:PilotInstallComponentTests.IN04_UpdateFromFiles_ExactlyAtTheLimitTaken_OneByteOverOrAPartMissingOrNoProgram_Refused'], recovery=['x:PilotInstallComponentTests.IN04_AChangedPackage_AForgedOrMisplacedSignature_AreRefused_NothingInstalled_ThenTheGenuineOneInstalls'], concurrency=NA('one update at a time, at night'),
    integrity=['x:UpdateSignatureComponentTests.OnlyTheVendorsSignatureOfThisExactVersionAndFile_IsAccepted', 'x:UpgradeTests.EveryEarlierVersion_OpensAsItIs_RestoresEveryPoint_AndGoesOn', 'x:PilotInstallComponentTests.IN04_ASignedVersion_IsDownloaded_Checked_HandedToItsInstallerByteIdentical_SettingsAndDataUntouched'], security=['x:UpdateSignatureComponentTests.OnlyTheVendorsSignatureOfThisExactVersionAndFile_IsAccepted', 'x:UpdaterTests.NewSignedVersion_IsFound_Downloaded_Checked_AndHandedToItsInstaller_AForgedOneIsRefused', 'x:PilotInstallComponentTests.IN04_AChangedPackage_AForgedOrMisplacedSignature_AreRefused_NothingInstalled_ThenTheGenuineOneInstalls'])
cap('IN-05', 'Install and update', 'Client packages: Windows, Linux, Mac; branding and server inside', False, 'S/ClientPackage.cs',
    happy=['x:PackageTests.ClientSoftwareCarriesTheCompanysNameAndServer_AndInstallsFromThePackage', 'x:PackageTests.LinuxClientPackage_KeepsExecutableBits_AndInstallsUnderTheProductName', 'x:PackageTests.MacClientPackage_BothProcessors_LaunchDaemonAndApp', 'x:PilotInstallComponentTests.IN05_TheWindowsSetup_CarriesEveryClientFileByteIdentical_ThisServerAndTheBranding'],
    failure=['x:PilotInstallComponentTests.IN05_MissingClientFiles_AreRefusedClearly_ACutDownloadIsDamaged', 'x:SetupPayloadTests.AloneWholeAndCut', 'x:PilotInstallIntegrationTests.IN05_TheWindowsDownload_RefusesWithoutAddressOrClientFiles_WithTheReason_AndAHostileNameGivesASafeFileName'], boundary=['x:PilotInstallComponentTests.IN05_AProductNameWindowsCannotHoldInAFileName_GivesSafeNames_HebrewKept', 'x:PilotInstallIntegrationTests.IN05_TheWindowsDownload_RefusesWithoutAddressOrClientFiles_WithTheReason_AndAHostileNameGivesASafeFileName'], recovery=NA('-'), concurrency=NA('-'), integrity=['x:PilotInstallComponentTests.IN05_TheWindowsSetup_CarriesEveryClientFileByteIdentical_ThisServerAndTheBranding', 'x:PilotInstallComponentTests.IN05_OneChangedByteAnywhereInTheSetupPayload_IsRefused_NeverReadAsDifferentFiles'], security=[])
cap('IN-06', 'Install and update', 'Reboot of the computer: service back, runs go on', True, 'A/AgentService.cs', happy=['x:PilotRebootComponentTests.IN06_TheServiceStartsWithTheComputer_AndAfterAnIdleRestart_ThePendingBackupCompletes_NothingReportedAsInterrupted'], failure=['x:PilotRebootComponentTests.IN06_ARebootInTheMiddleOfABackup_TheDeadRunIsReportedOnceWhenTheServerAnswers_TheNextRunCompletes_AndRestoresIdentical'], boundary=NA('-'), recovery=['x:PilotRebootComponentTests.IN06_ARebootInTheMiddleOfABackup_TheDeadRunIsReportedOnceWhenTheServerAnswers_TheNextRunCompletes_AndRestoresIdentical'], concurrency=NA('-'), integrity=['x:PilotRebootComponentTests.IN06_ARebootInTheMiddleOfABackup_TheDeadRunIsReportedOnceWhenTheServerAnswers_TheNextRunCompletes_AndRestoresIdentical'], security=NA('-'))
# ------------------------------------------------------------------ state, history, notifications
cap('SH-01', 'State and history', 'Last backup vs last result; history of every run; tasks page', True, 'S/Api.cs (UpdateStats), S/RunLog.cs',
    happy=['x:ResultComponentTests.EveryResult_HasItsColour_AndOnlyARunThatBackedUpMovesLastBackup', 'x:TasksTests.Tasks_Of24Hours_WithStatusAndCounts', 'qa:journeys/j3', 'x:PilotStateComponentTests.SH01_EveryRunIsInTheHistory_NewestFirst_WithItsColour_AfterARestart_ACutLineSkipped_400DaysKept'], failure=['x:ResultComponentTests.AReportWithoutAResult_TakesItsEndLine_OrIsAFailure_NeverASuccess', 'x:SourceTests.WholeSourceGone_IsAFailure_LastBackupNotRefreshed_FilesKept_AndComesBackCleanly', 'qa:failure-recovery/f5'], boundary=['x:ResultComponentTests.EveryResult_HasItsColour_AndOnlyARunThatBackedUpMovesLastBackup', 'x:PilotStateComponentTests.SH01_EveryRunIsInTheHistory_NewestFirst_WithItsColour_AfterARestart_ACutLineSkipped_400DaysKept'],
    recovery=['x:ResultComponentTests.AReportWithoutAResult_TakesItsEndLine_OrIsAFailure_NeverASuccess', 'qa:journeys/j5', 'x:PilotStateComponentTests.SH01_EveryRunIsInTheHistory_NewestFirst_WithItsColour_AfterARestart_ACutLineSkipped_400DaysKept'], concurrency=[], integrity=NA('-'), security=NA('-'), regression=[4])
cap('SH-02', 'State and history', 'Running now (live list) — no ghost', True, 'S/Api.cs (live, SweepInterrupted)',
    happy=['x:TasksTests.ActiveBackups_ReportedWhileRunning_GoneWhenDone', 'x:InterruptionTests.ProgressThatArrivesAfterTheEnd_DoesNotBringTheRunBack'], failure=['qa:journeys/j5', 'qa:failure-recovery/f1'],
    boundary=['x:InterruptionTests.ProgressThatArrivesAfterTheEnd_DoesNotBringTheRunBack'], recovery=['x:InterruptionTests.ComputerNeverComesBack_RunIsClosedAsInterrupted_AndAnotherProcessCanBackUp'],
    concurrency=['x:InterruptionTests.InterruptedRunReportedByManyPathsAtOnce_IsRecordedExactlyOnce'], integrity=NA('-'), security=NA('-'), regression=[1, 19, 26])
cap('SH-03', 'State and history', 'Mails: run report, failure, missed backup, quota, disk full', True, 'S/Notify.cs',
    happy=['x:NotifyComponentTests.EachResult_OneMail_WithTheRightMarkAndTitle', 'x:FeatureTests.BackupReportTestMailAndMissedBackupAlertAreSent_PasswordsNeverLeaveTheServer'],
    failure=['x:NotifyComponentTests.NoSmtpServer_NothingSent_NothingThrown', 'x:NotifyComponentTests.EachResult_OneMail_WithTheRightMarkAndTitle'],
    boundary=['x:NotifyComponentTests.TheCustomersChoice_FailureOnly_And_None', 'x:TimeMachineTests.TwentyFiveDays_SchedulesVersionsAlertsServiceCallsAndMails'],
    recovery=['x:NotifyComponentTests.EachResult_OneMail_WithTheRightMarkAndTitle'], concurrency=NA('-'), integrity=NA('-'),
    security=['x:FeatureTests.BackupReportTestMailAndMissedBackupAlertAreSent_PasswordsNeverLeaveTheServer'])
cap('SH-04', 'State and history', 'Service calls opened / closed by backup results', False, 'S/Tickets.cs',
    happy=['x:TicketTests.BackupFailures_OpenOneCall_AtThreshold_CloseItselfOnSuccess'], failure=[], boundary=['x:TicketTests.Thresholds_General_And_PerCustomer'], recovery=[], concurrency=[], integrity=NA('-'), security=['x:TicketTests.Api_Admin_And_Client'])
cap('SH-05', 'State and history', 'Ransomware suspicion: retention frozen, alert', True, 'S/Api.cs (CheckMassChange), S/Insights.cs',
    happy=['x:FeatureTests.MassChangeFreezesRetentionUntilAnAdministratorReleasesIt_AndAlertsByMail', 'x:PilotStateComponentTests.SH05_MostFilesRewrittenUnderANewExtension_IsSuspect_WithItsReason_AnOrdinaryRunIsNot'], failure=['x:PilotStateIntegrationTests.SH05_AnAttackWhileTheMailServerIsDown_StillFreezesRetention_ShownToTheAdmin_ThePointBeforeItRestoresIdentical_UntilReleased'], boundary=['x:AiTests.Ransomware_LearnsWhatIsNormalForEachSet'], recovery=['x:PilotStateComponentTests.SH05_AfterASuspectRun_TheLearningIgnoresIt_TheNextOrdinaryRunIsNotSuspect_ASecondAttackIsCaught', 'x:PilotStateIntegrationTests.SH05_AnAttackWhileTheMailServerIsDown_StillFreezesRetention_ShownToTheAdmin_ThePointBeforeItRestoresIdentical_UntilReleased'], concurrency=NA('-'), integrity=['x:PilotStateComponentTests.SH05_PacksDeletedDuringAnAttack_StayByteIdenticalInTheTrash_AndComeBackByteIdentical', 'x:PilotStateIntegrationTests.SH05_AnAttackWhileTheMailServerIsDown_StillFreezesRetention_ShownToTheAdmin_ThePointBeforeItRestoresIdentical_UntilReleased'], security=NA('-'))
cap('SH-06', 'State and history', 'Computers: list, disconnect, move to another customer with backups', False, 'S/Computers.cs',
    happy=['x:ComputerTests.List_Disconnect_MoveToAnotherCustomerWithTheBackups'], failure=[], boundary=[], recovery=[], concurrency=NA('-'), integrity=['x:ComputerTests.List_Disconnect_MoveToAnotherCustomerWithTheBackups'], security=[])
cap('SH-07', 'State and history', 'Licence: editions, limits, check-in', False, 'S/License.cs, S/LicenseCenter.cs',
    happy=['x:LicenseTests.LicenceLimits_UsersModulesAndStorage', 'x:PilotStateComponentTests.SH07_AnUnconfirmedLicence_StaysFullExactly7Days_ThenBasicWithItsReason_AndAConfirmationBringsItBack'], failure=['x:LicenseCenterTests.CheckIn_UnreachableMeans7DayTemporary_AddressChangeNeedsApproval_RevokedAndForgedAnswersRefused', 'x:PilotStateComponentTests.SH07_FailedCheckIns_StartTheGraceOnce_OneMail_TheLicenceStaysFull_ThenTheCentreAnswers_AndItIsFullAgain'], boundary=['x:LicenseTests.WithoutALicence_TheFreeEditionLimitsUsersAndModules_AndShowsPoweredBy', 'x:PilotStateComponentTests.SH07_AnUnconfirmedLicence_StaysFullExactly7Days_ThenBasicWithItsReason_AndAConfirmationBringsItBack', 'x:PilotStateComponentTests.SH07_AfterTheGrace_TheBasicEditionKeepsEveryExistingComputerBackingUp_OnlyANewOneIsRefusedWithTheReason'],
    recovery=['x:LicenseCenterTests.CheckIn_UnreachableMeans7DayTemporary_AddressChangeNeedsApproval_RevokedAndForgedAnswersRefused', 'x:PilotStateComponentTests.SH07_AnUnconfirmedLicence_StaysFullExactly7Days_ThenBasicWithItsReason_AndAConfirmationBringsItBack', 'x:PilotStateComponentTests.SH07_FailedCheckIns_StartTheGraceOnce_OneMail_TheLicenceStaysFull_ThenTheCentreAnswers_AndItIsFullAgain'], concurrency=NA('-'), integrity=NA('-'), security=['x:LicenseCenterTests.UpdateLicence_BringsTheNewQuotaTheOwnerIssued_OnlyGenuineAndOnlyForThisServer'])
# ------------------------------------------------------------------ user interfaces
cap('UI-01', 'User interfaces', 'Admin site: every page opens, no errors; sign-in, reload, sign-out', True, 'S/Web/app.js',
    happy=['qa:journeys/j1', 'robot:behaviour'], failure=['qa:journeys/j1'], boundary=['robot:behaviour'], recovery=['qa:journeys/j1'], concurrency=[], integrity=NA('-'), security=['x:FeatureTests.ManagementUiIsServedWithStrictHeadersAndBranding'], regression=[7, 8])
cap('UI-02', 'User interfaces', 'Admin site: set editor (every tab saved and read back)', True, 'S/Web/app.js (setEditor)', happy=['qa:journeys/j2', 'robot:options'], failure=[], boundary=[], recovery=NA('-'), concurrency=[], integrity=NA('-'), security=NA('-'))
cap('UI-03', 'User interfaces', 'Admin site: the truth after failures (red, failed, not running)', True, 'S/Web/app.js', happy=NA('-'), failure=['qa:failure-recovery/f5', 'qa:journeys/j5'], boundary=[], recovery=['qa:journeys/j5'], concurrency=NA('-'), integrity=NA('-'), security=NA('-'), regression=[4, 12])
cap('UI-04', 'User interfaces', 'Client window (every page, typing kept)', True, 'A/ClientForm.cs', happy=['ci:client-pages'], failure=[], boundary=[], recovery=[], concurrency=NA('-'), integrity=NA('-'), security=NA('-'))
cap('UI-05', 'User interfaces', 'Client installation wizard (Welcome → License → Install → Finish)', True, 'A/SetupForm.cs', happy=['win:W03'], failure=['win:W02'], boundary=[], recovery=[], concurrency=NA('-'), integrity=NA('-'), security=NA('-'))
cap('UI-06', 'User interfaces', 'Partner portal and licensing centre', False, 'S/Portal.cs, Web/portal.js', happy=['x:PortalTests.PartnerSignsUp_BrandsItsProduct_DownloadsServerThatLicensesItself_AndItsClients'], failure=[], boundary=[], recovery=NA('-'), concurrency=NA('-'), integrity=NA('-'), security=[])
cap('UI-07', 'User interfaces', 'Translations (13 languages), Hebrew screens', False, 'C/i18n', happy=['x:I18nTests.TemplatesTranslateFinishedMessages_AndEveryDictionaryKeepsItsPlaceholders', 'robot:behaviour'], failure=NA('-'), boundary=['x:LayoutLintTests.NoScreenFixesLeftOrRight'], recovery=NA('-'), concurrency=NA('-'), integrity=NA('-'), security=NA('-'))
cap('UI-08', 'User interfaces', 'AI: explain a failed run, insights, forecasts', False, 'S/Ai.cs, S/Insights.cs', happy=['x:AiTests.FailedJob_IsExplainedByTheAi_WithoutSecrets_AndOpensATicket', 'x:AiTests.Forecasts_DiskQuotaAndComputersAtRisk'], failure=[], boundary=[], recovery=NA('-'), concurrency=NA('-'), integrity=NA('-'),
    security=['x:AiTests.Redact_MasksSecretsAndKeepsTheEnd'])
cap('UI-09', 'User interfaces', 'Large installations: 500 customers, 5 000 sets stay fast', False, 'S/Api.cs', happy=['x:LoadTests.BigServer_500Customers_5000Sets_PagesAndComputersStayFast'], failure=NA('-'), boundary=['x:LoadTests.BigServer_500Customers_5000Sets_PagesAndComputersStayFast'], recovery=NA('-'), concurrency=['x:LoadTests.BigServer_500Customers_5000Sets_PagesAndComputersStayFast'], integrity=NA('-'), security=NA('-'))

cap('CO-01', 'Core', 'Shared formats: messages, profile, log lines, run ids, atomic file writes', True, 'C/Msg.cs, C/Profile.cs, C/Formats.cs, C/Json.cs',
    happy=['x:CoreFormatsTests.AMessage_ReadsBackExactly', 'x:FormatTests.LogLinesMatchAhsayFormat', 'x:FormatTests.ProfileUsesAhsayNamesAndKeepsUnknownAttributes', 'x:M365Tests.JsonReadsAndWritesGraphShapes', 'x:ResourceTests.Settings_Survive_TheProfile'],
    failure=['x:FuzzTests.JunkAndAttacks_AreRefusedClearly_TheServerKeepsRunning_NothingLeaks'], boundary=['x:CoreFormatsTests.AMessage_ReadsBackExactly'],
    recovery=['x:CoreFormatsTests.AStateFile_IsNeverSeenHalfWritten_AndTwoWritersNeverFail'], concurrency=['x:CoreFormatsTests.AStateFile_IsNeverSeenHalfWritten_AndTwoWritersNeverFail'],
    integrity=['x:CoreFormatsTests.AMessage_ReadsBackExactly', 'x:CoreFormatsTests.AStateFile_IsNeverSeenHalfWritten_AndTwoWritersNeverFail', 'x:FormatTests.ProfileUsesAhsayNamesAndKeepsUnknownAttributes'], security=NA('no access decision'))

# Files whose capability is named above only by area: every source file belongs to at least one capability (a new file
# that belongs to none is listed as a hidden area in COVERAGE-MATRIX.md until it is added here or to a capability's code)
EXTRA_COMPONENTS = {
    'Core/Formats.cs': ['CO-01'], 'Core/Json.cs': ['CO-01'], 'Core/Msg.cs': ['CO-01'], 'Core/Profile.cs': ['CO-01'],
    'Core/L.cs': ['UI-07'], 'Core/i18n/i18n.js': ['UI-07'], 'Core/WebAssets.cs': ['UI-01'], 'Core/i18n/qrcode.js': ['AU-01'],
    'Core/Passwords.cs': ['AU-01', 'AU-05'], 'Core/SystemClock.cs': ['AG-01'],
    'Server/AdminUi.cs': ['UI-01'], 'Server/BackupChecks.cs': ['SH-03', 'SH-04'], 'Server/ClientFiles.cs': ['IN-05'], 'Server/Compliance.cs': ['SH-01'],
    'Server/FolderTree.cs': ['UI-02'], 'Server/Infra.cs': ['AU-07', 'IN-01'], 'Server/NewDefaults.cs': ['UI-02'], 'Server/Program.cs': ['IN-01'],
    'Server/ServerService.cs': ['IN-01'], 'Server/Templates.cs': ['UI-02'], 'Server/TimeSettings.cs': ['IN-01'],
    'Server/Web/portal.js': ['UI-06'], 'Server/Web/report.js': ['SH-01'], 'Server/Web/restore.js': ['RS-04'], 'Server/Web/setup.js': ['IN-01'],
    'Agent/ClientUi.cs': ['UI-04'], 'Agent/FolderScan.cs': ['UI-02'], 'Agent/Program.cs': ['AG-02', 'RS-01'], 'Agent/SetupUi.cs': ['UI-05'], 'ClientApp/Program.cs': ['UI-04'],
}

# ------------------------------------------------------------------ evaluation
# Component tests of the agent against a recording stand-in server (tests/Tests/StubServer.cs) and of the server's store /
# sweep alone (no web server) — each against its contract in specs.py; a failing one is a finding and stays registered.
U, O, R, LS, SL, SW = 'x:UnreadableComponentTests.', 'x:OpenRunComponentTests.', 'x:RunRequestComponentTests.', 'x:LocalStateComponentTests.', 'x:StorageLimitsComponentTests.', 'x:SweepComponentTests.'
COMPONENT_C = {
    'BK-05': dict(happy=[U + 'ALockedFile_IsAnError_NotADeletion_TheOthersRestoreIdentical_AndItIsSentOnceFree'],
                  failure=[U + 'ALockedFile_IsAnError_NotADeletion_TheOthersRestoreIdentical_AndItIsSentOnceFree', U + 'EverySourceGone_IsAFailure_NothingDeleted_TheIndexKeepsEveryFile_AndTheSourceComesBackWithoutResending'],
                  boundary=[U + 'OneOfTwoSourcesGone_IsSuccessWithError_NamesIt_ItsFilesAreNotDeleted'],
                  recovery=[U + 'ALockedFile_IsAnError_NotADeletion_TheOthersRestoreIdentical_AndItIsSentOnceFree', U + 'EverySourceGone_IsAFailure_NothingDeleted_TheIndexKeepsEveryFile_AndTheSourceComesBackWithoutResending'],
                  integrity=[U + 'ALockedFile_IsAnError_NotADeletion_TheOthersRestoreIdentical_AndItIsSentOnceFree', U + 'EverySourceGone_IsAFailure_NothingDeleted_TheIndexKeepsEveryFile_AndTheSourceComesBackWithoutResending']),
    'AG-04': dict(happy=[O + 'WhileARunIsOpen_TheNoteHoldsIt_AConfirmedEndRemovesIt', O + 'ASilentUpload_StillSendsASignOfLifeWithinAMinute'],
                  failure=[O + 'ALostEnd_LeavesTheNote_TheIndexDoesNotMove_TheNextStartReportsItOnce_AndTheFilesAreSentAgain'],
                  boundary=[O + 'ANoteOfALiveProcess_IsNotReported_ADamagedNoteIsReportedAndRemoved_AnUnreachableServerKeepsTheNote'],
                  recovery=[O + 'ALostEnd_LeavesTheNote_TheIndexDoesNotMove_TheNextStartReportsItOnce_AndTheFilesAreSentAgain', O + 'ANoteOfALiveProcess_IsNotReported_ADamagedNoteIsReportedAndRemoved_AnUnreachableServerKeepsTheNote'],
                  integrity=[O + 'ALostEnd_LeavesTheNote_TheIndexDoesNotMove_TheNextStartReportsItOnce_AndTheFilesAreSentAgain']),
    'AG-02': dict(happy=[R + 'EachNewRequest_StartsExactlyOneRun_AnOldOrRepeatedOneNone', R + 'AStopNewerThanTheStart_EndsTheRunAsStopped_NothingDeleted_TheNextRunCompletes_AndRestoresIdentical'],
                  failure=[R + 'AnOlderStop_OrAServerThatFails_NeverStopsARun', R + 'ARequestWhoseBackupCouldNotStart_IsNotLost'],
                  recovery=[R + 'AStopNewerThanTheStart_EndsTheRunAsStopped_NothingDeleted_TheNextRunCompletes_AndRestoresIdentical'],
                  integrity=[R + 'AStopNewerThanTheStart_EndsTheRunAsStopped_NothingDeleted_TheNextRunCompletes_AndRestoresIdentical']),
    'AG-06': dict(happy=[LS + 'ALostIndex_IsRebuiltFromTheServer_OnlyTheChangedFileIsSent_AndEverythingRestoresIdentical'],
                  failure=[LS + 'ADamagedIndexLine_DoesNotFailEveryLaterBackup', LS + 'ADamagedChunkList_DoesNotFailEveryLaterBackup'],
                  boundary=[LS + 'ADamagedIndexLine_DoesNotFailEveryLaterBackup'],
                  recovery=[LS + 'ALostIndex_IsRebuiltFromTheServer_OnlyTheChangedFileIsSent_AndEverythingRestoresIdentical'],
                  integrity=[LS + 'ALostIndex_IsRebuiltFromTheServer_OnlyTheChangedFileIsSent_AndEverythingRestoresIdentical']),
    'ST-05': dict(happy=[SL + 'Quota_ExactlyTheRoomIsAccepted_OneByteOverIsRefused_NothingLeft_StoredPointsUnchanged', SL + 'AQuotaRefusalMidRun_EndsQuotaExceeded_NothingDeleted_TheRestWaits_TheNextRunCompletes'],
                  failure=[SL + 'AQuotaRefusalMidRun_EndsQuotaExceeded_NothingDeleted_TheRestWaits_TheNextRunCompletes'],
                  boundary=[SL + 'Quota_ExactlyTheRoomIsAccepted_OneByteOverIsRefused_NothingLeft_StoredPointsUnchanged'],
                  recovery=[SL + 'AQuotaRefusalMidRun_EndsQuotaExceeded_NothingDeleted_TheRestWaits_TheNextRunCompletes'],
                  integrity=[SL + 'Quota_ExactlyTheRoomIsAccepted_OneByteOverIsRefused_NothingLeft_StoredPointsUnchanged', SL + 'AQuotaRefusalMidRun_EndsQuotaExceeded_NothingDeleted_TheRestWaits_TheNextRunCompletes']),
    'ST-06': dict(failure=[SL + 'ServerDiskFull_MidObject_NoHalfObjectLeft_EarlierPointsUnchanged_TheSameObjectIsAcceptedOnceSpaceIsBack', SL + 'ServerDiskFull_TheRunFailsWithTheServersReason_TheIndexDoesNotMove_TheNextRunCompletes'],
                  recovery=[SL + 'ServerDiskFull_MidObject_NoHalfObjectLeft_EarlierPointsUnchanged_TheSameObjectIsAcceptedOnceSpaceIsBack', SL + 'ServerDiskFull_TheRunFailsWithTheServersReason_TheIndexDoesNotMove_TheNextRunCompletes'],
                  integrity=[SL + 'ServerDiskFull_MidObject_NoHalfObjectLeft_EarlierPointsUnchanged_TheSameObjectIsAcceptedOnceSpaceIsBack', SL + 'ServerDiskFull_TheRunFailsWithTheServersReason_TheIndexDoesNotMove_TheNextRunCompletes']),
    'SH-02': dict(happy=[SW + 'ARunThatKeepsSendingSignsOfLife_IsNeverClosedBySweeps', SW + 'ADeadRun_IsClosedOnceAfterItsLease_AsAFailure_InTheHistory_AndTheSetCanBeginAgain'],
                  failure=[SW + 'ADeadRun_IsClosedOnceAfterItsLease_AsAFailure_InTheHistory_AndTheSetCanBeginAgain'],
                  boundary=[SW + 'ADeadRun_IsClosedOnceAfterItsLease_AsAFailure_InTheHistory_AndTheSetCanBeginAgain'],
                  recovery=[SW + 'ADeadRun_IsClosedOnceAfterItsLease_AsAFailure_InTheHistory_AndTheSetCanBeginAgain']),
    'AU-07': dict(failure=['x:ApiInputComponentTests.JunkBodies_FailAsTheSendersMistake_Quickly_AndNeverPullInALocalFile', 'x:ApiInputComponentTests.BadNames_Objects_RunIds_AreRefused400_AndNothingIsWrittenAnywhere',
                           'x:ApiInputIntegrationTests.ADeeplyNestedMessage_IsRefused_AndTheServerStaysUp'],
                  boundary=['x:ApiInputComponentTests.JunkBodies_FailAsTheSendersMistake_Quickly_AndNeverPullInALocalFile'],
                  security=['x:ApiInputComponentTests.JunkBodies_FailAsTheSendersMistake_Quickly_AndNeverPullInALocalFile', 'x:ApiInputComponentTests.BadNames_Objects_RunIds_AreRefused400_AndNothingIsWrittenAnywhere',
                            'x:ApiInputIntegrationTests.ADeeplyNestedMessage_IsRefused_AndTheServerStaysUp']),
}
for _c in C:
    for _d, _ids in COMPONENT_C.get(_c['id'], {}).items():
        assert not isinstance(_c['dims'][_d], NA), (_c['id'], _d)
        _c['dims'][_d] = list(_c['dims'][_d]) + [i for i in _ids if i not in _c['dims'][_d]]

# Real Windows (tests/QA/windows/win-e2e.ps1): a journey counts only when it really ran on Windows and passed — its
# windows-<phase>.json is the record (NOT TESTED there stays not run here). W19 runs in the VM after a real restart.
WIN = {
    'BK-01': dict(happy=['win:W05', 'win:W06'], integrity=['win:W05']),
    'BK-05': dict(failure=['win:W12', 'win:W11'], recovery=['win:W12']),
    'BK-06': dict(happy=['win:W10'], failure=['win:W11'], recovery=['win:W11'], integrity=['win:W10']),
    'AP-01': dict(happy=['win:W17']),
    'RS-01': dict(happy=['win:W05'], integrity=['win:W05']),
    'ST-02': dict(recovery=['win:W07', 'win:W09']),
    'AG-04': dict(recovery=['win:W07', 'win:W08']),
    'AG-05': dict(recovery=['win:W09']),
    'AU-02': dict(happy=['win:W04']),
    'SH-02': dict(failure=['win:W07', 'win:W09'], recovery=['win:W07']),
    'IN-01': dict(happy=['win:W01']),
    'IN-02': dict(happy=['win:W03'], failure=['win:W02', 'win:W13'], recovery=['win:W13', 'win:W15'], integrity=['win:W15']),
    'IN-03': dict(happy=['win:W14'], failure=['win:W14'], recovery=['win:W14']),
    'IN-06': dict(happy=['win:W19'], recovery=['win:W19'], integrity=['win:W19']),
    'UI-04': dict(happy=['win:W04', 'win:W05', 'win:W16']),
    'UI-05': dict(happy=['win:W03'], failure=['win:W02']),
}
for _c in C:
    for _d, _ids in WIN.get(_c['id'], {}).items():
        if not isinstance(_c['dims'][_d], NA): _c['dims'][_d] = list(_c['dims'][_d]) + [i for i in _ids if i not in _c['dims'][_d]]

def windows_results():
    """win:Wnn from every windows-*.json under reports/windows (the main job and the VM's phases); a journey that ran
    in several phases is Failed if it failed in any, Passed if it passed in one, else not run."""
    r = {}
    base = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'reports', 'windows')
    for dp, dn, fn in os.walk(base):
        for f in fn:
            if not re.match(r'^windows-(main|before-reboot|after-reboot)\.json$', f): continue   # a phase summary, not windows-state.json
            try: d = json.load(open(os.path.join(dp, f), encoding='utf-8-sig'))
            except Exception: continue
            for j in d.get('journeys') or []:
                k = 'win:' + j['id']; v = {'PASS': 'Passed', 'FAIL': 'Failed'}.get(j.get('functional'), 'Skipped')
                if r.get(k) == 'Failed' or (r.get(k) == 'Passed' and v == 'Skipped'): continue
                r[k] = v
    return r

def worst(a, b):
    """Several results for one key: Failed > Skipped (not everything ran) > Passed."""
    order = ['Failed', 'Skipped', 'Passed']
    return min((x for x in (a, b) if x), key=lambda x: order.index(x) if x in order else 0, default=b)

def guarded_tests():
    """Class.Method of every xUnit test whose body opens with `if (...) return;` (a precondition guard)."""
    tdir = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'Tests'); out = set()
    if not os.path.isdir(tdir): return out
    for f in sorted(os.listdir(tdir)):
        if not f.endswith('.cs'): continue
        src = open(os.path.join(tdir, f), encoding='utf-8').read()
        for m in re.finditer(r'\[(Fact|Theory)[^\]]*\](?:\s*(?:\[[^\]]*\]|//[^\n]*))*\s*public\s+(?:async\s+\w+\s+|void\s+)(\w+)\s*\(', src):
            start = src.index('{', m.end()); body = src[start:start + 600]
            g = re.search(r'^\s*if \((.*)\)\s*return;', body, re.M)
            if g and g.start() < 400:
                cls = re.findall(r'^    (?:[a-z]+ )*class (\w+)', src[:m.start()], re.M)   # the test class, not a nested helper
                if cls: out.add(cls[-1] + '.' + m.group(2))
    return out

def results():
    """The outcome of every test id in the last runs: xUnit trx + QA Playwright json + Windows json + robots."""
    here = os.path.dirname(os.path.abspath(__file__)); r = {}
    trx = os.path.join(here, 'reports', 'trx', 'xunit.trx')
    if os.path.exists(trx):
        guarded = guarded_tests()
        for m in re.finditer(r'<UnitTestResult [^>]*testName="([^"]+)"[^>]*outcome="([^"]+)"', open(trx, encoding='utf-8').read()):
            name = m.group(1).replace('&quot;', '"'); mm = re.match(r'OnlineBackup\.Tests\.(\w+)\.(\w+)', name)
            if mm:
                outcome = m.group(2)
                if outcome == 'NotExecuted': outcome = 'Skipped'
                dur = re.search(r'duration="(\d+):(\d+):([\d.]+)"', m.group(0))
                secs = int(dur.group(1)) * 3600 + int(dur.group(2)) * 60 + float(dur.group(3)) if dur else None
                # L-1: a test whose body starts with `if (<precondition missing>) return;` and "passed" in under 50 ms
                # returned at its guard — xUnit says Passed, but nothing was tested: NOT TESTED, never PASS
                if outcome == 'Passed' and mm.group(1) + '.' + mm.group(2) in guarded and (secs is None or secs < 0.05): outcome = 'Skipped'
                for k in ('x:' + mm.group(1) + '.' + mm.group(2), 'x:*.' + mm.group(2)):   # also by method name: a class name never hides a test
                    r[k] = worst(r.get(k), outcome)
    js = os.path.join(here, 'reports', 'last-run.json')
    if os.path.exists(js):
        def walk(s, f):
            for x in s.get('suites', []): walk(x, x.get('file', f))
            for sp in s.get('specs', []):
                key = 'qa:' + (sp.get('file') or f).replace('.spec.ts', '')
                ok = sp.get('ok') and all(res.get('status') == 'passed' for t in sp.get('tests', []) for res in t.get('results', []))
                skipped = all(res.get('status') == 'skipped' for t in sp.get('tests', []) for res in t.get('results', []))
                r[key] = worst(r.get(key), 'Skipped' if skipped else 'Passed' if ok else 'Failed')   # L-2: several tests in one file
        walk(json.load(open(js)), '')
    r.update(windows_results())
    return r

def test_classes():
    """Method name → the test classes (top level, not nested helpers) that hold it."""
    tdir = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'Tests'); where = {}
    for f in sorted(os.listdir(tdir)) if os.path.isdir(tdir) else []:
        if not f.endswith('.cs'): continue
        cls = None
        for line in open(os.path.join(tdir, f), encoding='utf-8'):
            m = re.match(r'    (?:[a-z]+ )*class (\w+)', line)
            if m: cls = m.group(1)
            m = re.search(r'public\s+(?:async\s+\w+|void)\s+(\w+)\s*\(', line)
            if m and cls: where.setdefault(m.group(1), set()).add(cls)
    return where

def wrong_ids():
    """L-6: every x:Class.Method named in the matrix must exist in that class (else it is credited by another class's test)."""
    where = test_classes(); bad = []
    for c in C:
        for d in c['dims'].values():
            if isinstance(d, NA): continue
            for t in d:
                m = re.match(r'x:(\w+)\.(\w+)$', t)
                if m and m.group(1) not in where.get(m.group(2), set()): bad.append((c['id'], t, sorted(where.get(m.group(2), []))))
    return bad

def resolve(ref, res):
    # qa:journeys/j3 matches qa:journeys/j3-backup-now
    if ref.startswith('qa:'):
        hits = [v for k, v in res.items() if k == ref or k.startswith(ref + '-')]
        return 'Failed' if 'Failed' in hits else 'Passed' if 'Passed' in hits else 'Skipped' if hits else 'Not run'
    if ref in res: return res[ref]
    if ref.startswith('x:') and '.' in ref: return res.get('x:*.' + ref.split('.', 1)[1], 'Not run')
    return 'Not run'

# ------------------------------------------------------------------ the three layers
# COMPONENT   one component alone, against its written contract (specs.py: purpose, known input, expected output) —
#             an xUnit test that does not start the server (no `new Env(`): happy, failure/boundary, recovery, integrity
# INTEGRATION the components together: real server in-process + real HTTP + real files (xUnit with `new Env(`)
# END-TO-END  a real user operation on the real programs (separate server / agent processes, the browser, the installer)
#             through to a restore and a check of the data outside the product (SHA-256) — tests/QA and Windows
# A capability is FULLY VERIFIED only when all three layers are; a passing unit test or a passing journey alone never is.
LAYERS = ['component', 'integration', 'e2e']
E2E_DATA_CHECK = {'qa:journeys/j6', 'qa:journeys/j7', 'qa:journeys/j8', 'qa:journeys/j3', 'qa:journeys/j4', 'qa:journeys/j5', 'qa:failure-recovery/f1', 'qa:failure-recovery/f2', 'qa:failure-recovery/f3',
                  'qa:failure-recovery/f4', 'qa:failure-recovery/f5', 'qa:failure-recovery/f6', 'qa:failure-recovery/f7', 'win:W05', 'win:W06', 'win:W07', 'win:W08', 'win:W09', 'win:W10', 'win:W11', 'win:W12', 'win:W13', 'win:W14', 'win:W15', 'win:W19'}
def data_check_id(t):
    """qa:failure-recovery/f10-client-clock → qa:failure-recovery/f10 (the id without the file's title)."""
    head, sep, base = t.rpartition('/')
    return head + sep + base.split('-')[0]
NO_DATA = {'UI-01', 'UI-02', 'UI-04', 'UI-05', 'UI-06', 'UI-07', 'UI-08', 'UI-09', 'AU-01', 'AU-02', 'AU-03', 'AU-04', 'AU-06', 'AU-07',
           'SH-03', 'SH-04', 'SH-05', 'SH-06', 'SH-07', 'AG-02', 'AG-03', 'AG-07', 'IN-05'}   # the user operation ends without a restore

def xunit_kinds():
    """Every xUnit test method → 'component' or 'integration', read from its source (a new test is classified by itself)."""
    here = os.path.dirname(os.path.abspath(__file__)); kinds = {}
    tdir = os.path.join(here, '..', 'Tests')
    for f in sorted(os.listdir(tdir)):
        if not f.endswith('.cs'): continue
        src = open(os.path.join(tdir, f), encoding='utf-8').read()
        for m in re.finditer(r'\[(Fact|Theory)[^\]]*\](?:\s*(?:\[[^\]]*\]|//[^\n]*))*\s*public\s+(?:async\s+\w+\s+|void\s+)(\w+)\s*\(', src):
            k = m.end(); depth = 0; start = src.index('{', k); e = start
            for e in range(start, len(src)):
                if src[e] == '{': depth += 1
                elif src[e] == '}':
                    depth -= 1
                    if depth == 0: break
            body = src[start:e]
            kinds[m.group(2)] = 'integration' if re.search(r'new Env\(|\bEnv\.|StartAgent\(|\(env\b|\benv\.', body) else 'component'
    return kinds

def layer_of(t, kinds):
    if t.startswith(('qa:', 'win:', 'robot:')): return 'e2e'
    if t.startswith('x:'): return kinds.get(t.split('.', 1)[1] if '.' in t else t[2:], 'integration')
    return 'integration'

def evaluate(res):
    from specs import SPEC
    kinds = xunit_kinds(); rows = []
    for c in C:
        dims = {}
        for d in D:
            v = c['dims'][d]
            if isinstance(v, NA): dims[d] = ('n/a', [], str(v)); continue
            outs = [(t, resolve(t, res)) for t in v]
            passed = [t for t, o in outs if o == 'Passed']
            failed = [t for t, o in outs if o == 'Failed']
            dims[d] = ('FAIL' if failed else 'PASS' if passed else 'NOT TESTED' if not v else 'NOT RUN', outs, '')
        applicable = [d for d in D if dims[d][0] != 'n/a']
        # per layer: which dimensions have a passing test of that layer
        lay = {}
        for L in LAYERS:
            got = {d: [(t, o) for t, o in dims[d][1] if layer_of(t, kinds) == L] for d in D}
            ok = {d: any(o == 'Passed' for t, o in got[d]) for d in D}
            bad = any(o == 'Failed' for d in D for t, o in got[d])
            na = {d: dims[d][0] == 'n/a' for d in D}
            if L == 'component':
                need = [x for x in ('happy', 'recovery', 'integrity') if not na[x]]   # a dimension marked n/a (e.g. happy of a failure capability) is not required
                fb = na['failure'] and na['boundary'] or ok['failure'] or ok['boundary']
                verified = c['id'] in SPEC and all(ok[x] for x in need) and fb
                missing = ([] if c['id'] in SPEC else ['contract']) + [x for x in need if not ok[x]] + ([] if fb else ['failure/boundary'])
            elif L == 'integration':
                fb = all(na[x] for x in ('failure', 'boundary', 'recovery')) or any(ok[x] for x in ('failure', 'boundary', 'recovery'))
                verified = (ok['happy'] or na['happy']) and fb
                missing = ([] if ok['happy'] or na['happy'] else ['happy']) + ([] if fb else ['failure/recovery'])
            else:
                tests = [(t, o) for d in D for t, o in got[d]]
                data = c['id'] in NO_DATA or any(o == 'Passed' and data_check_id(t) in E2E_DATA_CHECK for t, o in tests)   # L-5: exact id, f1 never credits f10
                verified = any(o == 'Passed' for t, o in tests) and data
                missing = ([] if any(o == 'Passed' for t, o in tests) else ['a real user run']) + ([] if data else ['restore + SHA-256'])
            anyp = any(ok.values())
            lay[L] = dict(status='FAILING' if bad else 'VERIFIED' if verified else 'PARTIAL' if anyp else 'NONE', missing=missing,
                          tests=sorted(set(t for d in D for t, o in got[d])))
        if any(dims[d][0] == 'FAIL' for d in applicable) or any(lay[L]['status'] == 'FAILING' for L in LAYERS): status = 'FAILING'
        elif all(lay[L]['status'] == 'VERIFIED' for L in LAYERS): status = 'FULL'
        elif any(lay[L]['status'] in ('VERIFIED', 'PARTIAL') for L in LAYERS): status = 'PARTIAL'
        else: status = 'NONE'
        rows.append(dict(c, dimstatus={d: dims[d][0] for d in D}, outs={d: dims[d][1] for d in D}, layers=lay, spec=SPEC.get(c['id']), status=status))
    return rows

def components(rows):
    """Every source file of the product → the capabilities that cover it. A file no capability names is a hidden area."""
    root = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'src'); pre = {'A': 'Agent', 'S': 'Server', 'C': 'Core'}
    named = {}
    for r in rows:
        for part in r['code'].split(','):
            p = part.strip().split(' ')[0]
            if '/' not in p: continue
            a, b = p.split('/', 1); path = (pre.get(a, a) + '/' + b) if a in pre else p
            named.setdefault(path, []).append(r['id'])
    out = []
    for proj in ('Core', 'Server', 'Agent', 'Setup', 'ClientApp'):
        for dp, _, fs in os.walk(os.path.join(root, proj)):
            if '/bin' in dp or '/obj' in dp: continue
            for f in sorted(fs):
                if not f.endswith(('.cs', '.js')): continue
                rel = os.path.relpath(os.path.join(dp, f), root)
                caps = sorted(set(named.get(rel, []) + EXTRA_COMPONENTS.get(rel, [])))
                best = 'NONE' if not caps else max((r['status'] for r in rows if r['id'] in caps), key=['NONE', 'FAILING', 'PARTIAL', 'FULL'].index)
                out.append(dict(file=rel, caps=caps, status=best))
    return out

if __name__ == '__main__':
    res = results(); rows = evaluate(res)
    out = sys.argv[1] if len(sys.argv) > 1 else os.path.dirname(os.path.abspath(__file__))
    json.dump(rows, open(os.path.join(out, 'coverage.json'), 'w'), indent=1, default=str)
    cnt = lambda pred: sum(1 for r in rows if pred(r))
    print(json.dumps({'capabilities': len(rows), 'critical': cnt(lambda r: r['critical']), 'FULL': cnt(lambda r: r['status'] == 'FULL'), 'PARTIAL': cnt(lambda r: r['status'] == 'PARTIAL'),
                      'NONE': cnt(lambda r: r['status'] == 'NONE'), 'FAILING': cnt(lambda r: r['status'] == 'FAILING'),
                      'critical_FULL': cnt(lambda r: r['critical'] and r['status'] == 'FULL'),
                      'layers': {L: {k: cnt(lambda r, L=L, k=k: r['layers'][L]['status'] == k) for k in ('VERIFIED', 'PARTIAL', 'NONE', 'FAILING')} for L in LAYERS},
                      'hidden_components': sum(1 for x in components(rows) if not x['caps'])}))

def markdown(rows, path):
    sym = {'PASS': '✅', 'FAIL': '❌', 'NOT TESTED': '·', 'NOT RUN': '⚠', 'n/a': '—'}
    lsym = {'VERIFIED': '✅ מאומת', 'PARTIAL': '◐ חלקי', 'NONE': '· אין', 'FAILING': '❌ נכשל'}
    heb = {'FULL': 'מאומת במלואו', 'PARTIAL': 'חלקי', 'NONE': 'לא נבדק', 'FAILING': 'נכשל'}
    lname = {'component': 'רכיב', 'integration': 'אינטגרציה', 'e2e': 'קצה לקצה'}
    mname = {'contract': 'חוזה (מטרה/קלט/פלט צפוי)', 'happy': 'מסלול תקין', 'recovery': 'התאוששות', 'integrity': 'שלמות נתונים',
             'failure/boundary': 'כשל/גבולות', 'failure/recovery': 'כשל/התאוששות', 'a real user run': 'הרצה אמיתית של משתמש', 'restore + SHA-256': 'שחזור + SHA-256'}
    cnt = lambda pred: sum(1 for r in rows if pred(r))
    comps = components(rows); hidden = [x for x in comps if not x['caps']]
    L = ['# מטריצת כיסוי — שלוש שכבות לכל יכולת', '',
         'נוצרת אוטומטית מתוצאות ההרצה האחרונה (`tests/QA/capabilities.py`, החוזים ב-`tests/QA/specs.py`). לא נערכת ביד.', '',
         '- **רכיב**: הרכיב לבד, מול חוזה שנכתב מראש (מטרה, קלט ידוע, פלט צפוי): מסלול תקין, כשל/גבולות, התאוששות, שלמות נתונים. בדיקת xUnit שאינה מרימה שרת.',
         '- **אינטגרציה**: הרכיבים יחד: שרת אמיתי בתהליך, HTTP אמיתי, קבצים אמיתיים (xUnit עם `new Env(`).',
         '- **קצה לקצה**: פעולה אמיתית של משתמש על התוכנות האמיתיות (שרת וסוכן כתהליכים נפרדים, דפדפן, מתקין) עד שחזור ובדיקת הנתונים מחוץ למוצר (SHA-256). `tests/QA` ו-Windows.',
         '- **מאומת במלואו** רק כששלוש השכבות מאומתות. בדיקת יחידה שעברה, או מסע שעבר דרך היכולת, לבדם — לעולם לא.', '',
         '| | סה"כ | מאומת במלואו | חלקי | לא נבדק | נכשל |', '|---|---|---|---|---|---|',
         '| כל היכולות | %d | %d | %d | %d | %d |' % (len(rows), cnt(lambda r: r['status'] == 'FULL'), cnt(lambda r: r['status'] == 'PARTIAL'), cnt(lambda r: r['status'] == 'NONE'), cnt(lambda r: r['status'] == 'FAILING')),
         '| קריטיות | %d | %d | %d | %d | %d |' % (cnt(lambda r: r['critical']), cnt(lambda r: r['critical'] and r['status'] == 'FULL'), cnt(lambda r: r['critical'] and r['status'] == 'PARTIAL'), cnt(lambda r: r['critical'] and r['status'] == 'NONE'), cnt(lambda r: r['critical'] and r['status'] == 'FAILING')), '',
         '| שכבה | מאומת | חלקי | אין | נכשל |', '|---|---|---|---|---|']
    for Ly in LAYERS:
        L.append('| %s | %d | %d | %d | %d |' % (lname[Ly], *[cnt(lambda r, k=k: r['layers'][Ly]['status'] == k) for k in ('VERIFIED', 'PARTIAL', 'NONE', 'FAILING')]))
    L += ['', 'רכיבי קוד (קבצים) במוצר: **%d**; בלי יכולת שמכסה אותם (אזור נסתר): **%d** — ראו בסוף.' % (len(comps), len(hidden)), '',
          '| מזהה | יכולת | קריטי | מצב | רכיב | אינטגרציה | קצה לקצה | חסר | באגים (רגרסיה) |', '|' + '---|' * 9]
    for r in rows:
        miss = '; '.join('%s: %s' % (lname[Ly], ', '.join(mname.get(m, m) for m in r['layers'][Ly]['missing'])) for Ly in LAYERS if r['layers'][Ly]['status'] != 'VERIFIED' and r['layers'][Ly]['missing'])
        L.append('| %s | %s | %s | **%s** | %s | %s | %s | %s | %s |' % (r['id'], r['name'], 'כן' if r['critical'] else '', heb[r['status']],
                 lsym[r['layers']['component']['status']], lsym[r['layers']['integration']['status']], lsym[r['layers']['e2e']['status']], miss, ', '.join(str(b) for b in r['regression'])))
    L += ['', '## כל יכולת: החוזה, ובדיקות כל שכבה', '']
    for r in rows:
        L.append('### %s %s — %s' % (r['id'], r['name'], heb[r['status']]))
        sp = r.get('spec')
        if sp: L += ['- **מטרה:** ' + sp[0], '- **קלט ידוע:** ' + sp[1], '- **פלט צפוי (נקבע מראש):** ' + sp[2]]
        else: L.append('- **אין חוזה** — שכבת הרכיב לא יכולה להיות מאומתת')
        L.append('- קוד: `%s`' % r['code'])
        for Ly in LAYERS:
            t = r['layers'][Ly]
            L.append('- **%s** (%s): %s' % (lname[Ly], lsym[t['status']], ', '.join(t['tests']) if t['tests'] else 'אין בדיקות'))
        L.append('- ממדים: ' + ' · '.join('%s %s' % (d, sym[r['dimstatus'][d]]) for d in D))
        L.append('')
    L += ['## רכיבי קוד בלי יכולת (אזורים נסתרים)', '', 'כל קובץ קוד חדש מופיע כאן אוטומטית עד שיכולת במלאי מכסה אותו.', '']
    L += ['- `%s`' % x['file'] for x in hidden] or ['(אין)']
    open(path, 'w').write('\n'.join(L) + '\n')

if __name__ == '__main__':
    markdown(evaluate(results()), os.path.join(os.path.dirname(os.path.abspath(__file__)), 'COVERAGE-MATRIX.md'))
