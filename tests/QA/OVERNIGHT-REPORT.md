# Overnight QA report — 2026-10-06/07

Written during the night and updated at each check-in. Rules: NOT TESTED is never PASS; a backup counts as proven only
when its restore matches an independent SHA-256 manifest; an agent's word is not evidence.

## 1. Snapshot under test
Product snapshots of the night (each product change made a new one; tests re-run on it):
| Snapshot | Product change | What ran on it |
|---|---|---|
| `4c6deea` | start of the night | xUnit full; Windows run 8 |
| `d28b90e` | bugs 78-84 (Agent M) | xUnit full; Windows run 9 |
| `26368b0` | bugs 85-87 (Agent N) | Playwright full (old + fixed harness); Windows runs 10, 11, 12 |
| `8b95455` | bug 88 (schedule) | xUnit full ×2 (r1, r2); Playwright full ×2 (r1, r2) |
| `8bf7dc3` | bug 89 (window after a service restart) + server start diagnostics | Windows runs 13, 14; rounds P, Q |
| `35a995b` | bug 90 (customer session bound to the account) | auth/session/isolation classes (52 PASS); round S. **No full regression yet on this snapshot.** |
Branch `online-backup` (private) carries all of them; the mirror runs the Windows jobs.

## 2. Hours of QA
_(filled at the end)_

## 3–4. Tests per layer and results
| Layer | Run | Total | PASS | FAIL | SKIPPED / NOT TESTED |
|---|---|---|---|---|---|
| xUnit (component + integration), full | 4c6deea, 42 m 40 s | 351 | 345 | 4 (test-side, fixed) | 2 (76/77, owner decision) |
| xUnit, full | d28b90e, 43 m 59 s | 407 | 405 | 0 | 2 (76/77) |
| Playwright E2E (36 spec files, ~60 tests): first pass on 26368b0 with the OLD harness | 26368b0 | 17 files | 16 | 1 (F7: could not inject its fault — Q15) | – |
| Playwright E2E: every file again / the rest, with the fixed harness (Q15, Q16, L-8) | 26368b0 | 28 files | 27 files | 1 (N6b — N-1, NEEDS OWNER DECISION) | 0 |
| xUnit full | 8b95455 (bug 88), 42 m 17 s | 417 | 415 | 0 | 2 (76/77) |
| Playwright full (36 files) | 8b95455, 22:46-23:41 UTC | 36 files / 51 tests | 50 tests | 1 (N6b — N-1, NEEDS OWNER DECISION) | 0 |
| xUnit full, repeat r2 | 8b95455, 46 m 47 s | 417 | 414 | 1 (Q21: a race test read a file that exists only after a run ends — test defect, fixed 5cbc2b8) | 2 (76/77) |
| Playwright full, repeat r2 | 8b95455 (interrupted by a container restart at ~02:00, resumed from J4) | 36 files / 51 tests | 50 | 1 (N6b) | 0 |

Caveats (true for every number above):
- **65 xUnit tests start with a precondition guard** (`if (no restic / not Linux / no mount) return;`): xUnit reports them
  Passed when they return at once. Since 7c9ec45 the ledger counts a guarded test that "passed" in under 50 ms as NOT TESTED.
  On this machine (Linux, root, restic present) only a few return at once (LoadTests without OB_LOAD, the disk-full test
  without OB_ALLOW_MOUNT, "subfolder not readable" as root, the two mono/net40 tests without a net40 build).
- **Q15:** until 0767bd4 the Playwright "no half-written file left" oracle could not see the restore's temporary names (bug 78
  renamed them). Every PASS of that check before 0767bd4 (F7-F11, F15, N5, N6 in the full run) is not evidence for it;
  those scenarios are being run again with the corrected oracle.

