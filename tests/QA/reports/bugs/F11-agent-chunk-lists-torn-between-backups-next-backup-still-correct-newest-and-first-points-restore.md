# F11 agent chunk lists torn between backups → next backup still correct → newest and first points restore identical

| | |
|---|---|
| Severity | CRITICAL |
| Journey | failure-recovery/f11-agent-chunk-lists-torn.spec.ts |
| When | 2026-10-06T16:39:50.953Z |
| Commit |  |

## Steps done (the last one is where it failed)

1. 16:39:44 backup 1
2. 16:39:49 the files change: a text edited, the large file grows, 10 files deleted
3. 16:39:50 DAMAGE the computer's state: every chunk list (the large file's) torn in the middle of a line
4. 16:39:50 backup 2 on the damaged state
5. 16:39:50 backup 2 did not succeed; backup 3 (does it heal by itself?)

## Expected / actual

```
Error: the next backup after the damage must still be correct.
backup 2:
BS_STOP_BY_SYSTEM_ERROR new=0 upd=1 perm=0 del=0 bytes=554
err:  IndexOutOfRangeException: Index was outside the bounds of the array.

backup 3:
BS_STOP_BY_SYSTEM_ERROR new=0 upd=1 perm=0 del=0 bytes=554
err:  IndexOutOfRangeException: Index was outside the bounds of the array.


[2mexpect([22m[31mreceived[39m[2m).[22mtoMatch[2m([22m[32mexpected[39m[2m)[22m

Expected pattern: [32m/^BS_STOP_SUCCESS /m[39m
Received string:  [31m"BS_STOP_BY_SYSTEM_ERROR new=0 upd=1 perm=0 del=0 bytes=554[39m
[31merr:  IndexOutOfRangeException: Index was outside the bounds of the array.[39m
[31m"[39m
```

## Console errors

(none)

## Failed API calls

(none)

## Attached

Screenshot, video and Playwright trace (`npx playwright show-trace <trace.zip>`), server log, agent log, system.xml: in `artifacts/test-results/failure-recovery-f11-agent-c7caf-st-points-restore-identical`.
