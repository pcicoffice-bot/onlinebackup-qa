# Night round Q — end-to-end coverage of ST-07, ST-09, ST-10, ST-04, AG-02/AG-03, ST-05

Snapshot under test: `8bf7dc3` (branch `qa-agent-q`). No production code changed. Every run below was a single
Playwright run under the machine-wide lock (`night/q/run.sh` = `flock …/pw.lock npx playwright test <spec> --workers=1`),
`QA_NO_BUILD=1` after one build of `src/Server` and `src/Agent` in this worktree.

## Scope

Capabilities whose end-to-end layer had no test (per `capabilities.py`), now covered with real, separate server and
agent processes, the admin site in a browser where a person would use it, and — wherever data is involved — a restore
checked with SHA-256 against a manifest made at the source (`lib/world.ts` `manifest/compare`), never the product's own
"success". New files: `tests/QA/e2e/q1…q6-*.spec.ts`, helpers `tests/QA/e2e/q-helpers.ts`.

| Spec | Capability | What a person does | Oracle outside the product |
|---|---|---|---|
| q1-recycle-bin | ST-09 | deletes a set (Maintenance tab) and then the customer (customer page) on the site; Storage → Recycle bin → Restore; maintenance at +13 / +15 days | store folder gone / in `_recycle` byte-identical (SHA-256 of every stored object) / back byte-identical; agent `sets` + refused restore while deleted; restore SHA-256 identical; the changed set goes on (new backup restores identical); at +13 days the bin is kept, at +15 days its folder and every byte of the set's data are gone from disk |
| q2-verify-damaged-object | ST-04 | Maintenance → "Check the data (verify)", twice | FAULT PROVEN: object SHA-256 changed on disk AND a restore fails for exactly one file; after verify the object left the store and sits in `Quarantine/` with the damaged bytes; the next backup re-sends; restore identical; second verify 0 damaged |
| q3-settings-backup | ST-10 | Storage → settings backup: copy folder, Back up now, Download; later signs in again in a fresh browser | download SHA = server file SHA = copy-folder file SHA; zip holds system/conf + users/<login>/db and NO backed-up data; FAULT PROVEN: whole System Home + customer db deleted, the computer cannot reach its server; recovery exactly as documented in `ConfigBackup.cs`; admin sign-in (password + authenticator), customer and set on the site, saved key read back byte-identical (200), the computer with its OLD registration backs up a change, newest point and the point from before the loss restore identical |
| q4-replication (Q4a) | ST-07 | Storage → "A copy on a second server": on, address, token, Save | second real server process; objects on its disk; queue count on the first server; a computer registered on the SECOND server restores identical; FAULT PROVEN: second server killed, `fetch` to it fails; first server keeps backing up, its System log says "replication waiting", the site's "Waiting to copy" shows the count; BEHIND: second server back, restore before the queue drained gives the FIRST version whole (queue count 3 before and 3 after the restore — `q4-run2.txt` "restored while behind"); BACK: queue drains, both points restore identical from the second server |
| q4-replication (Q4b) | ST-07 | a customer that existed before the copy was switched on, then a new customer | the new customer's set must appear on the second server and restore from it — **FAILS (finding Q-F1)** |
| q5-service-follows-the-site | AG-03, AG-02 | Schedule tab time, Resources upload limit, Maintenance "Back up now" and "Stop" — all while the agent SERVICE runs | the service's own output lines (time + result), bytes arriving on the server's disk (rate), what the stopped point restores: run 2: scheduled run ended 03:20:39 for a 03:20 slot (not before); 200 KB/s limit measured on the server disk at 188.2 KB/s over 15 s; Stop → `BS_STOP_BY_USER` 6 s after the press, nothing more arrives in 8 s, the stopped point holds only identical files and lacks 24 of the 40 new ones; limit lifted + Back up now → everything identical |
| q6-quota | ST-05 | (FIXTURE quota 0.03 GB) backups over the room; quota lowered below usage; Quota tab raised to 1 GB | agent exit code + its message names the quota (`BS_STOP_QUOTA_EXCEEDED`, "quota is full"); store unchanged by a refused backup (SHA manifest); tasks page shows the run Failed and its detail names the quota; the point made before restores identical (twice: over the room, and full); newest point holds no half file; after the raise the next backup completes and restores identical |

