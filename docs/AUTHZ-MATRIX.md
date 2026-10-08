# Authorization matrix — the intended rule of every HTTP route (owner plan §2, step 1)

The **oracle** for the server's authorization, written as the INTENDED rule (docs/USER-GUIDE.md, docs/OWNER-DECISIONS.md and
the intent the code states in its own comments: TECH-010, VND-050, A7, PILOT-010 …), independent of how the checks are coded.
Every row is executed by `tests/Tests/AuthzOracleTests.cs` as every kind of caller; the test and this file list the same row
IDs (the test `TheMatrixDocument_AndTheOracle_ListTheSameRoutes` fails when they drift apart). Where the intended rule is not
written anywhere it is marked **UNKNOWN** and the test reports today's answer without judging it.

Scope of this file: the routes of `src/Server/Api.cs` and what it dispatches to (`ApiRestic.cs`, `AdminUi.Serve/ServeI18n`,
`WebRestoreApi`, `AdminTickets`, `Replica`). Not in it: the partner portal and licence centre (`Portal.cs`, `LicenseCenter.cs`)
and the setup wizard (`SetupWizard.cs`) — separate listeners of other programs, not the backup server's API.

## Callers

| Caller | Who | Credential |
|---|---|---|
| anon | nobody signed in | none |
| junkS / junkD | an unknown caller | a made-up `X-Session`; a forged `X-Device` with a real customer's name and device id and a wrong secret |
| revoked | a computer the administrator disconnected | its old device token |
| devA | **customer** anna's computer PC-annapc (scheduled work) | device token |
| devA2 | anna's OTHER computer PC-anna2 | device token |
| sesA | anna signed in interactively (password + 2FA when set) — the client window, web restore | customer session |
| devB / sesB | **another customer** (bob, of another reseller) — the cross-tenant caller | device token / session |
| admin | the server's main administrator (ADMIN) | staff session |
| staff | a second administrator (STAFF). "Technician": TECH-010 and the guide 2.7 — *administrators only, no technician or view roles, every administrator can do everything* — so a technician is this caller with the same rights | staff session |
| vndA | the administrator of the **reseller** that owns anna (VENDOR_ADMIN of acme-it) | staff session (vendor) |
| vndB | the administrator of ANOTHER reseller (beta) — the cross-tenant admin | staff session (vendor) |

