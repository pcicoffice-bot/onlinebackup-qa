# F3 two backups of one set at once → one succeeds, the other is refused cleanly, the backup restores identical

| | |
|---|---|
| Severity | CRITICAL |
| Journey | failure-recovery/f3-two-at-once.spec.ts |
| When | 2026-10-06T09:40:13.322Z |
| Commit |  |

## Steps done (the last one is where it failed)


## Expected / actual

```
Error: dotnet OnlineBackup.Server.dll adduser --system-home /tmp/obqa-pImn5n/sys --login qa-f3 --password Customer-Pass-1 --quota-gb 5 --email it@example.invalid → exit 143


Error: write EPIPE
```

## Console errors

(none)

## Failed API calls

(none)

## Attached

Screenshot, video and Playwright trace (`npx playwright show-trace <trace.zip>`), server log, agent log, system.xml: in `artifacts/test-results/failure-recovery-f3-two-at-ffcba-e-backup-restores-identical`.
