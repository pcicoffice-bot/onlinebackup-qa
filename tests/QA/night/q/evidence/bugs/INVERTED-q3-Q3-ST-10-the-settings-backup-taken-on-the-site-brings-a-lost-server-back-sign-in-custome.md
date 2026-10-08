# INVERTED q3 — Q3 ST-10 the settings backup taken on the site brings a lost server back: sign-in, customer, set, registration, key, backup and restore

| | |
|---|---|
| Severity | CRITICAL |
| Journey | e2e/zz-inv-q3.spec.ts |
| When | 2026-10-07T03:04:03.157Z |
| Commit |  |

## Steps done (the last one is where it failed)

1. 03:03:12 sign in as the administrator
2. 03:03:20 Storage on the server → Backup of the server settings → Back up now, then Download
3. 03:03:20 ORACLE: the download is the server's file, byte for byte; it holds the settings and the customer's db
4. 03:03:20 FAULT: the server stops; its whole System Home and the customer's db folder are lost
5. 03:03:20 PROOF of the fault: the folders are gone, and the computer cannot reach its server
6. 03:03:31 RECOVERY as documented: unzip "system" over the System Home and "users/qa-cfg/db" over the customer's db folder
7. 03:03:31 the administrator signs in again in a new browser (password and authenticator from the restored settings)
8. 03:03:32 the customer and its set are on the site
9. 03:03:32 ORACLE: the saved encryption key can still be read back
10. 03:04:00 ORACLE: the computer, with its old registration, backs up a change and restores identical

## Expected / actual

```
Error: [2mexpect([22m[31mreceived[39m[2m).[22mtoEqual[2m([22m[32mexpected[39m[2m) // deep equality[22m

[32m- Expected  - 1[39m
[31m+ Received  + 3[39m

[32m- Array [][39m
[31m+ Array [[39m
[31m+   "UNEXPECTED Documents/after-the-settings-came-back.txt",[39m
[31m+ ][39m
```

## Console errors

(none)

## Failed API calls

(none)

## Attached

Screenshot, video and Playwright trace (`npx playwright show-trace <trace.zip>`), server log, agent log, system.xml: in `artifacts/test-results/e2e-zz-inv-q3-INVERTED-q3--e3189-tion-key-backup-and-restore`.
