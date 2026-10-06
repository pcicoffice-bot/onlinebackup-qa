# OnlineBackup — QA report

| | |
|---|---|
| Version | dev |
| Commit | 01e12a3 |
| Mode | candidate |
| Environment | linux x64, node v22.22.2 |
| Start / end | 2026-10-06T10:11:18.327Z → 2026-10-06T10:31:16.535Z (20 min) |

## Verdict: **NOT READY**

Reasons:

- Failure / recovery suite: FAIL
- Regression tests of every known bug: FAIL
- Install smoke on real Windows (server + client + service + backup + restore): NOT TESTED
- Update on real Windows, then backup + restore: NOT TESTED
- Reboot during and after a backup on real Windows: NOT TESTED

## Build

- Core: PASS
- Server: PASS
- Agent (net40 + net8): PASS
- ClientApp: PASS
- Setup: PASS
- Tests: PASS

## Tests by level

| Level | Tests | Passed | Failed | Result |
|---|---|---|---|---|
| L1 Unit | 17 | 17 | 0 | PASS |
| L2 Integration | 77 | 77 | 0 | PASS |
| L3 API | 8 | 8 | 0 | PASS |
| L4 UI automation | 0 | 0 | 0 | NOT TESTED |
| L5 Full E2E | 48 | 48 | 0 | PASS |
| L6 Failure / Recovery | 17 | 16 | 1 | **FAIL** |
| L7 Install / Upgrade / Reboot | 19 | 19 | 0 | PASS |
| L8 Soak / Chaos | 0 | 0 | 0 | NOT TESTED |

## Release gate

| Item | Result |
|---|---|
| Build of every component | PASS |
| Unit tests | PASS |
| Integration tests | PASS |
| Critical UI flow: sign-in and navigation | PASS |
| Critical UI flow: edit a backup set (reaches the computer) | PASS |
| Backup E2E (Back up now → restore → SHA-256) | PASS |
| Restore E2E + SHA-256 (lost data, older version) | PASS |
| Crash recovery (agent killed mid-backup) | PASS |
| Failure / recovery suite | **FAIL** |
| Regression tests of every known bug | **FAIL** |
| Update E2E | PASS |
| Install smoke on real Windows (server + client + service + backup + restore) | **NOT TESTED** |
| Update on real Windows, then backup + restore | **NOT TESTED** |
| Reboot during and after a backup on real Windows | **NOT TESTED** |

## Failures

- **QA failure-recovery/f2-network-cut.spec.ts › F2 network cut mid-backup → failure, not success → line back → next backup restores identical, no ghost**
  ```
  Error: dotnet OnlineBackup.Agent.dll register --home /tmp/obqa-iPAhsX/agent-qa-f2-F2-PC --server http://127.0.0.1:38373/ --login qa-f2 --password Customer-Pass-1 --computer F2-PC → exit 1
  error: Not Found
  
  ```

Evidence of each failure (screenshot, video, trace, console, network, server and agent logs): `tests/QA/reports/bugs/` and `tests/QA/artifacts/`.

## NOT TESTED

Every item here was NOT run. None of them counts as PASS.

| Area | Why not yet | What is needed |
|---|---|---|
| Real Windows: server install as a service, client installer robot, backup / restore / SHA-256 on Windows, service killed, uninstall, reinstall | Written (`tests/QA/windows/`), not run yet: the Windows machines of GitHub are free only on a public repository, and the public mirror is not created yet | The owner creates the public repository `onlinebackup-qa`; the mirror is pushed; `.github/workflows/qa.yml` runs it |
| Reboot of a real computer (idle, during a backup, after an update) | GitHub's Windows machines cannot reboot and continue a test | A Windows VM of our own as a GitHub runner (a script is ready to register it) |
| Client window (ClientApp) robot — every screen and control through UI Automation | Not written yet (the CI already types into every page of the window, UI-030) | Next step after the installer robot runs green |
| Update of a real installed client and server, then backup + restore | Update logic is tested (UpdateInstallTests, UpdaterTests); a real Windows update chain is not | Windows job: install version N, update to N+1, smoke backup + restore |
| VSS, locked files, System State, SQL Server, Hyper-V, Exchange, Oracle, Domino on Windows | Need Windows Server with those products | Windows runner with SQL Server Express (scripted) for SQL; others need a lab |
| Permission denied (a folder the backup cannot read) | The Linux runs are root: permissions do not stop root | Run the agent as a normal user in the QA world |
| Disk full on the server or the client | Not simulated yet | A small mounted disk in the QA world |
| Soak / long run (hours of change → backup → restore sample) | Suite not written yet | `tests/QA/soak/` nightly |
| Exploratory runs by the Planner agent on a schedule | Run once by hand through the Playwright MCP (found bugs 7 and 8); not scheduled | A nightly Claude Code session with the planner agent |
| macOS / Linux client installers | No Mac; Linux client package not robot-tested | — |

