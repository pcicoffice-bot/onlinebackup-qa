# Instructions

- Following Playwright test failed.
- Explain why, be concise, respect Playwright best practices.
- Provide a snippet of code with the fix, if possible.

# Test info

- Name: failure-recovery/n9-repository-unavailable.spec.ts >> N9b the server stopped for the whole run → the run fails at once (time measured), no false success → server back → no ghost → backup → restore identical (SHA-256)
- Location: failure-recovery/n9-repository-unavailable.spec.ts:71:5

# Error details

```
Error: expect(received).toMatch(expected)

Expected pattern: /ECONNREFUSED/
Received string:  "AggregateError"
```

# Test source

```ts
  1   | // N9 — The repository cannot be used for a whole backup run:
  2   | //   (a) the server's folder of the set (<users>/<login>/files/<set>) is READ-ONLY (a read-only bind mount: what a
  3   | //       storage that went read-only after an error looks like; the server runs as root, so chmod would not stop it)
  4   | //   (b) the server is STOPPED for the whole run (F1 kills it in the middle; here it is down from the start)
  5   | // Proof from outside: findmnt shows the mount "ro" and a write there fails with EROFS; the server process is gone and
  6   | // its port refuses connections.
  7   | // Certainly required: no false success, nothing half stored, point 1 still restorable, the next backup after the fault
  8   | // succeeds and restores identical. What the server must RECORD of a run it could not even begin is not written down:
  9   | // observed and reported (NEEDS OWNER DECISION when it is nothing).
  10  | import { test, expect } from '../lib/fixtures';
  11  | import { goldenDataset, manifest, compare, restoredPath } from '../lib/world';
  12  | import { result, setRuns, points, sh, keep } from '../lib/nfault';
  13  | import { notWhole } from '../lib/fault';
  14  | import * as fs from 'fs';
  15  | import * as net from 'net';
  16  | import * as path from 'path';
  17  | 
  18  | test('N9a the server\'s set folder read-only for a whole run → no false success, point 1 restorable during the fault → writable again → backup → restore identical (SHA-256)', async ({ world, evidence }) => {
  19  |   world.addCustomer('qa-n9a');
  20  |   const ag = world.agent('qa-n9a', 'N9A-PC'); ag.register();
  21  |   const src = path.join(world.dir, 'data'); const v1 = goldenDataset(src);
  22  |   const id = ag.addSet('Files', [src]);
  23  |   const b1 = ag.backup(id); expect(b1.out, b1.out).toMatch(/^BS_STOP_SUCCESS /m);
  24  |   const p1 = points(ag, id)[0];
  25  |   const setFolder = path.join(world.usersDir, 'qa-n9a', 'files', id);
  26  |   expect(fs.existsSync(setFolder), 'the set\'s folder on the server: ' + setFolder).toBe(true);
  27  |   const mounted = sh('mount --bind "' + setFolder + '" "' + setFolder + '" && mount -o remount,bind,ro "' + setFolder + '" && echo OK');
  28  |   test.skip(mounted !== 'OK', 'NOT TESTED: this machine does not allow a read-only bind mount: ' + mounted);
  29  |   const ev: Record<string, unknown> = {};
  30  |   try {
  31  |     ev.proof = { findmnt: sh('findmnt -no OPTIONS "' + setFolder + '"'), write: sh('touch "' + setFolder + '/probe"') };
  32  |     evidence.step('PROOF: ' + JSON.stringify(ev.proof));
  33  |     expect((ev.proof as any).findmnt).toMatch(/^ro,/);
  34  |     expect((ev.proof as any).write).toMatch(/Read-only file system/);
  35  |     fs.writeFileSync(path.join(src, 'Documents/letter.txt'), 'changed in run 2\n');
  36  |     const before = sh('find "' + setFolder + '" -type f | wc -l');
  37  |     const b2 = ag.backup(id);
  38  |     ev.run2 = b2.out; ev.serverLog = fs.readFileSync(world.serverLog, 'utf8').split('\n').filter((l) => /read-only|Read-only|error/i.test(l)).slice(-10);
  39  |     evidence.step('run 2: exit ' + b2.code + '\n' + b2.out.trim());
  40  |     expect(b2.code, 'never reported successful').not.toBe(0);
  41  |     expect(result(b2.out)).not.toMatch(/^BS_STOP_SUCCESS/);
  42  |     expect(sh('find "' + setFolder + '" -type f | wc -l'), 'nothing was written in the read-only folder').toBe(before);
  43  |     const runs = await setRuns(world, 'qa-n9a', id);
  44  |     ev.runs = runs;
  45  |     evidence.step('server runs: ' + JSON.stringify(runs.map((r) => [r.job, r.result])));
  46  |     expect(runs.filter((r) => r.status === 'ok').length, 'no success recorded for run 2').toBe(1);
  47  |     expect(await world.live()).not.toContain(id);
  48  |     const pl = ag.cli(['points', '--set', id], true); ev.pointsDuring = pl.out;
  49  |     evidence.step('points during the fault: exit ' + pl.code + ' ' + pl.out.trim());
  50  |     const tf = path.join(world.dir, 'restore-during'); const rf = ag.restore(id, tf, ['--point', p1]);
  51  |     ev.restoreDuring = rf.out; ev.serverSysLog = sh('grep -rh error "' + path.join(world.sys, 'logs') + '" | tail -5');
  52  |     evidence.step('restore of point 1 during the fault: exit ' + rf.code + ' ' + rf.out.trim() + '\nserver system log: ' + ev.serverSysLog);
  53  |     expect(notWhole(v1, manifest(restoredPath(tf, src))), 'never a half file passing as whole').toEqual([]);
  54  |     // soft: the recovery below is still checked. A read-only store holds every earlier point; reading it needs no write
  55  |     expect.soft(pl.code, 'the points of a read-only repository can be listed:\n' + pl.out).toBe(0);
  56  |     expect.soft(rf.code, 'point 1 of a read-only repository can be restored:\n' + rf.out).toBe(0);
  57  |     expect.soft(compare(v1, manifest(restoredPath(tf, src)))).toEqual([]);
  58  |   } finally { sh('umount -l "' + setFolder + '"'); keep('n9a', ev); }
  59  | 
  60  |   evidence.step('RECOVERY: writable again → backup 3');
  61  |   const v3 = manifest(src);
  62  |   const b3 = ag.backup(id); expect(b3.out, b3.out).toMatch(/^BS_STOP_SUCCESS /m);
  63  |   expect(points(ag, id).length, 'two points: backup 1 and backup 3 (none from the failed run)').toBe(2);
  64  |   const t = path.join(world.dir, 'restore'); const r = ag.restore(id, t); expect(r.code, r.out).toBe(0);
  65  |   expect(compare(v3, manifest(restoredPath(t, src)))).toEqual([]);
  66  |   const t1 = path.join(world.dir, 'restore-1'); const r1 = ag.restore(id, t1, ['--point', p1]); expect(r1.code, r1.out).toBe(0);
  67  |   expect(compare(v1, manifest(restoredPath(t1, src)))).toEqual([]);
  68  |   evidence.step('server runs after recovery: ' + JSON.stringify((await setRuns(world, 'qa-n9a', id)).map((x) => [x.job, x.result])));
  69  | });
  70  | 
  71  | test('N9b the server stopped for the whole run → the run fails at once (time measured), no false success → server back → no ghost → backup → restore identical (SHA-256)', async ({ world, evidence }) => {
  72  |   world.addCustomer('qa-n9b');
  73  |   const ag = world.agent('qa-n9b', 'N9B-PC'); ag.register();
  74  |   const src = path.join(world.dir, 'data'); const v1 = goldenDataset(src);
  75  |   const id = ag.addSet('Files', [src]);
  76  |   const b1 = ag.backup(id); expect(b1.out, b1.out).toMatch(/^BS_STOP_SUCCESS /m);
  77  |   const pid = world.server!.pid!;
  78  |   world.killServer();
  79  |   await new Promise((r) => setTimeout(r, 1000));
  80  |   const port = Number(new URL(world.url).port);
  81  |   const refused = await new Promise<string>((r) => { const s = net.connect({ port, host: 'localhost' }); s.on('connect', () => { s.destroy(); r('CONNECTED'); }); s.on('error', (e) => r(String(e))); });
  82  |   const ev: Record<string, unknown> = { proof: { process: sh('ps -p ' + pid + ' -o stat= || echo GONE'), port: refused } };
  83  |   evidence.step('PROOF: ' + JSON.stringify(ev.proof));
> 84  |   expect(refused).toMatch(/ECONNREFUSED/);
      |                   ^ Error: expect(received).toMatch(expected)
  85  |   fs.writeFileSync(path.join(src, 'Documents/letter.txt'), 'changed while the server was down\n');
  86  |   const t0 = Date.now(); const b2 = ag.backup(id); ev.ms = Date.now() - t0; ev.run2 = b2.out;
  87  |   evidence.step('run 2 (server down): exit ' + b2.code + ' in ' + ev.ms + ' ms\n' + b2.out.trim());
  88  |   expect(b2.code).not.toBe(0);
  89  |   expect(result(b2.out)).not.toMatch(/^BS_STOP_SUCCESS/);
  90  |   expect(b2.out, 'the reason names the server / the connection').toMatch(/connection|server/i);
  91  |   await world.startServer();
  92  |   const runs = await setRuns(world, 'qa-n9b', id);
  93  |   ev.runsAfterRestart = runs;
  94  |   evidence.step('server runs after it is back (before any new run): ' + JSON.stringify(runs.map((r) => [r.job, r.result])));
  95  |   expect(await world.live()).not.toContain(id);
  96  |   const v3 = manifest(src);
  97  |   const b3 = ag.backup(id); expect(b3.out, b3.out).toMatch(/^BS_STOP_SUCCESS /m);
  98  |   const t = path.join(world.dir, 'restore'); const r = ag.restore(id, t); expect(r.code, r.out).toBe(0);
  99  |   expect(compare(v3, manifest(restoredPath(t, src)))).toEqual([]);
  100 |   const t1 = path.join(world.dir, 'restore-1'); const r1 = ag.restore(id, t1, ['--point', points(ag, id)[0]]); expect(r1.code, r1.out).toBe(0);
  101 |   expect(compare(v1, manifest(restoredPath(t1, src)))).toEqual([]);
  102 |   ev.runsEnd = (await setRuns(world, 'qa-n9b', id)).map((x) => [x.job, x.result]);
  103 |   keep('n9b', ev);
  104 | });
  105 | 
```