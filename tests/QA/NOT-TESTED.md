Every item here was NOT run. None of them counts as PASS.

| Area | Why not yet | What is needed |
|---|---|---|
| Real Windows: server install as a service, client installer robot, backup / restore / SHA-256 on Windows, service killed, uninstall, reinstall | Written (`tests/QA/windows/`), not run yet: the Windows machines of GitHub are free only on a public repository, and the public mirror is not created yet | The owner creates the public repository `onlinebackup-qa`; the mirror is pushed; `.github/workflows/qa.yml` runs it |
| Reboot of a real computer (idle, during a backup, after an update) | GitHub's Windows machines cannot reboot and continue a test | A Windows VM of our own as a GitHub runner (a script is ready to register it) |
| Client window (ClientApp) robot — every screen and control through UI Automation | Not written yet (the CI already types into every page of the window, UI-030) | Next step after the installer robot runs green |
| Update of a real installed client and server, then backup + restore | Update logic is tested (UpdateInstallTests, UpdaterTests); a real Windows update chain is not | Windows job: install version N, update to N+1, smoke backup + restore |
| VSS: a shadow copy that cannot be deleted after the backup is not reported (static review, open) | Windows only | Windows test: block the delete, expect a warning in the run |
| VSS, locked files, System State, SQL Server, Hyper-V, Exchange, Oracle, Domino on Windows | Need Windows Server with those products | Windows runner with SQL Server Express (scripted) for SQL; others need a lab |
| Disk full on the client (temp folder, local copy, restore target) | The server's full disk is tested (QA F6, bug 13); the client's is not simulated yet | A small mounted disk in the QA world |
| Soak / long run (hours of change → backup → restore sample) | Suite not written yet | `tests/QA/soak/` nightly |
| Exploratory runs by the Planner agent on a schedule | Run once by hand through the Playwright MCP (found bugs 7 and 8); not scheduled | A nightly Claude Code session with the planner agent |
| macOS / Linux client installers | No Mac; Linux client package not robot-tested | — |
