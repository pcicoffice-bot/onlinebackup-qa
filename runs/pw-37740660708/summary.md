# Journeys (Playwright) - run 37740660708 (073356e44a66f501b06a02812be931450f7089fd)

**72 tests: 70 PASS, 1 FAIL, 1 NOT TESTED** (a test is PASS only when every job that had to run it reports PASS)

Build SHA-256: `42b4da89301732f585e72f1143beb156fdf4d1fad7021829f0e44d2396d7e751` (every shard checks it before running). Mode: strict - NOT green.

| job | state | tests | runner | minutes |
|---|---|---|---|---|
| s01-r01 | results | 36 | GitHub Actions 1000001710 | 31 |
| s02-r01 | results | 36 | GitHub Actions 1000001711 | 44 |

## Not PASS

| test | verdict | per job |
|---|---|---|
| e2e/p13-au04-guard-blocks-an-address.spec.ts > P13 AU-04 an address that guesses passwords is blocked for everything, the administrator's own address is not, one alert mail, unblocked on the site | NOT TESTED | 0 PASS / 0 FAIL / 1 NOT TESTED of 1 - s01-r01: [declared] outcome NotExecuted: NOT TESTED: this computer's address 10.1.0.183 is an office (private) address, which the Guard never blocks by design |
| failure-recovery/n6-agent-disk-full.spec.ts > N6b the set folder on the computer is full after the server committed → server and computer tell the truth → next run correct → restore identical (SHA-256) | FAIL | 0 PASS / 1 FAIL / 0 NOT TESTED of 1 - s02-r01: Error: the computer and the server must tell the same outcome of one run  expect(received).toEqual(expected) // deep equality  - Expected  - 1 + Received  + 1   |
