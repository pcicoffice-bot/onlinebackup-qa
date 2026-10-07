# Journeys (Playwright) - run 37681772221 (c9bce6705996665d000303b0144eb86146d405e0)

**59 tests: 49 PASS, 0 FAIL, 10 NOT TESTED** (a test is PASS only when every job that had to run it reports PASS)

Build SHA-256: `0618af7038f413617dc071cb628901d64b6fe3fa397a2fff535e11f205b59c4a` (every shard checks it before running). Mode: strict - NOT green.

| job | state | tests | runner | minutes |
|---|---|---|---|---|
| s01-r01 | results | 15 | GitHub Actions 1000001552 | 10 |
| s02-r01 | results | 15 | GitHub Actions 1000001554 | 10 |
| s03-r01 | results | 15 | GitHub Actions 1000001551 | 27 |
| s04-r01 | results | 14 | GitHub Actions 1000001550 | 10 |

## Not PASS

| test | verdict | per job |
|---|---|---|
| failure-recovery/f14-cloned-computer.spec.ts > F14 cloned computer (same registration, same set, own files) → no success with the other copy's content → each newest point restores identical | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s01-r01: [declared] outcome NotExecuted: NOT TESTED: this machine does not allow a private mount namespace (unshare -m needs root) |
| failure-recovery/f8-client-disk-full-restore.spec.ts > F8 client disk full during a restore → reported failed, nothing half-written passes as whole → restore to a good disk identical | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s01-r01: [declared] outcome NotExecuted: NOT TESTED: this machine does not allow mounting a small disk (tmpfs needs root / CAP_SYS_ADMIN) |
| failure-recovery/n5-server-disk-full-second-run.spec.ts > N5 server disk full during backup 2 → failed truthfully, point 1 intact and restorable DURING the fault → space freed → backup 3 → both points identical (SHA-256) | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s01-r01: [declared] outcome NotExecuted: NOT TESTED: this machine does not allow mounting a small disk |
| failure-recovery/n10-external-process-hang.spec.ts > N10a pre-command that never ends → stopped at the real 1-hour limit (agent clock 120×) with its child → backup completes → restore identical (SHA-256) | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s02-r01: [declared] outcome NotExecuted: NOT TESTED: libfaketime is not installed (the 1-hour limit cannot be shortened by a setting) |
| failure-recovery/n10-external-process-hang.spec.ts > N10c restic hangs silently during the backup → stopped at the real 30-min idle limit (agent clock 120×), nothing left running, no false success → real restic → backup → restore identical (SHA-256) | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s02-r01: [declared] outcome NotExecuted: NOT TESTED: libfaketime is not installed |
| failure-recovery/n9-repository-unavailable.spec.ts > N9a the server's set folder read-only for a whole run → no false success, point 1 restorable during the fault → writable again → backup → restore identical (SHA-256) | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s02-r01: [declared] outcome NotExecuted: NOT TESTED: this machine does not allow a read-only bind mount: ERR 32 mount: /tmp/obqa-1tL3Ba/users/qa-n9a/files/1791405458232: must be su |
| failure-recovery/f6-server-disk-full.spec.ts > F6 server disk full mid-backup → clear failure, no lock, space given back → smaller backup succeeds and restores identical | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s03-r01: [declared] outcome NotExecuted: NOT TESTED: this machine does not allow mounting a small disk |
| failure-recovery/n6-agent-disk-full.spec.ts > N6a agent temp folder full → backup still correct; restore through the full temp fails truthfully, no half file → temp freed → restore identical (SHA-256) | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s03-r01: [declared] outcome NotExecuted: NOT TESTED: this machine does not allow mounting a small disk |
| failure-recovery/n6-agent-disk-full.spec.ts > N6b the set folder on the computer is full after the server committed → server and computer tell the truth → next run correct → restore identical (SHA-256) | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s03-r01: [declared] outcome NotExecuted: NOT TESTED: this machine does not allow mounting a small disk |
| failure-recovery/f13-client-clock-ahead.spec.ts > F13 client clock 3 h ahead of the server → the daily slot runs once, not again every 15 minutes → restore identical | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s04-r01: [declared] outcome NotExecuted: NOT TESTED: libfaketime (faketime) is not installed — the agent has no clock offset of its own |

## Control (the previous method) vs shards

**SAME** - every one of 59 tests has the same result in the control run and sharded
