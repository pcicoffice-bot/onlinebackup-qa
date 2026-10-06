# F2 network cut mid-backup → failure, not success → line back → next backup restores identical, no ghost

| | |
|---|---|
| Severity | CRITICAL |
| Journey | failure-recovery/f2-network-cut.spec.ts |
| When | 2026-10-06T10:24:00.596Z |
| Commit | 01e12a3 |

## Steps done (the last one is where it failed)


## Expected / actual

```
Error: dotnet OnlineBackup.Agent.dll register --home /tmp/obqa-iPAhsX/agent-qa-f2-F2-PC --server http://127.0.0.1:38373/ --login qa-f2 --password Customer-Pass-1 --computer F2-PC → exit 1
error: Not Found

```

## Console errors

(none)

## Failed API calls

(none)

## Attached

Screenshot, video and Playwright trace (`npx playwright show-trace <trace.zip>`), server log, agent log, system.xml: in `artifacts/test-results/failure-recovery-f2-networ-d207c-restores-identical-no-ghost`.
