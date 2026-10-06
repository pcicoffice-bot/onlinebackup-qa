# Verification report — numbers from the last runs (`tests/QA/report.py`)

## 1. Capabilities

| | Total | Fully verified (3 layers) | Partial | Not tested | Failing |
|---|---|---|---|---|---|
| All capabilities | 75 | 5 | 54 | 2 | 14 |
| Critical | 49 | 5 | 29 | 1 | 14 |

| Layer (all / critical) | Verified | Partial | None | Failing |
|---|---|---|---|---|
| component | 12 / 11 | 14 / 11 | 49 / 27 | 0 / 0 |
| integration | 36 / 27 | 27 / 12 | 12 / 10 | 0 / 0 |
| e2e | 14 / 14 | 1 / 1 | 46 / 20 | 14 / 14 |

Source files: 90, every one mapped to a capability (hidden areas: 3).

Fully verified critical capabilities: BK-03 File backup, restic engine, RS-03 Restore, restic engine, ST-01 Run commit (journal, roll forward), truncated/half objects refused, AG-01 Scheduler: times, days, several a day, missed runs (computer off, offline), AU-01 Administrator sign-in: password, mandatory two-step, lock, sessions, sign-out

## 2. Tests by type (last run)

| Type | Tests | Failed |
|---|---|---|
| Component (xUnit, no server) | 76 | 0 |
| Integration (xUnit, real server + HTTP + files) | 156 | 0 |
| End-to-end (Playwright: separate server and agent processes, browser, restore + SHA-256) | 16 | 0 |
| Windows (installer robot, Windows E2E) — written, NOT RUN | 4 scripts | — |

## 3. Failure scenarios

- Tests in the failure / recovery dimensions of the matrix: **107**
- Fault-injection runs on the real programs (E2E): **9** — failure-recovery/f1-server-crash, failure-recovery/f2-network-cut, failure-recovery/f3-two-at-once, failure-recovery/f4-restore-interrupted, failure-recovery/f5-source-gone-ui, failure-recovery/f6-server-disk-full, failure-recovery/f7-restore-cut-mid-file, journeys/j5-kill-agent, journeys/j7-schedule

## 4. Bugs found and fixed (each with a regression test that failed before the fix)

| Severity | Count | Bugs |
|---|---|---|
| Critical | 19 | 79, 2, 3, 4, 6, 14, 15, 17, 24, 28, 29, 35, 39, 41, 42, 48, 54, 55, 56 |
| High | 26 | 84, 83, 1, 5, 9, 10, 18, 20, 22, 27, 32, 33, 73, 74, 75, 36, 38, 40, 44, 46, 47, 49, 57, 59, 61, 66 |
| Medium | 33 | 85, 87, 82, 80, 76, 77, 11, 12, 13, 16, 19, 21, 23, 25, 26, 30, 31, 34, 37, 43, 45, 50, 51, 53, 58, 60, 62, 65, 67, 68, 69, 70, 71 |
| Low | 9 | 86, 81, 78, 7, 8, 52, 63, 64, 72 |
| **Total** | **87** | |

Static review: 325 hits — OK 320, FIXED 4 (+5 fixes recorded by class), BUG open 1, not classified 0.

## 5. Risk areas (critical capabilities not fully verified, with what is missing)

