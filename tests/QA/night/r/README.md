# Night round R — tenant isolation & authorization (AU-02, AU-04..07, SH-*)

Snapshot under test: commit 8bf7dc3, worktree `qa-agent-r`.
Scope: break tenant isolation / authorization across `src/Server/Api.cs` routes, agent local API
(`src/Agent/ClientUi.cs`), sessions/devices (`src/Server/Users.cs`), and the Guard (`src/Server/Guard.cs`).
Method: xUnit against the in-process real server (`new Env(...)`, HTTP). No production code changed.

## Headline finding

**R-01 (High) — a customer session token is not bound to the account; it survives deletion and is accepted
against a re-provisioned login of ANOTHER reseller.** Failing test, proven below.

---

## Route table — `src/Server/Api.cs` (isolation-relevant), who may call, and where it is tested

Legend: *dev* = device token (`X-Device`), *sess* = interactive customer session (`X-Session`, password+2FA),
*admin* = staff session, *vendor-scoped* = reseller admin limited to its own customers via `Owns()`.

### Agent routes (`/api/...`) — tenant = the login behind the token
| route | method | auth | isolation checked at | tested |
|---|---|---|---|---|
| `/api/contract`,`/api/brand` | GET | none | public | — |
| `/api/signup` | POST | none | Contract.Signup | ContractTests |
| `/api/register` | POST | password | CheckUser | AuditH (unknown-name oracle) |
| `/api/login` | POST | password | CheckUser | AuditH, FuzzTests |
| `/api/logout` | POST | sess | EndSession(token) | **NightR (logout rejects token)** |
| `/api/profile`,`/api/quota`,`/api/folders` | GET/POST | dev/sess | login from token | AuditH, FuzzTests |
| `/api/totp/{enable,confirm,disable}` | POST | sess (interactive) | login from token | AuditH_Session |
| `/api/tickets` | GET/POST | dev/sess | `Calls.List(..,login)` own only | TicketTests |
| `/api/sets` | POST | sess | CreateSet(login) | FeatureTests |
| `/api/sets/{id}/...` (key, settings, sharedkey, restic, resticreport, points, files, object, restoretest, restorelog, begin, interrupted, progress) | GET/POST | dev/sess | `prof.FindSet(setId)!=null` (own profile) → 404 else | **AuditH (by id/case/path), FuzzTests** |
| `/api/sets/{id}/jobs/{job}/{object,delete,commit,abort}` | PUT/POST | dev | own set via FindSet | AuditH, FuzzTests |
| `/api/webrestore/{id}/{points,search,ls,download}` | POST | sess (interactive) | own profile + ResticStore(login,set) | WebRestoreTests |
| `/api/client/{files,file}` | GET | dev/sess | ClientFiles allowlist | FuzzTests (`?name=../`) |

### Admin routes (`/api/admin/...`) — reseller isolation via `Owns(seg[3],vendor)` (Api.cs:891-899)
| route family | super only? | vendor-scope filter | tested |
|---|---|---|---|
| `users` GET/POST, `users/{login}/...` (sets, computers, folders, compliance, keys, security, quota, delete, unlock, resettotp, contacts, details, unfreeze, aidiagnose, untrash, move) | no (own customers) | `Owns(seg[3],vendor)` + per-item | **AuditH (`Reseller_CannotReach...`, 17 routes × 5 name tricks)** |
| `keys/{login}/{set}` | no (own) | `Owns(seg[3])` | AuditH |
| `tickets`, `tasks`, `dashboard`, `bulk`, `live`, `checks`, `insights` | no | `super||Owns(l)` filter in body | AuditH (dashboard, bulk, ticket), ResellerDashboard |
| `vendors`, `templates`, `defaults`, `guard`, `configbackup`, `time`, `contract`, `deletes`, `recycle`, `rebuild`, `verify`, `maintenance`, `homes`, system settings | yes | 403 VENDOR for non-super | (super gate at 898/904/930/991/996/1187/1193/1214) |

### Pre-auth routes
| route | handler | traversal? |
|---|---|---|
| `/admin/*`, `/restore/*` | AdminUi.Serve | **safe** — strict filename allowlist (AdminUi.cs:14) |
| `/i18n/*` | AdminUi.ServeI18n | safe — embedded resources + language allowlist |
| `/restic/{login}/{set}/...` | ApiRestic.Restic | Basic auth user==login + per-set token; FilePath from fixed Types; AbsolutePath normalizes `..` | FuzzTests probes `../../keys`, `%00` |

---

## FINDINGS

### R-01 — High — customer session not bound to account identity (survives delete, reaches reused login)
- **Capability:** AU-07 (tenant isolation), AU-06 (session lifetime).
- **Files:** `src/Server/Users.cs:439` `GetSession` (identity re-check guarded by `if (s.Admin)` only — no
  equivalent for customer sessions); `src/Server/Users.cs:235` `CheckSessionUser` (only existence/status/IP);
  `src/Server/Users.cs:361` `NewSession` (token → `Session{Login=...}`, no per-account secret/epoch);
  `src/Server/Users.cs:99` `RecycleUser` and `src/Server/Recycle.cs:88` `Execute` (delete does **not** purge
  `sessions` / `kept-sessions.xml`).
- **What happens:** Reseller *acme* has customer `reuse2026`; the customer signs in and keeps session token T.
  An admin deletes `reuse2026`. The login name is later re-provisioned as a **different** customer under reseller
  *beta*. Token T is still accepted and `GET /api/profile` returns the new account's profile — i.e. the former
  acme customer reads *beta*'s customer data. Sessions are keyed by login **name** only; the H-01 fix that re-binds a
  session to its account was added for `s.Admin` sessions and never extended to customers.
