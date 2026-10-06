// F8 — The customer's disk fills up in the middle of a RESTORE (the restore target is a small disk).
// Expected: the restore says it failed (exit code, "failed=" > 0, the server's record is not a success), no half-written
// file is left — neither under a temporary name nor under the real name as if it were whole — and a restore to a disk
// with room afterwards gives every file identical (SHA-256).
import { test, expect } from '../lib/fixtures';
import { goldenDataset, manifest, compare, restoredPath } from '../lib/world';
import { smallDisk, unmount, notWhole, leftovers, restoreRuns } from '../lib/fault';
import * as path from 'path';

test('F8 client disk full during a restore → reported failed, nothing half-written passes as whole → restore to a good disk identical', async ({ world, evidence }) => {
  world.addCustomer('qa-f8');
  const ag = world.agent('qa-f8', 'F8-PC'); ag.register();
  const src = path.join(world.dir, 'data'); const data = goldenDataset(src);     // ~24 MB
  const id = ag.addSet('Files', [src]);
  evidence.step('backup of the golden dataset');
  const b = ag.backup(id); expect(b.out, b.out).toMatch(/^BS_STOP_SUCCESS /m);

  const small = path.join(world.dir, 'small-disk');
  test.skip(!smallDisk(small, 8), 'NOT TESTED: this machine does not allow mounting a small disk (tmpfs needs root / CAP_SYS_ADMIN)');
  try {
    evidence.step('restore onto an 8 MB disk (the data is ~24 MB)');
    const r1 = ag.restore(id, small);
    evidence.step('restore output: ' + r1.out.trim().split('\n').pop());
    expect(r1.code, 'a restore that could not write everything must not end as a success:\n' + r1.out).not.toBe(0);
    expect(r1.out).toMatch(/failed=[1-9]/);
    const got = manifest(small);
    evidence.step('on the small disk: ' + got.size + ' files');
    expect(leftovers(got), 'temporary half files left on the customer\'s disk').toEqual([]);
    expect(notWhole(data, manifest(restoredPath(small, src))), 'files present under their real name but not identical (a half file passing as whole)').toEqual([]);
    evidence.step('the server\'s record of this restore is not a success');
    const rr = restoreRuns(await world.runs('qa-f8'));
    expect(rr.length, 'the restore is recorded on the server: ' + JSON.stringify(await world.runs('qa-f8'))).toBeGreaterThan(0);
    expect(rr[0].status, JSON.stringify(rr)).not.toBe('ok');
  } finally { unmount(small); }

  evidence.step('RECOVERY: restore to a disk with room → identical');
  const target = path.join(world.dir, 'restore');
  const r2 = ag.restore(id, target); expect(r2.code, r2.out).toBe(0);
  expect(compare(data, manifest(restoredPath(target, src)))).toEqual([]);
  expect(leftovers(manifest(target))).toEqual([]);
});
