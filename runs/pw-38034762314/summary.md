# Journeys (Playwright) - run 38034762314 (0f9501b13569f28490f0bad7e3eb09de30ee8df8)

**72 tests: 69 PASS, 2 FAIL, 1 NOT TESTED** (a test is PASS only when every job that had to run it reports PASS)

Build SHA-256: `4c363413b7f5caa3f36795bce1f2dccfc6c2103cfa4411c6abf5667fc631c6f3` (every shard checks it before running). Mode: strict - NOT green.

| job | state | tests | runner | minutes |
|---|---|---|---|---|
| s01-r01 | results | 36 | GitHub Actions 1000002307 | 31 |
| s02-r01 | results | 36 | GitHub Actions 1000002308 | 44 |

## Not PASS

| test | verdict | per job |
|---|---|---|
| e2e/p13-au04-guard-blocks-an-address.spec.ts > P13 AU-04 an address that guesses passwords is blocked for everything, the administrator's own address is not, one alert mail, unblocked on the site | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s01-r01: [declared] outcome NotExecuted: NOT TESTED: this computer's address 10.1.0.96 is an office (private) address, which the Guard never blocks by design |
| e2e/p01-rs06-restore-test.spec.ts > P01 RS-06 the monthly restore test runs by itself on a real backup: Passed 3/3 on the site, a changed source is not counted, a damaged object a month later is FAILED in red, no temp folder left | FAIL | 0 PASS / 1 FAIL / 0 NOT TESTED of 1 - s02-r01: Error: BS_STOP_SUCCESS new=0 upd=4 perm=0 del=0 bytes=309224 / agent read: changes-later.txt 1794301274670; letter.txt 1794301271807; photo.bin 1794301271807; t |
| failure-recovery/n6-agent-disk-full.spec.ts > N6b the set folder on the computer is full after the server committed → server and computer tell the truth → next run correct → restore identical (SHA-256) | FAIL | 0 PASS / 1 FAIL / 0 NOT TESTED of 1 - s02-r01: Error: the computer and the server must tell the same outcome of one run  expect(received).toEqual(expected) // deep equality  - Expected  - 1 + Received  + 1   |
