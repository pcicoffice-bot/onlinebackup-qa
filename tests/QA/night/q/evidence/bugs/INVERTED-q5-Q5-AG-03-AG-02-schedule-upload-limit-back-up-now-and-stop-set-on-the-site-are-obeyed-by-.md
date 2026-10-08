# INVERTED q5 — Q5 AG-03/AG-02 schedule, upload limit, back up now and stop set on the site are obeyed by the running agent service

| | |
|---|---|
| Severity | MAJOR |
| Journey | e2e/zz-inv-q5.spec.ts |
| When | 2026-10-07T03:15:22.399Z |
| Commit |  |

## Steps done (the last one is where it failed)

1. 03:13:09 sign in as the administrator
2. 03:13:14 the customer's first backup (from the client program), then the service runs
3. 03:13:16 Schedule on the site: 03:15 (the agent works in the computer's local time)
4. 03:13:17 ORACLE (agent): the service runs the backup by itself at that minute, not before

## Expected / actual

```
Error: the run at 2026-10-07T03:15:17 is not before the slot 2026-10-07T03:15:00

[2mexpect([22m[31mreceived[39m[2m).[22mtoBe[2m([22m[32mexpected[39m[2m) // Object.is equality[22m

Expected: [32mfalse[39m
Received: [31mtrue[39m
```

## Console errors

(none)

## Failed API calls

(none)

## Attached

Screenshot, video and Playwright trace (`npx playwright show-trace <trace.zip>`), server log, agent log, system.xml: in `artifacts/test-results/e2e-zz-inv-q5-INVERTED-q5--70a85-y-the-running-agent-service`.