## What ran (counts)

| Run | Result | File |
|---|---|---|
| q1 run 1 | FAIL — my oracle was too wide: it counted `stats/<set>.tsv` (Insights numbers) as data of the set; narrowed to backed-up data (files/restic/logs/_recycle) BEFORE the test was committed, the leftover is noted below (O-1) | q1-run1.txt, evidence/bugs/Q1-… |
| q1 run 2 | PASS (18.5 s) | q1-run2.txt |
| q2 run 1 | PASS (16.4 s) | q2-run1.txt |
| q3 run 1 | killed by me — the fresh sign-in waited forever on the old page (the restored session was still valid); the test now signs in in a new browser context | q3-run1.txt |
| q3 run 2, 3, 4 | PASS, PASS (adds: the key is stored, 200), PASS (adds: the copy folder set on the site holds the same file) | q3-run2/3/4.txt |
| q4 run 1 | Q4a FAIL at the end (→ Q-D1, owner decision; test restructured not to assume an answer), Q4b FAIL (Q-F1) | q4-run1.txt |
| q4 run 2 | Q4a PASS (7.2 min for both), Q4b FAIL again (Q-F1 reproduced twice) | q4-run2.txt |
| q5 run 1, 2 | PASS, PASS (7.6 min each) — timing-sensitive, so run twice; run 2 prints the measured facts | q5-run1/2.txt |
| q6 run 1, 2 | PASS, PASS; run 2 includes the "quota full" leg: `BS_STOP_QUOTA_EXCEEDED … err: The quota is full: new backups are stopped, existing backups are kept.` | q6-run1/2.txt |

Totals: 7 tests (6 spec files). PASS 6 (Q1, Q2, Q3, Q4a, Q5, Q6), FAIL 1 (Q4b = finding Q-F1).

### Each passing test can fail (one assertion inverted in a temporary copy `e2e/zz-inv-*.spec.ts`, run, copy deleted — `night/q/invert.py`)

| Test | Inverted assertion | Result of the inverted run |
|---|---|---|
| Q1 | the bin folder after +15 days exists | FAIL "the bin after 15 days — Expected true, Received false" (inv-q1.txt) |
| Q2 | the quarantined bytes are the GOOD ones | FAIL: SHA 404b83… received, 3805f5… (good) expected (inv-q2.txt) |
| Q3 | the restore equals the manifest from BEFORE the new file | FAIL: compare shows the difference (inv-q3.txt) |
| Q4a | the first restore from the second server differs | FAIL "Expected: not []" (inv-q4.txt) |
| Q5 | the scheduled run is before the slot | FAIL "the run at 03:15:17 is not before the slot 03:15:00" (inv-q5.txt) |
| Q6 | the over-the-room backup succeeded | FAIL: received "BS_STOP_QUOTA_EXCEEDED new=0 … bytes=0" (inv-q6.txt) |

## Findings

### Q-F1 (High, ST-07) — a customer the second server does not know stops the copy of EVERY customer, forever
- Test: `e2e/q4-replication.spec.ts` › Q4b — FAILS (q4-run1.txt, q4-run2.txt, evidence/bugs/Q4b-…):
  ```
  Error: timeout (240 s) waiting for: the new customer's set on the second server
  waiting to copy on the first server: 8
  replication waiting (commit qa-old): The user does not exist.   (every minute, 03:08:18 … 03:11:18)
  ```
- What happens: replication is switched on while a customer already exists (or a customer is made with the server's
  `adduser` command). That customer's next backup queues a "commit" event; the second server answers 404 "The user does
  not exist." for it; the queue stops at the first failure, so nothing after it — any other customer's backups, the
  brand-new customer's profile and objects — is ever copied. The site keeps showing "Waiting to copy: N" growing; there
  is no alert. The off-site copy silently stops for all customers.
- Root cause: `src/Server/Replication.cs:39` (nothing is queued while off, so the "user" event of an existing customer
  never exists); `src/Server/Replication.cs:53-61` (stops at the first failure, retries it forever);
  `src/Server/Replication.cs:95-107` ("commit" assumes the user exists on the second server);
  the receiver `src/Server/Api.cs:1642` (`users.UserDir(login)` → 404 NO_USER, `Users.cs:60`);
  `src/Server/Program.cs:178-179` (`adduser` never queues "user"/"db"); switching on (`Api.cs:1556`) queues nothing.
