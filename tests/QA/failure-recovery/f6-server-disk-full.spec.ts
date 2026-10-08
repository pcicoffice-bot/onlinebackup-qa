// F6 — The server's backup disk fills up in the middle of a backup.
// Expected: the run FAILS with a clear reason (never "success"), nothing stays running or locked, the space taken by
// the failed run is given back, and once the backup fits, it succeeds and restores identical.
import { test, expect } from '../lib/fixtures';
import { World, goldenDataset, manifest, compare, restoredPath } from '../lib/world';
import { execSync } from 'child_process';
import { smallDisk, unmount } from '../lib/fault';
import * as fs from 'fs';
import * as path from 'path';

test('F6 server disk full mid-backup → clear failure, no lock, space given back → smaller backup succeeds and restores identical', async ({ evidence }, info) => {
  const world = new World();
  world.usersDir = path.join(world.dir, 'small-disk'); fs.mkdirSync(world.usersDir);
  if (!smallDisk(world.usersDir, 12)) test.skip(true, 'NOT TESTED: this machine does not allow mounting a small disk (neither root nor passwordless sudo)');
  try {
    await world.start();
    world.addCustomer('qa-f6');
    const ag = world.agent('qa-f6', 'F6-PC'); ag.register();
    const src = path.join(world.dir, 'data'); goldenDataset(src);      // ~23 MB of random data on a 12 MB disk
    const id = ag.addSet('Files', [src]);
    evidence.step('backup onto a disk that is too small');
    const b1 = ag.backup(id);
    expect(b1.code, 'never reported as successful').not.toBe(0);
    expect(b1.out).not.toMatch(/^BS_STOP_SUCCESS /m);
    expect(b1.out, 'the reason is clear').toMatch(/space|full|quota|507|disk/i);
    expect(await world.live()).not.toContain(id);
    evidence.step('the failed run gave its space back');
    const used = Number(execSync('df --output=used -B1 "' + world.usersDir + '" | tail -1').toString().trim());
    expect(used, 'bytes still used on the backup disk after the failed run').toBeLessThan(3 * 1024 * 1024);
    evidence.step('the large files are taken out of the set; the next backup fits and succeeds');
    fs.rmSync(path.join(src, 'Binary'), { recursive: true });
    const now = manifest(src);
    const b2 = ag.backup(id);
    expect(b2.out, b2.out).toMatch(/^BS_STOP_SUCCESS /m);
    const target = path.join(world.dir, 'restore');
    const r = ag.restore(id, target); expect(r.code, r.out).toBe(0);
    expect(compare(now, manifest(restoredPath(target, src)))).toEqual([]);
  } finally {
    await world.stop();
    unmount(world.usersDir);
    if (info.status === info.expectedStatus) fs.rmSync(world.dir, { recursive: true, force: true });
  }
});
