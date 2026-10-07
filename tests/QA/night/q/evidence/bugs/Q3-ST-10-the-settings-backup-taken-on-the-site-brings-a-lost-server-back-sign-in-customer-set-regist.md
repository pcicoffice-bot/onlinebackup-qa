# Q3 ST-10 the settings backup taken on the site brings a lost server back: sign-in, customer, set, registration, key, backup and restore

| | |
|---|---|
| Severity | CRITICAL |
| Journey | e2e/q3-settings-backup.spec.ts |
| When | 2026-10-07T02:45:57.552Z |
| Commit |  |

## Steps done (the last one is where it failed)

1. 02:42:44 sign in as the administrator
2. 02:42:53 Storage on the server → Backup of the server settings → Back up now, then Download
3. 02:42:53 ORACLE: the download is the server's file, byte for byte; it holds the settings and the customer's db
4. 02:42:53 FAULT: the server stops; its whole System Home and the customer's db folder are lost
5. 02:42:53 PROOF of the fault: the folders are gone, and the computer cannot reach its server
6. 02:43:03 RECOVERY as documented: unzip "system" over the System Home and "users/qa-cfg/db" over the customer's db folder
7. 02:43:04 the administrator signs in again (password and authenticator from the restored settings)

## Expected / actual

```
Error: locator.fill: Target page, context or browser has been closed
Call log:
[2m  - waiting for locator('form.login input[autocomplete=username]')[22m

```

## Console errors

(none)

## Failed API calls

(none)

## Attached

Screenshot, video and Playwright trace (`npx playwright show-trace <trace.zip>`), server log, agent log, system.xml: in `artifacts/test-results/e2e-q3-settings-backup-Q3--161c5-tion-key-backup-and-restore`.
