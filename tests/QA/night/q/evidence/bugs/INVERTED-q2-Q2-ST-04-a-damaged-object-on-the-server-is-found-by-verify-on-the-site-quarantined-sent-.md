# INVERTED q2 — Q2 ST-04 a damaged object on the server is found by verify on the site, quarantined, sent again, and the next restore is identical

| | |
|---|---|
| Severity | CRITICAL |
| Journey | e2e/zz-inv-q2.spec.ts |
| When | 2026-10-07T03:03:08.573Z |
| Commit |  |

## Steps done (the last one is where it failed)

1. 03:02:59 sign in as the administrator
2. 03:03:06 FAULT: one stored object (the one of Documents/letter.txt is not known from outside, so: the smallest object) gets one byte flipped
3. 03:03:06 PROOF of the fault: the bytes on disk changed (3805f5f06b0f → 404b83a7b115)
4. 03:03:06 PROOF of the fault: a restore now fails for exactly one file
5. 03:03:08 the site: open the set, Maintenance, Check the data (verify)
6. 03:03:08 ORACLE (disk): the damaged object left the store and is in the set's Quarantine, byte for byte the damaged one

## Expected / actual

```
Error: [2mexpect([22m[31mreceived[39m[2m).[22mtoBe[2m([22m[32mexpected[39m[2m) // Object.is equality[22m

Expected: [32m"[7m3805f5f06b0f0c216546d7ad52ad5886c1b6845268abf8189eb6a7614ffa8[27mbe[7mc[27m"[39m
Received: [31m"[7m404b83a7b1155200bb56a47142f5a7a7a5bb9fab022c652ea1dab03ba2f3fe[27mbe"[39m
```

## Console errors

(none)

## Failed API calls

(none)

## Attached

Screenshot, video and Playwright trace (`npx playwright show-trace <trace.zip>`), server log, agent log, system.xml: in `artifacts/test-results/e2e-zz-inv-q2-INVERTED-q2--e6903-e-next-restore-is-identical`.
