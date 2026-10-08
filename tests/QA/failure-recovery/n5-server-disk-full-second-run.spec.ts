// N5 — The server's storage disk fills up in the middle of the SECOND backup of a set that already has a good point
// (F6 covers a first backup that never fits; here the question is what happens to what is already stored).
// The storage is a 64 MB tmpfs; after backup 1 it is filled with a filler file until ~3 MB are left, then 10 MB of new
// random data are backed up. The fault is proven by df (100 % / the free bytes) on the server's storage.
// Contract ST-06: "a clear message on the computer … space of the failed run given back … earlier points are
// byte-identical … the run after space is freed completes and restores identical"; the server's own message:
// "Existing backups are not affected".
import { test, expect } from '../lib/fixtures';
import { World, goldenDataset, manifest, compare, restoredPath } from '../lib/world';
import { bigFiles, notWhole, leftovers, unmount } from '../lib/fault';
import { result, setRuns, points, tmpfs, fill, df, keep } from '../lib/nfault';
import { execSync } from 'child_process';
import * as fs from 'fs';
import * as path from 'path';

test('N5 server disk full during backup 2 → failed truthfully, point 1 intact and restorable DURING the fault → space freed → backup 3 → both points identical (SHA-256)', async ({ evidence }, info) => {
  const world = new World();
  world.usersDir = path.join(world.dir, 'small-disk');
  if (!tmpfs(world.usersDir, 64)) test.skip(true, 'NOT TESTED: this machine does not allow mounting a small disk');
  const ev: Record<string, unknown> = {};
  try {
    await world.start();
    world.addCustomer('qa-n5');
    const ag = world.agent('qa-n5', 'N5-PC'); ag.register();
    const src = path.join(world.dir, 'data'); const v1 = goldenDataset(src);
    const id = ag.addSet('Files', [src]);
    const b1 = ag.backup(id); expect(b1.out, b1.out).toMatch(/^BS_STOP_SUCCESS /m);
    const p1 = points(ag, id)[0];
    ev.dfAfterBackup1 = df(world.usersDir);

    evidence.step('FILL the server\'s storage: a filler file until ~3 MB are left');
    const filler = fill(world.usersDir);
    const fillerMb = fs.statSync(filler).size;
    fs.truncateSync(filler, Math.max(0, fillerMb - 3 * 1024 * 1024));
    ev.dfFilled = df(world.usersDir);
    const avail = Number(execSync('df -B1 --output=avail "' + world.usersDir + '" | tail -1').toString().trim());
    evidence.step('df of the storage now: ' + ev.dfFilled);
    expect(avail, 'PROOF: the storage has less than 4 MB free').toBeLessThan(4 * 1024 * 1024);

    bigFiles(src, 1, 10);                                    // 10 MB of new random data: cannot fit
    fs.writeFileSync(path.join(src, 'Documents/letter.txt'), 'changed in run 2\n');
    const b2 = ag.backup(id);
    ev.backup2 = b2.out; ev.dfAfterFailedRun = df(world.usersDir);
    evidence.step('backup 2: exit ' + b2.code + '\n' + b2.out.trim() + '\ndf after: ' + ev.dfAfterFailedRun);
    expect(b2.code, 'never reported as successful').not.toBe(0);
    expect(result(b2.out)).not.toMatch(/^BS_STOP_SUCCESS/);
    expect(b2.out, 'the reason is clear').toMatch(/disk is full|no free disk space|space/i);
    const runs2 = await setRuns(world, 'qa-n5', id);
    evidence.step('server runs: ' + JSON.stringify(runs2.map((r) => [r.job, r.result])));
    expect(runs2.length, 'the failed run is recorded once').toBe(2);
    expect(runs2[0].status).toBe('bad');
    expect(await world.live()).not.toContain(id);
    expect(points(ag, id), 'no point made by the failed run').toEqual([p1]);

    evidence.step('DURING the fault: point 1 still restores identical (existing backups are not affected)');
    const tf = path.join(world.dir, 'restore-during'); const rf = ag.restore(id, tf, ['--point', p1]);
    ev.restoreDuringFault = rf.out;
    evidence.step('restore during the fault: exit ' + rf.code + ' ' + rf.out.trim().split('\n')[0]);
    expect(notWhole(v1, manifest(restoredPath(tf, src))), 'never a half file passing as whole').toEqual([]);
    expect(leftovers(manifest(tf))).toEqual([]);
    expect(rf.code, 'a restore of an existing point needs no room on the server\'s storage:\n' + rf.out).toBe(0);
    expect(compare(v1, manifest(restoredPath(tf, src)))).toEqual([]);

    evidence.step('RECOVERY: the filler removed; backup 3');
    fs.rmSync(filler);
    const v3 = manifest(src);
    const b3 = ag.backup(id); expect(b3.out, b3.out).toMatch(/^BS_STOP_SUCCESS /m);
    const t3 = path.join(world.dir, 'restore-3'); const r3 = ag.restore(id, t3); expect(r3.code, r3.out).toBe(0);
    expect(compare(v3, manifest(restoredPath(t3, src)))).toEqual([]);
    const t1 = path.join(world.dir, 'restore-1'); const r1 = ag.restore(id, t1, ['--point', p1]); expect(r1.code, r1.out).toBe(0);
    expect(compare(v1, manifest(restoredPath(t1, src)))).toEqual([]);
  } finally {
    keep('n5', ev);
    await world.stop();
    unmount(world.usersDir);
    if (info.status === info.expectedStatus) fs.rmSync(world.dir, { recursive: true, force: true });
  }
});
