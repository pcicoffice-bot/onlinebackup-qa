# windows-latest, select 'OnlineBackup.Tests.PilotChallengeSecurityTests.AU06_AResellersAdministrator_EveryAdminRoute_OnAnotherResellersOrTheSystemsCustomer_IsRefused_NothingChanges OnlineBackup.Tests.InterruptionTests.ServerDownMidBackupAndBack_NextBackupRunsAtOnce', 1 shards x 1 - run 37705110515 (ec114de33d3e7921a20ec5701a3d705caaf233c3)

**2 tests: 0 PASS, 2 FAIL, 0 NOT TESTED** (a test is PASS only when every job that had to run it reports PASS)

Build SHA-256: `4bccc3e12c60f99e17c892d7c12b2f47a2a9ee6483d69964b2aa8aa6c6c405e9` (every shard checks it before running). Mode: strict - NOT green.

| job | state | tests | runner | minutes |
|---|---|---|---|---|
| s01-r01 | results | 2 | GitHub Actions 1000001617 | 3 |

## Not PASS

| test | verdict | per job |
|---|---|---|
| OnlineBackup.Tests.InterruptionTests.ServerDownMidBackupAndBack_NextBackupRunsAtOnce | FAIL | 0 PASS / 1 FAIL / 0 NOT TESTED of 1 - s01-r01: the agent did not end after the server went away (at 23:59:08.8); what it printed: |
| OnlineBackup.Tests.PilotChallengeSecurityTests.AU06_AResellersAdministrator_EveryAdminRoute_OnAnotherResellersOrTheSystemsCustomer_IsRefused_NothingChanges | FAIL | 0 PASS / 1 FAIL / 0 NOT TESTED of 1 - s01-r01: 24 not refused: GET /api/admin/users/beta-c1%00/sets/1791417670774 -> 400  POST /api/admin/users/beta-c1%00/sets/1791417670774 -> 400  GET /api/admin/users/beta |