- **Evidence (test `NightR_IsolationTests.CustomerSession_SurvivesDeletion_AndReachesAReusedLoginOfAnotherReseller`):**
  ```
  FAIL: the deleted customer's session token still authenticates after the login name was reused:
  GET /api/profile returned 200 reaching the re-provisioned account owned by another reseller.
  profile=<ROOT><USER LOGIN_NAME="reuse2026" ALIAS="BetaCustomer" ... OWNER="beta" ... /></ROOT>
  ```
  Oracle is outside the product's success text: the returned profile XML carries `OWNER="beta"`/`ALIAS="BetaCustomer"`,
  the re-provisioned account, not acme's. Control in the same test: before deletion the token read `OWNER="acme"`.
- **Can-fail shown:** the test fails on current code (above). The sibling test
  `CustomerSession_AfterLogout_IsRejected` PASSES (401 after logout) — it checks a status that is 200 before logout
  and 401 after, and would fail if logout stopped revoking; together they show the server *can* revoke a token and
  simply does not on the delete path.
- **Proposed fix (orchestrator to apply, product decision noted):**
  1. On `RecycleUser` (and when a user is otherwise removed), purge that login's live sessions from `sessions` and
     remove its entries from `kept-sessions.xml`.
  2. Bind each customer session to a per-account value (e.g. a random `ACCOUNT_ID`/epoch written to Profile.xml at
     `Create`), stored on the `Session` and re-checked in `GetSession`/`CheckSessionUser`; a reused login gets a new
     id so old tokens fail even before expiry and even in-memory. This mirrors the existing admin re-check at
     Users.cs:447-451.
- **NEEDS OWNER DECISION:** whether deletion must *hard-revoke* a still-live session immediately (today a deleted,
  non-reused login already yields 404 at `CheckSessionUser`). The cross-tenant *reuse* exposure is not a policy
  choice — it is an isolation defect.

## Checked and found SAFE (negative results — no finding)

- **Guard client IP / X-Forwarded-For (SH-*, item 5).** The blocking/auth path derives the client address from
  `ctx.Request.RemoteEndPoint` only (`Api.cs:216`); `X-Forwarded-For`/`X-Real-IP` are **not** consulted for Guard,
  lockout or `ALLOWED_IPS`. So XFF spoofing can neither evade a block nor get another address blocked via the agent
  API. (XFF is read only in `LicenseCenter.cs:75` for the licence centre's external-IP display and in `Portal.cs` for
  self-URL/https hints — not security-load-bearing for isolation.) Not turned into a test (asserting a negative over
  HTTP is weak); documented here.
- **Agent local API (`ClientUi.cs`, item 4).** Listener binds `127.0.0.1` only (`:38`); Host header must be
  `127.0.0.1`/`localhost` before anything else (`:116-117`, DNS-rebinding guard); every `/api/*` call requires the
  one-time `X-Key` header (`:137`) which forces a CORS preflight the server never answers, and the key lives in the
  URL fragment (never sent, not cross-origin readable). No key bypass found.
- **Reseller ↔ reseller and customer ↔ customer for LIVE accounts** — already covered deeply by
  `AuditH_ApiTests` (17 per-customer admin routes × name/case/`%62`/trailing-dot tricks; customer set routes by
  id/`%00`/leading-zero/path; move/bulk/ticket cross-reseller). Re-reviewed `Owns()` (Api.cs:1822, unescapes then
  OrdinalIgnoreCase OWNER compare) and the vendor whitelist (Api.cs:891-899): no gap found beyond R-01.
- **Static file serving** (`/admin`, `/restore`, `/i18n`): filename allowlist, no path traversal.

## NOT DONE / NOT TESTED (ran out of budget ~2.5h)
- "Token after password change": there is **no** customer self-service password-change route in `Api.cs`, so N/A for
  customers. Staff/vendor-admin passwords *can* be changed (`Staff.cs:178`, `Vendors.cs:69`) and existing admin
  sessions are **not** invalidated by that change (GetSession checks existence/disabled/vendor, not the password hash).
  Likely the same class as R-01 for admins — reported here as a lead; **NEEDS OWNER DECISION** on whether a password
  change should drop sessions. No test written (policy-dependent).
- WebRestore `download`: `b["point"]` is echoed into the `Content-Disposition: filename="restore-<point>.zip"` header
  (`Api.cs:1906`) — possible response-header injection if HttpListener does not sanitise CR/LF. Not reproduced/tested.
- Restic object-name traversal (`seg[4]` → `ResticStore.FilePath`): reviewed; relies on URL normalisation + fixed
  `Types`. Not exhaustively fuzzed.
- H-02 TOTP step-replay and kept-session-for-deleted-admin already have dedicated coverage (AuditH_Session); not
  re-done.

## Tests added
- `tests/Tests/NightR_IsolationTests.cs`
  - `CustomerSession_SurvivesDeletion_AndReachesAReusedLoginOfAnotherReseller` — **FAIL** (proves R-01).
  - `CustomerSession_AfterLogout_IsRejected` — **PASS** (baseline; shows revocation works on the logout path).

Command: `dotnet test tests/Tests/Tests.csproj -c Debug --filter FullyQualifiedName~NightR_IsolationTests`
Result: Failed: 1, Passed: 1, Total: 2.
