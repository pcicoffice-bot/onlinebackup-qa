# windows-latest, select 'UploadLimit_SetOnTheServer_SlowsARealBackupToTheLimit', 1 shards x 10 - run 38034793016 (19bef2fe357ad440d3e21beabf711990cc2ffd23)

**1 tests: 0 PASS, 1 FAIL, 0 NOT TESTED** (a test is PASS only when every job that had to run it reports PASS)

Build SHA-256: `90ee59cc07fb71f6f581fc8aad58c37dcdbfbfbef1c48a35275dc15c1419dc21` (every shard checks it before running). Mode: strict - NOT green.

| job | state | tests | runner | minutes |
|---|---|---|---|---|
| s01-r01 | results | 1 | GitHub Actions 1000002477 | 2 |
| s01-r02 | results | 1 | GitHub Actions 1000002505 | 2 |
| s01-r03 | results | 1 | GitHub Actions 1000002590 | 2 |
| s01-r04 | results | 1 | GitHub Actions 1000002444 | 2 |
| s01-r05 | results | 1 | GitHub Actions 1000002523 | 1 |
| s01-r06 | results | 1 | GitHub Actions 1000002592 | 1 |
| s01-r07 | results | 1 | GitHub Actions 1000002593 | 1 |
| s01-r08 | results | 1 | GitHub Actions 1000002595 | 2 |
| s01-r09 | results | 1 | GitHub Actions 1000002597 | 2 |
| s01-r10 | results | 1 | GitHub Actions 1000002604 | 2 |

## Not PASS

| test | verdict | per job |
|---|---|---|
| OnlineBackup.Tests.OptionsTests.UploadLimit_SetOnTheServer_SlowsARealBackupToTheLimit | FAIL | 4 PASS / 6 FAIL / 0 NOT TESTED of 10 - s01-r01: RESTIC: without the limit it took 5.9 s; s01-r02: RESTIC: without the limit it took 6.0 s; s01-r03: RESTIC: without the limit it took 6.0 s; s01-r04: RESTIC: without the limit it took 5.9 s; s01-r06: RESTIC: without the limit it took 6.1 s; s01-r10: RESTIC: without the limit it took 5.9 s |

## Repetitions

| test | PASS | FAIL | NOT TESTED |
|---|---|---|---|
| OnlineBackup.Tests.OptionsTests.UploadLimit_SetOnTheServer_SlowsARealBackupToTheLimit | 4 | 6 | 0 |
