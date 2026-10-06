# UI-02 concurrency: two technicians edit different tabs of the same set — neither change is lost silently

| | |
|---|---|
| Severity | MAJOR |
| Journey | journeys/ui-02-set-editor.spec.ts |
| When | 2026-10-06T18:58:11.016Z |
| Commit |  |

## Steps done (the last one is where it failed)

1. 18:57:46 sign in as the administrator
2. 18:57:50 both technicians open the set
3. 18:57:50 technician A: upload limit 444 → Save

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

- Failed to load resource: the server responded with a status of 409 (Conflict) @ http://localhost:34177/api/admin/users/qa-ui2e/sets/1791313069369

## Failed API calls

- 409 POST http://localhost:34177/api/admin/users/qa-ui2e/sets/1791313069369

## Attached

Screenshot, video and Playwright trace (`npx playwright show-trace <trace.zip>`), server log, agent log, system.xml: in `artifacts/test-results/journeys-ui-02-set-editor--b4439-her-change-is-lost-silently`.
