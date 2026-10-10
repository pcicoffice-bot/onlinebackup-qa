# windows-latest, select 'OnlineBackup.Tests.PilotChallengeSecurityTests.AU06_AResellersAdministrator_EveryAdminRoute_OnAnotherResellersOrTheSystemsCustomer_IsRefused_NothingChanges OnlineBackup.Tests.InterruptionTests.ServerDownMidBackupAndBack_NextBackupRunsAtOnce', 1 shards x 1 - run 38034812632 (51768e7f17982ebbe498ee1d71961cdd2420b5a8)

**2 tests: 0 PASS, 2 FAIL, 0 NOT TESTED** (a test is PASS only when every job that had to run it reports PASS)

Build SHA-256: `407c3c557dbf6641b948f3376a048eaf9d584b27a20798b0e7d70b26d3c1bd0d` (every shard checks it before running). Mode: strict - NOT green.

| job | state | tests | runner | minutes |
|---|---|---|---|---|
| s01-r01 | results | 2 | GitHub Actions 1000002266 | 3 |

## Not PASS

| test | verdict | per job |
|---|---|---|
| OnlineBackup.Tests.InterruptionTests.ServerDownMidBackupAndBack_NextBackupRunsAtOnce | FAIL | 0 PASS / 1 FAIL / 0 NOT TESTED of 1 - s01-r01: the agent did not end after the server went away (at 08:30:20.1); what it printed: |
| OnlineBackup.Tests.PilotChallengeSecurityTests.AU06_AResellersAdministrator_EveryAdminRoute_OnAnotherResellersOrTheSystemsCustomer_IsRefused_NothingChanges | FAIL | 0 PASS / 1 FAIL / 0 NOT TESTED of 1 - s01-r01: 24 not refused: GET /api/admin/users/beta-c1%00/sets/1791621141378 -> 400  POST /api/admin/users/beta-c1%00/sets/1791621141378 -> 400  GET /api/admin/users/beta |