## 5. Windows runs
| Run | Snapshot | Main phase (hosted Windows Server 2025) | VM (real restarts) |
|---|---|---|---|
| 8 (37518574168) | 4c6deea | W01 PASS, W02 PASS, W03-W17 FAIL (robot could not find the client window), W18 NOT TESTED | cancelled by the next push |
| 9 (37523219377) | d28b90e | same as run 8 | cancelled by the next push |
| 10 (37527328252) | 26368b0 | same; the new diagnostics proved UI Automation HAD the window → robot defect Q14 | cancelled (Q14 made it worthless) |
| 11 (37531433128) | 26368b0, robot Q14 | W01-W03, W16 PASS (installer chain through the UI); W04 FAIL: text fields without UI Automation names (Q18) → W05-W12, W17 not signed in; W13-W15 robot Q17 | cancelled by push |
| 12 (37533243798) | 26368b0, robot Q17/Q18 | W01-W03 PASS; **W05 PASS: sign-in, new set, backup (112 files), delete, restore through the window, SHA-256 112/112**; W04/W05 step FAILs robot Q19; W06-W09 FAIL: after a SERVICE RESTART the open window refused every action → **product bug 89**; main job hit its 3 h limit | cancelled by push |
| 13 (37552591713) | 8bf7dc3 (bug 89 fixed) | W01-W05 PASS; W06: backup after the service restart PASS (bug 89 fixed on real Windows); restore after the restart FAIL — robot typed no password into the sign-in question (Q20); then the nightly schedule cancelled the run (qa.yml, fixed) | cancelled |
| 14 (37557981704) | 8bf7dc3, robot Q20 | _running_ | _running_ |

**Q14 (robot, not product):** the robot's window matchers were written with `$_` but called with an empty `$_`, so the
client's window and the removal's Yes/No question were never matched. Runs 8-10 therefore measured nothing about the client
(W03-W17): those FAILs are NOT TESTED for the product, not product failures.

## 6. C1 (client lifecycle on one Windows, one build, one run): **NOT VERIFIED** (so far)
Proven on real Windows in one run so far: install through the installer window (W03), sign-in (W04), new set, backup,
delete, restore through the window, SHA-256 112/112 (W05), backup after a service restart (W06, run 13). Not yet in one
run: restore after the service restart, the real reboot (VM phase), backup/restore after it, uninstall. See run 14.
## 7. C2 (crash / recovery): partly
Linux end-to-end (real processes): F1 server killed mid-backup, F9 server killed mid-restore, J5 agent killed mid-backup,
F4/F7 restore killed, N10 hung external process, F2/F10/N1 network cut — all PASS on 8b95455 twice, each with restore + SHA-256.
Unplanned: the container was killed THREE times tonight with the soak's server and agent running; the soak resumed each time
and backups/restores continued identical — but the server's first start after the first kill failed once ("Value cannot be
null."), not reproduced in 6 kill-restart cycles (Medium, open; diagnostics added).
Windows crash journeys (W07-W09: agent killed, service stopped, server killed mid-backup) have not yet run with a working
robot: NOT TESTED on Windows.
## 8. Consecutive passing runs
Snapshot 8b95455: two consecutive full runs (xUnit + Playwright). Both have the same single product FAIL (N6b, owner decision);
r2 also had one test-defect FAIL (Q21). Strictly: 0 clean consecutive runs; 2 consecutive runs with identical product results.
Not repeated on 35a995b yet.

## 9–12. New bugs, severity, fixes, regression test each
Product bugs fixed tonight (full rows in docs/R1-BUGLOG.md, each with root cause and its regression test): 73-75, 78-90
(16). Reverted pending owner decision: 76, 77. The latest:
- 88 (Medium) a schedule slot after a successful run waited up to 15 minutes — found by the soak.
- 89 (High) after the backup service restarted, the open window/tray icon refused every action until closed — found on real Windows.
- 90 (High, security) a deleted customer's sign-in opened a new account given the same name (another reseller's) — round R.

