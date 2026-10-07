// N9 — The repository cannot be used for a whole backup run:
//   (a) the server's folder of the set (<users>/<login>/files/<set>) is READ-ONLY (a read-only bind mount: what a
//       storage that went read-only after an error looks like; the server runs as root, so chmod would not stop it)
//   (b) the server is STOPPED for the whole run (F1 kills it in the middle; here it is down from the start)
// Proof from outside: findmnt shows the mount "ro" and a write there fails with EROFS; the server process is gone and
// its port refuses connections.
// Certainly required: no false success, nothing half stored, point 1 still restorable, the next backup after the fault
// succeeds and restores identical. What the server must RECORD of a run it could not even begin is not written down:
// observed and reported (NEEDS OWNER DECISION when it is nothing).
import { test, expect } from '../lib/fixtures';
import { goldenDataset, manifest, compare, restoredPath } from '../lib/world';
import { result, setRuns, points, sh, keep } from '../lib/nfault';
import { notWhole } from '../lib/fault';
import * as fs from 'fs';
import * as net from 'net';
import * as path from 'path';

test('N9a the server\'s set folder read-only for a whole run → no false success, point 1 restorable during the fault → writable again → backup → restore identical (SHA-256)', async ({ world, evidence }) => {
  world.addCustomer('qa-n9a');
  const ag = world.agent('qa-n9a', 'N9A-PC'); ag.register();
  const src = path.join(world.dir, 'data'); const v1 = goldenDataset(src);
  const id = ag.addSet('Files', [src]);
  const b1 = ag.backup(id); expect(b1.out, b1.out).toMatch(/^BS_STOP_SUCCESS /m);
  const p1 = points(ag, id)[0];
  const setFolder = path.join(world.usersDir, 'qa-n9a', 'files', id);
  expect(fs.existsSync(setFolder), 'the set\'s folder on the server: ' + setFolder).toBe(true);
  const mounted = sh('mount --bind "' + setFolder + '" "' + setFolder + '" && mount -o remount,bind,ro "' + setFolder + '" && echo OK');
  test.skip(mounted !== 'OK', 'NOT TESTED: this machine does not allow a read-only bind mount: ' + mounted);
  const ev: Record<string, unknown> = {};
  try {
    ev.proof = { findmnt: sh('findmnt -no OPTIONS "' + setFolder + '"'), write: sh('touch "' + setFolder + '/probe"') };
    evidence.step('PROOF: ' + JSON.stringify(ev.proof));
    expect((ev.proof as any).findmnt).toMatch(/^ro,/);
    expect((ev.proof as any).write).toMatch(/Read-only file system/);
    fs.writeFileSync(path.join(src, 'Documents/letter.txt'), 'changed in run 2\n');
    const before = sh('find "' + setFolder + '" -type f | wc -l');
    const b2 = ag.backup(id);
    ev.run2 = b2.out; ev.serverLog = fs.readFileSync(world.serverLog, 'utf8').split('\n').filter((l) => /read-only|Read-only|error/i.test(l)).slice(-10);
    evidence.step('run 2: exit ' + b2.code + '\n' + b2.out.trim());
    expect(b2.code, 'never reported successful').not.toBe(0);
    expect(result(b2.out)).not.toMatch(/^BS_STOP_SUCCESS/);
    expect(sh('find "' + setFolder + '" -type f | wc -l'), 'nothing was written in the read-only folder').toBe(before);
    const runs = await setRuns(world, 'qa-n9a', id);
    ev.runs = runs;
    evidence.step('server runs: ' + JSON.stringify(runs.map((r) => [r.job, r.result])));
    expect(runs.filter((r) => r.status === 'ok').length, 'no success recorded for run 2').toBe(1);
    expect(await world.live()).not.toContain(id);
    const pl = ag.cli(['points', '--set', id], true); ev.pointsDuring = pl.out;
    evidence.step('points during the fault: exit ' + pl.code + ' ' + pl.out.trim());
    const tf = path.join(world.dir, 'restore-during'); const rf = ag.restore(id, tf, ['--point', p1]);
    ev.restoreDuring = rf.out; ev.serverSysLog = sh('grep -rh error "' + path.join(world.sys, 'logs') + '" | tail -5');
    evidence.step('restore of point 1 during the fault: exit ' + rf.code + ' ' + rf.out.trim() + '\nserver system log: ' + ev.serverSysLog);
    expect(notWhole(v1, manifest(restoredPath(tf, src))), 'never a half file passing as whole').toEqual([]);
    // soft: the recovery below is still checked. A read-only store holds every earlier point; reading it needs no write
    expect.soft(pl.code, 'the points of a read-only repository can be listed:\n' + pl.out).toBe(0);
    expect.soft(rf.code, 'point 1 of a read-only repository can be restored:\n' + rf.out).toBe(0);
    expect.soft(compare(v1, manifest(restoredPath(tf, src)))).toEqual([]);
  } finally { sh('umount -l "' + setFolder + '"'); keep('n9a', ev); }

  evidence.step('RECOVERY: writable again → backup 3');
  const v3 = manifest(src);
  const b3 = ag.backup(id); expect(b3.out, b3.out).toMatch(/^BS_STOP_SUCCESS /m);
  expect(points(ag, id).length, 'two points: backup 1 and backup 3 (none from the failed run)').toBe(2);
  const t = path.join(world.dir, 'restore'); const r = ag.restore(id, t); expect(r.code, r.out).toBe(0);
  expect(compare(v3, manifest(restoredPath(t, src)))).toEqual([]);
  const t1 = path.join(world.dir, 'restore-1'); const r1 = ag.restore(id, t1, ['--point', p1]); expect(r1.code, r1.out).toBe(0);
  expect(compare(v1, manifest(restoredPath(t1, src)))).toEqual([]);
  evidence.step('server runs after recovery: ' + JSON.stringify((await setRuns(world, 'qa-n9a', id)).map((x) => [x.job, x.result])));
});

