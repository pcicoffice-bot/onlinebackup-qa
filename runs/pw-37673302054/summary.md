# Journeys (Playwright) - run 37673302054 (cd29707587ac805466a2cb87dd5f12bc10b03a9c)

**59 tests: 37 PASS, 0 FAIL, 22 NOT TESTED** (a test is PASS only when every job that had to run it reports PASS)

Build SHA-256: `c279d197b9a40c844c1640a70dc870431211d22b112f71f8bf798f10723a298c` (every shard checks it before running). Mode: strict - NOT green.

| job | state | tests | runner | minutes |
|---|---|---|---|---|
| s01-r01 | results | 15 | GitHub Actions 1000001500 | 9 |
| s02-r01 | results | 15 | GitHub Actions 1000001501 | 11 |
| s03-r01 | no results (cancelled, did not start, or crashed before writing) | 15 | ? | ? |
| s04-r01 | results | 14 | GitHub Actions 1000001502 | 10 |

## Not PASS

| test | verdict | per job |
|---|---|---|
| failure-recovery/f14-cloned-computer.spec.ts > F14 cloned computer (same registration, same set, own files) → no success with the other copy's content → each newest point restores identical | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s01-r01: [declared] outcome NotExecuted: NOT TESTED: this machine does not allow a private mount namespace (unshare -m needs root) |
| failure-recovery/f8-client-disk-full-restore.spec.ts > F8 client disk full during a restore → reported failed, nothing half-written passes as whole → restore to a good disk identical | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s01-r01: [declared] outcome NotExecuted: NOT TESTED: this machine does not allow mounting a small disk (tmpfs needs root / CAP_SYS_ADMIN) |
| failure-recovery/n5-server-disk-full-second-run.spec.ts > N5 server disk full during backup 2 → failed truthfully, point 1 intact and restorable DURING the fault → space freed → backup 3 → both points identical (SHA-256) | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s01-r01: [declared] outcome NotExecuted: NOT TESTED: this machine does not allow mounting a small disk |
| failure-recovery/n10-external-process-hang.spec.ts > N10a pre-command that never ends → stopped at the real 1-hour limit (agent clock 120×) with its child → backup completes → restore identical (SHA-256) | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s02-r01: [declared] outcome NotExecuted: NOT TESTED: libfaketime is not installed (the 1-hour limit cannot be shortened by a setting) |
| failure-recovery/n10-external-process-hang.spec.ts > N10c restic hangs silently during the backup → stopped at the real 30-min idle limit (agent clock 120×), nothing left running, no false success → real restic → backup → restore identical (SHA-256) | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s02-r01: [declared] outcome NotExecuted: NOT TESTED: libfaketime is not installed |
| failure-recovery/n9-repository-unavailable.spec.ts > N9a the server's set folder read-only for a whole run → no false success, point 1 restorable during the fault → writable again → backup → restore identical (SHA-256) | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s02-r01: [declared] outcome NotExecuted: NOT TESTED: this machine does not allow a read-only bind mount: ERR 32 mount: /tmp/obqa-Oei4li/users/qa-n9a/files/1791401326376: must be su |
| e2e/q6-quota.spec.ts > Q6 ST-05 a full quota stops the next backup clearly, keeps every existing backup restorable, and a raised quota lets the next one complete | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s03-r01: [missing] job s03-r01: no results (cancelled, did not start, or crashed before writing) |
| failure-recovery/f12-same-computer-name.spec.ts > F12 two computers with the same name → each set backed up on schedule by its own computer, nothing blocked or corrupted → both restore identical | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s03-r01: [missing] job s03-r01: no results (cancelled, did not start, or crashed before writing) |
| failure-recovery/f2-network-cut.spec.ts > F2 network cut mid-backup → failure, not success → line back → next backup restores identical, no ghost | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s03-r01: [missing] job s03-r01: no results (cancelled, did not start, or crashed before writing) |
| failure-recovery/f6-server-disk-full.spec.ts > F6 server disk full mid-backup → clear failure, no lock, space given back → smaller backup succeeds and restores identical | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s03-r01: [missing] job s03-r01: no results (cancelled, did not start, or crashed before writing) |
| failure-recovery/n3-slow-line.spec.ts > N3 line throttled to 50 KB/s for a whole backup → succeeds, only slower (duration recorded) → new backup → restore identical (SHA-256) | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s03-r01: [missing] job s03-r01: no results (cancelled, did not start, or crashed before writing) |
| failure-recovery/n6-agent-disk-full.spec.ts > N6a agent temp folder full → backup still correct; restore through the full temp fails truthfully, no half file → temp freed → restore identical (SHA-256) | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s03-r01: [missing] job s03-r01: no results (cancelled, did not start, or crashed before writing) |
| failure-recovery/n6-agent-disk-full.spec.ts > N6b the set folder on the computer is full after the server committed → server and computer tell the truth → next run correct → restore identical (SHA-256) | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s03-r01: [missing] job s03-r01: no results (cancelled, did not start, or crashed before writing) |
| journeys/j1-login-navigation.spec.ts > J1 sign-in, every menu page opens without errors, reload keeps the session, sign-out ends it on the server | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s03-r01: [missing] job s03-r01: no results (cancelled, did not start, or crashed before writing) |
| journeys/j5-kill-agent.spec.ts > J5 kill the agent during a backup → no ghost "running", the failure is shown, the next backup and its restore are right | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s03-r01: [missing] job s03-r01: no results (cancelled, did not start, or crashed before writing) |
| journeys/ui-01-every-screen.spec.ts > UI-01 sign-out in one tab ends the session for every tab (server check); a forged session shows the sign-in form | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s03-r01: [missing] job s03-r01: no results (cancelled, did not start, or crashed before writing) |
| journeys/ui-01-every-screen.spec.ts > UI-01 with real data: every customer tab, every set tab, run details and logs open without a console error or a failed request | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s03-r01: [missing] job s03-r01: no results (cancelled, did not start, or crashed before writing) |
| journeys/ui-03-truth-after-failures.spec.ts > UI-03 stopped by the technician: "Stopped" (never Succeeded), not running anywhere on the site | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s03-r01: [missing] job s03-r01: no results (cancelled, did not start, or crashed before writing) |
| journeys/ui-03-truth-after-failures.spec.ts > UI-03 the computer dies during a backup and never comes back: the "running" row goes away and the run is shown Failed | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s03-r01: [missing] job s03-r01: no results (cancelled, did not start, or crashed before writing) |
| journeys/ui-03-truth-after-failures.spec.ts > UI-03 the server crashes under a backup and comes back: the site shows the run Failed in red, nothing running | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s03-r01: [missing] job s03-r01: no results (cancelled, did not start, or crashed before writing) |
| seed.spec.ts > seed | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s03-r01: [missing] job s03-r01: no results (cancelled, did not start, or crashed before writing) |
| failure-recovery/f13-client-clock-ahead.spec.ts > F13 client clock 3 h ahead of the server → the daily slot runs once, not again every 15 minutes → restore identical | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s04-r01: [declared] outcome NotExecuted: NOT TESTED: libfaketime (faketime) is not installed — the agent has no clock offset of its own |

