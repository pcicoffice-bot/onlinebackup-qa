# F13 client clock 3 h ahead of the server → the daily slot runs once, not again every 15 minutes → restore identical

| | |
|---|---|
| Severity | CRITICAL |
| Journey | failure-recovery/f13-client-clock-ahead.spec.ts |
| When | 2026-10-06T16:32:17.957Z |
| Commit |  |

## Steps done (the last one is where it failed)

1. 16:29:11 the service runs with the computer clock at +3 h: the slot is due
2. 16:29:17 20 minutes later (computer time): the service starts again — the slot was already backed up
3. 16:30:47 40 minutes later (computer time): again
4. 16:32:17 backups of the set on the server: 2 ["2026-10-06-16-30-48","2026-10-06-16-29-12"]

## Expected / actual

```
Error: one daily slot must give one scheduled backup, not one every 15 minutes (the computer's clock is ahead of the server's)

[2mexpect([22m[31mreceived[39m[2m).[22mtoBe[2m([22m[32mexpected[39m[2m) // Object.is equality[22m

Expected: [32m1[39m
Received: [31m2[39m
```

## Console errors

(none)

## Failed API calls

(none)

## Attached

Screenshot, video and Playwright trace (`npx playwright show-trace <trace.zip>`), server log, agent log, system.xml: in `artifacts/test-results/failure-recovery-f13-clien-cd273-minutes-→-restore-identical`.
