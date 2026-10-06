# F15 lines of the agent index lost between backups → next backup still correct → newest point has no deleted file back, first point identical

| | |
|---|---|
| Severity | CRITICAL |
| Journey | failure-recovery/f15-agent-index-lines-lost.spec.ts |
| When | 2026-10-06T16:40:05.826Z |
| Commit |  |

## Steps done (the last one is where it failed)

1. 16:39:58 backup 1
2. 16:40:03 the files change: a text edited, the large file grows, 10 files deleted
3. 16:40:03 DAMAGE the computer's state: 20 lines of the local index lost (10 of them files deleted since), the last line torn
4. 16:40:03 backup 2 on the damaged state
5. 16:40:04 ORACLE: the newest point restores identical to the files as they are now

## Expected / actual

```
Error: [2mexpect([22m[31mreceived[39m[2m).[22mtoEqual[2m([22m[32mexpected[39m[2m) // deep equality[22m

[32m- Expected  -  1[39m
[31m+ Received  + 12[39m

[32m- Array [][39m
[31m+ Array [[39m
[31m+   "UNEXPECTED Many/file-000.csv",[39m
[31m+   "UNEXPECTED Many/file-001.csv",[39m
[31m+   "UNEXPECTED Many/file-002.csv",[39m
[31m+   "UNEXPECTED Many/file-003.csv",[39m
[31m+   "UNEXPECTED Many/file-004.csv",[39m
[31m+   "UNEXPECTED Many/file-005.csv",[39m
[31m+   "UNEXPECTED Many/file-006.csv",[39m
[31m+   "UNEXPECTED Many/file-007.csv",[39m
[31m+   "UNEXPECTED Many/file-008.csv",[39m
[31m+   "UNEXPECTED Many/file-009.csv",[39m
[31m+ ][39m
```

## Console errors

(none)

## Failed API calls

(none)

## Attached

Screenshot, video and Playwright trace (`npx playwright show-trace <trace.zip>`), server log, agent log, system.xml: in `artifacts/test-results/failure-recovery-f15-agent-380dc--back-first-point-identical`.