QA-system defects found tonight (each one could make a test PASS without testing, or fail without the product failing):
| Id | Severity | What | Fixed in | Proof |
|---|---|---|---|---|
| L-1 | High | 65 guarded xUnit tests counted PASS when they returned at once | 7c9ec45 | counting_proof case 1 now NOT PROVEN (UI-09 integration = NONE) |
| L-2 | High | one Playwright file with several tests: the last result won | 7c9ec45 | counting_proof case 2 → Failed |
| L-3 | High | RestoreInterruptedMidway never killed restic (staged restore) | 5eee5ac | new asserts: kill hit a live restore; PASS 11 s |
| L-4 | Medium | an environment variable could mark any test Passed | 7c9ec45 | removed; case 3 not proven |
| L-5/L-6 | Low | prefix credit f1→f10-19; 16 test ids in the wrong class | 7c9ec45 | ledger refuses wrong ids |
| M20 | Medium | upload stored-copy check protected by no test | 8ffcd50 | 2 tests; both FAIL with the check removed |
| M15 | Medium | restore temp-name test used the old name | 59ea986 | FAILS with a fixed temp name |
| Q14 | Critical (for Windows evidence) | robot never matched the client window / Yes-No dialog | 5d651c3 | self-test, run 11 |
| Q15 | High | leftover oracle blind to the restore's temp names since bug 78; F7 could not inject its fault | 0767bd4 | regex proven on 6 names; F7 etc. re-run |
| lint | Low | robot lint did not refuse non-ASCII (PS 5.1) | ccec504 | probe file → exit 1 |
| Q16 | High | F13's agent ran under faketime, which does not pass SIGTERM on: earlier phases' agents kept running beside later ones | 1622e2d | F13 asserts each phase's agent is gone |
| Q17 | High (Windows evidence) | `$s` local hid the script's `$S` (case-insensitive): install folder never kept | 74c5ae9 | lint rule (11 hits in old script) |
| Q18 | High (Windows evidence) | text fields without UI Automation names: robot could not sign in | 74c5ae9 | self-test; accessibility note A-1 |
| Q19 | High (Windows evidence) | a check ran inside Step, whose own `$t`/`$ok` hid the caller's | eeb5858 | lint (21 hits), self-test |
| Q20 | Medium | sign-in question answered with an empty password; nightly schedule cancelled a run | 8bf7dc3 | run 14 |
| Q21 | Low | race test read a file that exists only after a run ends | 5cbc2b8 | 5/5 |
| CI | Low | N7 (setpriv) and N9b (AggregateError) on the non-root runner | 39ed5e5 | CI run 14: both PASS |

## 13. Open
- C1 not verified (Windows run 14 running); Windows crash journeys W07-W09, real reboot (VM), uninstall: NOT TESTED.
- A-1 accessibility: the client's text fields have no name for screen readers although the code sets one (Windows only).
- Server's first start after the container kill failed once ("Value cannot be null."), not reproduced (Medium).
- No full regression on the latest snapshot 35a995b (bug 90); only the affected classes (52 PASS).
- Rounds P, Q, S running; their areas stay NOT TESTED until they report and the evidence is checked.
- Hosted CI runner: root/mount scenarios skip there (the new skipped==0 gate makes that job red, truthfully).
## 14. NEEDS OWNER DECISION
- **New (round R):** should an administrator's password change end that administrator's other sign-ins (common practice: yes)?
- Bug 76 — SQL log mode, SIMPLE-recovery database with "all databases": skip / warn / fail. Fix written and reverted;
  current behaviour: every log run ends "completed with errors". Benchmark: docs/PRODUCT-BENCHMARK.md.
- Bug 77 — GFS (daily/weekly/monthly) boundaries: server time zone or UTC. Fix written and reverted; current: UTC.
- Decisions taken earlier tonight, before the owner's benchmark rule, kept in the product and listed for review
  (see the benchmark document): 39/48 restore overwrite + staged restore, 50 a restore of nothing is an error,
  55 index rebuilt when it is not the server's last run, 56 a damaged version fails its file's restore,
  66 a set made by another registration without its key here is left to that computer (no failed run),
  67 the schedule on the computer's own clock, 72 the restore test only after a successful backup,
  75 a bare-metal run without a new image fails, 70 a time with no day is refused.

- **New tonight (benchmark, Q6):** a backup stopped by the user midway is committed as a normal restore point and is
  counted by retention — with "keep the last N backups", stopped (partial) runs can push complete restore points out.
  Veeam Agent makes no restore point in that case. Choose: keep it marked "partial" and outside the job count, or discard it.
