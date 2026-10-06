# UI-02 boundary: 22:58 made on the computer is kept (the page has no :60 to round to)

| | |
|---|---|
| Severity | MAJOR |
| Journey | journeys/ui-02-set-editor.spec.ts |
| When | 2026-10-06T18:57:11.184Z |
| Commit |  |

## Steps done (the last one is where it failed)

1. 18:56:46 sign in as the administrator
2. 18:56:51 the page shows 22:58; Save and exit without touching the time

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

- Failed to load resource: the server responded with a status of 409 (Conflict) @ http://localhost:46435/api/admin/users/qa-ui2f/sets/1791313010321

## Failed API calls

- 409 POST http://localhost:46435/api/admin/users/qa-ui2f/sets/1791313010321

## Attached

Screenshot, video and Playwright trace (`npx playwright show-trace <trace.zip>`), server log, agent log, system.xml: in `artifacts/test-results/journeys-ui-02-set-editor--eea20-page-has-no-60-to-round-to-`.