- Proposed fix: in `Send`, before "db"/"log"/"commit" of a login, make sure the second server has the user (the
  idempotent `POST /api/replica/user` with the profile's quota — `EnsureReplicaUser` already ignores a known login);
  when replication is switched on, queue "user"+"db" for every existing customer; make `adduser` queue them too; alert
  the administrators when the same event has failed for more than N minutes. Whether the earlier backups (history) of
  existing customers are copied on switch-on is a product decision (Q-D3).

### NEEDS OWNER DECISION

- **Q-D1 (ST-07)** A computer registered on the second server while the first server still sends copies is dropped
  there by the next copy: q4-run1.txt — after the queue drained, `points` on DR-PC1 → `error: The device was revoked or
  is unknown.` Cause: the "db" event sends the whole `db/` incl. `devices.xml` with overwrite (`Replication.cs:78-91`,
  `Api.cs:1646`), replacing the second server's own registrations. Decide: may the second server accept registrations
  (and keep them), or refuse them while it is a receiver, or should the documented way be "move the existing computer to
  the second server" only? Q4a does not assume an answer (it registers a new computer after the queue drained).
- **Q-D2 (ST-10)** The settings backup does not hold the run history or logs (`ConfigBackup.cs:23` SystemParts =
  conf, contract, policy, tickets, stats — not `runs`, `logs`). Fact (q3 runs 3/4): runs in the server's history before
  the loss 1, after the settings came back 0. Decide whether the history belongs in it.
- **Q-D3 (ST-07)** When the copy is switched on for a running server, existing customers' existing backups are not sent
  (only new commits are queued); a restore from the second server of such a customer's newest point needs objects that
  were never sent. Decide whether switch-on seeds the history.
- **Q-D4 (ST-07, UX)** The second server cannot be made a receiver from its admin site: `app.js` Storage page
  (1130-1136) has only the SENDER fields; `replicaReceiverToken` / `replicaDeleteDelayDays` exist only in the API
  (`Api.cs:1558-1560`). Q4 sets them through the API (FIXTURE). Decide: a field on the site, or a documented command.

### Observations (no test asserts them)
- **O-1 (ST-09, Low)** Deleting a set moves `files/`, `restic/`, `logs/` to the bin (`Recycle.cs:128`) but not the
  Insights numbers `stats/<set>.tsv` (`Insights.cs:41`), which stay after the bin is erased (q1-run1.txt). Numbers
  only, no customer data.
- **R-1 (ST-10, risk, NOT TESTED — Linux only here)** On Windows the saved keys, SMTP passwords and the replication
  token are protected with DPAPI *LocalMachine* (`Infra.cs:172-176`). A settings backup restored on a REPLACEMENT
  server (the case the backup is for) would not be able to read them (key recovery, mail, replication). Needs a real
  Windows run: restore a settings backup on a second Windows machine, then `GET keys/<login>/<set>`.

## Not done / not tested
- Windows (R-1 above). Restic-engine sets in these flows (all six use the native engine).
- ST-09 with two administrators (approval flow) on the site — the xUnit `RecycleTests.TwoAdministrators_ASecondOneApproves`
  covers the API; not driven in the browser.
- ST-07 deletion delay on the second server (retention on the first) — covered only by the xUnit test.
- The matrix (`capabilities.py`) is not edited here (shared file); proposed registrations:
  ST-09 happy/recovery/integrity `qa:e2e/q1`; ST-04 happy/failure/integrity `qa:e2e/q2`; ST-10 happy/recovery/integrity
  `qa:e2e/q3`; ST-07 happy/failure/recovery/integrity `qa:e2e/q4` (FAILING until Q-F1 is fixed); AG-03 happy and AG-02
  happy/recovery/integrity `qa:e2e/q5`; ST-05 failure/boundary/recovery/integrity `qa:e2e/q6`; and add
  `qa:e2e/q1`…`qa:e2e/q6` to `E2E_DATA_CHECK`.
