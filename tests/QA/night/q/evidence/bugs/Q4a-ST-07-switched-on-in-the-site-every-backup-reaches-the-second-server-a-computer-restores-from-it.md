# Q4a ST-07 switched on in the site: every backup reaches the second server; a computer restores from it; second server down, behind, back

| | |
|---|---|
| Severity | CRITICAL |
| Journey | e2e/q4-replication.spec.ts |
| When | 2026-10-07T02:49:43.574Z |
| Commit |  |

## Steps done (the last one is where it failed)

1. 02:47:08 sign in as the administrator
2. 02:47:10 Storage on the server → A copy on a second server: on, address, token, Save and exit
3. 02:47:11 a customer, a computer, a set, a backup on the first server
4. 02:47:17 wait until the first server has nothing waiting to copy (it copies every minute)
5. 02:47:42 DISASTER: a computer registers on the second server with the same customer, restores (SHA-256)
6. 02:47:45 FAULT: the second server is down; the first one goes on backing up
7. 02:47:46 the first server keeps the copy waiting (its log says why); the site shows it waiting
8. 02:48:41 BEHIND: the second server is back; before the next copy, a restore from it gives the first version, whole
9. 02:48:43 BACK: the queue drains; the second server now restores the second version, and the first one still

## Expected / actual

```
Error: dotnet OnlineBackup.Agent.dll points --home /tmp/obqa-48EP53/agent-qa-rep-DR-PC1 --set 1791341234872 → exit 1
error: The device was revoked or is unknown.

```

## Console errors

(none)

## Failed API calls

(none)

## Attached

Screenshot, video and Playwright trace (`npx playwright show-trace <trace.zip>`), server log, agent log, system.xml: in `artifacts/test-results/e2e-q4-replication-Q4a-ST--e7c59-ond-server-down-behind-back`.
