# UI-02 failure: a save the server refuses shows the reason, keeps the editor open, and stores nothing

| | |
|---|---|
| Severity | MAJOR |
| Journey | journeys/ui-02-set-editor.spec.ts |
| When | 2026-10-06T18:57:43.240Z |
| Commit |  |

## Steps done (the last one is where it failed)

1. 18:57:19 sign in as the administrator
2. 18:57:22 empty name + a bandwidth change → Save
3. 18:57:22 server + local copy without a folder → Save
4. 18:57:23 the technician fixes the folder → Saved, and now both changes are stored

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

- Failed to load resource: the server responded with a status of 400 (Bad Request) @ http://localhost:39115/api/admin/users/qa-ui2d/sets/1791313042186
- Failed to load resource: the server responded with a status of 400 (Bad Request) @ http://localhost:39115/api/admin/users/qa-ui2d/sets/1791313042186
- Failed to load resource: the server responded with a status of 409 (Conflict) @ http://localhost:39115/api/admin/users/qa-ui2d/sets/1791313042186

## Failed API calls

- 400 POST http://localhost:39115/api/admin/users/qa-ui2d/sets/1791313042186
- 400 POST http://localhost:39115/api/admin/users/qa-ui2d/sets/1791313042186
- 409 POST http://localhost:39115/api/admin/users/qa-ui2d/sets/1791313042186

## Attached

Screenshot, video and Playwright trace (`npx playwright show-trace <trace.zip>`), server log, agent log, system.xml: in `artifacts/test-results/journeys-ui-02-set-editor--ffc17-tor-open-and-stores-nothing`.