## Control (the previous method) vs shards

**DIFFERENT in 12 tests** - the sharded run is NOT proven equal to the previous method:
- e2e/q6-quota.spec.ts > Q6 ST-05 a full quota stops the next backup clearly, keeps every existing backup restorable, and a raised quota lets the next one complete: control PASS, sharded NOT TESTED
- failure-recovery/f12-same-computer-name.spec.ts > F12 two computers with the same name → each set backed up on schedule by its own computer, nothing blocked or corrupted → both restore identical: control PASS, sharded NOT TESTED
- failure-recovery/f2-network-cut.spec.ts > F2 network cut mid-backup → failure, not success → line back → next backup restores identical, no ghost: control PASS, sharded NOT TESTED
- failure-recovery/n3-slow-line.spec.ts > N3 line throttled to 50 KB/s for a whole backup → succeeds, only slower (duration recorded) → new backup → restore identical (SHA-256): control PASS, sharded NOT TESTED
- journeys/j1-login-navigation.spec.ts > J1 sign-in, every menu page opens without errors, reload keeps the session, sign-out ends it on the server: control PASS, sharded NOT TESTED
- journeys/j5-kill-agent.spec.ts > J5 kill the agent during a backup → no ghost "running", the failure is shown, the next backup and its restore are right: control PASS, sharded NOT TESTED
- journeys/ui-01-every-screen.spec.ts > UI-01 sign-out in one tab ends the session for every tab (server check); a forged session shows the sign-in form: control PASS, sharded NOT TESTED
- journeys/ui-01-every-screen.spec.ts > UI-01 with real data: every customer tab, every set tab, run details and logs open without a console error or a failed request: control PASS, sharded NOT TESTED
- journeys/ui-03-truth-after-failures.spec.ts > UI-03 stopped by the technician: "Stopped" (never Succeeded), not running anywhere on the site: control PASS, sharded NOT TESTED
- journeys/ui-03-truth-after-failures.spec.ts > UI-03 the computer dies during a backup and never comes back: the "running" row goes away and the run is shown Failed: control PASS, sharded NOT TESTED
- journeys/ui-03-truth-after-failures.spec.ts > UI-03 the server crashes under a backup and comes back: the site shows the run Failed in red, nothing running: control PASS, sharded NOT TESTED
- seed.spec.ts > seed: control PASS, sharded NOT TESTED
