# Owner decisions — binding product requirements

Decided by the owner on 2026-10-08 (from the decision table of the same day). Each line is a requirement: the code and the
tests follow it. A test that encoded the other option is changed to this requirement in the same commit as the code, and
the commit names the decision.

## Scope

- **Pilot 1 = Windows File Backup, native engine only.** Restic = NOT SUPPORTED in Pilot 1 (already blocked by the pilot
  switch, PilotScope.Restic). Restic tests stay in the regression gate, not on the pilot's critical path.
- Minimum Windows: Windows Server 2012. Direction for the agent: .NET Framework 4.8 — no migration before a plan and a
  separate approval.
- QA that does not depend on the admin UI sketch continues without waiting for the sketch choice.
- Out of Pilot 1: resellers page (UX-2), SQL (76 decided below, but SQL stays out), replication (Q-D1..Q-D4: after the pilot).

## Decisions

| ID | Requirement |
|---|---|
| N-1 | The server committed the run and the computer could not save its local index (its disk full): **both** the computer and the server report the run as **completed with a warning** ("this computer's disk is full"), never one "failed" and the other "success". The next run is correct. |
| 106 | A server update brought as **files** must carry the vendor's signature and is refused without it (as the portal path). The **GitHub** path checks the signature too; the audit line says "signed" only when a signature was checked. |
| 115 | The administrator's explicit "rebuild index" **is allowed** on a set of a kind blocked in the pilot, as a repair action written to the log. The set's backed-up data (objects) stays byte-identical; only the index is rebuilt. |
| 122 | The code that confirms the two-step set-up is **used up**: it cannot open a sign-in; the first sign-in waits for the next code. |
| P-1 | Pilot: a case-only rename is a **known, documented** behaviour (content identical, the restored name keeps the earlier spelling). After the pilot: a case-only rename is backed up (option A). |
| TZ-W | The Windows time-zone / DST cases run in CI by setting the Windows runner's own zone for that test group (no product change). |
| B2 | A run that completed with files not backed up is **partial**: its own amber status; the "last successful backup" counts only clean runs, and "last backup" (any) is shown separately; an alert when partial repeats. |
| 77 / D-77b | Long-term retention buckets (day / week / month / quarter / year) use the **server's time zone**. |
| Retention in an outage | Retention by days counts from the **last successful backup**, not from now. |
| A7 | Key recovery: the customer chooses at set-up; default "keep a recovery copy", with clear disclosure. The raw key is never handed to a device token. |
| S-1 | Pilot wording: "content is encrypted on the computer; file names are visible to the provider for support and reports". No claim that names are encrypted. |
| UX-1 | The client's Restore page fits a 1280×800 screen: Restore / Replace buttons visible without resizing; the "restore started" message in front. |
| W13 | Setup recognises an installation by its service and settings (not only the program file) and offers Repair / Remove. |
| L-1 | A mail when an unconfirmed licence drops to the basic edition, and a daily reminder; over the basic limit, existing customers keep backing up for a grace period, only new customers / sets are refused. |
| R-lead | An administrator's (and reseller administrator's) password change ends that account's other sign-ins. |
| B1 | Pilot: **automatic agent update is off** until updates are signed and refuse older versions (then option A). |
| B5 | Pilot: restore brings back content and modified time; the product says explicitly that permissions/attributes are not restored. After the pilot: restore them (option A). |
| M-3 | A daily slot inside the spring-forward gap runs at the first minute after the gap (03:00 for 02:30). Confirmed. |
| 76 | 'All databases' in log mode skips SIMPLE-recovery databases (and master) with a warning. SQL stays out of Pilot 1. |
| UX-2 | Out of Pilot 1. |
| Q-D1..Q-D4 | After the pilot. |

## Still open (not decided)

- (none)


## Added 2026-10-08 (later)

- **Fast / Deep gate (A):** Fast Gate on every product push for quick feedback; the full Deep Gate stays mandatory nightly,
  before Certification and before Release / Pilot. A Fast PASS is never a Certification PASS. Applied only at a safe point,
  after the running gate finishes; no running gate is stopped for it.
- **57 pilot blockers** stay the target: 51 capability + 6 cross-cutting (N2, N3, N5, N6, N7, N8) — docs/PILOT-BLOCKERS.md.
