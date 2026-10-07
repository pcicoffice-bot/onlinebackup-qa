# windows-latest, select 'UploadLimit_SetOnTheServer_SlowsARealBackupToTheLimit', 1 shards x 10 - run 37668513604 (69ee2a4815e613bd219407cb8c6a87cd28ed2ad6)

**1 tests: 0 PASS, 1 FAIL, 0 NOT TESTED** (a test is PASS only when every job that had to run it reports PASS)

Build SHA-256: `7534fec9c32186dbece5ba4a38eaaba4cae91ceaec2ab35e391b6b4d51c84f7a` (every shard checks it before running). Mode: strict - NOT green.

| job | state | tests | runner | minutes |
|---|---|---|---|---|
| s01-r01 | results | 1 | GitHub Actions 1000001453 | 2 |
| s01-r02 | results | 1 | GitHub Actions 1000001456 | 1 |
| s01-r03 | results | 1 | GitHub Actions 1000001452 | 2 |
| s01-r04 | results | 1 | GitHub Actions 1000001454 | 2 |
| s01-r05 | results | 1 | GitHub Actions 1000001455 | 2 |
| s01-r06 | results | 1 | GitHub Actions 1000001463 | 1 |
| s01-r07 | results | 1 | GitHub Actions 1000001464 | 2 |
| s01-r08 | results | 1 | GitHub Actions 1000001465 | 2 |
| s01-r09 | results | 1 | GitHub Actions 1000001466 | 2 |
| s01-r10 | results | 1 | GitHub Actions 1000001467 | 2 |

## Not PASS

| test | verdict | per job |
|---|---|---|
| OnlineBackup.Tests.OptionsTests.UploadLimit_SetOnTheServer_SlowsARealBackupToTheLimit | FAIL | 0 PASS / 10 FAIL / 0 NOT TESTED of 10 - s01-r01: RESTIC: without the limit it took 5.9 s; s01-r02: : without the limit it took 5.1 s; s01-r03: RESTIC: without the limit it took 6.3 s; s01-r04: RESTIC: without the limit it took 6.1 s; s01-r05: RESTIC: without the limit it took 6.2 s; s01-r06: RESTIC: without the limit it took 5.1 s; s01-r07: RESTIC: without the limit it took 5.8 s; s01-r08: RESTIC: without the limit it took 5.8 s; s01-r09: RESTIC: without the limit it took 5.6 s; s01-r10: RESTIC: without the limit it took 6.0 s |

## Repetitions

| test | PASS | FAIL | NOT TESTED |
|---|---|---|---|
| OnlineBackup.Tests.OptionsTests.UploadLimit_SetOnTheServer_SlowsARealBackupToTheLimit | 0 | 10 | 0 |
