# OnlineBackup — product benchmark against mature backup products

Written by QA agent K (night run) at snapshot `4c6deea`. Purpose: give the owner the facts behind each open product
decision so it can be approved quickly. No production code was changed.

## How the evidence was gathered (read this first)

- Direct page fetching was **blocked by the network egress policy** for every vendor documentation site
  (helpcenter.veeam.com, helpcenter.nakivo.com, acronis.com, help.msp360.com / kb.msp360.com, ahsay.com, learn.microsoft.com,
  rfc-editor.org, restic.readthedocs.io). All competitor statements below come from **web-search excerpts of the official
  documentation pages** whose URLs are given next to each claim. They are paraphrases of those excerpts, not verbatim quotes.
  Before acting on a claim that decides a NEEDS OWNER DECISION, open its URL once and confirm the sentence is still there.
- Forum posts and third-party blogs were **not** used as evidence, except where marked.
- Where no official statement was found, the text says **"not found in official docs"**. Nothing is guessed.
- "Current OnlineBackup behavior" was read from the code at `4c6deea` (file:line) and `docs/R1-BUGLOG.md`.

## Summary table

| # | Question | Decision | One-line recommendation |
|---|---|---|---|
| 1 | SQL log backup of SIMPLE-recovery DBs (and master) under "all databases" | NEEDS OWNER DECISION | Skip them in log mode with a visible note (as Veeam / MSP360 / Ahsay do), not a run error; a DB the customer named explicitly → warning |
| 2 | GFS day/week/month boundaries: zone and weekly day | NEEDS OWNER DECISION | Buckets in a configured local zone (server or per-customer), not UTC; make the weekly day explicit and the same in both engines (today: native Sunday, restic Monday) |
| 3 | Restore when the destination file exists | NEEDS OWNER DECISION | Keep today's safe default (do not overwrite, said as a warning); consider adding "keep both (rename)" and "only if newer" |
| 4 | Restore interrupted midway | NEEDS OWNER DECISION | Keep staging (bug 48); also remove stale native `*.ob-restoring` temp files at the next restore (today they stay) |
| 5 | Damaged restore point / damaged object | NEEDS OWNER DECISION | Keep today's behaviour (file fails with reason, rest restores, full copy sent next run, mail alert) — it matches the industry |
| 6 | Backup stopped by the user midway | NEEDS OWNER DECISION | Decide whether a stopped native run stays a restore point (today: yes, counted by retention); keep "Stopped" status and no restore test |
| 7 | Two machines with the same name / cloned machine | NEEDS OWNER DECISION | Identify by registration id (already partly done); add an explicit "possible clone" alert instead of silent rebuild |
| 8 | Client clock vs server clock | NEEDS OWNER DECISION | Keep the client clock for the schedule (industry pattern); add a clock-difference warning |
| 9 | Missed schedule | NEEDS OWNER DECISION | Keep today's behaviour (run when back, 5-minute delay, configurable) — matches Veeam/MSP360 options |
| 10 | "No backup for X" alert | NEEDS OWNER DECISION | Keep it on by default; consider making 48 h configurable per customer (Veeam ONE default 24 h, Ahsay 6 h after slot) |
| 11 | Destination unavailable: retries before fail | NEEDS OWNER DECISION | Add an in-run reconnect window (minutes, not seconds) before the run fails; keep the 15-minute rescheduling |
| 12 | Source folder missing | NEEDS OWNER DECISION | Industry: warning (MSP360) / skip (Veeam managed jobs); today: error. Owner chooses |
| 13 | Files that could not be read | NEEDS OWNER DECISION | Industry: warnings/skips; today: error. Owner chooses |
| 14 | Wrong encryption password on a new machine | NEEDS OWNER DECISION | Keep the clear "wrong key" refusal; never create a new repository silently next to the old one |
| 15 | Two administrators editing one job | NEEDS OWNER DECISION | Keep the optimistic 409 "reload" (bug 71); a lock UI (NAKIVO style) is optional |
| 16 | Session end on disable; TOTP reuse | AUTO-APPROVED | Keep bug 57/58 fixes: RFC 6238 §5.2 and OWASP are explicit |
| 17 | Pin overrides OS-trusted certificate | NEEDS OWNER DECISION | Keep "pin decides" (bug 61, OWASP pinning guidance); document that TLS-inspection proxies must exempt the server |
| 18 | Installer says "installed" only when service runs | NEEDS OWNER DECISION | Keep bug 32 fix (same as MSI ServiceControl Wait=1 semantics) |

---

## 1. SQL Server log backup of SIMPLE-recovery databases (and master) under "all databases"

**Question.** In log mode with "all databases" selected, BACKUP LOG on a SIMPLE-recovery database (and on master) is refused by
SQL Server. Fail the job, warn, or skip silently?

**Veeam.** The transaction-log backup statistics list "excluded databases"; a database may be excluded because "the database
recovery model is set to Simple" (also: offline, read-only, deleted after the latest full backup, AutoClose, excluded by the
user). Shown in the session's statistics, not as a failure.
https://helpcenter.veeam.com/docs/backup/vsphere/sql_backup_stats.html ;
https://helpcenter.veeam.com/docs/agentforwindows/userguide/monitoring_restore_points_db.html .
Databases in Simple recovery are skipped from "Backup logs periodically" processing because their log does not need to be backed up.
https://helpcenter.veeam.com/docs/backup/vsphere/backup_job_vss_sql_vm.html ;
https://helpcenter.veeam.com/docs/agentforwindows/userguide/backup_job_vss_sql.html .
Master specifically in log mode: not found in official docs (beyond the generic list above).

**Competitor 2 — MSP360.** KB "Transaction Log Backups are Not Supported Using Simple Recovery Model (code 1800)": log
backups are supported only for Full and Bulk-Logged; a Simple-recovery database is skipped and the KB tells the user to change
the recovery model. https://kb.msp360.com/standalone-backup/ms-sql-server/transaction-log-not-supported-simple-recovery .
KB "Master Database Skipped (code 1808)": master is skipped for differential ("SQL Server does not allow any other type ...
except full backup ... on master"); remedy: exclude master or give it its own full-only plan.
https://kb.msp360.com/standalone-backup/ms-sql-server/master-db-skipped . (Whether code 1800/1808 colours the run as warning
or error: not found in official docs.)

**Competitor 3 — Ahsay OBM.** "When performing a MS SQL transaction log backup ... a **warning** message is received" with
SQL Server's text "The statement BACKUP LOG is not allowed while the recovery model is SIMPLE"; resolution: set the recovery
model to FULL. https://www.ahsay.com/en/support/help-centre/troubleshooting/obm/mssql-backup-log-is-not-allowed .

**Industry pattern.** Skip the database for the log run and say so (excluded list / warning). None of the three fails the
whole run for it.

