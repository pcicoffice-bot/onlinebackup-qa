# Overnight QA report — 2026-10-06/07

Written during the night and updated at each check-in. Rules: NOT TESTED is never PASS; a backup counts as proven only
when its restore matches an independent SHA-256 manifest; an agent's word is not evidence.

## 1. Snapshot under test
- First snapshot `4c6deea` (mirror `2014d2b`). Product fixes during the night made two more product snapshots:
  `d28b90e` (bugs 78-84, Agent M) and `26368b0` (bugs 85-87, Agent N). **The product code (src/) has not changed since
  `26368b0`**; later commits (up to `0767bd4`) change only tests, the QA counting and the Windows robot.
- What ran on which snapshot: xUnit full on 4c6deea and d28b90e; Playwright full on 26368b0; Windows runs 8 (4c6deea),
  9 (d28b90e), 10 (26368b0), 11 (product = 26368b0, robot Q14 fix).

## 2. Hours of QA
_(filled at the end)_

## 3–4. Tests per layer and results
| Layer | Run | Total | PASS | FAIL | SKIPPED / NOT TESTED |
|---|---|---|---|---|---|
| xUnit (component + integration), full | 4c6deea, 42 m 40 s | 351 | 345 | 4 (test-side, fixed) | 2 (76/77, owner decision) |
| xUnit, full | d28b90e, 43 m 59 s | 407 | 405 | 0 | 2 (76/77) |
| Playwright E2E, full (journeys + failure/recovery) | 26368b0 | _running_ | | | |

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
| 11 (37531433128) | product 26368b0, robot Q14 fixed | _running_ | _running_ |

**Q14 (robot, not product):** the robot's window matchers were written with `$_` but called with an empty `$_`, so the
client's window and the removal's Yes/No question were never matched. Runs 8-10 therefore measured nothing about the client
(W03-W17): those FAILs are NOT TESTED for the product, not product failures.

## 6. C1 (client lifecycle on one Windows, one build, one run): _pending_
## 7. C2 (crash / recovery): _pending_
## 8. Consecutive passing runs: _pending_

## 9–12. New bugs, severity, fixes, regression test each
Product bugs fixed tonight (full rows in docs/R1-BUGLOG.md): 73-75, 78-87 (13) — see the table there for severity, root cause
and the regression test of each. Reverted pending owner decision: 76, 77.

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

## 13. Open
## 14. NEEDS OWNER DECISION
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
## 16. Failure scenarios run
## 17. Restore / SHA-256 evidence
## 18. Critical capabilities FULLY VERIFIED (of 49)
## 19. Benchmark decisions
- AUTO-APPROVED: Q16 only. Everything else waits for the owner (table at the top of docs/PRODUCT-BENCHMARK.md).
## 20. Release verdict
