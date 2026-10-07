# Q4b ST-07 a customer that existed before the copy was switched on must not stop the copy of every other customer

| | |
|---|---|
| Severity | MAJOR |
| Journey | e2e/q4-replication.spec.ts |
| When | 2026-10-07T03:11:59.728Z |
| Commit |  |

## Steps done (the last one is where it failed)

1. 03:07:49 a customer who already backs up (the server runs before the second server is set up)
2. 03:07:52 the copy to the second server is switched on (as in Q4a), then a new customer starts
3. 03:07:59 ORACLE: the new customer's backup reaches the second server and restores from it (SHA-256)

## Expected / actual

```
Error: Error: timeout (240 s) waiting for: the new customer's set on the second server
waiting to copy on the first server: 8
its log:
2026-10-07 03:08:18	-	replication waiting (commit qa-old): The user does not exist.
2026-10-07 03:09:18	-	replication waiting (commit qa-old): The user does not exist.
2026-10-07 03:10:18	-	replication waiting (commit qa-old): The user does not exist.
2026-10-07 03:11:18	-	replication waiting (commit qa-old): The user does not exist.
```

## Console errors

(none)

## Failed API calls

(none)

## Attached

Screenshot, video and Playwright trace (`npx playwright show-trace <trace.zip>`), server log, agent log, system.xml: in `artifacts/test-results/e2e-q4-replication-Q4b-ST--f8552-opy-of-every-other-customer`.
