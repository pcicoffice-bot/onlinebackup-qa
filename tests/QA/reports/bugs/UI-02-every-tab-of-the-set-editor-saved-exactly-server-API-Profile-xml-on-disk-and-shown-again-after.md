# UI-02 every tab of the set editor: saved exactly (server API + Profile.xml on disk) and shown again after a reload

| | |
|---|---|
| Severity | MAJOR |
| Journey | journeys/ui-02-set-editor.spec.ts |
| When | 2026-10-06T18:56:16.848Z |
| Commit |  |

## Steps done (the last one is where it failed)

1. 18:55:51 sign in as the administrator
2. 18:55:54 General: name, VSS off, permissions off
3. 18:55:54 What to back up: skip system and temporary files
4. 18:55:54 Schedule: Mon/Wed/Fri 03:35 + a second time every day 18:10; stop after 6 hours; missed backups off, 17 min, 9 h
5. 18:55:55 Backup method: differential
6. 18:55:55 Destination: server + local copy, folder, 21 days
7. 18:55:55 Versions kept: 12 backups; GFS 7 daily, 4 weekly, 0 monthly, 2 quarterly, 1 yearly
8. 18:55:55 Filters: skip names ending .bak (files); back up only names containing "report & co" (folders)
9. 18:55:56 Encryption and compression: fast
10. 18:55:56 Resources: 640 KB/s, not low priority, wait above 70% CPU
11. 18:55:56 Commands: before (stop on failure), after
12. 18:55:56 Reports and Maintenance open (no fields)
13. 18:55:56 Save and exit

## Expected / actual

```
Error: [2mexpect([22m[31mlocator[39m[2m).[22mtoBeVisible[2m([22m[2m)[22m failed

Locator: locator('#toast').filter({ hasText: 'Saved' })
Expected: visible
Timeout: 20000ms
Error: element(s) not found

Call log:
[2m  - Expect "toBeVisible" locator('#toast').filter({ hasText: 'Saved' }) with timeout 20000ms[22m
[2m  - waiting for locator('#toast').filter({ hasText: 'Saved' })[22m

```

## Console errors

- Failed to load resource: the server responded with a status of 409 (Conflict) @ http://localhost:38707/api/admin/users/qa-ui2/sets/1791312954112

## Failed API calls

- 409 POST http://localhost:38707/api/admin/users/qa-ui2/sets/1791312954112

## Attached

Screenshot, video and Playwright trace (`npx playwright show-trace <trace.zip>`), server log, agent log, system.xml: in `artifacts/test-results/journeys-ui-02-set-editor--67cd8--shown-again-after-a-reload`.