Scope chain: **server (tenant = the server, or a reseller's slice of it) → customer (login) → computer → backup set**.
A customer's device token and sessions act only on that customer (`login` comes from the credential, never from the URL);
a reseller acts only on customers whose `OWNER` is it (VND-050); the server's administrators act on everything.

## Order of the checks (required)

1. **Authentication** for the route's API: a caller without a valid credential of THAT API gets **401** — whatever the path,
   the body or the pilot switch say.
2. **Tenant / customer / computer / set**: a signed-in caller outside the target's scope gets 403 or 404 (404 = "no such
   thing for you", the repository's convention for another customer's set) and learns nothing of it.
3. **Capability / pilot policy** (interactive sign-in required, reseller vs. system administrator, `PilotScope`): 401 SESSION /
   403 (VENDOR, RIGHTS, SCOPE …).
4. The **handler**.

**Fail closed:** an unknown route, method or caller is refused (never 200, never a 5xx).
A refused call **changes nothing** (no file of the customer or of the server's settings) and shows nothing of the target.

## Columns

- **Allowed**: the callers that pass every check. Everyone else is refused (401 if they hold no valid credential of the API,
  else 401/403/404). `self` = the caller's own customer account (devA, devA2, sesA, devB, sesB each act on their own data).
- **Scope**: what the route acts on.
- **Pilot**: `P` the whole route is refused in Pilot 1 (`PilotScope`, 403 SCOPE); `S` refused for a set outside the pilot
  (`PilotScope.CheckSet`: restic, non-file types, commands, local copy); `-` not blocked; `?` UNKNOWN.
- **Today**: what `src/` does now, from the oracle's run — `=` the matrix; `AZF-n` a finding (below); `UNKNOWN: …` what it does.

## Public (no credential)

| ID | Method | Path | Allowed | Scope | Pilot | Rule source | Today |
|---|---|---|---|---|---|---|---|
| AZ-P01 | GET | /api/brand | everyone | server branding | - | BRAND-010 (before sign-in) | = |
| AZ-P02 | GET | /api/contract | everyone | contract text | - | CONTRACT-010 | = |
| AZ-P03 | POST | /api/signup | everyone (the body is the new account) | new customer | P | SIGNUP-010, PilotScope.Signup | = |
| AZ-P04 | POST | /api/register | everyone; the body's password (+2FA) authenticates | new device of that customer | - | the agent's registration | = |
| AZ-P05 | POST | /api/login | everyone; the body authenticates | new customer session | - | H-04 | = |
| AZ-P06 | POST | /api/admin/login | everyone; the body authenticates (wrong = 401 LOGIN, the handler's answer) | new staff session | - | TECH-010 | = |
| AZ-P07 | GET | /admin | everyone (static page) | — | - | AdminUi.Serve | = |
| AZ-P08 | GET | /admin/app.js | everyone (static) | — | - | AdminUi.Serve | = |
| AZ-P09 | GET | /admin/restore.html | everyone (static) | — | P | bug 114 | = |
| AZ-P10 | GET | /restore | everyone (static) | — | P | WEB-010 | = |
| AZ-P11 | GET | /i18n/theme.css | everyone (static) | — | - | I18N-010 | = |

## Customers' API — the caller's own account

| ID | Method | Path | Allowed | Scope | Pilot | Rule source | Today |
|---|---|---|---|---|---|---|---|
| AZ-A01 | POST | /api/logout | self, session only | own session | - | H-04 | = |
| AZ-A02 | GET | /api/client/files | self (device or session) | — | P | UPD-020, owner decision B1 | = |
| AZ-A03 | GET | /api/client/file?name= | self | — | P | UPD-020, B1 | = |
| AZ-A04 | GET | /api/quota | self | own customer | - | RST-090 | = |
| AZ-A05 | POST | /api/folders | self | own customer | - | SRC-030 | = |
| AZ-A06 | GET | /api/profile | self (secrets removed) | own customer | - | SafeProfile | = |
| AZ-A07 | POST | /api/totp/enable | self, session only | own 2FA | - | SEC-010 | = |
| AZ-A08 | POST | /api/totp/confirm | self, session only | own 2FA | - | SEC-010 | = |
| AZ-A09 | POST | /api/totp/disable | self, session only | own 2FA | - | SEC-010 | = |
| AZ-A10 | GET | /api/tickets | self | own calls only | - | TICKETS-030 | = |
| AZ-A11 | POST | /api/tickets | self | own customer | - | TICKETS-030 | = |
| AZ-A12 | POST | /api/sets | self, session only (+ CAN_ADD_SETS) | own customer | S | SET-040 | = |
| AZ-A13 | GET | /api/webrestore/sets | self, session only | own sets | P | WEB-010 | = |
| AZ-A14 | POST | /api/webrestore/{set}/points | sesA only (anna's restic set) | set | P | WEB-010 | = (pilot order: AZF-5) |
| AZ-A15 | POST | /api/webrestore/{set}/search | sesA only | set | P | WEB-010, AI-040 | = (AZF-5) |
| AZ-A16 | POST | /api/webrestore/{set}/ls | sesA only | set | P | WEB-010 | = (AZF-5) |
| AZ-A17 | POST | /api/webrestore/{set}/download | sesA only | set | P | WEB-010 | = (AZF-5) |
| AZ-A99 | GET | /api/no-such-route | nobody (fail closed) | — | - | — | = (401 without a credential, 404 with one) |

## Customers' API — one backup set of anna's

S1 = anna's set of PC-annapc (with a stored backup and its recovery key), C1 = its copy on PC-anna2 (SET-020), RS = anna's
restic set. Another customer's device or session never reaches them (404). Device token vs. interactive session: the code
says "a device token (scheduled work) or an interactive session (password + 2FA)"; routes that need a person say so
(`requireInteractive`).

| ID | Method | Path | Allowed | Scope | Pilot | Rule source | Today |
|---|---|---|---|---|---|---|---|
| AZ-S01 | POST | /api/sets/{S1}/key | sesA | set's recovery key | - | A7 (the customer chooses) | = |
| AZ-S02 | POST | /api/sets/{S1}/settings | sesA (+ rights) | set | S | SET-040 | = |
| AZ-S03 | GET | /api/sets/{C1}/sharedkey | **sesA only** | the raw key of the parent set | - | **A7: "The raw key is never handed to a device token."** | = (AZF-1 fixed: device tokens 401 SESSION, nothing created) |
| AZ-S04 | POST | /api/sets/{RS}/restic | devA, sesA; devA2 UNKNOWN | new restic token | P | RST-030 | = ; UNKNOWN: devA2 200 |
| AZ-S05 | POST | /api/sets/{RS}/resticreport | devA, sesA; devA2 UNKNOWN | run log | P | RST-040 | = ; UNKNOWN: devA2 200 |
| AZ-S06 | GET | /api/sets/{S1}/points | devA, sesA; devA2 UNKNOWN | set | S | restore, restore test | = ; UNKNOWN: devA2 200 |
| AZ-S07 | GET | /api/sets/{S1}/files | devA, sesA; devA2 UNKNOWN | set | S | restore | = ; UNKNOWN: devA2 200 |
| AZ-S08 | GET | /api/sets/{S1}/object | sesA (a person restores) | set's data | S | requireInteractive | = |
| AZ-S09 | GET | /api/sets/{S1}/object?test=1 | devA (daily allowance), sesA; devA2 UNKNOWN | set's data | S | automatic restore test | = ; UNKNOWN: devA2 200 |
| AZ-S10 | POST | /api/sets/{S1}/restoretest | devA, sesA; devA2 UNKNOWN | set's record | S | restore test | = ; UNKNOWN: devA2 200 |
| AZ-S11 | POST | /api/sets/{S1}/restorelog | sesA | set's record | S | requireInteractive | = |
| AZ-S12 | GET | /api/sets/{S1}/begin | devA, sesA; devA2 UNKNOWN | opens a run | S | backup | = ; UNKNOWN: devA2 reaches it (409 BUSY) |
| AZ-S13 | POST | /api/sets/{S1}/interrupted | devA, sesA; devA2 UNKNOWN | run | - | R1 | = ; UNKNOWN: devA2 200 |
| AZ-S14 | POST | /api/sets/{S1}/progress | devA, sesA; devA2 UNKNOWN | run | - | LIVE-010 | = ; UNKNOWN: devA2 200 |
| AZ-S15 | PUT | /api/sets/{S1}/jobs/{job}/object | devA, sesA; devA2 UNKNOWN | run's data | S | backup | = ; UNKNOWN: devA2 reaches it |
| AZ-S16 | POST | /api/sets/{S1}/jobs/{job}/delete | devA, sesA; devA2 UNKNOWN | run | S | backup | = ; UNKNOWN: devA2 200 |
| AZ-S17 | POST | /api/sets/{S1}/jobs/{job}/commit | devA, sesA; devA2 UNKNOWN | run | S | backup | = ; UNKNOWN: devA2 reaches it |
| AZ-S18 | POST | /api/sets/{S1}/jobs/{job}/abort | devA, sesA; devA2 UNKNOWN | run | - | backup | = ; UNKNOWN: devA2 200 (it ended anna's open run) |
| AZ-S99 | GET | /api/sets/{S1}/no-such-route | nobody | — | - | — | = |

**UNKNOWN — the computer level.** The guide says *"each set belongs to one computer"* (2.3) and *"backups of the customer's
other computers appear only in Restore"* (3.2), but no document says whether a DEVICE token of one computer may run, commit,
abort, read or restore-test a set of the customer's OTHER computer. Today a device token carries only the customer (the
server never compares the device with the set's `SCHEDULE_HOST`), so devA2 can do all of it on S1. Owner decision needed;
the oracle then judges devA2.

## Admin API — the server's own pages (every administrator; a reseller is refused, VND-050)

| ID | Method | Path | Allowed | Scope | Pilot | Rule source | Today |
|---|---|---|---|---|---|---|---|
| AZ-M01 | POST | /api/admin/logout | every administrator (own session) | own session | - | | = |
| AZ-M02 | GET | /api/admin/me | every administrator (also while enrolling 2FA) | own account | - | VND-065, TECH-010 | = |
| AZ-M03 | POST | /api/admin/totp/enable | every administrator, own | own 2FA | - | TECH-010 | = (refusals only; the allowed call is not sent) |
| AZ-M04 | POST | /api/admin/totp/confirm | every administrator, own | own 2FA | - | TECH-010 | = (refusals only) |
| AZ-M05 | GET | /api/admin/deletes | admin, staff | server | - | DEL-010/020 | = |
| AZ-M06 | POST | /api/admin/deletes/settings | admin, staff | server | - | DEL-020 | = |
| AZ-M07 | POST | /api/admin/deletes/{id}/approve | admin, staff | a deletion | - | DEL-020 | = |
| AZ-M08 | POST | /api/admin/deletes/{id}/cancel | admin, staff | a deletion | - | DEL-020 | = |
| AZ-M09 | GET | /api/admin/recycle | admin, staff | server | - | DEL-010 | = |
| AZ-M10 | POST | /api/admin/recycle/{x}/restore | admin, staff | server | - | DEL-010 | = |
| AZ-M11 | GET | /api/admin/templates | admin, staff | server | - | TPL-010 | = |
| AZ-M12 | POST | /api/admin/templates | admin, staff | server | - | TPL-010 | = |
| AZ-M13 | POST | /api/admin/templates/{n}/delete | admin, staff | server | - | TPL-010 | = |
| AZ-M14 | GET | /api/admin/time | admin, staff | server | - | | = |
| AZ-M15 | POST | /api/admin/time | admin, staff | server | - | | = |
| AZ-M16 | GET | /api/admin/contract | admin, staff | server | - | CONTRACT-010 | = |
| AZ-M17 | POST | /api/admin/contract | admin, staff | server | - | CONTRACT-010 | = |
| AZ-M18 | GET | /api/admin/ticketsettings | admin, staff | server | - | TICKETS-020 | = |
| AZ-M19 | POST | /api/admin/ticketsettings | admin, staff | server | - | TICKETS-020 | = |
| AZ-M20 | GET | /api/admin/vendors | admin, staff | server | ? | VND-010 | = ; pilot UNKNOWN |
| AZ-M21 | POST | /api/admin/vendors | admin, staff | server | ? | VND-010 | = ; pilot UNKNOWN |
| AZ-M22 | POST | /api/admin/vendors/{id}/admins | admin, staff | a reseller | ? | VND-020 | = ; pilot UNKNOWN |
| AZ-M23 | GET | /api/admin/defaults | admin, staff | server | - | DEF-010 | = |
| AZ-M24 | POST | /api/admin/defaults | admin, staff | server | - | DEF-010 | = |
| AZ-M25 | GET | /api/admin/guard | admin, staff | server | - | GUARD-010 | = |
| AZ-M26 | POST | /api/admin/guard | admin, staff | server | - | GUARD-010 | = |
| AZ-M27 | POST | /api/admin/guard/block | admin, staff | server | - | GUARD-010 | = |
| AZ-M28 | POST | /api/admin/guard/unblock | admin, staff | server | - | GUARD-010 | = |
| AZ-M29 | GET | /api/admin/configbackup | admin, staff | server | - | CFGBK-010 | = |
| AZ-M30 | POST | /api/admin/configbackup | admin, staff | server | - | CFGBK-010 | = |
| AZ-M31 | POST | /api/admin/configbackup/now | admin, staff | server | - | CFGBK-010 | = (see AZO-1) |
| AZ-M32 | GET | /api/admin/configbackup/{file} | admin, staff | server (every customer's db) | - | CFGBK-010 | = |
| AZ-M33 | GET | /api/admin/homes | admin, staff | server | - | | = |
| AZ-M34 | POST | /api/admin/rebuild | admin, staff | any set (body) | - | decision 115 | = |
| AZ-M35 | POST | /api/admin/verify | admin, staff | any set (body) | - | | = |
| AZ-M36 | POST | /api/admin/maintenance | admin, staff | server | - | | = |
| AZ-M37 | GET | /api/admin/update/source | admin, staff | server | - | UPD-040 | = |
| AZ-M38 | POST | /api/admin/update/source | admin, staff | server | - | UPD-040 | = |
| AZ-M39 | POST | /api/admin/update/upload | admin, staff, from the server itself only | server | - | UPD-030, decision 106 | = |
| AZ-M40 | GET | /api/admin/update | admin, staff | server | - | UPD-010 | = |
| AZ-M41 | POST | /api/admin/update | admin, staff | server | - | UPD-010 | = (refusals only; the allowed call would download) |
| AZ-M42 | GET | /api/admin/license | admin, staff | server | - | LIC-060 | = |
| AZ-M43 | POST | /api/admin/license | admin, staff | server | - | LIC-060 | = |
| AZ-M44 | GET | /api/admin/settings | admin, staff | server | - | | = |
| AZ-M45 | POST | /api/admin/settings | admin, staff (replication / AI fields: P) | server | - | PILOT-010 | = |
| AZ-M46 | POST | /api/admin/testmail | admin, staff | server | - | | = |
| AZ-M47 | GET | /api/admin/staff | admin, staff | server | - | TECH-010 | = |
| AZ-M48 | POST | /api/admin/staff | admin, staff | server | - | TECH-010 | = |
| AZ-M49 | POST | /api/admin/staff/{login}/{act} | admin, staff | an administrator | - | TECH-010 | = |
| AZ-M50 | GET | /api/admin/logs?cat=System | admin, staff | server | - | | = |
| AZ-M51 | GET | /api/admin/logs?cat=Backup&login=&set= | admin, staff | any customer's logs | - | bug 123 | = |
| AZ-M52 | POST | /api/admin/replicate | admin, staff | server | P | ST-07 | = |
| AZ-M53 | POST | /api/admin/aitest | admin, staff | server | P | UI-08 | = |
| AZ-M54 | GET | /api/admin/tickets/deleted | admin, staff | server | - | TICKETS-010 | = |
| AZ-M55 | POST | /api/admin/tickets/deleted/{i}/restore | admin, staff | server | - | TICKETS-010 | = |
| AZ-M56 | POST | /api/admin/brand | vndA, vndB (each its own branding; the server's own branding is in settings) | own reseller | - | VND-060 | = |
| AZ-M57 | GET | /api/admin/no-such-route | nobody | — | - | — | = |

## Admin API — lists every administrator opens (a reseller sees only its own customers)

`vndB` is allowed on these but must see and change nothing of anna's (the oracle checks the text and the files).

| ID | Method | Path | Allowed | Scope | Pilot | Rule source | Today |
|---|---|---|---|---|---|---|---|
| AZ-L01 | GET | /api/admin/tickets | every administrator, filtered | own customers' calls | - | TICKETS-010 | = |
| AZ-L02 | GET | /api/admin/tasks | every administrator, filtered | own customers' runs | - | TASKS-010 | = |
| AZ-L03 | GET | /api/admin/dashboard | every administrator, filtered | own customers | - | DASH-010, H-12 | = |
| AZ-L04 | GET | /api/admin/live | every administrator, filtered | own customers | - | LIVE-010 | = |
| AZ-L05 | GET | /api/admin/checks | every administrator, filtered | own customers | - | CHK-010 | = |
| AZ-L06 | GET | /api/admin/users | every administrator, filtered | own customers | - | VND-050 | = |
| AZ-L07 | POST | /api/admin/users | every administrator (a reseller's new customer is its own) | new customer | - | VND-050, LIC-030 | = |
| AZ-L08 | GET | /api/admin/clientpackage | every administrator (own branding) | — | - (linux/mac: P) | PKG-010 | = |
| AZ-L09 | GET | /api/admin/insights | every administrator, filtered | own customers | P | AI | = |
| AZ-L10 | POST | /api/admin/bulk | every administrator; each row only on own customers | the listed customers | - | BULK-010 | = |

## Admin API — one customer (anna, owned by vndA's reseller) and its set S2

| ID | Method | Path | Allowed | Scope | Pilot | Rule source | Today |
|---|---|---|---|---|---|---|---|
| AZ-C01 | POST | /api/admin/tickets (login=anna) | admin, staff, vndA | customer | - | TICKETS-010 | = |
| AZ-C02 | GET | /api/admin/tickets/{id} | admin, staff, vndA | the call's customer | - | TICKETS-010 | = |
| AZ-C03 | POST | /api/admin/tickets/{id}/note | admin, staff, vndA | the call's customer | - | TICKETS-010 | = |
| AZ-C04 | POST | /api/admin/tickets/{id}/delete | admin, staff, vndA | the call's customer | - | TICKETS-010 | = |
| AZ-C05 | GET | /api/admin/users/{login}/compliance | admin, staff, vndA | customer | - | AI-080 | = |
| AZ-C06 | POST | /api/admin/users/{login}/aidiagnose | admin, staff, vndA | customer's log | P | AI-020 | = |
| AZ-C07 | GET | /api/admin/users/{login}/sets/{S2} | admin, staff, vndA | set | - | SET-010 | = |
| AZ-C08 | POST | /api/admin/users/{login}/sets/{S2} | admin, staff, vndA | set | S | SET-010 | = |
| AZ-C09 | GET | /api/admin/users/{login}/sets/{S2}/runs | admin, staff, vndA | set | - | REP-020 | = |
| AZ-C10 | POST | /api/admin/users/{login}/sets/{S2}/run | admin, staff, vndA | set | S | SET-030 | = |
| AZ-C11 | POST | /api/admin/users/{login}/sets/{S2}/stop | admin, staff, vndA | set | - | SET-030 | = |
| AZ-C12 | POST | /api/admin/users/{login}/sets/{S2}/addcomputer | admin, staff, vndA | set → computer | S | SET-020 | = |
| AZ-C13 | POST | /api/admin/users/{login}/sets/{S2}/removecomputer | admin, staff, vndA | set → computer | - | SET-020 | = |
| AZ-C14 | POST | /api/admin/users/{login}/sets/{S2}/move | admin, staff, vndA | set → computer | S | SET-020 | = |
| AZ-C15 | GET | /api/admin/users/{login}/computers | admin, staff, vndA | customer's computers | - | COMP-010 | = |
| AZ-C16 | POST | /api/admin/users/{login}/computers/disconnect | admin, staff, vndA | computer | - | COMP-010 | = |
| AZ-C17 | POST | /api/admin/users/{login}/computers/move | admin, staff, vndA (target also its own) | computer → customer | P | COMP-010, SH-06 | = |
| AZ-C18 | GET | /api/admin/users/{login}/folders | admin, staff, vndA | computer's folder names | - | SRC-030 | = |
| AZ-C19 | POST | /api/admin/users/{login}/browse | admin, staff, vndA | computer | - | SRC-030 | = |
| AZ-C20 | POST | /api/admin/users/{login}/unlock | admin, staff, vndA | customer | - | | = |
| AZ-C21 | POST | /api/admin/users/{login}/delete | admin, staff, vndA | customer / set | - | DEL-010/020 | = |
| AZ-C22 | POST | /api/admin/users/{login}/contacts | admin, staff, vndA | customer | - | CUST-020 | = |
| AZ-C23 | POST | /api/admin/users/{login}/details | admin, staff, vndA | customer | - | SET-040 | = |
| AZ-C24 | POST | /api/admin/users/{login}/untrash?set= | admin, staff, vndA | restic set | S | RST-070 | = |
| AZ-C25 | POST | /api/admin/users/{login}/resettotp | admin, staff, vndA | customer | - | SEC-010 | = |
| AZ-C26 | POST | /api/admin/users/{login}/security | admin, staff, vndA | customer | - | SEC-020/030 | = |
| AZ-C27 | POST | /api/admin/users/{login}/quota | admin, staff, vndA | customer | - | | = |
| AZ-C28 | POST | /api/admin/users/{login}/unfreeze | admin, staff, vndA | customer | - | ransomware freeze | = |
| AZ-C29 | GET | /api/admin/keys/{login}/{set} | admin, staff, vndA (the customer chose key recovery) | set's recovery key | - | A7, VND-050 | = |

## Their own credentials: restic repositories, the second server, the old-Windows header

| ID | Method | Path | Allowed | Scope | Pilot | Rule source | Today |
|---|---|---|---|---|---|---|---|
| AZ-R01 | GET | /restic/{login}/{set}/config | HTTP Basic: the customer's name + THAT set's token; nothing else (no device, session or administrator) | set's repository | P | RST-010 | = ; pilot order: **AZF-2** |
| AZ-R02 | GET | /restic/{login}/{other set}/config | refused: the token of one set on another set | set | P | RST-010 | = |
| AZ-R03 | GET | /restic/{other login}/{set}/config | refused: another customer's path | customer | P | RST-010 | = |
| AZ-R04 | GET | /restic/{login}/{set}/keys/ | the set's token | set | P | RST-010 | = |
| AZ-R05 | DELETE | /restic/{login}/{set} | nobody (append-only: the repository is never deleted by an agent) | set | P | RST-010 | = |
| AZ-Q01 | POST | /api/replica/user | the second server's replica token only | server | P | REP-010, ST-07 | = ; pilot order: **AZF-3** |
| AZ-Q02 | PUT | /api/replica/file | replica token only | any customer's files | P | REP-010 | = |
| AZ-Q03 | POST | /api/replica/commit | replica token only | a set | P | REP-010 | = |
| AZ-Q04 | POST | /api/replica/retention | replica token only | a set | P | REP-010 | = |
| AZ-X01 | any | /api/... with `X-Agent: … Windows NT 5.x` | a signed-in computer: refused 403 SCOPE in the pilot; without a credential: 401 first | — | P | AG-08 | pilot order: **AZF-4** |

Missing and expired credentials: a customer session after 12 hours and an administrator session after 2 hours (sliding) are
refused **401 on every row** of their API (test `AuthzOracleExpiryTests`); a device token does not expire with time, it is
revoked by "disconnect" (caller `revoked`, refused 401 everywhere).

## Findings (today's code differs from the matrix)

Each is listed in `AuthzMatrix.Known` in the test: that case ends NOT TESTED naming the finding (never PASS); any other
difference fails the test, and a finding that stops reproducing fails it too until it is removed from both lists.

| Finding | Route | Caller | Expected | Actual | Severity |
|---|---|---|---|---|---|
| ~~AZF-1~~ **fixed** | AZ-S03 GET /api/sets/{copy}/sharedkey | devA, devA2 (any device token of the customer) | refused 401 SESSION — owner decision A7 "the raw key is never handed to a device token" | was: 200 with the raw key of the parent set (and the copy's empty store created). Now 401 SESSION, no store; the copy's computer gets the key at the customer's interactive sign-in there (KeyRecoveryA7Tests); removed from `AuthzMatrix.Known` | was **high** |
| AZF-2 | AZ-R01 /restic/… with the pilot on | anon | 401 (authentication first) | 403 SCOPE before the Basic credential is checked | low (feature state disclosed; nothing of a customer) |
| AZF-3 | AZ-Q01 /api/replica/… with the pilot on | anon | 401 | 403 SCOPE before the replica token | low |
| AZF-4 | AZ-X01 any /api/… with an old-Windows `X-Agent`, pilot on | anon | 401 | 403 SCOPE (`PilotScope.CheckAgent` runs before authentication) | low |
| AZF-5 | AZ-A14..A17 /api/webrestore/{set}/… with the pilot on | sesB (another customer) | the tenant refusal (403/404) before the pilot | 403 SCOPE (the feature switch runs before the set lookup; the same answer for any set id — nothing of anna's shown) | low |

**No cross-tenant finding:** another customer's device and session, and another reseller, were refused on every customer,
computer and set route, showed none of anna's names and changed none of her files or the server's settings (critical class:
0 findings).

## Observations that are not authorization

| ID | What | Where |
|---|---|---|
| AZO-1 | Two "back up the settings now" in the same second (two administrators) → the second answers **500**: both use the file name `config-yyyyMMdd-HHmmss.zip`. Reported here, not judged by the oracle (a 500 after the checks is not an authorization result). | AZ-M31, ConfigBackup.Make |
