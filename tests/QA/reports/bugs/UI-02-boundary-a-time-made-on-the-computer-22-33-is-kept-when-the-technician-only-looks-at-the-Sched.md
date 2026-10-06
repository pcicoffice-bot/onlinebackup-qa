# UI-02 boundary: a time made on the computer (22:33) is kept when the technician only looks at the Schedule tab and saves

| | |
|---|---|
| Severity | MAJOR |
| Journey | journeys/ui-02-set-editor.spec.ts |
| When | 2026-10-06T18:56:43.777Z |
| Commit |  |

## Steps done (the last one is where it failed)

1. 18:56:19 sign in as the administrator
2. 18:56:23 open the Schedule tab — what does it show?
3. 18:56:23 the page shows 22:33; rename the set only, Save and exit

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

- Failed to load resource: the server responded with a status of 409 (Conflict) @ http://localhost:39069/api/admin/users/qa-ui2b/sets/1791312982956

## Failed API calls

- 409 POST http://localhost:39069/api/admin/users/qa-ui2b/sets/1791312982956

## Attached

Screenshot, video and Playwright trace (`npx playwright show-trace <trace.zip>`), server log, agent log, system.xml: in `artifacts/test-results/journeys-ui-02-set-editor--86d92--the-Schedule-tab-and-saves`.
