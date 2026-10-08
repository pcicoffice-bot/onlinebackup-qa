# QA night round S — mutation testing of tonight's fixes + two NOT-TESTED items

Worktree `agent-s` (branch `qa-agent-s`), starting commit **35a995b**. Product under `src/**` never changed on a
commit (every mutant was applied, run, then `git checkout -- <file>`; `git status --short src` empty between/after).
Runner: `tests/QA/night/s/mut.py` (adapted from agent L's `night/l/mut.py` to this worktree). One `dotnet test` at a
time, filtered to the bug's own regression class. `OB_RESTIC` = scratchpad/restic.

## Scope
Part 1 — mutation testing of the fixes in buglog rows 78–90 landed since 4c6deea (bugs 78, 79, 81, 82, 83, 84, 85, 86,
87, 88, 89, 90). For each: locate the changed production lines, apply ONE mutation that undoes the fix's essence, run the
bug's regression test(s), record KILLED/SURVIVED, restore the file.
Part 2 — two items agent R left NOT TESTED, against a real in-process server (`new Env`):
(a) web-restore download `Content-Disposition` header injection via the `point` value;
(b) restic repository object routes (`ResticStore.FilePath`) traversal/encoded/backslash/null/overlong segments.

## Mutant table (part 1)

| Bug | File | Fix line mutated (essence undone) | Mutation | Regression test run | Verdict |
|----|------|-----------------------------------|----------|---------------------|---------|
| 78 | src/Agent/Restore.cs | the next restore removes the product's own `*.ob-restoring` leftovers | `foreach (var old in Directory.GetFiles(folder, …))` → `foreach (var old in new string[0])` | RestoreTempNameComponentTests.TheLeftoverOfACutRestore_* | **KILLED** |
| 79 | src/Server/SetStore.cs | VerifyRows re-checks a mismatch under the lock; a moved/replaced object is skipped | `if (now.Count==0 || now[0][0]!=sha){changed++;continue;}` → `if (false){…}` | NightM_RaceTests.CommitAndVerifyAll_* (50 iter) | **KILLED** |
| 81 | src/Server/Api.cs | missed-backup alert mark checked+set in one step under the lock | under-lock guard `if (alerted>0 && …<24h) return;` → `… && false) return;` | NightM_RaceTests.TwoMaintenanceRuns_* (30 iter) | **KILLED** |
| 82 | src/Agent/AgentApp.cs | lateness measured in real time (spring-forward gap) | `var late=(UtcOf(nowLocal)-UtcOf(slot.Value))…` → `(nowLocal-slot.Value)…` | NightM_ScheduleBoundaryTests.SpringForward_* | **KILLED** |
| 83 | src/Agent/AgentApp.cs | a slot that came while another set's run was in progress is not missed | `else if (late>15 && !BusyWithAnotherSet(…))` → `else if (late>15)` | NightM_ScheduleBoundaryTests.ASetDueWhileTheServiceIsBusyWithAnotherSet_* | **KILLED** |
| 84 | src/Server/SetStore.cs | ObjectPath serves the run's version wherever a commit moved it | `if (job!=null){var moved=Locate(loc,job);…}` → `if (false){…}` | NightM_RaceTests.RestoreOfTheLatestPoint_* (both methods) | **KILLED** |
| 85 | src/Agent/Client.cs | a local-disk write failure is LOCAL_DISK at once, not retried as the network | `catch (IOException e) { throw …LOCAL_DISK… }` → `catch (IOException e) when (false) { … }` | *(no xUnit cover — Playwright n6)* → **NightS_MutationKillTests.ALocalWriteFailureWhileDownloading_IsLocalDisk_NotRetriedAsNetwork** | **KILLED** by added test (mutant ends as NETWORK after ~10s of retries) |
| 86 | src/Agent/Restore.cs | the temp object path is noted BEFORE the download (cleanup of a failed download) | `paths.Add(p)` moved from before the fetch to after the checksum (its pre-fix place) | *(no xUnit cover — Playwright n6)* → **NightS_MutationKillTests.AFailedDownload_LeavesNoEmptyObjectInTheTempFolder** | **KILLED** by added test |
| 87 | src/Server/SetStore.cs | a read-only index is reopened immutable/read-only instead of 500 | `catch (SqliteException e) when ((14||8)&&File.Exists(IndexPath))` → `when (false && …)` | ReadOnlyStoreComponentTests | **KILLED** |
| 88 | src/Agent/AgentApp.cs | a slot after a successful run is due at its time (not the 15-min retry wait) | `bool slotAfterSuccess = attemptResult.StartsWith("BS_STOP_SUCCESS")&&slot>attempt;` → `= false && …;` | ScheduleAfterSuccessComponentTests | **KILLED** |
| 89 | src/Agent/LocalApi.cs | a refused key / closed port re-reads ui.txt and repeats the call once | `catch (AgentException e) when ((KEY||NOT_LISTENING)&&reread!=null)` → `when (false && …)` | ClientAfterServiceRestartComponentTests | **KILLED** (3 theory cases) |
| 90 | src/Server/Users.cs | every customer request compares the session's ACCOUNT_ID to the account's | `if (now==null||IsNullOrEmpty(s.Account)||now!=s.Account){EndSession(token);return null;}` → `if (false){…}` | NightR_IsolationTests.CustomerSession_SurvivesDeletion_* | **KILLED** |

Per-mutant console output and the KILLED/SURVIVED verdict lines are under `tests/QA/night/s/logs/<bug>.log`
(baseline-*.log are the clean baselines the verdicts are compared against).

No SURVIVORS among the existing regression tests. Bugs 85 and 86 have no xUnit regression cover (their regression is
Playwright failure-recovery/n6); the natural mutant therefore survives the *xUnit* suite, so two NightS_ tests were added
and each was shown to FAIL with the mutant and PASS without (logs `p…`/the runs recorded in this round).

## Part 2 results

### (a) Web-restore download — Content-Disposition header injection  →  NOT INJECTABLE
`src/Server/Api.cs` web-restore `download` builds `Content-Disposition: attachment; filename="restore-<point>.zip"`
from the request body's `point`. Reaching that line requires `WebRestore.Zip(...)` to run first, and both `Zip` and `Ls`
call `WebRestore.CheckPoint(point)` (`src/Server/WebRestore.cs:95`): a point must be hex and ≤64 chars, else 400 POINT —
so a CR/LF or quote point never reaches the header. Defense in depth: even with CheckPoint removed, a non-snapshot point
makes `restic restore` fail (500 RESTIC) before the header, and HttpListener itself rejects CR/LF in a header value.
Test `NightS_Part2Tests.WebRestoreDownload_PointValue_CannotInjectAResponseHeader` (real server, raw socket read of the
response bytes) fires four hostile points (CRLF header split, body split, quote/`filename=` breakout, path traversal) with
the CORRECT encryption key so the download case is actually reached; asserts no `X-Injected`/`X-Evil` header line and no
`evil.exe`/`passwd` in the real response headers, and a 4xx refusal. **PASS.** Shown able to fail: with CheckPoint
neutered the test fails (the point then reaches restic and the status changes 400→500), proving it exercises that path.

### (b) Restic object routes — path traversal  →  NOTHING OUTSIDE THE REPOSITORY IS READ OR WRITTEN
`src/Server/ResticStore.cs` `FilePath`/`ValidName`: `type` must be one of {config,data,keys,locks,snapshots,index},
`name` must be exactly 64 hex chars. Test `NightS_Part2Tests.ResticObjectRoutes_RejectTraversalAndNeverTouchFilesOutsideTheRepository`
plants a secret sentinel OUTSIDE the repo (in the user's home) and, with valid Basic auth (login + a minted set token),
fires 9 read + 2 write hostile segments (`%2e%2e`, `..%2f`, `..%5c`, `%00`, overlong `%c0%ae`, a non-64-hex name, the
token-store path). Observed statuses (`tests/QA/night/s/evidence/restic-route-results.txt`):

```
400  …/data/%2e%2e%2f%2e%2e%2f%2e%2e%2fOUTSIDE-SECRET.txt
400  …/keys/..%2f..%2fOUTSIDE-SECRET.txt
404  …/%2e%2e%2f%2e%2e%2fOUTSIDE-SECRET.txt
400  …/data/<63 a's>zz          (name not 64 hex)
400  …/data/%00                 (null)
400  …/data/..%5c..%5cOUTSIDE-SECRET.txt   (backslash)
404  …/%c0%ae%c0%ae%2fOUTSIDE-SECRET.txt   (overlong UTF-8)
200  …/config/..%2f..%2fOUTSIDE-SECRET.txt (collapses to the in-repo config; sentinel NOT in the body)
404  …/db/restic/<set>.token
POST 400  …/data/%2e%2e%2f…%2fPLANTED.txt
POST 400  …/keys/..%2f..%2fPLANTED.txt
```
Oracle (outside the code): the out-of-repo sentinel never appears in any response, no `PLANTED.txt` is created anywhere
under the user's home, and the sentinel's bytes are intact at the end. Positive control: a valid GET of the in-repo
`config` returns 200 with a non-empty body, proving the retrieval path really serves file bytes (so a traversal that
reached outside WOULD have surfaced the sentinel). **PASS.** Note: on Linux a single URL path segment cannot carry a real
directory separator, so the `name` whitelist is belt-and-suspenders; the test documents the observed containment rather
than relying on that platform detail.

## Tests added (committed)
- `tests/Tests/NightS_MutationKillTests.cs` — bug 85 (ALocalWriteFailureWhileDownloading…), bug 86 (AFailedDownload…).
- `tests/Tests/NightS_Part2Tests.cs` — part 2a and 2b.
- `tests/QA/night/s/mut.py`, `tests/QA/night/s/logs/*`, `tests/QA/night/s/evidence/*`.
