# N9a the server's set folder read-only for a whole run → no false success, point 1 restorable during the fault → writable again → backup → restore identical (SHA-256)

| | |
|---|---|
| Severity | CRITICAL |
| Journey | failure-recovery/n9-repository-unavailable.spec.ts |
| When | 2026-10-06T19:53:33.438Z |
| Commit |  |

## Steps done (the last one is where it failed)

1. 19:53:29 PROOF: {"findmnt":"ro,relatime,discard,no_prefetch_block_bitmaps,resv_strict,resuid=65534,resgid=65534","write":"ERR 1 touch: cannot touch '/tmp/obqa-HDmeJw/users/qa-n9a/files/1791316405677/probe': Read-only file system"}
2. 19:53:29 run 2: exit 1
BS_STOP_BY_SYSTEM_ERROR new=0 upd=0 perm=0 del=0 bytes=0
err:  Server error. The details are in the system log.
3. 19:53:29 server runs: [["2026-10-06-19-53-26","BS_STOP_SUCCESS"]]
4. 19:53:29 points during the fault: exit 1 error: Server error. The details are in the system log.
5. 19:53:30 restore of point 1 during the fault: exit 1 error: Server error. The details are in the system log.
server system log: 2026-10-06 19:53:29	127.0.0.1	error: POST /api/sets/1791316405677/begin: SqliteException SQLite Error 14: 'unable to open database file'.
2026-10-06 19:53:29	127.0.0.1	error: GET /api/sets/1791316405677/points: SqliteException SQLite Error 14: 'unable to open database file'.
2026-10-06 19:53:30	127.0.0.1	error: GET /api/sets/1791316405677/files: SqliteException SQLite Error 14: 'unable to open database file'.
6. 19:53:30 RECOVERY: writable again → backup 3
7. 19:53:33 server runs after recovery: [["2026-10-06-19-53-30","BS_STOP_SUCCESS"],["2026-10-06-19-53-26","BS_STOP_SUCCESS"]]

## Expected / actual

