# Overnight QA report — 2026-10-06/07

Written during the night and updated at each check-in. Rules: NOT TESTED is never PASS; a backup counts as proven only
when its restore matches an independent SHA-256 manifest; an agent's word is not evidence.

## 1. Snapshot under test
- Commit `4c6deea` (branch `online-backup`; public mirror commit `2014d2b`). Every heavy run tonight is on this commit
  unless a later snapshot is named below (with the reason and what was re-run).

## 2. Hours of QA
_(filled at the end)_

## 3–4. Tests per layer and results
_(filled as runs finish)_

## 5. Windows runs
_(run ids, phases, results)_

## 6. C1 (client lifecycle on one Windows, one build, one run): _pending_
## 7. C2 (crash / recovery): _pending_
## 8. Consecutive passing runs: _pending_

## 9–12. New bugs, severity, fixes, regression test each
_(see also docs/R1-BUGLOG.md)_

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