- **Benchmark (docs/PRODUCT-BENCHMARK.md, 18 questions):** 17 marked NEEDS OWNER DECISION, 1 AUTO-APPROVED (Q16: session
  ends with the admin; a two-step code used once — RFC 6238 §5.2). Important caveat: vendor pages could not be opened
  from this environment (network policy); every competitor claim is a paraphrase of a search excerpt with the official
  URL, to be opened once before approving.

- **N-1 (Agent N):** the server committed a run (BS_STOP_SUCCESS, a point), then the computer could not save its local
  index (disk full): the computer says BS_STOP_BY_SYSTEM_ERROR, the server says success, the customer sees "failed".
  The next run rebuilds the index and is correct. Which should the run be: success-with-warning on both, or failed?
- **N-4 part 2:** when the server cannot even begin a run (its storage read-only), the run is not in the server's history
  at all — record it as failed / alert, or not?
- **N-5:** with the native engine, pre-commands run before the run is visible on the server (up to the 1-hour limit,
  nothing on the live page); the restic engine shows it first. And a pre-command keeps running when the agent is killed.
- **M-3:** on a spring-forward night a slot inside the gap now runs at the first minute after the gap (03:00 for 02:30) —
  the exact minute is the owner's to confirm (before: it never ran with "run missed" off).
- **M (observations):** the log-backup interval counts from the end of the previous log run (drifts by its length);
  the 30-day restore-test mark is written before the test runs (a test that throws waits 30 days).

## 15. Soak
**8.02 hours** (2026-10-06 19:31 → 2026-10-07 03:32 UTC), one real server, a native set under the agent SERVICE with a slot every
5 minutes (SN), a native set (MN) and a restic set (MR) backed up by the agent program, changes between every backup
(creates, deletes, renames, mid-file overwrites, truncations, Hebrew/space names, a 31 MB file edited in place).
Product build of the soak: the night's FIRST snapshot (4c6deea) — later fixes (78-92) were not in it.
- Backups: **312, all BS_STOP_SUCCESS**, 0 failed runs, 0 stuck runs (server records, every minute).
- Restores: **127, all identical** to the SHA-256 manifest taken at that backup (79 newest, 27 older points, 21 final from ≥5 points per set).
- Verify: 51 rounds, 0 damaged — but the restic set's verify checks 0 objects (server verify does not apply to restic): NOT TESTED for restic.
- Resources (479 samples): server RSS 202 MB mean, no growth (slope −11.9 MB/h, r −0.66 — falling); server handles 98-131
  (slope −0.7/h); threads flat 15-22; service RSS mean 161 MB (slope +0.8 MB/h, r 0.05 — no trend); service handles flat ~105;
  one agent process at a time, no leftovers. Storage grew 73 MB/h, linear with the data churn (r 0.96).
- Schedule: 85 slots, every one got a run; only 19 on time (<2 min), the rest 3-16 min late — **bug 88** (found here, fixed
  after the soak started, so the soak itself still shows it).
