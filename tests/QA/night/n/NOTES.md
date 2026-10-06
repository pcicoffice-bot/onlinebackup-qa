# Agent N — failure matrix (night run, snapshot 4c6deea)

Specs: `tests/QA/failure-recovery/n1..n10-*.spec.ts`; helpers `lib/nfault.ts` (additive) and `runner/nproxy.mjs`
(a fault-injecting TCP proxy with its own JSON log: cut / back / throttle / drop the commit reply / duplicate the commit).
Evidence of every run: `night/n/evidence/*.json` (the proof the fault was injected, outputs, server records, durations);
Playwright output of each run: `night/n/runs/*.log`. Built once (Debug, as `lib/world.ts` uses), run with
`QA_NO_BUILD=1 OB_RESTIC=<scratchpad>/restic flock <scratchpad>/pw.lock npx playwright test <spec> --workers=1 --reporter=line`.

| Spec | Fault (proof from outside) | Result |
|---|---|---|
| n1a | line cut 5 s mid-upload (proxy: 2 sockets destroyed, 1 connection refused) | PASS — run resumed, BS_STOP_SUCCESS_WITH_WARNING, restores identical |
| n1b | line down until the agent gives up (proxy: 17 refused tries) | PASS — gave up after 44 s, BS_STOP_BY_SYSTEM_ERROR, recorded bad once, next run ok |
| n2a | commit reply dropped after the server committed (proxy: `HTTP/1.1 200` swallowed, connection closed; retry seen) | PASS — one record, two points, run 3 sends nothing |
| n2b | commit sent twice at once (proxy: copy answered 200 `repeat=1`) | PASS — one record, deletions counted once |
| n3 | 50 KB/s line, 9.6 MB through it (190 s, ≥ bytes/rate) | PASS — success, both points identical |
| n4 | 4 busy loops (state R, ticks) / 2.1 GB held (VmRSS, 40 % of MemFree) | PASS — baseline 2.5 s, CPU 24.1 s, memory 3.3 s; all identical |
| n5 | server storage full in backup 2 (df 96 %, 3 MB free) | PASS — failed with "disk is full", point 1 restorable DURING the fault |
| n6a | agent temp folder full (df 100 %, dd ENOSPC) | FAIL (soft) — N-3: 0-byte .obj left; N-2: ~10 s per file before failing |
| n6b | agent set folder full after commit (12 KB free; state.txt ENOSPC) | FAIL (soft) — N-1: agent says failed, server says success |
| n7 | chmod 000 file + folder (setpriv without DAC override: Permission denied), flock -x (HELD) | PASS — SUCCESS_WITH_ERROR naming all three, old versions kept |
| n8a | source renamed away mid-run (stat ENOENT, run alive) | PASS — SUCCESS_WITH_ERROR, del=0, every file in the newest point |
| n8b | source gone before run | PASS — BS_STOP_BY_SYSTEM_ERROR, del=0, next run sends only the new file |
| n9a | server set folder read-only bind mount (findmnt ro, touch EROFS) | FAIL (soft) — N-4: points/restore of the existing point impossible (500, SQLite 14) |
| n9b | server stopped (process gone, ECONNREFUSED) | PASS — failed in 10 s, no ghost, next run ok |
| n10a | pre-command sleeps for ever; agent clock 120× (faketime) → the real 1-h limit | PASS — stopped at "1 h" after 35 s real, child gone, WITH_WARNING |
| n10b | agent killed during a hung pre-command (observation) | PASS (observes N-5: the hung command is left running, PPID 1; server shows nothing for 70 s) |
| n10c | stand-in restic backup sleeps silently; agent clock 120× → 30-min idle limit | PASS — stopped after 18 s real, BS_STOP_BY_SYSTEM_ERROR, recorded bad |

First runs that failed because of the TEST (fixed, not product findings): N7 v1 locked an unchanged file (never
opened) and killed only `flock`, not the `sleep` holding the lock; N8a v1 renamed the folder while the last folder
of the walk was being sent (everything had been read — the BS_STOP_SUCCESS was truthful); N6a v1 restored all 170 files
through the full temp folder and was still running after 12 min (kept as evidence of N-2: evidence/n6a-first-attempt.txt).

NOT TESTED: a Windows sharing lock (different mechanism from a Linux flock / permission bits); the real 1-hour
pre-command limit and 30-min restic idle limit in real time (> 10 min: tested with the agent's clock sped up instead);
memory pressure up to OOM (the rule: never more than 50 % of free memory).
