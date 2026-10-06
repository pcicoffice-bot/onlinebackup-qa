# J5 kill the agent during a backup → no ghost "running", the failure is shown, the next backup and its restore are right

| | |
|---|---|
| Severity | CRITICAL |
| Journey | journeys/j5-kill-agent.spec.ts |
| When | 2026-10-06T09:19:55.957Z |
| Commit |  |

## Steps done (the last one is where it failed)

1. 09:15:10 sign in as the administrator
2. 09:15:14 the technician limits the upload to 200 KB/s (so the backup takes a while)
3. 09:15:15 the computer runs its service; Back up now
4. 09:15:15 the site shows the backup as running
5. 09:16:30 KILL the backup program on the computer (power cut)
6. 09:16:30 the computer starts again: the service comes back
7. 09:16:30 the site no longer shows it as running (no ghost)
8. 09:16:54 the history shows the interrupted run as failed, with a reason

## Expected / actual

```
Error: no "Failed" run of "Server files" on the tasks page after 3 minutes
```

## Console errors

- Failed to load resource: the server responded with a status of 404 (Not Found)
- Failed to load resource: the server responded with a status of 404 (Not Found)
- Failed to load resource: the server responded with a status of 404 (Not Found)

## Failed API calls

- FAILED GET http://localhost:35431/i18n/fonts/inter-latin.woff2 net::ERR_ABORTED
- FAILED GET http://localhost:35431/i18n/fonts/heebo-hebrew.woff2 net::ERR_ABORTED

## Attached

Screenshot, video and Playwright trace (`npx playwright show-trace <trace.zip>`), server log, agent log, system.xml: in `artifacts/test-results/journeys-j5-kill-agent-J5--15f85-p-and-its-restore-are-right`.
