# Pilot 1 blockers — 57 (51 capability + 6 cross-cutting)

The target stays 57 (owner, 2026-10-08). Capability state: last computed from CI evidence (night/blockers-now.txt, 03:01 UTC,
Linux run 20 + Windows run 25 + pw-05); a capability is FULLY VERIFIED only when component, integration and end-to-end are
VERIFIED by tests that ran and passed. E2E 'FAILING' rows come from old local Windows-robot results (UNKNOWN until re-run).

| ID | Component | Integration | E2E | State |
|---|---|---|---|---|
| AG-01 | VERIFIED | VERIFIED | VERIFIED | FULLY VERIFIED |
| AG-02 | VERIFIED | VERIFIED | VERIFIED | FULLY VERIFIED |
| AG-03 | VERIFIED | VERIFIED | VERIFIED | FULLY VERIFIED |
| AG-04 | VERIFIED | VERIFIED | FAILING | PARTIAL |
| AG-05 | VERIFIED | VERIFIED | FAILING | PARTIAL |
| AG-06 | VERIFIED | VERIFIED | VERIFIED | FULLY VERIFIED |
| AG-07 | VERIFIED | VERIFIED | NONE | PARTIAL |
| AP-07 | PARTIAL | VERIFIED | NONE | PARTIAL |
| AU-01 | VERIFIED | VERIFIED | VERIFIED | FULLY VERIFIED |
| AU-02 | VERIFIED | VERIFIED | FAILING | PARTIAL |
| AU-04 | VERIFIED | VERIFIED | NONE | PARTIAL |
| AU-05 | VERIFIED | VERIFIED | NONE | PARTIAL |
| AU-06 | VERIFIED | VERIFIED | VERIFIED | FULLY VERIFIED |
| AU-07 | VERIFIED | VERIFIED | NONE | PARTIAL |
| BK-01 | VERIFIED | VERIFIED | FAILING | PARTIAL |
| BK-02 | VERIFIED | VERIFIED | NONE | PARTIAL |
| BK-04 | VERIFIED | PARTIAL | VERIFIED | PARTIAL |
| BK-05 | VERIFIED | PARTIAL | FAILING | PARTIAL |
| BK-06 | PARTIAL | NONE | FAILING | PARTIAL |
| BK-07 | VERIFIED | VERIFIED | VERIFIED | FULLY VERIFIED |
| BK-08 | VERIFIED | VERIFIED | NONE | PARTIAL |
| CO-01 | VERIFIED | VERIFIED | NONE | PARTIAL |
| IN-01 | VERIFIED | VERIFIED | PARTIAL | PARTIAL |
| IN-02 | VERIFIED | VERIFIED | FAILING | PARTIAL |
| IN-03 | VERIFIED | VERIFIED | FAILING | PARTIAL |
| IN-04 | VERIFIED | VERIFIED | NONE | PARTIAL |
| IN-05 | VERIFIED | VERIFIED | NONE | PARTIAL |
| IN-06 | VERIFIED | VERIFIED | NONE | PARTIAL |
| RS-01 | VERIFIED | VERIFIED | FAILING | PARTIAL |
| RS-02 | VERIFIED | VERIFIED | VERIFIED | FULLY VERIFIED |
| RS-05 | VERIFIED | VERIFIED | VERIFIED | FULLY VERIFIED |
| SH-01 | VERIFIED | VERIFIED | VERIFIED | FULLY VERIFIED |
| SH-02 | VERIFIED | VERIFIED | FAILING | PARTIAL |
| SH-03 | VERIFIED | VERIFIED | NONE | PARTIAL |
| SH-05 | VERIFIED | VERIFIED | VERIFIED | FULLY VERIFIED |
| SH-07 | VERIFIED | VERIFIED | NONE | PARTIAL |
| ST-01 | VERIFIED | VERIFIED | VERIFIED | FULLY VERIFIED |
| ST-02 | VERIFIED | VERIFIED | FAILING | PARTIAL |
| ST-03 | VERIFIED | VERIFIED | VERIFIED | FULLY VERIFIED |
| ST-04 | VERIFIED | VERIFIED | VERIFIED | FULLY VERIFIED |
| ST-05 | VERIFIED | VERIFIED | VERIFIED | FULLY VERIFIED |
| ST-06 | VERIFIED | NONE | VERIFIED | PARTIAL |
| ST-08 | VERIFIED | VERIFIED | NONE | PARTIAL |
| ST-09 | VERIFIED | VERIFIED | VERIFIED | FULLY VERIFIED |
| ST-10 | VERIFIED | VERIFIED | VERIFIED | FULLY VERIFIED |
| UI-01 | VERIFIED | VERIFIED | VERIFIED | FULLY VERIFIED |
| UI-02 | VERIFIED | VERIFIED | VERIFIED | FULLY VERIFIED |
| UI-03 | VERIFIED | VERIFIED | VERIFIED | FULLY VERIFIED |
| UI-04 | VERIFIED | VERIFIED | FAILING | PARTIAL |
| UI-05 | VERIFIED | VERIFIED | FAILING | PARTIAL |
| UI-07 | VERIFIED | VERIFIED | NONE | PARTIAL |

## Cross-cutting (6) — reconstructed from the night report of 2026-10-07/08 (N4, third-party products, is outside Pilot 1)

| ID | Blocker | State (evidence) |
|---|---|---|
| N2 | Concurrent work on state files on Windows (Atomic) | PARTIAL — 5 bugs found and fixed (93, 96, 96b, 99, 99b); 99b rests on repetitions only, no statistical analysis |
| N3 | Reliability of the test system: silent returns, NOT TESTED counting, method equivalence | PARTIAL — T-1 fixed; equivalence on Windows not proven (T-3) |
| N5 | Windows version matrix (Server 2012 minimum per owner; 2016/2019/2022/2025, Windows 10/11; .NET 4.0 build) | PARTIAL — only the GitHub Windows image and Windows 11 in a VM; shipped net40 build natively on the GitHub image (qa-shards-28, 2/2) |
| N6 | Time zones and DST on Windows | NOT TESTED (26 cases) — owner decision TZ-W = B (CI runner zone) unblocks it |
| N7 | Soak (long run) and full regression on one snapshot | NOT TESTED (DEFERRED; Soak not started) |
| N8 | Disk full on the customer's side: temp, local copy, restore target | NOT TESTED — owner decision N-1 = A unblocks the post-commit case |

## Totals: 57 | FULLY VERIFIED 20 | PARTIAL 34 | NOT TESTED 3 | Owner-blocked 0 (all decided 2026-10-08; implementation pending where noted)
