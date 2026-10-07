# INVERTED q1 — Q1 ST-09 a set and a customer deleted on the site come back from the recycle bin intact; after 14 days the bin is really erased

| | |
|---|---|
| Severity | MAJOR |
| Journey | e2e/zz-inv-q1.spec.ts |
| When | 2026-10-07T03:02:55.868Z |
| Commit |  |

## Steps done (the last one is where it failed)

1. 03:02:40 sign in as the administrator
2. 03:02:48 open the set, Maintenance, Delete the set, Yes
3. 03:02:49 ORACLE (disk): the store folder is gone, its bytes are in the customer's recycle bin
4. 03:02:49 ORACLE (agent): the computer no longer has the set, a restore of it is refused
5. 03:02:49 Storage on the server → Recycle bin: the set is listed; Restore
6. 03:02:50 ORACLE: the store is back byte for byte, the computer sees the set, the restore is identical (SHA-256)
7. 03:02:51 the restored set goes on: a change is backed up and restores identical
8. 03:02:53 open the customer, Delete customer, Yes
9. 03:02:53 ORACLE: the customer's folder is gone, the computer cannot sign in any more
10. 03:02:53 Storage → Recycle bin: the customer is listed; Restore
11. 03:02:53 ORACLE: the customer's backups are back byte for byte, the computer signs in, the restore is identical
12. 03:02:55 delete the set again; maintenance at +13 days keeps it, at +15 days erases it
13. 03:02:55 ORACLE (disk): the bin folder and its bytes are gone; the site's bin is empty

## Expected / actual

```
Error: the bin after 15 days

[2mexpect([22m[31mreceived[39m[2m).[22mtoBe[2m([22m[32mexpected[39m[2m) // Object.is equality[22m

Expected: [32mtrue[39m
Received: [31mfalse[39m
```

## Console errors

(none)

## Failed API calls

(none)

## Attached

Screenshot, video and Playwright trace (`npx playwright show-trace <trace.zip>`), server log, agent log, system.xml: in `artifacts/test-results/e2e-zz-inv-q1-INVERTED-q1--6b3a2-ys-the-bin-is-really-erased`.
