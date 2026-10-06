# N6b the set folder on the computer is full after the server committed → server and computer tell the truth → next run correct → restore identical (SHA-256)

| | |
|---|---|
| Severity | CRITICAL |
| Journey | failure-recovery/n6-agent-disk-full.spec.ts |
| When | 2026-10-06T20:13:21.767Z |
| Commit |  |

## Steps done (the last one is where it failed)

1. 20:13:14 PROOF: the set folder has ~12 KB free: 1048576 1036288 12288  99% /tmp/obqa-1b5Obb/agent-qa-n6b-N6B-PC/sets/1791317594612 / 64 KB written there: ERR 1 head: error writing 'standard output': No space left on device
2. 20:13:17 backup 1: exit 1
BS_STOP_BY_SYSTEM_ERROR new=163 upd=0 perm=0 del=0 bytes=24198424
err:  IOException: No space left on device : '/tmp/obqa-1b5Obb/agent-qa-n6b-N6B-PC/sets/1791317594612/state.txt.tmpb8f90a7e'
3. 20:13:18 server runs: [["2026-10-06-20-13-15","BS_STOP_SUCCESS"]]
4. 20:13:18 points: ["2026-10-06-20-13-15"]; set folder: filler.bin,last-attempt.txt,state.txt.tmpb8f90a7e
5. 20:13:18 RECOVERY: room again; backup 2 and restore
6. 20:13:18 backup 2: BS_STOP_SUCCESS new=1 upd=0 perm=0 del=0 bytes=506

## Expected / actual

```
Error: the computer and the server must tell the same outcome of one run

[2mexpect([22m[31mreceived[39m[2m).[22mtoEqual[2m([22m[32mexpected[39m[2m) // deep equality[22m

[32m- Expected  - 1[39m
[31m+ Received  + 1[39m

[2m  Object {[22m
[32m-   "agentOk": true,[39m
[31m+   "agentOk": false,[39m
[2m    "serverOk": true,[22m
[2m  }[22m
```

## Console errors

(none)

## Failed API calls

(none)

## Attached

Screenshot, video and Playwright trace (`npx playwright show-trace <trace.zip>`), server log, agent log, system.xml: in `artifacts/test-results/failure-recovery-n6-agent--cbf03--restore-identical-SHA-256-`.
