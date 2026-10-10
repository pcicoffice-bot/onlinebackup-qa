# windows-latest, select 'ResticComponentTests DbHyperVTests AuditJ_ResticEngineTests Restic_LongChain WebRestoreTests', 5 shards x 1 - run 38034789032 (6f35724d3ecaabad6e8ff1b04a41c91562ffe076)

**18 tests: 7 PASS, 11 FAIL, 0 NOT TESTED** (a test is PASS only when every job that had to run it reports PASS)

Build SHA-256: `bb48131bf07ef8a84bd825e9888ff1a0e1b61ab1a07d8e18be26cf1263ae7a35` (every shard checks it before running). Mode: strict - NOT green.

| job | state | tests | runner | minutes |
|---|---|---|---|---|
| s01-r01 | results | 7 | GitHub Actions 1000002331 | 9 |
| s02-r01 | results | 6 | GitHub Actions 1000002406 | 1 |
| s03-r01 | results | 3 | GitHub Actions 1000002493 | 1 |
| s04-r01 | results | 1 | GitHub Actions 1000002388 | 23 |
| s05-r01 | results | 1 | GitHub Actions 1000002503 | 1 |

## Not PASS

| test | verdict | per job |
|---|---|---|
| OnlineBackup.Tests.AuditJ_ResticEngineTests.Restore_UnicodeSpacesAndLongNames_ByteIdentical_WholeAndSingleFile | FAIL | 0 PASS / 1 FAIL / 0 NOT TESTED of 1 - s01-r01: System.IO.IOException : The filename, directory name, or volume label syntax is incorrect. : 'C:\Users\runneradmin\AppData\Local\Temp\obaj-b9ec2912\src\semi;col |
| OnlineBackup.Tests.ResticComponentTests.ABackupStartedWhileTheRepositoryIsLockedForAMoment_WaitsForTheLock_AndCompletes | FAIL | 0 PASS / 1 FAIL / 0 NOT TESTED of 1 - s02-r01: System.IO.IOException : The filename, directory name, or volume label syntax is incorrect. : 'C:\Users\runneradmin\AppData\Local\Temp\obrc-be6f1f72\src\a\b\Repo |
| OnlineBackup.Tests.ResticComponentTests.AnotherKey_ReadsNothing | FAIL | 0 PASS / 1 FAIL / 0 NOT TESTED of 1 - s02-r01: System.IO.IOException : The filename, directory name, or volume label syntax is incorrect. : 'C:\Users\runneradmin\AppData\Local\Temp\obrc-df2142f7\src\a\b\Repo |
| OnlineBackup.Tests.ResticComponentTests.DamagedPack_IsFoundByCheck_AndItsRestoreFailsLoudly | FAIL | 0 PASS / 1 FAIL / 0 NOT TESTED of 1 - s02-r01: System.IO.IOException : The filename, directory name, or volume label syntax is incorrect. : 'C:\Users\runneradmin\AppData\Local\Temp\obrc-11892995\src\a\b\Repo |
| OnlineBackup.Tests.ResticComponentTests.EveryWritingResticCommand_WaitsForALockedRepository | FAIL | 0 PASS / 1 FAIL / 0 NOT TESTED of 1 - s02-r01: System.IO.IOException : The filename, directory name, or volume label syntax is incorrect. : 'C:\Users\runneradmin\AppData\Local\Temp\obrc-2b0a14a8\src\a\b\Repo |
| OnlineBackup.Tests.ResticComponentTests.KnownTree_BacksUp_ChecksClean_RestoresIdentical_SecondRunAddsNothing | FAIL | 0 PASS / 1 FAIL / 0 NOT TESTED of 1 - s02-r01: System.IO.IOException : The filename, directory name, or volume label syntax is incorrect. : 'C:\Users\runneradmin\AppData\Local\Temp\obrc-3800f2f0\src\a\b\Repo |
| OnlineBackup.Tests.ResticComponentTests.StoppedInTheMiddle_RepositoryChecksClean_NextBackupCompletes_RestoresIdentical | FAIL | 0 PASS / 1 FAIL / 0 NOT TESTED of 1 - s02-r01: System.IO.IOException : The filename, directory name, or volume label syntax is incorrect. : 'C:\Users\runneradmin\AppData\Local\Temp\obrc-47e531d1\src\a\b\Repo |
| OnlineBackup.Tests.DbHyperVTests.HyperV_VmsExportedOnline_OnlyChangedDiskBlocksSent_RestoredAndImported | FAIL | 0 PASS / 1 FAIL / 0 NOT TESTED of 1 - s03-r01: 1791630312210,start,,0,,,,0 1791630312210,info,,0,"Start [ Microsoft Windows NT 10.0.26100.0 (PC-agent), engine restic ]",,,0 1791630314969,info,,0,Repository c |
| OnlineBackup.Tests.DbHyperVTests.MySqlAndPostgres_OnRestic_AndAFailingDatabaseIsReported | FAIL | 0 PASS / 1 FAIL / 0 NOT TESTED of 1 - s03-r01: Assert.Equal() Failure: Strings differ                    ↓ (pos 8) Expected: "BS_STOP_SUCCESS_WITH_WARNING" Actual:   "BS_STOP_BY_SYSTEM_ERROR"             |
| OnlineBackup.Tests.NightP_RestoreChainTests.Restic_LongChain_EverySnapshotRestoresIdentical | FAIL | 0 PASS / 1 FAIL / 0 NOT TESTED of 1 - s04-r01: System.IO.IOException : Access to the path '\\?\C:\Users\runneradmin\AppData\Local\Temp\obnp-ba2eb520\restore-0\C\Users' is denied. |
| OnlineBackup.Tests.WebRestoreTests.CustomerRestoresChosenFilesFromTheWebsite_WithTheEncryptionPassword | FAIL | 0 PASS / 1 FAIL / 0 NOT TESTED of 1 - s05-r01: OnlineBackup.Agent.AgentException : The restore failed: {"message_type":"exit_error","code":1,"message":"Fatal: All path filters must be absolute, starting with |
