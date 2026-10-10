# Journeys (Playwright) - run 38034751727 (daad38e786b14f3cc261196f6a7277e43d6e46ed)

**59 tests: 48 PASS, 1 FAIL, 10 NOT TESTED** (a test is PASS only when every job that had to run it reports PASS)

Build SHA-256: `3d8c87236616e0ad5a4a2c7042abc967036089fd883d038cfc53dd70b298ff28` (every shard checks it before running). Mode: strict - NOT green.

| job | state | tests | runner | minutes |
|---|---|---|---|---|
| s01-r01 | results | 15 | GitHub Actions 1000002327 | 10 |
| s02-r01 | results | 15 | GitHub Actions 1000002348 | 15 |
| s03-r01 | results | 15 | GitHub Actions 1000002309 | 27 |
| s04-r01 | results | 14 | GitHub Actions 1000002390 | 10 |

## Not PASS

| test | verdict | per job |
|---|---|---|
| failure-recovery/f14-cloned-computer.spec.ts > F14 cloned computer (same registration, same set, own files) → no success with the other copy's content → each newest point restores identical | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s01-r01: [declared] outcome NotExecuted: NOT TESTED: this machine does not allow a private mount namespace (unshare -m needs root) |
| failure-recovery/f4-restore-interrupted.spec.ts > F4 restore killed mid-way → restore again into the same folder → every file identical, no leftovers | FAIL | 0 PASS / 1 FAIL / 0 NOT TESTED of 1 - s01-r01: Error: ENOENT: no such file or directory, open '/tmp/obqa-QF50fw/restore/tmp/obqa-QF50fw/data/Binary/random.bin.14c9899b.ob-restoring' |
| failure-recovery/f8-client-disk-full-restore.spec.ts > F8 client disk full during a restore → reported failed, nothing half-written passes as whole → restore to a good disk identical | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s01-r01: [declared] outcome NotExecuted: NOT TESTED: this machine does not allow mounting a small disk (tmpfs needs root / CAP_SYS_ADMIN) |
| failure-recovery/n5-server-disk-full-second-run.spec.ts > N5 server disk full during backup 2 → failed truthfully, point 1 intact and restorable DURING the fault → space freed → backup 3 → both points identical (SHA-256) | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s01-r01: [declared] outcome NotExecuted: NOT TESTED: this machine does not allow mounting a small disk |
| failure-recovery/n10-external-process-hang.spec.ts > N10a pre-command that never ends → stopped at the real 1-hour limit (agent clock 120×) with its child → backup completes → restore identical (SHA-256) | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s02-r01: [declared] outcome NotExecuted: NOT TESTED: libfaketime is not installed (the 1-hour limit cannot be shortened by a setting) |
| failure-recovery/n10-external-process-hang.spec.ts > N10c restic hangs silently during the backup → stopped at the real 30-min idle limit (agent clock 120×), nothing left running, no false success → real restic → backup → restore identical (SHA-256) | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s02-r01: [declared] outcome NotExecuted: NOT TESTED: libfaketime is not installed |
| failure-recovery/n9-repository-unavailable.spec.ts > N9a the server's set folder read-only for a whole run → no false success, point 1 restorable during the fault → writable again → backup → restore identical (SHA-256) | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s02-r01: [declared] outcome NotExecuted: NOT TESTED: this machine does not allow a read-only bind mount: ERR 32 mount: /tmp/obqa-lgWOsA/users/qa-n9a/files/1791626024652: must be su |
| failure-recovery/f6-server-disk-full.spec.ts > F6 server disk full mid-backup → clear failure, no lock, space given back → smaller backup succeeds and restores identical | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s03-r01: [declared] outcome NotExecuted: NOT TESTED: this machine does not allow mounting a small disk |
| failure-recovery/n6-agent-disk-full.spec.ts > N6a agent temp folder full → backup still correct; restore through the full temp fails truthfully, no half file → temp freed → restore identical (SHA-256) | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s03-r01: [declared] outcome NotExecuted: NOT TESTED: this machine does not allow mounting a small disk |
| failure-recovery/n6-agent-disk-full.spec.ts > N6b the set folder on the computer is full after the server committed → server and computer tell the truth → next run correct → restore identical (SHA-256) | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s03-r01: [declared] outcome NotExecuted: NOT TESTED: this machine does not allow mounting a small disk |
| failure-recovery/f13-client-clock-ahead.spec.ts > F13 client clock 3 h ahead of the server → the daily slot runs once, not again every 15 minutes → restore identical | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s04-r01: [declared] outcome NotExecuted: NOT TESTED: libfaketime (faketime) is not installed — the agent has no clock offset of its own |

## Control (the previous method) vs shards

**DIFFERENT in 1 tests** - the sharded run is NOT proven equal to the previous method:
- failure-recovery/f4-restore-interrupted.spec.ts > F4 restore killed mid-way → restore again into the same folder → every file identical, no leftovers: control PASS, sharded FAIL
