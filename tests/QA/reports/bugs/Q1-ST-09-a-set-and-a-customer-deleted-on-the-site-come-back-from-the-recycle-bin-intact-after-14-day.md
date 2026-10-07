# Q1 ST-09 a set and a customer deleted on the site come back from the recycle bin intact; after 14 days the bin is really erased

| | |
|---|---|
| Severity | MAJOR |
| Journey | e2e/q1-recycle-bin.spec.ts |
| When | 2026-10-07T06:26:08.546Z |
| Commit |  |

## Steps done (the last one is where it failed)

1. 06:26:00 sign in as the administrator
2. 06:26:08 open the set, Maintenance, Delete the set, Yes

## Expected / actual

```
Error: write EPIPE

Error: locator.click: Target page, context or browser has been closed
```

## Console errors

(none)

## Failed API calls

(none)

## Attached

Screenshot, video and Playwright trace (`npx playwright show-trace <trace.zip>`), server log, agent log, system.xml: in `artifacts/test-results/e2e-q1-recycle-bin-Q1-ST-0-f8714-ys-the-bin-is-really-erased`.
