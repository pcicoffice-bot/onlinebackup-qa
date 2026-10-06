# F5 source disappears → the site shows the failure truthfully → source back → backup restores identical

| | |
|---|---|
| Severity | CRITICAL |
| Journey | failure-recovery/f5-source-gone-ui.spec.ts |
| When | 2026-10-06T09:43:51.238Z |
| Commit |  |

## Steps done (the last one is where it failed)

1. 09:42:42 sign in as the administrator
2. 09:42:49 the disk is disconnected
3. 09:42:50 the tasks page: Failed
4. 09:43:51 the customer's set list: a red "Last run failed"

## Expected / actual

```
Error: [2mexpect([22m[31mlocator[39m[2m).[22mtoContainText[2m([22m[32mexpected[39m[2m)[22m failed

Locator: getByRole('row').filter({ hasText: 'Shared disk' }).first().locator('.pill.bad')
Expected substring: [32m"failed"[39m
Error: strict mode violation: getByRole('row').filter({ hasText: 'Shared disk' }).first().locator('.pill.bad') resolved to 2 elements:
    1) <span class="pill bad">Last run failed 06/10/2026 09:43</span> aka getByText('Last run failed 06/10/2026 09:')
    2) <span class="pill bad">Failed</span> aka getByText('Failed', { exact: true })

Call log:
[2m  - Expect "toContainText" getByRole('row').filter({ hasText: 'Shared disk' }).first().locator('.pill.bad') with timeout 20000ms[22m
[2m  - waiting for getByRole('row').filter({ hasText: 'Shared disk' }).first().locator('.pill.bad')[22m

```

## Console errors

(none)

## Failed API calls

(none)

## Attached

Screenshot, video and Playwright trace (`npx playwright show-trace <trace.zip>`), server log, agent log, system.xml: in `artifacts/test-results/failure-recovery-f5-source-90efa-→-backup-restores-identical`.