**Current OnlineBackup behavior.** `src/Agent/Sources.cs:159-160` lists every ONLINE database except tempdb (master
included). In LOG mode `Sources.cs:177-183` runs BACKUP LOG for each; SQL Server refuses → `Sources.cs:225-229` calls
`warn(...)` and adds the DB to `unreachable`; `src/Agent/BackupRun.cs:237-239` turns every unreachable source into an error
("Not backed up: this source could not be read now"), so the run ends `BS_STOP_SUCCESS_WITH_ERROR`
(`BackupRun.cs:270`). If **no** database gave a log (all SIMPLE), `BackupRun.cs:242-245` makes it `BS_STOP_BY_SYSTEM_ERROR`.
With a 15-minute log interval this is an error mail/run every 15 minutes (bug 76; the skip fix was reverted).

**Proposed OnlineBackup behavior.** In LOG mode only: a SIMPLE-recovery database and master found through "all databases"
are skipped with one info line each ("log backup not possible: recovery model SIMPLE — covered by the full/differential
backup"); the run's status is not lowered by them. A database the customer selected **by name** that is SIMPLE → warning
(the customer explicitly asked for its log). A log run where every database was skipped → warning "no database in FULL
recovery". Optionally a once-a-day summary instead of per-run lines.

**Reason.** Matches all three products; today's behaviour produces a permanent false error that trains the customer to
ignore errors.

**Decision.** NEEDS OWNER DECISION (skipping a database; what counts as Success/Warning/Failed).

## 2. GFS / long-term retention boundaries

**Question.** Are day/week/month/quarter/year boundaries computed in local time (whose) or UTC? Which weekday is "weekly"?

**Veeam.** The weekly GFS flag goes to the full restore point of a **week day the user selects** ("If multiple full backups
exist, use the one from"), monthly to a selected week. https://helpcenter.veeam.com/docs/backup/vsphere/gfs_how_flags_assigned.html?ver=120 ;
https://helpcenter.veeam.com/docs/agentforwindows/userguide/backup_job_gfs_settings.html . Agent configurator: weekly day
parameter `DesiredTime` 0=Sunday..6=Saturday, start of keeping given as `BeginTimeUtc`.
https://helpcenter.veeam.com/docs/agentforwindows/configurator/job_parameters_retention.html . Agent jobs run on the
computer's local time (see Q8). Default weekday and the time zone used for GFS bucket edges: not found in official docs.

**Competitor 2 — Acronis Cyber Protect.** "A weekly backup is the **first** backup on the day of the week that you specify in
the Weekly backup option ... The preset is: **Monday**." "A monthly backup is the **first** backup each month."
https://www.acronis.com/en-us/support/documentation/CyberProtectionService/retention-rules-backup-scheme.html ;
https://www.acronis.com/en/support/documentation/CyberProtectionService/weekly-backup.html . Schedules use "the time settings
(including the time zone) of the operating system where the agent is installed".
https://www.acronis.com/en/support/documentation/CyberProtectionService/schedule.html .

**Competitor 3 — Ahsay.** Advanced retention policies Daily/Weekly/Monthly/Quarterly/Yearly/Custom, where the user picks the
weekday(s) / day of month (examples: "last four Saturdays", "last day of every month").
https://www.ahsay.com/en/support/help-centre/cbs/doc/v10/overview/user/backup-set . Each backup user has a **Timezone** in
the user profile (Users > Backup User > User Profile > General), updated automatically when the client logs in.
https://www.ahsay.com/en/support/help-centre/troubleshooting/obm/troubleshooting-problem-with-missing-scheduled-backup .

**Also relevant — restic (OnlineBackup's second engine).** "Weeks are Monday 00:00 -> Sunday 23:59"; calendar options
"work on natural time boundaries". https://restic.readthedocs.io/en/stable/060_forget.html .

**Industry pattern.** Local calendar time, never UTC; the weekly day is chosen by the user (Acronis preset Monday; Veeam's
default not documented). Acronis keeps the **first** backup of a period; Veeam flags a scheduled full; restic keeps the
**last** snapshot of a period.

**Current OnlineBackup behavior.** Native engine: `src/Core/Formats.cs:141-146` buckets on `RunId.Parse` which is **UTC**
(`Formats.cs:71`); weekly bucket = `d.AddDays(-(int)d.DayOfWeek)` → weeks start **Sunday** (UTC); keeps the **latest** run
per bucket (`Formats.cs:150-156`). Called from `src/Server/SetStore.cs:815-816` with `nowUtc`. Restic engine:
`src/Agent/ResticRunner.cs:479-486` passes `--keep-daily/weekly/monthly/yearly` → restic's Monday weeks in the snapshot's
(client) time; quarterly is approximated as `--keep-monthly max(monthly, 3×quarterly)` (`ResticRunner.cs:486`). So the two
engines already differ (Sunday-UTC vs Monday-client-local). The server has a configured time zone used for display
(`TimeSettings.Local`, e.g. `src/Server/Api.cs:2004`). Bug 77 (fix to the server's zone) reverted pending decision.

**Proposed OnlineBackup behavior.** Buckets in one configured local zone — the server's configured zone, or (Ahsay-like)
a per-customer zone — for both engines; one documented weekly day (suggest Monday = ISO, which matches restic) or a
setting; keep "last of the period" (consistent with restic). Owner to confirm all three choices.

**Reason.** UTC edges keep the wrong backup at month starts east of UTC (bug 77, proven); engine inconsistency is visible to a
customer comparing sets.

**Decision.** NEEDS OWNER DECISION (retention; deleting backups).

## 3. Restore when the destination file already exists

**Veeam.** Restore to original location offers **Overwrite** or **Keep** ("adds the RESTORED- prefix ... and saves it in the
same location"). https://helpcenter.veeam.com/docs/agentforwindows/userguide/files_restore_initial.html ;
https://helpcenter.veeam.com/docs/agentforwindows/userguide/integration_file_restore_complete_original.html . Default: not
found in official docs.

**Competitor 2 — NAKIVO.** "Overwrite behavior": "Rename recovered item if such item exists", "Skip recovered item if such
item exists", "Overwrite the original item if such item exists".
https://helpcenter.nakivo.com/User-Guide/Content/Recovery/Granular-Recovery/File-Recovery/File-Recovery-Wizard-Options.htm .
Default: not found in official docs.

**Competitor 3 — Acronis Cyber Protect.** Three options: "Overwrite existing files", "Overwrite an existing file if it is
older", "Do not overwrite existing files".
https://www.acronis.com/en/support/documentation/CyberProtectionService/recovering-files-web-console.html . The command-line
reference's `overwrite` parameter is reported (search excerpt) to default to "always".
https://www.acronis.com/en-us/support/documentation/AcronisCyberProtect_15_Command_Line_Reference/overwrite.html — **low
confidence; verify**. Console default: not found in official docs.

**Also — MSP360.** "Overwrite existing files" option, with "Restore only new files" (restore when the cloud copy is newer).
https://help.msp360.com/cloudberry-backup/restore/restore-files-folders/step-6-specify-the-restore-destination .
**Ahsay.** Advanced restore options "Overwrite when exist" / "Skip when exist" (excerpt from the cloud-file restore guide).
https://wiki.ahsay.com/doku.php?id=public%3Adocuments_and_guides%3Av8%3Aobm%3Acloud-file-windows%3Arestore .

**Industry pattern.** Always a user choice among overwrite / skip / keep-both (rename) / newer-only. Defaults are mostly not
documented.

**Current OnlineBackup behavior.** Native: `src/Agent/Restore.cs:87` skips an existing file unless `overwrite`; skipped files
make the result `RESTORE_STOP_WITH_WARNING` with a line telling the user to choose overwrite (`Restore.cs:100-101`). Restic:
`ResticRunner.cs:550-562` keeps existing files unless asked (bug 39). The window's "Replace existing files" checkbox is
unchecked by default (`src/Agent/client.html:201,225`); CLI needs `--overwrite` (`src/Agent/Program.cs:17,111,117`).
No rename / newer-only option.

**Proposed OnlineBackup behavior.** Keep "do not overwrite" as default (safest: never destroys a newer customer file). Option
for the owner: add "keep both (restore as `name (restored).ext`)" and "only if the backup is newer".

**Reason.** Default skip cannot lose data and is said as a warning; adding the two extra options brings parity.

**Decision.** NEEDS OWNER DECISION (overwrite; UX).

## 4. A restore interrupted midway

**Veeam.** When something disrupts a restore (server crash, network down), "the restore process stays in waiting mode and
performs 10 automatic retries every 5 minutes"; afterwards it can be retried manually (excerpt; page:
https://helpcenter.veeam.com/docs/vbr/userguide/vep_restore_manage_session.html — **verify which restore type it covers**).
Data transport: "resume on disconnect" every 15 seconds for 30 minutes.
https://helpcenter.veeam.com/docs/backup/vsphere/replica_resume_disconnect.html . What is left on disk after a killed file
restore: not found in official docs.

**Competitor 2 — MSP360.** Not found in official docs for an interrupted restore. (Official KB covers restoring the intact
part of a damaged backup — see Q5.)

**Competitor 3 — Ahsay.** The OBM temporary directory is used during restore "to store full and incremental/differential delta
files ... as well as for merging" (Q&A PDF:
https://www.ahsay.com/download/customer/document/qa-tips-on-temporary-directory-setup.pdf ). "Resume restoration" after a
stop is documented for **Ahsay Mobile** only: https://www.ahsay.com/en/support/help-centre/doc/app/v9/restore-backup . For
OBM: not found in official docs.

**Industry pattern.** Retry/resume of the session is documented; the on-disk state of half-restored files is not documented
by any of them.

**Current OnlineBackup behavior.** Restic: restores into a fresh `.ob-restoring-<id>` folder in the target; only a restore
with exit 0 is moved into place; old stage folders are deleted at the next restore (`ResticRunner.cs:521-541`, bug 48).
Native: each file is written to `<dest>.<8 hex>.ob-restoring` (CreateNew) and renamed over the real name only when its size
matches (`Restore.cs:138-157`); on an exception the temp file is deleted (`Restore.cs:154`). **If the process is killed**, the
temp file stays next to the real file and nothing removes it later (no other reference to `.ob-restoring` in `src/`); the
real name is never half-written. Next restore: files already finished exist → skipped unless overwrite (fine, they are
complete). No resume: a restore starts over.

**Proposed OnlineBackup behavior.** Keep both staging designs. Add: the next native restore into the same target removes stale
`*.ob-restoring` files it finds (or the restore lists them). Resume is optional.

**Reason.** Never a half file under a real name (good); the leftover temp files are clutter and can fill a disk after repeated
kills.

**Decision.** NEEDS OWNER DECISION (overwrite/what is left in the customer's folders). The stale-temp cleanup alone is a small
technical change.

## 5. Damaged restore point / damaged object

**Veeam.** Agent for Windows: when the health check finds corrupted data, the job completes with **Error** and a health-check
retry transports the needed blocks from the computer to repair the restore point; if all retries fail the job must be retried
manually. https://helpcenter.veeam.com/docs/agentforwindows/userguide/backup_health_check.html . Backup & Replication: the
restore point is marked corrupted in the configuration database and "you need to perform the active full backup".
https://helpcenter.veeam.com/docs/backup/vsphere/backup_health_check.html .

**Competitor 2 — MSP360.** Codes 2513/2514 ("Missed/Corrupted backup data detected"): restore the intact part with "Ignore
Missing Data". https://help.msp360.com/cloudberry-backup/restore/restore-corrupted ;
https://kb.msp360.com/managed-backup-service/errors-and-warnings/corrupted-backup . Consistency check: if problems are found,
a full backup is forced. https://help.msp360.com/cloudberry-backup-mac-linux/backup/about/mandatory-consistency-check ;
https://help.msp360.com/cloudberry-backup-mac-linux/backup/about/full-consistency-check .

**Competitor 3 — Ahsay.** Data Integrity Check "cannot fix or repair files that are already corrupted. It will only identify and
remove any corrupted files ... so these files can be re-uploaded again on the next backup job if they still exist on the
backup source"; corrupted files removed from the retention area are no longer restorable.
https://www.ahsay.com/en/support/help-centre/doc/cbs/v10/run-on-server-backup-and-restore/data-integrity-check ;
https://www.ahsay.com/en/support/help-centre/acb/how-to/backup/run-data-integrity-check-in-acb-and-obm .

**Industry pattern.** Flag the damage loudly, allow restoring what is intact, and heal by re-sending from the source
(full/active full or block re-transport).

**Current OnlineBackup behavior.** Verify (`src/Server/SetStore.cs:661-692`): a bad object is quarantined, remembered in
`lost`, removed from `objects`, and queued in `resend`. Points that held it list the file with `damaged=1`
(`SetStore.cs:589-602`); restoring that file throws "found damaged ... cannot be restored. Restore an earlier point"
(`src/Agent/Restore.cs:111-112`) while the other files restore → `RESTORE_STOP_WITH_ERROR` (`Restore.cs:100`). Next backup
sends a full copy of the file (`BackupRun.cs:143,189-195`). An admin alert mail is sent when a post-backup check finds damage
(`src/Server/Api.cs:690-691`); a damage found in the commit check raises the run's warnings (`BackupRun.cs:303`). Unreadable
(not damaged) objects are not quarantined (bug 46, `SetStore.cs:670-678`).

**Proposed OnlineBackup behavior.** Keep as is. Owner may add: the restore window marks points that contain damaged files
before the user starts.

**Reason.** Same pattern as Veeam Agent / Ahsay / MSP360; never a silent wrong version (bug 56).

**Decision.** NEEDS OWNER DECISION (which version a restore returns; status).

## 6. A backup run stopped by the user midway

**Veeam.** Agent for Windows: when you stop a backup job "the job session will finish immediately, and Veeam Agent will not
produce a new restore point". https://helpcenter.veeam.com/docs/agentforwindows/userguide/backup_job_stop.html .
Backup & Replication (managed agent jobs): Immediate stop → new restore point only for computers already processed; Graceful
stop → also for those being processed. https://helpcenter.veeam.com/docs/vbr/userguide/agent_job_start_stop.html .

**Competitor 2 — Acronis.** "Backup plan is canceled" alert: the last backup "was not completed and your data was not
protected at the expected time"; deactivated when the backup runs again. https://kb.acronis.com/content/59755 . Whether a
partial backup is kept: not found in official docs.

**Competitor 3 — Ahsay.** Not found in official docs (status of a user-stopped OBM backup and whether its data forms a point).

**Industry pattern.** Veeam: no restore point from an interrupted machine; Acronis: shown as canceled with an alert. Restore
verification on a stopped run: not found in official docs.

**Current OnlineBackup behavior.** Native: on stop, the loop breaks (`BackupRun.cs:178`), deletions are not computed
(`BackupRun.cs:251`), result `BS_STOP_BY_USER` (`BackupRun.cs:269`), and the run **is committed** (`BackupRun.cs:271-280`) —
the server stores it as a normal point (`SetStore.cs:438`, status 'OK'), so it is restorable and counted by retention
(`SetStore.cs:815`). Status shown "stopped" (`src/Server/RunLog.cs:63`); mail "Backup stopped before the end — not all data
was backed up" (`src/Server/Notify.cs:125`); no ticket (`src/Server/Tickets.cs:379`). The restore test runs only after a
`BS_STOP_SUCCESS*` attempt (`src/Agent/AgentApp.cs:302-306`, bug 72). Restic: `StoppedException` → `BS_STOP_BY_USER`
(`ResticRunner.cs:246-252,282`), no snapshot id is reported.

**Proposed OnlineBackup behavior.** Owner chooses: (a) keep the partial native point but mark it "partial" in the restore list
and exclude it from "keep N last jobs" counting, or (b) Veeam-like: do not create a point. Keep "Stopped" status and no restore
test either way.

**Reason.** With retention by job count, several stopped runs can push out complete points — data-loss risk.

**Decision.** NEEDS OWNER DECISION (Success/Warning/Failed; retention).

## 7. Two machines with the same name / cloned machine

**Veeam.** Protection-group rescan collects per computer "BIOS UUID", hostname, OS, IP and agent info.
https://helpcenter.veeam.com/docs/backup/agents/discovery_job.html . Behaviour for two agents with the same UUID: not found in
official docs.

**Competitor 2 — Acronis.** Agent identity is `MMSCurrentMachineID` ("Agent ID") and `InstanceID`; a machine restored from an
image of an already-registered machine fails to register ("Client with same ID is already created") until new GUIDs are
generated. https://kb.acronis.com/content/62866 ;
https://care.acronis.com/s/article/65008-Acronis-Cyber-Protect-Acronis-Cyber-Backup-How-to-change-MMScurrentMachineID-and-InstanceID?language=en_US ;
https://kb.acronis.com/content/62727 .

**Competitor 3 — MSP360.** The backup prefix defaults to the computer name; backups from two computers with the same prefix
at the same time are not allowed ("generation number collision due to simultaneous backups from cloned instances"); remedy:
change the prefix. https://kb.msp360.com/backup/warnings/consistency-check-warnings ;
https://help.msp360.com/cloudberry-backup/tools/continue-backup .

**Also — Ahsay.** The scheduler checks that the computer name equals the one in the backup set; otherwise the scheduled
backup is skipped. https://www.ahsay.com/en/support/help-centre/troubleshooting/obm/troubleshooting-problem-with-missing-scheduled-backup .

**Industry pattern.** A machine/agent GUID is the identity (Acronis, Veeam UUID); name-based products (MSP360, Ahsay) detect the
collision and stop/skip, telling the admin.

**Current OnlineBackup behavior.** Registration gives a device id (`src/Agent/AgentApp.cs:44,77`); a set records its device
(`AgentApp.cs:95`). `Mine()` (`AgentApp.cs:82-87`): same name and (no device, same device, or this computer holds the key) →
mine; another registration's set without the key here is left alone (bug 66). A cloned disk (same device token and key)
is detected only indirectly: the server's last run differs from the local index → warning, index rebuilt from the server
(`BackupRun.cs:146-155`, bug 55). Server "disconnect" revokes devices by **name** (`src/Server/Computers.cs:88`).

**Proposed OnlineBackup behavior.** Keep registration-id identity. Add: when one device token is seen from two different
machine fingerprints / IPs alternating, raise an admin alert "possible cloned computer" and ask to re-register one copy.

**Reason.** Silent index rebuilds on every alternate run hide the clone; Acronis/MSP360 surface it.

**Decision.** NEEDS OWNER DECISION (alerts; skipping a backup).

## 8. Client clock different from server clock

**Veeam.** Agent jobs (including policies managed by a backup server) run "according to the local time of the computer".
https://helpcenter.veeam.com/docs/vbr/userguide/agent_policy_win_schedule_workstation.html ;
https://helpcenter.veeam.com/docs/backup/agents/agent_policy_win_schedule_server.html . Skew tolerance: not found in official docs.

**Competitor 2 — Acronis.** Schedule "employs the time settings (including the time zone) of the operating system where the agent
is installed". https://www.acronis.com/en/support/documentation/CyberProtectionService/schedule.html . Skew tolerance: not
found in official docs.

**Competitor 3 — Ahsay.** Wrong user time zone causes false "missed backup" alerts; system clocks of client and server being
off can contribute to missed backups.
https://www.ahsay.com/en/support/help-centre/troubleshooting/obm/troubleshooting-problem-with-missing-scheduled-backup .
Tolerance value: not found in official docs.

**Also — MSP360.** Web console shows the agent's local time zone next to the provider's and the browser's.
https://www.msp360.com/resources/blog/introducing-msp360-backup-5-8-with-new-restore-plan-wizards-flexible-scheduling-and-consistent-date-and-time-format/
(vendor blog, not a manual).

**Industry pattern.** The client clock drives the schedule; no documented skew tolerance.

**Current OnlineBackup behavior.** Scheduler `Due(s, SystemClock.Now)` (`AgentApp.cs:551`) on the computer's local clock;
last success compared on the computer's clock (`LastSuccessLocalMs`, `AgentApp.cs:434-436`, bug 67). The server's
"missed backup" check uses server UTC vs `LAST_BACKUP_COMPLETE` (`Api.cs:1985-1994`). No clock-difference detection (no
"skew" logic in `src/`).

**Proposed OnlineBackup behavior.** Keep client clock. Add a warning on the computer's page when its clock differs from the
server's by more than N minutes (N = owner; e.g. 5).

**Reason.** Matches all vendors; a visible skew warning explains otherwise confusing missed/duplicate runs.

**Decision.** NEEDS OWNER DECISION (scheduling; alerts).

## 9. Missed schedule (machine off at the scheduled time)

**Veeam.** "Backup once powered on": at start-up the agent checks missed scheduled backups and runs the missed one; applies to
scheduled backups only. https://helpcenter.veeam.com/docs/agentforwindows/userguide/scheduled_backup_missed.html ;
https://helpcenter.veeam.com/docs/agentforwindows/userguide/backup_job_schedule_free_desktop.html .

**Competitor 2 — Acronis.** "If the machine is turned off, run missed tasks at the machine startup" — reported **disabled by
default** (excerpt). https://www.acronis.com/en/support/documentation/AcronisCyberProtect_15/schedule.html — verify.

**Competitor 3 — MSP360.** "Run missed scheduled backup immediately when computer starts up" (option).
https://help.msp360.com/cloudberry-backup/backup/back-up-microsoft-sql-server-data/step-10-schedule-your-backup-plan ;
https://help.msp360.com/cloudberry-backup/backup/file-backup/nbf .

**Also — Ahsay.** Off/hibernated computers are listed as a cause of missed backups (no catch-up option found).
https://www.ahsay.com/en/support/help-centre/troubleshooting/obm/troubleshooting-problem-with-missing-scheduled-backup .

**Industry pattern.** A configurable "run missed at start-up" option; default differs (Acronis off; Veeam/MSP360 default not
found).

**Current OnlineBackup behavior.** `src/Core/Profile.cs:102-104,125`: `RunMissed` on, `MissedDelayMinutes` 5,
`MissedMinHours` 0, `RunMissedNet` on. `AgentApp.cs:438-472`: missed slot runs after the delay; missed because offline runs
when the server is back; retry every 15 minutes (`AgentApp.cs:474`).

**Proposed OnlineBackup behavior.** Keep.

**Reason.** Same as Veeam's "Backup once powered on"; on by default is the safer choice for an MSP product.

**Decision.** NEEDS OWNER DECISION (scheduling) — recommended: approve as is.

## 10. "No backup for X" alert

**Veeam.** Veeam ONE alarm "Computer without backup" (computers with Veeam agents not backed up within the RPO) — reported as
enabled by default with 24 hours (excerpt). https://helpcenter.veeam.com/docs/one/userguide/backup_alarms_events.html — verify
default.

**Competitor 2 — Acronis.** Alert "No successful backups have been performed ... for more than X days" exists; default X: not
found in official docs. https://kb.acronis.com/content/71019 .

**Competitor 3 — Ahsay.** A backup not started 6 hours after its scheduled time is a "missed backup"; a "Missed scheduled
backup reminder" mail exists; the "inactive backup report when my backup set hasn't run for this number of days" is **not**
selected by default. https://wiki.ahsay.com/doku.php?id=public:smtp ;
https://www.ahsay.com/en/support/help-centre/cbs/doc/v9/administration/monitoring .

**Also — MSP360.** "Alert plan as overdue after" (configurable). Default: not found in official docs.
https://www.msp360.com/resources/blog/creating-file-level-backup-plan-with-msp360-managed-backup/ (vendor blog).

**Industry pattern.** An on-by-default "no backup within the RPO" alarm, threshold configurable (24 h Veeam ONE; Ahsay per
slot + 6 h).

**Current OnlineBackup behavior.** Admin mail after **48 h** with no completed backup, at most once per 24 h, hard-coded
(`src/Server/Api.cs:1994-2004`), checked for every set in the nightly maintenance (`Api.cs:1718`). Ticket after
`MissedHours` (default 48, configurable 0–1440, `src/Server/Tickets.cs:59,84`).

**Proposed OnlineBackup behavior.** Keep on by default; make the mail threshold use the same configurable `MissedHours`
(default 48, or 24 like Veeam ONE — owner).

**Reason.** One setting for both; parity with the industry's configurable RPO alarm.

**Decision.** NEEDS OWNER DECISION (alert sent / not sent).

## 11. Destination unavailable at backup time

**Veeam.** Agent: automatic retries for scheduled jobs — by default 3 retries, every 10 minutes; no retry for manual jobs or
after Success/Warning. https://helpcenter.veeam.com/docs/agentforwindows/userguide/scheduled_backup_retry.html . Transport
resume on disconnect: every 15 seconds for 30 minutes.
https://helpcenter.veeam.com/docs/backup/vsphere/replica_resume_disconnect.html .

**Competitor 2 — Acronis.** "Re-attempt, if an error occurs": preset enabled, 30 attempts, 30 seconds apart; for cloud storage
300 attempts × 30 s. https://www.acronis.com/en/support/documentation/AcronisCyberProtect_15/error-handling.html .

**Competitor 3 — NAKIVO.** "Auto retry failed jobs": 2–10 retries, 1–60 minutes apart (system setting).
https://helpcenter.nakivo.com/User-Guide/Content/Settings/General/System-Settings-Configuration.htm . Default: not found in
official docs.

**Industry pattern.** Minutes of in-run reconnect attempts (Acronis 15 min local / 2.5 h cloud; Veeam 30 min transport), then
job-level retries.

**Current OnlineBackup behavior.** Each API call: 3 retries after 1, 2, 3 s (`src/Agent/Client.cs:24,124-148`); object upload:
3 attempts, waits 2 s and 8 s (`BackupRun.cs:422-478`). Then the run aborts as `BS_STOP_BY_SYSTEM_ERROR`
(`BackupRun.cs:219-227`) and the scheduler retries every 15 minutes / as soon as the server answers (`AgentApp.cs:451-458,474`).
So a ~10-second outage fails the run.

**Proposed OnlineBackup behavior.** Add an in-run reconnect window (e.g. retry every 30 s for up to 10–30 min) before failing;
keep the 15-minute rescheduling.

**Reason.** A short line drop should not produce a failed run and an alert; every product waits minutes, not seconds.

**Decision.** NEEDS OWNER DECISION (what counts as Failed; scheduling).

## 12. A source folder missing / not readable

**Veeam.** Agent jobs managed by Backup & Replication: if a specified directory does not exist on a computer, "the job will skip
such folder ... and back up existing ones" (excerpt). https://helpcenter.veeam.com/docs/backup/agents/agent_job_folders_linux.html .
Status colour: not found in official docs.

**Competitor 2 — MSP360.** KB "**Warning**. One or more backup paths do not exist".
https://kb.msp360.com/standalone-backup/general/warning-one-or-more-backup-paths-do-not-exist .

**Competitor 3 — Acronis / Ahsay.** Not found in official docs.

**Industry pattern.** Warning (MSP360); skip and continue (Veeam).

**Current OnlineBackup behavior.** `BackupRun.cs:537` (`Scanner.Files`) warns and marks the source unreachable; its files are
kept, not deleted (`BackupRun.cs:254-256`); `BackupRun.cs:239` makes it an **error** → `BS_STOP_SUCCESS_WITH_ERROR`; all sources
missing → `BS_STOP_BY_SYSTEM_ERROR` (`BackupRun.cs:242-245`). Restic: error per missing source, `NO_SOURCE` failure if none
(`ResticRunner.cs:235-236`).

**Proposed OnlineBackup behavior.** Owner chooses: keep error (stricter than the industry, safest for unnoticed USB/share
loss), or warning like MSP360. All-missing stays Failed in either case.

**Reason.** Stricter status cannot lose data but creates more alerts.

**Decision.** NEEDS OWNER DECISION (Success/Warning/Failed).

## 13. Files that could not be read (locked / permission)

**Veeam.** Not found in official docs (file-level). A locked **volume** in the scope makes the job fail (excerpt).
https://helpcenter.veeam.com/docs/agentforwindows/userguide/backup_job_folders.html — verify.

**Competitor 2 — MSP360.** KB entries "Files and folders were skipped (code 1603)", "No Access to Some Files (code 1622)",
"Access Denied (code 1610)". https://kb.msp360.com/standalone-backup/file-level-backup/files-and-folders-were-skipped-code-1603 ;
https://kb.msp360.com/standalone-backup/file-level-backup/no-access2files-1622 ;
https://kb.msp360.com/standalone-backup/general/access-denied . Severity label: not confirmed in the excerpts.

**Competitor 3 — Acronis.** With no snapshot, files open by another program are skipped and the backup "may complete with
warnings"; with "Do not create a snapshot" a locked file fails the backup unless silent mode is on.
https://kb.acronis.com/content/63888 ; https://kb.acronis.com/content/46941 .

**Industry pattern.** Skipped files are usually a warning (with a list), not a failed run.

**Current OnlineBackup behavior.** Native: `BackupRun.cs:228-232` → `Err` → `BS_STOP_SUCCESS_WITH_ERROR` (earlier version kept).
Restic: restic's per-file errors and exit 3 → error (`ResticRunner.cs:262,270`, bug 40). Server mail title "Backup completed
with errors — some data was not backed up" (`Notify.cs:124`).

**Proposed OnlineBackup behavior.** Owner chooses: keep "completed with errors" (explicit) or Acronis-like warning. Note
`R1 (GPT audit 4)` at `BackupRun.cs:235-236` deliberately made it an error.

**Reason.** Status semantics are an owner decision; today's is stricter than the industry.

**Decision.** NEEDS OWNER DECISION (Success/Warning/Failed).

## 14. Wrong encryption password on a new machine

**Veeam.** On a computer without the key in its database, the password must be the one used to encrypt; the agent shows the
password **hint**; lost passwords can be recovered via Enterprise Manager only if password loss protection is enabled.
https://helpcenter.veeam.com/docs/agentforwindows/userguide/restore_encrypted.html .

**Competitor 2 — Acronis.** Lost password cannot be recovered; the error is "The password for the protected backup is
incorrect". https://care.acronis.com/s/article/Backup-Archive-Password-Protection?language=en_US ;
https://care.acronis.com/s/article/68811-Acronis-Cyber-Protect-Attempting-to-access-an-encrypted-backup-archive-fails-with-The-password-for-the-protected-backup-is-incorrect?language=en_US .

**Competitor 3 — Ahsay.** After reinstall / on another machine the user is prompted for the encryption key of each existing
backup set; without the key and without "Encryption Recovery" the data cannot be restored.
https://wiki.ahsay.com/doku.php?id=public%3A5034_best_practices_for_managing_encryption_key ;
https://www.ahsay.com/en/services/encryption-key-recovery .

**Industry pattern.** Refuse with a clear "wrong password" message; offer a hint or key recovery if it was set up. Creating a new
repository next to the old one under the same set: not found in official docs for any product.

**Current OnlineBackup behavior.** Native: `AgentApp.cs:114-116` — "This computer does not have the set's encryption key. Enter
the key or get it from your provider (key recovery)" / "The encryption key is wrong." (check value vs `KeyCheck`). Restic:
`ResticRunner.cs:157-166` — wrong key is reported ("does not open its backup ... nothing was changed") and never falls through
to `restic init` (Agent J, J-4). A brand-new set always gets its own key and salt (`AgentApp.cs:89-101`); whether the client
UI offers "create a new set instead" from the wrong-key screen: not verified.

**Proposed OnlineBackup behavior.** Keep. Option: a password hint (Veeam) at set creation.

**Reason.** Already the industry pattern; silently starting a new repository would split history.

**Decision.** NEEDS OWNER DECISION (UX).

## 15. Two administrators editing the same job

**Veeam.** Not found in official docs.

**Competitor 2 — NAKIVO.** API "Get Job for Editing" takes an **exclusive lock** (15-second timeout, refreshed by the caller); a
save must carry the `lockUuid`. https://helpcenter.nakivo.com/api-reference/Content/Job-Management/Get-Job-for-Editing.htm ;
https://helpcenter.nakivo.com/api-reference/Content/Job-Management/Creating-Editing-a-Job.htm .

**Competitor 3 — Acronis / MSP360.** Not found in official docs.

**Industry pattern.** Only NAKIVO documents it (pessimistic lock).

**Current OnlineBackup behavior.** `src/Server/SetControl.cs:62-70` settings version (hash of editable settings);
`SetControl.cs:85-88` a save made on an older version → 409 "Another administrator changed this set after you opened it.
Reload it and make your change again." (bug 71).

**Proposed OnlineBackup behavior.** Keep (optimistic, no stuck locks).

**Reason.** Prevents the silent undo; no data path involved.

**Decision.** NEEDS OWNER DECISION (UX), recommended approve as is — the industry pattern is not uniform enough to auto-approve.

## 16. Session end on disable; TOTP code reuse

**Standard — RFC 6238 §5.2.** "The verifier MUST NOT accept the second attempt of the OTP after the successful validation has
been issued for the first OTP, which ensures one-time only use of an OTP."
https://datatracker.ietf.org/doc/html/rfc6238#section-5.2 (sentence confirmed through search excerpts quoting the RFC).

**Standard — OWASP Session Management Cheat Sheet.** Sessions must be invalidated server-side; session ids renewed on privilege
changes; sessions invalidated on password reset.
https://cheatsheetseries.owasp.org/cheatsheets/Session_Management_Cheat_Sheet.html .

**Veeam.** MFA with an authenticator OTP for console/web UI; idle auto-logoff.
https://helpcenter.veeam.com/docs/vbr/userguide/mfa.html . Immediate session end on user removal: not found in official docs.

**Competitor 2 / 3.** Not found in official docs.

**Industry pattern.** RFC: one use per code. OWASP: server-side invalidation when the reason for the session ends.

**Current OnlineBackup behavior.** `src/Server/Users.cs:445-451`: every request re-checks an admin session against the staff
account (removed / disabled / reseller changed → session ended, bug 57). TOTP: `src/Core/Crypto.cs:224-233` accepts the step
±1; `src/Server/Staff.cs:85-90` and `src/Server/Users.cs:198-200` accept a step only if greater than `TOTP_LAST_STEP` (bug 58).

**Proposed OnlineBackup behavior.** Keep.

**Reason.** Mandated by RFC 6238 and OWASP; no product decision involved.

**Decision.** AUTO-APPROVED.

## 17. Pinning the server certificate in the client

**Veeam.** Agents connecting to a Cloud Connect / backup server check the entered **thumbprint** against the obtained TLS
certificate; on certificate change the server must be acknowledged again.
https://helpcenter.veeam.com/docs/agentforwindows/userguide/backup_job_sp_settings.html?ver=13 ;
https://helpcenter.veeam.com/docs/agentforlinux/userguide/manage_vbr_add.html . Whether a pin overrides OS trust: not found in
official docs.

**Standard — OWASP Pinning Cheat Sheet.** Pinning defends against a rogue CA certificate and a CA injected into the trust store;
do not allow-list an interception proxy unless risk acceptance instructs adding its key to the pinset.
https://cheatsheetseries.owasp.org/cheatsheets/Pinning_Cheat_Sheet.html .

**Competitor 3 — Ahsay.** Default self-signed certificates should not be used for public servers; trusted CA list per version.
https://www.ahsay.com/en/support/help-centre/technical-videos/when-are-default-ahsay-dummy-self-signed-ssl-certificates-acceptable-business-use ;
https://www.ahsay.com/en/support/help-centre/how-to/cbs/compatibility/trusted-certificate-authority-list-v10 . Pinning: not
found in official docs.

**Industry pattern.** Thumbprint = the decision (Veeam); OWASP says a pin is meant precisely to beat OS-trusted interception.

**Current OnlineBackup behavior.** `src/Agent/Client.cs:59-93`: with a pin for the backup server's host, only the pin decides
(even if Windows trusts the certificate); other hosts use system trust (bug 61).

**Proposed OnlineBackup behavior.** Keep; document that a TLS-inspection proxy must bypass the backup server, and make the
error say "certificate is not the pinned one (possible TLS inspection)".

**Reason.** Security best practice; but customers behind inspection proxies will see backups fail until exempted.

**Decision.** NEEDS OWNER DECISION (a backup can stop running for such customers).

## 18. Installer: "installed" only after the service is running

**Veeam.** Unattended install reports result codes (1000 = installed), and "during unattended setup, Veeam Agent ... will not
start the ... service, but after you reboot the computer, the service will be started".
https://helpcenter.veeam.com/docs/agentforwindows/userguide/installation_unattended.html . Interactive last page vs service
state: not found in official docs.

**Standard — Windows Installer ServiceControl table.** `Wait` = 1 "means to wait until the service actually completes before
proceeding", the event is critical and its failure cannot be ignored.
https://learn.microsoft.com/en-us/windows/win32/msi/servicecontrol-table .

**Competitor 2 / 3.** Not found in official docs.

**Industry pattern.** MSI-based installers commonly fail the install when a critical service does not start (Wait=1).

**Current OnlineBackup behavior.** `src/Agent/AgentService.cs:45-52`: create, set restart-on-failure, start, then
`ServiceState.WaitRunning` (`src/Core/ServiceState.cs:24-35`) — anything but RUNNING throws; the setup's Finish page then shows
"The installation did not finish" in red (`src/Agent/SetupForm.cs:264-268`) (bug 32).

**Proposed OnlineBackup behavior.** Keep.

**Reason.** Matches MSI Wait=1 semantics; never a green page on an unprotected computer.

**Decision.** NEEDS OWNER DECISION (installer/UX) — recommended approve as is.

---

## Not found in official docs (summary)

- Veeam: default weekly GFS day; time zone of GFS bucket edges; behaviour with duplicate BIOS UUIDs; on-disk state after a killed
  file restore; immediate session end on user removal; concurrent job editing; overwrite default in file restore.
- NAKIVO: default of "Auto retry failed jobs"; default overwrite behaviour.
- Acronis: console default of file overwriting (CLI default "always" is low-confidence); default X of "no successful backups"
  alert; whether a canceled backup's partial data is kept; missing-source status.
- MSP360: default "overdue" days; severity of codes 1800/1808/1603/1622.
- Ahsay: status of a user-stopped OBM backup; "Resume restoration" in OBM (documented only for Ahsay Mobile); master in log mode;
  pinning.
- Any vendor: documented clock-skew tolerance; creating a new repository next to an old one after a wrong password.

## Sources

- https://helpcenter.veeam.com/docs/backup/vsphere/sql_backup_stats.html
- https://helpcenter.veeam.com/docs/agentforwindows/userguide/monitoring_restore_points_db.html
- https://helpcenter.veeam.com/docs/backup/vsphere/backup_job_vss_sql_vm.html
- https://helpcenter.veeam.com/docs/agentforwindows/userguide/backup_job_vss_sql.html
- https://helpcenter.veeam.com/docs/backup/vsphere/gfs_how_flags_assigned.html?ver=120
- https://helpcenter.veeam.com/docs/agentforwindows/userguide/backup_job_gfs_settings.html
- https://helpcenter.veeam.com/docs/agentforwindows/configurator/job_parameters_retention.html
- https://helpcenter.veeam.com/docs/agentforwindows/userguide/files_restore_initial.html
- https://helpcenter.veeam.com/docs/agentforwindows/userguide/integration_file_restore_complete_original.html
- https://helpcenter.veeam.com/docs/vbr/userguide/vep_restore_manage_session.html
- https://helpcenter.veeam.com/docs/backup/vsphere/replica_resume_disconnect.html
- https://helpcenter.veeam.com/docs/agentforwindows/userguide/backup_health_check.html
- https://helpcenter.veeam.com/docs/backup/vsphere/backup_health_check.html
- https://helpcenter.veeam.com/docs/agentforwindows/userguide/backup_job_stop.html
- https://helpcenter.veeam.com/docs/vbr/userguide/agent_job_start_stop.html
- https://helpcenter.veeam.com/docs/backup/agents/discovery_job.html
- https://helpcenter.veeam.com/docs/vbr/userguide/agent_policy_win_schedule_workstation.html
- https://helpcenter.veeam.com/docs/backup/agents/agent_policy_win_schedule_server.html
- https://helpcenter.veeam.com/docs/agentforwindows/userguide/scheduled_backup_missed.html
- https://helpcenter.veeam.com/docs/agentforwindows/userguide/backup_job_schedule_free_desktop.html
- https://helpcenter.veeam.com/docs/one/userguide/backup_alarms_events.html
- https://helpcenter.veeam.com/docs/agentforwindows/userguide/scheduled_backup_retry.html
- https://helpcenter.veeam.com/docs/backup/agents/agent_job_folders_linux.html
- https://helpcenter.veeam.com/docs/agentforwindows/userguide/backup_job_folders.html
- https://helpcenter.veeam.com/docs/agentforwindows/userguide/restore_encrypted.html
- https://helpcenter.veeam.com/docs/vbr/userguide/mfa.html
- https://helpcenter.veeam.com/docs/agentforwindows/userguide/backup_job_sp_settings.html?ver=13
- https://helpcenter.veeam.com/docs/agentforlinux/userguide/manage_vbr_add.html
- https://helpcenter.veeam.com/docs/agentforwindows/userguide/installation_unattended.html
- https://helpcenter.nakivo.com/User-Guide/Content/Recovery/Granular-Recovery/File-Recovery/File-Recovery-Wizard-Options.htm
- https://helpcenter.nakivo.com/User-Guide/Content/Settings/General/System-Settings-Configuration.htm
- https://helpcenter.nakivo.com/api-reference/Content/Job-Management/Get-Job-for-Editing.htm
- https://helpcenter.nakivo.com/api-reference/Content/Job-Management/Creating-Editing-a-Job.htm
- https://www.acronis.com/en-us/support/documentation/CyberProtectionService/retention-rules-backup-scheme.html
- https://www.acronis.com/en/support/documentation/CyberProtectionService/weekly-backup.html
- https://www.acronis.com/en/support/documentation/CyberProtectionService/schedule.html
- https://www.acronis.com/en/support/documentation/CyberProtectionService/recovering-files-web-console.html
- https://www.acronis.com/en-us/support/documentation/AcronisCyberProtect_15_Command_Line_Reference/overwrite.html
- https://www.acronis.com/en/support/documentation/AcronisCyberProtect_15/schedule.html
- https://www.acronis.com/en/support/documentation/AcronisCyberProtect_15/error-handling.html
- https://kb.acronis.com/content/59755
- https://kb.acronis.com/content/62866
- https://kb.acronis.com/content/62727
- https://care.acronis.com/s/article/65008-Acronis-Cyber-Protect-Acronis-Cyber-Backup-How-to-change-MMScurrentMachineID-and-InstanceID?language=en_US
- https://kb.acronis.com/content/71019
- https://kb.acronis.com/content/63888
- https://kb.acronis.com/content/46941
- https://care.acronis.com/s/article/Backup-Archive-Password-Protection?language=en_US
- https://care.acronis.com/s/article/68811-Acronis-Cyber-Protect-Attempting-to-access-an-encrypted-backup-archive-fails-with-The-password-for-the-protected-backup-is-incorrect?language=en_US
- https://kb.msp360.com/standalone-backup/ms-sql-server/transaction-log-not-supported-simple-recovery
- https://kb.msp360.com/standalone-backup/ms-sql-server/master-db-skipped
- https://help.msp360.com/cloudberry-backup/restore/restore-files-folders/step-6-specify-the-restore-destination
- https://help.msp360.com/cloudberry-backup/restore/restore-corrupted
- https://kb.msp360.com/managed-backup-service/errors-and-warnings/corrupted-backup
- https://help.msp360.com/cloudberry-backup-mac-linux/backup/about/mandatory-consistency-check
- https://help.msp360.com/cloudberry-backup-mac-linux/backup/about/full-consistency-check
- https://kb.msp360.com/backup/warnings/consistency-check-warnings
- https://help.msp360.com/cloudberry-backup/tools/continue-backup
- https://www.msp360.com/resources/blog/introducing-msp360-backup-5-8-with-new-restore-plan-wizards-flexible-scheduling-and-consistent-date-and-time-format/
- https://help.msp360.com/cloudberry-backup/backup/back-up-microsoft-sql-server-data/step-10-schedule-your-backup-plan
- https://help.msp360.com/cloudberry-backup/backup/file-backup/nbf
- https://www.msp360.com/resources/blog/creating-file-level-backup-plan-with-msp360-managed-backup/
- https://kb.msp360.com/standalone-backup/general/warning-one-or-more-backup-paths-do-not-exist
- https://kb.msp360.com/standalone-backup/file-level-backup/files-and-folders-were-skipped-code-1603
- https://kb.msp360.com/standalone-backup/file-level-backup/no-access2files-1622
- https://kb.msp360.com/standalone-backup/general/access-denied
- https://www.ahsay.com/en/support/help-centre/troubleshooting/obm/mssql-backup-log-is-not-allowed
- https://www.ahsay.com/en/support/help-centre/cbs/doc/v10/overview/user/backup-set
- https://www.ahsay.com/en/support/help-centre/troubleshooting/obm/troubleshooting-problem-with-missing-scheduled-backup
- https://wiki.ahsay.com/doku.php?id=public%3Adocuments_and_guides%3Av8%3Aobm%3Acloud-file-windows%3Arestore
- https://www.ahsay.com/download/customer/document/qa-tips-on-temporary-directory-setup.pdf
- https://www.ahsay.com/en/support/help-centre/doc/app/v9/restore-backup
- https://www.ahsay.com/en/support/help-centre/doc/cbs/v10/run-on-server-backup-and-restore/data-integrity-check
- https://www.ahsay.com/en/support/help-centre/acb/how-to/backup/run-data-integrity-check-in-acb-and-obm
- https://wiki.ahsay.com/doku.php?id=public:smtp
- https://www.ahsay.com/en/support/help-centre/cbs/doc/v9/administration/monitoring
- https://wiki.ahsay.com/doku.php?id=public%3A5034_best_practices_for_managing_encryption_key
- https://www.ahsay.com/en/services/encryption-key-recovery
- https://www.ahsay.com/en/support/help-centre/technical-videos/when-are-default-ahsay-dummy-self-signed-ssl-certificates-acceptable-business-use
- https://www.ahsay.com/en/support/help-centre/how-to/cbs/compatibility/trusted-certificate-authority-list-v10
- https://restic.readthedocs.io/en/stable/060_forget.html
- https://datatracker.ietf.org/doc/html/rfc6238#section-5.2
- https://cheatsheetseries.owasp.org/cheatsheets/Session_Management_Cheat_Sheet.html
- https://cheatsheetseries.owasp.org/cheatsheets/Pinning_Cheat_Sheet.html
- https://learn.microsoft.com/en-us/windows/win32/msi/servicecontrol-table
