# F1 server killed mid-backup → the run fails cleanly → server back → no ghost, not locked, next backup restores identical

| | |
|---|---|
| Severity | CRITICAL |
| Journey | failure-recovery/f1-server-crash.spec.ts |
| When | 2026-10-06T09:40:49.262Z |
| Commit |  |

## Steps done (the last one is where it failed)

1. 09:40:38 a slow backup as its own process
2. 09:40:38 KILL the server
3. 09:40:48 the server starts again
4. 09:40:48 nothing is shown as running
5. 09:40:48 the next backup is not refused (the set is not locked) and succeeds

## Expected / actual

```
Error: BS_STOP_BY_SYSTEM_ERROR new=0 upd=0 perm=0 del=0 bytes=0
err:  Another backup of this set is still running (it last answered 0 minutes ago).


[2mexpect([22m[31mreceived[39m[2m).[22mtoMatch[2m([22m[32mexpected[39m[2m)[22m

Expected pattern: [32m/^BS_STOP_SUCCESS /m[39m
Received string:  [31m"BS_STOP_BY_SYSTEM_ERROR new=0 upd=0 perm=0 del=0 bytes=0[39m
[31merr:  Another backup of this set is still running (it last answered 0 minutes ago).[39m
[31m"[39m
```

## Console errors

(none)

## Failed API calls

(none)

## Attached

Screenshot, video and Playwright trace (`npx playwright show-trace <trace.zip>`), server log, agent log, system.xml: in `artifacts/test-results/failure-recovery-f1-server-c38fe-t-backup-restores-identical`.
