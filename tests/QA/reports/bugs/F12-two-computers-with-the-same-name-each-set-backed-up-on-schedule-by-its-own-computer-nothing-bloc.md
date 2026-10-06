# F12 two computers with the same name → each set backed up on schedule by its own computer, nothing blocked or corrupted → both restore identical

| | |
|---|---|
| Severity | CRITICAL |
| Journey | failure-recovery/f12-same-computer-name.spec.ts |
| When | 2026-10-06T18:45:09.710Z |
| Commit |  |

## Steps done (the last one is where it failed)

1. 18:44:58 two computers register with the same name
2. 18:45:04 both computers run their backup service
3. 18:45:09 runs on the server: [{"set":"Reception B","kind":"Backup","result":"BS_STOP_BY_SYSTEM_ERROR"},{"set":"Reception A","kind":"RestoreTest","result":"OK"},{"set":"Reception B","kind":"RestoreTest","result":"OK"},{"set":"Reception A","kind":"Backup","result":"BS_STOP_SUCCESS"},{"set":"Reception B","kind":"Backup","result":"BS_STOP_SUCCESS"}]

## Expected / actual

```
Error: no failed backup run

[2mexpect([22m[31mreceived[39m[2m).[22mtoEqual[2m([22m[32mexpected[39m[2m) // deep equality[22m

[32m- Expected  -  1[39m
[31m+ Received  + 14[39m

[32m- Array [][39m
[31m+ Array [[39m
[31m+   Object {[39m
[31m+     "computer": "RECEPTION",[39m
[31m+     "job": "2026-10-06-18-45-07",[39m
[31m+     "kind": "Backup",[39m
[31m+     "log": "2026-10-06-18-45-07.log",[39m
[31m+     "login": "qa-f12",[39m
[31m+     "result": "BS_STOP_BY_SYSTEM_ERROR",[39m
[31m+     "set": "1791312304358",[39m
[31m+     "setName": "Reception B",[39m
[31m+     "status": "bad",[39m
[31m+     "time": "1791312307502",[39m
[31m+   },[39m
[31m+ ][39m
```

## Console errors

(none)

## Failed API calls

(none)

## Attached

Screenshot, video and Playwright trace (`npx playwright show-trace <trace.zip>`), server log, agent log, system.xml: in `artifacts/test-results/failure-recovery-f12-same--3ca9d-ed-→-both-restore-identical`.
