# Q1 ST-09 a set and a customer deleted on the site come back from the recycle bin intact; after 14 days the bin is really erased

| | |
|---|---|
| Severity | MAJOR |
| Journey | e2e/q1-recycle-bin.spec.ts |
| When | 2026-10-07T02:40:42.737Z |
| Commit |  |

## Steps done (the last one is where it failed)

1. 02:40:26 sign in as the administrator
2. 02:40:35 open the set, Maintenance, Delete the set, Yes
3. 02:40:36 ORACLE (disk): the store folder is gone, its bytes are in the customer's recycle bin
4. 02:40:36 ORACLE (agent): the computer no longer has the set, a restore of it is refused
5. 02:40:36 Storage on the server → Recycle bin: the set is listed; Restore
6. 02:40:36 ORACLE: the store is back byte for byte, the computer sees the set, the restore is identical (SHA-256)
7. 02:40:38 the restored set goes on: a change is backed up and restores identical
8. 02:40:40 open the customer, Delete customer, Yes
9. 02:40:40 ORACLE: the customer's folder is gone, the computer cannot sign in any more
10. 02:40:40 Storage → Recycle bin: the customer is listed; Restore
11. 02:40:40 ORACLE: the customer's backups are back byte for byte, the computer signs in, the restore is identical
12. 02:40:42 delete the set again; maintenance at +13 days keeps it, at +15 days erases it
13. 02:40:42 ORACLE (disk): the bin folder and its bytes are gone; the site's bin is empty

## Expected / actual

```
Error: files of the erased set left anywhere in the customer's folder

[2mexpect([22m[31mreceived[39m[2m).[22mtoEqual[2m([22m[32mexpected[39m[2m) // deep equality[22m

[32m- Expected  - 1[39m
[31m+ Received  + 3[39m

[32m- Array [][39m
[31m+ Array [[39m
[31m+   "stats/1791340832082.tsv",[39m
[31m+ ][39m
```

## Console errors

(none)

## Failed API calls

(none)

## Attached

Screenshot, video and Playwright trace (`npx playwright show-trace <trace.zip>`), server log, agent log, system.xml: in `artifacts/test-results/e2e-q1-recycle-bin-Q1-ST-0-f8714-ys-the-bin-is-really-erased`.
