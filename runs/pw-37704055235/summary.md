# Journeys (Playwright) - run 37704055235 (ac19b4deb91eda64260ac8984c75872982dd14e2)

**72 tests: 65 PASS, 1 FAIL, 6 NOT TESTED** (a test is PASS only when every job that had to run it reports PASS)

Build SHA-256: `1efb13a65ae6ed7465a5a7af9121f33020db302d84aafc828999d3135d985ed9` (every shard checks it before running). Mode: strict - NOT green.

| job | state | tests | runner | minutes |
|---|---|---|---|---|
| s01-r01 | results | 36 | GitHub Actions 1000001613 | 21 |
| s02-r01 | results | 36 | GitHub Actions 1000001614 | 44 |

## Not PASS

| test | verdict | per job |
|---|---|---|
| e2e/p07-bk07-ag02-max-duration-and-stop.spec.ts > P07 BK-07/AG-02 a backup past its maximum duration ends Stopped; Stop on the site ends the service's run; the next run completes and restores identical | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s01-r01: [declared] outcome NotExecuted: NOT TESTED: libfaketime is not installed (the maximum duration is at least 1 hour) |
| e2e/p13-au04-guard-blocks-an-address.spec.ts > P13 AU-04 an address that guesses passwords is blocked for everything, the administrator's own address is not, one alert mail, unblocked on the site | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s01-r01: [declared] outcome NotExecuted: NOT TESTED: this computer's address 10.1.0.140 is an office (private) address, which the Guard never blocks by design |
| failure-recovery/f13-client-clock-ahead.spec.ts > F13 client clock 3 h ahead of the server → the daily slot runs once, not again every 15 minutes → restore identical | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s01-r01: [declared] outcome NotExecuted: NOT TESTED: libfaketime (faketime) is not installed — the agent has no clock offset of its own |
| e2e/p01-rs06-restore-test.spec.ts > P01 RS-06 the monthly restore test runs by itself on a real backup: Passed 3/3 on the site, a changed source is not counted, a damaged object a month later is FAILED in red, no temp folder left | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s02-r01: [declared] outcome NotExecuted: NOT TESTED: libfaketime (faketime) is not installed — the month cannot pass for the agent program |
| failure-recovery/n10-external-process-hang.spec.ts > N10a pre-command that never ends → stopped at the real 1-hour limit (agent clock 120×) with its child → backup completes → restore identical (SHA-256) | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s02-r01: [declared] outcome NotExecuted: NOT TESTED: libfaketime is not installed (the 1-hour limit cannot be shortened by a setting) |
| failure-recovery/n10-external-process-hang.spec.ts > N10c restic hangs silently during the backup → stopped at the real 30-min idle limit (agent clock 120×), nothing left running, no false success → real restic → backup → restore identical (SHA-256) | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s02-r01: [declared] outcome NotExecuted: NOT TESTED: libfaketime is not installed |
| failure-recovery/n6-agent-disk-full.spec.ts > N6b the set folder on the computer is full after the server committed → server and computer tell the truth → next run correct → restore identical (SHA-256) | FAIL | 0 PASS / 1 FAIL / 0 NOT TESTED of 1 - s02-r01: Error: the computer and the server must tell the same outcome of one run  expect(received).toEqual(expected) // deep equality  - Expected  - 1 + Received  + 1   |
