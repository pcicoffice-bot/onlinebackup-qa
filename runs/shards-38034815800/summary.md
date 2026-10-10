# windows-latest, select 'OnlineBackup.Tests.InterruptionTests.ServerDownMidBackupAndBack_NextBackupRunsAtOnce', 1 shards x 2 - run 38034815800 (d03f95afbcfdb963c5023d5a7d782c13141c3249)

**1 tests: 0 PASS, 1 FAIL, 0 NOT TESTED** (a test is PASS only when every job that had to run it reports PASS)

Build SHA-256: `507dbaeec4534c2f5ea7c40ba31c4b57d7e751cff295eae878c31bb81cbab983` (every shard checks it before running). Mode: strict - NOT green.

| job | state | tests | runner | minutes |
|---|---|---|---|---|
| s01-r01 | results | 1 | GitHub Actions 1000002399 | 3 |
| s01-r02 | results | 1 | GitHub Actions 1000002365 | 3 |

## Not PASS

| test | verdict | per job |
|---|---|---|
| OnlineBackup.Tests.InterruptionTests.ServerDownMidBackupAndBack_NextBackupRunsAtOnce | FAIL | 0 PASS / 2 FAIL / 0 NOT TESTED of 2 - s01-r01: the agent did not end after the server went away (at 10:11:52.8); what it printed:; s01-r02: the agent did not end after the server went away (at 09:50:23.1); what it printed: |

## Repetitions

| test | PASS | FAIL | NOT TESTED |
|---|---|---|---|
| OnlineBackup.Tests.InterruptionTests.ServerDownMidBackupAndBack_NextBackupRunsAtOnce | 0 | 2 | 0 |
