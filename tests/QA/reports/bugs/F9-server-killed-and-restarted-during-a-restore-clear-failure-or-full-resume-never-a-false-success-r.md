# F9 server killed and restarted during a restore → clear failure or full resume, never a false success → restore again identical

| | |
|---|---|
| Severity | CRITICAL |
| Journey | failure-recovery/f9-server-restart-during-restore.spec.ts |
| When | 2026-10-06T16:38:55.744Z |
| Commit |  |

## Steps done (the last one is where it failed)

1. 16:38:20 backup
2. 16:38:30 restore as its own process
3. 16:38:31 KILL the server with 4 files written; 20 s later it starts again
4. 16:38:52 restore ended: exit 1 · restored=4 failed=163 skipped=0
5. 16:38:52 the restore says failed: what it wrote must be whole
6. 16:38:52 RECOVERY: restore again into the same folder (no overwrite) → identical
7. 16:38:55 the interrupted restore is in the server's history as failed (the technician must see it)
8. 16:38:55 restore records on the server: ["RESTORE_STOP_WITH_WARNING"]

## Expected / actual

```
Error: the restore that failed (restored=4 failed=163 skipped=0) has no failed record on the server; records: ["RESTORE_STOP_WITH_WARNING"]

[2mexpect([22m[31mreceived[39m[2m).[22mtoBeGreaterThan[2m([22m[32mexpected[39m[2m)[22m

Expected: > [32m0[39m
Received:   [31m0[39m
```

## Console errors

(none)

## Failed API calls

(none)

## Attached

Screenshot, video and Playwright trace (`npx playwright show-trace <trace.zip>`), server log, agent log, system.xml: in `artifacts/test-results/failure-recovery-f9-server-b3f53-s-→-restore-again-identical`.
