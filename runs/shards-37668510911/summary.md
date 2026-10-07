# windows-latest, select 'ResticComponentTests DbHyperVTests AuditJ_ResticEngineTests WebRestoreTests ResticPathComponentTests NoSilentPassTests', 6 shards x 1 - run 37668510911 (6098a090e4469fb63dc471eb3ee210ceae4a3bd3)

**28 tests: 23 PASS, 1 FAIL, 4 NOT TESTED** (a test is PASS only when every job that had to run it reports PASS)

Build SHA-256: `7047e6204b18b9d2c0e52f5f8bd58066f0ab787e7abc7152b6b7af8fb0e334ae` (every shard checks it before running). Mode: strict - NOT green.

| job | state | tests | runner | minutes |
|---|---|---|---|---|
| s01-r01 | results | 7 | GitHub Actions 1000001459 | 10 |
| s02-r01 | results | 9 | GitHub Actions 1000001458 | 1 |
| s03-r01 | results | 1 | GitHub Actions 1000001457 | 1 |
| s04-r01 | results | 2 | GitHub Actions 1000001461 | 1 |
| s05-r01 | results | 3 | GitHub Actions 1000001460 | 1 |
| s06-r01 | results | 6 | GitHub Actions 1000001462 | 9 |

## Not PASS

| test | verdict | per job |
|---|---|---|
| OnlineBackup.Tests.AuditJ_ResticEngineTests.UnreadableFile_ExitCode3_IsAnErrorNamedInTheLog | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s01-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: needs restic and the right to drop capabilities (setpriv) |
| OnlineBackup.Tests.WebRestoreTests.CustomerRestoresChosenFilesFromTheWebsite_WithTheEncryptionPassword | FAIL | 0 PASS / 1 FAIL / 0 NOT TESTED of 1 - s03-r01: OnlineBackup.Agent.AgentException : The restore failed: {"message_type":"exit_error","code":1,"message":"Fatal: All path filters must be absolute, starting with |
| OnlineBackup.Tests.DbHyperVTests.HyperV_VmsExportedOnline_OnlyChangedDiskBlocksSent_RestoredAndImported | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s05-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: needs Linux (the stand-in tools are shell scripts) |
| OnlineBackup.Tests.DbHyperVTests.MySqlAndPostgres_OnRestic_AndAFailingDatabaseIsReported | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s05-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: needs Linux (the stand-in tools are shell scripts) |
| OnlineBackup.Tests.DbHyperVTests.MySql_EveryDatabaseDumped_PasswordNeverInTheLog_DumpsTravelAsDeltas_LoadIntoANewDatabase | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s05-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: needs Linux (the stand-in tools are shell scripts) |