| Capability | Component | Integration | End-to-end | Missing |
|---|---|---|---|---|
| BK-01 File backup, native engine (first, incremental: new / changed / deleted / permissions) | PARTIAL | VERIFIED | FAILING | component: happy, recovery, integrity, failure/boundary; e2e:  |
| BK-02 Delta chains (incremental / differential), long chain → new full | PARTIAL | PARTIAL | NONE | component: happy, recovery, integrity; integration: failure/recovery; e2e: a real user run, restore + SHA-256 |
| BK-04 Filters, skipped folders, links | PARTIAL | VERIFIED | NONE | component: integrity, failure/boundary; e2e: a real user run, restore + SHA-256 |
| BK-05 Unreadable data is an error (permission denied, locked file, folder gone) | NONE | VERIFIED | FAILING | component: happy, recovery, integrity, failure/boundary; e2e:  |
| BK-06 Volume Shadow Copy (open files) | NONE | NONE | FAILING | component: happy, recovery, integrity, failure/boundary; integration: happy, failure/recovery; e2e: a real user run, restore + SHA-256 |
| DB-01 SQL Server: full, differential, log; free-space check | NONE | VERIFIED | NONE | component: happy, recovery, integrity, failure/boundary; e2e: a real user run, restore + SHA-256 |
| DB-02 MySQL / PostgreSQL dumps | NONE | VERIFIED | NONE | component: happy, recovery, integrity, failure/boundary; e2e: a real user run, restore + SHA-256 |
| AP-01 System State (wbadmin / ntbackup) | NONE | NONE | FAILING | component: happy, recovery, integrity, failure/boundary; integration: happy, failure/recovery; e2e: a real user run, restore + SHA-256 |
| AP-02 Bare-metal image | NONE | VERIFIED | NONE | component: happy, recovery, integrity, failure/boundary; e2e: a real user run, restore + SHA-256 |
| AP-07 External programs: time limit, both streams, process tree | PARTIAL | PARTIAL | NONE | component: happy, recovery; integration: happy; e2e: a real user run, restore + SHA-256 |
| RS-01 Restore all / a folder / a file, native, any point | VERIFIED | VERIFIED | FAILING | e2e:  |
| RS-02 Restore to the original place: overwrite rules, existing files | VERIFIED | NONE | VERIFIED | integration: happy, failure/recovery |
| RS-04 Restore from the website (download) | NONE | PARTIAL | NONE | component: happy, recovery, integrity, failure/boundary; integration: failure/recovery; e2e: a real user run, restore + SHA-256 |
| RS-05 Restore on a new computer (key recovery, local index rebuilt) | NONE | VERIFIED | NONE | component: happy, recovery, integrity, failure/boundary; e2e: a real user run, restore + SHA-256 |
| ST-02 Run lease, interrupted runs closed and recorded | VERIFIED | VERIFIED | FAILING | e2e:  |
| ST-03 Retention (days / jobs / GFS), never the current version | PARTIAL | VERIFIED | VERIFIED | component: recovery, integrity |
| ST-04 Verify, damaged object quarantined and resent; index rebuild | NONE | VERIFIED | NONE | component: happy, recovery, integrity, failure/boundary; e2e: a real user run, restore + SHA-256 |
| ST-05 Quota (compressed / original), stop new backups, keep existing | NONE | VERIFIED | NONE | component: happy, recovery, integrity, failure/boundary; e2e: a real user run, restore + SHA-256 |
| ST-06 Server disk full | NONE | NONE | VERIFIED | component: recovery, integrity, failure/boundary; integration: failure/recovery |
| ST-08 Earlier versions of the storage open as they are (upgrade with existing data) | NONE | PARTIAL | NONE | component: happy, recovery, integrity, failure/boundary; integration: failure/recovery; e2e: a real user run, restore + SHA-256 |
| AG-02 "Back up now" and "Stop" from the server reach the computer | NONE | PARTIAL | VERIFIED | component: happy, recovery, integrity, failure/boundary; integration: failure/recovery |
| AG-03 Settings changed on the server reach the computer | NONE | PARTIAL | VERIFIED | component: happy, failure/boundary; integration: failure/recovery |
| AG-04 Heartbeat, open-run note, report of a dead run | NONE | VERIFIED | FAILING | component: happy, recovery, integrity, failure/boundary; e2e:  |
| AG-05 Network: server unreachable, line cut, reconnect | PARTIAL | VERIFIED | FAILING | component: integrity; e2e:  |
| AG-06 Local state (chunk index, keys) — lost, damaged | PARTIAL | VERIFIED | NONE | component: happy, recovery, integrity, failure/boundary; e2e: a real user run, restore + SHA-256 |
| AG-07 TLS: built-in TLS 1.2 for old Windows, certificate pin | NONE | VERIFIED | NONE | component: happy, integrity, failure/boundary; e2e: a real user run |
| AU-02 Customer sign-in, register a computer, device token | VERIFIED | VERIFIED | FAILING | e2e: a real user run |
| AU-05 Encryption: keys (password / random / custom), check value, tamper detection | PARTIAL | PARTIAL | NONE | component: recovery; integration: happy; e2e: a real user run, restore + SHA-256 |
| AU-07 API refuses junk and attacks clearly | NONE | VERIFIED | NONE | component: failure/boundary; e2e: a real user run |
| IN-01 Server installation (wizard / script), Windows service, certificate, port | PARTIAL | NONE | PARTIAL | component: recovery; integration: happy, failure/recovery; e2e: restore + SHA-256 |
| IN-02 Client installation (Setup.exe wizard), service, uninstall, reinstall | NONE | PARTIAL | FAILING | component: happy, recovery, integrity, failure/boundary; integration: failure/recovery; e2e: a real user run, restore + SHA-256 |
| IN-03 Client update (all or nothing) | PARTIAL | PARTIAL | FAILING | component: integrity; integration: failure/recovery; e2e: a real user run, restore + SHA-256 |
| IN-04 Server update (signed, SHA-256, from files) | NONE | VERIFIED | NONE | component: happy, recovery, integrity, failure/boundary; e2e: a real user run, restore + SHA-256 |
| IN-06 Reboot of the computer: service back, runs go on | NONE | NONE | NONE | component: happy, recovery, integrity, failure/boundary; integration: happy, failure/recovery; e2e: a real user run, restore + SHA-256 |
| SH-01 Last backup vs last result; history of every run; tasks page | NONE | VERIFIED | VERIFIED | component: happy, recovery, failure/boundary |
| SH-02 Running now (live list) — no ghost | NONE | VERIFIED | FAILING | component: happy, recovery, failure/boundary; e2e:  |
| SH-03 Mails: run report, failure, missed backup, quota, disk full | VERIFIED | VERIFIED | NONE | e2e: a real user run |
| SH-05 Ransomware suspicion: retention frozen, alert | PARTIAL | PARTIAL | NONE | component: happy, recovery, integrity; integration: failure/recovery; e2e: a real user run |
| UI-01 Admin site: every page opens, no errors; sign-in, reload, sign-out | NONE | PARTIAL | VERIFIED | component: happy, recovery, failure/boundary; integration: happy, failure/recovery |
| UI-02 Admin site: set editor (every tab saved and read back) | NONE | NONE | VERIFIED | component: happy, failure/boundary; integration: happy, failure/recovery |
| UI-03 Admin site: the truth after failures (red, failed, not running) | NONE | NONE | VERIFIED | component: recovery, failure/boundary; integration: failure/recovery |
| UI-04 Client window (every page, typing kept) | NONE | NONE | FAILING | component: happy, recovery, failure/boundary; integration: happy, failure/recovery; e2e: a real user run |
| UI-05 Client installation wizard (Welcome → License → Install → Finish) | NONE | NONE | FAILING | component: happy, recovery, failure/boundary; integration: happy, failure/recovery; e2e: a real user run |
| CO-01 Shared formats: messages, profile, log lines, run ids, atomic file writes | VERIFIED | PARTIAL | NONE | integration: happy; e2e: a real user run, restore + SHA-256 |

## 6. Verdict

**NOT READY.** Ready requires every critical capability fully verified in all three layers; 5 of 49 are. Failing tests in the last run: 0. The Windows layer (installer, service, VSS, SQL Server, reboot) has not run at all.
