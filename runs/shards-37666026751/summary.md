# windows-latest, select 'ResticComponentTests DbHyperVTests AuditJ_ResticEngineTests Restic_LongChain WebRestoreTests', 5 shards x 1 - run 37666026751 (866df87bb4a21112cf12f7c53818557527e2b3c9)

**18 tests: 7 PASS, 11 FAIL, 0 NOT TESTED** (a test is PASS only when every job that had to run it reports PASS)

Build SHA-256: `befb2b229bc86f2828c103ad30201e0202c090d80871192f16b90f9c319900fc` (every shard checks it before running). Mode: strict - NOT green.

| job | state | tests | runner | minutes |
|---|---|---|---|---|
| s01-r01 | results | 7 | GitHub Actions 1000001435 | 9 |
| s02-r01 | results | 6 | GitHub Actions 1000001436 | 1 |
| s03-r01 | results | 3 | GitHub Actions 1000001438 | 1 |
| s04-r01 | results | 1 | GitHub Actions 1000001437 | 23 |
| s05-r01 | results | 1 | GitHub Actions 1000001434 | 1 |

## Not PASS

| test | verdict | per job |
|---|---|---|
| OnlineBackup.Tests.AuditJ_ResticEngineTests.Restore_UnicodeSpacesAndLongNames_ByteIdentical_WholeAndSingleFile | FAIL | 0 PASS / 1 FAIL / 0 NOT TESTED of 1 - s01-r01: System.IO.IOException : The filename, directory name, or volume label syntax is incorrect. : 'C:\Users\runneradmin\AppData\Local\Temp\obaj-17a43887\src\semi;col |
| OnlineBackup.Tests.ResticComponentTests.ABackupStartedWhileTheRepositoryIsLockedForAMoment_WaitsForTheLock_AndCompletes | FAIL | 0 PASS / 1 FAIL / 0 NOT TESTED of 1 - s02-r01: System.IO.IOException : The filename, directory name, or volume label syntax is incorrect. : 'C:\Users\runneradmin\AppData\Local\Temp\obrc-e4d2583f\src\a\b\Repo |
| OnlineBackup.Tests.ResticComponentTests.AnotherKey_ReadsNothing | FAIL | 0 PASS / 1 FAIL / 0 NOT TESTED of 1 - s02-r01: System.IO.IOException : The filename, directory name, or volume label syntax is incorrect. : 'C:\Users\runneradmin\AppData\Local\Temp\obrc-7f42a2d1\src\a\b\Repo |
| OnlineBackup.Tests.ResticComponentTests.DamagedPack_IsFoundByCheck_AndItsRestoreFailsLoudly | FAIL | 0 PASS / 1 FAIL / 0 NOT TESTED of 1 - s02-r01: System.IO.IOException : The filename, directory name, or volume label syntax is incorrect. : 'C:\Users\runneradmin\AppData\Local\Temp\obrc-067e1898\src\a\b\Repo |
| OnlineBackup.Tests.ResticComponentTests.EveryWritingResticCommand_WaitsForALockedRepository | FAIL | 0 PASS / 1 FAIL / 0 NOT TESTED of 1 - s02-r01: System.IO.IOException : The filename, directory name, or volume label syntax is incorrect. : 'C:\Users\runneradmin\AppData\Local\Temp\obrc-b7c98ca9\src\a\b\Repo |
| OnlineBackup.Tests.ResticComponentTests.KnownTree_BacksUp_ChecksClean_RestoresIdentical_SecondRunAddsNothing | FAIL | 0 PASS / 1 FAIL / 0 NOT TESTED of 1 - s02-r01: System.IO.IOException : The filename, directory name, or volume label syntax is incorrect. : 'C:\Users\runneradmin\AppData\Local\Temp\obrc-5127c859\src\a\b\Repo |
| OnlineBackup.Tests.ResticComponentTests.StoppedInTheMiddle_RepositoryChecksClean_NextBackupCompletes_RestoresIdentical | FAIL | 0 PASS / 1 FAIL / 0 NOT TESTED of 1 - s02-r01: System.IO.IOException : The filename, directory name, or volume label syntax is incorrect. : 'C:\Users\runneradmin\AppData\Local\Temp\obrc-265deb41\src\a\b\Repo |
| OnlineBackup.Tests.DbHyperVTests.HyperV_VmsExportedOnline_OnlyChangedDiskBlocksSent_RestoredAndImported | FAIL | 0 PASS / 1 FAIL / 0 NOT TESTED of 1 - s03-r01: 1791397355618,start,,0,,,,0 1791397355618,info,,0,"Start [ Microsoft Windows NT 10.0.26100.0 (PC-agent), engine restic ]",,,0 1791397358030,info,,0,Repository c |
| OnlineBackup.Tests.DbHyperVTests.MySqlAndPostgres_OnRestic_AndAFailingDatabaseIsReported | FAIL | 0 PASS / 1 FAIL / 0 NOT TESTED of 1 - s03-r01: Assert.Equal() Failure: Strings differ                    ↓ (pos 8) Expected: "BS_STOP_SUCCESS_WITH_WARNING" Actual:   "BS_STOP_BY_SYSTEM_ERROR"             |
| OnlineBackup.Tests.NightP_RestoreChainTests.Restic_LongChain_EverySnapshotRestoresIdentical | FAIL | 0 PASS / 1 FAIL / 0 NOT TESTED of 1 - s04-r01: System.IO.IOException : Access to the path '\\?\C:\Users\runneradmin\AppData\Local\Temp\obnp-312944b0\restore-0\C\Users' is denied. |
| OnlineBackup.Tests.WebRestoreTests.CustomerRestoresChosenFilesFromTheWebsite_WithTheEncryptionPassword | FAIL | 0 PASS / 1 FAIL / 0 NOT TESTED of 1 - s05-r01: OnlineBackup.Agent.AgentException : The restore failed: {"message_type":"exit_error","code":1,"message":"Fatal: All path filters must be absolute, starting with |
