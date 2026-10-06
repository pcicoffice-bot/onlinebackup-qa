# Instructions

- Following Playwright test failed.
- Explain why, be concise, respect Playwright best practices.
- Provide a snippet of code with the fix, if possible.

# Test info

- Name: failure-recovery/n7-permission-denied-and-locked.spec.ts >> N7 unreadable folder, unreadable file, exclusively locked file → success WITH ERROR naming each, nothing deleted, old versions kept → fault removed → BS_STOP_SUCCESS → restore identical (SHA-256)
- Location: failure-recovery/n7-permission-denied-and-locked.spec.ts:18:5

# Error details

```
Error: setpriv: apply bounding set: Operation not permitted


expect(received).toMatch(expected)

Expected pattern: /^BS_STOP_SUCCESS /m
Received string:  "setpriv: apply bounding set: Operation not permitted
"
```

# Test source

```ts
  1  | // N7 — Parts of the source cannot be read on the second backup:
  2  | //   a folder with no permission (chmod 000 "Many", 150 files), a single file with no permission (chmod 000, changed
  3  | //   since backup 1), and a file held under an exclusive lock by another program (flock -x: on Linux an advisory lock,
  4  | //   which .NET honours when it opens a file — NOT the same mechanism as a Windows sharing lock, which is NOT TESTED here).
  5  | // The agent runs as root on this machine, so its DAC override is dropped (setpriv bounding set) — the permission bits
  6  | // then apply to it as to any user. The faults are proven from outside: stat (the bits), the same command line without
  7  | // the override failing to read, /proc/locks and `flock -n -s` failing.
  8  | // Contract BK-05: "partly unreadable = success with error (red) … last good backup kept and still restorable …
  9  | // an err line naming that file, no deletion sent for it; after the lock is released the next run is BS_STOP_SUCCESS".
  10 | import { test, expect } from '../lib/fixtures';
  11 | import { goldenDataset, manifest, compare, restoredPath } from '../lib/world';
  12 | import { agentAsync, backupAsync, result, setRuns, points, sh, NO_DAC, keep } from '../lib/nfault';
  13 | import { spawn } from 'child_process';
  14 | import * as crypto from 'crypto';
  15 | import * as fs from 'fs';
  16 | import * as path from 'path';
  17 | 
  18 | test('N7 unreadable folder, unreadable file, exclusively locked file → success WITH ERROR naming each, nothing deleted, old versions kept → fault removed → BS_STOP_SUCCESS → restore identical (SHA-256)', async ({ world, evidence }) => {
  19 |   world.addCustomer('qa-n7');
  20 |   const ag = world.agent('qa-n7', 'N7-PC'); ag.register();
  21 |   const src = path.join(world.dir, 'data'); const v1 = goldenDataset(src);
  22 |   fs.chmodSync(world.dir, 0o755);                                   // mkdtemp makes it 700 (owner root: fine for the agent)
  23 |   const id = ag.addSet('Files', [src]);
  24 |   const opts = { wrap: NO_DAC };
> 25 |   const b1 = await backupAsync(ag, id, opts).done; expect(b1.out, b1.out).toMatch(/^BS_STOP_SUCCESS /m);
     |                                                                           ^ Error: setpriv: apply bounding set: Operation not permitted
  26 |   const p1 = points(ag, id)[0];
  27 | 
  28 |   const letter = path.join(src, 'Documents/letter.txt'), many = path.join(src, 'Many'), locked = path.join(src, 'Binary/random.bin');
  29 |   fs.writeFileSync(letter, 'changed after backup 1, then made unreadable\n');
  30 |   fs.writeFileSync(path.join(src, 'Documents/new-in-run-2.txt'), 'new in run 2\n');
  31 |   fs.writeFileSync(locked, crypto.randomBytes(64 * 1024));         // changed since backup 1: run 2 must open it
  32 |   const vNow = manifest(src);
  33 |   fs.chmodSync(letter, 0o000); fs.chmodSync(many, 0o000);
  34 |   const holder = spawn('flock', ['-x', locked, 'sleep', '600'], { stdio: 'ignore', detached: true });   // its own group: the sleep holds the lock too
  35 |   const ev: Record<string, unknown> = {};
  36 |   try {
  37 |     await new Promise((r) => setTimeout(r, 500));
  38 |     ev.proof = {
  39 |       modes: sh('stat -c "%a %U %n" "' + letter + '" "' + many + '"'),
  40 |       readFile: sh(NO_DAC.join(' ') + ' cat "' + letter + '"'),
  41 |       listFolder: sh(NO_DAC.join(' ') + ' ls "' + many + '"'),
  42 |       lockHolder: sh('cat /proc/locks | grep -c FLOCK'), lockTest: sh('flock -n -s "' + locked + '" true && echo FREE || echo HELD'),
  43 |     };
  44 |     evidence.step('PROOF of the faults: ' + JSON.stringify(ev.proof));
  45 |     expect((ev.proof as any).modes).toMatch(/^0 /m);
  46 |     expect((ev.proof as any).readFile).toMatch(/Permission denied/);
  47 |     expect((ev.proof as any).listFolder).toMatch(/Permission denied/);
  48 |     expect((ev.proof as any).lockTest).toMatch(/HELD/);
  49 | 
  50 |     const b2 = await backupAsync(ag, id, opts).done;
  51 |     ev.backup2 = b2.out;
  52 |     evidence.step('backup 2: exit ' + b2.code + '\n' + b2.out.trim());
  53 |     expect(result(b2.out), 'partly unreadable = success with error (BK-05)').toBe('BS_STOP_SUCCESS_WITH_ERROR');
  54 |     expect(b2.out, 'the unreadable file is named').toMatch(/letter\.txt/);
  55 |     expect(b2.out, 'the unreadable folder is named').toMatch(/Many/);
  56 |     // a Linux flock is advisory: whether .NET's open (FileShare.ReadWrite) is refused by it is the runtime's choice —
  57 |     // either the file is read whole (new version in the point) or it is named as an error (old version kept)
  58 |     const lockNamed = /random\.bin/.test(b2.out); ev.lockNamed = lockNamed;
  59 |     evidence.step('the locked file named as not read: ' + lockNamed);
  60 |     expect(b2.out, 'nothing deleted for what could not be read').toMatch(/ del=0 /);
  61 |     const runs = await setRuns(world, 'qa-n7', id);
  62 |     expect(runs[0].result, 'the server records the same result').toBe('BS_STOP_SUCCESS_WITH_ERROR');
  63 |     expect(runs[0].status, 'shown red').toBe('bad');
  64 | 
  65 |     evidence.step('ORACLE during the fault: the newest point keeps the last good version of what could not be read');
  66 |     const expected = new Map(v1); expected.set('Documents/new-in-run-2.txt', vNow.get('Documents/new-in-run-2.txt')!);
  67 |     if (!lockNamed) expected.set('Binary/random.bin', vNow.get('Binary/random.bin')!);
  68 |     const tn = path.join(world.dir, 'restore-during'); const rn = agentAsync(ag, ['restore', '--set', id, '--password', 'Customer-Pass-1', '--target', tn]);
  69 |     const rr = await rn.done; expect(rr.code, rr.out).toBe(0);
  70 |     expect(compare(expected, manifest(restoredPath(tn, src))), 'newest point = backup 1 + the new file (unreadable ones kept as they were)').toEqual([]);
  71 |   } finally {
  72 |     try { process.kill(-holder.pid!, 'SIGKILL'); } catch { }
  73 |     await new Promise((r) => setTimeout(r, 500));
  74 |     ev.lockAfterRelease = sh('flock -n -s "' + locked + '" true && echo FREE || echo HELD');
  75 |     try { fs.chmodSync(letter, 0o644); fs.chmodSync(many, 0o755); } catch { }
  76 |     keep('n7', ev);
  77 |   }
  78 | 
  79 |   evidence.step('RECOVERY: permissions back, lock released → backup 3');
  80 |   const b3 = await backupAsync(ag, id, opts).done;
  81 |   evidence.step('backup 3: ' + b3.out.trim());
  82 |   expect(result(b3.out), b3.out).toBe('BS_STOP_SUCCESS');
  83 |   expect(b3.out, 'the file changed during the fault is sent now').toMatch(/upd=[1-9]/);
  84 |   const t = path.join(world.dir, 'restore'); const r = ag.restore(id, t); expect(r.code, r.out).toBe(0);
  85 |   expect(compare(vNow, manifest(restoredPath(t, src)))).toEqual([]);
  86 |   const t1 = path.join(world.dir, 'restore-1'); const r1 = ag.restore(id, t1, ['--point', p1]); expect(r1.code, r1.out).toBe(0);
  87 |   expect(compare(v1, manifest(restoredPath(t1, src)))).toEqual([]);
  88 | });
  89 | 
```