# Run result — one truth table (owner plan §3, decision B2)

The agent ends a backup run with one of these codes (src/Agent/BackupRun.cs ~249-299, ResticRunner.cs ~290-307; the server may
rewrite: Api.cs 585 an aborted run is never a success, 710 a run with missing objects becomes WITH_ERROR):

| Code | Meaning today |
|---|---|
| BS_STOP_SUCCESS | every selected file read and sent |
| BS_STOP_SUCCESS_WITH_WARNING | sent, with warnings only (nothing missing) |
| BS_STOP_SUCCESS_WITH_ERROR | the run completed, **some files were NOT backed up** (errors) |
| BS_STOP_BY_USER | stopped by the user / admin (or by the maximum duration with no error) |
| BS_STOP_BY_SYSTEM_ERROR | failed (or aborted / lease expired / interrupted) |
| BS_STOP_QUOTA_EXCEEDED | refused by the quota |
| BS_STOP_BY_PRE_COMMAND | the pre-command failed |

## As-is: how each place interprets the same result (read from the code, 2026-10-08)

| Consumer | SUCCESS | WITH_WARNING | **WITH_ERROR** | BY_USER | SYSTEM_ERROR / QUOTA / PRE |
|---|---|---|---|---|---|
| "Last backup" date LAST_BACKUP_COMPLETE (Api.Completed, Api.cs 821/862) | updates | updates | **updates (counted complete)** | no | no |
| Run history colour (RunLog.Status) | ok | warn | **bad (red)** | stopped | bad |
| Admin site set row (app.js 56-58) | ok | warn | **red pill "some data not backed up"** | stopped | red "last run failed" |
| Mail notice (Notify.cs 116-124) | ok | warning | **"completed with errors — some data was not backed up"** | — | failure |
| Service calls (Tickets.cs 376-388; switched off with the pilot switch) | ok, closes calls | warn | **counted as a failure, on purpose ("no silent failures")** | nothing (duration stop → warn) | failure |
| Native Windows client window (ClientForm.cs 471-490) | "Completed successfully" | "Completed with warnings" | **"Completed with warnings" (amber) — the errors are hidden as warnings** | ? | Bad |
| Client HTML page (client.html 128) | ok | warn | "Completed with errors" | | |
| Client job state (ClientUi.cs 314) | ok | ok | **ok** | failed | failed |
| Agent CLI exit code (Program.cs 88) | 0 | 0 | **0** | 1 | 1 |
| "No change" check (BackupChecks.cs 76) | success | success | **success** | — | — |
| Restore-test due after a good backup (AgentApp.cs 360: last-attempt contains "BS_STOP_SUCCESS") | yes | yes | **yes** | no | no |
| Scheduler: the slot counts as done (AgentApp.cs 587) | yes | yes | **yes** | no | no |
| Missed-backup alert (uses LAST_BACKUP_COMPLETE) | resets | resets | **resets** | no | no |
| AI auto-diagnosis (Api.cs 783) | no | no | yes | no | yes |

**Finding (conformance):** the same WITH_ERROR run is "complete" for the last-backup date, the missed alert, the restore-test
trigger, the scheduler, the client job and the exit code; "warnings" in the native client window; and a failure (red) in the
history, the admin site and the service calls. A customer whose PST is locked every night sees amber "warnings" on the
computer, red on the server, and "last backup: last night" — while the PST has not been backed up for months.

## Target (owner decision B2, 2026-10-08)

| Result | Shown as | Last Backup Attempt | **Last Complete Backup** | Alert / service call | Restore verification | Scheduler slot |
|---|---|---|---|---|---|---|
| Complete (SUCCESS) | Complete (green) | updates | updates | closes open alerts | due after it | done |
| Complete with warnings (WARNING; nothing missing) | Complete (green, with a note) | updates | updates | none | due after it | done |
| **Partial** (WITH_ERROR) | **Partial (amber)**, the files not backed up listed | updates | **does not update** | alert when partial repeats (threshold) | due only after a complete run | done (the next scheduled run tries again) |
| Stopped (BY_USER) | Stopped (grey) | updates | no | none (duration stop: warning) | no | not done |
| Failed (SYSTEM_ERROR, QUOTA, PRE_COMMAND, interrupted) | Failed (red) | updates | no | failure count → call | no | not done |

Next step (an agent, after the BK-05 agent finishes — same code area): conformance tests that run ONE run of each result
through every consumer above and compare with this target table (failing on today's code where the table says so), then the
minimal changes to make every consumer read the same rule. No refactor is planned unless the tests show the consumers cannot
share one rule without it.
