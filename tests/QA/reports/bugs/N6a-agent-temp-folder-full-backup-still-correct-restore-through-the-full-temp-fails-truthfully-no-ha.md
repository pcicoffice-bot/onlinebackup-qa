# N6a agent temp folder full → backup still correct; restore through the full temp fails truthfully, no half file → temp freed → restore identical (SHA-256)

| | |
|---|---|
| Severity | CRITICAL |
| Journey | failure-recovery/n6-agent-disk-full.spec.ts |
| When | 2026-10-06T19:52:08.775Z |
| Commit |  |

## Steps done (the last one is where it failed)

1. 19:51:43 PROOF: the temp folder is full: 2097152 2097152     0 100% /tmp/obqa-lN7gjY/agent-qa-n6a-N6A-PC/temp / a write there: ERR 1 dd: error writing '/tmp/obqa-lN7gjY/agent-qa-n6a-N6A-PC/temp/probe.bin': No space left on device
1+0 records in
0+0 records out
0 bytes copied, 6.1192e-05 s, 0.0 kB/s
2. 19:51:46 backup with a full temp folder: BS_STOP_SUCCESS new=163 upd=0 perm=0 del=0 bytes=24198424
3. 19:51:46 restore of 2 files (filter "Duplicates") while the temp folder is full
4. 19:52:06 restore: exit 1 in 20424 ms
restored=0 failed=2 skipped=0
  1791316316928,err,/tmp/obqa-lN7gjY/restore-during/tmp/obqa-lN7gjY/data/Duplicates/b.txt,0,The download failed: No space left on device : '/tmp/obqa-lN7gjY/agent-qa-n6a-N6A-PC/temp/89be7ac96e49484ea3b7b6c99ecebc7d.obj',,,0
  1791316326952,err,/tmp/obqa-lN7gjY/restore-during/tmp/obqa-lN7gjY/data/Duplicates/a.txt,0,The download failed: No space left on device : '/tmp/obqa-lN7gjY/agent-qa-n6a-N6A-PC/temp/7836b8e4058b40e38e93bd2610e71e79.obj',,,0
temp leftovers: ["7836b8e4058b40e38e93bd2610e71e79.obj 0","89be7ac96e49484ea3b7b6c99ecebc7d.obj 0"]
5. 19:52:06 RECOVERY: the temp folder has room again; a new backup and a restore

## Expected / actual

```
Error: the failed downloads leave nothing in the temp folder

[2mexpect([22m[31mreceived[39m[2m).[22mtoEqual[2m([22m[32mexpected[39m[2m) // deep equality[22m

[32m- Expected  - 1[39m
[31m+ Received  + 4[39m

[32m- Array [][39m
[31m+ Array [[39m
[31m+   "7836b8e4058b40e38e93bd2610e71e79.obj",[39m
[31m+   "89be7ac96e49484ea3b7b6c99ecebc7d.obj",[39m
[31m+ ][39m
```

## Console errors

(none)

## Failed API calls

(none)

## Attached

Screenshot, video and Playwright trace (`npx playwright show-trace <trace.zip>`), server log, agent log, system.xml: in `../../../agent-n-pw/n6/failure-recovery-n6-agent--6706c--restore-identical-SHA-256-`.
