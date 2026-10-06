# Code review guide — Online Backup server and client

> For an outside reviewer (a person or another AI). It says what the product is, how the code is laid out, how to
> build and test it, the security model, and where a careful look is most valuable. The owner's language is Hebrew;
> the code and this guide are in English.

## 1. What it is

A backup product for an IT company that serves many small businesses, in the manner of Ahsay CBS/OBM:

- **Server** (`src/Server`, .NET 8, Windows Server; runs on Linux for tests): stores every customer's encrypted backups,
  the admin website (`src/Server/Web`), web restore, service calls, reports, licensing.
- **Client / agent** (`src/Agent`, .NET 4.0 **and** .NET 8 — it must run on Windows Server 2003 up to today, Linux, Mac):
  backs up files, SQL Server, MySQL/PostgreSQL, Oracle, Domino, Hyper-V, VMware, Microsoft 365, Google Workspace,
  System State, whole-disk images; restores; serves the customer's own screen on localhost (`src/Agent/client.html`).
- **Core** (`src/Core`, both frameworks): encryption, chunking, the stored-object format, profiles, messages, i18n.
- Two storage engines per set: the product's own (Ahsay 6 layout, `SetStore.cs` / `BackupRun.cs`) and **restic**
  (`ResticRunner.cs` on the agent, `ResticStore.cs` + `ApiRestic.cs` as its REST backend on the server).
- No database server: every piece of state is a file (XML profiles and settings, append-only logs, the object store).

About 16,000 lines of C# (Core 10 files, Server 36, Agent 24) and the web UI in plain JavaScript (`app.js`, no framework).

## 2. Build and test

```sh
OB_RESTIC=/path/to/restic ./run-tests.sh          # ~135 tests, ~9 minutes; without OB_RESTIC the restic tests skip
OB_LOAD=1 dotnet test tests/Tests --filter LoadTests   # 500 customers / 5,000 sets / 50 computers at once
OB_RESTIC=/path/to/restic tools/ui-check/run.sh        # screen robot: every screen, 15 languages, computer + phone
OB_RESTIC=/path/to/restic ROBOT=options tools/ui-check/run.sh   # every field of every editor: change, save, read back
```

The quality gate (`.github/workflows/online-backup.yml`, `docs/QUALITY-GATE.he.md`) runs all of them; no package is
built unless everything passes. `tests/Tests/OptionsTests.cs` fails when a backup-set option has no test that
proves what it does.

## 3. Security model (please challenge it)

| Area | How | Where |
|---|---|---|
| Backup content | Encrypted on the client before upload: AES-256 + HMAC-SHA256, separate keys for content, MAC and names (96 bytes). Password-type keys: PBKDF2, 200,000 iterations. The server never has a key unless the customer allows "keep the key for recovery" | `Core/Crypto.cs`, `Core/BackupObject.cs` |
| Key kept for recovery | Protected with DPAPI (machine scope) on Windows, a server key file elsewhere; reading it back is admin-only and logged | `Server/Infra.cs` (KeyVault) |
| Passwords of people | PBKDF2 (`pbkdf2$iterations$salt$hash`), 100,000 iterations; password rule; account lock after wrong passwords (cannot be switched off) | `Core/Crypto.cs` (PasswordHash), `Users.cs`, `Staff.cs` |
| Administrators | Two-step verification (TOTP, RFC 6238) is mandatory; sessions by `X-Session` header | `Staff.cs`, `Users.cs` |
| Computers | A device token per registered computer (only its SHA-256 is stored); revocable; restore needs an interactive sign-in, the device token alone cannot download data | `Users.CheckDevice`, `Api.cs` |
| Attacks from the internet | An address with 10 wrong sign-ins / 4 user names / 30 tokenless refused requests in 10 minutes is blocked for 24 h; private networks and this server never; alerts by mail | `Guard.cs`, `GuardTests.cs` |
| Allowed addresses | Per customer and per server, IPv4/IPv6 ranges | `Users.CheckIp`, `SecurityTests.cs` |
| Transport | HTTPS; the agent can pin the server's certificate; .NET 4.0 on old Windows uses a built-in TLS 1.2 (BouncyCastle) | `Agent/BuiltinTls.cs`, `TlsTests.cs` |
| Secrets on the client | SQL / database passwords, M365 / Google keys: kept protected on the client only, never sent to the server; the SQL password reaches `sqlcmd` in its environment, never on the command line | `Agent/LocalState.cs`, `Agent/Sources.cs` |
| Deletion | Nothing is erased at once: a recycle bin for 14 days; optionally a second administrator must approve | `Recycle.cs` |
| Ransomware | A backup that changes too many files freezes the old versions until an administrator releases them | `Api.cs` (RANSOM_*), `FeatureTests.cs` |
| Logs | Never a password: sign-in failures log the address and the name tried only | `Guard.cs`, `SysLog` |