test('N9b the server stopped for the whole run → the run fails at once (time measured), no false success → server back → no ghost → backup → restore identical (SHA-256)', async ({ world, evidence }) => {
  world.addCustomer('qa-n9b');
  const ag = world.agent('qa-n9b', 'N9B-PC'); ag.register();
  const src = path.join(world.dir, 'data'); const v1 = goldenDataset(src);
  const id = ag.addSet('Files', [src]);
  const b1 = ag.backup(id); expect(b1.out, b1.out).toMatch(/^BS_STOP_SUCCESS /m);
  const pid = world.server!.pid!;
  world.killServer();
  await new Promise((r) => setTimeout(r, 1000));
  const port = Number(new URL(world.url).port);
  const refused = await new Promise<string>((r) => { const s = net.connect({ port, host: 'localhost' }); s.on('connect', () => { s.destroy(); r('CONNECTED'); }); s.on('error', (e: any) => r([String(e), e.code, ...(e.errors || []).map((x: any) => x.code)].filter(Boolean).join(' '))); });   // localhost = ::1 and 127.0.0.1: both refused is an AggregateError (CI run 12)
  const ev: Record<string, unknown> = { proof: { process: sh('ps -p ' + pid + ' -o stat= || echo GONE'), port: refused } };
  evidence.step('PROOF: ' + JSON.stringify(ev.proof));
  expect(refused).toMatch(/ECONNREFUSED/);
  fs.writeFileSync(path.join(src, 'Documents/letter.txt'), 'changed while the server was down\n');
  const t0 = Date.now(); const b2 = ag.backup(id); ev.ms = Date.now() - t0; ev.run2 = b2.out;
  evidence.step('run 2 (server down): exit ' + b2.code + ' in ' + ev.ms + ' ms\n' + b2.out.trim());
  expect(b2.code).not.toBe(0);
  expect(result(b2.out)).not.toMatch(/^BS_STOP_SUCCESS/);
  expect(b2.out, 'the reason names the server / the connection').toMatch(/connection|server/i);
  await world.startServer();
  const runs = await setRuns(world, 'qa-n9b', id);
  ev.runsAfterRestart = runs;
  evidence.step('server runs after it is back (before any new run): ' + JSON.stringify(runs.map((r) => [r.job, r.result])));
  expect(await world.live()).not.toContain(id);
  const v3 = manifest(src);
  const b3 = ag.backup(id); expect(b3.out, b3.out).toMatch(/^BS_STOP_SUCCESS /m);
  const t = path.join(world.dir, 'restore'); const r = ag.restore(id, t); expect(r.code, r.out).toBe(0);
  expect(compare(v3, manifest(restoredPath(t, src)))).toEqual([]);
  const t1 = path.join(world.dir, 'restore-1'); const r1 = ag.restore(id, t1, ['--point', points(ag, id)[0]]); expect(r1.code, r1.out).toBe(0);
  expect(compare(v1, manifest(restoredPath(t1, src)))).toEqual([]);
  ev.runsEnd = (await setRuns(world, 'qa-n9b', id)).map((x) => [x.job, x.result]);
  keep('n9b', ev);
});