- **Unplanned: the container was killed three times (≈00:40, ≈02:00 and with the soak's processes) — server and agent died
  without warning; the soak resumed each time, every later backup and restore correct.** After the first kill the server's
  first start failed once ("Value cannot be null."), the next start worked — unexplained, not reproduced (6 kill-restarts).
- LOW, all night: the restic set's run counts never report deletions (del=0 while files were deleted) — the restore itself is right.
## 16. Failure scenarios run
Every one with the fault proven injected, then recovery, backup, restore and SHA-256 (Linux, real processes), PASS on 8b95455
twice (r1, r2): server killed mid-backup (F1) and mid-restore (F9); network cut mid-backup (F2, N1 ×2) and mid-restore (F10);
two backups at once (F3); restore killed (F4) and cut mid-file (F7); source gone (F5, N8 ×2); server disk full (F6, N5);
client disk full during restore (F8, N6a); chunk lists torn (F11); same computer name (F12); computer clock 3 h ahead (F13);
cloned computer (F14); index lines lost (F15); commit reply lost / duplicated (N2 ×2); slow line (N3); CPU and memory
pressure (N4); permission denied and locked file (N7); repository unavailable (N9 ×2); external process hang (N10 ×3);
agent killed (J5); and round Q's damaged stored object (Q2), lost System Home (Q3), second server killed (Q4a), quota (Q6).
FAIL: N6b (the agent's folder full after the server committed — NEEDS OWNER DECISION N-1).
Windows: W07-W09 (crash journeys) NOT TESTED yet with a working robot.
## 17. Restore / SHA-256 evidence
- Linux E2E: every journey and failure scenario above ends in a restore compared with an independent manifest (SHA-256 per file).
- Soak: 127 restores identical. Round P: every point of 46-run chains, 31 restic snapshots, retention with gaps — all identical.
- Windows (real Windows Server 2025, through the program's window): run 12 and 13 W05 — 112/112 files identical after delete
  and restore. Evidence: qa-evidence branch runs/37533243798 and runs/37552591713 (journey.json, screenshots, manifests).
## 18. Critical capabilities FULLY VERIFIED (of 49)
## 19. Benchmark decisions
- AUTO-APPROVED: Q16 only. Everything else waits for the owner (table at the top of docs/PRODUCT-BENCHMARK.md).

## Additional QA rounds (owner's request at 02:35: use idle capacity on NOT TESTED / partial areas and challenge PASSes)
Each round is separate, in its own worktree on snapshot 8bf7dc3, results below as they arrive (NOT TESTED until reported).
| Round | Scope | Status | Findings |
|---|---|---|---|
| P | disprove restore PASSes: long delta chains at every point, point selection, chunk edges, metadata, retention interplay | done (tests/QA/night/p) | 15 cases, 13 PASS — every point of 46-run chains (incremental/differential, short/long MAX_DELTA, shrink to 0), names reused, chunk edges, retention with gaps, restic 31 snapshots: all restore identical (each mutation-proofed). **P-2 High → bug 91 fixed** (differential→incremental→differential made 'successful' unrestorable points). P-1 Low: case-only rename not backed up — NEEDS OWNER DECISION. Not claimed, noted: read-only/hidden attributes not restored. NOT TESTED: restic via the server's store, local-copy and web restore |
| Q | no-E2E capabilities: replication, recycle bin, settings backup, verify/quarantine, site→agent commands, quota | done (tests/QA/night/q, tests/QA/e2e/q1-q6) | NEW E2E PASS: recycle bin (Q1), verify+quarantine+resend (Q2), settings backup and full recovery of a lost System Home (Q3), replication to a real second server incl. kill/behind/back (Q4a), schedule, upload limit and Stop from the site obeyed by the running service (Q5), quota (Q6) — each with restore + SHA-256 and shown able to fail. **Q-F1 High → bug 92 fixed** (one customer unknown to the second server stopped replication for EVERY customer forever; Q4b FAIL without the fix, PASS with it). NEEDS OWNER DECISION: Q-D1 a computer registered on the second server is dropped by the next copy; Q-D2 settings backup lacks run history/logs; Q-D3 switching replication on does not copy existing backups; Q-D4 the second server cannot be configured as receiver from its site. Risk (Windows, untested): DPAPI-protected secrets in a settings backup restored on another machine |
| R | security: tenant/vendor isolation per route, IDOR, path inputs, sessions, agent local API, Guard evasion | done (route table in tests/QA/night/r) | **R-01 High → bug 90 fixed** (a deleted customer's session opened a NEW customer of another reseller with the same name). Lead: admin password change keeps sessions — NEEDS OWNER DECISION. Checked safe: Guard ignores X-Forwarded-For; agent local API (loopback + key + host check); static files. NOT TESTED: header injection via the web-restore point name, restic object-name fuzzing |
| S | mutation testing of tonight's product fixes (bugs 78-90); R's NOT TESTED items | done (tests/QA/night/s) | 12/12 mutants KILLED (85, 86 only by 2 new xUnit tests — their own regression was Playwright-only); orchestrator re-checked bug 90's mutant: KILLED, src clean. Web-restore header injection: not possible (point validated + HttpListener refuses CR/LF); restic object routes: no read/write outside the repository (11 hostile paths, sentinel intact). No product defect. NOT TESTED: bug 80's mutant; 85/86 against their Playwright test |

## 20. Release verdict