Known choices worth a second opinion:

- PBKDF2 uses `Rfc2898DeriveBytes` with its default HMAC-SHA1, because the agent must run on .NET 4.0 (no SHA-256
  overload there). Is the iteration count enough, and should the server side (people's passwords, .NET 8) move to
  SHA-256 with a version prefix?
- Admin requests are XML bodies; check every place that builds XML or a file path from input (`Api.cs`, `SetControl.cs`,
  `FolderTree.cs`, `ConfigBackup.PathOf`, `ResticStore.ValidName`).
- `sqlcmd` / `osql` are started with a query string built from database names (`Sources.cs`, `SqlBackup`): names are
  bracket-quoted and `'` doubled — please look for injection paths.
- The restic REST backend on the server (`ApiRestic.cs`): an agent must not delete its repository or its config, and
  every upload must match its SHA-256 name.

## 4. Data integrity (please challenge it too)

- Own engine: a run becomes a restore point only at commit; the commit writes a journal first, so a crash is rolled
  forward or back (`SetStore.cs`, `EndToEndTests.CrashDuringCommit…`).
- A damaged stored object is detected (MAC), quarantined and sent again from the source (`EndToEndTests.Damaged…`).
- The index can be rebuilt from the stored objects (`RebuildRecreatesTheIndexFromTheDisk`).
- A full server disk mid-backup leaves earlier backups intact (`ReliabilityTests`).
- Replication to a second server replays commits in order (`Replication.cs`).

## 5. Where a careful look is most valuable

1. `src/Server/Api.cs` (1,700 lines): every route, its authorisation (`super` / vendor / customer / device), and input
   validation. Vendors (resellers) must see only their own customers.
2. `src/Server/SetStore.cs` and `src/Agent/BackupRun.cs`: delta chains, retention, the commit journal.
3. `src/Core/Crypto.cs`, `src/Core/BackupObject.cs`: the format, nonce/IV handling, MAC-then-decrypt order.
4. Concurrency: `Users.cs` has one lock for profile changes; the users index is cached and dropped on every write
   (`FreshIndex` / `WriteIndex`); device check-ins read without the lock. Look for lost updates between the admin site
   and the agent writing the same profile.
5. `src/Agent/ClientUi.cs`: a local web server on the customer's computer — it must answer only this computer and
   only the signed-in user.
6. The web UI (`src/Server/Web/app.js`): every text goes through `h()` (DOM nodes, no innerHTML); look for any place
   that is not.

## 6. What the review should produce

A list of findings, each with: file and line, what is wrong, a concrete failing input or scenario, severity
(critical / high / medium / low), and the smallest fix. Please separate real defects from style preferences.

### A prompt to start with

> You are reviewing a C# backup product (server .NET 8, client .NET 4.0 + .NET 8). Read `docs/REVIEW-GUIDE.md`
> first. Then review for security (authentication, authorisation between customers and resellers, injection into
> XML / file paths / sqlcmd, secrets in logs), data integrity (commit journal, retention, delta chains, crash at any
> point) and concurrency (two writers of one profile). For each finding give file:line, a concrete scenario that
> fails, severity, and the smallest fix. Do not report style.
