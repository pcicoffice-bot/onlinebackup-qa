# INVERTED q4 — Q4a ST-07 switched on in the site: every backup reaches the second server; a computer restores from it; second server down, behind, back

| | |
|---|---|
| Severity | CRITICAL |
| Journey | e2e/zz-inv-q4.spec.ts |
| When | 2026-10-07T03:13:04.663Z |
| Commit |  |

## Steps done (the last one is where it failed)

1. 03:12:28 sign in as the administrator
2. 03:12:30 Storage on the server → A copy on a second server: on, address, token, Save and exit
3. 03:12:31 a customer, a computer, a set, a backup on the first server
4. 03:12:36 wait until the first server has nothing waiting to copy (it copies every minute)
5. 03:13:01 DISASTER: a computer registers on the second server with the same customer, restores (SHA-256)

## Expected / actual

```
Error: [2mexpect([22m[31mreceived[39m[2m).[22mnot[2m.[22mtoEqual[2m([22m[32mexpected[39m[2m) // deep equality[22m

Expected: not [32m[][39m

```

## Console errors

(none)

## Failed API calls

(none)

## Attached

Screenshot, video and Playwright trace (`npx playwright show-trace <trace.zip>`), server log, agent log, system.xml: in `artifacts/test-results/e2e-zz-inv-q4-INVERTED-q4--94820-ond-server-down-behind-back`.
