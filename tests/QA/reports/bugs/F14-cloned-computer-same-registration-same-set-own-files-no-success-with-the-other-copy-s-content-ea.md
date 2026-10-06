# F14 cloned computer (same registration, same set, own files) → no success with the other copy's content → each newest point restores identical

| | |
|---|---|
| Severity | CRITICAL |
| Journey | failure-recovery/f14-cloned-computer.spec.ts |
| When | 2026-10-06T17:20:05.477Z |
| Commit |  |

## Steps done (the last one is where it failed)

1. 17:20:00 the original computer backs up
2. 17:20:02 the computer is CLONED: program folder and disk copied
3. 17:20:02 the clone's files diverge: 2 edited, 1 deleted, 1 new
4. 17:20:02 the clone backs up
5. 17:20:03 clone backup: BS_STOP_SUCCESS new=1 upd=2 perm=0 del=1 bytes=1502
6. 17:20:04 the original computer backs up again (its files did not change)
7. 17:20:04 original backup: BS_STOP_SUCCESS new=0 upd=0 perm=0 del=0 bytes=0
8. 17:20:04 ORACLE: the original's backup said success → its newest point must be the original's files

## Expected / actual

```
Error: the original computer's backup said success, but its newest point holds the clone's content

[2mexpect([22m[31mreceived[39m[2m).[22mtoEqual[2m([22m[32mexpected[39m[2m) // deep equality[22m

[32m- Expected  - 1[39m
[31m+ Received  + 6[39m

[32m- Array [][39m
[31m+ Array [[39m
[31m+   "DIFFERENT Documents/letter.txt (38 → 23 bytes)",[39m
[31m+   "DIFFERENT Duplicates/a.txt (12 → 26 bytes)",[39m
[31m+   "MISSING Folder with spaces/file with spaces.txt",[39m
[31m+   "UNEXPECTED Documents/clone-only.txt",[39m
[31m+ ][39m
```

## Console errors

(none)

## Failed API calls

(none)

## Attached

Screenshot, video and Playwright trace (`npx playwright show-trace <trace.zip>`), server log, agent log, system.xml: in `artifacts/test-results/failure-recovery-f14-clone-562be-st-point-restores-identical`.
