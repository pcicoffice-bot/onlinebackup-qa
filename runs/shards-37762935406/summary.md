# ubuntu-latest, select 'Restic', 4 shards x 1, control (previous method) - run 37762935406 (bad7c5f74e44b13df44b3bc0498c6a26cb766d77)

**64 tests: 64 PASS, 0 FAIL, 0 NOT TESTED** (a test is PASS only when every job that had to run it reports PASS)

Build SHA-256: `265ab40bf8ebaebf03bcb09f16f43de0ad56544a0bb567743c459995da94e597` (every shard checks it before running). Mode: strict - green.

| job | state | tests | runner | minutes |
|---|---|---|---|---|
| s01-r01 | results | 1 | GitHub Actions 1000001775 | 8 |
| s02-r01 | results | 14 | GitHub Actions 1000001776 | 5 |
| s03-r01 | results | 23 | GitHub Actions 1000001774 | 6 |
| s04-r01 | results | 26 | GitHub Actions 1000001777 | 4 |

## Control (the previous method) vs shards

**SAME** - every one of 64 tests has the same result in the control run and sharded
