# INVERTED q6 — Q6 ST-05 a full quota stops the next backup clearly, keeps every existing backup restorable, and a raised quota lets the next one complete

| | |
|---|---|
| Severity | CRITICAL |
| Journey | e2e/zz-inv-q6.spec.ts |
| When | 2026-10-07T03:04:19.141Z |
| Commit |  |

## Steps done (the last one is where it failed)

1. 03:04:09 sign in as the administrator
2. 03:04:16 the customer adds 16 MB of new data: more than the room left in the quota
3. 03:04:19 ORACLE (agent): the backup is not a success, and the computer says why (the quota)

## Expected / actual

```
Error: BS_STOP_QUOTA_EXCEEDED new=0 upd=0 perm=0 del=0 bytes=0
err: /tmp/obqa-F6jc43/data/Binary/new-16mb.bin The user's quota was exceeded.


[2mexpect([22m[31mreceived[39m[2m).[22mtoMatch[2m([22m[32mexpected[39m[2m)[22m

Expected pattern: [32m/^BS_STOP_SUCCESS /m[39m
Received string:  [31m"BS_STOP_QUOTA_EXCEEDED new=0 upd=0 perm=0 del=0 bytes=0[39m
[31merr: /tmp/obqa-F6jc43/data/Binary/new-16mb.bin The user's quota was exceeded.[39m
[31m"[39m
```

## Console errors

(none)

## Failed API calls

(none)

## Attached

Screenshot, video and Playwright trace (`npx playwright show-trace <trace.zip>`), server log, agent log, system.xml: in `artifacts/test-results/e2e-zz-inv-q6-INVERTED-q6--26184--lets-the-next-one-complete`.