```
Error: the points of a read-only repository can be listed:
error: Server error. The details are in the system log.


[2mexpect([22m[31mreceived[39m[2m).[22mtoBe[2m([22m[32mexpected[39m[2m) // Object.is equality[22m

Expected: [32m0[39m
Received: [31m1[39m

Error: point 1 of a read-only repository can be restored:
error: Server error. The details are in the system log.


[2mexpect([22m[31mreceived[39m[2m).[22mtoBe[2m([22m[32mexpected[39m[2m) // Object.is equality[22m

Expected: [32m0[39m
Received: [31m1[39m

Error: [2mexpect([22m[31mreceived[39m[2m).[22mtoEqual[2m([22m[32mexpected[39m[2m) // deep equality[22m

[32m- Expected  -   1[39m
[31m+ Received  + 165[39m

[32m- Array [][39m
[31m+ Array [[39m
[31m+   "MISSING Binary/large.bin",[39m
[31m+   "MISSING Binary/random.bin",[39m
[31m+   "MISSING Deep/level/level/level/level/level/level/level/level/level/level/level/level/deep.txt",[39m
[31m+   "MISSING Documents/letter.txt",[39m
[31m+   "MISSING Duplicates/a.txt",[39m
[31m+   "MISSING Duplicates/b.txt",[39m
[31m+   "MISSING Empty/zero.bin",[39m
[31m+   "MISSING Folder with spaces/file with spaces.txt",[39m
[31m+   "MISSING Long/a-very-long-file-name-a-very-long-file-name-a-very-long-file-name-a-very-long-file-name-a-very-long-file-name-a-very-long-file-name-a-very-long-file-name-a-very-long-file-name-.txt",[39m
[31m+   "MISSING Many/file-000.csv",[39m
[31m+   "MISSING Many/file-001.csv",[39m
[31m+   "MISSING Many/file-002.csv",[39m
[31m+   "MISSING Many/file-003.csv",[39m
[31m+   "MISSING Many/file-004.csv",[39m
[31m+   "MISSING Many/file-005.csv",[39m
[31m+   "MISSING Many/file-006.csv",[39m
[31m+   "MISSING Many/file-007.csv",[39m
[31m+   "MISSING Many/file-008.csv",[39m
[31m+   "MISSING Many/file-009.csv",[39m
[31m+   "MISSING Many/file-010.csv",[39m
[31m+   "MISSING Many/file-011.csv",[39m
[31m+   "MISSING Many/file-012.csv",[39m
[31m+   "MISSING Many/file-013.csv",[39m
[31m+   "MISSING Many/file-014.csv",[39m
[31m+   "MISSING Many/file-015.csv",[39m
[31m+   "MISSING Many/file-016.csv",[39m
[31m+   "MISSING Many/file-017.csv",[39m
[31m+   "MISSING Many/file-018.csv",[39m
[31m+   "MISSING Many/file-019.csv",[39m
[31m+   "MISSING Many/file-020.csv",[39m
[31m+   "MISSING Many/file-021.csv",[39m
[31m+   "MISSING Many/file-022.csv",[39m
[31m+   "MISSING Many/file-023.csv",[39m
[31m+   "MISSING Many/file-024.csv",[39m
[31m+   "MISSING Many/file-025.csv",[39m
[31m+   "MISSING Many/file-026.csv",[39m
[31m+   "MISSING Many/file-027.csv",[39m
[31m+   "MISSING Many/file-028.csv",[39m
[31m+   "MISSING Many/file-029.csv",[39m
[31m+   "MISSING Many/file-030.csv",[39m
[31m+   "MISSING Many/file-031.csv",[39m
[31m+   "MISSING Many/file-032.csv",[39m
[31m+   "MISSING Many/file-033.csv",[39m
[31m+   "MISSING Many/file-034.csv",[39m
[31m+   "MISSING Many/file-035.csv",[39m
[31m+   "MISSING Many/file-036.csv",[39m
[31m+   "MISSING Many/file-037.csv",[39m
[31m+   "MISSING Many/file-038.csv",[39m
[31m+   "MISSING Many/file-039.csv",[39m
[31m+   "MISSING Many/file-040.csv",[39m
[31m+   "MISSING Many/file-041.csv",[39m
[31m+   "MISSING Many/file-042.csv",[39m
[31m+   "MISSING Many/file-043.csv",[39m
[31m+   "MISSING Many/file-044.csv",[39m
[31m+   "MISSING Many/file-045.csv",[39m
[31m+   "MISSING Many/file-046.csv",[39m
[31m+   "MISSING Many/file-047.csv",[39m
[31m+   "MISSING Many/file-048.csv",[39m
[31m+   "MISSING Many/file-049.csv",[39m
[31m+   "MISSING Many/file-050.csv",[39m
[31m+   "MISSING Many/file-051.csv",[39m
[31m+   "MISSING Many/file-052.csv",[39m
[31m+   "MISSING Many/file-053.csv",[39m
[31m+   "MISSING Many/file-054.csv",[39m
[31m+   "MISSING Many/file-055.csv",[39m
[31m+   "MISSING Many/file-056.csv",[39m
[31m+   "MISSING Many/file-057.csv",[39m
[31m+   "MISSING Many/file-058.csv",[39m
[31m+   "MISSING Many/file-059.csv",[39m
[31m+   "MISSING Many/file-060.csv",[39m
[31m+   "MISSING Many/file-061.csv",[39m
[31m+   "MISSING Many/file-062.csv",[39m
[31m+   "MISSING Many/file-063.csv",[39m
[31m+   "MISSING Many/file-064.csv",[39m
[31m+   "MISSING Many/file-065.csv",[39m
[31m+   "MISSING Many/file-066.csv",[39m
[31m+   "MISSING Many/file-067.csv",[39m
[31m+   "MISSING Many/file-068.csv",[39m
[31m+   "MISSING Many/file-069.csv",[39m
[31m+   "MISSING Many/file-070.csv",[39m
[31m+   "MISSING Many/file-071.csv",[39m
[31m+   "MISSING Many/file-072.csv",[39m
[31m+   "MISSING Many/file-073.csv",[39m
[31m+   "MISSING Many/file-074.csv",[39m
[31m+   "MISSING Many/file-075.csv",[39m
[31m+   "MISSING Many/file-076.csv",[39m
[31m+   "MISSING Many/file-077.csv",[39m
[31m+   "MISSING Many/file-078.csv",[39m
[31m+   "MISSING Many/file-079.csv",[39m
[31m+   "MISSING Many/file-080.csv",[39m
[31m+   "MISSING Many/file-081.csv",[39m
[31m+   "MISSING Many/file-082.csv",[39m
[31m+   "MISSING Many/file-083.csv",[39m
[31m+   "MISSING Many/file-084.csv",[39m
[31m+   "MISSING Many/file-085.csv",[39m
[31m+   "MISSING Many/file-086.csv",[39m
[31m+   "MISSING Many/file-087.csv",[39m
[31m+   "MISSING Many/file-088.csv",[39m
[31m+   "MISSING Many/file-089.csv",[39m
[31m+   "MISSING Many/file-090.csv",[39m
[31m+   "MISSING Many/file-091.csv",[39m
[31m+   "MISSING Many/file-092.csv",[39m
[31m+   "MISSING Many/file-093.csv",[39m
[31m+   "MISSING Many/file-094.csv",[39m
[31m+   "MISSING Many/file-095.csv",[39m
[31m+   "MISSING Many/file-096.csv",[39m
[31m+   "MISSING Many/file-097.csv",[39m
[31m+   "MISSING Many/file-098.csv",[39m
[31m+   "MISSING Many/file-099.csv",[39m
[31m+   "MISSING Many/file-100.csv",[39m
[31m+   "MISSING Many/file-101.csv",[39m
[31m+   "MISSING Many/file-102.csv",[39m
[31m+   "MISSING Many/file-103.csv",[39m
[31m+   "MISSING Many/file-104.csv",[39m
[31m+   "MISSING Many/file-105.csv",[39m
[31m+   "MISSING Many/file-106.csv",[39
```

## Console errors

(none)

## Failed API calls

(none)

## Attached

Screenshot, video and Playwright trace (`npx playwright show-trace <trace.zip>`), server log, agent log, system.xml: in `../../../agent-n-pw/n9/failure-recovery-n9-reposi-2e16b--restore-identical-SHA-256-`.
