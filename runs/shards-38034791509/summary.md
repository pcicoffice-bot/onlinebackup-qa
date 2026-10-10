# windows-latest, select 'ResticComponentTests DbHyperVTests AuditJ_ResticEngineTests WebRestoreTests ResticPathComponentTests NoSilentPassTests', 6 shards x 1 - run 38034791509 (31a8f6db9d9fcefa70b82054a1442bed54e43be2)

**28 tests: 23 PASS, 1 FAIL, 4 NOT TESTED** (a test is PASS only when every job that had to run it reports PASS)

Build SHA-256: `4f61ccf1e8bff364935582a9fef8bf2816eb6b34096adb8594844372fdb19057` (every shard checks it before running). Mode: strict - NOT green.

| job | state | tests | runner | minutes |
|---|---|---|---|---|
| s01-r01 | results | 7 | GitHub Actions 1000002344 | 10 |
| s02-r01 | results | 9 | GitHub Actions 1000002288 | 1 |
| s03-r01 | results | 1 | GitHub Actions 1000002326 | 1 |
| s04-r01 | results | 2 | GitHub Actions 1000002381 | 1 |
| s05-r01 | results | 3 | GitHub Actions 1000002322 | 1 |
| s06-r01 | results | 6 | GitHub Actions 1000002449 | 9 |

## Not PASS

| test | verdict | per job |
|---|---|---|
| OnlineBackup.Tests.AuditJ_ResticEngineTests.UnreadableFile_ExitCode3_IsAnErrorNamedInTheLog | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s01-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: needs restic and the right to drop capabilities (setpriv) |
| OnlineBackup.Tests.WebRestoreTests.CustomerRestoresChosenFilesFromTheWebsite_WithTheEncryptionPassword | FAIL | 0 PASS / 1 FAIL / 0 NOT TESTED of 1 - s03-r01: OnlineBackup.Agent.AgentException : The restore failed: {"message_type":"exit_error","code":1,"message":"Fatal: All path filters must be absolute, starting with |
| OnlineBackup.Tests.DbHyperVTests.HyperV_VmsExportedOnline_OnlyChangedDiskBlocksSent_RestoredAndImported | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s05-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: needs Linux (the stand-in tools are shell scripts) |
| OnlineBackup.Tests.DbHyperVTests.MySqlAndPostgres_OnRestic_AndAFailingDatabaseIsReported | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s05-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: needs Linux (the stand-in tools are shell scripts) |
| OnlineBackup.Tests.DbHyperVTests.MySql_EveryDatabaseDumped_PasswordNeverInTheLog_DumpsTravelAsDeltas_LoadIntoANewDatabase | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s05-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: needs Linux (the stand-in tools are shell scripts) |
