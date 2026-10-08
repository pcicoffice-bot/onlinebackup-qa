# windows-latest, select 'OnlineBackup.Tests.InterruptionTests.ServerDownMidBackupAndBack_NextBackupRunsAtOnce', 1 shards x 2 - run 37708168783 (4e13aa01a2dce323fb167b3d91cc66c037884d55)

**1 tests: 0 PASS, 1 FAIL, 0 NOT TESTED** (a test is PASS only when every job that had to run it reports PASS)

Build SHA-256: `0c32d667a831808ad14339d5af5aae9e1299b6b5f607cd252134411d9f104b79` (every shard checks it before running). Mode: strict - NOT green.

| job | state | tests | runner | minutes |
|---|---|---|---|---|
| s01-r01 | results | 1 | GitHub Actions 1000001629 | 3 |
| s01-r02 | results | 1 | GitHub Actions 1000001628 | 3 |

## Not PASS

| test | verdict | per job |
|---|---|---|
| OnlineBackup.Tests.InterruptionTests.ServerDownMidBackupAndBack_NextBackupRunsAtOnce | FAIL | 0 PASS / 2 FAIL / 0 NOT TESTED of 2 - s01-r01: the agent did not end after the server went away (at 00:33:12.6); what it printed:; s01-r02: the agent did not end after the server went away (at 00:33:11.2); what it printed: |

## Repetitions

| test | PASS | FAIL | NOT TESTED |
|---|---|---|---|
| OnlineBackup.Tests.InterruptionTests.ServerDownMidBackupAndBack_NextBackupRunsAtOnce | 0 | 2 | 0 |
