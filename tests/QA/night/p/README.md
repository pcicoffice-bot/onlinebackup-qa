# Night round P: restore correctness and version selection

Snapshot under test: 8bf7dc3 (branch qa-agent-p). Product code was not changed. Mutations and fix probes were applied
for one run each, then reverted with `git checkout`. Each log in `mutation-logs/` shows the diff and the run output,
and `mut.py` prints `git diff --stat src` = empty after the revert.

## Scope: what I tried to disprove

The claim under attack: "every point restores byte-identical" (BK-02, RS-01, RS-03, retention).

Every test uses a real server (`Env`) and a real agent. Every listed point is restored into its own empty folder. The
oracle is a manifest of the **source**, taken right after each run: the SHA-256 of every file plus its file time in ms
(RS-01 claims the time: `RestoreComponentTests.EveryFile_Identical_WithItsTime…`). The oracle never uses the product's
own result.

The tests use the product's time machine (`SystemClock.Use`, as in `TimeMachineTests`). This gives distinct run ids
without sleeping, and real days for retention.

All tests are in `tests/Tests/NightP_RestoreChainTests.cs`.

| # | Test | What it does | Result |
|---|---|---|---|
| 1 | `LongChain_EveryPointRestoresIdentical` × 6 (I/D × MAX_DELTA_NO 100/7 × with/without shrink-to-0) | One 8 MB file is edited in 45 runs: overwrite, insert, append, truncate, cut, one byte at the same size, all new bytes at the same size, shrink to 0 / grow back, prepend. Also an all-zero file that grows and a small file. MAX_DELTA_RATIO is high so the chain stays a chain. All 46 points are restored. | PASS (6/6). Longest chain: **46 objects** without shrink, 12 with shrink, 7 with MAX_DELTA_NO 7 (from the server's own index) |
| 2 | `NamesReusedAcrossRuns_EveryPointGivesItsOwnVersion` | A file deleted, then re-created with its old content and old time; renamed away, with a new file under the old name; folder → file of the same name → folder; file → folder → file; a big delta file deleted mid-chain, re-created with its v1 bytes and time, then delta'd again. 6 points. | PASS |
| 3 | `CaseOnlyRename_LaterPointsRestoreTheNewSpelling` | `Reports/Report.txt` → `REPORTS/report.txt` (content and time unchanged) | **FAIL → finding P-1** |
| 4 | `ChunkerEdges_EveryPointRestoresIdentical` | Sizes 0, 1, 256K±1, 1M±1, 8M±1 (the chunker's min / avg / max). All-zero files of 8M and 20M+5. One 300 KB block × 40. The same 2 MB under 12 names. Then: inserts that shift every later cut, a file grown or shrunk across the 8 MiB boundary, a file that takes another file's content, a file that becomes empty and comes back. 4 points. | PASS |
| 5 | `RetentionWithGaps_EveryKeptPointRestoresIdentical` × 2 (I/D) | 18 daily runs with chains that roll over (MAX_DELTA_NO 4) and a folder deleted and re-created. Retention is "last 2 + 3 weekly", so the kept points are 10-17, 10-24 and 10-25, with the runs between them expired. 29 stored objects were really deleted (asserted). Every kept point is restored after the first maintenance and again after a second one. | PASS |
| 6 | `DifferentialAfterAnIncrementalFullCopy_EveryPointRestoresIdentical` | The set is switched D → I (a new full copy at MAX_DELTA_NO) → D, then a region goes back to its first-version bytes | **FAIL → finding P-2** |
| 7 | `Restic_LongChain_EverySnapshotRestoresIdentical` | restic engine (product `ResticRunner` + real restic, local repository). 31 runs with the same edit cycle plus folder ↔ file swaps and a deleted / re-created file. Every snapshot from `SnapshotList()` is restored through `RestoreMany` and its position is checked against the run order. | PASS (31/31) |

Runs: 15 test cases, 13 PASS, 2 FAIL (both are findings). Logs are in `evidence/` (run1…run5).

Command (one at a time):
`OB_RESTIC=<scratchpad>/restic dotnet test tests/Tests/Tests.csproj --no-build --filter "FullyQualifiedName~NightP_RestoreChainTests" --logger "console;verbosity=detailed"`

## Proof that the passing tests can fail (mutation, `mutation-logs/`)

| Mutation (temporary, reverted) | Killed by |
|---|---|
| M1 `Restore.cs`: the recipe is taken from the first object of the chain, not the last | all 6 LongChain cases, NamesReused and ChunkerEdges (e.g. "44 of 46 points differ") |
| M2 `SetStore.FilesAt`: `removed>=point` (a point also lists a chain removed AT it) | NamesReused (extra doc.txt / a/x.txt / swap2 …) |
| M3 `SetStore.ApplyRetention`: only the newest kept point protects run folders | RetentionWithGaps I and D ("missing big.pst …" at 10-17) |
| M4 `ResticRunner.SnapshotList`: the list is not reversed (oldest first) | Restic_LongChain (position check). See `mutation-logs/M4-*.log` |

Fix probes (the proposed fix applied temporarily; the failing test then passes):

- `FIXPROBE-P2-base-list-on-every-full`: P-2 passes.
- `FIXPROBE-P2-regression-chains`: with the same fix, all LongChain cases, RetentionWithGaps,
  `EndToEndTests.DifferentialChain…` and `EndToEndTests.EveryPointOfADeltaChain…` pass.
- `FIXPROBE-P1-case-rename-resends`: P-1 passes. Only P-1 was run with this fix: no regression run.

## Findings

### P-2 (BK-02, High): a differential set switched to incremental and back makes "successful" points that cannot be restored

- **Where:**
  - `src/Agent/BackupRun.cs:373`: the local list of the last full copy's chunks (`*.full.txt`) is written only when a full copy is sent while the set is differential.
  - `src/Agent/BackupRun.cs:386`: a differential delta trusts that list (`LoadBaseChunks`) without checking that it belongs to the server's current chain.
- **Sequence:**
  1. D: a full copy of v0 (local "full" list = v0).
  2. The administrator changes the set to I.
  3. In the I period a new full copy is made (MAX_DELTA_NO, MAX_DELTA_RATIO, a resend…). The server moves the v0 chain out of Current, but the local "full" list is still v0's.
  4. The administrator changes the set back to D.
  5. A region of the file returns to its v0 bytes. Its chunk is "known" and is not sent, but no object of the current chain holds it.
- **What the customer sees:** run 3 ends `BS_STOP_SUCCESS`, upd=1. Restoring the newest point fails with "A chunk of the file is missing from the backup chain". Every later point stays broken until the next full copy.
- **Evidence:** `evidence/run3-stale-base.log`:
  `runs: 0:BS_STOP_SUCCESS … | 3:BS_STOP_SUCCESS upd=1 sent=3325695`
  `base point … (run 3): failed=1 missing ledger.mdb log: …,err,…/ledger.mdb,0,A chunk of the file is missing from the backup chain.`
- **Proposed fix:** write the base list on every full copy, whatever the delta type (`pendingBase[rel] = chunks` without the `DeltaType == "D"` test). Fix-probed: P-2 passes and the chain tests stay green. For more defence, store with the base list the run id of the full copy and send a full copy when the server's seq-0 object for the file is from another run.
- **Note:** the trigger is a normal set edit (delta type) done twice with a full copy in between. Nothing in the run or in the restore test before a restore shows it.

### P-1 (RS-01, Low; NEEDS OWNER DECISION on whether the case of a name is part of the restore contract): a case-only rename is never backed up

- **Where:**
  - `src/Core/Crypto.cs:104`: server-side names are lowercased ("case-insensitive like Windows").
  - `src/Agent/BackupRun.cs:192-211`: the change test is size + time only, so the rename is "unchanged" (line 211). `next[rel]` takes the new path, but nothing is uploaded.
  - The point keeps the old encrypted path (`SetStore.FilesAt`, `enc` of the last object, `SetStore.cs:623`).
- **What happens:** after `Reports/Report.txt` → `REPORTS/report.txt`, every later point restores `Reports/Report.txt`. The contents are identical; only the name differs from the source at that run. On Windows the file opens the same, but the name shown to the customer is the old one. Run 2 reported new=0 upd=0 del=0.
- **Evidence:** `evidence/run1-names.log`:
  `case point … (run 1): failed=0 missing REPORTS/report.txt; extra Reports/Report.txt`
- **Proposed fix (if the owner wants the current spelling):** treat a path that differs only in case (`!string.Equals(old.Path, item.Path, Ordinal)`) like a permission-only change, so a new object carries the new encrypted name. Fix-probed: P-1 passes. That fix re-sends each renamed file (a full copy for small files). A lighter option: a server call that updates `enc` only.

## Notes: not findings, not claimed

- **Attributes:** read-only / hidden attributes are backed up as part of `attrs` (a change counts as a "permission
  updated file"), but `Restore.RestoreFile` sets only the file time and never the attributes. I found no claim that
  restore keeps attributes (README, USER-GUIDE, specs.py), so this is not tested as a requirement. Hidden does not
  exist on Linux.
- **No dedup across files:** the same 2 MB under 12 names was sent 12 times (run 0 sent 56.7 MB). Dedup is per
  object only, and none is claimed.
- **Change detection:** it is size + time. A same-size edit that also restores the old time is not seen, by design
  (Ahsay model). Not tested as a requirement.
- **Restore checks:** native restore checks each object's SHA and the total size. A point that lists fewer files
  (the M3 mutant) restores "successfully" with files missing. That is the mutant's index, not the product; noted
  only because restore has no "expected file count" check of its own.

## Not done / not tested

- Restic with the **server's** restic store (ApiRestic / ResticStore): only the local-repository mode was run.
- Restic retention interplay (forget --prune with gaps): not run.
- Native restore from the **local copy** (`LocalSource`): not run.
- Web restore (`WebRestore`): not run.
- Windows-only metadata (ACL / SDDL, hidden, VSS): not run. The machine is Linux.
- The restic test takes about 11.5 min (about 22 s per run, with `forget --prune` every run). The cause of the
  per-run time was not investigated.
- Playwright: not used. Everything was xUnit, so the shared Playwright lock was never needed.
